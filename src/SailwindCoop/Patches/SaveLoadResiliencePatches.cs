using System;
using HarmonyLib;
using UnityEngine;

namespace SailwindCoop.Patches
{
    /// <summary>
    /// CO-OP JOIN RESILIENCE. SaveLoadManager.LoadGame loads the world by looping over every saved object and
    /// calling <c>SaveableObject.Load(savedObject)</c>. That loop has NO per-object try/catch, so a SINGLE
    /// corrupt saveable that throws (observed: SaveableBoatCustomization.LoadData -> ArgumentOutOfRangeException
    /// on a boat whose customization indices are out of range for the current game version) aborts the entire
    /// load. The vanilla load coroutine never reaches its "GameState.playing = true", so a title-screen joiner
    /// waits the full timeout, sees "Load timed out", and never connects - which looked exactly like a network
    /// failure but was a bad save.
    ///
    /// Fix: a Finalizer on SaveableObject.Load that swallows the exception ONLY while
    /// <see cref="TitleJoinManager.SuppressLoadErrors"/> is set (i.e. only during a co-op phantom load). The
    /// LoadGame loop then continues to the next object, the load completes, playing goes true, and the join
    /// proceeds. The one skipped object loads with default state - acceptable because the guest is teleported
    /// onto the HOST's boat on join, so their own phantom world's contents are purely cosmetic. Solo and host
    /// play never set the flag, so they keep vanilla crash-on-corruption behavior unchanged.
    /// </summary>
    [HarmonyPatch(typeof(SaveableObject), "Load", new Type[] { typeof(SaveObjectData) })]
    public static class SaveLoadResiliencePatches
    {
        /// <summary>
        /// True if either resilience finalizer had to swallow an exception during the most recent co-op
        /// suppressed load (i.e. the phantom save contains corrupt/removed-mod saveables). Reset at the
        /// start of each co-op suppressed LoadGame; cleared by CoopSave.TrySelfHealSave once a clean
        /// rewrite of the phantom has been committed.
        /// </summary>
        public static bool CorruptSkipsOccurredThisLoad;

        static Exception Finalizer(Exception __exception, SaveableObject __instance)
        {
            if (__exception != null && TitleJoinManager.SuppressLoadErrors)
            {
                CorruptSkipsOccurredThisLoad = true;
                string who = __instance != null ? __instance.name : "<null>";
                Plugin.Log.LogWarning($"[TitleJoin] co-op load: skipped a corrupt saveable '{who}' so the load can finish " +
                                      $"({__exception.GetType().Name}: {__exception.Message})");
                return null; // suppress -> SaveLoadManager.LoadGame's loop continues to the next object
            }
            return __exception; // not a co-op load: rethrow (vanilla behavior preserved)
        }
    }

