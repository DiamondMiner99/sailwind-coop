using System.Collections.Generic;
using System.Linq;

namespace SailwindCoop.Compat
{
    /// <summary>
    /// (v0.2.32) Composes ONE opaque mod-set token from every per-mod compat module, in a FIXED
    /// deterministic order, for the lobby-data pre-check and the P2P handshake. The composed token
    /// obeys the same contract as the old SE-only token: compare with == only, never parse for the
    /// GATE decision. DescribeMismatch splits it for the refusal MESSAGE only, so users learn WHICH
    /// mod (and for NAND Tweaks, which sim vector) differs instead of a generic "mismatch".
    /// Segment order: SE, SCF, NT, DP, TB, LEO. Empty segments (mod absent) are dropped EXCEPT NT,
    /// which always emits (a vanilla peer advertises the vanilla sim vector - that equivalence is
    /// the whole tiered-gate design).
    /// </summary>
    public static class CompatRegistry
    {
        private static string _cached;

        /// <summary>
        /// Init every compat module IN SEGMENT ORDER and drop any prematurely cached composed token.
        /// Plugin.Awake calls THIS instead of the six individual Init()s: a read of ModSignature that
        /// somehow happened before init would otherwise freeze an "everything absent" token for the
        /// whole process (a silent fail-open where a modded peer advertises vanilla).
        /// </summary>
        public static void InitAll()
        {
            SECompat.Init();
            SCFCompat.Init();
            NANDTweaksCompat.Init();
            DeepPortsCompat.Init();
            TowableBoatsCompat.Init();
            LeopardCompat.Init();
            _cached = null;
            // The report-only manifest is built from the chainloader, not from these modules, but it shares
            // their lifetime and the same "never freeze a premature read" hazard, so it is reset here too.
            ModManifest.Reset();
        }

        public static string ModSignature
        {
            get
            {
                if (_cached != null) return _cached;
                var parts = new List<string>
                {
                    SECompat.ModSignature,
                    SCFCompat.ModSignature,
                    NANDTweaksCompat.ModSignature,   // always non-empty (vanilla vector when absent)
                    DeepPortsCompat.ModSignature,
                    TowableBoatsCompat.ModSignature,
                    LeopardCompat.ModSignature,
                };
                _cached = string.Join(";", parts.Where(p => !string.IsNullOrEmpty(p)));
                Plugin.Log.LogInfo($"[MODS] Composed mod-set token: [{_cached}]");
                return _cached;
            }
        }

        /// <summary>
        /// Human-readable diff of two composed tokens, for refusal messages ONLY (the gate itself
        /// stays exact string equality). Groups segments by their prefix before '='.
        /// </summary>
        public static string DescribeMismatch(string hostToken, string ourToken, bool weAreTheHost = false)
        {
            var lines = DescribeMismatchLines(hostToken, ourToken, weAreTheHost);
            return lines.Count == 0 ? "mod tokens differ" : string.Join("; ", lines.ToArray());
        }

        /// <summary>
        /// (v0.3.0) The same diff as DescribeMismatch but kept as SEPARATE LINES, for the readable message
        /// panel. The joined single-string form is still what the log and the notification ticker use; a
        /// semicolon-joined paragraph is precisely what made the on-screen refusal unreadable.
        /// </summary>
        public static List<string> DescribeMismatchLines(string hostToken, string ourToken, bool weAreTheHost = false)
        {
            var host = Segments(hostToken);
            var ours = Segments(ourToken);
            var keys = new List<string>(host.Keys);
            foreach (var k in ours.Keys) if (!keys.Contains(k)) keys.Add(k);

            var diffs = new List<string>();
            foreach (var k in keys)
            {
                host.TryGetValue(k, out var h);
                ours.TryGetValue(k, out var o);
                if (h == o) continue;
                string name = FriendlyName(k);
                if (h == null) diffs.Add($"{name}: you have it, the host does not. Remove it, or ask them to install it.");
                else if (o == null) diffs.Add($"{name}: the host has it, you do not. Install it to sail with them.");
                // Name the actual OPTIONS that differ. "host [NT=b1s0w1f1v1d0] vs you [NT=b0s0w1f1v1d0]" is
                // unreadable to a player and gives them nothing to act on. Only reachable when the reconcile
                // could not fix it.
                // (v0.3.0) NAND Tweaks gets ONE LINE PER SETTING. It is the only entry here that can
                // contribute five differing options at once, and joining them made a single bullet that
                // read as a paragraph and swamped the one-line entries around it. Each returned line names
                // the mod itself, so they still read correctly on their own.
                else if (k == "NT") diffs.AddRange(NANDTweaksCompat.DescribeVectorDiffLines(h, o));
                else diffs.Add($"{name}: {DescribeSegmentDiff(k, h, o, weAreTheHost)}");
            }
            return diffs;
        }

