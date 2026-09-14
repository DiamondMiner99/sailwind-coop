using HarmonyLib;
using SailwindCoop.Debug;
using SailwindCoop.Networking.Packets;
using SailwindCoop.Sync;
using UnityEngine;

namespace SailwindCoop.Patches
{
    /// <summary>
    /// Patches for trading synchronization.
    /// </summary>
    public static class TradingPatches
    {
        // IslandMarket.Update patch REMOVED - was costing 1.9ms (32 markets × every frame)
        // (2026-09-11) The guest no longer runs the economy for ports the host has sent: see GuestEconCyclePatch
        // and TradingSyncManager's market round-robin.

        /// <summary>
        /// Sync item spawns from market purchases (host buying directly).
        /// Replaces SpawnGood to add sync call after spawning.
        /// </summary>
        /// <summary>
        /// (2026-09-11, market prices) A guest does not run the random supply drift for a port whose supply the
        /// host is sending. Called by vanilla Update several times a second per market and 50 times by
        /// DoPreheat, so this stays a single set lookup. Ports without host data yet keep simulating.
        /// </summary>
        [HarmonyPatch(typeof(IslandMarket), "EconCycle")]
        public static class GuestEconCyclePatch
        {
            [HarmonyPrefix]
            public static bool Prefix(IslandMarket __instance)
            {
                if (!Plugin.IsMultiplayer || Plugin.IsHost) return true;
                return !TradingSyncManager.IsPortHostSynced(__instance.GetPortIndex());
            }
        }

        /// <summary>
        /// (2026-09-11, market prices) Trader boats are not synced at all; every machine runs its own on its own
        /// random routes, and on arrival they buy and sell into that machine's market. On a guest that moved a
        /// host-owned port's supply between host broadcasts. Skip the trade itself for such ports; the boat's
        /// movement, route choice and price-report hand-off (TraderBoat.Update / EnterIsland) are untouched.
        /// </summary>
        [HarmonyPatch(typeof(TraderBoat), "SellGoods")]
        public static class GuestTraderSellPatch
        {
            [HarmonyPrefix]
            public static bool Prefix(TraderBoat __instance) => !GuestSkipsTraderTrade(__instance);
        }

        [HarmonyPatch(typeof(TraderBoat), "BuyGoods")]
        public static class GuestTraderBuyPatch
        {
            [HarmonyPrefix]
            public static bool Prefix(TraderBoat __instance) => !GuestSkipsTraderTrade(__instance);
        }

        private static bool GuestSkipsTraderTrade(TraderBoat trader)
        {
            if (!Plugin.IsMultiplayer || Plugin.IsHost) return false;
            var market = Traverse.Create(trader).Field("currentIslandMarket").GetValue<IslandMarket>();
            return market != null && TradingSyncManager.IsPortHostSynced(market.GetPortIndex());
        }

        /// <summary>
        /// (2026-09-11, market prices) Exchange rates. The guest skips its own daily random walk once it holds
        /// the host's rates; the host flags a send whenever its rates move (daily walk, or an exchange,
        /// including one it executes for a guest).
        /// </summary>
        [HarmonyPatch(typeof(CurrencyMarket), nameof(CurrencyMarket.MarketCycle))]
        public static class CurrencyMarketCyclePatch
        {
            [HarmonyPrefix]
            public static bool Prefix() => !TradingSyncManager.HasHostCurrencyRates;

            [HarmonyPostfix]
            public static void Postfix() => TradingSyncManager.Instance?.MarkCurrencyRatesDirty();
        }

        [HarmonyPatch(typeof(CurrencyMarket), nameof(CurrencyMarket.BuyCurrency))]
        public static class CurrencyMarketBuyPatch
        {
            [HarmonyPostfix]
            public static void Postfix() => TradingSyncManager.Instance?.MarkCurrencyRatesDirty();
        }

        [HarmonyPatch(typeof(CurrencyMarket), nameof(CurrencyMarket.SellCurrency))]
        public static class CurrencyMarketSellPatch
        {
            [HarmonyPostfix]
            public static void Postfix() => TradingSyncManager.Instance?.MarkCurrencyRatesDirty();
        }

        [HarmonyPatch(typeof(IslandMarket), nameof(IslandMarket.SpawnGood))]
        public static class SpawnGoodPatch
        {
            [HarmonyPrefix]
            public static bool Prefix(IslandMarket __instance, GameObject goodPrefab)
            {
                // Only intercept in multiplayer when host
                if (!Plugin.IsMultiplayer || !Plugin.IsHost) return true;

                // Replicate SpawnGood behavior
                var spawned = Object.Instantiate(goodPrefab, __instance.transform.position + Vector3.up, __instance.transform.rotation);
                spawned.GetComponent<ShipItem>().sold = true;
                spawned.GetComponent<SaveablePrefab>().RegisterToSave();
                spawned.GetComponent<Good>().RegisterAsMissionless();

                // Sync spawned item to guest
                var instanceId = spawned.GetComponent<SaveablePrefab>()?.instanceId ?? 0;
                ItemSyncManager.Instance?.OnLocalItemSpawned(spawned.GetComponent<ShipItem>());
                VerboseLogger.Log("TRADING", "SPAWN", $"Market item spawned and synced: {goodPrefab.name}, id={instanceId}");

                return false; // Skip original
            }
        }

