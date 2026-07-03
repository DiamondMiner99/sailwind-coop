using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using HarmonyLib;
using SailwindCoop.Networking.Packets;

namespace SailwindCoop.Sync
{
    public static class BoatStateCollector
    {
        /// <summary>
        /// Collect full state of all boats for initial sync.
        /// </summary>
        public static BoatWorldStatePacket CollectWorldState()
        {
            var boats = BoatUtility.FindAllBoats();
            var boatDataList = new List<NetworkBoatData>();

            foreach (var kvp in boats)
            {
                var boatData = CollectBoatData(kvp.Value);
                boatDataList.Add(boatData);
            }

            var currentBoat = BoatUtility.GetCurrentBoat();
            var player = Refs.charController?.transform;

            // Use the correct coordinate space for player position: when on a boat, charController is in
            // physics space (Y~200), but we need visual space. Mirror the logic from PlayerSyncManager.
            Vector3 hostPosition;
            Quaternion hostRotation;
            bool isOnBoat = false;

            var visualBoat = GameState.currentBoat;
            if (visualBoat != null && player != null)
            {
                // Host is on boat - send boat-relative BODY position (visual space).
                // Mirror PlayerSyncManager: source from Refs.observerMirror (camera-independent) so
                // the join snapshot is correct even if the host is in third person at join time.
                isOnBoat = true;
                var bodyTransform = Refs.observerMirror != null ? Refs.observerMirror.transform : null;
                if (bodyTransform != null)
                {
                    hostPosition = visualBoat.transform.InverseTransformPoint(bodyTransform.position);
                    // FLOAT-ON-BOAT fix: drop controller-origin -> feet (must match PlayerSyncManager's
                    // 20Hz send exactly, or the avatar pops vertically on the first packet after join).
                    hostPosition.y -= PlayerSyncManager.ControllerFeetGap();
                }
                else
                {
                    const float eyeHeight = 1.7f;
                    var cameraFeetPos = Camera.main.transform.position - new Vector3(0, eyeHeight, 0);
                    hostPosition = visualBoat.transform.InverseTransformPoint(cameraFeetPos);
                }
                hostRotation = Quaternion.Inverse(visualBoat.transform.rotation) * (Camera.main?.transform.rotation ?? Quaternion.identity);
                Plugin.Log.LogInfo($"[BOAT:COLLECT] Host on boat, boatRelPos={hostPosition}");
            }
            else
            {
                // Host is on land - use world coordinates, converted to real (offset-independent)
                // coords for cross-region sync
                var offset = FloatingOriginManager.instance?.outCurrentOffset ?? Vector3.zero;
                var localPos = player?.position ?? Vector3.zero;
                hostPosition = localPos - offset;
                hostRotation = player?.rotation ?? Quaternion.identity;
                Plugin.Log.LogInfo($"[BOAT:COLLECT] Host on land, localPos={localPos}, realPos={hostPosition}");
            }

            // Include host's offset and region for cross-region join
            var hostOffset = FloatingOriginManager.instance?.outCurrentOffset ?? Vector3.zero;
            // currentTargetRegion is private, use Traverse to access it
            var currentRegion = RegionBlender.instance != null
                ? Traverse.Create(RegionBlender.instance).Field("currentTargetRegion").GetValue<Region>()
                : null;
            var hostRegion = currentRegion?.gameObject.name ?? "";

            // Collect world items (not parented to any boat)
            var worldItems = CollectWorldItems();

            // Seat the joiner's GameState.currentBoat even when the host is ashore at snapshot time. The
            // host's own currentBoat is "" whenever the host is on land, and a guest who receives
            // CurrentBoatName="" never has GameState.currentBoat seated and cannot control the shared boat
            // (helm/sails dead, push-colliders left enabled). When the host's currentBoat is empty, fall back
            // to the shared boat name (lastOwnedBoat ?? lastBoat) so a land-spawned guest is still seated onto
            // the shared boat. This mirrors the same fallback in BoatSyncManager.SendBoatWorldState.
            string currentBoatName = currentBoat?.gameObject.name ?? "";
            if (string.IsNullOrEmpty(currentBoatName))
                currentBoatName = GameState.lastOwnedBoat?.name ?? GameState.lastBoat?.name ?? "";

            return new BoatWorldStatePacket
            {
                Boats = boatDataList.ToArray(),
                CurrentBoatName = currentBoatName,
                WindState = Wind.currentWind,
                HostPlayerPosition = hostPosition,
                HostPlayerRotation = hostRotation,
                IsHostOnBoat = isOnBoat,
                WeatherState = WeatherSyncManager.CollectWeatherState(),
                HostOffset = hostOffset,
                HostRegionName = hostRegion,
                NearestPortName = FindNearestRecoveryPortName(),
                WorldItems = worldItems
            };
        }

