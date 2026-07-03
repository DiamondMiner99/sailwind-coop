using HarmonyLib;
using SailwindCoop.Debug;
using SailwindCoop.Networking.Packets;
using SailwindCoop.Sync;
using UnityEngine;

namespace SailwindCoop.Patches
{
    /// <summary>
    /// Harmony patches for item synchronization.
    /// </summary>
    [HarmonyPatch]
    public static class ItemPatches
    {
        /// <summary>
        /// Patch GoPointer.PickUpItem to sync pickup events.
        /// </summary>
        [HarmonyPatch(typeof(GoPointer), "PickUpItem")]
        [HarmonyPostfix]
        public static void OnPickUpItem(GoPointer __instance, PickupableItem item)
        {
            if (!Plugin.IsMultiplayer) return;
            if (ItemSyncManager.Instance?.IsApplyingRemoteState == true) return;

            var shipItem = item as ShipItem;
            if (shipItem != null)
            {
                var prefab = shipItem.GetComponent<SaveablePrefab>();
                VerboseLogger.Log("TRADING", "LOCAL", $"PickUpItem called, item={shipItem.name}, instanceId={prefab?.instanceId}");

                // LAZY ID CORRELATION: send ItemPickedUp - it establishes shared identity. Do not skip
                // SOLD shop items here; the pickup packet is what drives ID correlation for them.
                // (UNSOLD shop items are a vanilla-local INSPECTION and are gated inside OnLocalPickup -
                // broadcasting them made receivers Sell() the item for free.)
                ItemSyncManager.Instance?.OnLocalPickup(shipItem, -1);
            }
        }

        /// <summary>
        /// Patch GPButtonInventorySlot.InsertItem to sync when item enters player inventory.
        /// Without this, items appear "dropped" to other player when put in inventory.
        /// </summary>
        [HarmonyPatch(typeof(GPButtonInventorySlot), "InsertItem")]
        [HarmonyPostfix]
        public static void OnInventoryInsertItem(GPButtonInventorySlot __instance, ShipItem item)
        {
            if (!Plugin.IsMultiplayer) return;
            if (ItemSyncManager.Instance?.IsApplyingRemoteState == true) return;

            var prefab = item?.GetComponent<SaveablePrefab>();
            VerboseLogger.Log("ITEM", "LOCAL", $"InsertItem called, item={item?.name}, instanceId={prefab?.instanceId}, slot={__instance.slotIndex}");

            // Send pickup with inventory slot index (0-4) instead of -1 (hand)
            ItemSyncManager.Instance?.OnLocalPickup(item, __instance.slotIndex);
        }

        /// <summary>
        /// Patch GoPointer.DropItem to sync drop events.
        /// </summary>
        [HarmonyPatch(typeof(GoPointer), "DropItem")]
        [HarmonyPrefix]
        public static void OnDropItemPrefix(GoPointer __instance)
        {
            if (!Plugin.IsMultiplayer) return;
            if (ItemSyncManager.Instance?.IsApplyingRemoteState == true) return;

            // Get the item before it's dropped
            var item = __instance.GetHeldItem();
            var shipItem = item as ShipItem;
            if (shipItem != null)
            {
                ItemSyncManager.Instance?.OnLocalDrop(shipItem);
            }
        }

