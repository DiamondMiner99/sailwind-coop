using System;
using UnityEngine;

namespace SailwindCoop.Networking.Packets
{
    [Serializable]
    public struct WeatherStatePacket
    {
        public Vector3 Wind;
        public int TargetWeatherIndex;    // 0=clear, 1=cloudy, 2=rain, 3=storm
        public float WeatherLerp;          // 0-1 blend progress
        public float RainIntensity;
        public int RegionIndex;
        // REAL (offset-independent) coords since 2026-09-10: the host sends position minus its
        // FloatingOriginManager offset and the guest adds its own back. Raw local positions were off by the
        // offset difference whenever the two machines sat in different origin cells, which the shift's
        // hysteresis allows even for players standing side by side.
        public Vector3[] StormPositions;
        public int ActiveStormIndex;       // Index of active storm in StormPositions, -1 if none
        // (2026-09-10) Bit i = storms[i].active on the host. Vanilla lets several storms be active at once
        // (WanderingStorm.UpdateActiveInRegion) and the guest's own FindClosestStorm needs the whole set.
        // Replaces the single-index apply, which switched every storm off whenever the index was -1.
        public int ActiveStormMask;
        // (2026-09-10) Name of the host's RegionBlender.currentTargetRegion GameObject, "" if unknown. The
        // region picks the preset WeatherSets, fog density included, and nothing synced it before this.
        public string RegionName;

        // WavesInertia sync - controls wave height/direction independent of wind
        public Quaternion WaveDirection;   // WavesInertia.transform.rotation
        public float WaveInertia;          // WavesInertia.currentInertia (1-70)
        public float WaveMagnitude;        // WavesInertia.currentMagnitude

        // Crest ocean time sync - controls wave phase (position of peaks/troughs)
        public float OceanTime;            // OceanRenderer.CurrentTime

        // OceanUpdaterCrest crossfade INPUTS (the live Crest wave drive). Guest writes these
        // into its own OceanUpdaterCrest so its local DCTInertiaUpdate recomputes weights identically
        // (per-player distanceToLand/eyesClosed damping stays local). Replaces the dead FFTOceanTime.
        public float HostCurrentMult;          // OceanUpdaterCrest.currentMult (crossfade 0-1)
        public byte HostWavesUp;               // OceanUpdaterCrest.wavesUp (inertia wave slot 0/1)
        public float HostTargetInertiaAngle;   // OceanUpdaterCrest.targetInertiaAngle
        public float HostWindWavesWeight;      // OceanUpdaterCrest.windWaves._weight (lerp state)
    }
}
