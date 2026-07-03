using HarmonyLib;
using SailwindCoop.Sync;
using UnityEngine;

namespace SailwindCoop.Patches
{
    /// <summary>
    /// Harmony patches for fishing synchronization.
    /// </summary>
    [HarmonyPatch]
    public static class FishingPatches
    {
        /// <summary>
        /// Patch ShipItem.OnPickup to detect rod pickup.
        /// Must patch ShipItem (not PickupableItem) because ShipItem overrides OnPickup.
        /// </summary>
        [HarmonyPatch(typeof(ShipItem), "OnPickup")]
        [HarmonyPostfix]
        public static void OnShipItemPickup(ShipItem __instance)
        {
            if (!Plugin.IsMultiplayer) return;
            if (FishingSyncManager.Instance?.IsApplyingRemoteState == true) return;

            var rod = __instance as ShipItemFishingRod;
            if (rod != null)
            {
                FishingSyncManager.Instance?.OnLocalRodPickedUp(rod);
            }
        }

        /// <summary>
        /// Patch ShipItem.OnDrop to detect rod drop.
        /// Must patch ShipItem (not PickupableItem) because ShipItem overrides OnDrop.
        /// </summary>
        [HarmonyPatch(typeof(ShipItem), "OnDrop")]
        [HarmonyPostfix]
        public static void OnShipItemDrop(ShipItem __instance)
        {
            if (!Plugin.IsMultiplayer) return;
            if (FishingSyncManager.Instance?.IsApplyingRemoteState == true) return;

            var rod = __instance as ShipItemFishingRod;
            if (rod != null)
            {
                FishingSyncManager.Instance?.OnLocalRodDropped(rod);
            }
        }

        /// <summary>
        /// Patch FishingRodFish.CatchFish - block on guest unless receiving remote state.
        /// Prevents guest from getting independent fish bites.
        /// </summary>
        [HarmonyPatch(typeof(FishingRodFish), "CatchFish")]
        [HarmonyPrefix]
        public static bool OnCatchFishPrefix(FishingRodFish __instance)
        {
            if (!Plugin.IsMultiplayer) return true;
            if (FishingSyncManager.Instance?.IsApplyingRemoteState == true) return true; // Allow when receiving packet

            var rod = Traverse.Create(__instance).Field("rod").GetValue<ShipItemFishingRod>();
            if (rod == null) return true;

            var prefab = rod.GetComponent<SaveablePrefab>();
            if (prefab == null) return true;

            // Block if rod owned by remote player (we're just viewing)
            if (!FishingSyncManager.Instance.IsLocalPlayerOwner(prefab.instanceId))
                return false;

            return true; // Allow for local owner
        }

        /// <summary>
        /// Patch FishingRodFish.CatchFish to broadcast fish bite.
        /// </summary>
        [HarmonyPatch(typeof(FishingRodFish), "CatchFish")]
        [HarmonyPostfix]
        public static void OnCatchFishPostfix(FishingRodFish __instance)
        {
            if (!Plugin.IsMultiplayer) return;
            if (FishingSyncManager.Instance?.IsApplyingRemoteState == true) return;

            var rod = Traverse.Create(__instance).Field("rod").GetValue<ShipItemFishingRod>();
            if (rod == null) return;

            // Get fish prefab index
            var currentFish = __instance.currentFish;
            int prefabIndex = GetFishPrefabIndex(currentFish);

            FishingSyncManager.Instance?.OnLocalFishBite(rod, prefabIndex);
        }

        /// <summary>
        /// Patch FishingRodFish.ReleaseFish - block on guest unless receiving remote state.
        /// Prevents guest from getting independent fish escapes.
        /// </summary>
        [HarmonyPatch(typeof(FishingRodFish), "ReleaseFish")]
        [HarmonyPrefix]
        public static bool OnReleaseFishPrefix(FishingRodFish __instance)
        {
            if (!Plugin.IsMultiplayer) return true;
            if (FishingSyncManager.Instance?.IsApplyingRemoteState == true) return true; // Allow when receiving packet

            var rod = Traverse.Create(__instance).Field("rod").GetValue<ShipItemFishingRod>();
            if (rod == null) return true;

            var prefab = rod.GetComponent<SaveablePrefab>();
            if (prefab == null) return true;

            // Block if rod owned by remote player (we're just viewing)
            if (!FishingSyncManager.Instance.IsLocalPlayerOwner(prefab.instanceId))
                return false;

            return true; // Allow for local owner
        }

