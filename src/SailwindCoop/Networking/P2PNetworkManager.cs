using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Steamworks;
using Steamworks.Data;
using SailwindCoop.Networking.Packets;

namespace SailwindCoop.Networking
{
    public class P2PNetworkManager
    {
        private const int ChannelReliable = 0;
        private const int ChannelUnreliable = 1;
        private const int MaxPacketSize = 1024 * 256; // 256 KB max packet size
        // Thresholds for the serialize-time oversize diagnostic (see SerializePacket).
        private const int LargePacketWarn = 256 * 1024;    // 256 KB - getting big, worth logging
        private const int ReliableHardLimit = 1024 * 1024; // ~1 MB legacy Steam reliable P2P ceiling

        private readonly Dictionary<PacketType, Action<SteamId, BinaryReader>> _handlers;
        private readonly HashSet<SteamId> _connectedPeers;
        private readonly byte[] _receiveBuffer;

        public event Action<SteamId> OnConnected;
        public event Action<SteamId> OnDisconnected;

        public IReadOnlyCollection<SteamId> ConnectedPeers => _connectedPeers;

        public P2PNetworkManager()
        {
            _handlers = new Dictionary<PacketType, Action<SteamId, BinaryReader>>();
            _connectedPeers = new HashSet<SteamId>();
            _receiveBuffer = new byte[MaxPacketSize];

            SteamNetworking.OnP2PSessionRequest += OnP2PSessionRequest;
            SteamNetworking.OnP2PConnectionFailed += OnP2PConnectionFailed;

            Plugin.Log.LogInfo("P2PNetworkManager initialized");
        }

        public void Shutdown()
        {
            SteamNetworking.OnP2PSessionRequest -= OnP2PSessionRequest;
            SteamNetworking.OnP2PConnectionFailed -= OnP2PConnectionFailed;

            foreach (var peer in _connectedPeers)
            {
                SteamNetworking.CloseP2PSessionWithUser(peer);
            }
            _connectedPeers.Clear();
            _handlers.Clear();

            Plugin.Log.LogInfo("P2PNetworkManager shut down");
        }

        public void RegisterHandler(PacketType type, Action<SteamId, BinaryReader> handler)
        {
            if (_handlers.ContainsKey(type))
            {
                Plugin.Log.LogWarning($"Handler for {type} already registered, replacing");
            }
            _handlers[type] = handler;
            Plugin.Log.LogDebug($"Registered handler for {type}");
        }

        public void UnregisterHandler(PacketType type)
        {
            if (_handlers.Remove(type))
            {
                Plugin.Log.LogDebug($"Unregistered handler for {type}");
            }
        }

        // Track sent packets for debugging
        private Dictionary<PacketType, int> _sentPacketCounts = new Dictionary<PacketType, int>();

        public void SendPacket(SteamId target, PacketType type, Action<BinaryWriter> writePayload, bool reliable = true)
        {
            var data = SerializePacket(type, writePayload);
            if (data == null) return;

            int channel = reliable ? ChannelReliable : ChannelUnreliable;
            var sendType = reliable ? P2PSend.Reliable : P2PSend.Unreliable;

            // Count sent packets
            if (!_sentPacketCounts.ContainsKey(type))
                _sentPacketCounts[type] = 0;
            _sentPacketCounts[type]++;

            bool success = SteamNetworking.SendP2PPacket(target, data, data.Length, channel, sendType);
            if (!success)
            {
                Plugin.Log.LogWarning($"Failed to send {type} packet to {target}");
            }
        }

        public void LogSentPacketCounts()
        {
            if (_sentPacketCounts.Count == 0) return;
            var counts = string.Join(", ", _sentPacketCounts.Select(kv => $"{kv.Key}={kv.Value}"));
            Plugin.Log.LogWarning($"Packets SENT: {counts}");
            _sentPacketCounts.Clear();
        }

        public void SendReliable(SteamId target, PacketType type, Action<BinaryWriter> writePayload)
        {
            SendPacket(target, type, writePayload, reliable: true);
        }

