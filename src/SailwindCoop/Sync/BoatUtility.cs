using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace SailwindCoop.Sync
{
    public static class BoatUtility
    {
        // Cache boats. (v0.2.32) Boats CAN change during gameplay - modded boats (HMS Leopard's
        // cutter) are spawned/activated at runtime - so the cache is invalidated on every lobby
        // create/join/leave (Plugin lobby handlers) and whenever a compat module spawns or toggles
        // a boat (LeopardSyncManager.ApplyCutterState).
        private static Dictionary<string, SaveableObject> _cachedBoats;

        // Cache rope controllers per boat - must be invalidated when sails change (via LoadData)
        private static Dictionary<SaveableObject, RopeController[]> _cachedRopes =
            new Dictionary<SaveableObject, RopeController[]>();

        /// <summary>
        /// Find all boats in the scene (cached until ClearCaches is called - lobby lifecycle events
        /// and boat spawn/activation invalidate it; see the field comment above).
        /// </summary>
        public static Dictionary<string, SaveableObject> FindAllBoats()
        {
            if (_cachedBoats != null)
            {
                return _cachedBoats;
            }

            // EMPTY-CACHE GUARD: an early packet during scene load (SaveLoadManager not ready / no boats
            // registered yet) must NOT cache an empty dict forever - that would silently re-enable every
            // wrong-frame fallback for the rest of the session. Only cache when we actually found boats.
            var boats = new Dictionary<string, SaveableObject>();
            var allObjects = SaveLoadManager.instance?.GetCurrentObjects();
            if (allObjects == null) return boats;

            foreach (var obj in allObjects)
            {
                if (obj != null && obj.GetComponent<BoatRefs>() != null)
                {
                    boats[obj.gameObject.name] = obj;
                }
            }

            if (boats.Count > 0)
            {
                _cachedBoats = boats;
            }

            return boats;
        }

        /// <summary>
        /// Find a boat by name.
        /// </summary>
        public static SaveableObject FindBoatByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var boats = FindAllBoats();
            boats.TryGetValue(name, out var boat);
            return boat;
        }

        /// <summary>
        /// Get the boat the player is currently on.
        /// </summary>
        public static SaveableObject GetCurrentBoat()
        {
            if (GameState.currentBoat == null) return null;

            // GameState.currentBoat is the boatModel, parent is the actual boat
            var boatTransform = GameState.currentBoat.parent;
            if (boatTransform == null) return null;

            return boatTransform.GetComponent<SaveableObject>();
        }

        /// <summary>
        /// Get the Rigidbody for a boat.
        /// </summary>
        public static Rigidbody GetBoatRigidbody(SaveableObject boat)
        {
            return boat?.GetComponent<Rigidbody>();
        }

        /// <summary>
        /// Resolve a boat's Anchor WITHOUT a child search. Vanilla Anchor.Awake reparents the anchor
        /// OUT of the boat hierarchy (transform.parent = transform.parent.parent.parent), so
        /// boat.GetComponentInChildren&lt;Anchor&gt;() is ALWAYS null after Awake - the reason anchor
        /// set/release sync was silently dead. Resolve through the boat root's serialized references
        /// instead: BoatMooringRopes.anchor (public serialized field on the boat root, vanilla uses it
        /// in AnyRopeMoored), falling back to the registered RopeControllerAnchor's joint object (the
        /// joint sits on the anchor itself; RopeControllerAnchor.RegisterToBoat wires it at Start),
        /// then a child search as a last resort (covers pre-Awake calls during load).
        /// </summary>
        public static Anchor GetAnchor(SaveableObject boat)
        {
            if (boat == null) return null;

            var mooring = boat.GetComponent<BoatMooringRopes>();
            if (mooring != null)
            {
                if (mooring.anchor != null) return mooring.anchor;

                var anchorCtrl = mooring.GetAnchorController();
                var joint = anchorCtrl != null ? anchorCtrl.joint : null;
                if (joint != null)
                {
                    var viaJoint = joint.GetComponent<Anchor>();
                    if (viaJoint != null) return viaJoint;
                }
            }

            // Pre-Awake (anchor still parented under the boat) or unusual hierarchies.
            return boat.GetComponentInChildren<Anchor>(true);
        }

        /// <summary>
        /// Check if a boat is currently anchored.
        /// </summary>
        public static bool IsBoatAnchored(SaveableObject boat)
        {
            var anchor = GetAnchor(boat);
            if (anchor == null) return false;

            // IsSet() is the authoritative vanilla flag. (The old isKinematic read was both unreachable -
            // the child search always returned null - and wrong: ExtraFixedUpdate also sets
            // isKinematic=true while the anchor item is merely HELD.)
            return anchor.IsSet();
        }

        /// <summary>
        /// Get all RopeController components on a boat (cached until InvalidateRopeCache is called).
        /// Call InvalidateRopeCache after sail customization changes.
        ///
        /// Q1 (reef/rope index-swap fix): the RAW GetComponentsInChildren&lt;RopeController&gt;() order is NOT stable
        /// across machines. The guest rebuilds the sail hierarchy via SaveableBoatCustomization.LoadData, which
        /// re-instantiates and reparent-APPENDS sails in a DIFFERENT order than the host's original build, so the
        /// same flat index points at a DIFFERENT mast on the guest (main &lt;-&gt; mizzen reef swap). The rope sync packet
        /// carries an int index keyed to THIS array, and the join snapshot RopeLengths array is matched positionally,
        /// so a non-deterministic order corrupts BOTH. Fix: sort the array by a STABLE per-rope key (mast orderIndex
        /// + sail mast-order + rope role/side) that both host and guest compute IDENTICALLY from local scene data.
        /// Because every send/apply path resolves its index through THIS method, the deterministic order makes index
        /// i refer to the same logical rope on every client - with zero wire-format change.
        /// </summary>
        public static RopeController[] GetRopeControllers(SaveableObject boat)
        {
            if (boat == null) return new RopeController[0];

            if (_cachedRopes.TryGetValue(boat, out var ropes))
            {
                return ropes;
            }

            var raw = boat.GetComponentsInChildren<RopeController>() ?? new RopeController[0];
            // Stable, deterministic ordering by the cross-machine rope key. OrderBy is a STABLE sort, so any
            // ropes that map to the same key (should not happen for distinct controllers, but defensive) keep
            // their original relative order rather than reshuffling non-deterministically.
            ropes = raw.Where(r => r != null).OrderBy(GetStableRopeKey, System.StringComparer.Ordinal).ToArray();
            _cachedRopes[boat] = ropes;
            return ropes;
        }

        /// <summary>
        /// Q1: derive a STABLE, machine-independent key for a RopeController so the host and a guest (which
        /// rebuilds the sail hierarchy in a different child order) agree on which logical rope an index refers to.
        /// The key is built only from data that is identical on both sides: the rope's role (concrete type), the
        /// owning mast's authored orderIndex, the sail's deterministic on-mast order (Mast.UpdateSailOrder sorts by
        /// world Y, same on both clients), and the winch side for paired angle ropes. Zero-padded numeric segments
        /// keep the ordinal string sort numerically correct. Anchor and steering-wheel ropes are singletons per
        /// boat and get fixed top/bottom keys so they always sort to a stable position regardless of hierarchy.
        /// </summary>
        public static string GetStableRopeKey(RopeController rope)
        {
            if (rope == null) return "9~null";

            // Singletons: exactly one per boat. Fixed keys so they never move relative to the sail ropes.
            if (rope is RopeControllerAnchor) return "0~anchor";
            if (rope is RopeControllerSteeringWheel) return "0~helm";

            // Resolve the owning Sail through the public link field on each angle/reef controller type.
            Sail sail = null;
            string role = "z";   // role tag within a (mast,sail) group; keeps ordering deterministic
            string side = "0";   // left/right winch disambiguation for paired controllers

            switch (rope)
            {
                case RopeControllerSailReef reef:
                    sail = reef.sail;
                    role = "1reef";
                    break;
                case RopeControllerSailAngleJib jib:
                    sail = jib.jibAngleMaster != null && jib.jibAngleMaster.sailHinge != null
                        ? jib.jibAngleMaster.sailHinge.GetComponent<Sail>() : null;
                    role = "2jib";
                    side = ((int)jib.side).ToString();
                    break;
                case RopeControllerSailAngleSquare sq:
                    sail = sq.squareAngleMaster != null && sq.squareAngleMaster.GetHinge() != null
                        ? sq.squareAngleMaster.GetHinge().GetComponent<Sail>() : null;
                    role = "3square";
                    side = ((int)sq.side).ToString();
                    break;
                case RopeControllerSailAngle ang:
                    sail = ang.sailHinge != null ? ang.sailHinge.GetComponent<Sail>() : null;
                    role = "4angle";
                    // A fore-and-aft sail has THREE base RopeControllerSailAngle ropes (mid/left/right) that share
                    // this type AND sail, so role alone would collide and let the sort swap left<->right
                    // non-deterministically (the very class of bug Q1 fixes). The base type carries no side flag,
                    // so disambiguate by which SailConnections slot holds this exact controller - identical wiring
                    // on host and guest (Mast.UpdateControllerAttachments assigns from the same SailConnections).
                    var conn = sail != null ? sail.GetComponent<SailConnections>() : null;
                    if (conn != null)
                    {
                        if (conn.angleControllerMid == ang) side = "0mid";
                        else if (conn.angleControllerLeft == ang) side = "1left";
                        else if (conn.angleControllerRight == ang) side = "2right";
                    }
                    break;
            }

            int mastOrder = 99;
            int sailOrder = 99;
            if (sail != null)
            {
                sailOrder = sail.mastOrder;
                var mast = sail.GetComponentInParent<Mast>();
                if (mast != null) mastOrder = mast.orderIndex;
            }

            // mast > sail-on-mast > role > side. All segments identical on host and guest.
            return $"1~m{mastOrder:D2}~s{sailOrder:D2}~{role}~{side}";
        }

        /// <summary>
        /// Invalidate rope cache for a specific boat.
        /// Must be called after sail customization changes (LoadData destroys old RopeControllers and creates new ones).
        /// </summary>
        public static void InvalidateRopeCache(SaveableObject boat)
        {
            if (boat != null)
            {
                _cachedRopes.Remove(boat);
            }
        }

        /// <summary>
        /// Clear all caches. Call when leaving multiplayer.
        /// </summary>
        public static void ClearCaches()
        {
            _cachedBoats = null;
            _cachedRopes.Clear();
        }

        // (v0.2.32) Tow-pin rescan: mooring state changes on FOUR host-side paths (local attach
        // patch, local detach patch, relayed-moor apply, relayed-unmoor apply), and per-rope
        // bookkeeping cannot know whether OTHER ropes still hold a tow (a boat can be towed by two
        // ropes) or whether the boat's pin is owned elsewhere (the deployed cutter's pin belongs to
        // LeopardSyncManager). Rescan the boat's ropes instead: pinned iff ANY rope is currently
        // moored to a TowingCleat; the active cutter is never unpinned here.
        // (final review) LAZY, not a static initializer: a FieldRefAccess throw in the static ctor
        // would be a TypeInitializationException that kills EVERY BoatUtility caller (FindAllBoats,
        // GetAnchor, the rope sort key - the whole mod) for the session. Lazy + try means a vanilla
        // rename degrades exactly one feature (tow pinning) with a log line.
        private static HarmonyLib.AccessTools.FieldRef<PickupableBoatMooringRope, UnityEngine.SpringJoint> _towPinSpringRef;
        private static bool _towPinSpringRefFailed;

        private static UnityEngine.SpringJoint GetMooredSpring(PickupableBoatMooringRope rope)
        {
            if (_towPinSpringRefFailed) return null;
            if (_towPinSpringRef == null)
            {
                try { _towPinSpringRef = HarmonyLib.AccessTools.FieldRefAccess<PickupableBoatMooringRope, UnityEngine.SpringJoint>("mooredToSpring"); }
                catch (System.Exception e)
                {
                    _towPinSpringRefFailed = true;
                    Plugin.Log.LogWarning("[BoatUtility] mooredToSpring did not resolve (vanilla changed?); tow stream pinning disabled. " + e.Message);
                    return null;
                }
            }
            return _towPinSpringRef(rope);
        }

        /// <summary>
        /// Host-only: re-derive whether a boat should be pinned into the always-stream set because
        /// it is under tow. Call after ANY moor/unmoor that could involve a towing cleat.
        /// </summary>
        public static void UpdateTowStreamPin(SaveableObject boat)
        {
            if (!Plugin.IsHost || boat == null) return;
            // Tows only exist with TB; the cutter's pin is owned by LeopardSyncManager. Without TB
            // HasTowingCleat is always false, so this helper could only ever UNPIN sets it does not own.
            if (!Compat.TowableBoatsCompat.IsInstalled) return;

            var mooringRopes = boat.GetComponent<BoatMooringRopes>();
            if (mooringRopes?.ropes == null) return;

            bool towed = false;
            foreach (var rope in mooringRopes.ropes)
            {
                if (rope == null || !rope.IsMoored()) continue;
                var spring = GetMooredSpring(rope);
                if (spring != null && Compat.TowableBoatsCompat.HasTowingCleat(spring.gameObject))
                {
                    towed = true;
                    break;
                }
            }

            if (towed)
            {
                BoatSyncManager.RegisterAlwaysStream(boat.gameObject.name);
            }
            else
            {
                // The deployed cutter's pin is OWNED by LeopardSyncManager (deploy/stow events);
                // a mere rope detach must not strip it.
                if (boat.gameObject.name == Compat.LeopardCompat.CutterRootName
                    && Compat.LeopardCompat.GetCutterActive()) return;
                BoatSyncManager.UnregisterAlwaysStream(boat.gameObject.name);
            }
        }
    }
}