        /// <summary>
        /// Find the nearest port that has a RecoveryPort component.
        /// This ensures the guest can recover to this port during join.
        /// </summary>
        private static string FindNearestRecoveryPortName()
        {
            var recoveryPorts = Object.FindObjectsOfType<RecoveryPort>();
            var camPos = Camera.main.transform.position;

            RecoveryPort nearest = null;
            float nearestDist = float.MaxValue;

            foreach (var rp in recoveryPorts)
            {
                float dist = Vector3.Distance(camPos, rp.transform.position);
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearest = rp;
                }
            }

            var portName = nearest?.parentPort?.GetPortName() ?? "";
            Plugin.Log.LogInfo($"[BOAT:COLLECT] Nearest RecoveryPort: {portName} at dist={nearestDist:F0}m");
            return portName;
        }

        /// <summary>
        /// Collect state for a single boat.
        /// </summary>
        public static NetworkBoatData CollectBoatData(SaveableObject boat)
        {
            var customization = boat.GetComponent<SaveableBoatCustomization>();
            var damage = boat.GetComponent<BoatDamage>();

            // Get customization data
            var customData = customization?.GetData();

            // Convert to real (offset-independent) coordinates so sync works when host and guest
            // have different FloatingOriginManager offsets
            var offset = FloatingOriginManager.instance?.outCurrentOffset ?? Vector3.zero;
            var realPosition = boat.transform.position - offset;

            // Collect dirt texture
            byte[] dirtTexture = null;
            var cleanable = boat.GetComponent<SaveableObject>()?.GetCleanable();
            if (cleanable != null)
            {
                var texture = cleanable.GetCurrentDirtTex() as Texture2D;
                if (texture != null)
                {
                    try
                    {
                        dirtTexture = texture.EncodeToPNG();
                        Plugin.Log.LogInfo($"[CLEANING:COLLECT] Encoded dirt texture for {boat.gameObject.name}: {dirtTexture.Length} bytes");
                    }
                    catch (System.Exception ex)
                    {
                        Plugin.Log.LogWarning($"[CLEANING:COLLECT] Failed to encode dirt texture for {boat.gameObject.name}: {ex.Message}");
                    }
                }
            }

            return new NetworkBoatData
            {
                Name = boat.gameObject.name,
                Position = realPosition,
                Rotation = boat.transform.rotation,

                // Customization
                MastsEnabled = customData?.masts ?? new bool[30],
                Sails = CollectSails(customData),
                PartActiveOptions = customData?.partActiveOptions?.ToArray() ?? new int[0],

                // Items
                Items = CollectItems(boat),

                // Ropes
                RopeLengths = CollectRopeLengths(boat),

                // Anchor
                IsAnchored = BoatUtility.IsBoatAnchored(boat),
                AnchorRopeLength = GetAnchorLength(boat),

                // Mooring ropes
                MooringRopes = CollectMooringRopes(boat),

                // Damage
                WaterLevel = damage?.waterLevel ?? 0,
                HullDamage = damage?.hullDamage ?? 0,
                Oakum = damage?.oakum ?? 0,

                // Ownership (extraSetting = true means player owns boat)
                IsOwned = boat.extraSetting,

                // Cleaning
                DirtTexture = dirtTexture
            };
        }

        private static NetworkSailData[] CollectSails(SaveBoatCustomizationData customData)
        {
            if (customData?.sails == null)
            {
                return new NetworkSailData[0];
            }

            return customData.sails.Select(s => new NetworkSailData
            {
                PrefabIndex = s.prefabIndex,
                MastIndex = s.mastIndex,
                InstallHeight = s.installHeight,
                MinAngle = s.minAngle,
                MaxAngle = s.maxAngle,
                Health = s.health,
                Color = s.sailColor,
                ScaleY = s.scaleY,  // BS1: carry custom (reefed/resized) sail scale to the guest
                ScaleZ = s.scaleZ
            }).ToArray();
        }