        public void SendUnreliable(SteamId target, PacketType type, Action<BinaryWriter> writePayload)
        {
            SendPacket(target, type, writePayload, reliable: false);
        }

        public void SendToAll(PacketType type, Action<BinaryWriter> writePayload, bool reliable = true)
        {
            if (_connectedPeers.Count == 0) return;

            var data = SerializePacket(type, writePayload);
            if (data == null) return;

            int channel = reliable ? ChannelReliable : ChannelUnreliable;
            var sendType = reliable ? P2PSend.Reliable : P2PSend.Unreliable;

            foreach (var peer in _connectedPeers)
            {
                bool success = SteamNetworking.SendP2PPacket(peer, data, data.Length, channel, sendType);
                if (!success)
                {
                    Plugin.Log.LogWarning($"Failed to send {type} packet to {peer}");
                }
            }
        }

        public void SendToAllReliable(PacketType type, Action<BinaryWriter> writePayload)
        {
            SendToAll(type, writePayload, reliable: true);
        }

        public void SendToAllUnreliable(PacketType type, Action<BinaryWriter> writePayload)
        {
            SendToAll(type, writePayload, reliable: false);
        }

        // STAR topology host-relay primitive: send to every connected peer EXCEPT `origin` (the peer the
        // packet was relayed FROM). Mirrors SendToAll exactly, minus the origin. At N=1 the only peer IS the
        // origin, so this targets no one (no-op) - keeping single-guest behavior byte-identical.
        public void SendToAllExcept(SteamId origin, PacketType type, Action<BinaryWriter> writePayload, bool reliable = true)
        {
            if (_connectedPeers.Count == 0) return;

            var data = SerializePacket(type, writePayload);
            if (data == null) return;

            int channel = reliable ? ChannelReliable : ChannelUnreliable;
            var sendType = reliable ? P2PSend.Reliable : P2PSend.Unreliable;

            foreach (var peer in _connectedPeers)
            {
                if (peer == origin) continue;

                bool success = SteamNetworking.SendP2PPacket(peer, data, data.Length, channel, sendType);
                if (!success)
                {
                    Plugin.Log.LogWarning($"Failed to send {type} packet to {peer}");
                }
            }
        }

        public void ProcessIncomingPackets()
        {
            // Process reliable channel
            ProcessChannel(ChannelReliable);

            // Process unreliable channel
            ProcessChannel(ChannelUnreliable);
        }

        private int _packetsProcessedThisFrame;
        private const int BaseMaxPacketsPerFrame = 100;
        // Hard ceiling so a flood (or a large lobby) can't make a single frame drain unbounded packets.
        private const int MaxPacketsPerFrameCap = 800;

        // N-player: scale the per-frame intake budget with the number of connected peers so N guests
        // don't starve the host's drain (each guest contributes its own packet stream). At N=1 this is
        // Max(base, base*1) = base, byte-identical to the old single-peer behavior. Clamped at a sane cap.
        private int MaxPacketsPerFrame
        {
            get
            {
                // GUEST PACKET BUDGET: a guest's _connectedPeers.Count is always 1 (star topology),
                // so peer-count scaling never helps the guest - yet the guest receives the host's relayed
                // N-multiplied stream. Scale by crew size (host+guests) instead, so the guest's drain keeps
                // up. At N=1 GetMemberCount()==2 vs Count==1; max picks 2, still well within the cap, and
                // the host (whose Count tracks all guests) is unaffected. Clamped at the same sane cap.
                int crew = SteamLobbyManager.Instance?.GetMemberCount() ?? 1;
                int scaled = BaseMaxPacketsPerFrame * Math.Max(1, Math.Max(_connectedPeers.Count, crew));
                return Math.Min(scaled, MaxPacketsPerFrameCap);
            }
        }

