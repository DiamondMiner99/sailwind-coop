using HarmonyLib;
using SailwindCoop.Debug;
using SailwindCoop.Networking.Packets;
using SailwindCoop.Sync;
using UnityEngine;

namespace SailwindCoop.Patches
{
    /// <summary>
    /// Patches to disable cooking simulation on guest (host-authoritative).
    /// Also intercepts guest interactions to route through host.
    /// </summary>
    [HarmonyPatch]
    public static class CookingPatches
    {
        #region Disable Guest Simulation

        /// <summary>
        /// Skip CookableFood.Cook() on guest - host sends cooked state.
        /// </summary>
        [HarmonyPatch(typeof(CookableFood), nameof(CookableFood.Cook))]
        [HarmonyPrefix]
        public static bool CookableFood_Cook_Prefix()
        {
            if (!Plugin.IsMultiplayer) return true;
            if (Plugin.IsHost) return true;

            // Guest: skip cooking simulation
            return false;
        }

        // CookableFood.Cook is VIRTUAL and CookableFoodSoup/CookableFoodKettle OVERRIDE it.
        // A Harmony patch on the base MethodInfo does NOT intercept a subclass override (callvirt dispatches to
        // the override's own MethodInfo), so the base prefix above only covered plain food - a guest kept locally
        // simulating soup/kettle cooking between the 2Hz CookingSyncManager overwrites (cosmetic inter-tick drift).
        // Patch the overrides with the same guest-skip body. Solo-safe (returns true when !IsMultiplayer).
        [HarmonyPatch(typeof(CookableFoodSoup), nameof(CookableFoodSoup.Cook))]
        [HarmonyPrefix]
        public static bool CookableFoodSoup_Cook_Prefix()
        {
            if (!Plugin.IsMultiplayer) return true;
            if (Plugin.IsHost) return true;
            return false; // Guest: skip soup cooking simulation (host is authoritative)
        }

        [HarmonyPatch(typeof(CookableFoodKettle), nameof(CookableFoodKettle.Cook))]
        [HarmonyPrefix]
        public static bool CookableFoodKettle_Cook_Prefix()
        {
            if (!Plugin.IsMultiplayer) return true;
            if (Plugin.IsHost) return true;
            return false; // Guest: skip kettle cooking simulation (host is authoritative)
        }

        // FoodState.Update patch REMOVED - was costing 5.47ms (94 calls/frame)
        // Guest runs locally, CookingSyncManager overwrites at 2Hz - drift is negligible

        /// <summary>
        /// Skip ShipItemSoup.UpdateSpoiled() (called from ExtraLateUpdate) on guest.
        /// We patch ExtraLateUpdate to be safe.
        /// </summary>
        [HarmonyPatch(typeof(ShipItemSoup), "ExtraLateUpdate")]
        [HarmonyPrefix]
        public static bool ShipItemSoup_ExtraLateUpdate_Prefix(ShipItemSoup __instance)
        {
            if (!Plugin.IsMultiplayer) return true;
            if (Plugin.IsHost) return true;

            // Guest: skip soup spoilage update, but still allow drinking animation
            // Only skip the UpdateSpoiled call, let drinking work
            // Actually this is tricky - ExtraLateUpdate does both
            // For now, let it run but the sync will overwrite values
            return true;
        }

        /// <summary>
        /// Skip kettle BrewTea (called from ExtraLateUpdate) on guest.
        /// </summary>
        [HarmonyPatch(typeof(ShipItemKettle), "ExtraLateUpdate")]
        [HarmonyPrefix]
        public static bool ShipItemKettle_ExtraLateUpdate_Prefix()
        {
            if (!Plugin.IsMultiplayer) return true;
            if (Plugin.IsHost) return true;

            // Guest: skip brewing, but let animation work
            // Same issue as soup - let it run, sync overwrites
            return true;
        }

        #endregion

        #region Guest Interactions - Stove Food

