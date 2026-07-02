using UnityEngine;
using HarmonyLib;
using SailwindCoop.Debug;
using SailwindCoop.Networking.Packets;
using Steamworks;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Manages trading synchronization: price knowledge, supply, and trade requests.
    /// </summary>
    public class TradingSyncManager : MonoBehaviour
    {
        public static TradingSyncManager Instance { get; private set; }

        // Polling state
        private float _supplyPollTimer = 0f;
        private const float SUPPLY_POLL_INTERVAL = 1f; // 1Hz
        private int _lastDockedPortIndex = -1;

        private void Awake()
        {
            if (Instance != null)
            {
                Destroy(this);
                return;
            }
            Instance = this;
        }

        private void Update()
        {
            if (!Plugin.IsMultiplayer) return;

            Plugin.Profiler?.StartMeasure();

            if (Plugin.IsHost)
            {
                PollIslandSupply();
            }

            Plugin.Profiler?.EndMeasure("Trading");
        }

        #region Host: Supply Polling

        private void PollIslandSupply()
        {
            _supplyPollTimer += Time.deltaTime;
            if (_supplyPollTimer < SUPPLY_POLL_INTERVAL) return;
            _supplyPollTimer = 0f;

            // Check if player is docked at a port
            var currentIsland = GetCurrentDockedIsland();
            if (currentIsland == null)
            {
                _lastDockedPortIndex = -1;
                return;
            }

            int portIndex = currentIsland.GetPortIndex();

            // Send supply sync
            SendIslandSupplySync(portIndex, currentIsland);
            _lastDockedPortIndex = portIndex;
        }

        private IslandMarket GetCurrentDockedIsland()
        {
            // Check if EconomyUI is open or player is in market area
            if (EconomyUI.instance != null && EconomyUI.instance.uiActive)
            {
                return Traverse.Create(EconomyUI.instance)
                    .Field("currentIsland")
                    .GetValue<IslandMarket>();
            }
            return null;
        }

        private void SendIslandSupplySync(int portIndex, IslandMarket market)
        {
            var supply = Traverse.Create(market)
                .Field("currentSupply")
                .GetValue<float[]>();

            if (supply == null) return;

            var packet = new IslandSupplySyncPacket
            {
                PortIndex = portIndex,
                Supply = (float[])supply.Clone()
            };

            VerboseLogger.Log("TRADING", "SEND", $"IslandSupply, port={portIndex}");

            Plugin.NetworkManager.SendToAllReliable(PacketType.IslandSupplySync, w =>
                PacketSerializer.WriteIslandSupplySync(w, packet));
        }

        /// <summary>
        /// Public method to sync market supply immediately (called when trade UI opens).
        /// </summary>
        public void SyncMarketSupply(IslandMarket market)
        {
            if (!Plugin.IsHost || market == null) return;
            SendIslandSupplySync(market.GetPortIndex(), market);
        }

        #endregion

        #region Host: Initial Sync

        /// <summary>
        /// Send full price knowledge and day logs on guest join. Broadcasts to all peers.
        /// </summary>
        public void SendFullStateImmediate()
        {
            if (!Plugin.IsHost) return;

            SendPriceKnowledgeSync();
            SendDayLogsSync();
        }

        /// <summary>
        /// Send full trading state to ONE joining guest (N-player Phase 3 targeted join resync).
        /// Same payload as SendFullStateImmediate, targeted so already-settled guests aren't re-synced.
        /// At N=1 the target is the only peer, so this == the broadcast.
        /// </summary>
        public void SendFullStateTo(SteamId target)
        {
            if (!Plugin.IsHost) return;

            SendPriceKnowledgeSync(target);
            SendDayLogsSync(target);
        }

        // N-player (Phase 3): optional `target` routes to ONE joining guest; null => broadcast (unchanged).
        // At N=1 the target is the only peer, so a targeted send == the broadcast.
        private void SendPriceKnowledgeSync(SteamId? target = null)
        {
            var knownPrices = GameState.playerKnownPrices;
            if (knownPrices == null) return;

            var reports = new NetworkPriceReport[knownPrices.Length];
            for (int i = 0; i < knownPrices.Length; i++)
            {
                var pr = knownPrices[i];
                if (pr == null)
                {
                    reports[i] = new NetworkPriceReport { PortIndex = i };
                    continue;
                }

                reports[i] = new NetworkPriceReport
                {
                    PortIndex = i,
                    BuyPrices = pr.buyPrices != null ? (int[])pr.buyPrices.Clone() : new int[0],
                    SellPrices = pr.sellPrices != null ? (int[])pr.sellPrices.Clone() : new int[0],
                    Day = pr.day,
                    Approved = pr.approved
                };
            }

            var packet = new PriceKnowledgeSyncPacket { Reports = reports };

            VerboseLogger.Log("TRADING", "SEND", $"PriceKnowledge, ports={reports.Length}{(target.HasValue ? $" (to {target.Value})" : "")}");

            if (target.HasValue)
                Plugin.NetworkManager.SendReliable(target.Value, PacketType.PriceKnowledgeSync, w =>
                    PacketSerializer.WritePriceKnowledgeSync(w, packet));
            else
                Plugin.NetworkManager.SendToAllReliable(PacketType.PriceKnowledgeSync, w =>
                    PacketSerializer.WritePriceKnowledgeSync(w, packet));
        }

        private void SendDayLogsSync(SteamId? target = null)
        {
            if (DayLogs.instance?.dayLogs == null)
            {
                VerboseLogger.Log("TRADING", "WARN", "DayLogs not available for sync");
                return;
            }

            var packet = new DayLogsFullSyncPacket
            {
                Logs = new NetworkDaySheet[4][]
            };

            for (int c = 0; c < 4; c++)
            {
                packet.Logs[c] = new NetworkDaySheet[21];

                if (c >= DayLogs.instance.dayLogs.Length || DayLogs.instance.dayLogs[c] == null)
                {
                    // Fill with empty sheets
                    for (int s = 0; s < 21; s++)
                    {
                        packet.Logs[c][s] = new NetworkDaySheet { Profits = new int[15], Expenses = new int[15] };
                    }
                    continue;
                }

                var dayLog = DayLogs.instance.dayLogs[c];

                // First 20 entries are day sheets
                for (int s = 0; s < 20; s++)
                {
                    if (dayLog.daySheets != null && s < dayLog.daySheets.Length && dayLog.daySheets[s] != null)
                    {
                        packet.Logs[c][s] = ExtractDaySheet(dayLog.daySheets[s]);
                    }
                    else
                    {
                        packet.Logs[c][s] = new NetworkDaySheet { Profits = new int[15], Expenses = new int[15] };
                    }
                }

                // 21st entry is allTimeSheet
                if (dayLog.allTimeSheet != null)
                {
                    packet.Logs[c][20] = ExtractDaySheet(dayLog.allTimeSheet);
                }
                else
                {
                    packet.Logs[c][20] = new NetworkDaySheet { Profits = new int[15], Expenses = new int[15] };
                }
            }

            VerboseLogger.Log("TRADING", "SEND", $"DayLogsFullSync{(target.HasValue ? $" (to {target.Value})" : "")}");

            if (target.HasValue)
                Plugin.NetworkManager.SendReliable(target.Value, PacketType.DayLogsFullSync, w =>
                    PacketSerializer.WriteDayLogsFullSync(w, packet));
            else
                Plugin.NetworkManager.SendToAllReliable(PacketType.DayLogsFullSync, w =>
                    PacketSerializer.WriteDayLogsFullSync(w, packet));
        }

        private NetworkDaySheet ExtractDaySheet(DaySheet sheet)
        {
            return new NetworkDaySheet
            {
                Day = sheet.day,
                Profits = sheet.profits != null ? (int[])sheet.profits.Clone() : new int[15],
                Expenses = sheet.expenses != null ? (int[])sheet.expenses.Clone() : new int[15]
            };
        }

        #endregion

        #region Guest: Receive Handlers

        public void OnPriceKnowledgeSyncReceived(PriceKnowledgeSyncPacket packet)
        {
            if (Plugin.IsHost) return;

            VerboseLogger.Log("TRADING", "RECV", $"PriceKnowledge, ports={packet.Reports?.Length ?? 0}");

            if (packet.Reports == null) return;

            // Initialize if needed
            if (GameState.playerKnownPrices == null)
                GameState.playerKnownPrices = new PriceReport[34];

            for (int i = 0; i < packet.Reports.Length && i < 34; i++)
            {
                var nr = packet.Reports[i];
                if (nr.BuyPrices == null || nr.BuyPrices.Length == 0) continue;

                GameState.playerKnownPrices[i] = new PriceReport
                {
                    buyPrices = nr.BuyPrices,
                    sellPrices = nr.SellPrices,
                    day = nr.Day,
                    approved = nr.Approved
                };
            }

            VerboseLogger.Log("TRADING", "APPLY", "PriceKnowledge applied");
        }

        public void OnPriceDiscoveryReceived(PriceDiscoveryPacket packet)
        {
            if (Plugin.IsHost) return;

            var nr = packet.Report;
            VerboseLogger.Log("TRADING", "RECV", $"PriceDiscovery, port={nr.PortIndex}");

            if (GameState.playerKnownPrices == null)
                GameState.playerKnownPrices = new PriceReport[34];

            if (nr.PortIndex >= 0 && nr.PortIndex < 34)
            {
                GameState.playerKnownPrices[nr.PortIndex] = new PriceReport
                {
                    buyPrices = nr.BuyPrices,
                    sellPrices = nr.SellPrices,
                    day = nr.Day,
                    approved = nr.Approved
                };
            }

            VerboseLogger.Log("TRADING", "APPLY", $"PriceDiscovery for port {nr.PortIndex}");
        }

        public void OnIslandSupplySyncReceived(IslandSupplySyncPacket packet)
        {
            if (Plugin.IsHost) return;

            VerboseLogger.Log("TRADING", "RECV", $"IslandSupply, port={packet.PortIndex}");

            // Find the market and apply supply
            if (Port.ports == null || packet.PortIndex < 0 || packet.PortIndex >= Port.ports.Length)
                return;

            var port = Port.ports[packet.PortIndex];
            if (port == null) return;

            var market = port.GetComponent<IslandMarket>();
            if (market == null) return;

            var supplyField = Traverse.Create(market).Field("currentSupply");
            var currentSupply = supplyField.GetValue<float[]>();

            if (currentSupply != null && packet.Supply != null)
            {
                for (int i = 0; i < packet.Supply.Length && i < currentSupply.Length; i++)
                {
                    currentSupply[i] = packet.Supply[i];
                }
            }

            // TRADE-REFRESH: just updating currentSupply isn't enough - whoever has THIS
            // market's trade screen open keeps showing the stale buy/sell prices (vanilla only recomputes them
            // on open), so a remote trade was invisible until close+reopen.
            RefreshOpenMarketUI(market);

            VerboseLogger.Log("TRADING", "APPLY", $"IslandSupply for port {packet.PortIndex}");
        }

        /// <summary>Re-show the open trade screen if it's displaying THIS market, so a remotely-applied change
        /// (the host's broadcast supply on a guest, or a guest's trade the host just executed on the host)
        /// updates the buy/sell prices live instead of going stale until close+reopen. RefreshPage() is public
        /// and self-guards on ui.activeInHierarchy; we also match the market so an unrelated port's UI is left
        /// alone. Used by both sides, so the live refresh is bidirectional.</summary>
        public static void RefreshOpenMarketUI(IslandMarket market)
        {
            if (market == null || EconomyUI.instance == null || !EconomyUI.instance.uiActive) return;
            var openIsland = Traverse.Create(EconomyUI.instance).Field("currentIsland").GetValue<IslandMarket>();
            if (openIsland == market) EconomyUI.instance.RefreshPage();
        }

        /// <summary>Resolve a port's market and refresh the open trade UI if it's that market (host-side, after
        /// applying a guest's trade so the host's own open screen updates too).</summary>
        private static void RefreshOpenMarketUIForPort(int portIndex)
        {
            if (Port.ports == null || portIndex < 0 || portIndex >= Port.ports.Length) return;
            RefreshOpenMarketUI(Port.ports[portIndex]?.GetComponent<IslandMarket>());
        }

        public void OnDayLogsFullSyncReceived(DayLogsFullSyncPacket packet)
        {
            if (Plugin.IsHost) return;

            VerboseLogger.Log("TRADING", "RECV", "DayLogsFullSync");

            if (DayLogs.instance?.dayLogs == null)
            {
                VerboseLogger.Log("TRADING", "WARN", "DayLogs not initialized on guest");
                return;
            }

            for (int c = 0; c < 4 && c < packet.Logs.Length; c++)
            {
                if (c >= DayLogs.instance.dayLogs.Length || DayLogs.instance.dayLogs[c] == null)
                    continue;

                var dayLog = DayLogs.instance.dayLogs[c];

                // Apply day sheets
                for (int s = 0; s < 20 && s < packet.Logs[c].Length; s++)
                {
                    if (dayLog.daySheets != null && s < dayLog.daySheets.Length)
                    {
                        ApplyDaySheet(dayLog.daySheets[s], packet.Logs[c][s]);
                    }
                }

                // Apply allTimeSheet
                if (packet.Logs[c].Length > 20 && dayLog.allTimeSheet != null)
                {
                    ApplyDaySheet(dayLog.allTimeSheet, packet.Logs[c][20]);
                }
            }

            VerboseLogger.Log("TRADING", "APPLY", "DayLogsFullSync applied");
        }

        private void ApplyDaySheet(DaySheet target, NetworkDaySheet source)
        {
            if (target == null) return;

            target.day = source.Day;

            if (source.Profits != null && target.profits != null)
            {
                for (int i = 0; i < source.Profits.Length && i < target.profits.Length; i++)
                {
                    target.profits[i] = source.Profits[i];
                }
            }

            if (source.Expenses != null && target.expenses != null)
            {
                for (int i = 0; i < source.Expenses.Length && i < target.expenses.Length; i++)
                {
                    target.expenses[i] = source.Expenses[i];
                }
            }
        }

        public void OnTransactionDeltaReceived(TransactionDeltaPacket packet)
        {
            if (Plugin.IsHost) return;

            VerboseLogger.Log("TRADING", "RECV", $"TransactionDelta: currency={packet.CurrencyIndex}, amount={packet.Amount}, category={packet.Category}, profit={packet.IsProfit}");

            if (DayLogs.instance?.dayLogs == null) return;
            if (packet.CurrencyIndex < 0 || packet.CurrencyIndex >= DayLogs.instance.dayLogs.Length) return;

            var dayLog = DayLogs.instance.dayLogs[packet.CurrencyIndex];
            if (dayLog == null) return;

            // LogTransaction uses positive for profit, negative for expense
            int signedAmount = packet.IsProfit ? packet.Amount : -packet.Amount;
            dayLog.LogTransaction(signedAmount, (TransactionCategory)packet.Category);

            VerboseLogger.Log("TRADING", "APPLY", $"TransactionDelta applied to currency {packet.CurrencyIndex}");
        }

        /// <summary>
        /// Guest: outcome of a market trade we requested. Success -> gold sound + money notif (the
        /// feedback vanilla plays in the local actor, which is the host in co-op). Rejection -> the
        /// vanilla-style notification the guest never saw, fixing the "Buy did nothing" confusion.
        /// </summary>
        public void OnMarketTradeResultReceived(MarketTradeResultPacket packet)
        {
            if (Plugin.IsHost) return;

            if (packet.Success)
            {
                UISoundPlayer.instance?.PlayGoldSound();
                int signed = packet.IsBuying ? -packet.Amount : packet.Amount; // buy = expense, sell = profit
                MoneyNotification.instance?.PlayNotif(signed, packet.CurrencyIndex);
                VerboseLogger.Log("TRADING", "APPLY", $"MarketTradeResult success, signed={signed}");
            }
            else
            {
                string msg;
                switch (packet.Reason)
                {
                    case 2: msg = "Not enough money."; break;
                    case 3: msg = "Nothing to sell."; break;
                    default: msg = "Out of stock."; break; // reason 1
                }
                NotificationUi.instance?.ShowNotification(msg, 3f);
                VerboseLogger.Log("TRADING", "APPLY", $"MarketTradeResult reject, reason={packet.Reason}");
            }
        }

        #endregion

        #region Guest: Trade Requests

        public void RequestMarketTrade(int portIndex, int goodIndex, bool isBuying, int currencyIndex = -1)
        {
            if (Plugin.IsHost) return;

            var packet = new MarketTradeRequestPacket
            {
                PortIndex = portIndex,
                GoodIndex = goodIndex,
                IsBuying = isBuying,
                CurrencyIndex = currencyIndex  // T3: selected payment currency (-1 = host falls back to port region)
            };

            VerboseLogger.Log("TRADING", "SEND", $"MarketTradeRequest, port={portIndex}, good={goodIndex}, buying={isBuying}, currency={currencyIndex}");

            Plugin.NetworkManager.SendToAllReliable(PacketType.MarketTradeRequest, w =>
                PacketSerializer.WriteMarketTradeRequest(w, packet));
        }

        // Note: RequestShopTrade is no longer used - shopkeeper patches send packets directly
        // after optimistic local execution (see EconomyPatches.ShopkeeperBuyPatch/SellPatch)

        #endregion

        #region Host: Trade Request Handlers

        public void OnMarketTradeRequestReceived(SteamId sender, MarketTradeRequestPacket packet)
        {
            if (!Plugin.IsHost) return;

            VerboseLogger.Log("TRADING", "RECV", $"MarketTradeRequest from {sender}, port={packet.PortIndex}, good={packet.GoodIndex}, buying={packet.IsBuying}");

            ExecuteMarketTrade(sender, packet.PortIndex, packet.GoodIndex, packet.IsBuying, packet.CurrencyIndex);

            // The host applied a GUEST's trade; the host doesn't receive its own supply broadcast, so refresh
            // the host's own open trade screen here (the guest's own screen + other guests refresh via the
            // broadcast). Completes the bidirectional live-refresh.
            RefreshOpenMarketUIForPort(packet.PortIndex);
        }

        // T1: when the host logs a GUEST-originated SHOP trade, the requesting guest ALREADY logged it
        // optimistically (vanilla Shopkeeper Buy/SellItem ran via Postfix before sending the request). Set this
        // to that requester for the duration of the host's LogShopTransaction so LogTransactionPatch excludes
        // them from the authoritative TransactionDelta broadcast (else their day-log double-counts the trade).
        // Null = broadcast to all (used by host's own trades AND market trades, where the requester's vanilla
        // path was BLOCKED so it did NOT log optimistically and therefore needs the delta).
        public static SteamId? DeltaExcludeRequester;

        public void OnShopTradeRequestReceived(SteamId sender, ShopTradeRequestPacket packet)
        {
            if (!Plugin.IsHost) return;

            VerboseLogger.Log("TRADING", "RECV", $"ShopTradeRequest from {sender}, port={packet.PortIndex}, good={packet.GoodIndex}, price={packet.Price}, buying={packet.IsBuying}");

            DeltaExcludeRequester = sender;
            try { ExecuteShopTrade(sender, packet); }
            finally { DeltaExcludeRequester = null; }

            // Re-assert the authoritative wallet to the buyer. A SUCCESSFUL trade is
            // already broadcast to everyone by CheckAndSyncCurrency (host-wallet delta), but a REJECT produces no
            // delta and thus no correction, leaving the buyer stuck at the optimistic local deduction vanilla
            // SellItem already applied. This targeted, cache-neutral resync corrects the buyer either way.
            EconomySyncManager.Instance?.ResyncCurrencyTo(sender);

            // Refresh the host's own open trade screen for the guest's shop trade (see OnMarketTradeRequestReceived).
            RefreshOpenMarketUIForPort(packet.PortIndex);
        }

        /// <summary>
        /// A guest confirmed a shipyard order. Vanilla Shipyard.ConfirmOrder
        /// charged the guest's LOCAL mirror of the shared wallet (so only the guest's money dropped); the
        /// guest's ConfirmOrder patch RESTORED that local wallet deduction but deliberately KEPT its
        /// optimistic day-log entry (so this requester must be EXCLUDED from the host's TransactionDelta
        /// below - do NOT re-add a guest-side day-log undo). It then sent this request. The host now
        /// performs the AUTHORITATIVE charge against the shared wallet here, mirroring ExecuteShopTrade.
        /// The host's OWN shipyard orders never reach this path (they run vanilla ConfirmOrder unchanged).
        /// </summary>
        public void OnShipyardOrderRequestReceived(SteamId sender, ShipyardOrderRequestPacket packet)
        {
            if (!Plugin.IsHost) return;

            VerboseLogger.Log("TRADING", "RECV", $"ShipyardOrderRequest from {sender}, boat={packet.BoatName}, region={packet.Region}, total={packet.Total}");

            // The requesting guest already logged this order optimistically (vanilla Shipyard.ConfirmOrder ran
            // its -Total expenses[boat] entry, and our patch deliberately did NOT undo it - see
            // ShipyardConfirmOrderPatch). Exclude that requester from the host's authoritative TransactionDelta
            // broadcast so their day-log doesn't double-count; other peers still receive the delta. Mirrors the
            // SHOP-trade path (OnShopTradeRequestReceived).
            DeltaExcludeRequester = sender;
            try { ExecuteShipyardOrder(sender, packet); }
            finally { DeltaExcludeRequester = null; }

            // Re-assert the authoritative wallet to the requester (cache-neutral). A SUCCESSFUL charge already
            // reaches everyone via CheckAndSyncCurrency's host-wallet delta, but a REJECT (insufficient funds)
            // produces no delta - this corrects the requester either way. Mirrors the shop-trade reject path.
            EconomySyncManager.Instance?.ResyncCurrencyTo(sender);
        }

        /// <summary>
        /// Host-authoritative shipyard charge. Validates + deducts against the SHARED wallet (the same
        /// currency slot vanilla ConfirmOrder charges, packet.Region) and logs the expense to the day log
        /// (TransactionCategory.boat, matching vanilla). LogTransaction is patched (LogTransactionPatch) to
        /// broadcast the TransactionDelta - the caller (OnShipyardOrderRequestReceived) has set
        /// DeltaExcludeRequester = sender, so the requesting guest (whose own vanilla expenses[boat] entry was
        /// kept, NOT undone) is EXCLUDED from the broadcast to avoid a double-count; all other peers receive it.
        /// Mirrors the SHOP-trade path. A NEGATIVE total is a net refund (vanilla does currency -= total =>
        /// adds), handled symmetrically.
        /// </summary>
        private void ExecuteShipyardOrder(SteamId sender, ShipyardOrderRequestPacket packet)
        {
            int region = packet.Region;
            if (PlayerGold.currency == null || region < 0 || region >= PlayerGold.currency.Length)
            {
                VerboseLogger.Log("TRADING", "REJECT", $"ShipyardOrder: invalid region {region}");
                return;
            }

            int total = packet.Total;
            if (total == 0)
            {
                VerboseLogger.Log("TRADING", "INFO", "ShipyardOrder: zero total, nothing to charge");
                return;
            }

            // Vanilla affordability check (only meaningful for a positive charge; a refund always succeeds).
            if (total > 0 && PlayerGold.currency[region] < total)
            {
                VerboseLogger.Log("TRADING", "REJECT", $"ShipyardOrder: not enough money in region {region} (have {PlayerGold.currency[region]}, need {total})");
                return;
            }

            // Authoritative charge/refund, exactly as vanilla Shipyard.ConfirmOrder: currency[region] -= total.
            PlayerGold.currency[region] -= total;

            // Day-log the boat expense/refund (vanilla logs -total under TransactionCategory.boat). The host's
            // LogTransactionPatch broadcasts the delta to all peers EXCEPT the requester (the caller set
            // DeltaExcludeRequester = sender), whose own optimistic vanilla expenses[boat] entry was kept.
            if (DayLogs.instance?.dayLogs != null && region < DayLogs.instance.dayLogs.Length)
                DayLogs.instance.dayLogs[region]?.LogTransaction(-total, TransactionCategory.boat);

            VerboseLogger.Log("TRADING", "EXEC", $"ShipyardOrder charged: region={region}, total={total}, newBalance={PlayerGold.currency[region]}");
        }

        #endregion

        #region Host: Trade Execution

        private void ExecuteMarketTrade(SteamId requester, int portIndex, int goodIndex, bool isBuying, int currencyIndex = -1)
        {
            if (Port.ports == null || portIndex < 0 || portIndex >= Port.ports.Length)
            {
                Plugin.Log.LogWarning($"Invalid port index: {portIndex}");
                return;
            }

            var port = Port.ports[portIndex];
            if (port == null) return;

            var market = port.GetComponent<IslandMarket>();
            if (market == null)
            {
                Plugin.Log.LogWarning($"No IslandMarket at port {portIndex}");
                return;
            }

            int region = (int)port.region;
            // T3: charge the wallet the guest actually selected (EconomyUI.currentPlayerCurrency). -1 / out of
            // range falls back to the port region (legacy behavior / same-currency case).
            int currency = (currencyIndex >= 0 && PlayerGold.currency != null && currencyIndex < PlayerGold.currency.Length)
                ? currencyIndex : region;
            bool withConversionFee = region != currency;
            // Defensive (vanilla EconomyUI gates the buy/sell buttons the same way): a non-local currency is
            // only valid when the port allows currency conversion. The guest UI already enforces this, but the
            // host must not honor a spoofed/edge request that would charge a wallet the port doesn't accept.
            if (withConversionFee && !market.allowCurrencyConversion)
            {
                VerboseLogger.Log("TRADING", "REJECT", $"Currency {currency} not accepted at region {region} (no conversion)");
                SendMarketTradeResult(requester, false, 1, currency, 0, isBuying);
                return;
            }

            if (isBuying)
            {
                // Buying from market
                if (!market.HasGood(goodIndex))
                {
                    VerboseLogger.Log("TRADING", "REJECT", $"Market has no stock of good {goodIndex}");
                    SendMarketTradeResult(requester, false, 1, currency, 0, true); // base-game-checks g1: guest feedback
                    return;
                }

                // T3: convert the port-region base price into the selected currency, matching vanilla
                // EconomyUI.GetBuyPrice -> CurrencyMarket.GetBuyPriceInCurrency.
                int basePrice = market.GetBuyPrice(goodIndex);
                int price = CurrencyMarket.instance != null
                    ? CurrencyMarket.instance.GetBuyPriceInCurrency((Currency)currency, basePrice, withConversionFee)
                    : basePrice;
                if (PlayerGold.currency[currency] < price)
                {
                    VerboseLogger.Log("TRADING", "REJECT", $"Not enough currency: have {PlayerGold.currency[currency]}, need {price}");
                    SendMarketTradeResult(requester, false, 2, currency, price, true); // base-game-checks g1: "not enough money"
                    return;
                }

                // Execute purchase - update supply
                market.PurchaseGood(goodIndex);

                // Spawn item manually (instead of market.SpawnGood) so we can sync it
                int itemIndex = PrefabsDirectory.GoodToItemIndex(goodIndex);
                var prefab = PrefabsDirectory.instance.directory[itemIndex];
                var spawned = Object.Instantiate(prefab, market.transform.position + Vector3.up, market.transform.rotation);
                spawned.GetComponent<ShipItem>().sold = true;
                spawned.GetComponent<SaveablePrefab>().RegisterToSave();
                spawned.GetComponent<Good>().RegisterAsMissionless();

                // Sync spawned item to guest
                ItemSyncManager.Instance?.OnLocalItemSpawned(spawned.GetComponent<ShipItem>());

                PlayerGold.currency[currency] -= price;

                // T2: log the trade so it appears in the day log (vanilla EconomyUI.BuyGood logged it, but that
                // path is blocked for guests). LogShopTransaction -> LogTransactionPatch broadcasts the delta to
                // ALL (DeltaExcludeRequester is null here, and the requester's vanilla was blocked so it needs it).
                LogShopTransaction(currency, price, isBuying: true, goodIndex);

                VerboseLogger.Log("TRADING", "EXEC", $"Market BUY: good={goodIndex}, price={price}, currency={currency}, itemId={spawned.GetComponent<SaveablePrefab>()?.instanceId}");
                SendMarketTradeResult(requester, true, 0, currency, price, true); // guest-sounds: gold sound + money notif
            }
            else
            {
                // Selling to market
                var playerGoods = Traverse.Create(market).Field("currentPlayerGoods").GetValue<int[]>();
                if (playerGoods == null || playerGoods[goodIndex] <= 0)
                {
                    VerboseLogger.Log("TRADING", "REJECT", $"Player has no good {goodIndex} to sell");
                    SendMarketTradeResult(requester, false, 3, currency, 0, false); // base-game-checks g1: nothing to sell (reason 3)
                    return;
                }

                // T3: convert the port-region base sell price into the selected currency (vanilla GetSellPrice).
                int basePrice = market.GetSellPrice(goodIndex);
                int price = CurrencyMarket.instance != null
                    ? CurrencyMarket.instance.GetSellPriceInCurrency((Currency)currency, basePrice, withConversionFee)
                    : basePrice;

                // SellGood updates supply, DespawnGood destroys item
                // DestroyItem is already patched to sync destruction
                market.SellGood(goodIndex);
                market.DespawnGood(goodIndex);

                PlayerGold.currency[currency] += price;

                // T2: log the sell (see BUY branch).
                LogShopTransaction(currency, price, isBuying: false, goodIndex);

                VerboseLogger.Log("TRADING", "EXEC", $"Market SELL: good={goodIndex}, price={price}, currency={currency}");
                SendMarketTradeResult(requester, true, 0, currency, price, false); // guest-sounds: gold sound + money notif
            }

            // Send immediate supply sync after any trade
            SendIslandSupplySync(portIndex, market);
        }

        /// <summary>
        /// Host -> Guest: tell the requesting guest the outcome of a market trade so it can play the
        /// gold sound / money notif on success (vanilla plays these locally on the actor = the host in
        /// co-op) or show the "not enough money."/"out of stock." notification it never saw on rejection
        /// (the guest's buy click was routed to the host and returned silently).
        /// </summary>
        private void SendMarketTradeResult(SteamId target, bool success, byte reason, int currencyIndex, int amount, bool isBuying)
        {
            if (!Plugin.IsHost) return;
            // N-player: target ONLY the requesting guest, so uninvolved crew don't get phantom gold sounds /
            // money notifs / reject toasts for a trade they didn't make. The host has the sender SteamId via
            // OnMarketTradeRequestReceived. At N=1 the requester is the only guest, so this is identical to
            // a broadcast.
            var packet = new MarketTradeResultPacket
            {
                Success = success,
                Reason = reason,
                CurrencyIndex = currencyIndex,
                Amount = amount,
                IsBuying = isBuying
            };
            Plugin.NetworkManager.SendReliable(target, PacketType.MarketTradeResult, w =>
                PacketSerializer.WriteMarketTradeResult(w, packet));
            VerboseLogger.Log("TRADING", "SEND", $"MarketTradeResult to {target}, success={success}, reason={reason}, amount={amount}, buying={isBuying}");
        }

        /// <summary>
        /// Host -> requesting guest: outcome of a stall (Shopkeeper) buy. Targeted like
        /// SendMarketTradeResult so uninvolved crew never see phantom feedback. Sent on BOTH outcomes:
        /// reject drives the guest's item-restore + "Not enough money." toast; success releases (destroys)
        /// the guest's parked optimistic copy.
        /// </summary>
        private void SendShopTradeResult(SteamId target, bool success, byte reason, int currencyIndex, int amount)
        {
            if (!Plugin.IsHost) return;
            var packet = new ShopTradeResultPacket
            {
                Success = success,
                Reason = reason,
                PriceAmount = amount,
                CurrencyIndex = currencyIndex
            };
            Plugin.NetworkManager.SendReliable(target, PacketType.ShopTradeResult, w =>
                PacketSerializer.WriteShopTradeResult(w, packet));
            VerboseLogger.Log("TRADING", "SEND", $"ShopTradeResult to {target}, success={success}, reason={reason}, amount={amount}");
        }

        /// <summary>
        /// Guest: outcome of a stall buy we requested. Success -> destroy the parked optimistic display
        /// item (vanilla already played the gold sound/notif locally in Shopkeeper.SellItem; the
        /// authoritative ItemSpawned copy is inbound). Reject -> vanilla-style "Not enough money."
        /// notification (mirrors OnMarketTradeResultReceived) and restore the parked item to the stall.
        /// </summary>
        public void OnShopTradeResultReceived(ShopTradeResultPacket packet)
        {
            if (Plugin.IsHost) return;

            if (packet.Success)
            {
                VerboseLogger.Log("TRADING", "APPLY", $"ShopTradeResult success, amount={packet.PriceAmount}");
                SailwindCoop.Patches.EconomyPatches.ShopkeeperSellItemPatch.ResolvePendingStallBuy(true);
            }
            else
            {
                NotificationUi.instance?.ShowNotification("Not enough money.", 3f);
                VerboseLogger.Log("TRADING", "APPLY", $"ShopTradeResult reject, reason={packet.Reason}");
                SailwindCoop.Patches.EconomyPatches.ShopkeeperSellItemPatch.ResolvePendingStallBuy(false);
            }
        }

        private void ExecuteShopTrade(SteamId sender, ShopTradeRequestPacket packet)
        {
            // Get port and region
            if (Port.ports == null || packet.PortIndex < 0 || packet.PortIndex >= Port.ports.Length)
            {
                VerboseLogger.Log("TRADING", "REJECT", $"Invalid port index: {packet.PortIndex}");
                // A silent early return would let the guest's parked buy item settle as SUCCESS via
                // the 30s timeout (item destroyed, nothing spawned). Reject explicitly + resync the wallet.
                if (packet.IsBuying)
                {
                    SendShopTradeResult(sender, false, 2, 0, packet.Price);
                    EconomySyncManager.Instance?.ResyncCurrencyTo(sender);
                }
                return;
            }

            var port = Port.ports[packet.PortIndex];
            if (port == null)
            {
                VerboseLogger.Log("TRADING", "REJECT", $"Port {packet.PortIndex} is null");
                if (packet.IsBuying)
                {
                    SendShopTradeResult(sender, false, 2, 0, packet.Price);
                    EconomySyncManager.Instance?.ResyncCurrencyTo(sender);
                }
                return;
            }

            int region = (int)port.region;
            // Charge the wallet the guest actually paid with (packet.CurrencyIndex), falling back to the port
            // region for legacy/same-currency requests. Mirrors ExecuteMarketTrade. Using `region` directly
            // would mean a guest paying from currency slot 2 while this dock's port.region resolved to 0 hits
            // "Insufficient currency: have 0, need 42" and the whole charge is rejected -> the shared wallet
            // is never debited and the buyer keeps the item for free.
            int currency = (packet.CurrencyIndex >= 0 && PlayerGold.currency != null && packet.CurrencyIndex < PlayerGold.currency.Length)
                ? packet.CurrencyIndex : region;

            // Validate and deduct currency
            if (packet.IsBuying)
            {
                // Guest bought from shop - host deducts currency
                if (PlayerGold.currency[currency] < packet.Price)
                {
                    VerboseLogger.Log("TRADING", "REJECT", $"Insufficient currency: have {PlayerGold.currency[currency]}, need {packet.Price} (currency {currency})");
                    // With the guest's local wallet gate bypassed (ShopkeeperTryToSellItemPatch), a silent
                    // reject would strand the buyer's parked optimistic item and give zero feedback. Tell
                    // the requester so it restores the stall item + shows "Not enough money.". The guest's
                    // vanilla Shopkeeper.SellItem already deducted its LOCAL wallet optimistically (the
                    // gate bypass runs it before arbitration); the host wallet is unchanged here so no
                    // CurrencySync fires on its own - resync the requester explicitly (mirrors the market
                    // reject path) or its local wallet stays skewed / can go negative.
                    SendShopTradeResult(sender, false, 2, currency, packet.Price);
                    EconomySyncManager.Instance?.ResyncCurrencyTo(sender);
                    return;
                }
                PlayerGold.currency[currency] -= packet.Price;
                VerboseLogger.Log("TRADING", "EXEC", $"ShopBuy deducted {packet.Price} from currency {currency}");

                // Spawn the guest's stall-buy AUTHORITATIVELY on the host, mirroring ExecuteMarketTrade's BUY
                // block exactly, then OnLocalItemSpawned broadcasts the existing ItemSpawned packet so every
                // peer (incl. the buyer) receives the host-authoritative copy. Without this, the bought item
                // is invisible to the host + other guests and the guest's optimistic vanilla copy carries an
                // id the host never registered (-> pickup denied -> forced drop). The buyer suppresses its own
                // optimistic item in EconomyPatches.ShopkeeperSellItemPatch, so there is no duplicate.
                // Spawn BEFORE confirming - if we confirmed first and the spawn then degraded (bad prefab
                // slot / exception), the guest would have already destroyed its parked copy: money charged,
                // item nowhere. On spawn failure refund the deduction, reject, and resync.
                if (!SpawnAuthoritativeStallItem(sender, packet))
                {
                    PlayerGold.currency[currency] += packet.Price;
                    VerboseLogger.Log("TRADING", "REJECT", $"ShopBuy authoritative spawn failed; refunded {packet.Price} to currency {currency}");
                    SendShopTradeResult(sender, false, 2, currency, packet.Price);
                    EconomySyncManager.Instance?.ResyncCurrencyTo(sender);
                    return;
                }
                // Confirm the buy so the requester destroys its parked optimistic copy (the
                // authoritative ItemSpawned from SpawnAuthoritativeStallItem is the canonical item).
                SendShopTradeResult(sender, true, 0, currency, packet.Price);
            }
            else
            {
                // Guest sold to shop - host adds currency
                PlayerGold.currency[currency] += packet.Price;
                VerboseLogger.Log("TRADING", "EXEC", $"ShopSell added {packet.Price} to currency {currency}");
            }

            // Log transaction to DayLog
            LogShopTransaction(currency, packet.Price, packet.IsBuying, packet.GoodIndex);

            // Try to update island economy if host is at same island
            TryUpdateIslandEconomy(packet);
        }

        /// <summary>
        /// Host-authoritative spawn of a stall-bought item.
        ///
        /// PREFAB: we spawn DIRECTLY from PrefabsDirectory.directory[packet.PrefabIndex] - the same indexing
        /// the directory uses (SaveablePrefab.prefabIndex == directory index). Do NOT round-trip through
        /// GoodToItemIndex(ItemToGoodIndex(prefabIndex)): that is a bijection ONLY for real goods (1..30, 201+).
        /// For the dead band 31..200 (tools/clothing/lanterns/cookware/instruments) ItemToGoodIndex is a no-op
        /// but GoodToItemIndex then ADDS 170, resolving the WRONG prefab (or out of range) - non-good buys
        /// would spawn the wrong item / nothing while the buyer already paid. GoodIndex stays only for economy logic.
        ///
        /// POSITION (floating origin): packet.ShopkeeperPos is a world coord in the GUEST's
        /// floating-origin frame. FloatingOriginManager.outCurrentOffset is per-client, so instantiating at that
        /// raw value in the HOST's frame (and then OnLocalItemSpawned subtracting the host offset) puts the item
        /// in the ocean/underground when host and guest aren't co-located. We resolve the host's OWN local
        /// shopkeeper near the packet position (reusing TryUpdateIslandEconomy's pattern) and spawn there. If the
        /// host isn't co-located (no local shopkeeper found), we fall back to the BUYER's tracked avatar position
        /// (RemotePlayerManager) - which is already maintained in the HOST's frame - so the item appears at/near
        /// the buyer on every client, never at a cross-frame world coord.
        ///
        /// Then mark sold, register to save + as missionless, and OnLocalItemSpawned broadcasts the existing
        /// ItemSpawned packet to all peers (incl. the buyer, who suppressed its optimistic copy) - no duplicate.
        /// </summary>
        private bool SpawnAuthoritativeStallItem(SteamId sender, ShopTradeRequestPacket packet)
        {
            // Use the RAW prefab index directly. prefabIndex 0 is the directory's null slot.
            int prefabIndex = packet.PrefabIndex;
            if (prefabIndex <= 0)
            {
                // A guest that couldn't resolve a SaveablePrefab sends prefabIndex <= 0; it ALSO left its
                // optimistic copy intact (no suppression), so we degrade gracefully here by NOT spawning - the
                // buyer keeps its own item. (Currency was still deducted authoritatively above, matching vanilla.)
                // This IS a completed purchase, so report success (the buyer parked nothing to restore).
                VerboseLogger.Log("TRADING", "WARN", $"ShopBuy: no valid prefab index ({prefabIndex}); skipping authoritative spawn (buyer keeps optimistic copy)");
                return true;
            }

            if (PrefabsDirectory.instance == null || PrefabsDirectory.instance.directory == null)
            {
                VerboseLogger.Log("TRADING", "WARN", "ShopBuy: PrefabsDirectory not available");
                return false;
            }

            if (prefabIndex >= PrefabsDirectory.instance.directory.Length)
            {
                VerboseLogger.Log("TRADING", "WARN", $"ShopBuy: prefab index {prefabIndex} out of range");
                return false;
            }

            var prefab = PrefabsDirectory.instance.directory[prefabIndex];
            if (prefab == null)
            {
                VerboseLogger.Log("TRADING", "WARN", $"ShopBuy: prefab at index {prefabIndex} is null");
                return false;
            }

            // Floating origin: resolve a frame-correct spawn position.
            var guestShopkeeperPos = new Vector3(packet.ShopkeeperPosX, packet.ShopkeeperPosY, packet.ShopkeeperPosZ);
            Vector3 spawnPos;
            if (TryResolveLocalShopkeeperPos(guestShopkeeperPos, out var localShopkeeperPos))
            {
                // Host is co-located: use the host's OWN local shopkeeper transform (host frame).
                spawnPos = localShopkeeperPos + Vector3.up;
            }
            else
            {
                // Host not co-located: spawn at the BUYER's avatar position (already tracked in the host frame),
                // so the item lands at/near the buyer on every client instead of a cross-frame ocean coord.
                var avatarPos = SailwindCoop.Player.RemotePlayerManager.Instance?.GetLastKnownPosition(sender) ?? Vector3.zero;
                if (avatarPos == Vector3.zero)
                {
                    // Last resort (no avatar yet / unknown): the guest's own avatar isn't tracked - keep the item
                    // near the local player so it's at least reachable rather than at world origin.
                    avatarPos = Refs.charController != null
                        ? Refs.charController.transform.position
                        : (FloatingOriginManager.instance != null ? FloatingOriginManager.instance.outCurrentOffset : Vector3.zero);
                    VerboseLogger.Log("TRADING", "WARN", $"ShopBuy: no local shopkeeper and no avatar for {sender}; spawning near local player");
                }
                spawnPos = avatarPos + Vector3.up;
            }

            try
            {
                var spawned = Object.Instantiate(prefab, spawnPos, Quaternion.identity);
                spawned.GetComponent<ShipItem>().sold = true;
                spawned.GetComponent<SaveablePrefab>().RegisterToSave();
                spawned.GetComponent<Good>()?.RegisterAsMissionless();

                // Broadcast the spawned item to all peers (existing ItemSpawned packet) - includes the buyer, whose
                // optimistic vanilla copy was suppressed in EconomyPatches so only this authoritative one remains.
                ItemSyncManager.Instance?.OnLocalItemSpawned(spawned.GetComponent<ShipItem>());

                VerboseLogger.Log("TRADING", "SPAWN", $"ShopBuy authoritative item spawned: prefab={prefabIndex} ({prefab.name}), good={packet.GoodIndex}, pos={spawnPos}, id={spawned.GetComponent<SaveablePrefab>()?.instanceId}");
                return true;
            }
            catch (System.Exception ex)
            {
                VerboseLogger.Log("TRADING", "WARN", $"ShopBuy: authoritative spawn threw: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Find the host's OWN local shopkeeper nearest the guest-sent world position, within ~5m, and return its
        /// host-frame transform position. Mirrors TryUpdateIslandEconomy's nearest-shopkeeper search so both use
        /// the same tolerance. Returns false if the host isn't co-located (no shopkeeper within tolerance).
        /// </summary>
        private bool TryResolveLocalShopkeeperPos(Vector3 guestShopkeeperPos, out Vector3 localPos)
        {
            localPos = Vector3.zero;
            var shopkeepers = UnityEngine.Object.FindObjectsOfType<Shopkeeper>();
            Shopkeeper nearest = null;
            float nearestDist = float.MaxValue;
            foreach (var sk in shopkeepers)
            {
                float dist = Vector3.Distance(sk.transform.position, guestShopkeeperPos);
                if (dist < nearestDist && dist < 5f) // within 5m tolerance (same as TryUpdateIslandEconomy)
                {
                    nearestDist = dist;
                    nearest = sk;
                }
            }
            if (nearest == null) return false;
            localPos = nearest.transform.position;
            return true;
        }

        private void LogShopTransaction(int region, int price, bool isBuying, int goodIndex)
        {
            if (DayLogs.instance == null || DayLogs.instance.dayLogs == null) return;
            if (region < 0 || region >= DayLogs.instance.dayLogs.Length) return;

            var dayLog = DayLogs.instance.dayLogs[region];
            if (dayLog == null) return;

            // LogTransaction uses positive=profit, negative=expense
            // Category: bulkGood(9) for goods, otherItems(5) for other items
            int signedPrice = isBuying ? -price : price;
            var category = goodIndex >= 0 ? TransactionCategory.bulkGood : TransactionCategory.otherItems;
            dayLog.LogTransaction(signedPrice, category);

            VerboseLogger.Log("TRADING", "LOG", $"Transaction logged: region={region}, amount={signedPrice}, buying={isBuying}");
        }

        private void TryUpdateIslandEconomy(ShopTradeRequestPacket packet)
        {
            // Only update economy if host is at the same port
            var currentIsland = GetCurrentDockedIsland();
            if (currentIsland == null) return;

            int currentPortIndex = currentIsland.GetPortIndex();
            if (currentPortIndex != packet.PortIndex) return;

            // Find shopkeeper by position
            var shopkeeperPos = new Vector3(packet.ShopkeeperPosX, packet.ShopkeeperPosY, packet.ShopkeeperPosZ);
            var shopkeepers = UnityEngine.Object.FindObjectsOfType<Shopkeeper>();

            Shopkeeper nearestShopkeeper = null;
            float nearestDist = float.MaxValue;

            foreach (var sk in shopkeepers)
            {
                float dist = Vector3.Distance(sk.transform.position, shopkeeperPos);
                if (dist < nearestDist && dist < 5f) // Within 5m tolerance
                {
                    nearestDist = dist;
                    nearestShopkeeper = sk;
                }
            }

            if (nearestShopkeeper == null)
            {
                VerboseLogger.Log("TRADING", "WARN", $"Shopkeeper not found near {shopkeeperPos}");
                return;
            }

            // Update island economy
            if (packet.GoodIndex >= 0)
            {
                if (packet.IsBuying)
                {
                    currentIsland.PurchaseGood(packet.GoodIndex);
                    VerboseLogger.Log("TRADING", "ECON", $"Island economy updated: PurchaseGood({packet.GoodIndex})");
                }
                else
                {
                    currentIsland.SellGood(packet.GoodIndex);
                    VerboseLogger.Log("TRADING", "ECON", $"Island economy updated: SellGood({packet.GoodIndex})");
                }
            }
        }

        // TAVERN (unified): charge the crew ONCE when sleep actually STARTS (host-authoritative), not at
        // click. The host captures the room's region+price at click and charges it in
        // SleepSyncManager.TransitionToSleeping, after both players are committed. This fixes the gold leak
        // (charged-then-never-sleep), free-on-insufficient-funds, and free-repeat-rooms findings.
        private static int _pendingTavernRegion = -1;
        private static int _pendingTavernPrice = 0;

        public static void SetPendingTavernCharge(int region, int price)
        {
            _pendingTavernRegion = region;
            _pendingTavernPrice = price;
        }

        public static void ClearPendingTavern()
        {
            VerboseLogger.Log("TRADING", "CLEAR", $"Tavern pending cleared without charging: region={_pendingTavernRegion}, price={_pendingTavernPrice}");
            _pendingTavernRegion = -1;
            _pendingTavernPrice = 0;
        }

        /// <summary>
        /// Host-only. Charge the pending tavern room now that sleep is actually starting. Returns true if the
        /// room is paid (or there was nothing pending); false if the crew can't afford it (caller aborts sleep).
        /// </summary>
        public static bool TryChargePendingTavern()
        {
            if (!Plugin.IsHost) return true;
            if (_pendingTavernRegion < 0 || _pendingTavernPrice <= 0) return true; // nothing to charge
            int region = _pendingTavernRegion, price = _pendingTavernPrice;
            if (PlayerGold.currency[region] < price)
            {
                VerboseLogger.Log("TRADING", "REJECT", $"Tavern charge FAILED: insufficient funds, region={region}, have={PlayerGold.currency[region]}, need={price}");
                return false;
            }
            PlayerGold.currency[region] -= price;
            if (DayLogs.instance?.dayLogs != null && region < DayLogs.instance.dayLogs.Length)
                DayLogs.instance.dayLogs[region]?.LogTransaction(-price, TransactionCategory.other);
            MoneyNotification.instance?.PlayNotif(-price, region);
            VerboseLogger.Log("TRADING", "EXEC", $"Tavern charge SUCCESS: region={region}, price={price}, newBalance={PlayerGold.currency[region]}");
            ClearPendingTavern();
            return true;
        }

        #endregion

        public void Reset()
        {
            _supplyPollTimer = 0f;
            _lastDockedPortIndex = -1;
        }
    }
}
