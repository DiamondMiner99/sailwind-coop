using System;

namespace SailwindCoop.Networking.Packets
{
    [Serializable]
    public struct TimeStatePacket
    {
        public float GlobalTime;   // 0-24 hours
        public float Timescale;    // Time multiplier (1x normal, 9x sleep)
        public int Day;            // Day counter
        public float MoonPhase;    // 0-1, 28-day lunar cycle
    }
}
