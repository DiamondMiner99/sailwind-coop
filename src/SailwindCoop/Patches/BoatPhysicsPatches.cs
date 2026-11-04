using HarmonyLib;
using UnityEngine;
using SailwindCoop.Debug;
using SailwindCoop.Player;

namespace SailwindCoop.Patches
{
    /// <summary>
    /// Disables boat physics simulation on guest.
    /// Guest receives authoritative state from host.
    /// </summary>
    public static class BoatPhysicsPatches
    {
        /// <summary>
        /// Wind forces now run on guest - we sync wind direction/speed, so forces match.
        /// Guest physics runs with velocity correction toward host's authoritative state.
        /// </summary>
        // [HarmonyPatch(typeof(BoatWind), "FixedUpdate")] - DISABLED: Let guest run physics
        // public static class BoatWindPatch { ... }

        /// <summary>
        /// Buoyancy now runs on guest - we sync FFT ocean time, so wave phase matches.
        /// Guest physics runs with velocity correction toward host's authoritative state.
        /// </summary>
        // [HarmonyPatch(typeof(Buoyancy), "FixedUpdate")] - DISABLED: Let guest run physics
        // public static class BuoyancyPatch { ... }

        // BoatMass.FixedUpdate patch REMOVED - was costing 1.41ms (10 boats × 50Hz × Traverse reflection)
        // Items are synced via ItemSyncManager, so mass calculation works naturally.
        // Remote player weight is added by UpdateMass Postfix below.

        /// <summary>
        /// After UpdateMass runs, add the weight of EVERY remote crew member standing on this boat.
        /// Uses same formula as host player: 160kg + center of mass offset, applied once per avatar.
        /// </summary>
        [HarmonyPatch(typeof(BoatMass), "UpdateMass")]
        public static class BoatMassGuestWeightPatch
        {
            // (v0.3.0) Set while we have temporarily hidden GameState.currentBoat from vanilla's own
            // center-of-mass math; see the prefix below.
            private static Transform _suppressedCurrentBoat;

            /// <summary>
            /// (v0.3.0) Stop an ASHORE player from wrenching a boat's center of mass halfway to the island.
            ///
            /// Vanilla adds the local player's weight at a lever arm of
            /// <c>Refs.observerMirror.transform.localPosition</c>, guarded by "is GameState.currentBoat one of
            /// mine". That reads as a boat-LOCAL offset of a few metres, and in vanilla it always is, because
            /// PlayerDisembark nulls GameState.currentBoat and reparents the observer to the shifting world in
            /// the same breath. The two facts are one fact.
            ///
            /// This mod separates them. A joining crewmate has GameState.currentBoat seated on the shared boat
            /// unconditionally, so that the join lands them on the right hull even when the host is ashore at
            /// snapshot time - but the observer is only reparented under the boat when they are actually
            /// aboard. Stand on the dock and vanilla's guard still passes while localPosition has become a
            /// position in the shifting world: in one captured session, a lever arm of 379 metres where the
            /// formula expects single digits. The center of mass then swings metres off the keel and the hull
            /// librates, which is a boat that will not sit still and cannot be corrected into sitting still.
            ///
            /// Rather than reimplement vanilla's mass math, this hides currentBoat for the duration of the
            /// call when the observer is not in fact parented under this boat - which makes vanilla's own
            /// guard answer the question it was actually asking. Restored in the postfix, unconditionally.
            /// </summary>
            [HarmonyPrefix]
            public static void Prefix(BoatMass __instance)
            {
                _suppressedCurrentBoat = null;
                try
                {
                    var current = GameState.currentBoat;
                    if (current == null || current.parent != __instance.transform) return;

                    var observer = Refs.observerMirror != null ? Refs.observerMirror.transform : null;
                    if (observer == null) return;

                    // Aboard means the observer actually hangs off this boat. IsChildOf covers the walkCol
                    // and boatModel frames alike without caring which one embarking happened to use.
                    if (observer.IsChildOf(__instance.transform)) return;

                    _suppressedCurrentBoat = current;
                    GameState.currentBoat = null;
                }
                catch (System.Exception e)
                {
                    _suppressedCurrentBoat = null;
                    Plugin.Log.LogWarning("[BoatMass] could not check whether the player is aboard: " + e.Message);
                }
            }

