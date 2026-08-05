using System;
using System.Collections.Generic;
using System.IO;
using Steamworks;
using Steamworks.Data;
using SailwindCoop.Debug;

namespace SailwindCoop.Networking
{
    public class SteamLobbyManager
    {
        public const uint SteamAppId = 1764530;
        // Crew cap (host + guests). Config-backed via Plugin.MaxPlayersConfig; defaults to 8 if the
        // config hasn't been bound yet (e.g. very early startup). This value flows into the Steam
        // lobby's max-members argument in CreateLobby (CreateLobbyAsync below).
        public static int MaxPlayers => Plugin.MaxPlayersConfig?.Value ?? 8;

        private static SteamLobbyManager _instance;
        public static SteamLobbyManager Instance => _instance ??= new SteamLobbyManager();

        public event Action<Lobby> OnLobbyCreated;
        public event Action<Lobby> OnLobbyJoined;
        public event Action OnLobbyLeft;
        public event Action<Friend> OnPlayerJoined;
        public event Action<Friend> OnPlayerLeft;

        private Lobby? _currentLobby;
        private bool _isInitialized;
        private float _lastInviteToastTime = -999f; // throttle the "invited you to co-op" toast
        // (v0.2.36) De-dupe co-op invites by lobby id, PERSISTED across launches. Steam re-delivers the
        // same pending invite on EVERY launch (it fires the instant the game starts, for a long-dead lobby),
        // which spammed the toast day after day for one stale invite. We show each unique invite ONCE, ever:
        // a lobby id already recorded here is a stale re-delivery and is suppressed silently. File lives next
        // to the mod's other per-user data (~/.sailwind-coop/seen-invites.txt).
        private readonly HashSet<ulong> _seenInviteLobbies = new HashSet<ulong>();
        private bool _seenInvitesLoaded;
        private string _seenInvitesPath;

        // (v0.2.38) Per-sender invite ignore list; union of the config entry and ignored-inviters.txt.
        // The snapshot lets a mid-session config edit take effect without a restart.
        private readonly HashSet<ulong> _ignoredInviters = new HashSet<ulong>();
        private bool _ignoredInvitersLoaded;
        private string _ignoredInvitersConfigSnapshot;
        // One-shot so a retry after an IO fault does not re-spam the same parse warnings every invite.
        private bool _ignoredInviterWarningsEmitted;
        // Ids the HOST invited via the in-game menu this lobby; the admission gate in
        // HandleLobbyMemberJoined only admits these (unless AllowCrewInvites). Cleared per lobby.
        private readonly HashSet<SteamId> _hostSentInvites = new HashSet<SteamId>();
        // (v0.2.25) Members the HOST's admission gate actually ADMITTED (OnPlayerJoined fired for them).
        // This is the authoritative host-side admission set: P2PNetworkManager consults it (IsAdmitted)
        // before accepting a P2P session or dispatching packets. The v0.2.23/24 playtest logs proved that
        // lobby-level refusal alone was NOT enough - the raw Steam P2P session still connected on both
        // sides and every peer-based sync manager ran, so a REFUSED guest played an entire session
        // half-initialized (no join snapshot, stale phantom-save needs). Host-side only; cleared per lobby.
        private readonly HashSet<SteamId> _admittedMembers = new HashSet<SteamId>();

        public bool IsInLobby => _currentLobby.HasValue;

        // PERF: IsHost/HostSteamId are read by nearly every per-frame patch, and each raw read
        // costs native Steamworks interop calls (Lobby.Owner + SteamClient.SteamId). Memoize both per
        // FRAME (Time.frameCount); the lobby enter/leave/create handlers invalidate the cache so role
        // transitions are exact within the frame they happen.
        private int _roleCacheFrame = -1;
        private bool _isHostCached;
        private SteamId _hostIdCached;

        private void RefreshRoleCacheIfStale()
        {
            int frame = UnityEngine.Time.frameCount;
            if (frame == _roleCacheFrame) return;
            _roleCacheFrame = frame;
            _hostIdCached = _currentLobby?.Owner.Id ?? default;
            _isHostCached = _currentLobby.HasValue && _hostIdCached == SteamClient.SteamId;
        }

        private void InvalidateRoleCache() => _roleCacheFrame = -1;

        public bool IsHost
        {
            get { RefreshRoleCacheIfStale(); return _isHostCached; }
        }

        public SteamId LobbyId => _currentLobby?.Id ?? default;
        // The lobby OWNER (host) SteamId. Used for STAR topology: a guest connects only to the host,
        // not to every member. Returns default when not in a lobby. (Same source IsHost compares against.)
        public SteamId HostSteamId
        {
            get { RefreshRoleCacheIfStale(); return _hostIdCached; }
        }

        /// True once the mod's OWN Steam client has initialized. When false, every lobby action is a
        /// no-op, but the pause menu still appears (it doesn't need Steam), so the buttons look dead.
        /// Surfaced via Plugin.SteamReady / the debug overlay / Plugin.EnsureCoopReady's retry.
        public bool IsInitialized => _isInitialized;

