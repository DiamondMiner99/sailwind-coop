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
        public Vector3[] StormPositions;
        public int ActiveStormIndex;       // Index of active storm in StormPositions, -1 if none

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
