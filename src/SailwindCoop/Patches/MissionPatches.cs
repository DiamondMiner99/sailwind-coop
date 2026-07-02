using HarmonyLib;
using SailwindCoop.Sync;
using UnityEngine;

namespace SailwindCoop.Patches
{
    /// <summary>
    /// Harmony patches for mission synchronization.
    /// Host: broadcast after mission operations.
    /// Guest: intercept and route to host.
    /// </summary>
    public static class MissionPatches
    {
        #region Host Broadcast Patches

        /// <summary>
        /// After host accepts mission, broadcast to guest.
        /// </summary>
        [HarmonyPatch(typeof(PlayerMissions), nameof(PlayerMissions.AcceptMission))]
        public static class AcceptMissionPatch
        {
            [HarmonyPostfix]
            public static void Postfix(Mission mission)
            {
                Debug.VerboseLogger.Log("MISSION", "PATCH", $"AcceptMission fired, mission={mission?.missionName ?? "null"}");
                if (!Plugin.IsMultiplayer || !Plugin.IsHost) return;

                MissionSyncManager.Instance?.BroadcastMissionAccepted(mission);
            }
        }

        /// <summary>
        /// After mission good is registered (spawned), broadcast to guest.
        /// This hooks into Mission.RegisterGood which is called from Port.DoSpawnGoods.
        /// </summary>
        [HarmonyPatch(typeof(Mission), nameof(Mission.RegisterGood))]
        public static class MissionRegisterGoodPatch
        {
            [HarmonyPostfix]
            public static void Postfix(Mission __instance, GameObject good)
            {
                if (!Plugin.IsMultiplayer || !Plugin.IsHost) return;

                // Broadcast item spawn to guest
                var shipItem = good?.GetComponent<ShipItem>();
                if (shipItem != null)
                {
                    // Ensure item has instanceId before syncing.
                    // Newly instantiated items have instanceId=0 until RegisterToSave() is called.
                    // Without this, all mission crates sync with id=0, causing guest to skip
                    // "already exists" items after the first one.
                    //
                    // Note: Don't call RegisterToSave() directly as it may throw if
                    // SaveLoadManager.instance is null during early spawning.
                    var prefab = good.GetComponent<SaveablePrefab>();
                    if (prefab != null && prefab.instanceId == 0)
                    {
                        // Assign unique ID directly for network sync
                        prefab.instanceId = UnityEngine.Random.Range(1, int.MaxValue);
                        Debug.VerboseLogger.Log("MISSION", "PATCH",
                            $"Assigned instanceId={prefab.instanceId} to mission good {good.name}");
                    }

                    ItemSyncManager.Instance?.OnLocalItemSpawned(shipItem);
                }
            }
        }

        /// <summary>
        /// After host completes mission, broadcast to guest.
        /// </summary>
        [HarmonyPatch(typeof(PlayerMissions), nameof(PlayerMissions.CompleteMission))]
        public static class CompleteMissionPatch
        {
            // Capture the mission name in a PREFIX, before vanilla CompleteMission nulls the slot,
            // so the guest can show "Mission complete:\n<missionName>" matching the host.
            [HarmonyPrefix]
            public static void Prefix(int missionIndex, out string __state)
            {
                __state = null;
                if (!Plugin.IsMultiplayer || !Plugin.IsHost) return;
                var m = (PlayerMissions.missions != null && missionIndex >= 0 && missionIndex < PlayerMissions.missions.Length)
                    ? PlayerMissions.missions[missionIndex] : null;
                __state = m?.missionName;
            }

            [HarmonyPostfix]
            public static void Postfix(int missionIndex, string __state)
            {
                if (!Plugin.IsMultiplayer || !Plugin.IsHost) return;

                MissionSyncManager.Instance?.BroadcastMissionCompleted(missionIndex, __state);
            }
        }