        /// <summary>
        /// Collect all items on a boat using the game's save system.
        /// </summary>
        private static NetworkSaveData[] CollectItems(SaveableObject boat)
        {
            var items = new List<NetworkSaveData>();
            var itemIds = new List<int>();
            var localItems = boat.GetComponent<BoatLocalItems>();

            if (localItems == null) return items.ToArray();

            // Get all SaveablePrefab components that are children of this boat
            var prefabs = boat.GetComponentsInChildren<SaveablePrefab>();

            foreach (var prefab in prefabs)
            {
                // Skip if not a ship item
                var shipItem = prefab.GetComponent<ShipItem>();
                if (shipItem == null) continue;

                // HELD-ITEM PHANTOM FIX (boat path, 2026-07-02): skip items currently in someone's hand.
                // Same rationale as the CollectWorldItems held filter: a held item can still be
                // boat-childed (ShipItem.EnterBoat parents it under the boat and holding does not
                // re-parent it out), so it would be serialized as a loose boat item under the
                // ORIGINAL's instanceId. Skipping BOTH serialization and itemIds registration means
                // the joiner's synced set never contains the id, so the holder's next drop/pickup
                // triggers the per-peer AnyPeerMissingSyncedItem backfill and spawn-syncs it cleanly.
                if (shipItem.held != null)
                {
                    Plugin.Log.LogInfo($"[ITEM:COLLECT] Skipping in-hand boat item {prefab.name} (id={prefab.instanceId}) - held items are backfilled on drop, not serialized to joiners");
                    continue;
                }

                // POCKET-INHERIT FIX (boat path, cluster B-pipe-not-synced): skip items stowed in the
                // HOST's personal pockets while aboard. Vanilla never re-parents an item when it is
                // pocketed (ItemRigidbody.EnterInventorySlot only disables the collider), so an item
                // pocketed while standing ON the boat remains a child of the boat and is enumerated
                // here instead of by CollectWorldItems' pocket filter. Serializing it puts the host's
                // ghost into the JOINER's pocket slot (SaveablePrefab.Load -> PutInInventory), the
                // join clean-slate destroys it, and because the id was registered as synced the
                // pickup-time backfill never fires - every later ItemDropped/PipeFilled for that id
                // no-ops on the guest for the whole session. Skip ONLY the genuine player-pocket
                // range (0..99); boat CargoCarrier shelves (>=100) ARE shared boat state and are kept.
                int invSlot = shipItem.GetCurrentInventorySlot();
                if (invSlot >= 0 && invSlot < 100)
                {
                    Plugin.Log.LogInfo($"[ITEM:COLLECT] Skipping host pocket item {prefab.name} (id={prefab.instanceId}, slot={invSlot}) (boat-childed) - personal inventory is not synced to joiners");
                    continue;
                }

                // Items loaded from save have instanceId=0; assign unique IDs so they can be synced.
                if (prefab.instanceId == 0)
                {
                    prefab.instanceId = UnityEngine.Random.Range(1, int.MaxValue);
                    Plugin.Log.LogInfo($"[ITEM:COLLECT] Assigned new ID {prefab.instanceId} to {prefab.name}");
                }

                var networkData = ConvertToNetworkSaveData(prefab, boat, false);
                items.Add(networkData);
                itemIds.Add(prefab.instanceId);
            }

            // Register boat items with ItemSyncManager
            ItemSyncManager.Instance?.RegisterBoatItems(itemIds);

            Plugin.Log.LogDebug($"Collected {items.Count} items from {boat.gameObject.name}");
            return items.ToArray();
        }

