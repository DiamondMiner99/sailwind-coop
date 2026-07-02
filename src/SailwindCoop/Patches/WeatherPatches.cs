using HarmonyLib;
using SailwindCoop.Debug;

namespace SailwindCoop.Patches
{
    /// <summary>
    /// Minimal weather patches for multiplayer sync.
    ///
    /// Strategy: Sync storm positions and wind, let guest's weather system run naturally.
    /// Both players are on the same boat, so same distance to storms = same weather.
    ///
    /// We only need to:
    /// 1. Block Wind.Update - wind direction comes from host
    /// 2. Block WanderingStorm.Update - storm positions come from host (storms don't move on their own)
    ///
    /// We let WeatherStorms.Update run normally - it calls ApplyStorm() at 20Hz which
    /// calculates blendedSet based on distance to storms. Since we sync storm positions,
    /// the guest calculates the same weather as host.
    ///
    /// The legacy FFT 'Ocean' class is never instantiated in shipped Sailwind; the LIVE ocean is the
    /// Crest stack (OceanRenderer + ShapeGerstnerBatched, driven by OceanUpdaterCrest + WavesInertia),
    /// so wave sync targets that state in WeatherSyncManager instead.
    /// </summary>
    public static class WeatherPatches
    {
        /// <summary>
        /// Set to true during join storm effect to let guest control its own weather temporarily
        /// </summary>
        public static bool LocalWeatherOverride { get; set; }

        /// <summary>
        /// Skip wind calculation on guest - host sends authoritative wind.
        /// </summary>
        [HarmonyPatch(typeof(Wind), "Update")]
        public static class WindUpdatePatch
        {
            [HarmonyPrefix]
            public static bool Prefix()
            {
                PatchProfiler.Begin("Wind.Update");
                if (!Plugin.IsMultiplayer)
                {
                    PatchProfiler.End("Wind.Update");
                    return true;
                }
                if (LocalWeatherOverride)
                {
                    PatchProfiler.End("Wind.Update");
                    return true;
                }
                PatchProfiler.End("Wind.Update");
                return Plugin.IsHost;
            }
        }

        /// <summary>
        /// Skip storm movement on guest - positions come from host.
        /// The storm positions are synced at 2Hz, and WeatherStorms.Update() runs normally
        /// to calculate weather based on those positions.
        /// </summary>
        [HarmonyPatch(typeof(WanderingStorm), "Update")]
        public static class WanderingStormUpdatePatch
        {
            [HarmonyPrefix]
            public static bool Prefix()
            {
                PatchProfiler.Begin("WanderingStorm.Update");
                if (!Plugin.IsMultiplayer)
                {
                    PatchProfiler.End("WanderingStorm.Update");
                    return true;
                }
                if (LocalWeatherOverride)
                {
                    PatchProfiler.End("WanderingStorm.Update");
                    return true;
                }
                PatchProfiler.End("WanderingStorm.Update");
                return Plugin.IsHost;
            }
        }
    }
}
