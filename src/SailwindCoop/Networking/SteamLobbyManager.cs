using System;
using System.Collections.Generic;
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
        public string LastInitError { get; private set; }

        /// A player-facing explanation of why co-op can't start, tailored to the failure (a missing
        /// Facepunch.Steamworks library vs a Steam/runtime error). Shown instead of a silent dead button.
        public string InitFailureHint()
        {
            string reason = string.IsNullOrEmpty(LastInitError) ? "Steam not initialized" : LastInitError;
            bool missingLib = reason.IndexOf("Facepunch", StringComparison.OrdinalIgnoreCase) >= 0
                           || reason.IndexOf("could not load", StringComparison.OrdinalIgnoreCase) >= 0
                           || reason.IndexOf("file or assembly", StringComparison.OrdinalIgnoreCase) >= 0
                           || reason.IndexOf("unable to load", StringComparison.OrdinalIgnoreCase) >= 0   // native steam_api64.dll (DllNotFoundException)
                           || reason.IndexOf("steam_api", StringComparison.OrdinalIgnoreCase) >= 0;
            string hint = missingLib
                ? "Re-extract the FULL mod zip - Facepunch.Steamworks.Win64.dll must sit next to SailwindCoop.dll."
                : "Check Steam is running and you launched Sailwind through Steam, then re-extract the full mod zip and relaunch.";
            return $"Co-op can't start - Steam init failed ({reason}). {hint}";
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
                // If Facepunch's Steam client is already up - because a PRIOR mod init threw AFTER Facepunch
                // set its internal `initialized` flag (e.g. an interface-version mismatch or partial native
                // load), or another component brought it up - adopt it instead of calling Init again.
                // Re-calling SteamClient.Init when it's already initialized throws "already initialized",
                // which would mask the real first-failure reason and block the EnsureCoopReady retry from
                // ever healing. (On a clean first launch IsValid is false, so we Init normally.)
                if (!SteamClient.IsValid)
                    SteamClient.Init(SteamAppId, false);

                _isInitialized = true;
                LastInitError = null;

                RegisterCallbacks();

                Plugin.Log.LogInfo($"Steam initialized successfully. User: {SteamClient.Name} ({SteamClient.SteamId})");
                return true;
            }
            catch (Exception ex)
            {
                LastInitError = ex.Message;
                Plugin.Log.LogError($"Failed to initialize Steam: {ex.GetType().Name}: {ex.Message}");
                if (ex.InnerException != null) Plugin.Log.LogError($"  inner: {ex.InnerException.Message}");
                return false;
            }
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
                lobby.SetFriendsOnly();
                lobby.SetData("name", $"{SteamClient.Name}'s Sailwind Voyage");
                lobby.SetData("version", Plugin.PluginVersion);
                lobby.SetJoinable(true);

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
                var lobby = await SteamMatchmaking.JoinLobbyAsync(lobbyId);

                if (!lobby.HasValue)
                {
                    Plugin.Log.LogError($"Failed to join lobby {lobbyId}");
                    Plugin.Notify("Co-op: couldn't join the host's lobby (no response). Make sure you're both on the same mod build, then try again.", 6f);
                    return;
                }

                _currentLobby = lobby.Value;
                InvalidateRoleCache();
                Plugin.Log.LogInfo($"Joined lobby: {lobby.Value.Id}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Exception joining lobby: {ex.Message}");
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
                Plugin.Log.LogInfo($"Invited friend: {friendId}");
            }
            else
            {
                Plugin.Log.LogError($"Failed to invite friend: {friendId}");
            }

            return result;
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
            OnPlayerJoined?.Invoke(friend);
        }

        private void HandleLobbyMemberLeave(Lobby lobby, Friend friend)
        {
            VerboseLogger.LobbyEvent($"Player left: {friend.Name} ({friend.Id})");
            OnPlayerLeft?.Invoke(friend);
        }

        private void HandleLobbyInvite(Friend friend, Lobby lobby)
        {
            VerboseLogger.LobbyEvent($"Invite received from {friend.Name}, lobby={lobby.Id}");

            // Do NOT auto-join. Auto-joining yanked the friend out of their own game with no consent (and
            // risked their save). Let Steam show its own clickable invite; ACCEPTING it fires
            // OnGameLobbyJoinRequested -> RouteJoin, which is the explicit opt-in. They can also use the
            // in-game pause menu "Join Friend".
            if (IsInLobby)
            {
                VerboseLogger.LobbyEvent($"Already in lobby, ignoring invite");
                return;
            }

            // Steam re-delivers the same invite repeatedly - throttle the toast so it can't spam.
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now - _lastInviteToastTime < 15f) return;
            _lastInviteToastTime = now;

            // Friend.Name reads "[unknown]" until Steam loads that user's persona; fall back to a generic name.
            string name = (string.IsNullOrEmpty(friend.Name) || friend.Name == "[unknown]") ? "A friend" : friend.Name;
            Plugin.Notify($"{name} invited you to co-op - accept it in Steam to join", 6f);
        }

        private void HandleLobbyGameCreated(Lobby lobby, uint ip, ushort port, SteamId gameServerId)
        {
            VerboseLogger.LobbyEvent($"Game server created, lobby={lobby.Id}, {ip}:{port}");
        }

        private void HandleGameLobbyJoinRequested(Lobby lobby, SteamId friendId)
        {
            VerboseLogger.LobbyEvent($"Join requested, lobby={lobby.Id}");

            if (!IsInLobby)
            {
                VerboseLogger.LobbyEvent($"Joining via friends list");
                RouteJoin(lobby.Id);
            }
            else
            {
                VerboseLogger.LobbyEvent($"Already in lobby, ignoring join request");
            }
        }

        // Accepting a co-op invite while still at the title menu: load a save FIRST, then join, so the guest
        // is fully in-world when the host sends boat state (reuses the proven in-game join path). If already
        // in-game, join immediately as before.
        private void RouteJoin(SteamId lobbyId)
        {
            if (!GameState.playing)
                TitleJoinManager.Begin(lobbyId);
            else
                JoinLobby(lobbyId);
        }
    }
}
