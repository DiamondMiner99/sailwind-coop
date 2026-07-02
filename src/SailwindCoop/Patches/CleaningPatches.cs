using HarmonyLib;
using UnityEngine;
using SailwindCoop.Debug;
using SailwindCoop.Sync;

namespace SailwindCoop.Patches
{
    /// <summary>
    /// Harmony patches for boat cleaning synchronization.
    /// </summary>
    public static class CleaningPatches
    {
        /// <summary>
        /// Intercepts MasterPainter.PaintObject to sync broom cleaning strokes.
        /// </summary>
        [HarmonyPatch(typeof(MasterPainter), "PaintObject")]
        public static class MasterPainterPaintObjectPatch
        {
            [HarmonyPostfix]
            public static void Postfix(CleanableObject paintedObject, Vector2 offset)
            {
                if (!Plugin.IsMultiplayer) return;
                if (CleaningSyncManager.Instance?.IsApplyingRemoteState == true) return;
                if (paintedObject == null) return;

                // Find the boat this CleanableObject belongs to
                var boat = paintedObject.GetComponentInParent<SaveableObject>();
                if (boat == null)
                {
                    // Try getting it directly (CleanableObject might be on same GO as SaveableObject)
                    boat = paintedObject.GetComponent<SaveableObject>();
                }

                if (boat == null)
                {
                    VerboseLogger.CleaningEvent("PaintObject postfix: no boat found for CleanableObject");
                    return;
                }

                // Send the cleaning stroke
                CleaningSyncManager.Instance?.OnLocalCleaningStroke(boat.gameObject.name, offset);
            }
        }

        /// <summary>
        /// Intercepts CleanableObject.CleanFully to sync shipyard hull cleaning.
        /// </summary>
        [HarmonyPatch(typeof(CleanableObject), "CleanFully")]
        public static class CleanableObjectCleanFullyPatch
        {
            [HarmonyPostfix]
            public static void Postfix(CleanableObject __instance)
            {
                if (!Plugin.IsMultiplayer) return;
                if (CleaningSyncManager.Instance?.IsApplyingRemoteState == true) return;
                if (__instance == null) return;

                // Find the boat this CleanableObject belongs to
                var boat = __instance.GetComponentInParent<SaveableObject>();
                if (boat == null)
                {
                    boat = __instance.GetComponent<SaveableObject>();
                }

                if (boat == null)
                {
                    VerboseLogger.CleaningEvent("CleanFully postfix: no boat found for CleanableObject");
                    return;
                }

                // Send the full clean event
                CleaningSyncManager.Instance?.OnLocalCleanFully(boat.gameObject.name);
            }
        }
    }
}