        /// <summary>
        /// Patch ShipItem.DestroyItem to sync destruction.
        /// No blocking needed - both players are on same boat, items are never "out of range".
        /// </summary>
        [HarmonyPatch(typeof(ShipItem), "DestroyItem")]
        [HarmonyPrefix]
        public static bool OnDestroyItem(ShipItem __instance)
        {
            if (!Plugin.IsMultiplayer) return true;
            if (ItemSyncManager.Instance?.IsApplyingRemoteState == true) return true;

            // Vanilla ItemRigidbody.FixedUpdate auto-destroys a SOLD, off-boat item once
            // it drifts >600m from THIS client's camera (a purely LOCAL level-of-detail cull, decomp
            // ItemRigidbody.cs:342-352: sold && currentWalkCol==null && outOfRange && layer!=26 && !recovering).
            // In co-op that local destroy goes through OnLocalItemDestroyed -> broadcast ItemDestroyed, which
            // deletes the SHARED crate for EVERYONE - so a mission crate set down on a dock vanishes for the
            // whole crew the moment one player sails >600m away (exactly the "a crate I placed disappeared for
            // the host" report). Range-culling must NOT propagate: suppress it locally and never broadcast, so
            // the crate persists (the host stays the source of truth for world crates). Matched to vanilla's
            // exact cull signature + a generous distance gate (no player-initiated destroy - eat/use/unseal/
            // deliver - ever happens hundreds of metres from the camera, so this can't swallow a real destroy).
            if (__instance != null && __instance.sold && __instance.currentWalkCol == null
                && !GameState.recovering && __instance.gameObject.layer != 26
                && !MissionPatches.AbandoningMission   // an abandon's cargo-good destroys are intentional, not a LOD cull
                // STOVE-FUEL exemption (2026-07-02 "-17300/3" stove counter): the comment below claims no
                // real destroy happens far from the camera, but fuel BURNOUT is a SIM-initiated destroy -
                // StoveFuel.Update runs `UnregisterBurntFuel(); DestroyItem();` EVERY FRAME until the object
                // dies. Suppressing it leaves an immortal burnt fuel whose per-frame UnregisterBurntFuel
                // decrements the stove's fuel count without floor (~60/s into deep negatives). A burnt
                // fuel's destroy is legitimate at ANY distance - never eat it.
                && !(__instance.GetComponent<StoveFuel>()?.inserted == true)
                && Camera.main != null
                && Vector3.Distance(Camera.main.transform.position, __instance.transform.position) > 300f)
            {
                VerboseLogger.Log("ITEM", "LOCAL",
                    $"Suppressed out-of-range auto-destroy of {__instance.name} (co-op: a local range-cull must not delete the shared crate for everyone)");
                return false; // skip vanilla DestroyItem -> item persists locally, no ItemDestroyed broadcast
            }

            // During a RECOVERY, vanilla's sink-teardown (BoatLocalItems.CacheItemsOnSinking
            // -> SetItemsLoaded(false)) destroys every boat-parented furniture item and then RE-SPAWNS them locally
            // on each client. Broadcasting that destroy would wipe the furniture on guests.
            // Suppress only the BROADCAST for boat-parented
            // items mid-recovery; still destroy locally so vanilla's own respawn runs, and the authoritative
            // recovery snapshot (ClearBoatItems+SpawnItems) reconciles aboard guests. World items are untouched.
            if (Plugin.IsMultiplayer && GameState.recovering
                && __instance != null && __instance.GetComponentInParent<BoatRefs>() != null)
            {
                VerboseLogger.Log("ITEM", "LOCAL",
                    $"Suppressed recovery-teardown ItemDestroyed broadcast for boat item {__instance.name} (vanilla respawns it locally; snapshot reconciles)");
                return true; // destroy locally (vanilla respawns), do NOT broadcast
            }

            var prefab = __instance.GetComponent<SaveablePrefab>();
            if (prefab != null)
            {
                ItemSyncManager.Instance?.OnLocalItemDestroyed(prefab.instanceId);
            }
            return true; // Let original run
        }

        /// <summary>
        /// Patch CrateInventory.InsertItem to sync crate insertions.
        /// </summary>
        [HarmonyPatch(typeof(CrateInventory), "InsertItem")]
        [HarmonyPostfix]
        public static void OnCrateInsertItem(CrateInventory __instance, ShipItem item)
        {
            if (!Plugin.IsMultiplayer) return;
            if (ItemSyncManager.Instance?.IsApplyingRemoteState == true) return;

            // During a crate UNSEAL, the bulk CrateUnsealed packet is the single source of truth;
            // suppress the redundant per-item insert broadcast (it would flood guests with "item or crate not
            // found" because inserts arrive before the bulk spawn, and be double-emitted). Scoped to the crate unsealing.
            var crateId = __instance.GetComponent<SaveablePrefab>()?.instanceId ?? 0;
            if (crateId != 0 && ItemSyncManager.Instance?.IsCrateUnsealing(crateId) == true) return;

            ItemSyncManager.Instance?.OnLocalItemInsertedInCrate(item, __instance);
        }

