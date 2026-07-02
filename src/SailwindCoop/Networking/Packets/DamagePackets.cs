using System;

namespace SailwindCoop.Networking.Packets
{
    /// <summary>
    /// Full damage state sent by host at 1Hz.
    /// </summary>
    [Serializable]
    public struct DamageStatePacket
    {
        public string BoatName;
        public float WaterLevel;
        public float HullDamage;
        public float Oakum;
        public bool Sunk;
    }

    /// <summary>
    /// Immediate impact event sent by host when collision occurs.
    /// </summary>
    [Serializable]
    public struct DamageImpactPacket
    {
        public string BoatName;
        public float HullDamage;
    }

    /// <summary>
    /// Guest bilge pump input sent when pumping starts/stops/changes.
    /// </summary>
    [Serializable]
    public struct GuestPumpInputPacket
    {
        public string BoatName;
        public float PumpInput;
    }

    /// <summary>
    /// Guest oakum repair request sent when guest uses oakum item.
    /// </summary>
    [Serializable]
    public struct GuestOakumRepairPacket
    {
        public string BoatName;
        public int ItemInstanceId;  // The oakum item being used
    }

    /// <summary>
    /// Guest bail request sent when guest uses bucket/bottle to remove water from boat.
    /// </summary>
    [Serializable]
    public struct GuestBailRequestPacket
    {
        public string BoatName;
        public int BottleInstanceId;  // The bottle/bucket being used
        public float AmountBailed;    // Units of water removed from boat
    }
}
