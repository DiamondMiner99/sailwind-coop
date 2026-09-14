using HarmonyLib;
using SailwindCoop.Debug;
using UnityEngine;

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
    /// 2. Block WanderingStorm movement - storm positions and the active set come from host. The emission
    ///    toggle in the same method still runs on the guest (see WanderingStormUpdatePatch).
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
        ///
        /// (2026-09-10) The guest still runs the emission half of the method. Vanilla toggles the storm's
        /// bottomParticles only from inside this Update, so skipping the whole method left guests with
        /// whatever emission state the prefab shipped with: the host watched a storm roll in and the crew saw
        /// nothing until the rain started. Movement, UpdateActiveInRegion and the 44km reposition stay
        /// host-only; position and the active set arrive in WeatherState.
        /// </summary>
        [HarmonyPatch(typeof(WanderingStorm), "Update")]
        public static class WanderingStormUpdatePatch
        {
            [HarmonyPrefix]
            public static bool Prefix(WanderingStorm __instance)
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
                if (!Plugin.IsHost)
                    UpdateGuestStormEmission(__instance);
                PatchProfiler.End("WanderingStorm.Update");
                return Plugin.IsHost;
            }
        }

        private static bool _guestStormEmissionDisabled;

        /// <summary>
        /// The emission block of vanilla WanderingStorm.Update, on the storm's own once-a-second timer. Uses
        /// the private `timer` field, which nothing else writes on a guest because vanilla Update never runs
        /// there. A failure to resolve the private fields turns this off for the session, falling back to the
        /// old behavior instead of throwing every frame for every storm.
        /// </summary>
        private static void UpdateGuestStormEmission(WanderingStorm storm)
        {
            if (_guestStormEmissionDisabled || !GameState.playing) return;
            try
            {
                ref float timer = ref StormFields.Timer(storm);
                if (timer <= 0f)
                {
                    timer = 1f;
                    var cam = Camera.main;
                    var particles = StormFields.BottomParticles(storm);
                    if (cam != null && particles != null)
                    {
                        float dist = Vector3.Distance(storm.transform.position, cam.transform.position);
                        var emission = particles.emission;
                        emission.enabled = dist <= StormFields.ParticlesDistance(storm) && storm.active;
                    }
                }
                timer -= Time.deltaTime;
            }
            catch (System.Exception e)
            {
                _guestStormEmissionDisabled = true;
                Plugin.Log.LogError($"[WEATHER] Guest storm emission sync disabled for this session: {e}");
            }
        }

        private static class StormFields
        {
            internal static readonly AccessTools.FieldRef<WanderingStorm, float> Timer;
            internal static readonly AccessTools.FieldRef<WanderingStorm, float> ParticlesDistance;
            internal static readonly AccessTools.FieldRef<WanderingStorm, ParticleSystem> BottomParticles;

            // Explicit static constructor on purpose. Without one the class is beforefieldinit and the runtime
            // may run the initializers when UpdateGuestStormEmission is JIT-compiled, which is outside the try
            // that is meant to catch a renamed vanilla field.
            static StormFields()
            {
                Timer = AccessTools.FieldRefAccess<WanderingStorm, float>("timer");
                ParticlesDistance = AccessTools.FieldRefAccess<WanderingStorm, float>("particlesDistance");
                BottomParticles = AccessTools.FieldRefAccess<WanderingStorm, ParticleSystem>("bottomParticles");
            }
        }
    }
}
