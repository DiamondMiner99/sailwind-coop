using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using UnityEngine;

namespace SailwindCoop.Debug
{
    /// <summary>
    /// Profiles Harmony patch execution time and call counts.
    /// Uses DebugMode.Enabled - zero overhead when debug mode is off.
    /// </summary>
    public static class PatchProfiler
    {
        private class PatchStats
        {
            public long CallCount;
            public long TotalTicks;
            public Stopwatch Stopwatch = new Stopwatch();
        }

        private static Dictionary<string, PatchStats> _stats = new Dictionary<string, PatchStats>();
        private static float _lastReportTime;
        private const float ReportInterval = 5f;

        // Thread-local current patch for nested tracking
        [System.ThreadStatic]
        private static string _currentPatch;

        // Cached stats for overlay display (updated every ReportInterval)
        private static Dictionary<string, PatchDisplayStats> _cachedDisplayStats = new Dictionary<string, PatchDisplayStats>();
        private static float _cachedTotalMs;
        private static long _cachedTotalCalls;

        public struct PatchDisplayStats
        {
            public string Name;
            public long CallCount;
            public float TotalMs;
            public float AvgMicroseconds;
        }

        /// <summary>
        /// Get cached patch stats for overlay display.
        /// </summary>
        public static IEnumerable<PatchDisplayStats> GetDisplayStats()
        {
            return _cachedDisplayStats.Values.OrderByDescending(s => s.TotalMs);
        }

        /// <summary>
        /// Get total patch overhead in ms (per frame average).
        /// </summary>
        public static float GetTotalMs() => _cachedTotalMs;

        /// <summary>
        /// Get total patch calls per frame.
        /// </summary>
        public static long GetTotalCalls() => _cachedTotalCalls;

        /// <summary>
        /// Call at the START of a patch method.
        /// Returns a token to pass to EndPatch.
        /// </summary>
        public static void Begin(string patchName)
        {
            if (!DebugMode.Enabled) return;

            if (!_stats.TryGetValue(patchName, out var stats))
            {
                stats = new PatchStats();
                _stats[patchName] = stats;
            }

            stats.CallCount++;
            stats.Stopwatch.Restart();
            _currentPatch = patchName;
        }

        /// <summary>
        /// Call at the END of a patch method.
        /// </summary>
        public static void End(string patchName)
        {
            if (!DebugMode.Enabled) return;

            if (_stats.TryGetValue(patchName, out var stats))
            {
                stats.Stopwatch.Stop();
                stats.TotalTicks += stats.Stopwatch.ElapsedTicks;
            }
            _currentPatch = null;
        }

        private static int _frameCount;

        /// <summary>
        /// Call every frame to track frame count.
        /// </summary>
        public static void OnFrame()
        {
            _frameCount++;
        }

        /// <summary>
        /// Call from PerformanceProfiler.Update() to report stats periodically.
        /// </summary>
        public static void Update()
        {
            if (Time.time - _lastReportTime < ReportInterval) return;
            _lastReportTime = Time.time;

            if (_stats.Count == 0 || _frameCount == 0)
            {
                _frameCount = 0;
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine("[PatchProfiler] Patch execution stats (last 5s):");

            double ticksToMs = 1000.0 / Stopwatch.Frequency;

            // Sort by total time descending
            var sorted = _stats
                .OrderByDescending(kvp => kvp.Value.TotalTicks)
                .ToList();

            long totalCalls = 0;
            double totalMs = 0;

            // Update cached display stats
            _cachedDisplayStats.Clear();

            foreach (var kvp in sorted)
            {
                var name = kvp.Key;
                var stats = kvp.Value;
                double ms = stats.TotalTicks * ticksToMs;
                double avgUs = stats.CallCount > 0 ? (ms * 1000.0 / stats.CallCount) : 0;
                float msPerFrame = (float)(ms / _frameCount);

                totalCalls += stats.CallCount;
                totalMs += ms;

                // Cache for overlay (per-frame average)
                _cachedDisplayStats[name] = new PatchDisplayStats
                {
                    Name = name,
                    CallCount = stats.CallCount / _frameCount, // calls per frame
                    TotalMs = msPerFrame,
                    AvgMicroseconds = (float)avgUs
                };

                sb.AppendLine($"  {name}: {stats.CallCount} calls, {ms:F2}ms total, {avgUs:F1}µs/call");
            }

            sb.AppendLine($"  TOTAL: {totalCalls} calls, {totalMs:F2}ms");

            // Cache totals (per-frame average)
            _cachedTotalMs = (float)(totalMs / _frameCount);
            _cachedTotalCalls = totalCalls / _frameCount;

            Plugin.Log.LogInfo(sb.ToString());

            // Reset for next interval
            foreach (var stats in _stats.Values)
            {
                stats.CallCount = 0;
                stats.TotalTicks = 0;
            }
            _frameCount = 0;
        }
    }
}
