using System;

namespace SailwindCoop.Networking.Packets
{
    [Serializable]
    public struct ShipyardCustomizationPacket
    {
        public string BoatName;
        public bool[] MastsEnabled;
        public NetworkSailData[] Sails;
        public int[] PartActiveOptions;
    }

    /// <summary>
    /// Guest -> host. A guest confirmed a shipyard order. Vanilla
    /// Shipyard.ConfirmOrder deducts PlayerGold.currency[region] -= total LOCALLY, which on a guest charges
    /// only the guest's mirror of the shared wallet (the host wallet never changes). The guest's ConfirmOrder
    /// patch undoes that local deduction + optimistic day-log and sends this request; the host validates the
    /// authoritative SHARED wallet and performs the deduction + day-log (broadcasting the normal CurrencySync
    /// + TransactionDelta). Region is the currency slot vanilla charges (Shipyard.region); Total is
    /// Shipyard.GetCurrentOrderTotal() (can be NEGATIVE for a net refund - host adds in that case).
    /// </summary>
    /// <summary>
    /// Editing peer -> all (host relays). A boat was admitted to (Active=true) or discharged from
    /// (Active=false) a shipyard cradle. Non-editing peers freeze the boat in place (kinematic) while
    /// active and snap to the host's post-discharge pose on release; BoatSyncManager suppresses both
    /// send and apply for the boat, and DamagePatches suppresses BoatDamage.Impact for a short window
    /// after release so the discharge teleport can never register hull damage.
    /// </summary>
    [Serializable]
    public struct ShipyardStatePacket
    {
        public string BoatName;   // root SaveableObject.gameObject.name (matches BoatUtility.FindBoatByName)
        public bool Active;       // true = in the cradle (AdmitShip), false = released (DischargeShip)
    }

    [Serializable]
    public struct ShipyardOrderRequestPacket
    {
        public string BoatName;   // SaveableObject.gameObject.name of the boat in the cradle (logging/diagnostic)
        public int Region;        // currency slot (Shipyard.region) - same slot vanilla ConfirmOrder charges
        public int Total;         // GetCurrentOrderTotal(); >0 = charge, <0 = refund (net), 0 = no-op
    }
}
