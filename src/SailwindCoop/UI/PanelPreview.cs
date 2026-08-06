using System.Collections.Generic;

namespace SailwindCoop.UI
{
    /// <summary>
    /// (v0.3.0) DEBUG-ONLY preview of <see cref="CoopMessagePanel"/>.
    ///
    /// WHY THIS EXISTS. The panel it previews only ever appears on a FAILED join, which means the only way to
    /// look at it is to arrange two machines whose mod sets deliberately disagree - and then you get about two
    /// seconds of it before the refusal path quits the session out from under you. That is a miserable loop for
    /// judging whether a block of text is readable, and readability is the entire point of the class (the
    /// field report that prompted it was "you can't even read it, it all runs off the screen").
    ///
    /// So: build the same message, from the same code, and show it on demand.
    ///
    /// FIDELITY MATTERS MORE THAN CONVENIENCE HERE. The sample is NOT hardcoded prose. It runs the real
    /// CompatRegistry.DescribeMismatchLines and ModManifest.DescribeDifferences over THIS machine's real
    /// token and real plugin list, against a copy with a few plausible differences introduced. What you see
    /// is what those methods actually emit for your install, including the real mod names and the real line
    /// lengths - which is the only thing that can honestly answer "does it wrap or does it run off the edge".
    /// A mockup with invented short names would prove nothing about the case that failed.
    ///
    /// Inert unless Debug.PreviewMessagePanel is on. Nothing here touches settings, the network, or a save.
    /// </summary>
    public static class PanelPreview
    {
        private static int _variant;

        /// <summary>Show the next message shape, cycling. Never throws.</summary>
        public static void ShowNext(float autoHideSeconds)
        {
            int v = _variant % 3;
            _variant++;
            Show(v, autoHideSeconds);
        }

        private static void Show(int variant, float autoHideSeconds)
        {
            try
            {
                string ourToken = Compat.CompatRegistry.ModSignature;
                string ourManifest = Compat.ModManifest.Local;
                string hostToken = PerturbToken(ourToken);
                string hostManifest = PerturbManifest(ourManifest);

                var lines = new List<string>();
                string title, footer;

                switch (variant)
                {
                    case 1:
                        // The ADMITTED-ANYWAY shape: same body, different framing, and it self-expires because
                        // the session continues behind it.
                        title = "Joining anyway - your mods differ from the host's";
                        lines.AddRange(Compat.CompatRegistry.DescribeMismatchLines(hostToken, ourToken));
                        lines.AddRange(Compat.ModManifest.DescribeDifferences(hostManifest, ourManifest, "the host"));
                        footer = "Coop.AllowModMismatch is on, so you were admitted. Expect desyncs.";
                        break;

                    case 2:
                        // The HOST-SIDE refusal, which leads with the co-op version line rather than a token
                        // diff - a different first line and worth eyeballing separately.
                        title = "The host refused your join";
                        lines.Add("Co-op mod version: the host runs v0.2.38, you run v" + Plugin.PluginVersion);
                        lines.AddRange(Compat.ModManifest.DescribeDifferences(hostManifest, ourManifest, "the host"));
                        footer = "Match the host's mods and versions, then try again. The full list is in the BepInEx log.";
                        break;

                    default:
                        // The shape from the field report: the guest-side pre-join refusal, longest body.
                        title = "Cannot join: your mods differ from the host's";
                        lines.AddRange(Compat.CompatRegistry.DescribeMismatchLines(hostToken, ourToken));
                        lines.AddRange(Compat.ModManifest.DescribeDifferences(hostManifest, ourManifest, "the host"));
                        footer = "Everyone must run the same gameplay mods, at the same versions. The full list is in the BepInEx log.";
                        break;
                }

                // A perfectly convincing fake refusal is a support ticket waiting to happen, so say plainly
                // that nothing happened. Last line and footer both, because either can be the one that is read.
                if (lines.Count == 0)
                    lines.Add("(no differences could be synthesised on this install - the panel is still drawn so its layout can be checked)");
                lines.Add("");
                lines.Add("PREVIEW ONLY - these differences are invented. No join was attempted and no settings were changed.");

                footer += "   [Debug.PreviewMessagePanel is on - this is a sample.]";

                Plugin.Log.LogInfo($"[UI] Message-panel preview, variant {variant}: {lines.Count} lines.");
                CoopMessagePanel.Show(title, lines, footer, autoHideSeconds);
            }
            catch (System.Exception e)
            {
                // A debug aid must never be the thing that breaks a launch.
                Plugin.Log.LogWarning("[UI] Message-panel preview failed: " + e.Message);
            }
        }

        /// <summary>
        /// A plausible "host" token: flip a couple of NAND Tweaks options so the real per-option diff text
        /// runs, and invent a mod the host has that we do not. Deliberately produces the two DIFFERENT line
        /// shapes DescribeMismatchLines can emit.
        /// </summary>
        private static string PerturbToken(string ours)
        {
            if (string.IsNullOrEmpty(ours)) return Compat.NANDTweaksCompat.VanillaVector;

            var segs = ours.Split(';');
            for (int i = 0; i < segs.Length; i++)
            {
                if (!segs[i].StartsWith("NT=")) continue;
                // Body after "NT=" is <tag><0|1> pairs. Flip the first and third options so the message has
                // to name more than one, which is the interesting layout case.
                var chars = segs[i].ToCharArray();
                int flipped = 0;
                for (int c = 4; c < chars.Length && flipped < 3; c += 2)
                {
                    if (flipped != 1) chars[c] = chars[c] == '1' ? '0' : '1';
                    flipped++;
                }
                segs[i] = new string(chars);
            }

            var list = new List<string>(segs);
            // "the host has it, you do not" - only invent this when the mod really is absent, so an install
            // that HAS Shipyard Expansion previews the honest "host [x] vs you [y]" line instead.
            bool hasSE = false;
            foreach (var s in list) if (s.StartsWith("SE=")) hasSE = true;
            if (!hasSE) list.Insert(0, "SE=t1a0");

            return string.Join(";", list.ToArray());
        }

        /// <summary>
        /// A plausible "host" plugin list: this machine's real one, with one version bumped, one plugin
        /// removed and two invented. That yields all three report lines (theirs-only, ours-only, version
        /// drift) with real mod names at real lengths.
        /// </summary>
        private static string PerturbManifest(string ours)
        {
            // Empty means "no information" and DescribeDifferences correctly says nothing about it. Preserve
            // that rather than inventing a list, so the preview cannot imply a comparison the real path
            // would refuse to make.
            if (string.IsNullOrEmpty(ours)) return "";

            var entries = new List<string>(ours.Split(';'));

            // Version drift on a real entry.
            if (entries.Count > 0)
            {
                var f = entries[0].Split('|');
                if (f.Length >= 2)
                {
                    f[1] = f[1] + ".1";
                    entries[0] = string.Join("|", f);
                }
            }

            // A plugin you have and the "host" does not.
            if (entries.Count > 2) entries.RemoveAt(1);

            // Two the "host" has and you do not. Long names on purpose: the reported failure was long text
            // running off the screen, so the preview must contain some.
            entries.Add("com.example.flagsandbanners|2.4.0|More Flags and Banners (Extended Edition)");
            entries.Add("com.example.navinstruments|0.9.7|Realistic Navigation Instruments and Charts");

            entries.Sort(System.StringComparer.Ordinal);
            return string.Join(";", entries.ToArray());
        }
    }
}