        /// <summary>
        /// Patch CrateInventory.WithdrawItem to sync crate withdrawals.
        /// </summary>
        [HarmonyPatch(typeof(CrateInventory), "WithdrawItem")]
        [HarmonyPostfix]
        public static void OnCrateWithdrawItem(CrateInventory __instance, ShipItem item)
        {
            if (!Plugin.IsMultiplayer) return;
            if (ItemSyncManager.Instance?.IsApplyingRemoteState == true) return;

            ItemSyncManager.Instance?.OnLocalItemRemovedFromCrate(item, __instance);
        }

        /// <summary>
        /// Verbose trading trace: logs when a shop item is clicked (OnAltActivate).
        /// </summary>
        [HarmonyPatch(typeof(ShipItem), "OnAltActivate")]
        public static class ShipItemOnAltActivateDiagnosticPatch
        {
            [HarmonyPrefix]
            public static void Prefix(ShipItem __instance)
            {
                if (!Plugin.IsMultiplayer) return;
                var prefab = __instance.GetComponent<SaveablePrefab>();
                var shopArea = Traverse.Create(__instance).Field("shopArea").GetValue<ShopArea>();
                VerboseLogger.Log("TRADING", "DIAG", $"OnAltActivate, item={__instance.name}, id={prefab?.instanceId}, sold={__instance.sold}, hasShopArea={(shopArea != null)}");
            }
        }

        /// <summary>
        /// Verbose trading trace: logs the full purchase flow at TryToSellItem (player BUYING from shop).
        /// </summary>
        [HarmonyPatch(typeof(Shopkeeper), nameof(Shopkeeper.TryToSellItem))]
        public static class TryToSellItemDiagnosticPatch
        {
            [HarmonyPrefix]
            public static void Prefix(ShipItem item)
            {
                if (!Plugin.IsMultiplayer) return;
                var prefab = item?.GetComponent<SaveablePrefab>();
                VerboseLogger.Log("TRADING", "DIAG", $"TryToSellItem ENTER, item={item?.name}, id={prefab?.instanceId}, sold={item?.sold}");
            }

            [HarmonyPostfix]
            public static void Postfix(ShipItem item)
            {
                if (!Plugin.IsMultiplayer) return;
                var prefab = item?.GetComponent<SaveablePrefab>();
                VerboseLogger.Log("TRADING", "DIAG", $"TryToSellItem EXIT, item={item?.name}, id={prefab?.instanceId}, sold={item?.sold}");
            }
        }

        /// <summary>
        /// Patch ShipItem.Sell to sync when item is bought (becomes owned by player).
        /// Captures the original position in Prefix for shop item removal sync, and
        /// marks the item as purchased in Prefix to prevent a pickup race condition.
        /// </summary>
        [HarmonyPatch(typeof(ShipItem), "Sell")]
        public static class ShipItemSellPatch
        {
            // Capture data from Prefix to pass to Postfix
            [System.ThreadStatic]
            private static Vector3 _originalPosition;
            [System.ThreadStatic]
            private static int _prefabIndex;
            // Whether the buyer ALREADY held the item when Sell() started. Discriminates the two purchase
            // flows: point-at-buy (held==null at ENTER; Sell's own pointedAtBy.PickUpItem fires the pickup
            // broadcast) vs inspect-then-buy-in-hand (held!=null at ENTER; NO pickup ever fires after the
            // purchase, so Postfix must retro-send one). Checking held in Postfix alone can't tell them
            // apart - it is non-null in BOTH by then, and retro-sending on the point-at-buy flow would
            // double-broadcast the pickup.
            [System.ThreadStatic]
            private static bool _heldAtSellEnter;