        /// Message from the last FAILED Steam init (null if the last attempt succeeded). Drives the
        /// in-game failure notice and the debug-overlay status line.
        ///
        /// (v0.2.39) The value now LIVES in SteamInitDiagnostics, which carries no Facepunch types and can
        /// therefore still be read when this class cannot load at all. Kept here as a forwarder for callers
        /// that already hold a live manager - see that class for why the distinction is load-bearing.
        public string LastInitError { get { return SteamInitDiagnostics.LastError; } }

        /// A player-facing explanation of why co-op can't start. The logic lives in SteamInitDiagnostics,
        /// which has no Facepunch-typed members and so survives the exact failure this message describes;
        /// this forwarder is for the call sites inside this class, which by definition already loaded.
        public string InitFailureHint()
        {
            return SteamInitDiagnostics.Describe();
        }

        public IEnumerable<Friend> LobbyMembers
        {
            get
            {
                if (!_currentLobby.HasValue)
                    yield break;

                foreach (var member in _currentLobby.Value.Members)
                {
                    yield return member;
                }
            }
        }

        private SteamLobbyManager()
        {
        }

        public bool Initialize()
        {
            if (_isInitialized)
            {
                Plugin.Log.LogWarning("Steam already initialized");
                return true;
            }

            try
            {
                // (v0.2.39) Make sure the NATIVE Steam library can be found before anything tries to call
                // into it - see PreloadNativeSteamApi for why a mod manager install could not.
                PreloadNativeSteamApi();

                // If Facepunch's Steam client is already up - because a PRIOR mod init threw AFTER Facepunch
                // set its internal `initialized` flag (e.g. an interface-version mismatch or partial native
                // load), or another component brought it up - adopt it instead of calling Init again.
                // Re-calling SteamClient.Init when it's already initialized throws "already initialized",
                // which would mask the real first-failure reason and block the EnsureCoopReady retry from
                // ever healing. (On a clean first launch IsValid is false, so we Init normally.)
                if (!SteamClient.IsValid)
                    SteamClient.Init(SteamAppId, false);

                _isInitialized = true;
                SteamInitDiagnostics.RecordSuccess();

                RegisterCallbacks();

                Plugin.Log.LogInfo($"Steam initialized successfully. User: {SteamClient.Name} ({SteamClient.SteamId})");
                return true;
            }
            catch (Exception ex)
            {
                SteamInitDiagnostics.RecordFailure(ex);
                Plugin.Log.LogError($"Failed to initialize Steam: {ex.GetType().Name}: {ex.Message}");
                if (ex.InnerException != null) Plugin.Log.LogError($"  inner: {ex.InnerException.Message}");
                return false;
            }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern System.IntPtr LoadLibraryW(string lpFileName);

        private static bool _nativePreloadAttempted;

        /// <summary>
        /// (v0.2.39) Load steam_api64.dll by full path, so co-op works from a mod manager install.
        ///
        /// THE ASYMMETRY THAT BROKE THUNDERSTORE. Facepunch.Steamworks is a MANAGED assembly, and BepInEx
        /// resolves those out of any subfolder of the plugin path - so it loads fine wherever the mod is
        /// installed. Every one of its 928 P/Invokes then targets a NATIVE library, steam_api64.dll, and
        /// Windows resolves that one itself: the executable's own folder, System32, the working directory,
        /// PATH. The plugins folder is on none of those lists, and neither is a mod manager profile.
        ///
        /// That is the whole bug behind "it only works if I launch through Steam". A Thunderstore package
        /// physically cannot place a file next to Sailwind.exe - the manager flattens a package into
        /// BepInEx/plugins/&lt;Author&gt;-&lt;Package&gt;/ and only the BepInEx pack itself is allowed at the
        /// profile root. So the native library never reached the one folder Windows would look in, and the
        /// first Steam call died with a DllNotFoundException that the mod reported as a generic Steam
        /// failure. Whoever "fixed it by launching through Steam" was really running a separate game-root
        /// install that happened to have the file.
        ///
        /// Loading it here by absolute path fixes that: Windows caches a loaded module under its base name,
        /// so once steam_api64 is in the process, every later P/Invoke binds to it without searching. This
        /// deliberately does NOT touch the process-wide search path (SetDllDirectory takes only one
        /// directory and would displace whatever else set it; SetDefaultDllDirectories changes resolution
        /// for every other mod in the process). One library, by name, loaded once.
        ///
        /// A game-root copy WINS. If the file is already next to the exe, this does nothing at all, so a
        /// normal install keeps resolving exactly as it always has and there is no chance of loading a
        /// different build of the library than the one the player installed.
        /// </summary>
        private static void PreloadNativeSteamApi()
        {
            if (_nativePreloadAttempted) return;
            _nativePreloadAttempted = true;

            const string NativeName = "steam_api64.dll";
            try
            {
                // The ordinary install puts it beside the executable, where Windows finds it unaided.
                string exeDir = System.AppDomain.CurrentDomain.BaseDirectory;
                if (!string.IsNullOrEmpty(exeDir) && File.Exists(Path.Combine(exeDir, NativeName)))
                    return;

                foreach (var dir in NativeSearchDirs())
                {
                    if (string.IsNullOrEmpty(dir)) continue;
                    string candidate = Path.Combine(dir, NativeName);
                    if (!File.Exists(candidate)) continue;

                    var handle = LoadLibraryW(candidate);
                    if (handle != System.IntPtr.Zero)
                    {
                        Plugin.Log.LogInfo($"Loaded the native Steam library from {candidate}");
                        return;
                    }

                    int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                    Plugin.Log.LogWarning($"Found {candidate} but Windows would not load it (error {err}).");
                }

                // Not fatal here - Steam init will fail next and report it properly, with the hint that now
                // names the right file. Logged so the log shows we looked.
                Plugin.Log.LogWarning($"{NativeName} is not next to Sailwind.exe and was not found beside the " +
                    "mod either. Co-op cannot start without it.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Could not pre-load {NativeName}: {e.Message}");
            }
        }

        /// <summary>Places the native library may sit when the game root is not an option: beside our own
        /// assembly (the mod-manager case), and the plugin root above it.</summary>
        private static IEnumerable<string> NativeSearchDirs()
        {
            string here = null;
            try
            {
                var loc = typeof(SteamLobbyManager).Assembly.Location;
                if (!string.IsNullOrEmpty(loc)) here = Path.GetDirectoryName(loc);
            }
            catch { }

            if (here != null)
            {
                yield return here;
                string parent = null;
                try { parent = Path.GetDirectoryName(here); } catch { }
                if (parent != null) yield return parent;
            }

            string pluginPath = null;
            try { pluginPath = BepInEx.Paths.PluginPath; } catch { }
            if (pluginPath != null) yield return pluginPath;
        }

        public void Shutdown()
        {
            if (!_isInitialized)
                return;

            LeaveLobby();
            UnregisterCallbacks();
            SteamClient.Shutdown();
            _isInitialized = false;

            Plugin.Log.LogInfo("Steam shutdown");
        }

        public void RunCallbacks()
        {
            if (_isInitialized)
            {
                SteamClient.RunCallbacks();
            }
        }

        private void RegisterCallbacks()
        {
            SteamMatchmaking.OnLobbyCreated += HandleLobbyCreated;
            SteamMatchmaking.OnLobbyEntered += HandleLobbyEntered;
            SteamMatchmaking.OnLobbyMemberJoined += HandleLobbyMemberJoined;
            SteamMatchmaking.OnLobbyMemberLeave += HandleLobbyMemberLeave;
            SteamMatchmaking.OnLobbyInvite += HandleLobbyInvite;
            SteamMatchmaking.OnLobbyGameCreated += HandleLobbyGameCreated;
            SteamFriends.OnGameLobbyJoinRequested += HandleGameLobbyJoinRequested;

            Plugin.Log.LogInfo("Steam callbacks registered");
        }

        private void UnregisterCallbacks()
        {
            SteamMatchmaking.OnLobbyCreated -= HandleLobbyCreated;
            SteamMatchmaking.OnLobbyEntered -= HandleLobbyEntered;
            SteamMatchmaking.OnLobbyMemberJoined -= HandleLobbyMemberJoined;
            SteamMatchmaking.OnLobbyMemberLeave -= HandleLobbyMemberLeave;
            SteamMatchmaking.OnLobbyInvite -= HandleLobbyInvite;
            SteamMatchmaking.OnLobbyGameCreated -= HandleLobbyGameCreated;
            SteamFriends.OnGameLobbyJoinRequested -= HandleGameLobbyJoinRequested;

            Plugin.Log.LogInfo("Steam callbacks unregistered");
        }

        public async void CreateLobby()
        {
            if (!_isInitialized)
            {
                Plugin.Log.LogError("Cannot create lobby: Steam not initialized");
                Plugin.Notify(InitFailureHint(), 8f);
                return;
            }

            if (IsInLobby)
            {
                Plugin.Log.LogWarning("Already in a lobby. Leave current lobby first.");
                return;
            }

            Plugin.Log.LogInfo("Creating friends-only lobby...");

            try
            {
                var lobbyResult = await SteamMatchmaking.CreateLobbyAsync(MaxPlayers);

                if (!lobbyResult.HasValue)
                {
                    Plugin.Log.LogError("Failed to create lobby: No result returned");
                    Plugin.Notify("Co-op: Steam didn't return a lobby (no response). Check your Steam connection and try Host Co-op again.", 6f);
                    return;
                }

                var lobby = lobbyResult.Value;
                // PRIVATE, not FriendsOnly (2026-07-02 access-control report): a Steam friends-only
                // lobby is visible and DIRECTLY joinable by friends of ANY member - a guest's friend,
                // a total stranger to the host, clicked "Join Game" on the guest and boarded the crew.
                // Private = invisible + joinable only through an invite; who gets admitted past an
                // invite is then enforced host-side in HandleLobbyMemberJoined.
                lobby.SetPrivate();
                lobby.SetData("name", $"{SteamClient.Name}'s Sailwind Voyage");
                lobby.SetData("version", Plugin.PluginVersion);
                // (v0.2.32) Composed mod-set token (SE + SCF + NAND Tweaks sim vector + Deep Ports
                // bundle hash + Towable Boats + HMS Leopard). Guests pre-check this BEFORE opening
                // P2P, exactly like the version stamp above. Opaque - compare for equality only.
                lobby.SetData("mods", SailwindCoop.Compat.CompatRegistry.ModSignature);
                // (v0.2.39) REPORT-ONLY plugin manifest, published here as well as over P2P so a guest
                // refused at the LOBBY PRE-CHECK can still be told what differs. That refusal happens before
                // any P2P session exists, so the handshake copy never reaches the one player who most needs
                // it - they were the only person getting no information at all.
                //
                // CLAMPED, and that is not defensive padding: Lobby.SetData THROWS above 8192 chars, and this
                // whole block sits inside a try/catch whose handler only logs. An oversized manifest would
                // therefore abort lobby creation BEFORE SetJoinable(true) below and leave a silently broken
                // host. Never consulted by the gate - see ModManifest's class doc.
                lobby.SetData("manifest", SailwindCoop.Compat.ModManifest.ForLobbyData());
                lobby.SetJoinable(true);

                _hostSentInvites.Clear();
                _admittedMembers.Clear(); // (v0.2.25) fresh lobby = fresh admission set
                _currentLobby = lobby;
                InvalidateRoleCache();

                Plugin.Log.LogInfo($"Lobby created: {lobby.Id}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Exception creating lobby: {ex.Message}");
            }
        }

        public async void JoinLobby(SteamId lobbyId)
        {
            if (!_isInitialized)
            {
                Plugin.Log.LogError("Cannot join lobby: Steam not initialized");
                Plugin.Notify(InitFailureHint(), 8f);
                return;
            }

            if (IsInLobby)
            {
                Plugin.Log.LogWarning("Already in a lobby. Leave current lobby first.");
                return;
            }

            Plugin.Log.LogInfo($"Joining lobby {lobbyId}...");

            try
            {
                // (v0.2.39) Use Lobby.Join(), NOT SteamMatchmaking.JoinLobbyAsync.
                //
                // JoinLobbyAsync DISCARDS Steam's EChatRoomEnterResponse and hands back a non-null Lobby
                // even when the join was REFUSED - a closed lobby, a full one, a lobby that never existed.
                // The mod then latched _currentLobby and believed it was in a session that does not exist.
                // Downstream that is not a quiet no-op: the guest path reads lobby data, gets "" for the
                // host's mod token while our own is always non-empty, concludes the mod sets differ, and
                // shows a FABRICATED "your mods differ from the host's" refusal - which on the title-join
                // path ends in Application.Quit(). So clicking a stale invite could close the player's
                // game and blame their mods for it. Steam re-delivers pending invites on every launch, so
                // stale is the COMMON case, not an edge one.
                //
                // Lobby.Join() returns the RoomEnter enum, which is the difference between "we are in" and
                // "Steam said no".
                var target = new Steamworks.Data.Lobby(lobbyId);
                var result = await target.Join();
                if (result != Steamworks.RoomEnter.Success)
                {
                    Plugin.Log.LogError($"Failed to join lobby {lobbyId}: {result}");
                    Plugin.Notify(DescribeJoinFailure(result), 7f);
                    return;
                }

                var lobby = (Steamworks.Data.Lobby?)target;
                _currentLobby = lobby.Value;
                InvalidateRoleCache();
                Plugin.Log.LogInfo($"Joined lobby: {lobby.Value.Id}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Exception joining lobby: {ex.Message}");
            }
        }

        /// <summary>
        /// Say WHY a join failed, in the player's terms. The old message blamed mismatched mod builds for
        /// every failure, which was usually wrong and sent people off copying config files at each other -
        /// a stale invite to a lobby the host closed hours ago is by far the most common cause.
        /// </summary>
        private static string DescribeJoinFailure(Steamworks.RoomEnter result)
        {
            switch (result)
            {
                case Steamworks.RoomEnter.DoesntExist:
                    return "Co-op: that session no longer exists - the host has closed it. Ask them to invite you again.";
                case Steamworks.RoomEnter.Full:
                    return "Co-op: that crew is full.";
                case Steamworks.RoomEnter.NotAllowed:
                case Steamworks.RoomEnter.Banned:
                    return "Co-op: the host's session is invite-only and this invite is no longer valid. Ask them to invite you again.";
                case Steamworks.RoomEnter.Error:
                    return "Co-op: Steam refused the join. Check that Steam is online, then try again.";
                default:
                    return $"Co-op: couldn't join that session ({result}). Ask the host to invite you again.";
            }
        }

        public void LeaveLobby()
        {
            if (!_currentLobby.HasValue)
            {
                Plugin.Log.LogWarning("Not in a lobby");
                return;
            }

            var lobbyId = _currentLobby.Value.Id;
            _currentLobby.Value.Leave();
            _currentLobby = null;
            _hostSentInvites.Clear();
            _admittedMembers.Clear(); // (v0.2.25) admission is per-lobby; never carry it across sessions
            InvalidateRoleCache();

            Plugin.Log.LogInfo($"Left lobby: {lobbyId}");
            OnLobbyLeft?.Invoke();
        }

        public bool InviteFriend(SteamId friendId)
        {
            if (!_currentLobby.HasValue)
            {
                Plugin.Log.LogError("Cannot invite friend: Not in a lobby");
                return false;
            }

            var result = _currentLobby.Value.InviteFriend(friendId);

            if (result)
            {
                // Record HOST-sent invites: the admission gate in HandleLobbyMemberJoined only admits
                // ids on this list (unless AllowCrewInvites). Guests inviting via the Steam overlay
                // bypass this method entirely, which is exactly why the gate exists.
                _hostSentInvites.Add(friendId);
                Plugin.Log.LogInfo($"Invited friend: {friendId}");
            }
            else
            {
                Plugin.Log.LogError($"Failed to invite friend: {friendId}");
            }

            return result;
        }

        /// (v0.2.25) HOST-side transport admission check. True only for peers the admission gate in
        /// HandleLobbyMemberJoined actually let in (plus ourselves). P2PNetworkManager gates P2P session
        /// accepts AND per-packet dispatch on this, so a lobby member the host REFUSED can no longer keep
        /// a live P2P session and feed the host's sync managers (the v0.2.23/24 half-initialized-session
        /// defect). Meaningless on a guest (the set is only populated by the host's gate) - callers must
        /// only consult it when Plugin.IsHost.
        public bool IsAdmitted(SteamId id)
        {
            return id == SteamClient.SteamId || _admittedMembers.Contains(id);
        }

        /// (v0.2.27) HOST-side: revoke a previously-granted admission (version-mismatch refusal).
        /// The transport gate (IsAdmitted) then drops the peer's session and packets exactly like a
        /// never-admitted stranger; a re-join runs the admission gate (and version handshake) afresh.
        public void RevokeAdmission(SteamId id)
        {
            if (_admittedMembers.Remove(id))
                Plugin.Log.LogInfo($"[LOBBY] Admission revoked for {id}");
        }

        public int GetMemberCount()
        {
            return _currentLobby?.MemberCount ?? 0;
        }

        public string GetLobbyData(string key)
        {
            return _currentLobby?.GetData(key);
        }

        public void SetLobbyData(string key, string value)
        {
            if (!IsHost)
            {
                Plugin.Log.LogWarning("Only host can set lobby data");
                return;
            }

            _currentLobby?.SetData(key, value);
        }

        private void HandleLobbyCreated(Result result, Lobby lobby)
        {
            if (result != Result.OK)
            {
                Plugin.Log.LogError($"Lobby creation failed with result: {result}");
                _currentLobby = null;
                InvalidateRoleCache();
                return;
            }

            VerboseLogger.LobbyEvent($"Lobby created, id={lobby.Id}, owner={lobby.Owner.Name}");
            OnLobbyCreated?.Invoke(lobby);
        }

        private void HandleLobbyEntered(Lobby lobby)
        {
            _currentLobby = lobby;
            InvalidateRoleCache(); // role may flip this frame (e.g. we just became a guest)
            VerboseLogger.LobbyEvent($"Entered lobby, id={lobby.Id}, host={lobby.Owner.Name}");
            OnLobbyJoined?.Invoke(lobby);
        }

        private void HandleLobbyMemberJoined(Lobby lobby, Friend friend)
        {
            VerboseLogger.LobbyEvent($"Player joined: {friend.Name} ({friend.Id})");

            // HOST ADMISSION GATE (2026-07-02, refined 2026-07-04 for GH #2). Admit anyone the host
            // legitimately let in; refuse only true strangers. A joiner is ADMITTED when ANY holds:
            //   - they are on the HOST's own Steam friends list (friend.IsFriend). An overlay "Invite to
            //     Game" - what players actually use, incl. the pause-menu Invite button (which calls
            //     SteamFriends.OpenGameInviteOverlay) - can only reach a friend, so this is the NORMAL admit
            //     path. Critically, the old gate only admitted _hostSentInvites, but InviteFriend() is never
            //     called anywhere, so that set is ALWAYS EMPTY: with the default AllowCrewInvites=false the
            //     old gate refused EVERY guest (incl. the host's own invited friend) = GH #2 "always spawns
            //     at own start". The 2026-07-02 concern was a STRANGER to the host (a guest's friend) boarding
            //     uninvited; such a person is not the host's friend, so !friend.IsFriend still bounces them.
            //   - the host explicitly invited them via InviteFriend (_hostSentInvites), or
            //   - the host set AllowCrewInvites (anyone a crew member invites may join).
            // Steam gives the owner no kick, so ADMISSION (not removal) is the control: an unadmitted member
            // never gets OnPlayerJoined, so the host never peers with them or sends join state. (v0.2.25)
            // The host ALSO refuses their raw P2P session + drops their packets (P2PNetworkManager gates on
            // IsAdmitted) - previously the transport still connected and sync managers ran half-initialized.
            // On the refused GUEST's side, their join-state watchdog (Plugin.GuestJoinWatchdog) notices no
            // join snapshot ever arrived, warns them the host didn't admit them, and quits them cleanly.
            // (An older comment here claimed a warn-and-quit timeout already existed; it did not - an
            // unadmitted guest silently played on. The watchdog is what makes that claim true.) Host-only gate.
            if (Plugin.IsHost
                && Plugin.AllowCrewInvitesConfig?.Value != true
                && friend.Id != SteamClient.SteamId
                && !friend.IsFriend
                && !_hostSentInvites.Contains(friend.Id))
            {
                string name = string.IsNullOrEmpty(friend.Name) ? friend.Id.ToString() : friend.Name;
                Plugin.Log.LogError($"[LOBBY] REFUSED admission for {name} ({friend.Id}): not on your Steam friends list and not invited by you. Add them as a Steam friend, or enable AllowCrewInvites in the config, to let them in.");
                NotifyRefusedLoud(name);
                return;
            }

            // (v0.2.25) Record the admission so the transport layer can enforce it: P2PNetworkManager
            // refuses P2P sessions from (and drops packets of) lobby members NOT in this set. Only
            // meaningful on the host (guests never consult it - they only ever peer with the host).
            if (Plugin.IsHost && friend.Id != SteamClient.SteamId)
                _admittedMembers.Add(friend.Id);

            OnPlayerJoined?.Invoke(friend);
        }

        // GH #2: a single 10s toast was easy to miss (the refused host in the report never noticed), so a
        // genuinely-refused stranger slipped past. Repeat a long toast a few times (~continuous 20s) and log
        // at Error so a real crasher is obvious. Runs on the Unity main thread from the Steam callback pump,
        // so StartCoroutine is safe; falls back to a single toast if the plugin MonoBehaviour isn't up yet.
        private void NotifyRefusedLoud(string name)
        {
            string msg = $"{name} tried to join but isn't your Steam friend and wasn't invited by you - NOT admitted. (Config: Coop.AllowCrewInvites)";
            if (Plugin.Instance != null)
                Plugin.Instance.StartCoroutine(RepeatRefusalToast(msg));
            else
                Plugin.Notify(msg, 12f);
        }

        private System.Collections.IEnumerator RepeatRefusalToast(string msg)
        {
            for (int i = 0; i < 3; i++)
            {
                Plugin.Notify(msg, 12f);
                yield return new UnityEngine.WaitForSecondsRealtime(4f);
            }
        }

        private void HandleLobbyMemberLeave(Lobby lobby, Friend friend)
        {
            VerboseLogger.LobbyEvent($"Player left: {friend.Name} ({friend.Id})");
            // (v0.2.25) Revoke transport admission on leave: if the same id later re-joins it must pass
            // the admission gate again, and until then the host won't accept its P2P session/packets.
            _admittedMembers.Remove(friend.Id);
            OnPlayerLeft?.Invoke(friend);
        }

        private void HandleLobbyInvite(Friend friend, Lobby lobby)
        {
            VerboseLogger.LobbyEvent($"Invite received from {friend.Name} ({friend.Id}), lobby={lobby.Id}");

            // Do NOT auto-join. Let Steam show its own clickable invite; ACCEPTING it fires
            // OnGameLobbyJoinRequested -> RouteJoin, the explicit opt-in. (Also joinable via the pause menu.)
            if (IsInLobby)
            {
                VerboseLogger.LobbyEvent($"Already in lobby, ignoring invite");
                return;
            }

            // (v0.2.38) Per-SENDER ignore list, checked BEFORE the lobby de-dupe below so an ignored account
            // never even records a lobby id (otherwise the file would grow one line per unwanted invite).
            // This exists because the v0.2.36 lobby-id de-dupe cannot stop a sender who keeps creating NEW
            // lobbies - see the config comment in Plugin.cs.
            if (IsInviterIgnored(friend.Id))
            {
                // Mirror to the MAIN log, not just VerboseLogger (which is F8/DebugMode-gated and writes to a
                // separate file). Without this an ignored invite leaves no trace anywhere, so a mistyped id
                // that silences the wrong account would be undiagnosable from the log the user actually has.
                Plugin.Log.LogInfo($"Co-op invite from {friend.Name} ({friend.Id}) suppressed (Coop.IgnoredInviters).");
                return;
            }

            // (v0.2.39) An invite that ANSWERS OUR OWN REQUEST is never a stale re-delivery, by definition -
            // we asked minutes ago and this is the yes. It must therefore skip the de-dupe below, which is
            // otherwise permanent: ask, get declined, ask the same captain again later, and the second
            // acceptance would be swallowed as "already seen" with no way to ever undo that. This is also
            // the only invite we can describe accurately, so it gets its own wording.
            bool answersOurRequest = CoopPresence.AnswersOurRecentAsk(lobby.Id.Value);

            // (v0.2.36) Only surface a LIVE invite, once. Steam replays the same pending invite on EVERY
            // launch (fires the instant the game starts, for a stale/dead lobby), which nagged for days about
            // one old invite. De-dupe by lobby id, persisted across launches: a lobby we've already recorded
            // is a stale re-delivery -> suppress silently. Only a genuinely NEW invite (new lobby) reaches
            // the toast + the traceable main-log line.
            EnsureSeenInvitesLoaded();
            bool alreadySeen = !_seenInviteLobbies.Add(lobby.Id.Value);
            if (alreadySeen && !answersOurRequest)
            {
                VerboseLogger.LobbyEvent($"Invite to lobby {lobby.Id} already seen; suppressing stale re-delivery");
                return;
            }
            if (!alreadySeen) PersistSeenInvite(lobby.Id.Value);

            // Mirror to the main BepInEx log with the sender's SteamID (once per unique invite), so an
            // unwanted invite can be traced to an exact account and blocked - persona names are changeable
            // and can collide, so the name alone can't positively identify the account.
            Plugin.Log.LogInfo($"Co-op invite received from {friend.Name} ({friend.Id}), lobby={lobby.Id}");

            // Belt-and-suspenders throttle (the de-dupe already caps one toast per lobby). An answer to our
            // own request is exempt: it is the one invite the player is actively waiting for, and silently
            // eating it because an unrelated invite arrived 14 seconds ago would be indefensible.
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (!answersOurRequest && now - _lastInviteToastTime < 15f) return;
            _lastInviteToastTime = now;

            // Friend.Name reads "[unknown]" until Steam loads that user's persona; fall back to a generic name.
            string name = (string.IsNullOrEmpty(friend.Name) || friend.Name == "[unknown]") ? "Someone" : friend.Name;
            // (v0.2.39) Say where to act. The old toast announced the invite and left the player to accept
            // it through Steam's own UI - which shows nothing at all when the sender is not a Steam friend
            // or the overlay is off, so the message was frequently a dead end.
            if (answersOurRequest)
                Plugin.NotifyWithHint($"{name} says come aboard.", "Open the menu to join", 8f);
            else
                Plugin.NotifyWithHint($"{name} invited you to co-op.", "Open the menu to join", 6f);

            // (v0.2.39) KEEP the invite instead of discarding it, so the pause menu can offer it. A single
            // slot rather than a list: the toast is throttled to one per 15s anyway, and a queue of stale
            // invitations is a worse thing to present than the newest one. The most recent invite wins.
            _pendingInvite = new PendingInvite
            {
                LobbyId = lobby.Id,
                SenderId = friend.Id,
                SenderName = name,
                ReceivedRealtime = now,
            };
        }

        /// <summary>
        /// (v0.2.38) Parse one ignore-list entry. Tolerant on purpose, because the person typing it has just
        /// been pestered by a stranger and the only handle they may have is what Steam gave them:
        ///  - text after '#' is a comment, so an opaque 17-digit number can be labelled with a name;
        ///  - a pasted profile URL works, not just a bare id. A PRIVATE Steam profile does not appear in
        ///    Steam search, so ".../profiles/&lt;id&gt;" is frequently the ONLY form the user can obtain.
        /// Parsing is NumberStyles.None + InvariantCulture: no signs, no whitespace, no digit grouping, and
        /// no dependence on the machine's locale.
        /// </summary>
        private static bool TryParseInviterId(string raw, out ulong id)
        {
            id = 0;
            if (string.IsNullOrEmpty(raw)) return false;

            var s = raw.Split('#')[0].Trim().TrimEnd('/');
            if (s.Length == 0) return false;

            int slash = s.LastIndexOf('/');
            if (slash >= 0) s = s.Substring(slash + 1).Trim();

            return ulong.TryParse(s, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out id);
        }

        /// <summary>
        /// (v0.2.38) True when invites from this Steam ID should be dropped silently. Sources are UNIONED so
        /// neither can silently disable the other: the BepInEx config entry (Coop.IgnoredInviters, easy to
        /// edit, lost if the config is regenerated) and ~/.sailwind-coop/ignored-inviters.txt (survives that,
        /// and sits beside the other per-user mod data).
        ///
        /// Read ONCE, on the first invite of the session. The `configured != snapshot` arm cannot actually
        /// fire under stock BepInEx 5.4.23: it has no config-file watcher, the mod never calls
        /// ConfigFile.Reload(), so .Value is frozen for the process. Edits to EITHER source take effect on
        /// the next launch, which suits the real workflow - Steam re-delivers pending invites at launch, so
        /// the loop is edit, restart, gone. The arm is kept because a ConfigurationManager-style plugin can
        /// write .Value live.
        ///
        /// Unparseable entries are skipped WITH A WARNING rather than voiding the whole list: one typo must
        /// never silently un-ignore someone, because that failure is invisible to the user.
        /// </summary>
        private bool IsInviterIgnored(SteamId sender)
        {
            string configured = Plugin.IgnoredInvitersConfig?.Value ?? "";
            if (!_ignoredInvitersLoaded || configured != _ignoredInvitersConfigSnapshot)
            {
                // Build into a LOCAL set and swap in only on success. Assigning as we go would mean a
                // mid-read IO fault left a half-built list latched as authoritative for the whole session.
                var loaded = new HashSet<ulong>();
                bool fileOk = true;

                foreach (var raw in configured.Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (TryParseInviterId(raw, out var id)) loaded.Add(id);
                    else if (!_ignoredInviterWarningsEmitted)
                        Plugin.Log.LogWarning($"[LOBBY] ignoring unparseable IgnoredInviters entry '{raw.Trim()}'");
                }

                try
                {
                    string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sailwind-coop");
                    string path = Path.Combine(dir, "ignored-inviters.txt");
                    if (File.Exists(path))
                    {
                        foreach (var line in File.ReadAllLines(path))
                        {
                            var trimmed = line.Trim();
                            if (trimmed.Length == 0 || trimmed[0] == '#') continue;
                            if (TryParseInviterId(trimmed, out var id)) loaded.Add(id);
                            else if (!_ignoredInviterWarningsEmitted)
                                Plugin.Log.LogWarning($"[LOBBY] ignoring unparseable ignored-inviters.txt line '{trimmed}'");
                        }
                    }
                }
                catch (Exception e)
                {
                    // Config entries still apply; only the file's contribution is missing. Leaving
                    // _ignoredInvitersLoaded false lets the NEXT invite retry instead of degrading for the
                    // rest of the session behind a warning the user will not connect to the symptom.
                    fileOk = false;
                    Plugin.Log.LogWarning($"[LOBBY] could not load ignored-inviters list, using config entries only: {e.Message}");
                }

                _ignoredInviterWarningsEmitted = true;
                _ignoredInviters.Clear();
                foreach (var id in loaded) _ignoredInviters.Add(id);

                if (fileOk)
                {
                    _ignoredInvitersLoaded = true;
                    _ignoredInvitersConfigSnapshot = configured;
                    Plugin.Log.LogInfo($"[LOBBY] ignoring co-op invites from {_ignoredInviters.Count} Steam ID(s)");
                }
            }

            return _ignoredInviters.Contains(sender.Value);
        }

        /// <summary>
        /// (v0.2.36) Lazily load the persisted set of already-seen invite lobby ids from
        /// ~/.sailwind-coop/seen-invites.txt (one decimal lobby id per line). Non-fatal on any error.
        /// </summary>
        private void EnsureSeenInvitesLoaded()
        {
            if (_seenInvitesLoaded) return;
            _seenInvitesLoaded = true;
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sailwind-coop");
                _seenInvitesPath = Path.Combine(dir, "seen-invites.txt");
                if (File.Exists(_seenInvitesPath))
                {
                    foreach (var line in File.ReadAllLines(_seenInvitesPath))
                        if (ulong.TryParse(line.Trim(), out var id)) _seenInviteLobbies.Add(id);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[LOBBY] could not load seen-invites list: {e.Message}");
            }
        }

        /// <summary>Append a newly-seen invite lobby id to the persistence file (one line per unique lobby;
        /// grows only on a genuinely new invite, so it stays tiny). Non-fatal on any error.</summary>
        private void PersistSeenInvite(ulong lobbyId)
        {
            try
            {
                if (string.IsNullOrEmpty(_seenInvitesPath)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(_seenInvitesPath));
                File.AppendAllText(_seenInvitesPath, lobbyId.ToString() + Environment.NewLine);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[LOBBY] could not persist seen-invite: {e.Message}");
            }
        }

        private void HandleLobbyGameCreated(Lobby lobby, uint ip, ushort port, SteamId gameServerId)
        {
            VerboseLogger.LobbyEvent($"Game server created, lobby={lobby.Id}, {ip}:{port}");
        }

        /// <summary>
        /// (v0.2.39) The most recent invite we could still act on, or null.
        ///
        /// Invites used to be announced and then thrown away: HandleLobbyInvite logged one, toasted it, and
        /// dropped the lobby handle, leaving Steam's own invite UI as the only way to accept. That UI shows
        /// NOTHING when the sender is not a Steam friend, or when the overlay is off - so a player could be
        /// told they had been invited and have no way whatsoever to say yes.
        /// </summary>
        public sealed class PendingInvite
        {
            public SteamId LobbyId;
            public SteamId SenderId;
            public string SenderName;
            public float ReceivedRealtime;
        }

        private PendingInvite _pendingInvite;

        /// <summary>The invite offered by the pause menu, or null. Cleared once we are in a lobby.</summary>
        public PendingInvite CurrentInvite
        {
            get { return IsInLobby ? null : _pendingInvite; }
        }

        /// <summary>Take up the pending invite. Same path Steam's own "Join Game" drives, so joining from
        /// the title menu still loads a save first (TitleJoinManager) rather than joining world-less.</summary>
        public void AcceptPendingInvite()
        {
            var inv = CurrentInvite;
            if (inv == null) return;
            _pendingInvite = null;
            Plugin.Log.LogInfo($"Accepting co-op invite from {inv.SenderName} ({inv.SenderId}), lobby={inv.LobbyId}");
            RouteJoin(inv.LobbyId, inv.SenderId);
        }

        /// <summary>Dismiss the pending invite without joining. Does NOT add the sender to the ignore list -
        /// declining once is not the same as never wanting to hear from someone again, and that list is a
        /// blunt, persistent instrument best left to a deliberate config edit.</summary>
        public void DeclinePendingInvite()
        {
            if (_pendingInvite == null) return;
            Plugin.Log.LogInfo($"Declined co-op invite from {_pendingInvite.SenderName} ({_pendingInvite.SenderId}).");
            _pendingInvite = null;
        }

        private void HandleGameLobbyJoinRequested(Lobby lobby, SteamId friendId)
        {
            VerboseLogger.LobbyEvent($"Join requested, lobby={lobby.Id}");

            if (!IsInLobby)
            {
                VerboseLogger.LobbyEvent($"Joining via friends list");
                // (v0.2.25) friendId = whose game/invite the guest clicked through (the host in
                // practice); keys the title-join phantom save per host (Lobby.Owner is unreadable
                // before the lobby is actually entered).
                RouteJoin(lobby.Id, friendId);
            }
            else
            {
                VerboseLogger.LobbyEvent($"Already in lobby, ignoring join request");
            }
        }

        // Accepting a co-op invite while still at the title menu: load a save FIRST, then join, so the guest
        // is fully in-world when the host sends boat state (reuses the proven in-game join path). If already
        // in-game, join immediately as before.
        private void RouteJoin(SteamId lobbyId, SteamId hostId)
        {
            if (!GameState.playing)
                TitleJoinManager.Begin(lobbyId, hostId);
            else
                JoinLobby(lobbyId);
        }
    }
}