            [HarmonyPostfix]
            public static void Postfix(BoatMass __instance, Rigidbody ___body, float ___selfMass, float ___leverageMult,
                System.Collections.Generic.List<ItemRigidbody> ___itemsOnBoat)
            {
                // Read before the restore below: this is the answer vanilla's own guard just gave.
                bool vanillaCountedPlayer = GameState.currentBoat != null && GameState.currentBoat.parent == __instance.transform;

                // Put it back FIRST and unconditionally - every early return below must not leak the
                // suppression into the rest of the frame, where currentBoat means "the boat I am on".
                if (_suppressedCurrentBoat != null)
                {
                    GameState.currentBoat = _suppressedCurrentBoat;
                    _suppressedCurrentBoat = null;
                }

                CorrectRemoteHeldItems(__instance, ___body, ___selfMass, ___leverageMult, ___itemsOnBoat, vanillaCountedPlayer);

                // Only run on host in multiplayer
                if (!Plugin.IsMultiplayer || !Plugin.IsHost) return;

                var remoteManager = RemotePlayerManager.Instance;
                if (remoteManager == null || !remoteManager.HasRemotePlayer) return;

                // (v0.3.0) Crew weight used to be skipped entirely while moored OR anchored, so a crewmate
                // walking a docked deck moved nothing. The hazard it was guarding against is real but far
                // narrower than the guard: a mooring spring's stiffness is `boatRigidbody.mass * 6`, and
                // extra crew mass in that figure stiffens it enough that driving a still-moored boat heels
                // the deck under the waterline, where WaveSplashZone.Overflow floods it with no hull damage.
                //
                // But vanilla reads that mass ONCE, inside MoorTo, and never recomputes it while the boat
                // stays tied up. So the exposure is a single frame, and the answer is to suppress the crew
                // contribution for that frame (see MooringSpringCrewMassPatch below) rather than for the
                // whole time the boat is at a dock.
                //
                // The anchored half of the old guard had no justification at all: AnyRopeMoored() also
                // returns true for a set anchor, but nothing in the anchor derives a joint stiffness from
                // boat mass. Anchored crew weight was suppressed purely by association.

                // Per-crew weight (kg). Vanilla uses 160 for every person (host included); this is configurable
                // and lower by default so a crowd on one side of a small boat doesn't pile up enough heel to
                // flip it. HOST-ONLY value (this whole patch is host-gated above), so nothing to sync.
                float crewWeight = Plugin.CrewMemberWeightConfig != null ? Plugin.CrewMemberWeightConfig.Value : 90f;
                if (crewWeight <= 0f) return;

                // Add weight for each remote crew member currently on this boat.
                //
                // (v0.3.0) THIS LOOP NEVER RAN. It filtered on `avatar.CurrentBoat != __instance.transform`,
                // and those are two different objects: BoatMass sits on the boat ROOT (vanilla's own guard is
                // `GameState.currentBoat.parent == base.transform`), while CurrentBoat comes from
                // FindBoatByName, which resolves through BoatRefs and deliberately returns the boatMODEL
                // CHILD. The comparison was always unequal, every crewmate hit `continue`, and the host added
                // exactly zero crew mass. That is why a guest leaning on the rail heeled the boat on their own
                // screen and did nothing on the host's, which is the authoritative one. Dead in every
                // published release, not a v0.3.0 regression.
                //
                // Identity now comes from the reported NAME - the sender takes it from GameState.lastBoat, the
                // root SaveableObject name, which is the key BoatUtility is keyed by and is unambiguous about
                // which node of the hierarchy it means.
                foreach (var avatar in remoteManager.Avatars)
                {
                    // A crewmate still loading has no meaningful position yet, and one that has gone quiet may
                    // have dropped without us noticing. Either would trim the boat from a stale spot.
                    if (avatar == null || !avatar.HasStreamed) continue;
                    if (Time.unscaledTime - avatar.LastRemotePacketTime > StaleCrewSeconds) continue;

                    var boatName = avatar.CurrentBoatName;
                    if (string.IsNullOrEmpty(boatName)) continue;                 // ashore, no contribution
                    if (boatName != __instance.gameObject.name) continue;         // standing on some other hull

                    // Lever arm from the WIRE, not from the avatar's world transform. The packet is already
                    // boatModel-local, which is the frame vanilla's centre-of-mass maths works in
                    // (Refs.observerMirror.transform.localPosition). Re-deriving it from the avatar would pick
                    // up the position smoothing and capsule-height offset applied on receive, and the old line
                    // inverse-transformed through the ROOT - a second frame error hiding behind the first.
                    Vector3 deckLocal = avatar.BoatLocalFeetPosition;
                    // The wire carries FEET; vanilla's lever arm is the controller origin.
                    deckLocal.y += Sync.PlayerSyncManager.ControllerFeetGap();

                    // Reject anything that cannot be a place to stand on this boat. One bad packet here moves
                    // the centre of mass of a whole ship, so it is worth being strict.
                    if (!IsFinite(deckLocal) || deckLocal.magnitude > MaxCrewLeverMeters) continue;

                    // Add guest mass
                    ___body.mass += crewWeight;

                    // Center-of-mass offset (same formula as the host player in BoatMass.UpdateMass) - scales
                    // with the weight, so lowering crewWeight also lightens the heel this crew member induces.
                    float ratio = crewWeight / ___selfMass;
                    Vector3 offset = Quaternion.Euler(0f, -90f, 0f) * deckLocal * ratio * ___leverageMult;
                    ___body.centerOfMass += offset;
                }
            }

