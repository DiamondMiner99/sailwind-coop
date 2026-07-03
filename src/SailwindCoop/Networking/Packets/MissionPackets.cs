using System;

namespace SailwindCoop.Networking.Packets
{
    /// <summary>
    /// Network-serializable mission data (mirrors SaveMissionData).
    /// </summary>
    [Serializable]
    public struct NetworkMissionData
    {
        public int SlotIndex;           // 0-4, which mission slot
        public int OriginPortIndex;
        public int DestinationPortIndex;
        public int GoodPrefabIndex;
        public int GoodCount;
        public int DeliveredCount;
        public int TotalPrice;
        public float InsuranceLevel;
        public float Distance;
        public int DueDay;
        public bool IsValid;            // false = empty slot
    }

    /// <summary>
    /// Full mission state sync (on join).
    /// </summary>
    [Serializable]
    public struct MissionStateSyncPacket
    {
        public NetworkMissionData[] Missions; // Always 5 slots
    }

    /// <summary>
    /// Single mission accepted notification.
    /// </summary>
    [Serializable]
    public struct MissionAcceptedPacket
    {
        public NetworkMissionData Mission;
    }

    /// <summary>
    /// Delivery progress update.
    /// </summary>
    [Serializable]
    public struct MissionProgressPacket
    {
        public int SlotIndex;
        public int DeliveredCount;
        public string GoodName;   // For the guest's specific "Delivered <name>\n( N / M )" toast (matches vanilla Mission.DeliverGood)
        public int GoodCount;     // Mission's total required good count
    }

    /// <summary>
    /// Mission completed or abandoned.
    /// </summary>
    [Serializable]
    public struct MissionEndedPacket
    {
        public int SlotIndex;
        public string MissionName; // For the guest's "Mission complete:\n<missionName>" toast (Completed path; Abandoned sends empty)
    }

    /// <summary>
    /// Guest request to accept mission.
    /// </summary>
    [Serializable]
    public struct MissionAcceptRequestPacket
    {
        public int PortIndex;
        public int BoardSlot;           // 0-4 on the displayed board
        public int Page;                // Mission board page
        public bool IsWorldMission;     // Local vs world tab
    }

    /// <summary>
    /// Guest request to abandon mission.
    /// </summary>
    [Serializable]
    public struct MissionAbandonRequestPacket
    {
        public int SlotIndex;           // 0-4 in PlayerMissions.missions
    }

    /// <summary>
    /// Guest request for mission board data.
    /// </summary>
    [Serializable]
    public struct MissionBoardRequestPacket
    {
        public int PortIndex;
        public int Page;
        public bool IsWorldMission;
    }

    /// <summary>
    /// Host response with mission board data.
    /// </summary>
    [Serializable]
    public struct MissionBoardResponsePacket
    {
        public NetworkMissionData[] Missions; // Up to 5 missions
        public int TotalCount;                // Total missions available
    }

    /// <summary>
    /// Full currency state (4 regions).
    /// </summary>
    [Serializable]
    public struct CurrencySyncPacket
    {
        public int[] Currency; // 4 regional currencies
    }

    /// <summary>
    /// Full reputation state (4 regions).
    /// </summary>
    [Serializable]
    public struct ReputationSyncPacket
    {
        public int[] Reputation; // 4 regional reputations
    }

    /// <summary>
    /// Guest request to deliver cargo.
    /// </summary>
    [Serializable]
    public struct DeliverGoodRequestPacket
    {
        public int ItemInstanceId;
        public int PrefabIndex;
        public int PortIndex;
    }

    /// <summary>
    /// Guest request to exchange currency.
    /// </summary>
    [Serializable]
    public struct ExchangeRequestPacket
    {
        public int SellCurrency;
        public int BuyCurrency;
        public int Amount;
    }

    /// <summary>
    /// Price report for a single port (mirrors game's PriceReport).
    /// </summary>
    [Serializable]
    public struct NetworkPriceReport
    {
        public int PortIndex;
        public int[] BuyPrices;   // 65 goods
        public int[] SellPrices;  // 65 goods
        public int Day;           // When report was made
        public bool Approved;
    }

    /// <summary>
    /// Full price knowledge sync (on join).
    /// </summary>
    [Serializable]
    public struct PriceKnowledgeSyncPacket
    {
        public NetworkPriceReport[] Reports; // 34 ports
    }

    /// <summary>
    /// Single port price discovery.
    /// </summary>
    [Serializable]
    public struct PriceDiscoveryPacket
    {
        public NetworkPriceReport Report;
    }

    /// <summary>
    /// Current island supply sync.
    /// </summary>
    [Serializable]
    public struct IslandSupplySyncPacket
    {
        public int PortIndex;
        public float[] Supply; // 65 goods
    }

    /// <summary>
    /// Market trade request (buy or sell goods).
    /// </summary>
    [Serializable]
    public struct MarketTradeRequestPacket
    {
        public int PortIndex;
        public int GoodIndex;
        public bool IsBuying;
        public int CurrencyIndex;  // T3: the guest's selected payment currency (EconomyUI.currentPlayerCurrency),
                                   // so the host charges the right wallet at currency-conversion ports.
    }

