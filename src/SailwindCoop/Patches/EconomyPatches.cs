using HarmonyLib;
using SailwindCoop.Networking.Packets;
using SailwindCoop.Sync;

namespace SailwindCoop.Patches
{
    /// <summary>
    /// Harmony patches for economy synchronization.
    /// Protects guest currency and reputation from local modification.
    /// Guest receives these values only via sync from host.
    ///
    /// NOTE: PlayerGold has no AddGold/RemoveGold methods - currency is modified directly
    /// via PlayerGold.currency[region] += amount. PlayerReputation has ChangeReputation().
    ///
    /// Strategy: Patch the high-level methods that trigger currency/reputation changes.
    /// The guest will see optimistic local feedback but EconomySyncManager will overwrite
    /// with authoritative host values immediately after.
    /// </summary>
    public static class EconomyPatches
    {
        #region Block avatar merchant

        /// <summary>
        /// The remote player avatar is a runtime clone of a shopkeeper NPC. If its trade trigger ever survives
        /// the avatar strip, walking near it with a held shop item opens the merchant screen. Block the trade
        /// UI whenever the shopkeeper that triggered it is (under) the remote avatar. Provably correct
        /// regardless of which component survived, and a no-op for real port merchants.
        /// </summary>
        [HarmonyPatch(typeof(BuyItemUI), nameof(BuyItemUI.ActivateUI))]
        public static class BlockAvatarMerchantPatch
        {
            [HarmonyPrefix]
            public static bool Prefix(Shopkeeper shopkeeper)
            {
                var manager = SailwindCoop.Player.RemotePlayerManager.Instance;
                if (manager != null && shopkeeper != null)
                {
                    // Block if the triggering shopkeeper is (under) ANY remote crew member's avatar.
                    foreach (var avatar in manager.Avatars)
                    {
                        var cap = avatar.GetRemoteCapsule();
                        if (cap != null && shopkeeper.transform.IsChildOf(cap))
                        {
                            Debug.VerboseLogger.Log("ECONOMY", "BLOCK", "Avatar merchant trade UI blocked");
                            return false; // never let a player avatar act as a shop
                        }
                    }
                }
                return true;
            }
        }

        #endregion

        #region Guest Reputation Protection

        /// <summary>
        /// Block guest reputation changes - only accept from sync.
        /// This is the single method that modifies reputation.
        /// </summary>
        [HarmonyPatch(typeof(PlayerReputation), nameof(PlayerReputation.ChangeReputation))]
        public static class ChangeReputationPatch
        {
            [HarmonyPrefix]
            public static bool Prefix(int rep, PortRegion region)
            {
                // Guest should only receive reputation changes from sync
                if (Plugin.IsMultiplayer && !Plugin.IsHost)
                {
                    Debug.VerboseLogger.Log("ECONOMY", "BLOCK", $"Guest reputation change blocked: {rep} for region {region}");
                    return false;
                }
                return true;
            }
        }

        #endregion

        #region Guest Currency Protection - High-Level Patches

        // Currency is directly modified via PlayerGold.currency[i] += amount
        // We can't patch array access, so we patch the high-level operations.
        // These patches block the entire transaction for guests.

        /// <summary>
        /// Intercept guest buying at market and route to host.
        /// </summary>
        [HarmonyPatch(typeof(EconomyUI), "BuyGood")]
        public static class BuyGoodPatch
        {
            [HarmonyPrefix]
            public static bool Prefix(EconomyUI __instance)
            {
                if (Plugin.IsMultiplayer && !Plugin.IsHost)
                {
                    var currentIsland = Traverse.Create(__instance).Field("currentIsland").GetValue<IslandMarket>();
                    var currentSelectedGood = Traverse.Create(__instance).Field("currentSelectedGood").GetValue<int>();
                    // T3: forward the guest's selected payment currency so the host charges the right wallet at
                    // currency-conversion ports (vanilla deducts from PlayerGold.currency[currentPlayerCurrency]).
                    var currency = (int)Traverse.Create(__instance).Field("currentPlayerCurrency").GetValue<Currency>();

                    if (currentIsland != null)
                    {
                        int portIndex = currentIsland.GetPortIndex();
                        TradingSyncManager.Instance?.RequestMarketTrade(portIndex, currentSelectedGood, isBuying: true, currencyIndex: currency);
                        Debug.VerboseLogger.Log("ECONOMY", "REQUEST", $"Guest market buy: port={portIndex}, good={currentSelectedGood}, currency={currency}");
                    }
                    return false;
                }
                return true;
            }
        }

        /// <summary>
        /// Intercept guest selling at market and route to host.
        /// </summary>
        [HarmonyPatch(typeof(EconomyUI), "SellGood")]
        public static class SellGoodPatch
        {
            [HarmonyPrefix]
            public static bool Prefix(EconomyUI __instance)
            {
                if (Plugin.IsMultiplayer && !Plugin.IsHost)
                {
                    var currentIsland = Traverse.Create(__instance).Field("currentIsland").GetValue<IslandMarket>();
                    var currentSelectedGood = Traverse.Create(__instance).Field("currentSelectedGood").GetValue<int>();
                    // T3: forward the guest's selected payment currency (see BuyGoodPatch).
                    var currency = (int)Traverse.Create(__instance).Field("currentPlayerCurrency").GetValue<Currency>();

                    if (currentIsland != null)
                    {
                        int portIndex = currentIsland.GetPortIndex();
                        TradingSyncManager.Instance?.RequestMarketTrade(portIndex, currentSelectedGood, isBuying: false, currencyIndex: currency);
                        Debug.VerboseLogger.Log("ECONOMY", "REQUEST", $"Guest market sell: port={portIndex}, good={currentSelectedGood}, currency={currency}");
                    }
                    return false;
                }
                return true;
            }
        }

