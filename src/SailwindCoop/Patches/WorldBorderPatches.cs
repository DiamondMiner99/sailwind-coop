using HarmonyLib;
using SailwindCoop.Debug;

namespace SailwindCoop.Patches
{
    /// <summary>
    /// Disables WorldBorder checks on guest.
    /// Guest position is controlled by host via boat sync.
    /// </summary>
    public static class WorldBorderPatches
    {
        [HarmonyPatch(typeof(WorldBorder), "Update")]
        public static class WorldBorderUpdatePatch
        {
            [HarmonyPrefix]
            public static bool Prefix()
            {
                PatchProfiler.Begin("WorldBorder.Update");

                // Skip WorldBorder on guest - host controls world state
                if (Plugin.IsMultiplayer && !Plugin.IsHost)
                {
                    PatchProfiler.End("WorldBorder.Update");
                    return false;
                }

                PatchProfiler.End("WorldBorder.Update");
                return true;
            }
        }
    }
}
