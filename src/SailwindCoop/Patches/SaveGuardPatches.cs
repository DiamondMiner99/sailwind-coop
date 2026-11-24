using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SailwindCoop.Patches
{
    /// <summary>
    /// SAVE GUARD (vanilla latent bug). SaveLoadManager.DoSaveGame sets its private <c>busy</c> flag on entry and
    /// only clears it on its last line, with no try/finally. Any exception partway through the save leaves
    /// <c>busy</c> set for the rest of the process, and SaveGame refuses every later save on it: autosave, the bed
    /// save and the quit save all no-op with one "not ready to save" warning and no icon. A crew reported the last
    /// hour or more of two sessions unsaved, which fits this exactly (2026-11-15). A host-side audit of co-op found
    /// no path that throws there, so the likely sources are another mod's SaveModData hook or a file the backup
    /// rotation could not move, but the guard does not depend on knowing which.
    ///
    /// Three parts, all on solo, host and guest alike since the bug is vanilla's:
    ///  1. A finalizer on the DoSaveGame coroutine logs the full exception (so the next occurrence names its cause
    ///     in LogOutput.log) and clears <c>busy</c> so saving resumes on the next autosave or bed.
    ///  2. A watchdog in front of SaveGame clears a <c>busy</c> that has been set for 30 real seconds. A healthy
    ///     save finishes in one frame; this catches a save coroutine that was stopped rather than thrown.
    ///  3. Before each save, destroyed items are dropped from the save list and items filed under an object that
    ///     no longer exists are saved at their world position. Both would otherwise throw in PrepareSaveData.
    /// The exception logging always runs. <see cref="Plugin.SaveGuardConfig"/> turns off everything else.
    /// </summary>
    public static class SaveGuard
    {
        internal const float WedgeSeconds = 30f;

        private static readonly AccessTools.FieldRef<SaveLoadManager, bool> BusyRef =
            AccessTools.FieldRefAccess<SaveLoadManager, bool>("busy");

        // Real time the in-flight save started (or was first seen in flight); -1 when no save is running.
        private static float _saveStartedAt = -1f;

        internal static bool Enabled => Plugin.SaveGuardConfig == null || Plugin.SaveGuardConfig.Value;

        /// <summary>Clears <c>busy</c> if a save has held it for longer than any real save takes. Safe to call any time.</summary>
        public static void ClearIfWedged(SaveLoadManager slm)
        {
            if (!Enabled || slm == null) return;
            if (!BusyRef(slm)) { _saveStartedAt = -1f; return; }

            float now = Time.realtimeSinceStartup;
            if (_saveStartedAt < 0f) { _saveStartedAt = now; return; } // first time seen busy: time it from here
            if (now - _saveStartedAt < WedgeSeconds) return;

            Plugin.Log.LogError($"[SaveGuard] The save system has been stuck busy for {now - _saveStartedAt:0}s because an " +
                                "earlier save stopped partway. Clearing it so saving resumes.");
            BusyRef(slm) = false;
            _saveStartedAt = -1f;
        }

        internal static void NoteSaveStarted(SaveLoadManager slm)
        {
            if (slm != null && BusyRef(slm) && _saveStartedAt < 0f) _saveStartedAt = Time.realtimeSinceStartup;
        }

        internal static void NoteSaveEnded(bool clearBusy)
        {
            _saveStartedAt = -1f;
            var slm = SaveLoadManager.instance;
            if (clearBusy && slm != null) BusyRef(slm) = false;
        }

        /// <summary>Removes the two kinds of save-list entry that make PrepareSaveData throw.</summary>
        internal static void ScrubSaveList(SaveLoadManager slm)
        {
            if (!Enabled || slm == null) return;
            var prefabs = slm.GetCurrentPrefabs();
            if (prefabs == null) return;

            // Unity null: the item was destroyed without SaveablePrefab.Unregister.
            int dead = prefabs.RemoveAll(p => p == null);
            if (dead > 0)
                Plugin.Log.LogError($"[SaveGuard] Removed {dead} destroyed item(s) from the save list. Something destroyed " +
                                    "them without unregistering, and the save would have failed on them.");

            var objects = slm.GetCurrentObjects();
            foreach (var p in prefabs)
            {
                int parent = p.GetParentObject();
                if (parent <= 0) continue; // world position or a vanilla cache flag
                bool exists = objects != null && parent < objects.Length && objects[parent] != null;
                if (exists) continue;
                Plugin.Log.LogError($"[SaveGuard] {p.name} (id={p.instanceId}) was filed under object {parent}, which does " +
                                    "not exist. Saving it at its world position instead.");
                p.SetParentObject(-1);
            }
        }
    }

    [HarmonyPatch(typeof(SaveLoadManager), nameof(SaveLoadManager.SaveGame), new Type[] { typeof(bool) })]
    public static class SaveGuardSaveGamePatch
    {
        static void Prefix(SaveLoadManager __instance)
        {
            try
            {
                SaveGuard.ClearIfWedged(__instance);
                SaveGuard.ScrubSaveList(__instance);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[SaveGuard] Pre-save check failed, saving anyway: {e}");
            }
        }

        static void Postfix(SaveLoadManager __instance)
        {
            try { SaveGuard.NoteSaveStarted(__instance); }
            catch (Exception e) { Plugin.Log.LogWarning($"[SaveGuard] Could not note the save start: {e.Message}"); }
        }
    }

    [HarmonyPatch]
    public static class SaveGuardDoSaveGamePatch
    {
        private static MethodBase _target;

        static bool Prepare()
        {
            var method = AccessTools.Method(typeof(SaveLoadManager), "DoSaveGame");
            _target = method != null ? AccessTools.EnumeratorMoveNext(method) : null;
            if (_target == null) Plugin.Log.LogWarning("[SaveGuard] SaveLoadManager.DoSaveGame not found; save failures will not be caught");
            return _target != null;
        }

        static MethodBase TargetMethod() => _target;

        static Exception Finalizer(Exception __exception, bool __result)
        {
            if (__exception == null)
            {
                if (!__result) SaveGuard.NoteSaveEnded(clearBusy: false); // finished normally; vanilla cleared busy
                return null;
            }

            Plugin.Log.LogError($"[SaveGuard] Saving failed and this save was NOT written. Slot {SaveSlots.currentSlot}, " +
                                $"day {GameState.day}.\n{__exception}");
            if (!SaveGuard.Enabled) return __exception; // vanilla behavior: busy stays set

            SaveGuard.NoteSaveEnded(clearBusy: true);
            return null; // MoveNext returns false, the coroutine ends, and the next save can run
        }
    }
}
