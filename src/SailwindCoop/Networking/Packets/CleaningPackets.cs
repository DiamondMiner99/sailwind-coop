using System;

namespace SailwindCoop.Networking.Packets
{
    /// <summary>
    /// Packet for broom cleaning stroke - syncs UV coordinates where deck was cleaned.
    /// </summary>
    [Serializable]
    public struct CleaningStrokePacket
    {
        /// <summary>
        /// Name of the boat being cleaned.
        /// </summary>
        public string BoatName;

        /// <summary>
        /// UV texture coordinate X (0-1).
        /// </summary>
        public float UVX;

        /// <summary>
        /// UV texture coordinate Y (0-1).
        /// </summary>
        public float UVY;
    }

    /// <summary>
    /// Packet for shipyard hull cleaning - syncs full deck clean.
    /// </summary>
    [Serializable]
    public struct CleanFullyPacket
    {
        /// <summary>
        /// Name of the boat that was fully cleaned.
        /// </summary>
        public string BoatName;
    }
}