        /// <summary>
        /// Intercept CookableFood.InsertIntoCookTrigger on guest - send request to host.
        /// </summary>
        [HarmonyPatch(typeof(CookableFood), nameof(CookableFood.InsertIntoCookTrigger))]
        [HarmonyPrefix]
        public static bool CookableFood_InsertIntoCookTrigger_Prefix(CookableFood __instance, StoveCookTrigger trigger)
        {
            if (!Plugin.IsMultiplayer) return true;
            if (Plugin.IsHost) return true;
            if (CookingSyncManager.Instance?.IsApplyingRemoteState == true) return true;

            // Guest: send request to host
            var foodPrefab = __instance.GetComponent<SaveablePrefab>();
            var stove = trigger.GetComponentInParent<ShipItemStove>();
            var stovePrefab = stove?.GetComponent<SaveablePrefab>();

            if (foodPrefab == null || stovePrefab == null) return true;

            var packet = new FoodPlaceOnStoveRequestPacket
            {
                FoodInstanceId = foodPrefab.instanceId,
                FoodPrefabIndex = foodPrefab.prefabIndex,
                StoveInstanceId = stovePrefab.instanceId,
                StovePrefabIndex = stovePrefab.prefabIndex
            };

            VerboseLogger.CookingRequest($"FoodPlaceOnStove, food={packet.FoodInstanceId}, foodPrefab={packet.FoodPrefabIndex}, stove={packet.StoveInstanceId}, stovePrefab={packet.StovePrefabIndex}");

            Plugin.NetworkManager.SendToAllReliable(PacketType.FoodPlaceOnStoveRequest, w =>
                PacketSerializer.WriteFoodPlaceOnStoveRequest(w, packet));

            return false; // Don't execute locally, wait for host
        }

        /// <summary>
        /// Intercept CookableFood.TakeOutOfCooker on guest - send request to host.
        /// </summary>
        [HarmonyPatch(typeof(CookableFood), nameof(CookableFood.TakeOutOfCooker))]
        [HarmonyPrefix]
        public static bool CookableFood_TakeOutOfCooker_Prefix(CookableFood __instance)
        {
            if (!Plugin.IsMultiplayer) return true;
            if (Plugin.IsHost) return true;
            if (CookingSyncManager.Instance?.IsApplyingRemoteState == true) return true;

            var foodPrefab = __instance.GetComponent<SaveablePrefab>();
            if (foodPrefab == null) return true;

            var packet = new FoodRemoveFromStoveRequestPacket
            {
                FoodInstanceId = foodPrefab.instanceId,
                FoodPrefabIndex = foodPrefab.prefabIndex
            };

            VerboseLogger.CookingRequest($"FoodRemoveFromStove, food={packet.FoodInstanceId}, prefab={packet.FoodPrefabIndex}");

            Plugin.NetworkManager.SendToAllReliable(PacketType.FoodRemoveFromStoveRequest, w =>
                PacketSerializer.WriteFoodRemoveFromStoveRequest(w, packet));

            return false;
        }

        #endregion

        #region Guest Interactions - Cutting

        /// <summary>
        /// Verbose cooking interaction trace: logs ShipItemKnife.OnAltActivate decision points
        /// (visible only with F8 verbose logging) so users can report why cutting fails.
        /// </summary>
        [HarmonyPatch(typeof(ShipItemKnife), "OnAltActivate")]
        [HarmonyPrefix]
        public static void ShipItemKnife_OnAltActivate_Diagnostic(ShipItemKnife __instance)
        {
            if (!Plugin.IsMultiplayer) return;

            var knifeSold = __instance.sold;
            var held = __instance.held;
            ShipItem pointedAtItem = held?.GetPointedAtItem();
            var pointedItemSold = pointedAtItem?.sold ?? false;
            var hasFoodState = pointedAtItem?.GetComponent<FoodState>() != null;
            var slicePrefabIndex = pointedAtItem?.GetComponent<FoodState>()?.slicePrefabIndex ?? -1;

            VerboseLogger.CookingEvent($"DIAG OnAltActivate: knife.sold={knifeSold}, held={held?.name ?? "NULL"}, pointedAt={pointedAtItem?.name ?? "NULL"}, pointedSold={pointedItemSold}, hasFoodState={hasFoodState}, slicePrefabIndex={slicePrefabIndex}");

            // Log the expected flow
            if (!knifeSold)
                VerboseLogger.CookingEvent($"DIAG: Will try to BUY knife (not owned)");
            else if (held == null)
                VerboseLogger.CookingEvent($"DIAG: FAIL - held is NULL (knife not in hand?)");
            else if (pointedAtItem == null)
                VerboseLogger.CookingEvent($"DIAG: FAIL - pointedAtItem is NULL (not looking at item?)");
            else if (!pointedItemSold)
                VerboseLogger.CookingEvent($"DIAG: FAIL - pointedAtItem.sold is false");
            else if (!hasFoodState)
                VerboseLogger.CookingEvent($"DIAG: FAIL - pointedAtItem has no FoodState");
            else if (slicePrefabIndex == 0)
                VerboseLogger.CookingEvent($"DIAG: FAIL - slicePrefabIndex is 0 (cannot slice)");
            else
                VerboseLogger.CookingEvent($"DIAG: Should call CutFood!");
        }