            /// <summary>
            /// Vanilla weighs a held item at the local player's position, because a held item could only ever be
            /// in the local player's hand. A crewmate's item carries a stand-in held reference on this machine, so
            /// it was weighed at the wrong person, or not at all with the local player ashore. Then the drop moved
            /// its whole lever in one physics step. That is a jerk on every machine but the dropper's, and the
            /// lever scales with the item's mass. Runs on host and guest alike: each has its own hull.
            /// </summary>
            private static void CorrectRemoteHeldItems(BoatMass boatMass, Rigidbody body, float selfMass, float leverageMult,
                System.Collections.Generic.List<ItemRigidbody> items, bool vanillaCountedPlayer)
            {
                if (!Plugin.IsMultiplayer || items == null) return;
                if (Plugin.RemoteHeldMassConfig != null && !Plugin.RemoteHeldMassConfig.Value) return;

                var manager = Sync.ItemSyncManager.Instance;
                if (manager == null) return;

                Transform boatModel = null;
                foreach (var itemBody in items)
                {
                    if (itemBody == null || itemBody.GetCurrentInventorySlot() != null) continue;
                    var ship = itemBody.GetShipItem();
                    if (ship == null || ship.held == null || !manager.IsRemoteHeld(ship)) continue;

                    if (boatModel == null) boatModel = boatMass.GetComponent<BoatRefs>()?.boatModel;
                    if (boatModel == null) return;

                    float mass = itemBody.GetBody().mass;
                    float ratio = mass / selfMass;
                    Quaternion frame = Quaternion.Euler(0f, -90f, 0f);

                    // A carried item keeps its boat latch for the whole carry, so it stays on this list after the
                    // holder walks down the dock with it, or after the ship sails off without them. Weigh it only
                    // while the holder is standing on this boat, the same test the crew weight loop uses. An item
                    // on the holder's belt weighs nothing here either, as it does on their machine.
                    Vector3 local = boatModel.InverseTransformPoint(ship.transform.position);
                    bool aboard = ship.gameObject.activeSelf && manager.HolderIsAboard(ship, boatMass.gameObject.name)
                        && IsFinite(local) && local.magnitude <= MaxCrewLeverMeters;

                    if (_holderWasAboard.Count > MaxTrackedCarries) _holderWasAboard.Clear();
                    if (_holderWasAboard.TryGetValue(itemBody, out bool wasAboard) && wasAboard != aboard)
                        BoatMassBalanceEasePatch.NoteItemSetDown(boatMass);
                    _holderWasAboard[itemBody] = aboard;

                    if (!aboard)
                    {
                        if (vanillaCountedPlayer && Refs.observerMirror != null)
                        {
                            body.mass -= mass;
                            body.centerOfMass -= frame * Refs.observerMirror.transform.localPosition * ratio * leverageMult;
                        }
                        continue;
                    }

                    if (vanillaCountedPlayer)
                    {
                        if (Refs.observerMirror == null) continue;
                        body.centerOfMass -= frame * Refs.observerMirror.transform.localPosition * ratio * leverageMult;
                    }
                    else
                    {
                        body.mass += mass;
                    }
                    body.centerOfMass += frame * local * ratio * leverageMult;
                }
            }

            private const int MaxTrackedCarries = 64;
            private static readonly System.Collections.Generic.Dictionary<ItemRigidbody, bool> _holderWasAboard =
                new System.Collections.Generic.Dictionary<ItemRigidbody, bool>();

            /// <summary>Seconds of silence after which a crewmate stops contributing trim. They may have
            /// dropped without a clean disconnect, and a boat held in a heel by a player who is gone reads as
            /// a physics bug with no visible cause.</summary>
            private const float StaleCrewSeconds = 5f;

