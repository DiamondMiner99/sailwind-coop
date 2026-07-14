using System;
using UnityEngine;

namespace SailwindCoop.Networking.Packets
{
    /// <summary>(v0.2.32) Absolute trapdoor/door/hatch state. See PacketType.TrapdoorState.</summary>
    [Serializable]
    public struct TrapdoorStatePacket
    {
        public string BoatName;       // root SaveableObject.gameObject.name
        public string Key;            // "{trapdoorName}~{occurrence}" - or the gunport group name when IsGunportGroup
        public bool IsOpen;
        public bool IsGunportGroup;   // true = Key is "lower"/"upper"/"quarter" on the Leopard
    }

    /// <summary>
    /// (v0.2.32) HMS Leopard cutter deploy/recover. IsRequest=true: guest -> host intent (host runs
    /// the mod's own gates by invoking its controller). IsRequest=false: host -> all authoritative
    /// result. Position is REAL (floating-origin-independent) coords.
    /// </summary>
    [Serializable]
    public struct CutterStatePacket
    {
        public bool Active;
        public Vector3 RealPosition;
        public Quaternion Rotation;
        public bool IsRequest;
    }

    /// <summary>
    /// (v0.2.32) Held-key bits from whoever is rowing the Leopard cutter. bit0=MoveUp, bit1=MoveDown,
    /// bit2=MoveLeft, bit3=MoveRight. Host applies force; everyone else animates the oars.
    /// </summary>
    [Serializable]
    public struct OarInputPacket
    {
        public byte KeyBits;
        public ulong AuthorId;
    }
}
