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
        // load. This is the v0.3.0 build (shipyard sail-sync rope-cache fix, host-settings reconcile +
        // mod manifest report, readable message panel, avatar look/crouch fixes, join sky-fall + toast,
        // held-item smoothing, sustained-divergence escalation); shows as 0.3.0.
        public const string PluginVersion = "0.3.1";

        public static Plugin Instance { get; private set; }
        public static ManualLogSource Log { get; private set; }

        // Crew cap (host + guests) for the Steam lobby. Default 8 (host + 7). Read by
        // SteamLobbyManager.MaxPlayers, which feeds Steam's lobby max-members argument.
        public static ConfigEntry<int> MaxPlayersConfig { get; private set; }
        public static ConfigEntry<bool> AllowCrewInvitesConfig { get; private set; }
        public static ConfigEntry<string> IgnoredInvitersConfig { get; private set; }
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
        // (v0.3.0) Kill switch for the guest hull-physics derivation - see the Config.Bind call for why a
        // feature that cannot be tested solo needs one. Read by DamagePatches every frame, so it is live.
        public static ConfigEntry<bool> GuestHullPhysicsConfig { get; private set; }

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
        // (v0.3.0) Squat shaping + ground plant - see the Config.Bind calls for the full rationale.
        public static ConfigEntry<float> CrouchThighLiftDegConfig { get; private set; }
        public static ConfigEntry<float> CrouchHipSetbackMaxMetersConfig { get; private set; }
        public static ConfigEntry<float> AvatarSoleOffsetMetersConfig { get; private set; }

        // (v0.3.0) This player's chosen avatar appearance, persisted as a "key=value;..." string.
        public static ConfigEntry<string> AppearanceConfig { get; private set; }
        public static ConfigEntry<float> CoopMenuButtonScaleConfig { get; private set; }
        private static bool _localAppearanceLoaded;
        private static Player.CoopAppearance _localAppearance;

        /// <summary>
        /// This machine's chosen look. Parsed once from config; an empty setting resolves to a
        /// deterministic look derived from the player's own SteamId, so a player who never opens the
        /// character screen still has a face of their own rather than slot 0 like everyone else.
        /// </summary>
        public static Player.CoopAppearance LocalAppearance
        {
            get
            {
                if (!_localAppearanceLoaded)
                {
                    string raw = AppearanceConfig != null ? AppearanceConfig.Value : null;
                    if (string.IsNullOrEmpty(raw))
                    {
                        ulong id = 0UL;
                        try { if (SteamClient.IsValid) id = SteamClient.SteamId; } catch { }
                        // Only LATCH once we could actually read our own id. A first read before Steam is
                        // valid would otherwise freeze the all-zero seed look for the whole process, and
                        // every player in that situation would end up identical - the precise outcome the
                        // deterministic default exists to avoid. Until then, recompute each read.
                        if (id != 0UL) _localAppearanceLoaded = true;
                        _localAppearance = Player.CoopAppearance.DeterministicFor(id);
                    }
                    else
                    {
                        _localAppearanceLoaded = true;
                        _localAppearance = Player.CoopAppearance.Deserialize(raw);
                    }
                }
                return _localAppearance;
            }
        }

        /// <summary>Adopt a new look and persist it. Callers must rebuild the local body to see it - the
        /// appearance is baked when the clone is activated, not applied live (see CoopAppearance).</summary>
        public static void SetLocalAppearance(Player.CoopAppearance a)
        {
            _localAppearance = a;
            _localAppearanceLoaded = true;
            if (AppearanceConfig != null) AppearanceConfig.Value = a.Serialize();
        }

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

        // (v0.3.0) DEBUG: preview of the co-op message panel. The panel only ever appears on a failed join,
        // so without this the only way to check that it is readable is to arrange a real mismatched session
        // and then read fast before the refusal quits it. Entirely inert unless the bool is on.
        public static ConfigEntry<bool> PreviewMessagePanelConfig { get; private set; }
        public static ConfigEntry<string> PreviewMessagePanelKeyConfig { get; private set; }
        public static ConfigEntry<float> PreviewMessagePanelSecondsConfig { get; private set; }
        public static ConfigEntry<float> PreviewMessagePanelStartDelayConfig { get; private set; }

        // (v0.2.37) Guest-only physics relief during a co-op sleep warp. OPT-IN (default false), local and
        // per-player. See the Bind description and SleepSyncManager.ApplySleepCycleState for the rationale
        // and the risk (Time.fixedDeltaTime is global).
        public static ConfigEntry<bool> GuestSleepPhysicsReliefConfig { get; private set; }

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

        // (v0.3.0) GUEST: we entered a lobby whose mod set differs from ours, waived by our own
        // Coop.AllowModMismatch. The host gates on ITS copy of that setting, so this join can still be
        // refused - and when it is refused in silence, this is what lets the join watchdog say so.
        private static bool _joinedOverModMismatch;

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

            // (v0.2.38) Per-SENDER invite suppression. v0.2.36 de-duped invites by LOBBY id on the theory that
            // Steam replays ONE stale invite forever. That was WRONG: the reporting user's seen-invites.txt
            // accumulated SIX DISTINCT lobby ids from the same account, so a lobby-keyed de-dupe can never
            // suppress a sender who keeps creating fresh lobbies - by that fix's own logic a new lobby is a
            // genuinely new invite. Blocking on Steam is the other remedy, but it is awkward when the sender
            // has a PRIVATE profile: those do not appear in Steam search, so only the direct SteamID URL
            // reaches them. An ID-keyed ignore list works regardless of lobby churn or profile privacy.
            IgnoredInvitersConfig = Config.Bind(
                "Coop",
                "IgnoredInviters",
                "",
                "Comma-separated 64-bit Steam IDs whose co-op invites are ignored silently. A NEW invite logs " +
                "its sender's ID to BepInEx/LogOutput.log, so you can copy it straight from there; a pasted " +
                "steamcommunity.com/profiles/<id> URL works too. Also read from " +
                "~/.sailwind-coop/ignored-inviters.txt (one ID per line, '#' starts a comment), which survives " +
                "config resets. Both are read on the first invite after launch, so edits apply next launch.");
            Log.LogInfo($"IgnoredInviters: [{IgnoredInvitersConfig.Value}]");

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

            // (v0.3.0) Kill switch for the guest hull-physics derivation. This exists because the feature
            // it guards CANNOT BE TESTED WITHOUT A SECOND MACHINE - it only runs on a guest - so the first
            // people to exercise it are a crew mid-voyage. If it ever goes wrong for them, the honest
            // remedy has to be something they can do without waiting for a build. Read live, so a crew can
            // flip it with Configuration Manager and see the difference immediately.
            // Setting it false restores the pre-v0.3.0 behavior exactly: guests derive nothing and their
            // hulls keep whatever buoyancy they loaded with, including a stale or zeroed one.
            GuestHullPhysicsConfig = Config.Bind(
                "Coop",
                "GuestHullPhysics",
                true,
                "Let crew work out a boat's buoyancy and drag from the captain's water level. Default on. " +
                "The game derives those from flooding in one place, which co-op used to skip entirely on " +
                "crew machines, so a flooding boat floated at one height for the captain and another for " +
                "everyone else, and a hull that loaded sunk stayed weightless forever. Turn this off only " +
                "if boats start behaving worse than that for your crew; it restores the old behavior.");
            Log.LogInfo($"GuestHullPhysics: {GuestHullPhysicsConfig.Value}");
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

            // (v0.3.0) SQUAT vs SEIZA. The crouch used to drop the hips straight down while the feet stayed
            // planted directly beneath them, which is kneeling geometry, not squatting - the reported "looks
            // like I'm sitting on my own feet". A real squat sends the hips BACKWARD as they drop. That is
            // also the ONLY lever available: with the hip above the foot the knee lies on a horizontal
            // circle, so the IK pole sets the knee's compass direction but never its height, and with
            // roughly equal thigh/shin bones the knee can never rise above the hip at all. Moving the hip
            // back is what opens the thigh angle up; the shins and ankles then re-solve to follow.
            CrouchThighLiftDegConfig = Config.Bind("Crouch", "CrouchThighLiftDeg", 8f,
                new ConfigDescription("How far the thigh lifts toward the chest at full crouch, in degrees above the hip-to-ankle line. The hip setback needed to achieve it is solved from your rig's own measured bone lengths, so the same angle looks the same on any character - which is why this is an angle and not a distance. 0 keeps the old straight-down drop.",
                    new AcceptableValueRange<float>(-30f, 25f)));
            CrouchHipSetbackMaxMetersConfig = Config.Bind("Crouch", "CrouchHipSetbackMaxMeters", 0.35f,
                new ConfigDescription("Safety clamp (metres) on how far back the hips may travel for CrouchThighLiftDeg. SET THIS TO 0 to disable the squat setback entirely and get the previous straight-down crouch back, without needing a new build.",
                    new AcceptableValueRange<float>(0f, 0.6f)));

            // (v0.3.0) Avatar ground plant. The body used to be planted with a hardcoded 0.9m guess at the
            // distance from the player root down to the ground; the real distance is read live from the
            // vanilla CharacterController instead (PlayerSyncManager.ControllerFeetGap), which is the same
            // number the network send path has always used. This knob is only the residual nudge on top.
            AvatarSoleOffsetMetersConfig = Config.Bind("Crouch", "AvatarSoleOffsetMeters", 0f,
                new ConfigDescription("Fine adjustment (metres) to how high avatars stand relative to the surface under them. POSITIVE raises, NEGATIVE sinks. Leave at 0 unless bodies visibly hover above or sink into decks; the base value is now measured from the game rather than assumed. Takes effect on the next avatar build (leave and re-enter third person, or rejoin).",
                    new AcceptableValueRange<float>(-0.5f, 0.5f)));

            CoopMenuButtonScaleConfig = Config.Bind("Coop", "MenuButtonScale", 1f,
                new ConfigDescription("Size of the buttons on the co-op pause menu, relative to vanilla. Default 1.0 is vanilla-sized; the parchment is made taller to fit them rather than the buttons being shrunk to fit the parchment. Lower it if you would rather have a shorter scroll. Applies the next time the menu lays out (open the pause menu again).",
                    new AcceptableValueRange<float>(0.5f, 1f)));

            AppearanceConfig = Config.Bind("Appearance", "Character", "",
                "Your character's appearance, as \"slot=variant\" pairs (e.g. \"gender=0;hair=3;torso=7\"). Normally written by the in-game character screen rather than edited here. Leave EMPTY to get a look derived from your Steam ID - stable across sessions, and different from your crewmates' rather than everyone sharing one face. Unknown slot names are ignored and out-of-range variants fall back to that slot's default, so a hand-edited value can never produce an invisible or broken character.");

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

            GuestSleepPhysicsReliefConfig = Config.Bind("Coop", "GuestSleepPhysicsRelief", false,
                "EXPERIMENTAL, crewmate-side only. During an at-sea (unmoored) sleep the game warps to 16x, which runs physics at 72 steps per real second instead of 45 - the main cause of the 'slideshow' while sleeping. Turn this on to halve the crewmate's physics rate for the duration of the warp (the screen is black and the host is authoritative for the boat, so it should not be visible). Local-only and per-player: no effect on the host, on solo play, or on anyone else's game, and it does NOT change how much rest you get. Left off by default because Unity's physics step is global - it also coarsens deck cargo, ropes and the player body for those ~35 seconds. Try it if sleeping at sea is still a slideshow for you.");

            AllowVersionMismatchConfig = Config.Bind("Coop", "AllowVersionMismatch", false,
                "Let players on a DIFFERENT mod version join anyway (both sides get a warning instead of a refusal). The network format is not versioned - mixed builds can desync silently or corrupt a session, so leave this off unless you know the two builds are wire-compatible. Both the host and the mismatched guest must enable it. Gameplay-mod differences are gated separately by Coop.AllowModMismatch.");

            AllowModMismatchConfig = Config.Bind("Coop", "AllowModMismatch", false,
                "Let players whose GAMEPLAY MOD SET differs from the host's join anyway (warning instead of refusal). Covers Shipyard Expansion, Sail Collision Fix, NAND Tweaks simulation options, Deep Ports (including its terrain bundle), Towable Boats and HMS Leopard. Mixed mod sets desync physics, terrain and rigs - leave this off unless you know exactly what differs. Both the host and the mismatched guest must enable it.");

            PreviewMessagePanelConfig = Config.Bind("Debug", "PreviewMessagePanel", false,
                "DEBUG / UI CHECK ONLY. Shows a SAMPLE of the co-op message panel - the 'your mods differ from the host's' screen - shortly after launch, and binds a key to re-show it, so its readability can be checked without arranging a real mismatched join. The sample is built from YOUR actual installed mods with a few differences invented, so it reads at real length with real names; it is clearly labelled as a preview, no join is attempted and nothing is changed. Off by default and has no effect on real sessions either way.");

            PreviewMessagePanelKeyConfig = Config.Bind("Debug", "PreviewMessagePanelKey", "F9",
                "Key that re-shows the sample panel while Debug.PreviewMessagePanel is on. Each press cycles the three real message shapes: refused before joining, admitted-with-warning, and refused by the host. Any UnityEngine.KeyCode name (F9, F10, Backslash...). Ignored when the preview is off.");

            PreviewMessagePanelSecondsConfig = Config.Bind("Debug", "PreviewMessagePanelSeconds", 15f,
                new ConfigDescription("How long the sample panel stays up before hiding itself. Escape or the Close button dismisses it early. Real refusals never auto-hide - the player is stuck and has to act - so this applies to the preview only.",
                    new AcceptableValueRange<float>(1f, 600f)));

            PreviewMessagePanelStartDelayConfig = Config.Bind("Debug", "PreviewMessagePanelStartDelay", 8f,
                new ConfigDescription("Seconds after the mod loads before the sample appears once on its own; the main menu needs a moment to come up first. Set to 0 to disable the automatic show and use only the key, which is the reliable route if this delay lands mid-load.",
                    new AcceptableValueRange<float>(0f, 120f)));

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
            // (v0.3.0) Guard the TOUCH of LobbyManager, not just what it does. If
            // Facepunch.Steamworks.Win64.dll is missing, SteamLobbyManager cannot be laid out in memory
            // (it holds a Lobby? field), so this line throws a TypeLoadException before Initialize() runs
            // and its own try/catch never gets the chance to record why. Catching it here is what lets the
            // player be told which file to restore instead of watching every co-op button do nothing.
            try
            {
                _steamInitialized = LobbyManager.Initialize();
            }
            catch (System.Exception ex)
            {
                _steamInitialized = false;
                Networking.SteamInitDiagnostics.RecordFailure(ex);
                Log.LogError($"Steam layer could not load: {ex.GetType().Name}: {ex.Message}");
                if (ex.InnerException != null) Log.LogError($"  inner: {ex.InnerException.Message}");
            }

            if (_steamInitialized)
            {
                Log.LogInfo($"Steam user: {SteamClient.Name} ({SteamClient.SteamId})");
            }
            else
            {
                Log.LogError("Failed to initialize Steam. Coop features will be disabled.");
                Log.LogError(Networking.SteamInitDiagnostics.Describe());
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
                // (v0.3.0) Ask SteamInitDiagnostics, NOT LobbyManager. When the failure is a missing
                // Facepunch.Steamworks.Win64.dll, SteamLobbyManager cannot load at all - it has a Lobby?
                // field, so Mono must resolve that type just to lay the class out - and merely touching the
                // LobbyManager property here would throw, replacing the explanation with a second failure.
                // The message naming the missing file has to come from somewhere that does not need it.
                Notify(Networking.SteamInitDiagnostics.Describe(), 8f);
                return false;
            }
            return true;
        }

        // Tracks the last disconnect notification time so the clean (OnPlayerLeft) and P2P-drop
        // (OnDisconnected) paths don't both toast for the same leave.
        private static float _lastDisconnectNotifyTime = -10f;

        // (v0.3.0) How long the host keeps a refused guest's P2P session open after queueing the refusal,
        // so Steam can actually flush it. Three seconds is generous for a reliable packet of a few hundred
        // bytes even over the relay, and costs nothing: admission is already revoked, so the guest's own
        // traffic is being dropped for the whole window.
        private const float RefusalDeliveryGraceSeconds = 3f;

        // (v0.3.0) Backstop for the guest-quit panel: how long we wait for the player to close a refusal
        // before exiting anyway. Only reached if the panel never drew or cannot take a click.
        private const float GuestQuitReadCapSeconds = 300f;

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

        /// <summary>
        /// (v0.3.0) A notification with a quieter second line, for telling the player what to DO about it.
        ///
        /// This paints into VANILLA's notification scroll - the same one every game message uses - so the
        /// banner keeps its native size and look on purpose. Resizing it would mean resizing a shared mesh
        /// and shrinking every vanilla notification along with ours.
        ///
        /// The subtitle is styled with rich-text tags, which that TextMesh does not enable by default, so
        /// we turn it on immediately before writing. Harmless for vanilla messages (none contain tags), and
        /// if the field cannot be reached we fall back to a plain second line rather than printing raw
        /// markup at the player.
        /// </summary>
        public static void NotifyWithHint(string message, string hint, float duration = 6f)
        {
            if (NotificationUi.instance == null)
            {
                Log.LogInfo($"[Coop] {message} ({hint})");
                return;
            }

            bool rich = false;
            int hintSize = 0;
            try
            {
                var tm = HarmonyLib.Traverse.Create(NotificationUi.instance).Field("text").GetValue<TextMesh>();
                if (tm != null)
                {
                    tm.richText = true;
                    rich = true;
                    // Shrink the hint relative to whatever the notification is actually set to, rather than
                    // hard-coding a point size that would be wrong if the game ever retunes its own.
                    if (tm.fontSize > 0) hintSize = Mathf.Max(1, Mathf.RoundToInt(tm.fontSize * 0.8f));
                }
            }
            catch { /* fall through to the plain second line */ }

            // (v0.3.0) NO ITALIC TAG, and that is the whole reason this is worth a comment. The hint used
            // to be wrapped in <i>, which looked right in principle and wrong on screen: Sailwind's menu
            // font has no italic face, and Unity answers a style it cannot supply by falling back to Arial.
            // So the one line rendered in a completely different typeface from everything around it -
            // reported as "looks more arial-like". Size and colour carry the emphasis instead.
            //
            // Colour went #524439 -> #4F3A1F -> this. The first two read as washed-out grey placeholder
            // text on a light parchment; a hint should be quieter than the message, not faded.
            string open = "<color=#2E2114>";
            if (hintSize > 0) open = "<size=" + hintSize + ">" + open;
            string close = "</color>" + (hintSize > 0 ? "</size>" : "");

            string body = WrapForScroll(message) + "\n" + (rich ? open + hint + close : hint);
            NotificationUi.instance.ShowNotification(body, duration);
        }

        // The vanilla NotificationUi paints into a fixed-width parchment via a NON-wrapping TextMesh, so a long
        // co-op line (e.g. "Server opened - waiting for crew (close from the menu)") spills off both edges.
        // Resizing that shared mesh would warp every vanilla notification, so instead we soft-wrap our OWN text
        // on word boundaries at ~NotifyWrapWidth chars (TextMesh honours explicit '\n'). Short messages (the
        // common case, and all vanilla ones) are returned unchanged, so normal notifications look identical.
        // (v0.3.0) 26, down from 36. Measured off a screenshot of "diamondminer99 invited you to co-op."
        // (36 characters, so the old width let it through unwrapped): the text rendered about 975px wide
        // across a parchment about 700px wide, running off both edges. That is roughly 27px per character,
        // so the scroll holds about 26. Steam names are the reason this shows up now - the line is short
        // until someone with a long handle sends the invite.
        private const int NotifyWrapWidth = 26; // chars/line that fit the notification scroll (tune to taste)
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

                // (v0.3.0) Re-announce our own look to the crew whenever anyone joins. Everyone doing this
                // means a newcomer learns every existing player's appearance even without the host's roster
                // replay, and a guest is not dependent on having been connected at the moment someone else
                // last changed. It is a handful of bytes on a rare event, so the redundancy is free.
                Player.AppearanceSync.BroadcastLocal();

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
                // (v0.3.0) Mooring latches are per-peer too. BOTH leave paths need this: a crewmate who
                // quits to the menu comes through here, not through the P2P-drop handler, and either way a
                // rope or length adjuster they were still holding would stay latched to an avatar that no
                // longer exists for the rest of the session.
                Sync.MooringRopeAdjustSync.OnPeerLeft(friend.Id.Value);
                Sync.MooringRopeHoldSync.OnPeerLeft(friend.Id.Value);

                // The host-closed reason is latched ABOVE (before RemovePeer) so the accurate
                // message wins over the RemovePeer-driven "Lost connection" path. Only the HOST leaving ends
                // a guest's session; a fellow guest leaving must NOT quit us.
                VerboseLogger.LobbyEvent($"Player left: leaverIsHost={leaverIsHost}, guest role={_joinedAsGuest}");
            };

            LobbyManager.OnLobbyLeft += () =>
            {
                Sync.BoatUtility.ClearCaches(); // (v0.2.32, P2) fresh session = fresh boat map
                // (v0.3.0) Hand back any mod settings we adopted from the host to make the join work. This
                // is the other half of the reconcile contract: the player's own settings are borrowed for the
                // session only, never written to their config file, and always returned here. No-op when
                // nothing was adopted. Runs BEFORE the save below so a host's borrowed values can never be
                // observed by anything that writes state.
                Compat.CompatRegistry.RestoreLocalSettings();
                // (v0.3.0) The panel is DontDestroyOnLoad, so without this a co-op message (and its cursor
                // capture) would follow the player back into singleplayer.
                UI.CoopMessagePanel.Hide();
                // (v0.3.0) Appearances are per-session knowledge. Keeping them would let a stale entry
                // dress a future crewmate in whoever last occupied that id in an earlier session.
                Player.AppearanceRegistry.Clear();
                // Carried-rope state is per-session too; a stale entry would pin a rope to an avatar that
                // no longer exists.
                Sync.MooringRopeHoldSync.Clear();
                // Same for a rope someone was still hauling on when the session ended - Clear() runs the
                // release animation first, so the pull rope and spinning coil cannot follow the player into
                // singleplayer.
                Sync.MooringRopeAdjustSync.Clear();
                // (v0.3.0) Requests to come aboard, and the "not now" answers to them, belonged to the
                // voyage that just ended. Rich presence is re-published on the next tick from whatever is
                // true then, so friends stop being told we are sailing.
                Networking.CoopPresence.OnLobbyEnded();
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
                // (v0.3.0) A peer that drops while hauling a rope's length adjuster leaves the pull rope
                // stretched to wherever their avatar was, for the rest of the session - nothing else clears
                // that latch. Runs the release animation as though they had let go.
                Sync.MooringRopeAdjustSync.OnPeerLeft(peerId.Value);
                Sync.MooringRopeHoldSync.OnPeerLeft(peerId.Value);

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
                    _joinedOverModMismatch = false; // fresh verdict per join attempt
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
                            EndGuestSessionAndQuit(mismatchMsg, "Cannot join: mod versions differ",
                                new System.Collections.Generic.List<string>
                                {
                                    $"The host runs co-op v{hostVersion}.",
                                    $"You run co-op v{PluginVersion}.",
                                },
                                "Everyone must install the same version of the co-op mod. The game will now quit.");
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

                    // (v0.3.0) SETTINGS RECONCILE, before the refusal. The commonest way to fail this gate is
                    // not a missing mod - it is a crew running identical mods where one config line differs,
                    // which used to be a hard refusal plus an unreadable vector dump. Adopt the host's
                    // runtime-applicable settings for the session instead and let the join proceed. Only
                    // converges when EVERY differing segment was fixable, so a genuine incompatibility still
                    // refuses (see CompatRegistry.TryReconcileWith). Undone on session teardown.
                    if (hostMods != ourMods && Compat.CompatRegistry.TryReconcileWith(hostMods))
                    {
                        ourMods = Compat.CompatRegistry.ModSignature;
                        Notify("Matched the host's mod settings for this session (your own settings are restored when you leave).", 8f);
                    }

                    if (hostMods != ourMods)
                    {
                        string modsMsg = "Mod set mismatch - " +
                            Compat.CompatRegistry.DescribeMismatch(hostMods, ourMods) +
                            ". Everyone must run the same gameplay mods (and the same settings for the flagged ones).";
                        Log.LogError($"[MODS] {modsMsg}");

                        // (v0.3.0) Put the actionable version on a READABLE surface. This message is a list
                        // of mods with versions and settings, and the vanilla notification ticker clips it
                        // rather than wrapping - the reported "you can't even read it, it all runs off the
                        // screen". It is also the moment the player is stuck and must act, so it is exactly
                        // the wrong thing to render as ambient diegetic flavour.
                        //
                        // The manifest comes from LOBBY DATA here, not the handshake: this refusal happens
                        // before any P2P session exists, so the handshake copy does not exist yet.
                        // Built once, but SHOWN inside each branch below - the outcome differs and the panel
                        // must not assert a refusal that did not happen. Raising "Cannot join" before this
                        // branch meant an AllowModMismatch join proceeded normally with a box on screen
                        // telling the player they had been refused and should fix their mods and try again.
                        var panelLines = new System.Collections.Generic.List<string>(
                            Compat.CompatRegistry.DescribeMismatchLines(hostMods, ourMods));
                        panelLines.AddRange(Compat.ModManifest.DescribeDifferences(
                            LobbyManager.GetLobbyData("manifest") ?? "", Compat.ModManifest.Local, "the host"));

                        if (AllowModMismatchConfig != null && AllowModMismatchConfig.Value)
                        {
                            // (v0.3.0) Remember that we came in over a known mismatch, so if the join then
                            // dies in silence the watchdog can name the likeliest reason instead of sending
                            // the player off to check a Steam friendship that was never the problem.
                            _joinedOverModMismatch = true;
                            Notify(modsMsg + "\n(Coop.AllowModMismatch is on here - joining anyway; expect desyncs.)", 10f);
                            // (v0.3.0) Waits for the player to dismiss it rather than expiring on a timer:
                            // a mod mismatch you were admitted through is exactly the thing you want to have
                            // actually READ when the session desyncs an hour later. NOT sticky through
                            // teardown though - unlike a refusal, this is about a session the player is
                            // still in, so leaving the lobby should take it away rather than carry it into
                            // singleplayer.
                            // (v0.3.0) AUTO-HIDES, unlike a refusal. This panel takes the cursor, and a
                            // panel holding the cursor on a join that is still proceeding reads as "the
                            // join is waiting for me" - a player reported exactly that, sitting on this
                            // message believing it had blocked them. A refusal earns a click because
                            // nothing continues without it; this is a warning about a session already
                            // under way, so it says its piece and gets out of the way.
                            // (v0.3.0) The subtitle used to say "so you were admitted", which this end cannot
                            // promise. AllowModMismatch is read independently on both machines: ours got us
                            // past this gate, but the host's own copy decides the handshake, and if it is off
                            // the host still refuses. Say what our config actually did.
                            UI.CoopMessagePanel.Show("Joining anyway - your mods differ from the host's", panelLines,
                                "Coop.AllowModMismatch is on here, so this end is not blocking the join. The host must also have it on, or the host will still refuse. Expect desyncs.",
                                20f, stickyThroughTeardown: false);
                        }
                        else if (SaveSlots.currentSlot == CoopSave.PhantomSlot)
                        {
                            // (v0.3.0) Hand the bullets to the quit path rather than showing them and then
                            // quitting, which replaced them in the same frame with the joined one-string
                            // version. This is the SECOND site with that shape; the handshake refusal below
                            // is the other. The panel here is the one players actually hit, because this
                            // gate fires before any P2P session exists.
                            _joinedAsGuest = true;
                            EndGuestSessionAndQuit(modsMsg, "Cannot join: your mods differ from the host's", panelLines,
                                "Everyone must run the same gameplay mods, at the same versions. The game will now quit.");
                            return;
                        }
                        else
                        {
                            UI.CoopMessagePanel.Show("Cannot join: your mods differ from the host's", panelLines,
                                "Everyone must run the same gameplay mods, at the same versions. The full list is in the BepInEx log.");
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
                        // (v0.3.0) Additive trailing field, same tolerance contract as the mod token above:
                        // an older host stops reading after the token and never sees this. REPORT ONLY - it
                        // is never consulted by the gate below (see ModManifest's class doc).
                        w.Write(Compat.ModManifest.Local);
                    });

                    // (v0.3.0) Announce our look on the same proven connection, immediately after the
                    // handshake. OnPlayerJoined does NOT fire on a guest for the host - the host is not
                    // "joining" - so without this the host would never learn what a guest looks like, and
                    // its roster replay to later joiners would be missing them.
                    Player.AppearanceSync.BroadcastLocal();
                }

                if (joinedExistingPlayer)
                {
                    // (v0.3.0) This used to say "Aboard the host's ship!" and it was simply not true. This
                    // handler fires the instant Steam reports we entered the LOBBY - `joinedExistingPlayer`
                    // means nothing more than "the lobby has another member in it" (its original purpose was
                    // only to stop a solo host toasting themselves). At this point the guest is standing in
                    // their OWN world, no host state has arrived, and the actual embark is anywhere from a
                    // few seconds to a minute away (the host defers the snapshot up to 30s while asleep, and
                    // the scene-load wait can add 30s more). It also claimed "aboard a ship" when the host was
                    // on LAND, and when the host had refused admission and we were about to be quit.
                    //
                    // The real toast now fires from the join coroutine at the moment the embark actually
                    // succeeds (BoatStateApplicator, STEP 7). This one is honest about being progress.
                    Notify("Connected to the host - loading their world...", 5f);
                    // (No ocean reseed here: shipped Sailwind never instantiates the legacy FFT Ocean
                    // class. The live Crest wave state is synced by WeatherSyncManager, seeded by the
                    // host's join-time one-shot weather send.)
                }

                // (v0.2.34) SESSION-START sleep reset (join side): the sleep machine was only ever reset by
                // TEARDOWN paths, so a half-torn previous session (vanilla GameState.sleeping stuck true
                // after a failed wake) survived into the next lobby - the reported "host re-invites and is
                // permanently stuck sleeping, crew can never sleep with them again". AbortSleep is a no-op
                // when clean and (since its v0.2.34 guard) also repairs vanilla-only leftovers.
                Sync.SleepSyncManager.Instance?.AbortSleep();

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

                    // (v0.3.0) Raise the join screen. Started HERE rather than when the host's snapshot
                    // arrives, because the wait for that snapshot is itself part of the join and is the one
                    // stretch with no other feedback at all. The screen keeps itself off the title menu and
                    // vanilla's load screen on its own, so starting early costs nothing on the title path.
                    SailwindCoop.UI.JoinProgressScreen.Begin();
                }
            };

            LobbyManager.OnLobbyCreated += lobby =>
            {
                Sync.BoatUtility.ClearCaches(); // (v0.2.32, P2) fresh session = fresh boat map
                // (v0.2.34) SESSION-START sleep reset (create side) - see the OnLobbyJoined twin above.
                Sync.SleepSyncManager.Instance?.AbortSleep();
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
        /// <summary>
        /// (v0.3.0) HOST: drop a refused guest once its refusal packet has had time to reach it.
        ///
        /// Admission is already revoked by the caller, so throughout this wait the guest's packets are
        /// dropped and nothing of theirs reaches a sync manager. What the wait buys is delivery: Steam
        /// discards a peer's queued packets the moment its session closes, so tearing down in the same
        /// frame as the send loses the one packet that tells the guest why it was refused.
        ///
        /// REALTIME, deliberately. A host that opened its session from the pause menu can be sitting at
        /// timeScale 0, where a scaled wait never completes and the refused guest would never be dropped.
        /// </summary>
        private System.Collections.IEnumerator DropRefusedGuestAfterDelivery(SteamId guest)
        {
            yield return new UnityEngine.WaitForSecondsRealtime(RefusalDeliveryGraceSeconds);

            NetworkManager?.EndRefusalGrace(guest);
            NetworkManager?.RemovePeer(guest);
            Log.LogInfo($"[VERSION] Dropped refused guest {guest} after the refusal delivery window.");
        }

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
                // (v0.3.0) Informational plugin manifest; absent on pre-v0.3.0 guests, which reads as ""
                // and makes the report a no-op rather than a false "you are missing everything".
                string guestManifest = "";
                try { guestManifest = reader.ReadString(); } catch { /* pre-v0.3.0 guest */ }
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
                        // weAreTheHost: this message is shown to the HOST about a GUEST, so "you" is the
                        // local captain and the guest is the one who has to change something. Without the
                        // flag the wording tells the captain to edit a config that is already correct.
                        : "has a different mod set - " + Compat.CompatRegistry.DescribeMismatch(
                              Compat.CompatRegistry.ModSignature, guestMods, weAreTheHost: true);
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
                    w.Write(Compat.ModManifest.Local);           // (v0.3.0) report-only manifest, old guests ignore
                });

                // (v0.3.0) Report non-gated mod differences to the HOST. Only for a guest we are actually
                // admitting: a refused guest already got a specific refusal, and burying that under a
                // cosmetic-mod list would just muddy it. Never affects admission.
                if (allow)
                {
                    var diffs = Compat.ModManifest.DescribeDifferences(guestManifest, Compat.ModManifest.Local, guestName);
                    if (diffs.Count > 0)
                    {
                        // (v0.3.0) LOG ONLY, deliberately no panel. The manifest covers every loaded plugin
                        // indiscriminately, so on a real install (20+ mods) almost any join produces a diff -
                        // one different HUD or skybox is enough. Raising a screen-centre panel for that would
                        // put a box over the horizon on essentially every join, for information the host does
                        // not need to act on. The panel is reserved for refusals, where the player is stuck.
                        Compat.ModManifest.LogDifferences(diffs, guestName);
                        Notify($"{guestName} joined with {diffs.Count} mod difference(s) - see the log.", 6f);
                    }
                }

                if (!allow)
                {
                    // Same teeth as the admission gate: revoke admission NOW, so from this line on the
                    // mismatched guest's packets are dropped before they reach any sync manager.
                    LobbyManager.RevokeAdmission(sender);

                    // (v0.3.0) But do NOT tear the session down in this frame. SendReliable only queues;
                    // Steam flushes later, and every teardown path discards what is still queued for that
                    // peer. The v0.3.0 playtest is the whole argument: host logged "REFUSED", revoked, and
                    // closed within one frame, the guest never saw the ack, and after 45s of silence it
                    // reported a P2P timeout and told the player to check their Steam friendship - for what
                    // was actually a mod-set refusal. Hold the session open just long enough to deliver it.
                    NetworkManager.BeginRefusalGrace(sender);
                    StartCoroutine(DropRefusedGuestAfterDelivery(sender));
                }
            });

            NetworkManager.RegisterHandler(PacketType.HandshakeAck, (sender, reader) =>
            {
                var version = reader.ReadString();
                var accepted = reader.ReadBoolean();
                // (v0.2.31) Tolerant read: a pre-0.2.31 host's ack ends after the bool.
                string hostMods = "";
                try { hostMods = reader.ReadString(); } catch { /* pre-0.2.31 host */ }
                // (v0.3.0) Informational plugin manifest; absent on a pre-v0.3.0 host.
                string hostManifest = "";
                try { hostManifest = reader.ReadString(); } catch { /* pre-v0.3.0 host */ }
                Log.LogInfo($"[VERSION] Handshake response from {sender}: version {version}, mods [{hostMods}], accepted: {accepted}");

                // (v0.3.0) Report non-gated mod differences to the GUEST, when we are being admitted. On a
                // refusal the block below delivers the actual reason and this would only compete with it.
                if (!IsHost && accepted)
                {
                    var diffs = Compat.ModManifest.DescribeDifferences(hostManifest, Compat.ModManifest.Local, "the host");
                    if (diffs.Count > 0)
                    {
                        // Log only - same reasoning as the host side above.
                        Compat.ModManifest.LogDifferences(diffs, "the host");
                        Notify($"You have {diffs.Count} mod difference(s) from the host - see the log.", 6f);
                    }
                }

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

                    // (v0.3.0) Readable panel alongside the quit notice. Here we DO have the host's manifest
                    // (it rode the ack), so a refused guest finally learns about non-curated differences too -
                    // previously this was reported only to players who were being ADMITTED, i.e. exactly the
                    // people who did not need it.
                    var lines = new System.Collections.Generic.List<string>();
                    if (version != PluginVersion)
                        lines.Add($"Co-op mod version: the host runs v{version}, you run v{PluginVersion}");
                    else
                        lines.AddRange(Compat.CompatRegistry.DescribeMismatchLines(hostMods, Compat.CompatRegistry.ModSignature));
                    var refusedDiffs = Compat.ModManifest.DescribeDifferences(hostManifest, Compat.ModManifest.Local, "the host");
                    lines.AddRange(refusedDiffs);
                    // The footer below promises the full list is in the log, and on this branch it was not:
                    // LogDifferences ran only for guests who were being ADMITTED. So a refused guest's
                    // non-curated plugin differences were computed, written into a panel that the quit
                    // routine replaces in the same frame, and then existed nowhere at all. This is the one
                    // piece of information that was genuinely lost rather than reformatted.
                    if (refusedDiffs.Count > 0) Compat.ModManifest.LogDifferences(refusedDiffs, "the host");

                    // (v0.3.0) Hand the BULLETS to the quit routine rather than showing them here. This used
                    // to be its own Show immediately followed by EndGuestSessionAndQuit, and a coroutine
                    // body runs to its first yield inside StartCoroutine - so the quit routine's own Show
                    // replaced this one in the same frame, before a single OnGUI pass. The player never saw
                    // these lines; they saw the semicolon-joined one-string version, which is a wall of
                    // run-on text naming six mods and four settings in one paragraph.
                    EndGuestSessionAndQuit(reason, "The host refused your join", lines,
                        "Match the host's mods and versions, then try again. The game will now quit.");
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
                // (v0.3.0) BOUND IT. This gate has no timeout and no coroutine of its own - it is cleared
                // only when a later BoatWorldState arrives. The host side that must deliver that
                // (ResendWorldStateAfterRecovery) calls SendBoatWorldState with no try/catch, over a
                // collector that dereferences Camera.main unguarded, so a single throw there strands this
                // flag TRUE for the rest of the session. The codebase already documents what that costs:
                // "ApplyBoatTransform early-returns every frame -> the ashore guest's boat sync is dead for
                // the session (a permanent desync softlock)". Self-heal after a generous window, mirroring
                // the ClearRecoveryTextAfter coroutine this handler already starts.
                Instance?.StartCoroutine(ClearRecoveryGateAfter(45f));

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

            // (v0.3.0) Avatar appearance. Cosmetic and additive; see PacketType.PlayerAppearance for the
            // wire shape and the two independent trust guards.
            NetworkManager.RegisterHandler(PacketType.PlayerAppearance, (sender, reader) =>
            {
                Player.AppearanceSync.OnReceived(sender, reader);
            });

            // (v0.3.0) Who is carrying a mooring rope.
            NetworkManager.RegisterHandler(PacketType.MooringRopeHeld, (sender, reader) =>
            {
                Sync.MooringRopeHoldSync.OnReceived(sender, reader);
            });

            // (v0.3.0) Who is adjusting a moored rope's length (the R interaction).
            NetworkManager.RegisterHandler(PacketType.MooringRopeAdjusting, (sender, reader) =>
            {
                Sync.MooringRopeAdjustSync.OnReceived(sender, reader);
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

            // (v0.3.0) Park remotely-carried mooring ropes on their carriers. LateUpdate so the boat and
            // the avatar are both placed for this frame - the same ordering the held-item visuals need.
            Sync.MooringRopeHoldSync.LateUpdate();

            // (v0.3.0) Same, for a crewmate hauling a moored rope's length adjuster. Also drives the coil
            // spin and pull-material scroll, which vanilla gates on `held` - a field a receiver must leave
            // null, or this machine's scroll wheel would retrim a rope someone else is holding.
            Sync.MooringRopeAdjustSync.LateUpdate();
        }

        /// <summary>(v0.3.0) IMGUI surface for our own screens. Draws nothing while they are closed.</summary>
        private void OnGUI()
        {
            try { SailwindCoop.UI.CharacterScreen.Draw(); }
            catch (System.Exception e) { Log.LogWarning("[Character] Draw failed: " + e.Message); }
            try { SailwindCoop.UI.FriendsScreen.Draw(); }
            catch (System.Exception e) { Log.LogWarning("[Friends] Draw failed: " + e.Message); }
            try { SailwindCoop.Networking.HostLinkWatchdog.Draw(); }
            catch (System.Exception e) { Log.LogWarning("[HostLink] Draw failed: " + e.Message); }
            // Last, so it covers everything above it: while a join is in flight the player should be
            // looking at the join and nothing else.
            try { SailwindCoop.UI.JoinProgressScreen.Draw(); }
            catch (System.Exception e) { Log.LogWarning("[JoinScreen] Draw failed: " + e.Message); }
        }

        // --- DEBUG message-panel preview (v0.3.0) ------------------------------------------------------
        private bool _panelPreviewAutoShown;
        private float _panelPreviewDueAt = -1f;
        private KeyCode _panelPreviewKey = KeyCode.F9;
        private bool _panelPreviewKeyParsed;
        private bool _panelPreviewFailed;

        /// <summary>
        /// Drives Debug.PreviewMessagePanel. Wholly inert - not even a key read - unless that bool is on.
        ///
        /// The deadline is armed on the first Update rather than in Awake because Awake runs at chainloader
        /// time, well before the main menu exists; timing it from the first frame of the game loop is what
        /// makes "8 seconds" land somewhere near the menu instead of somewhere in the splash. Even so the key
        /// is the dependable route, which is why the delay can be set to 0 and the key left as the only path.
        /// </summary>
        private void TickMessagePanelPreview()
        {
            try
            {
                if (_panelPreviewFailed) return;
                if (PreviewMessagePanelConfig == null || !PreviewMessagePanelConfig.Value) return;

                if (!_panelPreviewKeyParsed)
                {
                    _panelPreviewKeyParsed = true;
                    var keyName = PreviewMessagePanelKeyConfig?.Value;
                    if (!string.IsNullOrEmpty(keyName))
                    {
                        try { _panelPreviewKey = (KeyCode)System.Enum.Parse(typeof(KeyCode), keyName, true); }
                        catch
                        {
                            Log.LogWarning($"[UI] Debug.PreviewMessagePanelKey '{keyName}' is not a KeyCode name; " +
                                "falling back to F9.");
                        }
                    }
                    Log.LogInfo($"[UI] Message-panel preview is ON (press {_panelPreviewKey} to re-show; " +
                        "Debug.PreviewMessagePanel=false turns this off).");
                }

                float hold = PreviewMessagePanelSecondsConfig != null ? PreviewMessagePanelSecondsConfig.Value : 15f;
                float startDelay = PreviewMessagePanelStartDelayConfig != null ? PreviewMessagePanelStartDelayConfig.Value : 8f;

                if (!_panelPreviewAutoShown && startDelay > 0f)
                {
                    if (_panelPreviewDueAt < 0f) _panelPreviewDueAt = Time.realtimeSinceStartup + startDelay;
                    if (Time.realtimeSinceStartup >= _panelPreviewDueAt)
                    {
                        _panelPreviewAutoShown = true;
                        UI.PanelPreview.ShowNext(hold);
                    }
                }

                if (Input.GetKeyDown(_panelPreviewKey))
                {
                    _panelPreviewAutoShown = true; // a manual show also satisfies the one-shot
                    UI.PanelPreview.ShowNext(hold);
                }
            }
            catch (System.Exception e)
            {
                // Stand down for the rest of the process rather than logging every frame. Deliberately a
                // local flag and NOT a write to the ConfigEntry: BepInEx saves on set, so that would edit
                // the player's config file behind their back over a transient failure.
                _panelPreviewFailed = true;
                Log.LogWarning("[UI] Message-panel preview tick failed, disabling it for this session: " + e.Message);
            }
        }

        private void Update()
        {
            // Process command system (works even without Steam)
            CommandProcessor?.Update();

            // (v0.3.0) DEBUG panel preview. ABOVE the Steam gate on purpose: checking that a refusal
            // message is readable must not require a working Steam init, and the whole block self-gates
            // on a config that is off by default.
            TickMessagePanelPreview();

            // (v0.3.0) Character screen. ABOVE the Steam gate deliberately: this screen closes itself the
            // moment the player leaves a cursor menu, and that watchdog must run even when Steam never
            // initialised (a state the mod explicitly supports - the pause parchment still replaces vanilla
            // pause solo). Below the gate, the watchdog and the pause-key handling were both dead code
            // exactly where a stranded panel would be hardest to escape.
            SailwindCoop.UI.CharacterScreen.Tick();
            // Same reasoning for the friends screen: its watchdog is what makes a stranded, cursor-holding
            // panel impossible, and that has to run whether or not Steam ever came up.
            SailwindCoop.UI.FriendsScreen.Tick();
            // Same again, and for this one the reason is sharper: its Tick owns the hard cap that guarantees
            // a join blackout can never outlive the join. That backstop is worthless if it can be switched
            // off by an unrelated failure, so it runs above the Steam gate like the other two watchdogs.
            SailwindCoop.UI.JoinProgressScreen.Tick();

            if (!_steamInitialized) return;

            // (v0.3.0) Publish/read Steam rich presence: what makes "friends playing now" and asking to
            // come aboard possible against a lobby that is deliberately invisible. Self-throttling - it
            // publishes only on change and sweeps only when a screen, a host, or an outstanding request
            // would actually consume the result.
            Networking.CoopPresence.Tick();

            // Process Steam callbacks
            Profiler?.StartMeasure();
            LobbyManager.RunCallbacks();
            Profiler?.EndMeasureSteamCallbacks();

            // Process incoming network packets
            Profiler?.StartMeasure();
            NetworkManager?.ResetPacketCounter();
            NetworkManager?.ProcessIncomingPackets();
            Profiler?.EndMeasurePacketProcessing();

            // (v0.3.1) Immediately after the drain, so anything that arrived this frame has already
            // cleared the counter and no live packet is ever mistaken for silence.
            try { SailwindCoop.Networking.HostLinkWatchdog.Tick(_joinedAsGuest, _endingGuestSession); }
            catch (System.Exception e) { Log.LogWarning("[HostLink] Tick failed: " + e.Message); }

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

        /// <summary>
        /// (v0.3.0) Watchdog for the recovery-scoped boat-sync gate. If the host's post-recovery
        /// BoatWorldState never arrives (its send path is unguarded and can throw), clear the gate anyway so
        /// the guest's boat sync resumes instead of being dead for the session.
        ///
        /// REALTIME, and generous: the host's own recovery is several seconds of scaled waits plus a boat
        /// pass, and a normal resync clears this flag long before the timeout. Only fires on the failure
        /// path. Fail-OPEN by design - a spurious clear costs one snap, a missed clear costs the session.
        /// </summary>
        private static System.Collections.IEnumerator ClearRecoveryGateAfter(float seconds)
        {
            yield return new WaitForSecondsRealtime(seconds);
            if (!BoatSyncManager.IsJoinInProgress) yield break;

            // OWNERSHIP CHECK - do not clear a gate someone is actively using. The same recovery that armed
            // this timer normally triggers a follow-up world-state APPLY, and that coroutine sets this very
            // flag for itself. Its own budget legitimately exceeds our timeout (a 2s settle plus a terrain
            // load explicitly allowed up to 30s, after a host-side recovery that already burned several
            // seconds), so clearing here would re-enable boat sync mid-teleport - the exact physics fight the
            // gate exists to prevent. Every wait inside that coroutine is realtime-bounded and its finally
            // clears the flag on all non-abort exits, so deferring to it cannot reintroduce a permanent latch.
            if (Sync.BoatStateApplicator.IsApplyInFlight)
            {
                Log.LogInfo("[RECOVERY] Gate watchdog stood down: a world-state apply owns the gate and will clear it.");
                yield break;
            }

            Log.LogWarning($"[RECOVERY] No post-recovery world state after {seconds:F0}s and no apply in flight; " +
                "clearing the boat-sync gate so sync resumes (the host's resync appears to have failed).");
            BoatSyncManager.IsJoinInProgress = false;
            GameState.recovering = false;
            BoatSyncManager.Instance?.SnapBoatToLiveTarget();
        }

        /// <summary>
        /// (v0.3.1) Public entry to the guest leave path for HostLinkWatchdog, which lives in Networking
        /// and so cannot reach the private original. Nothing else is exposed: every other caller of the
        /// leave path is already inside this class, and this keeps the choke point single.
        /// </summary>
        public static void EndGuestSessionFromWatchdog(string reason, string title,
                                                       System.Collections.Generic.List<string> lines,
                                                       string footer)
            => EndGuestSessionAndQuit(reason, title, lines, footer);

        /// <summary>
        /// End a guest session and quit, showing the player why.
        ///
        /// (v0.3.0) The optional title/lines/footer exist because callers that have a STRUCTURED reason
        /// used to show their own panel and then call this, which replaced it in the same frame: a
        /// coroutine body runs to its first yield inside StartCoroutine, and this routine's Show sits above
        /// its first yield. So the detailed version never survived to a single OnGUI pass and the player
        /// always got the flat joined string. Callers now hand their bullets down instead of racing us.
        /// </summary>
        private static void EndGuestSessionAndQuit(string reason, string title = null,
                                                   System.Collections.Generic.List<string> lines = null,
                                                   string footer = null)
        {
            if (!_joinedAsGuest || _endingGuestSession) return;
            _endingGuestSession = true;
            // The single choke point every guest-session failure goes through: mod refusal, host closed the
            // server, connection lost, watchdog. All of them raise a panel the player has to read, so the
            // join blackout has to be gone before any of them draw. Idempotent and safe when nothing is up.
            SailwindCoop.UI.JoinProgressScreen.Abort(reason);
            Log.LogInfo($"[Coop] Guest co-op ended: {reason} - warning then quitting");
            if (Instance != null) Instance.StartCoroutine(GuestQuitRoutine(reason, title, lines, footer));
            else Application.Quit();
        }

        private static System.Collections.IEnumerator GuestQuitRoutine(string reason, string title = null,
                                                                       System.Collections.Generic.List<string> lines = null,
                                                                       string footer = null)
        {
            // Make sure we're out of the (possibly hostless) lobby, then freeze the world while the warning shows.
            if (LobbyManager.IsInLobby) LobbyManager.LeaveLobby();
            Time.timeScale = 0f;

            // (v0.3.0) Show it in OUR panel, which wraps and stays on screen. The old "persistent backup"
            // wrote into Sleep.instance.recoveryText - a world-space mesh sized for two or three words of
            // sleep status - so a full refusal reason rendered in letters a foot tall running off both edges
            // of the screen. The player was told their game was closing by a wall of text they could not
            // read, which is the exact failure the message panel exists to end.
            bool panelUp = false;
            try
            {
                UI.CoopMessagePanel.Show(
                    title ?? "Leaving the crew",
                    (lines != null && lines.Count > 0) ? lines : new System.Collections.Generic.List<string> { reason },
                    footer ?? "The game will now quit.", 0f, stickyThroughTeardown: true);
                panelUp = UI.CoopMessagePanel.IsShowing;
            }
            catch (System.Exception e) { Log.LogWarning("[Coop] could not show the quit panel: " + e.Message); }
            Notify("Co-op session ended - the game will close.", 10f);

            // (v0.3.0) Wait for the player to close the panel rather than quitting on a fixed timer. The six
            // seconds this used to allow was not enough to read a refusal that lists several mods, so the
            // reported experience was a wall of text followed by an instant drop to desktop, indistinguishable
            // from a crash. The cap is a backstop for a panel that failed to appear or cannot be clicked; it
            // is long enough that nobody hits it by reading slowly.
            float deadline = Time.realtimeSinceStartup + (panelUp ? GuestQuitReadCapSeconds : 6f);
            while (Time.realtimeSinceStartup < deadline && UI.CoopMessagePanel.IsShowing)
                yield return null;

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

            // Drop the join screen before the explanation goes up, or the panel telling the player what went
            // wrong would be drawn underneath a blackout that is still claiming the join is progressing.
            SailwindCoop.UI.JoinProgressScreen.Abort("no world state from the host");

            // (v0.3.0) Lead with the reason we have evidence for. We already know our mod set differs from
            // the host's (we came in past our own gate on AllowModMismatch), and the host refuses that by
            // default, so a silent join is far likelier to be a mod refusal than a friendship problem. The
            // old text named only Steam friendship and AllowCrewInvites, which sent a player whose real
            // problem was mods off to check a setting that was already correct.
            string why = _joinedOverModMismatch
                ? "The host never admitted you, and your mods differ from the host's - that is the likely reason.\nEither match the host's mods, or ask the host to enable Coop.AllowModMismatch as well. It has to be on for the HOST, not just for you."
                : "The host did not admit you to the crew (or your join failed) - no world state ever arrived.\nAsk the host to add you as a Steam friend or enable Coop.AllowCrewInvites, then try again.";
            EndGuestSessionAndQuit(why);
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

            // Shutdown networking. Take our rich-presence keys down FIRST, while Steam is still up:
            // afterwards there is no client to tell, and friends would be left being told we are sailing.
            Networking.CoopPresence.Stop();
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
            // Stale reef on join: RopeState is edge-triggered like HelmState, so a guest joining after
            // sails were reefed/angled would never receive the current rope lengths and would board with
            // them at default trim. Re-send EVERY boat's rope set as reliable terminals (v0.3.1: was
            // current-boat-only, which is why a rejoin fixed a stale background boat for the rejoiner and
            // nobody else). TARGETED to the joiner, unlike the old broadcast: a crew-wide send would turn
            // one machine's stale copy of an unoccupied boat into everyone's truth on every join. The
            // existing crew converges through the host's periodic rope reconcile instead.
            RunJoinStep("ResendRope", () => ControlSyncManager?.ResendRopeForAllBoatsTo(friend.Id));
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
                // (v0.3.0) Same late-join problem for avatar appearance: the joiner missed everyone's
                // PlayerAppearance broadcast, so replay the roster or they see a crew of fallback faces.
                Player.AppearanceSync.SendRosterTo(friend.Id);
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

