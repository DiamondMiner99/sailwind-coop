using HarmonyLib;
using SailwindCoop.Sync;

namespace SailwindCoop.Patches
{
    /// <summary>
    /// Harmony patches for chip log (speedometer) synchronization.
    /// </summary>
    [HarmonyPatch]
    public static class ChipLogPatches
    {
        /// <summary>
        /// Broadcast the throw. ThrowRod is an IEnumerator, so this postfix fires at iterator
        /// creation (the StartCoroutine call in vanilla ExtraLateUpdate), before the coroutine
        /// body runs - the same timing the fishing cast precedent relies on. Unlike the fishing
        /// rod there is no charge to capture (the chip log throw is animation-only), so a plain
        /// postfix suffices. Remote replays go through StartCoroutine("ThrowRod") under
        /// IsApplyingRemoteState, so they never re-broadcast.
        /// </summary>
        [HarmonyPatch(typeof(ShipItemChipLog), "ThrowRod")]
        [HarmonyPostfix]
        public static void OnChipLogThrowRod(ShipItemChipLog __instance)
        {
            if (!Plugin.IsMultiplayer) return;
            if (ChipLogSyncManager.Instance?.IsApplyingRemoteState == true) return;

            ChipLogSyncManager.Instance?.OnLocalChipLogThrown(__instance);
        }

        /// <summary>
        /// Stow reset: vanilla OnEnterInventory parks the bobber and resets thrown/line purely
        /// locally, and once the item is out of the stream set nothing would ever tell viewers -
        /// send one authoritative Thrown=false and stop the stream.
        /// </summary>
        [HarmonyPatch(typeof(ShipItemChipLog), "OnEnterInventory")]
        [HarmonyPostfix]
        public static void OnChipLogEnterInventory(ShipItemChipLog __instance)
        {
            if (!Plugin.IsMultiplayer) return;
            if (ChipLogSyncManager.Instance?.IsApplyingRemoteState == true) return;
            if (ItemSyncManager.Instance?.IsApplyingRemoteState == true) return;

            ChipLogSyncManager.Instance?.OnLocalChipLogStowed(__instance);
        }

        /// <summary>
        /// InstantUnthrow (alt-use while thrown) snaps the line back to minLength locally; the 5Hz
        /// stream would catch the thrown=false transition within 0.2s, but sending the reset
        /// immediately avoids a visible lag spike on viewers.
        /// </summary>
        [HarmonyPatch(typeof(ShipItemChipLog), "InstantUnthrow")]
        [HarmonyPostfix]
        public static void OnChipLogInstantUnthrow(ShipItemChipLog __instance)
        {
            if (!Plugin.IsMultiplayer) return;
            if (ChipLogSyncManager.Instance?.IsApplyingRemoteState == true) return;

            ChipLogSyncManager.Instance?.OnLocalChipLogStowed(__instance);
        }
    }
}
