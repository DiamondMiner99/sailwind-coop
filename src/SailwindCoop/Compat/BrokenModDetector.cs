using System.Collections.Generic;
using HarmonyLib;

namespace SailwindCoop.Compat
{
    /// <summary>
    /// (v0.2.39) Find mods that started patching the game and then died, and say so.
    ///
    /// WHY THIS IS CO-OP'S PROBLEM. It looks like someone else's, and it is - but the failure lands here.
    /// When a BepInEx plugin throws in its Awake, the chainloader REMOVES it from Chainloader.PluginInfos
    /// before logging the error. Everything that asks "what mods is this player running" therefore gets an
    /// answer with the broken mod missing from it, including this mod's own report of what differs between
    /// two crew members. A player with a crashed mod and a player without it look identical to us, and the
    /// crashed one is the one whose game is behaving strangely. When that happens during co-op, co-op gets
    /// the blame - which is exactly what happened with Anchor Improvements, a mod that is broken on Sailwind
    /// 0.38 whether or not this mod is installed at all.
    ///
    /// THE DETECTOR. Harmony patches applied before the throw are never rolled back: the chainloader's catch
    /// handler removes the plugin and logs, and does not unpatch. That leaves a contradiction only a crashed
    /// mod can produce - live patches owned by an id that no loaded plugin claims. Enumerating patch owners
    /// rather than checking a list of known mods means this finds tomorrow's broken mod too, not just the one
    /// that prompted it.
    ///
    /// It only ever reports. Nothing is unpatched, disabled or refused on the strength of this: a partially
    /// patched mod is the player's to resolve, and a heuristic that can produce a false positive must never
    /// be allowed to break someone's game.
    /// </summary>
    public static class BrokenModDetector
    {
        private static bool _run;

        /// <summary>Known ids that legitimately own patches without being a plugin in their own right.</summary>
        private static readonly HashSet<string> _expected = new HashSet<string>
        {
            Plugin.PluginGUID,             // us
            "com.bepis.bepinex.chainloader",
            "com.bepinex.chainloader",
            "io.bepinex.chainloader",
        };

        /// <summary>Friendly names for ids we happen to recognize, so the message can name the mod rather
        /// than a reverse-DNS string. Anything unlisted is reported by its id, which is still actionable.</summary>
        private static readonly Dictionary<string, string> _known = new Dictionary<string, string>
        {
            { "com.nandbrew.anchorimprovements", "Anchor Improvements" },
            { "com.nandbrew.shipyardexpansion", "Shipyard Expansion" },
            { "com.nandbrew.sailcollisionfix", "Sail Collision Fix" },
            { "com.nandbrew.nandtweaks", "NAND Tweaks" },
            { "com.nandbrew.towableboats", "Towable Boats" },
        };

        /// <summary>
        /// Run once. Safe to call repeatedly; only the first call does anything. Must run after the
        /// chainloader has finished, which is why it is driven from the game loop rather than from Awake.
        /// </summary>
        public static void RunOnce()
        {
            if (_run) return;
            _run = true;

            try
            {
                // Every id that currently owns a live patch.
                var owners = new HashSet<string>();
                foreach (var method in Harmony.GetAllPatchedMethods())
                {
                    var info = Harmony.GetPatchInfo(method);
                    if (info == null) continue;
                    foreach (var id in info.Owners) owners.Add(id);
                }

                // Every id a LOADED plugin could plausibly be patching under. A mod is free to use a harmony
                // id that is not its GUID, so match loosely in both directions before calling anything
                // broken - a false accusation about someone else's mod is worse than staying quiet.
                var loaded = new List<string>();
                foreach (var kv in BepInEx.Bootstrap.Chainloader.PluginInfos)
                {
                    loaded.Add(kv.Key);
                    var meta = kv.Value != null ? kv.Value.Metadata : null;
                    if (meta != null && !string.IsNullOrEmpty(meta.Name)) loaded.Add(meta.Name);
                }

                var orphans = new List<string>();
                foreach (var owner in owners)
                {
                    if (string.IsNullOrEmpty(owner) || _expected.Contains(owner)) continue;
                    if (IsClaimedBy(owner, loaded)) continue;
                    orphans.Add(owner);
                }

                if (orphans.Count == 0) return;

                orphans.Sort(System.StringComparer.OrdinalIgnoreCase);
                foreach (var owner in orphans)
                {
                    string name;
                    if (!_known.TryGetValue(owner, out name)) name = owner;
                    Plugin.Log.LogWarning($"[MODS] '{name}' patched the game and then failed to finish loading. " +
                        "Some of its changes are live and the rest are not, which can cause odd behavior that " +
                        "has nothing to do with co-op. Check the lines above for its own error, and update or " +
                        "remove that mod. It is also invisible to the crew mod-difference report, because " +
                        "BepInEx drops a plugin that fails to start.");
                }

                // One toast, naming at most a couple, so the player knows to go and look.
                string first;
                if (!_known.TryGetValue(orphans[0], out first)) first = orphans[0];
                string msg = orphans.Count == 1
                    ? $"{first} failed to load properly. Part of it is running and part is not, which can cause " +
                      "problems that look like co-op bugs. See the log."
                    : $"{first} and {orphans.Count - 1} other mod(s) failed to load properly. See the log.";
                Plugin.Notify(msg, 12f);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("[MODS] could not check for half-loaded mods: " + e.Message);
            }
        }

        /// <summary>True when some loaded plugin plausibly owns this harmony id.</summary>
        private static bool IsClaimedBy(string owner, List<string> loaded)
        {
            foreach (var l in loaded)
            {
                if (string.IsNullOrEmpty(l)) continue;
                if (string.Equals(owner, l, System.StringComparison.OrdinalIgnoreCase)) return true;
                if (owner.IndexOf(l, System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (l.IndexOf(owner, System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }
    }
}
