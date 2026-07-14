using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace SailwindCoop.Compat
{
    /// <summary>
    /// (v0.2.32) Towable Boats reuses the vanilla mooring SpringJoint with the bollard on a MOVING
    /// boat (a TowingCleat : GPButtonDockMooring under "<boat root>/towing set/"). Presence + version
    /// is hard-gated (it adds cleat GameObjects to hulls and flips which boats run full BoatProbes
    /// physics), and its "Small boats can tow" config joins the token: it decides whether cog/dhow/
    /// kakam get a towing set child AT ALL (TowingSet.cs:141-143, applied at Awake, restart-scoped) -
    /// a hierarchy difference that breaks path-keyed sync. Its "Performance mode" config is
    /// deliberately NOT in the token: guests neutralize BoatPerformanceSwitcher for remote hulls
    /// (BoatPhysicsPatches), which makes that config irrelevant to them.
    /// </summary>
    public static class TowableBoatsCompat
    {
        public const string TBGuid = "com.nandbrew.towableboats";
        private const string TBAssemblyName = "TowableBoats";

        public static bool IsInstalled { get; private set; }
        public static string Version { get; private set; } = "";
        /// <summary>TowableBoats.TowingCleat, resolved once; null when TB absent or reflection failed.</summary>
        public static Type TowingCleatType { get; private set; }
        private static string _configToken;

        public static string ModSignature
        {
            get
            {
                if (!IsInstalled) return "";
                if (_configToken == null) _configToken = ReadConfigToken();
                return "TB=" + Version + _configToken;
            }
        }

        public static bool IsTowingCleat(Component c)
            => c != null && TowingCleatType != null && TowingCleatType.IsInstanceOfType(c);

        /// <summary>True when this GameObject (a collider hit / mooring target) carries a TowingCleat.</summary>
        public static bool HasTowingCleat(GameObject go)
            => go != null && TowingCleatType != null && go.GetComponent(TowingCleatType) != null;

        public static void Init()
        {
            IsInstalled = false;
            Version = "";
            TowingCleatType = null;
            _configToken = null;
            try
            {
                if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(TBGuid, out var info) || info == null)
                {
                    Plugin.Log.LogInfo("[TBCompat] Towable Boats not installed.");
                    return;
                }
                IsInstalled = true;
                Version = info.Metadata.Version.ToString();

                var asm = info.Instance != null ? info.Instance.GetType().Assembly : null;
                asm = asm ?? AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == TBAssemblyName);
                TowingCleatType = asm?.GetType("TowableBoats.TowingCleat");
                if (TowingCleatType == null)
                    Plugin.Log.LogWarning($"[TBCompat] Towable Boats v{Version} detected but TowingCleat did not " +
                        "resolve - tow-target detection disabled (tows will sync as unresolvable moors). " +
                        "Check for a Sailwind Co-op update.");
                else
                    Plugin.Log.LogInfo($"[TBCompat] Towable Boats v{Version} detected; tow-aware mooring sync enabled.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[TBCompat] Detection threw. " + e);
            }
        }

        private static string ReadConfigToken()
        {
            try
            {
                BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(TBGuid, out var info);
                var asm = info?.Instance != null ? info.Instance.GetType().Assembly : null;
                asm = asm ?? AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == TBAssemblyName);
                var pluginType = asm?.GetType("TowableBoats.Plugin");
                var f = pluginType?.GetField("smallBoats", BindingFlags.NonPublic | BindingFlags.Static);
                var entry = f?.GetValue(null) as BepInEx.Configuration.ConfigEntry<bool>;
                if (entry == null) return "/cfg?";
                return "/sb" + (entry.Value ? '1' : '0');
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[TBCompat] Could not read smallBoats config: " + e.Message);
                return "/cfg?";
            }
        }
    }
}
