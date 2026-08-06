using Crest;          // BoatProbes - the hull buoyancy component a guest must keep in step with the host
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
        /// The host stays authoritative for how much water is in a hull. A GUEST, however, must still turn
        /// that number into physics.
        ///
        /// (v0.3.0) This patch used to return false and stop, and that was a large hole rather than a small
        /// one. `UpdateWaterAndDrag` is TWO things bolted together: it integrates waterLevel from leaks,
        /// rain and bailing, and it then DERIVES the hull's physics from whatever waterLevel now is. It is
        /// the only place in the entire game that writes `BoatProbes._forceMultiplier` (buoyancy),
        /// `Rigidbody.drag` and the hull collider's enabled flag at runtime. Skipping the whole method
        /// therefore did not just stop the guest simulating damage - it froze the guest's copy of the hull
        /// at whatever buoyancy and drag it happened to have, permanently, however flooded the host's copy
        /// became. A hull taking on water in a storm gets heavier and draggier on the host and stays buoyant
        /// and slippery on the guest, so the two float at different heights: the boat rides high or sits
        /// low depending on which end you are standing on.
        ///
        /// The nastier variant is the one that used to survive a restart. `BoatDamage.LoadDamage` pins
        /// `_forceMultiplier` to 0 for a hull loaded at waterLevel >= 1 (a sunk wreck). A guest's phantom
        /// save is a copy of their own solo world and is reused on every future join, so once it contained a
        /// sunk boat, that hull loaded with zero buoyancy and - with this method skipped - NOTHING would
        /// ever put it back. Not a rejoin, not a new session, not the join snapshot, which sets waterLevel
        /// and clears `sunk` but never touches buoyancy.
        ///
        /// So the guest now runs the DERIVATION half and skips the INTEGRATION half. waterLevel and
        /// hullDamage keep arriving from the host exactly as before; everything computed from them is
        /// recomputed locally every frame, which also means a hull that loads wrong heals itself on the
        /// next frame instead of staying wrong forever.
        ///
        /// Deliberately NOT replicated: `CacheItemsOnSinking()` and `sinkRotation`. Items and boat rotation
        /// are both synced by their own managers, and letting the guest independently decide to cache a
        /// sinking boat's contents would be a second authority over state that already has one.
        ///
        /// KEEP IN STEP WITH VANILLA: the body below is a transcription of the second half of
        /// BoatDamage.UpdateWaterAndDrag. If that method changes in a game update, this has to change too.
        /// </summary>
        [HarmonyPatch(typeof(BoatDamage), "UpdateWaterAndDrag")]
        public static class BoatDamageUpdateWaterAndDragPatch
        {
            [HarmonyPrefix]
            public static bool Prefix(BoatDamage __instance)
            {
                if (Plugin.IsMultiplayer && !Plugin.IsHost)
                {
                    // Coop.GuestHullPhysics is a kill switch, not a preference: this whole path is
                    // unreachable without a second machine, so a crew that hits trouble with it needs a way
                    // out that does not involve waiting for a build. Off = the pre-v0.3.0 behavior exactly.
                    if (Plugin.GuestHullPhysicsConfig == null || Plugin.GuestHullPhysicsConfig.Value)
                        ApplyGuestHullPhysics(__instance);
                    return false; // the host owns the water level itself
                }
                return true;
            }
        }

        // Private vanilla fields, resolved once. This runs per boat per frame, so a Traverse lookup per call
        // would be the wrong tool.
        private static readonly AccessTools.FieldRef<BoatDamage, float> _baseBuoyancy =
            AccessTools.FieldRefAccess<BoatDamage, float>("baseBuoyancy");
        private static readonly AccessTools.FieldRef<BoatDamage, BoatProbes> _probes =
            AccessTools.FieldRefAccess<BoatDamage, BoatProbes>("boat");
        private static readonly AccessTools.FieldRef<BoatDamage, CapsuleCollider> _hullCol =
            AccessTools.FieldRefAccess<BoatDamage, CapsuleCollider>("boatCol");
        private static readonly AccessTools.FieldRef<BoatDamage, Rigidbody> _body =
            AccessTools.FieldRefAccess<BoatDamage, Rigidbody>("rigidbody");

        /// <summary>
        /// Turn the host's water level into the same hull physics the host has. Never advances waterLevel -
        /// that remains the host's to decide. See the patch doc above for why this exists at all.
        /// </summary>
        private static void ApplyGuestHullPhysics(BoatDamage d)
        {
            try
            {
                var body = _body(d);
                var probes = _probes(d);
                var col = _hullCol(d);
                // Start() has not run yet (a boat streamed in this frame). Do NOT fall back to
                // GetComponent: a null here means vanilla has not captured baseBuoyancy either, so we would
                // derive against a baseline of zero and pin the hull's buoyancy off. Next frame has it.
                if (body == null || probes == null) return;

                float baseBuoyancy = _baseBuoyancy(d);

                // Vanilla clamps these before using them, and the values arrive over the wire, so clamp here
                // too rather than trusting a packet to be in range.
                if (d.waterLevel > 1f) d.waterLevel = 1f;
                if (d.waterLevel < 0f) d.waterLevel = 0f;
                if (d.hullDamage > 1f) d.hullDamage = 1f;
                if (d.hullDamage < 0f) d.hullDamage = 0f;

                if (d.sunk) body.drag += Time.deltaTime * 6.5f;
                else body.drag = Mathf.Lerp(0f, d.waterDrag, d.waterLevel);
                if (body.drag > 10f) body.drag = 10f;
                if (body.drag < 0f) body.drag = 0f;

                if (!d.sunk)
                    probes._forceMultiplier = Mathf.Lerp(baseBuoyancy, baseBuoyancy * 0.66f, (d.waterLevel - 0.5f) * 2f);

                if (d.waterLevel >= 1f)
                {
                    if (!d.sunk)
                    {
                        d.sunk = true;
                        VerboseLogger.DamageEvent($"Hull '{d.gameObject.name}' is under; deriving sunk physics locally.");
                    }
                    probes._forceMultiplier -= Time.deltaTime * baseBuoyancy * 0.33f;
                    if (probes._forceMultiplier < 0f) probes._forceMultiplier = 0f;
                    if (col != null) col.enabled = false;
                }
                else
                {
                    d.sunk = false;
                    if (col != null) col.enabled = true;
                }
            }
            catch (System.Exception e)
            {
                // A hull that cannot derive its physics is a floating-height desync, not a crash. Log it and
                // leave the hull alone rather than taking the frame down.
                Plugin.Log.LogWarning("[Damage] could not derive guest hull physics: " + e.Message);
            }
        }

        /// <summary>
        /// Disable Impact on guest - host processes collisions.
        /// </summary>
        [HarmonyPatch(typeof(BoatDamage), "Impact")]
        public static class BoatDamageImpactPatch
        {
            [HarmonyPrefix]
            public static bool Prefix(BoatDamage __instance)
            {
                if (Plugin.IsMultiplayer && !Plugin.IsHost)
                {
                    return false; // Skip on guest
                }

                // (v0.2.28 Fix C) Shipyard discharge suppression: vanilla DischargeShip instant-teleports
                // the boat to shipReleasePosition and re-enables physics - the water/dock depenetration
                // registers a >1.5 m/s impact and the boat "damages itself" leaving the cradle (and on
                // remote peers the forced convergence snap can do the same). Skip Impact for a short
                // window after any shipyard release of THIS boat. Runs in multiplayer only; vanilla solo
                // behavior is untouched.
                // Cheap early-out: HasActiveSuppression is a Count check, so the common Impact path
                // (no discharge window live anywhere) never pays the GetComponent lookup.
                if (Plugin.IsMultiplayer && ShipyardSyncManager.HasActiveSuppression)
                {
                    var boatName = __instance.GetComponent<SaveableObject>()?.gameObject.name;
                    if (ShipyardSyncManager.IsImpactSuppressed(boatName))
                    {
                        VerboseLogger.DamageEvent($"Impact suppressed for '{boatName}' (recent shipyard discharge)");
                        return false;
                    }
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

                // A: vanilla only repairs when aboard (GameState.currentBoat != null); sold oakum used
                // ashore is a no-op. Let vanilla run its no-op instead of routing a repair to lastBoat.
                if (GameState.currentBoat == null) return true;

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
            // (v0.2.32, P1) __runOriginal + HarmonyBefore: NANDTweaks patches this same method with a
            // PREFIX THAT RETURNS FALSE (its bailingTweaks full replacement, NANDTweaks
            // BoatDamagePatches.cs:54). In Harmony 2, once any prefix returns false, later prefixes are
            // SKIPPED unless they declare __runOriginal - so without it, __state stayed 0, the postfix
            // early-returned, and a guest running NANDTweaks bailed water locally while the host never
            // received SendBailRequest (silent water-level divergence, snapped back by the next
            // authoritative damage sync). HarmonyBefore additionally orders us first when both are
            // present; __runOriginal is the belt-and-braces for any other mod that skips this method.
            // The capture itself only reads bottle state, so it is safe to run whether or not the
            // original (or NANDTweaks' replacement) executes.
            [HarmonyPrefix]
            [HarmonyBefore("com.nandbrew.nandtweaks")]
            public static void Prefix(PickupableItem heldItem, out float __state, bool __runOriginal)
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

                // If bottle now has sea water, bailing occurred (true for vanilla AND for NANDTweaks'
                // bailingTweaks replacement, which writes amount/health the same way).
                if (bottle.amount == 9f && bottle.health > 0f)
                {
                    DamageSyncManager.Instance?.SendBailRequest(prefab.instanceId, __state);
                }
            }
        }

        #endregion
    }
}
