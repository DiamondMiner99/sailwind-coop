using System;
using System.Linq;
using System.Reflection;

namespace SailwindCoop.Compat
{
    /// <summary>
    /// (v0.2.32) Soft-dependency bridge to NANDbrew's Sail Collision Fix. SCF has no runtime data to
    /// sync - its three config bools decide WHICH rigs are legal (IsCollidingWithSail/IsObstructed
    /// forced false) and how far sails sheet (colAngleMin/Max reset to the full range on
    /// OnTriggerEnter; SCF SailCollisionFix.cs:14-43). All three change what an identical shipyard
    /// edit BUILDS, so they ride the handshake token and a divergent crew is refused at the door.
    /// Note "Ignore sail collision" DEFAULTS TO TRUE: a peer who installed the DLL and never opened
    /// the config already diverges from a vanilla peer, which is exactly why presence alone gates.
    /// </summary>
    public static class SCFCompat
    {
        public const string SCFGuid = "com.nandbrew.sailcollisionfix";
        private const string SCFAssemblyName = "SailCollisionFix";
        private static readonly string[] ConfigFields = { "ignoreSailsCollision", "ignoreObstructed", "ignoreAngleLimits" };
        private static readonly string[] ConfigTags = { "c", "o", "a" };

        public static bool IsInstalled { get; private set; }
        public static string Version { get; private set; } = "";
        private static string _configToken; // computed lazily; null until first read

        public static string ModSignature
        {
            get
            {
                if (!IsInstalled) return "";
                if (_configToken == null) _configToken = ReadConfigToken();
                return "SCF=" + Version + _configToken;
            }
        }

        public static void Init()
        {
            IsInstalled = false;
            Version = "";
            _configToken = null;
            try
            {
                if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(SCFGuid, out var info) || info == null)
                {
                    Plugin.Log.LogInfo("[SCFCompat] Sail Collision Fix not installed.");
                    return;
                }
                IsInstalled = true;
                Version = info.Metadata.Version.ToString();
                Plugin.Log.LogInfo($"[SCFCompat] Sail Collision Fix v{Version} detected; its three config " +
                    "options join the handshake token (they change rig legality and sail angle limits).");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[SCFCompat] Detection threw after the chainloader probe; token degrades fail-closed. " + e);
            }
        }

        // Lazy: SCF binds its configs in ITS Awake. The soft BepInDependency in Plugin.cs orders SCF
        // before us, but the AppDomain fallback below covers exotic load orders anyway, and lazy
        // evaluation (first lobby create/join) runs long after every plugin's Awake.
        private static string ReadConfigToken()
        {
            try
            {
                var asm = ResolveAssembly();
                var mainType = asm?.GetType("SailCollisionFix.Main");
                if (mainType == null) return "/cfg?";
                var sb = new System.Text.StringBuilder("/");
                for (int i = 0; i < ConfigFields.Length; i++)
                {
                    var f = mainType.GetField(ConfigFields[i], BindingFlags.NonPublic | BindingFlags.Static);
                    var entry = f?.GetValue(null) as BepInEx.Configuration.ConfigEntry<bool>;
                    if (entry == null) return "/cfg?"; // absent or unbound: fail closed into a distinct token
                    sb.Append(ConfigTags[i]).Append(entry.Value ? '1' : '0');
                }
                return sb.ToString();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[SCFCompat] Could not read SCF configs: " + e.Message);
                return "/cfg?";
            }
        }

