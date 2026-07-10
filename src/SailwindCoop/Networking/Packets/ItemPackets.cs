using System;
using UnityEngine;

namespace SailwindCoop.Networking.Packets
{
    [Serializable]
    public struct ItemPickedUpPacket
    {
        public int ItemInstanceId;
        public ulong PlayerSteamId;
        public int InventorySlot;   // -1 = hand, >= 0 = inventory slot
        public int PrefabIndex;     // For position-based correlation
        public Vector3 Position;    // Position when picked up (for lazy ID correlation)
        public string ParentBoatName; // Boat name if on boat, empty if on land
        public bool IsLocalPosition;  // True if Position is boat-local
    }

    [Serializable]
    public struct ItemDroppedPacket
    {
        public int ItemInstanceId;
        public Vector3 Position;
        public Quaternion Rotation;
        public string ParentBoatName; // Empty if world/dock
        public bool IsLocalPosition;  // True if Position is boat-local
    }

    [Serializable]
    public struct ItemPickupRequestPacket
    {
        public int ItemInstanceId;
        public int PrefabIndex;
        public int InventorySlot;     // -1 = hand, >= 0 = inventory slot
        public Vector3 Position;      // Position for lazy ID correlation
        public string ParentBoatName; // Boat name if on boat, empty if on land
        public bool IsLocalPosition;  // True if Position is boat-local
    }

    [Serializable]
    public struct ItemPickupDeniedPacket
    {
        public int ItemInstanceId;
        public byte Reason; // 0 = already held, 1 = doesn't exist
    }

    /// <summary>
    /// (v0.2.25) Host -> one guest: "destroy your local copy of this instanceId". Sent after the host
    /// denies ItemPickupRequest with reason=1 (unknown id) for the SAME id more than twice from the
    /// same guest - that repetition is the ghost-item signature (a local-only item the host was never
    /// told about, e.g. a phantom-save cache residual). Targeted + additive: old clients drop it.
    /// </summary>
    [Serializable]
    public struct GhostItemPurgePacket
    {
        public int ItemInstanceId;
    }

    [Serializable]
    public struct ItemSpawnedPacket
    {
        public int ItemInstanceId;
        public int PrefabIndex;
        public Vector3 Position;
        public Quaternion Rotation;
        public string ParentBoatName;
        public bool IsLocalPosition;
        public float Health;
        public float Amount;
        public int MissionIndex;  // -1 if not mission cargo, 0-4 for mission slot
    }

    [Serializable]
    public struct ItemDestroyedPacket
    {
        public int ItemInstanceId;
    }

    [Serializable]
    public struct ItemAmountChangedPacket
    {
        public int ItemInstanceId;
        public float NewAmount;
    }

    [Serializable]
    public struct ItemCratePacket
    {
        public int ItemInstanceId;
        public int CrateInstanceId;
    }

    [Serializable]
    public struct ItemHungPacket
    {
        public int ItemInstanceId;
        public int HookInstanceId;
    }

    [Serializable]
    public struct ItemUnhungPacket
    {
        public int ItemInstanceId;
    }

    [Serializable]
    public struct CrateSpawnedItemData
    {
        public int InstanceId;
        public int PrefabIndex;
        public float Health;
        public float Amount;
        public bool IsSmoked;
        public bool IsDried;
    }

    [Serializable]
    public struct CrateUnsealedPacket
    {
        public int CrateInstanceId;
        public CrateSpawnedItemData[] SpawnedItems;
    }

    [Serializable]
    public struct ItemHealthChangedPacket
    {
        public int ItemInstanceId;
        public float NewHealth;
    }

    [Serializable]
    public struct CrateUnsealRequestPacket
    {
        public int CrateInstanceId;
        public int CratePrefabIndex;
    }

    /// <summary>
    /// Sent by host to guest when item validation fails.
    /// Guest destroys wrong item and spawns correct one.
    /// </summary>
    [Serializable]
    public struct ItemResyncPacket
    {
        public int InstanceId;
        public int PrefabIndex;
        public float Health;
        public float Amount;
        public bool Sold;

        // Location
        public bool IsOnBoat;
        public string BoatName;       // If IsOnBoat
        public Vector3 LocalPosition; // Boat-relative if IsOnBoat, world if not
        public Quaternion Rotation;

        // Mission link
        public int MissionIndex;      // -1 if not mission cargo, 0-4 for mission slot
    }

    /// <summary>
    /// Syncs light state (lantern on/off) between players.
    /// Sent when player toggles a ShipItemLight via SetLight().
    /// </summary>
    [Serializable]
    public struct LightStatePacket
    {
        public int ItemInstanceId;
        public bool IsOn;
    }

    /// <summary>
    /// Syncs an item being nailed/un-nailed with the hammer.
    /// Sent on ShipItemHammer.NailItem (Nailed=true) and the OnAltActivate un-nail path (Nailed=false).
    /// The receiver just flips ShipItem.nailed; ItemRigidbody re-evaluates kinematic every FixedUpdate.
    /// </summary>
    [Serializable]
    public struct NailStatePacket
    {
        public int ItemInstanceId;
        public bool Nailed;
    }

    /// <summary>
    /// Syncs pipe being filled with tobacco.
    /// Sent when player loads tobacco into a pipe via LoadTobacco().
    /// TobaccoType: 1=cigarette(white), 2=cigar(green), 3=pipe(black), 4=hookah(brown)
    /// </summary>
    [Serializable]
    public struct PipeFilledPacket
    {
        public int PipeInstanceId;
        public int TobaccoType;
    }

    /// <summary>
    /// Guest -> Host: the guest's join coroutine finished and all snapshot spawns are applied.
    /// The host replies with a targeted mission-cargo resync (one ItemSpawned per live mission Good)
    /// so a joiner whose join snapshot applied only partially (per-item spawn losses) still receives
    /// every mission crate. A snapshot lost outright never starts the join coroutine, so this signal
    /// does not fire and cannot repair that case.
    /// No payload: the sender SteamId carries all the information the host needs.
    /// </summary>
    [Serializable]
    public struct GuestJoinCompletePacket
    {
    }
}
