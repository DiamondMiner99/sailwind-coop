using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UnityEngine;

namespace SailwindCoop.Debug
{
    /// <summary>
    /// Profiler to measure where time is spent each frame.
    /// Data displayed on F8 debug overlay.
    /// </summary>
    public class PerformanceProfiler : MonoBehaviour
    {
        public static PerformanceProfiler Instance { get; private set; }

        private Stopwatch _stopwatch = new Stopwatch();

        // Generic system timing using dictionary
        private Dictionary<string, long> _systemTicks = new Dictionary<string, long>();

        private int _frameCount;

        // Track frame times
        private float _maxDeltaTime;
        private float _totalDeltaTime;

        // Auto-reset interval (seconds)
        private const float ResetInterval = 5f;
        private float _lastResetTime;

        // Cached stats for overlay display
        private float _cachedFps;
        private float _cachedAvgFrameMs;
        private float _cachedMaxFrameMs;
        private Dictionary<string, float> _cachedSystemMs = new Dictionary<string, float>();
        private float _cachedTotalModMs;

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
            // Track frame times
            _frameCount++;
            _totalDeltaTime += Time.deltaTime;
            if (Time.deltaTime > _maxDeltaTime)
                _maxDeltaTime = Time.deltaTime;

            // Track frames for patch profiler
            PatchProfiler.OnFrame();

            // Auto-reset and cache stats periodically
            if (Time.time - _lastResetTime >= ResetInterval)
            {
                CacheAndReset();
                _lastResetTime = Time.time;
            }

            // Report patch profiling stats
            PatchProfiler.Update();
        }

        /// <summary>
        /// Get patch overhead in ms per frame.
        /// </summary>
        public float GetPatchOverheadMs() => PatchProfiler.GetTotalMs();

        /// <summary>
        /// Get combined total (sync managers + patches).
        /// </summary>
        public float GetCombinedTotalMs() => _cachedTotalModMs + PatchProfiler.GetTotalMs();

        // Debug: track measurement state
        private string _currentMeasurement;
        private int _measurementOverlapCount;

        public void StartMeasure()
        {
            // Detect overlapping measurements (bug detector)
            if (_stopwatch.IsRunning && _currentMeasurement != null)
            {
                _measurementOverlapCount++;
                if (_measurementOverlapCount <= 5) // Log first 5 overlaps
                {
                    Plugin.Log.LogWarning($"[Profiler] Measurement overlap! '{_currentMeasurement}' was running when new measurement started");
                }
            }
            _stopwatch.Restart();
            _currentMeasurement = "(unknown)";
        }

        /// <summary>
        /// End measurement and record time for named system.
        /// </summary>
        public void EndMeasure(string systemName)
        {
            _stopwatch.Stop();
            _currentMeasurement = null;

            if (!_systemTicks.ContainsKey(systemName))
                _systemTicks[systemName] = 0;
            _systemTicks[systemName] += _stopwatch.ElapsedTicks;
        }

        // Legacy methods for existing code - redirect to generic EndMeasure
        public void EndMeasureSteamCallbacks() => EndMeasure("Steam");
        public void EndMeasurePacketProcessing() => EndMeasure("Packets");
        public void EndMeasureBoatSync() => EndMeasure("Boat");
        public void EndMeasureControlSync() => EndMeasure("Control");
        public void EndMeasurePlayerSync() => EndMeasure("Player");

        private void CacheAndReset()
        {
            if (_frameCount == 0) return;

            double ticksToMs = 1000.0 / Stopwatch.Frequency;

            _cachedFps = _frameCount / _totalDeltaTime;
            _cachedAvgFrameMs = (_totalDeltaTime / _frameCount) * 1000f;
            _cachedMaxFrameMs = _maxDeltaTime * 1000f;

            _cachedSystemMs.Clear();
            _cachedTotalModMs = 0f;

            var sb = new StringBuilder();
            sb.Append($"[Profiler] {_frameCount} frames, {_cachedFps:F0} FPS, {_cachedAvgFrameMs:F1}ms avg | Systems: ");

            foreach (var kvp in _systemTicks)
            {
                float avgMs = (float)((kvp.Value * ticksToMs) / _frameCount);
                _cachedSystemMs[kvp.Key] = avgMs;
                _cachedTotalModMs += avgMs;
                sb.Append($"{kvp.Key}={avgMs:F2}ms ");
            }

            sb.Append($"| TOTAL={_cachedTotalModMs:F2}ms");
            if (_measurementOverlapCount > 0)
            {
                sb.Append($" | OVERLAPS={_measurementOverlapCount}");
            }
            Plugin.Log.LogInfo(sb.ToString());

            // Reset for next interval
            _systemTicks.Clear();
            _frameCount = 0;
            _totalDeltaTime = 0;
            _maxDeltaTime = 0;
            _measurementOverlapCount = 0;
        }

        /// <summary>
        /// Get current FPS.
        /// </summary>
        public float GetFps() => _cachedFps;

        /// <summary>
        /// Get average frame time in ms.
        /// </summary>
        public float GetAvgFrameMs() => _cachedAvgFrameMs;

        /// <summary>
        /// Get max frame time in ms.
        /// </summary>
        public float GetMaxFrameMs() => _cachedMaxFrameMs;

        /// <summary>
        /// Get total mod time in ms.
        /// </summary>
        public float GetTotalModMs() => _cachedTotalModMs;

        /// <summary>
        /// Get system times dictionary (name -> ms per frame).
        /// </summary>
        public Dictionary<string, float> GetSystemTimes() => _cachedSystemMs;

        /// <summary>
        /// Calculate FPS impact if system was removed.
        /// Returns estimated FPS gain.
        /// </summary>
        public float CalculateFpsImpact(float systemMs)
        {
            if (_cachedAvgFrameMs <= 0 || systemMs <= 0) return 0f;

            float currentFps = 1000f / _cachedAvgFrameMs;
            float fpsWithout = 1000f / (_cachedAvgFrameMs - systemMs);
            return fpsWithout - currentFps;
        }
    }
}
