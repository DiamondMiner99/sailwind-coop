using System;
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
    /// "everything off" both match a vanilla peer. NOTE the mod's DEFAULTS are not vanilla (four of
    /// six default true), so host-at-defaults vs no-mod-guest is a REAL sim difference and is
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
                BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(NTGuid, out var info);
                var asm = info?.Instance != null ? info.Instance.GetType().Assembly : null;
                asm = asm ?? AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == NTAssemblyName);
                var pluginType = asm?.GetType("NANDTweaks.Plugin");
                if (pluginType == null) return "NT=?";
                var sb = new System.Text.StringBuilder("NT=");
                for (int i = 0; i < SimConfigFields.Length; i++)
                {
                    var f = pluginType.GetField(SimConfigFields[i], BindingFlags.NonPublic | BindingFlags.Static);
                    var entry = f?.GetValue(null) as BepInEx.Configuration.ConfigEntry<bool>;
                    if (entry == null) return "NT=?"; // fail closed: unreadable sim vector must not pass as vanilla
                    sb.Append(SimConfigTags[i]).Append(entry.Value ? '1' : '0');
                }
                return sb.ToString();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[NTCompat] Could not read NAND Tweaks sim configs: " + e.Message);
                return "NT=?";
            }
        }
    }
}