        /// <summary>
        /// Intercept guest currency exchange and route to host.
        /// </summary>
        [HarmonyPatch(typeof(CurrencyExchangeUI), nameof(CurrencyExchangeUI.ConfirmExchange))]
        public static class CurrencyExchangePatch
        {
            [HarmonyPrefix]
            public static bool Prefix(CurrencyExchangeUI __instance)
            {
                if (Plugin.IsMultiplayer && !Plugin.IsHost)
                {
                    var sellCurrency = Traverse.Create(__instance).Field("currentSellCurrency").GetValue<int>();
                    var buyCurrency = Traverse.Create(__instance).Field("currentBuyCurrency").GetValue<int>();
                    var sellAmount = Traverse.Create(__instance).Field("currentSellAmount").GetValue<int>();

                    var packet = new ExchangeRequestPacket
                    {
                        SellCurrency = sellCurrency,
                        BuyCurrency = buyCurrency,
                        Amount = sellAmount
                    };

                    Debug.VerboseLogger.Log("ECONOMY", "REQUEST", $"Guest exchange: sell={sellCurrency}, buy={buyCurrency}, amount={sellAmount}");

                    Plugin.NetworkManager.SendToAllReliable(PacketType.ExchangeRequest, w =>
                        PacketSerializer.WriteExchangeRequest(w, packet));

                    return false;
                }
                return true;
            }
        }

        /// <summary>
        /// Intercept guest buying from shopkeeper - optimistic local + send request.
        /// Note: Game's TryToBuyItem means player is SELLING to shopkeeper (confusing naming).
        /// We patch the private BuyItem method which is called when sale succeeds.
        /// </summary>
        [HarmonyPatch(typeof(Shopkeeper), "BuyItem")]
        public static class ShopkeeperBuyItemPatch
        {
            [HarmonyPostfix]
            public static void Postfix(Shopkeeper __instance, ShipItem item, int price)
            {
                // Only for multiplayer guest
                if (!Plugin.IsMultiplayer || Plugin.IsHost) return;
                if (item == null) return;

                // Get port info via reflection (parentRegion is private)
                var parentRegion = Traverse.Create(__instance).Field("parentRegion").GetValue<Region>();
                if (parentRegion == null) return;

                var port = parentRegion.GetComponentInParent<Port>();
                int portIndex = port != null ? port.portIndex : 0;

                // Get good index from SaveablePrefab
                var saveable = item.GetComponent<SaveablePrefab>();
                int goodIndex = saveable != null ? PrefabsDirectory.ItemToGoodIndex(saveable.prefabIndex) : -1;
                int prefabIndex = saveable != null ? saveable.prefabIndex : -1;

                // Send request to host (IsBuying=false because player is selling to shop)
                var packet = new ShopTradeRequestPacket
                {
                    PortIndex = portIndex,
                    ShopkeeperPosX = __instance.transform.position.x,
                    ShopkeeperPosY = __instance.transform.position.y,
                    ShopkeeperPosZ = __instance.transform.position.z,
                    GoodIndex = goodIndex,
                    Price = price,
                    IsBuying = false,
                    CurrencyIndex = -1,  // sell-to-shop has no selected currency here; -1 => host falls back to port region. MUST be set explicitly since the struct default 0 is a real wallet slot.
                    PrefabIndex = prefabIndex   // carry the raw prefab index. Host doesn't spawn on a sell, but set explicitly so the struct default 0 isn't mistaken for a valid prefab.
                };

                Debug.VerboseLogger.Log("TRADING", "SEND", $"ShopSell: port={portIndex}, good={goodIndex}, price={price}");

                Plugin.NetworkManager.SendToAllReliable(PacketType.ShopTradeRequest, w =>
                    PacketSerializer.WriteShopTradeRequest(w, packet));
            }
        }

        /// <summary>
        /// A dock-STALL buy that the guest can't afford LOCALLY would never reach the host.
        /// Vanilla Shopkeeper.TryToSellItem (decomp Shopkeeper.cs:80-92) gates the private SellItem behind a
        /// LOCAL-wallet check (localPrice &lt;= PlayerGold.currency[portRegion]); only SellItem is postfixed
        /// (ShopkeeperSellItemPatch) to route the buy to the host. So if the guest's local mirror of the shared
        /// wallet is low/0/in the wrong currency slot, vanilla shows "Not enough money", SellItem never runs, the
        /// postfix never fires, and NOTHING is routed to the host. Market buys are immune because BuyGoodPatch
        /// routes to the host BEFORE vanilla's wallet check. Make the stall path symmetric: for the MP GUEST,
        /// replicate the NON-gate parts of vanilla TryToSellItem (resolve item + localPrice + portRegion exactly
        /// as vanilla does) but SKIP the local affordability check, then invoke the private SellItem directly so
        /// the existing ShopkeeperSellItemPatch postfix still fires and sends the ShopTradeRequest to the host.
        /// The host's ExecuteShopTrade is the REAL affordability authority (it rejects + resyncs the shared wallet
        /// if it can't afford it) and spawns the item authoritatively. Host and solo are unaffected (return true).
        /// </summary>
        [HarmonyPatch(typeof(Shopkeeper), nameof(Shopkeeper.TryToSellItem))]
        public static class ShopkeeperTryToSellItemPatch
        {
            // Cached handle to the private Shopkeeper.SellItem(ShipItem, int, int). Invoking it via reflection
            // still triggers its Harmony patches (ShopkeeperSellItemPatch prefix+postfix), which is what routes
            // the buy to the host. Cached once; AccessTools resolves the private instance method.
            private static readonly System.Reflection.MethodInfo SellItemMethod =
                AccessTools.Method(typeof(Shopkeeper), "SellItem");

