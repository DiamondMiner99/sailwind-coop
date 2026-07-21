using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using SailwindCoop.Debug;
using SailwindCoop.Networking;
using SailwindCoop.Networking.Packets;
using SailwindCoop.Player;
using SailwindCoop.Sync;
using SailwindCoop.UI;
using Steamworks;
using UnityEngine;

namespace SailwindCoop
{
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    [BepInDependency(Compat.SECompat.SEGuid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(Compat.SCFCompat.SCFGuid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(Compat.NANDTweaksCompat.NTGuid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(Compat.DeepPortsCompat.DPGuid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(Compat.TowableBoatsCompat.TBGuid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(Compat.LeopardCompat.LeopardGuid, BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGUID = "com.sailwindcoop.mod";
        public const string PluginName = "Sailwind Coop";
        // BUMP THIS FOR EVERY NEW ZIP - even a one-byte / packaging-only change (e.g. adding a bundled
        // DLL counts). Every shipped zip MUST carry a unique version (0.2.1, 0.2.2, 0.2.3, ...) so two
        // machines can NEVER silently differ, and the friend zip filename is named to match. Shown in
        // the log, lobby data, handshake, verbose-log header and the F8 overlay - so "are we on the same
        // build?" is answerable at a glance on both screens.
        // MUST stay System.Version-parseable (major.minor[.build]): BepInEx 5 does NOT strip semver
        // pre-release suffixes - a "-alpha" tag makes the chainloader reject the plugin ("version is
        // invalid") and skip it entirely. The "alpha" status lives as prose in the README/INSTALL only.
        // Must be a valid System.Version (BepInPlugin parses it) - no "-dev"/suffix or the plugin fails to
        // load. This is the v0.2.33 build (invite-log SteamID + toast reword); shows as 0.2.33.
        public const string PluginVersion = "0.2.33";

        public static Plugin Instance { get; private set; }
        public static ManualLogSource Log { get; private set; }

        // Crew cap (host + guests) for the Steam lobby. Default 8 (host + 7). Read by
        // SteamLobbyManager.MaxPlayers, which feeds Steam's lobby max-members argument.
        public static ConfigEntry<int> MaxPlayersConfig { get; private set; }
        public static ConfigEntry<bool> AllowCrewInvitesConfig { get; private set; }
        public static ConfigEntry<bool> BedRestConfig { get; private set; }
        // Crew spending feed (UI.TradeFeed): receiver-side gates - turning them down/off changes only
        // THIS machine's feed lines and quiet coin cue, never what the host broadcasts.
        public static ConfigEntry<bool> SpendingFeedConfig { get; private set; }
        public static ConfigEntry<float> SpendingFeedVolumeConfig { get; private set; }
        // Controller stick-drift deadzone (vanilla bug mitigation): vanilla feeds the RAW analog stick into
        // the pointer's keyboard delta with no deadzone and no deltaTime scaling; the winch then divides by
        // deltaTime, amplifying idle stick drift ~20-60x into a constant let-out on any grabbed winch.
        // Read by ControlPatches.ControllerDeadzonePatch; 0 disables.
        public static ConfigEntry<float> ControllerDeadzoneConfig { get; private set; }
        // (v0.2.25) Item buoyancy restore (v0.38 vanilla REGRESSION mitigation, LOCAL-ONLY physics):
        // older game builds floated free items (ToggleCollider did `floater.enabled = state`); v0.38
        // regressed that to a hard-coded disable every fixed frame, so dropped items/crates sink even
        // in singleplayer despite their authored floaterHeight. Read by ItemBuoyancyPatches; default
        // TRUE = restore the pre-0.38 floating players expect (set false for current-build sinking).
        public static ConfigEntry<bool> RestoreItemBuoyancyConfig { get; private set; }

        // Crouch pose tuning (v0.2.25), read live every frame by RemotePlayerManager + LocalPlayerBody so
        // they can be tuned in-game with Configuration Manager. The crouch is a SQUAT: the hips/body drop and
        // per-leg 2-bone IK re-plants the feet at their standing spot (feet stay on the deck at any depth), so
        // the knobs are IK-appropriate (drop depth, torso lean, arm bend, stride cut, knee-forward flip)
        // rather than raw joint angles. All applied * the 0..1 crouch amount.
        public static ConfigEntry<float> CrouchDropMetersConfig { get; private set; }
        public static ConfigEntry<float> CrouchTorsoLeanDegConfig { get; private set; }
        public static ConfigEntry<float> CrouchArmBendDegConfig { get; private set; }
        public static ConfigEntry<float> CrouchStrideCutConfig { get; private set; }
        public static ConfigEntry<float> CrouchKneeForwardConfig { get; private set; }

        // LOOK-LEAN pose tuning (torso pitches on the hips toward where the player looks vertically, like a
        // Phasmophobia player model). Read live every frame by RemotePlayerManager + LocalPlayerBody. Applies
        // in ALL states (standing, walking, crouched) and COMPOSES additively with the crouch torso fold.
        public static ConfigEntry<float> LookPitchScaleConfig { get; private set; }
        public static ConfigEntry<float> LookPitchMaxDegConfig { get; private set; }

        // Crew weight: kg each REMOTE crew member adds to the boat they stand on (vanilla models every
        // person, host included, at 160). HOST-ONLY physics (BoatMass.UpdateMass patch early-returns on
        // clients), so only the host's value is ever used - nothing to sync; tunable live by the host.
        public static ConfigEntry<float> CrewMemberWeightConfig { get; private set; }

        // (v0.2.27) Version handshake escape hatch: when true, a mod-version mismatch between host and
        // guest is warned about instead of refused. Off by default - the wire format is unversioned and
        // mixed builds can desync silently. Checked on BOTH sides (guest lobby-data pre-check + host
        // Handshake gate), so both peers must enable it to actually play mismatched.
        public static ConfigEntry<bool> AllowVersionMismatchConfig { get; private set; }

        // (v0.2.32) Mod-set gate escape hatch, split out of AllowVersionMismatch: one flag unlocking
        // BOTH the version gate and the mod gate was too blunt with six gated mods. Off by default.
        // Checked on BOTH sides, so both peers must enable it to actually play mismatched.
        public static ConfigEntry<bool> AllowModMismatchConfig { get; private set; }

        public static SteamLobbyManager LobbyManager => SteamLobbyManager.Instance;
        public static P2PNetworkManager NetworkManager { get; private set; }
        public static RemotePlayerManager RemotePlayerManager { get; private set; }
        public static BoatSyncManager BoatSyncManager { get; private set; }
        public static ControlSyncManager ControlSyncManager { get; private set; }
        public static PushSyncManager PushSyncManager { get; private set; }
        public static WeatherSyncManager WeatherSyncManager { get; private set; }
        public static TimeSyncManager TimeSyncManager { get; private set; }
        public static SurvivalSyncManager SurvivalSyncManager { get; private set; }
        public static ItemSyncManager ItemSyncManager { get; private set; }
        public static SleepSyncManager SleepSyncManager { get; private set; }
        public static DamageSyncManager DamageSyncManager { get; private set; }
        public static ShipyardSyncManager ShipyardSyncManager { get; private set; }
        public static TrapdoorSyncManager TrapdoorSyncManager { get; private set; }
        public static LeopardSyncManager LeopardSyncManager { get; private set; }
        public static MissionSyncManager MissionSyncManager { get; private set; }
        public static EconomySyncManager EconomySyncManager { get; private set; }
        public static TradingSyncManager TradingSyncManager { get; private set; }
        public static FishingSyncManager FishingSyncManager { get; private set; }
        public static ChipLogSyncManager ChipLogSyncManager { get; private set; }
        public static NavigationSyncManager NavigationSyncManager { get; private set; }
        public static ChartKitGhostManager ChartKitGhostManager { get; private set; }
        public static CookingSyncManager CookingSyncManager { get; private set; }
        public static NPCBoatSyncManager NPCBoatSyncManager { get; private set; }
        public static PlayerSyncManager PlayerSyncManager { get; private set; }
        public static CleaningSyncManager CleaningSyncManager { get; private set; }
        public static PerformanceProfiler Profiler { get; private set; }
        public static CommandProcessor CommandProcessor { get; private set; }
        public static bool IsMultiplayer => LobbyManager.IsInLobby;
        public static bool IsHost => LobbyManager.IsHost;
        // PER-PEER JoinPending: a guest's join state-send can be queued/in-flight on the host (esp. a
        // DEFERRED join that waits for the host to finish a sleep handshake). The freshly-joined guest
        // streams no position packets until its ~15-20s load completes, so the sleep guest-liveness watchdog
        // must skip THAT peer, or it would false-abort a healthy host sleep (see SleepSyncManager.Update).
        // Tracking it per-peer (instead of one global flag) means a deferred join blinds the watchdog only
        // for the JOINING peer, not the whole crew - so a DIFFERENT crewmate freezing is still caught.
        private static readonly System.Collections.Generic.HashSet<Steamworks.SteamId> _joinPendingPeers =
            new System.Collections.Generic.HashSet<Steamworks.SteamId>();
        public static bool IsJoinPendingFor(Steamworks.SteamId id) => _joinPendingPeers.Contains(id);
        // Kept for any caller that still reads the crew-wide "is any join pending?" question.
        public static bool JoinPending => _joinPendingPeers.Count > 0;
        // True if we joined someone else's lobby (a guest). Stable across Steam lobby-ownership transfer,
        // unlike !IsHost. Used to never write the shared host world to the guest's own save slot on quit.
        public static bool IsGuest => _joinedAsGuest;
        // True only when a guest is ACTUALLY connected (remote avatar spawned), not merely "in a lobby".
        // Sleep gates on this so hosting a lobby alone (or before a guest joins) sleeps via the vanilla flow.
        public static bool HasConnectedGuest =>
            SailwindCoop.Player.RemotePlayerManager.Instance != null &&
            SailwindCoop.Player.RemotePlayerManager.Instance.HasRemotePlayer;

        // N-player (Phase 0, additive): number of connected peers (guests for a host, the host for a
        // guest). At N=1 this is 0 or 1 and call sites still use HasConnectedGuest; later phases migrate
        // count-based logic to this. Does NOT change HasConnectedGuest's semantics.
        public static int ConnectedGuestCount => NetworkManager?.ConnectedPeers.Count ?? 0;

        // Convenience alias matching HasConnectedGuest exactly (any crew connected at all). Additive;
        // later phases may point this at a multi-avatar check.
        public static bool AnyConnectedCrew => HasConnectedGuest;

        private Harmony _harmony;
        private bool _steamInitialized;

        // Session role: true if we joined someone else's lobby (a guest). Stable across Steam lobby-ownership
        // transfer - when the host leaves, Steam can hand the guest ownership, which would scramble IsHost.
        private static bool _joinedAsGuest;
        private static bool _endingGuestSession; // guard so the guest warn-and-quit runs at most once

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            Log.LogInfo($"{PluginName} v{PluginVersion} loading...");

            // (v0.2.32) Mod-compat soft-detect: every module must init in Awake so the composed
            // lobby-data / handshake token (CompatRegistry.ModSignature) is ready before any lobby
            // is created or joined. InitAll runs the six module Inits in segment order AND clears
            // any prematurely cached composed token (see CompatRegistry.InitAll).
            Compat.CompatRegistry.InitAll();

            // Crew cap: host + up to 7 guests. Clamped to a sane Steam-lobby range. Bound before Steam
            // init so SteamLobbyManager.MaxPlayers reads the configured value when a lobby is created.
            MaxPlayersConfig = Config.Bind(
                "Coop",
                "MaxPlayers",
                8,
                new ConfigDescription(
                    "Maximum crew on one shared boat, including the host (default 8 = host + 7 guests).",
                    new AcceptableValueRange<int>(2, 8)));
            Log.LogInfo($"Crew cap (MaxPlayers): {MaxPlayersConfig.Value}");

            // ACCESS CONTROL (2026-07-02 report: a guest's Steam friend - a stranger to the HOST -
            // walked into the crew): the lobby is now created PRIVATE (invite-only), and by default the
            // host additionally refuses to admit anyone the HOST didn't personally invite (a guest can
            // still fire Steam-overlay invites; those joiners are turned away at admission). Hosts who
            // trust their crew to bring friends can flip this on.
            AllowCrewInvitesConfig = Config.Bind(
                "Coop",
                "AllowCrewInvites",
                false,
                "When true, people invited by ANY crew member may join. When false (default), only players the HOST invited are admitted to the session.");
            Log.LogInfo($"AllowCrewInvites: {AllowCrewInvitesConfig.Value}");

            // Crew spending feed: bottom-right killfeed line (+ quiet coin cue for OTHER crew members'
            // trades) whenever anyone in the crew buys or sells against the shared wallet.
            SpendingFeedConfig = Config.Bind(
                "Coop",
                "SpendingFeed",
                true,
                "Show a bottom-right feed line (and play a quiet coin sound for other crew members' trades) whenever anyone in the crew buys or sells. Receiver-side: affects only this machine.");
            SpendingFeedVolumeConfig = Config.Bind(
                "Coop",
                "SpendingFeedVolume",
                0.35f,
                new ConfigDescription(
                    "Volume of the quiet coin sound played for OTHER crew members' trades (your own trades already play the vanilla gold sound at full volume).",
                    new AcceptableValueRange<float>(0f, 1f)));

            // Controller stick-drift deadzone: vanilla GoPointerMovement.ApplyKeyboardRotation adds the raw
            // gamepad stick axes to keyboardDelta UNSCALED by deltaTime (the W/S key terms ARE dt-scaled),
            // and GPButtonRopeWinch.Update divides that delta BY deltaTime - so a tiny idle stick drift
            // becomes a constant let-out on any grabbed winch (and a slow wheel creep). Strip sub-deadzone
            // stick input before consumers read it (ControlPatches.ControllerDeadzonePatch).
            ControllerDeadzoneConfig = Config.Bind(
                "Coop",
                "ControllerDeadzone",
                0.15f,
                new ConfigDescription(
                    "Suppress gamepad stick input below this magnitude before it feeds winches and the steering wheel (vanilla has no deadzone, so idle stick drift slowly lets sails out / creeps the wheel). 0 disables.",
                    new AcceptableValueRange<float>(0f, 0.9f)));

            // (v0.2.25) Item buoyancy restore: items floating IS vanilla behavior - older builds did
            // `floater.enabled = state` in ItemRigidbody.ToggleCollider, but the current v0.38 build
            // regressed it to a hard-coded disable that runs every fixed frame (verified in the live IL;
            // looks like shipped debug leftovers), so dropped items sink even in singleplayer. ON by
            // default because it restores the floating players expect; the postfix re-enables the
            // floater for loose items only (not held, not resting on a boat, not stowed, not already
            // deep underwater). Purely local: no wire change, each machine floats or sinks its own items.
            RestoreItemBuoyancyConfig = Config.Bind(
                "Coop",
                "RestoreItemBuoyancy",
                true,
                "Re-enable floating for dropped items. Older Sailwind builds floated free items; the current v0.38 build regressed this to a hard-coded floater disable every physics frame, so dropped items/crates sink even in singleplayer. Default on = restore the pre-0.38 floating everyone expects. Applies to THIS machine only; other crew members see their own local physics either way. Set false for exact current-build (sinking) behavior.");
            Log.LogInfo($"RestoreItemBuoyancy: {RestoreItemBuoyancyConfig.Value}");

            // Crouch pose tuning - live-editable (Configuration Manager). The crouch is a SQUAT: the body
            // drops and 2-bone leg IK re-plants the feet at their standing spot. All applied * the 0..1 crouch
            // amount. Shared by remote avatars and your own third-person (orbit-cam) body.
            CrouchDropMetersConfig = Config.Bind("Crouch", "CrouchDropMeters", 0.6f,
                new ConfigDescription("Squat depth: how far the hips/body drop at full crouch. The leg IK keeps the feet planted on the deck at any depth, so the head comes down toward the camera without the feet clipping through.",
                    new AcceptableValueRange<float>(0f, 1.2f)));
            CrouchTorsoLeanDegConfig = Config.Bind("Crouch", "CrouchTorsoLeanDeg", 28f,
                new ConfigDescription("Forward torso fold at full crouch (about the body's world right axis) - brings the chest/head down and forward toward the camera. Negative leans back.",
                    new AcceptableValueRange<float>(-80f, 80f)));
            CrouchArmBendDegConfig = Config.Bind("Crouch", "CrouchArmBendDeg", 45f,
                new ConfigDescription("Elbow flex for a ready/tactical arm pose at full crouch (composed on top of the walk arm swing; the upper arms also raise slightly). Negative flexes the other way.",
                    new AcceptableValueRange<float>(-120f, 120f)));
            CrouchStrideCutConfig = Config.Bind("Crouch", "CrouchStrideCut", 0.5f,
                new ConfigDescription("Fraction the walk stride shrinks while crouched (crouch-walk).",
                    new AcceptableValueRange<float>(0f, 0.95f)));
            CrouchKneeForwardConfig = Config.Bind("Crouch", "CrouchKneeForward", 1f,
                new ConfigDescription("Knee-forward pole sign for the leg IK. +1 bends the knees FORWARD (a squat). If the knees bend the wrong way (backward), set this to -1 to flip the pole live.",
                    new AcceptableValueRange<float>(-1f, 1f)));

            // LOOK-LEAN tuning - live-editable (Configuration Manager). The avatar's upper body (Spine_01 ->
            // chest/head/arms) pitches on the hips toward where the player looks vertically, in every state
            // (standing/walking/crouched), composed on top of the crouch fold. Shared by remote avatars and
            // your own third-person (orbit-cam) body.
            LookPitchScaleConfig = Config.Bind("Crouch", "LookPitchScale", 0.9f,
                new ConfigDescription("Torso look-lean: fraction of your vertical look angle the upper body pitches on the hips (1.0 = follows your look 1:1). Looking DOWN folds the torso forward, looking UP leans it back. Set NEGATIVE to flip the direction if it bends the wrong way in-game.",
                    new AcceptableValueRange<float>(-2f, 2f)));
            LookPitchMaxDegConfig = Config.Bind("Crouch", "LookPitchMaxDeg", 55f,
                new ConfigDescription("Clamp (degrees) on the torso look-lean so it never over-bends up or down. Must exceed the crouch fold (~28 deg) for the torso to lean BACK past vertical while crouched + looking up.",
                    new AcceptableValueRange<float>(0f, 90f)));

            CrewMemberWeightConfig = Config.Bind("Coop", "CrewMemberWeightKg", 90f,
                new ConfigDescription("Weight (kg) each REMOTE crew member adds to the boat they stand on. Vanilla models every person (the host too) at 160, so several people crowding one side of a small hull pile up a big tipping moment and can flip it. Lower this to reduce that heel/flip. HOST-ONLY: only the host computes crew weight (clients just receive the resulting boat motion), so only the host's value matters - safe to tune live mid-session.",
                    new AcceptableValueRange<float>(0f, 200f)));

            BedRestConfig = Config.Bind("Coop", "BedRest", true,
                "Lying in a bed while AWAKE (e.g. waiting for the rest of the crew, or just going AFK) slowly restores sleep up to 60/100 and freezes hunger/thirst/protein/vitamin drain. Real crew sleep is still the only way to rest fully. Per-player and local-only - each machine applies its own value.");

            AllowVersionMismatchConfig = Config.Bind("Coop", "AllowVersionMismatch", false,
                "Let players on a DIFFERENT mod version join anyway (both sides get a warning instead of a refusal). The network format is not versioned - mixed builds can desync silently or corrupt a session, so leave this off unless you know the two builds are wire-compatible. Both the host and the mismatched guest must enable it. Gameplay-mod differences are gated separately by Coop.AllowModMismatch.");

            AllowModMismatchConfig = Config.Bind("Coop", "AllowModMismatch", false,
                "Let players whose GAMEPLAY MOD SET differs from the host's join anyway (warning instead of refusal). Covers Shipyard Expansion, Sail Collision Fix, NAND Tweaks simulation options, Deep Ports (including its terrain bundle), Towable Boats and HMS Leopard. Mixed mod sets desync physics, terrain and rigs - leave this off unless you know exactly what differs. Both the host and the mismatched guest must enable it.");

            try
            {
                _harmony = new Harmony(PluginGUID);
                _harmony.PatchAll();
                PatchVerifier.Verify(_harmony);
                // (v0.2.32) Manual patches on HMS Leopard's own controller types (attribute patches
                // cannot reference maybe-absent types). Hard no-op when Leopard is absent.
                Compat.LeopardCompat.ApplyPatches(_harmony);
                Log.LogInfo($"Harmony patches applied successfully");
            }
            catch (System.Exception ex)
            {
                Log.LogError($"Harmony patching failed: {ex.Message}");
                Log.LogError($"Stack trace: {ex.StackTrace}");
            }

            try
            {
                InitializeSteam();
            }
            catch (System.Exception ex)
            {
                Log.LogError($"Steam initialization failed: {ex.Message}");
                Log.LogError($"Stack trace: {ex.StackTrace}");
            }

            try
            {
                InitializeNetworking();
            }
            catch (System.Exception ex)
            {
                Log.LogError($"Networking initialization failed: {ex.Message}");
                Log.LogError($"Stack trace: {ex.StackTrace}");
            }

            // Add debug overlay and managers
            try
            {
                Profiler = gameObject.AddComponent<PerformanceProfiler>();
                Log.LogInfo("PerformanceProfiler added (F7 for stats)");
                gameObject.AddComponent<DebugOverlay>();
                Log.LogInfo("DebugOverlay added");
                RemotePlayerManager = gameObject.AddComponent<RemotePlayerManager>();
                Log.LogInfo("RemotePlayerManager added");
                gameObject.AddComponent<SailwindCoop.Player.LocalPlayerBody>();
                Log.LogInfo("LocalPlayerBody added (your own body in third person)");
                BoatSyncManager = gameObject.AddComponent<BoatSyncManager>();
                Log.LogInfo("BoatSyncManager added");
                ControlSyncManager = gameObject.AddComponent<ControlSyncManager>();
                Log.LogInfo("ControlSyncManager added");
                PushSyncManager = gameObject.AddComponent<PushSyncManager>();
                Log.LogInfo("PushSyncManager added");
                WeatherSyncManager = gameObject.AddComponent<WeatherSyncManager>();
                Log.LogInfo("WeatherSyncManager added");
                TimeSyncManager = gameObject.AddComponent<TimeSyncManager>();
                Log.LogInfo("TimeSyncManager added");
                SurvivalSyncManager = gameObject.AddComponent<SurvivalSyncManager>();
                Log.LogInfo("SurvivalSyncManager added");
                ItemSyncManager = gameObject.AddComponent<ItemSyncManager>();
                Log.LogInfo("ItemSyncManager added");
                SleepSyncManager = gameObject.AddComponent<SleepSyncManager>();
                Log.LogInfo("SleepSyncManager added");
                DamageSyncManager = gameObject.AddComponent<DamageSyncManager>();
                Log.LogInfo("DamageSyncManager added");
                ShipyardSyncManager = gameObject.AddComponent<ShipyardSyncManager>();
                Log.LogInfo("ShipyardSyncManager added");
                TrapdoorSyncManager = gameObject.AddComponent<TrapdoorSyncManager>();
                Log.LogInfo("TrapdoorSyncManager added");
                LeopardSyncManager = gameObject.AddComponent<LeopardSyncManager>();
                Log.LogInfo("LeopardSyncManager added");
                MissionSyncManager = gameObject.AddComponent<MissionSyncManager>();
                Log.LogInfo("MissionSyncManager added");
                EconomySyncManager = gameObject.AddComponent<EconomySyncManager>();
                Log.LogInfo("EconomySyncManager added");
                TradingSyncManager = gameObject.AddComponent<TradingSyncManager>();
                Log.LogInfo("TradingSyncManager added");
                FishingSyncManager = gameObject.AddComponent<FishingSyncManager>();
                Log.LogInfo("FishingSyncManager added");
                ChipLogSyncManager = gameObject.AddComponent<ChipLogSyncManager>();
                Log.LogInfo("ChipLogSyncManager added");
                NavigationSyncManager = gameObject.AddComponent<NavigationSyncManager>();
                Log.LogInfo("NavigationSyncManager added");
                ChartKitGhostManager = gameObject.AddComponent<ChartKitGhostManager>();
                Log.LogInfo("ChartKitGhostManager added");
                CookingSyncManager = gameObject.AddComponent<CookingSyncManager>();
                Log.LogInfo("CookingSyncManager added");
                NPCBoatSyncManager = gameObject.AddComponent<NPCBoatSyncManager>();
                Log.LogInfo("NPCBoatSyncManager added");
                PlayerSyncManager = gameObject.AddComponent<PlayerSyncManager>();
                Log.LogInfo("PlayerSyncManager added");
                CleaningSyncManager = gameObject.AddComponent<CleaningSyncManager>();
                Log.LogInfo("CleaningSyncManager added");
            }
            catch (System.Exception ex)
            {
                Log.LogError($"Component initialization failed: {ex.Message}");
                Log.LogError($"Stack trace: {ex.StackTrace}");
            }

            CommandProcessor = new CommandProcessor(Log);

            Log.LogInfo($"{PluginName} loaded successfully!");
        }

        private void InitializeSteam()
        {
            _steamInitialized = LobbyManager.Initialize();
            if (_steamInitialized)
            {
                Log.LogInfo($"Steam user: {SteamClient.Name} ({SteamClient.SteamId})");
            }
            else
            {
                Log.LogError("Failed to initialize Steam. Coop features will be disabled.");
            }
        }

        /// True once the mod's own Steam client initialized (at Awake or via a later lazy retry). When
        /// false, co-op is non-functional and the menu buttons would otherwise be silently dead.
        public static bool SteamReady => Instance != null && Instance._steamInitialized;

        /// Lazily finishes co-op init if it was skipped/failed at startup (e.g. Steam wasn't ready when
        /// Awake ran on a slower machine - a likely cause of "the menu shows but every lobby button does
        /// nothing"). Instant + idempotent on the healthy path (returns true immediately). On an
        /// unrecoverable failure it surfaces the reason in-game (instead of a dead button) and returns
        /// false. Call this before any lobby action.
        public static bool EnsureCoopReady()
        {
            var p = Instance;
            if (p == null) return false;

            if (!p._steamInitialized)
            {
                Log.LogWarning("Co-op not initialized yet - attempting a late Steam init...");
                try { p.InitializeSteam(); }
                catch (System.Exception ex) { Log.LogError($"Late Steam init failed: {ex.Message}"); }

                // InitializeNetworking early-returns when Steam isn't up, so it was skipped at Awake.
                // Now that Steam is up, build the networking layer it skipped (exactly once).
                if (p._steamInitialized && NetworkManager == null)
                {
                    try { p.InitializeNetworking(); Log.LogInfo("Late networking init complete."); }
                    catch (System.Exception ex) { Log.LogError($"Late networking init failed: {ex.Message}"); }
                }
            }

            if (!p._steamInitialized)
            {
                Notify(LobbyManager.InitFailureHint(), 8f);
                return false;
            }
            return true;
        }

        // Tracks the last disconnect notification time so the clean (OnPlayerLeft) and P2P-drop
        // (OnDisconnected) paths don't both toast for the same leave.
        private static float _lastDisconnectNotifyTime = -10f;

        // F8-overlay ping loop cadence (seconds, realtime clock so pauses don't stall it).
        private const float PingInterval = 2f;
        private static float _lastPingSendTime = -10f;

        // #8: the P2P-disconnect handler, stored so it can be RE-SUBSCRIBED each time OnLobbyLeft recreates the
        // NetworkManager. A one-time inline subscription was bound to the first (now shut-down) manager, so every
        // ungraceful-drop cleanup was dead in session 2+.
        private System.Action<Steamworks.SteamId> _onPeerDisconnected;

        /// <summary>
        /// Show an on-screen message via the game's NotificationUi, falling back to the BepInEx
        /// log if the UI isn't ready yet (e.g. at the main menu before a save is loaded).
        /// </summary>
        public static void Notify(string message, float duration = 4f)
        {
            if (NotificationUi.instance != null)
                NotificationUi.instance.ShowNotification(WrapForScroll(message), duration);
            else
                Log.LogInfo($"[Coop] {message}");
        }

        // The vanilla NotificationUi paints into a fixed-width parchment via a NON-wrapping TextMesh, so a long
        // co-op line (e.g. "Server opened - waiting for crew (close from the menu)") spills off both edges.
        // Resizing that shared mesh would warp every vanilla notification, so instead we soft-wrap our OWN text
        // on word boundaries at ~NotifyWrapWidth chars (TextMesh honours explicit '\n'). Short messages (the
        // common case, and all vanilla ones) are returned unchanged, so normal notifications look identical.
        private const int NotifyWrapWidth = 36; // chars/line that fit the notification scroll (tune to taste)
        private static string WrapForScroll(string message)
        {
            if (string.IsNullOrEmpty(message) || message.Length <= NotifyWrapWidth) return message;
            var sb = new System.Text.StringBuilder(message.Length + 8);
            int lineLen = 0;
            foreach (var word in message.Split(' '))
            {
                if (lineLen > 0 && lineLen + 1 + word.Length > NotifyWrapWidth)
                {
                    sb.Append('\n');
                    lineLen = 0;
                }
                else if (lineLen > 0)
                {
                    sb.Append(' ');
                    lineLen++;
                }
                sb.Append(word);
                lineLen += word.Length;
            }
            return sb.ToString();
        }

        private void InitializeNetworking()
        {
            if (!_steamInitialized) return;

            NetworkManager = new P2PNetworkManager();

            // Wire lobby events to P2P manager
            LobbyManager.OnPlayerJoined += friend =>
            {
                // STAR topology (N-player): the HOST adds each joining guest as a transport peer. A GUEST
                // must NOT peer with a newly-joined OTHER guest - guests only ever peer with the host (the
                // host relays). At N=1 this fires only on the host (one guest joining), so AddPeer runs
                // exactly as before. SpawnRemotePlayer stays unconditional so the avatar still appears
                // (Phase 2 makes that multi-avatar); only the transport peering is host-gated.
                if (IsHost)
                    NetworkManager.AddPeer(friend.Id);
                RemotePlayerManager.SpawnRemotePlayer(friend.Id, friend.Name);

                Notify($"{friend.Name} joined the crew", 4f);

                // (v0.2.27) Version-handshake grace watchdog: pre-v0.2.27 guests never send a
                // Handshake, so their (possibly mismatched) build is invisible to the version gate.
                // Warn the host if an admitted guest stays silent - warn-only, since a wire-compatible
                // older build may still be a deliberate choice.
                if (IsHost && Instance != null)
                    Instance.StartCoroutine(WarnIfNoVersionHandshake(friend));

                // Send boat world state to new player
                if (IsHost)
                {
                    // JOIN-WHILE-HOST-ASLEEP: if the host is mid sleep-handshake (CurrentState !=
                    // Awake) or under a time-warp (timeScale != 1), DEFER the world-state snapshot + teleport
                    // until the host is awake and running at normal speed. The boat snapshot is a point-in-time
                    // capture of a moving boat; taking it on a warp-accelerated boat (or while the host is in an
                    // unstable sleep state) is what dumped a rejoining guest in open water at their old position.
                    // SendJoinStateToGuest carries the N-player (Phase 3) TARGETED resync - all heavy sends go
                    // ONLY to the joining peer (friend.Id), never re-running settled crew through the join.
                    bool hostBusy = (SleepSyncManager != null &&
                                     SleepSyncManager.CurrentState != SleepSyncManager.SleepState.Awake) ||
                                    UnityEngine.Time.timeScale != 1f;
                    if (hostBusy)
                    {
                        VerboseLogger.LobbyEvent($"Guest joined while host busy (sleepState={SleepSyncManager?.CurrentState}, timeScale={UnityEngine.Time.timeScale}); deferring join state until awake");
                        _joinPendingPeers.Add(friend.Id); // suppress the sleep watchdog for THIS peer while its join is queued
                        Instance.StartCoroutine(SendJoinStateWhenReady(friend));
                    }
                    else
                    {
                        SendJoinStateToGuest(friend);
                    }
                }
            };

            LobbyManager.OnPlayerLeft += friend =>
            {
                // (v0.2.27) a re-joining peer must handshake again (mirrors the admission revoke)
                _versionHandshaked.Remove(friend.Id);
                Notify($"{friend.Name} left the crew", 4f);
                _lastDisconnectNotifyTime = Time.time;

                // N-player (Phase 3): distinguish the HOST leaving from a fellow GUEST leaving. With 3+
                // crew, OnPlayerLeft fires on every member; a guest seeing ANOTHER guest leave must NOT
                // treat it as the host vanishing (no force-quit, no dropping the host's items). Only the
                // HOST going away ends a guest's session. At N=1 the only peer a guest ever sees is the
                // host, so leaverIsHost is always true for a guest -> identical to the old behavior.
                bool leaverIsHost = friend.Id == LobbyManager.HostSteamId;
                var lastPos = RemotePlayerManager?.GetLastKnownPosition(friend.Id) ?? Vector3.zero;

                if (_joinedAsGuest && leaverIsHost)
                {
                    // WE are a guest and the player who left is the HOST -> the server is gone.
                    // Drop the host's items (the only peer's items we track) and warn + quit.
                    ItemSyncManager?.OnHostDisconnected(lastPos);
                }
                else
                {
                    // A NON-host peer left (a fellow guest, seen by the host or by another guest).
                    // Clean up ONLY this peer's state: drop just this leaver's carried items and despawn
                    // just this leaver's avatar. Do NOT force-quit and do NOT touch other holders' items.
                    // (On the host this also covers the normal "a guest left" path; on a guest it covers
                    // a fellow guest leaving. At N=1 a guest never reaches this branch.)
                    ItemSyncManager?.OnPeerDisconnected(friend.Id, lastPos);
                }

                // HOST-LEAVE MESSAGE: latch the ACCURATE host-closed reason BEFORE RemovePeer below,
                // which synchronously fires OnDisconnected -> the generic "Lost connection to the host."
                // would otherwise latch first and win. After this runs, the RemovePeer-driven OnDisconnected
                // becomes a no-op (EndGuestSessionAndQuit only latches the first reason). All other cleanup
                // (RemovePeer, Despawn, OnPeerLeft, CleanupPeerControlState) still runs below.
                if (_joinedAsGuest && leaverIsHost) EndGuestSessionAndQuit("The host closed the co-op server.");

                // Per-peer JoinPending: un-blind the watchdog immediately if a deferred peer leaves.
                _joinPendingPeers.Remove(friend.Id);

                FishingSyncManager?.OnPlayerDisconnected(friend.Id.Value);
                NetworkManager.RemovePeer(friend.Id);
                RemotePlayerManager.DespawnRemotePlayer(friend.Id);
                // N-player (Phase 4): sleep is now per-peer. Drop JUST this leaver from the sleep quorums
                // so it can't block the remaining crew's wake (RemovePeer above already pruned it from
                // ConnectedPeers, so OnPeerLeft sees the correct live count). When the crew is now empty
                // (e.g. a guest's only peer - the host - left) OnPeerLeft falls back to a full reset,
                // matching the old OnDisconnect; at N<=2 behavior is identical.
                SleepSyncManager?.OnPeerLeft(friend.Id); // drop leaver from sleep quorum; full reset if crew now empty
                NavigationSyncManager?.OnPeerLeft(friend.Id); // host: clear leaver's dangling map temp-line + free its draw lock
                // N-player (Phase 5): push/pump/helm are now PER-PEER. Drop ONLY this leaver's entries so the
                // remaining crew's pushes/pumps/helm keep working. A guest leaving mid-push/pump never sends a
                // stop, so this is what stops the host applying its phantom force/drain. RemovePeer above
                // already pruned ConnectedPeers, so "no peers remain" => full reset (matches the old global
                // ClearState/ClearTrackedControls). At N<=2 the leaver is the only peer => full reset, identical.
                CleanupPeerControlState(friend.Id);

                // The host-closed reason is latched ABOVE (before RemovePeer) so the accurate
                // message wins over the RemovePeer-driven "Lost connection" path. Only the HOST leaving ends
                // a guest's session; a fellow guest leaving must NOT quit us.
                VerboseLogger.LobbyEvent($"Player left: leaverIsHost={leaverIsHost}, guest role={_joinedAsGuest}");
            };

            LobbyManager.OnLobbyLeft += () =>
            {
                Sync.BoatUtility.ClearCaches(); // (v0.2.32, P2) fresh session = fresh boat map
                Notify("Lobby closed - playing solo", 5f);

                // The HOST saves their world normally. A GUEST now ALSO saves on leave - but to the hidden
                // PHANTOM file, not their solo slot: currentSlot==99 structurally redirects the write to
                // coop_session.save (the SaveSlots patch), persisting the guest's co-op needs for next time.
                // This replaces the old "guest does not save" guard, which is no longer needed for safety.
                if (!_joinedAsGuest)
                {
                    Log.LogInfo("Host leaving lobby - saving game state");
                    SaveLoadManager.instance?.SaveGame(compressed: true);
                }
                else
                {
                    Log.LogInfo("Guest leaving lobby - persisting co-op needs to phantom file (currentSlot=99)");
                    CoopSave.SaveCoopSession(); // no-op unless currentSlot==99, so it can never write a real slot
                }
                SetGuestSaveSuppressed(false); // re-enable normal saving once back to solo
                // Role-neutral: this line fires on the HOST's lobby close too (the suppression flags are only
                // ever ON for a guest, so for a host this is a no-op re-assert).
                VerboseLogger.LobbyEvent($"Save suppression cleared on lobby exit (role={(_joinedAsGuest ? "guest" : "host")})");

                RemotePlayerManager.DespawnAll();
                BoatSyncManager?.Reset();
                ControlSyncManager?.Reset();
                WeatherSyncManager?.Reset();
                TimeSyncManager?.Reset();
                SurvivalSyncManager?.Reset();
                ItemSyncManager?.Reset();
                SleepSyncManager?.OnDisconnect();
                DamageSyncManager?.Reset();
                ShipyardSyncManager?.Reset();
                MissionSyncManager?.Reset();
                EconomySyncManager?.Reset();
                TradingSyncManager?.Reset();
                Patches.EconomyPatches.ShopkeeperSellItemPatch.ResetPendingStallBuys(); // drain parked stall buys so a
                                               // stale entry can't mis-pair with the next session's first verdict
                FishingSyncManager?.Reset();
                ChipLogSyncManager?.Reset();
                NavigationSyncManager?.Reset();
                ChartKitGhostManager?.Reset();
                CookingSyncManager?.Reset();
                NPCBoatSyncManager?.Reset();
                CleaningSyncManager?.Reset();
                PushSyncManager?.ClearState(); // PushSyncManager has no Reset(); without this, a direct lobby
                                               // close / hot-reload without a prior per-peer leave would leak stale
                                               // push state (_remotePushes entries / _localPushActive) into the
                                               // next session.
                _joinPendingPeers.Clear(); // this full-reset path must clear the per-peer join-pending set too
                NetworkManager.Shutdown();
                NetworkManager = new P2PNetworkManager();
                RegisterPacketHandlers(); // Re-register handlers for new NetworkManager
                NetworkManager.OnDisconnected += _onPeerDisconnected; // #8: re-attach the P2P-drop handler to the new manager

                // A guest who left the lobby must not linger in the host's world on the host's save: warn + quit.
                // (If this leave was triggered by the host dropping, EndGuestSessionAndQuit already ran with that
                // reason and this call is a no-op.)
                if (_joinedAsGuest)
                {
                    VerboseLogger.LobbyEvent($"Guest-initiated lobby exit: save suppressed={!(SaveLoadManager.instance?.enableSaveOnSleep ?? true)}");
                    EndGuestSessionAndQuit("You left the co-op server.");
                }
                else
                {
                    VerboseLogger.LobbyEvent("Host closed the lobby: back to solo (world saved above)");
                }
            };

            // Handle P2P disconnect (network failure) - cleanup capsule even if Steam lobby doesn't detect leave.
            // #8: store in a field and subscribe below (+ re-subscribe in OnLobbyLeft after the manager is
            // recreated), so ungraceful-drop cleanup keeps working in session 2+.
            _onPeerDisconnected = peerId =>
            {
                Log.LogInfo($"P2P disconnected: {peerId}, cleaning up remote player");

                // De-dupe: a clean leave fires OnPlayerLeft first (which calls RemovePeer ->
                // OnDisconnected), so only show this generic toast if OnPlayerLeft didn't just fire.
                if (Time.time - _lastDisconnectNotifyTime > 2f)
                {
                    Notify("Crewmate disconnected", 4f);
                    _lastDisconnectNotifyTime = Time.time;
                }

                // Drop the dropped peer's items at their last position (host only).
                // N-player (Phase 3): drop ONLY this peer's items, not every guest's, so a network
                // drop of one crewmate doesn't force-drop everyone else's carried items. At N=1 the
                // dropped peer is the only holder, so this is identical to the old OnGuestDisconnected.
                if (IsHost && ItemSyncManager != null)
                {
                    var lastPos = RemotePlayerManager?.GetLastKnownPosition(peerId) ?? Vector3.zero;
                    ItemSyncManager.OnPeerDisconnected(peerId, lastPos);
                }

                // Per-peer JoinPending: un-blind the watchdog if a deferred peer drops.
                _joinPendingPeers.Remove(peerId);

                FishingSyncManager?.OnPlayerDisconnected(peerId.Value);
                RemotePlayerManager?.DespawnRemotePlayer(peerId);
                // N-player (Phase 4): sleep is per-peer. RemovePeer (which raised THIS OnDisconnected) has
                // already pruned peerId from ConnectedPeers, so OnPeerLeft sees the correct live count and
                // drops just this peer from the sleep quorums; it full-resets when the crew is now empty.
                SleepSyncManager?.OnPeerLeft(peerId); // P2P drop: drop leaver from sleep quorum (full reset if empty)
                NavigationSyncManager?.OnPeerLeft(peerId); // host: clear leaver's dangling map temp-line + free its draw lock
                // N-player (Phase 5): per-peer push/pump/helm cleanup (see CleanupPeerControlState). A P2P
                // drop mid-push/pump never sends a stop, so dropping this peer's entries is what stops the
                // host applying its phantom force/drain. Full reset only when no peers remain.
                CleanupPeerControlState(peerId);

                // Guest lost the host's connection -> the server is gone for us. Warn + quit.
                VerboseLogger.LobbyEvent($"P2P connection lost: peerId={peerId}, guest={_joinedAsGuest}, forcing quit");
                if (_joinedAsGuest) EndGuestSessionAndQuit("Lost connection to the host.");
            };
            NetworkManager.OnDisconnected += _onPeerDisconnected;

            LobbyManager.OnLobbyJoined += lobby =>
            {
                // (v0.2.32, P2) Boat-map rebuild: _cachedBoats was built once per PROCESS and never
                // invalidated (ClearCaches had zero call sites), so a boat spawned after the first
                // FindAllBoats() call - e.g. HMS Leopard's runtime-deployed cutter - stayed invisible
                // to every name-keyed sync forever, and a leave/rejoin kept stale SaveableObject refs.
                Sync.BoatUtility.ClearCaches();
                // STAR topology (N-player): a GUEST connects to the HOST only (the lobby owner), NOT to
                // every existing member. The host relays each guest's position to the other guests, so
                // guests never open direct P2P sessions with each other. At N=1 the only other member IS
                // the host, so the guest still ends up with exactly one peer (the host) - identical to the
                // old full-mesh behavior. We still SpawnRemotePlayer for existing members for the avatar
                // (Phase 2 makes that multi-avatar); transport peering is host-only.
                // (v0.2.27) VERSION HANDSHAKE, guest side (layer 1): the host stamps its mod version
                // into the lobby data at creation (present since the first networking build), so a
                // joining guest can catch a mismatched crew BEFORE opening a P2P session or touching
                // any save state. The wire format is unversioned - mixed builds desync silently - so
                // refuse by default; Coop.AllowVersionMismatch downgrades the refusal to a warning.
                if (!IsHost)
                {
                    var hostVersion = LobbyManager.GetLobbyData("version");
                    if (!string.IsNullOrEmpty(hostVersion) && hostVersion != PluginVersion)
                    {
                        string mismatchMsg = $"Mod version mismatch: the host runs v{hostVersion}, you run v{PluginVersion}. Everyone must install the same version.";
                        Log.LogError($"[VERSION] {mismatchMsg}");
                        if (AllowVersionMismatchConfig != null && AllowVersionMismatchConfig.Value)
                        {
                            Notify(mismatchMsg + "\n(Coop.AllowVersionMismatch is on - joining anyway; expect desyncs.)", 10f);
                        }
                        else if (SaveSlots.currentSlot == CoopSave.PhantomSlot)
                        {
                            // Title-screen join: the phantom co-op world is already loaded, so a bare
                            // lobby-leave would strand the guest in a dead session - quit cleanly instead.
                            _joinedAsGuest = true;
                            EndGuestSessionAndQuit(mismatchMsg);
                            return;
                        }
                        else
                        {
                            // Mid-game (Continue -> join) path: nothing co-op has touched their solo
                            // world yet; leaving the lobby returns them to normal singleplayer.
                            Notify(mismatchMsg, 12f);
                            LobbyManager.LeaveLobby();
                            return;
                        }
                    }

                    // (v0.2.32) MOD-SET GATE, guest side (layer 1): the composed CompatRegistry token
                    // covers SE + SCF + NAND Tweaks sim vector + Deep Ports (bundle-hashed) + Towable
                    // Boats + HMS Leopard. Refuse before P2P, symmetric in both directions. The token
                    // is OPAQUE for the gate (exact equality); DescribeMismatch splits it for the
                    // MESSAGE only so the user learns which mod differs.
                    var hostMods = LobbyManager.GetLobbyData("mods") ?? "";
                    var ourMods = Compat.CompatRegistry.ModSignature;
                    if (hostMods != ourMods)
                    {
                        string modsMsg = "Mod set mismatch - " +
                            Compat.CompatRegistry.DescribeMismatch(hostMods, ourMods) +
                            ". Everyone must run the same gameplay mods (and the same settings for the flagged ones).";
                        Log.LogError($"[MODS] {modsMsg}");
                        if (AllowModMismatchConfig != null && AllowModMismatchConfig.Value)
                        {
                            Notify(modsMsg + "\n(Coop.AllowModMismatch is on - joining anyway; expect desyncs.)", 10f);
                        }
                        else if (SaveSlots.currentSlot == CoopSave.PhantomSlot)
                        {
                            _joinedAsGuest = true;
                            EndGuestSessionAndQuit(modsMsg);
                            return;
                        }
                        else
                        {
                            Notify(modsMsg, 12f);
                            LobbyManager.LeaveLobby();
                            return;
                        }
                    }
                }

                var hostId = LobbyManager.HostSteamId;
                bool joinedExistingPlayer = false;
                foreach (var member in LobbyManager.LobbyMembers)
                {
                    if (member.Id != SteamClient.SteamId)
                    {
                        if (member.Id == hostId)
                            NetworkManager.AddPeer(member.Id);
                        RemotePlayerManager.SpawnRemotePlayer(member.Id, member.Name);
                        joinedExistingPlayer = true;
                    }
                }

                // OnLobbyJoined ALSO fires when the host enters their own freshly-created (empty)
                // lobby, so only show the "you're aboard the host's ship" toast when we actually
                // found another player to join - otherwise a solo host gets this bogus message.
                // (v0.2.27) VERSION HANDSHAKE, guest side (layer 2): announce our version to the host
                // over P2P. Layer 1 can't protect a NEWER host from an OLDER guest (pre-v0.2.27 guests
                // never ran the lobby check), so the host independently gates on this packet - and
                // warns when an admitted guest never sends it (an old build). Sent before the role
                // bookkeeping below; the host's HandshakeAck refusal (if any) arrives strictly after
                // this handler finished, so _joinedAsGuest is already recorded by then.
                if (!IsHost && joinedExistingPlayer)
                {
                    // (v0.2.32) Handshake body: version + composed mod-set token. Older hosts read only
                    // the version string and ignore the trailing bytes (per-packet framing), so this is
                    // not a wire break.
                    NetworkManager.SendReliable(hostId, PacketType.Handshake, w =>
                    {
                        w.Write(PluginVersion);
                        w.Write(Compat.CompatRegistry.ModSignature);
                    });
                }

                if (joinedExistingPlayer)
                {
                    Notify("Aboard the host's ship!", 5f);
                    // (No ocean reseed here: shipped Sailwind never instantiates the legacy FFT Ocean
                    // class. The live Crest wave state is synced by WeatherSyncManager, seeded by the
                    // host's join-time one-shot weather send.)
                }

                // Record our session role NOW (before any later ownership transfer) and, for a guest, suppress
                // autosave + save-on-sleep so co-op state never overwrites their own solo slot.
                _joinedAsGuest = !IsHost;
                if (_joinedAsGuest)
                {
                    SetGuestSaveSuppressed(true);
                    VerboseLogger.LobbyEvent($"Guest save protection: enabled on join (role IsGuest={_joinedAsGuest})");

                    // PHANTOM CO-OP SAVE (mid-game join path): the title-screen join already set
                    // currentSlot=99 before loading. But a guest who joined from an already-live SOLO
                    // world (Continue -> join) still has currentSlot pointing at a real slot. Redirect
                    // all subsequent writes to the phantom file by entering the phantom context now.
                    // (Their world was solo-loaded, but from here every save lands on coop_session.save.)
                    // We do NOT reset needs here - they keep their current solo needs for this session;
                    // baseline reset only applies to a freshly-created phantom on the title-join path.
                    if (SaveSlots.currentSlot != CoopSave.PhantomSlot)
                    {
                        // (v0.2.25) hostId keys the phantom file per host; on the mid-game path the
                        // lobby is already entered, so the authoritative lobby owner is available.
                        if (CoopSave.EnterCoopSaveContext(LobbyManager.HostSteamId.Value, out _))
                        {
                            VerboseLogger.LobbyEvent("Phantom save: mid-game guest join redirected currentSlot to 99");
                        }
                        else
                        {
                            // Seeding failed (e.g. File.Copy threw). Safety must NOT depend on the
                            // suppression flags alone: hard-set currentSlot=99 anyway so the STRUCTURAL
                            // invariant holds - every later write still resolves to the phantom path and
                            // can never touch a real slot. The phantom file just doesn't exist yet; the
                            // first committable SaveCoopSession's DoSaveGame creates it by writing
                            // GetCurrentSavePath() (== phantom). _savedThisSession was reset inside
                            // EnterCoopSaveContext, so that retry is allowed.
                            SaveSlots.currentSlot = CoopSave.PhantomSlot;
                            Log.LogWarning("[Coop] Mid-game guest join: phantom seeding failed; forced currentSlot=99 to preserve the no-real-slot invariant (phantom file created lazily on first save)");
                            VerboseLogger.LobbyEvent("Phantom save: mid-game seeding failed; forced currentSlot=99 (structural invariant preserved)");
                        }
                    }

                    // WALLET AUTHORITY: we deliberately do NOT touch PlayerGold.currency here. A mid-game
                    // guest still holds its rich solo balance after this join, but the host's authoritative
                    // CurrencySync overwrites it reliably: the guest's join coroutine sends an
                    // EconomySyncRequest once settled, and the host replies with a TARGETED SendCurrencySync
                    // (plus one delayed re-send) that element-wise replaces the wallet. Zeroing the wallet
                    // here instead breaks vanilla's local-wallet buy gate, so we rely on the overwrite.

                    // JOIN-ROBUSTNESS: that one-shot EconomySyncRequest (BoatStateApplicator) can be
                    // starved (a guest can otherwise receive ZERO CurrencySync all session). Track "first
                    // CurrencySync applied since join" and RETRY the request every 5s (realtime, max 6)
                    // until it lands.
                    EconomySyncManager?.MarkJoinStarted();
                    StartCoroutine(GuestEconomySyncRetry());

                    // SELF-HEAL: if this guest's phantom-save load had to skip corrupt saveables,
                    // schedule ONE clean rewrite well after the join settles (contract: call once, ~60s).
                    StartCoroutine(GuestSelfHealSaveAfterJoin());

                    // (v0.2.25) JOIN-STATE WATCHDOG: if the host never admits us (admission gate refused,
                    // or the join snapshot was lost), the P2P transport can still connect and every sync
                    // manager runs - in the v0.2.23/24 playtests a refused guest silently played an ENTIRE
                    // session half-initialized (no world state, stale phantom-save needs). Watch for the
                    // BoatWorldState snapshot and warn-and-quit if it never arrives. Guest-only by
                    // construction (this whole branch is _joinedAsGuest).
                    StartCoroutine(GuestJoinWatchdog());
                }
            };

            LobbyManager.OnLobbyCreated += lobby =>
            {
                Sync.BoatUtility.ClearCaches(); // (v0.2.32, P2) fresh session = fresh boat map
                // (v0.2.32 review) Tows created BEFORE the lobby existed (singleplayer, or restored
                // during load under the phantom-load gates) never fired the moor-event pin. Rescan
                // every boat once so a pre-existing towed hull streams from the first packet.
                StartCoroutine(PinPreExistingTows());
                _joinedAsGuest = false; // we're the host
                _versionHandshaked.Clear(); // (v0.2.27) fresh lobby = fresh handshake set
                // Registry population moved to OnPlayerJoined (save may not be loaded yet)
                Notify("Server opened - waiting for crew (close from the menu)", 5f);
            };

            // Register basic packet handlers
            RegisterPacketHandlers();
        }

        /// <summary>
        /// (v0.2.32 review) One-shot tow-pin rescan at lobby creation. The always-stream pin is
        /// maintained on moor/unmoor EVENTS only, so a tow that already existed when the lobby opened
        /// (built in singleplayer, or restored during load under the phantom-load gates that suppress
        /// the mooring patches) has NO pin: the host would never stream the towed hull and it would
        /// drift away from the towing boat on every guest.
        ///
        /// WHY A COROUTINE and not an inline foreach: BoatUtility.UpdateTowStreamPin is host-gated
        /// (Plugin.IsHost), and IsHost is still FALSE while OnLobbyCreated runs - SteamLobbyManager
        /// assigns _currentLobby (which IsHost is derived from) in the awaited CreateLobbyAsync
        /// continuation and in HandleLobbyEntered, BOTH of which run after Steam's synchronous
        /// LobbyCreated callback that raises this event. An inline rescan would silently no-op.
        /// </summary>
        private System.Collections.IEnumerator PinPreExistingTows()
        {
            float deadline = Time.realtimeSinceStartup + 10f;
            while (!IsHost && Time.realtimeSinceStartup < deadline)
                yield return null;

            if (!IsHost)
            {
                Log.LogWarning("[Coop] Tow pin rescan skipped: host role never settled after lobby creation");
                yield break;
            }

            int pinned = 0;
            foreach (var b in Sync.BoatUtility.FindAllBoats().Values)
            {
                Sync.BoatUtility.UpdateTowStreamPin(b);
                pinned++;
            }
            VerboseLogger.LobbyEvent($"Tow pin rescan at lobby creation: examined {pinned} boat(s) for pre-existing tows");
        }

        private void RegisterPacketHandlers()
        {
            // (v0.2.27) Version handshake, host side. Guests send their mod version right after
            // peering (OnLobbyJoined); the host refuses a mismatch unless Coop.AllowVersionMismatch
            // is on. The packet existed (dormant, log-only) since Phase 1, so this is not a wire
            // change - old guests simply never send it, which the grace warning in
            // WarnIfNoVersionHandshake surfaces to the host instead.
            NetworkManager.RegisterHandler(PacketType.Handshake, (sender, reader) =>
            {
                var version = reader.ReadString();
                // (v0.2.31) Tolerant read: a pre-0.2.31 guest's handshake ends after the version
                // string; treat a missing field as an empty mod set - the symmetric compare below
                // then refuses them exactly when this host runs SE (they could not sync SE anyway).
                string guestMods = "";
                try { guestMods = reader.ReadString(); } catch { /* legacy short payload */ }
                Log.LogInfo($"[VERSION] Handshake from {sender}: version {version} (ours {PluginVersion}), mods [{guestMods}] (ours [{Compat.CompatRegistry.ModSignature}])");

                if (!IsHost) return;
                _versionHandshaked.Add(sender);

                bool versionMatch = version == PluginVersion;
                // Opaque token, exact equality only - never parse it (it can carry a "/noSailData"
                // or "/noSync" suffix precisely so those cases mismatch and get refused).
                bool modsMatch = guestMods == Compat.CompatRegistry.ModSignature;
                bool match = versionMatch && modsMatch;
                bool allow = (versionMatch || (AllowVersionMismatchConfig != null && AllowVersionMismatchConfig.Value))
                          && (modsMatch || (AllowModMismatchConfig != null && AllowModMismatchConfig.Value));

                string guestName = sender.ToString();
                foreach (var member in LobbyManager.LobbyMembers)
                    if (member.Id == sender) { guestName = member.Name; break; }

                if (!match)
                {
                    string what = !versionMatch
                        ? $"is on mod v{version} (you run v{PluginVersion})"
                        : "has a different mod set - " + Compat.CompatRegistry.DescribeMismatch(
                              Compat.CompatRegistry.ModSignature, guestMods);
                    string fix = !versionMatch
                        ? $"Everyone must run v{PluginVersion}."
                        : "Everyone must match the host's gameplay mods.";
                    Notify(allow
                        ? $"{guestName} {what} - allowed by config; expect desyncs."
                        : $"{guestName} {what} - refused. {fix}", 10f);
                    Log.LogWarning($"[VERSION] {guestName} ({sender}) version {version} mods [{guestMods}] vs host {PluginVersion} [{Compat.CompatRegistry.ModSignature}]: {(allow ? "ALLOWED by config" : "REFUSED")}");
                }

                // Ack BEFORE any revoke, or the refusal could never reach the guest.
                NetworkManager.SendReliable(sender, PacketType.HandshakeAck, w =>
                {
                    w.Write(PluginVersion);
                    w.Write(allow);
                    w.Write(Compat.CompatRegistry.ModSignature); // (v0.2.31, token composed since v0.2.32) trailing field, old guests ignore
                });

                if (!allow)
                {
                    // Same teeth as the admission gate: drop transport admission + peering so the
                    // mismatched guest cannot keep feeding the sync managers. The guest quits itself
                    // on the refused ack; if that packet is lost, its 45s join watchdog still fires.
                    LobbyManager.RevokeAdmission(sender);
                    NetworkManager.RemovePeer(sender);
                }
            });

            NetworkManager.RegisterHandler(PacketType.HandshakeAck, (sender, reader) =>
            {
                var version = reader.ReadString();
                var accepted = reader.ReadBoolean();
                // (v0.2.31) Tolerant read: a pre-0.2.31 host's ack ends after the bool.
                string hostMods = "";
                try { hostMods = reader.ReadString(); } catch { /* pre-0.2.31 host */ }
                Log.LogInfo($"[VERSION] Handshake response from {sender}: version {version}, mods [{hostMods}], accepted: {accepted}");

                // (v0.2.27) The host refused us - quit cleanly instead of playing a half-admitted
                // session (the host has already revoked our admission). (v0.2.31) Name the actual
                // mismatch: version when versions differ, otherwise the composed mod-set token.
                if (!IsHost && !accepted)
                {
                    string reason = version != PluginVersion
                        ? $"Mod version mismatch: the host runs v{version}, you run v{PluginVersion}. Everyone must install the same version."
                        : "Mod set mismatch - " + Compat.CompatRegistry.DescribeMismatch(
                              hostMods, Compat.CompatRegistry.ModSignature) +
                          ". Everyone must run the same gameplay mods.";
                    EndGuestSessionAndQuit(reason);
                }
            });

            // Player position packet (boat-relative coordinates + held item)
            NetworkManager.RegisterHandler(PacketType.PlayerPosition, (sender, reader) =>
            {
                // N-player STAR: the AUTHOR SteamId is the first body field (the player whose position this
                // is). Prefer it over the transport `sender`, because a host-relayed packet has the HOST as
                // transport sender, not the original author. At N=1 author == the one guest == sender.
                var authorRaw = reader.ReadUInt64();
                var author = new SteamId { Value = authorRaw };

                var isOnBoat = reader.ReadBoolean();
                var boatName = reader.ReadString();
                var x = reader.ReadSingle();
                var y = reader.ReadSingle();
                var z = reader.ReadSingle();
                var rotX = reader.ReadSingle();
                var rotY = reader.ReadSingle();
                var rotZ = reader.ReadSingle();
                var rotW = reader.ReadSingle();

                var relativePos = new Vector3(x, y, z);
                var rotation = new Quaternion(rotX, rotY, rotZ, rotW);

                // Read held item data (if present)
                var hasHeldItem = reader.ReadBoolean();
                int heldItemId = 0;
                Vector3 heldItemPos = Vector3.zero;
                Quaternion heldItemRot = Quaternion.identity;
                if (hasHeldItem)
                {
                    heldItemId = reader.ReadInt32();
                    heldItemPos = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    heldItemRot = new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

                    // Update held item position
                    // N-player: identify the holder by the body AUTHOR, not the transport sender (which is
                    // the host for relayed packets). At N=1 author == the one guest, so behavior is identical.
                    ItemSyncManager.Instance?.UpdateRemoteHeldItemPosition(heldItemId, heldItemPos, heldItemRot, isOnBoat, boatName, author);
                }

                // CROUCH (v0.2.25 wire change): trailing byte = quantized 0..1 crouch amount, appended
                // AFTER the held-item block. Probe remaining length so a packet from a pre-v0.2.25
                // sender (no crouch field) still parses as standing instead of throwing EndOfStream.
                byte crouchByte = 0;
                if (reader.BaseStream.Position < reader.BaseStream.Length)
                    crouchByte = reader.ReadByte();
                float crouch01 = crouchByte / 255f;

                // LOOK-LEAN (wire change): trailing signed byte AFTER the crouch byte = the sender's clamped
                // vertical look pitch. Same stream-length probe as crouch so a pre-look sender's shorter packet
                // still parses; the neutral default 128 decodes to 0 deg (looking straight ahead = no lean).
                // Decode: [0,255] -> [-90,90] deg.
                byte lookByte = 128;
                if (reader.BaseStream.Position < reader.BaseStream.Length)
                    lookByte = reader.ReadByte();
                float lookPitchDeg = (lookByte - 128) / 127f * 90f;

                // Pass boat-relative position with boat name for correct reference frame
                // Note: PlayerRecv logging is done in RemoteAvatar.UpdatePosition
                // N-player: identify the avatar by the body AUTHOR, not the transport sender.
                RemotePlayerManager.UpdateRemotePosition(author, relativePos, rotation, isOnBoat, boatName, crouch01, lookPitchDeg);

                // HOST RELAY (STAR topology): after applying locally, the host forwards this position to all
                // OTHER guests, re-writing the SAME payload (INCLUDING the author field) so they see who it
                // belongs to. Positions are high-frequency state -> unreliable, matching the original send.
                // SendToAllExcept(author) skips the originating guest; at N=1 that's the only peer -> no-op.
                if (IsHost)
                {
                    NetworkManager.SendToAllExcept(author, PacketType.PlayerPosition, writer =>
                    {
                        writer.Write(authorRaw);
                        writer.Write(isOnBoat);
                        writer.Write(boatName);
                        writer.Write(relativePos.x);
                        writer.Write(relativePos.y);
                        writer.Write(relativePos.z);
                        writer.Write(rotation.x);
                        writer.Write(rotation.y);
                        writer.Write(rotation.z);
                        writer.Write(rotation.w);
                        writer.Write(hasHeldItem);
                        if (hasHeldItem)
                        {
                            writer.Write(heldItemId);
                            writer.Write(heldItemPos.x);
                            writer.Write(heldItemPos.y);
                            writer.Write(heldItemPos.z);
                            writer.Write(heldItemRot.x);
                            writer.Write(heldItemRot.y);
                            writer.Write(heldItemRot.z);
                            writer.Write(heldItemRot.w);
                        }
                        // CROUCH (v0.2.25): forward the crouch byte so guests BEHIND the host (star
                        // topology) also get it. Without this the host would strip crouch on relay and
                        // only host<->sender would animate. Trailing-append matches the sender layout.
                        writer.Write(crouchByte);
                        // LOOK-LEAN: forward the look byte too so guests behind the host also get the torso
                        // pitch (same star-relay reason as crouch). Trailing-append after the crouch byte
                        // matches the sender layout; a pre-look sender relays as neutral 128 (0 deg).
                        writer.Write(lookByte);
                    }, reliable: false);
                }
            });

            // Boat world state (initial sync)
            // Note: BoatRecv logging is done in BoatSyncManager.OnBoatWorldStateReceived
            NetworkManager.RegisterHandler(PacketType.BoatWorldState, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadBoatWorldState(reader);
                BoatSyncManager?.OnBoatWorldStateReceived(packet);
            });

            // Boat transform (continuous sync)
            // Note: BoatRecv logging is done in BoatSyncManager.OnBoatTransformReceived
            NetworkManager.RegisterHandler(PacketType.BoatTransform, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadBoatTransform(reader);
                BoatSyncManager?.OnBoatTransformReceived(packet);
            });

            // Current boat changed
            NetworkManager.RegisterHandler(PacketType.CurrentBoatChanged, (sender, reader) =>
            {
                var boatName = reader.ReadString();
                VerboseLogger.BoatRecv($"CurrentBoatChanged, boat={boatName}");

                var boats = BoatUtility.FindAllBoats();
                if (boats.TryGetValue(boatName, out var boat))
                {
                    var refs = boat.GetComponent<BoatRefs>();
                    if (refs != null)
                    {
                        GameState.currentBoat = refs.boatModel;
                        GameState.lastBoat = boat.transform;
                        VerboseLogger.BoatApply($"Switched to boat: {boatName}");
                    }
                }
            });

            // Control packets - logging is done in ControlSyncManager.OnRemote* methods
            // N-player STAR: rope/anchor/mooring changes from a guest are REQUESTS - the host applies them
            // (authoritative) and relays the result to the other guests (SendToAllExcept(sender) inside the
            // OnRemote* handlers). Pass `sender` so the relay can skip the originating guest.
            NetworkManager.RegisterHandler(PacketType.RopeState, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadRopeState(reader);
                ControlSyncManager?.OnRemoteRopeChanged(packet, sender);
            });

            NetworkManager.RegisterHandler(PacketType.HelmState, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadHelmState(reader);
                ControlSyncManager?.OnRemoteHelmChanged(packet);
            });

            NetworkManager.RegisterHandler(PacketType.HelmInput, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadHelmInput(reader);
                // N-player: pass the sender so the host applies HelmInput only from the helm-lease holder.
                ControlSyncManager?.OnRemoteHelmInput(sender, packet);
            });

            NetworkManager.RegisterHandler(PacketType.HelmDenied, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadHelmDenied(reader);
                ControlSyncManager?.OnHelmDeniedReceived(packet);
            });

            NetworkManager.RegisterHandler(PacketType.HelmLock, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadHelmLock(reader);
                // Route based on role: host receives toggle requests, guest receives state updates
                if (Plugin.IsHost)
                    ControlSyncManager?.OnRemoteHelmLockToggle(packet);
                else
                    ControlSyncManager?.OnRemoteHelmLockState(packet);
            });