            /// <summary>Furthest a crewmate can be from the boat origin and still be standing on it. Larger
            /// than any hull in the game, so it rejects only genuinely broken values.</summary>
            private const float MaxCrewLeverMeters = 100f;

            /// <summary>float.IsFinite is .NET Core only; this target is net472.</summary>
            internal static bool IsFinite(Vector3 v)
            {
                return !float.IsNaN(v.x) && !float.IsInfinity(v.x)
                    && !float.IsNaN(v.y) && !float.IsInfinity(v.y)
                    && !float.IsNaN(v.z) && !float.IsInfinity(v.z);
            }

            /// <summary>
            /// (v0.3.0) Total remote crew mass currently standing on the named boat root. Uses the same
            /// eligibility rules as the mass loop above, so the two cannot drift apart.
            /// </summary>
            internal static float RemoteCrewMassOn(string boatRootName)
            {
                if (string.IsNullOrEmpty(boatRootName)) return 0f;
                if (!Plugin.IsMultiplayer || !Plugin.IsHost) return 0f;

                var remoteManager = RemotePlayerManager.Instance;
                if (remoteManager == null || !remoteManager.HasRemotePlayer) return 0f;

                float crewWeight = Plugin.CrewMemberWeightConfig != null ? Plugin.CrewMemberWeightConfig.Value : 90f;
                if (crewWeight <= 0f) return 0f;

                float total = 0f;
                foreach (var avatar in remoteManager.Avatars)
                {
                    if (avatar == null || !avatar.HasStreamed) continue;
                    if (Time.unscaledTime - avatar.LastRemotePacketTime > StaleCrewSeconds) continue;
                    if (avatar.CurrentBoatName != boatRootName) continue;
                    total += crewWeight;
                }
                return total;
            }
        }

        /// <summary>
        /// (v0.3.0) Keep remote crew weight out of a mooring spring's stiffness, so crew weight can apply
        /// while a boat is tied up instead of being switched off for the whole time it is at a dock.
        ///
        /// Vanilla sets `mooring.spring.spring = boatRigidbody.mass * 6` inside MoorTo, and never revisits
        /// it while the boat stays moored. With crew mass folded into that one reading, the spring comes out
        /// stiff enough that driving a still-moored boat heels the deck under the waterline, where
        /// WaveSplashZone.Overflow floods the hull with no damage to explain it. That is a real hazard, but
        /// it lives in a single frame, so the whole-time suppression it used to justify was far too broad -
        /// it is why a crewmate walking a docked deck moved nothing at all.
        ///
        /// Subtracting the crew here gives the spring the stiffness vanilla would have computed for the hull
        /// alone. The value is restored immediately, and BoatMass.UpdateMass rewrites body.mass from scratch
        /// on the next FixedUpdate regardless, so nothing downstream sees the dip.
        /// </summary>
        [HarmonyPatch(typeof(PickupableBoatMooringRope), "MoorTo")]
        public static class MooringSpringCrewMassPatch
        {
            [HarmonyPrefix]
            public static void Prefix(PickupableBoatMooringRope __instance, out float __state)
            {
                __state = 0f;
                if (!Plugin.IsMultiplayer || !Plugin.IsHost) return;
                try
                {
                    var body = __instance.GetBoatRigidbody();
                    if (body == null) return;
                    var saveable = body.GetComponent<SaveableObject>();
                    if (saveable == null) return;

                    float crewMass = BoatMassGuestWeightPatch.RemoteCrewMassOn(saveable.gameObject.name);
                    // Never drive the mass to zero or negative - a spring of stiffness 0 would not hold the
                    // boat to the dock at all, which is a far worse failure than a slightly stiff one.
                    if (crewMass <= 0f || crewMass >= body.mass) return;

                    body.mass -= crewMass;
                    __state = crewMass;
                }
                catch (System.Exception e)
                {
                    __state = 0f;
                    Plugin.Log.LogWarning("[BoatMass] Could not exclude crew mass from a mooring spring: " + e.Message);
                }
            }

            [HarmonyPostfix]
            public static void Postfix(PickupableBoatMooringRope __instance, float __state)
            {
                if (__state <= 0f) return;
                try
                {
                    var body = __instance.GetBoatRigidbody();
                    if (body != null) body.mass += __state;
                }
                catch { /* UpdateMass rewrites body.mass next FixedUpdate anyway */ }
            }
        }

