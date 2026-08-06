using System.Collections.Generic;

namespace SailwindCoop.Compat
{
    /// <summary>
    /// (v0.3.0) Translate the mod-gate token's internal names into the words a player can actually find.
    ///
    /// THE BUG THIS FIXES IS A WORDING BUG WITH A MECHANICAL CAUSE. The gate token is built by reflecting
    /// over each third-party mod's ConfigEntry FIELDS, so it carries C# field names: `topsailPatch`,
    /// `addSails`, `saveLoadState`. None of those strings exist in the config file the player opens.
    /// Shipyard Expansion's `topsailPatch` field is bound to a setting displayed as "Link topmasts";
    /// `addSails` is "Add lug sails"; NAND Tweaks' `saveLoadState` is "Save and load ship state". So a
    /// refusal that named `topsailPatch` sent a player to search their configs for a string that is not in
    /// them, which is exactly what happened: a crew spent an evening copying whole config files at each
    /// other because the message named something unfindable and they reasonably assumed the whole file was
    /// the unit of matching.
    ///
    /// Each entry also records whether changing it needs a RESTART, because several of these are read once
    /// at boot. Telling someone to change a setting without telling them to restart produces a second failed
    /// join and the conclusion that the message was wrong.
    /// </summary>
    internal static class SettingLabels
    {
        internal sealed class Setting
        {
            public string Label;        // what the config file actually calls it
            public string Section;      // the [Section] it sits under
            public bool NeedsRestart;   // read once at boot, so an edit does not take effect until relaunch
            public Setting(string label, string section, bool needsRestart)
            {
                Label = label; Section = section; NeedsRestart = needsRestart;
            }
            public override string ToString()
            {
                return "\"" + Label + "\" (" + Section + ")" + (NeedsRestart ? ", then restart the game" : "");
            }
        }

        // Keyed by "<mod key>:<token name or tag>".
        private static readonly Dictionary<string, Setting> _map = new Dictionary<string, Setting>
        {
            // Shipyard Expansion - token carries field names, config shows these.
            // topsailPatch is adopted automatically now, so reaching a refusal on it should be rare; addSails
            // can never be adopted (its sails are built in a plain Unity Start that cannot re-run).
            { "SE:topsailPatch", new Setting("Link topmasts", "Settings", true) },
            { "SE:addSails",     new Setting("Add lug sails", "Settings", true) },
            { "SE:noSailData",   new Setting("skip sail data", "zDebug", true) },

            // Sail Collision Fix - all three are adopted for the session, so a refusal here is a version gap.
            { "SCF:c", new Setting("Ignore sail collision", "Options", false) },
            { "SCF:o", new Setting("Ignore obstructions",   "Options", false) },
            { "SCF:a", new Setting("Ignore angle limits",   "Options", false) },

            // NAND Tweaks - the first three are adopted for the session; the last three are read once at
            // world load and are the ones that genuinely refuse.
            { "NT:b", new Setting("Bailing Tweaks", "---- Water & Bailing ----", false) },
            { "NT:s", new Setting("Drunken Sleep",  "--------- Sleep ---------", false) },
            { "NT:w", new Setting("Wheel centering", "----- Miscellaneous -----", false) },
            { "NT:f", new Setting("Albacore Area",  "----- Miscellaneous -----", true) },
            { "NT:v", new Setting("Save and load ship state", "------- Ship State -------", true) },
            { "NT:d", new Setting("Include doors",  "------- Ship State -------", true) },

            // Towable Boats - not in the reconcile set, so this one always needs a manual edit.
            { "TB:sb", new Setting("Small boats can tow", "Settings", true) },
        };

        /// <summary>The player-visible description of a gated setting, or null if we have no mapping for it
        /// (in which case callers should fall back to the raw name rather than inventing one).</summary>
        internal static Setting Find(string modKey, string tokenName)
        {
            Setting s;
            return _map.TryGetValue(modKey + ":" + tokenName, out s) ? s : null;
        }

        /// <summary>"Link topmasts" (Settings), then restart the game - or the raw token name when unmapped.</summary>
        internal static string Describe(string modKey, string tokenName)
        {
            var s = Find(modKey, tokenName);
            return s != null ? s.ToString() : tokenName;
        }

        /// <summary>
        /// Health flags that are NOT settings: a player cannot match these by editing anything, so a message
        /// that lists them alongside settings sends them looking for a config line that does not exist.
        /// Returns null when the flag is not one of these.
        /// </summary>
        internal static string DescribeHealthFlag(string modKey, string flag)
        {
            switch (modKey + ":" + flag)
            {
                case "SE:noBundles":
                    return "Shipyard Expansion's asset files are missing - reinstall it from the full download";
                case "SE:noSync":
                    return "Shipyard Expansion did not finish loading on one machine - check for plugin load errors in the log";
                case "LEO:noSync":
                    return "HMS Leopard did not finish loading on one machine - check for plugin load errors in the log";
                case "DP:nobundle":
                    return "the Deep Ports asset file is missing on one machine";
                default:
                    // A "?" anywhere means the mod could not read its own settings on that machine.
                    // ANY trailing '?' means that machine could not read the setting. Shipyard Expansion
                    // emits "/<fieldName>?" per unresolvable field rather than one fixed marker, so an exact
                    // match against a few known strings missed the most common shape and diffed it as though
                    // it were a real setting the player could go and change.
                    if (!string.IsNullOrEmpty(flag) && flag[flag.Length - 1] == '?')
                        return "one machine could not read that mod's settings, so they cannot be compared";
                    return null;
            }
        }
    }
}
