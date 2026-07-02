using HarmonyLib;
using SailwindCoop.Networking.Packets;
using SailwindCoop.Sync;
using UnityEngine;

namespace SailwindCoop.Patches
{
    /// <summary>
    /// Harmony patches for navigation item interactions.
    /// </summary>
    [HarmonyPatch]
    public static class NavigationPatches
    {
        // === Clock Lid ===

        [HarmonyPatch(typeof(ShipItemClock), "OnAltActivate")]
        [HarmonyPostfix]
        public static void ClockOnAltActivate_Postfix(ShipItemClock __instance)
        {
            if (!Plugin.IsMultiplayer) return;
            if (NavigationSyncManager.Instance?.IsApplyingRemoteState == true) return;

            // Check if this clock has a lid
            var lidField = typeof(ShipItemClock).GetField("lid",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var lid = lidField?.GetValue(__instance) as Transform;
            if (lid == null) return;

            // Get current lid state
            var lidOpenField = typeof(ShipItemClock).GetField("lidOpen",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            bool lidOpen = (bool)(lidOpenField?.GetValue(__instance) ?? false);

            var instanceId = __instance.GetComponent<SaveablePrefab>()?.instanceId ?? 0;
            if (instanceId == 0) return;

            NavigationSyncManager.Instance?.OnLocalNavItemStateChanged(
                instanceId,
                NavItemStateType.ClockLid,
                lidOpen ? 1f : 0f);
        }

        // === Quadrant Inspect ===

        [HarmonyPatch(typeof(ShipItemQuadrant), "OnAltActivate")]
        [HarmonyPostfix]
        public static void QuadrantOnAltActivate_Postfix(ShipItemQuadrant __instance)
        {
            if (!Plugin.IsMultiplayer) return;
            if (NavigationSyncManager.Instance?.IsApplyingRemoteState == true) return;

            var inspectingField = typeof(ShipItemQuadrant).GetField("inspecting",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            bool inspecting = (bool)(inspectingField?.GetValue(__instance) ?? false);

            var instanceId = __instance.GetComponent<SaveablePrefab>()?.instanceId ?? 0;
            if (instanceId == 0) return;

            NavigationSyncManager.Instance?.OnLocalNavItemStateChanged(
                instanceId,
                NavItemStateType.QuadrantInspect,
                inspecting ? 1f : 0f);
        }

        // === Spyglass Zoom ===

        [HarmonyPatch(typeof(ShipItemSpyglass), "OnScroll")]
        [HarmonyPostfix]
        public static void SpyglassOnScroll_Postfix(ShipItemSpyglass __instance)
        {
            if (!Plugin.IsMultiplayer) return;
            if (NavigationSyncManager.Instance?.IsApplyingRemoteState == true) return;

            var zoomField = typeof(ShipItemSpyglass).GetField("currentZoom",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            float zoom = (float)(zoomField?.GetValue(__instance) ?? 0f);

            var instanceId = __instance.GetComponent<SaveablePrefab>()?.instanceId ?? 0;
            if (instanceId == 0) return;

            NavigationSyncManager.Instance?.OnLocalNavItemStateChanged(
                instanceId,
                NavItemStateType.SpyglassZoom,
                zoom);
        }

        // === Compass Latitude ===

        [HarmonyPatch(typeof(ShipItemCompass), "OnScroll")]
        [HarmonyPostfix]
        public static void CompassOnScroll_Postfix(ShipItemCompass __instance, float input)
        {
            if (!Plugin.IsMultiplayer) return;
            if (NavigationSyncManager.Instance?.IsApplyingRemoteState == true) return;
            if (__instance.chronoLatitude == null) return;

            var currentRotField = typeof(ChronometerLatitude).GetField("currentRot",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            float currentRot = (float)(currentRotField?.GetValue(__instance.chronoLatitude) ?? -28f);

            // B8: vanilla ChronometerLatitude.Update applies the +/-0.5 step NEXT frame (OnScroll only sets
            // currentInput); reading currentRot here returns the PRE-scroll value, so the remote dial lagged a
            // notch and the final setting was never sent. Anticipate Update's result (same +/-0.5 + clamp to
            // [-45,-11]) so we ship the value the dial will actually settle on. Idempotent under multiple scrolls
            // in one frame (Update steps once; each call computes the same anticipated value off the unchanged rot).
            if (input > 0f) currentRot += 0.5f;
            else if (input < 0f) currentRot -= 0.5f;
            currentRot = Mathf.Clamp(currentRot, -45f, -11f);

            var instanceId = __instance.GetComponent<SaveablePrefab>()?.instanceId ?? 0;
            if (instanceId == 0) return;

            NavigationSyncManager.Instance?.OnLocalNavItemStateChanged(
                instanceId,
                NavItemStateType.CompassLatitude,
                currentRot);
        }

        // === Map Fold ===

        [HarmonyPatch(typeof(ShipItemFoldable), "OnAltActivate")]
        [HarmonyPostfix]
        public static void MapOnAltActivate_Postfix(ShipItemFoldable __instance)
        {
            if (!Plugin.IsMultiplayer) return;
            if (NavigationSyncManager.Instance?.IsApplyingRemoteState == true) return;

            var instanceId = __instance.GetComponent<SaveablePrefab>()?.instanceId ?? 0;
            if (instanceId == 0) return;

            // amount > 0 means folded
            bool isFolded = __instance.amount > 0f;

            NavigationSyncManager.Instance?.OnLocalMapFoldStateChanged(instanceId, isFolded);
        }

        // === Map Drawing ===

        /// <summary>
        /// Guest intercepts map click to request permission before entering drawing mode.
        /// </summary>
        [HarmonyPatch(typeof(ShipItemFoldable), "OnItemClick")]
        [HarmonyPrefix]
        public static bool MapOnItemClick_Prefix(ShipItemFoldable __instance, PickupableItem heldItem, ref bool __result)
        {
            if (!Plugin.IsMultiplayer) return true;
            if (Plugin.IsHost) return true; // Host handles locally

            // Only intercept if this is an ink set click on a chartable map
            if (!__instance.allowCharting) return true;
            if (__instance.amount >= 1f) return true; // Map is folded
            if (heldItem?.GetType() != typeof(ShipItemInkSet)) return true;

            var prefab = __instance.GetComponent<SaveablePrefab>();
            if (prefab == null || prefab.instanceId == 0) return true;

            // Request drawing permission from host
            NavigationSyncManager.Instance?.RequestMapDrawing(prefab.instanceId, prefab.prefabIndex);

            // Store pending request info for response handling
            _pendingMapDrawRequest = prefab.instanceId;
            _pendingMapItem = __instance;
            _pendingInkSet = heldItem;

            __result = false;
            return false; // Don't execute original - wait for response
        }

        private static int _pendingMapDrawRequest;
        private static ShipItemFoldable _pendingMapItem;
        private static PickupableItem _pendingInkSet;

        /// <summary>
        /// Called when guest receives drawing permission response.
        /// </summary>
        public static void OnMapDrawResponseReceived(MapDrawResponsePacket packet)
        {
            // Draw-lock guard: the GRANT is broadcast, so a 2nd guest that was DENIED must ignore a
            // response addressed to a DIFFERENT requester - otherwise it would open the draw cam off
            // someone else's grant. (RequesterSteamId==0 is a legacy/host-origin response - don't filter.)
            if (packet.RequesterSteamId != 0 && packet.RequesterSteamId != Steamworks.SteamClient.SteamId) return;
            if (packet.ItemInstanceId != _pendingMapDrawRequest) return;
            if (!packet.Granted)
            {
                _pendingMapDrawRequest = 0;
                _pendingMapItem = null;
                _pendingInkSet = null;
                return;
            }

            // Permission granted - proceed with opening map camera
            if (_pendingMapItem != null && _pendingInkSet != null)
            {
                int kitPos = 0;
                Vector3 localKitPos = _pendingMapItem.transform.InverseTransformPoint(_pendingInkSet.transform.position);
                if (localKitPos.x > 0f) kitPos = 1;
                if (localKitPos.x < 0f) kitPos = -1;

                MapTableCamera.instance.EnableMapCam(_pendingMapItem, _pendingInkSet, kitPos);
            }

            _pendingMapDrawRequest = 0;
            _pendingMapItem = null;
            _pendingInkSet = null;
        }

        /// <summary>
        /// Host notifies when entering drawing mode.
        /// </summary>
        [HarmonyPatch(typeof(MapTableCamera), "EnableMapCam")]
        [HarmonyPostfix]
        public static void MapTableCamera_EnableMapCam_Postfix(ShipItem lookAtMap)
        {
            if (!Plugin.IsMultiplayer) return;
            if (lookAtMap == null) return;

            var instanceId = lookAtMap.GetComponent<SaveablePrefab>()?.instanceId ?? 0;
            if (instanceId == 0) return;

            if (Plugin.IsHost)
            {
                NavigationSyncManager.Instance?.LockMapForDrawing(instanceId);
            }
        }

        /// <summary>
        /// Notify when exiting drawing mode.
        /// </summary>
        [HarmonyPatch(typeof(MapTableCamera), "DisableMapCam")]
        [HarmonyPostfix]
        public static void MapTableCamera_DisableMapCam_Postfix(MapTableCamera __instance)
        {
            if (!Plugin.IsMultiplayer) return;

            // Get the map that was being used (currentMap is about to be nulled)
            var currentMapField = typeof(MapTableCamera).GetField("currentMap",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            var currentMap = currentMapField?.GetValue(__instance) as ShipItem;

            // currentMap may already be null in postfix, use stored reference
            // Actually this runs after, so need to track it differently
        }

        /// <summary>
        /// Track map before DisableMapCam clears it.
        /// </summary>
        [HarmonyPatch(typeof(MapTableCamera), "DisableMapCam")]
        [HarmonyPrefix]
        public static void MapTableCamera_DisableMapCam_Prefix(MapTableCamera __instance)
        {
            if (!Plugin.IsMultiplayer) return;
            if (__instance.currentMap == null) return;

            var instanceId = __instance.currentMap.GetComponent<SaveablePrefab>()?.instanceId ?? 0;
            if (instanceId == 0) return;

            NavigationSyncManager.Instance?.ReleaseMapDrawing(instanceId);
            NavigationSyncManager.Instance?.OnLocalMapTempLineCleared(instanceId);
        }

        /// <summary>
        /// Sync when line is committed.
        /// </summary>
        [HarmonyPatch(typeof(MapChart), "OnActivate")]
        [HarmonyPostfix]
        public static void MapChart_OnActivate_Postfix(MapChart __instance)
        {
            if (!Plugin.IsMultiplayer) return;

            // Check if we just finished a line (drawingLine is false after commit)
            var drawingLineField = typeof(MapChart).GetField("drawingLine",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            bool isDrawing = (bool)(drawingLineField?.GetValue(__instance) ?? false);

            // B7: vanilla MapChart.OnActivate only appends to chartData.lines when currentLineColor > 0; a
            // protractor/eraser click (color 0) commits NO new line, so re-sending lines[last] here would
            // duplicate the previous committed line on every peer. Only proceed for a real ink draw.
            if (MapTableCamera.instance == null || MapTableCamera.instance.currentLineColor <= 0) return;

            if (!isDrawing && __instance.chartData?.lines?.Count > 0)
            {
                // A line was just committed.
                // B6: MapChart.LateUpdate reparents the chart to the map cam for the whole drawing session, so
                // __instance.transform.parent is the CAMERA here, not the foldable (instanceId would be 0 and the
                // line would never sync). Resolve the owning foldable from the active map instead (currentMap is
                // the ShipItem passed to EnableMapCam == the foldable being drawn on).
                var foldable = MapTableCamera.instance.currentMap as ShipItemFoldable;
                var instanceId = foldable?.GetComponent<SaveablePrefab>()?.instanceId ?? 0;
                if (instanceId == 0) return;

                var lastLine = __instance.chartData.lines[__instance.chartData.lines.Count - 1];
                NavigationSyncManager.Instance?.OnLocalMapLineAdded(instanceId, lastLine);
            }
        }

        /// <summary>
        /// Sync temp line while drawing.
        /// </summary>
        [HarmonyPatch(typeof(MapChart), "OnLook")]
        [HarmonyPostfix]
        public static void MapChart_OnLook_Postfix(MapChart __instance)
        {
            if (!Plugin.IsMultiplayer) return;

            var drawingLineField = typeof(MapChart).GetField("drawingLine",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            bool isDrawing = (bool)(drawingLineField?.GetValue(__instance) ?? false);

            if (!isDrawing) return;

            // B6: resolve the foldable from the active map, not transform.parent (reparented to the cam during
            // the drawing session, so the parent lookup returns the camera and the temp line never syncs).
            var foldable = MapTableCamera.instance?.currentMap as ShipItemFoldable;
            var instanceId = foldable?.GetComponent<SaveablePrefab>()?.instanceId ?? 0;
            if (instanceId == 0) return;

            NavigationSyncManager.Instance?.OnLocalMapTempLineChanged(instanceId, __instance.chartData?.tempLine);
        }
    }
}