            [HarmonyPrefix]
            public static void Prefix(ShipItem __instance)
            {
                if (!Plugin.IsMultiplayer) return;
                var prefab = __instance.GetComponent<SaveablePrefab>();

                // Capture original position BEFORE item moves to player's hands
                _originalPosition = __instance.transform.position;
                _prefabIndex = prefab?.prefabIndex ?? 0;
                _heldAtSellEnter = __instance.held != null;

                // Mark as just purchased EARLY to prevent pickup race
                // This ensures OnPickUpItem skips sync even if it runs before Postfix
                ItemSyncManager.Instance?.MarkItemAsJustPurchased(prefab?.instanceId ?? 0);

                var pointedAtBy = Traverse.Create(__instance).Field("pointedAtBy").GetValue<GoPointer>();
                VerboseLogger.Log("TRADING", "DIAG", $"ShipItem.Sell ENTER, item={__instance.name}, id={prefab?.instanceId}, pointedAtBy={(pointedAtBy != null ? "valid" : "NULL")}, pos={_originalPosition}");
            }

            [HarmonyPostfix]
            public static void Postfix(ShipItem __instance)
            {
                if (!Plugin.IsMultiplayer) return;
                if (ItemSyncManager.Instance?.IsApplyingRemoteState == true) return;

                var prefab = __instance.GetComponent<SaveablePrefab>();
                var held = __instance.held;
                VerboseLogger.Log("TRADING", "DIAG", $"ShipItem.Sell EXIT, item={__instance.name}, id={prefab?.instanceId}, sold={__instance.sold}, held={(held != null ? "picked up" : "NOT picked up")}, parent={__instance.transform.parent?.name}, pos={__instance.transform.position}");

                // LAZY ID CORRELATION: Don't spawn shop items - both players already have them
                // ID sync happens via ItemPickedUp packet with position correlation
                // Only spawn truly new items (mission rewards, etc. - not from vendor stalls)
                if (_prefabIndex <= 0)
                {
                    // Not a shop item - spawn for other player
                    ItemSyncManager.Instance?.OnLocalItemSpawned(__instance);
                }
                else
                {
                    VerboseLogger.Log("TRADING", "DIAG", $"Skipping ItemSpawned for shop item - using lazy ID correlation");
                }

                // ShopItemBought is now disabled (receiver is a deliberate no-op; the ItemPickedUp-driven
                // Sell() in OnRemoteItemPickedUp is the purchase propagation)
                // Keeping the send for backwards compatibility but receiver ignores it
                if (_prefabIndex > 0)
                {
                    ItemSyncManager.Instance?.OnLocalShopItemBought(_prefabIndex, _originalPosition);
                }

                // INSPECT-THEN-BUY-IN-HAND retro-send (2026-07-02 free-item fix, step 4): with unsold-item
                // inspection pickups now suppressed (ItemSyncManager.OnLocalPickup gate), the ONE purchase
                // flow left with no pickup broadcast is buying an item you are ALREADY holding - vanilla
                // Sell() skips pointedAtBy.PickUpItem when held (ShipItem.cs:514-517), so no pickup event
                // fires after the purchase and the other machines would never learn the sale. Retro-send
                // the pickup NOW, carrying the Prefix-captured TABLE position instead of the in-hand pose:
                // receivers' prefab+position correlation (maxDist=3) must hit their copy, which still sits
                // on the shop table at dist~0 - the hand pose would correlate to nothing (or the wrong
                // item). Receivers then Sell() their copy via the normal ItemPickedUp purchase propagation.
                // Effectively host-buy-only: a guest's purchases route via ShopTradeRequest, whose Sell runs
                // under IsApplyingRemoteState (already excluded above).
                if (_heldAtSellEnter && __instance.held != null)
                {
                    ItemSyncManager.Instance?.OnLocalPickup(__instance, -1, _originalPosition);
                }
            }
        }

        // NOTE: ItemRigidbody.FixedUpdate patch removed - both players on same boat,
        // items are never 600m out of range. No need to disable cleanup every frame.