        /// <summary>
        /// Intercept ShipItemKnife.CutFood in multiplayer.
        /// Host: Execute our sync-aware cutting logic.
        /// Guest: Send request to host.
        /// </summary>
        [HarmonyPatch(typeof(ShipItemKnife), nameof(ShipItemKnife.CutFood))]
        [HarmonyPrefix]
        public static bool ShipItemKnife_CutFood_Prefix(ShipItemKnife __instance, FoodState food)
        {
            if (!Plugin.IsMultiplayer) return true;

            var knifePrefab = __instance.GetComponent<SaveablePrefab>();
            var foodPrefab = food.GetComponent<SaveablePrefab>();

            if (knifePrefab == null || foodPrefab == null) return true;

            if (Plugin.IsHost)
            {
                // Host: Execute cutting with proper sync
                VerboseLogger.CookingEvent($"Host CutFood, knife={knifePrefab.instanceId}, food={foodPrefab.instanceId}");
                CookingSyncManager.Instance?.ExecuteCutFood(food);
                return false; // Skip original - we handled it
            }
            else
            {
                // Guest: Send request to host
                var packet = new FoodCutRequestPacket
                {
                    KnifeInstanceId = knifePrefab.instanceId,
                    KnifePrefabIndex = knifePrefab.prefabIndex,
                    FoodInstanceId = foodPrefab.instanceId,
                    FoodPrefabIndex = foodPrefab.prefabIndex
                };

                VerboseLogger.CookingRequest($"FoodCut, knife={packet.KnifeInstanceId}, knifePrefab={packet.KnifePrefabIndex}, food={packet.FoodInstanceId}, foodPrefab={packet.FoodPrefabIndex}");

                Plugin.NetworkManager.SendToAllReliable(PacketType.FoodCutRequest, w =>
                    PacketSerializer.WriteFoodCutRequest(w, packet));

                return false; // Don't execute locally, wait for host
            }
        }

        #endregion

        #region Guest Interactions - Salting

        /// <summary>
        /// Intercept ShipItemSalt.SaltFood on guest - send request to host.
        /// </summary>
        [HarmonyPatch(typeof(ShipItemSalt), nameof(ShipItemSalt.SaltFood))]
        [HarmonyPrefix]
        public static bool ShipItemSalt_SaltFood_Prefix(ShipItemSalt __instance, FoodState food)
        {
            if (!Plugin.IsMultiplayer) return true;
            if (Plugin.IsHost) return true;
            if (CookingSyncManager.Instance?.IsApplyingRemoteState == true) return true;

            var saltPrefab = __instance.GetComponent<SaveablePrefab>();
            var foodPrefab = food.GetComponent<SaveablePrefab>();

            if (saltPrefab == null || foodPrefab == null) return true;

            var packet = new FoodSaltRequestPacket
            {
                SaltInstanceId = saltPrefab.instanceId,
                SaltPrefabIndex = saltPrefab.prefabIndex,
                FoodInstanceId = foodPrefab.instanceId,
                FoodPrefabIndex = foodPrefab.prefabIndex
            };

            VerboseLogger.CookingRequest($"FoodSalt, salt={packet.SaltInstanceId}, saltPrefab={packet.SaltPrefabIndex}, food={packet.FoodInstanceId}, foodPrefab={packet.FoodPrefabIndex}");

            Plugin.NetworkManager.SendToAllReliable(PacketType.FoodSaltRequest, w =>
                PacketSerializer.WriteFoodSaltRequest(w, packet));

            return false;
        }

