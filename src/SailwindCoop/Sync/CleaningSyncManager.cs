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
        public void OnLocalCleaningStroke(string boatName, Vector2 uv)
        {
            if (!Plugin.IsMultiplayer) return;
            if (IsApplyingRemoteState) return;

            VerboseLogger.CleaningSend($"stroke, boat={boatName}, uv=({uv.x:F2}, {uv.y:F2})");

            var packet = new CleaningStrokePacket
            {
                BoatName = boatName,
                UVX = uv.x,
                UVY = uv.y
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

            var cleanable = boat.GetCleanable();
            if (cleanable == null)
            {
                VerboseLogger.CleaningApply($"stroke FAILED: no CleanableObject on boat: {packet.BoatName}");
                return;
            }

            // Apply the cleaning stroke
            var uv = new Vector2(packet.UVX, packet.UVY);
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

            VerboseLogger.CleaningApply($"CleanFully on {packet.BoatName}");
        }

        /// <summary>
        /// Reset sync state when disconnecting.
        /// </summary>
        public void Reset()
        {
            IsApplyingRemoteState = false;
        }
    }
}