        /// <summary>
        /// Patch HangableItem.ConnectJoint to sync when item is hung.
        /// </summary>
        [HarmonyPatch(typeof(HangableItem), "ConnectJoint")]
        [HarmonyPostfix]
        public static void OnConnectJoint(HangableItem __instance, Collider hook)
        {
            if (!Plugin.IsMultiplayer) return;
            if (ItemSyncManager.Instance?.IsApplyingRemoteState == true) return;

            var item = __instance.GetComponent<ShipItem>();
            var hookItem = hook?.GetComponent<ShipItem>();

            if (item != null && hookItem != null)
            {
                ItemSyncManager.Instance?.OnLocalItemHung(item, hookItem);
            }
        }

        /// <summary>
        /// Patch HangableItem.DisconnectJoint to sync when item is unhung.
        /// Skip if item is held - pickup handles that, and game calls DisconnectJoint defensively.
        /// </summary>
        [HarmonyPatch(typeof(HangableItem), "DisconnectJoint")]
        [HarmonyPostfix]
        public static void OnDisconnectJoint(HangableItem __instance)
        {
            if (!Plugin.IsMultiplayer) return;
            if (ItemSyncManager.Instance?.IsApplyingRemoteState == true) return;

            var item = __instance.GetComponent<ShipItem>();
            if (item == null) return;

            // Skip if item is held locally
            if (item.held != null) return;

            // Skip if item is held by any player (including remote) - prevents echo
            var prefab = item.GetComponent<SaveablePrefab>();
            if (prefab != null && ItemSyncManager.Instance?.IsItemHeld(prefab.instanceId) == true) return;

            ItemSyncManager.Instance?.OnLocalItemUnhung(item);
        }

        /// <summary>
        /// Patch CrateSealUI.Activate to intercept unseal requests.
        /// When player clicks "unseal" on sealed crate, route through sync system.
        /// </summary>
        [HarmonyPatch(typeof(CrateSealUI), "Activate")]
        [HarmonyPrefix]
        public static bool OnCrateUnseal(CrateSealUI __instance)
        {
            if (!Plugin.IsMultiplayer) return true; // Let original run

            var crate = __instance.currentCrate;
            if (crate != null)
            {
                // Route through sync - host unseals, broadcasts result
                ItemSyncManager.Instance?.OnLocalCrateUnsealRequest(crate);
                __instance.HideUI(true);
                return false; // Skip original
            }

            return true;
        }

        /// <summary>
        /// Patch CrateInventory.OpenCrate to suppress UI on host during remote unseal.
        /// When guest unseals, host calls UnsealCrate() which internally opens UI after 2 frames.
        /// We must block this UI on host - only guest should see it.
        /// </summary>
        [HarmonyPatch(typeof(CrateInventory), "OpenCrate")]
        [HarmonyPrefix]
        public static bool OnCrateOpen(CrateInventory __instance)
        {
            if (!Plugin.IsMultiplayer) return true;
            if (!Plugin.IsHost) return true; // Only affects host

            // Check if this specific crate is being remotely unsealed
            var prefab = __instance.GetComponent<SaveablePrefab>();
            if (prefab != null && ItemSyncManager.Instance?.IsRemoteUnsealing(prefab.instanceId) == true)
            {
                return false; // Block UI for this crate on host
            }

            return true;
        }

        #region Pipe Sync

        /// <summary>
        /// Patch ShipItemPipe.LoadTobacco to sync pipe filling.
        /// Called when player clicks pipe with tobacco to fill it.
        /// </summary>
        [HarmonyPatch(typeof(ShipItemPipe), "LoadTobacco")]
        [HarmonyPostfix]
        public static void OnLoadTobacco(ShipItemPipe __instance, ShipItemTobacco tobacco)
        {
            if (!Plugin.IsMultiplayer) return;
            if (ItemSyncManager.Instance?.IsApplyingRemoteState == true) return;

            var prefab = __instance.GetComponent<SaveablePrefab>();
            if (prefab == null || prefab.instanceId == 0) return;

            // tobacco.tobaccoType is already stored in pipe.amount by LoadTobacco
            var packet = new PipeFilledPacket
            {
                PipeInstanceId = prefab.instanceId,
                TobaccoType = tobacco.tobaccoType
            };

            VerboseLogger.Log("ITEM", "SEND", $"PipeFilled, pipeId={prefab.instanceId}, tobaccoType={tobacco.tobaccoType}");

            Plugin.NetworkManager.SendToAllReliable(PacketType.PipeFilled, w =>
            {
                PacketSerializer.WritePipeFilled(w, packet);
            });
        }