        /// <summary>
        /// (v0.3.0) Say what actually differs about one mod, in the player's own vocabulary.
        ///
        /// This used to print the two raw segments side by side: "host [SE=0.10.0/topsailPatch1/addSails1]
        /// vs you [SE=0.10.0/topsailPatch0/addSails1]". Two 34-character strings differing in one character,
        /// which the player has to find by eye - and when they do find it, the word they find is an internal
        /// field name that appears nowhere in their config file. A crew read exactly that message, concluded
        /// the whole config file was the unit of matching, and spent an evening copying files at each other.
        ///
        /// So: separate the VERSION (install or update the mod) from the SETTINGS (change this named line and
        /// possibly restart) from the INSTALL-HEALTH flags (nothing to match, the mod is broken on one end),
        /// because those are three different jobs and only one of them involves opening a config file.
        /// </summary>
        private static string DescribeSegmentDiff(string key, string hostSeg, string ourSeg, bool weAreTheHost)
        {
            try
            {
                string hostVer, ourVer;
                // Only Sail Collision Fix packs its options as a single-letter tag run. Restricting the tag
                // parse to it matters because Deep Ports' 8-hex bundle fingerprint can look exactly like one
                // by chance (a1b0c1d0), and reading a file hash as a list of settings would tell a player to
                // go and change options that do not exist.
                bool tagged = key == "SCF";
                var hostFlags = SplitSegment(hostSeg, tagged, out hostVer);
                var ourFlags = SplitSegment(ourSeg, tagged, out ourVer);

                var parts = new List<string>();

                if (hostVer != ourVer)
                    parts.Add($"the host has version {Show(hostVer)}, you have {Show(ourVer)}. Match their version.");

                // Every flag name either side mentions, in the host's order first so the message reads
                // consistently between the two machines.
                var names = new List<string>(hostFlags.Keys);
                foreach (var n in ourFlags.Keys) if (!names.Contains(n)) names.Add(n);

                foreach (var n in names)
                {
                    string hv, ov;
                    hostFlags.TryGetValue(n, out hv);
                    ourFlags.TryGetValue(n, out ov);
                    if (hv == ov) continue;

                    string health = SettingLabels.DescribeHealthFlag(key, n);
                    if (health != null) { parts.Add(health + "."); continue; }

                    // Presence-encoded markers (SE's /noBundles, /noSailData) and any flag only one side
                    // carries are NOT a value difference. Treating them as one produced the meaningless
                    // "differs (host set, you set)" plus an instruction to go and change a setting that
                    // would not have fixed anything.
                    bool bothValued = (hv == "0" || hv == "1") && (ov == "0" || ov == "1");
                    if (!bothValued)
                    {
                        string present = (hv != null) ? "the host" : "you";
                        string absent = (hv != null) ? "you do" : "the host does";
                        var oneSided = SettingLabels.Find(key, n);
                        parts.Add(oneSided != null
                            ? $"{present} has \"{oneSided.Label}\" set and {absent} not."
                            : $"{present} reports '{n}' and {absent} not.");
                        continue;
                    }

                    var setting = SettingLabels.Find(key, n);
                    if (setting == null)
                    {
                        // Deep Ports' segment carries a fingerprint of its asset file rather than any
                        // setting, so there is nothing to change - the two downloads simply are not the
                        // same file. Saying that plainly beats printing the hash at someone.
                        if (key == "DP")
                        {
                            parts.Add("your Deep Ports download is not the same file as the host's - " +
                                      "reinstall it from the same source and version.");
                            continue;
                        }
                        // An unmapped flag. Print it, but do not pretend to know where it lives.
                        parts.Add($"'{n}' differs (host {OnOff(hv)}, you {OnOff(ov)}).");
                        continue;
                    }

                    // Only tell the reader to change something when the reader is the one who differs. On
                    // the host's screen 'you' is the REMOTE guest, so an imperative here would instruct the
                    // captain to edit a config that is already correct.
                    parts.Add(weAreTheHost
                        ? $"\"{setting.Label}\" differs (you {OnOff(hv)}, they {OnOff(ov)}) - they need to " +
                          $"change it under [{setting.Section}] in that mod's config" +
                          (setting.NeedsRestart ? " and restart their game." : ".")
                        : $"\"{setting.Label}\" differs (host {OnOff(hv)}, you {OnOff(ov)}) - " +
                          $"change it under [{setting.Section}] in that mod's config" +
                          (setting.NeedsRestart ? " and restart the game." : "."));
                }

                if (parts.Count == 0) return $"host [{hostSeg}] vs you [{ourSeg}]";
                // Deep Ports' two differing file fingerprints parse as two DIFFERENT flag names, so the loop
                // above visits both and emits the identical sentence twice. De-duplicate rather than
                // special-casing, since any future presence-encoded pair would do the same.
                var unique = new List<string>();
                foreach (var p in parts) if (!unique.Contains(p)) unique.Add(p);
                return string.Join(" ", unique.ToArray());
            }
            catch
            {
                // Never let a message-formatting slip swallow the refusal itself.
                return $"host [{hostSeg}] vs you [{ourSeg}]";
            }
        }

