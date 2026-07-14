using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using SailwindCoop.Debug;
using SailwindCoop.Sync;

namespace SailwindCoop.Patches
{
    /// <summary>
    /// Harmony patches for boat control synchronization.
    /// Patches base class GoPointerButton.OnActivate/OnUnactivate since derived classes don't override.
    /// Active controls are polled by ControlSyncManager at 10Hz.
    /// </summary>
    public static class ControlPatches
    {
        // Track currently active (being interacted with) controls
        public static readonly HashSet<GPButtonRopeWinch> ActiveRopeWinches = new HashSet<GPButtonRopeWinch>();
        public static readonly HashSet<GPButtonSteeringWheel> ActiveSteeringWheels = new HashSet<GPButtonSteeringWheel>();

        // Cache boat references to avoid GetComponentInParent calls during polling
        public static readonly Dictionary<GPButtonRopeWinch, SaveableObject> RopeWinchBoats =
            new Dictionary<GPButtonRopeWinch, SaveableObject>();
        public static readonly Dictionary<GPButtonSteeringWheel, SaveableObject> SteeringWheelBoats =
            new Dictionary<GPButtonSteeringWheel, SaveableObject>();

        /// <summary>
        /// Clear all tracked controls. Call when leaving multiplayer.
        /// </summary>
        public static void ClearTrackedControls()
        {
            ActiveRopeWinches.Clear();
            ActiveSteeringWheels.Clear();
            RopeWinchBoats.Clear();
            SteeringWheelBoats.Clear();
            ActiveBoatPush.Clear();
            ActiveSailPush.Clear();
            ActiveDockPush.Clear();
            // Also wipe the per-instance lean-in start distances. ClearTrackedControls is the canonical
            // full-reset hook (last-peer-leave); without this the dicts retain Unity-destroyed push-col keys
            // across scene reloads, accumulating one stale entry per push-col per rejoined co-op session.
            BoatPushStartDist.Clear();
            DockPushStartDist.Clear();
            _anchorLastHeldTime.Clear();
        }

        // === BOAT PUSH / SAIL PUSH (guest -> host input) ===
        // The shared boat is host-authoritative, so a GUEST's local AddForce is just streamed back to the
        // host's state ("pushes on his end, drifts right back"). PushSyncManager already has the full
        // send/apply path; it was simply never triggered - the old patches targeted OnActivate, which these
        // buttons don't override. They DO override ExtraFixedUpdate (where the force is applied). We use a
        // void prefix so the guest's ORIGINAL still runs (it manages the push collider's enable/disable), and
        // on click edges we forward the push to the host as INPUT; the host applies it to the authoritative
        // boat and the motion streams back to everyone.
        static readonly HashSet<GPButtonBoatPushCol> ActiveBoatPush = new HashSet<GPButtonBoatPushCol>();
        static readonly HashSet<GPButtonSailPusher> ActiveSailPush = new HashSet<GPButtonSailPusher>();
        // On-deck dock push. DockPushCol.ExtraFixedUpdate is a SEPARATE vanilla push collider; without its
        // own patch an on-deck push isn't relayed and BoatSyncManager nudges the boat back. Mirrors
        // BoatPushGuestPatch with a DOCK push type.
        static readonly HashSet<DockPushCol> ActiveDockPush = new HashSet<DockPushCol>();
        // Per-instance lean-in start distance for the boat-push gate (mirrors vanilla pushStartDistance).
        static readonly Dictionary<GPButtonBoatPushCol, float> BoatPushStartDist = new Dictionary<GPButtonBoatPushCol, float>();
        // Same lean-in gate for the dock push (vanilla DockPushCol also has a pushStartDistance + 0.25m gate).
        static readonly Dictionary<DockPushCol, float> DockPushStartDist = new Dictionary<DockPushCol, float>();
        static readonly AccessTools.FieldRef<GoPointerButton, bool> PushIsClickedRef =
            AccessTools.FieldRefAccess<GoPointerButton, bool>("isClicked");
        static readonly AccessTools.FieldRef<GoPointerButton, GoPointerMovement> PushIsClickedByRef =
            AccessTools.FieldRefAccess<GoPointerButton, GoPointerMovement>("isClickedBy");
        static readonly AccessTools.FieldRef<GPButtonBoatPushCol, float> BoatPushForceMultRef =
            AccessTools.FieldRefAccess<GPButtonBoatPushCol, float>("pushForceMult");
        static readonly AccessTools.FieldRef<GPButtonSailPusher, float> SailPushForceMultRef =
            AccessTools.FieldRefAccess<GPButtonSailPusher, float>("pushForceMult");
        // DockPushCol.pushForceMult is a private (non-serialized) constant (~ -0.55f); read it reflectively so
        // the host applies the matching force without an extra packet field.
        static readonly AccessTools.FieldRef<DockPushCol, float> DockPushForceMultRef =
            AccessTools.FieldRefAccess<DockPushCol, float>("pushForceMult");