        #endregion

        #region Nail Sync

        /// <summary>
        /// Patch ShipItemHammer.NailItem to sync nailing. NailItem has early-return failure paths
        /// (not on floor, still moving), so only send when the flag actually flipped to true.
        /// </summary>
        [HarmonyPatch(typeof(ShipItemHammer), "NailItem")]
        [HarmonyPostfix]
        public static void OnNailItem(ShipItem item)
        {
            if (!Plugin.IsMultiplayer) return;
            if (ItemSyncManager.Instance?.IsApplyingRemoteState == true) return;
            if (item == null || !item.nailed) return;

            var prefab = item.GetComponent<SaveablePrefab>();
            if (prefab == null || prefab.instanceId == 0) return;

            SendNailState(prefab.instanceId, true);
        }

        /// <summary>
        /// Patch ShipItemHammer.OnAltActivate to sync un-nailing. The instant un-nail path flips
        /// pointedAtItem.nailed true->false inside the original, so capture the pointed-at item
        /// in a prefix and compare after.
        /// </summary>
        [HarmonyPatch(typeof(ShipItemHammer), "OnAltActivate")]
        [HarmonyPrefix]
        public static void OnHammerAltActivatePrefix(ShipItemHammer __instance, out ShipItem __state)
        {
            __state = null;
            if (!Plugin.IsMultiplayer) return;
            if (__instance.held == null) return;

            var pointedAt = __instance.held.GetPointedAtItem();
            if (pointedAt != null && pointedAt.nailed)
                __state = pointedAt;
        }

        [HarmonyPatch(typeof(ShipItemHammer), "OnAltActivate")]
        [HarmonyPostfix]
        public static void OnHammerAltActivatePostfix(ShipItem __state)
        {
            if (!Plugin.IsMultiplayer) return;
            if (ItemSyncManager.Instance?.IsApplyingRemoteState == true) return;
            if (__state == null || __state.nailed) return; // was nailed before, still nailed => no un-nail happened

            var prefab = __state.GetComponent<SaveablePrefab>();
            if (prefab == null || prefab.instanceId == 0) return;

            SendNailState(prefab.instanceId, false);
        }

        private static void SendNailState(int instanceId, bool nailed)
        {
            var packet = new NailStatePacket
            {
                ItemInstanceId = instanceId,
                Nailed = nailed
            };

            VerboseLogger.Log("ITEM", "SEND", $"NailState, itemId={instanceId}, nailed={nailed}");

            Plugin.NetworkManager.SendToAllReliable(PacketType.NailState, w =>
            {
                PacketSerializer.WriteNailState(w, packet);
            });
        }

        #endregion

        #region Light Sync

        /// <summary>
        /// Patch ShipItemLight.SetLight to sync lantern on/off state.
        /// Called when player clicks lantern to toggle light.
        /// </summary>
        [HarmonyPatch(typeof(ShipItemLight), "SetLight")]
        [HarmonyPostfix]
        public static void OnSetLight(ShipItemLight __instance, bool state)
        {
            if (!Plugin.IsMultiplayer) return;
            if (ItemSyncManager.Instance?.IsApplyingRemoteState == true) return;

            var prefab = __instance.GetComponent<SaveablePrefab>();
            if (prefab == null || prefab.instanceId == 0) return;

            var packet = new LightStatePacket
            {
                ItemInstanceId = prefab.instanceId,
                IsOn = state
            };

            VerboseLogger.Log("ITEM", "SEND", $"LightState, id={prefab.instanceId}, on={state}");

            Plugin.NetworkManager.SendToAllReliable(PacketType.LightState, w =>
            {
                PacketSerializer.WriteLightState(w, packet);
            });
        }

        #endregion
    }
}