        private void ProcessChannel(int channel)
        {
            // LOW-FPS BACKLOG FIX: each channel gets its OWN budget (previously both channels shared one
            // via _packetsProcessedThisFrame, so a reliable flood on channel 0 fully starved fresh
            // unreliable positions on channel 1). The absolute MaxPacketsPerFrameCap still bounds each.
            int budget = MaxPacketsPerFrame;

            if (channel == ChannelUnreliable)
            {
                ProcessUnreliableChannelCoalesced(budget);
                return;
            }

            int packetsInChannel = 0;
            while (SteamNetworking.IsP2PPacketAvailable(channel) && packetsInChannel < budget)
            {
                var packet = SteamNetworking.ReadP2PPacket(channel);
                if (!packet.HasValue) break;

                var p2pPacket = packet.Value;
                ProcessPacket(p2pPacket.SteamId, p2pPacket.Data);
                packetsInChannel++;
                _packetsProcessedThisFrame++;
            }

            NoteBacklog(channel, packetsInChannel);
        }

        private struct PendingPacket
        {
            public SteamId Sender;
            public byte[] Data;
        }

        private readonly List<PendingPacket> _unreliableDrain = new List<PendingPacket>();
        private readonly Dictionary<string, int> _coalesceNewestIndex = new Dictionary<string, int>();

        // LOW-FPS BACKLOG FIX: drain the unreliable channel into a list first, then dispatch. For a STRICT
        // whitelist of pure last-write-wins snapshot types (see GetCoalesceKey) only the NEWEST packet per
        // (type, entity) key is dispatched - replaying 100+ stale positions serially on a 6-11 FPS guest is
        // what delayed fresh state for whole seconds. Everything else passes through untouched, in its
        // original relative order (kept packets dispatch in arrival order too). Reliable channel is not
        // coalesced - its ordering stays byte-identical.
        private void ProcessUnreliableChannelCoalesced(int budget)
        {
            _unreliableDrain.Clear();
            while (SteamNetworking.IsP2PPacketAvailable(ChannelUnreliable) && _unreliableDrain.Count < budget)
            {
                var packet = SteamNetworking.ReadP2PPacket(ChannelUnreliable);
                if (!packet.HasValue) break;
                _unreliableDrain.Add(new PendingPacket { Sender = packet.Value.SteamId, Data = packet.Value.Data });
            }

            int drained = _unreliableDrain.Count;
            if (drained > 1)
            {
                // Pass 1: last index wins per coalesce key.
                _coalesceNewestIndex.Clear();
                for (int i = 0; i < drained; i++)
                {
                    string key = GetCoalesceKey(_unreliableDrain[i].Sender, _unreliableDrain[i].Data);
                    if (key != null) _coalesceNewestIndex[key] = i;
                }

                // Pass 2: dispatch in arrival order, skipping superseded snapshots.
                for (int i = 0; i < drained; i++)
                {
                    var p = _unreliableDrain[i];
                    string key = GetCoalesceKey(p.Sender, p.Data);
                    if (key != null && _coalesceNewestIndex[key] != i) continue; // stale; a newer snapshot for this key arrived this frame
                    ProcessPacket(p.Sender, p.Data);
                    _packetsProcessedThisFrame++;
                }
            }
            else if (drained == 1)
            {
                ProcessPacket(_unreliableDrain[0].Sender, _unreliableDrain[0].Data);
                _packetsProcessedThisFrame++;
            }

            _unreliableDrain.Clear(); // release byte[] refs
            NoteBacklog(ChannelUnreliable, drained);
        }

        // STRICT WHITELIST of coalescable types - all verified sent UNRELIABLE with pure-overwrite handlers:
        //  - PlayerPosition: body starts with the AUTHOR SteamId (ulong at offset 1) - key per author, since
        //    on a guest the transport sender is always the host relay.
        //  - BoatTransform / NPCBoatState: body starts with the boat name / hierarchy path (length-prefixed
        //    string at offset 1) - key per entity.
        // Event-semantics packets (and HelmState, whose IsFinal flag carries event meaning) return null and
        // are NEVER coalesced. Any parse doubt also returns null -> safe pass-through.
        private string GetCoalesceKey(SteamId sender, byte[] data)
        {
            if (data == null || data.Length < 2) return null;
            var type = (PacketType)data[0];
            switch (type)
            {
                case PacketType.PlayerPosition:
                    if (data.Length < 9) return null;
                    return "PP:" + BitConverter.ToUInt64(data, 1);
                case PacketType.BoatTransform:
                    {
                        string name = TryReadPrefixedString(data, 1);
                        return name != null ? "BT:" + name : null;
                    }
                case PacketType.NPCBoatState:
                    {
                        string name = TryReadPrefixedString(data, 1);
                        return name != null ? "NB:" + name : null;
                    }
                default:
                    return null;
            }
        }

