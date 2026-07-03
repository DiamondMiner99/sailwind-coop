using System;

namespace SailwindCoop.Networking.Packets
{
    /// <summary>
    /// Chip log thrown event (thrower -> all). Replays the vanilla ThrowRod coroutine on viewers
    /// so their bobber un-parks (vanilla only ever throws from local input).
    /// </summary>
    [Serializable]
    public struct ChipLogThrowPacket
    {
        public int ItemInstanceId;
    }

    /// <summary>
    /// Chip log line stream (5Hz from the thrower, coalesced). Carries the full thrown state so a
    /// receiver can idempotently establish it (self-healing for late joiners and Throw/Line
    /// ordering races): Thrown=true means the bobber must be dynamic with this line length,
    /// Thrown=false means reeled in - vanilla re-parks once the limit is back at minLength.
    /// </summary>
    [Serializable]
    public struct ChipLogLineSyncPacket
    {
        public int ItemInstanceId;
        public float LineLength;
        public bool Thrown;
    }
}