        /// <summary>
        /// Eases the center of mass for a moment after a crewmate sets an item down or carries one off. An item
        /// carried aboard from a dock has no weight until it is put down, however it is weighed, so that step
        /// still has to go somewhere. Last of the UpdateMass postfixes, so it sees the finished value.
        /// </summary>
        [HarmonyPatch(typeof(BoatMass), "UpdateMass")]
        public static class BoatMassBalanceEasePatch
        {
            private const float EaseSeconds = 1.5f;
            private const float EaseRate = 4f;
            private static readonly System.Collections.Generic.Dictionary<BoatMass, float> _easeUntil =
                new System.Collections.Generic.Dictionary<BoatMass, float>();
            private static readonly System.Collections.Generic.Dictionary<BoatMass, Vector3> _lastCenter =
                new System.Collections.Generic.Dictionary<BoatMass, Vector3>();

            public static void NoteItemSetDown(BoatMass boatMass)
            {
                if (boatMass != null) _easeUntil[boatMass] = Time.time + EaseSeconds;
            }

            [HarmonyPostfix]
            [HarmonyPriority(Priority.Last)]
            public static void Postfix(BoatMass __instance, Rigidbody ___body)
            {
                if (!Plugin.IsMultiplayer) return;
                if (Plugin.RemoteHeldMassConfig != null && !Plugin.RemoteHeldMassConfig.Value) return;

                Vector3 target = ___body.centerOfMass;
                if (_easeUntil.TryGetValue(__instance, out float until) && Time.time < until
                    && _lastCenter.TryGetValue(__instance, out Vector3 last))
                {
                    target = Vector3.Lerp(last, target, 1f - Mathf.Exp(-EaseRate * Time.deltaTime));
                    ___body.centerOfMass = target;
                }
                _lastCenter[__instance] = target;
            }
        }

        /// <summary>
        /// Skip kinematic state management on guest - we control it via BoatSyncManager.
        /// BoatHorizon.UpdateKinematic() sets isKinematic=false when player is close,
        /// which fights with our sync code that needs the boat kinematic.
        /// </summary>
        [HarmonyPatch(typeof(BoatHorizon), "UpdateKinematic")]
        public static class BoatHorizonKinematicPatch
        {
            [HarmonyPrefix]
            public static bool Prefix()
            {
                PatchProfiler.Begin("BoatHorizon.UpdateKinematic");

                // Only run on host or when not in multiplayer
                if (!Plugin.IsMultiplayer)
                {
                    PatchProfiler.End("BoatHorizon.UpdateKinematic");
                    return true;
                }

                PatchProfiler.End("BoatHorizon.UpdateKinematic");
                return Plugin.IsHost;
            }
        }

        // (v0.2.32) Towable Boats neutralization, guest side. TB's own prefix on
        // BoatPerformanceSwitcher.Update (BoatPerformancePatches.cs:28-42, returns false) forces full
        // BoatProbes physics for boats in the tow chain, derived from GameState.lastBoat - a value
        // that DIFFERS on every client. Host and guests would therefore disagree about which hulls
        // run full physics, and a guest's local integration fights the authoritative transform
        // stream with varying strength. On guests we replicate the VANILLA decision (lastBoat or
        // sunk = full physics, everything else = performance mode) and return false, which also
        // skips TB's prefix (it declares no __runOriginal; HarmonyBefore orders us first). Host and
        // singleplayer keep TB's behavior untouched - the host IS the physics authority.
        [HarmonyPatch(typeof(BoatPerformanceSwitcher), "Update")]
        public static class BoatPerformanceSwitcherGuestPatch
        {
            private static readonly System.Reflection.MethodInfo SetPerformanceMode =
                AccessTools.Method(typeof(BoatPerformanceSwitcher), "SetPerformanceMode");

            [HarmonyPrefix]
            [HarmonyBefore("com.nandbrew.towableboats")]
            public static bool Prefix(BoatPerformanceSwitcher __instance)
            {
                if (!Plugin.IsMultiplayer || Plugin.IsHost) return true;
                if (!SailwindCoop.Compat.TowableBoatsCompat.IsInstalled) return true;

                var damage = __instance.GetComponent<BoatDamage>();
                bool wantFullPhysics = GameState.lastBoat == __instance.transform
                    || (damage != null && damage.sunk);
                bool perfOn = __instance.performanceModeIsOn();

                if (wantFullPhysics && perfOn)
                    SetPerformanceMode?.Invoke(__instance, new object[] { false });
                else if (!wantFullPhysics && !perfOn)
                    SetPerformanceMode?.Invoke(__instance, new object[] { true });

                return false; // skip TB's prefix AND vanilla (we just ran the vanilla decision)
            }
        }
    }
}
