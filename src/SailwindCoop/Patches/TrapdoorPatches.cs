using HarmonyLib;
using SailwindCoop.Sync;

namespace SailwindCoop.Patches
{
    /// <summary>
    /// (v0.2.32) Local trapdoor events -> TrapdoorSyncManager. Prefix captures IsOpen() so the
    /// postfix can tell a real toggle from an inMotion no-op (vanilla OnActivate returns silently
    /// while the open/close coroutine runs, GPButtonTrapdoor decomp). The no-arg overload is the one
    /// GPButtonTrapdoor overrides; the coroutine flips `open` synchronously before its first yield,
    /// so the postfix already reads the NEW state.
    /// </summary>
    [HarmonyPatch(typeof(GPButtonTrapdoor), "OnActivate", new System.Type[0])]
    public static class TrapdoorOnActivatePatch
    {
        [HarmonyPrefix]
        // __runOriginal: same P1 doctrine as DamagePatches - if any other mod's prefix skips
        // OnActivate, our capture must still run or every skipped CLOSE would read as a no-op
        // and never sync. No known mod skips it today; this is the cheap insurance.
        public static void Prefix(GPButtonTrapdoor __instance, out bool __state, bool __runOriginal)
        {
            __state = __instance != null && __instance.IsOpen();
        }

        [HarmonyPostfix]
        public static void Postfix(GPButtonTrapdoor __instance, bool __state)
        {
            if (!Plugin.IsMultiplayer || __instance == null) return;
            bool stateChanged = __instance.IsOpen() != __state;
            TrapdoorSyncManager.Instance?.OnLocalTrapdoorActivated(__instance, stateChanged);
        }
    }
}