        [HarmonyPatch(typeof(GPButtonBoatPushCol), "ExtraFixedUpdate")]
        public static class BoatPushGuestPatch
        {
            [HarmonyPrefix]
            public static void Prefix(GPButtonBoatPushCol __instance)
            {
                if (!Plugin.IsMultiplayer || Plugin.IsHost) return; // host runs vanilla push physics
                bool clicked = PushIsClickedRef(__instance);
                var clickedBy = PushIsClickedByRef(__instance);
                // Losing control (faint/sleep) does NOT produce a click-release edge, so an active
                // push would stay active and the host keeps driving the boat while the guest is out.
                // Treat "control disabled" as not-clicked: this fires the stop edge (sends PushStop) and
                // prevents the start branch from re-arming until control returns.
                // Also include PlayerSwimming.swimming - vanilla GPButtonBoatPushCol.ExtraFixedUpdate zeroes
                // the push force (num2=0) while swimming, so a guest that falls into the water mid-push must
                // stop forwarding force or the host keeps shoving the boat. (Boat push ONLY - the vanilla sail
                // pusher has no swimming gate, so SailPushGuestPatch must NOT add this.)
                bool controlDisabled = SailwindCoop.Patches.SurvivalPatches.PlayerNeedsPassOutPatch.SuppressDecay
                    || GameState.sleeping || GameState.inBed || PlayerSwimming.swimming
                    || (Refs.charController != null && !Refs.charController.enabled);
                clicked = clicked && !controlDisabled;

                // Mirror vanilla GPButtonBoatPushCol's lean-in distance gate. Vanilla applies force ONLY
                // while the pointer stays within pushStartDistance + 0.25m (you must keep leaning toward it);
                // backing away stops the push WITHOUT releasing the click. Forwarding only the click edge
                // would keep the host pushing after the guest leans back. Treat "out of lean range" as a stop edge
                // (and re-arm on return). Boat push ONLY - the sail pusher has no distance gate in vanilla.
                bool withinRange = true;
                if (clicked && clickedBy != null)
                {
                    float dist = Vector3.Distance(clickedBy.transform.position, __instance.transform.position);
                    if (!BoatPushStartDist.TryGetValue(__instance, out float start))
                    {
                        start = dist;
                        BoatPushStartDist[__instance] = start;
                    }
                    withinRange = dist <= start + 0.25f;
                }
                else
                {
                    BoatPushStartDist.Remove(__instance); // released/control-lost -> reset (vanilla: pushStartDistance = -1)
                }
                clicked = clicked && clickedBy != null && withinRange;

                bool wasActive = ActiveBoatPush.Contains(__instance);
                if (clicked && !wasActive)
                {
                    ActiveBoatPush.Add(__instance);
                    var boat = __instance.GetComponentInParent<SaveableObject>();
                    // forceMult = pushForceMult only; the host multiplies by the boat mass itself.
                    PushSyncManager.Instance?.OnLocalPushStart(PushSyncManager.PushTypeBoat, boat != null ? boat.gameObject.name : "",
                        BoatPushForceMultRef(__instance), clickedBy);
                }
                else if (!clicked && wasActive)
                {
                    if (controlDisabled) VerboseLogger.ControlLocal($"Push STOP forced by control-disabled (sleep/faint/bed/charController), not voluntary release");
                    ActiveBoatPush.Remove(__instance);
                    PushSyncManager.Instance?.OnLocalPushStop();
                }
            }
        }

        // On-deck dock push relay. DockPushCol applies force to the boat the guest is STANDING ON
        // (vanilla targets GameState.currentBoat.parent's rigidbody), with pushForceMult ~ -0.55, NO up-lift and
        // NO point offset. Mirror BoatPushGuestPatch: void prefix so vanilla still runs (it manages the col's
        // gameObject.layer), and on click edges forward the push to the host as a DOCK push type. The host
        // applies the matching force in PushSyncManager.FixedUpdate and the motion streams back to everyone.
        [HarmonyPatch(typeof(DockPushCol), "ExtraFixedUpdate")]
        public static class DockPushGuestPatch
        {
            [HarmonyPrefix]
            public static void Prefix(DockPushCol __instance)
            {
                if (!Plugin.IsMultiplayer || Plugin.IsHost) return; // host runs vanilla dock-push physics
                bool clicked = PushIsClickedRef(__instance);
                var clickedBy = PushIsClickedByRef(__instance);

                // Vanilla DockPushCol only applies force while GameState.currentBoat != null (on-deck push). If
                // the guest steps off the boat the push must stop. Treat off-boat as not-clicked.
                bool onBoat = GameState.currentBoat != null;

                // Mirror BoatPushGuestPatch: losing control (faint/sleep/in-bed/charController-off) does
                // NOT produce a click-release edge, so force the stop edge so the host stops shoving the boat.
                // No swimming gate here (dock push is on-deck; vanilla DockPushCol has no swimming term).
                bool controlDisabled = SailwindCoop.Patches.SurvivalPatches.PlayerNeedsPassOutPatch.SuppressDecay
                    || GameState.sleeping || GameState.inBed
                    || (Refs.charController != null && !Refs.charController.enabled);
                clicked = clicked && onBoat && !controlDisabled;

                // Mirror BoatPushGuestPatch's lean-in gate: vanilla DockPushCol applies force ONLY while the pointer stays
                // within pushStartDistance + 0.25m (keep leaning toward it); backing away stops the push WITHOUT
                // releasing the click. Treat "out of lean range" as a stop edge (and re-arm on return).
                bool withinRange = true;
                if (clicked && clickedBy != null)
                {
                    float dist = Vector3.Distance(clickedBy.transform.position, __instance.transform.position);
                    if (!DockPushStartDist.TryGetValue(__instance, out float start))
                    {
                        start = dist;
                        DockPushStartDist[__instance] = start;
                    }
                    withinRange = dist <= start + 0.25f;
                }
                else
                {
                    DockPushStartDist.Remove(__instance); // released/control-lost/off-boat -> reset (vanilla: pushStartDistance = -1)
                }
                clicked = clicked && clickedBy != null && withinRange;

                bool wasActive = ActiveDockPush.Contains(__instance);
                if (clicked && !wasActive)
                {
                    ActiveDockPush.Add(__instance);
                    // Dock push targets the boat the guest is standing ON (vanilla GameState.currentBoat.parent),
                    // not a parent of the dock collider. Resolve it the same way BoatUtility does.
                    var boat = BoatUtility.GetCurrentBoat();
                    // forceMult = DockPushCol.pushForceMult (~ -0.55) only; the host multiplies by the hull mass.
                    PushSyncManager.Instance?.OnLocalPushStart(PushSyncManager.PushTypeDock, boat != null ? boat.gameObject.name : "",
                        DockPushForceMultRef(__instance), clickedBy);
                }
                else if (!clicked && wasActive)
                {
                    if (controlDisabled || !onBoat) VerboseLogger.ControlLocal($"Dock push STOP forced by control-disabled/off-boat, not voluntary release");
                    ActiveDockPush.Remove(__instance);
                    PushSyncManager.Instance?.OnLocalPushStop();
                }
            }
        }

