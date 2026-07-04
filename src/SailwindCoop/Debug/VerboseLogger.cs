using System;
using System.IO;
using BepInEx;

namespace SailwindCoop.Debug
{
    /// <summary>
    /// Always-on verbose logging to separate file for debugging multiplayer issues.
    /// Log format: [HH:mm:ss.fff] [SYSTEM:DIRECTION] message
    /// </summary>
    public static class VerboseLogger
    {
        private static StreamWriter _writer;
        private static string _logPath;
        private static bool _initialized;

        // Throttling for high-frequency events (2Hz = 500ms)
        private static DateTime _lastPlayerLog = DateTime.MinValue;
        private static DateTime _lastBoatTransformLog = DateTime.MinValue;
        private const int ThrottleMs = 500;

        // Per-session verbose logs: the old single fixed filename (append: false) meant every session
        // OVERWROTE the previous log - which destroyed bug evidence twice. Each session gets a timestamped
        // file instead, and the oldest are pruned so the folder never grows unbounded.
        private const string LogFilePrefix = "SailwindCoop-verbose-";
        private const int MaxLogFiles = 10;

        public static void Initialize()
        {
            if (_initialized) return;

            try
            {
                PruneOldLogs();
                var fileName = LogFilePrefix + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log";
                _logPath = Path.Combine(Paths.BepInExRootPath, fileName);
                _writer = new StreamWriter(_logPath, append: false) { AutoFlush = true };
                _initialized = true;

                var role = Plugin.LobbyManager?.IsHost == true ? "Host" : "Guest";
                Log("SYSTEM", "EVENT", $"Session started, role={role}, version={Plugin.PluginVersion}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"VerboseLogger failed to initialize: {ex.Message}");
            }
        }

        /// <summary>
        /// Keep the newest (MaxLogFiles - 1) timestamped verbose logs so the session file about to be
        /// created lands within the cap. The yyyyMMdd-HHmmss names sort chronologically by filename, so a
        /// plain sort puts the oldest first. Best-effort: a locked/undeletable file never blocks logging.
        /// </summary>
        private static void PruneOldLogs()
        {
            try
            {
                // One-time cleanup of the pre-timestamp fixed-name log, which the prefix glob never matches.
                var legacy = Path.Combine(Paths.BepInExRootPath, "SailwindCoop-verbose.log");
                if (File.Exists(legacy))
                {
                    try { File.Delete(legacy); } catch { }
                }

                var files = Directory.GetFiles(Paths.BepInExRootPath, LogFilePrefix + "*.log");
                if (files.Length < MaxLogFiles) return;
                Array.Sort(files);
                int deleteCount = files.Length - (MaxLogFiles - 1);
                for (int i = 0; i < deleteCount; i++)
                {
                    try { File.Delete(files[i]); } catch { }
                }
            }
            catch { }
        }

        public static void Shutdown()
        {
            if (!_initialized) return;

            try
            {
                Log("SYSTEM", "EVENT", "Session ended");
                _writer?.Flush();
                _writer?.Close();
                _writer = null;
                _initialized = false;
            }
            catch { }
        }

        public static void Log(string system, string direction, string message)
        {
            if (!DebugMode.Enabled) return;
            if (!_initialized || _writer == null) return;

            try
            {
                var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
                _writer.WriteLine($"[{timestamp}] [{system}:{direction}] {message}");
            }
            catch { }
        }

        // ============ CONTROL ============

        public static void ControlLocal(string message)
        {
            Log("CONTROL", "LOCAL", message);
        }

        public static void ControlSend(string message)
        {
            Log("CONTROL", "SEND", message);
        }

        public static void ControlRecv(string message)
        {
            Log("CONTROL", "RECV", message);
        }

        public static void ControlApply(string message)
        {
            Log("CONTROL", "APPLY", message);
        }

        public static void ControlEvent(string message) => Log("CONTROL", "EVENT", message);

        // ============ BOAT ============

        public static void BoatSend(string message, bool throttle = false)
        {
            if (throttle && !ShouldLogBoatTransform()) return;
            Log("BOAT", "SEND", message);
        }

        public static void BoatRecv(string message, bool throttle = false)
        {
            if (throttle && !ShouldLogBoatTransform()) return;
            Log("BOAT", "RECV", message);
        }

        public static void BoatApply(string message)
        {
            Log("BOAT", "APPLY", message);
        }

        public static void BoatEvent(string message)
        {
            Log("BOAT", "EVENT", message);
        }

        private static bool ShouldLogBoatTransform()
        {
            var now = DateTime.Now;
            if ((now - _lastBoatTransformLog).TotalMilliseconds < ThrottleMs)
                return false;
            _lastBoatTransformLog = now;
            return true;
        }

        // ============ PLAYER ============

        public static void PlayerLocal(string message, bool throttle = false)
        {
            if (throttle && !ShouldLogPlayer()) return;
            Log("PLAYER", "LOCAL", message);
        }

        public static void PlayerSend(string message, bool throttle = false)
        {
            if (throttle && !ShouldLogPlayer()) return;
            Log("PLAYER", "SEND", message);
        }

        public static void PlayerRecv(string message, bool throttle = false)
        {
            if (throttle && !ShouldLogPlayer()) return;
            Log("PLAYER", "RECV", message);
        }

        public static void PlayerApply(string message)
        {
            Log("PLAYER", "APPLY", message);
        }

        public static void PlayerEvent(string message)
        {
            Log("PLAYER", "EVENT", message);
        }

        private static bool ShouldLogPlayer()
        {
            var now = DateTime.Now;
            if ((now - _lastPlayerLog).TotalMilliseconds < ThrottleMs)
                return false;
            _lastPlayerLog = now;
            return true;
        }

        // ============ LOBBY ============

        public static void LobbyEvent(string message)
        {
            Log("LOBBY", "EVENT", message);
        }

        // ============ WEATHER ============

        private static DateTime _lastWeatherLog = DateTime.MinValue;

        public static void WeatherSend(string message, bool throttle = false)
        {
            if (throttle && !ShouldLogWeather()) return;
            Log("WEATHER", "SEND", message);
        }

        public static void WeatherRecv(string message, bool throttle = false)
        {
            if (throttle && !ShouldLogWeather()) return;
            Log("WEATHER", "RECV", message);
        }

        public static void WeatherApply(string message, bool throttle = false)
        {
            if (throttle && !ShouldLogWeather()) return;
            Log("WEATHER", "APPLY", message);
        }

        private static bool ShouldLogWeather()
        {
            var now = DateTime.Now;
            if ((now - _lastWeatherLog).TotalMilliseconds < ThrottleMs)
                return false;
            _lastWeatherLog = now;
            return true;
        }

        // ============ TIME ============

        public static void TimeSend(string message)
        {
            Log("TIME", "SEND", message);
        }

        public static void TimeRecv(string message)
        {
            Log("TIME", "RECV", message);
        }

        public static void TimeApply(string message)
        {
            Log("TIME", "APPLY", message);
        }

        // ============ SURVIVAL ============

        private static DateTime _lastSurvivalLog = DateTime.MinValue;

        public static void SurvivalSend(string message, bool throttle = false)
        {
            if (throttle && !ShouldLogSurvival()) return;
            Log("SURVIVAL", "SEND", message);
        }

        public static void SurvivalRecv(string message, bool throttle = false)
        {
            if (throttle && !ShouldLogSurvival()) return;
            Log("SURVIVAL", "RECV", message);
        }

        public static void SurvivalApply(string message)
        {
            Log("SURVIVAL", "APPLY", message);
        }

        public static void SurvivalEvent(string message)
        {
            Log("SURVIVAL", "EVENT", message);
        }

        private static bool ShouldLogSurvival()
        {
            var now = DateTime.Now;
            if ((now - _lastSurvivalLog).TotalMilliseconds < ThrottleMs)
                return false;
            _lastSurvivalLog = now;
            return true;
        }

        // ============ ITEM ============

        public static void ItemLocal(string message) => Log("ITEM", "LOCAL", message);
        public static void ItemSend(string message) => Log("ITEM", "SEND", message);
        public static void ItemRecv(string message) => Log("ITEM", "RECV", message);
        public static void ItemApply(string message) => Log("ITEM", "APPLY", message);

        // ============ SLEEP ============

        public static void SleepLocal(string message) => Log("SLEEP", "LOCAL", message);
        public static void SleepSend(string message) => Log("SLEEP", "SEND", message);
        public static void SleepRecv(string message) => Log("SLEEP", "RECV", message);
        public static void SleepApply(string message) => Log("SLEEP", "APPLY", message);
        public static void SleepEvent(string message) => Log("SLEEP", "EVENT", message);

        // ============ DAMAGE ============

        private static DateTime _lastDamageLog = DateTime.MinValue;

        public static void DamageLocal(string message) => Log("DAMAGE", "LOCAL", message);
        public static void DamageSend(string message, bool throttle = false)
        {
            if (throttle && !ShouldLogDamage()) return;
            Log("DAMAGE", "SEND", message);
        }
        public static void DamageRecv(string message) => Log("DAMAGE", "RECV", message);
        public static void DamageApply(string message) => Log("DAMAGE", "APPLY", message);
        public static void DamageEvent(string message) => Log("DAMAGE", "EVENT", message);

        private static bool ShouldLogDamage()
        {
            var now = DateTime.Now;
            if ((now - _lastDamageLog).TotalMilliseconds < ThrottleMs)
                return false;
            _lastDamageLog = now;
            return true;
        }

        // ============ SHIPYARD ============

        private static DateTime _lastShipyardLog = DateTime.MinValue;

        public static void ShipyardPoll(string message, bool throttle = false)
        {
            if (throttle && !ShouldLogShipyard()) return;
            Log("SHIPYARD", "POLL", message);
        }

        public static void ShipyardSend(string message)
        {
            Log("SHIPYARD", "SEND", message);
        }

        public static void ShipyardRecv(string message)
        {
            Log("SHIPYARD", "RECV", message);
        }

        public static void ShipyardApply(string message)
        {
            Log("SHIPYARD", "APPLY", message);
        }

        private static bool ShouldLogShipyard()
        {
            var now = DateTime.Now;
            if ((now - _lastShipyardLog).TotalMilliseconds < ThrottleMs)
                return false;
            _lastShipyardLog = now;
            return true;
        }

        // ============ RECOVERY ============

        public static void RecoveryEvent(string message) => Log("RECOVERY", "EVENT", message);
        public static void RecoverySend(string message) => Log("RECOVERY", "SEND", message);
        public static void RecoveryRecv(string message) => Log("RECOVERY", "RECV", message);
        public static void RecoveryApply(string message) => Log("RECOVERY", "APPLY", message);

        // ============ FISHING ============

        private static DateTime _lastFishingLog = DateTime.MinValue;

        public static void FishingLocal(string message)
        {
            Log("FISHING", "LOCAL", message);
        }

        public static void FishingSend(string message, bool throttle = false)
        {
            if (throttle && !ShouldLogFishing()) return;
            Log("FISHING", "SEND", message);
        }

        public static void FishingRecv(string message, bool throttle = false)
        {
            if (throttle && !ShouldLogFishing()) return;
            Log("FISHING", "RECV", message);
        }

        public static void FishingApply(string message)
        {
            Log("FISHING", "APPLY", message);
        }

        public static void FishingEvent(string message)
        {
            Log("FISHING", "EVENT", message);
        }

        private static bool ShouldLogFishing()
        {
            var now = DateTime.Now;
            if ((now - _lastFishingLog).TotalMilliseconds < ThrottleMs)
                return false;
            _lastFishingLog = now;
            return true;
        }

        // === Navigation ===

        public static void NavLocal(string message)
        {
            Log("NAV", "LOCAL", message);
        }

        public static void NavSend(string message)
        {
            Log("NAV", "SEND", message);
        }

        public static void NavRecv(string message)
        {
            Log("NAV", "RECV", message);
        }

        public static void NavApply(string message)
        {
            Log("NAV", "APPLY", message);
        }

        public static void NavEvent(string message)
        {
            Log("NAV", "EVENT", message);
        }

        // ============ CLEANING ============

        public static void CleaningSend(string message)
        {
            Log("CLEANING", "SEND", message);
        }

        public static void CleaningRecv(string message)
        {
            Log("CLEANING", "RECV", message);
        }

        public static void CleaningApply(string message)
        {
            Log("CLEANING", "APPLY", message);
        }

        public static void CleaningEvent(string message)
        {
            Log("CLEANING", "EVENT", message);
        }

        // ============ COOKING ============

        private static DateTime _lastCookingLog = DateTime.MinValue;

        public static void CookingPoll(string message)
        {
            if (!ShouldLogCooking()) return;
            Log("COOKING", "POLL", message);
        }

        public static void CookingSend(string message, bool throttle = false)
        {
            if (throttle && !ShouldLogCooking()) return;
            Log("COOKING", "SEND", message);
        }

        public static void CookingRecv(string message, bool throttle = false)
        {
            if (throttle && !ShouldLogCooking()) return;
            Log("COOKING", "RECV", message);
        }

        public static void CookingApply(string message)
        {
            Log("COOKING", "APPLY", message);
        }

        public static void CookingRequest(string message)
        {
            Log("COOKING", "REQUEST", message);
        }

        public static void CookingEvent(string message)
        {
            Log("COOKING", "EVENT", message);
        }

        private static bool ShouldLogCooking()
        {
            var now = DateTime.Now;
            if ((now - _lastCookingLog).TotalMilliseconds < ThrottleMs)
                return false;
            _lastCookingLog = now;
            return true;
        }

        // ============ NPC BOAT ============

        private static DateTime _lastNPCBoatLog = DateTime.MinValue;

        public static void NPCBoatPoll(string message)
        {
            Log("NPCBOAT", "POLL", message);
        }

        public static void NPCBoatSend(string message, bool throttle = false)
        {
            if (throttle)
            {
                var now = DateTime.Now;
                if ((now - _lastNPCBoatLog).TotalMilliseconds < ThrottleMs) return;
                _lastNPCBoatLog = now;
            }
            Log("NPCBOAT", "SEND", message);
        }

        public static void NPCBoatRecv(string message, bool throttle = false)
        {
            if (throttle)
            {
                var now = DateTime.Now;
                if ((now - _lastNPCBoatLog).TotalMilliseconds < ThrottleMs) return;
                _lastNPCBoatLog = now;
            }
            Log("NPCBOAT", "RECV", message);
        }

        public static void NPCBoatApply(string message)
        {
            Log("NPCBOAT", "APPLY", message);
        }

        // ============ TELEPORT TRACING ============

        /// <summary>
        /// Log teleport-related diagnostic info.
        /// Traces rare boat/capsule teleport behavior for user reports.
        /// </summary>
        public static void TeleportDebug(string message)
        {
            Log("TELEPORT", "DEBUG", message);
        }
    }
}