        /// <summary>
        /// Break "SE=0.10.0/topsailPatch1/addSails0" into version "0.10.0" and {topsailPatch:1, addSails:0},
        /// and "SCF=1.2.0/c1o0a1" into version "1.2.0" and {c:1, o:0, a:1}. A bare word like "noBundles" or
        /// "hash?" becomes a flag with no value, which is how install-health markers are carried.
        /// </summary>
        private static Dictionary<string, string> SplitSegment(string segment, bool tagged, out string version)
        {
            version = "";
            var flags = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(segment)) return flags;

            int eq = segment.IndexOf('=');
            string body = eq > 0 ? segment.Substring(eq + 1) : segment;
            var chunks = body.Split('/');
            version = chunks.Length > 0 ? chunks[0] : "";

            for (int i = 1; i < chunks.Length; i++)
            {
                string c = chunks[i];
                if (c.Length == 0) continue;

                // A run of single-letter tags with a 0/1 each: "c1o0a1". Only ever produced by the
                // tag-style mods, and distinguishable because every odd character is a digit.
                if (tagged && IsTagRun(c))
                {
                    for (int j = 0; j + 1 < c.Length; j += 2) flags[c[j].ToString()] = c[j + 1].ToString();
                    continue;
                }

                // "topsailPatch1" - a name with a trailing 0/1.
                char last = c[c.Length - 1];
                if (c.Length > 1 && (last == '0' || last == '1'))
                    flags[c.Substring(0, c.Length - 1)] = last.ToString();
                else
                    flags[c] = "";   // a bare marker: noBundles, noSync, hash?, or Deep Ports' bundle hash
            }
            return flags;
        }

        private static bool IsTagRun(string s)
        {
            if (s.Length < 2 || s.Length % 2 != 0) return false;
            for (int i = 0; i < s.Length; i += 2)
                if (!char.IsLetter(s[i]) || (s[i + 1] != '0' && s[i + 1] != '1')) return false;
            return true;
        }

        private static string OnOff(string v)
        {
            if (v == "1") return "on";
            if (v == "0") return "off";
            return string.IsNullOrEmpty(v) ? "set" : v;
        }

        private static string Show(string v) { return string.IsNullOrEmpty(v) ? "(none)" : v; }