        /// <summary>
        /// Patch FishingRodFish.ReleaseFish to broadcast fish escape.
        /// </summary>
        [HarmonyPatch(typeof(FishingRodFish), "ReleaseFish")]
        [HarmonyPostfix]
        public static void OnReleaseFishPostfix(FishingRodFish __instance)
        {
            if (!Plugin.IsMultiplayer) return;
            if (FishingSyncManager.Instance?.IsApplyingRemoteState == true) return;

            var rod = Traverse.Create(__instance).Field("rod").GetValue<ShipItemFishingRod>();
            if (rod == null) return;

            FishingSyncManager.Instance?.OnLocalFishEscape(rod);
        }

        /// <summary>
        /// Patch ShipItemFishingRod.ThrowRod to sync cast event.
        /// </summary>
        [HarmonyPatch(typeof(ShipItemFishingRod), "ThrowRod")]
        [HarmonyPostfix]
        public static void OnThrowRod(ShipItemFishingRod __instance)
        {
            if (!Plugin.IsMultiplayer) return;
            if (FishingSyncManager.Instance?.IsApplyingRemoteState == true) return;

            var throwCharge = Traverse.Create(__instance).Field("throwCharge").GetValue<float>();
            FishingSyncManager.Instance?.OnLocalRodCast(__instance, throwCharge);
        }

        /// <summary>
        /// Patch ShipItemFishingRod.OnScroll to sync line length changes.
        /// </summary>
        [HarmonyPatch(typeof(ShipItemFishingRod), "OnScroll")]
        [HarmonyPostfix]
        public static void OnRodScroll(ShipItemFishingRod __instance)
        {
            if (!Plugin.IsMultiplayer) return;
            if (FishingSyncManager.Instance?.IsApplyingRemoteState == true) return;

            var bobberJoint = Traverse.Create(__instance).Field("bobberJoint").GetValue<ConfigurableJoint>();
            if (bobberJoint != null)
            {
                var currentLength = Traverse.Create(__instance).Field("currentTargetLength").GetValue<float>();
                FishingSyncManager.Instance?.OnLocalLineLengthChanged(__instance, currentLength);
            }
        }

        /// <summary>
        /// K2: patch ShipItemFishingRod.OnEnterInventory to broadcast the vanilla stow-time line reset
        /// (currentTargetLength = minLength runs purely locally in vanilla) and stop the cast stream.
        /// Receivers apply it through the normal FishingLineLength path.
        /// </summary>
        [HarmonyPatch(typeof(ShipItemFishingRod), "OnEnterInventory")]
        [HarmonyPostfix]
        public static void OnRodEnterInventory(ShipItemFishingRod __instance)
        {
            if (!Plugin.IsMultiplayer) return;
            if (FishingSyncManager.Instance?.IsApplyingRemoteState == true) return;
            if (ItemSyncManager.Instance?.IsApplyingRemoteState == true) return;

            FishingSyncManager.Instance?.OnLocalRodStowed(__instance);
        }

        /// <summary>
        /// K4: patch ShipItemFishingRod.OnItemClick to sync a hook ATTACH. Vanilla sets rod.health=1 and
        /// destroys the held hook, but rod health was never broadcast from this path (OnLocalItemHealthChanged
        /// only fired from food/drink patches), so a successful attach left the other machines' rod hookless.
        /// health > 0 with __result==true can only be the attach transition: vanilla returns false early
        /// whenever health was ALREADY > 0, and the non-hook click path leaves health at 0.
        /// </summary>
        [HarmonyPatch(typeof(ShipItemFishingRod), "OnItemClick")]
        [HarmonyPostfix]
        public static void OnRodItemClick(ShipItemFishingRod __instance, bool __result)
        {
            if (!Plugin.IsMultiplayer) return;
            if (FishingSyncManager.Instance?.IsApplyingRemoteState == true) return;
            if (ItemSyncManager.Instance?.IsApplyingRemoteState == true) return;

            if (__result && __instance.health > 0f)
            {
                ItemSyncManager.Instance?.OnLocalItemHealthChanged(__instance, forceSync: true);
            }
        }