    /// <summary>
    /// ABSURD-MOORING-ROPE HEAL (vanilla latent bug, made routine by the co-op phantom quit-save).
    /// Vanilla saves every object at <c>transform.position - outCurrentOffset</c> (SaveLoadManager.cs ~240).
    /// A rope moored via vanilla MoorTo is PARENTED TO THE DOCK CLEAT, and IslandHorizon.ApplyNewHorizon
    /// sinks far island roots by -(d^2/(2*515662))-2*camY every LateUpdate (uncapped, ~-10km at 100km) -
    /// so a rope moored at a far island gets its VIEW-DEPENDENT render Y persisted. On load, a was-moored
    /// rope (extraSetting=true) is restored at that raw position with parent=null and NO spring
    /// (SaveableObject.Load ~143-148): a loose physical rope object kilometers away / on the seabed, with
    /// the boat's LineRenderer stretching to it.
    ///
    /// Heal: a POSTFIX SWEEP after SaveLoadManager.LoadGame completes (not a per-object Load postfix - the
    /// load loop's object order is registration order, so a rope can load BEFORE its boat and a per-object
    /// distance check against the boat's pre-load position would false-positive a legit moored rope).
    /// Detects a detached (parent==null, the was-moored restore signature), unmoored rope whose distance
    /// from its own boat rigidbody exceeds 50m (vanilla max rope length is sqrt(maxLength=900)=30m, decomp
    /// PickupableBoatMooringRope.cs Awake:83, so a legitimately restored moored rope is always well inside
    /// 50m) or whose Y is below -50 (far under any sea level). On detection: restow on the hanger and clear
    /// the was-moored flag. A legit near-dock restore is untouched (it sits at the cleat at sea level and
    /// re-moors via the vanilla OnTriggerEnter path). Runs for BOTH the co-op phantom load and normal solo
    /// saves (no SuppressLoadErrors gate) since the underlying bug is vanilla's.
    /// </summary>
    [HarmonyPatch(typeof(SaveLoadManager), "LoadGame", new Type[] { typeof(int) })]
    public static class MooringRopeLoadHealPatch
    {
        static void Postfix()
        {
            try
            {
                foreach (var rope in UnityEngine.Object.FindObjectsOfType<PickupableBoatMooringRope>())
                {
                    if (rope == null || rope.IsMoored()) continue;
                    if (rope.transform.parent != null) continue; // only the was-moored detached restore

                    var boatRb = rope.GetBoatRigidbody();
                    if (boatRb == null) continue;

                    float distFromBoat = Vector3.Distance(rope.transform.position, boatRb.transform.position);
                    float ropeY = rope.transform.position.y;
                    if (distFromBoat <= 50f && ropeY >= -50f) continue; // sane restore - leave for vanilla re-moor

                    // Restow on the hanger: re-parent to the private initialParent (captured in the rope's
                    // Awake) before ResetRopePos() restores the hanger-local pos/rot. No spring exists at
                    // load time (IsMoored() checked above), so no Unmoor() is needed.
                    var initialParent = Traverse.Create(rope).Field("initialParent").GetValue<UnityEngine.Transform>();
                    if (initialParent != null) rope.transform.parent = initialParent;
                    rope.ResetRopePos();

                    // Clear the "was moored" persistence flag so the heal sticks across the next save.
                    var saveable = rope.GetComponent<SaveableObject>();
                    if (saveable != null) saveable.extraSetting = false;

                    Plugin.Log.LogInfo($"[SaveHeal] mooring rope '{rope.name}' restored at an absurd position " +
                                       $"(distFromBoat={distFromBoat:F0}m, y={ropeY:F0}); restowed on hanger and cleared " +
                                       $"was-moored flag (vanilla far-island horizon-sink save bug)");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[SaveHeal] mooring rope sanity sweep failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Phantom-load top-level fault guard. The per-object Finalizer above only catches a corrupt saveable whose
    /// <c>SaveableObject.Load()</c> THROWS. But <c>SaveLoadManager.LoadGame</c> can hard-fault OUTSIDE any
    /// per-object Load() - the deserialize loop is <c>currentObjects[savedObject.sceneIndex].Load(savedObject)</c>
    /// (in SaveLoadManager.LoadGame's deserialize loop), and when a removed mod (e.g. Shipyard Expansion) leaves a saved object
    /// whose <c>sceneIndex</c> no longer maps to a live <c>currentObjects[..]</c> entry, indexing/dereferencing it
    /// NREs BEFORE Load() is ever entered, so the per-object Finalizer never runs. That NRE aborted the entire
    /// LoadGame, so the vanilla LoadGameAnimation coroutine never set <c>GameState.playing=true</c>, and the
    /// title-join waited out its 45s timeout and never connected (observed when a guest's save contained objects
    /// from a removed mod). <c>GameState.playing</c> is set by the COROUTINE after LoadGame returns, so swallowing the
    /// top-level fault here (ONLY during a co-op phantom load) lets the coroutine continue, playing goes true, and
    /// the join completes. The guest is then teleported onto the HOST's boat and receives the host's authoritative
    /// world state, so a partially-loaded phantom is just scaffolding the sync replaces. Solo/host never set the
    /// flag, so they keep vanilla crash-on-corruption behavior. (Reusing a still-poisoned coop_session.save is now
    /// harmless - the load survives - and re-seeding it from the same corrupt solo save wouldn't help anyway.)
    /// </summary>
    [HarmonyPatch(typeof(SaveLoadManager), "LoadGame", new Type[] { typeof(int) })]
    public static class SaveLoadGameResiliencePatch
    {
        // Load start: reset the corrupt-skip flag so it reflects only THIS co-op suppressed load.
        static void Prefix()
        {
            if (TitleJoinManager.SuppressLoadErrors)
                SaveLoadResiliencePatches.CorruptSkipsOccurredThisLoad = false;
        }

        static Exception Finalizer(Exception __exception)
        {
            if (__exception != null && TitleJoinManager.SuppressLoadErrors)
            {
                SaveLoadResiliencePatches.CorruptSkipsOccurredThisLoad = true;
                Plugin.Log.LogWarning($"[TitleJoin] co-op load: SaveLoadManager.LoadGame faulted after the corrupt-saveable skips " +
                                      $"({__exception.GetType().Name}: {__exception.Message}); continuing the join anyway - " +
                                      $"the host's world state will reconcile the guest (likely leftover data from a removed mod).");
                return null; // swallow -> LoadGameAnimation continues -> GameState.playing=true -> JoinLobby fires
            }
            return __exception; // not a co-op load: rethrow (vanilla behavior preserved)
        }
    }

    /// <summary>
    /// DUPLICATE-DEFAULT-ITEMS GUARD (issue #3, belt-and-suspenders for the ClearBoatItems cache flush).
    /// Vanilla BoatLocalItems lazily instantiates a boat's cached default items (map/compass/lantern/...) via
    /// SpawnCachedItems, gated only on player proximity + kinematic state. During a guest join the teleport
    /// repositions every boat (driving them out of / back into range), which can RE-CACHE and RE-SPAWN the
    /// joiner's phantom-save copies AFTER ClearBoatItems + the host's SpawnItems have already run - producing
    /// duplicate items the host can't authorize. BoatStateApplicator.ClearBoatItems flushes the cache, but the
    /// re-cache can race the teleport window; this blocks the spawn outright while a join is in progress. Once
    /// the join completes the flag clears and the HOST's authoritative items stream normally (under host ids).
    /// </summary>
    [HarmonyPatch(typeof(BoatLocalItems), "SpawnCachedItems")]
    public static class BoatLocalItemsJoinSpawnGuard
    {
        static bool Prefix(BoatLocalItems __instance)
        {
            if (!Sync.BoatSyncManager.IsJoinInProgress) return true; // outside the join window: vanilla spawn.

            // During the join: skip the spawn (false = don't run original). The vanilla caller sets
            // itemsLoaded=true right after, so ALSO null cachedItems here to keep BoatLocalItems' invariant
            // (SpawnCachedItems is the only vanilla site that nulls it) - otherwise the boat is stuck in a
            // never-vanilla state (itemsLoaded=true && cachedItems!=null) where neither Update branch fires
            // again, which could strand a secondary boat's host items that cached out->in during the join.
            try { HarmonyLib.Traverse.Create(__instance).Field("cachedItems").SetValue(null); } catch { }
            return false;
        }
    }

    /// <summary>
    /// (v0.2.25) UNVISITED-BOAT DEFAULT ITEMS, root fix for the ClearBoatItems flush data-loss.
    /// A boat the crew hasn't sailed near keeps its default items (map/compass/lantern/manual/...)
    /// CACHED in BoatLocalItems; the join-time ClearBoatItems flush wipes the guest's copies, and the
    /// host's join snapshot only carries LIVE ShipItems - so when the HOST later sails such a boat into
    /// range, vanilla SpawnCachedItems instantiates its default items host-side only and the guests
    /// never see them. Fix: host-only Postfix that broadcasts every item the vanilla call just
    /// instantiated via the existing item-spawn path (OnLocalItemSpawned = the ShipItem.Sell backfill
    /// route; receivers dedup by id, so re-broadcast is a safe no-op). The item set is captured in the
    /// Prefix from the boat's cachedItems list (the exact SavePrefabData batch about to be spawned;
    /// SpawnCachedItems nulls it before returning) and resolved to live ShipItems by instanceId after.
    /// Guards: host only, session active, not during join-state application (the JoinSpawnGuard above
    /// skips the vanilla spawn then anyway, and OnLocalItemSpawned's IsApplyingRemoteState check
    /// backstops it). The guest-side join flush is deliberately unchanged.
    /// </summary>
    [HarmonyPatch(typeof(BoatLocalItems), "SpawnCachedItems")]
    public static class BoatLocalItemsHostSpawnBroadcast
    {
        static void Prefix(BoatLocalItems __instance, out System.Collections.Generic.HashSet<int> __state)
        {
            __state = null;
            if (!Plugin.IsMultiplayer || !Plugin.IsHost) return;
            if (Sync.BoatSyncManager.IsJoinInProgress) return; // vanilla spawn is skipped by the guard above
            var mgr = Sync.ItemSyncManager.Instance;
            if (mgr == null || mgr.IsApplyingRemoteState) return;

            var cached = __instance.GetCachedItems();
            if (cached == null || cached.Count == 0) return;
            __state = new System.Collections.Generic.HashSet<int>();
            foreach (var data in cached)
                if (data != null && data.instanceId > 0) __state.Add(data.instanceId);
        }

        static void Postfix(BoatLocalItems __instance, System.Collections.Generic.HashSet<int> __state)
        {
            if (__state == null || __state.Count == 0) return;
            var mgr = Sync.ItemSyncManager.Instance;
            if (mgr == null) return;

            // Single scene scan; resolve the just-instantiated batch by the saved instanceIds
            // (SaveablePrefab.Load re-applies data.instanceId, so the ids survive the spawn).
            int sent = 0;
            foreach (var item in UnityEngine.Object.FindObjectsOfType<ShipItem>())
            {
                var prefab = item != null ? item.GetComponent<SaveablePrefab>() : null;
                if (prefab == null || !__state.Contains(prefab.instanceId)) continue;
                mgr.OnLocalItemSpawned(item); // broadcasts ItemSpawned + registers + marks synced per peer
                sent++;
            }
            if (sent > 0)
                Plugin.Log.LogInfo($"[ITEM] Broadcast {sent} default boat item(s) SpawnCachedItems just instantiated on {__instance.gameObject.name} (unvisited-boat root fix)");
        }
    }
}
