using UnityEngine;
using SailwindCoop.Debug;
using SailwindCoop.Networking.Packets;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Manages time synchronization between host and guest.
    /// Host sends TimeStatePacket at 1Hz + on change, guest applies received values.
    /// </summary>
    public class TimeSyncManager : MonoBehaviour
    {
        public static TimeSyncManager Instance { get; private set; }

        private const float SyncInterval = 1.0f; // 1 Hz
        private float _lastSyncTime;

        // Change detection (host only)
        private float _lastTimescale;
        private int _lastDay;

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
            if (!Plugin.IsHost) return;

            Plugin.Profiler?.StartMeasure();

            // Host only: send time state
            bool shouldSend = false;

            // 1Hz heartbeat. (v0.2.25) Halved during a host co-op sleep (Time.time runs 16x under the
            // warp -> ~16Hz real) so the high-freq channels can't saturate a guest's packet budget.
            if (Time.time - _lastSyncTime >= SyncInterval * SleepSyncManager.HostSleepSendIntervalScale)
            {
                shouldSend = true;
            }

            // Immediate on timescale change
            if (Sun.sun != null && Sun.sun.timescale != _lastTimescale)
            {
                shouldSend = true;
                _lastTimescale = Sun.sun.timescale;
            }

            // Immediate on day change
            if (GameState.day != _lastDay)
            {
                shouldSend = true;
                _lastDay = GameState.day;
            }

            if (shouldSend)
            {
                SendTimeState();
                _lastSyncTime = Time.time;
            }

            Plugin.Profiler?.EndMeasure("Time");
        }

        private void SendTimeState()
        {
            if (Sun.sun == null) return;

            var moonPhase = 0f;
            if (Moon.instance != null)
            {
                moonPhase = Moon.instance.currentPhase;
            }

            var packet = new TimeStatePacket
            {
                GlobalTime = Sun.sun.globalTime,
                Timescale = Sun.sun.timescale,
                Day = GameState.day,
                MoonPhase = moonPhase
            };

            VerboseLogger.TimeSend($"globalTime={packet.GlobalTime:F2}, timescale={packet.Timescale:F1}, day={packet.Day}, moon={packet.MoonPhase:F3}");

            Plugin.NetworkManager.SendToAllReliable(PacketType.TimeState, writer =>
            {
                PacketSerializer.WriteTimeState(writer, packet);
            });
        }

        /// <summary>
        /// Called when TimeState packet is received from host (guest only).
        /// </summary>
        public void OnTimeStateReceived(TimeStatePacket packet)
        {
            if (Plugin.IsHost) return; // Host ignores

            VerboseLogger.TimeRecv($"globalTime={packet.GlobalTime:F2}, timescale={packet.Timescale:F1}, day={packet.Day}, moon={packet.MoonPhase:F3}");

            // (v0.2.34) Stale/reordered TimeState guard. Hard-assigning a BACKWARDS clock is not just
            // jitter: rewinding globalTime across a midnight makes the guest's own Sun.Update re-roll the
            // day and re-fire every OnNewDay subscriber (dirt regen, economy re-rolls, daily damage), and
            // the next fresh packet then reflection-fires them AGAIN below - repeated day-event storms
            // during the 16x sleep warp were a prime freeze amplifier. Compare against the LAST APPLIED
            // HOST stamp, never the guest's local clock: the guest's phantom save may legitimately sit on
            // a LATER day than the host's world (comparing locally would then reject every host packet all
            // session). The first packet after join always applies (adopting the host clock, forward or
            // backward, is exactly the intended authority).
            if (_lastAppliedHostDay != int.MinValue &&
                (packet.Day < _lastAppliedHostDay ||
                 (packet.Day == _lastAppliedHostDay && packet.GlobalTime < _lastAppliedHostGlobalTime - 1f)))
            {
                VerboseLogger.TimeRecv($"stale TimeState dropped (packet day={packet.Day} t={packet.GlobalTime:F2} vs last applied day={_lastAppliedHostDay} t={_lastAppliedHostGlobalTime:F2})");
                return;
            }
            _lastAppliedHostDay = packet.Day;
            _lastAppliedHostGlobalTime = packet.GlobalTime;

            // Track day change before applying
            int previousDay = GameState.day;

            // Apply time state
            if (Sun.sun != null)
            {
                Sun.sun.globalTime = packet.GlobalTime;
                Sun.sun.timescale = packet.Timescale;
            }

            GameState.day = packet.Day;

            if (Moon.instance != null)
            {
                // (v0.2.34) Normalize to [0,1): vanilla Moon.Update drains an out-of-range phase with an
                // unguarded `while (currentPhase > 1f) currentPhase -= 1f` - a corrupt/huge network value
                // would spin that loop into a true main-thread hang. Repeat() makes that impossible.
                Moon.instance.currentPhase = Mathf.Repeat(packet.MoonPhase, 1f);
            }

            // Fire OnNewDay event if day changed
            // This ensures all day-based systems update on guest:
            // - CleanableObject.ApplyDailyDirt (dirt accumulation)
            // - BoatDamage.DailyDamage (oakum degradation)
            // - IslandEconomy.RandomizeDemand (market changes)
            // - CurrencyMarket.MarketCycle (exchange rates)
            // - CargoCarrier.RegisterDayPassed (storage fees)
            if (packet.Day > previousDay)
            {
                VerboseLogger.TimeApply($"Day changed {previousDay} -> {packet.Day}, firing OnNewDay event");
                FireOnNewDayEvent();
            }

            VerboseLogger.TimeApply($"Applied time state");
        }

        /// <summary>
        /// Manually fires the Sun.OnNewDay event using reflection.
        /// Events can only be invoked from within their declaring class,
        /// so we access the underlying delegate field directly.
        /// </summary>
        private static void FireOnNewDayEvent()
        {
            try
            {
                // Get the backing field for the event
                var eventField = typeof(Sun).GetField("OnNewDay",
                    System.Reflection.BindingFlags.Static |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic);

                if (eventField != null)
                {
                    var eventDelegate = eventField.GetValue(null) as System.Delegate;
                    eventDelegate?.DynamicInvoke();
                }
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning($"Failed to fire OnNewDay event: {ex.Message}");
            }
        }

        // (v0.2.34) Last APPLIED host time stamp - the reference for the stale-packet guard above.
        // int.MinValue = nothing applied yet this session (first packet always applies).
        private int _lastAppliedHostDay = int.MinValue;
        private float _lastAppliedHostGlobalTime;

        /// <summary>
        /// Reset sync state when disconnecting.
        /// </summary>
        public void Reset()
        {
            _lastSyncTime = 0f;
            _lastTimescale = 0f;
            _lastDay = 0;
            _lastAppliedHostDay = int.MinValue; // (v0.2.34) next session's first TimeState must always apply
            _lastAppliedHostGlobalTime = 0f;
        }
    }
}
