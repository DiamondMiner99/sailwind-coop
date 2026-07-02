using System;
using UnityEngine;

namespace SailwindCoop.Networking.Packets
{
    [Serializable]
    public struct RopeStatePacket
    {
        public string BoatName;
        public int RopeIndex;     // DEPRECATED: kept for backward compat, use RopeName
        public string RopeName;   // Unique rope identifier (game object name)
        public float Length;      // 0-1 normalized
        public bool IsFinal;      // True on release (send reliable)
    }

    [Serializable]
    public struct HelmStatePacket
    {
        public string BoatName;
        public float Input;       // Wheel angle
        public bool IsFinal;
    }

    /// <summary>
    /// Host -> Guest: the guest's helm input for this boat was rejected because another crew member holds the
    /// single-controller lease. The denied guest stops local prediction and accepts host corrections so its
    /// wheel can't diverge from the authoritative rudder. (C2)
    /// </summary>
    [Serializable]
    public struct HelmDeniedPacket
    {
        public string BoatName;
    }

    [Serializable]
    public struct AnchorEventPacket
    {
        public string BoatName;
        public bool IsSet;        // Anchored or not
        public float RopeLength;
    }

    [Serializable]
    public struct MooringStatePacket
    {
        public string BoatName;
        public int RopeIndex;     // Index in BoatMooringRopes.ropes[]
        public bool IsMoored;
        public Vector3 DockPosition;
        public float LengthSquared;
    }

    [Serializable]
    public struct ApplyForcePacket
    {
        public string BoatName;
        public Vector3 Force;
        public Vector3 WorldPoint;
        public bool IsSailPush;   // true = sail, false = boat
    }

    // Event-based push sync packets (replaces polling ApplyForce)
    [Serializable]
    public struct PushStartPacket
    {
        public byte PushType;      // 0=Boat, 1=Sail
        public string BoatName;
        public Vector3 Direction;  // Player forward
        public Vector3 Position;   // Force application point
        public float ForceMult;    // pushForceMult from component
        public int SailIndex;      // sail-push only: index of the GPButtonSailPusher within the boat (-1 = boat push / unknown)
    }

    [Serializable]
    public struct PushUpdatePacket
    {
        public Vector3 Direction;
        public Vector3 Position;
    }

    // PushStop has no data - just signals end of push

    [Serializable]
    public struct MooringRopeLengthPacket
    {
        public string BoatName;
        public int RopeIndex;
        public float LengthSquared;  // currentRopeLengthSquared value
        public bool IsFinal;         // R4.9: true on the debounced settled value (send + relay reliable)
    }

    [Serializable]
    public struct HelmInputPacket
    {
        public string BoatName;
        public float InputDelta;     // Delta to add to currentInput
    }

    [Serializable]
    public struct HelmLockPacket
    {
        public string BoatName;
        public bool IsLocked;        // Wheel lock state
    }
}
