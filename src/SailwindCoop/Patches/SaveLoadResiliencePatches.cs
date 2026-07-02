using System;
using HarmonyLib;

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
}
