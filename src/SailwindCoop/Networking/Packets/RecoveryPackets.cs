using System;

namespace SailwindCoop.Networking.Packets
{
    /// <summary>
    /// Sent when host's recovery starts (passed out). Direction: host -> guest.
    /// </summary>
    [Serializable]
    public struct RecoveryStartedPacket
    {
        public byte Reason;  // RecoveryReason enum cast to byte
    }

    /// <summary>
    /// Sent when host's recovery ends (wake up). Direction: host -> guest.
    /// </summary>
    [Serializable]
    public struct RecoveryEndedPacket
    {
        // Empty - just signals recovery complete
    }
}
