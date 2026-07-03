using System.Collections.Generic;
using Steamworks;

namespace SailwindCoop.Networking
{
    /// <summary>
    /// Tiny static store for per-peer network diagnostics, fed by the PingRequest/PingReply loop in
    /// Plugin (2s cadence, unreliable path) and read by the Shift+F8 DebugOverlay. Exponentially
    /// smoothed (alpha 0.3) so a single delayed echo doesn't make the displayed number jump around.
    /// Main-thread only (packet handlers and OnGUI both run there), so no locking.
    /// </summary>
    public static class NetworkStats
    {
        private const float Smoothing = 0.3f;

        /// <summary>Smoothed round-trip time in milliseconds, keyed by peer SteamId.</summary>
        public static readonly Dictionary<SteamId, float> PingMs = new Dictionary<SteamId, float>();

        public static void RecordPing(SteamId peer, float rttMs)
        {
            if (rttMs < 0f) return; // clock went backwards / garbage echo - drop the sample
            PingMs[peer] = PingMs.TryGetValue(peer, out var prev)
                ? prev + Smoothing * (rttMs - prev)
                : rttMs; // first sample: seed directly instead of smoothing up from zero
        }

        public static void Forget(SteamId peer) => PingMs.Remove(peer);

        public static void Clear() => PingMs.Clear();
    }
}
