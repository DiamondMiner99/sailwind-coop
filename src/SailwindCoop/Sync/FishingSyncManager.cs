using System.Collections.Generic;
using UnityEngine;
using HarmonyLib;
using Steamworks;
using SailwindCoop.Debug;
using SailwindCoop.Networking.Packets;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Manages fishing rod synchronization.
    /// Owner-authoritative for fishing experience, host-authoritative for items.
    /// </summary>
    public class FishingSyncManager : MonoBehaviour
    {
        public static FishingSyncManager Instance { get; private set; }

        // Rod ownership: rodInstanceId -> ownerSteamId (0 = no owner)
        private Dictionary<int, ulong> _rodOwners = new Dictionary<int, ulong>();

        // Rods with hooked fish (for 5Hz state sync)
        private HashSet<int> _hookedRods = new HashSet<int>();

        // K1: rods that have been CAST (bobber out) but not necessarily hooked. Vanilla pays the line out
        // via the auto-unroll in ShipItemFishingRod.Update, which only unrolls meaningfully on the OWNER's
        // machine (the viewer's bobber is never launched, so its joint sees no pull; note viewer rods do
        // NOT have held=false - ItemSyncManager fakes a non-null `held` on remote-held items, which is why
        // vanilla's held-gated code, including the FishingRodFish bite roll, still runs on viewers). Without
        // streaming, the viewer's line stays at minLength from cast until the first FishingState at the
        // bite. Owned rods in (_castRods U _hookedRods) stream FishingLineLength at 5Hz, coalesced by
        // LineLengthCoalesceDelta.
        private readonly HashSet<int> _castRods = new HashSet<int>();
        private readonly Dictionary<int, float> _lastSentLineLength = new Dictionary<int, float>();
        private const float LineLengthCoalesceDelta = 0.25f; // metres of change before a resend

        // B (bobber stream, send side): the bobber launch is emergent local physics on the owner
        // (measured rod angular velocity + the owner's camera forward, decomp ShipItemFishingRod
        // :264-289), so a viewer's bobber never leaves the rod - it dangles vertically through the
        // deck, puts floater.InWater=true under the boat and mislocates the hooked-fish mesh. Owned
        // rods in (_castRods U _hookedRods) stream the bobber position at 5Hz, coalesced on world
        // movement > BobberCoalesceDelta.
        private readonly Dictionary<int, Vector3> _lastSentBobberPos = new Dictionary<int, Vector3>();
        private const float BobberCoalesceDelta = 0.5f; // metres of world movement before a resend

        // B (receive side): remote-owned bobbers are made kinematic (joint/float forces ignored) and
        // lerped to the streamed target every frame; a boat-frame target tracks the boat between
        // packets. Keyed by rod instanceId.
        private class RemoteBobberState
        {
            public Rigidbody Body;
            public Transform BoatModel; // null = world frame
            public Vector3 TargetLocal; // boat-local, or world minus floating-origin offset
        }
        private readonly Dictionary<int, RemoteBobberState> _remoteBobbers = new Dictionary<int, RemoteBobberState>();
        private readonly List<int> _remoteBobberRemovalScratch = new List<int>();

        // Vanilla hardcodes this in ShipItemFishingRod.OnLoad (decomp :103); fallback if the private
        // field reads back zero (e.g. called before OnLoad ran on this copy).
        private static readonly Vector3 FallbackInitialBobberPos = new Vector3(0.3091888f, 1.137f, 0.71f);

        // Timing
        private const float StatesSyncInterval = 0.2f; // 5Hz
        private float _lastStateSyncTime;

        // Flag to prevent re-entrant calls during remote state application
        public bool IsApplyingRemoteState { get; private set; }

        private void Awake()
        {
            if (Instance != null)
            {
                Destroy(this);
                return;
            }
            Instance = this;
        }

        private void Update()
        {
            if (!Plugin.IsMultiplayer) return;

            UpdateRemoteBobbers();

            // Sync hooked rod state at 5Hz
            if (Time.time - _lastStateSyncTime >= StatesSyncInterval)
            {
                _lastStateSyncTime = Time.time;

                Plugin.Profiler?.StartMeasure();
                SyncHookedRodStates();
                SyncCastRodLineLengths();
                Plugin.Profiler?.EndMeasure("Fishing");
            }
        }

        #region Ownership

        public ulong GetRodOwner(int rodInstanceId)
        {
            return _rodOwners.TryGetValue(rodInstanceId, out var owner) ? owner : 0;
        }

        public bool IsLocalPlayerOwner(int rodInstanceId)
        {
            var owner = GetRodOwner(rodInstanceId);
            return owner == SteamClient.SteamId.Value;
        }

        public void SetRodOwner(int rodInstanceId, ulong ownerId)
        {
            _rodOwners[rodInstanceId] = ownerId;
            VerboseLogger.FishingEvent($"Rod owner set, rod={rodInstanceId}, owner={ownerId}");
        }

        #endregion

        #region Hooked State Tracking

        public void MarkRodHooked(int rodInstanceId)
        {
            _hookedRods.Add(rodInstanceId);
        }

        public void MarkRodUnhooked(int rodInstanceId)
        {
            _hookedRods.Remove(rodInstanceId);
        }

        private void SyncHookedRodStates()
        {
            // Only owners sync their hooked rods
            foreach (var rodId in _hookedRods)
            {
                if (!IsLocalPlayerOwner(rodId)) continue;

                var rod = FindRodByInstanceId(rodId);
                if (rod == null) continue;

                var fish = Traverse.Create(rod).Field("fish").GetValue<FishingRodFish>();
                if (fish == null || fish.currentFish == null) continue;

                var bobberJoint = Traverse.Create(rod).Field("bobberJoint").GetValue<ConfigurableJoint>();
                if (bobberJoint == null) continue;

                var tension = Traverse.Create(fish).Field("currentTargetTension").GetValue<float>();
                var energy = Traverse.Create(fish).Field("fishEnergy").GetValue<float>();

                var packet = new FishingStatePacket
                {
                    RodInstanceId = rodId,
                    LineLength = bobberJoint.linearLimit.limit,
                    Tension = tension,
                    FishEnergy = energy
                };

                VerboseLogger.FishingSend($"FishingState, rod={rodId}, line={packet.LineLength:F2}, tension={packet.Tension:F2}", throttle: true);

                Plugin.NetworkManager.SendToAllReliable(PacketType.FishingStateSync, w =>
                    PacketSerializer.WriteFishingState(w, packet));
            }
        }

        /// <summary>
        /// K1/K2 + B: 5Hz line-length AND bobber-position streaming for the local player's CAST rods
        /// (union with hooked rods, so both keep converging even in the hooked window between
        /// FishingState ticks). Each stream is coalesced independently.
        /// </summary>
        private void SyncCastRodLineLengths()
        {
            if (_castRods.Count == 0 && _hookedRods.Count == 0) return;

            foreach (var rodId in _castRods)
                SyncOneCastRod(rodId);
            foreach (var rodId in _hookedRods)
            {
                if (!_castRods.Contains(rodId))
                    SyncOneCastRod(rodId);
            }
        }

        private void SyncOneCastRod(int rodId)
        {
            if (!IsLocalPlayerOwner(rodId)) return;

            var rod = FindRodByInstanceId(rodId);
            if (rod == null) return;

            var bobberJoint = Traverse.Create(rod).Field("bobberJoint").GetValue<ConfigurableJoint>();
            if (bobberJoint == null) return;

            SyncOneCastRodLineLength(rodId, bobberJoint);
            SyncOneCastRodBobber(rodId, rod, bobberJoint);
        }

        private void SyncOneCastRodLineLength(int rodId, ConfigurableJoint bobberJoint)
        {
            float len = bobberJoint.linearLimit.limit;
            if (_lastSentLineLength.TryGetValue(rodId, out var last) && Mathf.Abs(len - last) <= LineLengthCoalesceDelta)
                return;
            _lastSentLineLength[rodId] = len;

            var packet = new FishingLineLengthPacket
            {
                RodInstanceId = rodId,
                LineLength = len
            };

            VerboseLogger.FishingSend($"LineLength (cast stream), rod={rodId}, len={len:F2}", throttle: true);

            Plugin.NetworkManager.SendToAllReliable(PacketType.FishingLineLengthSync, w =>
                PacketSerializer.WriteFishingLineLength(w, packet));
        }

        /// <summary>
        /// B: owner-side bobber position stream. Same boat-frame scheme as ItemSyncManager drops: the
        /// rod's own latched boat, else the boat whose EmbarkCol the rod's trigger is inside, else the
        /// caster's own boat (a HELD rod never latches currentActualBoat); world coords minus the
        /// floating-origin offset when the caster is on land/dock.
        /// </summary>
        private void SyncOneCastRodBobber(int rodId, ShipItemFishingRod rod, ConfigurableJoint bobberJoint)
        {
            Vector3 worldPos = bobberJoint.transform.position;
            if (_lastSentBobberPos.TryGetValue(rodId, out var last)
                && (worldPos - last).sqrMagnitude <= BobberCoalesceDelta * BobberCoalesceDelta)
                return;
            _lastSentBobberPos[rodId] = worldPos;

            Transform boatModel = rod.currentActualBoat;
            if (boatModel == null)
            {
                try
                {
                    var col = Traverse.Create(rod).Field("currentlyStayedEmbarkCol").GetValue<Collider>();
                    if (col != null) boatModel = col.transform.parent;
                }
                catch { }
            }
            if (boatModel == null) boatModel = GameState.currentBoat;

            string boatName = "";
            Vector3 position;
            var boatSaveable = boatModel != null ? boatModel.parent?.GetComponent<SaveableObject>() : null;
            if (boatSaveable != null)
            {
                boatName = boatSaveable.gameObject.name;
                position = boatModel.InverseTransformPoint(worldPos);
            }
            else
            {
                var offset = FloatingOriginManager.instance?.outCurrentOffset ?? Vector3.zero;
                position = worldPos - offset;
            }

            byte inWater = 0;
            try
            {
                // SimpleFloatingObject lives in Assembly-CSharp but also name-collides with a Crest
                // type, so read InWater reflectively instead of referencing the type.
                var fish = Traverse.Create(rod).Field("fish").GetValue<FishingRodFish>();
                if (fish != null && Traverse.Create(fish).Field("floater").Property("InWater").GetValue<bool>())
                    inWater = 1;
            }
            catch { }

            var packet = new FishingBobberSyncPacket
            {
                RodInstanceId = rodId,
                BoatName = boatName,
                Position = position,
                InWater = inWater
            };

            VerboseLogger.FishingSend($"BobberSync, rod={rodId}, boat={boatName}, pos={position}, inWater={inWater}", throttle: true);

            Plugin.NetworkManager.SendToAllReliable(PacketType.FishingBobberSync, w =>
                PacketSerializer.WriteFishingBobberSync(w, packet));
        }

        /// <summary>
        /// B: per-frame convergence of remote-owned bobbers toward their streamed targets. Bodies are
        /// kinematic while tracked, so this is the only thing moving them; a boat-frame target keeps
        /// riding the boat between 5Hz samples.
        /// </summary>
        private void UpdateRemoteBobbers()
        {
            if (_remoteBobbers.Count == 0) return;

            foreach (var kvp in _remoteBobbers)
            {
                var state = kvp.Value;
                if (state.Body == null)
                {
                    _remoteBobberRemovalScratch.Add(kvp.Key);
                    continue;
                }

                Vector3 target = state.BoatModel != null
                    ? state.BoatModel.TransformPoint(state.TargetLocal)
                    : state.TargetLocal + (FloatingOriginManager.instance?.outCurrentOffset ?? Vector3.zero);

                var t = state.Body.transform;
                if ((target - t.position).sqrMagnitude > 100f)
                    t.position = target; // teleport-scale divergence: snap instead of a long glide
                else
                    t.position = Vector3.Lerp(t.position, target, Mathf.Clamp01(10f * Time.deltaTime));
            }

            if (_remoteBobberRemovalScratch.Count > 0)
            {
                foreach (var id in _remoteBobberRemovalScratch)
                    _remoteBobbers.Remove(id);
                _remoteBobberRemovalScratch.Clear();
            }
        }

        /// <summary>
        /// B: stop tracking a remote-owned bobber and optionally hand it back to local joint physics.
        /// restoreDynamic=false when the caller parks the bobber itself (remote stow).
        /// </summary>
        public void ClearRemoteBobber(int rodInstanceId, bool restoreDynamic)
        {
            if (!_remoteBobbers.TryGetValue(rodInstanceId, out var state)) return;
            _remoteBobbers.Remove(rodInstanceId);
            if (restoreDynamic && state.Body != null)
                state.Body.isKinematic = false;
        }

        #endregion

        #region Rod Lookup

        private ShipItemFishingRod FindRodByInstanceId(int instanceId)
        {
            var allRods = FindObjectsOfType<ShipItemFishingRod>();
            foreach (var rod in allRods)
            {
                var prefab = rod.GetComponent<SaveablePrefab>();
                if (prefab != null && prefab.instanceId == instanceId)
                    return rod;
            }
            return null;
        }

        #endregion

        #region Local Events (called by patches)

        public void OnLocalRodPickedUp(ShipItemFishingRod rod)
        {
            var prefab = rod.GetComponent<SaveablePrefab>();
            if (prefab == null) return;

            int rodId = prefab.instanceId;
            ulong myId = SteamClient.SteamId.Value;

            // Take ownership BEFORE the grab-steal ReleaseFish below: OnReleaseFishPrefix blocks
            // non-owners and OnReleaseFishPostfix is now __runOriginal/owner-gated, so releasing
            // while the previous owner is still registered would go fully silent (fish never
            // escapes anywhere and _hookedRods goes stale).
            var previousOwner = GetRodOwner(rodId);
            SetRodOwner(rodId, myId);

            // Check if fish was hooked by previous owner - auto-escape
            var fish = Traverse.Create(rod).Field("fish").GetValue<FishingRodFish>();
            if (fish != null && fish.currentFish != null && !fish.fishDead)
            {
                if (previousOwner != 0 && previousOwner != myId)
                {
                    VerboseLogger.FishingEvent($"Rod grabbed from other player, fish escapes, rod={rodId}");
                    fish.ReleaseFish();
                }
            }

            var packet = new RodOwnerChangedPacket
            {
                RodInstanceId = rodId,
                NewOwnerId = myId
            };

            VerboseLogger.FishingSend($"RodOwnerChanged, rod={rodId}, owner={myId}");
            Plugin.NetworkManager.SendToAllReliable(PacketType.RodOwnerChanged, w =>
                PacketSerializer.WriteRodOwnerChanged(w, packet));
        }

        public void OnLocalRodDropped(ShipItemFishingRod rod)
        {
            var prefab = rod.GetComponent<SaveablePrefab>();
            if (prefab == null) return;

            int rodId = prefab.instanceId;

            // Fish escapes on drop (if hooked)
            var fish = Traverse.Create(rod).Field("fish").GetValue<FishingRodFish>();
            if (fish != null && fish.currentFish != null && !fish.fishDead)
            {
                VerboseLogger.FishingEvent($"Rod dropped with fish, fish escapes, rod={rodId}");
                fish.ReleaseFish();
                MarkRodUnhooked(rodId);

                var escapePacket = new FishEscapePacket { RodInstanceId = rodId };
                Plugin.NetworkManager.SendToAllReliable(PacketType.FishEscape, w =>
                    PacketSerializer.WriteFishEscape(w, escapePacket));
            }

            // Rod left the hand: stop the cast-rod line/bobber streams for it (K1/B bookkeeping).
            _castRods.Remove(rodId);
            _lastSentLineLength.Remove(rodId);
            _lastSentBobberPos.Remove(rodId);

            // Clear ownership
            SetRodOwner(rodId, 0);

            var packet = new RodOwnerChangedPacket
            {
                RodInstanceId = rodId,
                NewOwnerId = 0
            };

            VerboseLogger.FishingSend($"RodOwnerChanged (dropped), rod={rodId}, owner=0");
            Plugin.NetworkManager.SendToAllReliable(PacketType.RodOwnerChanged, w =>
                PacketSerializer.WriteRodOwnerChanged(w, packet));
        }

        public void OnLocalFishBite(ShipItemFishingRod rod, int fishPrefabIndex)
        {
            var prefab = rod.GetComponent<SaveablePrefab>();
            if (prefab == null) return;

            int rodId = prefab.instanceId;
            MarkRodHooked(rodId);

            var packet = new FishBitePacket
            {
                RodInstanceId = rodId,
                FishPrefabIndex = fishPrefabIndex
            };

            VerboseLogger.FishingEvent($"Fish bite, rod={rodId}, prefab={fishPrefabIndex}");
            VerboseLogger.FishingSend($"FishBite, rod={rodId}, prefab={fishPrefabIndex}");

            Plugin.NetworkManager.SendToAllReliable(PacketType.FishBite, w =>
                PacketSerializer.WriteFishBite(w, packet));
        }

        public void OnLocalFishEscape(ShipItemFishingRod rod)
        {
            var prefab = rod.GetComponent<SaveablePrefab>();
            if (prefab == null) return;

            int rodId = prefab.instanceId;
            MarkRodUnhooked(rodId);

            var packet = new FishEscapePacket { RodInstanceId = rodId };

            VerboseLogger.FishingEvent($"Fish escape, rod={rodId}");
            VerboseLogger.FishingSend($"FishEscape, rod={rodId}");

            Plugin.NetworkManager.SendToAllReliable(PacketType.FishEscape, w =>
                PacketSerializer.WriteFishEscape(w, packet));
        }

        public void OnLocalLineLengthChanged(ShipItemFishingRod rod, float newLength)
        {
            var prefab = rod.GetComponent<SaveablePrefab>();
            if (prefab == null) return;

            int rodId = prefab.instanceId;
            if (!IsLocalPlayerOwner(rodId)) return;

            var packet = new FishingLineLengthPacket
            {
                RodInstanceId = rodId,
                LineLength = newLength
            };

            VerboseLogger.FishingSend($"LineLength, rod={rodId}, len={newLength:F2}");

            Plugin.NetworkManager.SendToAllReliable(PacketType.FishingLineLengthSync, w =>
                PacketSerializer.WriteFishingLineLength(w, packet));
        }

        public void OnLocalRodCast(ShipItemFishingRod rod, float throwCharge)
        {
            var prefab = rod.GetComponent<SaveablePrefab>();
            if (prefab == null) return;

            int rodId = prefab.instanceId;

            // K1: start streaming the line length for this cast rod (5Hz, coalesced) so viewers see the
            // vanilla auto-unroll payout instead of a line stuck at minLength until the bite.
            _castRods.Add(rodId);
            _lastSentLineLength.Remove(rodId); // force the first stream tick to send
            _lastSentBobberPos.Remove(rodId);  // ditto for the bobber stream (B)

            var packet = new FishingCastPacket
            {
                RodInstanceId = rodId,
                ThrowCharge = throwCharge
            };

            VerboseLogger.FishingEvent($"Rod cast, rod={rodId}, charge={throwCharge:F2}");
            VerboseLogger.FishingSend($"FishingCast, rod={rodId}, charge={throwCharge:F2}");

            Plugin.NetworkManager.SendToAllReliable(PacketType.FishingCast, w =>
                PacketSerializer.WriteFishingCast(w, packet));
        }

        public void OnLocalFishCollect(ShipItemFishingRod rod, int fishPrefabIndex)
        {
            var prefab = rod.GetComponent<SaveablePrefab>();
            if (prefab == null) return;

            int rodId = prefab.instanceId;
            MarkRodUnhooked(rodId);
            // Collect gate guarantees the owner reeled to minLength; stop the cast streams.
            _castRods.Remove(rodId);
            _lastSentLineLength.Remove(rodId);
            _lastSentBobberPos.Remove(rodId);

            // If host, process immediately
            if (Plugin.IsHost)
            {
                ProcessFishCollection(rodId, fishPrefabIndex);
            }
            else
            {
                // Guest: send request to host
                var packet = new FishCollectRequestPacket
                {
                    RodInstanceId = rodId,
                    RodPrefabIndex = prefab.prefabIndex,
                    FishPrefabIndex = fishPrefabIndex
                };

                VerboseLogger.FishingSend($"FishCollectRequest, rod={rodId}, rodPrefab={prefab.prefabIndex}, fishPrefab={fishPrefabIndex}");

                Plugin.NetworkManager.SendToAllReliable(PacketType.FishCollectRequest, w =>
                    PacketSerializer.WriteFishCollectRequest(w, packet));
            }
        }

        /// <summary>
        /// K2: called from the ShipItemFishingRod.OnEnterInventory postfix. Vanilla resets
        /// currentTargetLength=minLength purely locally on stow, and once the rod is out of _hookedRods
        /// nothing streams the line any more - so remote copies kept the last extended length forever.
        /// Send one authoritative reset and stop the cast stream.
        /// </summary>
        public void OnLocalRodStowed(ShipItemFishingRod rod)
        {
            var prefab = rod.GetComponent<SaveablePrefab>();
            if (prefab == null) return;

            int rodId = prefab.instanceId;
            _castRods.Remove(rodId);
            _hookedRods.Remove(rodId);
            _lastSentLineLength.Remove(rodId);
            _lastSentBobberPos.Remove(rodId);

            if (!IsLocalPlayerOwner(rodId)) return;

            float minLength = 0.5f;
            try
            {
                var m = Traverse.Create(rod).Field("minLength").GetValue<float>();
                if (m > 0f) minLength = m;
            }
            catch { }

            var packet = new FishingLineLengthPacket
            {
                RodInstanceId = rodId,
                LineLength = minLength
            };

            VerboseLogger.FishingSend($"LineLength (stow reset), rod={rodId}, len={minLength:F2}");

            Plugin.NetworkManager.SendToAllReliable(PacketType.FishingLineLengthSync, w =>
                PacketSerializer.WriteFishingLineLength(w, packet));
        }

        /// <summary>
        /// G (rod line through the earth): the rod's bobber+line assembly is NOT a child of the rod -
        /// vanilla OnLoad reparents it to the shifting world - so the mod's remote hide/show
        /// (gameObject.SetActive on the rod alone) left the bobber an active NON-kinematic rigidbody
        /// whose joint went limp when the rod deactivated; it fell through all geometry and rendered a
        /// line from the stow point into the ground. Vanilla's own stow protection (OnEnterInventory:
        /// bobber kinematic + teleport to initialBobberPos) only runs on the stowing player's machine.
        /// This helper replicates it on remote hide/show. Call with stowed=true BEFORE
        /// item.gameObject.SetActive(false) and with stowed=false AFTER item.gameObject.SetActive(true).
        /// No-op for anything that is not a ShipItemFishingRod.
        /// </summary>
        public static void SyncExternalRodParts(ShipItem item, bool stowed)
        {
            var rod = item as ShipItemFishingRod;
            if (rod == null) return;

            try
            {
                var t = Traverse.Create(rod);
                var bobberJoint = t.Field("bobberJoint").GetValue<ConfigurableJoint>();
                if (bobberJoint == null) return;

                var initialBobberPos = t.Field("initialBobberPos").GetValue<Vector3>();
                if (initialBobberPos == Vector3.zero)
                    initialBobberPos = FallbackInitialBobberPos; // hardcoded in vanilla OnLoad (decomp :103)

                var bobberRb = bobberJoint.GetComponent<Rigidbody>();

                if (stowed)
                {
                    // B: stop the remote-bobber lerp fighting the park below (keep it kinematic - the
                    // park is intentional; the show branch restores dynamics).
                    var rodPrefab = rod.GetComponent<SaveablePrefab>();
                    if (rodPrefab != null)
                        Instance?.ClearRemoteBobber(rodPrefab.instanceId, restoreDynamic: false);

                    // Park the bobber the way vanilla OnEnterInventory does, then fully hide it (the
                    // prefab's own supported unsold state, OnLoad :107, so deactivating is safe).
                    if (bobberRb != null) bobberRb.isKinematic = true;
                    bobberJoint.transform.position = rod.transform.TransformPoint(initialBobberPos);
                    try
                    {
                        var minLength = t.Field("minLength").GetValue<float>();
                        t.Field("currentTargetLength").SetValue(minLength > 0f ? minLength : 0.5f);
                    }
                    catch { }
                    bobberJoint.gameObject.SetActive(false);
                    VerboseLogger.FishingEvent($"Parked external rod parts (remote stow), rod={rod.name}");
                }
                else
                {
                    // Unity re-creates the native joint when the component's GameObject re-enables and
                    // connectedBody is still assigned, so the line reels/casts normally afterwards.
                    if (rod.sold) bobberJoint.gameObject.SetActive(true);
                    bobberJoint.transform.position = rod.transform.TransformPoint(initialBobberPos);
                    if (bobberRb != null) bobberRb.isKinematic = false;
                    VerboseLogger.FishingEvent($"Restored external rod parts (remote show), rod={rod.name}");
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"[FISHING] SyncExternalRodParts({(stowed ? "stow" : "show")}) failed on {item.name}: {e.Message}");
            }
        }

        /// <summary>
        /// G hygiene: called (one line) from EconomyPatches.SettleStallBuy when a stall-buy confirm
        /// destroys the guest's parked OPTIMISTIC rod. That rod broadcast RodOwnerChanged(owner=me) once
        /// on the optimistic pickup and is destroyed without ever sending owner=0, leaving a stale
        /// ghost-rod ownership entry on the host. Clear it locally and broadcast the release.
        /// </summary>
        public void OnOptimisticRodDestroyed(int rodInstanceId)
        {
            if (rodInstanceId == 0) return;

            _rodOwners.Remove(rodInstanceId);
            _hookedRods.Remove(rodInstanceId);
            _castRods.Remove(rodInstanceId);
            _lastSentLineLength.Remove(rodInstanceId);
            _lastSentBobberPos.Remove(rodInstanceId);

            var packet = new RodOwnerChangedPacket
            {
                RodInstanceId = rodInstanceId,
                NewOwnerId = 0
            };

            VerboseLogger.FishingSend($"RodOwnerChanged (optimistic rod destroyed), rod={rodInstanceId}, owner=0");
            Plugin.NetworkManager.SendToAllReliable(PacketType.RodOwnerChanged, w =>
                PacketSerializer.WriteRodOwnerChanged(w, packet));
        }

        #endregion

        #region Host Processing

        private void ProcessFishCollection(int rodId, int fishPrefabIndex)
        {
            if (!Plugin.IsHost) return;

            var rod = FindRodByInstanceId(rodId);
            if (rod == null) return;

            var fish = Traverse.Create(rod).Field("fish").GetValue<FishingRodFish>();

            // Get fish prefab and spawn
            var fishPrefab = GetFishPrefabByIndex(fishPrefabIndex);
            if (fishPrefab == null)
            {
                // B10: a local-region fish (OceanFishes localFishesRegion) has no index in the synced fishPrefabs
                // array, so the request carries -1 and GetFishPrefabByIndex returns null. The guest already cleared
                // its visuals optimistically, so bailing here silently LOSES the catch. Fall back to the rod's
                // actual hooked fish (set on the host by the FishBite CatchFish() fallback) so an item is still
                // spawned and ItemSynced to the guest.
                fishPrefab = fish != null ? fish.currentFish : null;
            }
            if (fishPrefab == null)
            {
                VerboseLogger.FishingEvent($"Fish prefab not found, index={fishPrefabIndex}");
                return;
            }

            // K5: spawn at the ROD, not the fish/bobber transform. The bobber rigidbody is never synced,
            // so for a guest-owned rod the HOST's fish transform is garbage (dangling below the rod,
            // through deck/water) and the caught-fish item could spawn underwater/in geometry and vanish.
            // The collect gate guarantees the owner reeled to minLength, so the rod position IS correct.
            var spawnPos = rod.transform.position + Vector3.up * 0.75f;

            var fishItem = Object.Instantiate(fishPrefab, spawnPos, Quaternion.identity).GetComponent<ShipItem>();
            fishItem.sold = true;
            fishItem.GetComponent<SaveablePrefab>().RegisterToSave();

            int fishItemId = fishItem.GetComponent<SaveablePrefab>().instanceId;

            // Roll hook consumption (30% chance)
            bool hookConsumed = Random.Range(0f, 100f) > 69f;
            if (hookConsumed)
            {
                rod.health = 0f;
                // Update hook visuals
                var hookVisuals = Traverse.Create(rod).Field("hookVisuals").GetValue<GameObject>();
                if (hookVisuals != null) hookVisuals.SetActive(false);
            }

            // Clear fish visuals on rod
            if (fish != null)
            {
                fish.GetComponent<MeshFilter>().sharedMesh = null;
                fish.GetComponent<Renderer>().enabled = false;
                Traverse.Create(fish).Field("currentFish").SetValue(null);
            }

            var response = new FishCollectResponsePacket
            {
                RodInstanceId = rodId,
                FishItemId = fishItemId,
                HookConsumed = hookConsumed
            };

            // K5 bookkeeping symmetry: when the host processes a GUEST's collect request, only the guest's
            // OnLocalFishCollect ran MarkRodUnhooked - mirror it here so the host stops 5Hz-syncing a rod
            // that no longer has a fish (and stops the cast stream; the line is at minLength).
            MarkRodUnhooked(rodId);
            _castRods.Remove(rodId);
            _lastSentLineLength.Remove(rodId);
            _lastSentBobberPos.Remove(rodId);
            // B: for a GUEST-owned rod the host is a viewer; release its kinematic bobber like
            // OnFishCollectResponseReceived does on the other guests.
            ClearRemoteBobber(rodId, restoreDynamic: true);

            VerboseLogger.FishingEvent($"Fish collected, rod={rodId}, fishItem={fishItemId}, hookConsumed={hookConsumed}");
            VerboseLogger.FishingSend($"FishCollectResponse, rod={rodId}, fishItem={fishItemId}, hookConsumed={hookConsumed}");

            Plugin.NetworkManager.SendToAllReliable(PacketType.FishCollectResponse, w =>
                PacketSerializer.WriteFishCollectResponse(w, response));

            // Sync item spawn to guest
            ItemSyncManager.Instance?.OnLocalItemSpawned(fishItem);
        }

        private GameObject GetFishPrefabByIndex(int index)
        {
            // OceanFishes has fish prefab arrays
            if (OceanFishes.instance == null) return null;

            var fishPrefabs = Traverse.Create(OceanFishes.instance).Field("fishPrefabs").GetValue<GameObject[]>();
            if (fishPrefabs != null && index >= 0 && index < fishPrefabs.Length)
                return fishPrefabs[index];

            return null;
        }

        #endregion

        #region Packet Handlers (called by Plugin.cs)

        public void OnRodOwnerChangedReceived(RodOwnerChangedPacket packet, SteamId sender = default)
        {
            // Star-relay: forward to the other guests so the whole crew tracks rod ownership.
            if (Plugin.IsHost)
                Plugin.NetworkManager.SendToAllExcept(sender, PacketType.RodOwnerChanged, w =>
                    PacketSerializer.WriteRodOwnerChanged(w, packet));

            VerboseLogger.FishingRecv($"RodOwnerChanged, rod={packet.RodInstanceId}, owner={packet.NewOwnerId}");
            SetRodOwner(packet.RodInstanceId, packet.NewOwnerId);

            // B: rod no longer remote-owned here (released, or ownership came to us) - hand the
            // bobber back to local joint physics.
            if (packet.NewOwnerId == 0 || packet.NewOwnerId == SteamClient.SteamId.Value)
                ClearRemoteBobber(packet.RodInstanceId, restoreDynamic: true);
        }

        public void OnFishingCastReceived(FishingCastPacket packet, SteamId sender = default)
        {
            // Star-relay: forward BEFORE the owner early-return so the host (not the owner) still relays.
            if (Plugin.IsHost)
                Plugin.NetworkManager.SendToAllExcept(sender, PacketType.FishingCast, w =>
                    PacketSerializer.WriteFishingCast(w, packet));

            if (IsLocalPlayerOwner(packet.RodInstanceId)) return; // Owner already cast

            VerboseLogger.FishingRecv($"FishingCast, rod={packet.RodInstanceId}, charge={packet.ThrowCharge:F2}");

            IsApplyingRemoteState = true;
            try
            {
                var rod = FindRodByInstanceId(packet.RodInstanceId);
                if (rod == null) return;

                // Set throw charge and trigger the throw coroutine (built-in method)
                Traverse.Create(rod).Field("throwCharge").SetValue(packet.ThrowCharge);
                rod.StartCoroutine("ThrowRod");

                VerboseLogger.FishingApply($"FishingCast applied, rod={packet.RodInstanceId}");
            }
            finally
            {
                IsApplyingRemoteState = false;
            }
        }

        public void OnFishingBobberSyncReceived(FishingBobberSyncPacket packet, SteamId sender = default)
        {
            // Star-relay: forward BEFORE the owner early-return so the host (not the owner) still relays.
            if (Plugin.IsHost)
                Plugin.NetworkManager.SendToAllExcept(sender, PacketType.FishingBobberSync, w =>
                    PacketSerializer.WriteFishingBobberSync(w, packet));

            if (IsLocalPlayerOwner(packet.RodInstanceId)) return; // Owner's bobber is the source of truth

            VerboseLogger.FishingRecv($"BobberSync, rod={packet.RodInstanceId}, boat={packet.BoatName}, pos={packet.Position}, inWater={packet.InWater}", throttle: true);

            IsApplyingRemoteState = true;
            try
            {
                var rod = FindRodByInstanceId(packet.RodInstanceId);
                if (rod == null) return;

                var bobberJoint = Traverse.Create(rod).Field("bobberJoint").GetValue<ConfigurableJoint>();
                if (bobberJoint == null || !bobberJoint.gameObject.activeInHierarchy) return; // remotely stowed

                var body = bobberJoint.GetComponent<Rigidbody>();
                if (body == null) return;

                Transform boatModel = null;
                if (!string.IsNullOrEmpty(packet.BoatName))
                {
                    var boat = BoatUtility.FindBoatByName(packet.BoatName);
                    boatModel = boat != null ? boat.GetComponent<BoatRefs>()?.boatModel : null;
                    if (boatModel == null)
                    {
                        // Applying a boat-local pos in the wrong frame is worse than a dropped sample.
                        VerboseLogger.FishingEvent($"BobberSync boat '{packet.BoatName}' not found, sample dropped, rod={packet.RodInstanceId}");
                        return;
                    }
                }

                body.isKinematic = true;

                bool firstSample = !_remoteBobbers.TryGetValue(packet.RodInstanceId, out var state);
                if (firstSample)
                {
                    state = new RemoteBobberState();
                    _remoteBobbers[packet.RodInstanceId] = state;
                }
                state.Body = body;
                state.BoatModel = boatModel;
                state.TargetLocal = packet.Position;

                if (firstSample)
                {
                    // The local bobber is wherever local physics dropped it (typically dangling under
                    // the deck); a cut to the streamed spot beats a long visible glide through the hull.
                    body.transform.position = boatModel != null
                        ? boatModel.TransformPoint(packet.Position)
                        : packet.Position + (FloatingOriginManager.instance?.outCurrentOffset ?? Vector3.zero);
                }

                VerboseLogger.FishingApply($"BobberSync applied, rod={packet.RodInstanceId}");
            }
            finally
            {
                IsApplyingRemoteState = false;
            }
        }

        public void OnFishingStateReceived(FishingStatePacket packet, SteamId sender = default)
        {
            // Star-relay: forward BEFORE the owner early-return so the host (not the owner) still relays.
            if (Plugin.IsHost)
                Plugin.NetworkManager.SendToAllExcept(sender, PacketType.FishingStateSync, w =>
                    PacketSerializer.WriteFishingState(w, packet));

            if (IsLocalPlayerOwner(packet.RodInstanceId)) return; // Owner ignores own state

            VerboseLogger.FishingRecv($"FishingState, rod={packet.RodInstanceId}, line={packet.LineLength:F2}, tension={packet.Tension:F2}", throttle: true);

            IsApplyingRemoteState = true;
            try
            {
                var rod = FindRodByInstanceId(packet.RodInstanceId);
                if (rod == null) return;

                // Apply line length
                var bobberJoint = Traverse.Create(rod).Field("bobberJoint").GetValue<ConfigurableJoint>();
                if (bobberJoint != null)
                {
                    var limit = bobberJoint.linearLimit;
                    limit.limit = packet.LineLength;
                    bobberJoint.linearLimit = limit;
                    Traverse.Create(rod).Field("currentTargetLength").SetValue(packet.LineLength);
                }

                // Apply rod tension (visual)
                rod.SetRodTension(packet.Tension);

                VerboseLogger.FishingApply($"State applied, rod={packet.RodInstanceId}");
            }
            finally
            {
                IsApplyingRemoteState = false;
            }
        }

        public void OnFishingLineLengthReceived(FishingLineLengthPacket packet, SteamId sender = default)
        {
            // Star-relay: forward BEFORE the owner early-return so the host (not the owner) still relays.
            if (Plugin.IsHost)
                Plugin.NetworkManager.SendToAllExcept(sender, PacketType.FishingLineLengthSync, w =>
                    PacketSerializer.WriteFishingLineLength(w, packet));

            if (IsLocalPlayerOwner(packet.RodInstanceId)) return;

            VerboseLogger.FishingRecv($"LineLength, rod={packet.RodInstanceId}, len={packet.LineLength:F2}");

            IsApplyingRemoteState = true;
            try
            {
                var rod = FindRodByInstanceId(packet.RodInstanceId);
                if (rod == null) return;

                var bobberJoint = Traverse.Create(rod).Field("bobberJoint").GetValue<ConfigurableJoint>();
                if (bobberJoint != null)
                {
                    var limit = bobberJoint.linearLimit;
                    limit.limit = packet.LineLength;
                    bobberJoint.linearLimit = limit;
                    Traverse.Create(rod).Field("currentTargetLength").SetValue(packet.LineLength);
                }

                VerboseLogger.FishingApply($"LineLength applied, rod={packet.RodInstanceId}, len={packet.LineLength:F2}");
            }
            finally
            {
                IsApplyingRemoteState = false;
            }
        }

        public void OnFishBiteReceived(FishBitePacket packet, SteamId sender = default)
        {
            // Star-relay: forward BEFORE the owner early-return so the host (not the owner) still relays.
            if (Plugin.IsHost)
                Plugin.NetworkManager.SendToAllExcept(sender, PacketType.FishBite, w =>
                    PacketSerializer.WriteFishBite(w, packet));

            if (IsLocalPlayerOwner(packet.RodInstanceId)) return; // Owner already handled it

            VerboseLogger.FishingRecv($"FishBite, rod={packet.RodInstanceId}, prefab={packet.FishPrefabIndex}");

            IsApplyingRemoteState = true;
            try
            {
                var rod = FindRodByInstanceId(packet.RodInstanceId);
                if (rod == null) return;

                var fish = Traverse.Create(rod).Field("fish").GetValue<FishingRodFish>();
                if (fish == null) return;

                // Get the exact fish prefab from packet to match owner's fish
                var fishPrefab = GetFishPrefabByIndex(packet.FishPrefabIndex);
                if (fishPrefab != null)
                {
                    // Manually replicate CatchFish() with correct prefab
                    Traverse.Create(fish).Field("currentFish").SetValue(fishPrefab);
                    fish.GetComponent<MeshFilter>().sharedMesh = fishPrefab.GetComponent<MeshFilter>().sharedMesh;
                    fish.GetComponent<Renderer>().enabled = true;
                    Traverse.Create(fish).Field("fishTimer").SetValue(6f);
                    Traverse.Create(fish).Field("fishEnergy").SetValue(1f);
                    fish.fishDead = false;
                }
                else
                {
                    // Fallback: fish from local region not in main array, let viewer pick own fish
                    fish.CatchFish();
                }

                MarkRodHooked(packet.RodInstanceId);
                VerboseLogger.FishingApply($"FishBite applied, rod={packet.RodInstanceId}, prefab={packet.FishPrefabIndex}");
            }
            finally
            {
                IsApplyingRemoteState = false;
            }
        }

        public void OnFishEscapeReceived(FishEscapePacket packet, SteamId sender = default)
        {
            // Star-relay: forward BEFORE the owner early-return so the host (not the owner) still relays.
            if (Plugin.IsHost)
                Plugin.NetworkManager.SendToAllExcept(sender, PacketType.FishEscape, w =>
                    PacketSerializer.WriteFishEscape(w, packet));

            if (IsLocalPlayerOwner(packet.RodInstanceId)) return;

            VerboseLogger.FishingRecv($"FishEscape, rod={packet.RodInstanceId}");

            IsApplyingRemoteState = true;
            try
            {
                var rod = FindRodByInstanceId(packet.RodInstanceId);
                if (rod == null) return;

                var fish = Traverse.Create(rod).Field("fish").GetValue<FishingRodFish>();
                if (fish != null)
                {
                    // Use built-in method - handles audio, visuals, hook detach
                    fish.ReleaseFish();
                }

                MarkRodUnhooked(packet.RodInstanceId);
                VerboseLogger.FishingApply($"FishEscape applied, rod={packet.RodInstanceId}");
            }
            finally
            {
                IsApplyingRemoteState = false;
            }
        }

        public void OnFishCollectRequestReceived(FishCollectRequestPacket packet, SteamId sender)
        {
            if (!Plugin.IsHost) return;

            VerboseLogger.FishingRecv($"FishCollectRequest, rod={packet.RodInstanceId}, rodPrefab={packet.RodPrefabIndex}, fishPrefab={packet.FishPrefabIndex}");

            // Validate rod item
            if (!ItemSyncManager.Instance.ValidateItem(packet.RodInstanceId, packet.RodPrefabIndex, out int expectedPrefab))
            {
                if (expectedPrefab == -1)
                {
                    ItemSyncManager.Instance.SendItemDestroyed(packet.RodInstanceId, sender);
                }
                else
                {
                    ItemSyncManager.Instance.SendItemResync(packet.RodInstanceId, sender);
                }
                return;
            }

            ProcessFishCollection(packet.RodInstanceId, packet.FishPrefabIndex);
        }

        public void OnFishCollectResponseReceived(FishCollectResponsePacket packet)
        {
            if (Plugin.IsHost) return; // Host already processed

            VerboseLogger.FishingRecv($"FishCollectResponse, rod={packet.RodInstanceId}, fishItem={packet.FishItemId}, hookConsumed={packet.HookConsumed}");

            IsApplyingRemoteState = true;
            try
            {
                var rod = FindRodByInstanceId(packet.RodInstanceId);
                if (rod == null) return;

                var fish = Traverse.Create(rod).Field("fish").GetValue<FishingRodFish>();
                if (fish != null)
                {
                    // Manual visual cleanup - can't use CollectFish() as it spawns item (would duplicate)
                    // Fish item comes via ItemSync from host
                    fish.GetComponent<MeshFilter>().sharedMesh = null;
                    fish.GetComponent<Renderer>().enabled = false;
                    Traverse.Create(fish).Field("currentFish").SetValue(null);
                }

                // Sync hook consumption
                if (packet.HookConsumed)
                {
                    rod.health = 0f;
                    var hookVisuals = Traverse.Create(rod).Field("hookVisuals").GetValue<GameObject>();
                    if (hookVisuals != null) hookVisuals.SetActive(false);
                }

                MarkRodUnhooked(packet.RodInstanceId);
                // B: owner stops streaming after a collect (rod leaves _castRods/_hookedRods there);
                // the collect gate guarantees the line is at minLength, so joint physics parks the
                // bobber at the rod tip once it is dynamic again.
                ClearRemoteBobber(packet.RodInstanceId, restoreDynamic: true);
                VerboseLogger.FishingApply($"FishCollect applied, rod={packet.RodInstanceId}");
            }
            finally
            {
                IsApplyingRemoteState = false;
            }
        }

        #endregion

        public void Reset()
        {
            // Hand remote-driven bobbers back to physics before dropping tracking: the LOCAL player is
            // leaving, so no stream will ever un-kinematic them again (vanilla only restores in
            // OnLeaveInventory), and the rods stay in this continuing local session.
            if (_remoteBobbers.Count > 0)
            {
                var trackedIds = new List<int>(_remoteBobbers.Keys);
                foreach (var id in trackedIds)
                    ClearRemoteBobber(id, restoreDynamic: true);
            }

            _rodOwners.Clear();
            _hookedRods.Clear();
            _castRods.Clear();
            _lastSentLineLength.Clear();
            _lastSentBobberPos.Clear();
            _lastStateSyncTime = 0f;
        }

        /// <summary>
        /// Called when a player disconnects. Broadcasts FishEscape for any hooked rods they owned.
        /// </summary>
        public void OnPlayerDisconnected(ulong steamId)
        {
            // Find all rods owned by the disconnected player
            var rodsToRelease = new List<int>();
            foreach (var kvp in _rodOwners)
            {
                if (kvp.Value == steamId)
                {
                    rodsToRelease.Add(kvp.Key);
                }
            }

            foreach (var rodId in rodsToRelease)
            {
                // If rod was hooked, broadcast escape
                if (_hookedRods.Contains(rodId))
                {
                    VerboseLogger.FishingEvent($"Player disconnected with hooked fish, rod={rodId}, player={steamId}");

                    var escapePacket = new FishEscapePacket { RodInstanceId = rodId };
                    Plugin.NetworkManager.SendToAllReliable(PacketType.FishEscape, w =>
                        PacketSerializer.WriteFishEscape(w, escapePacket));

                    MarkRodUnhooked(rodId);

                    // Clear fish visuals locally
                    var rod = FindRodByInstanceId(rodId);
                    if (rod != null)
                    {
                        var fish = Traverse.Create(rod).Field("fish").GetValue<FishingRodFish>();
                        if (fish != null)
                        {
                            Traverse.Create(fish).Field("currentFish").SetValue(null);
                            fish.GetComponent<MeshFilter>().sharedMesh = null;
                            fish.GetComponent<Renderer>().enabled = false;
                            Traverse.Create(fish).Field("fishDead").SetValue(true);
                        }
                    }
                }

                // Clear ownership; the leaver's bobber stream is gone, return the bobber to physics.
                _rodOwners.Remove(rodId);
                ClearRemoteBobber(rodId, restoreDynamic: true);
            }

            VerboseLogger.FishingEvent($"Player disconnect cleanup, player={steamId}, rodsReleased={rodsToRelease.Count}");
        }
    }
}
