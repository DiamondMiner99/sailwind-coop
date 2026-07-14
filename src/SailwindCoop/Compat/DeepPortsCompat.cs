using System;
using System.IO;
using System.Security.Cryptography;

namespace SailwindCoop.Compat
{
    /// <summary>
    /// (v0.2.32) Deep Ports swaps Terrain.terrainData AND TerrainCollider.terrainData on Gold Rock,
    /// Fort (Aestrin) and Dragon Cliffs, plus two collider meshes (Deep-Ports Patches.cs:23-38) - the
    /// physics heightfield itself. A peer without it runs aground on shoals the host dredged away, so
    /// presence + version is hard-gated. Version alone is NOT enough: the heightfields live entirely
    /// in the "deepports" AssetBundle, the DLL loads whatever bundle sits next to it, and a MISSING
    /// bundle fails SILENTLY (Patches.cs:50-54 logs and does nothing while the plugin still registers
    /// as loaded). So the token carries an 8-hex SHA-256 prefix of the bundle FILE - computed once in
    /// Init (the file is ~30 MB; never hash per-join) - and an installed-but-broken peer advertises
    /// one of three distinct tags: an 8-hex hash means healthy (bundle present and readable),
    /// "nobundle" means the file is ABSENT (vanilla terrain), and "hash?" means the file is PRESENT
    /// but unreadable (modified terrain this machine could not hash) - each mismatches the others.
    /// </summary>
    public static class DeepPortsCompat
    {
        public const string DPGuid = "com.winter.deepports";

        public static bool IsInstalled { get; private set; }
        public static string Version { get; private set; } = "";
        private static string _bundleTag = "nobundle";

        public static string ModSignature => IsInstalled ? $"DP={Version}/{_bundleTag}" : "";

        public static void Init()
        {
            IsInstalled = false;
            Version = "";
            _bundleTag = "nobundle";
            try
            {
                if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(DPGuid, out var info) || info == null)
                {
                    Plugin.Log.LogInfo("[DPCompat] Deep Ports not installed.");
                    return;
                }
                IsInstalled = true;
                Version = info.Metadata.Version.ToString();

                // Exact path Deep Ports itself loads (Patches.cs:50): PluginPath\Deep Ports\deepports
                string bundlePath = Path.Combine(BepInEx.Paths.PluginPath, "Deep Ports", "deepports");
                if (File.Exists(bundlePath))
                {
                    using (var sha = SHA256.Create())
                    // AssetBundle.LoadFromFile (Deep Ports' own Awake, which runs BEFORE ours under
                    // the soft dependency ordering) may hold the file open with a write-share handle;
                    // requesting only FileShare.Read could make every DP peer hash-fail in lockstep
                    // and void the gate. Share both read and write so we never contend with it.
                    using (var fs = new FileStream(bundlePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        var hash = sha.ComputeHash(fs);
                        _bundleTag = BitConverter.ToString(hash, 0, 4).Replace("-", "").ToLowerInvariant();
                    }
                    Plugin.Log.LogInfo($"[DPCompat] Deep Ports v{Version} detected; terrain bundle hash {_bundleTag} " +
                        "joins the handshake token (every peer must run the identical bundle).");
                }
                else
                {
                    Plugin.Log.LogWarning($"[DPCompat] Deep Ports v{Version} is installed but its 'deepports' asset " +
                        "bundle is MISSING - Deep Ports will silently run VANILLA terrain on this machine. Joins " +
                        "with working Deep Ports peers will be refused until the bundle is restored.");
                }
            }
            catch (Exception e)
            {
                // Keep IsInstalled true when we got that far: refusing is safer than passing as vanilla.
                // A peer whose bundle EXISTS but is unreadable is running MODIFIED terrain (Deep Ports
                // loaded it), so it must not advertise the same tag as a vanilla-terrain bundle-less
                // peer - "nobundle" would let two differently-broken peers match. Fail closed with a
                // third, distinct tag.
                _bundleTag = "hash?";
                Plugin.Log.LogWarning("[DPCompat] Bundle hash failed; advertising /hash? (mismatches both healthy and bundle-less peers). " + e);
            }
        }
    }
}
