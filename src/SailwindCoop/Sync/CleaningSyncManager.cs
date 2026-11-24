using System.Collections.Generic;
using UnityEngine;
using SailwindCoop.Debug;
using SailwindCoop.Networking.Packets;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Manages synchronization of boat cleaning (broom strokes and shipyard hull cleaning).
    /// </summary>
    public class CleaningSyncManager : MonoBehaviour
    {
        public static CleaningSyncManager Instance { get; private set; }

        /// <summary>
        /// Set to true when applying remote state to prevent feedback loops.
        /// </summary>
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

        // === Local Events (called from patches) ===

        /// <summary>
        /// Called when local player cleans with broom (from MasterPainter.PaintObject postfix).
        /// </summary>
        public void OnLocalCleaningStroke(string boatName, Vector2 uv, sbyte extraIndex = -1)
        {
            if (!Plugin.IsMultiplayer) return;
            if (IsApplyingRemoteState) return;

            VerboseLogger.CleaningSend($"stroke, boat={boatName}, surface={extraIndex}, uv=({uv.x:F2}, {uv.y:F2})");

            var packet = new CleaningStrokePacket
            {
                BoatName = boatName,
                UVX = uv.x,
                UVY = uv.y,
                ExtraIndex = extraIndex
            };

            Plugin.NetworkManager.SendToAllReliable(PacketType.CleaningStroke, w =>
                PacketSerializer.WriteCleaningStroke(w, packet));
        }

        /// <summary>
        /// Called when local player triggers shipyard hull cleaning (from CleanableObject.CleanFully postfix).
        /// </summary>
        public void OnLocalCleanFully(string boatName)
        {
            if (!Plugin.IsMultiplayer) return;
            if (IsApplyingRemoteState) return;

            VerboseLogger.CleaningSend($"fullClean, boat={boatName}");

            var packet = new CleanFullyPacket
            {
                BoatName = boatName
            };

            Plugin.NetworkManager.SendToAllReliable(PacketType.CleanFully, w =>
                PacketSerializer.WriteCleanFully(w, packet));
        }

        // === Remote Events (called from packet handlers) ===

        /// <summary>
        /// Called when remote player cleans with broom.
        /// </summary>
        public void OnRemoteCleaningStroke(CleaningStrokePacket packet, Steamworks.SteamId sender = default)
        {
            VerboseLogger.CleaningRecv($"stroke, boat={packet.BoatName}, uv=({packet.UVX:F2}, {packet.UVY:F2})");

            // Star-relay: forward the broom stroke to the other guests (reliable).
            if (Plugin.IsHost)
                Plugin.NetworkManager.SendToAllExcept(sender, PacketType.CleaningStroke, w =>
                    PacketSerializer.WriteCleaningStroke(w, packet));

            IsApplyingRemoteState = true;
            try
            {
                ApplyCleaningStroke(packet);
            }
            finally
            {
                IsApplyingRemoteState = false;
            }
        }

        /// <summary>
        /// Called when remote player triggers shipyard hull cleaning.
        /// </summary>
        public void OnRemoteCleanFully(CleanFullyPacket packet, Steamworks.SteamId sender = default)
        {
            VerboseLogger.CleaningRecv($"fullClean, boat={packet.BoatName}");

            // Star-relay: forward the full-clean to the other guests (reliable).
            if (Plugin.IsHost)
                Plugin.NetworkManager.SendToAllExcept(sender, PacketType.CleanFully, w =>
                    PacketSerializer.WriteCleanFully(w, packet));

            IsApplyingRemoteState = true;
            try
            {
                ApplyCleanFully(packet);
            }
            finally
            {
                IsApplyingRemoteState = false;
            }
        }

        // === Apply Methods ===

        private void ApplyCleaningStroke(CleaningStrokePacket packet)
        {
            var boat = BoatUtility.FindBoatByName(packet.BoatName);
            if (boat == null)
            {
                VerboseLogger.CleaningApply($"stroke FAILED: boat not found: {packet.BoatName}");
                return;
            }

            var uv = new Vector2(packet.UVX, packet.UVY);

            // (0.41) A stroke on one of the boat's extra surfaces, addressed by its position in extraCleanables.
            if (packet.ExtraIndex >= 0)
            {
                var extras = boat.extraCleanables;
                var extra = extras != null && packet.ExtraIndex < extras.Length ? extras[packet.ExtraIndex] : null;
                // An extra on a part option this machine does not show has never started, has no dirt
                // material yet, and would throw.
                if (extra == null || !extra.gameObject.activeInHierarchy)
                {
                    VerboseLogger.CleaningApply($"stroke skipped: surface {packet.ExtraIndex} of {packet.BoatName} is missing or hidden here");
                    return;
                }
                try
                {
                    MasterPainter.instance.PaintObject(extra, uv, null);
                    VerboseLogger.CleaningApply($"applied stroke to {packet.BoatName} surface {packet.ExtraIndex}");
                }
                catch (System.Exception e)
                {
                    VerboseLogger.CleaningApply($"stroke FAILED on {packet.BoatName} surface {packet.ExtraIndex}: {e.Message}");
                }
                return;
            }

            var cleanable = boat.GetCleanable();
            if (cleanable == null)
            {
                VerboseLogger.CleaningApply($"stroke FAILED: no CleanableObject on boat: {packet.BoatName}");
                return;
            }

            // Apply the cleaning stroke
            MasterPainter.instance.PaintObject(cleanable, uv, null);

            VerboseLogger.CleaningApply($"applied stroke to {packet.BoatName}");
        }

        private void ApplyCleanFully(CleanFullyPacket packet)
        {
            var boat = BoatUtility.FindBoatByName(packet.BoatName);
            if (boat == null)
            {
                VerboseLogger.CleaningApply($"fullClean FAILED: boat not found: {packet.BoatName}");
                return;
            }

            var cleanable = boat.GetCleanable();
            if (cleanable == null)
            {
                VerboseLogger.CleaningApply($"fullClean FAILED: no CleanableObject on boat: {packet.BoatName}");
                return;
            }

            // Apply the full clean
            cleanable.CleanFully();

            // (0.41) The shipyard cleans every extra surface after the main hull; the sender only reports the
            // main one, so do the rest here the same way.
            var extras = boat.extraCleanables;
            if (extras != null)
            {
                for (int i = 0; i < extras.Length; i++)
                {
                    if (extras[i] == null || !extras[i].gameObject.activeInHierarchy) continue;
                    try { extras[i].CleanFully(); }
                    catch (System.Exception e) { VerboseLogger.CleaningApply($"fullClean FAILED on {packet.BoatName} surface {i}: {e.Message}"); }
                }
            }

            VerboseLogger.CleaningApply($"CleanFully on {packet.BoatName}");
        }

        // === Join: extra surface dirt (0.41) ===
        //
        // The join snapshot carries each boat's main hull dirt only. 0.41 boats also have extra cleanable
        // surfaces with their own textures, which a joiner otherwise takes from its own phantom save. They go in
        // a separate packet per boat rather than in BoatWorldState, which already holds a PNG per boat plus every
        // item and has to stay under Steam's reliable size limit.

        // Below P2PNetworkManager's 256 KB large-packet warning, with room for the per-surface headers.
        private const int MaxExtraDirtPacketBytes = 200 * 1024;
        private const float PendingExtraDirtTtl = 10f;

        private class PendingExtraDirt
        {
            public readonly Dictionary<int, byte[]> Textures = new Dictionary<int, byte[]>();
            public float Arrived;
        }

        // Boat name -> extra dirt that arrived during the join. Applied once the join is over: Phase A's LoadData
        // can switch part options on, and a surface on a part that has just appeared has not started yet.
        private static readonly Dictionary<string, PendingExtraDirt> _pendingExtraDirt = new Dictionary<string, PendingExtraDirt>();
        private bool _wasInSession;

        private static bool JoinExtraDirtOn => Plugin.JoinExtraDirtConfig == null || Plugin.JoinExtraDirtConfig.Value;

        /// <summary>HOST, join step: send every boat's extra surface dirt to the joining guest.</summary>
        public void SendExtraDirtTo(Steamworks.SteamId target)
        {
            if (!JoinExtraDirtOn) return;

            int sent = 0;
            foreach (var kv in BoatUtility.FindAllBoats())
            {
                var textures = kv.Value != null ? kv.Value.extraTextures : null;
                if (textures == null) continue;

                var indices = new List<byte>();
                var pngs = new List<byte[]>();
                int bytes = 0;
                for (int i = 0; i < textures.Length && i <= byte.MaxValue; i++)
                {
                    if (textures[i] == null) continue;
                    byte[] png;
                    try { png = textures[i].EncodeToPNG(); }
                    catch (System.Exception e)
                    {
                        Plugin.Log.LogWarning($"[CLEANING:COLLECT] Failed to encode dirt of surface {i} on {kv.Key}: {e.Message}");
                        continue;
                    }
                    if (png == null || png.Length == 0) continue;

                    if (indices.Count > 0 && (bytes + png.Length > MaxExtraDirtPacketBytes || indices.Count == byte.MaxValue))
                    {
                        SendExtraDirt(target, kv.Key, indices, pngs);
                        sent++;
                        indices.Clear();
                        pngs.Clear();
                        bytes = 0;
                    }
                    indices.Add((byte)i);
                    pngs.Add(png);
                    bytes += png.Length;
                }

                if (indices.Count > 0)
                {
                    SendExtraDirt(target, kv.Key, indices, pngs);
                    sent++;
                }
            }

            if (sent > 0) Plugin.Log.LogInfo($"[CLEANING:COLLECT] Sent extra surface dirt to the joiner in {sent} packet(s)");
        }

        private static void SendExtraDirt(Steamworks.SteamId target, string boatName, List<byte> indices, List<byte[]> pngs)
        {
            var packet = new BoatExtraDirtPacket
            {
                BoatName = boatName,
                Indices = indices.ToArray(),
                Textures = pngs.ToArray()
            };
            VerboseLogger.CleaningSend($"extraDirt -> {target}, boat={boatName}, surfaces={packet.Indices.Length}");
            Plugin.NetworkManager.SendReliable(target, PacketType.BoatExtraDirt, w =>
                PacketSerializer.WriteBoatExtraDirt(w, packet));
        }

        /// <summary>GUEST: park the host's extra surface dirt until the join apply is over.</summary>
        public void OnRemoteBoatExtraDirt(BoatExtraDirtPacket packet)
        {
            VerboseLogger.CleaningRecv($"extraDirt, boat={packet.BoatName}, surfaces={packet.Indices?.Length ?? 0}");
            if (Plugin.IsHost || !JoinExtraDirtOn) return;
            if (string.IsNullOrEmpty(packet.BoatName) || packet.Indices == null || packet.Textures == null) return;

            if (!_pendingExtraDirt.TryGetValue(packet.BoatName, out var pending))
            {
                pending = new PendingExtraDirt();
                _pendingExtraDirt[packet.BoatName] = pending;
            }
            pending.Arrived = Time.unscaledTime;
            for (int i = 0; i < packet.Indices.Length && i < packet.Textures.Length; i++)
            {
                if (packet.Textures[i] != null && packet.Textures[i].Length > 0)
                    pending.Textures[packet.Indices[i]] = packet.Textures[i];
            }
        }

        private void Update()
        {
            if (!Plugin.IsMultiplayer)
            {
                // Session over: nothing parked may carry into the next one.
                if (_wasInSession) _pendingExtraDirt.Clear();
                _wasInSession = false;
                return;
            }
            _wasInSession = true;

            if (_pendingExtraDirt.Count == 0) return;
            if (BoatSyncManager.IsJoinInProgress || !BoatSyncManager.HasReceivedWorldState) return;

            // Resolve first, expire second, like the SE rig blobs: the clock ran during the join, and a boat
            // that is here gets its dirt however long that took.
            List<string> done = null;
            foreach (var kv in _pendingExtraDirt)
            {
                var boat = BoatUtility.FindBoatByName(kv.Key);
                if (boat != null) ApplyExtraDirt(boat, kv.Value.Textures);
                else if (Time.unscaledTime - kv.Value.Arrived <= PendingExtraDirtTtl) continue;
                (done ?? (done = new List<string>())).Add(kv.Key);
            }
            if (done != null)
            {
                foreach (var name in done) _pendingExtraDirt.Remove(name);
            }
        }

        private static void ApplyExtraDirt(SaveableObject boat, Dictionary<int, byte[]> textures)
        {
            var extras = boat.extraCleanables;
            int applied = 0;
            foreach (var kv in textures)
            {
                var cleanable = extras != null && kv.Key < extras.Length ? extras[kv.Key] : null;
                // A surface on a part option this machine does not show has never started and cannot take it.
                if (cleanable == null || !cleanable.gameObject.activeInHierarchy) continue;

                Texture2D texture = null;
                try
                {
                    texture = new Texture2D(128, 128, TextureFormat.ARGB32, false);
                    texture.LoadImage(kv.Value);
                    cleanable.LoadTexture(texture);
                    applied++;
                }
                catch (System.Exception e)
                {
                    if (texture != null) Object.Destroy(texture);
                    Plugin.Log.LogWarning($"[CLEANING:APPLY] Failed to apply dirt to surface {kv.Key} of {boat.gameObject.name}: {e.Message}");
                }
            }
            Plugin.Log.LogInfo($"[CLEANING:APPLY] Applied {applied} of {textures.Count} extra dirt texture(s) to {boat.gameObject.name}");
        }

        /// <summary>
        /// Reset sync state when disconnecting.
        /// </summary>
        public void Reset()
        {
            IsApplyingRemoteState = false;
            _pendingExtraDirt.Clear();
        }
    }
}