        /// <summary>
        /// (v0.3.0) Try to make THIS machine's token equal the host's by adopting the host's SETTINGS, so a
        /// crew running identical mods is not refused over a single config line.
        ///
        /// Only differences that are purely config-valued AND applicable at runtime can be reconciled. Today
        /// that is exactly the NAND Tweaks sim vector, and that is a verified property, not an optimistic
        /// default (see NANDTweaksCompat.TryAdoptVector). Everything else is deliberately NOT reconciled:
        ///
        ///   - a mod one side does not have, or a different mod VERSION: no runtime fix exists.
        ///   - SE's rig-contract configs (topsailPatch, addSails): SE's own config description marks them
        ///     "(requires a restart)", so writing them live would make the token match while the two peers
        ///     kept behaving differently - strictly worse than an honest refusal.
        ///   - any segment carrying "?" (a peer that could not read its own settings): unknown, never guess.
        ///
        /// Returns true only when the FULL token now matches, i.e. the join can proceed. A partial adopt
        /// still returns false and the caller refuses, so this can never turn a real incompatibility into an
        /// admitted-but-broken session.
        /// </summary>
        public static bool TryReconcileWith(string hostToken)
        {
            if (string.IsNullOrEmpty(hostToken)) return false;
            if (hostToken == ModSignature) return true;

            var host = Segments(hostToken);
            var ours = Segments(ModSignature);

            // Every differing segment must be one we can actually fix; bail on the first that is not, without
            // applying anything, so we never half-adopt a host's settings on a doomed join.
            var keys = new List<string>(host.Keys);
            foreach (var k in ours.Keys) if (!keys.Contains(k)) keys.Add(k);
            foreach (var k in keys)
            {
                host.TryGetValue(k, out var h);
                ours.TryGetValue(k, out var o);
                if (h == o) continue;
                // A segment is only reconcilable if BOTH sides have it (a mod one side lacks entirely can
                // never be fixed at runtime) and the owning module can apply it live.
                if (h == null || o == null || (k != "NT" && k != "SCF" && k != "SE"))
                {
                    Plugin.Log.LogInfo($"[MODS] Cannot auto-reconcile '{k}' (host [{h}] vs ours [{o}]) - not a runtime-applicable setting.");
                    return false;
                }
            }

            // Each module decides for ITSELF whether the specific difference is adoptable, and returns false
            // if it is not (e.g. SE refuses an addSails difference, NT refuses a world-load-only option).
            // A module that returns false here means the join is refused, which is the honest outcome.
            foreach (var k in keys)
            {
                host.TryGetValue(k, out var h);
                ours.TryGetValue(k, out var o);
                if (h == o) continue;

                bool adopted;
                switch (k)
                {
                    case "NT": adopted = NANDTweaksCompat.TryAdoptVector(h); break;
                    case "SCF": adopted = SCFCompat.TryAdoptToken(h); break;
                    case "SE": adopted = SECompat.TryAdoptRigContract(h); break;
                    default: adopted = false; break;
                }
                if (!adopted)
                {
                    // MUST drop the composed cache even on the refusing path. Modules are tried in segment
                    // order and each writes as it goes, so an EARLIER module may already have adopted before a
                    // LATER one refuses (e.g. SE adopts topsailPatch, then NT refuses on a world-load-only
                    // option). Returning without invalidating would leave ModSignature advertising a token
                    // that predates a write which actually happened. The half-adopt itself is undone by
                    // RestoreLocalSettings on the refusal's teardown path.
                    _cached = null;
                    Plugin.Log.LogInfo($"[MODS] '{k}' could not be auto-matched (host [{h}] vs ours [{o}]); refusing.");
                    return false;
                }
            }

            _cached = null; // a segment moved, so the composed token must be re-derived
            bool ok = ModSignature == hostToken;
            if (!ok)
                Plugin.Log.LogWarning($"[MODS] Reconcile did not converge: ours [{ModSignature}] vs host [{hostToken}]. Refusing.");
            return ok;
        }

        /// <summary>
        /// Undo everything TryReconcileWith adopted. Called on session teardown so a co-op session never
        /// leaves the player's singleplayer settings changed behind their back.
        /// </summary>
        public static void RestoreLocalSettings()
        {
            // Each restore is independently try/caught inside the module and no-ops when that module never
            // adopted, so one failing module cannot strand another module's settings on the host's values.
            NANDTweaksCompat.RestoreLocalVector();
            SCFCompat.RestoreLocalToken();
            SECompat.RestoreLocalRigContract();
            // AFTER the restores, never before: releasing auto-save first would make the restore writes
            // themselves flush to disk, persisting exactly what we are trying to undo.
            ConfigAdoption.ReleaseSuppression();
            _cached = null;
        }

        private static Dictionary<string, string> Segments(string token)
        {
            var map = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(token)) return map;
            foreach (var seg in token.Split(';'))
            {
                int eq = seg.IndexOf('=');
                map[eq > 0 ? seg.Substring(0, eq) : seg] = seg;
            }
            return map;
        }

        private static string FriendlyName(string key)
        {
            switch (key)
            {
                case "SE": return "Shipyard Expansion";
                case "SCF": return "Sail Collision Fix";
                case "NT": return "NAND Tweaks (sim options)";
                case "DP": return "Deep Ports";
                case "TB": return "Towable Boats";
                case "LEO": return "HMS Leopard";
                default: return key;
            }
        }
    }
}
