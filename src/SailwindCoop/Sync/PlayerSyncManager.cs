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

            // Rate limit to 20 Hz
            if (Time.time - _lastSyncTime < SyncInterval) return;
            _lastSyncTime = Time.time;

            SendPlayerPosition(charController);
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
                        // Fallback: camera feet projected into the visual boat frame (already FEET)
                        const float eyeHeight = 1.7f;
                        var cameraFeetPos = Camera.main.transform.position - new Vector3(0, eyeHeight, 0);
                        relativePos = visualBoat.transform.InverseTransformPoint(cameraFeetPos);
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
                // Player is on land - use world coordinates at FEET level
                // Use camera position minus eye height (same approach as on-boat case)
                // This ensures receiver can add capsuleHalfHeight consistently
                isOnBoat = false;
                boatName = "";
                const float eyeHeight = 1.7f;
                var cameraFeetPos = Camera.main != null
                    ? Camera.main.transform.position - new Vector3(0, eyeHeight, 0)
                    : position; // fallback to charController center if no camera

                // Convert to REAL (offset-independent) position
                // This ensures correct position when sender/receiver have different FloatingOriginManager offsets
                var offset = FloatingOriginManager.instance?.outCurrentOffset ?? Vector3.zero;
                relativePos = cameraFeetPos - offset;

                VerboseLogger.PlayerSend($"OnLand, localFeetPos={cameraFeetPos}, realPos={relativePos}", throttle: true);
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
                        // Use boat-relative if on boat, otherwise REAL world position
                        var visualBoat = GameState.currentBoat;
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
            });
        }
    }
}