        [HarmonyPatch(typeof(GPButtonSailPusher), "ExtraFixedUpdate")]
        public static class SailPushGuestPatch
        {
            [HarmonyPrefix]
            public static bool Prefix(GPButtonSailPusher __instance)
            {
                if (!Plugin.IsMultiplayer || Plugin.IsHost) return true; // host runs vanilla sail-push physics
                bool clicked = PushIsClickedRef(__instance);
                var clickedBy = PushIsClickedByRef(__instance);
                // See BoatPushGuestPatch - treat control-disabled (faint/sleep/in-bed) as not-clicked
                // so the stop edge fires (sends PushStop) and the start branch can't re-arm until control returns.
                bool controlDisabled = SailwindCoop.Patches.SurvivalPatches.PlayerNeedsPassOutPatch.SuppressDecay
                    || GameState.sleeping || GameState.inBed
                    || (Refs.charController != null && !Refs.charController.enabled);
                clicked = clicked && !controlDisabled;
                bool wasActive = ActiveSailPush.Contains(__instance);
                if (clicked && clickedBy != null && !wasActive)
                {
                    ActiveSailPush.Add(__instance);
                    var boat = __instance.GetComponentInParent<SaveableObject>();
                    // Identify WHICH sail pusher this is by its index among the boat's pushers, so the host
                    // applies force to the matching sail's own rigidbody (not the hull). GetComponentsInChildren
                    // is deterministic (hierarchy order), so host and guest resolve the same pusher.
                    int sailIndex = -1;
                    if (boat != null)
                    {
                        var pushers = boat.GetComponentsInChildren<GPButtonSailPusher>(true);
                        sailIndex = System.Array.IndexOf(pushers, __instance);
                    }
                    PushSyncManager.Instance?.OnLocalPushStart(PushSyncManager.PushTypeSail, boat != null ? boat.gameObject.name : "",
                        SailPushForceMultRef(__instance), clickedBy, sailIndex);
                }
                else if (!clicked && wasActive)
                {
                    if (controlDisabled) VerboseLogger.ControlLocal($"Push STOP forced by control-disabled (sleep/faint/bed/charController), not voluntary release");
                    ActiveSailPush.Remove(__instance);
                    PushSyncManager.Instance?.OnLocalPushStop();
                }

                // Suppress the vanilla local force on the guest: the sail rigidbody/boat is host-authoritative
                // and streamed back, so applying force locally fights the correction stream (jitter/rubber-band).
                // GPButtonSailPusher.ExtraFixedUpdate does ONLY force application (no collider management), so
                // skipping it is safe. (BoatPushGuestPatch stays a void prefix - its vanilla manages col.enabled
                // and is already self-gated by currentBoat==null.)
                return false;
            }
        }

        // === CONTROLLER STICK-DRIFT DEADZONE (config-gated vanilla bug mitigation) ===
        // Vanilla GoPointerMovement.ApplyKeyboardRotation (decomp ~77-98) adds GetPrimaryVertical()/
        // GetPrimaryHorizontal() to keyboardDelta UNSCALED by Time.deltaTime (the MoveUp/MoveDown key terms
        // ARE dt-scaled), and GPButtonRopeWinch.Update then DIVIDES the delta by Time.deltaTime - so idle
        // gamepad stick drift is amplified ~20-60x into a constant let-out on any grabbed winch (and a slow
        // wheel creep via GetKeyboardDelta().x). Postfix: when a stick axis is below the configured
        // deadzone, ADD BACK exactly the term vanilla subtracted (y -= vertical * keyboardMult;
        // x -= horizontal * keyboardMult * 0.05f), leaving key input untouched. Vanilla resets keyboardDelta
        // at the top of ApplyKeyboardRotation every frame and consumers read GetKeyboardDelta() afterwards,
        // so a postfix here filters the value before anything consumes it. NOT gated on IsMultiplayer - the
        // drift bug is vanilla; ControllerDeadzone=0 disables the behavior entirely.

        static readonly AccessTools.FieldRef<GoPointerMovement, Vector3> KeyboardDeltaRef =
            AccessTools.FieldRefAccess<GoPointerMovement, Vector3>("keyboardDelta");

        [HarmonyPatch(typeof(GoPointerMovement), "ApplyKeyboardRotation")]
        public static class ControllerDeadzonePatch
        {
            [HarmonyPostfix]
            public static void Postfix(GoPointerMovement __instance)
            {
                float deadzone = Plugin.ControllerDeadzoneConfig?.Value ?? 0f;
                if (deadzone <= 0f) return;

                float vertical = GameInput.GetPrimaryVertical();
                float horizontal = GameInput.GetPrimaryHorizontal();
                bool cullVertical = Mathf.Abs(vertical) < deadzone && vertical != 0f;
                bool cullHorizontal = Mathf.Abs(horizontal) < deadzone && horizontal != 0f;
                if (!cullVertical && !cullHorizontal) return;

                ref Vector3 keyboardDelta = ref KeyboardDeltaRef(__instance);
                if (cullVertical)
                    keyboardDelta.y += vertical * __instance.keyboardMult;
                if (cullHorizontal)
                    keyboardDelta.x += horizontal * __instance.keyboardMult * 0.05f;
            }
        }