            NetworkManager.RegisterHandler(PacketType.AnchorEvent, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadAnchorEvent(reader);
                ControlSyncManager?.OnRemoteAnchorChanged(packet, sender);
            });

            NetworkManager.RegisterHandler(PacketType.MooringState, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadMooringState(reader);
                ControlSyncManager?.OnRemoteMooringChanged(packet, sender);
            });

            NetworkManager.RegisterHandler(PacketType.MooringRopeLength, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadMooringRopeLength(reader);
                ControlSyncManager?.OnRemoteMooringRopeLengthChanged(packet, sender);
            });

            // ApplyForce packet deprecated - replaced by PushSyncManager event-based sync
            PushSyncManager?.RegisterPacketHandlers();

            // Weather sync
            NetworkManager.RegisterHandler(PacketType.WeatherState, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadWeatherState(reader);
                WeatherSyncManager.Instance?.OnWeatherStateReceived(packet);
            });

            // Time state sync
            NetworkManager.RegisterHandler(PacketType.TimeState, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadTimeState(reader);
                TimeSyncManager?.OnTimeStateReceived(packet);
            });

            // Survival packets
            NetworkManager.RegisterHandler(PacketType.SurvivalStats, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadSurvivalStats(reader);
                SurvivalSyncManager?.OnSurvivalStatsReceived(packet);
            });

            NetworkManager.RegisterHandler(PacketType.ActivityState, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadActivityState(reader);
                SurvivalSyncManager?.OnActivityStateReceived(packet);
            });

            NetworkManager.RegisterHandler(PacketType.ConsumptionDelta, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadConsumptionDelta(reader);
                SurvivalSyncManager?.OnConsumptionDeltaReceived(packet);
            });

            // Item sync packets
            NetworkManager.RegisterHandler(PacketType.ItemPickedUp, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadItemPickedUp(reader);
                ItemSyncManager?.OnRemoteItemPickedUp(packet, sender);
            });

            NetworkManager.RegisterHandler(PacketType.ItemDropped, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadItemDropped(reader);
                // N-player: pass sender so the host honors the drop only from the recorded holder and
                // relays the authoritative result to the other guests (star topology).
                ItemSyncManager?.OnRemoteItemDropped(packet, sender);
            });

            NetworkManager.RegisterHandler(PacketType.ItemPickupRequest, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadItemPickupRequest(reader);
                ItemSyncManager?.OnRemoteItemPickupRequest(packet, sender);
            });

            NetworkManager.RegisterHandler(PacketType.ItemPickupDenied, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadItemPickupDenied(reader);
                ItemSyncManager?.OnRemoteItemPickupDenied(packet);
            });

            NetworkManager.RegisterHandler(PacketType.ItemSpawned, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadItemSpawned(reader);
                ItemSyncManager?.OnRemoteItemSpawned(packet, sender);
            });

            NetworkManager.RegisterHandler(PacketType.ItemDestroyed, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadItemDestroyed(reader);
                ItemSyncManager?.OnRemoteItemDestroyed(packet, sender);
            });

            NetworkManager.RegisterHandler(PacketType.ItemAmountChanged, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadItemAmountChanged(reader);
                ItemSyncManager?.OnRemoteItemAmountChanged(packet, sender);
            });

            NetworkManager.RegisterHandler(PacketType.ItemCrateInsert, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadItemCrate(reader);
                ItemSyncManager?.OnRemoteItemCrateInsert(packet, sender);
            });

            NetworkManager.RegisterHandler(PacketType.ItemCrateRemove, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadItemCrate(reader);
                ItemSyncManager?.OnRemoteItemCrateRemove(packet, sender);
            });

            // Phase 2 item sync packets
            NetworkManager.RegisterHandler(PacketType.ItemHealthChanged, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadItemHealthChanged(reader);
                ItemSyncManager?.OnRemoteItemHealthChanged(packet, sender);
            });

            // Light state sync (lantern on/off)
            NetworkManager.RegisterHandler(PacketType.LightState, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadLightState(reader);
                ItemSyncManager?.OnRemoteLightStateChanged(packet, sender);
            });

            // Pipe filled with tobacco sync
            NetworkManager.RegisterHandler(PacketType.PipeFilled, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadPipeFilled(reader);
                ItemSyncManager?.OnRemotePipeFilled(packet, sender);
            });

            // Nail state sync (hammer nail/un-nail)
            NetworkManager.RegisterHandler(PacketType.NailState, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadNailState(reader);
                ItemSyncManager?.OnRemoteNailState(packet, sender);
            });

            NetworkManager.RegisterHandler(PacketType.ItemHung, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadItemHung(reader);
                ItemSyncManager?.OnRemoteItemHung(packet, sender);
            });

            NetworkManager.RegisterHandler(PacketType.ItemUnhung, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadItemUnhung(reader);
                ItemSyncManager?.OnRemoteItemUnhung(packet, sender);
            });

            NetworkManager.RegisterHandler(PacketType.CrateUnsealRequest, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadCrateUnsealRequest(reader);
                ItemSyncManager?.OnRemoteCrateUnsealRequest(packet, sender);
            });

            NetworkManager.RegisterHandler(PacketType.CrateUnsealed, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadCrateUnsealed(reader);
                ItemSyncManager?.OnRemoteCrateUnsealed(packet);
            });

            NetworkManager.RegisterHandler(PacketType.ItemResync, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadItemResync(reader);
                ItemSyncManager?.OnRemoteItemResync(packet);
            });

            // Cargo transport hire (v0.2.29): host-routed carrier transactions
            NetworkManager.RegisterHandler(PacketType.CargoInsertRequest, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadCargoInsertRequest(reader);
                ItemSyncManager?.OnRemoteCargoInsertRequest(packet, sender);
            });

            NetworkManager.RegisterHandler(PacketType.CargoInserted, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadCargoInserted(reader);
                ItemSyncManager?.OnRemoteCargoInserted(packet);
            });

            NetworkManager.RegisterHandler(PacketType.CargoWithdrawRequest, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadCargoWithdrawRequest(reader);
                ItemSyncManager?.OnRemoteCargoWithdrawRequest(packet, sender);
            });

            NetworkManager.RegisterHandler(PacketType.CargoWithdrawn, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadCargoWithdrawn(reader);
                ItemSyncManager?.OnRemoteCargoWithdrawn(packet);
            });

            // Sleep sync packets
            NetworkManager.RegisterHandler(PacketType.SleepRequest, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadSleepRequest(reader);
                // N-player (Phase 4): forward the sender so the host tracks per-peer in-bed state.
                SleepSyncManager?.OnSleepRequestReceived(packet, sender);
            });

            NetworkManager.RegisterHandler(PacketType.SleepWaiting, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadSleepWaiting(reader);
                // N-player (Phase 4): forward the sender so the host tracks per-peer in-bed state.
                SleepSyncManager?.OnSleepWaitingReceived(packet, sender);
            });

            NetworkManager.RegisterHandler(PacketType.SleepApproved, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadSleepApproved(reader);
                SleepSyncManager?.OnSleepApprovedReceived(packet);
            });

            NetworkManager.RegisterHandler(PacketType.SleepCancelled, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadSleepCancelled(reader);
                // N-player (Phase 4): forward the sender so the host drops just that peer from the in-bed set.
                SleepSyncManager?.OnSleepCancelledReceived(packet, sender);
            });

            NetworkManager.RegisterHandler(PacketType.SleepCycleState, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadSleepCycleState(reader);
                SleepSyncManager?.OnSleepCycleStateReceived(packet);
            });

            NetworkManager.RegisterHandler(PacketType.WakeUp, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadWakeUp(reader);
                // Pass sender so the host relays a guest's manual wake to the OTHER guests.
                SleepSyncManager?.OnWakeUpReceived(packet, sender);
            });

            // INDEPENDENT NEEDS: guest -> host, zero-payload "I'm fully rested" for the all-rested gate.
            // N-player (Phase 4): the sender SteamId is added to the host's per-peer rested set; the gate
            // opens only once EVERY connected peer (+ host) is rested (AllCrewRested). At N=1 this matches
            // the old single-guest gate exactly.
            NetworkManager.RegisterHandler(PacketType.SleepRested, (s, r) =>
                SailwindCoop.Sync.SleepSyncManager.Instance?.OnGuestRested(s));

            // Recovery handler - the guest stays connected and re-syncs onto the recovered boat (no kick).
            NetworkManager.RegisterHandler(PacketType.RecoveryStarted, (sender, reader) =>
            {
                var reason = (RecoveryReason)reader.ReadByte();
                Log.LogInfo($"[RECOVERY] Host recovering (reason={reason}); pausing boat sync, awaiting resync");
                VerboseLogger.RecoveryRecv($"RecoveryStarted, reason={reason}; blocking boat sync until resync");

                // Gate the guest's boat-transform sync so the host's long teleport (to the last port) doesn't
                // hard-snap the guest's boat without the join machinery. The host resends BoatWorldState when
                // recovery finishes, and its ApplyWorldState clears this flag + teleports us onto the boat.
                BoatSyncManager.IsJoinInProgress = true;

                if (Sleep.instance != null && Sleep.instance.recoveryText != null)
                {
                    var msg = "Host is recovering the boat...\n\nre-syncing.";
                    Sleep.instance.recoveryText.text = msg;
                    Instance?.StartCoroutine(ClearRecoveryTextAfter(12f, msg));
                }
                // Do NOT leave the lobby.
            });

            // Damage handlers
            NetworkManager.RegisterHandler(PacketType.DamageState, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadDamageState(reader);
                DamageSyncManager?.OnDamageStateReceived(packet);
            });

            NetworkManager.RegisterHandler(PacketType.DamageImpact, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadDamageImpact(reader);
                DamageSyncManager?.OnDamageImpactReceived(packet);
            });

            NetworkManager.RegisterHandler(PacketType.GuestPumpInput, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadGuestPumpInput(reader);
                // N-player: route the pump input into THIS sender's per-peer slot so concurrent pumpers sum.
                DamageSyncManager?.OnGuestPumpInputReceived(sender, packet);
            });

            NetworkManager.RegisterHandler(PacketType.GuestOakumRepair, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadGuestOakumRepair(reader);
                DamageSyncManager?.OnGuestOakumRepairReceived(packet);
            });

            NetworkManager.RegisterHandler(PacketType.GuestBailRequest, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadGuestBailRequest(reader);
                DamageSyncManager?.OnGuestBailRequestReceived(packet);
            });

            // Shipyard customization sync
            NetworkManager.RegisterHandler(PacketType.ShipyardCustomization, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadShipyardCustomization(reader);
                ShipyardSyncManager?.OnCustomizationReceived(packet, sender);
            });

            // Shipyard cradle state (210, v0.2.28): editing peer announces AdmitShip/DischargeShip so
            // non-editing peers freeze the boat and suppress transform sync + discharge impact damage.
            NetworkManager.RegisterHandler(PacketType.ShipyardState, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadShipyardState(reader);
                ShipyardSyncManager?.OnShipyardStateReceived(packet, sender);
            });

            // Shipyard Expansion sail-extras blob (215, v0.2.31): SE's angle/flip/texture/scale edits live
            // outside vanilla SaveBoatCustomizationData, so they ride their own packet. The host star-relays
            // it; the receiver applies it strictly AFTER any customization apply for that boat, or buffers it
            // (see ShipyardSyncManager.OnSERigStateReceived).
            NetworkManager.RegisterHandler(PacketType.SERigState, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadSERigState(reader);
                ShipyardSyncManager?.OnSERigStateReceived(packet, sender);
            });

            // Trapdoor/door/hatch absolute state (216, v0.2.32). Peer-origin; host star-relays inside
            // the manager. Applies with an inMotion retry (vanilla OnActivate no-ops mid-animation).
            NetworkManager.RegisterHandler(PacketType.TrapdoorState, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadTrapdoorState(reader);
                TrapdoorSyncManager?.OnRemoteTrapdoorState(packet, sender);
            });

            // Leopard cutter deploy/recover (217, v0.2.32).
            NetworkManager.RegisterHandler(PacketType.CutterState, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadCutterState(reader);
                LeopardSyncManager?.OnCutterState(packet, sender);
            });

            // Leopard oar input (218, v0.2.32). Unreliable stream; manager relays + applies.
            NetworkManager.RegisterHandler(PacketType.OarInput, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadOarInput(reader);
                LeopardSyncManager?.OnOarInput(packet, sender);
            });

            // Leopard bell (219, v0.2.32).
            NetworkManager.RegisterHandler(PacketType.BellRing, (sender, reader) =>
            {
                var authorId = reader.ReadUInt64();
                LeopardSyncManager?.OnBellRing(authorId, sender);
            });

            // Mission sync packets
            NetworkManager.RegisterHandler(PacketType.MissionStateSync, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadMissionStateSync(reader);
                MissionSyncManager?.OnMissionStateSyncReceived(packet);
            });

            NetworkManager.RegisterHandler(PacketType.MissionAccepted, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadMissionAccepted(reader);
                MissionSyncManager?.OnMissionAcceptedReceived(packet);
            });

            NetworkManager.RegisterHandler(PacketType.MissionProgress, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadMissionProgress(reader);
                MissionSyncManager?.OnMissionProgressReceived(packet);
            });

            NetworkManager.RegisterHandler(PacketType.MissionCompleted, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadMissionEnded(reader);
                MissionSyncManager?.OnMissionCompletedReceived(packet);
            });

            NetworkManager.RegisterHandler(PacketType.MissionAbandoned, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadMissionEnded(reader);
                MissionSyncManager?.OnMissionAbandonedReceived(packet);
            });

            NetworkManager.RegisterHandler(PacketType.MissionAcceptRequest, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadMissionAcceptRequest(reader);
                MissionSyncManager?.OnMissionAcceptRequestReceived(sender, packet);
            });

            NetworkManager.RegisterHandler(PacketType.MissionAbandonRequest, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadMissionAbandonRequest(reader);
                MissionSyncManager?.OnMissionAbandonRequestReceived(sender, packet);
            });

            NetworkManager.RegisterHandler(PacketType.MissionBoardRequest, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadMissionBoardRequest(reader);
                MissionSyncManager?.OnMissionBoardRequestReceived(sender, packet);
            });

            NetworkManager.RegisterHandler(PacketType.MissionBoardResponse, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadMissionBoardResponse(reader);
                MissionSyncManager?.OnMissionBoardResponseReceived(packet);
            });

            // Economy sync packets
            NetworkManager.RegisterHandler(PacketType.CurrencySync, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadCurrencySync(reader);
                EconomySyncManager?.OnCurrencySyncReceived(packet);
            });

            NetworkManager.RegisterHandler(PacketType.ReputationSync, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadReputationSync(reader);
                EconomySyncManager?.OnReputationSyncReceived(packet);
            });

            NetworkManager.RegisterHandler(PacketType.DeliverGoodRequest, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadDeliverGoodRequest(reader);
                MissionSyncManager?.OnDeliverGoodRequestReceived(sender, packet);
            });

            NetworkManager.RegisterHandler(PacketType.ExchangeRequest, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadExchangeRequest(reader);
                EconomySyncManager?.OnExchangeRequestReceived(sender, packet);
            });

            NetworkManager.RegisterHandler(PacketType.EconomySyncRequest, (sender, reader) =>
            {
                // Guest requests economy re-sync at the END of its join coroutine (after LoadGame put its
                // rich SOLO-save balance in currency[]). Reply with a TARGETED authoritative overwrite to
                // JUST the requester (N-player: never re-sync already-settled guests). This is the reliable
                // wallet overwrite that kills the retained-solo-balance 'extra money' - it does NOT depend on
                // the host's own wallet changing (CheckAndSyncCurrency only broadcasts on a host-side delta).
                if (IsHost)
                {
                    Log.LogInfo($"[ECONOMY] Received EconomySyncRequest from {sender}, sending targeted fresh state (+delayed currency resync)");
                    EconomySyncManager?.SendFullStateTo(sender);
                    // ADDITIONALLY: one more targeted, cache-neutral currency resend a short delay later, so it
                    // lands AFTER the guest's LoadGame/economy fully settles (idempotent element-wise replace).
                    Instance?.StartCoroutine(DelayedResyncCurrencyTo(sender, 2f));
                }
            });

            // Boat ownership packets
            NetworkManager.RegisterHandler(PacketType.BoatOwnershipChanged, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadBoatOwnershipChanged(reader);
                EconomySyncManager?.OnBoatOwnershipChangedReceived(packet);
            });

            NetworkManager.RegisterHandler(PacketType.BoatPurchaseRequest, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadBoatPurchaseRequest(reader);
                EconomySyncManager?.OnBoatPurchaseRequestReceived(sender, packet);
            });

            // Guest shipyard order -> host charges the shared wallet
            NetworkManager.RegisterHandler(PacketType.ShipyardOrderRequest, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadShipyardOrderRequest(reader);
                TradingSyncManager?.OnShipyardOrderRequestReceived(sender, packet);
            });

            // Trading sync packets
            NetworkManager.RegisterHandler(PacketType.PriceKnowledgeSync, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadPriceKnowledgeSync(reader);
                TradingSyncManager?.OnPriceKnowledgeSyncReceived(packet);
            });

            NetworkManager.RegisterHandler(PacketType.PriceDiscovery, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadPriceDiscovery(reader);
                TradingSyncManager?.OnPriceDiscoveryReceived(packet);
            });

            NetworkManager.RegisterHandler(PacketType.IslandSupplySync, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadIslandSupplySync(reader);
                TradingSyncManager?.OnIslandSupplySyncReceived(packet);
            });

            NetworkManager.RegisterHandler(PacketType.MarketTradeRequest, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadMarketTradeRequest(reader);
                TradingSyncManager?.OnMarketTradeRequestReceived(sender, packet);
            });

            NetworkManager.RegisterHandler(PacketType.MarketTradeResult, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadMarketTradeResult(reader);
                TradingSyncManager?.OnMarketTradeResultReceived(packet);
            });

            NetworkManager.RegisterHandler(PacketType.ShopTradeRequest, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadShopTradeRequest(reader);
                TradingSyncManager?.OnShopTradeRequestReceived(sender, packet);
            });

            // Host -> requesting guest stall-trade verdict (restore-or-destroy the parked
            // optimistic item + "Not enough money." feedback on reject).
            NetworkManager.RegisterHandler(PacketType.ShopTradeResult, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadShopTradeResult(reader);
                TradingSyncManager?.OnShopTradeResultReceived(packet);
            });

            // Host -> all guests: crew spending-feed line. Guests never send this (the host observes
            // every trade), so no relay; pure UI/audio on the receiver.
            NetworkManager.RegisterHandler(PacketType.TradeFeedEvent, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadTradeFeedEvent(reader);
                TradeFeed.OnRemoteTradeFeedEvent(packet);
            });

            // (v0.2.25) Host -> one guest: destroy your local ghost copy of an instanceId the host has
            // repeatedly denied as UNKNOWN (reason=1). Targeted - no relay; guest-side guards ensure only
            // a genuinely loose, untracked local item is destroyed.
            NetworkManager.RegisterHandler(PacketType.GhostItemPurge, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadGhostItemPurge(reader);
                ItemSyncManager?.OnRemoteGhostItemPurge(packet);
            });

            // Guest -> host: guest's join coroutine finished; reply with a targeted mission-cargo
            // resync so a partially-applied join snapshot cannot hide mission crates from the joiner.
            // A snapshot lost outright never runs the join coroutine, so this request never arrives
            // for that failure mode. Host-targeted request: no relay to other guests.
            NetworkManager.RegisterHandler(PacketType.GuestJoinComplete, (sender, reader) =>
            {
                PacketSerializer.ReadGuestJoinComplete(reader);
                ItemSyncManager?.ResyncMissionCargoTo(sender);
                // The join snapshot always applies items un-nailed (no nail flag on the 0.2.22 wire),
                // so replay nailed state to the joiner as targeted NailState packets.
                ItemSyncManager?.ResyncNailedStateTo(sender);
                // The hung-lantern joint is not persisted or in the snapshot either; replay hung state AFTER
                // the nailed resync so the hook is on-wall before the lantern re-hangs (issue #4).
                ItemSyncManager?.ResyncHungStateTo(sender);
                // Carrier cargo arrives in the snapshot as plain world items; tuck it back into the
                // port cargo carriers (v0.2.29 cargo transport sync).
                ItemSyncManager?.ResyncCargoCarriersTo(sender);
            });

            // Ping loop (F8 overlay diagnostics). Both legs are UNRELIABLE on purpose: the number
            // should measure the same path the high-rate gameplay sync uses, and a lost probe just
            // means no sample until the next 2s cycle. SendTime is the REQUESTER's clock echoed back
            // verbatim, so only the requester's own Time.realtimeSinceStartup is ever compared.
            NetworkManager.RegisterHandler(PacketType.PingRequest, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadPingRequest(reader);
                NetworkManager?.SendUnreliable(sender, PacketType.PingReply, w =>
                    PacketSerializer.WritePingReply(w, new PingReplyPacket { SendTime = packet.SendTime }));
            });

            NetworkManager.RegisterHandler(PacketType.PingReply, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadPingReply(reader);
                float rttMs = (Time.realtimeSinceStartup - packet.SendTime) * 1000f;
                NetworkStats.RecordPing(sender, rttMs);
            });

            // Day Logs Full Sync
            NetworkManager.RegisterHandler(PacketType.DayLogsFullSync, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadDayLogsFullSync(reader);
                TradingSyncManager?.OnDayLogsFullSyncReceived(packet);
            });

            // Transaction Delta
            NetworkManager.RegisterHandler(PacketType.TransactionDelta, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadTransactionDelta(reader);
                TradingSyncManager?.OnTransactionDeltaReceived(packet);
            });

            // Shop Item Bought (vendor stall sync)
            NetworkManager.RegisterHandler(PacketType.ShopItemBought, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadShopItemBought(reader);
                ItemSyncManager?.OnRemoteShopItemBought(packet);
            });

            // NOTE: RecoveryStarted/RecoveryEnded packets removed - recovery now uses BoatWorldState resync
            // (handled by existing BoatWorldState handler in BoatSyncManager)

            // Fishing sync handlers
            NetworkManager.RegisterHandler(PacketType.FishingStateSync, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadFishingState(reader);
                FishingSyncManager?.OnFishingStateReceived(packet, sender);
            });

            NetworkManager.RegisterHandler(PacketType.FishingLineLengthSync, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadFishingLineLength(reader);
                FishingSyncManager?.OnFishingLineLengthReceived(packet, sender);
            });

            NetworkManager.RegisterHandler(PacketType.FishBite, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadFishBite(reader);
                FishingSyncManager?.OnFishBiteReceived(packet, sender);
            });

            NetworkManager.RegisterHandler(PacketType.FishEscape, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadFishEscape(reader);
                FishingSyncManager?.OnFishEscapeReceived(packet, sender);
            });

            NetworkManager.RegisterHandler(PacketType.FishCollectRequest, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadFishCollectRequest(reader);
                FishingSyncManager?.OnFishCollectRequestReceived(packet, sender);
            });

            NetworkManager.RegisterHandler(PacketType.FishCollectResponse, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadFishCollectResponse(reader);
                FishingSyncManager?.OnFishCollectResponseReceived(packet);
            });

            NetworkManager.RegisterHandler(PacketType.RodOwnerChanged, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadRodOwnerChanged(reader);
                FishingSyncManager?.OnRodOwnerChangedReceived(packet, sender);
            });

            NetworkManager.RegisterHandler(PacketType.FishingCast, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadFishingCast(reader);
                FishingSyncManager?.OnFishingCastReceived(packet, sender);
            });

            NetworkManager.RegisterHandler(PacketType.FishingBobberSync, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadFishingBobberSync(reader);
                FishingSyncManager?.OnFishingBobberSyncReceived(packet, sender);
            });

            // Chip log sync handlers
            NetworkManager.RegisterHandler(PacketType.ChipLogThrow, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadChipLogThrow(reader);
                ChipLogSyncManager?.OnChipLogThrowReceived(packet, sender);
            });

            NetworkManager.RegisterHandler(PacketType.ChipLogLineSync, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadChipLogLineSync(reader);
                ChipLogSyncManager?.OnChipLogLineSyncReceived(packet, sender);
            });

            // Navigation sync handlers
            NetworkManager.RegisterHandler(PacketType.NavItemState, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadNavItemState(reader);
                NavigationSyncManager?.OnRemoteNavItemStateChanged(packet, sender);
            });

            NetworkManager.RegisterHandler(PacketType.MapFoldState, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadMapFoldState(reader);
                NavigationSyncManager?.OnRemoteMapFoldStateChanged(packet, sender);
            });

            NetworkManager.RegisterHandler(PacketType.MapDrawRequest, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadMapDrawRequest(reader);
                NavigationSyncManager?.OnMapDrawRequest(sender, packet);
            });

            NetworkManager.RegisterHandler(PacketType.MapDrawResponse, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadMapDrawResponse(reader);
                NavigationSyncManager?.OnMapDrawResponse(packet);
                Patches.NavigationPatches.OnMapDrawResponseReceived(packet);
            });

            NetworkManager.RegisterHandler(PacketType.MapDrawLocked, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadMapDrawLocked(reader);
                NavigationSyncManager?.OnMapDrawLocked(packet);
            });

            NetworkManager.RegisterHandler(PacketType.MapDrawRelease, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadMapDrawRelease(reader);
                NavigationSyncManager?.OnMapDrawRelease(packet);
            });

            NetworkManager.RegisterHandler(PacketType.MapLineAdd, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadMapLine(reader);
                NavigationSyncManager?.OnRemoteMapLineAdded(packet, sender);
            });

            NetworkManager.RegisterHandler(PacketType.MapTempLine, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadMapTempLine(reader);
                NavigationSyncManager?.OnRemoteMapTempLineChanged(packet, sender);
            });

            NetworkManager.RegisterHandler(PacketType.MapFullSync, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadMapFullSync(reader);
                NavigationSyncManager?.OnMapFullSync(packet);
            });

            NetworkManager.RegisterHandler(PacketType.ChartSession, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadChartSession(reader);
                NavigationSyncManager?.OnRemoteChartSession(packet, sender);
            });

            NetworkManager.RegisterHandler(PacketType.ChartCursor, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadChartCursor(reader);
                NavigationSyncManager?.OnRemoteChartCursor(packet, sender);
            });

            // Cooking sync
            NetworkManager.RegisterHandler(PacketType.CookingState, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadCookingState(reader);
                CookingSyncManager?.OnCookingStateReceived(packet);
            });

            NetworkManager.RegisterHandler(PacketType.FoodPlaceOnStoveRequest, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadFoodPlaceOnStoveRequest(reader);
                CookingSyncManager?.OnFoodPlaceOnStoveRequest(packet, sender);
            });

            NetworkManager.RegisterHandler(PacketType.FoodRemoveFromStoveRequest, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadFoodRemoveFromStoveRequest(reader);
                CookingSyncManager?.OnFoodRemoveFromStoveRequest(packet, sender);
            });

            NetworkManager.RegisterHandler(PacketType.FoodCutRequest, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadFoodCutRequest(reader);
                CookingSyncManager?.OnFoodCutRequest(packet, sender);
            });

            NetworkManager.RegisterHandler(PacketType.FoodCutResult, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadFoodCutResult(reader);
                CookingSyncManager?.OnFoodCutResultReceived(packet);
            });

            NetworkManager.RegisterHandler(PacketType.FoodSaltRequest, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadFoodSaltRequest(reader);
                CookingSyncManager?.OnFoodSaltRequest(packet, sender);
            });

            NetworkManager.RegisterHandler(PacketType.SoupAddFoodRequest, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadSoupAddFoodRequest(reader);
                CookingSyncManager?.OnSoupAddFoodRequest(packet, sender);
            });

            NetworkManager.RegisterHandler(PacketType.SoupAddWaterRequest, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadAddWaterRequest(reader);
                CookingSyncManager?.OnSoupAddWaterRequest(packet, sender);
            });

            NetworkManager.RegisterHandler(PacketType.KettleAddTeaRequest, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadKettleAddTeaRequest(reader);
                CookingSyncManager?.OnKettleAddTeaRequest(packet, sender);
            });

            NetworkManager.RegisterHandler(PacketType.KettlePourRequest, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadKettlePourRequest(reader);
                CookingSyncManager?.OnKettlePourRequest(packet);
            });

            NetworkManager.RegisterHandler(PacketType.FuelInsertedEvent, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadFuelInserted(reader);
                CookingSyncManager?.OnFuelInsertedReceived(packet, sender);
            });

            // NPC Boat sync handlers
            NetworkManager.RegisterHandler(PacketType.NPCBoatState, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadNPCBoatState(reader);
                NPCBoatSyncManager?.OnNPCBoatStateReceived(packet);
            });

            NetworkManager.RegisterHandler(PacketType.NPCBoatSnapshot, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadNPCBoatSnapshot(reader);
                NPCBoatSyncManager?.OnNPCBoatSnapshotReceived(packet);
            });

            // Authoritative NPC boat damage/sink state (host -> guest).
            NetworkManager.RegisterHandler(PacketType.NPCBoatDamage, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadNPCBoatDamage(reader);
                NPCBoatSyncManager?.OnNPCBoatDamageReceived(packet);
            });

            // Guest reports ramming an NPC boat; host applies damage + relays state.
            NetworkManager.RegisterHandler(PacketType.NPCBoatHitRequest, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadNPCBoatHitRequest(reader);
                NPCBoatSyncManager?.OnNPCBoatHitRequestReceived(packet);
            });

            // Cleaning sync
            NetworkManager.RegisterHandler(PacketType.CleaningStroke, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadCleaningStroke(reader);
                CleaningSyncManager?.OnRemoteCleaningStroke(packet, sender);
            });

            NetworkManager.RegisterHandler(PacketType.CleanFully, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadCleanFully(reader);
                CleaningSyncManager?.OnRemoteCleanFully(packet, sender);
            });
        }

        private void LateUpdate()
        {
            // Re-pin the world-space co-op pause menu AFTER the camera has followed the bobbing observerMirror
            // this frame (Camera.main moves in LateUpdate). Doing this in Update lagged the menu one frame, so
            // it bobbed/drifted on screen while the boat moved. Cheap + self-guards on IsOpen.
            SailwindCoop.UI.CoopPauseMenu.LatePin();
        }

        private void Update()
        {
            // Process command system (works even without Steam)
            CommandProcessor?.Update();

            if (!_steamInitialized) return;

            // Process Steam callbacks
            Profiler?.StartMeasure();
            LobbyManager.RunCallbacks();
            Profiler?.EndMeasureSteamCallbacks();

            // Process incoming network packets
            Profiler?.StartMeasure();
            NetworkManager?.ResetPacketCounter();
            NetworkManager?.ProcessIncomingPackets();
            Profiler?.EndMeasurePacketProcessing();

            // Co-op hosting/joining lives in the main + pause menus (see CoopMenu/CoopPauseMenu);
            // F9 was removed. Keep the menu labels + player list live while a menu is open.
            SailwindCoop.UI.CoopMenu.Tick();
            SailwindCoop.UI.CoopPauseMenu.Tick();

            // 2s ping loop for the F8 overlay: each machine probes its DIRECT peers (star topology,
            // so a guest's ConnectedPeers is just the host, and the host's is every guest). Unreliable
            // on purpose - see the PingRequest handler comment.
            if (IsMultiplayer && NetworkManager != null)
            {
                if (Time.realtimeSinceStartup - _lastPingSendTime > PingInterval)
                {
                    _lastPingSendTime = Time.realtimeSinceStartup;
                    foreach (var peer in NetworkManager.ConnectedPeers)
                    {
                        NetworkManager.SendUnreliable(peer, PacketType.PingRequest, w =>
                            PacketSerializer.WritePingRequest(w, new PingRequestPacket { SendTime = Time.realtimeSinceStartup }));
                    }
                }
            }
            else if (NetworkStats.PingMs.Count > 0)
            {
                // Left the lobby - drop stale readings so a future session can't show a ghost ping.
                NetworkStats.Clear();
            }
        }

        private void OnApplicationQuit()
        {
            if (LobbyManager.IsInLobby && !_joinedAsGuest)
            {
                // Host: save their world normally.
                Log.LogInfo("Host quitting while in lobby - saving game state");
                SaveLoadManager.instance?.SaveGame(compressed: true);
            }
            else if (_joinedAsGuest)
            {
                // Guest: persist co-op needs to the PHANTOM file before the app closes. currentSlot==99
                // guarantees this lands on coop_session.save, never a real slot. (The guest quit path may
                // have already left the lobby, so this is keyed off the session-stable _joinedAsGuest, not
                // IsInLobby.) SaveCoopSession is a no-op if currentSlot somehow isn't 99.
                Log.LogInfo("Guest quitting - persisting co-op needs to phantom file (currentSlot=99)");
                CoopSave.SaveCoopSession();
            }
        }

        /// <summary>
        /// N-player (Phase 5): per-peer cleanup of the per-SteamId control state when ONE crew member
        /// disconnects. Drops just that peer's push, pump input, helm lease, and held-item carrier slot so
        /// the remaining crew keep functioning; if NO peers remain after the drop, falls back to a full
        /// reset (the old global ClearState / ClearTrackedControls behavior). Call AFTER RemovePeer has
        /// pruned ConnectedPeers, so the count reflects the live crew. At N&lt;=2 the leaver is the only peer,
        /// so this always hits the full-reset branch - identical to the old global cleanup.
        /// </summary>
        private void CleanupPeerControlState(SteamId peer)
        {
            // Drop just this peer's per-SteamId entries.
            PushSyncManager?.OnPeerDisconnected(peer);
            DamageSyncManager?.OnPeerDisconnected(peer);
            ControlSyncManager?.ReleaseHelmLeasesForPeer(peer);
            // Forget this peer's held-item VISUAL slot. On the host, ItemSyncManager.OnPeerDisconnected
            // (called earlier with the leaver's last pos) already did this AND dropped the items; this call
            // is idempotent there and additionally covers a GUEST seeing a fellow guest leave (where the
            // host-only OnPeerDisconnected early-returns, leaving the visual slot dangling).
            ItemSyncManager?.ForgetCarrierHeldItemVisual(peer);
            // Drop the leaver's F8-overlay ping reading (harmless if absent).
            NetworkStats.Forget(peer);

            // Full reset only when the crew is now empty (e.g. a guest's only peer - the host - left, or the
            // last guest left the host). This also clears the LOCAL active-control tracking, which must NOT
            // be wiped while other crew are still aboard.
            bool noPeersRemain = (NetworkManager?.ConnectedPeers?.Count ?? 0) == 0;
            if (noPeersRemain)
            {
                PushSyncManager?.ClearState();
                SailwindCoop.Patches.ControlPatches.ClearTrackedControls();
                VerboseLogger.LobbyEvent("Last peer left - full push/control reset");
            }
        }

        /// <summary>
        /// Suppress (or restore) the guest's saving while in co-op. A guest is on the HOST's shared boat, so
        /// any save to the guest's own slot would clobber their solo progress. We toggle the game's own
        /// autosave + save-on-sleep flags rather than patching the save path.
        /// </summary>
        private static void SetGuestSaveSuppressed(bool suppressed)
        {
            var slm = SaveLoadManager.instance;
            if (slm == null) return;
            slm.enableAutosave = !suppressed;
            slm.enableSaveOnSleep = !suppressed;
            Log.LogInfo($"[Coop] Guest save suppression {(suppressed ? "ON" : "OFF")} (autosave={slm.enableAutosave})");
        }

        /// <summary>
        /// A guest's co-op session has ended (host closed the server, the guest left, or the host dropped).
        /// The guest must NOT linger in the host's world on the host's save, so warn them why and close the
        /// game. Runs at most once; safe even after Steam transfers lobby ownership to the
        /// guest, because it keys off the session-stable _joinedAsGuest flag, not IsHost.
        /// </summary>
        // (v0.2.27) HOST-side: peers that sent a version Handshake this lobby. Consulted by the grace
        // watchdog below; cleared per lobby (fresh lobby = fresh set) and per leaving member.
        private static readonly System.Collections.Generic.HashSet<SteamId> _versionHandshaked =
            new System.Collections.Generic.HashSet<SteamId>();

        /// (v0.2.27) Host-side grace watchdog: if an admitted guest sends no version Handshake within
        /// the window, they are almost certainly on a pre-v0.2.27 build - the version gate cannot see
        /// them, so at least tell the host. 15s is generous for one reliable packet right after
        /// peering, and early enough to act before the crew sails off with a desyncing member.
        private static System.Collections.IEnumerator WarnIfNoVersionHandshake(Steamworks.Friend friend)
        {
            yield return new WaitForSecondsRealtime(15f);
            if (!IsHost || !LobbyManager.IsInLobby || _versionHandshaked.Contains(friend.Id)) yield break;

            bool stillInLobby = false;
            foreach (var member in LobbyManager.LobbyMembers)
                if (member.Id == friend.Id) { stillInLobby = true; break; }
            if (!stillInLobby) yield break;

            Log.LogWarning($"[VERSION] No version handshake from {friend.Name} ({friend.Id}) after 15s - likely a pre-v0.2.27 mod build");
            Notify($"{friend.Name} sent no version handshake - they are likely on an older mod build. Everyone should run v{PluginVersion}.", 10f);
        }

        private static void EndGuestSessionAndQuit(string reason)
        {
            if (!_joinedAsGuest || _endingGuestSession) return;
            _endingGuestSession = true;
            Log.LogInfo($"[Coop] Guest co-op ended: {reason} - warning then quitting");
            if (Instance != null) Instance.StartCoroutine(GuestQuitRoutine(reason));
            else Application.Quit();
        }

        private static System.Collections.IEnumerator GuestQuitRoutine(string reason)
        {
            // Make sure we're out of the (possibly hostless) lobby, then freeze the world while the warning shows.
            if (LobbyManager.IsInLobby) LobbyManager.LeaveLobby();
            Time.timeScale = 0f;

            string msg = reason + "\n\nThe game will now close.";
            Notify(msg, 10f);
            // Persistent backup in case the toast UI isn't up.
            if (Sleep.instance != null && Sleep.instance.recoveryText != null)
                Sleep.instance.recoveryText.text = msg;

            yield return new UnityEngine.WaitForSecondsRealtime(6f);
            Application.Quit();
        }

        // (v0.2.25) How long a guest waits for the host's BoatWorldState join snapshot before concluding
        // the host never admitted it. Deliberately GENEROUS: the host legitimately defers the join send up
        // to 30s while asleep/time-warping (SendJoinStateWhenReady), plus transfer time for a large
        // snapshot - so 45s of realtime silence is a confident "not admitted / join failed" signal, not a
        // slow host. A const (not config): there is no guest-side join config knob to sit next to, and a
        // player-tunable value here only creates support noise.
        private const float GuestJoinSnapshotTimeoutSeconds = 45f;

        /// <summary>
        /// (v0.2.25) Guest-side join-state watchdog. The lobby-level admission gate on the HOST only
        /// withholds OnPlayerJoined - it cannot reach across and stop THIS guest's sync managers, and the
        /// raw P2P session still connects. Before this watchdog, a refused (or snapshot-lost) guest just
        /// silently played on, half-initialized, keeping stale phantom-save survival needs and receiving
        /// no world state (proven in the v0.2.23/24 playtest logs; a comment in HandleLobbyMemberJoined
        /// even claimed this watchdog existed when it did not). Polls the authoritative snapshot-arrival
        /// flag (BoatSyncManager.HasReceivedWorldState, set the moment BoatWorldState is received) and, if
        /// it never arrives, warns the player and runs the standard guest leave path
        /// (EndGuestSessionAndQuit -> LeaveLobby + warn + quit). Never fires for the host (started only in
        /// the _joinedAsGuest branch of OnLobbyJoined) and never fires when the snapshot arrived.
        /// </summary>
        private static System.Collections.IEnumerator GuestJoinWatchdog()
        {
            float t0 = Time.unscaledTime;
            while (Time.unscaledTime - t0 < GuestJoinSnapshotTimeoutSeconds)
            {
                // Snapshot arrived: the host admitted us and the normal join machinery owns everything
                // from here. Stand down permanently (mid-session recoveries re-use BoatWorldState but the
                // flag stays true, so the watchdog can never mis-fire later).
                if (BoatSyncManager.HasReceivedWorldState) yield break;
                // Session already ending for another reason (host left, we left, connection dropped):
                // that path owns the messaging; don't stack a second warn/quit on top.
                if (!_joinedAsGuest || _endingGuestSession) yield break;
                yield return new UnityEngine.WaitForSecondsRealtime(1f); // realtime: survives timeScale changes
            }

            if (BoatSyncManager.HasReceivedWorldState || !_joinedAsGuest || _endingGuestSession) yield break;

            Log.LogError($"[Coop] Join-state watchdog: no BoatWorldState snapshot from the host within {GuestJoinSnapshotTimeoutSeconds:F0}s - the host did not admit this client (or the join snapshot was lost). Leaving the session.");
            EndGuestSessionAndQuit("The host did not admit you to the crew (or your join failed) - no world state ever arrived.\nAsk the host to add you as a Steam friend or enable Coop.AllowCrewInvites, then try again.");
        }

        private void OnDestroy()
        {
            Log.LogInfo("Plugin OnDestroy - cleaning up for hot reload...");

            VerboseLogger.Shutdown();

            // Leave lobby first (triggers cleanup events)
            if (LobbyManager.IsInLobby)
            {
                LobbyManager.LeaveLobby();
            }
            // A hot-reload keeps the process alive but the delayed guest-quit coroutine on this
            // (destroyed) Plugin never fires, so SaveSlots.currentSlot can linger at 99. Clear the redirect
            // context now (after OnLobbyLeft already wrote the phantom) so post-reload solo saves go to the real
            // slot and can't contaminate the phantom file.
            CoopSave.ClearContext();

            // Cleanup managers
            RemotePlayerManager?.DespawnAll();
            BoatSyncManager?.Reset();
            ControlSyncManager?.Reset();
            WeatherSyncManager?.Reset();
            TimeSyncManager?.Reset();
            SurvivalSyncManager?.Reset();
            ItemSyncManager?.Reset();
            SleepSyncManager?.OnDisconnect();
            DamageSyncManager?.Reset();
            ShipyardSyncManager?.Reset();
            MissionSyncManager?.Reset();
            EconomySyncManager?.Reset();
            TradingSyncManager?.Reset();
            Patches.EconomyPatches.ShopkeeperSellItemPatch.ResetPendingStallBuys(); // drain parked stall buys (see OnLobbyLeft)
            FishingSyncManager?.Reset();
            ChipLogSyncManager?.Reset();
            NavigationSyncManager?.Reset();
            ChartKitGhostManager?.Reset(); // destroys spawned ghost kit objects; a reloaded plugin can't reach them
            CookingSyncManager?.Reset();
            NPCBoatSyncManager?.Reset();
            CleaningSyncManager?.Reset();
            PushSyncManager?.ClearState(); // PushSyncManager has no Reset() (see OnLobbyLeft)
            _joinPendingPeers.Clear(); // clear the per-peer join-pending set on hot-reload teardown too

            // Shutdown networking
            NetworkManager?.Shutdown();
            LobbyManager.Shutdown();

            // Unpatch Harmony
            _harmony?.UnpatchSelf();

            Log.LogInfo("Plugin cleanup complete");
        }

        /// <summary>
        /// Reset IgnoreRemoteItemDestruction after guest join completes.
        /// N-player (Phase 3): closes ONE join window (ref-counted). The guard only actually
        /// re-enables remote destruction once every in-flight join's window has closed, so an
        /// already-settled guest is never caught mid-join and two overlapping joins don't clobber
        /// each other. At N=1 this is the only open window -> guard re-enabled here, as before.
        /// </summary>
        /// <summary>
        /// JOIN-WHILE-HOST-ASLEEP fix: wait until the host is awake and at normal time scale, then send
        /// the full join state. Real-frame WaitUntil (not time-scaled) with a hard 30s fallback so a stuck
        /// pause/handshake can't strand the joining guest forever.
        /// </summary>
        private static System.Collections.IEnumerator SendJoinStateWhenReady(Steamworks.Friend friend)
        {
            float t0 = UnityEngine.Time.unscaledTime;
            yield return new UnityEngine.WaitUntil(() =>
                ((SleepSyncManager == null || SleepSyncManager.CurrentState == SleepSyncManager.SleepState.Awake)
                 && UnityEngine.Time.timeScale == 1f
                 // The readiness gate must also cover RECOVERY. RecoveryRecoverPlayerPatch calls
                 // ForceWakeCrew() (-> Awake + timeScale=1) BEFORE vanilla Recovery sets GameState.recovering=true
                 // and teleports the shared boat. Without these checks a deferred join could fire in that window,
                 // snapshotting the boat mid-recovery (transient/wreck location) and arming a 2nd join + destruction
                 // guard racing the recovery. Hold until recovery (and any other join) finishes; the post-recovery
                 // ResendWorldStateAfterRecovery broadcast then covers this guest.
                 && !GameState.recovering
                 && !BoatSyncManager.IsJoinInProgress)
                || UnityEngine.Time.unscaledTime - t0 > 30f);
            // The guest may have left while we waited; don't push full state to an absent peer.
            if (!HasConnectedGuest) { _joinPendingPeers.Remove(friend.Id); yield break; }
            VerboseLogger.LobbyEvent($"Host ready ({UnityEngine.Time.unscaledTime - t0:F1}s after join); sending deferred join state");
            SendJoinStateToGuest(friend);
        }

        /// <summary>
        /// Host-side: push the full world/boat/economy/mission/map state to a freshly joined guest. Extracted
        /// from OnPlayerJoined so it can be either run inline (host awake) or deferred (host asleep/warping).
        /// </summary>
        private static void SendJoinStateToGuest(Steamworks.Friend friend)
        {
            // Populate item registry now (save is loaded, guest is joining)
            RunJoinStep("PopulateItemRegistry", () => ItemSyncManager?.PopulateRegistryFromScene());

            // Ignore ItemDestroyed packets during guest join (guest's cleanup destroys items
            // that shouldn't affect host). N-player (Phase 3): ref-counted guard, so an already-settled
            // guest's legitimate destroy during an overlapping join is preserved; destruction re-enables
            // only once ALL in-flight joins finish. At N=1 there is exactly one join -> identical to before.
            if (ItemSyncManager != null)
            {
                ItemSyncManager.BeginJoinDestructionGuard();
                // Reset after 30 seconds (join takes ~15-20s)
                Instance.StartCoroutine(ResetIgnoreDestructionAfterDelay(30f));
            }

            // N-player (Phase 3): TARGETED JOIN RESYNC. The heavy full-state-on-join sends go ONLY to the
            // joining peer (friend.Id), never SendToAll - re-sending to settled crew would re-run their
            // ~15-20s teleport-join coroutine and needlessly re-broadcast mission/economy/trading/NPC state.
            // At N=1 the joiner IS the only peer, so SendXTo(joiner) == the old SendToAll(oneGuest). (RECOVERY
            // resync stays a broadcast elsewhere; it legitimately re-syncs all crew.)
            //
            // JOIN-STATE ROBUSTNESS: each step runs in its OWN try/catch. Without this, one throwing
            // step (e.g. a bad map foldable) would abort the whole method, silently starving the guest of
            // EVERY later send (no CurrencySync/Mission/Trading/Reputation for the whole session, leaving
            // them on their solo-save wallet). A failure is loud (LogError names the step) and the
            // remaining sends still go out.
            RunJoinStep("BoatWorldState", () => BoatSyncManager.SendBoatWorldStateTo(friend.Id));
            // (v0.2.31) Shipyard Expansion sail extras: one SERigState blob per boat, sent right after the
            // world snapshot on the same reliable, ordered channel. The guest's handler BUFFERS them while
            // IsJoinInProgress; the join applies each one at the tail of Phase A, after that boat's vanilla
            // customization rebuild and before the frame-wait that precedes the rope re-key. Hard no-op when
            // SE is not installed, so a vanilla crew sends nothing at all.
            RunJoinStep("SERigState", () => ShipyardSyncManager?.SendAllRigBlobsTo(friend.Id));
            // (v0.2.32) Authoritative door/hatch/gunport states: the guest's phantom load may have
            // restored ITS OWN door states (NAND Tweaks toggleDoors); the host's reliable sends win.
            RunJoinStep("TrapdoorStates", () => TrapdoorSyncManager?.SendAllStatesTo(friend.Id));
            // (v0.2.32) Cutter deployed/stowed + live transform: the mod persists cutterActive in
            // modData, which co-op does NOT transfer - without this send host and guest diverge on
            // the second boat from the first frame.
            RunJoinStep("CutterState", () => LeopardSyncManager?.SendCutterStateTo(friend.Id));
            // JOIN helm seed: HelmState is edge-triggered, so a guest joining while the host holds the
            // wheel steady would never receive the current rudder angle. Re-broadcast it now. This is a
            // host-side SEND of current helm state - orthogonal to the N-player helm LEASE (which arbitrates
            // guest INPUT). The broadcast is idempotent on already-settled crew (they re-apply the same value).
            RunJoinStep("ResendHelm", () => ControlSyncManager?.ResendHelmForCurrentBoat());
            // Stale reef on join: RopeState is edge-triggered like HelmState, so a guest joining after the
            // host reefed/angled sails would never receive the current rope lengths and would board with sails at
            // the default trim. Re-send all current rope lengths for the shared boat now (reliable terminals), so
            // the joiner's reef/angle state matches. Same host-side seed pattern as the helm re-broadcast above.
            RunJoinStep("ResendRope", () => ControlSyncManager?.ResendRopeForCurrentBoat());
            // Set shared boat for host too
            RunJoinStep("SetSharedBoat", () => SleepSyncManager?.SetSharedBoat(GameState.lastBoat?.name ?? ""));
            // INDEPENDENT NEEDS: do not seed the guest's stats on join; the guest keeps its own.
            RunJoinStep("MissionFullState", () => MissionSyncManager?.SendFullStateTo(friend.Id));
            RunJoinStep("EconomyFullState", () => EconomySyncManager?.SendFullStateTo(friend.Id));
            RunJoinStep("TradingFullState", () => TradingSyncManager?.SendFullStateTo(friend.Id));
            RunJoinStep("NPCBoatSnapshot", () => NPCBoatSyncManager?.SendSnapshotTo(friend.Id));
            // One-shot weather/wave state so WavesInertia + Crest crossfade inputs land on the
            // joiner immediately instead of waiting for the next periodic broadcast.
            RunJoinStep("WeatherState", () => WeatherSyncManager?.SendWeatherStateTo(friend.Id));

            // Send initial map data for all maps
            RunJoinStep("MapFullSync", () =>
            {
                var foldables = UnityEngine.Object.FindObjectsOfType<ShipItemFoldable>();
                foreach (var foldable in foldables)
                {
                    if (foldable.allowCharting && foldable.mapChart != null)
                    {
                        NavigationSyncManager?.SendMapFullSyncToGuest(friend.Id.Value, foldable);
                    }
                }
                // Ghost kit late-join replay: if someone is mid-charting, the joiner missed the
                // ChartSession start - re-send every active session so the ghost appears.
                NavigationSyncManager?.ReplayActiveChartSessionsTo(friend.Id.Value);
            });

            // The join state is out; the guest still won't stream position until its load completes, so
            // re-baseline THIS peer's liveness clock and clear the pending flag together. This bounds the
            // silence window the sleep watchdog sees to the guest's post-teleport load, not the whole deferral.
            if (RemotePlayerManager != null) RemotePlayerManager.NoteJoinStateSent(friend.Id);
            _joinPendingPeers.Remove(friend.Id); // this peer's join is done -> un-blind the watchdog for it
        }

        /// <summary>
        /// JOIN-STATE ROBUSTNESS: run one join-state send step, converting an exception into a
        /// LOUD LogError naming the step instead of aborting the remaining sends.
        /// </summary>
        private static void RunJoinStep(string stepName, System.Action step)
        {
            try
            {
                step();
            }
            catch (System.Exception ex)
            {
                Log.LogError($"[JOIN] Join-state step '{stepName}' FAILED (guest may be missing this state): {ex}");
            }
        }

        private static System.Collections.IEnumerator ResetIgnoreDestructionAfterDelay(float seconds)
        {
            // REALTIME wait. This guard suppresses a joining guest's mid-join ItemDestroyed
            // packets for the wall-clock ~15-20s of its teleport-join cleanup. A scaled WaitForSeconds
            // would collapse to ~30/16 = ~1.9s if the HOST started a co-op sleep (timeScale=16) within the
            // window, ending the guard ~13-18s early and re-applying the still-joining guest's destructions
            // on the host.
            // Matches the sibling realtime waits (GuestQuitRoutine / ClearRecoveryTextAfter / SendJoinStateWhenReady).
            yield return new UnityEngine.WaitForSecondsRealtime(seconds);
            if (ItemSyncManager != null)
            {
                ItemSyncManager.EndJoinDestructionGuard();
            }
        }

        /// <summary>
        /// Clear the recovery TextMesh after a delay. The guest who got the "host is recovering" message
        /// stays in-world (no reload), so nothing else clears it. Only clears if it still shows our message,
        /// so a genuine recovery message the guest triggers later isn't stomped. Realtime so a pause (timescale
        /// 0) doesn't pin the text indefinitely.
        /// </summary>
        private static System.Collections.IEnumerator ClearRecoveryTextAfter(float seconds, string onlyIfEquals)
        {
            yield return new UnityEngine.WaitForSecondsRealtime(seconds);
            if (Sleep.instance != null && Sleep.instance.recoveryText != null
                && Sleep.instance.recoveryText.text == onlyIfEquals)
            {
                Sleep.instance.recoveryText.text = "";
            }
        }

        // GUEST-ONLY join robustness: the guest's join coroutine (BoatStateApplicator) sends ONE
        // EconomySyncRequest when it settles - if THAT send (or the host's reply) is lost/starved, the guest
        // plays the whole session on its solo-save wallet. This loop waits for the normal join window
        // (~20s realtime; the join coroutine takes ~15-20s), then resends EconomySyncRequest every 5s
        // (realtime, max 6 tries) until the first authoritative CurrencySync has actually been APPLIED
        // (EconomySyncManager.FirstCurrencyAppliedSinceJoin, set in OnCurrencySyncReceived). All waits are
        // realtime so a co-op sleep timeScale can't collapse them. Keeps the join coroutine's one-shot intact.
        private System.Collections.IEnumerator GuestEconomySyncRetry()
        {
            yield return new UnityEngine.WaitForSecondsRealtime(20f);
            for (int attempt = 1; attempt <= 6; attempt++)
            {
                if (!IsMultiplayer || IsHost || !_joinedAsGuest) yield break;
                var econ = EconomySyncManager;
                if (econ == null || econ.FirstCurrencyAppliedSinceJoin)
                {
                    if (attempt > 1) Log.LogInfo("[ECONOMY] Guest wallet sync confirmed; stopping EconomySyncRequest retries");
                    yield break;
                }
                Log.LogWarning($"[ECONOMY] No CurrencySync applied since join - resending EconomySyncRequest (retry {attempt}/6)");
                NetworkManager?.SendToAllReliable(PacketType.EconomySyncRequest, w => { });
                yield return new UnityEngine.WaitForSecondsRealtime(5f);
            }
            if (!(EconomySyncManager?.FirstCurrencyAppliedSinceJoin ?? false))
                Log.LogError("[ECONOMY] Guest wallet STILL unsynced after 6 EconomySyncRequest retries - shared-wallet state is suspect");
        }

        // One deferred CoopSave.TrySelfHealSave() per guest join (safe no-op unless this load had
        // to skip corrupt saveables). Realtime so a host time-warp can't collapse the settle window.
        private System.Collections.IEnumerator GuestSelfHealSaveAfterJoin()
        {
            yield return new UnityEngine.WaitForSecondsRealtime(60f);
            if (!_joinedAsGuest) yield break;
            CoopSave.TrySelfHealSave();
        }

        // HOST-ONLY: fired from the EconomySyncRequest handler to re-assert the authoritative wallet to ONE
        // joining guest a short delay after the immediate targeted send, so it lands AFTER that guest's
        // LoadGame/economy has fully settled (the guest's join coroutine may still be finalizing when its
        // request arrives). Cache-neutral (ResyncCurrencyTo -> SendCurrencySync(target), does NOT touch
        // _lastCurrency), so the normal on-change broadcast still diffs correctly for everyone else, and the
        // element-wise replace is idempotent. REALTIME wait, matching the sibling coroutines above, so a
        // host-side timeScale change (e.g. co-op sleep) can't collapse the delay. ResyncCurrencyTo is itself
        // IsHost-gated, so this no-ops if the role flipped in the interim.
        private static System.Collections.IEnumerator DelayedResyncCurrencyTo(Steamworks.SteamId target, float seconds)
        {
            yield return new UnityEngine.WaitForSecondsRealtime(seconds);
            EconomySyncManager?.ResyncCurrencyTo(target);
        }
    }
}