        /// <summary>
        /// Collect all world items (not parented to any boat, sold=true).
        /// </summary>
        public static NetworkSaveData[] CollectWorldItems()
        {
            var items = new List<NetworkSaveData>();
            var itemIds = new List<int>();
            var boats = BoatUtility.FindAllBoats();
            var boatTransforms = new HashSet<Transform>(boats.Values.Select(b => b.transform));

            // Get all SaveablePrefab components in scene
            var prefabs = Object.FindObjectsOfType<SaveablePrefab>();

            foreach (var prefab in prefabs)
            {
                // Skip if not a ship item
                var shipItem = prefab.GetComponent<ShipItem>();
                if (shipItem == null) continue;

                // Skip shop inventory (not owned by player)
                if (!shipItem.sold) continue;

                // HELD-ITEM PHANTOM FIX (2026-07-02 playtest): skip items currently in someone's hand.
                // An in-hand item is not boat-parented and reports inventory slot -1, so it passed every
                // filter below and was serialized as a LOOSE world item at the holder's hand position,
                // under the ORIGINAL's instanceId. The joiner then spawned a phantom copy on the ground
                // "in front of" the players; picking the phantom up resolved by id to the REAL item on
                // the host (by then back on the boat) and hijacked it out of the world. held is non-null
                // both for the host's own vanilla hold and for the fake held reference the mod sets on
                // remote-held items, so this covers any carrier. The item is NOT lost to the joiner:
                // the holder's next drop spawn-syncs it first (per-peer AnyPeerMissingSyncedItem backfill
                // in SendDropPacket / the OnRemoteItemDropped relay), mirroring the pickup path.
                if (shipItem.held != null)
                {
                    Plugin.Log.LogInfo($"[ITEM:COLLECT] Skipping in-hand item {prefab.name} (id={prefab.instanceId}) - held items are backfilled on drop, not serialized to joiners");
                    continue;
                }

                // POCKET-INHERIT FIX: skip items held in the HOST's personal inventory pockets.
                // Host pocket items are sold AND parented to a UI pocket-slot transform (not a boat), so
                // they pass both the sold and the IsParentedToBoat filters. ConvertToNetworkSaveData then
                // copies their InventorySlot, and on apply SaveablePrefab.Load -> PutInInventory(slot)
                // inserts them into the JOINER's own pockets, where they sit as non-interactable ghosts
                // (they carry the host's instanceId; the host still holds the originals). Pocket items are
                // personal, not shared world state, so they must never be serialized to a joiner.
                //
                // ShipItem.GetCurrentInventorySlot() is the reliable signal: -1 = loose in the world,
                // 0..4 = one of the player's five pocket slots, >=100 = a boat CargoCarrier. Skip ONLY the
                // genuine player-pocket range (0..99); a loose ground/dock item returns -1 and is kept,
                // and boat cargo (>=100) is left to the boat-item path. Read via the same accessor the rest
                // of the mod uses (ItemSyncManager / ItemPatches reference GetCurrentInventorySlot).
                // NOTE: CollectItems (boat path) enforces the same held + pocket filters, since an
                // item pocketed while ABOARD stays boat-childed and is enumerated there instead.
                int invSlot = shipItem.GetCurrentInventorySlot();
                if (invSlot >= 0 && invSlot < 100)
                {
                    Plugin.Log.LogInfo($"[ITEM:COLLECT] Skipping host pocket item {prefab.name} (id={prefab.instanceId}, slot={invSlot}) - personal inventory is not synced to joiners");
                    continue;
                }

                // Skip items parented to boats (already collected in boat sync)
                if (IsParentedToBoat(prefab.transform, boatTransforms)) continue;

                // Items loaded from save have instanceId=0; assign unique IDs so they can be synced.
                if (prefab.instanceId == 0)
                {
                    prefab.instanceId = UnityEngine.Random.Range(1, int.MaxValue);
                    Plugin.Log.LogInfo($"[ITEM:COLLECT] Assigned new ID {prefab.instanceId} to world item {prefab.name}");
                }

                var networkData = ConvertToNetworkSaveData(prefab, null, true);
                items.Add(networkData);
                itemIds.Add(prefab.instanceId);
            }

            // Register world items with ItemSyncManager
            ItemSyncManager.Instance?.RegisterBoatItems(itemIds);

            Plugin.Log.LogInfo($"[ITEM:COLLECT] Collected {items.Count} world items");
            return items.ToArray();
        }

        /// <summary>
        /// Check if a transform is parented to any boat.
        /// </summary>
        private static bool IsParentedToBoat(Transform transform, HashSet<Transform> boatTransforms)
        {
            var current = transform.parent;
            while (current != null)
            {
                if (boatTransforms.Contains(current)) return true;
                current = current.parent;
            }
            return false;
        }