        // === ANCHOR PATCHES (event-based, these methods exist on Anchor class) ===

        // Anchor.Awake reparents the anchor OUT of the boat hierarchy and stores the BOAT ROOT's
        // SaveableObject in the private `boatSaveable` field (from joint.connectedBody). The old
        // GetComponentInParent<SaveableObject>() resolved the anchor's OWN saveable (e.g. "anchor_M"),
        // so the receiver's FindAllBoats lookup (keyed on boat ROOT names) missed and silently dropped
        // every anchor event. Read boatSaveable instead; fall back to the joint's connected body.
        static readonly AccessTools.FieldRef<Anchor, SaveableObject> AnchorBoatSaveableRef =
            AccessTools.FieldRefAccess<Anchor, SaveableObject>("boatSaveable");

        static SaveableObject ResolveAnchorBoat(Anchor anchor)
        {
            var boat = AnchorBoatSaveableRef(anchor);
            if (boat != null) return boat;
            var connected = anchor.GetComponent<ConfigurableJoint>()?.connectedBody;
            return connected != null ? connected.GetComponent<SaveableObject>() : null;
        }

        // ANCHOR AUTHORITY WAR (Jav1k 0711, v0.2.28): vanilla Anchor.ExtraFixedUpdate AUTO-releases a
        // set anchor (taut joint at <60 deg, or winched below 8m) and AUTO-sets a loose one lying flat
        // on ground - a purely local sim that BOTH machines run on a synced anchor. Each side's postfix
        // then broadcast those automatic "corrections": guest applies host's set=True, its stale/taut
        // local geometry auto-releases one fixed frame later and broadcasts set=False; host applies
        // that, its anchor is genuinely grounded so it auto-sets and re-broadcasts set=True - a ~3s
        // ping-pong that flips the guest anchor's kinematic state against a taut joint every cycle
        // (ship "spazzing out, flipping, vibrating, diving" on the guest). Fix: the HOST is the anchor
        // authority. On GUESTS, vanilla's automatic transitions are gated by KIND (adversarial-review
        // refinement) so the ping-pong cannot survive even a hands-on window:
        //   - RELEASE: allowed only while the anchor is CURRENTLY held (Anchor.cs `set && held ->
        //     ReleaseAnchor`, a real manual pull-up). The taut-release and winch-release branches always
        //     run with held==null, so this blocks the set=False half of the loop UNCONDITIONALLY.
        //   - SET: allowed only if the anchor was held within the last few seconds - a guest's own
        //     physical drop grounds+sets a frame or two after it leaves the hand, so the recent-hold
        //     stamp (taken in the scope prefix while held!=null) covers that legit ground-set.
        // Rope winching needs no exception: the winch drives rope length, which streams to the host,
        // whose own auto logic performs the release/set authoritatively and broadcasts it back.
        private static bool _inAnchorAutoUpdate;
        private static readonly System.Collections.Generic.Dictionary<int, float> _anchorLastHeldTime
            = new System.Collections.Generic.Dictionary<int, float>();
        private const float AnchorLocalInteractionWindow = 10f;

        private static bool AnchorRecentlyHeldLocally(Anchor anchor)
        {
            if (anchor.held != null) return true;
            return _anchorLastHeldTime.TryGetValue(anchor.GetInstanceID(), out var t)
                   && UnityEngine.Time.time - t < AnchorLocalInteractionWindow;
        }

        /// <summary>True = let vanilla run; false = block a guest-side automatic transition.
        /// isRelease distinguishes the two vanilla calls: a release is host-authoritative unless the
        /// guest is literally holding the anchor; a set is allowed briefly after a local hold (drop).</summary>
        private static bool AllowAnchorTransition(Anchor __instance, bool isRelease)
        {
            if (!Plugin.IsMultiplayer || Plugin.IsHost) return true;               // host sim is authoritative
            if (ControlSyncManager.Instance?.IsApplyingRemoteState == true) return true; // host-sent state applies freely
            if (!_inAnchorAutoUpdate) return true;                                  // manual/load paths untouched

            if (isRelease)
            {
                // Only a hands-on pull-up may release + broadcast; every automatic release is the host's call.
                if (__instance.held != null) return true;
            }
            else if (AnchorRecentlyHeldLocally(__instance))
            {
                return true; // the guest's own drop grounding into a set
            }

            VerboseLogger.ControlLocal($"Blocked guest auto anchor {(isRelease ? "release" : "set")} (host-authoritative)");
            return false;
        }

        [HarmonyPatch(typeof(Anchor), "ExtraFixedUpdate")]
        public static class AnchorAutoUpdateScopePatch
        {
            [HarmonyPrefix]
            public static void Prefix(Anchor __instance)
            {
                if (!Plugin.IsMultiplayer) return;
                if (__instance.held != null)
                    _anchorLastHeldTime[__instance.GetInstanceID()] = UnityEngine.Time.time;
                _inAnchorAutoUpdate = true;
            }

            // Finalizer, not postfix: the flag must clear even if vanilla throws mid-update,
            // or every later manual transition would be misclassified as auto.
            [HarmonyFinalizer]
            public static void Finalizer()
            {
                _inAnchorAutoUpdate = false;
            }
        }

        [HarmonyPatch(typeof(Anchor), "SetAnchor")]
        public static class AnchorSetPatch
        {
            [HarmonyPrefix]
            public static bool Prefix(Anchor __instance, ref bool __state)
            {
                __state = AllowAnchorTransition(__instance, isRelease: false);
                return __state;
            }