        #endregion

        #region Guest Interactions - Soup

        /// <summary>
        /// Intercept ShipItemSoup.InsertFood on guest - send request to host.
        /// </summary>
        [HarmonyPatch(typeof(ShipItemSoup), nameof(ShipItemSoup.InsertFood))]
        [HarmonyPrefix]
        public static bool ShipItemSoup_InsertFood_Prefix(ShipItemSoup __instance, ShipItemFood food)
        {
            if (!Plugin.IsMultiplayer) return true;
            if (Plugin.IsHost) return true;
            if (CookingSyncManager.Instance?.IsApplyingRemoteState == true) return true;

            var soupPrefab = __instance.GetComponent<SaveablePrefab>();
            var foodPrefab = food.GetComponent<SaveablePrefab>();

            if (soupPrefab == null || foodPrefab == null) return true;

            var packet = new SoupAddFoodRequestPacket
            {
                FoodInstanceId = foodPrefab.instanceId,
                FoodPrefabIndex = foodPrefab.prefabIndex,
                SoupInstanceId = soupPrefab.instanceId,
                SoupPrefabIndex = soupPrefab.prefabIndex
            };

            VerboseLogger.CookingRequest($"SoupAddFood, food={packet.FoodInstanceId}, foodPrefab={packet.FoodPrefabIndex}, soup={packet.SoupInstanceId}, soupPrefab={packet.SoupPrefabIndex}");

            Plugin.NetworkManager.SendToAllReliable(PacketType.SoupAddFoodRequest, w =>
                PacketSerializer.WriteSoupAddFoodRequest(w, packet));

            return false;
        }

        /// <summary>
        /// Intercept ShipItemSoup.OnItemClick on guest when using bottle - send water request to host.
        /// This replaces the FillWater patch since we need access to the bottle being used.
        /// </summary>
        [HarmonyPatch(typeof(ShipItemSoup), nameof(ShipItemSoup.OnItemClick))]
        [HarmonyPrefix]
        public static bool ShipItemSoup_OnItemClick_Prefix(ShipItemSoup __instance, PickupableItem heldItem, ref bool __result)
        {
            if (!Plugin.IsMultiplayer) return true;
            if (Plugin.IsHost) return true;
            if (CookingSyncManager.Instance?.IsApplyingRemoteState == true) return true;

            // Check if this is a bottle interaction (water fill)
            var bottle = heldItem?.GetComponent<ShipItemBottle>();
            if (bottle != null && bottle.amount == 1f && bottle.sold && __instance.sold)
            {
                var soupPrefab = __instance.GetComponent<SaveablePrefab>();
                var bottlePrefab = bottle.GetComponent<SaveablePrefab>();

                if (soupPrefab != null && bottlePrefab != null)
                {
                    var packet = new AddWaterRequestPacket
                    {
                        BottleInstanceId = bottlePrefab.instanceId,
                        ContainerInstanceId = soupPrefab.instanceId
                    };

                    VerboseLogger.CookingRequest($"SoupAddWater, bottle={packet.BottleInstanceId}, soup={packet.ContainerInstanceId}");

                    Plugin.NetworkManager.SendToAllReliable(PacketType.SoupAddWaterRequest, w =>
                        PacketSerializer.WriteAddWaterRequest(w, packet));

                    // Update bottle locally - empty it since water is transferred to host's soup
                    // Soup state will sync back via CookingState packets
                    bottle.health = 0;
                    UISoundPlayer.instance?.PlayLiquidPourSound();

                    __result = false; // Indicates the interaction was handled
                    return false; // Skip local soup fill - host handles it
                }
            }

            // For food insertion, let the InsertFood patch handle it
            // For other interactions (placing items), let it proceed locally
            return true;
        }

        #endregion

        #region Guest Interactions - Kettle

