using System.Collections.Generic;
using UnityEngine;
using HarmonyLib;
using Steamworks;
using SailwindCoop.Debug;
using SailwindCoop.Networking.Packets;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Chip log (speedometer) synchronization, cloned from the fishing-rod K1 cast stream.
    /// The chip log is 100% client-local in vanilla: the throw (un-kinematic bobber +
    /// afterThrowTimer) and the line payout only ever run on the thrower's machine
    /// (ExtraLateUpdate sets currentMinVelocity=99999 when !held), so a viewer's bobber stays
    /// parked at initialBobberPos and the ChipLogRopeEnd needle reads zero. The local thrower
    /// broadcasts the throw event and then streams line length + thrown flag at 5Hz; once the
    /// viewer's bobber is dynamic with the streamed line length it drags in the viewer's own
    /// water behind the synced boat and the needle reads approximately right for free.
    /// </summary>
    public class ChipLogSyncManager : MonoBehaviour
    {
        public static ChipLogSyncManager Instance { get; private set; }

        // Chip logs WE threw and are streaming for ("owner" = local thrower; no ownership
        // registry needed - remote applies never re-enter here thanks to IsApplyingRemoteState).
        private readonly HashSet<int> _thrownChipLogs = new HashSet<int>();
        private readonly Dictionary<int, float> _lastSentLineLength = new Dictionary<int, float>();
        private readonly Dictionary<int, float> _lastSentLineTime = new Dictionary<int, float>();
        private readonly List<int> _removalScratch = new List<int>();
        private const float LineLengthCoalesceDelta = 0.25f; // metres of change before a resend
        // Keepalive: a line pinned at max length coalesces forever, so a late joiner who missed the
        // Throw packet would never converge (LineSync alone establishes the full thrown state on
        // receivers). Resend periodically even when unchanged.
        private const float KeepaliveInterval = 5f;

        private const float StreamInterval = 0.2f; // 5Hz
        private float _lastStreamTime;

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
            if (_thrownChipLogs.Count == 0) return;

            if (Time.time - _lastStreamTime < StreamInterval) return;
            _lastStreamTime = Time.time;

            foreach (var id in _thrownChipLogs)
            {
                var log = FindChipLogByInstanceId(id);
                if (log == null)
                {
                    _removalScratch.Add(id);
                    continue;
                }

                var t = Traverse.Create(log);
                var bobberJoint = t.Field("bobberJoint").GetValue<ConfigurableJoint>();
                if (bobberJoint == null)
                {
                    _removalScratch.Add(id);
                    continue;
                }

                // Ownership handoff: a remote player picked up the dropped chip log; their machine
                // streams for it now (local thrown stays true on our copy, so the thrown=false
                // terminal below would never fire). Stop streaming without sending.
                if (ItemSyncManager.Instance != null)
                {
                    var holder = ItemSyncManager.Instance.GetItemHolder(id);
                    if (holder.Value != 0 && holder.Value != SteamClient.SteamId.Value)
                    {
                        _removalScratch.Add(id);
                        continue;
                    }
                }

                bool thrown = t.Field("thrown").GetValue<bool>();
                if (!thrown)
                {
                    // Reeled back in (natural reel-in, InstantUnthrow, or stow): one final
                    // authoritative reset so viewers re-park, then stop the stream.
                    SendLineSync(id, GetMinLength(log), thrown: false);
                    _removalScratch.Add(id);
                    continue;
                }

                float len = bobberJoint.linearLimit.limit;
                if (_lastSentLineLength.TryGetValue(id, out var last) && Mathf.Abs(len - last) <= LineLengthCoalesceDelta
                    && _lastSentLineTime.TryGetValue(id, out var lastTime) && Time.time - lastTime < KeepaliveInterval)
                    continue;
                _lastSentLineLength[id] = len;
                _lastSentLineTime[id] = Time.time;

                SendLineSync(id, len, thrown: true);
            }

            if (_removalScratch.Count > 0)
            {
                foreach (var id in _removalScratch)
                {
                    _thrownChipLogs.Remove(id);
                    _lastSentLineLength.Remove(id);
                    _lastSentLineTime.Remove(id);
                }
                _removalScratch.Clear();
            }
        }

        private void SendLineSync(int itemId, float lineLength, bool thrown)
        {
            var packet = new ChipLogLineSyncPacket
            {
                ItemInstanceId = itemId,
                LineLength = lineLength,
                Thrown = thrown
            };

            VerboseLogger.Log("CHIPLOG", "SEND", $"LineSync, item={itemId}, len={lineLength:F2}, thrown={thrown}");

            Plugin.NetworkManager.SendToAllReliable(PacketType.ChipLogLineSync, w =>
                PacketSerializer.WriteChipLogLineSync(w, packet));
        }

        private static float GetMinLength(ShipItemChipLog log)
        {
            try
            {
                var m = Traverse.Create(log).Field("minLength").GetValue<float>();
                if (m > 0f) return m;
            }
            catch { }
            return 0.5f;
        }

        private ShipItemChipLog FindChipLogByInstanceId(int instanceId)
        {
            var allLogs = FindObjectsOfType<ShipItemChipLog>();
            foreach (var log in allLogs)
            {
                var prefab = log.GetComponent<SaveablePrefab>();
                if (prefab != null && prefab.instanceId == instanceId)
                    return log;
            }
            return null;
        }

        #region Local Events (called by patches)

        public void OnLocalChipLogThrown(ShipItemChipLog log)
        {
            var prefab = log.GetComponent<SaveablePrefab>();
            if (prefab == null) return;

            int itemId = prefab.instanceId;
            _thrownChipLogs.Add(itemId);
            _lastSentLineLength.Remove(itemId); // force the first stream tick to send
            _lastSentLineTime.Remove(itemId);

            var packet = new ChipLogThrowPacket { ItemInstanceId = itemId };

            VerboseLogger.Log("CHIPLOG", "SEND", $"Throw, item={itemId}");

            Plugin.NetworkManager.SendToAllReliable(PacketType.ChipLogThrow, w =>
                PacketSerializer.WriteChipLogThrow(w, packet));
        }

        /// <summary>
        /// Stow-time reset (OnEnterInventory postfix). Vanilla resets thrown/currentTargetLength
        /// purely locally on stow; send one authoritative Thrown=false so viewers re-park, and
        /// stop the stream. Idempotent - harmless for a never-thrown chip log.
        /// </summary>
        public void OnLocalChipLogStowed(ShipItemChipLog log)
        {
            var prefab = log.GetComponent<SaveablePrefab>();
            if (prefab == null) return;

            int itemId = prefab.instanceId;
            _thrownChipLogs.Remove(itemId);
            _lastSentLineLength.Remove(itemId);
            _lastSentLineTime.Remove(itemId);

            SendLineSync(itemId, GetMinLength(log), thrown: false);
        }

        #endregion

        /// <summary>
        /// Ownership handoff heal: _thrownChipLogs means "we threw it and stream for it", but nothing
        /// clears it if we drop the thrown chip log and another player picks it up (their machine runs
        /// InstantUnthrow/re-throws; ours would early-return on their packets forever). If the item is
        /// now REMOTE-held, release ownership so the incoming packet applies. Returns true if released.
        /// </summary>
        private bool ReleaseOwnershipIfRemoteHeld(int itemId)
        {
            if (ItemSyncManager.Instance == null) return false;
            var holder = ItemSyncManager.Instance.GetItemHolder(itemId);
            if (holder.Value == 0 || holder.Value == SteamClient.SteamId.Value) return false;

            _thrownChipLogs.Remove(itemId);
            _lastSentLineLength.Remove(itemId);
            _lastSentLineTime.Remove(itemId);
            VerboseLogger.Log("CHIPLOG", "EVENT", $"Released chip log ownership (remote-held), item={itemId}, holder={holder.Value}");
            return true;
        }

        #region Packet Handlers (called by Plugin.cs)

        public void OnChipLogThrowReceived(ChipLogThrowPacket packet, SteamId sender = default)
        {
            // Star-relay: forward BEFORE the thrower early-return so the host (not the thrower) still relays.
            if (Plugin.IsHost)
                Plugin.NetworkManager.SendToAllExcept(sender, PacketType.ChipLogThrow, w =>
                    PacketSerializer.WriteChipLogThrow(w, packet));

            if (_thrownChipLogs.Contains(packet.ItemInstanceId)
                && !ReleaseOwnershipIfRemoteHeld(packet.ItemInstanceId)) return; // we threw it ourselves

            VerboseLogger.Log("CHIPLOG", "RECV", $"Throw, item={packet.ItemInstanceId}");

            IsApplyingRemoteState = true;
            try
            {
                var log = FindChipLogByInstanceId(packet.ItemInstanceId);
                if (log == null || !log.gameObject.activeInHierarchy) return; // remotely stowed; the line stream self-heals later

                var t = Traverse.Create(log);
                var bobberJoint = t.Field("bobberJoint").GetValue<ConfigurableJoint>();
                if (bobberJoint == null || !bobberJoint.gameObject.activeInHierarchy) return;

                // Vanilla sets thrown=true in ExtraLateUpdate BEFORE starting the coroutine; the
                // coroutine itself un-kinematics the bobber and sets afterThrowTimer=6.
                t.Field("thrown").SetValue(true);
                log.StartCoroutine("ThrowRod");

                VerboseLogger.Log("CHIPLOG", "APPLY", $"Throw applied, item={packet.ItemInstanceId}");
            }
            finally
            {
                IsApplyingRemoteState = false;
            }
        }

        public void OnChipLogLineSyncReceived(ChipLogLineSyncPacket packet, SteamId sender = default)
        {
            // Star-relay: forward BEFORE the thrower early-return so the host (not the thrower) still relays.
            if (Plugin.IsHost)
                Plugin.NetworkManager.SendToAllExcept(sender, PacketType.ChipLogLineSync, w =>
                    PacketSerializer.WriteChipLogLineSync(w, packet));

            if (_thrownChipLogs.Contains(packet.ItemInstanceId)
                && !ReleaseOwnershipIfRemoteHeld(packet.ItemInstanceId)) return; // we are the thrower

            VerboseLogger.Log("CHIPLOG", "RECV", $"LineSync, item={packet.ItemInstanceId}, len={packet.LineLength:F2}, thrown={packet.Thrown}");

            IsApplyingRemoteState = true;
            try
            {
                var log = FindChipLogByInstanceId(packet.ItemInstanceId);
                if (log == null) return;

                var t = Traverse.Create(log);
                var bobberJoint = t.Field("bobberJoint").GetValue<ConfigurableJoint>();
                if (bobberJoint == null || !bobberJoint.gameObject.activeInHierarchy) return; // remotely stowed

                var limit = bobberJoint.linearLimit;
                limit.limit = packet.LineLength;
                bobberJoint.linearLimit = limit;
                t.Field("currentTargetLength").SetValue(packet.LineLength);

                // Idempotently establish the full thrown state from the stream alone: if the Throw
                // packet was missed (late joiner) or vanilla ExtraLateUpdate re-parked the bobber
                // before the first line packet arrived (limit was still <=minLength when the throw
                // coroutine ended), only raising the limit would leave a frozen bobber on a long
                // rope - nothing would un-kinematic it again.
                t.Field("thrown").SetValue(packet.Thrown);
                if (packet.Thrown)
                {
                    var bobberBody = t.Field("bobberBody").GetValue<Rigidbody>();
                    if (bobberBody != null && bobberBody.isKinematic)
                        bobberBody.isKinematic = false;
                    t.Field("afterThrowTimer").SetValue(6f);
                }
                // Thrown=false: vanilla ExtraLateUpdate re-parks once the limit is back at minLength.

                VerboseLogger.Log("CHIPLOG", "APPLY", $"LineSync applied, item={packet.ItemInstanceId}, len={packet.LineLength:F2}, thrown={packet.Thrown}");
            }
            finally
            {
                IsApplyingRemoteState = false;
            }
        }

        #endregion

        /// <summary>
        /// Stow/park hygiene for remote hide/show, mirroring FishingSyncManager.SyncExternalRodParts:
        /// the chip-log bobber is also reparented to the shifting world at OnLoad, so hiding the item
        /// alone would leave a visible orphaned bobber floating at the ship's position. Call with
        /// stowed=true BEFORE item.gameObject.SetActive(false) and with stowed=false AFTER
        /// item.gameObject.SetActive(true). No-op for anything that is not a ShipItemChipLog.
        /// </summary>
        public static void SyncExternalChipLogParts(ShipItem item, bool stowed)
        {
            var log = item as ShipItemChipLog;
            if (log == null) return;

            try
            {
                var t = Traverse.Create(log);
                var bobberJoint = t.Field("bobberJoint").GetValue<ConfigurableJoint>();
                if (bobberJoint == null) return;

                // Vanilla reads this off the joint at OnLoad (initialBobberPos = connectedAnchor),
                // so the joint itself is a safe fallback if OnLoad has not run on this copy.
                var initialBobberPos = t.Field("initialBobberPos").GetValue<Vector3>();
                if (initialBobberPos == Vector3.zero)
                    initialBobberPos = bobberJoint.connectedAnchor;

                var bobberRb = bobberJoint.GetComponent<Rigidbody>();

                if (stowed)
                {
                    // Park the bobber the way vanilla OnEnterInventory does, then fully hide it.
                    if (bobberRb != null) bobberRb.isKinematic = true;
                    bobberJoint.transform.position = log.transform.TransformPoint(initialBobberPos);
                    t.Field("thrown").SetValue(false);
                    t.Field("currentTargetLength").SetValue(GetMinLength(log));
                    bobberJoint.gameObject.SetActive(false);
                    VerboseLogger.Log("CHIPLOG", "EVENT", $"Parked external chip log parts (remote stow), item={log.name}");
                }
                else
                {
                    // Unity re-creates the native joint when the component's GameObject re-enables
                    // and connectedBody is still assigned.
                    if (log.sold) bobberJoint.gameObject.SetActive(true);
                    bobberJoint.transform.position = log.transform.TransformPoint(initialBobberPos);
                    if (bobberRb != null) bobberRb.isKinematic = false;
                    VerboseLogger.Log("CHIPLOG", "EVENT", $"Restored external chip log parts (remote show), item={log.name}");
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"[CHIPLOG] SyncExternalChipLogParts({(stowed ? "stow" : "show")}) failed on {item.name}: {e.Message}");
            }
        }

        public void Reset()
        {
            _thrownChipLogs.Clear();
            _lastSentLineLength.Clear();
            _lastSentLineTime.Clear();
            _removalScratch.Clear();
            _lastStreamTime = 0f;
        }
    }
}