            [HarmonyPostfix]
            public static void Postfix(Anchor __instance, bool __state)
            {
                if (!__state) return; // transition was blocked - vanilla did not run, nothing to broadcast
                if (!Plugin.IsMultiplayer) return;
                // Prevent feedback loop when applying remote state
                if (ControlSyncManager.Instance?.IsApplyingRemoteState == true) return;

                var boat = ResolveAnchorBoat(__instance);
                if (boat == null)
                {
                    Plugin.Log.LogWarning("AnchorSetPatch: could not resolve boat root for anchor - event NOT sent");
                    return;
                }

                var joint = __instance.GetComponent<ConfigurableJoint>();
                var ropeLength = joint?.linearLimit.limit ?? 0f;

                VerboseLogger.ControlLocal($"Anchor set, boat={boat.gameObject.name}, ropeLen={ropeLength:F2}");

                ControlSyncManager.Instance?.OnLocalAnchorChanged(
                    boat.gameObject.name,
                    true,
                    ropeLength
                );
            }
        }

        [HarmonyPatch(typeof(Anchor), "ReleaseAnchor")]
        public static class AnchorReleasePatch
        {
            [HarmonyPrefix]
            public static bool Prefix(Anchor __instance, ref bool __state)
            {
                __state = AllowAnchorTransition(__instance, isRelease: true);
                return __state;
            }

            [HarmonyPostfix]
            public static void Postfix(Anchor __instance, bool __state)
            {
                if (!__state) return; // transition was blocked - vanilla did not run, nothing to broadcast
                if (!Plugin.IsMultiplayer) return;
                // Prevent feedback loop when applying remote state
                if (ControlSyncManager.Instance?.IsApplyingRemoteState == true) return;

                var boat = ResolveAnchorBoat(__instance);
                if (boat == null)
                {
                    Plugin.Log.LogWarning("AnchorReleasePatch: could not resolve boat root for anchor - event NOT sent");
                    return;
                }

                var joint = __instance.GetComponent<ConfigurableJoint>();
                var ropeLength = joint?.linearLimit.limit ?? 0f;

                VerboseLogger.ControlLocal($"Anchor released, boat={boat.gameObject.name}, ropeLen={ropeLength:F2}");

                ControlSyncManager.Instance?.OnLocalAnchorChanged(
                    boat.gameObject.name,
                    false,
                    ropeLength
                );
            }
        }

        // === MOORING PATCHES ===

        [HarmonyPatch(typeof(PickupableBoatMooringRope), "MoorTo")]
        public static class MooringAttachPatch
        {
            [HarmonyPostfix]
            public static void Postfix(PickupableBoatMooringRope __instance, GPButtonDockMooring mooring)
            {
                if (!Plugin.IsMultiplayer) return;

                // PHANTOM-LOAD GATE (Robin 0711, "host loses mooring ropes when loading in"): a joining
                // guest's own phantom save restores a was-moored rope detached at the cleat, and vanilla's
                // OnTriggerEnter re-moors it DURING the load - a purely local, pre-authoritative event.
                // Broadcasting it sent the guest's phantom moor (with a mid-load floating-origin dockPos)
                // to the host as if it were a player action. The host's join snapshot is the sole authority
                // for mooring state; nothing from the phantom world may be broadcast. The third clause
                // covers the connected-but-pre-snapshot gap: SuppressLoadErrors clears right after
                // JoinLobby, but until the host's BoatWorldState arrives this guest's world is still its
                // own phantom (HasReceivedWorldState is guest-only, false until the first snapshot).
                if (TitleJoinManager.SuppressLoadErrors || BoatSyncManager.IsJoinInProgress
                    || (!Plugin.IsHost && !BoatSyncManager.HasReceivedWorldState)) return;

                bool isApplying = ControlSyncManager.Instance?.IsApplyingRemoteState == true;
                bool wasRecent = ControlSyncManager.Instance?.WasRecentlyChangedByNetwork(__instance) == true;

                if (isApplying) return;
                if (wasRecent) return;

                var boat = __instance.GetBoatRigidbody()?.GetComponent<SaveableObject>();
                if (boat == null) return;

                var mooringRopes = boat.GetComponent<BoatMooringRopes>();
                if (mooringRopes == null) return;

                var ropeIndex = System.Array.IndexOf(mooringRopes.ropes, __instance);
                if (ropeIndex < 0) return;

                // Convert dock position from local to real (offset-independent) coordinates
                // Receiver will add their offset to find dock in their coordinate system
                //
                // (v0.2.32) Tow-aware target: a TowingCleat (Towable Boats; also baked into the
                // Leopard prefab) is a GPButtonDockMooring ON A MOVING BOAT - a world position is
                // stale the moment the tow boat moves, so boat targets travel as a
                // (towBoatName, cleatPath) reference instead.
                if (SailwindCoop.Compat.TowableBoatsCompat.IsTowingCleat(mooring))
                {
                    var towBoat = mooring.GetComponentInParent<SaveableObject>();
                    var cleatPath = towBoat != null
                        ? Sync.SyncPathUtil.GetRelativePath(towBoat.transform, mooring.transform) : null;
                    if (towBoat != null)
                    {
                        if (string.IsNullOrEmpty(cleatPath))
                        {
                            // (v0.2.32 review) Path derivation failed (an unaddressable name, a cleat
                            // re-parented off the boat root). Send the cleat packet ANYWAY, with an EMPTY
                            // path: the receiver's cleat resolve fails cleanly (FindByRelativePath returns
                            // null for an empty path) -> retry -> stow. NEVER fall back to the dock-position
                            // send here: a moving cleat's world position can land within the receiver's 5m
                            // XZ match radius of a REAL pier bollard and moor the boat to the DOCK instead
                            // of the tow - a silent, physically wrong moor is far worse than a stowed rope.
                            Plugin.Log.LogWarning($"Cleat moor could not derive a path on towBoat={towBoat.gameObject.name}; " +
                                                  "sending an unresolvable cleat target (receiver stows the rope) rather than a dock position");
                        }
                        else
                        {
                            VerboseLogger.ControlLocal($"Mooring attached to CLEAT, boat={boat.gameObject.name}, rope={ropeIndex}, towBoat={towBoat.gameObject.name}, cleat={cleatPath}");
                        }

                        Sync.BoatUtility.UpdateTowStreamPin(boat); // (P4) host-only inside; rescan-based
                        ControlSyncManager.Instance?.OnLocalMooringChanged(
                            boat.gameObject.name, ropeIndex, true, Vector3.zero,
                            __instance.currentRopeLengthSquared,
                            Networking.Packets.MooringTargetKind.BoatCleat,
                            towBoat.gameObject.name, cleatPath ?? string.Empty);
                        return;
                    }
                    // No SaveableObject above the cleat at all: there is no boat reference to send, so the
                    // dock-position path below is the only thing left (and its 5m match is a real chance).
                    Plugin.Log.LogWarning("Cleat moor could not resolve a towing boat (no SaveableObject parent); falling back to dock-position sync");
                }

                var offset = FloatingOriginManager.instance?.outCurrentOffset ?? Vector3.zero;
                var realDockPos = mooring.transform.position - offset;

                VerboseLogger.ControlLocal($"Mooring attached, boat={boat.gameObject.name}, rope={ropeIndex}, dock={mooring?.name}, realDockPos={realDockPos}");

                ControlSyncManager.Instance?.OnLocalMooringChanged(
                    boat.gameObject.name,
                    ropeIndex,
                    true,
                    realDockPos,
                    __instance.currentRopeLengthSquared
                );
            }
        }