            [HarmonyPrefix]
            public static bool Prefix(Shopkeeper __instance, ShipItem item)
            {
                // Host / solo: run vanilla TryToSellItem unchanged (host is the shared-wallet authority).
                if (!Plugin.IsMultiplayer || Plugin.IsHost) return true;
                if (item == null) return true; // defensive: let vanilla handle the null path

                if (SellItemMethod == null)
                {
                    Debug.VerboseLogger.Log("TRADING", "WARN", "Shopkeeper.SellItem not found via AccessTools; falling back to vanilla stall buy");
                    return true; // couldn't reflect the private method: fall back to vanilla (local-gated) behavior
                }

                // Replicate the NON-gate parts of vanilla TryToSellItem (decomp Shopkeeper.cs:80-92): resolve
                // portRegion from the private parentRegion and localPrice via the public GetLocalPrice - exactly
                // as vanilla does - but SKIP the `localPrice <= PlayerGold.currency[portRegion]` affordability
                // check. The host is the affordability authority.
                var parentRegion = Traverse.Create(__instance).Field("parentRegion").GetValue<Region>();
                if (parentRegion == null) return true; // no region resolved: let vanilla run its own path
                int portRegion = (int)parentRegion.portRegion;
                int localPrice = __instance.GetLocalPrice(item);

                Debug.VerboseLogger.Log("TRADING", "REQUEST", $"Guest stall buy (wallet-independent): item={item.name}, price={localPrice}, region={portRegion}");

                // Invoke the private SellItem directly. Its Harmony postfix (ShopkeeperSellItemPatch) fires and
                // sends the ShopTradeRequest to the host (and suppresses the optimistic copy for the auth spawn).
                SellItemMethod.Invoke(__instance, new object[] { item, localPrice, portRegion });

                return false; // skip vanilla TryToSellItem (we already ran SellItem + routed to host)
            }
        }

        /// <summary>
        /// Intercept guest selling to shopkeeper - optimistic local + send request.
        /// Note: Game's TryToSellItem means player is BUYING from shopkeeper (confusing naming).
        /// We patch the private SellItem method which is called when purchase succeeds.
        /// </summary>
        [HarmonyPatch(typeof(Shopkeeper), "SellItem")]
        public static class ShopkeeperSellItemPatch
        {
            // Vanilla Shopkeeper.SellItem calls item.Sell() (decomp ShipItem.cs:514-517), which
            // AUTO-PICKS the display item into the buyer's hand (pointedAtBy.PickUpItem(this)). That fires our
            // GoPointer.PickUpItem postfix -> ItemSyncManager.OnLocalPickup -> SendItemPickupRequest BEFORE the
            // Postfix below can suppress the copy. On a co-located host that early request can lazy-correlate to
            // the host's OWN unsold display item and Sell() it, and then SpawnAuthoritativeStallItem also spawns
            // a copy -> DUPLICATE. We must stop that pickup request at the SOURCE: a Prefix sets
            // ItemSyncManager.IsApplyingRemoteState BEFORE vanilla runs, so OnLocalPickup short-circuits (it bails
            // on IsApplyingRemoteState, same as the drop/destroy patches). The Postfix restores it. We only do
            // this when the item has a valid prefab index (i.e. the host WILL spawn authoritatively); for a
            // no-SaveablePrefab item we leave vanilla untouched so the buyer doesn't lose a paid item.

            // True if this item will be handled by the host-authoritative spawn path (has a real prefab index).
            // prefabIndex 0 is the directory's null slot (PrefabsDirectory.GetItem(0) => null), so treat <=0 as invalid.
            private static bool WillSpawnAuthoritatively(ShipItem item)
            {
                if (item == null) return false;
                var saveable = item.GetComponent<SaveablePrefab>();
                return saveable != null && saveable.prefabIndex > 0;
            }

            [HarmonyPrefix]
            public static void Prefix(ShipItem item, out bool __state)
            {
                __state = false; // did WE raise the remote-state guard?
                if (!Plugin.IsMultiplayer || Plugin.IsHost) return;
                if (!WillSpawnAuthoritatively(item)) return; // leave vanilla intact when no valid prefab to spawn

                var sync = ItemSyncManager.Instance;
                if (sync == null) return;
                // Suppress the optimistic pickup request that vanilla item.Sell() would emit via its auto-pickup
                // (the GoPointer.PickUpItem postfix bails while IsApplyingRemoteState is true). Only flip it if it
                // wasn't already set, and record that WE did so the Postfix restores exactly once.
                if (!sync.IsApplyingRemoteState)
                {
                    sync.SetApplyingRemoteState(true);
                    __state = true;
                }
            }