        /// <summary>
        /// True while PlayerMissions.AbandonMission is executing. Mission.AbandonMission loops its cargo goods
        /// and calls ShipItem.DestroyItem() on each; those goods sit on a distant PORT DOCK (sold,
        /// currentWalkCol==null), so abandoning from far away (the mission log opens anywhere) they otherwise
        /// match the co-op out-of-range-cull SUPPRESSION gate in ItemPatches.OnDestroyItem and would be wrongly
        /// KEPT ALIVE - leaving orphaned mission crates that belong to no mission. This flag tells that gate
        /// "this is an INTENTIONAL abandon destroy - let it through and broadcast", so only the genuine vanilla
        /// >600m LOD cull is suppressed. Guards the race between the cull-suppression gate and an abandon's
        /// legitimate remote destroys of distant dock cargo.
        /// </summary>
        public static bool AbandoningMission;

        /// <summary>
        /// After host abandons mission, broadcast to guest. Also brackets the call with AbandoningMission so the
        /// out-of-range-cull suppression doesn't swallow the cargo-good destroys.
        /// </summary>
        [HarmonyPatch(typeof(PlayerMissions), nameof(PlayerMissions.AbandonMission))]
        public static class AbandonMissionPatch
        {
            [HarmonyPrefix]
            public static void Prefix() => AbandoningMission = true;

            [HarmonyPostfix]
            public static void Postfix(int missionIndex)
            {
                if (!Plugin.IsMultiplayer || !Plugin.IsHost) return;

                MissionSyncManager.Instance?.BroadcastMissionAbandoned(missionIndex);
            }

            // Finalizer guarantees the flag clears even if AbandonMission throws.
            [HarmonyFinalizer]
            public static void Finalizer() => AbandoningMission = false;
        }

        /// <summary>
        /// After good is delivered, broadcast progress.
        /// </summary>
        [HarmonyPatch(typeof(Mission), nameof(Mission.DeliverGood))]
        public static class DeliverGoodPatch
        {
            [HarmonyPostfix]
            public static void Postfix(Mission __instance)
            {
                if (!Plugin.IsMultiplayer || !Plugin.IsHost) return;

                // Carry the good name + total count so guests show the SAME specific toast as the host.
                string goodName = __instance.goodPrefab?.GetComponent<ShipItem>()?.name ?? "";
                MissionSyncManager.Instance?.BroadcastMissionProgress(
                    __instance.missionIndex,
                    __instance.GetDeliveredCount(),
                    goodName,
                    __instance.goodCount);
            }
        }

        /// <summary>
        /// Force-propagate the destroy of a mission good when the HOST delivers it.
        /// Vanilla Good.Deliver() (set Untagged -> Mission.DeliverGood() -> ShipItem.DestroyItem()) destroys the
        /// crate, but if a guest had dropped that same crate within the last 2s, OnLocalItemDestroyed's
        /// recently-synced guard SUPPRESSES the ItemDestroyed broadcast -> the crate persists on every guest ("two crates on
        /// clients, zero on host"). Capture the instanceId before the destroy (Prefix), then force-broadcast it
        /// past the guard (Postfix). Host-only; the guest's own deliveries are intercepted upstream
        /// (PortDudeDeliveryPatch) so Good.Deliver only runs host-side for mission cargo.
        /// </summary>
        [HarmonyPatch(typeof(Good), nameof(Good.Deliver))]
        public static class GoodDeliverPatch
        {
            [HarmonyPrefix]
            public static void Prefix(Good __instance, out int __state)
            {
                __state = -1;
                if (!Plugin.IsMultiplayer || !Plugin.IsHost || __instance == null) return;
                var prefab = __instance.GetComponent<SaveablePrefab>();
                if (prefab != null) __state = prefab.instanceId;
            }

            [HarmonyPostfix]
            public static void Postfix(int __state)
            {
                if (!Plugin.IsMultiplayer || !Plugin.IsHost) return;
                if (__state > 0) // >0 only: id==0 is an unregistered prefab (invalid to broadcast); -1 is the prefix's skip sentinel
                    ItemSyncManager.Instance?.ForceBroadcastItemDestroyed(__state);
            }
        }

        #endregion

        #region Guest Interception Patches

