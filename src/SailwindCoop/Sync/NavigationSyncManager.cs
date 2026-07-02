using System.Collections.Generic;
using UnityEngine;
using Steamworks;
using SailwindCoop.Debug;
using SailwindCoop.Networking.Packets;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Manages synchronization of navigation items (clock, compass, quadrant, spyglass, map).
    /// </summary>
    public class NavigationSyncManager : MonoBehaviour
    {
        public static NavigationSyncManager Instance { get; private set; }

        /// <summary>
        /// Set to true when applying remote state to prevent feedback loops.
        /// </summary>
        public bool IsApplyingRemoteState { get; private set; }

        // === Map Lock State ===

        // Host tracks which maps are locked (itemId -> steamId)
        private Dictionary<int, ulong> _mapDrawingLocks = new Dictionary<int, ulong>();

        // === Map Line Sync ===

        private const float TempLineSyncInterval = 0.2f; // 5Hz
        private float _lastTempLineSyncTime;

        private void Awake()
        {
            if (Instance != null)
            {
                Destroy(this);
                return;
            }
            Instance = this;
        }

        // === Simple Navigation Items ===

        /// <summary>
        /// Called when local player changes a navigation item state (clock lid, quadrant, spyglass, compass).
        /// </summary>
        public void OnLocalNavItemStateChanged(int itemInstanceId, NavItemStateType stateType, float value)
        {
            if (!Plugin.IsMultiplayer) return;
            if (IsApplyingRemoteState) return;

            VerboseLogger.NavSend($"NavItemState, item={itemInstanceId}, type={stateType}, value={value:F3}");

            var packet = new NavItemStatePacket
            {
                ItemInstanceId = itemInstanceId,
                StateType = stateType,
                Value = value
            };

            Plugin.NetworkManager.SendToAllReliable(PacketType.NavItemState, w =>
                PacketSerializer.WriteNavItemState(w, packet));
        }

        /// <summary>
        /// Called when remote player changes a navigation item state.
        /// </summary>
        public void OnRemoteNavItemStateChanged(NavItemStatePacket packet, SteamId sender = default)
        {
            VerboseLogger.NavRecv($"NavItemState, item={packet.ItemInstanceId}, type={packet.StateType}, value={packet.Value:F3}");

            // Star-relay: forward to the other guests so all crew see the nav-item change.
            if (Plugin.IsHost)
                Plugin.NetworkManager.SendToAllExcept(sender, PacketType.NavItemState, w =>
                    PacketSerializer.WriteNavItemState(w, packet));

            IsApplyingRemoteState = true;
            try
            {
                ApplyNavItemState(packet);
            }
            finally
            {
                IsApplyingRemoteState = false;
            }
        }

        private void ApplyNavItemState(NavItemStatePacket packet)
        {
            // Find item by instance ID
            var item = FindItemByInstanceId(packet.ItemInstanceId);
            if (item == null)
            {
                VerboseLogger.NavApply($"NavItemState FAILED: item {packet.ItemInstanceId} not found");
                return;
            }

            switch (packet.StateType)
            {
                case NavItemStateType.ClockLid:
                    ApplyClockLidState(item, packet.Value > 0.5f);
                    break;
                case NavItemStateType.QuadrantInspect:
                    ApplyQuadrantInspectState(item, packet.Value > 0.5f);
                    break;
                case NavItemStateType.SpyglassZoom:
                    ApplySpyglassZoomState(item, packet.Value);
                    break;
                case NavItemStateType.CompassLatitude:
                    ApplyCompassLatitudeState(item, packet.Value);
                    break;
            }
        }

        private void ApplyClockLidState(PickupableItem item, bool isOpen)
        {
            var clock = item as ShipItemClock;
            if (clock == null) return;

            // Access private fields via reflection
            var lidField = typeof(ShipItemClock).GetField("lidOpen",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var lidAnimField = typeof(ShipItemClock).GetField("lidAnimPlaying",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var lidTransformField = typeof(ShipItemClock).GetField("lid",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            if (lidField == null || lidTransformField == null) return;

            var lid = lidTransformField.GetValue(clock) as Transform;
            if (lid == null) return;

            lidField.SetValue(clock, isOpen);
            lidAnimField?.SetValue(clock, false);

            // Set lid rotation directly
            float targetAngle = isOpen ? 105f : 0f;
            lid.localRotation = Quaternion.Euler(targetAngle, 0f, 180f);

            VerboseLogger.NavApply($"Clock lid set, item={item.GetComponent<SaveablePrefab>()?.instanceId}, open={isOpen}");
        }

        private void ApplyQuadrantInspectState(PickupableItem item, bool isInspecting)
        {
            var quadrant = item as ShipItemQuadrant;
            if (quadrant == null) return;

            var inspectingField = typeof(ShipItemQuadrant).GetField("inspecting",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var rotatingField = typeof(ShipItemQuadrant).GetField("rotating",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var rotatingParentField = typeof(ShipItemQuadrant).GetField("rotatingParent",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var initialRotField = typeof(ShipItemQuadrant).GetField("initialRot",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var inspectRotField = typeof(ShipItemQuadrant).GetField("inspectRot",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            if (inspectingField == null || rotatingParentField == null) return;

            var rotatingParent = rotatingParentField.GetValue(quadrant) as Transform;
            if (rotatingParent == null) return;

            var initialRot = (Quaternion)initialRotField.GetValue(quadrant);
            var inspectRot = (Quaternion)inspectRotField.GetValue(quadrant);

            inspectingField.SetValue(quadrant, isInspecting);
            rotatingField?.SetValue(quadrant, false);

            // Set rotation directly
            rotatingParent.localRotation = isInspecting ? inspectRot : initialRot;

            VerboseLogger.NavApply($"Quadrant inspect set, item={item.GetComponent<SaveablePrefab>()?.instanceId}, inspecting={isInspecting}");
        }

        private void ApplySpyglassZoomState(PickupableItem item, float zoom)
        {
            var spyglass = item as ShipItemSpyglass;
            if (spyglass == null) return;

            var zoomField = typeof(ShipItemSpyglass).GetField("currentZoom",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            if (zoomField == null) return;

            zoomField.SetValue(spyglass, zoom);

            // Call private update methods via reflection
            var updateCamMethod = typeof(ShipItemSpyglass).GetMethod("UpdateCam",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var updatePartsMethod = typeof(ShipItemSpyglass).GetMethod("UpdateMovingParts",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var updateColMethod = typeof(ShipItemSpyglass).GetMethod("UpdateCapsuleCol",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            updateCamMethod?.Invoke(spyglass, null);
            updatePartsMethod?.Invoke(spyglass, null);
            updateColMethod?.Invoke(spyglass, null);

            VerboseLogger.NavApply($"Spyglass zoom set, item={item.GetComponent<SaveablePrefab>()?.instanceId}, zoom={zoom:F3}");
        }

        private void ApplyCompassLatitudeState(PickupableItem item, float currentRot)
        {
            var compass = item as ShipItemCompass;
            if (compass == null) return;

            var chronoLatitude = compass.chronoLatitude;
            if (chronoLatitude == null) return;

            var currentRotField = typeof(ChronometerLatitude).GetField("currentRot",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            if (currentRotField == null) return;

            currentRotField.SetValue(chronoLatitude, currentRot);

            VerboseLogger.NavApply($"Compass latitude set, item={item.GetComponent<SaveablePrefab>()?.instanceId}, rot={currentRot:F1}");
        }

        // === Map Fold State ===

        public void OnLocalMapFoldStateChanged(int itemInstanceId, bool isFolded)
        {
            if (!Plugin.IsMultiplayer) return;
            if (IsApplyingRemoteState) return;

            VerboseLogger.NavSend($"MapFoldState, item={itemInstanceId}, folded={isFolded}");

            var packet = new MapFoldStatePacket
            {
                ItemInstanceId = itemInstanceId,
                IsFolded = isFolded
            };

            Plugin.NetworkManager.SendToAllReliable(PacketType.MapFoldState, w =>
                PacketSerializer.WriteMapFoldState(w, packet));
        }

        public void OnRemoteMapFoldStateChanged(MapFoldStatePacket packet, SteamId sender = default)
        {
            VerboseLogger.NavRecv($"MapFoldState, item={packet.ItemInstanceId}, folded={packet.IsFolded}");

            // Star-relay: forward to the other guests so all crew see the fold change.
            if (Plugin.IsHost)
                Plugin.NetworkManager.SendToAllExcept(sender, PacketType.MapFoldState, w =>
                    PacketSerializer.WriteMapFoldState(w, packet));

            IsApplyingRemoteState = true;
            try
            {
                var item = FindItemByInstanceId(packet.ItemInstanceId);
                if (item == null)
                {
                    VerboseLogger.NavApply($"MapFoldState FAILED: item {packet.ItemInstanceId} not found");
                    return;
                }

                var foldable = item as ShipItemFoldable;
                if (foldable == null) return;

                // Set amount (0 = unfolded, 1 = folded) and trigger visual update
                foldable.amount = packet.IsFolded ? 1f : 0f;

                // Call private Fold/Unfold methods
                var methodName = packet.IsFolded ? "Fold" : "Unfold";
                var method = typeof(ShipItemFoldable).GetMethod(methodName,
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                method?.Invoke(foldable, null);

                VerboseLogger.NavApply($"Map fold set, item={packet.ItemInstanceId}, folded={packet.IsFolded}");
            }
            finally
            {
                IsApplyingRemoteState = false;
            }
        }

        // === Map Drawing Lock ===

        /// <summary>
        /// Guest requests to enter drawing mode on a map.
        /// </summary>
        public void RequestMapDrawing(int itemInstanceId, int prefabIndex)
        {
            if (Plugin.IsHost) return; // Host doesn't request, just locks directly

            VerboseLogger.NavSend($"MapDrawRequest, item={itemInstanceId}, prefab={prefabIndex}");

            var packet = new MapDrawRequestPacket
            {
                ItemInstanceId = itemInstanceId,
                PrefabIndex = prefabIndex
            };
            Plugin.NetworkManager.SendToAllReliable(PacketType.MapDrawRequest, w =>
                PacketSerializer.WriteMapDrawRequest(w, packet));
        }

        /// <summary>
        /// Host receives drawing request from guest.
        /// </summary>
        public void OnMapDrawRequest(SteamId sender, MapDrawRequestPacket packet)
        {
            if (!Plugin.IsHost) return;

            VerboseLogger.NavRecv($"MapDrawRequest, item={packet.ItemInstanceId}, prefab={packet.PrefabIndex}, from={sender}");

            // Validate map item
            if (!ItemSyncManager.Instance.ValidateItem(packet.ItemInstanceId, packet.PrefabIndex, out int expectedPrefab))
            {
                if (expectedPrefab == -1)
                {
                    ItemSyncManager.Instance.SendItemDestroyed(packet.ItemInstanceId, sender);
                }
                else
                {
                    ItemSyncManager.Instance.SendItemResync(packet.ItemInstanceId, sender);
                }
                return;
            }

            bool granted = !_mapDrawingLocks.ContainsKey(packet.ItemInstanceId);
            ulong lockedBy = 0;

            if (granted)
            {
                _mapDrawingLocks[packet.ItemInstanceId] = sender.Value;
                lockedBy = sender.Value;
            }
            else
            {
                lockedBy = _mapDrawingLocks[packet.ItemInstanceId];
            }

            var response = new MapDrawResponsePacket
            {
                ItemInstanceId = packet.ItemInstanceId,
                Granted = granted,
                LockedBySteamId = lockedBy,
                RequesterSteamId = sender.Value // address the reply to the guest that asked
            };

            VerboseLogger.NavSend($"MapDrawResponse, item={packet.ItemInstanceId}, granted={granted}, to={sender}");
            Plugin.NetworkManager.SendToAllReliable(PacketType.MapDrawResponse, w =>
                PacketSerializer.WriteMapDrawResponse(w, response));
        }

        /// <summary>
        /// Guest receives drawing response from host.
        /// </summary>
        public void OnMapDrawResponse(MapDrawResponsePacket packet)
        {
            VerboseLogger.NavRecv($"MapDrawResponse, item={packet.ItemInstanceId}, granted={packet.Granted}, to={packet.RequesterSteamId}");

            // N-player: the response is broadcast, so ignore one addressed to a different guest.
            // (RequesterSteamId == 0 means a legacy/host-origin response - don't filter it out.)
            if (packet.RequesterSteamId != 0 && packet.RequesterSteamId != SteamClient.SteamId)
            {
                VerboseLogger.NavRecv($"MapDrawResponse ignored (not for us), to={packet.RequesterSteamId}");
                return;
            }

            if (!packet.Granted)
            {
                // Show notification
                NotificationUi.instance?.ShowNotification("Map is being used by another player");
                VerboseLogger.NavEvent($"Map drawing denied, item={packet.ItemInstanceId}, lockedBy={packet.LockedBySteamId}");
            }
            // If granted, the MapTableCamera.EnableMapCam will proceed (handled in patch)
        }

        /// <summary>
        /// Host locks a map for drawing (host entering drawing mode).
        /// </summary>
        public void LockMapForDrawing(int itemInstanceId)
        {
            if (!Plugin.IsHost) return;

            _mapDrawingLocks[itemInstanceId] = Steamworks.SteamClient.SteamId;

            var packet = new MapDrawLockedPacket
            {
                ItemInstanceId = itemInstanceId,
                LockedBySteamId = Steamworks.SteamClient.SteamId
            };

            VerboseLogger.NavSend($"MapDrawLocked, item={itemInstanceId}");
            Plugin.NetworkManager.SendToAllReliable(PacketType.MapDrawLocked, w =>
                PacketSerializer.WriteMapDrawLocked(w, packet));
        }

        /// <summary>
        /// Guest receives notification that host locked a map.
        /// </summary>
        public void OnMapDrawLocked(MapDrawLockedPacket packet)
        {
            VerboseLogger.NavRecv($"MapDrawLocked, item={packet.ItemInstanceId}, by={packet.LockedBySteamId}");
            // Store lock state locally if needed for UI feedback
        }

        /// <summary>
        /// Player releases drawing lock (exits drawing mode).
        /// </summary>
        public void ReleaseMapDrawing(int itemInstanceId)
        {
            if (Plugin.IsHost)
            {
                _mapDrawingLocks.Remove(itemInstanceId);
            }

            var packet = new MapDrawReleasePacket { ItemInstanceId = itemInstanceId };

            VerboseLogger.NavSend($"MapDrawRelease, item={itemInstanceId}");
            Plugin.NetworkManager.SendToAllReliable(PacketType.MapDrawRelease, w =>
                PacketSerializer.WriteMapDrawRelease(w, packet));
        }

        /// <summary>
        /// Receives notification that drawing lock was released.
        /// </summary>
        public void OnMapDrawRelease(MapDrawReleasePacket packet)
        {
            VerboseLogger.NavRecv($"MapDrawRelease, item={packet.ItemInstanceId}");

            if (Plugin.IsHost)
            {
                _mapDrawingLocks.Remove(packet.ItemInstanceId);
            }
        }

        /// <summary>
        /// Check if a map is available for drawing (host-side check).
        /// </summary>
        public bool IsMapAvailableForDrawing(int itemInstanceId)
        {
            if (!Plugin.IsHost) return true; // Guest always requests
            return !_mapDrawingLocks.ContainsKey(itemInstanceId);
        }

        // === Map Line Sync ===

        /// <summary>
        /// Called when a line is committed (finished drawing).
        /// </summary>
        public void OnLocalMapLineAdded(int itemInstanceId, ChartLine line)
        {
            if (!Plugin.IsMultiplayer) return;

            var packet = new MapLinePacket
            {
                ItemInstanceId = itemInstanceId,
                StartX = line.startX,
                StartY = line.startY,
                EndX = line.endX,
                EndY = line.endY,
                Color = line.color
            };

            VerboseLogger.NavSend($"MapLineAdd, item={itemInstanceId}, color={line.color}");
            Plugin.NetworkManager.SendToAllReliable(PacketType.MapLineAdd, w =>
                PacketSerializer.WriteMapLine(w, packet));
        }

        /// <summary>
        /// Called while drawing a line (temp line update).
        /// </summary>
        public void OnLocalMapTempLineChanged(int itemInstanceId, ChartLine tempLine)
        {
            if (!Plugin.IsMultiplayer) return;

            // Throttle to 5Hz
            if (Time.time - _lastTempLineSyncTime < TempLineSyncInterval) return;
            _lastTempLineSyncTime = Time.time;

            var packet = new MapTempLinePacket
            {
                ItemInstanceId = itemInstanceId,
                HasLine = tempLine != null,
                StartX = tempLine?.startX ?? 0,
                StartY = tempLine?.startY ?? 0,
                EndX = tempLine?.endX ?? 0,
                EndY = tempLine?.endY ?? 0,
                Color = tempLine?.color ?? 0
            };

            VerboseLogger.NavSend($"MapTempLine, item={itemInstanceId}, hasLine={packet.HasLine}");
            Plugin.NetworkManager.SendToAllReliable(PacketType.MapTempLine, w =>
                PacketSerializer.WriteMapTempLine(w, packet));
        }

        /// <summary>
        /// Clear temp line when drawing is cancelled or finished.
        /// </summary>
        public void OnLocalMapTempLineCleared(int itemInstanceId)
        {
            if (!Plugin.IsMultiplayer) return;

            var packet = new MapTempLinePacket
            {
                ItemInstanceId = itemInstanceId,
                HasLine = false
            };

            VerboseLogger.NavSend($"MapTempLine clear, item={itemInstanceId}");
            Plugin.NetworkManager.SendToAllReliable(PacketType.MapTempLine, w =>
                PacketSerializer.WriteMapTempLine(w, packet));
        }

        /// <summary>
        /// Receives a committed line from remote player.
        /// </summary>
        public void OnRemoteMapLineAdded(MapLinePacket packet, SteamId sender = default)
        {
            VerboseLogger.NavRecv($"MapLineAdd, item={packet.ItemInstanceId}, color={packet.Color}");

            // Star-relay: forward the committed line to the other guests (reliable).
            if (Plugin.IsHost)
                Plugin.NetworkManager.SendToAllExcept(sender, PacketType.MapLineAdd, w =>
                    PacketSerializer.WriteMapLine(w, packet));

            var item = FindItemByInstanceId(packet.ItemInstanceId);
            var foldable = item as ShipItemFoldable;
            if (foldable?.mapChart?.chartData == null) return;

            var line = new ChartLine
            {
                startX = packet.StartX,
                startY = packet.StartY,
                endX = packet.EndX,
                endY = packet.EndY,
                color = packet.Color
            };

            foldable.mapChart.chartData.lines.Add(line);
            foldable.mapChart.UpdateTexture();

            VerboseLogger.NavApply($"Map line added, item={packet.ItemInstanceId}, totalLines={foldable.mapChart.chartData.lines.Count}");
        }

        /// <summary>
        /// Receives a temp line update from remote player.
        /// </summary>
        public void OnRemoteMapTempLineChanged(MapTempLinePacket packet, SteamId sender = default)
        {
            VerboseLogger.NavRecv($"MapTempLine, item={packet.ItemInstanceId}, hasLine={packet.HasLine}");

            // Star-relay: forward temp-line updates to the other guests. Sent RELIABLE here (matching
            // the original send and avoiding a dropped terminal clear) - low 5Hz volume makes this cheap.
            if (Plugin.IsHost)
                Plugin.NetworkManager.SendToAllExcept(sender, PacketType.MapTempLine, w =>
                    PacketSerializer.WriteMapTempLine(w, packet));

            var item = FindItemByInstanceId(packet.ItemInstanceId);
            var foldable = item as ShipItemFoldable;
            if (foldable?.mapChart?.chartData == null) return;

            if (packet.HasLine)
            {
                var tempLine = new ChartLine
                {
                    startX = packet.StartX,
                    startY = packet.StartY,
                    endX = packet.EndX,
                    endY = packet.EndY,
                    color = packet.Color
                };
                foldable.mapChart.chartData.tempLine = tempLine;
            }
            else
            {
                foldable.mapChart.chartData.tempLine = null;
            }

            foldable.mapChart.UpdateTexture();

            VerboseLogger.NavApply($"Map temp line set, item={packet.ItemInstanceId}, hasLine={packet.HasLine}");
        }

        /// <summary>
        /// Send full map sync to a joining guest.
        /// </summary>
        public void SendMapFullSyncToGuest(ulong guestSteamId, ShipItemFoldable foldable)
        {
            if (!Plugin.IsHost) return;
            if (foldable?.mapChart?.chartData?.lines == null) return;

            var instanceId = foldable.GetComponent<SaveablePrefab>()?.instanceId ?? 0;
            if (instanceId == 0) return;

            var lines = foldable.mapChart.chartData.lines;
            var linePackets = new MapLinePacket[lines.Count];

            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                linePackets[i] = new MapLinePacket
                {
                    ItemInstanceId = instanceId,
                    StartX = line.startX,
                    StartY = line.startY,
                    EndX = line.endX,
                    EndY = line.endY,
                    Color = line.color
                };
            }

            var packet = new MapFullSyncPacket
            {
                ItemInstanceId = instanceId,
                Lines = linePackets
            };

            VerboseLogger.NavSend($"MapFullSync, item={instanceId}, lines={lines.Count}, to={guestSteamId}");
            Plugin.NetworkManager.SendReliable(guestSteamId, PacketType.MapFullSync, w =>
                PacketSerializer.WriteMapFullSync(w, packet));
        }

        /// <summary>
        /// Receives full map sync from host.
        /// </summary>
        public void OnMapFullSync(MapFullSyncPacket packet)
        {
            VerboseLogger.NavRecv($"MapFullSync, item={packet.ItemInstanceId}, lines={packet.Lines?.Length ?? 0}");

            var item = FindItemByInstanceId(packet.ItemInstanceId);
            var foldable = item as ShipItemFoldable;
            if (foldable?.mapChart?.chartData == null) return;

            // Clear and repopulate lines
            foldable.mapChart.chartData.lines.Clear();

            if (packet.Lines != null)
            {
                foreach (var linePacket in packet.Lines)
                {
                    var line = new ChartLine
                    {
                        startX = linePacket.StartX,
                        startY = linePacket.StartY,
                        endX = linePacket.EndX,
                        endY = linePacket.EndY,
                        color = linePacket.Color
                    };
                    foldable.mapChart.chartData.lines.Add(line);
                }
            }

            foldable.mapChart.UpdateTexture();

            VerboseLogger.NavApply($"Map full sync applied, item={packet.ItemInstanceId}, lines={foldable.mapChart.chartData.lines.Count}");
        }

        // === Utility ===

        private PickupableItem FindItemByInstanceId(int instanceId)
        {
            // Find all SaveablePrefabs and match by instanceId
            var prefabs = FindObjectsOfType<SaveablePrefab>();
            foreach (var prefab in prefabs)
            {
                if (prefab.instanceId == instanceId)
                {
                    return prefab.GetComponent<PickupableItem>();
                }
            }
            return null;
        }

        /// <summary>
        /// Per-peer leave cleanup (host-only). A guest that crashes / P2P-drops mid-draw never sends its
        /// terminal MapTempLine(HasLine=false) or DisableMapCam, so (a) its draw lock stays held for the whole
        /// crew until a full reset, and (b) the in-progress temp line it was streaming stays painted on every
        /// OTHER guest's chart for the rest of the session (temp lines are relayed to bystander guests).
        /// For each map whose draw lock the leaver held: drop the lock, clear the host's own rendered temp line,
        /// and broadcast a terminal MapTempLine(clear) + MapDrawRelease so all remaining guests converge.
        /// Called from Plugin's OnPlayerLeft / OnDisconnected alongside the sleep/control per-peer cleanups.
        /// </summary>
        public void OnPeerLeft(SteamId leaver)
        {
            if (!Plugin.IsHost) return;

            // Collect first - we mutate _mapDrawingLocks in the loop below.
            List<int> heldByLeaver = new List<int>();
            foreach (var kvp in _mapDrawingLocks)
                if (kvp.Value == leaver.Value) heldByLeaver.Add(kvp.Key);
            if (heldByLeaver.Count == 0) return;

            foreach (var itemInstanceId in heldByLeaver)
            {
                _mapDrawingLocks.Remove(itemInstanceId);

                // Clear the host's own copy of the leaver's dangling temp line (the host may have been rendering it).
                var foldable = FindItemByInstanceId(itemInstanceId) as ShipItemFoldable;
                if (foldable?.mapChart?.chartData != null)
                {
                    foldable.mapChart.chartData.tempLine = null;
                    foldable.mapChart.UpdateTexture();
                }

                // Tell every remaining guest to clear the dangling temp line and free the lock (reliable; these
                // are one-shot terminal events, not the 5Hz stream).
                Plugin.NetworkManager.SendToAllReliable(PacketType.MapTempLine, w =>
                    PacketSerializer.WriteMapTempLine(w, new MapTempLinePacket { ItemInstanceId = itemInstanceId, HasLine = false }));
                Plugin.NetworkManager.SendToAllReliable(PacketType.MapDrawRelease, w =>
                    PacketSerializer.WriteMapDrawRelease(w, new MapDrawReleasePacket { ItemInstanceId = itemInstanceId }));

                VerboseLogger.NavEvent($"OnPeerLeft {leaver}: cleared dangling map temp-line + released draw lock for item {itemInstanceId}");
            }
        }

        public void Reset()
        {
            _mapDrawingLocks.Clear();
            _lastTempLineSyncTime = 0f;
        }
    }
}
