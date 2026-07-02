using System;
using UnityEngine;

namespace SailwindCoop.Networking.Packets
{
    /// <summary>
    /// State of a single NPC boat: transform + sail positions.
    /// Sent at 2Hz per visible NPC boat.
    /// </summary>
    [Serializable]
    public struct NPCBoatStatePacket
    {
        public string HierarchyPath;   // Full path e.g. "Region_Medi/NPCBoats/FishingBoat_01"
        public Vector3 Position;       // World position (with FloatingOrigin offset)
        public Quaternion Rotation;    // World rotation
        public float[] SailLengths;    // Each sail's currentLength (0-1), indexed by GetComponentsInChildren order
    }

    /// <summary>
    /// Initial sync: all visible NPC boats.
    /// Sent once when guest joins.
    /// </summary>
    [Serializable]
    public struct NPCBoatSnapshotPacket
    {
        public NPCBoatStatePacket[] Boats;
    }

    /// <summary>
    /// Authoritative NPC-boat damage/sink state, host -> all guests.
    /// NPC boats are scene-baked (BoatDamage runs only on the host; guest BoatDamage sim is disabled by
    /// DamagePatches), so a host-side NPC that takes damage or sinks otherwise stays alive/full on every
    /// guest screen. The host broadcasts this on a meaningful change or a sink transition so each guest
    /// reconciles the rammed/sunk NPC instead of seeing a phantom healthy hull. Keyed by hierarchy path
    /// (same key NPCBoatStatePacket uses) since NPC boats have no SaveableObject boat-name like player boats.
    /// </summary>
    [Serializable]
    public struct NPCBoatDamagePacket
    {
        public string HierarchyPath;   // Same key as NPCBoatStatePacket.HierarchyPath
        public float WaterLevel;       // BoatDamage.waterLevel (0-1)
        public float HullDamage;       // BoatDamage.hullDamage (0-1)
        public bool Sunk;              // BoatDamage.sunk
    }

    /// <summary>
    /// Guest -> host NPC-boat hit report. A guest ramming an NPC boat can't
    /// apply damage locally (guest BoatDamage.Impact is disabled and is host-authoritative), so the guest
    /// reports the collision to the host, which applies the impact to the NPC's BoatDamage and then relays
    /// the resulting authoritative state to everyone via NPCBoatDamagePacket. Host never sends this.
    /// </summary>
    [Serializable]
    public struct NPCBoatHitRequestPacket
    {
        public string HierarchyPath;   // Which NPC boat was hit
        public float ImpactForce;      // collision relativeVelocity magnitude (same units BoatDamage.Impact takes)
    }
}