        /// <summary>
        /// K5 (glitchy perpetual rod bend): block FishingRodFish.FixedUpdate for REMOTE-owned rods. The
        /// viewer's local sim recomputed currentTargetTension from its own (wrong, unsynced) bobber joint
        /// forces every physics tick and fought the 5Hz FishingState applies. With it blocked, the
        /// packet-driven SetRodTension fully owns the viewer's rod bend. Mirrors the existing
        /// CatchFish/ReleaseFish prefix-block pattern (no IsApplyingRemoteState allowance needed: no
        /// packet handler ever drives FixedUpdate).
        /// </summary>
        // 50Hz per rod: use a cached FieldRef, not per-tick Traverse reflection (review finding).
        private static readonly AccessTools.FieldRef<FishingRodFish, ShipItemFishingRod> FishRodRef =
            AccessTools.FieldRefAccess<FishingRodFish, ShipItemFishingRod>("rod");

        [HarmonyPatch(typeof(FishingRodFish), "FixedUpdate")]
        [HarmonyPrefix]
        public static bool OnFishFixedUpdatePrefix(FishingRodFish __instance)
        {
            if (!Plugin.IsMultiplayer) return true;

            var mgr = FishingSyncManager.Instance;
            if (mgr == null) return true;

            var rod = FishRodRef(__instance);
            if (rod == null) return true;

            var prefab = rod.GetComponent<SaveablePrefab>();
            if (prefab == null) return true;

            var owner = mgr.GetRodOwner(prefab.instanceId);
            if (owner != 0 && !mgr.IsLocalPlayerOwner(prefab.instanceId))
                return false; // remote-owned: viewer sim off, FishingState packets own the bend

            return true;
        }

        /// <summary>
        /// Patch FishingRodFish.CollectFish to route through host.
        /// </summary>
        [HarmonyPatch(typeof(FishingRodFish), "CollectFish")]
        [HarmonyPrefix]
        public static bool OnCollectFishPrefix(FishingRodFish __instance, ref ShipItem __result)
        {
            if (!Plugin.IsMultiplayer) return true;
            if (FishingSyncManager.Instance?.IsApplyingRemoteState == true) return true;

            var rod = Traverse.Create(__instance).Field("rod").GetValue<ShipItemFishingRod>();
            if (rod == null) return true;

            // Get fish prefab index before clearing
            var currentFish = __instance.currentFish;
            int prefabIndex = GetFishPrefabIndex(currentFish);

            // Notify manager (will route to host if guest). On the HOST this synchronously runs
            // ProcessFishCollection (spawns the fish item, rolls the hook, clears the fish visuals, broadcasts the
            // response, and ItemSyncs the spawn).
            FishingSyncManager.Instance?.OnLocalFishCollect(rod, prefabIndex);

            // B9: block vanilla FishingRodFish.CollectFish on BOTH roles. Previously the host returned true, so
            // vanilla ran Object.Instantiate(currentFish) AFTER ProcessFishCollection had already spawned the item
            // AND nulled currentFish -> Instantiate(null) threw on every host catch (aborting the rest of vanilla
            // collect). The guest clears visuals here because its OnLocalFishCollect only SENDS a request; on the
            // host ProcessFishCollection already cleared them.
            if (!Plugin.IsHost)
            {
                __instance.GetComponent<MeshFilter>().sharedMesh = null;
                __instance.GetComponent<Renderer>().enabled = false;
                Traverse.Create(__instance).Field("currentFish").SetValue(null);
            }
            __result = null;
            return false;
        }

        private static int GetFishPrefabIndex(GameObject fish)
        {
            if (fish == null || OceanFishes.instance == null) return -1;

            var fishPrefabs = Traverse.Create(OceanFishes.instance).Field("fishPrefabs").GetValue<GameObject[]>();
            if (fishPrefabs == null) return -1;

            for (int i = 0; i < fishPrefabs.Length; i++)
            {
                if (fishPrefabs[i] == fish) return i;
            }

            return -1;
        }
    }
}
