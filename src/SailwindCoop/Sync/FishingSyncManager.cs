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

            // Sync hooked rod state at 5Hz
            if (Time.time - _lastStateSyncTime >= StatesSyncInterval)
            {
                _lastStateSyncTime = Time.time;

                Plugin.Profiler?.StartMeasure();
                SyncHookedRodStates();
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

            // Check if fish was hooked by previous owner - auto-escape
            var fish = Traverse.Create(rod).Field("fish").GetValue<FishingRodFish>();
            if (fish != null && fish.currentFish != null && !fish.fishDead)
            {
                var previousOwner = GetRodOwner(rodId);
                if (previousOwner != 0 && previousOwner != myId)
                {
                    VerboseLogger.FishingEvent($"Rod grabbed from other player, fish escapes, rod={rodId}");
                    fish.ReleaseFish();
                }
            }

            SetRodOwner(rodId, myId);

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

            var spawnPos = fish != null ? fish.transform.position : rod.transform.position;

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
            _rodOwners.Clear();
            _hookedRods.Clear();
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

                // Clear ownership
                _rodOwners.Remove(rodId);
            }

            VerboseLogger.FishingEvent($"Player disconnect cleanup, player={steamId}, rodsReleased={rodsToRelease.Count}");
        }
    }
}
