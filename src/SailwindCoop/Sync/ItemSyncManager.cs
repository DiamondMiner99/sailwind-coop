using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Steamworks;
using SailwindCoop.Debug;
using SailwindCoop.Networking;
using SailwindCoop.Networking.Packets;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Manages runtime item synchronization between host and guest.
    /// Handles pickup, drop, crate interactions, and item spawn/destroy.
    /// </summary>
    public class ItemSyncManager : MonoBehaviour
    {
        public static ItemSyncManager Instance { get; private set; }

        /// <summary>
        /// Tracks which items are currently held by which player.
        /// Key: item instanceId, Value: SteamId of holder
        /// </summary>
        private Dictionary<int, SteamId> _heldItems = new Dictionary<int, SteamId>();

        /// <summary>
        /// Tracks items held by remote player for visual following.
        /// Key: item instanceId, Value: the ShipItem component
        /// </summary>
        private Dictionary<int, ShipItem> _remoteHeldItems = new Dictionary<int, ShipItem>();

        /// <summary>
        /// Tracks items in each guest's inventory for disconnect cleanup (host only).
        /// Partitioned per holder so ONE guest leaving drops only its own items,
        /// not every connected guest's. Outer key: holder SteamId; inner set: that holder's item
        /// instanceIds. The authoritative holder lookup is still _heldItems (instanceId -> holder); this map
        /// is the inverse index used to enumerate one holder's items on disconnect.
        /// </summary>
        private Dictionary<SteamId, HashSet<int>> _guestInventoryItems = new Dictionary<SteamId, HashSet<int>>();

        /// <summary>
        /// Ref-count of in-flight guest joins. While > 0 the host ignores
        /// remote ItemDestroyed packets (a joining guest's cleanup destroys items that must not affect
        /// the host's world). Ref-counted (not a bare bool) so overlapping joins and already-settled
        /// guests destroying items mid-join are handled correctly. IgnoreRemoteItemDestruction mirrors
        /// (_joinDestructionGuardCount > 0). At N=1 the count is 0 -> 1 -> 0, identical to the old bool.
        /// </summary>
        private int _joinDestructionGuardCount;

        /// <summary>
        /// Tracks items that have been synced to guests (host only).
        /// Used to know when to send ItemSpawned for scene items on first pickup.
        ///
        /// Partitioned PER PEER. A single session-global HashSet&lt;int&gt; would break later joiners: once an
        /// id had been sent to guest #1 (e.g. on the host's first scene-item pickup), the gate would skip it
        /// forever - a LATER joiner (guest #2) would never be sent an ItemSpawned for that id (it isn't in his join
        /// snapshot if the host is currently holding it), so the host's subsequent drops would no-op on him and he
        /// would see only originals he couldn't pick up. Keyed by the peer's SteamId, mirroring the existing
        /// per-peer _guestInventoryItems pattern, so each joiner's "what have I already been told about" set is
        /// independent. At N=1 there is exactly one peer entry, so the gate behaves identically to the old
        /// flat set.
        /// </summary>
        private Dictionary<SteamId, HashSet<int>> _syncedItemIds = new Dictionary<SteamId, HashSet<int>>();

        /// <summary>
        /// Throttle health sync for continuous consumption (soup).
        /// Key: instanceId, Value: last sync time
        /// </summary>
        private Dictionary<int, float> _lastHealthSyncTime = new Dictionary<int, float>();
        private const float HEALTH_SYNC_INTERVAL = 0.5f; // 2Hz max

        /// <summary>
        /// Track items recently synced via remote packets.
        /// Don't broadcast ItemDestroyed for these - they're host-authoritative.
        /// Key: instanceId, Value: time when synced
        /// </summary>
        private Dictionary<int, float> _recentlyRemoteSyncedItems = new Dictionary<int, float>();
        private const float REMOTE_SYNC_PROTECTION_TIME = 2f; // Protect for 2 seconds

        /// <summary>
        /// Host-authoritative item registry.
        /// Maps instanceId → prefabIndex for validation.
        /// Only populated on host.
        /// </summary>
        private Dictionary<int, int> _itemRegistry = new Dictionary<int, int>();

        /// <summary>
        /// Track items just purchased to skip redundant pickup sync.
        /// Key: instanceId, Value: time when marked
        /// </summary>
        private Dictionary<int, float> _justPurchasedItems = new Dictionary<int, float>();
        private const float JUST_PURCHASED_TIMEOUT = 0.5f; // Clear after 0.5s

        /// <summary>
        /// Set to true when applying remote state to prevent feedback loops.
        /// SPLIT into two backing flags. The long-lived JOIN-coroutine guard
        /// (BoatStateApplicator, held across its yields via SetApplyingRemoteState) and the short-lived PER-PACKET
        /// echo guard (the OnRemote* item handlers, which set the property and unconditionally clear it in their
        /// finally) must not share one bool: a remote item packet processed DURING a join yield would clear the
        /// coroutine's guard out from under it, so the rest of the join would run unguarded (echo storm / leaked local
        /// sends). The per-packet handlers only touch _applyingPerPacket; the coroutine only touches
        /// _applyingJoinState; the getter is true when EITHER is set, so neither can clear the other.
        /// </summary>
        private bool _applyingPerPacket;
        private bool _applyingJoinState;
        public bool IsApplyingRemoteState
        {
            get => _applyingPerPacket || _applyingJoinState;
            private set => _applyingPerPacket = value; // property setter (per-packet handlers) only
        }

        /// <summary>
        /// True on the host while one or more guest joins are in flight, so ItemDestroyed
        /// packets are ignored (a joining guest's cleanup destroys items that must not affect the host).
        /// Derived from the join ref-count instead of a bare settable bool, so a
        /// single guest's join end can't re-enable destruction while another join is still in progress,
        /// and an already-settled guest is never caught in a join window opened by a different joiner.
        /// At N=1 this is true for exactly the one join window, identical to the old bool.
        /// </summary>
        public bool IgnoreRemoteItemDestruction => _joinDestructionGuardCount > 0;

        /// <summary>
        /// Open a join window for one joining guest (host). Increments the ref-count; while > 0,
        /// remote ItemDestroyed packets are ignored. Pair with EndJoinDestructionGuard.
        /// </summary>
        public void BeginJoinDestructionGuard()
        {
            _joinDestructionGuardCount++;
            Plugin.Log.LogInfo($"[ITEM] Join destruction guard opened (active joins={_joinDestructionGuardCount})");
        }

        /// <summary>
        /// Close one join window (host). Decrements the ref-count; remote ItemDestroyed is only
        /// re-enabled once ALL in-flight joins have closed (count returns to 0). Clamped at 0 so a
        /// stray extra End can't drive it negative and silently disable the guard.
        /// </summary>
        public void EndJoinDestructionGuard()
        {
            if (_joinDestructionGuardCount > 0) _joinDestructionGuardCount--;
            Plugin.Log.LogInfo($"[ITEM] Join destruction guard closed (active joins={_joinDestructionGuardCount})");
        }

        /// <summary>
        /// Instance IDs of crates currently being remotely unsealed (empty if none). Used to suppress the
        /// crate UI on the host for those crates. A SET, not a single id - at 3+ players two guests can
        /// unseal different crates concurrently, and a single field let the second overwrite the first (leaking
        /// a spurious crate window). Call IsRemoteUnsealing(id) to check.
        /// </summary>
        private readonly HashSet<int> _remoteUnsealingCrates = new HashSet<int>();
        public bool IsRemoteUnsealing(int instanceId) => _remoteUnsealingCrates.Contains(instanceId);

        // Crates whose vanilla UnsealCrate inserts are in progress. While a crate is here, the
        // per-item CrateInventory.InsertItem postfix (ItemPatches.OnCrateInsertItem) must NOT broadcast a separate
        // ItemCrateInsert - the single bulk CrateUnsealed packet is the source of truth. Suppresses the per-item
        // "ItemCrateInsert" flood (+ its double-emit) that arrived before the guest spawned the items
        // ("OnRemoteItemCrateInsert: item or crate not found"). Scoped per-crate so unrelated manual inserts sync.
        private readonly HashSet<int> _unsealingCrateIds = new HashSet<int>();
        public bool IsCrateUnsealing(int crateInstanceId) => _unsealingCrateIds.Contains(crateInstanceId);

        /// <summary>
        /// Instance IDs of crates the local (guest) player requested to unseal (used to only show the crate UI
        /// when the matching response arrives). A SET, not a single field: with one slot, a CrateUnsealed for a
        /// DIFFERENT crate (host-initiated, or another guest's, arriving first) cleared the slot, so the guest
        /// who clicked unseal got the items but NO inventory UI. Remove ONLY the matching id (mirrors the
        /// _remoteUnsealingCrates single-field-to-set design above).
        /// </summary>
        private readonly HashSet<int> _pendingUnsealCrateIds = new HashSet<int>();

        /// <summary>
        /// Mark an item as just purchased (to skip pickup sync).
        /// </summary>
        public void MarkItemAsJustPurchased(int instanceId)
        {
            if (instanceId == 0) return;
            _justPurchasedItems[instanceId] = Time.time;
        }

        /// <summary>
        /// Check if an item was just purchased (should skip pickup sync).
        /// </summary>
        public bool WasJustPurchased(int instanceId)
        {
            if (instanceId == 0) return false;
            if (_justPurchasedItems.TryGetValue(instanceId, out float time))
            {
                if (Time.time - time < JUST_PURCHASED_TIMEOUT)
                {
                    return true;
                }
                _justPurchasedItems.Remove(instanceId);
            }
            return false;
        }

        /// <summary>
        /// Get-or-create THIS peer's synced-id set. Mirrors AddToGuestInventory's per-holder
        /// lazy-create. NOTE: the join snapshot (CollectWorldState) is NOT threaded per-joiner - the send path
        /// SendBoatWorldStateTo(target) -> CollectWorldState does not pass the joiner's SteamId into the
        /// collector, so RegisterBoatItems(ids) records the shared snapshot ids under EVERY currently-connected
        /// peer (see that overload). The per-peer partitioning still earns its keep via the pickup gate
        /// (AnyPeerMissingSyncedItem): that is where a LATER joiner is correctly treated as not-yet-told about a
        /// scene item the host picks up AFTER an earlier guest joined. On a guest this is reached via the
        /// parameterless overload with the local SteamId, where the set is written but never read (the gate is
        /// host-only).
        /// </summary>
        private HashSet<int> GetSyncedSetForPeer(SteamId peer)
        {
            if (!_syncedItemIds.TryGetValue(peer, out var set))
            {
                set = new HashSet<int>();
                _syncedItemIds[peer] = set;
            }
            return set;
        }

        /// <summary>
        /// Internal per-peer registration helper - records <paramref name="instanceIds"/> in
        /// exactly <paramref name="target"/>'s synced set. This is NOT a per-joiner snapshot path: the only
        /// caller is the parameterless RegisterBoatItems(ids) below, which fans these ids out across either
        /// every connected peer (host) or the local SteamId (guest / no peers). There is intentionally no
        /// public per-target caller - the join snapshot (CollectWorldState) does not thread the joiner's
        /// SteamId, so a fresh joiner does NOT start with an empty per-peer set; later-joiner correctness for
        /// host-picked scene items comes from the per-peer pickup gate (AnyPeerMissingSyncedItem) instead.
        /// </summary>
        private void RegisterBoatItems(SteamId target, IEnumerable<int> instanceIds)
        {
            var set = GetSyncedSetForPeer(target);
            int count = 0;
            foreach (var id in instanceIds)
            {
                if (id > 0 && set.Add(id))
                {
                    count++;
                }
            }
            Plugin.Log.LogInfo($"[ITEM:REGISTER] Registered {count} boat items in _syncedItemIds for peer {target}");
        }

        /// <summary>
        /// Register boat items from initial sync (guest side / role-agnostic). On the GUEST this
        /// records the ids the guest just spawned from the host's snapshot; the guest never reads this set (the
        /// scene-item pickup gate is host-only), so the local SteamId is a fine partition key.
        ///
        /// On the HOST this is reached from BoatStateCollector while building a join/recovery snapshot. The
        /// snapshot is built WITHOUT the joiner's SteamId (CollectWorldState takes no target), and its
        /// world/boat items are the SHARED world (identical for every peer), so we register them under EVERY
        /// currently-connected peer: the new joiner (who is receiving this very snapshot) and any already-settled
        /// peer (a harmless no-op re-add - it already has these from its own join/live sync). A LATER joiner is NOT given an empty
        /// set here; instead it is still tracked independently for scene items the host picks up AFTER it joined,
        /// by the per-peer OnLocalPickup gate (AnyPeerMissingSyncedItem), while shared snapshot items remain
        /// known to all. At N=1 there is one peer, identical to before.
        /// </summary>
        public void RegisterBoatItems(IEnumerable<int> instanceIds)
        {
            if (Plugin.IsHost)
            {
                var peers = Plugin.NetworkManager?.ConnectedPeers;
                if (peers == null || peers.Count == 0)
                {
                    // No peers yet (snapshot built before any connection settled): stash under the host's own
                    // key so the ids aren't lost; MarkSyncedForAllPeers/the pickup gate operate per connected
                    // peer regardless, so this is purely defensive.
                    RegisterBoatItems(SteamClient.SteamId, instanceIds);
                    return;
                }
                // Materialize once - instanceIds may be a lazy enumerable consumed by the first peer.
                var ids = instanceIds as ICollection<int> ?? instanceIds.ToList();
                foreach (var peer in peers)
                    RegisterBoatItems(peer, ids);
                return;
            }
            RegisterBoatItems(SteamClient.SteamId, instanceIds);
        }

        /// <summary>
        /// Get count of synced items for diagnostics (summed across peers).
        /// </summary>
        public int GetSyncedItemCount() => _syncedItemIds.Values.Sum(s => s.Count);

        /// <summary>
        /// True if ANY currently-connected peer has NOT yet been told about <paramref name="instanceId"/>
        /// (host scene-item pickup gate). Drives the decision to (re)broadcast an ItemSpawned. With no connected
        /// peers this is false (nobody to sync to), so a solo/empty host never spams. At N=1 this is "the single
        /// guest hasn't seen the id".
        /// </summary>
        private bool AnyPeerMissingSyncedItem(int instanceId)
        {
            var peers = Plugin.NetworkManager?.ConnectedPeers;
            if (peers == null) return false;
            foreach (var peer in peers)
            {
                if (!_syncedItemIds.TryGetValue(peer, out var set) || !set.Contains(instanceId))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Record <paramref name="instanceId"/> as synced for EVERY currently-connected peer
        /// (called right after a broadcast ItemSpawned, which reaches them all). Keeps each peer's set in step
        /// with what it has actually been sent.
        /// </summary>
        private void MarkSyncedForAllPeers(int instanceId)
        {
            var peers = Plugin.NetworkManager?.ConnectedPeers;
            if (peers == null) return;
            foreach (var peer in peers)
                GetSyncedSetForPeer(peer).Add(instanceId);
        }

        #region Item Registry

        /// <summary>
        /// Generate a unique item ID.
        /// </summary>
        private int GenerateItemId()
        {
            return UnityEngine.Random.Range(1, int.MaxValue);
        }

        /// <summary>
        /// Ensure item has a valid instanceId and register it.
        /// If instanceId is 0, assigns a new one.
        /// Returns the instanceId (new or existing).
        /// </summary>
        public int EnsureItemHasId(ShipItem item)
        {
            if (item == null) return 0;

            var prefab = item.GetComponent<SaveablePrefab>();
            if (prefab == null) return 0;

            if (prefab.instanceId == 0)
            {
                prefab.instanceId = GenerateItemId();
                Plugin.Log.LogInfo($"[ITEM:REGISTRY] Assigned new ID {prefab.instanceId} to {item.name}");
            }

            // Register in registry (host only)
            if (Plugin.IsHost)
            {
                _itemRegistry[prefab.instanceId] = prefab.prefabIndex;
            }

            return prefab.instanceId;
        }

        /// <summary>
        /// Validate that an item ID matches the expected prefabIndex.
        /// Returns true if valid, false if mismatch or unknown.
        /// </summary>
        public bool ValidateItem(int instanceId, int prefabIndex, out int expectedPrefabIndex)
        {
            expectedPrefabIndex = -1;

            if (!Plugin.IsHost) return true; // Guest doesn't validate

            if (!_itemRegistry.TryGetValue(instanceId, out expectedPrefabIndex))
            {
                Plugin.Log.LogWarning($"[ITEM:REGISTRY] Unknown item {instanceId} (not in registry)");
                return false;
            }

            if (expectedPrefabIndex != prefabIndex)
            {
                Plugin.Log.LogWarning($"[ITEM:REGISTRY] Mismatch for {instanceId}: expected prefab {expectedPrefabIndex}, got {prefabIndex}");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Get registry count for diagnostics.
        /// </summary>
        public int GetRegistryCount() => _itemRegistry.Count;

        /// <summary>
        /// Check if an item is registered (for debug overlay).
        /// </summary>
        public bool IsItemRegistered(int instanceId) => _itemRegistry.ContainsKey(instanceId);

        /// <summary>
        /// Register an item (for initial sync and debug overlay).
        /// </summary>
        public void RegisterItem(int instanceId, int prefabIndex)
        {
            _itemRegistry[instanceId] = prefabIndex;
        }

        /// <summary>
        /// Populate registry from all items in scene.
        /// Called on multiplayer start (host only).
        /// </summary>
        public void PopulateRegistryFromScene()
        {
            if (!Plugin.IsHost) return;

            _itemRegistry.Clear();

            var prefabs = Object.FindObjectsOfType<SaveablePrefab>();
            int assigned = 0;
            int registered = 0;

            foreach (var prefab in prefabs)
            {
                var item = prefab.GetComponent<ShipItem>();
                if (item == null) continue;

                // Assign ID if missing
                if (prefab.instanceId == 0)
                {
                    prefab.instanceId = GenerateItemId();
                    assigned++;
                }

                // Register
                _itemRegistry[prefab.instanceId] = prefab.prefabIndex;
                registered++;
            }

            Plugin.Log.LogInfo($"[ITEM:REGISTRY] Populated registry: {registered} items, {assigned} IDs assigned");
        }

        /// <summary>
        /// Send ItemDestroyed to a specific player.
        /// Used when validation fails and item doesn't exist on host.
        /// </summary>
        public void SendItemDestroyed(int instanceId, SteamId targetPlayer)
        {
            var packet = new ItemDestroyedPacket { ItemInstanceId = instanceId };

            VerboseLogger.ItemSend($"ItemDestroyed (targeted), id={instanceId}, to={targetPlayer}");

            Plugin.NetworkManager.SendReliable(targetPlayer, PacketType.ItemDestroyed, w =>
            {
                PacketSerializer.WriteItemDestroyed(w, packet);
            });
        }

        /// <summary>
        /// Send ItemResync to guest to fix mismatched item.
        /// </summary>
        public void SendItemResync(int instanceId, SteamId targetPlayer)
        {
            var item = FindItemByInstanceId(instanceId);
            if (item == null)
            {
                // Item doesn't exist on host - send destroy instead
                SendItemDestroyed(instanceId, targetPlayer);
                return;
            }

            var prefab = item.GetComponent<SaveablePrefab>();
            if (prefab == null) return;

            // Determine location
            bool isOnBoat = item.currentActualBoat != null;
            string boatName = "";
            Vector3 localPos;

            if (isOnBoat)
            {
                // Get boat name from SaveableObject
                var boatSaveable = item.currentActualBoat.parent?.GetComponent<SaveableObject>();
                if (boatSaveable != null)
                {
                    boatName = boatSaveable.gameObject.name;
                }
                // Boat-relative position
                localPos = item.transform.localPosition;
            }
            else
            {
                // World position - convert to offset-independent coords
                var offset = FloatingOriginManager.instance?.outCurrentOffset ?? Vector3.zero;
                localPos = item.transform.position - offset;
            }

            // Get mission index if this is mission cargo
            var good = item.GetComponent<Good>();
            int missionIndex = good != null ? good.GetMissionIndex() : -1;

            var packet = new ItemResyncPacket
            {
                InstanceId = prefab.instanceId,
                PrefabIndex = prefab.prefabIndex,
                Health = item.health,
                Amount = item.amount,
                Sold = item.sold,
                IsOnBoat = isOnBoat,
                BoatName = boatName,
                LocalPosition = localPos,
                Rotation = item.transform.rotation,
                MissionIndex = missionIndex
            };

            VerboseLogger.ItemSend($"ItemResync, id={instanceId}, prefab={prefab.prefabIndex}, onBoat={isOnBoat}, mission={missionIndex}");

            Plugin.NetworkManager.SendReliable(targetPlayer, PacketType.ItemResync, w =>
            {
                PacketSerializer.WriteItemResync(w, packet);
            });
        }

        /// <summary>
        /// Handle ItemResync from host - destroy wrong item, spawn correct one.
        /// </summary>
        public void OnRemoteItemResync(ItemResyncPacket packet)
        {
            VerboseLogger.ItemRecv($"ItemResync, id={packet.InstanceId}, prefab={packet.PrefabIndex}");

            // 1. Destroy existing item if found
            var existing = FindItemByInstanceId(packet.InstanceId);
            if (existing != null)
            {
                Plugin.Log.LogInfo($"[ITEM:RESYNC] Destroying wrong item {existing.name} for ID {packet.InstanceId}");
                Object.Destroy(existing.gameObject);
            }

            // 2. Spawn correct item
            if (packet.PrefabIndex <= 0 || packet.PrefabIndex >= PrefabsDirectory.instance.directory.Length)
            {
                Plugin.Log.LogError($"[ITEM:RESYNC] Invalid prefabIndex {packet.PrefabIndex}");
                return;
            }

            var prefabObj = PrefabsDirectory.instance.directory[packet.PrefabIndex];
            if (prefabObj == null)
            {
                Plugin.Log.LogError($"[ITEM:RESYNC] Prefab at index {packet.PrefabIndex} is null");
                return;
            }

            var go = Object.Instantiate(prefabObj);

            // 3. Set instanceId
            var saveable = go.GetComponent<SaveablePrefab>();
            if (saveable != null)
            {
                saveable.instanceId = packet.InstanceId;
            }

            // 4. Set item properties
            var item = go.GetComponent<ShipItem>();
            if (item != null)
            {
                item.sold = packet.Sold;
                item.health = packet.Health;
                item.amount = packet.Amount;
            }

            // 5. Parent and position
            IsApplyingRemoteState = true;
            try
            {
                bool isOnBoat = packet.IsOnBoat && !string.IsNullOrEmpty(packet.BoatName);

                if (isOnBoat)
                {
                    // Full dual-transform setup for boat items
                    // Items on boat use two coordinate spaces:
                    // - ShipItem.transform in visual space (parented to boatModel)
                    // - ItemRigidbody.transform in physics space (parented to walkCol)
                    // Both use the SAME localPosition/localRotation values

                    var boat = BoatUtility.FindBoatByName(packet.BoatName);
                    if (boat == null)
                    {
                        Plugin.Log.LogWarning($"[ITEM:RESYNC] Boat {packet.BoatName} not found");
                        return;
                    }

                    var boatRefs = boat.GetComponent<BoatRefs>();
                    if (boatRefs == null)
                    {
                        Plugin.Log.LogWarning($"[ITEM:RESYNC] BoatRefs not found on {packet.BoatName}");
                        return;
                    }

                    var boatModel = boatRefs.boatModel;
                    // Get walkCol via BoatEmbarkCollider (how game does it) with fallback
                    var embarkCol = boatRefs.GetComponentInChildren<BoatEmbarkCollider>();
                    var walkCol = embarkCol?.walkCollider ?? boatRefs.walkCol;

                    if (boatModel == null || walkCol == null)
                    {
                        Plugin.Log.LogWarning($"[ITEM:RESYNC] boatModel or walkCol is null on {packet.BoatName}");
                        return;
                    }

                    // 1. Set boat reference fields on ShipItem FIRST
                    if (item != null)
                    {
                        item.currentActualBoat = boatModel;
                        item.currentWalkCol = walkCol;
                    }

                    // 2. Parent ShipItem to visual boat (boatModel)
                    go.transform.SetParent(boatModel, worldPositionStays: false);
                    go.transform.localPosition = packet.LocalPosition;
                    go.transform.localRotation = packet.Rotation;

                    VerboseLogger.ItemApply($"Resync {packet.InstanceId} boat: parent={boatModel.name}, localPos={packet.LocalPosition}");

                    // 3. Setup ItemRigidbody in physics space
                    if (item?.itemRigidbodyC != null)
                    {
                        var itemRbTransform = item.itemRigidbodyC.transform;

                        // Parent ItemRigidbody to physics walk collider
                        itemRbTransform.SetParent(walkCol, worldPositionStays: false);

                        // Set same local position/rotation (different parent handles coordinate conversion)
                        itemRbTransform.localPosition = packet.LocalPosition;
                        itemRbTransform.localRotation = packet.Rotation;

                        // Reset physics state
                        var rb = item.itemRigidbodyC.GetBody();
                        if (rb != null)
                        {
                            rb.velocity = Vector3.zero;
                            rb.angularVelocity = Vector3.zero;
                        }

                        // Enable colliders
                        foreach (var col in item.itemRigidbodyC.GetComponents<Collider>())
                            col.enabled = true;

                        // Enable ItemRigidbody component
                        item.itemRigidbodyC.enabled = true;

                        // Mark on-boat so FixedUpdate uses the vanilla
                        // MoveItemToWalkColRigidbody mapping instead of snapping a SOLD item's mesh into the
                        // boat's separate ~205m physics frame (see OnRemoteItemDropped for the full rationale).
                        try { HarmonyLib.Traverse.Create(item.itemRigidbodyC).Field("onBoat").SetValue(true); }
                        catch (System.Exception e) { Plugin.Log.LogWarning($"[ITEM:RESYNC] could not set onBoat on {packet.InstanceId}: {e.Message}"); }

                        VerboseLogger.ItemApply($"Resync {packet.InstanceId} ItemRigidbody: parent={walkCol.name}");
                    }
                }
                else
                {
                    // Land item: add receiver's offset and use world position
                    var offset = FloatingOriginManager.instance?.outCurrentOffset ?? Vector3.zero;
                    var worldPos = packet.LocalPosition + offset;
                    var shiftingWorld = GameObject.Find("_shifting world");

                    // Clear boat references the vanilla way - see ClearBoatLatch (stale private
                    // currentBoatCollider from a manual clear permanently blocked re-latching).
                    if (item != null)
                    {
                        ClearBoatLatch(item);
                    }

                    go.transform.SetParent(shiftingWorld?.transform);
                    go.transform.position = worldPos;
                    go.transform.rotation = packet.Rotation;

                    if (item?.itemRigidbodyC != null)
                    {
                        item.itemRigidbodyC.transform.SetParent(shiftingWorld?.transform);
                        item.itemRigidbodyC.transform.position = worldPos;
                        item.itemRigidbodyC.transform.rotation = packet.Rotation;

                        // Reset physics state
                        var rb = item.itemRigidbodyC.GetBody();
                        if (rb != null)
                        {
                            rb.velocity = Vector3.zero;
                            rb.angularVelocity = Vector3.zero;
                        }

                        // Enable colliders
                        foreach (var col in item.itemRigidbodyC.GetComponents<Collider>())
                            col.enabled = true;

                        // Enable ItemRigidbody component
                        item.itemRigidbodyC.enabled = true;
                    }

                    VerboseLogger.ItemApply($"Resync {packet.InstanceId} land: worldPos={worldPos}");
                }

                // Reset layer to Default (0) for raycast interaction
                go.layer = 0;
            }
            finally
            {
                IsApplyingRemoteState = false;
            }

            // Register mission cargo to its mission
            var good = go.GetComponent<Good>();
            if (good != null)
            {
                if (packet.MissionIndex > -1)
                {
                    var mission = PlayerMissions.missions[packet.MissionIndex];
                    if (mission != null)
                    {
                        mission.RegisterGood(go);
                        VerboseLogger.Log("ITEM", "RESYNC", $"Registered item {packet.InstanceId} to mission slot {packet.MissionIndex}");
                    }
                    else
                    {
                        Plugin.Log.LogWarning($"[ITEM:RESYNC] Mission slot {packet.MissionIndex} is empty, registering as missionless");
                        good.RegisterAsMissionless();
                    }
                }
                else
                {
                    good.RegisterAsMissionless();
                }
            }

            // Mark as recently synced to protect from cleanup
            _recentlyRemoteSyncedItems[packet.InstanceId] = Time.time;

            Plugin.Log.LogInfo($"[ITEM:RESYNC] Spawned correct item {go.name} at {packet.LocalPosition}, mission={packet.MissionIndex}");
        }

        #endregion

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

            Plugin.Profiler?.StartMeasure();

            // Update visual position of items held by remote player
            UpdateRemoteHeldItemVisuals();

            // Send a one-shot reliable terminal for locally dropped items once they come to rest,
            // so every machine converges on the dropper's resting pose.
            SweepDropSettleTerminals();

            Plugin.Profiler?.EndMeasure("Items");
        }

        // Synced held-item position, PER CARRIER (from PlayerPosition packets at 20Hz).
        // EACH crew member carries their OWN item, so this is a per-carrier map keyed by the body
        // AUTHOR (the SteamId of whoever is carrying the item). Each carrier's item renders/positions
        // against THAT carrier's avatar, so two crew carrying items simultaneously don't fight over one
        // slot. At N=1 there is exactly one carrier entry.
        // Positions are stored boat-relative and converted to world each frame.
        private class HeldItemState
        {
            public int ItemId;
            public Vector3 RelativePos;   // boat-relative if on boat, world if on land
            public Quaternion RelativeRot;
            public bool IsOnBoat;
            // Authoritative boat ROOT name from the carrier's position packet, so the visuals
            // loop resolves the CARRIER's hull by name instead of "nearest embark collider to MY camera"
            // (which picks the wrong hull with multiple boats / distant observers).
            public string BoatName;
        }
        // carrier SteamId -> their currently-synced held item
        private readonly Dictionary<SteamId, HeldItemState> _syncedHeldItems = new Dictionary<SteamId, HeldItemState>();
        // reverse index itemId -> carrier, so the visuals loop (which enumerates items by id) can find the
        // carrier for an item in O(1). Kept in lockstep with _syncedHeldItems.
        private readonly Dictionary<int, SteamId> _heldItemCarrier = new Dictionary<int, SteamId>();

        /// <summary>
        /// Updates the synced position for ONE carrier's held item (called from PlayerPosition handler).
        /// Don't convert to world here - store boat-relative and convert each frame.
        /// </summary>
        // `sender` is the body AUTHOR (the crew member actually carrying the item). We
        // route the update into THAT carrier's slot so multiple carriers no longer collide. A carrier
        // switching to a different item simply overwrites its own slot (and we re-point the reverse index).
        public void UpdateRemoteHeldItemPosition(int itemId, Vector3 relativePos, Quaternion rotation, bool isOnBoat, string boatName, SteamId sender = default)
        {
            if (!_syncedHeldItems.TryGetValue(sender, out var state))
            {
                state = new HeldItemState();
                _syncedHeldItems[sender] = state;
            }

            // If this carrier was carrying a DIFFERENT item, drop the stale reverse-index entry first.
            if (state.ItemId != itemId)
            {
                if (_heldItemCarrier.TryGetValue(state.ItemId, out var prevCarrier) && prevCarrier == sender)
                    _heldItemCarrier.Remove(state.ItemId);
            }

            state.ItemId = itemId;
            state.IsOnBoat = isOnBoat;
            state.BoatName = boatName;
            _heldItemCarrier[itemId] = sender;

            if (isOnBoat)
            {
                // Store boat-relative position - will convert to world each frame in UpdateRemoteHeldItemVisuals
                // This keeps item stable relative to boat as it moves between 20Hz sync packets
                state.RelativePos = relativePos;
                state.RelativeRot = rotation;
            }
            else
            {
                // On land - convert to world now (world doesn't move relative to itself)
                // Sender subtracted their FOM offset, we add ours back
                var offset = FloatingOriginManager.instance?.outCurrentOffset ?? Vector3.zero;
                state.RelativePos = relativePos + offset;
                state.RelativeRot = rotation;
            }
        }

        /// <summary>
        /// Public per-peer held-item visual cleanup, safe on EITHER role. The host's authoritative
        /// OnPeerDisconnected also drops items + inventory; this method only forgets the per-carrier synced
        /// VISUAL slot, so it's the right call for a GUEST that sees a fellow guest leave (the host-only
        /// OnPeerDisconnected early-returns on a guest, leaving the visual slot dangling otherwise). At N=1 a
        /// guest never sees a fellow guest leave, so this is never reached there.
        /// </summary>
        public void ForgetCarrierHeldItemVisual(SteamId carrier)
        {
            ClearSyncedHeldItemForCarrier(carrier);
        }

        /// <summary>
        /// Forget a carrier's synced held-item slot (e.g. on their disconnect, or when their item is
        /// dropped/destroyed). Keeps the reverse index consistent. At N=1 this clears the single slot.
        /// </summary>
        private void ClearSyncedHeldItemForCarrier(SteamId carrier)
        {
            if (_syncedHeldItems.TryGetValue(carrier, out var state))
            {
                if (_heldItemCarrier.TryGetValue(state.ItemId, out var c) && c == carrier)
                    _heldItemCarrier.Remove(state.ItemId);
                _syncedHeldItems.Remove(carrier);
            }
        }

        /// <summary>
        /// Forget a synced held-item slot by ITEM id (e.g. when that specific item is dropped/destroyed),
        /// regardless of which carrier had it. Keeps both maps consistent.
        /// </summary>
        private void ClearSyncedHeldItemById(int itemId)
        {
            if (_heldItemCarrier.TryGetValue(itemId, out var carrier))
            {
                _heldItemCarrier.Remove(itemId);
                if (_syncedHeldItems.TryGetValue(carrier, out var state) && state.ItemId == itemId)
                    _syncedHeldItems.Remove(carrier);
            }
        }

        /// <summary>
        /// Find BoatEmbarkCollider near the camera - same approach as RemotePlayerManager.
        /// </summary>
        private BoatEmbarkCollider FindBoatEmbarkColliderNearCamera()
        {
            if (Camera.main == null) return null;

            var camPos = Camera.main.transform.position;
            var allEmbarkColliders = Object.FindObjectsOfType<BoatEmbarkCollider>();

            BoatEmbarkCollider nearest = null;
            float nearestDist = float.MaxValue;

            foreach (var collider in allEmbarkColliders)
            {
                var dist = Vector3.Distance(collider.transform.position, camPos);
                if (dist < nearestDist && dist < 200f)
                {
                    nearestDist = dist;
                    nearest = collider;
                }
            }

            return nearest;
        }

        /// <summary>
        /// Updates the visual position of items held by the remote player.
        /// Uses synced position if available, otherwise falls back to capsule offset.
        /// Converts boat-relative to world EACH FRAME to avoid jitter.
        /// </summary>
        private void UpdateRemoteHeldItemVisuals()
        {
            if (_remoteHeldItems.Count == 0) return;

            foreach (var kvp in _remoteHeldItems)
            {
                var item = kvp.Value;
                if (item == null || item.gameObject == null) continue;

                // Resolve THIS item's carrier and that carrier's synced position (if any). Each carrier's
                // item is positioned against that carrier's own avatar, so concurrent carriers no longer
                // collide. At N=1 there is one carrier, identical to the old single-slot lookup.
                HeldItemState state = null;
                SteamId carrier = default;
                if (_heldItemCarrier.TryGetValue(kvp.Key, out carrier))
                {
                    _syncedHeldItems.TryGetValue(carrier, out state);
                    // Guard: the carrier's slot may have moved to a different item.
                    if (state != null && state.ItemId != kvp.Key) state = null;
                }

                // Use synced position if available for this item
                if (state != null)
                {
                    Vector3 worldPos;
                    Quaternion worldRot;

                    if (state.IsOnBoat)
                    {
                        // Convert boat-relative to world EACH FRAME
                        // This keeps item stable relative to boat as it moves.
                        // Resolve the CARRIER's boat by the authoritative ROOT name from
                        // their position packet (same approach as the avatar frame), NOT by nearest
                        // embark collider to MY camera - that picked the wrong hull with multiple boats.
                        Transform boatModel = null;
                        var boatRoot = BoatUtility.FindBoatByName(state.BoatName);
                        if (boatRoot != null)
                        {
                            boatModel = boatRoot.GetComponent<BoatRefs>()?.boatModel;
                        }
                        if (boatModel == null)
                        {
                            // Last resort: legacy camera-proximity resolution (single-boat case).
                            var boatEmbarkCollider = FindBoatEmbarkColliderNearCamera();
                            if (boatEmbarkCollider != null) boatModel = boatEmbarkCollider.transform.parent;
                        }
                        if (boatModel != null)
                        {
                            worldPos = boatModel.TransformPoint(state.RelativePos);
                            worldRot = boatModel.rotation * state.RelativeRot;
                        }
                        else
                        {
                            // Can't find boat - use capsule fallback
                            goto capsuleFallback;
                        }
                    }
                    else
                    {
                        // On land - already in world coords
                        worldPos = state.RelativePos;
                        worldRot = state.RelativeRot;
                    }

                    item.transform.position = worldPos;
                    item.transform.rotation = worldRot;

                    // Also update ItemRigidbody to prevent physics interference
                    if (item.itemRigidbodyC != null)
                    {
                        item.itemRigidbodyC.transform.position = worldPos;
                        item.itemRigidbodyC.transform.rotation = worldRot;
                    }
                    continue;
                }

            capsuleFallback:
                // Fallback: position at the CARRIER's avatar capsule (for items not yet synced or boat not
                // found). Resolve the avatar by the carrier SteamId so the item follows the right crew
                // member's body. If we don't know the carrier (no synced position yet for this item),
                // there's no avatar to attach to, so leave the item where it is.
                Transform remotePlayer = null;
                if (state != null)
                {
                    remotePlayer = Plugin.RemotePlayerManager?.GetAvatar(carrier)?.GetRemoteCapsule();
                }
                if (remotePlayer != null)
                {
                    Vector3 holdOffset = remotePlayer.forward * 0.5f + Vector3.up * 0.5f;
                    item.transform.position = remotePlayer.position + holdOffset;
                    item.transform.rotation = remotePlayer.rotation;
                }
            }
        }

        /// <summary>
        /// Finds an item by its instanceId across all loaded scenes.
        /// </summary>
        public static ShipItem FindItemByInstanceId(int instanceId)
        {
            var prefabs = Object.FindObjectsOfType<SaveablePrefab>();
            ShipItem found = null;
            int count = 0;

            foreach (var prefab in prefabs)
            {
                if (prefab.instanceId == instanceId)
                {
                    count++;
                    if (found == null)
                    {
                        found = prefab.GetComponent<ShipItem>();
                    }
                    else
                    {
                        // Log duplicate for debugging
                        var item = prefab.GetComponent<ShipItem>();
                        Plugin.Log.LogWarning($"DUPLICATE item id={instanceId}: " +
                            $"first={found?.name} at {found?.transform.position}, " +
                            $"dup={item?.name} at {item?.transform.position}, sold={item?.sold}");
                    }
                }
            }

            if (count > 1)
            {
                Plugin.Log.LogWarning($"FindItemByInstanceId: {count} items with id={instanceId}!");
            }

            return found;
        }

        /// <summary>
        /// Like FindItemByInstanceId but also scans INACTIVE scene objects (Object.FindObjectsOfType can't see
        /// SetActive(false) ones). Used by disconnect cleanup to recover a leaver's inventory-stashed item that
        /// was hidden via SetActive(false), so it gets reactivated + dropped instead of leaked. Scene-only:
        /// scene.IsValid() skips prefab ASSETS (which Resources.FindObjectsOfTypeAll also returns).
        /// </summary>
        public static ShipItem FindInactiveItemByInstanceId(int instanceId)
        {
            foreach (var prefab in Resources.FindObjectsOfTypeAll<SaveablePrefab>())
            {
                if (prefab == null || prefab.instanceId != instanceId) continue;
                if (!prefab.gameObject.scene.IsValid()) continue; // skip prefab assets, keep real scene instances
                return prefab.GetComponent<ShipItem>();
            }
            return null;
        }

        /// <summary>
        /// Finds the nearest item of a specific prefab type near a position.
        /// Used for lazy ID correlation - when a player picks up an item, we find
        /// the matching local item by type and position rather than by pre-synced ID.
        /// </summary>
        /// <param name="prefabIndex">The prefab type to match</param>
        /// <param name="position">World position to search near</param>
        /// <param name="boatName">If not empty, search on this boat using local coords</param>
        /// <param name="isLocalPosition">If true, position is boat-local</param>
        /// <param name="maxDistance">Maximum distance to consider (default 3m)</param>
        /// <param name="logMissAsError">Pass false when a miss is EXPECTED (e.g. join-time dedup orphan
        /// probe) so the miss logs as a neutral [ITEM:DEDUP] line instead of a [ITEM:CORRELATE] miss that
        /// reads like an error. Default true preserves existing behavior for all other callers.</param>
        public static ShipItem FindItemByPrefabNearPosition(int prefabIndex, Vector3 position, string boatName, bool isLocalPosition, float maxDistance = 3f, bool logMissAsError = true)
        {
            ShipItem nearest = null;
            float nearestDist = maxDistance;

            // If on boat, find the boat and convert position
            Transform searchParent = null;
            Vector3 searchPos = position;

            if (isLocalPosition && !string.IsNullOrEmpty(boatName))
            {
                var boats = BoatUtility.FindAllBoats();
                if (boats.TryGetValue(boatName, out var boat))
                {
                    var boatRefs = boat.GetComponent<BoatRefs>();
                    searchParent = boatRefs?.boatModel;
                    // Position is already local, we'll compare to item.localPosition
                }
            }

            var allItems = Object.FindObjectsOfType<ShipItem>();
            foreach (var item in allItems)
            {
                if (item == null) continue;

                var prefab = item.GetComponent<SaveablePrefab>();
                if (prefab == null || prefab.prefabIndex != prefabIndex) continue;

                // Skip items already held
                if (item.held != null) continue;

                // Calculate distance
                float dist;
                if (searchParent != null)
                {
                    // Compare local positions on boat
                    if (item.transform.parent == searchParent || item.transform.IsChildOf(searchParent))
                    {
                        dist = Vector3.Distance(item.transform.localPosition, position);
                    }
                    else
                    {
                        continue; // Item not on this boat
                    }
                }
                else
                {
                    // Compare world positions
                    dist = Vector3.Distance(item.transform.position, searchPos);
                }

                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearest = item;
                }
            }

            if (nearest != null)
            {
                VerboseLogger.Log("ITEM", "CORRELATE", $"Found {nearest.name} (prefab={prefabIndex}) at dist={nearestDist:F2}m");
            }
            else if (logMissAsError)
            {
                VerboseLogger.Log("ITEM", "CORRELATE", $"No item found for prefab={prefabIndex} near pos={position} (maxDist={maxDistance})");
            }
            else
            {
                VerboseLogger.Log("ITEM", "DEDUP", $"no local orphan (expected) for prefab={prefabIndex} near pos={position} (maxDist={maxDistance})");
            }

            return nearest;
        }

        /// <summary>
        /// Checks if an item is currently held by any player.
        /// </summary>
        public bool IsItemHeld(int instanceId)
        {
            return _heldItems.ContainsKey(instanceId);
        }

        /// <summary>
        /// Gets the SteamId of the player holding an item, or default if not held.
        /// </summary>
        public SteamId GetItemHolder(int instanceId)
        {
            return _heldItems.TryGetValue(instanceId, out var holder) ? holder : default;
        }

        /// <summary>
        /// Contested-grab helper: find who currently holds a physical ShipItem OBJECT, regardless of which
        /// per-client random instanceId it was tracked under. The id-keyed IsItemHeld/GetItemHolder can't see
        /// that a correlated crate is already held under the WINNER's different id; this reverse lookup over
        /// _remoteHeldItems (object -> id) + _heldItems (id -> holder) can. Small map, host-side only.
        /// </summary>
        private bool TryGetHolderOfObject(ShipItem obj, out SteamId holder)
        {
            holder = default;
            if (obj == null) return false;
            foreach (var kvp in _remoteHeldItems)
            {
                if (kvp.Value == obj && _heldItems.TryGetValue(kvp.Key, out holder))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Enable/disable the ShipItem's OWN trigger collider(s) (the ones on the
        /// ShipItem GameObject that ShipItem.Awake forces isTrigger=true - distinct from the itemRigidbodyC
        /// clone's colliders). Disabled while an item is remotely held so the host-driven, avatar-following
        /// item can't enter a real land Shopkeeper's trigger and pop a sell menu for it; re-enabled on drop.
        /// </summary>
        private static void SetShipItemOwnTriggers(ShipItem item, bool enabled)
        {
            if (item == null) return;
            foreach (var col in item.GetComponents<Collider>())
                col.enabled = enabled;
        }

        /// <summary>
        /// Check if item was recently synced via network.
        /// Used to protect items from cleanup after ItemDropped/ItemSpawned.
        /// </summary>
        public bool IsRecentlySynced(int instanceId)
        {
            if (_recentlyRemoteSyncedItems.TryGetValue(instanceId, out float syncTime))
            {
                return Time.time - syncTime < REMOTE_SYNC_PROTECTION_TIME;
            }
            return false;
        }

        // The JOIN coroutine's long-lived guard goes to _applyingJoinState (NOT the property),
        // so a per-packet handler toggling the property mid-join can't clear it.
        public void SetApplyingRemoteState(bool value)
        {
            _applyingJoinState = value;
        }

        /// <summary>
        /// Mark an item as recently synced from remote, protecting it from broadcast on destruction.
        /// Items spawned during join shouldn't broadcast ItemDestroyed to host.
        /// </summary>
        public void MarkAsRecentlySynced(int instanceId)
        {
            _recentlyRemoteSyncedItems[instanceId] = Time.time;
        }

        /// <summary>
        /// Reset state when leaving multiplayer session.
        /// </summary>
        public void Reset()
        {
            _heldItems.Clear();
            _remoteHeldItems.Clear();
            _guestInventoryItems.Clear();
            _joinDestructionGuardCount = 0;
            _lastHealthSyncTime.Clear();
            _recentlyRemoteSyncedItems.Clear();
            _itemRegistry.Clear();
            _syncedHeldItems.Clear();
            _heldItemCarrier.Clear();
            // _syncedItemIds MUST be cleared here: if it survived into a NEXT session, the host's skip-
            // respawn check would still hold stale ids and a new guest would never receive ItemSpawned for
            // those scene items (item invisible on the guest) - plus it would grow across the process
            // lifetime. _justPurchasedItems + _remoteUnsealingCrates cleared for per-session hygiene.
            _syncedItemIds.Clear();
            _justPurchasedItems.Clear();
            _remoteUnsealingCrates.Clear();
            _pendingUnsealCrateIds.Clear();
            _unsealingCrateIds.Clear();
            _pendingDropTerminals.Clear();
            _skippedDropsWhileHeld.Clear();
        }

        /// <summary>
        /// Per-holder inventory bookkeeping helper (host). Records that <paramref name="holder"/> now
        /// carries <paramref name="instanceId"/>, so a later disconnect of that holder drops only its
        /// own items. At N=1 there is a single holder, so this is equivalent to a flat HashSet.Add.
        /// </summary>
        private void AddToGuestInventory(SteamId holder, int instanceId)
        {
            if (!_guestInventoryItems.TryGetValue(holder, out var set))
            {
                set = new HashSet<int>();
                _guestInventoryItems[holder] = set;
            }
            set.Add(instanceId);
        }

        /// <summary>
        /// Remove an item from whichever holder's inventory set contains it (host). The callers that
        /// remove items (drop/destroy) don't know the holder, so we scan the per-holder partition. At
        /// N=1 there is a single holder set, so this is equivalent to a flat HashSet.Remove.
        /// </summary>
        private void RemoveFromGuestInventory(int instanceId)
        {
            SteamId emptiedHolder = default;
            bool removed = false;
            foreach (var kvp in _guestInventoryItems)
            {
                if (kvp.Value.Remove(instanceId))
                {
                    if (kvp.Value.Count == 0) emptiedHolder = kvp.Key;
                    removed = true;
                    break;
                }
            }
            // Prune an emptied holder set AFTER the loop (never mutate the dict while enumerating it).
            if (removed && emptiedHolder != default) _guestInventoryItems.Remove(emptiedHolder);
        }

        #region Local Events (called from patches)

        /// <summary>
        /// Called when local player picks up an item.
        /// Host: records and broadcasts. Guest: sends request.
        /// </summary>
        public void OnLocalPickup(ShipItem item, int inventorySlot = -1)
        {
            if (IsApplyingRemoteState) return;
            if (item == null) return;

            var prefab = item.GetComponent<SaveablePrefab>();
            if (prefab == null) return;

            int instanceId = prefab.instanceId;

            // SEND-SIDE HARDENING: never broadcast id=0. Unsold vendor-table items all share instanceId==0;
            // if we broadcast 0, receivers that look up by id match an ARBITRARY first id-0 item (wrong prefab).
            // Assign a real id here (mirrors CollectWorldItems / EnsureItemHasId) so every pickup carries a
            // stable non-zero id. Receivers still guard against 0 (old/other senders), but this fixes the source.
            if (instanceId == 0)
            {
                instanceId = GenerateItemId();
                prefab.instanceId = instanceId;
                Plugin.Log.LogInfo($"[ITEM:PICKUP] Assigned new ID {instanceId} to {item.name}");
            }

            // Calculate position for lazy ID correlation
            // Use boat-local if on boat, world position if on land
            Vector3 position;
            string boatName = "";
            bool isLocalPosition = false;

            if (item.currentActualBoat != null)
            {
                // On boat - use local position
                boatName = item.currentActualBoat.name;
                position = item.transform.localPosition;
                isLocalPosition = true;
            }
            else
            {
                // On land - use world position
                position = item.transform.position;
            }

            VerboseLogger.ItemLocal($"Pickup, item={item.name}, id={instanceId}, slot={inventorySlot}, boat={boatName}, pos={position}, sold={item.sold}");

            if (Plugin.IsHost)
            {
                // If item hasn't been synced to guest yet, spawn it first
                // This handles scene items (pre-placed in world) that guest doesn't have
                // EXCEPTION: Shop items exist on both sides - use lazy ID correlation instead
                // Check WasJustPurchased to detect shop items (set by Sell Prefix before PickUpItem)
                bool isShopPurchase = WasJustPurchased(instanceId);

                if (isShopPurchase)
                {
                    VerboseLogger.ItemLocal($"Item {instanceId} is shop purchase, using lazy ID correlation (no ItemSpawned)");
                }
                else if (AnyPeerMissingSyncedItem(instanceId))
                {
                    // Per-peer gate. With a GLOBAL set, an id sent to an EARLIER joiner
                    // would be skipped forever, so a LATER joiner would never get the ItemSpawned and the host's
                    // drops would no-op on him. So we re-broadcast if ANY currently-connected peer is missing it. The
                    // ItemSpawned send is a SendToAll broadcast;
                    // peers that already have the id ignore the duplicate (OnRemoteItemSpawned early-returns on
                    // an existing instanceId), so re-sending is a safe no-op for them. Then mark synced for ALL
                    // connected peers (the broadcast reached every one). At N=1 this is exactly "send
                    // once, then skip".
                    SendItemSpawnedForExisting(item, prefab);
                    MarkSyncedForAllPeers(instanceId);
                    VerboseLogger.ItemSend($"Synced scene item {instanceId} to guest(s) before pickup (a peer was missing it)");
                }
                else
                {
                    VerboseLogger.ItemLocal($"Item {instanceId} already synced to all peers, skipping spawn sync");
                }

                // Host: register item for validation and record held state
                _itemRegistry[instanceId] = prefab.prefabIndex;
                _heldItems[instanceId] = SteamClient.SteamId;
                SendItemPickedUp(instanceId, SteamClient.SteamId, inventorySlot, prefab.prefabIndex, position, boatName, isLocalPosition);
            }
            else
            {
                // Guest: send request to host (host-authoritative)
                VerboseLogger.ItemSend($"Guest requesting pickup for item {instanceId}");
                SendItemPickupRequest(instanceId, prefab.prefabIndex, inventorySlot, position, boatName, isLocalPosition);
            }
        }

        /// <summary>
        /// Called when local player drops an item.
        /// </summary>
        public void OnLocalDrop(ShipItem item)
        {
            if (IsApplyingRemoteState) return;
            if (item == null) return;

            var prefab = item.GetComponent<SaveablePrefab>();
            if (prefab == null) return;

            // Skip drop sync if item is in inventory slot
            // (Game calls DropItem when item enters inventory, but it's not a real drop)
            if (item.itemRigidbodyC?.GetCurrentInventorySlot() != null)
            {
                VerboseLogger.ItemLocal($"Skipping drop sync for item {prefab.instanceId} - in inventory slot");
                return;
            }

            // Skip drop sync if item is hanging on a hook
            // (Game calls DropItem after OnItemClick, but hung items sync via ItemHung packet)
            var hangable = item.GetComponent<HangableItem>();
            if (hangable != null && hangable.IsHanging())
            {
                VerboseLogger.ItemLocal($"Skipping drop sync for item {prefab.instanceId} - hanging on hook");
                return;
            }

            // Delay sync for wall-attachable items to let snap complete
            if (item.wallAttachment)
            {
                StartCoroutine(DelayedDropSync(item, prefab.instanceId));
                return;
            }

            // Immediate sync for normal items
            SendDropPacket(item, prefab.instanceId);
        }

        private IEnumerator DelayedDropSync(ShipItem item, int instanceId)
        {
            // Wait for wall snap to complete
            yield return new WaitForSeconds(0.15f);

            if (item == null) yield break;

            SendDropPacket(item, instanceId);
        }

        // === Dropped-item settle terminal ===
        //
        // A drop is sent ONCE with the release pose; each machine then simulates the fall LOCALLY, so the
        // remote rigidbody diverges (framerate dependent) and can sleep early, leaving the item's resting
        // position/rotation different across machines until someone picks it up. Mirror the mooring-rope /
        // regular-rope settle terminals in ControlSyncManager: the machine that made the LOCAL drop owns
        // the trajectory, tracks the item until its rigidbody settles, then re-sends ONE reliable
        // ItemDropped carrying the final pose. Receivers apply it through the normal OnRemoteItemDropped
        // snap (dual-frame boat parenting, floating-origin offset, zero velocity), so everyone converges
        // on the dropper's resting pose. Reuses the existing ItemDropped packet verbatim: no wire change,
        // and the host relays the terminal to other guests exactly like any other drop.
        private class DropSettleTracker
        {
            public ShipItem Item;
            public int InstanceId;
            public float DropTime;         // when the drop was sent; base for the hard timeout
            public float QuietSince;       // when the current quiet window started
            public bool SeenDynamic;       // the rigidbody has been observed non-kinematic since the drop
            public bool HasRef;            // a reference pose has been captured
            public Vector3 RefLocalPos;    // pose relative to the physics parent frame, so a moving boat
            public Quaternion RefLocalRot; // or a floating-origin shift does not look like item motion
        }
        private readonly Dictionary<int, DropSettleTracker> _pendingDropTerminals = new Dictionary<int, DropSettleTracker>();
        private readonly List<int> _dropTerminalRemovalScratch = new List<int>();
        private readonly List<DropSettleTracker> _settledDropScratch = new List<DropSettleTracker>();
        // Drops skipped because the LOCAL player appeared to hold the item, kept so a later
        // ItemPickupDenied (a contested grab this player lost) can replay the authoritative pose
        // instead of leaving this machine at its own diverged local pose.
        private struct SkippedDrop { public ItemDroppedPacket Packet; public float Time; }
        private readonly Dictionary<int, SkippedDrop> _skippedDropsWhileHeld = new Dictionary<int, SkippedDrop>();
        private const float SkippedDropReplayWindow = 5f; // a stale skipped drop is never replayed
        private const float DropSettleQuietTime = 0.4f;    // consecutive quiet seconds before the pose counts as settled
        private const float DropSettleTimeout = 10f;       // stop tracking a never-settling item (e.g. rocking on a heeling boat)
        private const float DropSettlePosEpsilonSq = 0.005f * 0.005f; // 5 mm of local movement resets the quiet window
        private const float DropSettleAngEpsilon = 1f;     // 1 degree of local rotation resets the quiet window

        /// <summary>
        /// Sweeps items dropped by the LOCAL player until their rigidbody settles, then re-sends ONE
        /// reliable ItemDropped with the final resting pose so receivers snap to the dropper's result
        /// instead of keeping their own diverged simulation. Settled = the rigidbody sleeps, or its pose
        /// relative to its physics parent stays within tiny epsilons for DropSettleQuietTime. The pose is
        /// compared in the parent's LOCAL frame (walkCol on a boat, shifting world on land) so an item at
        /// rest on a sailing boat's deck still settles, while a genuinely rocking item keeps resetting the
        /// window and is dropped from tracking at the hard timeout WITHOUT a terminal. Entries are removed
        /// on send, pickup, destroy/hide, remote drop for the same item, and timeout, and the dict is cleared in
        /// Reset(), so it stays bounded by concurrently settling local drops. Only reachable from Update's
        /// IsMultiplayer gate, and only armed by local drop sends, so solo play never runs any of this.
        /// </summary>
        private void SweepDropSettleTerminals()
        {
            if (_pendingDropTerminals.Count == 0) return;

            float now = Time.time;
            _dropTerminalRemovalScratch.Clear();
            _settledDropScratch.Clear();

            foreach (var kvp in _pendingDropTerminals)
            {
                var t = kvp.Value;
                var item = t.Item;

                // Item gone (destroyed/sold-away) or hidden (moved into an inventory): stop quietly.
                if (item == null || !item.gameObject.activeInHierarchy)
                {
                    _dropTerminalRemovalScratch.Add(kvp.Key);
                    continue;
                }

                // Item is held again (locally via vanilla held, by anyone per the holder map) or is being
                // visually driven as a remote-held item: the drop trajectory no longer matters. Safe to
                // check held here even though the drop patch is a PREFIX (held still set at arm time):
                // vanilla DropItem finishes in the same call stack, so held is null before any Update runs.
                if (item.held != null || _heldItems.ContainsKey(kvp.Key) || _remoteHeldItems.ContainsKey(kvp.Key))
                {
                    _dropTerminalRemovalScratch.Add(kvp.Key);
                    continue;
                }

                // Hard timeout: a never-settling item is not tracked forever and gets no terminal.
                if (now - t.DropTime > DropSettleTimeout)
                {
                    _dropTerminalRemovalScratch.Add(kvp.Key);
                    continue;
                }

                var rb = item.itemRigidbodyC != null ? item.itemRigidbodyC.GetBody() : item.GetComponent<Rigidbody>();
                if (rb == null)
                {
                    VerboseLogger.ItemLocal($"Drop settle cancelled for item {t.InstanceId}: no rigidbody");
                    _dropTerminalRemovalScratch.Add(kvp.Key);
                    continue;
                }
                if (rb.isKinematic)
                {
                    // A kinematic body right after the drop is NOT a capture: vanilla keeps the body
                    // kinematic the whole time an item is held, and GoPointer.DropItem only clears the
                    // held reference; ItemRigidbody restores dynamics on its NEXT update after that.
                    // The drop patch is a prefix in that same call stack, so the first sweep frames can
                    // legitimately see a still-kinematic body. Do not cancel until the body has been
                    // observed dynamic at least once; the hard timeout above still bounds the entry.
                    if (!t.SeenDynamic) continue;
                    // Vanilla also flips a FREE item kinematic as housekeeping: a sleeping meshCol item
                    // once its dynamic-collider timer expires, and ALL items while the game is paused
                    // (sleep, recovery, shipyard). If the pose still matches the last dynamic sample,
                    // the body was frozen at its rest pose, which IS the settled pose: send the
                    // terminal now. Otherwise it was frozen mid-motion (global pause); keep the entry
                    // so tracking resumes when dynamics return, still bounded by the hard timeout.
                    if (t.HasRef)
                    {
                        var trKin = item.itemRigidbodyC != null ? item.itemRigidbodyC.transform : item.transform;
                        if ((trKin.localPosition - t.RefLocalPos).sqrMagnitude <= DropSettlePosEpsilonSq
                            && Quaternion.Angle(trKin.localRotation, t.RefLocalRot) <= DropSettleAngEpsilon)
                        {
                            VerboseLogger.ItemLocal($"Drop settle: item {t.InstanceId} went kinematic at its rest pose, sending terminal");
                            _dropTerminalRemovalScratch.Add(kvp.Key);
                            _settledDropScratch.Add(t);
                            continue;
                        }
                    }
                    continue;
                }
                t.SeenDynamic = true;

                bool settled = rb.IsSleeping();
                if (!settled)
                {
                    var tr = item.itemRigidbodyC != null ? item.itemRigidbodyC.transform : item.transform;
                    var localPos = tr.localPosition;
                    var localRot = tr.localRotation;
                    if (!t.HasRef
                        || (localPos - t.RefLocalPos).sqrMagnitude > DropSettlePosEpsilonSq
                        || Quaternion.Angle(localRot, t.RefLocalRot) > DropSettleAngEpsilon)
                    {
                        // Still moving in its parent frame: restart the quiet window from this pose.
                        t.HasRef = true;
                        t.RefLocalPos = localPos;
                        t.RefLocalRot = localRot;
                        t.QuietSince = now;
                        continue;
                    }
                    settled = now - t.QuietSince >= DropSettleQuietTime;
                }

                if (settled)
                {
                    _dropTerminalRemovalScratch.Add(kvp.Key);
                    _settledDropScratch.Add(t);
                }
            }

            foreach (var id in _dropTerminalRemovalScratch)
                _pendingDropTerminals.Remove(id);

            // Send AFTER the enumeration finishes. armSettleTerminal:false so the terminal send cannot
            // re-arm the tracker and loop forever. SendDropPacket re-encodes the item's CURRENT frame, so
            // an item that latched onto (or fell off) a boat after the drop goes out in the right frame.
            foreach (var t in _settledDropScratch)
            {
                VerboseLogger.ItemLocal($"Drop settle terminal for item {t.InstanceId} after {Time.time - t.DropTime:F2}s");
                SendDropPacket(t.Item, t.InstanceId, armSettleTerminal: false);
            }
            _settledDropScratch.Clear();
        }

        private void SendDropPacket(ShipItem item, int instanceId, bool armSettleTerminal = true)
        {
            // Determine parent boat if any
            string parentBoatName = "";
            bool isLocalPosition = false;
            Quaternion rotation = item.transform.localRotation;  // Use local rotation for consistency

            // Use actual item position for precise placement
            // item.transform.position reflects where the player placed/dropped the item
            Vector3 dropWorldPos = item.transform.position;

            VerboseLogger.ItemLocal($"Drop position: {dropWorldPos}");

            Vector3 position;
            // Resolve which boat frame to encode this drop in. Normally the item's own
            // embark latch (item.currentActualBoat) is set once the item has dwelled UN-HELD inside the
            // boat's EmbarkCol for a frame or two (ShipItem.ExtraFixedUpdate -> EnterBoat). But a CARRIED
            // item - notably a sealed mission CRATE - is `held` the whole time it is over the deck, so that
            // state machine never latches currentActualBoat before GoPointer.DropItem fires on the release
            // frame. With no boat frame the drop went out as a LAND drop (boat=""), and the receiver pinned
            // the crate to the static "_shifting world"; the boat then sailed away in the floating-origin
            // frame, leaving the crate's mesh behind at a stale world spot (invisible to anyone following
            // the boat) while its deck collider remained => "crate disappears on the guest but you can still
            // bump it". FALLBACK: when the item hasn't latched but its own trigger is inside a boat's
            // EmbarkCol (vanilla ShipItem.currentlyStayedEmbarkCol, private; read via Traverse), derive the
            // boat FROM THAT COLLIDER: embarkCol.transform.parent is exactly what vanilla EnterBoat assigns
            // to currentActualBoat, so the drop is encoded in the frame of the boat the item is PHYSICALLY
            // over. (v0.2.20 used GameState.currentBoat - the boat the local PLAYER stands on - here, which
            // broke the SETTLE TERMINAL: it re-derives the frame seconds after the drop, by which time the
            // player may have stepped off, so a deck drop was re-broadcast as a LAND drop at a stale world
            // position and receivers pinned the crate to the static world inside the moving hull. Deriving
            // from the item's own embark trigger is player-independent and stable across drop and settle.)
            // An item on a DOCK/LAND has no stayedEmbarkCol, so it still encodes as a land drop even if the
            // dropper stands aboard a moored boat. Solo: SendDropPacket is never reached (OnDropItemPrefix
            // gates on IsMultiplayer), so this is a no-op in singleplayer.
            Transform dropBoatModel = item.currentActualBoat;
            if (dropBoatModel == null)
            {
                Collider stayedEmbarkCol = null;
                try { stayedEmbarkCol = HarmonyLib.Traverse.Create(item).Field("currentlyStayedEmbarkCol").GetValue<Collider>(); }
                catch { }
                if (stayedEmbarkCol != null) dropBoatModel = stayedEmbarkCol.transform.parent;
            }
            var boatSaveable = dropBoatModel != null ? dropBoatModel.parent?.GetComponent<SaveableObject>() : null;
            if (dropBoatModel != null && boatSaveable != null)
            {
                parentBoatName = boatSaveable.gameObject.name;
                position = dropBoatModel.InverseTransformPoint(dropWorldPos);
                isLocalPosition = true;
                if (item.currentActualBoat == null)
                    VerboseLogger.ItemLocal($"Drop boat-frame fallback: item {instanceId} had no latched boat; using its embark-trigger boat ({parentBoatName})");
            }
            else
            {
                // World drop - convert to offset-independent coords (like spawn does)
                // SaveablePrefab uses: transform.position = data.position + offset
                // So we send: localPosition - offset (matching save format)
                var offset = FloatingOriginManager.instance?.outCurrentOffset ?? Vector3.zero;
                position = dropWorldPos - offset;
                VerboseLogger.ItemLocal($"Drop world item: dropWorldPos={dropWorldPos}, offset={offset}, adjustedPos={position}");
            }

            VerboseLogger.ItemLocal($"Drop, item={item.name}, id={instanceId}, boat={parentBoatName}, pos={position}, isLocal={isLocalPosition}");

            // Remove from held tracking
            _heldItems.Remove(instanceId);

            // HELD-ITEM PHANTOM FIX companion: held items are no longer serialized to joiners (join
            // snapshot + mission resync), so a peer that joined while this item was in-hand has never
            // seen its id - this drop is the first time the item surfaces for them, and a bare
            // ItemDropped would silently no-op there ("item not found"), leaving the item invisible.
            // Mirror OnLocalPickup's per-peer gate: spawn-sync first, then drop. Peers that already
            // track the id ignore the duplicate ItemSpawned (OnRemoteItemSpawned dedups by id). Shop
            // purchases keep using lazy id correlation, exactly like the pickup path.
            if (Plugin.IsHost && !WasJustPurchased(instanceId) && AnyPeerMissingSyncedItem(instanceId))
            {
                var dropPrefab = item.GetComponent<SaveablePrefab>();
                if (dropPrefab != null && dropPrefab.instanceId != 0)
                {
                    SendItemSpawnedForExisting(item, dropPrefab);
                    MarkSyncedForAllPeers(instanceId);
                    VerboseLogger.ItemSend($"Synced item {instanceId} to guest(s) before drop (a peer was missing it)");
                }
            }

            // Send drop packet
            SendItemDropped(instanceId, position, rotation, parentBoatName, isLocalPosition);

            // Arm the settle-terminal tracker for this LOCAL drop, so a divergent remote fall converges
            // once the item comes to rest here. Only real local drops arm it: remote applies never reach
            // SendDropPacket (OnLocalDrop and the drop patch both bail on IsApplyingRemoteState), and the
            // terminal send itself passes armSettleTerminal:false. Re-dropping the same item overwrites
            // its entry, so the dict stays keyed one entry per in-flight item.
            if (armSettleTerminal)
            {
                _pendingDropTerminals[instanceId] = new DropSettleTracker
                {
                    Item = item,
                    InstanceId = instanceId,
                    DropTime = Time.time
                };
            }
        }

        #endregion

        #region Send Methods

        private void SendItemPickedUp(int instanceId, SteamId playerId, int inventorySlot, int prefabIndex, Vector3 position, string boatName, bool isLocalPosition)
        {
            var packet = new ItemPickedUpPacket
            {
                ItemInstanceId = instanceId,
                PlayerSteamId = playerId.Value,
                InventorySlot = inventorySlot,
                PrefabIndex = prefabIndex,
                Position = position,
                ParentBoatName = boatName,
                IsLocalPosition = isLocalPosition
            };

            VerboseLogger.ItemSend($"ItemPickedUp, id={instanceId}, player={playerId}, prefab={prefabIndex}, pos={position}, boat={boatName}");

            Plugin.NetworkManager.SendToAllReliable(PacketType.ItemPickedUp, w =>
            {
                PacketSerializer.WriteItemPickedUp(w, packet);
            });
        }

        private void SendItemPickupRequest(int instanceId, int prefabIndex, int inventorySlot, Vector3 position, string boatName, bool isLocalPosition)
        {
            var packet = new ItemPickupRequestPacket
            {
                ItemInstanceId = instanceId,
                PrefabIndex = prefabIndex,
                InventorySlot = inventorySlot,
                Position = position,
                ParentBoatName = boatName,
                IsLocalPosition = isLocalPosition
            };

            VerboseLogger.ItemSend($"ItemPickupRequest, id={instanceId}, prefab={prefabIndex}, pos={position}, boat={boatName}");

            Plugin.NetworkManager.SendToAllReliable(PacketType.ItemPickupRequest, w =>
            {
                PacketSerializer.WriteItemPickupRequest(w, packet);
            });
        }

        private void SendItemDropped(int instanceId, Vector3 position, Quaternion rotation, string parentBoatName, bool isLocalPosition)
        {
            var packet = new ItemDroppedPacket
            {
                ItemInstanceId = instanceId,
                Position = position,
                Rotation = rotation,
                ParentBoatName = parentBoatName,
                IsLocalPosition = isLocalPosition
            };

            VerboseLogger.ItemSend($"ItemDropped, id={instanceId}, boat={parentBoatName}, pos={position}");

            Plugin.NetworkManager.SendToAllReliable(PacketType.ItemDropped, w =>
            {
                PacketSerializer.WriteItemDropped(w, packet);
            });
        }

        private void SendItemPickupDenied(int instanceId, byte reason, SteamId targetPlayer)
        {
            var packet = new ItemPickupDeniedPacket
            {
                ItemInstanceId = instanceId,
                Reason = reason
            };

            VerboseLogger.ItemSend($"ItemPickupDenied, id={instanceId}, reason={reason}");

            Plugin.NetworkManager.SendReliable(targetPlayer, PacketType.ItemPickupDenied, w =>
            {
                PacketSerializer.WriteItemPickupDenied(w, packet);
            });
        }

        #endregion

        #region Remote Event Handlers

        /// <summary>
        /// Called when receiving ItemPickedUp packet.
        /// Uses lazy ID correlation: find local item by prefab type + position if not found by ID.
        /// </summary>
        public void OnRemoteItemPickedUp(ItemPickedUpPacket packet, SteamId sender)
        {
            VerboseLogger.ItemRecv($"ItemPickedUp, id={packet.ItemInstanceId}, player={packet.PlayerSteamId}, prefab={packet.PrefabIndex}, pos={packet.Position}");

            // id==0 GUARD: unsold vendor-table items all share instanceId==0, so FindItemByInstanceId(0) /
            // _remoteHeldItems[0] would resolve to an ARBITRARY first id-0 item (wrong prefab). Skip the id-keyed
            // lookups entirely and go straight to prefab/position correlation, which is stable.
            ShipItem item = null;
            if (packet.ItemInstanceId != 0)
            {
                // Try to find item - first by ID, then by _remoteHeldItems (for hidden items), then by position correlation
                item = FindItemByInstanceId(packet.ItemInstanceId);

                // If not found by ID, check _remoteHeldItems (item may be hidden with SetActive(false))
                if (item == null && _remoteHeldItems.TryGetValue(packet.ItemInstanceId, out var heldItem))
                {
                    item = heldItem;
                    VerboseLogger.ItemApply($"Found hidden item {packet.ItemInstanceId} in _remoteHeldItems");
                }
            }

            if (item == null)
            {
                // LAZY ID CORRELATION: Find nearest item of same type near the position
                item = FindItemByPrefabNearPosition(
                    packet.PrefabIndex,
                    packet.Position,
                    packet.ParentBoatName,
                    packet.IsLocalPosition
                );

                if (item != null)
                {
                    // Found a matching local item - assign the remote player's ID to it
                    var prefab = item.GetComponent<SaveablePrefab>();
                    if (prefab != null)
                    {
                        int oldId = prefab.instanceId;
                        prefab.instanceId = packet.ItemInstanceId;

                        VerboseLogger.Log("ITEM", "CORRELATE", $"Assigned ID {packet.ItemInstanceId} to local item {item.name} (was {oldId})");
                    }
                }
            }

            if (item == null)
            {
                Plugin.Log.LogWarning($"OnRemoteItemPickedUp: no matching item for id={packet.ItemInstanceId}, prefab={packet.PrefabIndex}");
                return;
            }

            // Always register - whether found by ID or correlation
            _itemRegistry[packet.ItemInstanceId] = packet.PrefabIndex;

            // Record who is holding this item
            _heldItems[packet.ItemInstanceId] = new SteamId { Value = packet.PlayerSteamId };

            // Any stashed skipped drop predates this pickup, so it must never be replayed.
            _skippedDropsWhileHeld.Remove(packet.ItemInstanceId);

            // If it's the remote player holding it, track for visual following
            if (packet.PlayerSteamId != SteamClient.SteamId.Value)
            {
                // Track for disconnect cleanup (always needed)
                _remoteHeldItems[packet.ItemInstanceId] = item;

                // BUG FIX: Hangable items - disconnect from hook when picked up remotely
                // Without this, 'attached' remains true and item floats when dropped
                var hangable = item.GetComponent<HangableItem>();
                if (hangable != null && hangable.IsHanging())
                {
                    hangable.DisconnectJoint();
                    VerboseLogger.ItemApply($"Disconnected hangable item {packet.ItemInstanceId} from hook");
                }

                // If this is a shop item, call game's built-in purchase method
                // Handles: sold=true, reparent to world, save registration, OnBuy()
                if (!item.sold)
                {
                    item.Sell();
                    VerboseLogger.ItemApply($"Called Sell() on shop item {item.name}");
                }

                // INVENTORY SLOT HANDLING:
                // When item is in remote player's inventory (slot >= 0), hide it entirely.
                // When item is in hand (slot == -1), show it and position at hand.
                if (packet.InventorySlot >= 0)
                {
                    // Item is in remote player's inventory - hide it
                    item.gameObject.SetActive(false);
                    VerboseLogger.ItemApply($"Item {packet.ItemInstanceId} hidden (in remote inventory slot {packet.InventorySlot})");
                }
                else
                {
                    // Item is in hand - make sure it's visible and set up physics
                    item.gameObject.SetActive(true);

                    // REMOTE HELD ITEM PHYSICS FIX:
                    // When a player holds an item locally, the game sets held=GoPointer which triggers:
                    // - isKinematic=true on rigidbodies
                    // - isTrigger=true on colliders (so held item doesn't knock other items)
                    //
                    // On the VIEWER's machine (other player), held=null because we don't sync GoPointer.
                    // The game's ItemRigidbody.Update() checks held state and resets physics settings
                    // every frame, overwriting any values we set.
                    //
                    // SOLUTION: Disable the ItemRigidbody MonoBehaviour entirely while item is remotely held.
                    // This stops its Update/FixedUpdate from running, so our settings persist.
                    // Re-enabled in OnRemoteItemDropped when the item is released.

                    // Set held to non-null so ShipItem thinks item is held
                    item.held = Object.FindObjectOfType<GoPointer>();

                    var rb = item.GetComponent<Rigidbody>();
                    if (rb != null) rb.isKinematic = true;

                    if (item.itemRigidbodyC != null)
                    {
                        item.itemRigidbodyC.enabled = false;

                        var irbRb = item.itemRigidbodyC.GetComponent<Rigidbody>();
                        if (irbRb != null) irbRb.isKinematic = true;

                        // Disable ALL colliders (some items like barrel/mug have multiple)
                        foreach (var col in item.itemRigidbodyC.GetComponents<Collider>())
                            col.enabled = false;
                    }

                    // Also disable the ShipItem's OWN trigger collider (ShipItem.Awake
                    // forces it isTrigger=true; it lives on the ShipItem object, NOT itemRigidbodyC). While the
                    // host drives a guest-held item along the remote avatar, that live trigger would enter a
                    // real land Shopkeeper's trigger and pop a SELL menu for the guest's item on the host.
                    SetShipItemOwnTriggers(item, false);

                    VerboseLogger.ItemApply($"Item {packet.ItemInstanceId} now following remote player (physics disabled)");
                }
            }
        }

        /// <summary>
        /// Called when receiving ItemDropped packet.
        /// </summary>
        /// <summary>
        /// Fully un-latch an item from its boat, the way vanilla does. The mod's land-drop/resync paths
        /// used to null currentActualBoat/currentWalkCol by hand, which leaves TWO vanilla pieces stale:
        /// (1) the PRIVATE ShipItem.currentBoatCollider - ShipItem.ExtraFixedUpdate's EnterBoat gate is
        /// `currentlyStayedEmbarkCol != currentBoatCollider`, so a stale collider PERMANENTLY blocks the
        /// item from ever re-latching onto that same boat. The item then sits on the deck as a world-frame
        /// DYNAMIC rigidbody, and Unity depenetration between it and the bobbing hull shoves the boat -
        /// confirmed to drive a moored brig underwater in ~10s (2026-07-02 playtest sink).
        /// (2) ItemRigidbody.ExitBoat's cleanup (BoatMass.RemoveItem + onBoat=false), so the boat kept
        /// phantom cargo mass and FixedUpdate kept using the boat frame mapping with null walkCol refs.
        /// Route through the private vanilla ShipItem.ExitBoat when latched (it clears all three fields,
        /// calls ItemRigidbody.ExitBoat, and detaches hangables); always also clear currentBoatCollider
        /// afterwards to cover an item that carries a stale collider WITHOUT being latched (the state the
        /// old manual clears left behind).
        /// </summary>
        internal static void ClearBoatLatch(ShipItem item)
        {
            if (item == null) return;
            if (item.currentActualBoat != null)
            {
                try
                {
                    HarmonyLib.Traverse.Create(item).Method("ExitBoat").GetValue();
                }
                catch (System.Exception e)
                {
                    Plugin.Log.LogWarning($"[ITEM] vanilla ExitBoat failed on {item.name}: {e.Message}; clearing fields manually");
                    item.currentActualBoat = null;
                    item.currentWalkCol = null;
                    if (item.itemRigidbodyC != null)
                    {
                        try { HarmonyLib.Traverse.Create(item.itemRigidbodyC).Field("onBoat").SetValue(false); }
                        catch { }
                    }
                }
            }
            try { HarmonyLib.Traverse.Create(item).Field("currentBoatCollider").SetValue(null); }
            catch (System.Exception e) { Plugin.Log.LogWarning($"[ITEM] could not clear currentBoatCollider on {item.name}: {e.Message}"); }
        }

        public void OnRemoteItemDropped(ItemDroppedPacket packet, SteamId sender = default)
        {
            VerboseLogger.ItemRecv($"ItemDropped, id={packet.ItemInstanceId}, boat={packet.ParentBoatName}, pos={packet.Position}, isLocal={packet.IsLocalPosition}, from={sender}");

            // HOLDER VALIDATION: on the host (authoritative), honor a drop ONLY from the
            // RECORDED holder of that item. A stale/duplicate drop from a non-holder (e.g. a guest that
            // already lost the item to someone else) is ignored so it can't desync everyone. We only
            // validate when we actually have a holder on record; an unknown item falls through (the old
            // behavior). At N=1 the sole guest IS the holder, so its drops always pass.
            if (Plugin.IsHost && _heldItems.TryGetValue(packet.ItemInstanceId, out var recordedHolder)
                && sender != default(SteamId) && recordedHolder != sender)
            {
                VerboseLogger.ItemApply($"ItemDropped IGNORED: id={packet.ItemInstanceId} from {sender} but holder is {recordedHolder}");
                return;
            }

            // STAR host-relay: after the host validates a guest's drop, forward it to the OTHER guests so the
            // whole crew sees the item released. At N=1 SendToAllExcept(sender) targets no one (no-op).
            if (Plugin.IsHost)
            {
                // HELD-ITEM PHANTOM FIX companion: a peer that joined while a GUEST carried this item has
                // never been sent its id (held items are excluded from the join snapshot / mission resync),
                // so the relayed ItemDropped would no-op there and the item would stay invisible. Spawn-sync
                // it BEFORE the relay, mirroring the OnLocalPickup/SendDropPacket per-peer backfill; peers
                // that already track the id dedup the ItemSpawned to a no-op. The spawn goes out at the
                // item's current (still-held) pose; the relayed drop right behind it sets the real pose.
                if (AnyPeerMissingSyncedItem(packet.ItemInstanceId))
                {
                    var backfillItem = FindItemByInstanceId(packet.ItemInstanceId);
                    if (backfillItem == null && _remoteHeldItems.TryGetValue(packet.ItemInstanceId, out var heldBackfill))
                        backfillItem = heldBackfill;
                    var backfillPrefab = backfillItem != null ? backfillItem.GetComponent<SaveablePrefab>() : null;
                    if (backfillPrefab != null && backfillPrefab.instanceId != 0)
                    {
                        SendItemSpawnedForExisting(backfillItem, backfillPrefab);
                        MarkSyncedForAllPeers(packet.ItemInstanceId);
                        VerboseLogger.ItemSend($"Synced item {packet.ItemInstanceId} to guest(s) before relaying drop (a peer was missing it)");
                    }
                }

                Plugin.NetworkManager.SendToAllExcept(sender, PacketType.ItemDropped,
                    w => PacketSerializer.WriteItemDropped(w, packet));
            }

            // An authoritative remote drop supersedes any local settle tracking for this item. Without
            // this, a remote pickup AND drop applied in the same frame (packet backlog after a hitch)
            // slips past the sweep's once-per-frame held checks and leaves a stale tracker that later
            // emits a competing terminal. Placed after the host holder validation so a rejected stale
            // drop cannot disarm a legitimate tracker.
            _pendingDropTerminals.Remove(packet.ItemInstanceId);

            // Try to find item - check _remoteHeldItems first (item may be hidden with SetActive(false))
            var item = FindItemByInstanceId(packet.ItemInstanceId);
            if (item == null && _remoteHeldItems.TryGetValue(packet.ItemInstanceId, out var heldItem))
            {
                item = heldItem;
                VerboseLogger.ItemApply($"Found hidden item {packet.ItemInstanceId} in _remoteHeldItems for drop");
            }
            if (item == null)
            {
                Plugin.Log.LogWarning($"OnRemoteItemDropped: item {packet.ItemInstanceId} not found");
                return;
            }

            // Skip repositioning for fuel items already inserted into stove
            // FuelInsertRequest arrives before ItemDropped, so insertion already happened
            var stoveFuel = item.GetComponent<StoveFuel>();
            if (stoveFuel != null && stoveFuel.inserted)
            {
                VerboseLogger.ItemApply($"Skipping ItemDropped for inserted fuel {packet.ItemInstanceId}");
                // Still remove from held tracking
                _heldItems.Remove(packet.ItemInstanceId);
                _remoteHeldItems.Remove(packet.ItemInstanceId);
                ClearSyncedHeldItemById(packet.ItemInstanceId);
                return;
            }

            // Never yank an item out of the LOCAL player's hands. A drop (notably the settle terminal,
            // which arrives up to several seconds after the release) can race a pickup: this player
            // grabs the item, then the dropper's reliable drop lands. The host's holder validation
            // above already rejects a drop once the new pickup is recorded, but the grab can still be
            // in flight, so guard here too and skip repositioning entirely (held-state bookkeeping for
            // this item stays intact; the pickup broadcast restores everyone else). The holder map
            // covers a recorded local hold; the GoPointer check covers the just-grabbed window before
            // the host's ItemPickedUp broadcast returns.
            if (_heldItems.TryGetValue(packet.ItemInstanceId, out var localHolder) && localHolder == SteamClient.SteamId)
            {
                VerboseLogger.ItemApply($"Skipping ItemDropped for item {packet.ItemInstanceId} held by local player");
                _skippedDropsWhileHeld[packet.ItemInstanceId] = new SkippedDrop { Packet = packet, Time = Time.time };
                return;
            }
            var localPointer = Object.FindObjectOfType<GoPointer>();
            if (localPointer != null && ReferenceEquals(localPointer.GetHeldItem(), item))
            {
                VerboseLogger.ItemApply($"Skipping ItemDropped for item {packet.ItemInstanceId} in local hand");
                _skippedDropsWhileHeld[packet.ItemInstanceId] = new SkippedDrop { Packet = packet, Time = Time.time };
                return;
            }

            // This drop is being applied, so any stashed skipped drop for the item is superseded.
            _skippedDropsWhileHeld.Remove(packet.ItemInstanceId);

            // Mark as recently synced to prevent echo back of ItemDestroyed
            _recentlyRemoteSyncedItems[packet.ItemInstanceId] = Time.time;

            IsApplyingRemoteState = true;
            try
            {
                // Remove from held tracking
                _heldItems.Remove(packet.ItemInstanceId);
                _remoteHeldItems.Remove(packet.ItemInstanceId);
                RemoveFromGuestInventory(packet.ItemInstanceId);
                // Stop the per-carrier held-item visual from chasing this (now dropped) item.
                ClearSyncedHeldItemById(packet.ItemInstanceId);

                // Re-enable item if it was hidden (in inventory)
                item.gameObject.SetActive(true);

                // Clear the fake held reference we set during pickup
                item.held = null;

                // TRADER-CROSSWIRE fix: restore the ShipItem's own trigger collider disabled while guest-held,
                // so the item is interactable/raycastable again after it's dropped.
                SetShipItemOwnTriggers(item, true);

                // BUG FIX: Ensure hangable items are disconnected from hooks
                // Safety net in case pickup sync missed the DisconnectJoint call
                var hangable = item.GetComponent<HangableItem>();
                if (hangable != null && hangable.IsHanging())
                {
                    hangable.DisconnectJoint();
                    VerboseLogger.ItemApply($"Safety disconnect of hangable item {packet.ItemInstanceId} on drop");
                }

                if (packet.IsLocalPosition && !string.IsNullOrEmpty(packet.ParentBoatName))
                {
                    // BOAT DROP - Full dual-transform setup required
                    // Items on boat use two coordinate spaces:
                    // - ShipItem.transform in visual space (parented to boatModel)
                    // - ItemRigidbody.transform in physics space (parented to walkCol)
                    // Both use the SAME localPosition/localRotation values

                    // Find boat by name from packet (not by proximity - receiver might be on land)
                    var boats = BoatUtility.FindAllBoats();
                    if (!boats.TryGetValue(packet.ParentBoatName, out var boat))
                    {
                        Plugin.Log.LogWarning($"OnRemoteItemDropped: boat {packet.ParentBoatName} not found");
                        return;
                    }

                    var boatRefs = boat.GetComponent<BoatRefs>();
                    if (boatRefs == null)
                    {
                        Plugin.Log.LogWarning($"OnRemoteItemDropped: BoatRefs not found on {packet.ParentBoatName}");
                        return;
                    }

                    var boatModel = boatRefs.boatModel;
                    // BoatRefs.walkCol is null on some boats (junk small singleroof).
                    // Use BoatEmbarkCollider.walkCollider as primary source (how game does it).
                    var embarkCol = boatRefs.GetComponentInChildren<BoatEmbarkCollider>();
                    var walkCol = embarkCol?.walkCollider ?? boatRefs.walkCol;

                    if (boatModel == null || walkCol == null)
                    {
                        Plugin.Log.LogWarning($"OnRemoteItemDropped: boatModel or walkCol is null on {packet.ParentBoatName}");
                        return;
                    }

                    // 1. Set boat reference fields on ShipItem FIRST (before enabling ItemRigidbody)
                    item.currentActualBoat = boatModel;
                    item.currentWalkCol = walkCol;

                    // 2. Parent ShipItem to visual boat (boatModel)
                    item.transform.SetParent(boatModel, worldPositionStays: false);

                    // 3. Set local position/rotation on ShipItem
                    item.transform.localPosition = packet.Position;
                    item.transform.localRotation = packet.Rotation;

                    VerboseLogger.ItemApply($"Item {packet.ItemInstanceId} boat drop: parent={boatModel.name}, localPos={packet.Position}");

                    // 4. Setup ItemRigidbody in physics space
                    if (item.itemRigidbodyC != null)
                    {
                        var itemRbTransform = item.itemRigidbodyC.transform;

                        // Parent ItemRigidbody to physics walk collider
                        itemRbTransform.SetParent(walkCol, worldPositionStays: false);

                        // Set same local position/rotation (different parent handles coordinate conversion)
                        itemRbTransform.localPosition = packet.Position;
                        itemRbTransform.localRotation = packet.Rotation;

                        // Reset physics state
                        var rb = item.itemRigidbodyC.GetBody();
                        if (rb != null)
                        {
                            rb.velocity = Vector3.zero;
                            rb.angularVelocity = Vector3.zero;
                            // Let physics run (not kinematic) so items fall naturally
                        }

                        // Re-enable ALL colliders (was disabled during pickup)
                        foreach (var col in item.itemRigidbodyC.GetComponents<Collider>())
                            col.enabled = true;

                        // 5. Re-enable ItemRigidbody LAST (after all fields and parenting set)
                        item.itemRigidbodyC.enabled = true;

                        // Mark the ItemRigidbody on-boat so ItemRigidbody.FixedUpdate
                        // maps the visual<->physics across the boat's two ~205m-offset frames via the vanilla
                        // MoveItemToWalkColRigidbody path. Without onBoat, a SOLD item (mission cargo) falls to
                        // FixedUpdate's "item.transform.position = base.transform.position" branch, which yanks the
                        // visible mesh to the rigidbody's PHYSICS-frame world position (~205m off the rendered hull)
                        // until vanilla's auto-EnterBoat heals it a couple frames later - a flicker/blink on the
                        // guest. onBoat is private; set via Traverse. (currentActualBoat/currentWalkCol set above and
                        // the rb is parented to walkCol, so the mapping is correct immediately.)
                        try { HarmonyLib.Traverse.Create(item.itemRigidbodyC).Field("onBoat").SetValue(true); }
                        catch (System.Exception e) { Plugin.Log.LogWarning($"[ITEM] could not set onBoat on dropped boat item {packet.ItemInstanceId}: {e.Message}"); }

                        VerboseLogger.ItemApply($"Item {packet.ItemInstanceId} ItemRigidbody: parent={walkCol.name}, localPos={packet.Position}");
                    }
                }
                else
                {
                    // LAND DROP - Both transforms in same coordinate space (simpler)
                    // Add receiver's FOM offset (sender subtracted theirs)
                    var offset = FloatingOriginManager.instance?.outCurrentOffset ?? Vector3.zero;
                    var worldPos = packet.Position + offset;

                    VerboseLogger.ItemApply($"Land drop: packetPos={packet.Position}, offset={offset}, worldPos={worldPos}");

                    // Clear boat references THE VANILLA WAY (ExitBoat: also clears the private
                    // currentBoatCollider, removes BoatMass cargo weight, resets onBoat) - the old manual
                    // null-out left currentBoatCollider stale, permanently blocking re-latch onto the same
                    // boat (the 2026-07-02 moored-brig sink). Positioning below overrides ExitBoat's own.
                    ClearBoatLatch(item);

                    // Parent to shifting world
                    var shiftingWorld = GameObject.Find("_shifting world")?.transform;

                    item.transform.SetParent(shiftingWorld, worldPositionStays: false);
                    item.transform.position = worldPos;
                    item.transform.rotation = packet.Rotation;

                    if (item.itemRigidbodyC != null)
                    {
                        var itemRbTransform = item.itemRigidbodyC.transform;

                        // Same parent as ShipItem for land drops
                        itemRbTransform.SetParent(shiftingWorld, worldPositionStays: false);
                        itemRbTransform.position = worldPos;
                        itemRbTransform.rotation = packet.Rotation;

                        // Reset physics state
                        var rb = item.itemRigidbodyC.GetBody();
                        if (rb != null)
                        {
                            rb.velocity = Vector3.zero;
                            rb.angularVelocity = Vector3.zero;
                        }

                        // Re-enable ALL colliders (was disabled during pickup)
                        foreach (var col in item.itemRigidbodyC.GetComponents<Collider>())
                            col.enabled = true;

                        // Re-enable ItemRigidbody
                        item.itemRigidbodyC.enabled = true;
                    }

                    VerboseLogger.ItemApply($"Item {packet.ItemInstanceId} land drop at {worldPos}");
                }

                // Reset layer to Default (0) for raycast interaction.
                // The game's DropItem() sets layer=0; remote drops must do the same or
                // dropped items stay on the IgnoreRaycast layer and can't be clicked
                item.gameObject.layer = 0;
            }
            finally
            {
                IsApplyingRemoteState = false;
            }
        }


        /// <summary>
        /// Called when receiving ItemPickupRequest packet (host only).
        /// Uses lazy ID correlation: find local item by prefab type + position,
        /// then assign the remote player's ID to create a shared identity.
        /// </summary>
        public void OnRemoteItemPickupRequest(ItemPickupRequestPacket packet, SteamId sender)
        {
            if (!Plugin.IsHost) return;

            VerboseLogger.ItemRecv($"ItemPickupRequest, id={packet.ItemInstanceId}, prefab={packet.PrefabIndex}, slot={packet.InventorySlot}, pos={packet.Position}, boat={packet.ParentBoatName}, from={sender}");

            // Check if item is already held by someone
            if (IsItemHeld(packet.ItemInstanceId))
            {
                var currentHolder = GetItemHolder(packet.ItemInstanceId);

                // Allow same player to update inventory slot (hand -> inventory or inventory -> hand)
                if (currentHolder == sender)
                {
                    VerboseLogger.ItemApply($"Same player updating slot for item {packet.ItemInstanceId} to slot {packet.InventorySlot}");

                    // Use _remoteHeldItems instead of FindItemByInstanceId
                    // (FindObjectsOfType can't find inactive objects after SetActive(false))
                    if (_remoteHeldItems.TryGetValue(packet.ItemInstanceId, out var existingItem) && existingItem != null)
                    {
                        if (packet.InventorySlot >= 0)
                        {
                            // Moving to inventory - hide
                            existingItem.gameObject.SetActive(false);
                            VerboseLogger.ItemApply($"Host hiding guest item {packet.ItemInstanceId} (moved to inventory slot {packet.InventorySlot})");
                        }
                        else
                        {
                            // Moving to hand - show
                            existingItem.gameObject.SetActive(true);
                            VerboseLogger.ItemApply($"Host showing guest item {packet.ItemInstanceId} (moved to hand)");
                        }
                    }
                    else
                    {
                        VerboseLogger.ItemApply($"WARNING: Item {packet.ItemInstanceId} not found in _remoteHeldItems for slot update");
                    }

                    // Broadcast the slot change to all
                    SendItemPickedUp(packet.ItemInstanceId, sender, packet.InventorySlot, packet.PrefabIndex, packet.Position, packet.ParentBoatName, packet.IsLocalPosition);
                    return;
                }

                VerboseLogger.ItemApply($"Denying pickup - item {packet.ItemInstanceId} already held by {currentHolder}");
                SendItemPickupDenied(packet.ItemInstanceId, 0, sender);
                return;
            }

            // Try to find item - first by ID (if already registered), then by position correlation.
            // id==0 GUARD: unsold vendor-table items all share instanceId==0, so FindItemByInstanceId(0) would
            // resolve to an ARBITRARY first id-0 item (wrong prefab). Skip the id-keyed lookup for 0 and rely on
            // prefab/position correlation below, which is stable.
            ShipItem item = packet.ItemInstanceId != 0 ? FindItemByInstanceId(packet.ItemInstanceId) : null;

            if (item == null)
            {
                // LAZY ID CORRELATION: Find nearest item of same type near the position
                // This handles items that exist locally but haven't been synced yet
                item = FindItemByPrefabNearPosition(
                    packet.PrefabIndex,
                    packet.Position,
                    packet.ParentBoatName,
                    packet.IsLocalPosition
                );

                if (item != null)
                {
                    // Found a matching local item - assign the remote player's ID to it
                    var prefab = item.GetComponent<SaveablePrefab>();
                    if (prefab != null)
                    {
                        int oldId = prefab.instanceId;
                        // A HOST-held item is recorded in _heldItems by its OWN id but NOT in
                        // _remoteHeldItems, so the TryGetHolderOfObject check below (which scans _remoteHeldItems)
                        // can't see it - and reassigning oldId -> the requester's id here would ORPHAN the host's
                        // hold and let the guest STEAL a crate the host is carrying (notably one the host stowed
                        // in inventory, where held==null so FindItemByPrefabNearPosition didn't skip it). Deny
                        // BEFORE reassigning. Same-holder (oldHolder==sender) is allowed (a legit re-grab); a free
                        // item's id is removed from _heldItems on drop/destroy, so no stale-entry false deny.
                        if (oldId != 0 && _heldItems.TryGetValue(oldId, out var oldHolder) && oldHolder != sender)
                        {
                            VerboseLogger.ItemApply($"Denying pickup - correlated crate {item.name} already held by {oldHolder} (id {oldId}; host/other-held) - not stealing it");
                            SendItemPickupDenied(packet.ItemInstanceId, 0, sender);
                            return;
                        }
                        prefab.instanceId = packet.ItemInstanceId;

                        VerboseLogger.Log("ITEM", "CORRELATE", $"Assigned ID {packet.ItemInstanceId} to local item {item.name} (was {oldId})");
                    }
                }
            }

            if (item == null)
            {
                VerboseLogger.ItemApply($"Denying pickup - no matching item found for prefab={packet.PrefabIndex} near pos={packet.Position}");
                SendItemPickupDenied(packet.ItemInstanceId, 1, sender);
                return;
            }

            // Object-identity arbitration: the IsItemHeld gate at the top is keyed on the REQUESTER's
            // random instanceId, but a contested crate is already held under the WINNER's DIFFERENT random id.
            // If correlation resolved to a physical crate some OTHER player already holds, deny this second grab
            // so the loser rolls back (OnRemoteItemPickupDenied) instead of binding a 2nd id to one object
            // (which leaves a phantom/desynced crate for the crew). FindItemByPrefabNearPosition already skips
            // in-hand-held items, so this is the backstop for the active-but-tracked edge.
            if (TryGetHolderOfObject(item, out var existingHolder) && existingHolder != sender)
            {
                VerboseLogger.ItemApply($"Denying pickup - correlated crate {item.name} already held by {existingHolder} under a different id (contested grab)");
                SendItemPickupDenied(packet.ItemInstanceId, 0, sender);
                return;
            }

            // PHANTOM-GRAB GUARD (2026-07-02 hijack): deny ONLY the hijack signature - the requester
            // claims the item sits on LAND (empty boat name) while the host's real item is latched on a
            // boat. That is what a stale ground copy looks like (e.g. the pre-fix held-item join phantom):
            // approving would silently yank the real item off a deck nobody is looking at. Deny and push
            // the authoritative state so the requester's copy heals; their next grab then succeeds.
            // Deliberately ONE-DIRECTIONAL and graced (review finding): vanilla EnterBoat/ExitBoat run
            // per-machine on independent ~2-frame dwell counters, so the two ends legitimately disagree
            // for short windows around any drop near a deck. The reverse direction (requester names a
            // boat, host says land = host latch lag) and cross-boat naming differences are left approved,
            // and the drop-settle window (_pendingDropTerminals, host's own drops) plus the 2s
            // remote-sync window (IsRecentlySynced, fed by OnRemoteItemDropped for guest drops) exempt
            // freshly moved items. Both ends name the boat the same way (item.currentActualBoat.name,
            // see OnLocalPickup), so the comparison is like-for-like.
            string hostFrameBoat = item.currentActualBoat != null ? item.currentActualBoat.name : "";
            string requesterFrameBoat = packet.ParentBoatName ?? "";
            if (hostFrameBoat != "" && requesterFrameBoat == ""
                && !_pendingDropTerminals.ContainsKey(packet.ItemInstanceId)
                && !IsRecentlySynced(packet.ItemInstanceId))
            {
                VerboseLogger.ItemApply($"Denying pickup - phantom-grab signature for item {item.name} id={packet.ItemInstanceId}: requester says land, host has it on boat '{hostFrameBoat}'; resyncing requester");
                SendItemPickupDenied(packet.ItemInstanceId, 0, sender);
                var mismatchPrefab = item.GetComponent<SaveablePrefab>();
                if (mismatchPrefab != null && mismatchPrefab.instanceId != 0)
                    SendItemResync(mismatchPrefab.instanceId, sender);
                return;
            }

            // Always register - whether found by ID or correlation
            _itemRegistry[packet.ItemInstanceId] = packet.PrefabIndex;

            // Item found - approve pickup
            var onBoat = item.currentActualBoat != null ? item.currentActualBoat.name : "null";
            VerboseLogger.ItemApply($"Approving pickup - item {item.name}, id={packet.ItemInstanceId}, onBoat={onBoat}");

            // Approve: record and broadcast
            _heldItems[packet.ItemInstanceId] = sender;

            // Track for guest disconnect cleanup, partitioned by holder so this guest
            // leaving drops only ITS items.
            AddToGuestInventory(sender, packet.ItemInstanceId);

            // Host must track guest-held items for visual following
            // (Host doesn't receive its own broadcast, so must track here)
            _remoteHeldItems[packet.ItemInstanceId] = item;

            // BUG FIX: Hangable items - disconnect from hook when picked up by guest
            // Without this, 'attached' remains true and item floats when dropped
            var hangable = item.GetComponent<HangableItem>();
            if (hangable != null && hangable.IsHanging())
            {
                hangable.DisconnectJoint();
                VerboseLogger.ItemApply($"Disconnected hangable item {packet.ItemInstanceId} from hook (guest pickup)");
            }

            // If this is a shop item, call game's built-in purchase method
            // Handles: sold=true, reparent to world, save registration, OnBuy()
            if (!item.sold)
            {
                item.Sell();
                VerboseLogger.ItemApply($"Called Sell() on shop item {item.name}");
            }

            // INVENTORY SLOT HANDLING (host processing guest request):
            // When item is in guest's inventory (slot >= 0), hide it entirely.
            // When item is in hand (slot == -1), show it and position at hand.
            if (packet.InventorySlot >= 0)
            {
                // Item is in guest's inventory - hide it
                item.gameObject.SetActive(false);
                VerboseLogger.ItemApply($"Host hiding guest item {packet.ItemInstanceId} (in inventory slot {packet.InventorySlot})");
            }
            else
            {
                // Item is in hand - make sure it's visible and set up physics
                item.gameObject.SetActive(true);

                // REMOTE HELD ITEM PHYSICS FIX (see detailed comment in OnRemoteItemPickedUp):
                // Disable ItemRigidbody component to prevent game from resetting physics settings.
                // Without this, held items knock other objects around on the viewer's screen.

                item.held = Object.FindObjectOfType<GoPointer>();

                var rb = item.GetComponent<Rigidbody>();
                if (rb != null) rb.isKinematic = true;

                if (item.itemRigidbodyC != null)
                {
                    item.itemRigidbodyC.enabled = false;

                    var irbRb = item.itemRigidbodyC.GetComponent<Rigidbody>();
                    if (irbRb != null) irbRb.isKinematic = true;

                    // Disable ALL colliders (some items like barrel/mug have multiple)
                    foreach (var col in item.itemRigidbodyC.GetComponents<Collider>())
                        col.enabled = false;
                }

                // Disable the ShipItem's OWN trigger collider too (see OnRemoteItemPickedUp).
                SetShipItemOwnTriggers(item, false);

                VerboseLogger.ItemApply($"Host tracking guest-held item {packet.ItemInstanceId}");
            }

            // Broadcast to all (including requester) - pass position for lazy correlation
            SendItemPickedUp(packet.ItemInstanceId, sender, packet.InventorySlot, packet.PrefabIndex, packet.Position, packet.ParentBoatName, packet.IsLocalPosition);
        }

        /// <summary>
        /// Called when receiving ItemPickupDenied packet (guest only).
        /// </summary>
        public void OnRemoteItemPickupDenied(ItemPickupDeniedPacket packet)
        {
            if (Plugin.IsHost) return;

            VerboseLogger.ItemRecv($"ItemPickupDenied, id={packet.ItemInstanceId}, reason={packet.Reason}");

            // Force drop the item locally (wrapped to prevent feedback packet)
            var item = FindItemByInstanceId(packet.ItemInstanceId);
            if (item != null && item.held != null)
            {
                IsApplyingRemoteState = true;
                try
                {
                    item.held.DropItem();
                    VerboseLogger.ItemApply($"Forced drop of denied item {packet.ItemInstanceId}");
                }
                finally
                {
                    IsApplyingRemoteState = false;
                }
            }

            // Contested-grab loser convergence: if the winner's drop arrived while this player still
            // appeared to hold the item, its apply was skipped. Replay it now that the deny released
            // the optimistic local hold, so this machine snaps to the authoritative pose instead of
            // keeping its own local one. Guests never relay, so the normal apply path is safe here.
            if (_skippedDropsWhileHeld.TryGetValue(packet.ItemInstanceId, out var skipped))
            {
                _skippedDropsWhileHeld.Remove(packet.ItemInstanceId);
                if (Time.time - skipped.Time <= SkippedDropReplayWindow)
                {
                    VerboseLogger.ItemApply($"Replaying skipped ItemDropped for denied item {packet.ItemInstanceId}");
                    OnRemoteItemDropped(skipped.Packet);
                }
            }
        }

        #endregion

        #region Item Spawn/Destroy

        /// <summary>
        /// Called when a new item is created locally (bought from shop, etc.).
        /// </summary>
        public void OnLocalItemSpawned(ShipItem item)
        {
            if (IsApplyingRemoteState) return;
            if (item == null) return;

            var prefab = item.GetComponent<SaveablePrefab>();
            if (prefab == null) return;

            var packet = BuildItemSpawnedPacket(item, prefab);

            VerboseLogger.ItemSend($"ItemSpawned, id={prefab.instanceId}, prefab={prefab.prefabIndex}, pos={packet.Position}, isLocal={packet.IsLocalPosition}, mission={packet.MissionIndex}");

            Plugin.NetworkManager.SendToAllReliable(PacketType.ItemSpawned, w =>
            {
                PacketSerializer.WriteItemSpawned(w, packet);
            });

            // Mark as synced (host) and add to registry (both players for debug overlay).
            // This ItemSpawned was a SendToAll broadcast, so every currently-connected peer
            // now has it - record it in each peer's per-peer synced set.
            if (Plugin.IsHost)
            {
                MarkSyncedForAllPeers(prefab.instanceId);
            }
            _itemRegistry[prefab.instanceId] = prefab.prefabIndex;
        }

        /// <summary>
        /// Builds an ItemSpawnedPacket for an item: boat-relative or floating-origin-corrected position,
        /// health, amount, and mission index. Shared by the local-spawn broadcast and the targeted
        /// mission-cargo resync.
        /// </summary>
        private static ItemSpawnedPacket BuildItemSpawnedPacket(ShipItem item, SaveablePrefab prefab)
        {
            // Determine parent boat
            string parentBoatName = "";
            bool isLocalPosition = false;
            Vector3 position = item.transform.position;

            if (item.currentActualBoat != null)
            {
                var boatSaveable = item.currentActualBoat.parent?.GetComponent<SaveableObject>();
                if (boatSaveable != null)
                {
                    parentBoatName = boatSaveable.gameObject.name;
                    position = item.currentActualBoat.InverseTransformPoint(item.transform.position);
                    isLocalPosition = true;
                }
            }
            else
            {
                // World item (not on boat) - convert to "real world" coords for sync
                // SaveablePrefab.Load() does: transform.position = data.position + offset
                // So we need to send: localPosition - offset (matching save format)
                var offset = FloatingOriginManager.instance?.outCurrentOffset ?? Vector3.zero;
                position = position - offset;
            }

            // Get mission index if this is mission cargo
            var good = item.GetComponent<Good>();
            int missionIndex = good != null ? good.GetMissionIndex() : -1;

            return new ItemSpawnedPacket
            {
                ItemInstanceId = prefab.instanceId,
                PrefabIndex = prefab.prefabIndex,
                Position = position,
                Rotation = item.transform.rotation,
                ParentBoatName = parentBoatName,
                IsLocalPosition = isLocalPosition,
                Health = item.health,
                Amount = item.amount,
                MissionIndex = missionIndex
            };
        }

        /// <summary>
        /// Host-only: resends an ItemSpawned for every live mission cargo item to ONE peer. Fired when
        /// that peer reports GuestJoinComplete at the end of its join coroutine, so it repairs a join that
        /// COMPLETED with per-item spawn losses on apply. It does NOT repair a join snapshot lost outright:
        /// the guest never runs the join coroutine then and never sends the trigger. Because the request
        /// arrives strictly AFTER the guest finished applying its snapshot, OnRemoteItemSpawned's duplicate
        /// guard makes each resent spawn idempotent: already-present crates no-op, missing crates spawn
        /// with their mission index so the receiver registers them to the mission.
        /// Known limits: mission goods hidden inactive on the host (stashed in a remote player's inventory)
        /// are invisible to FindObjectsOfType and are not resent; they heal when the holder drops them
        /// (ItemDropped repositions by id). An item currently held IN A HAND is deliberately skipped
        /// (see the held check below): resending it as a loose ItemSpawned created a ground phantom under
        /// the original's id (2026-07-02 playtest). Held items reach the joiner via the drop-time
        /// spawn-sync backfill instead.
        /// </summary>
        public void ResyncMissionCargoTo(SteamId target)
        {
            if (!Plugin.IsMultiplayer || !Plugin.IsHost) return;

            int sent = 0;
            foreach (var good in Object.FindObjectsOfType<Good>())
            {
                if (good.GetMissionIndex() < 0) continue;

                var item = good.GetComponent<ShipItem>();
                var prefab = good.GetComponent<SaveablePrefab>();
                if (item == null || prefab == null) continue;

                // Never send id 0: on the receiver FindItemByInstanceId(0) matches an ARBITRARY scene item
                // (unsold vendor items all share id 0), so the dedup either falsely no-ops against an
                // unrelated item or the spawn creates an unaddressable id-0 duplicate. Matches the guard in
                // ForceBroadcastItemDestroyed / OnLocalItemAmountChanged.
                if (prefab.instanceId == 0) continue;

                // Mirror the join-snapshot collector's filters (CollectWorldItems): personal pocket items
                // (slot 0..99) and unsold shop stock are deliberately never serialized to a joiner, so
                // resending one here would spawn a loose ghost copy carrying the host's instanceId.
                int invSlot = item.GetCurrentInventorySlot();
                if (invSlot >= 0 && invSlot < 100) continue;
                if (!item.sold) continue;

                // HELD-ITEM PHANTOM FIX (2026-07-02 playtest): never resend an item that is in someone's
                // hand (vanilla hold or the mod's fake held on remote-held items). BuildItemSpawnedPacket
                // would encode it as a LOOSE item at the carrier's hand position under the original's id -
                // a ground phantom that can later hijack the real item via id correlation. The joiner gets
                // it from the drop-time spawn-sync backfill when the carrier releases it.
                if (item.held != null) continue;

                var packet = BuildItemSpawnedPacket(item, prefab);

                VerboseLogger.ItemSend($"ItemSpawned (mission resync), id={prefab.instanceId}, prefab={prefab.prefabIndex}, mission={packet.MissionIndex}, target={target}");

                Plugin.NetworkManager.SendReliable(target, PacketType.ItemSpawned, w =>
                {
                    PacketSerializer.WriteItemSpawned(w, packet);
                });
                sent++;
            }

            Plugin.Log.LogInfo($"[ITEMS] Mission cargo resync to {target}: {sent} item(s) resent");
        }

        /// <summary>
        /// Send ItemSpawned packet for an existing item (scene item being picked up for first time).
        /// Similar to OnLocalItemSpawned but for items that already exist in the world.
        /// </summary>
        private void SendItemSpawnedForExisting(ShipItem item, SaveablePrefab prefab)
        {
            // Use current position (item is about to be picked up, so it's at valid world pos)
            Vector3 position = item.transform.position;
            string parentBoatName = "";
            bool isLocalPosition = false;

            if (item.currentActualBoat != null)
            {
                var boatSaveable = item.currentActualBoat.parent?.GetComponent<SaveableObject>();
                if (boatSaveable != null)
                {
                    parentBoatName = boatSaveable.gameObject.name;
                    position = item.currentActualBoat.InverseTransformPoint(item.transform.position);
                    isLocalPosition = true;
                }
            }
            else
            {
                // World item - convert to offset-independent coords
                var offset = FloatingOriginManager.instance?.outCurrentOffset ?? Vector3.zero;
                position = position - offset;
            }

            // Get mission index if this is mission cargo
            var good = item.GetComponent<Good>();
            int missionIndex = good != null ? good.GetMissionIndex() : -1;

            var packet = new ItemSpawnedPacket
            {
                ItemInstanceId = prefab.instanceId,
                PrefabIndex = prefab.prefabIndex,
                Position = position,
                Rotation = item.transform.rotation,
                ParentBoatName = parentBoatName,
                IsLocalPosition = isLocalPosition,
                Health = item.health,
                Amount = item.amount,
                MissionIndex = missionIndex
            };

            VerboseLogger.ItemSend($"ItemSpawned (scene), id={prefab.instanceId}, prefab={prefab.prefabIndex}, pos={position}, mission={missionIndex}");

            Plugin.NetworkManager.SendToAllReliable(PacketType.ItemSpawned, w =>
            {
                PacketSerializer.WriteItemSpawned(w, packet);
            });
        }

        /// <summary>
        /// Called when an item is destroyed locally.
        /// </summary>
        public void OnLocalItemDestroyed(int instanceId)
        {
            if (IsApplyingRemoteState) return;

            // Don't broadcast ItemDestroyed for items recently synced from remote.
            // This prevents echo back when guest's cleanup destroys host-dropped items.
            if (_recentlyRemoteSyncedItems.TryGetValue(instanceId, out float syncTime))
            {
                if (Time.time - syncTime < REMOTE_SYNC_PROTECTION_TIME)
                {
                    VerboseLogger.ItemLocal($"Skipping ItemDestroyed broadcast for recently synced item {instanceId}");
                    _recentlyRemoteSyncedItems.Remove(instanceId);
                    // Still clean up tracking
                    _heldItems.Remove(instanceId);
                    _remoteHeldItems.Remove(instanceId);
                    ClearSyncedHeldItemById(instanceId);
                    RemoveFromGuestInventory(instanceId);
                    return;
                }
                _recentlyRemoteSyncedItems.Remove(instanceId);
            }

            var packet = new ItemDestroyedPacket { ItemInstanceId = instanceId };

            VerboseLogger.ItemSend($"ItemDestroyed, id={instanceId}");

            Plugin.NetworkManager.SendToAllReliable(PacketType.ItemDestroyed, w =>
            {
                PacketSerializer.WriteItemDestroyed(w, packet);
            });

            // Clean up tracking
            _heldItems.Remove(instanceId);
            _remoteHeldItems.Remove(instanceId);
            ClearSyncedHeldItemById(instanceId);
            RemoveFromGuestInventory(instanceId);
        }

        /// <summary>
        /// Force-broadcast an ItemDestroyed, BYPASSING the
        /// _recentlyRemoteSyncedItems suppression. A host-side MISSION DELIVERY (the host walking a crate into
        /// the port building) is a genuine, intentional global destroy. The normal OnLocalItemDestroyed path
        /// suppresses the broadcast for any id a guest dropped within the last 2s -> guests never delete the
        /// delivered crate -> it duplicates ("two crates on clients, zero on host"). This forces the destroy out.
        /// Host-only by construction (only called from the host DeliverGood postfix), so it cannot re-open
        /// the guest-cleanup-echo case; idempotent on receivers (a second ItemDestroyed for an already-gone
        /// id simply no-ops).
        /// </summary>
        public void ForceBroadcastItemDestroyed(int instanceId)
        {
            // Enforce host-only in the method itself (not just via the single caller, so a
            // future caller can't re-open the guest-cleanup-echo class) AND never broadcast id==0 (an
            // unregistered prefab's default id; on receivers FindItemByInstanceId(0) matches an ARBITRARY scene
            // item and would DestroyItem() it on every client). Matches OnLocalItemAmountChanged/HealthChanged.
            if (!Plugin.IsMultiplayer || !Plugin.IsHost || instanceId == 0) return;

            _recentlyRemoteSyncedItems.Remove(instanceId);

            var packet = new ItemDestroyedPacket { ItemInstanceId = instanceId };
            VerboseLogger.ItemSend($"ItemDestroyed (forced - mission delivery), id={instanceId}");
            Plugin.NetworkManager.SendToAllReliable(PacketType.ItemDestroyed, w =>
            {
                PacketSerializer.WriteItemDestroyed(w, packet);
            });

            _heldItems.Remove(instanceId);
            _remoteHeldItems.Remove(instanceId);
            ClearSyncedHeldItemById(instanceId);
            RemoveFromGuestInventory(instanceId);
        }

        /// <summary>
        /// Called when a bulk item's amount changes (drinking water, etc.).
        /// </summary>
        public void OnLocalItemAmountChanged(ShipItem item)
        {
            if (IsApplyingRemoteState) return;
            if (item == null) return;

            var prefab = item.GetComponent<SaveablePrefab>();
            if (prefab == null) return;
            if (prefab.instanceId == 0) return; // don't broadcast id=0

            var packet = new ItemAmountChangedPacket
            {
                ItemInstanceId = prefab.instanceId,
                NewAmount = item.amount
            };

            VerboseLogger.ItemSend($"ItemAmountChanged, id={prefab.instanceId}, amount={item.amount}");

            Plugin.NetworkManager.SendToAllReliable(PacketType.ItemAmountChanged, w =>
            {
                PacketSerializer.WriteItemAmountChanged(w, packet);
            });
        }

        /// <summary>
        /// Called when receiving ItemSpawned packet.
        /// </summary>
        public void OnRemoteItemSpawned(ItemSpawnedPacket packet, SteamId sender = default)
        {
            VerboseLogger.ItemRecv($"ItemSpawned, id={packet.ItemInstanceId}, prefab={packet.PrefabIndex}, mission={packet.MissionIndex}");

            // Star-relay: forward a guest-originated spawn to the OTHER guests (reliable). Every item
            // broadcast needs this host relay (Dropped/Destroyed/Amount/Health/Light/Pipe/Hung/Unhung all
            // have one) - without it, at 3+ players a 3rd guest never creates the item and every later
            // Pickup/Drop for it logs "item not found". Relay BEFORE the duplicate-guard so it still forwards
            // even when the host already has the item.
            if (Plugin.IsHost)
                Plugin.NetworkManager.SendToAllExcept(sender, PacketType.ItemSpawned, w =>
                    PacketSerializer.WriteItemSpawned(w, packet));

            // Check if item already exists. Falls back to the inactive scan: an inventory-stashed copy is
            // hidden via SetActive(false) and invisible to FindItemByInstanceId, and spawning over it
            // creates a second item the moment the holder drops the original.
            var existing = FindItemByInstanceId(packet.ItemInstanceId);
            if (existing == null)
                existing = FindInactiveItemByInstanceId(packet.ItemInstanceId);
            if (existing != null)
            {
                // Info, not warning: this is the expected no-op on every healthy join, where the post-join
                // mission-cargo resync resends crates the snapshot already applied and each one dedups here.
                Plugin.Log.LogInfo($"OnRemoteItemSpawned: item {packet.ItemInstanceId} already exists, skipping");
                return;
            }

            // Mark as recently synced to protect from cleanup
            _recentlyRemoteSyncedItems[packet.ItemInstanceId] = Time.time;

            IsApplyingRemoteState = true;
            try
            {
                // Find parent boat if specified
                SaveableObject boat = null;
                if (!string.IsNullOrEmpty(packet.ParentBoatName))
                {
                    var boats = BoatUtility.FindAllBoats();
                    boats.TryGetValue(packet.ParentBoatName, out boat);
                }

                // Create network save data (using new complete format)
                var itemData = new NetworkSaveData
                {
                    InstanceId = packet.ItemInstanceId,
                    PrefabIndex = packet.PrefabIndex,
                    Position = packet.IsLocalPosition ? packet.Position : Vector3.zero,
                    Rotation = packet.Rotation,
                    IsWorldPosition = !packet.IsLocalPosition,
                    ParentBoatName = packet.ParentBoatName ?? "",
                    IsSold = true,  // Runtime spawned items are always sold/owned
                    Health = packet.Health,
                    Amount = packet.Amount,
                    InventorySlot = -1,
                    CrateId = 0,
                    MissionIndex = packet.MissionIndex,  // Use packet's mission index
                    ParentObject = 0,
                    DaysInStorage = 0,
                    ExtraValue0 = 0, ExtraValue1 = 0, ExtraValue2 = 0, ExtraValue3 = 0, ExtraValue4 = 0,
                    HasChartData = false
                };

                if (boat != null)
                {
                    BoatStateApplicator.SpawnItem(boat, itemData);
                }
                else
                {
                    // Spawn in world - need different handling
                    SpawnWorldItem(itemData, packet.Position);
                }

                VerboseLogger.ItemApply($"Spawned item {packet.ItemInstanceId}");

                // Add to registry when receiving ItemSpawned
                // Host needs this for validation, guest needs it for debug overlay
                _itemRegistry[packet.ItemInstanceId] = packet.PrefabIndex;
                VerboseLogger.Log("ITEM", "REGISTRY", $"Registered remote-spawned item {packet.ItemInstanceId} (prefab={packet.PrefabIndex})");
            }
            finally
            {
                IsApplyingRemoteState = false;
            }
        }

        /// <summary>
        /// Spawns an item in world space (not on a boat).
        /// Position is in "real world" coords (localPos - offset).
        /// SaveablePrefab.Load() will add guest's offset to get correct local position.
        /// </summary>
        private void SpawnWorldItem(NetworkSaveData item, Vector3 realWorldPosition)
        {
            if (item.PrefabIndex <= 0 || item.PrefabIndex >= PrefabsDirectory.instance.directory.Length)
            {
                Plugin.Log.LogWarning($"Invalid prefab index: {item.PrefabIndex}");
                return;
            }

            var prefab = PrefabsDirectory.instance.directory[item.PrefabIndex];
            if (prefab == null) return;

            var offset = FloatingOriginManager.instance?.outCurrentOffset ?? Vector3.zero;
            VerboseLogger.ItemApply($"SpawnWorldItem: realWorldPos={realWorldPosition}, offset={offset}");

            // Instantiate at origin temporarily - Load() will set correct position
            var instance = Object.Instantiate(prefab, Vector3.zero, item.Rotation);
            var saveable = instance.GetComponent<SaveablePrefab>();

            if (saveable != null)
            {
                // Pass realWorldPosition to Load() - it will add guest's offset internally
                var saveData = new SavePrefabData(
                    realWorldPosition, item.Rotation, item.PrefabIndex,
                    item.IsSold, false, item.Health, item.Amount, item.InventorySlot,
                    0, item.MissionIndex, item.ParentObject, item.DaysInStorage, item.InstanceId
                );

                // Set extra values
                saveData.extraValue0 = item.ExtraValue0;
                saveData.extraValue1 = item.ExtraValue1;
                saveData.extraValue2 = item.ExtraValue2;
                saveData.extraValue3 = item.ExtraValue3;
                saveData.extraValue4 = item.ExtraValue4;

                saveable.Load(saveData);

                VerboseLogger.ItemApply($"SpawnWorldItem: final pos={instance.transform.position}");
            }
        }

        /// <summary>
        /// Called when receiving ItemDestroyed packet.
        /// </summary>
        public void OnRemoteItemDestroyed(ItemDestroyedPacket packet, SteamId sender = default)
        {
            VerboseLogger.ItemRecv($"ItemDestroyed, id={packet.ItemInstanceId}");

            // Host ignores destruction during guest join
            if (IgnoreRemoteItemDestruction)
            {
                Plugin.Log.LogInfo($"[ITEM:RECV] Ignoring ItemDestroyed {packet.ItemInstanceId} - guest is joining");
                return;
            }

            // Star-relay: forward to the other guests AFTER the join-window guard (so join-window
            // destroys are NOT relayed) but BEFORE the item lookup (reliable).
            if (Plugin.IsHost)
                Plugin.NetworkManager.SendToAllExcept(sender, PacketType.ItemDestroyed, w =>
                    PacketSerializer.WriteItemDestroyed(w, packet));

            var item = FindItemByInstanceId(packet.ItemInstanceId);
            if (item == null)
            {
                Plugin.Log.LogWarning($"OnRemoteItemDestroyed: item {packet.ItemInstanceId} not found");
                return;
            }

            // Skip destruction for burning fuel - let local simulation handle cleanup
            // so UnregisterBurntFuel() is called properly to decrement stove's currentFuel counter
            var stoveFuel = item.GetComponent<StoveFuel>();
            if (stoveFuel != null && stoveFuel.inserted)
            {
                VerboseLogger.ItemApply($"Skipping destruction for burning fuel {packet.ItemInstanceId}, letting local sim finish");
                return;
            }

            IsApplyingRemoteState = true;
            try
            {
                // Clean up tracking
                _heldItems.Remove(packet.ItemInstanceId);
                _remoteHeldItems.Remove(packet.ItemInstanceId);
                ClearSyncedHeldItemById(packet.ItemInstanceId);
                // Mirror OnLocalItemDestroyed - drop the destroyed item from the holder's inventory
                // partition too, else a stale id lingers in _guestInventoryItems until that guest disconnects.
                RemoveFromGuestInventory(packet.ItemInstanceId);

                item.DestroyItem();
                VerboseLogger.ItemApply($"Destroyed item {packet.ItemInstanceId}");
            }
            finally
            {
                IsApplyingRemoteState = false;
            }
        }

        /// <summary>
        /// Called when receiving ItemAmountChanged packet.
        /// </summary>
        public void OnRemoteItemAmountChanged(ItemAmountChangedPacket packet, SteamId sender = default)
        {
            VerboseLogger.ItemRecv($"ItemAmountChanged, id={packet.ItemInstanceId}, amount={packet.NewAmount}");

            // Star-relay: forward to the other guests (reliable).
            if (Plugin.IsHost)
                Plugin.NetworkManager.SendToAllExcept(sender, PacketType.ItemAmountChanged, w =>
                    PacketSerializer.WriteItemAmountChanged(w, packet));

            var item = FindItemByInstanceId(packet.ItemInstanceId);
            if (item == null)
            {
                Plugin.Log.LogWarning($"OnRemoteItemAmountChanged: item {packet.ItemInstanceId} not found");
                return;
            }

            IsApplyingRemoteState = true;
            try
            {
                item.amount = packet.NewAmount;
                try { item.UpdateLookText(); } catch { } // refresh the displayed liquid/label
                VerboseLogger.ItemApply($"Updated amount for item {packet.ItemInstanceId} to {packet.NewAmount}");
            }
            finally
            {
                IsApplyingRemoteState = false;
            }
        }

        #endregion

        #region Item Health Sync

        /// <summary>
        /// Called when item health changes (consumption).
        /// Throttled for continuous consumption like soup.
        /// </summary>
        public void OnLocalItemHealthChanged(ShipItem item, bool forceSync = false)
        {
            if (IsApplyingRemoteState) return;
            if (item == null) return;

            var prefab = item.GetComponent<SaveablePrefab>();
            if (prefab == null) return;

            int instanceId = prefab.instanceId;
            if (instanceId == 0) return; // never broadcast id=0 - the remote would match an arbitrary id-0 item

            // Throttle for continuous consumption
            if (!forceSync)
            {
                if (_lastHealthSyncTime.TryGetValue(instanceId, out float lastTime))
                {
                    if (Time.time - lastTime < HEALTH_SYNC_INTERVAL)
                    {
                        return; // Too soon, skip
                    }
                }
            }
            _lastHealthSyncTime[instanceId] = Time.time;

            var packet = new ItemHealthChangedPacket
            {
                ItemInstanceId = instanceId,
                NewHealth = item.health
            };

            VerboseLogger.ItemSend($"ItemHealthChanged, id={instanceId}, health={item.health}");

            Plugin.NetworkManager.SendToAllReliable(PacketType.ItemHealthChanged, w =>
            {
                PacketSerializer.WriteItemHealthChanged(w, packet);
            });

            // Also sync the liquid type/amount (e.g. a barrel hitting empty sets amount=0) so the remote
            // barrel/bottle doesn't keep stale liquid state. Reuses the existing amount channel - no wire change.
            OnLocalItemAmountChanged(item);
        }

        /// <summary>
        /// Called when receiving ItemHealthChanged packet.
        /// </summary>
        public void OnRemoteItemHealthChanged(ItemHealthChangedPacket packet, SteamId sender = default)
        {
            VerboseLogger.ItemRecv($"ItemHealthChanged, id={packet.ItemInstanceId}, health={packet.NewHealth}");

            // Star-relay: forward to the other guests (reliable).
            if (Plugin.IsHost)
                Plugin.NetworkManager.SendToAllExcept(sender, PacketType.ItemHealthChanged, w =>
                    PacketSerializer.WriteItemHealthChanged(w, packet));

            var item = FindItemByInstanceId(packet.ItemInstanceId);
            if (item == null)
            {
                Plugin.Log.LogWarning($"OnRemoteItemHealthChanged: item {packet.ItemInstanceId} not found");
                return;
            }

            IsApplyingRemoteState = true;
            try
            {
                item.health = packet.NewHealth;
                try { item.UpdateLookText(); } catch { } // refresh the displayed level (e.g. barrel %)
                VerboseLogger.ItemApply($"Updated health for item {packet.ItemInstanceId} to {packet.NewHealth}");
            }
            finally
            {
                IsApplyingRemoteState = false;
            }
        }

        /// <summary>
        /// Called when receiving LightState packet (lantern on/off).
        /// Finds the ShipItemLight and calls SetLight() to update visual state.
        /// </summary>
        public void OnRemoteLightStateChanged(LightStatePacket packet, SteamId sender = default)
        {
            VerboseLogger.ItemRecv($"LightState, id={packet.ItemInstanceId}, on={packet.IsOn}");

            // Star-relay: forward to the other guests (reliable).
            if (Plugin.IsHost)
                Plugin.NetworkManager.SendToAllExcept(sender, PacketType.LightState, w =>
                    PacketSerializer.WriteLightState(w, packet));

            var item = FindItemByInstanceId(packet.ItemInstanceId);
            if (item == null)
            {
                Plugin.Log.LogWarning($"OnRemoteLightStateChanged: item {packet.ItemInstanceId} not found");
                return;
            }

            var light = item.GetComponent<ShipItemLight>();
            if (light == null)
            {
                Plugin.Log.LogWarning($"OnRemoteLightStateChanged: item {packet.ItemInstanceId} has no ShipItemLight component");
                return;
            }

            IsApplyingRemoteState = true;
            try
            {
                // SetLight is private - use Traverse to call it
                HarmonyLib.Traverse.Create(light).Method("SetLight", packet.IsOn).GetValue();
                VerboseLogger.ItemApply($"Set light state for item {packet.ItemInstanceId} to {packet.IsOn}");
            }
            finally
            {
                IsApplyingRemoteState = false;
            }
        }

        /// <summary>
        /// Called when receiving PipeFilled packet (pipe loaded with tobacco).
        /// Finds the ShipItemPipe and applies the tobacco state.
        /// </summary>
        public void OnRemotePipeFilled(PipeFilledPacket packet, SteamId sender = default)
        {
            VerboseLogger.ItemRecv($"PipeFilled, pipeId={packet.PipeInstanceId}, tobaccoType={packet.TobaccoType}");

            // Star-relay: forward to the other guests (reliable).
            if (Plugin.IsHost)
                Plugin.NetworkManager.SendToAllExcept(sender, PacketType.PipeFilled, w =>
                    PacketSerializer.WritePipeFilled(w, packet));

            var item = FindItemByInstanceId(packet.PipeInstanceId);
            if (item == null)
            {
                Plugin.Log.LogWarning($"OnRemotePipeFilled: pipe {packet.PipeInstanceId} not found");
                return;
            }

            var pipe = item.GetComponent<ShipItemPipe>();
            if (pipe == null)
            {
                Plugin.Log.LogWarning($"OnRemotePipeFilled: item {packet.PipeInstanceId} has no ShipItemPipe component");
                return;
            }

            IsApplyingRemoteState = true;
            try
            {
                // Set the pipe state - LoadTobacco sets amount=tobaccoType and health=100
                pipe.amount = packet.TobaccoType;
                pipe.health = 100f;

                // Get the tobaccoGraphics Renderer (private field)
                var tobaccoGraphics = HarmonyLib.Traverse.Create(pipe).Field("tobaccoGraphics").GetValue<Renderer>();
                if (tobaccoGraphics != null)
                {
                    // Find any tobacco item with matching type to get the material
                    var matchingMaterial = FindTobaccoMaterial(packet.TobaccoType);
                    if (matchingMaterial != null)
                    {
                        tobaccoGraphics.sharedMaterial = matchingMaterial;
                    }

                    // Activate the tobacco visual
                    tobaccoGraphics.gameObject.SetActive(true);
                }

                VerboseLogger.ItemApply($"Applied pipe fill: id={packet.PipeInstanceId}, tobaccoType={packet.TobaccoType}");
            }
            finally
            {
                IsApplyingRemoteState = false;
            }
        }

        /// <summary>
        /// Find a tobacco material by looking for any ShipItemTobacco with matching type.
        /// </summary>
        private Material FindTobaccoMaterial(int tobaccoType)
        {
            // Search for any tobacco item of the same type to get its material
            foreach (var tobacco in Object.FindObjectsOfType<ShipItemTobacco>())
            {
                if (tobacco.tobaccoType == tobaccoType)
                {
                    var renderer = tobacco.GetComponent<Renderer>();
                    if (renderer != null)
                    {
                        return renderer.sharedMaterial;
                    }
                }
            }

            Plugin.Log.LogWarning($"FindTobaccoMaterial: no tobacco found with type {tobaccoType}");
            return null;
        }

        #endregion

        #region Hangable Items

        /// <summary>
        /// Called when local player hangs item on hook.
        /// </summary>
        public void OnLocalItemHung(ShipItem item, ShipItem hook)
        {
            if (IsApplyingRemoteState) return;
            if (item == null || hook == null) return;

            var itemPrefab = item.GetComponent<SaveablePrefab>();
            var hookPrefab = hook.GetComponent<SaveablePrefab>();
            if (itemPrefab == null || hookPrefab == null) return;

            // Clear held state - item is no longer held when hung on hook
            _heldItems.Remove(itemPrefab.instanceId);
            _remoteHeldItems.Remove(itemPrefab.instanceId);
            ClearSyncedHeldItemById(itemPrefab.instanceId);

            var packet = new ItemHungPacket
            {
                ItemInstanceId = itemPrefab.instanceId,
                HookInstanceId = hookPrefab.instanceId
            };

            VerboseLogger.ItemSend($"ItemHung, item={itemPrefab.instanceId}, hook={hookPrefab.instanceId}");

            Plugin.NetworkManager.SendToAllReliable(PacketType.ItemHung, w =>
            {
                PacketSerializer.WriteItemHung(w, packet);
            });
        }

        /// <summary>
        /// Called when local player unhungs item from hook.
        /// </summary>
        public void OnLocalItemUnhung(ShipItem item)
        {
            if (IsApplyingRemoteState) return;
            if (item == null) return;

            var prefab = item.GetComponent<SaveablePrefab>();
            if (prefab == null) return;

            var packet = new ItemUnhungPacket
            {
                ItemInstanceId = prefab.instanceId
            };

            VerboseLogger.ItemSend($"ItemUnhung, item={prefab.instanceId}");

            Plugin.NetworkManager.SendToAllReliable(PacketType.ItemUnhung, w =>
            {
                PacketSerializer.WriteItemUnhung(w, packet);
            });
        }

        /// <summary>
        /// Called when receiving ItemHung packet.
        /// </summary>
        public void OnRemoteItemHung(ItemHungPacket packet, SteamId sender = default)
        {
            VerboseLogger.ItemRecv($"ItemHung, item={packet.ItemInstanceId}, hook={packet.HookInstanceId}");

            // Star-relay: forward to the other guests (reliable).
            if (Plugin.IsHost)
                Plugin.NetworkManager.SendToAllExcept(sender, PacketType.ItemHung, w =>
                    PacketSerializer.WriteItemHung(w, packet));

            // Clear held state - item is no longer held when hung on hook
            _heldItems.Remove(packet.ItemInstanceId);
            _remoteHeldItems.Remove(packet.ItemInstanceId);
            ClearSyncedHeldItemById(packet.ItemInstanceId);
            // Also drop it from the holder's inventory partition. The drop-to-hang path clears this via
            // a preceding ItemDropped, but click-to-hang (ShipItemLampHook.OnItemClick -> ConnectJoint while
            // still held) sends ItemHung with NO prior drop, so without this the hung item lingers in
            // _guestInventoryItems and is force-dropped off its hook when that guest later disconnects.
            RemoveFromGuestInventory(packet.ItemInstanceId);

            var item = FindItemByInstanceId(packet.ItemInstanceId);
            var hook = FindItemByInstanceId(packet.HookInstanceId);

            if (item == null || hook == null)
            {
                Plugin.Log.LogWarning($"OnRemoteItemHung: item or hook not found");
                return;
            }

            var hangable = item.GetComponent<HangableItem>();
            if (hangable == null)
            {
                Plugin.Log.LogWarning($"OnRemoteItemHung: item {packet.ItemInstanceId} is not hangable");
                return;
            }

            IsApplyingRemoteState = true;
            try
            {
                hangable.ConnectJoint(hook.GetComponent<Collider>());
                VerboseLogger.ItemApply($"Hung item {packet.ItemInstanceId} on hook {packet.HookInstanceId}");
            }
            finally
            {
                IsApplyingRemoteState = false;
            }
        }

        /// <summary>
        /// Called when receiving ItemUnhung packet.
        /// </summary>
        public void OnRemoteItemUnhung(ItemUnhungPacket packet, SteamId sender = default)
        {
            VerboseLogger.ItemRecv($"ItemUnhung, item={packet.ItemInstanceId}");

            // Star-relay: forward to the other guests (reliable).
            if (Plugin.IsHost)
                Plugin.NetworkManager.SendToAllExcept(sender, PacketType.ItemUnhung, w =>
                    PacketSerializer.WriteItemUnhung(w, packet));

            var item = FindItemByInstanceId(packet.ItemInstanceId);
            if (item == null)
            {
                Plugin.Log.LogWarning($"OnRemoteItemUnhung: item {packet.ItemInstanceId} not found");
                return;
            }

            var hangable = item.GetComponent<HangableItem>();
            if (hangable == null)
            {
                Plugin.Log.LogWarning($"OnRemoteItemUnhung: item {packet.ItemInstanceId} is not hangable");
                return;
            }

            IsApplyingRemoteState = true;
            try
            {
                hangable.DisconnectJoint();
                VerboseLogger.ItemApply($"Unhung item {packet.ItemInstanceId}");
            }
            finally
            {
                IsApplyingRemoteState = false;
            }
        }

        #endregion

        #region Crate Interactions

        /// <summary>
        /// Called when local player inserts item into crate.
        /// </summary>
        public void OnLocalItemInsertedInCrate(ShipItem item, CrateInventory crate)
        {
            if (IsApplyingRemoteState) return;
            if (item == null || crate == null) return;

            var itemPrefab = item.GetComponent<SaveablePrefab>();
            var cratePrefab = crate.GetComponent<SaveablePrefab>();
            if (itemPrefab == null || cratePrefab == null) return;

            var packet = new ItemCratePacket
            {
                ItemInstanceId = itemPrefab.instanceId,
                CrateInstanceId = cratePrefab.instanceId
            };

            VerboseLogger.ItemSend($"ItemCrateInsert, item={itemPrefab.instanceId}, crate={cratePrefab.instanceId}");

            Plugin.NetworkManager.SendToAllReliable(PacketType.ItemCrateInsert, w =>
            {
                PacketSerializer.WriteItemCrate(w, packet);
            });
        }

        /// <summary>
        /// Called when local player removes item from crate.
        /// </summary>
        public void OnLocalItemRemovedFromCrate(ShipItem item, CrateInventory crate)
        {
            if (IsApplyingRemoteState) return;
            if (item == null || crate == null) return;

            var itemPrefab = item.GetComponent<SaveablePrefab>();
            var cratePrefab = crate.GetComponent<SaveablePrefab>();
            if (itemPrefab == null || cratePrefab == null) return;

            var packet = new ItemCratePacket
            {
                ItemInstanceId = itemPrefab.instanceId,
                CrateInstanceId = cratePrefab.instanceId
            };

            VerboseLogger.ItemSend($"ItemCrateRemove, item={itemPrefab.instanceId}, crate={cratePrefab.instanceId}");

            Plugin.NetworkManager.SendToAllReliable(PacketType.ItemCrateRemove, w =>
            {
                PacketSerializer.WriteItemCrate(w, packet);
            });
        }

        /// <summary>
        /// Called when receiving ItemCrateInsert packet.
        /// </summary>
        public void OnRemoteItemCrateInsert(ItemCratePacket packet, SteamId sender = default)
        {
            VerboseLogger.ItemRecv($"ItemCrateInsert, item={packet.ItemInstanceId}, crate={packet.CrateInstanceId}");

            // Star-relay: forward to the other guests (reliable).
            if (Plugin.IsHost)
                Plugin.NetworkManager.SendToAllExcept(sender, PacketType.ItemCrateInsert, w =>
                    PacketSerializer.WriteItemCrate(w, packet));

            var item = FindItemByInstanceId(packet.ItemInstanceId);
            var crate = FindCrateByInstanceId(packet.CrateInstanceId);

            if (item == null || crate == null)
            {
                Plugin.Log.LogWarning($"OnRemoteItemCrateInsert: item or crate not found");
                return;
            }

            IsApplyingRemoteState = true;
            try
            {
                crate.InsertItem(item);
                // Diag: capture scale after the crate insert (a crate item should be shrunk; full ~1.0 flags a scale desync).
                VerboseLogger.ItemApply($"Inserted item {packet.ItemInstanceId} into crate {packet.CrateInstanceId} (scale={item.transform.localScale.x:F3})");
            }
            finally
            {
                IsApplyingRemoteState = false;
            }
        }

        /// <summary>
        /// Called when receiving ItemCrateRemove packet.
        /// </summary>
        public void OnRemoteItemCrateRemove(ItemCratePacket packet, SteamId sender = default)
        {
            VerboseLogger.ItemRecv($"ItemCrateRemove, item={packet.ItemInstanceId}, crate={packet.CrateInstanceId}");

            // Star-relay: forward to the other guests (reliable).
            if (Plugin.IsHost)
                Plugin.NetworkManager.SendToAllExcept(sender, PacketType.ItemCrateRemove, w =>
                    PacketSerializer.WriteItemCrate(w, packet));

            var item = FindItemByInstanceId(packet.ItemInstanceId);
            var crate = FindCrateByInstanceId(packet.CrateInstanceId);

            if (item == null || crate == null)
            {
                Plugin.Log.LogWarning($"OnRemoteItemCrateRemove: item or crate not found");
                return;
            }

            IsApplyingRemoteState = true;
            try
            {
                crate.WithdrawItem(item);
                // Diag: a withdrawn item should return to full scale (~1.0); a stuck shrunk/huge scale here flags a desync.
                VerboseLogger.ItemApply($"Removed item {packet.ItemInstanceId} from crate {packet.CrateInstanceId} (scale={item.transform.localScale.x:F3})");
            }
            finally
            {
                IsApplyingRemoteState = false;
            }
        }

        /// <summary>
        /// Finds a crate by its instanceId.
        /// </summary>
        private static CrateInventory FindCrateByInstanceId(int instanceId)
        {
            var prefabs = Object.FindObjectsOfType<SaveablePrefab>();
            foreach (var prefab in prefabs)
            {
                if (prefab.instanceId == instanceId)
                {
                    return prefab.GetComponent<CrateInventory>();
                }
            }
            return null;
        }

        #endregion

        #region Crate Unsealing

        /// <summary>
        /// Called when local player wants to unseal a crate.
        /// Guest sends request, host processes directly.
        /// </summary>
        public void OnLocalCrateUnsealRequest(ShipItemCrate crate)
        {
            if (IsApplyingRemoteState) return;
            if (crate == null) return;

            var prefab = crate.GetComponent<SaveablePrefab>();
            if (prefab == null) return;

            if (Plugin.IsHost)
            {
                // Host: unseal directly and broadcast
                StartCoroutine(UnsealAndBroadcast(crate, prefab.instanceId));
            }
            else
            {
                // Guest: request permission from host
                // Track that we requested this crate (to show UI when response arrives)
                _pendingUnsealCrateIds.Add(prefab.instanceId);

                var packet = new CrateUnsealRequestPacket
                {
                    CrateInstanceId = prefab.instanceId,
                    CratePrefabIndex = prefab.prefabIndex
                };

                VerboseLogger.ItemSend($"CrateUnsealRequest, crate={prefab.instanceId}, prefab={prefab.prefabIndex}");

                Plugin.NetworkManager.SendToAllReliable(PacketType.CrateUnsealRequest, w =>
                {
                    PacketSerializer.WriteCrateUnsealRequest(w, packet);
                });
            }
        }

        private IEnumerator UnsealAndBroadcast(ShipItemCrate crate, int crateInstanceId, bool isRemoteRequest = false)
        {
            // Store expected count before unseal
            int expectedCount = (int)crate.amount;

            // Track which crate is being unsealed: _remoteUnsealingCrates suppresses the host UI; _unsealingCrateIds
            // makes the per-item InsertItem postfix skip its redundant ItemCrateInsert broadcast (the
            // bulk CrateUnsealed below is the source of truth). Both are added BEFORE the try and removed in the
            // FINALLY so a throw in vanilla UnsealCrate (it calls UpdateMass/PlayUISound with no null guards) or a
            // mid-poll coroutine abort can't leak a flag - a leak would silently suppress that crate's manual inserts
            // for the rest of the session. (try/finally around `yield` is legal; a try/CATCH around yield is not.)
            if (isRemoteRequest)
            {
                _remoteUnsealingCrates.Add(crateInstanceId);
            }
            _unsealingCrateIds.Add(crateInstanceId);

            var crateInventory = crate.GetComponent<CrateInventory>();
            float elapsed = 0f;
            const float timeout = 2f;
            const float pollInterval = 0.05f;

            try
            {
                // Do the unseal - this starts an internal coroutine that opens UI after 2 frames
                crate.UnsealCrate();

                // Poll until items are in crate inventory (timeout 2s)
                while (elapsed < timeout)
                {
                    if (crateInventory != null && crateInventory.containedItems.Count >= expectedCount)
                    {
                        break;
                    }
                    yield return new WaitForSeconds(pollInterval);
                    elapsed += pollInterval;
                }
            }
            finally
            {
                // ALWAYS clear the suppression flags, even on a throw/abort (the UI coroutine has run by now).
                _unsealingCrateIds.Remove(crateInstanceId);
                if (isRemoteRequest)
                {
                    _remoteUnsealingCrates.Remove(crateInstanceId);
                }
            }

            if (elapsed >= timeout)
            {
                Plugin.Log.LogWarning($"CrateUnseal timeout: expected {expectedCount} items, got {crateInventory?.containedItems.Count ?? 0}");
            }

            // Collect full item data
            var spawnedItems = new List<CrateSpawnedItemData>();
            if (crateInventory != null)
            {
                foreach (var item in crateInventory.containedItems)
                {
                    if (item == null) continue;

                    var itemPrefab = item.GetComponent<SaveablePrefab>();
                    if (itemPrefab == null) continue;

                    var foodState = item.GetComponent<FoodState>();

                    spawnedItems.Add(new CrateSpawnedItemData
                    {
                        InstanceId = itemPrefab.instanceId,
                        PrefabIndex = itemPrefab.prefabIndex,
                        Health = item.health,
                        Amount = item.amount,
                        IsSmoked = foodState != null && foodState.smoked >= 1f,
                        IsDried = foodState != null && foodState.dried >= 1f
                    });
                }
            }

            // Broadcast result
            var packet = new CrateUnsealedPacket
            {
                CrateInstanceId = crateInstanceId,
                SpawnedItems = spawnedItems.ToArray()
            };

            VerboseLogger.ItemSend($"CrateUnsealed, crate={crateInstanceId}, spawned={spawnedItems.Count} items");

            Plugin.NetworkManager.SendToAllReliable(PacketType.CrateUnsealed, w =>
            {
                PacketSerializer.WriteCrateUnsealed(w, packet);
            });
        }

        /// <summary>
        /// Called when receiving CrateUnsealRequest (host only).
        /// </summary>
        public void OnRemoteCrateUnsealRequest(CrateUnsealRequestPacket packet, SteamId sender)
        {
            if (!Plugin.IsHost) return;

            VerboseLogger.ItemRecv($"CrateUnsealRequest, crate={packet.CrateInstanceId}, prefab={packet.CratePrefabIndex}");

            // Validate crate against registry
            if (!ValidateItem(packet.CrateInstanceId, packet.CratePrefabIndex, out int expectedPrefab))
            {
                if (expectedPrefab == -1)
                {
                    Plugin.Log.LogWarning($"[ITEM:VALIDATE] Unknown crate {packet.CrateInstanceId}, sending destroy");
                    SendItemDestroyed(packet.CrateInstanceId, sender);
                }
                else
                {
                    Plugin.Log.LogWarning($"[ITEM:VALIDATE] Mismatch for crate {packet.CrateInstanceId}, sending resync");
                    SendItemResync(packet.CrateInstanceId, sender);
                }
                return;
            }

            var crate = FindCrateItemByInstanceId(packet.CrateInstanceId);
            if (crate == null || crate.amount <= 0)
            {
                Plugin.Log.LogWarning($"CrateUnsealRequest: invalid crate {packet.CrateInstanceId}");
                return;
            }

            // Process unseal on host (isRemoteRequest=true to suppress UI)
            StartCoroutine(UnsealAndBroadcast(crate, packet.CrateInstanceId, isRemoteRequest: true));
        }

        /// <summary>
        /// Called when receiving CrateUnsealed packet.
        /// Guest spawns items with host's IDs instead of calling UnsealCrate().
        /// </summary>
        public void OnRemoteCrateUnsealed(CrateUnsealedPacket packet)
        {
            VerboseLogger.ItemRecv($"CrateUnsealed, crate={packet.CrateInstanceId}, spawned={packet.SpawnedItems?.Length ?? 0}");

            var crate = FindCrateItemByInstanceId(packet.CrateInstanceId);
            if (crate == null)
            {
                Plugin.Log.LogWarning($"CrateUnsealed: crate {packet.CrateInstanceId} not found");
                return;
            }

            var crateInventory = crate.GetComponent<CrateInventory>();
            if (crateInventory == null)
            {
                Plugin.Log.LogWarning($"CrateUnsealed: crate {packet.CrateInstanceId} has no inventory");
                return;
            }

            IsApplyingRemoteState = true;
            try
            {
                // Set crate to empty (don't call UnsealCrate - we spawn items ourselves)
                crate.amount = 0;
                crate.UpdateLookText();

                // Update rigidbody mass to reflect empty crate
                var itemRb = crate.GetComponent<ItemRigidbody>();
                if (itemRb != null)
                {
                    itemRb.UpdateMass();
                }

                // Spawn each item with host's ID
                if (packet.SpawnedItems != null)
                {
                    Plugin.Log.LogInfo($"[CRATE-UNSEAL] Spawning {packet.SpawnedItems.Length} items from packet");
                    int spawnedCount = 0;
                    foreach (var itemData in packet.SpawnedItems)
                    {
                        Plugin.Log.LogDebug($"[CRATE-UNSEAL] Item {spawnedCount}: id={itemData.InstanceId}, prefab={itemData.PrefabIndex}, amount={itemData.Amount}");
                        SpawnCrateItem(itemData, crate.transform, crateInventory);
                        spawnedCount++;
                    }
                    Plugin.Log.LogInfo($"[CRATE-UNSEAL] Spawned {spawnedCount} items, crateInventory now has {crateInventory.containedItems?.Count ?? 0} items");
                }

                // Play unseal sound
                UISoundPlayer.instance?.PlayUISound(UISounds.crateSealBreak, 1f, 1f);

                // Only open crate UI if WE requested THIS unseal. Remove just this id (NOT a blanket clear), so
                // a CrateUnsealed for a different crate arriving first doesn't drop our pending request.
                bool weRequestedThis = _pendingUnsealCrateIds.Remove(packet.CrateInstanceId);

                if (weRequestedThis)
                {
                    StartCoroutine(OpenCrateUIDelayed(crateInventory));
                    VerboseLogger.ItemApply($"Unsealed crate {packet.CrateInstanceId} with {packet.SpawnedItems?.Length ?? 0} items (showing UI)");
                }
                else
                {
                    VerboseLogger.ItemApply($"Unsealed crate {packet.CrateInstanceId} with {packet.SpawnedItems?.Length ?? 0} items (no UI - host initiated)");
                }
            }
            finally
            {
                IsApplyingRemoteState = false;
            }
        }

        private IEnumerator OpenCrateUIDelayed(CrateInventory crateInventory)
        {
            yield return null; // Wait one frame
            yield return null; // Wait another frame (matches UnsealCrate behavior)
            crateInventory.OpenCrate();
        }

        /// <summary>
        /// Spawns a single item from crate unseal data with host's instanceId.
        /// </summary>
        private void SpawnCrateItem(CrateSpawnedItemData itemData, Transform crateTransform, CrateInventory crateInventory)
        {
            try
            {
                if (itemData.PrefabIndex <= 0 || itemData.PrefabIndex >= PrefabsDirectory.instance.directory.Length)
                {
                    Plugin.Log.LogWarning($"SpawnCrateItem: invalid prefab index {itemData.PrefabIndex}");
                    return;
                }

                var prefab = PrefabsDirectory.instance.directory[itemData.PrefabIndex];
                if (prefab == null)
                {
                    Plugin.Log.LogWarning($"SpawnCrateItem: prefab at index {itemData.PrefabIndex} is null");
                    return;
                }

                if (crateTransform == null)
                {
                    Plugin.Log.LogWarning($"SpawnCrateItem: crateTransform is null for item {itemData.InstanceId}");
                    return;
                }

                // Spawn at crate position + offset (same as UnsealCrate does)
                Vector3 spawnPos = crateTransform.position + new Vector3(0f, 100.5f, 0f);
                var instance = Object.Instantiate(prefab, spawnPos, crateTransform.rotation);

            // Set up SaveablePrefab with host's instanceId
            var saveable = instance.GetComponent<SaveablePrefab>();
            if (saveable != null)
            {
                // Crate items must have sold=true (player-owned)
                // Without this, ShipItemFood.OnAltHeld() and MouthCol.Update() fail
                // because they check sold before allowing eating
                var saveData = new SavePrefabData(
                    spawnPos,                    // pos
                    crateTransform.rotation,     // rot
                    itemData.PrefabIndex,        // prefabIndex
                    true,                        // isSold - crate items are player-owned
                    false,                       // isNailed
                    itemData.Health,             // health
                    itemData.Amount,             // amount
                    -1,                          // slot (not in inventory)
                    0,                           // crate (will be set by InsertItem)
                    -1,                          // missionIndex: MISSIONLESS, matching the host (vanilla UnsealCrate
                                                 // -> RegisterToSave uses good.GetMissionIndex() = -1). Hard-coding 0
                                                 // wrongly registered crate contents to mission SLOT 0 on the guest.
                    0,                           // parentObject
                    0,                           // daysInStorage
                    itemData.InstanceId          // instanceId - use host's value!
                );
                saveable.Load(saveData);
            }

            // Apply food state if applicable
            var foodState = instance.GetComponent<FoodState>();
            if (foodState != null)
            {
                if (itemData.IsSmoked)
                {
                    foodState.smoked = 1f;
                }
                if (itemData.IsDried)
                {
                    foodState.dried = 1f;
                }

                var cookable = instance.GetComponent<CookableFood>();
                if (cookable != null)
                {
                    cookable.UpdateMaterial();
                }
            }

            // Insert into crate inventory
            var shipItem = instance.GetComponent<ShipItem>();
            if (shipItem != null)
            {
                crateInventory.InsertItem(shipItem);
                Plugin.Log.LogDebug($"[CRATE-UNSEAL] Inserted {instance.name} (id={itemData.InstanceId}) into crate, inventory now has {crateInventory.containedItems?.Count ?? 0} items, scale={shipItem.transform.localScale.x:F3}"); // diag: confirm crate items are shrunk on the guest
            }
            else
            {
                Plugin.Log.LogWarning($"[CRATE-UNSEAL] Spawned {instance.name} has no ShipItem component!");
            }

            VerboseLogger.ItemApply($"Spawned crate item id={itemData.InstanceId}, prefab={itemData.PrefabIndex}");
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError($"[CRATE-UNSEAL] Exception spawning item {itemData.InstanceId}, prefab={itemData.PrefabIndex}: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Finds a ShipItemCrate by instanceId.
        /// </summary>
        private static ShipItemCrate FindCrateItemByInstanceId(int instanceId)
        {
            var prefabs = Object.FindObjectsOfType<SaveablePrefab>();
            foreach (var prefab in prefabs)
            {
                if (prefab.instanceId == instanceId)
                {
                    return prefab.GetComponent<ShipItemCrate>();
                }
            }
            return null;
        }

        #endregion

        #region Disconnect Handling

        /// <summary>
        /// Called on the HOST when ONE peer (a guest) disconnects. Drops ONLY that peer's carried items
        /// at their last position; every other holder's items stay tracked and held.
        ///
        /// The authoritative holder map is _heldItems (instanceId -> holder SteamId),
        /// so we enumerate it and act only on entries whose holder == the disconnecting id. Never clear
        /// the GLOBAL _heldItems / _remoteHeldItems here - that would force-drop/untrack every OTHER
        /// connected guest's items too.
        /// </summary>
        public void OnPeerDisconnected(SteamId leaver, Vector3 lastKnownPosition)
        {
            if (!Plugin.IsHost) return;

            // Snapshot first: DropItemAtPosition mutates _heldItems, so we can't enumerate it directly.
            var leaverItems = new List<int>();
            foreach (var kvp in _heldItems)
            {
                if (kvp.Value == leaver) leaverItems.Add(kvp.Key);
            }

            // Belt-and-suspenders: also include anything in this holder's inventory partition that
            // somehow isn't in _heldItems (e.g. hidden inventory items). At N=1 this is the same set.
            if (_guestInventoryItems.TryGetValue(leaver, out var inv))
            {
                foreach (var id in inv)
                    if (!leaverItems.Contains(id)) leaverItems.Add(id);
            }

            Plugin.Log.LogInfo($"Peer {leaver} disconnected, dropping {leaverItems.Count} of their items at {lastKnownPosition}");

            // Floating-origin symmetry: lastKnownPosition is a RAW host-frame world position, but land-drop packets
            // are offset-relative (each receiver re-adds its OWN FloatingOrigin offset). Subtract the host's
            // offset before broadcasting, or after any voyage (origin shifted) the item lands hundreds of
            // metres away on the other guests while sitting correctly on the host.
            var dropOffset = FloatingOriginManager.instance?.outCurrentOffset ?? Vector3.zero;

            foreach (var instanceId in leaverItems)
            {
                var item = FindItemByInstanceId(instanceId);
                if (item == null)
                {
                    // FindItemByInstanceId (Object.FindObjectsOfType) can't see SetActive(false) objects, so a
                    // leaver's item stashed in an INVENTORY SLOT (hidden via SetActive(false) on remote pickup)
                    // resolves to null and would be untracked-but-never-dropped => leaked INACTIVE in the scene
                    // forever. Recover it among inactive scene objects so DropItemAtPosition can
                    // reactivate + drop it like any other carried item.
                    item = FindInactiveItemByInstanceId(instanceId);
                }
                if (item == null)
                {
                    // Genuinely gone (already destroyed) - still untrack just this id.
                    _heldItems.Remove(instanceId);
                    _remoteHeldItems.Remove(instanceId);
                    continue;
                }

                // Use proper drop logic - same as OnRemoteItemDropped. DropItemAtPosition removes the
                // id from _heldItems/_remoteHeldItems (this holder's entries only).
                DropItemAtPosition(item, instanceId, lastKnownPosition);

                // HELD-ITEM PHANTOM FIX companion: a peer that joined while the LEAVER carried this item
                // never received its id (held items are excluded from the join snapshot / mission resync),
                // so the disconnect-drop broadcast below would no-op there and the item would stay
                // invisible to that peer forever. Spawn-sync first, mirroring the SendDropPacket and
                // relay backfills; peers that already track the id dedup the ItemSpawned to a no-op.
                // Runs AFTER DropItemAtPosition so the spawn already carries the dropped (land) pose.
                if (AnyPeerMissingSyncedItem(instanceId))
                {
                    var leaverPrefab = item.GetComponent<SaveablePrefab>();
                    if (leaverPrefab != null && leaverPrefab.instanceId != 0)
                    {
                        SendItemSpawnedForExisting(item, leaverPrefab);
                        MarkSyncedForAllPeers(instanceId);
                        VerboseLogger.ItemSend($"Synced item {instanceId} to guest(s) before disconnect-drop (a peer was missing it)");
                    }
                }

                // Disconnect-drop broadcast: the host (authoritative author) broadcasts the
                // authoritative land-drop so the REMAINING guests un-orphan the item (they were only
                // ever told the leaver picked it up). Position is offset-relative (lastKnownPosition -
                // host offset) so each receiver's "+ its own offset" resolves to the same world point the
                // host used; land drop => empty boatName, isLocalPosition=false. Plain SendToAll (no sender
                // to exclude) is correct since the host is the author here.
                SendItemDropped(instanceId, lastKnownPosition - dropOffset, item.transform.rotation, "", false);

                VerboseLogger.ItemApply($"Dropped peer {leaver} item {instanceId} at {lastKnownPosition}");
            }

            // Clear ONLY this holder's inventory partition; other holders keep theirs.
            _guestInventoryItems.Remove(leaver);

            // Drop the leaver's per-peer synced-id set too, so it doesn't leak across the
            // session and a future joiner reusing the SteamId starts clean. Other peers' sets are untouched.
            _syncedItemIds.Remove(leaver);

            // Forget this carrier's synced held-item slot so its item visual stops following a now-gone
            // avatar. Other carriers' slots are untouched. At N=1 this clears the single slot.
            ClearSyncedHeldItemForCarrier(leaver);
        }

        /// <summary>
        /// Back-compat shim for callers that drop "the guest's" items without a holder id.
        /// Retained so any external/legacy call still compiles; routes to the per-peer path using the
        /// sole connected peer when exactly one exists (the N&lt;=2 case), which matches the old behavior.
        /// New code calls OnPeerDisconnected(leaver, pos) directly.
        /// </summary>
        public void OnGuestDisconnected(Vector3 lastKnownPosition)
        {
            if (!Plugin.IsHost) return;
            var peers = Plugin.NetworkManager?.ConnectedPeers;
            if (peers != null && peers.Count == 1)
            {
                foreach (var p in peers) { OnPeerDisconnected(p, lastKnownPosition); return; }
            }
            // Ambiguous with 0 or 2+ peers - do nothing rather than dropping everyone's items.
            Plugin.Log.LogWarning("[ITEM] OnGuestDisconnected called without a holder id and peer count != 1; ignoring (use OnPeerDisconnected)");
        }

        /// <summary>
        /// Called when host disconnects. Drops all items host was holding.
        /// Guest only. Uses same drop logic as OnRemoteItemDropped for proper physics.
        /// </summary>
        public void OnHostDisconnected(Vector3 lastKnownPosition)
        {
            if (Plugin.IsHost) return;

            Plugin.Log.LogInfo($"Host disconnected, dropping {_remoteHeldItems.Count} items at {lastKnownPosition}");

            // Snapshot first: DropItemAtPosition mutates _remoteHeldItems, so we can't enumerate it
            // directly. (The guest force-quits right after a host-leave, so clearing everything here is
            // fine - on a guest the only remote holder is the host.)
            var snapshot = new List<KeyValuePair<int, ShipItem>>(_remoteHeldItems);
            foreach (var kvp in snapshot)
            {
                var item = kvp.Value;
                if (item == null) continue;

                // Use proper drop logic - same as OnRemoteItemDropped
                DropItemAtPosition(item, kvp.Key, lastKnownPosition);

                VerboseLogger.ItemApply($"Dropped host item {kvp.Key} at {lastKnownPosition}");
            }

            _remoteHeldItems.Clear();
            _heldItems.Clear();
            // Guest force-quits after host leave; clear the per-carrier held-item maps too (only the host
            // was a remote carrier here). At N=1 this is the single slot.
            _syncedHeldItems.Clear();
            _heldItemCarrier.Clear();
        }

        /// <summary>
        /// Properly drops an item at the specified world position.
        /// Handles both transforms, re-enables physics and colliders.
        /// Shared by OnRemoteItemDropped and OnGuestDisconnected.
        /// </summary>
        private void DropItemAtPosition(ShipItem item, int instanceId, Vector3 worldPos)
        {
            // This is an authoritative teardown applied locally (disconnect force-drop), not a player
            // action: raise the per-packet apply flag so patches triggered by the vanilla calls below
            // (notably ClearBoatLatch -> ShipItem.ExitBoat -> HangableItem.DisconnectJoint ->
            // OnLocalItemUnhung) don't echo packets mid-teardown (review finding). Both callers
            // (OnPeerDisconnected / OnHostDisconnected) are plain event handlers, never already inside
            // an apply window, so the finally can't clear an outer guard.
            IsApplyingRemoteState = true;
            try
            {
                DropItemAtPositionCore(item, instanceId, worldPos);
            }
            finally
            {
                IsApplyingRemoteState = false;
            }
        }

        private void DropItemAtPositionCore(ShipItem item, int instanceId, Vector3 worldPos)
        {
            // Remove from held tracking
            _heldItems.Remove(instanceId);
            _remoteHeldItems.Remove(instanceId);
            // Stop the per-carrier held-item visual from chasing this (now dropped) item.
            ClearSyncedHeldItemById(instanceId);

            // Re-enable item if it was hidden (in inventory)
            item.gameObject.SetActive(true);

            // Clear the fake held reference we set during pickup
            item.held = null;

            // TRADER-CROSSWIRE fix: restore the ShipItem's own trigger collider disabled while guest-held.
            SetShipItemOwnTriggers(item, true);

            // Clear boat references the vanilla way - see ClearBoatLatch (stale private
            // currentBoatCollider from a manual clear permanently blocked re-latching; 2026-07-02 sink).
            ClearBoatLatch(item);

            // Parent to shifting world
            var shiftingWorld = GameObject.Find("_shifting world")?.transform;

            item.transform.SetParent(shiftingWorld, worldPositionStays: false);
            item.transform.position = worldPos;

            if (item.itemRigidbodyC != null)
            {
                var itemRbTransform = item.itemRigidbodyC.transform;

                // Same parent as ShipItem for land drops
                itemRbTransform.SetParent(shiftingWorld, worldPositionStays: false);
                itemRbTransform.position = worldPos;

                // Reset physics state
                var rb = item.itemRigidbodyC.GetBody();
                if (rb != null)
                {
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }

                // Re-enable ALL colliders (was disabled during pickup)
                foreach (var col in item.itemRigidbodyC.GetComponents<Collider>())
                    col.enabled = true;

                // Re-enable ItemRigidbody
                item.itemRigidbodyC.enabled = true;
            }
        }

        #endregion

        #region Shop Item Sync

        /// <summary>
        /// Called when local player buys item from shop/vendor stall.
        /// Sends packet to tell other player to remove their local shop item at that position.
        /// </summary>
        public void OnLocalShopItemBought(int prefabIndex, Vector3 originalPosition)
        {
            var packet = new ShopItemBoughtPacket
            {
                PrefabIndex = prefabIndex,
                PositionX = originalPosition.x,
                PositionY = originalPosition.y,
                PositionZ = originalPosition.z
            };

            VerboseLogger.ItemSend($"ShopItemBought, prefab={prefabIndex}, pos={originalPosition}");

            Plugin.NetworkManager.SendToAllReliable(PacketType.ShopItemBought, w =>
            {
                PacketSerializer.WriteShopItemBought(w, packet);
            });
        }

        /// <summary>
        /// Called when receiving ShopItemBought packet.
        /// DISABLED: Now handled by item.Sell() in OnRemoteItemPickupRequest/OnRemoteItemPickedUp.
        /// Keeping method for packet compatibility but it no longer destroys items.
        /// </summary>
        public void OnRemoteShopItemBought(ShopItemBoughtPacket packet)
        {
            // No longer needed - shop item state is now handled by item.Sell()
            // in OnRemoteItemPickupRequest (host) and OnRemoteItemPickedUp (guest)
            // Just log for debugging
            Vector3 targetPos = new Vector3(packet.PositionX, packet.PositionY, packet.PositionZ);
            VerboseLogger.ItemRecv($"ShopItemBought (ignored - handled by Sell()), prefab={packet.PrefabIndex}, pos={targetPos}");
        }

        #endregion
    }
}