            [HarmonyPostfix]
            public static void Postfix(Shopkeeper __instance, ShipItem item, int price, int currency, bool __state)
            {
                // Only for multiplayer guest
                if (!Plugin.IsMultiplayer || Plugin.IsHost)
                {
                    // (host/solo: nothing to restore - the Prefix never raised the guard)
                    return;
                }
                if (item == null)
                {
                    if (__state) ItemSyncManager.Instance?.SetApplyingRemoteState(false);
                    return;
                }

                try
                {
                    // Get port info via reflection (parentRegion is private)
                    var parentRegion = Traverse.Create(__instance).Field("parentRegion").GetValue<Region>();
                    if (parentRegion == null) return;

                    var port = parentRegion.GetComponentInParent<Port>();
                    int portIndex = port != null ? port.portIndex : 0;

                    // Get good index + RAW prefab index from SaveablePrefab. GoodIndex stays for the host's
                    // currency/economy logic; PrefabIndex is what the host actually spawns.
                    var saveable = item.GetComponent<SaveablePrefab>();
                    int goodIndex = saveable != null ? PrefabsDirectory.ItemToGoodIndex(saveable.prefabIndex) : -1;
                    int prefabIndex = saveable != null ? saveable.prefabIndex : -1;

                    // Send request to host (IsBuying=true because player is buying from shop)
                    var packet = new ShopTradeRequestPacket
                    {
                        PortIndex = portIndex,
                        ShopkeeperPosX = __instance.transform.position.x,
                        ShopkeeperPosY = __instance.transform.position.y,
                        ShopkeeperPosZ = __instance.transform.position.z,
                        GoodIndex = goodIndex,
                        Price = price,
                        IsBuying = true,
                        CurrencyIndex = currency,   // forward the wallet the guest paid with so the host charges the correct currency slot (port.region alone can resolve a wrong/empty wallet => reject)
                        PrefabIndex = prefabIndex   // raw prefab index the host spawns directly (goods AND non-good stall items; the good<->item round-trip breaks for the dead band)
                    };

                    Debug.VerboseLogger.Log("TRADING", "SEND", $"ShopBuy: port={portIndex}, good={goodIndex}, prefab={prefabIndex}, price={price}, currency={currency}");

                    Plugin.NetworkManager.SendToAllReliable(PacketType.ShopTradeRequest, w =>
                        PacketSerializer.WriteShopTradeRequest(w, packet));

                    // MINOR fix: only suppress the optimistic copy when the host WILL spawn an authoritative one.
                    // For a no-SaveablePrefab item the Prefix left vanilla untouched, the host returns without
                    // spawning, and we must NOT destroy the guest's copy (else the buyer loses a paid item).
                    if (__state)
                    {
                        // Vanilla Shopkeeper.SellItem already ran item.Sell() on this GUEST, which marked the
                        // shop's display item sold, registered it to save (assigning a LOCAL id the host never
                        // knew), and auto-picked it into the buyer's hand. The host now spawns the item
                        // AUTHORITATIVELY (TradingSyncManager.ExecuteShopTrade -> SpawnAuthoritativeStallItem)
                        // and broadcasts ItemSpawned, so keeping this optimistic copy would duplicate the item.
                        // DON'T destroy it immediately - with the local wallet gate bypassed
                        // (ShopkeeperTryToSellItemPatch), a host REJECT (insufficient shared funds) would
                        // silently vaporize the stall's display item. PARK it hidden instead; the host's
                        // ShopTradeResult then resolves it: success => destroy (authoritative ItemSpawned is
                        // inbound), reject => restore it to the stall (un-sell + re-add to itemsForSale +
                        // return to shop position). The guard is still raised from the Prefix, so the
                        // GoPointer.DropItem / ShipItem.DestroyItem patches emit no packets either way.
                        ParkOptimisticStallItem(item);
                    }
                    else
                    {
                        // FIFO pairing: this request still gets a host verdict, so park a null
                        // placeholder entry to keep verdicts aligned with their own buys (see queue below).
                        ParkOptimisticStallItem(null);
                    }
                }
                finally
                {
                    // Restore the remote-state guard LAST (after suppression), exactly if WE raised it. Nested
                    // toggles inside SuppressOptimisticStallItem restore to their own captured prior (== true here),
                    // so this is the single authoritative reset back to the real prior value.
                    if (__state) ItemSyncManager.Instance?.SetApplyingRemoteState(false);
                }
            }

            // PENDING STALL BUYS: the guest's optimistic display items, parked hidden until the host's
            // ShopTradeResult verdicts arrive. A FIFO queue (not a single slot): a single slot would let two
            // quick buys force-settle the first as success and then pair verdict #1 with item #2. Verdicts
            // arrive in request order on the reliable channel, so each verdict dequeues and resolves the
            // OLDEST entry. Non-parked requests (no valid prefab) enqueue a null-item placeholder to keep the
            // pairing aligned. Each entry carries its own realtime deadline; the timeout sweep settles (as
            // success, oldest-first) only entries whose own deadline passed (worst case: the purchase falls
            // back to unrouted local behavior - item destroyed).
            private sealed class PendingStallBuy
            {
                public ShipItem Item;
                public float Deadline;
            }

            private static readonly System.Collections.Generic.Queue<PendingStallBuy> _pendingStallBuys
                = new System.Collections.Generic.Queue<PendingStallBuy>();

