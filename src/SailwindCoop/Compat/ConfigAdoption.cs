using System;
using System.Collections.Generic;
using BepInEx.Configuration;

namespace SailwindCoop.Compat
{
    /// <summary>
    /// (v0.2.39) Shared plumbing for the settings reconcile: adopting a peer's config VALUES for the
    /// duration of a session so a crew running identical mods is not refused over a config line.
    ///
    /// THE RULE EVERY CALLER MUST OBEY. A setting may only be adopted if changing it at runtime actually
    /// changes behavior THIS SESSION. "The mod reads .Value inside a method body" is NOT sufficient - what
    /// matters is whether that method still runs after we join. A setting whose only read site is reached
    /// from a world-load path is INERT once we are in a session, and adopting it produces a matching
    /// handshake token over divergent behavior, which is strictly worse than an honest refusal. That
    /// mistake was made once already (the first cut of the NAND Tweaks reconcile adopted three inert
    /// options); each compat module now records per-setting evidence for its own adopt set.
    /// </summary>
    public static class ConfigAdoption
    {
        /// <summary>
        /// Files whose SaveOnConfigSet we suppressed, mapped to the value to put back. Suppression lasts for
        /// the WHOLE adopted window, not merely our own write.
        /// </summary>
        private static readonly Dictionary<ConfigFile, bool> _suppressed = new Dictionary<ConfigFile, bool>();

        /// <summary>
        /// Write a ConfigEntry without letting BepInEx flush that file to disk, and KEEP the suppression in
        /// place until the session ends (ReleaseSuppression).
        ///
        /// ConfigFile.SaveOnConfigSet defaults to TRUE, so a naive `entry.Value = x` rewrites the player's own
        /// mod config on disk and silently changes their singleplayer game. Suppressing only around OUR write
        /// is NOT sufficient, and that is exactly why this holds the flag down for the whole window: BepInEx's
        /// Save() serialises the ENTIRE file, so any later set on ANY entry in the same ConfigFile - by
        /// another mod, or by the user in Configuration Manager - flushes our borrowed values permanently.
        ///
        /// Not hypothetical: Shipyard Expansion's own DoSaveGame prefix does `cleanSave.Value = false` on the
        /// very ConfigFile that holds the topsailPatch we borrow, so one in-game save would have written a
        /// host's setting into that player's config for good - breaking the promise this feature makes out
        /// loud, both in the toast ("your own settings are restored when you leave") and in Plugin's comment.
        ///
        /// KNOWN TRADE-OFF, stated rather than hidden: while values are borrowed, a genuine user edit to one
        /// of those files does not persist either, and SE's cleanSave self-disable does not persist. Both are
        /// strictly better than silently rewriting someone's singleplayer settings, and both end with the
        /// session.
        /// </summary>
        public static void SetWithoutSaving(ConfigEntry<bool> entry, bool value)
        {
            if (entry == null) return;
            var file = entry.ConfigFile;
            if (file != null && !_suppressed.ContainsKey(file))
            {
                _suppressed[file] = file.SaveOnConfigSet;
                file.SaveOnConfigSet = false;
                Plugin.Log.LogInfo($"[Compat] Config auto-save held OFF for '{file.ConfigFilePath}' while host " +
                    "settings are borrowed; restored when the co-op session ends.");
            }
            entry.Value = value;
        }

        /// <summary>
        /// Restore auto-save on every file we suppressed. MUST run AFTER the modules have put the player's own
        /// values back - otherwise the restore itself is the write that persists the borrowed state.
        /// Idempotent, and never throws: teardown must continue even if one file fails.
        /// </summary>
        public static void ReleaseSuppression()
        {
            if (_suppressed.Count == 0) return;
            foreach (var kvp in _suppressed)
            {
                try { if (kvp.Key != null) kvp.Key.SaveOnConfigSet = kvp.Value; }
                catch (Exception e) { Plugin.Log.LogWarning("[Compat] Could not restore config auto-save: " + e.Message); }
            }
            Plugin.Log.LogInfo($"[Compat] Restored config auto-save on {_suppressed.Count} file(s).");
            _suppressed.Clear();
        }

        /// <summary>
        /// Parse a run of "/name0" or "/name1" (or "/name?") segments into name -> bool, as used by SE's
        /// rig-contract token. A "?" segment is recorded as UNKNOWN by being omitted, so a caller comparing
        /// two tokens sees the raw strings differ and refuses rather than guessing a value.
        /// </summary>
        public static Dictionary<string, bool> ParseNamedFlags(string token)
        {
            var map = new Dictionary<string, bool>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(token)) return map;

            foreach (var seg in token.Split('/'))
            {
                if (string.IsNullOrEmpty(seg)) continue;
                char last = seg[seg.Length - 1];
                if (last != '0' && last != '1') continue; // "?" or a non-flag segment (version, /noSync)
                map[seg.Substring(0, seg.Length - 1)] = last == '1';
            }
            return map;
        }

        /// <summary>
        /// Parse a compact tagged vector body such as "c1o0a1" into tag -> bool. Null when malformed, which
        /// callers must treat as "refuse", never as "no differences".
        /// </summary>
        public static Dictionary<string, bool> ParseTaggedFlags(string body)
        {
            if (string.IsNullOrEmpty(body) || body.Length % 2 != 0) return null;

            var map = new Dictionary<string, bool>(StringComparer.Ordinal);
            for (int i = 0; i < body.Length; i += 2)
            {
                char v = body[i + 1];
                if (v != '0' && v != '1') return null;
                map[body[i].ToString()] = v == '1';
            }
            return map;
        }
    }
}