        // Reads a BinaryWriter length-prefixed (7-bit varint) UTF8 string at `offset` without allocating a
        // stream. Returns null on any bounds/format problem (caller treats null as "don't coalesce").
        private static string TryReadPrefixedString(byte[] data, int offset)
        {
            int length = 0, shift = 0, pos = offset;
            while (true)
            {
                if (pos >= data.Length || shift > 28) return null;
                byte b = data[pos++];
                length |= (b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
            }
            if (length < 0 || pos + length > data.Length) return null;
            return System.Text.Encoding.UTF8.GetString(data, pos, length);
        }

        // LOW-FPS BACKLOG FIX: the old ">10 packets" warning fired every frame during a flood (539 lines in
        // one session), adding disk I/O to an already-starved frame. Threshold raised, rate-limited to one
        // line per 5s REALTIME (survives timeScale changes), and reports the max backlog seen since last log.
        private const int BacklogWarnThreshold = 50;
        private const float BacklogWarnIntervalSeconds = 5f;
        private int _maxBacklogSinceLog;
        private float _lastBacklogLogRealtime = -999f;

        private void NoteBacklog(int channel, int packetsInChannel)
        {
            if (packetsInChannel <= BacklogWarnThreshold) return;
            if (packetsInChannel > _maxBacklogSinceLog) _maxBacklogSinceLog = packetsInChannel;

            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now - _lastBacklogLogRealtime < BacklogWarnIntervalSeconds) return;
            _lastBacklogLogRealtime = now;
            Plugin.Log.LogWarning($"Processed {packetsInChannel} packets on channel {channel} (max backlog since last log: {_maxBacklogSinceLog})");
            _maxBacklogSinceLog = 0;
        }

        public void ResetPacketCounter()
        {
            _packetsProcessedThisFrame = 0;
        }

        // Track packet types for debugging (logged via F7 / PerformanceProfiler)
        private Dictionary<PacketType, int> _packetTypeCounts = new Dictionary<PacketType, int>();