        /// <summary>
        /// J: Intercept ShipItemKettle.OnItemClick on guest when using a water bottle - send water request
        /// to host. Mirrors ShipItemSoup_OnItemClick_Prefix; reuses the SoupAddWaterRequest packet (the host
        /// handler resolves the container as soup OR kettle). Without this the guest fill ran purely locally
        /// and the 2Hz KettleState snapshot reverted it within 0.5s.
        /// </summary>
        [HarmonyPatch(typeof(ShipItemKettle), nameof(ShipItemKettle.OnItemClick))]
        [HarmonyPrefix]
        public static bool ShipItemKettle_OnItemClick_Prefix(ShipItemKettle __instance, PickupableItem heldItem, ref bool __result)
        {
            if (!Plugin.IsMultiplayer) return true;
            if (Plugin.IsHost) return true;
            if (CookingSyncManager.Instance?.IsApplyingRemoteState == true) return true;

            // Check if this is a bottle interaction (water fill)
            var bottle = heldItem?.GetComponent<ShipItemBottle>();
            if (bottle != null && bottle.amount == 1f && bottle.sold && __instance.sold)
            {
                var kettlePrefab = __instance.GetComponent<SaveablePrefab>();
                var bottlePrefab = bottle.GetComponent<SaveablePrefab>();

                if (kettlePrefab != null && bottlePrefab != null)
                {
                    var packet = new AddWaterRequestPacket
                    {
                        BottleInstanceId = bottlePrefab.instanceId,
                        ContainerInstanceId = kettlePrefab.instanceId
                    };

                    VerboseLogger.CookingRequest($"KettleAddWater (via SoupAddWaterRequest), bottle={packet.BottleInstanceId}, kettle={packet.ContainerInstanceId}");

                    Plugin.NetworkManager.SendToAllReliable(PacketType.SoupAddWaterRequest, w =>
                        PacketSerializer.WriteAddWaterRequest(w, packet));

                    // Update bottle locally - empty it since water is transferred to host's kettle.
                    // Kettle water syncs back via KettleState; the host's ItemHealthChanged echo corrects
                    // any over-drain when the kettle is nearly full (FillWater remainder).
                    bottle.health = 0;
                    UISoundPlayer.instance?.PlayLiquidPourSound();

                    __result = false; // Indicates the interaction was handled
                    return false; // Skip local kettle fill - host handles it
                }
            }

            // Tea insertion falls through to the InsertDrink prefix; placement falls through vanilla
            return true;
        }

        /// <summary>
        /// Intercept ShipItemKettle.InsertDrink on guest - send request to host.
        /// </summary>
        [HarmonyPatch(typeof(ShipItemKettle), nameof(ShipItemKettle.InsertDrink))]
        [HarmonyPrefix]
        public static bool ShipItemKettle_InsertDrink_Prefix(ShipItemKettle __instance, ShipItemTea drink)
        {
            if (!Plugin.IsMultiplayer) return true;
            if (Plugin.IsHost) return true;
            if (CookingSyncManager.Instance?.IsApplyingRemoteState == true) return true;

            var kettlePrefab = __instance.GetComponent<SaveablePrefab>();
            var teaPrefab = drink.GetComponent<SaveablePrefab>();

            if (kettlePrefab == null || teaPrefab == null) return true;

            var packet = new KettleAddTeaRequestPacket
            {
                TeaInstanceId = teaPrefab.instanceId,
                TeaPrefabIndex = teaPrefab.prefabIndex,
                KettleInstanceId = kettlePrefab.instanceId,
                KettlePrefabIndex = kettlePrefab.prefabIndex
            };

            VerboseLogger.CookingRequest($"KettleAddTea, tea={packet.TeaInstanceId}, teaPrefab={packet.TeaPrefabIndex}, kettle={packet.KettleInstanceId}, kettlePrefab={packet.KettlePrefabIndex}");

            Plugin.NetworkManager.SendToAllReliable(PacketType.KettleAddTeaRequest, w =>
                PacketSerializer.WriteKettleAddTeaRequest(w, packet));

            return false;
        }

