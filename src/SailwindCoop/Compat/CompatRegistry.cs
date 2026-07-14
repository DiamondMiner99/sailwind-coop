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
        public static string DescribeMismatch(string hostToken, string ourToken)
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
                if (h == null) diffs.Add($"{name}: you have [{o}], the host does not have it");
                else if (o == null) diffs.Add($"{name}: the host has [{h}], you do not have it");
                else diffs.Add($"{name}: host [{h}] vs you [{o}]");
            }
            return diffs.Count == 0 ? "mod tokens differ" : string.Join("; ", diffs);
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