            private static void ParkOptimisticStallItem(ShipItem item)
            {
                var entry = new PendingStallBuy { Item = item, Deadline = UnityEngine.Time.realtimeSinceStartup + 30f };

                if (item != null)
                {
                    var sync = ItemSyncManager.Instance;
                    bool prevApplying = sync?.IsApplyingRemoteState ?? false;
                    sync?.SetApplyingRemoteState(true);
                    try
                    {
                        // Clear BuyItemUI's reference so its OnTriggerExit doesn't poke a hidden/parked object.
                        if (BuyItemUI.instance != null && BuyItemUI.instance.recentlyBoughtItem == item)
                            BuyItemUI.instance.recentlyBoughtItem = null;

                        // If vanilla Sell auto-picked it into the hand, release it first so the GoPointer drops its
                        // ref (guard raised => no drop packet).
                        if (item.held != null)
                            item.held.DropItem();

                        // Park hidden instead of destroying (see call site). SetActive(false) also halts any
                        // SmoothlyReturnToShop coroutine vanilla might have started on the drop.
                        item.gameObject.SetActive(false);
                        Debug.VerboseLogger.Log("TRADING", "LOCAL", $"Parked optimistic stall-buy item {item.name} pending host ShopTradeResult ({_pendingStallBuys.Count} already pending)");
                    }
                    finally
                    {
                        if (!prevApplying) sync?.SetApplyingRemoteState(false);
                    }
                }

                _pendingStallBuys.Enqueue(entry);

                // Realtime fallback: if the verdict never arrives (packet loss / host mid-teardown), settle as
                // success after 30s so the hidden save-registered item can't leak into the next save.
                Plugin.Instance?.StartCoroutine(PendingStallBuyTimeout(entry, 30f));
            }

            private static System.Collections.IEnumerator PendingStallBuyTimeout(PendingStallBuy entry, float seconds)
            {
                yield return new UnityEngine.WaitForSecondsRealtime(seconds);
                // Settle expired entries oldest-first (FIFO order must hold even on timeout). Entries queued
                // after `entry` have later deadlines, so this never touches a not-yet-expired buy.
                while (_pendingStallBuys.Count > 0 && _pendingStallBuys.Peek().Deadline <= UnityEngine.Time.realtimeSinceStartup)
                {
                    var expired = _pendingStallBuys.Dequeue();
                    Debug.VerboseLogger.Log("TRADING", "WARN", "No ShopTradeResult within timeout; settling parked stall item as bought (destroy)");
                    SettleStallBuy(expired.Item, true);
                    if (expired == entry) break;
                }
            }

            /// <summary>
            /// Drain all parked stall buys on session teardown (lobby leave / manager reset). Settles each
            /// as success (destroy + unregister under the remote-state guard, same as the timeout path) so a
            /// stale entry can't mis-pair with the next session's first verdict, and a hidden parked item
            /// can't be persisted into a solo save after disconnect.
            /// </summary>
            public static void ResetPendingStallBuys()
            {
                while (_pendingStallBuys.Count > 0)
                    SettleStallBuy(_pendingStallBuys.Dequeue().Item, true);
            }

            /// <summary>
            /// Settle the OLDEST parked optimistic stall-buy item once the host's verdict is known (called
            /// from TradingSyncManager.OnShopTradeResultReceived; the per-entry timeout fallback settles via
            /// SettleStallBuy directly).
            /// success => destroy it (the host-authoritative ItemSpawned copy is the canonical item).
            /// reject  => restore it to the stall: un-sell, unregister from save, re-add to
            /// shopArea.itemsForSale and snap back to the recorded shop position/rotation (vanilla
            /// ShipItem.shopPos/shopRot, the same coords SmoothlyReturnToShop uses). The
            /// IsApplyingRemoteState guard suppresses any destroy packet. Safe no-op when nothing is pending.
            /// </summary>
            public static void ResolvePendingStallBuy(bool success)
            {
                // FIFO: verdicts arrive in request order (reliable channel), so this verdict belongs to the
                // OLDEST pending entry. Safe no-op when nothing is pending (e.g. already timed out).
                if (_pendingStallBuys.Count == 0) return;
                SettleStallBuy(_pendingStallBuys.Dequeue().Item, success);
            }

            private static void SettleStallBuy(ShipItem item, bool success)
            {
                if (item == null) return;

                var sync = ItemSyncManager.Instance;
                bool prevApplying = sync?.IsApplyingRemoteState ?? false;
                sync?.SetApplyingRemoteState(true);
                try
                {
                    if (success)
                    {
                        Debug.VerboseLogger.Log("TRADING", "LOCAL", $"Stall buy confirmed: destroying parked optimistic item {item.name} (authoritative copy inbound)");
                        // Use vanilla DestroyItem (NOT raw Object.Destroy): vanilla Sell() RegisterToSave'd this
                        // SaveablePrefab into SaveLoadManager.currentPrefabs, so a raw Destroy leaves a dangling
                        // entry that crashes the next SaveGame. DestroyItem() calls Unregister(); the guard makes
                        // the ShipItem.DestroyItem patch skip the ItemDestroyed broadcast.
                        item.DestroyItem();
                        return;
                    }

                    Debug.VerboseLogger.Log("TRADING", "LOCAL", $"Stall buy REJECTED by host: restoring {item.name} to the stall");
                    var t = Traverse.Create(item);

                    // Undo what vanilla Sell() did: un-mark sold, drop the save registration (an unsold stall
                    // display item is never save-registered in vanilla).
                    item.sold = false;
                    item.GetComponent<SaveablePrefab>()?.Unregister();

                    // Re-add to the shop's for-sale list and snap back to the recorded stall position/rotation
                    // (private ShipItem.shopArea/shopPos/shopRot - the exact coords vanilla's
                    // SmoothlyReturnToShop targets). Re-parent to the shop so it stays glued to the island
                    // through floating-origin shifts (Sell() had re-parented it to the FloatingOriginManager).
                    var shopArea = t.Field("shopArea").GetValue<ShopArea>();
                    item.gameObject.SetActive(true);
                    if (shopArea != null)
                    {
                        if (shopArea.itemsForSale != null && !shopArea.itemsForSale.Contains(item))
                            shopArea.itemsForSale.Add(item);
                        var shopPos = t.Field("shopPos").GetValue<UnityEngine.Vector3>();
                        var shopRot = t.Field("shopRot").GetValue<UnityEngine.Quaternion>();
                        item.transform.parent = shopArea.transform;
                        item.transform.position = shopArea.transform.TransformPoint(shopPos);
                        item.transform.rotation = shopRot;
                    }
                    // Refresh the look text back to the for-sale label (private in some builds - via Traverse).
                    try { t.Method("UpdateLookText").GetValue(); } catch { }
                    // NOTE: the optimistic wallet deduction + day-log entry vanilla SellItem made are corrected
                    // by the host's targeted ResyncCurrencyTo / TransactionDelta exclusion machinery; the
                    // day-log expense stays (same accepted skew as the shipyard reject path).
                    // ACCEPTED SKEW: the optimistic IslandMarket.PurchaseGood stock change and the
                    // day-log expense are NOT undone here on reject - the next economy full-sync reconverges
                    // the market stock. Do NOT attempt to reverse market stock locally.
                }
                finally
                {
                    if (!prevApplying) sync?.SetApplyingRemoteState(false);
                }
            }
        }

