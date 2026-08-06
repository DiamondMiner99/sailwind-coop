using UnityEngine;
using SailwindCoop.Debug;
using SailwindCoop.Networking.Packets;
using HarmonyLib;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Manages continuous player position synchronization.
    /// Sends at 20Hz regardless of movement (otherwise the remote capsule stays in place during helm/capstan/bed use).
    /// </summary>
    public class PlayerSyncManager : MonoBehaviour
    {
        public static PlayerSyncManager Instance { get; private set; }

        private const float SyncInterval = 0.05f; // 20 Hz
        private float _lastSyncTime;
        private GoPointer _cachedGoPointer;

        // CROUCH (v0.2.25): cached vanilla PlayerCrouching (lives on Refs.ovrCameraRig) + its private
        // initialHeight (the camera rig's standing local head height, captured in its Awake). Vanilla
        // crouch is purely the head height lerping initialHeight <-> 0.2 (t = dt*9), so normalizing
        // GetCurrentHeadHeight between those endpoints yields a smooth 0..1 crouch amount that already
        // reflects every vanilla cancel path (bed, jump, swimming) - no extra state to track.
        private PlayerCrouching _cachedCrouching;
        private float _crouchStandingHeight = -1f;

        // LOOK-LEAN: cached ref-accessor for MouseLook's PRIVATE clamped vertical-look field `rotationY`
        // (positive = looking UP, clamped ~[-60,60]). The instance is resolved by IDENTITY - see
        // SampleHeadLookPitchDeg. (v0.3.0: the old scene-wide scan that took the largest |rotationY| is
        // gone; that heuristic was a real bug, not merely a slow lookup, and its rationale is deleted here
        // rather than left sitting next to the code that disproves it.)
        private static readonly AccessTools.FieldRef<MouseLook, float> MouseLookRotationYRef =
            AccessTools.FieldRefAccess<MouseLook, float>("rotationY");
        // (v0.2.25) empty-scan throttle: earliest realtime a missed MouseLook re-scan may run again.
        private static float _nextMouseLookScanTime;
        private const float MouseLookRescanInterval = 1.5f;

        // A (guest-world-pinned-underway): embark self-heal watchdog state. Vanilla runs TWO parallel
        // embark state machines (PlayerEmbarkDisembarkTrigger + PlayerEmbarkerNew) whose predicates can
        // deadlock during moored on/off cargo cycles (static embarked sticks true / no fresh EmbarkCol
        // OnTriggerEnter), leaving the guest's CharacterController parented to "_shifting world" while
        // physically standing on the deck - when the host then unmoors, the guest is pinned in the world
        // and the boat sails out from under them. The watchdog detects "world-parented but standing on a
        // crew boat collider" sustained for EmbarkProbeRequiredHits consecutive probes (~1s dwell, so a
        // genuine jump/dock stand never trips it; dock colliders have no BoatRefs parent) and force-heals
        // via BoatStateApplicator.ForceEmbarkLocalPlayer - deliberately predicate-agnostic: it repairs
        // the pin whichever vanilla field stuck, rather than patching one fragile vanilla trigger path.
        private const float EmbarkProbeInterval = 0.25f;
        private const int EmbarkProbeRequiredHits = 4;
        private float _lastEmbarkProbeTime;
        private int _embarkProbeHits;
        private Transform _embarkProbeBoatRoot;

        private void Awake()
        {
            if (Instance != null)
            {
                Destroy(this);
                return;
            }
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }

        private void Update()
        {
            if (!Plugin.IsMultiplayer) return;

            var charController = Refs.charController;
            if (charController == null) return;

            // Runs before the 20Hz send gate on its own (slower) cadence, so the position rate limit
            // can't starve the probe.
            EmbarkSelfHealTick(charController);
            OutOfWorldRescueTick(charController);

            // Rate limit to 20 Hz.
            // (v0.2.37) SLEEP-WARP SCALE. Scaled Time.time runs 16x during a co-op sleep, so this gate fired
            // at up to 320Hz of real time. Note the ROLE-AGNOSTIC scale: unlike the other senders this Update
            // has no IsHost gate (every client streams its own avatar), so both peers are warped and
            // HostSleepSendIntervalScale would only fix the host half. At 16f the real rate during a sleep is
            // exactly the design 20Hz. Documented residual from the v0.2.35 scaling pass, now closed.
            // Avatars updating at 20Hz behind a black screen is not observable. No wire change.
            // WATCHDOG SAFETY (checked, because this is the ONLY channel that feeds it): PlayerPosition is
            // the sole writer of RemoteAvatar.LastRemotePacketTime (RemotePlayerManager.UpdatePosition), which
            // drives TryGetPeerSilence and hence SleepSyncManager's 12s unresponsive-crewmate abort. Worst-case
            // gap after this change is ~0.8s real, not 12s: during the ~3.1s before the warp starts, scaled
            // time == real time so the gate is 0.8s real (1.25Hz); once the warp is running, 0.8 scaled == 0.05
            // real (the design 20Hz); and on a frame-starved client the clamp makes each frame advance 1.6
            // scaled, which clears the 0.8 gate EVERY frame, so it degrades to the client's own frame rate, not
            // to silence. Margin over the 12s threshold is ~15x in the worst case.
            if (Time.time - _lastSyncTime < SyncInterval * SleepSyncManager.SleepSendIntervalScale) return;
            _lastSyncTime = Time.time;

            // (v0.3.0) DELIBERATELY NOT GATED ON IsJoinInProgress. An earlier cut of this fix added
            // `if (BoatSyncManager.IsJoinInProgress) return;` here, to stop peers watching a joiner descend
            // the 50m terrain-load perch. Adversarial review killed it, and the reason is the comment block
            // directly above: PlayerPosition is the ONLY writer of RemoteAvatar.LastRemotePacketTime, which
            // feeds the 12s unresponsive-crewmate watchdog. That gate is set on the GUEST only - the host
            // never sets it - so the host's watchdog would keep running against a peer we had just silenced
            // for the whole join (the codebase's own estimate is 15-20s, worst case 65s+). Both existing
            // watchdog exclusions are already spent by then: _joinPendingPeers is cleared in the same method
            // that sends the snapshot, and HasStreamedPacket is true for any real joiner. Net effect would be
            // a mid-sleep join aborting the entire crew's sleep - a far worse bug than the cosmetic one it set
            // out to fix.
            //
            // The descent itself is already fixed at its source: BoatStateApplicator now disables player
            // control before the perch teleport, so the joiner no longer falls and peers see them parked,
            // not plummeting. Suppressing the stream on top of that bought very little and cost the liveness
            // contract. If the parked-at-altitude frame ever needs hiding too, do it WITHOUT muting this
            // channel (e.g. hold the pre-join pose and keep sending it, or hide the avatar receiver-side).
            SendPlayerPosition(charController);
        }

        // (v0.3.0) Out-of-world rescue. See OutOfWorldRescueTick.
        private const float OutOfWorldProbeInterval = 1f;
        private const float OutOfWorldFloorY = -300f;    // well below the seabed at any island
        private const float OutOfWorldCeilingY = 2000f;  // well above any mast, cliff or storm
        private const float OutOfWorldRescueCooldown = 10f;
        private float _lastOutOfWorldProbeTime;
        private float _lastOutOfWorldRescueTime = -999f;
        private int _outOfWorldHits;

        /// <summary>
        /// (v0.3.0) Bring back a crewmate who has fallen out of the world.
        ///
        /// A guest currently has NO way back. Vanilla's safety net is WorldBorder, which after two minutes
        /// out of bounds calls Recovery.RecoverPlayer - and this mod disables WorldBorder outright for
        /// guests, and separately refuses guest-side recovery because recovering the shared boat is the
        /// captain's business. Both decisions are right on their own and together they leave a hole: a guest
        /// who ends up under the seabed keeps falling until they close the game. Two new players hit exactly
        /// this on a Reddit thread and concluded co-op did not work, which is a fair reading.
        ///
        /// The sibling watchdog above cannot help, because it heals by raycasting for a deck to stand on and
        /// someone in free fall is not standing on anything. This one keys on the only thing still true in
        /// that state: they are somewhere no part of the world exists.
        ///
        /// The rescue re-seats them on the crew boat rather than calling vanilla's Recovery, so nothing about
        /// the SHARED boat is touched - it moves the person, not the ship. Requires several consecutive
        /// probes so that a long legitimate fall (off a mast, off a cliff) is never interrupted mid-air, and
        /// rate-limits itself so a rescue that lands somewhere still bad cannot become a teleport loop.
        /// </summary>
        private void OutOfWorldRescueTick(CharacterController charController)
        {
            if (Time.time - _lastOutOfWorldProbeTime < OutOfWorldProbeInterval) return;
            _lastOutOfWorldProbeTime = Time.time;

            // Guest-only, and never during the states that legitimately park the player outside the world:
            // the join teleport parks them above the boat by design, recovery is vanilla moving them, and a
            // sleep warp has its own placement.
            if (Plugin.IsHost || !Plugin.IsMultiplayer
                || BoatSyncManager.IsJoinInProgress
                || GameState.recovering
                || GameState.sleeping)
            {
                _outOfWorldHits = 0;
                return;
            }

            float y = charController.transform.position.y;
            if (y > OutOfWorldFloorY && y < OutOfWorldCeilingY) { _outOfWorldHits = 0; return; }

            // Three seconds of being nowhere, not one frame of it.
            if (++_outOfWorldHits < 3) return;
            _outOfWorldHits = 0;

            if (Time.time - _lastOutOfWorldRescueTime < OutOfWorldRescueCooldown) return;
            _lastOutOfWorldRescueTime = Time.time;

            try
            {
                var boat = ResolveRescueBoat();
                var refs = boat != null ? boat.GetComponent<BoatRefs>() : null;
                var deck = refs != null ? refs.boatModel : null;
                if (deck == null)
                {
                    // Nothing to put them back onto. Say so rather than failing silently: without a boat
                    // this is unrecoverable in-session and the player needs to know that is what happened.
                    Plugin.Log.LogError($"[PLAYER:RESCUE] Player is out of the world at y={y:F0} and there is " +
                        "no crew boat to return them to.");
                    Plugin.Notify("You have fallen out of the world and there is no ship to return you to. " +
                        "Rejoining the crew is the only way back.", 12f);
                    return;
                }

                Plugin.Log.LogWarning($"[PLAYER:RESCUE] Player out of the world at y={y:F0}; returning them to " +
                    $"'{boat.gameObject.name}'.");

                // Place them ABOVE the deck and let them settle, the same "slightly high self-corrects,
                // slightly low clips" rule the join teleport uses.
                var target = deck.position + Vector3.up * 3f;
                bool wasEnabled = charController.enabled;
                charController.enabled = false;   // CharacterController ignores transform writes while enabled
                charController.transform.position = target;
                charController.enabled = wasEnabled;

                // Re-establish the deck parenting too, or they stand on a moving ship in the world frame and
                // get left behind the moment it makes way.
                BoatStateApplicator.ForceEmbarkLocalPlayer(boat.transform);

                Plugin.Notify("You fell out of the world. Back aboard.", 6f);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogError("[PLAYER:RESCUE] could not return the player to the ship: " + e.Message);
            }
        }

        /// <summary>
        /// (v0.3.0) Find the ship to put a fallen crewmate back on, using state that SURVIVES falling.
        ///
        /// The obvious answer, GameState.currentBoat, is the wrong one, and wrong in precisely the case this
        /// rescue exists for. Vanilla nulls that field on every disembark path, and going into the sea IS a
        /// disembark: PlayerEmbarkerNew disembarks after about three fixed frames of swimming, and anyone
        /// falling to y &lt; -300 passed through the water on the way. So by the time the watchdog's
        /// three-second dwell completes, the field it would have used has been cleared for roughly three
        /// seconds. The rescue would have reported "there is no ship to return you to" while the ship sat
        /// moored nearby - the exact dead end it was written to remove.
        ///
        /// GameState.lastBoat is only ever written on embark and vanilla never clears it, and the crew boat's
        /// name is recorded independently at join time. Either outlives the fall.
        /// </summary>
        private static SaveableObject ResolveRescueBoat()
        {
            var boat = BoatUtility.GetCurrentBoat();
            if (boat != null) return boat;

            // lastBoat: set on embark, never nulled by vanilla.
            var last = GameState.lastBoat;
            if (last != null)
            {
                var saveable = last.GetComponent<SaveableObject>();
                if (saveable != null) return saveable;
            }

            // The crew boat by name, recorded when the join seated us on it.
            string shared = SleepSyncManager.Instance != null ? SleepSyncManager.Instance.SharedBoatName : null;
            if (!string.IsNullOrEmpty(shared))
            {
                var byName = BoatUtility.FindBoatByName(shared);
                if (byName != null) return byName;
            }

            return null;
        }

        /// <summary>
        /// A (guest-world-pinned-underway): ~4Hz probe; see the field-block comment for the mechanism.
        /// Heal = the SAME dual-frame transfer the join path uses (ForceEmbarkLocalPlayer), which
        /// preserves the world pose - so on a moored boat the heal is visually a no-op and underway it
        /// snaps the guest's parenting back onto the deck they are already standing on.
        /// </summary>
        private void EmbarkSelfHealTick(CharacterController charController)
        {
            if (Time.time - _lastEmbarkProbeTime < EmbarkProbeInterval) return;
            _lastEmbarkProbeTime = Time.time;

            // Guest-only self-heal (the host's own embark state is authoritative on its own machine and
            // this failure mode is co-op-specific: the HOST unmoors while the GUEST is mid-cargo-cycle).
            // Skip every transient/legit world-parented state: join teleport in flight, recovery,
            // co-op sleep warp, and swimming (a swimmer is SUPPOSED to be world-parented next to the hull).
            if (Plugin.IsHost
                || BoatSyncManager.IsJoinInProgress
                || GameState.recovering
                || GameState.sleeping
                || PlayerSwimming.observerSwimming)
            {
                _embarkProbeHits = 0; _embarkProbeBoatRoot = null;
                return;
            }

            // Only the pinned state is interesting: charController parented directly to "_shifting world"
            // (the exact same parent test the 20Hz sender uses for onBoat, so watchdog and wire agree).
            var parent = charController.transform.parent;
            if (parent == null || parent.name != "_shifting world")
            {
                _embarkProbeHits = 0; _embarkProbeBoatRoot = null;
                return;
            }

            // Probe: short raycast straight down from the FEET (observerMirror tracks the controller
            // origin ~capsule center; drop by the live capsule geometry, same math as the 20Hz sender).
            // Solids only - EmbarkCol/dock trigger volumes must not count as "standing on".
            var bodyT = Refs.observerMirror != null ? Refs.observerMirror.transform : charController.transform;
            var feet = bodyT.position + Vector3.down * ControllerFeetGap();
            if (!Physics.Raycast(feet + Vector3.up * 0.25f, Vector3.down, out var hit, 2.75f,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                _embarkProbeHits = 0; _embarkProbeBoatRoot = null;
                return;
            }

            // "Standing on a crew boat" = the hit collider lives under a BoatRefs root (hull/deck/railing
            // colliders are all children of the boat root) that is NOT an NPC trader boat - the guest
            // must never be force-embarked onto AI traffic they happen to stand on.
            var boatRefs = hit.collider.GetComponentInParent<BoatRefs>();
            if (boatRefs == null || hit.collider.GetComponentInParent<NPCBoatController>() != null)
            {
                _embarkProbeHits = 0; _embarkProbeBoatRoot = null;
                return;
            }

            // DOCK-EDGE guard (review finding): a player overhanging the edge of a DOCK beside a moored
            // crew boat can have the feet ray miss the dock and hit the hull/railing 1-2m below - four
            // such probes would force-embark someone genuinely standing on land, the inverse of the bug
            // this watchdog heals. Someone actually STANDING on the deck has geometry directly underfoot,
            // so require a short hit distance (ray starts 0.25m above the feet).
            if (hit.distance > 0.9f)
            {
                _embarkProbeHits = 0; _embarkProbeBoatRoot = null;
                return;
            }

            // Require the SAME boat across all consecutive hits so a probe can't accumulate across
            // different boats (e.g. hopping between two moored hulls).
            if (boatRefs.transform != _embarkProbeBoatRoot)
            {
                _embarkProbeBoatRoot = boatRefs.transform;
                _embarkProbeHits = 1;
                return;
            }
            if (++_embarkProbeHits < EmbarkProbeRequiredHits) return;

            // ~1s of sustained world-parented-on-deck: the vanilla machines are deadlocked. Before
            // healing, dump the four vanilla embark fields so the next playtest log captures WHICH
            // predicate stuck (static-embarked-true vs missed EmbarkCol re-Enter) - the logs so far
            // could not distinguish them, and this dump is the designed instrument to do so.
            var boatRoot = _embarkProbeBoatRoot;
            _embarkProbeHits = 0; _embarkProbeBoatRoot = null;
            try
            {
                var trigger = Object.FindObjectOfType<PlayerEmbarkDisembarkTrigger>();
                object stayedTrigger = trigger != null
                    ? Traverse.Create(trigger).Field("currentlyStayedTrigger").GetValue()
                    : "no-trigger-component";
                var embarker = charController.GetComponent<PlayerEmbarkerNew>()
                               ?? Object.FindObjectOfType<PlayerEmbarkerNew>();
                object embarkerEmbarked = "no-embarker", embarkerBoat = "no-embarker";
                if (embarker != null)
                {
                    var et = Traverse.Create(embarker);
                    embarkerEmbarked = et.Field("embarked").GetValue();
                    embarkerBoat = et.Field("currentBoat").GetValue();
                }
                Plugin.Log.LogWarning(
                    $"[PLAYER:EMBARK-HEAL] World-pinned on '{boatRoot.name}' for {EmbarkProbeRequiredHits} probes. " +
                    $"Pre-heal vanilla state: Trigger.embarked(static)={PlayerEmbarkDisembarkTrigger.embarked}, " +
                    $"Trigger.currentlyStayedTrigger={stayedTrigger ?? "null"}, " +
                    $"Embarker.embarked={embarkerEmbarked}, Embarker.currentBoat={embarkerBoat ?? "null"}");
            }
            catch (System.Exception e)
            {
                // The dump is diagnostics only - never let a Traverse hiccup block the heal itself.
                Plugin.Log.LogWarning($"[PLAYER:EMBARK-HEAL] pre-heal state dump failed (non-fatal): {e.Message}");
            }

            bool healed = BoatStateApplicator.ForceEmbarkLocalPlayer(boatRoot);
            Plugin.Log.LogWarning($"[PLAYER:EMBARK-HEAL] ForceEmbarkLocalPlayer('{boatRoot.name}') => {(healed ? "healed" : "FAILED (walkCol unresolved)")}");
        }

        /// <summary>
        /// FLOAT-ON-BOAT fix: vertical distance (metres) from the CharacterController ORIGIN (which
        /// Refs.observerMirror tracks, ~capsule center) down to the capsule bottom (feet) =
        /// height/2 - center.y. Derived from live controller geometry so it stays correct across the
        /// runtime height scaling PlayerEmbarkerNew applies (col.height = initialHeight*1.05). Subtract
        /// this from the boat-local Y of the observerMirror position so the wire value is FEET, matching
        /// the on-land camera-feet contract. Returns 0 if the controller isn't available (no shift).
        /// Shared by BoatStateCollector's join snapshot so both paths agree (else the avatar pops on the
        /// first 20Hz packet after join).
        /// </summary>
        public static float ControllerFeetGap()
        {
            var cc = Refs.charController;
            if (cc == null) return 0f;
            return cc.height * 0.5f - cc.center.y;
        }

        /// <summary>
        /// CROUCH (v0.2.25): normalized 0..1 crouch amount for the LOCAL player, sampled from the vanilla
        /// PlayerCrouching head-height lerp (standing initialHeight -> crouched 0.2). Sending the lerped
        /// AMOUNT (not the bool) lets remote avatars reproduce the smooth stand/crouch transition even at
        /// 20Hz. Returns 0 when the component/height isn't available yet (pre-load, degenerate rig).
        /// </summary>
        private float SampleCrouch01()
        {
            if (_cachedCrouching == null)
            {
                var rig = Refs.ovrCameraRig;
                if (rig != null) _cachedCrouching = rig.GetComponent<PlayerCrouching>();
                if (_cachedCrouching == null) return 0f;
                // Private field, set once in PlayerCrouching.Awake (= rig localPosition.y while standing).
                _crouchStandingHeight = Traverse.Create(_cachedCrouching).Field("initialHeight").GetValue<float>();
            }
            // Degenerate standing height (component not initialized, or a rig where standing ~ crouched):
            // treat as not crouching rather than emitting garbage.
            if (_crouchStandingHeight <= 0.3f) return 0f;
            float head = _cachedCrouching.GetCurrentHeadHeight();
            // currentHeadHeight starts at 0 and only lerps while GameState.playing; a raw 0 would
            // normalize to FULL crouch, so treat the uninitialized band as standing (the real crouched
            // endpoint is 0.2 and the lerp approaches it from above).
            if (head < 0.1f) return 0f;
            return Mathf.Clamp01(Mathf.InverseLerp(_crouchStandingHeight, 0.2f, head));
        }

        /// <summary>
        /// LOOK-LEAN: the LOCAL player's clamped vertical look angle in degrees (~[-60,60]; positive = looking
        /// UP), read from the vanilla MouseLook.rotationY private field.
        ///
        /// (v0.3.0) Resolved by IDENTITY - the MouseLook on Refs.ovrCameraRig, which is the player head's
        /// vertical look. It used to scan every MouseLook in the scene and take the LARGEST ABSOLUTE
        /// rotationY, on the stated assumption that only the vertical head instance is ever non-zero. That
        /// assumption is false and it produced a reported bug: a crewmate's avatar was seen folded fully
        /// forward for a whole session, and only recovered when they toggled to the orbit camera and back.
        ///
        /// Vanilla has at least FIVE MouseLook instances (player yaw, player pitch, the bed/TrackingSpace
        /// look, BoatCamera orbit yaw, BoatCamera orbit pitch). rotationY is a private accumulator written
        /// ONLY in MouseLook.Update, so an instance that gets enabled=false (BoatCamera.SwitchOff, the
        /// shipyard rotator) FREEZES its last value forever - nothing in vanilla or this mod ever resets it.
        /// A parked orbit pitch sitting at its -60 clamp therefore wins the max-abs contest permanently and
        /// decodes to a ~54 degree spine fold against the 55 degree cap: visually maxed. The camera toggle
        /// "fixed" it only because it made that instance live-driven again.
        ///
        /// DO NOT "improve" this by filtering on ml.enabled - that INVERTS the bug. During the orbit camera
        /// the player looks are DISABLED and the boat looks ENABLED, so an enabled-filter would make the
        /// avatar mirror the orbit camera's pitch instead of holding the player's last first-person pitch.
        /// The invariant to preserve: while the orbit cam is on, the sent pitch stays the player's head
        /// pitch.
        ///
        /// Fails SAFE: if the head MouseLook cannot be resolved we return 0 (no lean, neutral spine) rather
        /// than guessing from another instance. A missing lean is a cosmetic nothing; a wrong one is the bug
        /// above.
        /// </summary>
        private float SampleLocalLookPitchDeg() => SampleHeadLookPitchDeg();

        /// <summary>
        /// Shared head-pitch sampler. Public+static so LocalPlayerBody's third-person body and the networked
        /// avatar cannot diverge - they previously held byte-identical copies of this logic, including the
        /// same wrong comment, which is why the defect existed in two places at once.
        /// </summary>
        public static float SampleHeadLookPitchDeg()
        {
            if (_headMouseLook == null)
            {
                // Throttle the re-resolve: during menus/loading the rig does not exist and this would
                // otherwise probe every call on the 20Hz path.
                float now = Time.realtimeSinceStartup;
                if (now < _nextMouseLookScanTime) return 0f;

                var rig = Refs.ovrCameraRig;
                _headMouseLook = rig != null ? rig.GetComponent<MouseLook>() : null;
                if (_headMouseLook == null)
                {
                    _nextMouseLookScanTime = now + MouseLookRescanInterval;
                    return 0f;
                }
            }
            return MouseLookRotationYRef(_headMouseLook);
        }

        /// <summary>The player head's vertical MouseLook. Null until resolved / after a scene change.</summary>
        private static MouseLook _headMouseLook;

        private void SendPlayerPosition(CharacterController charController)
        {
            var position = charController.transform.position;
            // ROTATION SOURCE: in first person the camera yaw IS the body's facing, so use it. But in the
            // ship-orbit ("third person") camera the camera ORBITS the boat independent of the body, so
            // sourcing yaw from it makes the remote avatar spin in place while you stand still. In orbit,
            // take yaw from the body (observerMirror/controller) instead - the actual facing, camera-
            // independent. The receiver applies yaw-only regardless, so a yaw-only quat here is fine.
            Quaternion rotation;
            if (BoatCamera.on)
            {
                var bodyT = Refs.observerMirror != null ? Refs.observerMirror.transform : charController.transform;
                rotation = Quaternion.Euler(0f, bodyT.eulerAngles.y, 0f);
            }
            else
            {
                rotation = Camera.main != null ? Camera.main.transform.rotation : Quaternion.identity;
            }

            // Determine coordinate system based on player's parent
            var playerParent = charController.transform.parent;
            bool isOnBoat = false;
            Vector3 relativePos;
            string boatName = "";

            // Check if player is parented to a boat (not "_shifting world" which is the land parent)
            if (playerParent != null && playerParent.name != "_shifting world")
            {
                // Player is on a boat - use VISUAL boat-relative coordinates
                // Key insight: Use GameState.currentBoat (visual model) not walkCollider (physics)
                // This matches what receiver uses (embarkCollider.transform.parent = boatModel)
                isOnBoat = true;

                var visualBoat = GameState.currentBoat;
                if (visualBoat != null)
                {
                    // BOAT-NAME FRAME fix: coords stay boatModel-LOCAL (visualBoat below), but the NAME we send
                    // must be the boat ROOT SaveableObject's name - that is the key BoatUtility._cachedBoats is
                    // keyed by, and the receiver's FindBoatByName looks up. GameState.currentBoat.name is the
                    // boatMODEL (visual child) name, which is NOT in that dictionary, so the strict by-name
                    // resolve on the receiver always missed and the avatar fell to a wrong-frame fallback (~205m
                    // off or invisible). Vanilla EnterBoat sets GameState.lastBoat = boatModel.parent = the root
                    // SaveableObject while aboard, so lastBoat.name IS the root key. (Item-sync already parent-
                    // hops to this same root name via item.currentActualBoat.parent.SaveableObject.name.)
                    boatName = GameState.lastBoat != null ? GameState.lastBoat.name : visualBoat.name;
                    // FIX (remote avatar stuck at ship-center underway / jumps to camera in third
                    // person): derive the sent position from the player BODY, not the camera chain.
                    // The old method (Camera.main.parent.parent.parent.localPosition) leaked the
                    // boat-orbit camera position in third person, and resolved to the physics
                    // walkCollider frame underway (mismatching the receiver's visual frame).
                    // Refs.observerMirror tracks the controller base in the VISUAL boat frame and is
                    // camera-independent, so it is correct in both camera modes and has no wave bob.
                    var bodyTransform = Refs.observerMirror != null ? Refs.observerMirror.transform : null;
                    if (bodyTransform != null)
                    {
                        relativePos = visualBoat.transform.InverseTransformPoint(bodyTransform.position);
                        // FLOAT-ON-BOAT fix: observerMirror tracks the CONTROLLER ORIGIN (~capsule center,
                        // ~0.3m above the feet) in the visual boat frame. Sending it directly floated the
                        // remote avatar, because the receiver plants the avatar's FEET at the sent point
                        // (matching the on-land camera-feet contract). Drop to the capsule bottom using the
                        // CharacterController geometry (height/center, already scaled at runtime by
                        // PlayerEmbarkerNew) so we transmit FEET, not center. gap = height/2 - center.y
                        // (~0.3m), applied in boat-local Y to match the receiver's boat-local round trip.
                        relativePos.y -= ControllerFeetGap();
                    }
                    else
                    {
                        // Fallback when observerMirror is missing. (v0.3.0) Was camera minus a constant 1.7m
                        // eye height, which is the same crouch bug the land branch below had: crouch moves the
                        // camera rig and nothing else, so a crouched player transmitted feet ~0.95m too low.
                        // Sourced from the controller instead, to match the primary path above.
                        relativePos = visualBoat.transform.InverseTransformPoint(charController.transform.position);
                        relativePos.y -= ControllerFeetGap();
                    }

                    VerboseLogger.PlayerSend($"OnBoat (visual), boat={boatName}, relPos={relativePos}", throttle: true);
                }
                else
                {
                    // Fallback when GameState.currentBoat is null: send the boat ROOT SaveableObject name
                    // (the key the receiver's FindBoatByName resolves - walkCol name is unresolvable).
                    // Coordinates stay in the PARENT (walkCol) local frame: by the dual-frame invariant the
                    // walkCol and boatModel share local coords, so the walkCol-local point IS the boat-local
                    // point the receiver's boatModel.TransformPoint expects. (Inverse-transforming the
                    // walkCol-frame WORLD position through the boatModel would be ~205m off underway.)
                    var rootSaveable = playerParent.GetComponentInParent<SaveableObject>();
                    boatName = rootSaveable != null ? rootSaveable.name : playerParent.name;
                    relativePos = playerParent.InverseTransformPoint(position);
                    Plugin.Log.LogWarning($"[SEND] GameState.currentBoat is NULL, falling back to root SaveableObject (boat={boatName})");
                }
            }
            else
            {
                // Player is on land - world coordinates at FEET level.
                isOnBoat = false;
                boatName = "";

                // (v0.3.0) CROUCH FIX: feet come from the CONTROLLER, not the camera.
                //
                // This used to be `Camera.main.position - 1.7f`, and vanilla crouch is implemented purely as
                // a camera-rig height lerp: PlayerCrouching lerps currentHeadHeight down to 0.2, and HeadBob
                // writes that straight into the rig's localPosition. Nothing in the game ever changes the
                // CharacterController's height or centre. So a crouching player's camera drops ~0.95m while
                // their capsule does not move at all, the constant 1.7 subtracted the whole crouch a second
                // time, and the receiver plants the avatar's soles exactly on the point it is sent - burying
                // a crouched crewmate in the dock.
                //
                // The on-boat branch above was always immune because it already sources observerMirror and
                // subtracts ControllerFeetGap(). This makes land agree with it, so "feet" now means one thing
                // on the wire instead of two. It also takes head-bob and landing-bob out of the transmitted
                // position, which were riding along in the old camera-derived value.
                //
                // The RECEIVER is correct and must not be touched: its plant offsets and crouch leg IK are
                // symmetric between land and boat, which is exactly why this bug was land-only.
                var feetPos = position - new Vector3(0f, ControllerFeetGap(), 0f);

                // Convert to REAL (offset-independent) position, so sender and receiver can disagree about
                // their FloatingOriginManager offsets.
                var offset = FloatingOriginManager.instance?.outCurrentOffset ?? Vector3.zero;
                relativePos = feetPos - offset;

                VerboseLogger.PlayerSend($"OnLand, feetPos={feetPos}, realPos={relativePos}", throttle: true);
            }

            // Check if player is holding an item
            bool hasHeldItem = false;
            int heldItemId = 0;
            Vector3 heldItemPos = Vector3.zero;
            Quaternion heldItemRot = Quaternion.identity;

            // Cache GoPointer reference (expensive to find every frame)
            if (_cachedGoPointer == null)
                _cachedGoPointer = Object.FindObjectOfType<GoPointer>();

            if (_cachedGoPointer != null)
            {
                var heldItem = _cachedGoPointer.GetHeldItem() as ShipItem;
                if (heldItem != null)
                {
                    var prefab = heldItem.GetComponent<SaveablePrefab>();
                    if (prefab != null)
                    {
                        hasHeldItem = true;
                        heldItemId = prefab.instanceId;
                        // Use boat-relative if on boat, otherwise REAL world position.
                        //
                        // (v0.3.0) The frame is chosen from `visualBoat`, but the isOnBoat FLAG on the wire
                        // is derived separately (from the player's parent). Those two could disagree: when
                        // GameState.currentBoat is null while the player is still parented to a hull, this
                        // sent a REAL-WORLD point under isOnBoat=TRUE, and the receiver - which picks its
                        // frame purely from the flag - fed that world point through boatModel.TransformPoint.
                        // The held item lands wherever the hull's transform maps a world coordinate to, i.e.
                        // wildly wrong, until the player re-grabs it.
                        //
                        // Reachable, not theoretical: vanilla Shipyard.DischargeShip nulls
                        // GameState.currentBoat without touching the player's parent, so any player holding
                        // something as a shipyard releases their ship hits it.
                        //
                        // Fix is to resolve the VISUAL model another way rather than to change the flag.
                        // Deliberately NOT changing the wire flag: there is only one, and the avatar's own
                        // frame depends on it, so repurposing it would be a wire change requiring the whole
                        // crew to update. Deliberately NOT using the walkCol/physics frame either: a held
                        // item is posed by GoPointer off the CAMERA, which lives in the visual frame, so
                        // inverse-transforming through walkCol would be ~205m out underway.
                        var visualBoat = GameState.currentBoat;
                        if (isOnBoat && visualBoat == null)
                        {
                            // Resolve from the player's PARENT, exactly as the avatar branch above already
                            // does to derive the boatName that goes on the wire. NOT BoatUtility
                            // .GetCurrentBoat(): its first statement is `if (GameState.currentBoat == null)
                            // return null`, i.e. the very condition we are in, so that fallback was dead code.
                            var boatRoot = playerParent != null ? playerParent.GetComponentInParent<SaveableObject>() : null;
                            var refs = boatRoot != null ? boatRoot.GetComponent<BoatRefs>() : null;
                            if (refs != null && refs.boatModel != null)
                            {
                                visualBoat = refs.boatModel;
                                VerboseLogger.PlayerSend("Held item: GameState.currentBoat null but still parented to a " +
                                    "hull; resolved the visual model via BoatRefs so the pose matches the isOnBoat flag.",
                                    throttle: true);
                            }
                        }

                        if (isOnBoat && visualBoat != null)
                        {
                            heldItemPos = visualBoat.transform.InverseTransformPoint(heldItem.transform.position);
                            heldItemRot = Quaternion.Inverse(visualBoat.transform.rotation) * heldItem.transform.rotation;
                        }
                        else
                        {
                            // Convert to REAL position for on-land held items
                            var itemOffset = FloatingOriginManager.instance?.outCurrentOffset ?? Vector3.zero;
                            heldItemPos = heldItem.transform.position - itemOffset;
                            heldItemRot = heldItem.transform.rotation;
                        }
                    }
                }
            }

            // N-player STAR: the host relays this position to other guests, so the transport-level sender
            // becomes the HOST. Carry the real AUTHOR (this player's own SteamId) in the body as the first
            // field, so receivers identify whose position this is regardless of who relayed it. At N=1 the
            // author equals the one guest, so the single-avatar receive path is unchanged.
            ulong authorSteamId = Steamworks.SteamClient.SteamId.Value;

            // CROUCH (v0.2.25 wire change): quantize the 0..1 crouch amount to a byte and append it as
            // the LAST field, AFTER the optional held-item block. Trailing-append keeps the packet
            // readable by pre-v0.2.25 receivers (their reads stop before it) and the receiver probes
            // remaining stream length so an old sender's shorter packet still parses.
            byte crouchByte = (byte)Mathf.RoundToInt(SampleCrouch01() * 255f);

            // LOOK-LEAN (wire change): quantize the local vertical look pitch to ONE signed byte and append it
            // AFTER the crouch byte. [-90,90] deg -> [0,255] (128 = 0 deg). Same trailing-append + stream-length
            // probe contract as crouch, so a pre-look receiver just stops before it and a pre-look sender's
            // shorter packet still parses (receiver reads neutral 128 = no lean).
            byte lookByte = (byte)Mathf.RoundToInt(Mathf.Clamp(SampleLocalLookPitchDeg(), -90f, 90f) / 90f * 127f + 128f);

            Plugin.NetworkManager.SendToAllUnreliable(PacketType.PlayerPosition, writer =>
            {
                writer.Write(authorSteamId);
                writer.Write(isOnBoat);
                writer.Write(boatName);
                writer.Write(relativePos.x);
                writer.Write(relativePos.y);
                writer.Write(relativePos.z);
                writer.Write(rotation.x);
                writer.Write(rotation.y);
                writer.Write(rotation.z);
                writer.Write(rotation.w);
                // Held item data
                writer.Write(hasHeldItem);
                if (hasHeldItem)
                {
                    writer.Write(heldItemId);
                    writer.Write(heldItemPos.x);
                    writer.Write(heldItemPos.y);
                    writer.Write(heldItemPos.z);
                    writer.Write(heldItemRot.x);
                    writer.Write(heldItemRot.y);
                    writer.Write(heldItemRot.z);
                    writer.Write(heldItemRot.w);
                }
                writer.Write(crouchByte); // CROUCH (v0.2.25): trailing 0-255 crouch amount
                writer.Write(lookByte);   // LOOK-LEAN: trailing signed look-pitch byte (after crouch)
            });
        }
    }
}
