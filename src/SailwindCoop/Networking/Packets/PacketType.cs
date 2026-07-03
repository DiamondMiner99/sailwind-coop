namespace SailwindCoop.Networking.Packets
{
    public enum PacketType : byte
    {
        // Connection (0-9)
        Handshake = 0,
        HandshakeAck = 1,
        Disconnect = 2,
        Ping = 3,
        Pong = 4,

        // Player (10-19)
        PlayerPosition = 10,
        PlayerRotation = 11,
        PlayerState = 12,

        // Boat (20-29)
        BoatTransform = 20,
        BoatWorldState = 21,          // Full world sync on join
        BoatAngularVelocity = 22,
        CurrentBoatChanged = 23,
        AnchorStateChanged = 24,
        WindStateChanged = 25,
        BoatOwnershipChanged = 26,    // Host -> peers: boat purchased/ownership changed at runtime
        BoatPurchaseRequest = 27,     // Guest -> host: request to buy a boat against the shared wallet

        // Controls (30-39)
        RopeState = 30,               // Continuous rope length
        HelmState = 31,               // Continuous helm state (host -> guest)
        AnchorEvent = 32,             // Anchor set/release
        MooringState = 33,            // Mooring attach/detach
        ApplyForce = 34,              // Push force from guest
        MooringRopeLength = 35,       // Mooring rope length adjustment
        HelmInput = 36,               // Helm input delta (guest -> host)
        HelmLock = 37,                // Helm lock state (bidirectional)
        HelmDenied = 38,              // Host -> Guest: your helm input was rejected (you don't hold the lease)

        // World (40-49)
        TimeState = 40,
        WindState = 41,
        WeatherState = 42,
        ShipyardCustomization = 43,

        // Push sync (44-47) - event-based push sync
        PushStart = 45,
        PushUpdate = 46,
        PushStop = 47,

        // Survival (50-59)
        SurvivalStats = 50,
        SleepRequest = 51,        // Guest/host entered bed
        SleepApproved = 52,       // Both in bed, sleep starts
        SleepWaiting = 53,        // Someone waiting (renamed from SleepDenied)
        WakeUp = 54,
        ActivityState = 55,       // Guest movement/activity flags
        ConsumptionDelta = 56,    // Guest eating/drinking stat changes
        SleepCancelled = 57,      // Waiting player left bed
        SleepCycleState = 58,     // Eyes open/closed, timeScale sync
        SleepRested = 59,         // Guest -> Host: guest's own sleep is fully restored (both-rested gate)

        // Events (60-69)
        PlayerDeath = 60,
        PlayerRespawn = 61,
        ChatMessage = 62,

        // Items (70-89)
        ItemPickedUp = 70,        // Player grabbed item
        ItemDropped = 71,         // Player released item
        ItemPickupRequest = 72,   // Guest requests to pick up (guest -> host)
        ItemPickupDenied = 73,    // Host denies pickup (host -> guest)
        ItemSpawned = 74,         // New item created (bought, etc.)
        ItemDestroyed = 75,       // Item removed from world
        ItemAmountChanged = 76,   // Bulk item amount changed (water barrel, etc.)
        ItemCrateInsert = 77,     // Item placed in crate
        ItemCrateRemove = 78,     // Item taken from crate
        ItemHung = 79,            // Item hung on hook
        ItemUnhung = 80,          // Item removed from hook
        CrateUnsealed = 81,       // Crate was unsealed, items spawned
        ItemResync = 82,          // Host → Guest: fix mismatched item
        ItemHealthChanged = 83,   // Bulk item health changed (consumption)
        CrateUnsealRequest = 84,  // Guest requests to unseal (guest -> host)

        // Damage (85-87)
        DamageState = 85,          // Host -> Guest: periodic full damage state
        DamageImpact = 86,         // Host -> Guest: immediate impact event
        GuestPumpInput = 87,       // Guest -> Host: bilge pump input

        // Recovery (88-89)
        RecoveryStarted = 88,      // Host started recovery (host -> guest)
        RecoveryEnded = 89,        // Host finished recovery (host -> guest)

        // Missions (90-99)
        MissionStateSync = 90,      // Full mission array (on join)
        MissionAccepted = 91,       // New mission accepted
        MissionProgress = 92,       // Delivery progress update
        MissionCompleted = 93,      // Mission completed
        MissionAbandoned = 94,      // Mission abandoned
        MissionAcceptRequest = 95,  // Guest requests to accept mission
        MissionAbandonRequest = 96, // Guest requests to abandon mission
        MissionBoardRequest = 97,   // Guest requests mission board data
        MissionBoardResponse = 98,  // Host sends mission board data

        // Economy (100-109)
        CurrencySync = 100,         // Full currency array (4 ints)
        ReputationSync = 101,       // Full reputation array (4 ints)
        // 102 unused (was TradeRequest, replaced by MarketTradeRequest/ShopTradeRequest)
        DeliverGoodRequest = 103,   // Guest delivered cargo
        ExchangeRequest = 104,      // Guest requests currency exchange
        EconomySyncRequest = 105,   // Guest requests economy re-sync (after join recovery)

        // Trading sync packets (110-119)
        PriceKnowledgeSync = 110,    // Host → Guest: Full price knowledge on join
        PriceDiscovery = 111,        // Host → Guest: Single port price update
        IslandSupplySync = 112,      // Host → Guest: Current island stock
        MarketTradeRequest = 113,    // Guest → Host: Market buy/sell
        ShopTradeRequest = 114,      // Guest → Host: Shopkeeper buy/sell
        // 115 retired: TavernSleepPayment (old click-time tavern charge; replaced by charge-at-sleep-start)
        TransactionDelta = 116,      // Host → Guest: Live transaction delta
        DayLogsFullSync = 117,       // Host → Guest: Full day logs on join
        ShopItemBought = 118,        // Bidirectional: shop item purchased, remove from stall
        MarketTradeResult = 119,     // Host → Guest: market trade outcome (gold sound on success, "not enough money"/"out of stock" notify on reject)

        // Fishing sync packets (120-129)
        FishingStateSync = 120,        // Owner → Other: tension, energy while hooked (5Hz)
        FishingLineLengthSync = 121,   // Owner → Other: line length on change
        FishBite = 122,                // Owner → Other: fish hooked event
        FishEscape = 123,              // Owner → Other: line snapped / fish escaped
        FishCollectRequest = 124,      // Owner → Host: request to collect fish
        FishCollectResponse = 125,     // Host → All: fish collected, item spawned
        RodOwnerChanged = 126,         // New owner → Other: ownership transfer
        FishingCast = 127,             // Owner → Other: rod cast event

        // Navigation sync packets (130-142)
        NavItemState = 130,           // Simple nav items (clock lid, quadrant inspect, spyglass zoom, compass dial)
        MapFoldState = 135,           // Map fold/unfold
        MapDrawRequest = 136,         // Guest requests drawing permission
        MapDrawResponse = 137,        // Host grants/denies
        MapDrawLocked = 138,          // Host notifies lock state
        MapDrawRelease = 139,         // Lock released
        MapLineAdd = 140,             // Line committed
        MapTempLine = 141,            // Line in progress
        MapFullSync = 142,            // Initial sync of all lines

        // Cooking sync packets (150-169)
        CookingState = 150,              // Host → Guest: 2Hz full cooking state
        StoveLightRequest = 151,         // Guest → Host: light stove (drop fuel)
        FoodPlaceOnStoveRequest = 152,   // Guest → Host: place food on stove
        FoodRemoveFromStoveRequest = 153,// Guest → Host: remove food from stove
        FoodCutRequest = 154,            // Guest → Host: cut food with knife
        FoodCutResult = 155,             // Host → Guest: cut result with slice IDs
        FoodSaltRequest = 156,           // Guest → Host: salt food
        SoupAddFoodRequest = 157,        // Guest → Host: add food to soup
        SoupAddWaterRequest = 158,       // Guest → Host: add water to soup
        KettleAddWaterRequest = 159,     // Guest → Host: add water to kettle
        KettleAddTeaRequest = 160,       // Guest → Host: add tea to kettle
        KettlePourRequest = 161,         // Guest → Host: pour tea to mug
        FuelInsertedEvent = 162,         // Host → Guest: fuel inserted into stove

        // NPC Boat sync packets (170-179)
        NPCBoatState = 170,              // Host → Guest: single NPC boat transform+sails (2Hz)
        NPCBoatSnapshot = 171,           // Host → Guest: all visible NPC boats on join
        NPCBoatDamage = 172,             // Host → Guest: authoritative NPC boat damage/sink state
        NPCBoatHitRequest = 173,         // Guest → Host: guest rammed an NPC boat, host applies+relays

        // Additional damage packets (180+)
        GuestOakumRepair = 180,          // Guest → Host: oakum repair request
        GuestBailRequest = 181,          // Guest → Host: bucket/bottle bailing water

        // Item state sync (190+)
        LightState = 190,                // Bidirectional: lantern/light on/off state
        PipeFilled = 191,                // Bidirectional: pipe filled with tobacco

        // Cleaning sync packets (195-199)
        CleaningStroke = 195,            // Bidirectional: broom cleaning stroke UV coords
        CleanFully = 196,                // Bidirectional: shipyard hull cleaning

        // Shipyard order (197)
        ShipyardOrderRequest = 197,      // Guest -> Host: charge a shipyard order against the shared wallet

        // Shop trade result (198): targeted host -> requester outcome of a stall trade
        ShopTradeResult = 198,           // Host -> requesting guest: stall buy accepted/rejected (reject => restore optimistic item + notify)

        // Guest join completion (199): explicit end-of-join signal for targeted resyncs
        GuestJoinComplete = 199,         // Guest -> Host: join coroutine finished; host replies with a targeted mission-cargo resync

        // Network diagnostics (200+): unreliable-path RTT measurement for the F8 overlay
        PingRequest = 200,               // Bidirectional: "echo this back" - carries sender's Time.realtimeSinceStartup
        PingReply = 201,                 // Bidirectional: verbatim echo of PingRequest.SendTime; receiver computes RTT

        // Nail sync (202): hammer nail/un-nail state on a ShipItem
        NailState = 202,                 // Bidirectional: item nailed (kinematic) or un-nailed; host relays

        // Fishing bobber stream (203): the bobber launch is emergent local physics on the owner
        // (rod angular velocity + owner camera forward), so viewers never reproduce it
        FishingBobberSync = 203,         // Owner -> all: cast bobber position in boat frame (or world); host relays

        // Chip log sync (204-205): the chip log throw/payout is entirely local physics in vanilla
        // (thrown flag + line auto-unroll only run on the thrower's machine), so viewers see a
        // parked bobber and a zero speedometer without these
        ChipLogThrow = 204,              // Thrower -> all: chip log thrown (replays ThrowRod); host relays
        ChipLogLineSync = 205,           // Thrower -> all: 5Hz line length + thrown flag stream; host relays

        // Charting kit ghost (206-207): the map-drawing kit/quill/ruler are a per-client camera-mode
        // rig on layer 23 in vanilla, so bystanders see nothing while a crewmate charts
        ChartSession = 206,              // Drawer -> all (reliable): charting session start/stop + kit placement; host relays + replays to late joiners
        ChartCursor = 207,               // Drawer -> all (UNRELIABLE, 10Hz coalesced): chart-local cursor for the ghost quill/ruler; host relays

        // Crew spending feed (208): the host observes EVERY crew trade (its own vanilla trades plus
        // every guest trade it executes), so only the host emits; guests never send this. Additive
        // wire - v0.2.22 clients silently drop the unknown type.
        TradeFeedEvent = 208,            // Host -> all guests: actor SteamId, buy/sell, item name, price, currency
    }
}
