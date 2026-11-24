using System;
using System.Collections.Generic;
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
        public bool Attached;         // A wall-attachable item (hook) that snapped to a wall on the sender
    }

    /// <summary>Guest asks for the contents of a boat or house that just streamed in.</summary>
    [Serializable]
    public struct BoatItemsRequestPacket
    {
        public string BoatName;
    }

    /// <summary>Full save data for items filed under a boat or house. Long lists span several packets.</summary>
    [Serializable]
    public struct BoatItemsPacket
    {
        public string BoatName;
        public NetworkSaveData[] Items;
    }

    /// <summary>Guest asks for one item it was told about and does not have.</summary>
    [Serializable]
    public struct ItemResyncRequestPacket
    {
        public int ItemInstanceId;
    }

    /// <summary>"I still show this item in your hands. Is it?"</summary>
    [Serializable]
    public struct ItemReleaseQueryPacket
    {
        public int ItemInstanceId;
        public ulong CarrierSteamId;
    }

    public enum HookAttachmentKind : byte
    {
        Item = 0,      // A ShipItem, by instance id
        Anchor = 1,    // A boat's anchor, by boat name
        ClearAll = 2,  // Joiner only: forget what your own save hung, the replay follows
    }

    /// <summary>HooksHangMore: something went onto or came off a lamp hook.</summary>
    [Serializable]
    public struct HookAttachmentPacket
    {
        public HookAttachmentKind Kind;
        public bool Attached;
        public int HookInstanceId;
        public int ItemInstanceId;
        public string BoatName;
    }

    public enum InstrumentNoteKind : byte
    {
        Strike = 0,   // Lute, nakers: one note, played once
        Voice = 1,    // Flute, hurdy-gurdy: play mode and the note now held (-1 for none)
        Horn = 2,     // Viking Horn: Playing = being blown
    }

    /// <summary>MusicMod: a note from an instrument in a crewmate's hands.</summary>
    [Serializable]
    public struct InstrumentNotePacket
    {
        public InstrumentNoteKind Kind;
        public int ItemInstanceId;
        public bool Playing;
        public sbyte Note;
    }

    /// <summary>Sail Damage: one sail's six values (DamageModel.Snapshot), by Sail Damage's own save key.</summary>
    [Serializable]
    public struct SailDamageEntry
    {
        public string Key;
        public float[] Values;
    }

    [Serializable]
    public struct SailDamagePacket
    {
        public bool ReplaceAll;   // Joiner: forget your own save's sail damage first
        public List<SailDamageEntry> Entries;
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

    /// <summary>
    /// (v0.2.29) Cargo transport hire sync. Requests are guest -> host; the host performs the vanilla
    /// transaction (charging the shared wallet, which CurrencySync then propagates) and broadcasts the
    /// applied result to everyone. Price/Requester ride on the result so ONLY the requester plays the
    /// gold sound + money notification (everyone's wallet updates silently via CurrencySync).
    /// </summary>
    [Serializable]
    public struct CargoInsertRequestPacket
    {
        public int PortIndex;        // CargoCarrier.portIndex (CargoCarrier.carriers registry key)
        public int ItemInstanceId;
    }

    [Serializable]
    public struct CargoInsertedPacket
    {
        public int PortIndex;
        public int ItemInstanceId;
        public int Price;            // What the host's wallet was charged (0 for join backfill)
        public ulong RequesterSteamId;
    }

    [Serializable]
    public struct CargoWithdrawRequestPacket
    {
        public int PortIndex;
        public int ItemInstanceId;   // Id, not list index - cargo list order is not guaranteed identical
    }

    [Serializable]
    public struct CargoWithdrawnPacket
    {
        public int PortIndex;
        public int ItemInstanceId;
        public int Price;
        public ulong RequesterSteamId; // This peer picks the item up into their hand after applying
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
        public bool FarLod;       // (0.41) floating item found at sea (the spawner's loot list, not ShipItem.farLod): drawn from far away, culled locally when left behind
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

        public bool FloatingLoot;     // (0.41) the host's floating item found at sea; written last, read only if present
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