        /// <summary>
        /// Co-op tavern sleep. Taverns charge + sleep in ONE method and call FallAsleep directly (no physical
        /// bed), so the bed-based sleep handshake never starts and each player's click would charge the shared
        /// wallet separately. Take it over for co-op: charge ONCE for the crew, and route the sleep through the
        /// handshake (OnLocalEnterBed) so both actually sleep together. Solo is unaffected (vanilla runs).
        /// </summary>
        [HarmonyPatch(typeof(Tavern), nameof(Tavern.ClickSleepButton))]
        public static class TavernClickSleepPatch
        {
            [HarmonyPrefix]
            public static bool Prefix(Tavern __instance)
            {
                if (!Plugin.HasConnectedGuest) return true; // solo / no guest: vanilla charge + sleep

                int region = (int)__instance.region;
                int price = Traverse.Create(__instance).Field("currentPrice").GetValue<int>();

                // TAVERN (unified): do NOT charge at click. The host captures its OWN room's region+price and
                // charges it ONCE in SleepSyncManager.TransitionToSleeping, after both players are committed.
                // Both host and guest are in the tavern context here, so the guest sends no price - the host
                // charges its own pending room. This fixes the gold-leak / free-sleep / free-repeat findings.
                if (Plugin.IsHost)
                {
                    TradingSyncManager.SetPendingTavernCharge(region, price);
                    Debug.VerboseLogger.Log("TRADING", "PEND", $"Tavern pending charge set: region={region}, price={price}");
                }

                __instance.roomUI.SetActive(false);
                GameState.sleepingInTavern = true;
                // Taverns have no bed, so start the co-op sleep handshake here (the bed hook never fires).
                SleepSyncManager.Instance?.OnLocalEnterBed(isTavern: true, isMoored: false);
                return false; // handled
            }
        }

        /// <summary>
        /// Taverns have no bed, so leaving the trigger zone mid-handshake (WAITING) has no way to cancel via
        /// the bed-leave hook. Cancel a still-pending tavern WAITING handshake when the local player walks out
        /// of the tavern collider (OnLocalLeaveBed sends SleepCancelled + TransitionToAwake; works without a
        /// real bed). Don't touch an in-progress SLEEPING warp.
        /// </summary>
        [HarmonyPatch(typeof(Tavern), "OnTriggerExit")]
        public static class TavernLeaveCancelPatch
        {
            [HarmonyPostfix]
            public static void Postfix(UnityEngine.Collider other)
            {
                if (!Plugin.HasConnectedGuest || other == null || !other.CompareTag("Player")) return;
                var m = SleepSyncManager.Instance;
                if (m != null && m.CurrentState == SleepSyncManager.SleepState.Waiting)
                    m.OnLocalLeaveBed();
            }
        }

        // Note: Recovery cost deduction happens inline in the DoRecoverPlayer coroutine, which can't be
        // patched directly. Instead, the guest requests a fresh currency sync after recovery completes
        // via the EconomySyncRequest packet.

        #endregion

        #region Boat Purchase