        /// <summary>
        /// Intercept guest delivery and route to host.
        /// </summary>
        [HarmonyPatch(typeof(PortDude), "OnTriggerEnter")]
        public static class PortDudeDeliveryPatch
        {
            [HarmonyPrefix]
            public static bool Prefix(PortDude __instance, Collider other)
            {
                // Only intercept for guest
                if (!Plugin.IsMultiplayer || Plugin.IsHost) return true;

                // Check if it's mission cargo
                if (other.CompareTag("Player")) return true;
                if (!other.CompareTag("Good")) return true;

                var good = other.GetComponent<Good>();
                if (good == null) return true;

                var mission = good.GetAssignedMission();
                if (mission == null) return true;

                var port = __instance.GetPort();
                if (mission.destinationPort != port)
                {
                    // Wrong port - let original handle notification
                    return true;
                }

                // Correct port - send delivery request to host instead of local delivery
                var shipItem = other.GetComponent<ShipItem>();
                if (shipItem == null)
                {
                    Plugin.Log.LogWarning("Good has no ShipItem component");
                    return true;
                }
                var prefab = shipItem.GetComponent<SaveablePrefab>();
                if (prefab == null)
                {
                    Plugin.Log.LogWarning("Good has no SaveablePrefab component");
                    return true;
                }
                MissionSyncManager.Instance?.RequestDeliverGood(prefab.instanceId, prefab.prefabIndex, port.portIndex);

                // Skip original method
                return false;
            }
        }

        /// <summary>
        /// Intercept guest clicking "Accept Mission" button and route to host.
        /// This patches GPButtonSetMission which calls MissionDetailsUI.ClickButton().
        /// We intercept at this level to prevent the button click from proceeding.
        /// </summary>
        [HarmonyPatch(typeof(GPButtonSetMission), nameof(GPButtonSetMission.OnActivate))]
        public static class AcceptMissionButtonPatch
        {
            [HarmonyPrefix]
            public static bool Prefix(GPButtonSetMission __instance)
            {
                // Only intercept for guest
                if (!Plugin.IsMultiplayer || Plugin.IsHost) return true;

                // Get the MissionDetailsUI from the button
                var missionUI = Traverse.Create(__instance).Field("missionUI").GetValue<MissionDetailsUI>();
                if (missionUI == null) return true;

                // Get the current mission from MissionDetailsUI
                var mission = Traverse.Create(missionUI).Field("currentMission").GetValue<Mission>();
                if (mission == null) return true;

                // Check if this is an accept (missionIndex == -1) or abandon action
                if (mission.missionIndex != -1)
                {
                    // This is an abandon action - send abandon request
                    MissionSyncManager.Instance?.RequestAbandonMission(mission.missionIndex);
                    NotificationUi.instance?.ShowNotification("Abandoning mission...");
                    return false;
                }

                // This is an accept action - get port and board slot info
                var port = mission.originPort;
                if (port == null) return true;

                // Get page and world missions state from MissionListUI
                var missionListUI = MissionListUI.instance;
                if (missionListUI == null)
                {
                    Plugin.Log.LogWarning("Could not find MissionListUI instance");
                    return true;
                }

                var page = Traverse.Create(missionListUI).Field("currentPage").GetValue<int>();
                var isWorld = missionListUI.worldMissions;

                // Find board slot by matching mission reference in the button list
                var missionButtons = Traverse.Create(missionListUI).Field("missionButtons").GetValue<GPButtonListedMission[]>();
                if (missionButtons == null) return true;

                int boardSlot = -1;
                for (int i = 0; i < missionButtons.Length; i++)
                {
                    if (missionButtons[i]?.mission == mission)
                    {
                        boardSlot = i;
                        break;
                    }
                }

                if (boardSlot == -1)
                {
                    Plugin.Log.LogWarning("Could not determine board slot for mission");
                    return true;
                }

                // Send request to host
                MissionSyncManager.Instance?.RequestAcceptMission(port.portIndex, boardSlot, page, isWorld);

                // Show feedback to guest
                NotificationUi.instance?.ShowNotification("Requesting mission...");

                // Skip original method (don't accept locally)
                return false;
            }
        }

