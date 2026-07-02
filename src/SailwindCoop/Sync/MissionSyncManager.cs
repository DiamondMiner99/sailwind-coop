using UnityEngine;
using HarmonyLib;
using SailwindCoop.Debug;
using SailwindCoop.Networking.Packets;
using Steamworks;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Manages synchronization of missions between host and guest.
    /// Host is authoritative for all mission operations.
    /// </summary>
    public class MissionSyncManager : MonoBehaviour
    {
        public static MissionSyncManager Instance { get; private set; }

        private void Awake()
        {
            if (Instance != null)
            {
                Destroy(this);
                return;
            }
            Instance = this;
        }

        #region Helper Methods

        /// <summary>
        /// Convert Mission to NetworkMissionData for network transfer.
        /// </summary>
        public static NetworkMissionData MissionToNetworkData(Mission mission, int slotIndex)
        {
            if (mission == null)
            {
                return new NetworkMissionData { SlotIndex = slotIndex, IsValid = false };
            }

            return new NetworkMissionData
            {
                SlotIndex = slotIndex,
                IsValid = true,
                OriginPortIndex = mission.originPort.portIndex,
                DestinationPortIndex = mission.destinationPort.portIndex,
                GoodPrefabIndex = mission.goodPrefab.GetComponent<SaveablePrefab>().prefabIndex,
                GoodCount = mission.goodCount,
                DeliveredCount = mission.GetDeliveredCount(),
                TotalPrice = mission.totalPrice,
                InsuranceLevel = mission.insuranceLevel,
                Distance = mission.distance,
                DueDay = mission.dueDay
            };
        }

        /// <summary>
        /// Reconstruct Mission from NetworkMissionData.
        /// </summary>
        public static Mission NetworkDataToMission(NetworkMissionData data)
        {
            if (!data.IsValid) return null;

            var saveData = new SaveMissionData(
                data.SlotIndex,
                data.OriginPortIndex,
                data.DestinationPortIndex,
                data.GoodPrefabIndex,
                data.GoodCount,
                data.TotalPrice,
                data.InsuranceLevel,
                data.Distance,
                data.DeliveredCount,
                data.DueDay
            );

            return new Mission(saveData);
        }

        #endregion

        #region Host Methods

        /// <summary>
        /// Send full mission state to all guests (on join).
        /// </summary>
        public void SendFullStateImmediate()
        {
            if (!Plugin.IsHost) return;

            var packet = new MissionStateSyncPacket
            {
                Missions = new NetworkMissionData[5]
            };

            for (int i = 0; i < 5; i++)
            {
                var mission = PlayerMissions.missions?[i];
                packet.Missions[i] = MissionToNetworkData(mission, i);
            }

            VerboseLogger.Log("MISSION", "SEND", $"FullState, count={PlayerMissions.GetMissionCount()}");

            Plugin.NetworkManager.SendToAllReliable(PacketType.MissionStateSync, w =>
                PacketSerializer.WriteMissionStateSync(w, packet));
        }

        /// <summary>
        /// Send full mission state to ONE joining guest (N-player Phase 3 targeted join resync).
        /// Identical payload to SendFullStateImmediate, sent only to the joiner so already-settled
        /// guests aren't re-synced. At N=1 the target is the only peer, so this == the broadcast.
        /// </summary>
        public void SendFullStateTo(SteamId target)
        {
            if (!Plugin.IsHost) return;

            var packet = new MissionStateSyncPacket
            {
                Missions = new NetworkMissionData[5]
            };

            for (int i = 0; i < 5; i++)
            {
                var mission = PlayerMissions.missions?[i];
                packet.Missions[i] = MissionToNetworkData(mission, i);
            }

            VerboseLogger.Log("MISSION", "SEND", $"FullState (to {target}), count={PlayerMissions.GetMissionCount()}");

            Plugin.NetworkManager.SendReliable(target, PacketType.MissionStateSync, w =>
                PacketSerializer.WriteMissionStateSync(w, packet));
        }

        /// <summary>
        /// Broadcast that a mission was accepted. Called after PlayerMissions.AcceptMission.
        /// </summary>
        public void BroadcastMissionAccepted(Mission mission)
        {
            if (!Plugin.IsHost || !Plugin.IsMultiplayer) return;

            var packet = new MissionAcceptedPacket
            {
                Mission = MissionToNetworkData(mission, mission.missionIndex)
            };

            VerboseLogger.Log("MISSION", "SEND", $"Accepted, slot={mission.missionIndex}, name={mission.missionName}");

            Plugin.NetworkManager.SendToAllReliable(PacketType.MissionAccepted, w =>
                PacketSerializer.WriteMissionAccepted(w, packet));
        }

        /// <summary>
        /// Broadcast mission progress. Called after Good.DeliverGood.
        /// </summary>
        public void BroadcastMissionProgress(int slotIndex, int deliveredCount, string goodName, int goodCount)
        {
            if (!Plugin.IsHost || !Plugin.IsMultiplayer) return;

            var packet = new MissionProgressPacket
            {
                SlotIndex = slotIndex,
                DeliveredCount = deliveredCount,
                GoodName = goodName ?? "",
                GoodCount = goodCount
            };

            VerboseLogger.Log("MISSION", "SEND", $"Progress, slot={slotIndex}, delivered={deliveredCount}/{goodCount}, good={goodName}");

            Plugin.NetworkManager.SendToAllReliable(PacketType.MissionProgress, w =>
                PacketSerializer.WriteMissionProgress(w, packet));
        }

        /// <summary>
        /// Broadcast mission completed. Called after PlayerMissions.CompleteMission.
        /// </summary>
        public void BroadcastMissionCompleted(int slotIndex, string missionName)
        {
            if (!Plugin.IsHost || !Plugin.IsMultiplayer) return;

            var packet = new MissionEndedPacket { SlotIndex = slotIndex, MissionName = missionName ?? "" };

            VerboseLogger.Log("MISSION", "SEND", $"Completed, slot={slotIndex}, mission={missionName}");

            Plugin.NetworkManager.SendToAllReliable(PacketType.MissionCompleted, w =>
                PacketSerializer.WriteMissionEnded(w, packet));
        }

        /// <summary>
        /// Broadcast mission abandoned. Called after PlayerMissions.AbandonMission.
        /// </summary>
        public void BroadcastMissionAbandoned(int slotIndex)
        {
            if (!Plugin.IsHost || !Plugin.IsMultiplayer) return;

            var packet = new MissionEndedPacket { SlotIndex = slotIndex };

            VerboseLogger.Log("MISSION", "SEND", $"Abandoned, slot={slotIndex}");

            Plugin.NetworkManager.SendToAllReliable(PacketType.MissionAbandoned, w =>
                PacketSerializer.WriteMissionEnded(w, packet));
        }

        /// <summary>
        /// Handle guest request to accept mission.
        /// </summary>
        public void OnMissionAcceptRequestReceived(SteamId sender, MissionAcceptRequestPacket packet)
        {
            if (!Plugin.IsHost) return;

            VerboseLogger.Log("MISSION", "RECV", $"AcceptRequest from {sender}, port={packet.PortIndex}, slot={packet.BoardSlot}");

            // Validate request
            if (PlayerMissions.MissionsFull())
            {
                Plugin.Log.LogWarning("Guest tried to accept mission but log is full");
                NotificationUi.instance?.ShowNotification("Mission log full!");
                return;
            }

            // Get port and generate missions to find the one guest wants
            if (packet.PortIndex < 0 || packet.PortIndex >= Port.ports.Length)
            {
                Plugin.Log.LogError($"Port index {packet.PortIndex} out of bounds");
                return;
            }

            var port = Port.ports[packet.PortIndex];
            if (port == null)
            {
                Plugin.Log.LogError($"Invalid port index {packet.PortIndex}");
                return;
            }

            var missions = port.GetMissions(packet.Page, packet.IsWorldMission);
            if (packet.BoardSlot >= missions.Length || missions[packet.BoardSlot] == null)
            {
                Plugin.Log.LogWarning("Mission no longer available");
                NotificationUi.instance?.ShowNotification("Mission no longer available");
                return;
            }

            var mission = missions[packet.BoardSlot];

            // Accept the mission (this triggers our postfix to broadcast)
            PlayerMissions.AcceptMission(mission);

            // The accept was on a GUEST's behalf, so no vanilla MissionDetailsUI.ClickButton ran on the host -
            // the host's own open board would keep showing the now-consumed mission until close+reopen. Re-display
            // it from local data so the offered list drops the accepted mission. (Other guests refresh via the
            // broadcast -> OnMissionAccepted; the requesting guest refreshes there too. This is the host's screen.)
            RefreshHostMissionBoardIfOpen();
        }

        /// <summary>Host-only: re-render the host's OWN open port mission board from local data (GetMissions),
        /// for when a guest's accept was applied here without a vanilla ClickButton to refresh it. Guests refresh
        /// via RequestMissionBoard instead (they have no local board data); the host does, so it re-displays
        /// directly. No-op if the board isn't open.</summary>
        private void RefreshHostMissionBoardIfOpen()
        {
            if (!GameState.inPortMissionList || MissionListUI.instance == null) return;
            var t = HarmonyLib.Traverse.Create(MissionListUI.instance);
            var dude = t.Field("currentPortDude").GetValue<PortDude>();
            if (dude != null && dude.GetPort() != null)
            {
                int page = t.Field("currentPage").GetValue<int>();
                var missions = dude.GetPort().GetMissions(page, MissionListUI.instance.worldMissions);
                MissionListUI.instance.DisplayMissions(missions);
            }
            if (MissionDetailsUI.instance != null)
                HarmonyLib.Traverse.Create(MissionDetailsUI.instance).Field("UI").GetValue<GameObject>()?.SetActive(false);
        }

        /// <summary>Re-render the local player's OPEN captain's-log mission list (the accepted-missions
        /// "current missions" tab) so an abandon applied here WITHOUT a vanilla button click drops the gone
        /// mission immediately instead of staying stale until the log is closed+reopened. The host applies a
        /// guest's relayed abandon (no vanilla MissionDetailsUI.ClickButton ran), and a guest applies the
        /// host's MissionAbandoned broadcast (its own abandon click was blocked + routed) - neither path
        /// refreshes the open log. No-op unless the captain's log book is open ON the current-missions tab;
        /// deliberately skips the PORT mission board (GameState.inPortMissionList), which has its own refresh
        /// path. Mirrors the accept-side refresh. MP-only (callers are role-gated), so solo never reaches it.</summary>
        private static void RefreshOpenMissionLog()
        {
            var ui = MissionListUI.instance;
            if (ui == null) return;
            var t = HarmonyLib.Traverse.Create(ui);
            if (!t.Field("UIActive").GetValue<bool>()) return;      // captain's log not open
            if (GameState.inPortMissionList) return;                // that's the port board, not the log
            var currentTab = t.Field("currentMissionsUI").GetValue<GameObject>();
            if (currentTab == null || !currentTab.activeSelf) return; // log open but on a different tab (history/rep/...)
            ui.DisplayMissions(PlayerMissions.missions);            // re-render accepted missions; drops the abandoned slot
            if (MissionDetailsUI.instance != null)                 // dismiss the stale details popup of the gone mission
                HarmonyLib.Traverse.Create(MissionDetailsUI.instance).Field("UI").GetValue<GameObject>()?.SetActive(false);
        }

        /// <summary>
        /// Handle guest request to abandon mission.
        /// </summary>
        public void OnMissionAbandonRequestReceived(SteamId sender, MissionAbandonRequestPacket packet)
        {
            if (!Plugin.IsHost) return;

            VerboseLogger.Log("MISSION", "RECV", $"AbandonRequest from {sender}, slot={packet.SlotIndex}");

            if (packet.SlotIndex < 0 || packet.SlotIndex >= 5)
            {
                Plugin.Log.LogError($"Invalid mission slot {packet.SlotIndex}");
                return;
            }

            if (PlayerMissions.missions?[packet.SlotIndex] == null)
            {
                Plugin.Log.LogWarning($"No mission in slot {packet.SlotIndex}");
                return;
            }

            // Abandon the mission (this triggers our postfix to broadcast)
            PlayerMissions.AbandonMission(packet.SlotIndex);

            // The abandon was on a GUEST's behalf, so no vanilla MissionDetailsUI.ClickButton ran on the host -
            // the host's own open captain's log would keep showing the now-gone mission until close+reopen.
            // Re-render it from local data. (The guest refreshes via the MissionAbandoned broadcast below.)
            RefreshOpenMissionLog();
        }

        /// <summary>
        /// Handle guest request for mission board data.
        /// </summary>
        public void OnMissionBoardRequestReceived(SteamId sender, MissionBoardRequestPacket packet)
        {
            if (!Plugin.IsHost) return;

            VerboseLogger.Log("MISSION", "RECV", $"BoardRequest from {sender}, port={packet.PortIndex}, page={packet.Page}");

            if (packet.PortIndex < 0 || packet.PortIndex >= Port.ports.Length)
            {
                Plugin.Log.LogError($"Port index {packet.PortIndex} out of bounds");
                return;
            }

            var port = Port.ports[packet.PortIndex];
            if (port == null)
            {
                Plugin.Log.LogError($"Invalid port index {packet.PortIndex}");
                return;
            }

            var missions = port.GetMissions(packet.Page, packet.IsWorldMission);

            var response = new MissionBoardResponsePacket
            {
                Missions = new NetworkMissionData[missions.Length],
                TotalCount = port.GetMissionCount()
            };

            for (int i = 0; i < missions.Length; i++)
            {
                response.Missions[i] = MissionToNetworkData(missions[i], i);
            }

            VerboseLogger.Log("MISSION", "SEND", $"BoardResponse, count={missions.Length}");

            Plugin.NetworkManager.SendReliable(sender, PacketType.MissionBoardResponse, w =>
                PacketSerializer.WriteMissionBoardResponse(w, response));
        }

        /// <summary>
        /// Handle guest request to deliver cargo.
        /// </summary>
        public void OnDeliverGoodRequestReceived(SteamId sender, DeliverGoodRequestPacket packet)
        {
            if (!Plugin.IsHost) return;

            VerboseLogger.Log("MISSION", "RECV", $"DeliverRequest from {sender}, item={packet.ItemInstanceId}, prefab={packet.PrefabIndex}, port={packet.PortIndex}");

            // Validate item against registry
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

            // Find the item by SaveablePrefab instanceId
            var targetItem = ItemSyncManager.FindItemByInstanceId(packet.ItemInstanceId);

            if (targetItem == null)
            {
                Plugin.Log.LogWarning($"Could not find item with ID {packet.ItemInstanceId}");
                return;
            }

            var good = targetItem.GetComponent<Good>();
            if (good == null)
            {
                Plugin.Log.LogWarning("Item is not a Good");
                return;
            }

            var mission = good.GetAssignedMission();
            if (mission == null)
            {
                Plugin.Log.LogWarning("Good has no assigned mission");
                return;
            }

            if (packet.PortIndex < 0 || packet.PortIndex >= Port.ports.Length)
            {
                Plugin.Log.LogError($"Port index {packet.PortIndex} out of bounds");
                return;
            }

            var port = Port.ports[packet.PortIndex];
            if (mission.destinationPort != port)
            {
                Plugin.Log.LogWarning("Wrong destination port");
                NotificationUi.instance?.ShowNotification("Wrong port!");
                return;
            }

            // Deliver the good (triggers reward and broadcasts via patches)
            good.Deliver();
        }

        #endregion

        #region Guest Methods

        public void OnMissionStateSyncReceived(MissionStateSyncPacket packet)
        {
            if (Plugin.IsHost) return;

            VerboseLogger.Log("MISSION", "RECV", "FullState");

            // Initialize missions array if needed
            if (PlayerMissions.missions == null)
            {
                PlayerMissions.missions = new Mission[5];
            }

            for (int i = 0; i < 5; i++)
            {
                PlayerMissions.missions[i] = NetworkDataToMission(packet.Missions[i]);
            }

            VerboseLogger.Log("MISSION", "APPLY", $"Missions applied, count={PlayerMissions.GetMissionCount()}");
        }

        public void OnMissionAcceptedReceived(MissionAcceptedPacket packet)
        {
            if (Plugin.IsHost) return;

            VerboseLogger.Log("MISSION", "RECV", $"Accepted, slot={packet.Mission.SlotIndex}");

            if (PlayerMissions.missions == null)
            {
                PlayerMissions.missions = new Mission[5];
            }

            PlayerMissions.missions[packet.Mission.SlotIndex] = NetworkDataToMission(packet.Mission);

            VerboseLogger.Log("MISSION", "APPLY", "Mission accepted from host");

            // MISSION-REFRESH fix: vanilla accept refreshes the open board inline (MissionDetailsUI.ClickButton
            // -> DisplayMissions + dismiss the details popup), but the guest's accept goes through the host so
            // ClickButton never runs - the board kept showing the now-consumed mission until close/reopen. If
            // the guest's board is open, re-request it (the page-change round-trip already used elsewhere) and
            // dismiss the stale details popup so the just-accepted mission drops off, matching vanilla.
            if (GameState.inPortMissionList && MissionListUI.instance != null)
            {
                var t = HarmonyLib.Traverse.Create(MissionListUI.instance);
                var dude = t.Field("currentPortDude").GetValue<PortDude>();
                if (dude != null && dude.GetPort() != null)
                {
                    int page = t.Field("currentPage").GetValue<int>();
                    RequestMissionBoard(dude.GetPort().portIndex, page, MissionListUI.instance.worldMissions);
                }
                // Dismiss the still-open details popup (vanilla ClickButton does UI.SetActive(false)).
                if (MissionDetailsUI.instance != null)
                {
                    HarmonyLib.Traverse.Create(MissionDetailsUI.instance).Field("UI").GetValue<GameObject>()?.SetActive(false);
                }
            }
        }

        public void OnMissionProgressReceived(MissionProgressPacket packet)
        {
            if (Plugin.IsHost) return;

            VerboseLogger.Log("MISSION", "RECV", $"Progress, slot={packet.SlotIndex}, delivered={packet.DeliveredCount}");

            if (packet.SlotIndex < 0 || packet.SlotIndex >= 5) return;

            // Update delivered count via reflection since it's private
            var mission = PlayerMissions.missions?[packet.SlotIndex];
            int prevDelivered = 0;
            if (mission != null)
            {
                prevDelivered = Traverse.Create(mission).Field("deliveredGoods").GetValue<int>();
                Traverse.Create(mission).Field("deliveredGoods").SetValue(packet.DeliveredCount);
            }

            // GUEST-SOUNDS fix: vanilla plays the delivery feedback in the LOCAL deliver method, but in
            // co-op the host is the actor so the guest got silence. Play the gold sound + a "delivered"
            // toast guest-side on a genuine increment (skip the join-time bulk apply where count != +1).
            if (mission != null && packet.DeliveredCount > prevDelivered)
            {
                UISoundPlayer.instance?.PlayGoldSound();
                // Match the host's specific vanilla toast (Mission.DeliverGood) instead of a generic one.
                string text = string.IsNullOrEmpty(packet.GoodName)
                    ? "Cargo delivered"
                    : $"Delivered {packet.GoodName}\n( {packet.DeliveredCount} / {packet.GoodCount} )";
                NotificationUi.instance?.ShowNotification(text, 3f);
            }

            VerboseLogger.Log("MISSION", "APPLY", "Progress updated");
        }

        public void OnMissionCompletedReceived(MissionEndedPacket packet)
        {
            if (Plugin.IsHost) return;

            VerboseLogger.Log("MISSION", "RECV", $"Completed, slot={packet.SlotIndex}");

            bool hadMission = PlayerMissions.missions != null && packet.SlotIndex >= 0 &&
                              packet.SlotIndex < PlayerMissions.missions.Length &&
                              PlayerMissions.missions[packet.SlotIndex] != null;

            if (PlayerMissions.missions != null)
            {
                PlayerMissions.missions[packet.SlotIndex] = null;
            }

            // GUEST-SOUNDS fix: completion feedback (writing sound + toast) guest-side. Gated on actually
            // having had a mission in the slot so the join-time state apply doesn't fire it spuriously.
            if (hadMission)
            {
                UISoundPlayer.instance?.PlayWritingSound();
                // Match the host's vanilla "Mission complete:\n<missionName>" (PlayerMissions.CompleteMission).
                string text = string.IsNullOrEmpty(packet.MissionName)
                    ? "Mission complete"
                    : $"Mission complete:\n{packet.MissionName}";
                NotificationUi.instance?.ShowNotification(text, 4f);
            }

            VerboseLogger.Log("MISSION", "APPLY", "Mission completed");
        }

        public void OnMissionAbandonedReceived(MissionEndedPacket packet)
        {
            if (Plugin.IsHost) return;

            VerboseLogger.Log("MISSION", "RECV", $"Abandoned, slot={packet.SlotIndex}");

            if (PlayerMissions.missions != null)
            {
                PlayerMissions.missions[packet.SlotIndex] = null;
            }

            // Live-refresh the guest's open captain's log (its own abandon click was blocked + routed to the
            // host, so vanilla never refreshed it; this broadcast is the confirmation). Matches the accept path.
            RefreshOpenMissionLog();

            VerboseLogger.Log("MISSION", "APPLY", "Mission abandoned");
        }

        public void OnMissionBoardResponseReceived(MissionBoardResponsePacket packet)
        {
            if (Plugin.IsHost) return;

            VerboseLogger.Log("MISSION", "RECV", $"BoardResponse, count={packet.Missions?.Length ?? 0}");

            // Convert network data to Mission objects
            var missions = new Mission[packet.Missions?.Length ?? 0];
            for (int i = 0; i < missions.Length; i++)
            {
                missions[i] = NetworkDataToMission(packet.Missions[i]);
                // Board missions are not yet accepted - set missionIndex to -1
                // so UI shows "Accept" instead of "Abandon"
                if (missions[i] != null)
                {
                    missions[i].missionIndex = -1;
                }
            }

            // If we have pending UI context, initialize and display
            if (_pendingMissionUI != null && _pendingPortDude != null)
            {
                // Set up the UI state manually (similar to EnablePortMissionUI)
                GameState.inPortMissionList = true;

                var ui = _pendingMissionUI;
                var missionTableAnchor = _pendingMissionTableAnchor;
                var dude = _pendingPortDude;

                // Clear pending state
                _pendingMissionUI = null;
                _pendingMissionTableAnchor = null;
                _pendingPortDude = null;

                // Get the port for display
                var port = dude.GetPort();

                // Set page count from host response
                port.SetMissonCount(packet.TotalCount);

                // Access private fields via Traverse
                var traverse = HarmonyLib.Traverse.Create(ui);
                traverse.Field("currentPortDude").SetValue(dude);
                traverse.Field("currentPage").SetValue(0);

                // Update page count
                float f = (float)packet.TotalCount / 5f;
                int pageCount = Mathf.CeilToInt(f);
                if (pageCount < 1) pageCount = 1;
                traverse.Field("currentPageCount").SetValue(pageCount);

                // Set up UI elements
                var pageButtons = traverse.Field("pageButtons").GetValue<GameObject>();
                var portWelcome = traverse.Field("portWelcome").GetValue<GameObject>();
                var portNameText = traverse.Field("portNameText").GetValue<TextMesh>();
                var list = traverse.Field("list").GetValue<GameObject>();
                var modeButtons = traverse.Field("modeButtons").GetValue<GameObject>();
                var pageCountText = traverse.Field("pageCountText").GetValue<TextMesh>();

                pageButtons?.SetActive(true);
                portWelcome?.SetActive(true);
                if (portNameText != null) portNameText.text = port.GetPortName();

                // PC-only: disable mouse look for mission UI
                // Note: VR not supported in v1, so we skip VR handling
                MouseLook.ToggleMouseLookAndCursor(false);

                list?.SetActive(true);
                ui.SwitchMode(MissionListMode.currentMissions);
                modeButtons?.SetActive(false);

                // Update page text
                if (pageCountText != null) pageCountText.text = $"1 / {pageCount}";

                // Display missions
                ui.SetWorldMissions(false);
                ui.DisplayMissions(missions);

                VerboseLogger.Log("MISSION", "APPLY", $"Displayed {missions.Length} missions from host");
            }
            else
            {
                // Response without pending UI (e.g., page change) - just display
                var ui = MissionListUI.instance;
                if (ui != null && GameState.inPortMissionList)
                {
                    ui.DisplayMissions(missions);
                    VerboseLogger.Log("MISSION", "APPLY", $"Updated mission list with {missions.Length} missions from host");
                }
            }
        }

        // Pending mission board UI context
        private MissionListUI _pendingMissionUI;
        private Transform _pendingMissionTableAnchor;
        private PortDude _pendingPortDude;

        /// <summary>
        /// Store context for pending mission board response.
        /// Called from MissionBoardUIPatch prefix.
        /// </summary>
        public void SetPendingMissionBoardContext(MissionListUI ui, Transform missionTableAnchor, PortDude dude)
        {
            _pendingMissionUI = ui;
            _pendingMissionTableAnchor = missionTableAnchor;
            _pendingPortDude = dude;
        }

        /// <summary>
        /// Request mission board data from host.
        /// </summary>
        public void RequestMissionBoard(int portIndex, int page, bool isWorld)
        {
            if (Plugin.IsHost) return;

            var packet = new MissionBoardRequestPacket
            {
                PortIndex = portIndex,
                Page = page,
                IsWorldMission = isWorld
            };

            VerboseLogger.Log("MISSION", "SEND", $"BoardRequest, port={portIndex}, page={page}");

            Plugin.NetworkManager.SendToAllReliable(PacketType.MissionBoardRequest, w =>
                PacketSerializer.WriteMissionBoardRequest(w, packet));
        }

        /// <summary>
        /// Request to accept a mission from host.
        /// </summary>
        public void RequestAcceptMission(int portIndex, int boardSlot, int page, bool isWorld)
        {
            if (Plugin.IsHost) return;

            var packet = new MissionAcceptRequestPacket
            {
                PortIndex = portIndex,
                BoardSlot = boardSlot,
                Page = page,
                IsWorldMission = isWorld
            };

            VerboseLogger.Log("MISSION", "SEND", $"AcceptRequest, port={portIndex}, slot={boardSlot}");

            Plugin.NetworkManager.SendToAllReliable(PacketType.MissionAcceptRequest, w =>
                PacketSerializer.WriteMissionAcceptRequest(w, packet));
        }

        /// <summary>
        /// Request to abandon a mission from host.
        /// </summary>
        public void RequestAbandonMission(int slotIndex)
        {
            if (Plugin.IsHost) return;

            var packet = new MissionAbandonRequestPacket
            {
                SlotIndex = slotIndex
            };

            VerboseLogger.Log("MISSION", "SEND", $"AbandonRequest, slot={slotIndex}");

            Plugin.NetworkManager.SendToAllReliable(PacketType.MissionAbandonRequest, w =>
                PacketSerializer.WriteMissionAbandonRequest(w, packet));
        }

        /// <summary>
        /// Request to deliver cargo at port.
        /// </summary>
        public void RequestDeliverGood(int itemInstanceId, int prefabIndex, int portIndex)
        {
            if (Plugin.IsHost) return;

            var packet = new DeliverGoodRequestPacket
            {
                ItemInstanceId = itemInstanceId,
                PrefabIndex = prefabIndex,
                PortIndex = portIndex
            };

            VerboseLogger.Log("MISSION", "SEND", $"DeliverRequest, item={itemInstanceId}, prefab={prefabIndex}, port={portIndex}");

            Plugin.NetworkManager.SendToAllReliable(PacketType.DeliverGoodRequest, w =>
                PacketSerializer.WriteDeliverGoodRequest(w, packet));
        }

        #endregion

        public void Reset()
        {
            _pendingMissionUI = null;
            _pendingMissionTableAnchor = null;
            _pendingPortDude = null;
        }
    }
}