        /// <summary>
        /// Vanilla PurchasableBoat.PurchaseBoat() is local, so a host buying a boat would only update the
        /// join SNAPSHOT - an already-connected guest never sees the purchase live (a later joiner does,
        /// via the snapshot). And a guest's own purchase would deduct the shared wallet locally and never
        /// reach the host.
        ///
        /// HOST: a Prefix is a no-op (vanilla runs: deduct currency[3], set extraSetting, log, gold sound);
        ///       the Postfix then broadcasts BoatOwnershipChanged to every peer, and the currency deduction
        ///       reaches them via the normal CurrencySync polling.
        /// GUEST: the Prefix suppresses the local purchase (don't touch the host-owned wallet) and sends a
        ///        BoatPurchaseRequest to the host, which validates the shared wallet + performs the
        ///        authoritative purchase. The guest's view corrects via the host's CurrencySync +
        ///        BoatOwnershipChanged. Solo / non-multiplayer is unaffected (Prefix returns true).
        /// </summary>
        [HarmonyPatch(typeof(PurchasableBoat), nameof(PurchasableBoat.PurchaseBoat))]
        public static class PurchaseBoatPatch
        {
            [HarmonyPrefix]
            public static bool Prefix(PurchasableBoat __instance)
            {
                // Solo or host: run vanilla purchase (host is authoritative; the Postfix broadcasts).
                if (!Plugin.IsMultiplayer || Plugin.IsHost) return true;

                // Guest: route the purchase through the host instead of charging the shared wallet locally.
                var saveable = __instance.GetComponent<SaveableObject>();
                string boatName = saveable != null ? saveable.gameObject.name : __instance.gameObject.name;

                var packet = new BoatPurchaseRequestPacket { BoatName = boatName };

                Debug.VerboseLogger.Log("ECONOMY", "REQUEST", $"Guest boat purchase: boat={boatName}");

                Plugin.NetworkManager.SendToAllReliable(PacketType.BoatPurchaseRequest, w =>
                    PacketSerializer.WriteBoatPurchaseRequest(w, packet));

                return false; // suppress local purchase; host performs it authoritatively
            }

            [HarmonyPostfix]
            public static void Postfix(PurchasableBoat __instance)
            {
                // Only the host broadcasts; guests never reach here (Prefix returned false).
                if (!Plugin.IsMultiplayer || !Plugin.IsHost) return;

                var saveable = __instance.GetComponent<SaveableObject>();
                string boatName = saveable != null ? saveable.gameObject.name : __instance.gameObject.name;

                EconomySyncManager.Instance?.SendBoatOwnershipChanged(boatName, true);
            }
        }

        /// <summary>
        /// Vanilla PurchasableBoat.PurchaseBoat logs the boat's PRICE via
        /// DayLogs.dayLogs[3].LogBoatPurchase(price) (expenses category 11), NOT the LogTransaction(int,
        /// TransactionCategory) overload that LogTransactionPatch hooks. So a HOST boat purchase deducts the
        /// shared wallet (synced via CurrencySync polling) but its day-log EXPENSE would never reach guests,
        /// leaving their day logs out of sync. Patch LogBoatPurchase host-only to broadcast a TransactionDelta for
        /// category 11 (expense), mirroring LogTransactionPatch. The guest applies it in
        /// OnTransactionDeltaReceived via dayLog.LogTransaction(-price, cat 11), which does expenses[11] -= price
        /// - identical to vanilla LogBoatPurchase.
        ///
        /// Guests never reach here: their PurchaseBoatPatch Prefix suppresses vanilla PurchaseBoat (so vanilla
        /// LogBoatPurchase never runs), and the host's authoritative purchase routes through here once.
        /// </summary>
        [HarmonyPatch(typeof(DayLog), nameof(DayLog.LogBoatPurchase))]
        public static class LogBoatPurchasePatch
        {
            [HarmonyPostfix]
            public static void Postfix(DayLog __instance, int price)
            {
                if (!Plugin.IsMultiplayer || !Plugin.IsHost) return;
                if (price == 0) return;

                // Resolve which currency this DayLog belongs to (vanilla uses dayLogs[3], but match defensively).
                int currencyIndex = -1;
                if (DayLogs.instance?.dayLogs != null)
                {
                    for (int i = 0; i < DayLogs.instance.dayLogs.Length; i++)
                    {
                        if (DayLogs.instance.dayLogs[i] == __instance) { currencyIndex = i; break; }
                    }
                }
                if (currencyIndex < 0) return;

                // Vanilla LogBoatPurchase does expenses[11] -= price => an expense of magnitude |price| in
                // category 11. Broadcast it as an expense so guests apply the identical change.
                var packet = new TransactionDeltaPacket
                {
                    CurrencyIndex = currencyIndex,
                    Amount = price >= 0 ? price : -price,   // always positive magnitude
                    Category = 11,                          // boat-purchase expense category (DayLog.LogBoatPurchase)
                    IsProfit = false                        // always an expense
                };

                Debug.VerboseLogger.Log("ECONOMY", "SEND", $"BoatPurchase TransactionDelta: currency={currencyIndex}, price={price}, category=11");

                Plugin.NetworkManager.SendToAllReliable(PacketType.TransactionDelta, w =>
                    PacketSerializer.WriteTransactionDelta(w, packet));
            }
        }

        #endregion

        #region Shipyard Order