        [HarmonyPatch(typeof(PickupableBoatMooringRope), "Unmoor")]
        public static class MooringDetachPatch
        {
            // Vanilla Unmoor() is a silent NO-OP when the rope has no spring (mooredToSpring == null),
            // but a Harmony postfix fires regardless. Vanilla calls Unmoor() speculatively on ropes that
            // are NOT moored - BoatMooringRopes.UnmoorAllRopes() inside SaveableObject.Load for EVERY
            // boat, plus Recovery and Shipyard paths - and any of those firing on a connected peer
            // broadcast an authoritative "unmoor rope N" that cut a perfectly real moor on every OTHER
            // machine (Robin 0711: ropes gone for host on load, client still moored, boat tug-of-war
            // until the client released). Only broadcast a transition that actually happened:
            // moored -> unmoored.
            [HarmonyPrefix]
            public static void Prefix(PickupableBoatMooringRope __instance, out bool __state)
            {
                __state = __instance != null && __instance.IsMoored();
            }

            [HarmonyPostfix]
            public static void Postfix(PickupableBoatMooringRope __instance, bool __state)
            {
                VerboseLogger.ControlLocal($"Unmoor PATCH FIRED, rope={__instance?.name}, isMP={Plugin.IsMultiplayer}, wasMoored={__state}");

                if (!__state) return; // no-op Unmoor on an already-unmoored rope: nothing to sync
                if (!Plugin.IsMultiplayer) return;

                // PHANTOM-LOAD GATE: same reasoning as MooringAttachPatch - nothing that happens to the
                // guest's phantom world during a title-join load/apply (or before the first host
                // snapshot arrives) may be broadcast as authoritative.
                if (TitleJoinManager.SuppressLoadErrors || BoatSyncManager.IsJoinInProgress
                    || (!Plugin.IsHost && !BoatSyncManager.HasReceivedWorldState)) return;

                bool isApplying = ControlSyncManager.Instance?.IsApplyingRemoteState == true;
                bool wasRecent = ControlSyncManager.Instance?.WasRecentlyChangedByNetwork(__instance) == true;

                VerboseLogger.ControlLocal($"Unmoor checks: isApplying={isApplying}, wasRecent={wasRecent}");

                if (isApplying) return;
                if (wasRecent) return;

                var boatRb = __instance.GetBoatRigidbody();
                var boat = boatRb?.GetComponent<SaveableObject>();
                VerboseLogger.ControlLocal($"Unmoor boat lookup: rb={boatRb?.name}, boat={boat?.gameObject.name}");

                if (boat == null) return;

                var mooringRopes = boat.GetComponent<BoatMooringRopes>();
                if (mooringRopes == null) return;

                var ropeIndex = System.Array.IndexOf(mooringRopes.ropes, __instance);
                if (ropeIndex < 0) return;

                VerboseLogger.ControlLocal($"Mooring detached, boat={boat.gameObject.name}, rope={ropeIndex}");

                ControlSyncManager.Instance?.OnLocalMooringChanged(
                    boat.gameObject.name,
                    ropeIndex,
                    false,
                    Vector3.zero,
                    0f
                );

                Sync.BoatUtility.UpdateTowStreamPin(boat); // (P4) rescan: only unpins when NO rope still holds a tow, never the deployed cutter
            }
        }

        // === MOORING ROPE LENGTH ADJUSTMENT ===
        // ChangeRopeLength is called when player scrolls while holding MooringRopeLengthAdjuster