        /// <summary>
        /// (v0.2.39) Adopt a peer's SCF config for this session. ALL THREE options qualify: each is read
        /// live at call time inside its Harmony patch body, and their effect points are shipyard sail-install
        /// checks plus a col-checker OnTriggerEnter postfix - all of which keep running after a join, so a
        /// runtime change genuinely converges behavior rather than only the token.
        ///
        /// Refuses (returns false) if the VERSION differs or either side reports "/cfg?" (unreadable), since
        /// neither is fixable at runtime.
        /// </summary>
        public static bool TryAdoptToken(string desired)
        {
            if (!IsInstalled || string.IsNullOrEmpty(desired) || !desired.StartsWith("SCF=")) return false;
            if (desired.Contains("cfg?") || ModSignature.Contains("cfg?")) return false;

            // Version must already match: "SCF=<ver>/<flags>". Only the flag run is adoptable.
            int slash = desired.IndexOf('/');
            if (slash < 0) return false;
            string desiredVersion = desired.Substring(4, slash - 4);
            if (desiredVersion != Version) return false;

            var wanted = ConfigAdoption.ParseTaggedFlags(desired.Substring(slash + 1));
            if (wanted == null) return false;

            try
            {
                var mainType = ResolveAssembly()?.GetType("SailCollisionFix.Main");
                if (mainType == null) return false;

                var entries = new BepInEx.Configuration.ConfigEntry<bool>[ConfigFields.Length];
                for (int i = 0; i < ConfigFields.Length; i++)
                {
                    var f = mainType.GetField(ConfigFields[i], BindingFlags.NonPublic | BindingFlags.Static);
                    entries[i] = f?.GetValue(null) as BepInEx.Configuration.ConfigEntry<bool>;
                    if (entries[i] == null) return false; // all-or-nothing, never half-adopt
                    if (!wanted.ContainsKey(ConfigTags[i])) return false;
                }

                if (_originalToken == null) _originalToken = ModSignature;

                var changed = new System.Collections.Generic.List<string>();
                for (int i = 0; i < entries.Length; i++)
                {
                    bool want = wanted[ConfigTags[i]];
                    if (entries[i].Value == want) continue;
                    ConfigAdoption.SetWithoutSaving(entries[i], want);
                    changed.Add($"{ConfigFields[i]} {(want ? "on" : "off")}");
                }

                _configToken = null; // re-derive
                if (changed.Count > 0)
                    Plugin.Log.LogInfo($"[SCFCompat] Adopted the host's Sail Collision Fix settings for this session: {string.Join(", ", changed)}");
                return ModSignature == desired;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[SCFCompat] Could not adopt the host's SCF settings: " + e.Message);
                _configToken = null;
                return false;
            }
        }

        /// <summary>Put the player's own SCF settings back after a co-op session. No-op if nothing adopted.</summary>
        public static void RestoreLocalToken()
        {
            if (_originalToken == null) return;
            string original = _originalToken;
            _originalToken = null; // clear FIRST so a throw cannot strand a permanent restore attempt

            try
            {
                int slash = original.IndexOf('/');
                if (slash < 0) return;
                var wanted = ConfigAdoption.ParseTaggedFlags(original.Substring(slash + 1));
                var mainType = ResolveAssembly()?.GetType("SailCollisionFix.Main");
                if (wanted == null || mainType == null) return;

                for (int i = 0; i < ConfigFields.Length; i++)
                {
                    var f = mainType.GetField(ConfigFields[i], BindingFlags.NonPublic | BindingFlags.Static);
                    var entry = f?.GetValue(null) as BepInEx.Configuration.ConfigEntry<bool>;
                    if (entry != null && wanted.TryGetValue(ConfigTags[i], out bool want) && entry.Value != want)
                        ConfigAdoption.SetWithoutSaving(entry, want);
                }
                _configToken = null;
                Plugin.Log.LogInfo($"[SCFCompat] Restored your own Sail Collision Fix settings ({original}).");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[SCFCompat] Could not restore your SCF settings; restart to be sure. " + e.Message);
                _configToken = null;
            }
        }

        /// <summary>The player's own token, captured before the first adopt. Null = nothing adopted.</summary>
        private static string _originalToken;

        private static Assembly ResolveAssembly()
        {
            BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(SCFGuid, out var info);
            var asm = info?.Instance != null ? info.Instance.GetType().Assembly : null;
            return asm ?? AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == SCFAssemblyName);
        }
    }
}