        /// <summary>
        /// Intercept mission board UI opening for guest.
        /// Guest requests mission list from host instead of generating locally.
        /// </summary>
        [HarmonyPatch(typeof(MissionListUI), nameof(MissionListUI.EnablePortMissionUI))]
        public static class MissionBoardUIPatch
        {
            [HarmonyPrefix]
            public static bool Prefix(MissionListUI __instance, Mission[] missions, Transform missionTableAnchor, PortDude dude)
            {
                var portIndex = dude?.GetPort()?.portIndex ?? -1;
                Debug.VerboseLogger.Log("MISSION", "PATCH", $"EnablePortMissionUI called, isMultiplayer={Plugin.IsMultiplayer}, isHost={Plugin.IsHost}, port={portIndex}, missionCount={missions?.Length ?? 0}");

                // Only intercept for guest
                if (!Plugin.IsMultiplayer || Plugin.IsHost) return true;

                Debug.VerboseLogger.Log("MISSION", "PATCH", "Guest intercepted, requesting from host");

                // Store context for when response arrives
                MissionSyncManager.Instance?.SetPendingMissionBoardContext(__instance, missionTableAnchor, dude);

                // Request missions from host
                var port = dude.GetPort();
                MissionSyncManager.Instance?.RequestMissionBoard(port.portIndex, 0, false);

                // Block original - we'll call it when response arrives
                return false;
            }
        }

        /// <summary>
        /// Intercept page change for guest to request from host.
        /// </summary>
        [HarmonyPatch(typeof(MissionListUI), nameof(MissionListUI.ChangePage))]
        public static class MissionBoardPageChangePatch
        {
            [HarmonyPrefix]
            public static bool Prefix(MissionListUI __instance, int pageChange)
            {
                // Only intercept for guest
                if (!Plugin.IsMultiplayer || Plugin.IsHost) return true;

                // Only when in port mission list mode
                if (!GameState.inPortMissionList) return true;

                var currentPortDude = Traverse.Create(__instance).Field("currentPortDude").GetValue<PortDude>();
                if (currentPortDude == null) return true;

                var currentPage = Traverse.Create(__instance).Field("currentPage").GetValue<int>();
                var currentPageCount = Traverse.Create(__instance).Field("currentPageCount").GetValue<int>();

                int newPage = currentPage + pageChange;
                if (newPage < 0 || newPage > currentPageCount - 1)
                {
                    // Out of bounds - let original handle (it won't change)
                    return true;
                }

                // Update page locally first
                Traverse.Create(__instance).Field("currentPage").SetValue(newPage);

                // Request new page from host
                var port = currentPortDude.GetPort();
                MissionSyncManager.Instance?.RequestMissionBoard(port.portIndex, newPage, __instance.worldMissions);

                // Play sound
                UISoundPlayer.instance?.PlayParchmentSound();

                // Update page text
                var pageCountText = Traverse.Create(__instance).Field("pageCountText").GetValue<TextMesh>();
                if (pageCountText != null)
                {
                    pageCountText.text = $"{newPage + 1} / {currentPageCount}";
                }

                // Block original
                return false;
            }
        }

        /// <summary>
        /// Intercept world/local mission toggle for guest to request from host.
        /// </summary>
        [HarmonyPatch(typeof(MissionListUI), nameof(MissionListUI.ResetPage))]
        public static class MissionBoardResetPagePatch
        {
            [HarmonyPrefix]
            public static bool Prefix(MissionListUI __instance)
            {
                // Only intercept for guest
                if (!Plugin.IsMultiplayer || Plugin.IsHost) return true;

                // Only when in port mission list mode
                if (!GameState.inPortMissionList) return true;

                var currentPortDude = Traverse.Create(__instance).Field("currentPortDude").GetValue<PortDude>();
                if (currentPortDude == null) return true;

                // Reset page to 0 locally
                Traverse.Create(__instance).Field("currentPage").SetValue(0);

                // Request page 0 from host
                var port = currentPortDude.GetPort();
                MissionSyncManager.Instance?.RequestMissionBoard(port.portIndex, 0, __instance.worldMissions);

                // Play sound
                UISoundPlayer.instance?.PlayParchmentSound();

                // Block original
                return false;
            }
        }

        // NOTE: do NOT freeze the player while the port mission list is open (e.g. to match the trade
        // menu's Refs.SetPlayerControl(false)). That approach disables charController, which kills gravity
        // (freeze mid-jump) and, more dangerously, can stop the player tracking a moving deck - the same
        // left-behind-at-sea risk that rules out a co-op pause freeze. Movement in cursor menus is left
        // enabled everywhere; only the vanilla trade menu freezes (always at a stationary port).

        #endregion
    }
}
