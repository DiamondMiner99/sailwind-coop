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

        /// <summary>
        /// (0.41) Which surface was swept: -1 = the main hull, otherwise a position in the boat's
        /// SaveableObject.extraCleanables.
        /// </summary>
        public sbyte ExtraIndex;
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

    /// <summary>
    /// (0.41) Host -> joiner: dirt textures of a boat's extra surfaces (PacketType.BoatExtraDirt).
    /// </summary>
    [Serializable]
    public struct BoatExtraDirtPacket
    {
        public string BoatName;

        /// <summary>Positions in the boat's SaveableObject.extraCleanables, one per texture.</summary>
        public byte[] Indices;

        /// <summary>PNG bytes, parallel to Indices.</summary>
        public byte[][] Textures;
    }
}
