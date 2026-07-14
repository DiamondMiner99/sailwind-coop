using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace SailwindCoop.Compat
{
    /// <summary>
    /// (v0.2.32) Soft-dependency bridge to winterspices' HMS Leopard. The Leopard spawns two full
    /// vanilla-component boats at runtime (roots "BOAT LEOPARD (207)(Clone)" and
    /// "BOAT CUTTER (212)(Clone)", instantiated by a FloatingOriginManager.Start prefix,
    /// HMSLeopard Patches.cs:29-150), so hull/item/damage/customization sync applies to them
    /// unchanged. What this module bridges is the MOD-OWNED state co-op must sync:
    ///   - Patches.cutterActive (public static bool) - the deployed-rowboat flag, the ONLY thing
    ///     the mod persists (SaveLoadManager.SaveModData postfix, key "com.winter.leopard")
    ///   - Gunports statics (recursive flag + the three port group lists) - one click fans out to
    ///     the whole group and toggles flooding masks with !activeSelf (Patch_OnActivate.cs), so
    ///     the wire carries GROUP intent and receivers force ABSOLUTE mask state afterwards
    ///   - the four controller types, for manual Harmony patches (attribute patches cannot target
    ///     types that may be absent at load).
    /// All reflection resolves once in Init; any failure sets SyncEnabled=false, appends "/noSync"
    /// to the token (so mixed crews still refuse) and disables every data path - the exact
    /// fail-closed contract SECompat established.
    /// </summary>
    public static class LeopardCompat
    {
        public const string LeopardGuid = "com.winter.leopard";
        private const string LeopardAssemblyName = "Leopard";

        public const string LeopardRootName = "BOAT LEOPARD (207)(Clone)";
        public const string CutterRootName = "BOAT CUTTER (212)(Clone)";

        public static bool IsInstalled { get; private set; }
        public static string Version { get; private set; } = "";
        public static bool SyncEnabled { get; private set; }

        public static string ModSignature
        {
            get
            {
                if (!IsInstalled) return "";
                return "LEO=" + Version + (SyncEnabled ? "" : "/noSync");
            }
        }

        // Leopard.Patches statics
        private static FieldInfo _fShip;          // GameObject ship (the Leopard clone)
        private static FieldInfo _fBoat;          // GameObject boat (the cutter clone)
        private static FieldInfo _fCutterActive;  // bool cutterActive
        // Leopard.Controllers.Gunports statics
        private static FieldInfo _fRecursive;     // bool recursive
        private static FieldInfo _fLower;         // List<Transform> lowerGunports
        private static FieldInfo _fUpper;         // List<Transform> upperGunports
        private static FieldInfo _fQuarter;       // List<Transform> quarterGunports
        // Controller types (manual patch targets; patches registered by LeopardSyncManager tasks)
        public static Type CutterControllerType { get; private set; }
        public static Type CutterRopeControllerType { get; private set; }
        public static Type OarControllerType { get; private set; }
        public static Type BellInteractType { get; private set; }
        // OarController members
        private static FieldInfo _fOarForce;      // public float forceAmount
        private static FieldInfo _fOarTurn;       // public float turnForce
        private static FieldInfo _fOarLeft;       // private GameObject leftOar
        private static FieldInfo _fOarRight;      // private GameObject rightOar
        private static MethodInfo _mSetOars;      // private void SetOars(bool)

        public static GameObject LeopardShip => SyncEnabled ? _fShip.GetValue(null) as GameObject : null;
        public static GameObject CutterBoat => SyncEnabled ? _fBoat.GetValue(null) as GameObject : null;

        public static bool GetCutterActive() => SyncEnabled && _fCutterActive.GetValue(null) is bool b && b;
        public static void SetCutterActive(bool value) { if (SyncEnabled) _fCutterActive.SetValue(null, value); }

        public static bool IsGunportFanoutInProgress => SyncEnabled && _fRecursive.GetValue(null) is bool r && r;

        // (v0.2.32) All five gate on SyncEnabled like every other data path in this file: a partial
        // reflection resolve (SyncEnabled=false) leaves these FieldInfo/MethodInfo handles null, and
        // future callers (oar sync) must get inert defaults, never an NRE out of a Harmony patch.
        public static float GetOarForceAmount(Component oar) => SyncEnabled ? (float)_fOarForce.GetValue(oar) : 0f;
        public static float GetOarTurnForce(Component oar) => SyncEnabled ? (float)_fOarTurn.GetValue(oar) : 0f;
        public static GameObject GetOarLeft(Component oar) => SyncEnabled ? _fOarLeft.GetValue(oar) as GameObject : null;
        public static GameObject GetOarRight(Component oar) => SyncEnabled ? _fOarRight.GetValue(oar) as GameObject : null;
        public static void InvokeSetOars(Component oar, bool set) { if (SyncEnabled) _mSetOars.Invoke(oar, new object[] { set }); }

        /// <summary>Play the Leopard's bell AudioSource (remote ring). Never calls OnActivate.</summary>
        public static void PlayBell()
        {
            var ship = LeopardShip;
            var bell = ship != null ? ship.transform.Find("boat leopard/structure_container/bell") : null;
            var audio = bell != null ? bell.GetComponent<AudioSource>() : null;
            if (audio != null) audio.Play();
        }

        public static void Init()
        {
            IsInstalled = false;
            Version = "";
            SyncEnabled = false;
            try
            {
                if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(LeopardGuid, out var info) || info == null)
                {
                    Plugin.Log.LogInfo("[LeopardCompat] HMS Leopard not installed.");
                    return;
                }
                IsInstalled = true;
                Version = info.Metadata.Version.ToString();

                var asm = info.Instance != null ? info.Instance.GetType().Assembly : null;
                asm = asm ?? AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == LeopardAssemblyName);
                if (asm == null)
                {
                    Plugin.Log.LogWarning($"[LeopardCompat] HMS Leopard v{Version} registered but its assembly is " +
                        "not loaded (load order). Leopard sync DISABLED; joins still require matching Leopard.");
                    return;
                }

                var patches = asm.GetType("Leopard.Patches");
                _fShip = patches?.GetField("ship", BindingFlags.Public | BindingFlags.Static);
                _fBoat = patches?.GetField("boat", BindingFlags.Public | BindingFlags.Static);
                _fCutterActive = patches?.GetField("cutterActive", BindingFlags.Public | BindingFlags.Static);

                var gunports = asm.GetType("Leopard.Controllers.Gunports");
                _fRecursive = gunports?.GetField("recursive", BindingFlags.Public | BindingFlags.Static);
                _fLower = gunports?.GetField("lowerGunports", BindingFlags.Public | BindingFlags.Static);
                _fUpper = gunports?.GetField("upperGunports", BindingFlags.Public | BindingFlags.Static);
                _fQuarter = gunports?.GetField("quarterGunports", BindingFlags.Public | BindingFlags.Static);

                CutterControllerType = asm.GetType("Leopard.Controllers.CutterController");
                CutterRopeControllerType = asm.GetType("Leopard.Controllers.CutterRopeController");
                OarControllerType = asm.GetType("Leopard.Controllers.OarController");
                BellInteractType = asm.GetType("Leopard.Controllers.LeopardBellInteract");

                _fOarForce = OarControllerType?.GetField("forceAmount", BindingFlags.Public | BindingFlags.Instance);
                _fOarTurn = OarControllerType?.GetField("turnForce", BindingFlags.Public | BindingFlags.Instance);
                _fOarLeft = OarControllerType?.GetField("leftOar", BindingFlags.NonPublic | BindingFlags.Instance);
                _fOarRight = OarControllerType?.GetField("rightOar", BindingFlags.NonPublic | BindingFlags.Instance);
                _mSetOars = OarControllerType?.GetMethod("SetOars", BindingFlags.NonPublic | BindingFlags.Instance);

                SyncEnabled = _fShip != null && _fBoat != null && _fCutterActive != null
                    && _fRecursive != null && _fLower != null && _fUpper != null && _fQuarter != null
                    && CutterControllerType != null && CutterRopeControllerType != null
                    && OarControllerType != null && BellInteractType != null
                    && _fOarForce != null && _fOarTurn != null && _fOarLeft != null && _fOarRight != null
                    && _mSetOars != null;

                if (SyncEnabled)
                    Plugin.Log.LogInfo($"[LeopardCompat] HMS Leopard v{Version} detected; Leopard sync enabled. " +
                        $"Handshake token [{ModSignature}].");
                else
                    Plugin.Log.LogWarning($"[LeopardCompat] HMS Leopard v{Version} detected but its internals " +
                        "changed (reflection surface incomplete). Leopard sync DISABLED; joins still require " +
                        "matching Leopard. Check for a Sailwind Co-op update.");
            }
            catch (Exception e)
            {
                SyncEnabled = false;
                if (IsInstalled)
                    Plugin.Log.LogError($"[LeopardCompat] HMS Leopard v{Version} WAS detected but resolving its " +
                        "internals threw. Leopard sync DISABLED; joins still require matching Leopard. " + e);
                else
                    Plugin.Log.LogWarning("[LeopardCompat] Detection threw before Leopard was identified. " + e);
            }
        }

        /// <summary>
        /// Manual Harmony patches on Leopard's own types (attribute patches cannot reference types
        /// that may be absent). Called from Plugin.Awake AFTER PatchAll, only when SyncEnabled.
        /// The actual patch registrations are added here by the cutter/oar/bell tasks.
        /// </summary>
        public static void ApplyPatches(HarmonyLib.Harmony harmony)
        {
            if (!SyncEnabled) return;
            try
            {
                // Cutter deploy: guests send intent instead of running the local gates (the velocity
                // gate reads a host-driven interpolated rigidbody; the recover gate reads live item
                // child counts - neither is guest-authoritative). Host runs vanilla + broadcasts.
                harmony.Patch(
                    CutterControllerType.GetMethod("OnActivate", new[] { typeof(GoPointer) }),
                    prefix: new HarmonyLib.HarmonyMethod(typeof(LeopardPatchImpl), nameof(LeopardPatchImpl.CutterDeployPrefix)),
                    postfix: new HarmonyLib.HarmonyMethod(typeof(LeopardPatchImpl), nameof(LeopardPatchImpl.CutterAnyPostfix)));
                harmony.Patch(
                    CutterRopeControllerType.GetMethod("OnActivate", Type.EmptyTypes),
                    prefix: new HarmonyLib.HarmonyMethod(typeof(LeopardPatchImpl), nameof(LeopardPatchImpl.CutterRecoverPrefix)),
                    postfix: new HarmonyLib.HarmonyMethod(typeof(LeopardPatchImpl), nameof(LeopardPatchImpl.CutterAnyPostfix)));

                // Oars: sample the rower's held keys AFTER the mod's own frame logic ran. The
                // GoPointerButton grab fields are protected - read via FieldRefAccess on the base.
                harmony.Patch(
                    OarControllerType.GetMethod("ExtraLateUpdate"),
                    postfix: new HarmonyLib.HarmonyMethod(typeof(LeopardPatchImpl), nameof(LeopardPatchImpl.OarPostfix)));

                // Bell: broadcast the ring; receivers play the AudioSource directly.
                harmony.Patch(
                    BellInteractType.GetMethod("OnActivate", new[] { typeof(GoPointer) }),
                    postfix: new HarmonyLib.HarmonyMethod(typeof(LeopardPatchImpl), nameof(LeopardPatchImpl.BellPostfix)));

                Plugin.Log.LogInfo("[LeopardCompat] Cutter controller patches applied.");
            }
            catch (Exception e)
            {
                // FAIL CLOSED (final review): a patch failure means guest clicks would run the mod's
                // LOCAL gates (guest-only cutter deploys). SyncEnabled=false kills every data path AND
                // flips the handshake token to /noSync - the token is composed lazily at lobby time,
                // long after Awake, so this propagates. Same contract as Init().
                SyncEnabled = false;
                Plugin.Log.LogError("[LeopardCompat] Manual patching failed; Leopard sync DISABLED " +
                    "(token now advertises /noSync; joins still require matching Leopard). " + e);
            }
        }

        /// <summary>Manual-patch bodies (attributes can't target maybe-absent types).</summary>
        internal static class LeopardPatchImpl
        {
            public static bool CutterDeployPrefix()
            {
                if (!Plugin.IsMultiplayer || Plugin.IsHost) return true;
                Sync.LeopardSyncManager.Instance?.RequestCutter(true);
                return false; // guest never runs the local gates
            }

            public static bool CutterRecoverPrefix()
            {
                if (!Plugin.IsMultiplayer) return true;
                if (Plugin.IsHost)
                {
                    // (review) Same policy as guest requests: never recover the cutter out from
                    // under a crew member - the guest-side rescue guard makes it survivable, but
                    // dumping someone in the water on a host click is not a feature.
                    if (Sync.LeopardSyncManager.AnyPlayerAboardCutter())
                    {
                        Plugin.Notify("Cutter recover refused - someone is aboard it", 5f);
                        return false;
                    }
                    return true;
                }
                Sync.LeopardSyncManager.Instance?.RequestCutter(false);
                return false;
            }

            // Host clicked deploy/recover itself: when vanilla actually ran (gates included),
            // broadcast the result. __runOriginal is false when a prefix refused the click (the
            // host aboard-gate) - state unchanged, nothing to broadcast. Guest requests still get
            // their explicit broadcast from OnCutterState.
            public static void CutterAnyPostfix(bool __runOriginal)
            {
                if (!__runOriginal) return;
                if (!Plugin.IsMultiplayer || !Plugin.IsHost) return;
                Sync.LeopardSyncManager.Instance?.BroadcastCutterState();
            }

            private static readonly HarmonyLib.AccessTools.FieldRef<GoPointerButton, GoPointer> StickyClickedByRef =
                HarmonyLib.AccessTools.FieldRefAccess<GoPointerButton, GoPointer>("stickyClickedBy");
            private static readonly HarmonyLib.AccessTools.FieldRef<GoPointerButton, bool> IsClickedRef =
                HarmonyLib.AccessTools.FieldRefAccess<GoPointerButton, bool>("isClicked");

            public static void OarPostfix(object __instance)
            {
                if (!Plugin.IsMultiplayer) return;
                var button = __instance as GoPointerButton;
                if (button == null) return;
                bool grabbed = StickyClickedByRef(button) != null || IsClickedRef(button);
                Sync.LeopardSyncManager.Instance?.SampleAndSendOarInput(button, grabbed);
            }

            public static void BellPostfix()
            {
                if (!Plugin.IsMultiplayer) return;
                ulong self = Steamworks.SteamClient.SteamId.Value;
                Plugin.NetworkManager.SendToAllReliable(
                    Networking.Packets.PacketType.BellRing, w => w.Write(self));
            }
        }

        // === Gunport group helpers (used by TrapdoorSyncManager) ===

        /// <summary>
        /// "lower"/"upper"/"quarter" for a Leopard gunport trapdoor name, else null.
        /// Name-substring match only - callers MUST also check the boat is the Leopard root
        /// (TrapdoorSyncManager does: it compares boat.gameObject.name against LeopardRootName)
        /// before treating the trapdoor as a gunport.
        /// </summary>
        public static string GunportGroupOf(string trapdoorName)
        {
            if (!SyncEnabled || trapdoorName == null || !trapdoorName.Contains("gunport")) return null;
            if (trapdoorName.Contains("lower")) return "lower";
            if (trapdoorName.Contains("upper")) return "upper";
            if (trapdoorName.Contains("quarter")) return "quarter";
            return null;
        }

        private static System.Collections.Generic.List<Transform> GroupList(string group)
        {
            if (!SyncEnabled) return null; // a peer that declared itself /noSync must never physically toggle gunports
            FieldInfo f = group == "lower" ? _fLower : group == "upper" ? _fUpper : group == "quarter" ? _fQuarter : null;
            return f?.GetValue(null) as System.Collections.Generic.List<Transform>;
        }

        public static bool? GetGunportGroupOpen(string group)
        {
            var list = GroupList(group);
            if (list == null || list.Count == 0 || list[0] == null) return null;
            var td = list[0].GetComponent<GPButtonTrapdoor>();
            return td != null ? td.IsOpen() : (bool?)null;
        }

        /// <summary>
        /// Toggle the whole group by invoking OnActivate() on ONE port - the Leopard's own prefix
        /// (Patch_OnActivate) then fans out to the siblings and runs the mask/audio/overflow logic,
        /// i.e. the receiver reproduces the sender's exact code path. Returns true when a toggle was
        /// issued (state differed). The caller MUST hold TrapdoorSyncManager.IsApplyingRemoteState
        /// so co-op's own postfix does not echo, and MUST call ForceGunportAbsolutes afterwards.
        /// </summary>
        public static bool ApplyGunportGroup(string group, bool open)
        {
            var list = GroupList(group);
            if (list == null || list.Count == 0 || list[0] == null) return false;
            var td = list[0].GetComponent<GPButtonTrapdoor>();
            if (td == null || td.IsOpen() == open) return false;
            td.OnActivate(); // no-ops while inMotion; TrapdoorSyncManager retries on divergence
            return true;
        }

        /// <summary>
        /// Force the flooding masks / overflow emitters / interior triggers to the ABSOLUTE state
        /// for a group. The mod toggles all of these with !activeSelf (Patch_OnActivate.cs:35-45,
        /// Gunports.ToggleOverflows), so any missed/echoed toggle would INVERT a guest's flooding
        /// forever. Mapping verified against the shipped v1.4.0 prefab's baked m_IsActive states
        /// (closed baseline: half-mask OFF, full-mask ON, lower overflows OFF, upper overflows ON,
        /// both interior triggers ON). Quarter ports have no side effects.
        /// </summary>
        public static void ForceGunportAbsolutes(string group, bool open)
        {
            var ship = LeopardShip;
            if (ship == null) return;
            if (group == "lower")
            {
                SetActivePath(ship.transform, "boat leopard/mask water half", open);
                SetActivePath(ship.transform, "boat leopard/mask water full", !open);
                for (int i = 1; i <= 4; i++)
                    SetActivePath(ship.transform, $"overflow particles lower {i}", open);
                for (int i = 1; i <= 5; i++)
                    SetActivePath(ship.transform, $"overflow particles upper {i}", !open);
                // ACCEPTED WRINKLE (spec 2026-07-14): the Leopard's own ToggleAudio transitions the
                // AudioMixers.instance indoor/outdoor snapshot on whichever machine runs the fan-out,
                // so a REMOTE player's gunport toggle briefly touches the local player's audio mix.
                // We deliberately do NOT re-fire audio here - the trigger's SetActive below IS the
                // state, and vanilla InteriorEffectsTrigger re-asserts the correct snapshot the next
                // time the local player crosses a trigger boundary. Not worth fighting harder.
                SetActivePath(ship.transform, "boat leopard/structure_container/interior trigger 2", !open);
            }
            else if (group == "upper")
            {
                SetActivePath(ship.transform, "boat leopard/structure_container/interior trigger 3", !open);
            }
            // quarter: fan-out only, no masks/audio/overflows
        }

        private static readonly System.Collections.Generic.HashSet<string> _warnedPaths = new System.Collections.Generic.HashSet<string>();

        private static void SetActivePath(Transform root, string path, bool active)
        {
            var t = root.Find(path);
            if (t == null)
            {
                // A wrong hardcoded path would silently desync a guest's flooding visuals; make a
                // future Leopard prefab-path regression self-reporting (once per path, not per toggle).
                if (_warnedPaths.Add(path))
                    Plugin.Log.LogWarning($"[LeopardCompat] Gunport absolute-state path not found: '{path}' (Leopard prefab changed? flooding visuals may desync)");
                return;
            }
            if (t.gameObject.activeSelf != active) t.gameObject.SetActive(active);
        }
    }
}
