using System;

namespace SailwindCoop.Networking.Packets
{
    /// <summary>
    /// Bidirectional RTT probe for the F8 debug overlay. Sent UNRELIABLE (we want to measure the
    /// unreliable gameplay path, and a lost probe simply means no sample this cycle - the 2s loop
    /// retries forever). SendTime is the SENDER's Time.realtimeSinceStartup; it is only ever
    /// compared against the same machine's clock after the echo comes back, so no cross-machine
    /// clock agreement is needed.
    /// </summary>
    [Serializable]
    public struct PingRequestPacket
    {
        public float SendTime;
    }

    /// <summary>
    /// Verbatim echo of PingRequestPacket.SendTime back to the requester, who computes
    /// rtt = Time.realtimeSinceStartup - SendTime. Also sent UNRELIABLE.
    /// </summary>
    [Serializable]
    public struct PingReplyPacket
    {
        public float SendTime;
    }
}