    /// <summary>
    /// Host -> Guest result of a market trade the guest requested. On success the guest plays the gold
    /// sound + money notif (vanilla plays these in the local actor, which is the host in co-op); on
    /// rejection it shows the vanilla-style notification the guest never saw (the buy click was silent).
    /// </summary>
    [Serializable]
    public struct MarketTradeResultPacket
    {
        public bool Success;
        public byte Reason;       // 0=ok, 1=out of stock, 2=not enough money
        public int CurrencyIndex; // region index for the money notif
        public int Amount;        // price (positive)
        public bool IsBuying;
    }

    /// <summary>
    /// Shopkeeper trade request (guest buys/sells item to/from shopkeeper).
    /// </summary>
    [Serializable]
    public struct ShopTradeRequestPacket
    {
        public int PortIndex;              // Region for currency
        public float ShopkeeperPosX;       // Position to find shopkeeper (if host nearby)
        public float ShopkeeperPosY;
        public float ShopkeeperPosZ;
        public int GoodIndex;              // For economy update (IslandMarket): the GOOD index (ItemToGoodIndex) - kept for currency/economy logic
        public int Price;                  // Authoritative deduction amount
        public bool IsBuying;              // true = guest buying from shop
        public int CurrencyIndex;          // Wallet slot the guest actually paid with (-1 = host falls back to port region). Mirrors MarketTradeRequestPacket.CurrencyIndex.
        public int PrefabIndex;            // The RAW prefab index (saveable.prefabIndex) of a bought item. GoodIndex<->ItemIndex is only a bijection for real goods; the dead band (31..200, non-good stall items) breaks the round-trip, so the host spawns directly from PrefabsDirectory.directory[PrefabIndex]. -1 / 0 = no valid prefab (host won't authoritatively spawn).
        // v0.2.20 WIRE CHANGE (appended at END - field order must match Write/ReadShopTradeRequest):
        // item STATE of the actual stall display item. "Cooked" is NOT a prefab - CookInShop sets
        // ShipItem.amount>=1 on the same raw prefab (decomp CookableFood.cs:90-98) - so a pristine host
        // Instantiate silently un-cooks the purchase. Carry amount/health + the 4 FoodState floats so
        // SpawnAuthoritativeStallItem reproduces the exact item the buyer paid for.
        public float ItemAmount;           // ShipItem.amount (>=1 means cooked for CookableFood)
        public float ItemHealth;           // ShipItem.health
        public float FoodDried;            // FoodState.dried (0 if no FoodState component)
        public float FoodSmoked;           // FoodState.smoked
        public float FoodSalted;           // FoodState.salted
        public float FoodSpoiled;          // FoodState.spoiled
    }

    /// <summary>
    /// Host -> requesting guest: outcome of a ShopTradeRequest (stall buy). With the guest's local wallet
    /// gate bypassed (ShopkeeperTryToSellItemPatch), a host-side reject (insufficient shared funds)
    /// was silent AND the buyer's parked optimistic display item needs a verdict: success => destroy it
    /// (authoritative ItemSpawned copy replaces it), reject => restore it to the stall + show the vanilla
    /// "Not enough money." notification. Mirrors MarketTradeResultPacket.
    /// </summary>
    [Serializable]
    public struct ShopTradeResultPacket
    {
        public bool Success;
        public byte Reason;       // 0=ok, 2=not enough money (matches MarketTradeResult reason codes)
        public int PriceAmount;   // price (positive)
        public int CurrencyIndex; // wallet slot the trade charged
        // v0.2.20 WIRE CHANGE (appended at END - field order must match Write/ReadShopTradeResult):
        // SaveablePrefab.instanceId of the host-authoritative spawned item on success (0 on reject /
        // no-spawn). The buyer uses it to auto-pick the inbound canonical copy into a free hand,
        // restoring vanilla ShipItem.Sell hand-attach parity.
        public int SpawnedItemId;
    }

    /// <summary>
    /// Notification that a shop item was purchased (to remove from other player's view).
    /// Sent bidirectionally - both host and guest send this when buying from vendor stall.
    /// </summary>
    [Serializable]
    public struct ShopItemBoughtPacket
    {
        public int PrefabIndex;        // Item prefab type (to verify match)
        public float PositionX;        // Original position before pickup
        public float PositionY;
        public float PositionZ;
    }

    /// <summary>
    /// Single day sheet (profits and expenses by category for one day).
    /// Mirrors game's DaySheet structure.
    /// </summary>
    [Serializable]
    public struct NetworkDaySheet
    {
        public int Day;
        public int[] Profits;    // 15 categories
        public int[] Expenses;   // 15 categories
    }

    /// <summary>
    /// Transaction delta for live sync when host logs transaction.
    /// </summary>
    [Serializable]
    public struct TransactionDeltaPacket
    {
        public int CurrencyIndex;   // 0-3
        public int Amount;          // Always positive
        public int Category;        // TransactionCategory enum
        public bool IsProfit;       // true = profit, false = expense
    }

    /// <summary>
    /// Full day logs sync (on join).
    /// Structure: [4 currencies][21 sheets per currency (20 days + allTime)]
    /// </summary>
    [Serializable]
    public struct DayLogsFullSyncPacket
    {
        public NetworkDaySheet[][] Logs; // [4][21]
    }
}
