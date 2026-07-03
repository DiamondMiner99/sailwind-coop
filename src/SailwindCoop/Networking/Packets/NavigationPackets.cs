namespace SailwindCoop.Networking.Packets
{
    public enum NavItemStateType : byte
    {
        ClockLid = 0,           // bool: 0=closed, 1=open
        QuadrantInspect = 1,    // bool: 0=normal, 1=inspecting
        SpyglassZoom = 2,       // float: 0.0-1.0
        CompassLatitude = 3     // float: -45 to -11
    }

    public struct NavItemStatePacket
    {
        public int ItemInstanceId;
        public NavItemStateType StateType;
        public float Value;
    }

    public struct MapFoldStatePacket
    {
        public int ItemInstanceId;
        public bool IsFolded;
    }

    public struct MapDrawRequestPacket
    {
        public int ItemInstanceId;
        public int PrefabIndex;
    }

    public struct MapDrawResponsePacket
    {
        public int ItemInstanceId;
        public bool Granted;
        public ulong LockedBySteamId;
        // N-player: the guest this response is addressed to. The host broadcasts the response
        // (SendToAllReliable), so non-requesting guests must ignore a reply not meant for them -
        // otherwise two guests pending on the SAME map item would both react to one grant/deny.
        // At N=1 the only requester IS the single guest, so this is unchanged for one guest.
        public ulong RequesterSteamId;
    }

    public struct MapDrawLockedPacket
    {
        public int ItemInstanceId;
        public ulong LockedBySteamId;
    }

    public struct MapDrawReleasePacket
    {
        public int ItemInstanceId;
    }

    public struct MapLinePacket
    {
        public int ItemInstanceId;
        public float StartX;
        public float StartY;
        public float EndX;
        public float EndY;
        public int Color;
    }

    public struct MapTempLinePacket
    {
        public int ItemInstanceId;
        public bool HasLine;  // false = clear temp line
        public float StartX;
        public float StartY;
        public float EndX;
        public float EndY;
        public int Color;
    }

    public struct MapFullSyncPacket
    {
        public int ItemInstanceId;
        public MapLinePacket[] Lines;
    }

    public struct ChartSessionPacket
    {
        public int ItemInstanceId;
        public bool Active;
        public sbyte KitPos;      // -1=left, 0=top, 1=right (mirrors MapTableCamera kitPos)
        public ulong UserSteamId; // who is charting (ghost ownership + disconnect cleanup)
    }

    public struct ChartCursorPacket
    {
        public int ItemInstanceId;
        public byte Tool;   // 0=none, 1=quill, 2=protSmall, 3=protLarge
        public float CursorX; // chart-local
        public float CursorY;
    }
}
