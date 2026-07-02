using System.Linq;
using UnityEngine;
using HarmonyLib;
using Crest;
using Steamworks;
using SailwindCoop.Debug;
using SailwindCoop.Networking.Packets;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Manages weather synchronization between host and guest.
    ///
    /// Strategy: Sync storm positions and wind, let guest's weather system run naturally.
    /// Both players are on the same boat, so same distance to storms = same weather.
    ///
    /// Host sends at 2Hz:
    /// - Wind direction/speed
    /// - Storm positions
    /// - Storm active states
    ///
    /// Guest applies positions and lets WeatherStorms.Update() run naturally at 20Hz,
    /// which calls ApplyStorm() to calculate blendedSet based on distance to storms.
    /// </summary>
    public class WeatherSyncManager : MonoBehaviour
    {
        public static WeatherSyncManager Instance { get; private set; }

        /// <summary>
        /// Set to true to pause weather sync (used during join storm effect)
        /// </summary>
        public static bool PauseSync { get; set; }

        private const float SyncInterval = 0.5f; // 2 Hz
        private float _lastSyncTime;

        // Guest wind interpolation
        private Vector3 _targetWind;
        private bool _hasReceivedState;

        // Guest ocean time sync (wave phase)
        private TimeProviderCustom _oceanTimeProvider;

        // ===== Crest wave drive sync =====
        // The LIVE ocean is the Crest stack: OceanUpdaterCrest crossfades two inertia
        // ShapeGerstnerBatched waves + a wind wave. Wave PHASES are already deterministic
        // (scene-serialized _randomSeed) and TIME is synced via the guest time provider, so the only
        // divergence is OceanUpdaterCrest's per-client crossfade state. We sync those INPUTS and let
        // the guest's own DCTInertiaUpdate recompute weights locally (keeps per-player
        // distanceToLand/eyesFullyClosed damping).
        private static readonly AccessTools.FieldRef<OceanUpdaterCrest, float> CurrentMultRef =
            AccessTools.FieldRefAccess<OceanUpdaterCrest, float>("currentMult");
        private static readonly AccessTools.FieldRef<OceanUpdaterCrest, int> WavesUpRef =
            AccessTools.FieldRefAccess<OceanUpdaterCrest, int>("wavesUp");
        private static readonly AccessTools.FieldRef<OceanUpdaterCrest, int> WavesDownRef =
            AccessTools.FieldRefAccess<OceanUpdaterCrest, int>("wavesDown");
        private static readonly AccessTools.FieldRef<OceanUpdaterCrest, float> TargetInertiaAngleRef =
            AccessTools.FieldRefAccess<OceanUpdaterCrest, float>("targetInertiaAngle");
        private static readonly AccessTools.FieldRef<OceanUpdaterCrest, ShapeGerstnerBatched> WindWavesRef =
            AccessTools.FieldRefAccess<OceanUpdaterCrest, ShapeGerstnerBatched>("windWaves");
        private static readonly AccessTools.FieldRef<OceanUpdaterCrest, ShapeGerstnerBatched[]> InertiaWavesRef =
            AccessTools.FieldRefAccess<OceanUpdaterCrest, ShapeGerstnerBatched[]>("inertiaWaves");

        // Cached scene instances (Unity-null when a scene reload destroys them -> refetch lazily).
        private static OceanUpdaterCrest _oceanUpdaterCrest;
        private static WavesInertia _wavesInertia;

        // Seed-once latch for the active inertia wave's direction: the swap branch in ApplyCrestState only
        // writes _windDirectionAngle when wavesUp CHANGES, so on ~half of joins (guest's wavesUp already
        // equal to host's) the active wave would keep the guest's solo-save direction indefinitely.
        // Cleared whenever the cached _oceanUpdaterCrest is (re)fetched or Reset.
        private static bool _crestDirectionSeeded;

        private static OceanUpdaterCrest GetOceanUpdaterCrest()
        {
            if (_oceanUpdaterCrest == null)
            {
                _oceanUpdaterCrest = Object.FindObjectOfType<OceanUpdaterCrest>();
                _crestDirectionSeeded = false;
            }
            return _oceanUpdaterCrest;
        }

        private static WavesInertia GetWavesInertia()
        {
            if (_wavesInertia == null)
                _wavesInertia = Object.FindObjectOfType<WavesInertia>();
            return _wavesInertia;
        }

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

            Plugin.Profiler?.StartMeasure();

            if (Plugin.IsHost)
            {
                SendWeatherState();
            }
            else
            {
                ApplyWindInterpolation();
                AdvanceOceanTime();
            }

            Plugin.Profiler?.EndMeasure("Weather");
        }

        private void SendWeatherState()
        {
            if (Time.time - _lastSyncTime < SyncInterval) return;
            _lastSyncTime = Time.time;

            var packet = CollectWeatherState();

            VerboseLogger.WeatherSend(
                $"wind={packet.Wind}, storms={packet.StormPositions?.Length ?? 0}, activeStorm={packet.ActiveStormIndex}",
                throttle: true);

            Plugin.NetworkManager.SendToAllReliable(PacketType.WeatherState, writer =>
            {
                PacketSerializer.WriteWeatherState(writer, packet);
            });
        }

        /// <summary>
        /// Join-time one-shot: send the current weather/wave state to ONE joining guest so
        /// WavesInertia + Crest crossfade state land immediately instead of waiting up to SyncInterval
        /// for the periodic broadcast. Same payload/serializer as the broadcast, targeted.
        /// </summary>
        public void SendWeatherStateTo(SteamId target)
        {
            if (!Plugin.IsMultiplayer || !Plugin.IsHost) return;

            var packet = CollectWeatherState();
            VerboseLogger.WeatherSend($"join one-shot to {target}: wind={packet.Wind}, waveInertia={packet.WaveInertia:F1}");
            Plugin.NetworkManager.SendReliable(target, PacketType.WeatherState, writer =>
            {
                PacketSerializer.WriteWeatherState(writer, packet);
            });
        }

        /// <summary>
        /// Collects current weather state from the host's world.
        /// Also used by initial world state sync.
        /// </summary>
        public static WeatherStatePacket CollectWeatherState()
        {
            var packet = new WeatherStatePacket
            {
                Wind = Wind.currentWind,
                ActiveStormIndex = -1
            };

            // Get storm positions and active storm index
            packet.StormPositions = CollectStormPositions(out int activeIndex);
            packet.ActiveStormIndex = activeIndex;

            // Collect WavesInertia state for wave sync
            var wavesInertia = GetWavesInertia();
            if (wavesInertia != null)
            {
                packet.WaveDirection = wavesInertia.transform.rotation;
                packet.WaveInertia = wavesInertia.currentInertia;
                packet.WaveMagnitude = wavesInertia.currentMagnitude;
            }

            // Collect Crest ocean time for wave phase sync
            if (OceanRenderer.Instance != null)
            {
                packet.OceanTime = OceanRenderer.Instance.CurrentTime;
            }

            // Collect OceanUpdaterCrest crossfade inputs (the live Crest wave drive)
            var updater = GetOceanUpdaterCrest();
            if (updater != null)
            {
                packet.HostCurrentMult = CurrentMultRef(updater);
                packet.HostWavesUp = (byte)WavesUpRef(updater);
                packet.HostTargetInertiaAngle = TargetInertiaAngleRef(updater);
                var windWaves = WindWavesRef(updater);
                packet.HostWindWavesWeight = windWaves != null ? windWaves._weight : 0f;
            }

            return packet;
        }

        private static Vector3[] CollectStormPositions(out int activeStormIndex)
        {
            activeStormIndex = -1;

            var weatherStorms = WeatherStorms.instance;
            if (weatherStorms == null) return new Vector3[0];

            var traverse = Traverse.Create(weatherStorms);
            var stormArray = traverse.Field("storms").GetValue<WanderingStorm[]>();

            if (stormArray == null || stormArray.Length == 0) return new Vector3[0];

            // Get the current active storm
            var currentStorm = traverse.Field("currentStorm").GetValue<WanderingStorm>();

            var positions = new Vector3[stormArray.Length];
            for (int i = 0; i < stormArray.Length; i++)
            {
                var storm = stormArray[i];
                positions[i] = storm?.transform.position ?? Vector3.zero;

                // Check if this is the active storm
                if (storm != null && storm == currentStorm && storm.active)
                {
                    activeStormIndex = i;
                }
            }
            return positions;
        }

        // ========== Guest-side ==========

        private void ApplyWindInterpolation()
        {
            if (!_hasReceivedState) return;

            // Smoothly interpolate wind (lerp rate 2 = ~0.5s to reach target)
            Wind.currentWind = Vector3.Lerp(Wind.currentWind, _targetWind, Time.deltaTime * 2f);

            // Update Wind.transform.rotation to match currentWind direction.
            // This is normally done in Wind.Update() which we disable on guest.
            // WavesInertia.Update() reads Wind.transform.rotation for wave direction.
            if (Wind.instance != null && Wind.currentWind.sqrMagnitude > 0.01f)
            {
                Wind.instance.transform.LookAt(Wind.instance.transform.position + Wind.currentWind);
            }
        }

        /// <summary>
        /// Called when guest receives weather state packet from host.
        /// </summary>
        public void OnWeatherStateReceived(WeatherStatePacket packet)
        {
            if (PauseSync) return;

            VerboseLogger.WeatherRecv(
                $"wind={packet.Wind}, storms={packet.StormPositions?.Length ?? 0}, activeStorm={packet.ActiveStormIndex}",
                throttle: true);

            // Store target wind for interpolation
            _targetWind = packet.Wind;
            _hasReceivedState = true;

            // Apply storm positions and active states
            // WeatherStorms.Update() runs naturally and will use these positions
            ApplyStormPositions(packet.StormPositions, packet.ActiveStormIndex);

            // Apply WavesInertia state from host
            ApplyWavesInertia(packet);

            // Sync ocean time (wave phase) - setup on first receive, then periodic re-sync
            if (packet.OceanTime > 0)
            {
                if (_oceanTimeProvider == null)
                {
                    SetupOceanTimeProvider(packet.OceanTime);
                }
                else
                {
                    // Periodic re-sync to correct drift from Time.deltaTime differences
                    _oceanTimeProvider._time = packet.OceanTime;
                }
            }

            // Apply OceanUpdaterCrest crossfade inputs
            ApplyCrestState(packet);

            VerboseLogger.WeatherApply($"wind lerping to {packet.Wind}, activeStorm={packet.ActiveStormIndex}, waveInertia={packet.WaveInertia:F1}", throttle: true);
        }

        private void ApplyStormPositions(Vector3[] positions, int activeStormIndex)
        {
            if (positions == null || positions.Length == 0) return;

            var weatherStorms = WeatherStorms.instance;
            if (weatherStorms == null) return;

            var traverse = Traverse.Create(weatherStorms);
            var stormArray = traverse.Field("storms").GetValue<WanderingStorm[]>();
            if (stormArray == null || stormArray.Length == 0) return;

            for (int i = 0; i < Mathf.Min(stormArray.Length, positions.Length); i++)
            {
                var storm = stormArray[i];
                if (storm != null)
                {
                    // Update position (storms don't move on their own - WanderingStorm.Update blocked)
                    storm.transform.position = positions[i];

                    // Set active flag based on host's active storm
                    storm.active = (i == activeStormIndex);
                }
            }

            // WeatherStorms.Update() runs naturally at 20Hz and will:
            // 1. Call FindClosestStorm() to find nearest active storm
            // 2. Call ApplyStorm() to calculate blendedSet based on distance
            // Since we synced positions and active states, guest calculates same weather as host
        }

        /// <summary>
        /// Apply WavesInertia state from host using the game's LoadInertia method.
        /// This keeps wave height/direction in sync between host and guest.
        /// </summary>
        private void ApplyWavesInertia(WeatherStatePacket packet)
        {
            var wavesInertia = GetWavesInertia();
            if (wavesInertia == null) return;

            // Use the game's existing method for loading inertia state
            wavesInertia.LoadInertia(packet.WaveDirection, packet.WaveInertia, packet.WaveMagnitude);
        }

        /// <summary>
        /// Write the host's OceanUpdaterCrest crossfade INPUTS into the guest's instance.
        /// The guest's own Update()/DCTInertiaUpdate keeps running and recomputes the actual
        /// ShapeGerstnerBatched weights from these inputs, preserving the per-player local
        /// distanceToLand/eyesFullyClosed damping. We never force the computed weights themselves.
        /// </summary>
        private void ApplyCrestState(WeatherStatePacket packet)
        {
            var updater = GetOceanUpdaterCrest();
            if (updater == null) return;

            int hostUp = packet.HostWavesUp == 0 ? 0 : 1;
            TargetInertiaAngleRef(updater) = packet.HostTargetInertiaAngle;

            if (WavesUpRef(updater) != hostUp)
            {
                // Host cycled to the other inertia wave slot; mirror what DCTInertiaNewCycle does so the
                // newly-up wave gets the host's direction (Update alone never sets _windDirectionAngle).
                WavesUpRef(updater) = hostUp;
                WavesDownRef(updater) = 1 - hostUp;
                var inertiaWaves = InertiaWavesRef(updater);
                if (inertiaWaves != null && inertiaWaves.Length > hostUp && inertiaWaves[hostUp] != null)
                {
                    inertiaWaves[hostUp]._windDirectionAngle = 0f - packet.HostTargetInertiaAngle;
                }
                _crestDirectionSeeded = true;
            }
            else if (!_crestDirectionSeeded)
            {
                // First apply after (re)fetching the updater with wavesUp ALREADY matching the host's: seed
                // the active wave's direction unconditionally once, else it keeps the guest's solo-save value
                // until the host's next cycle swap (potentially minutes).
                var inertiaWaves = InertiaWavesRef(updater);
                if (inertiaWaves != null && inertiaWaves.Length > hostUp && inertiaWaves[hostUp] != null)
                {
                    inertiaWaves[hostUp]._windDirectionAngle = 0f - packet.HostTargetInertiaAngle;
                }
                _crestDirectionSeeded = true;
            }
            CurrentMultRef(updater) = packet.HostCurrentMult;

            // Snap the wind-wave weight lerp state to the host's; the guest's own lerp continues from here.
            var windWaves = WindWavesRef(updater);
            if (windWaves != null)
            {
                windWaves._weight = packet.HostWindWavesWeight;
            }
        }

        /// <summary>
        /// Set up custom ocean time provider on guest to sync wave phase with host.
        /// Called once on first weather state receive.
        /// </summary>
        private void SetupOceanTimeProvider(float hostOceanTime)
        {
            if (_oceanTimeProvider != null) return; // Already set up

            var ocean = OceanRenderer.Instance;
            if (ocean == null)
            {
                Plugin.Log.LogWarning("OceanRenderer.Instance is null, cannot set up ocean time sync");
                return;
            }

            // Create TimeProviderCustom component on OceanRenderer
            _oceanTimeProvider = ocean.gameObject.AddComponent<TimeProviderCustom>();
            _oceanTimeProvider._time = hostOceanTime;
            _oceanTimeProvider._deltaTime = Time.deltaTime;

            // Set it as the active time provider via reflection
            var traverse = Traverse.Create(ocean);
            traverse.Field("_timeProvider").SetValue(_oceanTimeProvider);

            Plugin.Log.LogInfo($"Ocean time provider set up, initial time: {hostOceanTime:F2}");
        }

        /// <summary>
        /// Advance ocean time locally each frame on guest.
        /// This keeps wave phase in sync after initial sync.
        /// </summary>
        private void AdvanceOceanTime()
        {
            if (_oceanTimeProvider == null) return;

            _oceanTimeProvider._time += Time.deltaTime;
            _oceanTimeProvider._deltaTime = Time.deltaTime;
        }

        /// <summary>
        /// Reset sync state when disconnecting from multiplayer session.
        /// </summary>
        public void Reset()
        {
            _lastSyncTime = 0f;
            _hasReceivedState = false;
            _targetWind = Vector3.zero;
            Patches.WeatherPatches.LocalWeatherOverride = false;

            // Clean up ocean time provider
            if (_oceanTimeProvider != null)
            {
                // Restore default time provider
                var ocean = OceanRenderer.Instance;
                if (ocean != null)
                {
                    var traverse = Traverse.Create(ocean);
                    traverse.Field("_timeProvider").SetValue(null);
                }
                Object.Destroy(_oceanTimeProvider);
                _oceanTimeProvider = null;
            }

            // Drop cached scene instances (re-resolved lazily next session)
            _oceanUpdaterCrest = null;
            _wavesInertia = null;
            _crestDirectionSeeded = false;
        }
    }
}