        /// <summary>
        /// Vanilla Shipyard.ConfirmOrder() does a LOCAL PlayerGold.currency[region] -= currentOrderTotal.
        /// The other economy patches intercept market/shop/exchange/tavern/boat-purchase, and without this
        /// one a GUEST confirming an order would charge only its OWN mirror of the shared wallet (the host
        /// wallet never changes -> only the guest's money drops).
        ///
        /// We cannot suppress ConfirmOrder wholesale on the guest: the SAME method also runs the customization
        /// (sailInstaller.InstallSails / partsInstaller.ApplyCurrentOrder / clean / repair) that the guest must
        /// apply locally (ShipyardSyncManager then broadcasts the customization snapshot). So we let vanilla run
        /// in full and NEUTRALIZE only the money side afterward, then route an authoritative charge to the host.
        ///
        /// HOST: a no-op (vanilla runs unchanged; the host IS the shared wallet authority).
        /// GUEST: Prefix snapshots region + total + the pre-call wallet. Postfix detects whether vanilla
        ///   actually deducted by the WALLET DELTA (NOT the private shipyardFeeAlreadyCharged flag - vanilla
        ///   sets that true ONCE per shipyard visit and never resets it between orders, so after the first order
        ///   any later error-return still reports "charged"=true even though the wallet was untouched -> a
        ///   phantom charge of the SHARED wallet). We instead treat the order as applied IFF
        ///   (walletBefore - walletAfter) == Total: an error-return leaves the wallet unchanged (delta 0 != Total
        ///   => skip), while a real deduction (incl. Total==0 and negative-Total refunds) gives delta == Total.
        ///   When actually applied: restore the local wallet (undo the optimistic local deduction) and send a
        ///   ShipyardOrderRequest to the host. We do NOT undo the optimistic day-log here (vanilla
        ///   DayLog.LogTransaction routes by SIGN, so logging +Total would land in profits[] instead of
        ///   cancelling the -Total expense, and the host's TransactionDelta would then double-apply). Instead
        ///   the host EXCLUDES this requester from its authoritative TransactionDelta broadcast (the guest's own
        ///   vanilla expenses[boat] entry is already correct), exactly like the SHOP-trade path. The guest's
        ///   wallet corrects via the host's CurrencySync. Solo / non-multiplayer is unaffected.
        /// </summary>
        [HarmonyPatch(typeof(Shipyard), nameof(Shipyard.ConfirmOrder))]
        public static class ShipyardConfirmOrderPatch
        {
            // Carries the pre-call money snapshot from Prefix to Postfix.
            public struct OrderState
            {
                public bool IsGuest;       // did we intercept (MP guest)?
                public int Region;         // Shipyard.region (currency slot vanilla charges)
                public int Total;          // GetCurrentOrderTotal() captured BEFORE vanilla ran (it ResetOrder's after)
                public int WalletBefore;   // PlayerGold.currency[Region] before vanilla deducted
            }

            [HarmonyPrefix]
            public static void Prefix(Shipyard __instance, out OrderState __state)
            {
                __state = default;

                // Host / solo: vanilla is authoritative; nothing to intercept.
                if (!Plugin.IsMultiplayer || Plugin.IsHost) return;

                int region = __instance.region;
                int total = __instance.GetCurrentOrderTotal();
                int walletBefore = (PlayerGold.currency != null && region >= 0 && region < PlayerGold.currency.Length)
                    ? PlayerGold.currency[region]
                    : 0;

                __state = new OrderState
                {
                    IsGuest = true,
                    Region = region,
                    Total = total,
                    WalletBefore = walletBefore
                };
                // Let vanilla run (install + optimistic deduction); we neutralize money in the Postfix.
            }

            [HarmonyPostfix]
            public static void Postfix(Shipyard __instance, OrderState __state)
            {
                if (!__state.IsGuest) return; // host/solo: untouched

                int region = __state.Region;

                // Detect a REAL deduction by WALLET DELTA, not the sticky shipyardFeeAlreadyCharged flag (vanilla
                // sets it once per visit and never resets between orders -> after the first order, any later
                // error-return still reads true). An error-return leaves the wallet unchanged (delta 0); a real
                // ConfirmOrder did currency[region] -= Total (delta == Total). This correctly covers Total==0 and
                // negative-Total refunds, and skips every error-return (sail/obstruction/parts/not-enough-money).
                int walletAfter = (PlayerGold.currency != null && region >= 0 && region < PlayerGold.currency.Length)
                    ? PlayerGold.currency[region]
                    : __state.WalletBefore;
                int delta = __state.WalletBefore - walletAfter;
                if (delta != __state.Total)
                {
                    Debug.VerboseLogger.Log("ECONOMY", "INFO", $"Guest shipyard order did not apply (walletDelta={delta} != total={__state.Total}); no charge routed");
                    return;
                }

                // Undo the LOCAL deduction vanilla applied to the guest's mirror of the shared wallet. Restore
                // to exactly the pre-call balance; the host's authoritative CurrencySync will then set the real
                // post-charge value for everyone.
                if (PlayerGold.currency != null && region >= 0 && region < PlayerGold.currency.Length)
                    PlayerGold.currency[region] = __state.WalletBefore;

                // NOTE: do NOT undo the optimistic day-log here. Vanilla DayLog.LogTransaction routes by SIGN, so
                // logging +Total would add to profits[boat] instead of cancelling the -Total expense; the host's
                // authoritative TransactionDelta would then double-apply on this guest. The guest's own vanilla
                // expenses[boat] entry is already correct - the host EXCLUDES this requester from its
                // TransactionDelta broadcast (DeltaExcludeRequester in ExecuteShipyardOrder), mirroring the
                // SHOP-trade path. Other peers receive the delta and stay in sync.

                var saveable = __instance.GetCurrentBoat()?.GetComponent<SaveableObject>();
                string boatName = saveable != null ? saveable.gameObject.name
                    : (__instance.GetCurrentBoat() != null ? __instance.GetCurrentBoat().name : "");

                var packet = new ShipyardOrderRequestPacket
                {
                    BoatName = boatName,
                    Region = region,
                    Total = __state.Total
                };

                Debug.VerboseLogger.Log("ECONOMY", "REQUEST", $"Guest shipyard order: boat={boatName}, region={region}, total={__state.Total}");

                Plugin.NetworkManager.SendToAllReliable(PacketType.ShipyardOrderRequest, w =>
                    PacketSerializer.WriteShipyardOrderRequest(w, packet));
            }
        }

        #endregion
    }
}