        [HarmonyPatch(typeof(PickupableBoatMooringRope), "ChangeRopeLength")]
        public static class MooringRopeLengthPatch
        {
            [HarmonyPostfix]
            public static void Postfix(PickupableBoatMooringRope __instance, bool __result)
            {
                // Only send if length actually changed (method returned true)
                if (!__result) return;
                if (!Plugin.IsMultiplayer) return;
                // Prevent feedback loop when applying remote state
                if (ControlSyncManager.Instance?.IsApplyingRemoteState == true) return;

                var boat = __instance.GetBoatRigidbody()?.GetComponent<SaveableObject>();
                if (boat == null) return;

                var mooringRopes = boat.GetComponent<BoatMooringRopes>();
                if (mooringRopes == null) return;

                var ropeIndex = System.Array.IndexOf(mooringRopes.ropes, __instance);
                if (ropeIndex < 0) return;

                VerboseLogger.ControlLocal($"Mooring length changed, boat={boat.gameObject.name}, rope={ropeIndex}, lenSq={__instance.currentRopeLengthSquared:F2}");

                ControlSyncManager.Instance?.OnLocalMooringRopeLengthChanged(
                    boat.gameObject.name,
                    ropeIndex,
                    __instance.currentRopeLengthSquared
                );
            }
        }

        // === TOW-CLEAT TRIGGER GUARD (v0.2.32) ===
        // Vanilla auto-moors an unheld, displaced rope to ANY GPButtonDockMooring collider it touches
        // (decomp PickupableBoatMooringRope.cs:223-233). Towable Boats makes cleats-on-hulls such
        // targets, so a loose rope brushing a passing boat spontaneously creates a TOW on whichever
        // peers happen to run the trigger - including during load, where the save-restore overlap is
        // the mod's (order-dependent, non-deterministic) tow resurrection path. Tows must be
        // host-authoritative: guests never create one locally; the host's MoorTo broadcast
        // (MooringAttachPatch cleat branch) re-creates it for them. Dock triggers keep their existing
        // (playtested) local semantics. Also blocks SELF-tows on every machine: the mod's own guard
        // only covers OnItemClick (TowingCleat.cs:13), not the trigger path, and a SpringJoint whose
        // connectedBody is its own hull is undefined/explosive PhysX.
        [HarmonyPatch(typeof(PickupableBoatMooringRope), "OnTriggerEnter")]
        public static class MooringCleatTriggerGuardPatch
        {
            [HarmonyPrefix]
            public static bool Prefix(PickupableBoatMooringRope __instance, Collider other)
            {
                if (other == null) return true;
                if (!SailwindCoop.Compat.TowableBoatsCompat.HasTowingCleat(other.gameObject)) return true;

                // Self-tow guard (all machines, singleplayer included - it is a real mod bug).
                var cleatBoat = other.GetComponentInParent<SaveableObject>();
                var ropeBoat = __instance.GetBoatRigidbody()?.GetComponent<SaveableObject>();
                if (cleatBoat != null && cleatBoat == ropeBoat)
                {
                    VerboseLogger.ControlLocal($"Blocked SELF-tow trigger moor on {cleatBoat.gameObject.name}");
                    return false;
                }

                if (!Plugin.IsMultiplayer) return true;
                if (Plugin.IsHost) return true; // host is the tow authority

                // Guest: never trigger-moor to a cleat locally; the host's broadcast re-creates real tows.
                VerboseLogger.ControlLocal($"Suppressed guest trigger-moor to cleat {other.name}");
                return false;
            }
        }

        // === STEERING WHEEL PATCH - Guest input interception ===

        [HarmonyPatch(typeof(GPButtonSteeringWheel), "ExtraLateUpdate")]
        public static class SteeringWheelGuestPatch
        {
            // Access private/protected fields
            private static readonly AccessTools.FieldRef<GPButtonSteeringWheel, float> InputMultRef =
                AccessTools.FieldRefAccess<GPButtonSteeringWheel, float>("inputMult");
            private static readonly AccessTools.FieldRef<GPButtonSteeringWheel, TouchRotateHandle> RotHandleRef =
                AccessTools.FieldRefAccess<GPButtonSteeringWheel, TouchRotateHandle>("rotHandle");
            private static readonly AccessTools.FieldRef<GoPointerButton, GoPointer> StickyClickedByRef =
                AccessTools.FieldRefAccess<GoPointerButton, GoPointer>("stickyClickedBy");
            private static readonly AccessTools.FieldRef<GoPointerButton, bool> IsClickedRef =
                AccessTools.FieldRefAccess<GoPointerButton, bool>("isClicked");
            private static readonly AccessTools.FieldRef<GoPointerButton, GoPointerMovement> IsClickedByRef =
                AccessTools.FieldRefAccess<GoPointerButton, GoPointerMovement>("isClickedBy");

            // Access rotation limit for clamping
            private static readonly AccessTools.FieldRef<GPButtonSteeringWheel, float> RotationAngleLimitRef =
                AccessTools.FieldRefAccess<GPButtonSteeringWheel, float>("rotationAngleLimit");

            // Access locked field for helm lock sync
            public static readonly AccessTools.FieldRef<GPButtonSteeringWheel, bool> LockedRef =
                AccessTools.FieldRefAccess<GPButtonSteeringWheel, bool>("locked");

