using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SailwindCoop.Compat
{
    /// <summary>
    /// (v0.3.0) INFORMATIONAL inventory of every BepInEx plugin loaded on this machine, exchanged in the
    /// P2P handshake so a crew can SEE which mods differ.
    ///
    /// This is deliberately NOT a gate and must never become one. CompatRegistry.ModSignature is the gate:
    /// it is a hand-curated set of mods whose differences are known to break the shared simulation, and a
    /// mismatch there refuses the join. The manifest is the opposite - it covers EVERYTHING indiscriminately,
    /// including purely cosmetic mods (HUDs, skies, furniture, instrument displays), so feeding it into the
    /// gate would refuse a join over a wind-indicator overlay. Keep the two strictly separate:
    ///
    ///     ModSignature -> curated, opaque, EXACT-EQUALITY GATE, refuses joins
    ///     ModManifest  -> generic, parsed, REPORT ONLY, never refuses anything
    ///
    /// Why it earns its keep: the curated gate covers 6 mods, but a real install runs 20+. Everything outside
    /// those 6 is currently invisible - a crew can be running different world-object mods (furniture, tables,
    /// extra items) with no signal at all, which matters for a mod that syncs items by name. This turns silent
    /// divergence into a named report without touching who is allowed to join.
    ///
    /// WIRE: an additive trailing string on Handshake/HandshakeAck. The existing readers of those packets
    /// already demonstrate the pattern (a pre-v0.2.31 peer's payload simply ends early and the reader
    /// tolerates it), so a peer without this field is not a wire break - it reports an EMPTY manifest.
    /// </summary>
    public static class ModManifest
    {
        // Entry:  guid|version|name        Separator between entries: ';'
        // GUIDs and versions cannot contain either delimiter; the display NAME is sanitised on the way in.
        private const char EntrySep = ';';
        private const char FieldSep = '|';

        private static string _cached;

        /// <summary>
        /// Wire string for THIS machine, built once. An empty string means "could not read" and is treated
        /// by DescribeDifferences as "no information", NEVER as "this peer has no mods" - see that method.
        /// </summary>
        public static string Local
        {
            get
            {
                if (_cached != null) return _cached;
                _cached = Build();
                return _cached;
            }
        }

        /// <summary>Drop the cached manifest (mirrors CompatRegistry.InitAll, called from the same place).</summary>
        public static void Reset() => _cached = null;

        /// <summary>
        /// Steam lobby metadata caps a value at 8192 chars and Lobby.SetData THROWS above it rather than
        /// truncating - and the caller's try/catch would swallow that into a half-created, never-joinable
        /// lobby. So clamp HERE, on whole entries, and never hand SetData something it can reject.
        ///
        /// Dropping the tail is acceptable because this is a report, not a gate: the P2P copy carries the
        /// full list for anyone who actually connects, and a truncated lobby-data copy still names the first
        /// N mods to a guest who is being refused before P2P. Entries are ordinal-sorted, so what survives is
        /// stable rather than arbitrary.
        /// </summary>
        public static string ForLobbyData()
        {
            const int Cap = 8000; // headroom under Steam's 8192 for the marker below
            var s = Local;
            if (string.IsNullOrEmpty(s) || s.Length <= Cap) return s;

            int cut = s.LastIndexOf(EntrySep, Cap - 1);
            if (cut <= 0) return ""; // a single entry over the cap is not worth guessing at
            var clamped = s.Substring(0, cut);
            Plugin.Log.LogInfo($"[MODS] Lobby-data manifest clamped from {s.Length} to {clamped.Length} chars " +
                "(Steam caps metadata at 8192); the full list still travels over P2P.");
            return clamped;
        }

        private static string Build()
        {
            try
            {
                var entries = new List<string>();
                foreach (var kvp in BepInEx.Bootstrap.Chainloader.PluginInfos)
                {
                    var meta = kvp.Value?.Metadata;
                    if (meta == null) continue;
                    entries.Add(string.Join(FieldSep.ToString(), new[]
                    {
                        Clean(meta.GUID),
                        Clean(meta.Version != null ? meta.Version.ToString() : "?"),
                        Clean(meta.Name),
                    }));
                }

                // Ordinal sort so two machines with the same mods produce byte-identical strings. Nothing
                // compares whole manifests today, but an unordered list would make any future equality check
                // (or a log diff by eye) silently wrong, and the sort costs nothing at handshake time.
                entries.Sort(StringComparer.Ordinal);

                var s = string.Join(EntrySep.ToString(), entries);
                Plugin.Log.LogInfo($"[MODS] Local mod manifest: {entries.Count} plugins, {s.Length} bytes");
                return s;
            }
            catch (Exception e)
            {
                // Fail SILENT, not fail closed: this is a report, so the correct degraded behavior is to say
                // nothing. Returning "" makes DescribeDifferences skip the comparison entirely rather than
                // accusing the peer of having removed every mod.
                Plugin.Log.LogWarning("[MODS] Could not build the mod manifest; mod reporting disabled for this session. " + e.Message);
                return "";
            }
        }

        private static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "?";
            return s.Replace(EntrySep, ' ').Replace(FieldSep, ' ').Trim();
        }

        private struct Entry { public string Version; public string Name; }

        private static Dictionary<string, Entry> Parse(string manifest)
        {
            var map = new Dictionary<string, Entry>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(manifest)) return map;

            foreach (var raw in manifest.Split(EntrySep))
            {
                if (string.IsNullOrEmpty(raw)) continue;
                var f = raw.Split(FieldSep);
                if (f.Length < 1 || string.IsNullOrEmpty(f[0])) continue;
                map[f[0]] = new Entry
                {
                    Version = f.Length > 1 ? f[1] : "?",
                    Name = f.Length > 2 && !string.IsNullOrEmpty(f[2]) ? f[2] : f[0],
                };
            }
            return map;
        }

        /// <summary>
        /// Human-readable differences between two manifests, most actionable first (missing mods before
        /// version drift). Returns an EMPTY list when there is nothing to say, including when either side's
        /// manifest is empty.
        ///
        /// That empty-side rule is load-bearing. An empty manifest means the peer is on an older build that
        /// does not send the field, or its own build threw. Both are "no information". Diffing against it
        /// would report every one of the reader's own 20+ plugins as "the other side is missing this", which
        /// is both wrong and the single most likely way this feature could annoy a crew into ignoring it.
        /// </summary>
        public static List<string> DescribeDifferences(string theirManifest, string ourManifest, string themLabel)
        {
            var diffs = new List<string>();
            if (string.IsNullOrEmpty(theirManifest) || string.IsNullOrEmpty(ourManifest)) return diffs;

            var them = Parse(theirManifest);
            var us = Parse(ourManifest);

            var theirsOnly = new List<string>();
            var oursOnly = new List<string>();
            var versionDiffs = new List<string>();

            foreach (var kvp in them)
            {
                if (us.TryGetValue(kvp.Key, out var ourEntry))
                {
                    if (ourEntry.Version != kvp.Value.Version)
                        versionDiffs.Add($"{kvp.Value.Name}: {themLabel} has {kvp.Value.Version}, you have {ourEntry.Version}");
                }
                else theirsOnly.Add($"{kvp.Value.Name} {kvp.Value.Version}");
            }
            foreach (var kvp in us)
                if (!them.ContainsKey(kvp.Key)) oursOnly.Add($"{kvp.Value.Name} {kvp.Value.Version}");

            theirsOnly.Sort(StringComparer.Ordinal);
            oursOnly.Sort(StringComparer.Ordinal);
            versionDiffs.Sort(StringComparer.Ordinal);

            if (theirsOnly.Count > 0) diffs.Add($"{themLabel} has, you don't: {string.Join(", ", theirsOnly)}");
            if (oursOnly.Count > 0) diffs.Add($"you have, {themLabel} doesn't: {string.Join(", ", oursOnly)}");
            diffs.AddRange(versionDiffs);
            return diffs;
        }

        /// <summary>
        /// Write the full difference set to the log. (v0.3.0) The on-screen half moved to
        /// UI.CoopMessagePanel: this used to build a 3-line summary for the vanilla notification ticker,
        /// which clipped rather than wrapped and made a list of mod names unreadable - the whole reason the
        /// panel exists. The panel takes the full list, so nothing needs capping for display any more.
        /// </summary>
        public static void LogDifferences(List<string> diffs, string themLabel)
        {
            if (diffs == null || diffs.Count == 0) return;
            Plugin.Log.LogInfo($"[MODS] Mod differences vs {themLabel} ({diffs.Count}):");
            foreach (var d in diffs) Plugin.Log.LogInfo("[MODS]   " + d);
        }
    }
}
