using System;
using System.Collections.Generic;
using UnityEngine;

namespace SailwindCoop.Networking.Packets
{
    [Serializable]
    public struct NetworkMooringData
    {
        public bool IsMoored;
        public MooringTargetKind TargetKind;   // (v0.2.32) see MooringStatePacket
        public Vector3 DockPosition;
        public float LengthSquared;
        public string TowBoatName;
        public string CleatPath;
    }

    [Serializable]
    public struct NetworkSailData
    {
        public int PrefabIndex;
        public int MastIndex;
        public float InstallHeight;
        public float MinAngle;
        public float MaxAngle;
        public float Health;
        public int Color;
        public float ScaleY;  // BS1: shipyard-resized sail scale (0 = default, matches Mast.LoadSail's scaleY!=0 gate)
        public float ScaleZ;
    }

    // Chart/map data structures
    [Serializable]
    public struct NetworkChartLine
    {
        public float StartX;
        public float StartY;
        public float EndX;
        public float EndY;
        public int Color;
    }

    [Serializable]
    public struct NetworkChartPoint
    {
        public float PosX;
        public float PosY;
    }

    [Serializable]
    public struct NetworkChartData
    {
        public NetworkChartLine[] Lines;
        public NetworkChartPoint[] Points;
    }

    /// <summary>
    /// Network-serializable version of SavePrefabData.
    /// Mirrors all fields from the game's save system for complete item sync.
    /// </summary>
    [Serializable]
    public struct NetworkSaveData
    {
        // Identity
        public int InstanceId;
        public int PrefabIndex;

        // Transform - position is context-dependent:
        // - For boat items: boat-relative (LocalPosition)
        // - For world items: offset-independent world coordinates
        public Vector3 Position;
        public Quaternion Rotation;
        public bool IsWorldPosition;  // false = boat-relative, true = world (offset-independent)
        public string ParentBoatName; // empty for world items

        // Item state
        public bool IsSold;
        public float Health;
        public float Amount;
        public int InventorySlot;
        public int CrateId;
        public int MissionIndex;
        public int ParentObject;  // sceneIndex of parent
        public int DaysInStorage;

        // Extra values (fish size, food rot, etc.)
        public float ExtraValue0;
        public float ExtraValue1;
        public float ExtraValue2;
        public float ExtraValue3;
        public float ExtraValue4;

        // Chart/map data (null for non-maps)
        public bool HasChartData;
        public NetworkChartData ChartData;
    }

    [Serializable]
    public struct NetworkItemData
    {
        public int InstanceId;
        public int PrefabIndex;
        public Vector3 LocalPosition;
        public Quaternion Rotation;
        public float Health;
        public float Amount;
        public int InventorySlot;
        public int CrateId;
        public int MissionIndex;
    }

    [Serializable]
    public struct NetworkBoatData
    {
        // Identity
        public string Name;

        // Transform
        public Vector3 Position;
        public Quaternion Rotation;

        // Customization
        public bool[] MastsEnabled;
        public NetworkSailData[] Sails;
        public int[] PartActiveOptions;

        // Items (using new complete save data structure)
        public NetworkSaveData[] Items;

        // Rope states (sail deployment)
        public float[] RopeLengths;

        // Anchor
        public bool IsAnchored;
        public float AnchorRopeLength;

        // Mooring ropes
        public NetworkMooringData[] MooringRopes;

        // Damage
        public float WaterLevel;
        public float HullDamage;
        public float Oakum;

        // Ownership (extraSetting in game - true = player owns boat)
        public bool IsOwned;

        // Dirt texture (PNG bytes)
        public byte[] DirtTexture;
    }

    [Serializable]
    public struct BoatWorldStatePacket
    {
        public NetworkBoatData[] Boats;
        public string CurrentBoatName;
        public Vector3 WindState;
        public Vector3 HostPlayerPosition;  // Boat-relative if IsHostOnBoat, world coords otherwise
        public Quaternion HostPlayerRotation;
        public bool IsHostOnBoat;  // BUG-027 fix: track coordinate space

        // Weather state for initial sync
        public WeatherStatePacket WeatherState;

        // BUG-018 fix: Host's FloatingOriginManager offset for cross-region join
        // Guest needs this to properly shift their world to match host's coordinate frame
        public Vector3 HostOffset;

        // BUG-018 fix: Host's current region name for forcing RegionBlender switch
        public string HostRegionName;

        // BUG-018 fix: Nearest port to host for cross-region recovery teleport
        public string NearestPortName;

        // World items (not parented to any boat, sold=true)
        public NetworkSaveData[] WorldItems;

        // BS2: true when this is a RECOVERY resync (broadcast to all crew) rather than a fresh JOIN. The guest
        // uses it to skip the heavy teleport coroutine for crew who are NOT on the recovered boat (so an ashore
        // guest isn't yanked across the map). Default false = join.
        public bool IsRecovery;
    }

    [Serializable]
    public struct BoatTransformPacket
    {
        public string BoatName;
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Velocity;
        public Vector3 AngularVelocity;
        public bool IsAnchored;
        // v0.2.28 multi-boat streaming: true when this is the host's lastBoat (the primary crewed boat),
        // false for the additional "active" boats (any boat carrying a remote crew member). Guests key
        // their single-boat legacy state (SnapBoatToLiveTarget etc.) off the primary only.
        public bool IsPrimary;
    }

    /// <summary>
    /// Host -> peers. A boat's ownership changed at runtime (player bought it). Peers set
    /// extraSetting + refresh the "for sale" UI so an already-connected crew member sees the purchase live
    /// (a later joiner gets it via the join snapshot instead).
    /// </summary>
    [Serializable]
    public struct BoatOwnershipChangedPacket
    {
        public string BoatName;   // SaveableObject.gameObject.name (matches BoatUtility.FindBoatByName)
        public bool IsOwned;      // extraSetting; true = purchased
    }

    /// <summary>
    /// Guest -> host. Request to buy a boat against the shared wallet. The host validates gold
    /// and performs the authoritative PurchasableBoat.PurchaseBoat() (deduct + log + broadcast ownership);
    /// the guest's own vanilla purchase is suppressed and waits for the host's CurrencySync + ownership.
    /// </summary>
    [Serializable]
    public struct BoatPurchaseRequestPacket
    {
        public string BoatName;   // SaveableObject.gameObject.name of the boat to purchase
    }
}