            [HarmonyPrefix]
            public static bool Prefix(GPButtonSteeringWheel __instance)
            {
                PatchProfiler.Begin("SteeringWheel.ExtraLateUpdate");

                if (!Plugin.IsMultiplayer)
                {
                    PatchProfiler.End("SteeringWheel.ExtraLateUpdate");
                    return true;  // Let original run
                }
                if (Plugin.IsHost)
                {
                    PatchProfiler.End("SteeringWheel.ExtraLateUpdate");
                    return true;  // Host uses normal logic
                }

                // Guest: intercept input and send to host instead of applying locally
                var stickyClickedBy = StickyClickedByRef(__instance);
                bool isClicked = IsClickedRef(__instance);
                var isClickedBy = IsClickedByRef(__instance);
                var rotHandle = RotHandleRef(__instance);
                float inputMult = InputMultRef(__instance);
                bool isLocked = LockedRef(__instance);

                bool isSteering = stickyClickedBy != null || isClicked || (rotHandle != null && rotHandle.IsGrabbed());

                if (isSteering)
                {
                    // Check for lock toggle (right-click / AltButtonDown)
                    bool altPressed = false;
                    if (stickyClickedBy != null)
                    {
                        altPressed = stickyClickedBy.AltButtonDown();
                    }
                    else if (isClicked && Settings.steeringWithMouse && isClickedBy != null)
                    {
                        altPressed = isClickedBy.pointer.AltButtonDown();
                    }

                    if (altPressed)
                    {
                        var boat = __instance.GetComponentInParent<SaveableObject>();
                        if (boat != null)
                        {
                            // Send lock toggle request to host
                            ControlSyncManager.Instance?.OnLocalHelmLockToggle(boat.gameObject.name);
                            VerboseLogger.ControlLocal($"Guest helm lock toggle request sent, boat={boat.gameObject.name}");

                            // If locking, release the player from helm (same as game's Lock() does)
                            bool wasLocked = isLocked;
                            if (!wasLocked && stickyClickedBy != null)
                            {
                                // Call UnStickyClick to release player
                                __instance.UnStickyClick();
                            }
                        }
                    }

                    // Only process steering input if not locked
                    if (!isLocked)
                    {
                        float inputDelta = 0f;

                        if (stickyClickedBy != null)
                        {
                            // Keyboard steering mode
                            var keyboardDelta = stickyClickedBy.movement.GetKeyboardDelta();
                            inputDelta = keyboardDelta.x * inputMult * 0.1f;
                        }
                        else if (isClicked && Settings.steeringWithMouse && isClickedBy != null)
                        {
                            // Mouse steering mode
                            var deltaRot = isClickedBy.GetDeltaRotation();
                            inputDelta = deltaRot.z * inputMult;
                        }

                        if (Mathf.Abs(inputDelta) > 0.0001f)
                        {
                            var boat = __instance.GetComponentInParent<SaveableObject>();
                            if (boat != null)
                            {
                                // Send input to host
                                ControlSyncManager.Instance?.OnLocalHelmInput(boat.gameObject.name, inputDelta);

                                // C2: predict locally ONLY if the host hasn't denied us. A guest grabbing a wheel
                                // another crew member is steering gets a HelmDenied; while denied we must NOT
                                // predict (OnRemoteHelmChanged drives currentInput from the authoritative state so
                                // our wheel follows the real rudder), but we still SENT the input above so the host
                                // can grant us the lease the instant the current holder releases.
                                if (ControlSyncManager.Instance?.IsHelmDenied(boat.gameObject.name) != true)
                                {
                                    // Apply locally for prediction (host will correct via sync)
                                    __instance.currentInput += inputDelta;

                                    // Clamp to rotation limits
                                    float rotationAngleLimit = RotationAngleLimitRef(__instance);
                                    if (__instance.currentInput > rotationAngleLimit)
                                        __instance.currentInput = rotationAngleLimit;
                                    if (__instance.currentInput < -rotationAngleLimit)
                                        __instance.currentInput = -rotationAngleLimit;

                                    // Set HingeJoint spring target - physics will smooth the movement
                                    // Game's ExtraLateUpdate will read rudder.currentAngle and set wheel visual
                                    if (__instance.attachedRudder != null)
                                    {
                                        float rudderAngle = __instance.currentInput / __instance.gearRatio;
                                        var spring = __instance.attachedRudder.spring;
                                        spring.targetPosition = __instance.attachedRudder.limits.max * (__instance.currentInput / rotationAngleLimit);
                                        __instance.attachedRudder.spring = spring;

                                        // The spring target alone only animates the wheel - the
                                        // rudder hinge relaxes back to center on the guest, so the boat never
                                        // actually turns (BoatSyncManager only soft-nudges rotation). Mirror the
                                        // HOST apply path (ControlSyncManager.OnRemoteHelmInput ~741-746): drive
                                        // the rudder transform + Rudder.currentAngle DIRECTLY. Rudder.FixedUpdate
                                        // reads currentAngle to AddRelativeTorque on the ship rigidbody, so the
                                        // guest's own boat physically rotates from its own steering. Guest-MP-only
                                        // (this whole branch is gated !IsHost above); solo/host are unchanged.
                                        var rudder = __instance.attachedRudder.GetComponent<Rudder>();
                                        if (rudder != null)
                                        {
                                            var euler = rudder.transform.localEulerAngles;
                                            rudder.transform.localEulerAngles = new Vector3(euler.x, rudderAngle, euler.z);
                                            rudder.currentAngle = rudderAngle;
                                        }
                                    }

                                    VerboseLogger.ControlLocal($"Guest helm input: delta={inputDelta:F3}, newInput={__instance.currentInput:F1}");
                                }
                            }
                        }
                    }

                    // Update wheel visual ourselves since we skip original ExtraLateUpdate
                    // Wheel visual is code-driven (not physics), so we must set it explicitly
                    __instance.transform.localEulerAngles = new Vector3(__instance.currentInput, 0f, 0f);

                    // Skip original - we handled input and wheel visual above
                    PatchProfiler.End("SteeringWheel.ExtraLateUpdate");
                    return false;
                }

                // Not steering - let original ExtraLateUpdate run
                // It will read rudder.currentAngle (from physics) and update wheel visual smoothly
                PatchProfiler.End("SteeringWheel.ExtraLateUpdate");
                return true;
            }
        }

    }
}