        /// <summary>
        /// When host opens trade UI, sync supply + prices to guest immediately.
        /// This ensures guest has correct values even if they open UI before periodic sync.
        /// </summary>
        [HarmonyPatch(typeof(EconomyUI), nameof(EconomyUI.OpenUI))]
        public static class EconomyUIOpenPatch
        {
            [HarmonyPostfix]
            public static void Postfix(IslandMarket islandMarket)
            {
                if (!Plugin.IsMultiplayer) return;
                if (islandMarket == null) return;

                // (2026-09-11) Guest: ask for the host's price book for this island (the "other ports"
                // columns). Everything below is the host half.
                if (!Plugin.IsHost)
                {
                    TradingSyncManager.Instance?.RequestPriceBook(islandMarket.GetPortIndex());
                    return;
                }

                // Sync supply immediately (guest economy sim may have drifted)
                TradingSyncManager.Instance?.SyncMarketSupply(islandMarket);

                int portIndex = islandMarket.GetPortIndex();

                // Also sync price discovery
                if (GameState.playerKnownPrices == null || portIndex >= GameState.playerKnownPrices.Length)
                    return;

                var pr = GameState.playerKnownPrices[portIndex];
                if (pr == null) return;

                var packet = new PriceDiscoveryPacket
                {
                    Report = new NetworkPriceReport
                    {
                        PortIndex = portIndex,
                        BuyPrices = pr.buyPrices != null ? (int[])pr.buyPrices.Clone() : new int[0],
                        SellPrices = pr.sellPrices != null ? (int[])pr.sellPrices.Clone() : new int[0],
                        Day = pr.day,
                        Approved = pr.approved
                    }
                };

                VerboseLogger.Log("TRADING", "SEND", $"PriceDiscovery, port={portIndex}");

                Plugin.NetworkManager.SendToAllReliable(PacketType.PriceDiscovery, w =>
                    PacketSerializer.WritePriceDiscovery(w, packet));
            }
        }

        /// <summary>
        /// Broadcast transaction delta when host logs a transaction.
        /// Positive amount = profit, negative amount = expense.
        /// </summary>
        [HarmonyPatch(typeof(DayLog), nameof(DayLog.LogTransaction), new[] { typeof(int), typeof(TransactionCategory) })]
        public static class LogTransactionPatch
        {
            [HarmonyPostfix]
            public static void Postfix(DayLog __instance, int price, TransactionCategory category)
            {
                if (!Plugin.IsMultiplayer || !Plugin.IsHost) return;
                if (price == 0) return;

                // Find currency index for this DayLog
                int currencyIndex = GetCurrencyIndex(__instance);
                if (currencyIndex < 0) return;

                var packet = new TransactionDeltaPacket
                {
                    CurrencyIndex = currencyIndex,
                    Amount = price >= 0 ? price : -price, // Always positive
                    Category = (int)category,
                    IsProfit = price > 0
                };

                VerboseLogger.Log("TRADING", "SEND", $"TransactionDelta: currency={currencyIndex}, amount={price}, category={category}, profit={packet.IsProfit}");

                // T1: a guest-originated SHOP trade is logged optimistically on the requester (vanilla ran via
                // Postfix), so exclude that requester from the authoritative broadcast to avoid a double-count.
                // For everything else (host's own trades, guest MARKET trades whose vanilla was blocked) the flag
                // is null and we broadcast to all.
                var exclude = TradingSyncManager.DeltaExcludeRequester;
                if (exclude.HasValue)
                    Plugin.NetworkManager.SendToAllExcept(exclude.Value, PacketType.TransactionDelta, w =>
                        PacketSerializer.WriteTransactionDelta(w, packet));
                else
                    Plugin.NetworkManager.SendToAllReliable(PacketType.TransactionDelta, w =>
                        PacketSerializer.WriteTransactionDelta(w, packet));
            }
        }

        private static int GetCurrencyIndex(DayLog dayLog)
        {
            if (DayLogs.instance?.dayLogs == null) return -1;

            for (int i = 0; i < DayLogs.instance.dayLogs.Length; i++)
            {
                if (DayLogs.instance.dayLogs[i] == dayLog)
                    return i;
            }

            return -1;
        }
    }
}
