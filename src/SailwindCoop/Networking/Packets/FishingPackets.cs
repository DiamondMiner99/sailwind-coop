using System;
using Steamworks;
using UnityEngine;

namespace SailwindCoop.Networking.Packets
{
    /// <summary>
    /// Continuous state while fish is hooked (5Hz from owner).
    /// </summary>
    [Serializable]
    public struct FishingStatePacket
    {
        public int RodInstanceId;
        public float LineLength;
        public float Tension;
        public float FishEnergy;
    }

    /// <summary>
    /// Line length update (on scroll wheel).
    /// </summary>
    [Serializable]
    public struct FishingLineLengthPacket
    {
        public int RodInstanceId;
        public float LineLength;
    }

    /// <summary>
    /// Fish bite event.
    /// </summary>
    [Serializable]
    public struct FishBitePacket
    {
        public int RodInstanceId;
        public int FishPrefabIndex;
    }

    /// <summary>
    /// Fish escaped (line snap, rod dropped, etc).
    /// </summary>
    [Serializable]
    public struct FishEscapePacket
    {
        public int RodInstanceId;
    }

    /// <summary>
    /// Request to collect fish (owner → host).
    /// </summary>
    [Serializable]
    public struct FishCollectRequestPacket
    {
        public int RodInstanceId;
        public int RodPrefabIndex;
        public int FishPrefabIndex;
    }

    /// <summary>
    /// Fish collected response (host → all).
    /// </summary>
    [Serializable]
    public struct FishCollectResponsePacket
    {
        public int RodInstanceId;
        public int FishItemId;
        public bool HookConsumed;
    }

    /// <summary>
    /// Rod ownership changed.
    /// </summary>
    [Serializable]
    public struct RodOwnerChangedPacket
    {
        public int RodInstanceId;
        public ulong NewOwnerId; // SteamId, 0 = no owner
    }

    /// <summary>
    /// Rod cast event (line thrown into water).
    /// </summary>
    [Serializable]
    public struct FishingCastPacket
    {
        public int RodInstanceId;
        public float ThrowCharge; // 0-1, how hard the cast was
    }

    /// <summary>
    /// Bobber position stream (5Hz from owner, coalesced on >0.5m movement). The bobber launch is
    /// emergent local physics on the owner (rod angular velocity + the owner's camera forward), so a
    /// viewer's bobber never leaves the rod - stream the resulting position instead. Boat-frame when
    /// the caster is aboard a boat, offset-independent world coords otherwise (BoatName empty).
    /// </summary>
    [Serializable]
    public struct FishingBobberSyncPacket
    {
        public int RodInstanceId;
        public string BoatName;  // "" = world frame
        public Vector3 Position; // boat-local, or world minus floating-origin offset
        public byte InWater;     // owner's floater.InWater (1/0, diagnostic)
    }
}
