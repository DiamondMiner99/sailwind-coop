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
    /// 0.41 floating items. RandomFloatingItemSpawner sits on the local player and rolls with its own random
    /// numbers as that player travels, so every machine had a different sea, a crewmate's grab of its own find
    /// was refused by a host that had never heard of it, and the host's finds stayed invisible to the crew until
    /// he picked one up. The host rolls for the crew now and sends what it spawns, like the foraging spawner
    /// above; crewmates roll nothing. Coop.HostFloatingLoot off restores the per-machine rolls.
    ///
    /// Every machine, solo included, also remembers what its own spawner made, and a crewmate also remembers
    /// the host's that reach it (ItemSpawnedPacket.FarLod, NetworkSaveData.FarLod and ItemResyncPacket.FloatingLoot
    /// carry membership of this list; not vanilla's farLod, which some food prefabs also have set). Vanilla never saves these and culls them once they are left behind, and the co-op range-cull
    /// guard in ItemPatches.OnDestroyItem uses this list to let that cull happen.
    /// </summary>
    [HarmonyPatch(typeof(RandomFloatingItemSpawner), "Spawn")]
    public static class RandomFloatingItemSpawnerPatch
    {
        private static readonly HashSet<ShipItem> _loot = new HashSet<ShipItem>();
        internal static bool Capturing;
        internal static ShipItem Captured;

        private static bool HostRollsForCrew => Plugin.HostFloatingLootConfig == null || Plugin.HostFloatingLootConfig.Value;

        /// <summary>True for a floating item this machine's own spawner made, or one a crewmate sent.</summary>
        public static bool IsLoot(ShipItem item) => item != null && _loot.Contains(item);

        public static void Forget(ShipItem item)
        {
            if (item != null) _loot.Remove(item);
        }

        /// <summary>
        /// A floating item spawned here for a crewmate's packet. Vanilla sets farLod right after Instantiate and
        /// reads it when ShipItem adds its LOD group, a couple of frames after OnLoad, so setting it in the same
        /// frame as the spawn is in time; without it the copy gets the near-item LOD and only shows up close.
        /// </summary>
        public static void RememberRemote(GameObject instance)
        {
            var item = instance != null ? instance.GetComponent<ShipItem>() : null;
            if (item == null) return;
            item.farLod = true;
            if (_loot.Count > 0) Prune();
            _loot.Add(item);
        }

        /// <summary>Drop entries whose item no longer exists. The live ones stay, they are still loot.</summary>
        public static void Prune() => _loot.RemoveWhere(i => i == null);

        [HarmonyPrefix]
        public static bool Prefix()
        {
            if (Plugin.IsMultiplayer && HostRollsForCrew)
            {
                if (!Plugin.IsHost) return false;
                // A guest is joining: its snapshot is already on the way, and a spawn now could miss it.
                if (ItemSyncManager.Instance?.IgnoreRemoteItemDestruction == true) return false;
            }
            Capturing = true;
            Captured = null;
            return true;
        }

        [HarmonyFinalizer]
        public static void Finalizer(System.Exception __exception)
        {
            if (!Capturing) return;
            Capturing = false;
            var item = Captured;
            Captured = null;
            if (item == null) return;

            if (_loot.Count > 0) Prune();
            _loot.Add(item);

            if (__exception != null || !Plugin.IsMultiplayer || !Plugin.IsHost || !HostRollsForCrew) return;

            // The first roll after a load measures from the world origin and lands kilometers away, where
            // vanilla culls it within seconds. Leave that one local and unnumbered so its cull stays local too.
            var cam = Camera.main;
            if (cam == null || Vector3.Distance(cam.transform.position, item.transform.position) > 800f)
            {
                VerboseLogger.Log("ITEM", "SPAWN", $"Floating item {item.name} spawned out of range; not sent to the crew");
                return;
            }

            // Never registered to save, as in vanilla, or a load would bring it back as a saved item.
            var prefab = item.GetComponent<SaveablePrefab>();
            if (prefab == null) return;
            if (prefab.instanceId == 0) prefab.instanceId = Random.Range(1, int.MaxValue);

            ItemSyncManager.Instance?.OnLocalItemSpawned(item);
            VerboseLogger.Log("ITEM", "SPAWN", $"Floating item {item.name} spawned for the crew, id={prefab.instanceId}");
        }
    }

    /// <summary>
    /// Hands RandomFloatingItemSpawnerPatch the item its Spawn instantiates. ShipItem.Awake runs inside that
    /// Instantiate call and is the only Awake in the ShipItem hierarchy, and the new item is a scene root.
    /// </summary>
    [HarmonyPatch(typeof(ShipItem), "Awake")]
    public static class FloatingLootCapturePatch
    {
        [HarmonyPostfix]
        public static void Postfix(ShipItem __instance)
        {
            if (!RandomFloatingItemSpawnerPatch.Capturing || RandomFloatingItemSpawnerPatch.Captured != null) return;
            if (__instance.transform.parent != null) return;
            RandomFloatingItemSpawnerPatch.Captured = __instance;
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
