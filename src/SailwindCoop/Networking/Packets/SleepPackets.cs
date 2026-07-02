using System;

namespace SailwindCoop.Networking.Packets
{
    /// <summary>
    /// Sent when a player enters a bed. Direction: both ways.
    /// </summary>
    [Serializable]
    public struct SleepRequestPacket
    {
        public bool IsTavern;      // true if sleeping in tavern
        public bool IsMoored;      // true if boat is moored (for context check)
        public ulong AuthorId;     // N-player: SteamId of the crew member who entered bed (survives host relay
                                   // so other guests track the real author, not the relaying host's transport id)
    }

    /// <summary>
    /// Sent to notify partner someone is waiting. Direction: host -> guest (or vice versa).
    /// </summary>
    [Serializable]
    public struct SleepWaitingPacket
    {
        public bool IsTavern;      // context: tavern or boat
        public ulong AuthorId;     // N-player: SteamId of the waiting crew member (survives host relay)
    }

    /// <summary>
    /// Sent when both players are in bed and sleep begins. Direction: host -> guest.
    /// </summary>
    [Serializable]
    public struct SleepApprovedPacket
    {
        public bool IsTavern;      // tavern sleep (no cycles, fixed wake)
        public bool IsTimeskip;    // true if moored/tavern (time warp enabled)
    }

    /// <summary>
    /// Sent when waiting player leaves bed. Direction: both ways.
    /// </summary>
    [Serializable]
    public struct SleepCancelledPacket
    {
        public ulong AuthorId;     // N-player: SteamId of the crew member who left bed (survives host relay)
    }

    /// <summary>
    /// Sent during boat sleep cycles to sync visual state. Direction: host -> guest.
    /// </summary>
    [Serializable]
    public struct SleepCycleStatePacket
    {
        public bool EyesClosed;       // true = black screen, false = can see
        public float TimeScale;       // Unity Time.timeScale (16 during warp, 1 during eyes-open)
        public float FixedDeltaTime;  // Unity Time.fixedDeltaTime (0.2222 during warp, 0.02222 normal)
        public float FadeTarget;      // 0 = visible, 1 = black
        public float FadeDuration;    // seconds for fade transition
    }

    /// <summary>
    /// Sent when sleep ends. Direction: both ways.
    /// </summary>
    [Serializable]
    public struct WakeUpPacket
    {
        public bool WasManual;     // true if player clicked to wake, false if auto (99.99% sleep)
    }
}