        /// <summary>
        /// Convert a SaveablePrefab to NetworkSaveData using the game's save system.
        /// </summary>
        private static NetworkSaveData ConvertToNetworkSaveData(SaveablePrefab prefab, SaveableObject boat, bool isWorldItem)
        {
            // Use game's save system to get complete state
            var saveData = prefab.PrepareSaveData();

            var offset = FloatingOriginManager.instance?.outCurrentOffset ?? Vector3.zero;

            Vector3 position;
            if (isWorldItem)
            {
                // World item - use offset-independent world coordinates
                position = prefab.transform.position - offset;
            }
            else
            {
                // Boat item - use boat-relative coordinates
                position = boat.transform.InverseTransformPoint(prefab.transform.position);
            }

            var networkData = new NetworkSaveData
            {
                InstanceId = prefab.instanceId,
                PrefabIndex = saveData.prefabIndex,
                Position = position,
                Rotation = prefab.transform.rotation,
                IsWorldPosition = isWorldItem,
                ParentBoatName = boat?.gameObject.name ?? "",

                IsSold = saveData.isSold,
                Health = saveData.itemHealth,
                Amount = saveData.itemAmount,
                InventorySlot = saveData.inventorySlot,
                CrateId = saveData.crateId,
                MissionIndex = saveData.itemMissionIndex,
                ParentObject = saveData.itemParentObject,
                DaysInStorage = saveData.daysInStorage,

                ExtraValue0 = saveData.extraValue0,
                ExtraValue1 = saveData.extraValue1,
                ExtraValue2 = saveData.extraValue2,
                ExtraValue3 = saveData.extraValue3,
                ExtraValue4 = saveData.extraValue4,

                HasChartData = saveData.chartData != null,
                ChartData = ConvertChartData(saveData.chartData)
            };

            return networkData;
        }

        /// <summary>
        /// Convert game's ChartData to network-serializable format.
        /// </summary>
        private static NetworkChartData ConvertChartData(ChartData chartData)
        {
            if (chartData == null)
            {
                return new NetworkChartData
                {
                    Lines = new NetworkChartLine[0],
                    Points = new NetworkChartPoint[0]
                };
            }

            var lines = new List<NetworkChartLine>();
            if (chartData.lines != null)
            {
                foreach (var line in chartData.lines)
                {
                    lines.Add(new NetworkChartLine
                    {
                        StartX = line.startX,
                        StartY = line.startY,
                        EndX = line.endX,
                        EndY = line.endY,
                        Color = line.color
                    });
                }
            }

            var points = new List<NetworkChartPoint>();
            if (chartData.points != null)
            {
                foreach (var point in chartData.points)
                {
                    points.Add(new NetworkChartPoint
                    {
                        PosX = point.posX,
                        PosY = point.posY
                    });
                }
            }

            return new NetworkChartData
            {
                Lines = lines.ToArray(),
                Points = points.ToArray()
            };
        }

        private static float[] CollectRopeLengths(SaveableObject boat)
        {
            var ropes = BoatUtility.GetRopeControllers(boat);
            var lengths = ropes.Select(r => r.currentLength).ToArray();

            var logStr = string.Join(", ", lengths.Select((l, i) => $"[{i}]={l:F2}"));
            Plugin.Log.LogInfo($"[ROPE:COLLECT] boat={boat.gameObject.name}, ropes={lengths.Length}: {logStr}");

            return lengths;
        }

        private static float GetAnchorLength(SaveableObject boat)
        {
            var anchor = boat.GetComponentInChildren<Anchor>();
            if (anchor == null) return 0;

            var joint = anchor.GetComponent<ConfigurableJoint>();
            return joint?.linearLimit.limit ?? 0;
        }

        private static NetworkMooringData[] CollectMooringRopes(SaveableObject boat)
        {
            var mooringRopes = boat.GetComponent<BoatMooringRopes>();
            if (mooringRopes?.ropes == null)
            {
                return new NetworkMooringData[0];
            }

            // Convert dock positions to real (offset-independent) coordinates so mooring points
            // survive host/guest floating-origin offset differences.
            var offset = FloatingOriginManager.instance?.outCurrentOffset ?? Vector3.zero;

            var data = new NetworkMooringData[mooringRopes.ropes.Length];
            for (int i = 0; i < mooringRopes.ropes.Length; i++)
            {
                var rope = mooringRopes.ropes[i];
                bool isMoored = rope.IsMoored();
                Vector3 dockPos = Vector3.zero;

                if (isMoored)
                {
                    // Use rope's current world position when moored (the rope end is at the dock),
                    // converted to real coords for cross-region sync.
                    dockPos = rope.transform.position - offset;
                }

                data[i] = new NetworkMooringData
                {
                    IsMoored = isMoored,
                    DockPosition = dockPos,
                    LengthSquared = rope.currentRopeLengthSquared
                };
            }
            return data;
        }
    }
}
