using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using SailwindCoop.Debug;
using SailwindCoop.Sync;

namespace SailwindCoop.Patches
{
    /// <summary>
    /// A guest's boats and houses move their items in and out of BoatLocalItems' cache on the guest's own
    /// distance check, and that cache does not fit a crew in two ways, both handled here. Solo and host keep
    /// vanilla behavior.
    /// </summary>
    [HarmonyPatch(typeof(BoatLocalItems), "Update")]
    public static class BoatLocalItemsGuestUpdatePatch
    {
        [HarmonyPrefix]
        public static bool Prefix(BoatLocalItems __instance, BoatHorizon ___boatHorizon, bool ___itemsLoaded,
            List<SavePrefabData> ___cachedItems)
        {
            if (!Plugin.IsMultiplayer || Plugin.IsHost || ___boatHorizon == null) return true;

            bool close = ___boatHorizon.closeToPlayer;

            // JOIN. The boat can still read as out of range from the joiner's saved position right after the host's
            // items were spawned on it. Vanilla would cache them and flag them for destruction, the join's spawn
            // guard would then throw that cache away, and everything on board would be gone for this whole session
            // and for the next one if the save still starts out of range. Waits for the real distance, which is
            // re-measured as the join ends (BoatUtility.RefreshHorizonsAfterJoin).
            if (BoatSyncManager.IsJoinInProgress)
            {
                bool wouldCache = !close && ___itemsLoaded && (___cachedItems == null || ___cachedItems.Count == 0);
                if (wouldCache && (Plugin.JoinCacheGuardConfig == null || Plugin.JoinCacheGuardConfig.Value))
                    return false;
                return true;
            }

            // STREAM-IN. Vanilla only spawns a cache on a kinematic boat, and a guest's boats are never kinematic, so
            // the items never came back and vanilla logged an error every frame. The cache is also out of date by
            // now, because crew changes to items that are not live here have nowhere to land. The host has the real
            // list, so ask for it. Houses have no rigidbody and did spawn; they take the same path for the same
            // staleness, and for the full item state.
            if (close && !___itemsLoaded && ___cachedItems != null && IslandLoaded(__instance))
            {
                if (Plugin.HostItemsOnStreamInConfig != null && !Plugin.HostItemsOnStreamInConfig.Value) return true;

                var owner = __instance.GetComponent<SaveableObject>();
                if (owner == null) return true;

                Traverse.Create(__instance).Field("cachedItems").SetValue(null);
                __instance.SetItemsLoaded(true);
                Plugin.Log.LogInfo($"[ITEM] {owner.gameObject.name} streamed in: dropped {___cachedItems.Count} cached item(s), asking the host");
                ItemSyncManager.Instance?.RequestBoatItems(owner);
                return false;
            }

            return true;
        }

        // Vanilla's private IslandLoaded: a house waits for its island scene, or its items fall through the floor.
        private static bool IslandLoaded(BoatLocalItems items)
        {
            return items.houseParentIsland <= 0 || SceneManager.GetSceneByBuildIndex(items.houseParentIsland).isLoaded;
        }
    }

    /// <summary>
    /// 0.40 foraging. A spawner with a randomizer picks its own random spot on each machine, so the two copies of
    /// one mushroom land in different places and a pickup cannot match them by position: the other player's copy
    /// stays behind as a duplicate. The host spawns these for the crew. Spawners without a randomizer keep their
    /// existing per-machine behavior, where the fixed position lets a pickup correlate.
    /// </summary>
    [HarmonyPatch(typeof(WorldItemSpawner), "SpawnItem")]
    public static class WorldItemSpawnerRandomizedPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(WorldItemSpawnerRangeRandomizer ___randomizer)
        {
            return !(Plugin.IsMultiplayer && !Plugin.IsHost && ___randomizer != null);
        }

        [HarmonyPostfix]
        public static void Postfix(WorldItemSpawnerRangeRandomizer ___randomizer, ShipItem ___item)
        {
            if (!Plugin.IsMultiplayer || !Plugin.IsHost || ___randomizer == null || ___item == null) return;

            // Vanilla only registers a spawned item, which is what gives it an id, once someone picks it up. It
            // has to stay unregistered or a save would hold it and the spawner would make another after a load.
            var prefab = ___item.GetComponent<SaveablePrefab>();
            if (prefab == null) return;
            if (prefab.instanceId == 0) prefab.instanceId = Random.Range(1, int.MaxValue);

            ItemSyncManager.Instance?.OnLocalItemSpawned(___item);
            VerboseLogger.Log("ITEM", "SPAWN", $"Foraged item {___item.name} spawned for the crew, id={prefab.instanceId}");
        }
    }

    /// <summary>
    /// 0.40 tobacco pack. Cutting one makes the pieces with a bare Instantiate and then destroys the pack, and the
    /// destroy does sync. Everyone lost the pack and only the cutter got the tobacco. Same loop as vanilla, plus a
    /// broadcast.
    /// </summary>
    [HarmonyPatch(typeof(ShipItemTobaccoPack), "CutPack")]
    public static class ShipItemTobaccoPackCutPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(ShipItemTobaccoPack __instance)
        {
            if (!Plugin.IsMultiplayer) return true;

            var manager = ItemSyncManager.Instance;
            if (manager == null || manager.IsApplyingRemoteState) return true;

            var pieces = new List<ShipItem>();
            float offset = -0.09f;
            for (int i = 0; i < __instance.tobaccoAmount; i++)
            {
                GameObject piece = Object.Instantiate(__instance.tobaccoPrefab);
                piece.transform.SetPositionAndRotation(__instance.transform.position, __instance.transform.rotation);
                var ship = piece.GetComponent<ShipItem>();
                ship.sold = true;
                piece.transform.Translate(Vector3.right * (offset * 1.6f), Space.Self);
                piece.transform.Rotate(Vector3.up, Random.Range(0f, 360f), Space.Self);
                offset += 0.06f;

                // The broadcast needs an id, and this is the call that assigns one.
                piece.GetComponent<SaveablePrefab>()?.RegisterToSave();
                pieces.Add(ship);
            }

            manager.StartCoroutine(BroadcastWhenLatched(manager, pieces));
            __instance.DestroyItem();
            return false;
        }

        // Wait for the pieces to latch onto the boat they landed on. Sent earlier they go out in world coordinates,
        // and a crewmate's copy of a moving boat is not in the same spot, so they could land beside it.
        private static IEnumerator BroadcastWhenLatched(ItemSyncManager manager, List<ShipItem> pieces)
        {
            yield return new WaitForSecondsRealtime(0.3f);
            foreach (var piece in pieces)
            {
                if (piece != null) manager.OnLocalItemSpawned(piece);
            }
        }
    }
}
