using HarmonyLib;
using SailwindCoop.Debug;
using SailwindCoop.Sync;
using UnityEngine;

namespace SailwindCoop.Patches
{
    /// <summary>
    /// Harmony patches for boat damage synchronization.
    /// Disables guest damage simulation, captures host impacts and guest pump input.
    /// </summary>
    public static class DamagePatches
    {
        #region Disable Guest Damage Simulation

        /// <summary>
        /// Disable UpdateWaterAndDrag on guest - host is authoritative for water level.
        /// </summary>
        [HarmonyPatch(typeof(BoatDamage), "UpdateWaterAndDrag")]
        public static class BoatDamageUpdateWaterAndDragPatch
        {
            [HarmonyPrefix]
            public static bool Prefix()
            {
                if (Plugin.IsMultiplayer && !Plugin.IsHost)
                {
                    return false; // Skip on guest
                }
                return true;
            }
        }

        /// <summary>
        /// Disable Impact on guest - host processes collisions.
        /// </summary>
        [HarmonyPatch(typeof(BoatDamage), "Impact")]
        public static class BoatDamageImpactPatch
        {
            [HarmonyPrefix]
            public static bool Prefix()
            {
                if (Plugin.IsMultiplayer && !Plugin.IsHost)
                {
                    return false; // Skip on guest
                }
                return true;
            }

            [HarmonyPostfix]
            public static void Postfix(BoatDamage __instance)
            {
                // Host broadcasts impact to guest
                if (Plugin.IsMultiplayer && Plugin.IsHost)
                {
                    DamageSyncManager.Instance?.OnLocalImpact(__instance);
                }
            }
        }

        /// <summary>
        /// Disable DailyDamage on guest - host handles daily decay.
        /// </summary>
        [HarmonyPatch(typeof(BoatDamage), "DailyDamage")]
        public static class BoatDamageDailyDamagePatch
        {
            [HarmonyPrefix]
            public static bool Prefix()
            {
                if (Plugin.IsMultiplayer && !Plugin.IsHost)
                {
                    return false; // Skip on guest
                }
                return true;
            }
        }

        /// <summary>
        /// Disable Overflow on guest - host calculates wave overflow.
        /// </summary>
        [HarmonyPatch(typeof(BoatDamage), "Overflow")]
        public static class BoatDamageOverflowPatch
        {
            [HarmonyPrefix]
            public static bool Prefix()
            {
                if (Plugin.IsMultiplayer && !Plugin.IsHost)
                {
                    return false; // Skip on guest
                }
                return true;
            }
        }

        #endregion

        #region Guest Pump Input (Optimistic Local)

        /// <summary>
        /// Allow guest pump to work locally (optimistic feedback).
        /// The pump's drain effect is applied locally, and input is sent to host.
        /// </summary>
        [HarmonyPatch(typeof(BilgePump), "Update")]
        public static class BilgePumpUpdatePatch
        {
            [HarmonyPostfix]
            public static void Postfix(BilgePump __instance)
            {
                // D1: nothing to do. Vanilla BilgePump.Update already drains BoatDamage.waterLevel locally on the
                // guest (it gates only on currentInput>0 && !sunk, NOT on host/guest), giving the optimistic feel.
                // The old ApplyLocalPumpDrain call here drained waterLevel a SECOND time with the identical formula
                // (~2x too fast, snapping back each 1Hz DamageState). The guest's pump INTENT still reaches the
                // host via DamageSyncManager.PollAndSendPumpInput (a separate Update tick). Kept as a no-op so the
                // Harmony patch set is unchanged.
            }
        }

        #endregion

        #region Guest Oakum Repair

        /// <summary>
        /// Intercept oakum repair on guest - send request to host instead.
        /// Host-authoritative: guest skips local execution, host applies and syncs back.
        /// </summary>
        [HarmonyPatch(typeof(ShipItemOakum), "OnAltActivate")]
        public static class ShipItemOakumOnAltActivatePatch
        {
            [HarmonyPrefix]
            public static bool Prefix(ShipItemOakum __instance)
            {
                if (!Plugin.IsMultiplayer || Plugin.IsHost) return true;

                // H: an UNSOLD oakum's OnAltActivate is the PURCHASE path (vanilla base.OnAltActivate ->
                // Shopkeeper.TryToSellItem), not a repair. Let vanilla run so the guest stall-buy router
                // (ShopkeeperTryToSellItemPatch) host-routes the purchase. Only the sold branch is a repair.
                if (!__instance.sold) return true;

                // Guest: send request to host instead of executing locally
                var prefab = __instance.GetComponent<SaveablePrefab>();
                if (prefab == null) return true;

                // H hardening: an unregistered id=0 packet is doomed host-side (FindItemByInstanceId can't
                // resolve the shared id=0 pool). Log and drop rather than desync via a local fallback.
                if (prefab.instanceId == 0)
                {
                    Plugin.Log.LogWarning("[DAMAGE] Oakum repair skipped: item has unregistered instanceId=0 (legacy/scene oakum) - request not sent");
                    return false;
                }

                DamageSyncManager.Instance?.SendOakumRepairRequest(prefab.instanceId);

                return false; // Skip local execution on guest
            }
        }

        #endregion

        #region Guest Bail Water (Bucket/Bottle)

        /// <summary>
        /// Intercept water bailing on guest - send request to host.
        /// Guest executes locally for responsive feedback, host applies authoritatively.
        /// Uses Prefix to capture state, Postfix to send request with actual bailed amount.
        /// </summary>
        [HarmonyPatch(typeof(BoatDamageWaterButton), "OnItemClick")]
        public static class BoatDamageWaterButtonOnItemClickPatch
        {
            [HarmonyPrefix]
            public static void Prefix(PickupableItem heldItem, out float __state)
            {
                __state = 0f;

                if (!Plugin.IsMultiplayer || Plugin.IsHost) return;
                if (heldItem == null || heldItem.GetType() != typeof(ShipItemBottle)) return;

                var bottle = (ShipItemBottle)heldItem;

                // Store remaining capacity before bailing (this is what will be bailed)
                float remainingCapacity = bottle.GetRemainingCapacity();
                float capacity = bottle.GetCapacity();

                // Apply same cap as game: non-buckets max 5 units
                if (remainingCapacity > 5f && capacity != 9f)
                {
                    remainingCapacity = 5f;
                }

                __state = remainingCapacity;
            }

            [HarmonyPostfix]
            public static void Postfix(PickupableItem heldItem, float __state)
            {
                if (!Plugin.IsMultiplayer || Plugin.IsHost) return;
                if (__state <= 0f) return;

                if (heldItem == null || heldItem.GetType() != typeof(ShipItemBottle)) return;

                var bottle = (ShipItemBottle)heldItem;
                var prefab = bottle.GetComponent<SaveablePrefab>();
                if (prefab == null) return;

                // If bottle now has sea water, bailing occurred
                if (bottle.amount == 9f && bottle.health > 0f)
                {
                    DamageSyncManager.Instance?.SendBailRequest(prefab.instanceId, __state);
                }
            }
        }

        #endregion
    }
}
