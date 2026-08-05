using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace SailwindCoop.Compat
{
    /// <summary>
    /// (v0.2.32) NAND Tweaks is gated on BEHAVIOR, not presence: most of it is cosmetic (outlines,
    /// camera, UI, decals, thumbnails, keybinds - free per player), but six options change the
    /// simulation and must match across the crew:
    ///   b bailingTweaks  - replaces the bail routine, writes BoatDamage.waterLevel directly
    ///   s drunkenSleep   - drains PlayerNeeds.sleep scaled by Sun timescale (sleep duration)
    ///   w wheelCenter    - writes GPButtonSteeringWheel.currentInput in ExtraFixedUpdate
    ///   f albacoreArea   - injects a LocalFishesRegion + prefab into OceanFishes
    ///   v saveLoadState  - writes modData, restores Rigidbody.velocity/sails/wheel on load
    ///   d toggleDoors    - fires GPButtonTrapdoor.OnActivate() on load
    /// A peer WITHOUT the mod has the VANILLA vector (all zeros), so cosmetic-only installs and
    /// "everything off" both match a vanilla peer. NOTE the mod's DEFAULTS are not vanilla (FIVE of
    /// six default true - only wheelCenter defaults false), and that count is load-bearing for the
    /// v0.2.39 reconcile: all THREE non-adoptable options default TRUE, so an NT-at-defaults host
    /// facing a guest who turned any of them off still refuses, so host-at-defaults vs no-mod-guest is a REAL sim difference and is
    /// correctly refused - the refusal message (CompatRegistry.DescribeMismatch) names the vector so
    /// users can see which options differ. Values snapshot at token time (lobby create/join);
    /// mid-session config flips are not re-gated, same as SE.
    /// </summary>
    public static class NANDTweaksCompat
    {
        public const string NTGuid = "com.nandbrew.nandtweaks";
        private const string NTAssemblyName = "NANDTweaks";
        private static readonly string[] SimConfigFields = { "bailingTweaks", "drunkenSleep", "wheelCenter", "albacoreArea", "saveLoadState", "toggleDoors" };
        private static readonly string[] SimConfigTags = { "b", "s", "w", "f", "v", "d" };

        /// <summary>
        /// (v0.2.39) Which of the six can actually be ADOPTED from a host at join and take effect this
        /// session. Index-parallel to SimConfigFields/SimConfigTags.
        ///
        /// This distinction is the whole safety of the reconcile, and getting it wrong is worse than having
        /// no reconcile at all: adopting an option that cannot take effect makes the gate token MATCH while
        /// the two peers keep behaving differently, which is precisely the silent divergence the gate exists
        /// to prevent. "Reads .Value inside a method body" is NOT sufficient evidence of adoptability - what
        /// matters is whether the enclosing method still RUNS after we join.
        ///
        ///   bailingTweaks  LIVE    - read per click (BoatDamageWaterButton.OnItemClick prefix) and per frame
        ///                            (ExtraLateUpdate prefix). Its BoatDamageWater.Start postfix does latch,
        ///                            but only water-GAUGE calibration (connectedAnchor/localScale/renderer/
        ///                            audio) - no path from there to waterLevel, buoyancy or mass. The
        ///                            residual divergence is cosmetic gauge appearance.
        ///   drunkenSleep   LIVE    - PlayerNeeds.LateUpdate postfix, every frame.
        ///   wheelCenter    LIVE    - GPButtonSteeringWheel ExtraFixedUpdate postfix, every fixed frame.
        ///   albacoreArea   INERT   - read only in OceanFishes.Awake / PrefabsDirectory.Start. The ocean
        ///                            scene loads ONCE PER PROCESS at boot, so adopting it does nothing at
        ///                            all, in either direction.
        ///   saveLoadState  INERT   - its load half runs only from the LoadModData postfix (a boundary we are
        ///                            already past when we reconcile), so the session cannot converge. Its
        ///                            save half IS live and would reach the guest's phantom save on disk.
        ///   toggleDoors    INERT   - single read site sits inside SaveLoader.LoadSailConfig, reachable only
        ///                            via LoadAfterDelay from the LoadModData postfix. World-load only.
        /// </summary>
        private static readonly bool[] SimConfigAdoptable = { true, true, true, false, false, false };
        public const string VanillaVector = "NT=b0s0w0f0v0d0";

        public static bool IsInstalled { get; private set; }
        public static string Version { get; private set; } = "";
        private static string _token;

        public static string ModSignature
        {
            get
            {
                if (!IsInstalled) return VanillaVector;
                if (_token == null) _token = ReadSimVector();
                return _token;
            }
        }

        public static void Init()
        {
            IsInstalled = false;
            Version = "";
            _token = null;
            try
            {
                if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(NTGuid, out var info) || info == null)
                {
                    Plugin.Log.LogInfo("[NTCompat] NAND Tweaks not installed; advertising the vanilla sim vector.");
                    return;
                }
                IsInstalled = true;
                Version = info.Metadata.Version.ToString();
                Plugin.Log.LogInfo($"[NTCompat] NAND Tweaks v{Version} detected; gating on its six " +
                    "simulation-affecting options (cosmetic options stay free per player).");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[NTCompat] Detection threw after the chainloader probe; token degrades fail-closed. " + e);
            }
        }

        private static string ReadSimVector()
        {
            try
            {
                var entries = ResolveEntries();
                if (entries == null) return "NT=?";
                var sb = new System.Text.StringBuilder("NT=");
                for (int i = 0; i < entries.Length; i++)
                    sb.Append(SimConfigTags[i]).Append(entries[i].Value ? '1' : '0');
                return sb.ToString();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[NTCompat] Could not read NAND Tweaks sim configs: " + e.Message);
                return "NT=?";
            }
        }

        /// <summary>
        /// Resolve the six ConfigEntry objects, or null if ANY of them is unreachable. All-or-nothing on
        /// purpose: a partial set would let ReadSimVector emit a short vector that could collide with a
        /// legitimate one, and would let TryAdoptVector apply half of a host's settings.
        /// </summary>
        private static BepInEx.Configuration.ConfigEntry<bool>[] ResolveEntries()
        {
            BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(NTGuid, out var info);
            var asm = info?.Instance != null ? info.Instance.GetType().Assembly : null;
            asm = asm ?? AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == NTAssemblyName);
            var pluginType = asm?.GetType("NANDTweaks.Plugin");
            if (pluginType == null) return null;

            var result = new BepInEx.Configuration.ConfigEntry<bool>[SimConfigFields.Length];
            for (int i = 0; i < SimConfigFields.Length; i++)
            {
                var f = pluginType.GetField(SimConfigFields[i], BindingFlags.NonPublic | BindingFlags.Static);
                var entry = f?.GetValue(null) as BepInEx.Configuration.ConfigEntry<bool>;
                if (entry == null) return null;
                result[i] = entry;
            }
            return result;
        }

        /// <summary>
        /// (v0.2.39) Adopt a peer's sim vector for THIS SESSION, so a crew running the same mods is not
        /// refused over a checkbox.
        ///
        /// ONLY the options in SimConfigAdoptable can be adopted. If the host differs from us on any option
        /// outside that set, we return FALSE and let the caller refuse honestly, because those options cannot
        /// take effect this session and pretending otherwise would produce a matching token over divergent
        /// behavior. See SimConfigAdoptable for the per-option evidence.
        ///
        /// CORRECTION (v0.2.39, from an adversarial re-audit): an earlier version of this method adopted all
        /// six and justified it with "every option is read inside its patch body, and we apply at JOIN before
        /// the guest's world is live". BOTH halves of that were wrong. Reading `.Value` inside a method body
        /// says nothing about whether that method still runs after we join - three of the six are reachable
        /// only from world-load paths. And the reconcile does NOT run before the world is live: both join
        /// paths reach it with GameState.playing already true. Do not restore that reasoning.
        ///
        /// IN MEMORY ONLY, with one honest caveat. BepInEx's ConfigFile.SaveOnConfigSet defaults to TRUE, so
        /// a naive `entry.Value = x` would rewrite the player's own NAND Tweaks cfg on disk and silently
        /// change their singleplayer game. We suppress that around the write and restore the originals on
        /// session end (RestoreLocalVector). The one thing that is NOT purely in-memory is saveLoadState,
        /// whose save half writes its EFFECT into the guest's phantom save - which is part of why it is not
        /// in the adoptable set.
        ///
        /// Returns true only if the full vector was applied and now matches the requested one.
        /// </summary>
        public static bool TryAdoptVector(string desiredVector)
        {
            if (!IsInstalled) return false;
            if (string.IsNullOrEmpty(desiredVector) || !desiredVector.StartsWith("NT=")) return false;
            if (desiredVector.Contains("?")) return false; // peer could not read its own vector

            try
            {
                var wanted = ParseVector(desiredVector);
                if (wanted == null) return false;

                var entries = ResolveEntries();
                if (entries == null)
                {
                    Plugin.Log.LogWarning("[NTCompat] Cannot adopt the host's NAND Tweaks settings: its config entries did not resolve.");
                    return false;
                }

                // Snapshot ONCE. A second adopt in the same session (rejoin, host change) must still restore
                // the player's ORIGINAL values, not the previous host's.
                if (_originalVector == null) _originalVector = ReadSimVector();

                // PASS 1: refuse BEFORE writing anything if any differing option is one we cannot actually
                // apply. Checking up front (rather than bailing mid-loop) means a doomed reconcile never
                // leaves the player on a half-adopted mixture of their settings and the host's.
                for (int i = 0; i < entries.Length; i++)
                {
                    if (!wanted.TryGetValue(SimConfigTags[i], out bool want)) return false; // short/garbled vector
                    if (entries[i].Value == want) continue;
                    if (!SimConfigAdoptable[i])
                    {
                        Plugin.Log.LogInfo($"[NTCompat] Cannot auto-match '{SimConfigFields[i]}' (host " +
                            $"{(want ? "on" : "off")}, you {(entries[i].Value ? "on" : "off")}): it only takes effect " +
                            "at world load, so adopting it would match the handshake token without matching " +
                            "behavior. Refusing instead.");
                        return false;
                    }
                }

                // PASS 2: every difference is adoptable, so apply them.
                var changed = new List<string>();
                for (int i = 0; i < entries.Length; i++)
                {
                    if (!wanted.TryGetValue(SimConfigTags[i], out bool want)) return false;
                    if (entries[i].Value == want) continue;
                    SetWithoutSaving(entries[i], want);
                    changed.Add($"{SimConfigFields[i]} {(entries[i].Value ? "on" : "off")}");
                }

                _token = null; // force the next ModSignature read to re-derive from the new values
                if (changed.Count > 0)
                    Plugin.Log.LogInfo($"[NTCompat] Adopted the host's NAND Tweaks settings for this session: {string.Join(", ", changed)}");
                return ModSignature == desiredVector;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[NTCompat] Could not adopt the host's NAND Tweaks settings: " + e.Message);
                _token = null;
                return false;
            }
        }

        /// <summary>
        /// Put the player's own settings back after a co-op session. No-op when we never adopted anything.
        /// </summary>
        public static void RestoreLocalVector()
        {
            if (_originalVector == null) return;
            string original = _originalVector;
            _originalVector = null; // clear FIRST so a throw below cannot strand a permanent restore attempt

            try
            {
                var wanted = ParseVector(original);
                var entries = ResolveEntries();
                if (wanted == null || entries == null) return;

                // Restore unconditionally, NOT gated on SimConfigAdoptable: if a future edit widens the
                // adopt set, this must still put back everything that was actually changed.
                for (int i = 0; i < entries.Length; i++)
                    if (wanted.TryGetValue(SimConfigTags[i], out bool want) && entries[i].Value != want)
                        SetWithoutSaving(entries[i], want);

                _token = null;
                Plugin.Log.LogInfo($"[NTCompat] Restored your own NAND Tweaks settings after the co-op session ({original}).");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[NTCompat] Could not restore your NAND Tweaks settings; restart the game to be sure they are back. " + e.Message);
                _token = null;
            }
        }

        /// <summary>
        /// Write a ConfigEntry WITHOUT letting BepInEx flush the file to disk. SaveOnConfigSet is restored
        /// in a finally, so an exception mid-write cannot leave the player's config file permanently
        /// un-saveable for the rest of the process.
        /// </summary>
        private static void SetWithoutSaving(BepInEx.Configuration.ConfigEntry<bool> entry, bool value)
        {
            var file = entry.ConfigFile;
            bool prev = file != null && file.SaveOnConfigSet;
            if (file != null) file.SaveOnConfigSet = false;
            try { entry.Value = value; }
            finally { if (file != null) file.SaveOnConfigSet = prev; }
        }

        /// <summary>Parse "NT=b1s0w1f1v1d0" into tag -> bool. Null when malformed.</summary>
        private static Dictionary<string, bool> ParseVector(string vector)
        {
            if (string.IsNullOrEmpty(vector) || !vector.StartsWith("NT=")) return null;
            string body = vector.Substring(3);
            if (body.Length % 2 != 0 || body.Length == 0) return null;

            var map = new Dictionary<string, bool>();
            for (int i = 0; i < body.Length; i += 2)
            {
                char v = body[i + 1];
                if (v != '0' && v != '1') return null;
                map[body[i].ToString()] = v == '1';
            }
            return map;
        }

        /// <summary>The player's own vector, captured before the first adopt. Null = nothing adopted.</summary>
        private static string _originalVector;

        /// <summary>Human-readable per-option diff, for a refusal or reconcile message.</summary>
        public static string DescribeVectorDiff(string theirVector, string ourVector)
        {
            var them = ParseVector(theirVector);
            var us = ParseVector(ourVector);
            if (them == null || us == null) return "NAND Tweaks settings could not be read on one side";

            var diffs = new List<string>();
            for (int i = 0; i < SimConfigTags.Length; i++)
            {
                if (!them.TryGetValue(SimConfigTags[i], out bool t)) continue;
                if (!us.TryGetValue(SimConfigTags[i], out bool o)) continue;
                // (v0.2.39) Name the setting the way NAND Tweaks' own config file names it. This printed
                // SimConfigFields[i], the C# field name, which is exactly the unfindable-word problem
                // SettingLabels exists to end: a player told "saveLoadState" differs will not find that
                // string anywhere, because the line in the file reads "Save and load ship state".
                if (t != o)
                {
                    var label = SettingLabels.Find("NT", SimConfigTags[i]);
                    string shown = label != null ? "\"" + label.Label + "\"" : SimConfigFields[i];
                    string where = label == null ? ""
                        : $", under [{label.Section}]" + (label.NeedsRestart ? " - changing it needs a restart" : "");
                    diffs.Add($"{shown} (host {(t ? "on" : "off")}, you {(o ? "on" : "off")}){where}");
                }
            }
            return diffs.Count == 0 ? "NAND Tweaks settings differ" : string.Join(", ", diffs);
        }
    }
}
