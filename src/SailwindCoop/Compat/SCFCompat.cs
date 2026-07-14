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

        private static Assembly ResolveAssembly()
        {
            BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(SCFGuid, out var info);
            var asm = info?.Instance != null ? info.Instance.GetType().Assembly : null;
            return asm ?? AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == SCFAssemblyName);
        }
    }
}