        /// <summary>
        /// Intercept ShipItemKettle.PourTea on guest - send request to host.
        /// </summary>
        [HarmonyPatch(typeof(ShipItemKettle), nameof(ShipItemKettle.PourTea))]
        [HarmonyPrefix]
        public static bool ShipItemKettle_PourTea_Prefix(ShipItemKettle __instance, ShipItemBottle targetMug)
        {
            if (!Plugin.IsMultiplayer) return true;
            if (Plugin.IsHost) return true;
            if (CookingSyncManager.Instance?.IsApplyingRemoteState == true) return true;

            var kettlePrefab = __instance.GetComponent<SaveablePrefab>();
            var mugPrefab = targetMug.GetComponent<SaveablePrefab>();

            if (kettlePrefab == null || mugPrefab == null) return true;

            var packet = new KettlePourRequestPacket
            {
                KettleInstanceId = kettlePrefab.instanceId,
                MugInstanceId = mugPrefab.instanceId
            };

            VerboseLogger.CookingRequest($"KettlePour, kettle={packet.KettleInstanceId}, mug={packet.MugInstanceId}");

            Plugin.NetworkManager.SendToAllReliable(PacketType.KettlePourRequest, w =>
                PacketSerializer.WriteKettlePourRequest(w, packet));

            return false;
        }

        #endregion

        #region Fuel Insertion Sync

        /// <summary>
        /// When fuel is inserted into a stove, send event to other player.
        /// Both host and guest send this - it's bidirectional sync.
        /// </summary>
        [HarmonyPatch(typeof(StoveFuelTrigger), nameof(StoveFuelTrigger.InsertFuel))]
        [HarmonyPostfix]
        public static void StoveFuelTrigger_InsertFuel_Postfix(StoveFuelTrigger __instance, ShipItem item)
        {
            if (!Plugin.IsMultiplayer) return;
            if (CookingSyncManager.Instance?.IsApplyingRemoteState == true) return;

            // Verify insertion actually happened
            var stoveFuel = item.GetComponent<StoveFuel>();
            if (stoveFuel == null || !stoveFuel.inserted) return;

            // Get IDs
            var fuelPrefab = item.GetComponent<SaveablePrefab>();
            var stove = __instance.transform.parent?.GetComponent<ShipItemStove>();
            var stovePrefab = stove?.GetComponent<SaveablePrefab>();

            if (fuelPrefab == null || stovePrefab == null)
            {
                Plugin.Log.LogWarning($"[COOKING] InsertFuel_Postfix: missing prefab (fuel={fuelPrefab != null}, stove={stovePrefab != null})");
                return;
            }

            var packet = new FuelInsertedPacket
            {
                FuelInstanceId = fuelPrefab.instanceId,
                StoveInstanceId = stovePrefab.instanceId
            };

            VerboseLogger.CookingSend($"FuelInserted, fuel={packet.FuelInstanceId}, stove={packet.StoveInstanceId}");

            Plugin.NetworkManager.SendToAllReliable(PacketType.FuelInsertedEvent, w =>
                PacketSerializer.WriteFuelInserted(w, packet));
        }

        // STOVE FUEL-COUNT CLAMP (2026-07-02 "-17300/3" report): vanilla StoveFuel.Update runs
        // `UnregisterBurntFuel(); DestroyItem();` EVERY FRAME while a burnt fuel is lit and not yet
        // destroyed, and currentFuel-- has no floor. If anything no-ops the destroy (the range-cull
        // suppression exempted in ItemPatches was one confirmed way; an exception in the destroy chain
        // is another), the counter plunges ~60/s. Clamp at 0, repair the garbage look-text once, and
        // swallow the redundant call. Solo-safe: the clamp only ever fires when the counter is already
        // broken (vanilla can't legitimately call this with currentFuel <= 0).
        private static readonly AccessTools.FieldRef<StoveFuelTrigger, int> CurrentFuelRef =
            AccessTools.FieldRefAccess<StoveFuelTrigger, int>("currentFuel");

        [HarmonyPatch(typeof(StoveFuelTrigger), nameof(StoveFuelTrigger.UnregisterBurntFuel))]
        [HarmonyPrefix]
        public static bool StoveFuelTrigger_UnregisterBurntFuel_Prefix(StoveFuelTrigger __instance)
        {
            if (CurrentFuelRef(__instance) > 0) return true;

            CurrentFuelRef(__instance) = 0;
            try { Traverse.Create(__instance).Method("UpdateLookText").GetValue(); }
            catch (System.Exception e) { Plugin.Log.LogWarning($"[COOKING] could not refresh stove look text after fuel clamp: {e.Message}"); }
            Plugin.Log.LogWarning("[COOKING] Clamped stove fuel counter at 0 (burnt-fuel destroy is being suppressed or failing - see the -17300/3 fix notes)");
            return false;
        }

        #endregion
    }
}