        private void ProcessPacket(SteamId sender, byte[] data)
        {
            if (data == null || data.Length < 1)
            {
                Plugin.Log.LogWarning($"Received empty or null packet from {sender}");
                return;
            }

            try
            {
                using (var stream = new MemoryStream(data))
                using (var reader = new BinaryReader(stream))
                {
                    var packetType = (PacketType)reader.ReadByte();

                    // Count packet types
                    if (!_packetTypeCounts.ContainsKey(packetType))
                        _packetTypeCounts[packetType] = 0;
                    _packetTypeCounts[packetType]++;

                    if (_handlers.TryGetValue(packetType, out var handler))
                    {
                        handler(sender, reader);
                    }
                    else
                    {
                        Plugin.Log.LogDebug($"No handler registered for packet type {packetType} from {sender}");
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Error processing packet from {sender}: {ex.Message}");
            }
        }

        public void LogPacketTypeCounts()
        {
            if (_packetTypeCounts.Count == 0) return;

            var counts = string.Join(", ", _packetTypeCounts.Select(kv => $"{kv.Key}={kv.Value}"));
            Plugin.Log.LogWarning($"Packet types received: {counts}");
            _packetTypeCounts.Clear();
        }

        private byte[] SerializePacket(PacketType type, Action<BinaryWriter> writePayload)
        {
            try
            {
                using (var stream = new MemoryStream())
                using (var writer = new BinaryWriter(stream))
                {
                    writer.Write((byte)type);
                    writePayload?.Invoke(writer);
                    var data = stream.ToArray();

                    // The legacy Steam P2P RELIABLE channel fragments/reassembles only up
                    // to ~1 MB; beyond that SendP2PPacket silently returns false and Steam DROPS the message. The
                    // realistic offender is a large join snapshot (BoatWorldState: many items + PNG-encoded dirt
                    // textures) - a dropped join packet leaves the joining guest with an incomplete/empty world and
                    // no error surfaced today (only a generic per-peer "failed to send" warning). Chunking can't be
                    // done safely without a live playtest, so at minimum make the condition OBSERVABLE: ERROR on
                    // overflow, WARN when approaching the limit. This is the single serialize chokepoint for every
                    // send path (SendPacket / SendToAll / SendToAllExcept), so one check covers them all.
                    if (data.Length >= ReliableHardLimit)
                        Plugin.Log.LogError($"OVERSIZE {type} packet: {data.Length / 1024} KB exceeds the ~1MB Steam reliable P2P limit. Steam will DROP it; the recipient (likely a joining guest) gets incomplete state and a broken join. Reduce world load or chunk BoatWorldState.");
                    else if (data.Length >= LargePacketWarn)
                        Plugin.Log.LogWarning($"Large {type} packet: {data.Length / 1024} KB (approaching the ~1MB reliable P2P limit).");

                    return data;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Error serializing {type} packet: {ex.Message}");
                return null;
            }
        }

        public void AddPeer(SteamId peerId)
        {
            if (peerId == SteamClient.SteamId) return;

            if (_connectedPeers.Add(peerId))
            {
                Plugin.Log.LogInfo($"Peer connected: {peerId}");
                OnConnected?.Invoke(peerId);
            }
        }

        public void RemovePeer(SteamId peerId)
        {
            if (_connectedPeers.Remove(peerId))
            {
                SteamNetworking.CloseP2PSessionWithUser(peerId);
                Plugin.Log.LogInfo($"Peer disconnected: {peerId}");
                OnDisconnected?.Invoke(peerId);
            }
        }

        public bool IsPeerConnected(SteamId peerId)
        {
            return _connectedPeers.Contains(peerId);
        }

        private void OnP2PSessionRequest(SteamId requester)
        {
            // Always accept the underlying Steam P2P session - the lobby system already validated the
            // requester, and accepting avoids race conditions when joining. (Accepting is harmless even
            // if we don't peer with them; relayed traffic still flows host<->guest.)
            SteamNetworking.AcceptP2PSessionWithUser(requester);

            // STAR ENFORCEMENT (N-player Phase 5): only AddPeer when role-appropriate, so guests never
            // form a direct transport peer with another guest (the host relays all guest<->guest state).
            //  - HOST: any lobby member is a valid direct peer.
            //  - GUEST: only the HOST is a valid direct peer; ignore peering requests from other guests.
            // At N=1 the only requester a guest ever sees IS the host, so this is unchanged for a single
            // guest. We still accepted the session above either way.
            bool allowPeer;
            if (Plugin.IsHost)
            {
                allowPeer = true;
            }
            else
            {
                var hostId = SteamLobbyManager.Instance.HostSteamId;
                allowPeer = requester == hostId;
            }

            if (allowPeer)
            {
                if (!_connectedPeers.Contains(requester))
                {
                    AddPeer(requester);
                }
                Plugin.Log.LogInfo($"Accepted P2P session from {requester} (peered)");
            }
            else
            {
                // Guest received a session request from a non-host (another guest). Accept the session
                // but do NOT peer - star topology routes their state through the host.
                Plugin.Log.LogInfo($"Accepted P2P session from {requester} (NOT peered - non-host requester on guest; star topology)");
            }
        }

        private void OnP2PConnectionFailed(SteamId peerId, P2PSessionError error)
        {
            Plugin.Log.LogError($"P2P connection failed with {peerId}: {error}");

            if (_connectedPeers.Contains(peerId))
            {
                RemovePeer(peerId);
            }
        }

    }
}
