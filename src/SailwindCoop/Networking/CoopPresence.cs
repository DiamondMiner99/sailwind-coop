using System;
using System.Collections.Generic;
using Steamworks;

namespace SailwindCoop.Networking
{
    /// <summary>
    /// (v0.2.39) Steam rich presence: how a co-op crew finds each other when the lobby itself is invisible.
    ///
    /// THE PROBLEM THIS SOLVES. The lobby is <c>SetPrivate()</c> - deliberately, after a 2026-07-02 report of
    /// a stranger boarding through a friends-only lobby (a friend of a GUEST, unknown to the host, clicked
    /// "Join Game"). Private means Steam will not list it and will not let anyone in without an invite, so
    /// there is no lobby to browse and no "server list" to build. That is the right security posture and it
    /// is not up for renegotiation. But it left co-op with exactly one direction of travel: the host had to
    /// think of you first. If you knew your friend was sailing and wanted to join them, the game had no
    /// answer at all - you had to leave it and ask on Discord.
    ///
    /// WHAT RICH PRESENCE BUYS. Rich presence is a small key/value bag attached to your Steam account which
    /// Steam replicates AUTOMATICALLY to your friends who are playing the SAME GAME. It is orthogonal to
    /// lobby visibility: publishing it neither exposes the lobby nor makes it joinable. So a host can
    /// advertise "I am sailing, there is room" to their friends without opening the door, and - this is the
    /// part that closes the loop - a friend can publish "I would like to come aboard", which the host's
    /// game reads and offers as a prompt. Accepting sends a REAL Steam invite through the existing
    /// <see cref="SteamLobbyManager.InviteFriend"/>, so admission still runs the host's normal gate. The
    /// host remains the only party who can let anyone in; all this changes is that they can now be ASKED.
    ///
    /// WHAT IS DELIBERATELY NOT PUBLISHED: the reserved <c>"connect"</c> key. Setting it makes Steam show a
    /// "Join Game" button on our friends' list and hand them a connect string. Against a private lobby that
    /// button can only ever fail, so it would be an advertisement for something that does not work - the
    /// precise failure the accept-invite work was fixing. The lobby id we do publish is not a credential:
    /// Steam refuses a private lobby to anyone without an invite, id or no id, and only friends can read it.
    ///
    /// COST. Every read here is native interop, so a naive per-frame sweep of a 500-friend list would be a
    /// real cost for a feature nobody is looking at. Scans are therefore driven by Steam's
    /// <c>OnFriendRichPresenceUpdate</c> push (which only fires for friends in this same game) with a slow
    /// heartbeat as a backstop, and are skipped entirely when nothing would consume them.
    /// </summary>
    public static class CoopPresence
    {
        // --- our published keys -------------------------------------------------------------------------
        // Steam allows 20 keys, 64-byte keys, 256-byte values. We use seven short ones.
        private const string KeyStatus = "status";     // the line Steam itself shows on the friends list
        private const string KeyCoop = "coop";       // marker + our version; its presence means "has the mod"
        private const string KeyLobby = "coop_lobby";  // decimal lobby id we are sailing in (host OR crew)
        private const string KeyCrew = "coop_crew";   // "3/8"
        private const string KeyRole = "coop_role";   // "host" | "crew"
        private const string KeyCaptain = "coop_capt"; // captain's persona name, for display
        private const string KeyAsk = "coop_ask";    // decimal lobby id we are asking to board

        /// <summary>How long an unanswered "let me aboard" stays published before it expires itself.
        /// Long enough for a captain to notice a prompt and finish what they were doing; short enough that a
        /// request you have forgotten about cannot pull you into a session an hour later.</summary>
        private const float AskLifetimeSeconds = 180f;

        /// <summary>Backstop sweep interval. The real trigger is Steam's push callback; this only covers the
        /// case where a friend's presence was already set before we started listening.</summary>
        private const float HeartbeatSeconds = 5f;

        /// <summary>A host stops seeing a request this long after the asker's presence stops saying it -
        /// they cancelled, quit, or joined elsewhere. Not zero, because presence replication is not instant
        /// and a request that flickers out of a list mid-click is worse than one that lingers a moment.</summary>
        private const float RequestGraceSeconds = 20f;

        private static bool _started;
        private static float _nextSweep;
        private static bool _dirty = true;

        // What we last pushed, so we only pay for a SetRichPresence when something actually changed.
        private static string _publishedSignature;
        private static string _statusBeforeUs;
        private static bool _statusSnapshotTaken;

        // Our outstanding request to board someone else's ship.
        private static ulong _askLobby;
        private static ulong _askHostId;
        private static string _askHostName;
        private static float _askExpiresAt;

        // --- what we can see -----------------------------------------------------------------------------

        /// <summary>A friend who is in Sailwind right now, as far as Steam will tell us.</summary>
        public sealed class FriendPresence
        {
            public SteamId Id;
            public string Name;
            public bool HasCoop;        // running this mod
            public ulong LobbyId;       // non-zero when they are in a co-op session
            public bool IsCaptain;
            public string CaptainName;  // who is skippering their session (their own name when captain)
            public string Crew;         // "3/8", or null
            public bool IsAskingUs;     // they have asked to join OUR lobby
        }

        /// <summary>Someone who wants aboard our ship. Host-side only.</summary>
        public sealed class JoinRequest
        {
            public SteamId Id;
            public string Name;
            public bool IsSteamFriend;  // false = a friend of one of our crew, a stranger to us
            public float FirstSeen;
            public float LastSeen;
        }

        private static readonly List<FriendPresence> _friends = new List<FriendPresence>();
        private static readonly Dictionary<ulong, JoinRequest> _requests = new Dictionary<ulong, JoinRequest>();
        // Dismissed and accepted askers, each with the time it happened. BOTH need to lapse rather than
        // persist for the session: a permanent set is the same failure the invite path was just fixed for,
        // where a single de-dupe swallowed a later, deliberate second attempt forever.
        private static readonly Dictionary<ulong, float> _dismissedRequests = new Dictionary<ulong, float>();
        private static readonly Dictionary<ulong, float> _acceptedRequests = new Dictionary<ulong, float>();
        private static readonly HashSet<ulong> _toastedRequests = new HashSet<ulong>();

        /// <summary>How long a "not now" or an accepted invite suppresses the same asker. Long enough that
        /// the answer sticks while their presence still advertises the old request, short enough that
        /// someone deliberately asking again later is heard.</summary>
        private const float RequestAnswerMemorySeconds = AskLifetimeSeconds + RequestGraceSeconds;

        /// <summary>Friends currently in Sailwind, most interesting first (sailing, then modded, then the
        /// rest). Rebuilt by the sweep, never by the caller - the UI reads this every IMGUI frame and must
        /// not trigger interop.</summary>
        public static IList<FriendPresence> Friends { get { return _friends; } }

        // Kept sorted and handed out by reference. Both callers read this many times a second - the pause
        // menu every frame for its label, the screen twice per IMGUI pass - so building and sorting a fresh
        // list per read would be garbage generated continuously for a list that changes every few seconds.
        private static readonly List<JoinRequest> _requestsOrdered = new List<JoinRequest>();

        /// <summary>Outstanding requests to board our ship, oldest first. Empty unless we are the host.
        /// Do not mutate - this is the live list, not a copy.</summary>
        public static IList<JoinRequest> Requests { get { return _requestsOrdered; } }

        /// <summary>Number of outstanding requests. Free to read per frame.</summary>
        public static int RequestCount { get { return _requestsOrdered.Count; } }

        private static void RebuildRequestOrder()
        {
            _requestsOrdered.Clear();
            foreach (var r in _requests.Values) _requestsOrdered.Add(r);
            _requestsOrdered.Sort((a, b) => a.FirstSeen.CompareTo(b.FirstSeen));
        }

        /// <summary>The lobby we have asked to board, or 0. Read by the invite handler so that an invite
        /// arriving in answer to our own request is never mistaken for an unsolicited one.</summary>
        public static ulong OutstandingAskLobby { get { return _askLobby; } }

        // The last lobby we asked to board, kept a while AFTER the ask itself expires. The host's own grace
        // for an unanswered request outlives our ask, so a captain can accept in a window where we have
        // already forgotten asking - and the invite would then be treated as unsolicited and swallowed by
        // the permanent seen-invite de-dupe.
        private static ulong _recentAskLobby;
        private static float _recentAskUntil;

        /// <summary>True if an invite to this lobby is plausibly the answer to a request we made.</summary>
        public static bool AnswersOurRecentAsk(ulong lobbyId)
        {
            if (lobbyId == 0UL) return false;
            if (_askLobby == lobbyId) return true;
            return _recentAskLobby == lobbyId && UnityEngine.Time.realtimeSinceStartup < _recentAskUntil;
        }

        public static string OutstandingAskHostName { get { return _askHostName; } }

        // --- lifecycle -----------------------------------------------------------------------------------

        public static void Start()
        {
            if (_started) return;
            _started = true;
            try { SteamFriends.OnFriendRichPresenceUpdate += OnFriendPresenceChanged; }
            catch (Exception e) { Plugin.Log.LogWarning("[Presence] could not subscribe to presence updates: " + e.Message); }
        }

        public static void Stop()
        {
            if (!_started) return;
            _started = false;
            try { SteamFriends.OnFriendRichPresenceUpdate -= OnFriendPresenceChanged; } catch { }
            ClearPublished();
        }

        private static void OnFriendPresenceChanged(Friend f) { _dirty = true; }

        /// <summary>Ask for a sweep on the next tick. Called when something starts caring about the result -
        /// opening the friends screen, mainly - so the list is current rather than up to a heartbeat old.</summary>
        public static void RequestSweep() { _dirty = true; _nextSweep = 0f; }

        /// <summary>Drive from Plugin.Update, after the Steam gate. Cheap: publishes only on change, and
        /// sweeps only when something has changed or the backstop is due.</summary>
        public static void Tick()
        {
            try
            {
                Start();
                Publish();
                ExpireAsk();

                float now = UnityEngine.Time.realtimeSinceStartup;
                bool due = now >= _nextSweep;
                if (!_dirty && !due) return;

                // Nothing downstream would look at the result: no screen open, not hosting (so no requests to
                // notice), and no request of our own to follow up. Skip the interop entirely.
                bool wanted = UI.FriendsScreen.IsOpen || Plugin.IsHost || _askLobby != 0UL;
                _nextSweep = now + HeartbeatSeconds;
                _dirty = false;
                if (!wanted) return;

                Sweep(now);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[Presence] tick failed: " + e.Message);
            }
        }

        // --- publishing ----------------------------------------------------------------------------------

        /// <summary>How often we are willing to look at our own state to see whether presence needs
        /// updating. Building the signature costs interop (crew count, the captain's persona name), so doing
        /// it per frame would spend real time on a value that changes when someone joins or leaves - which
        /// is to say, almost never. Anything that needs an immediate push sets _nextPublish to 0.</summary>
        private const float PublishIntervalSeconds = 1f;
        private static float _nextPublish;

        private static void Publish()
        {
            float nowRt = UnityEngine.Time.realtimeSinceStartup;
            if (nowRt < _nextPublish) return;
            _nextPublish = nowRt + PublishIntervalSeconds;

            var lm = Plugin.LobbyManager;
            bool inLobby = lm != null && lm.IsInLobby;
            bool isHost = inLobby && lm.IsHost;

            ulong lobbyId = inLobby ? lm.LobbyId.Value : 0UL;
            int members = inLobby ? lm.GetMemberCount() : 0;
            int max = SteamLobbyManager.MaxPlayers;
            string captain = "";
            if (inLobby)
            {
                try { captain = isHost ? SteamClient.Name : new Friend(lm.HostSteamId).Name; }
                catch { captain = ""; }
            }

            string crew = inLobby ? members + "/" + max : "";
            string role = inLobby ? (isHost ? "host" : "crew") : "";
            // The "status" key is the ONE thing here Steam shows publicly, under the player's name in their
            // friends' lists. So it is set only while actually in a co-op session and taken straight back
            // down afterwards. Someone who installed this mod and then played alone all evening did not ask
            // to have their Steam status rewritten, and the feature does not need it: the custom keys below
            // are not displayed by Steam and carry everything the friends list actually reads.
            string status = !inLobby ? ""
                : (isHost
                    ? "Captain of a co-op voyage (" + crew + ")"
                    : "Sailing with " + (string.IsNullOrEmpty(captain) ? "a co-op crew" : captain) + " (" + crew + ")");

            // One string covering everything we publish, so an unchanged frame costs a comparison rather
            // than seven interop calls.
            string sig = string.Join("", new[]
            {
                status, lobbyId.ToString(), crew, role, captain, _askLobby.ToString(),
            });
            if (sig == _publishedSignature) return;

            if (!_statusSnapshotTaken)
            {
                // Vanilla Sailwind publishes no rich presence, but "status" is a Steam-reserved key that any
                // future game update (or another mod) could legitimately own. Remember what was there so
                // clearing ours puts it back rather than blanking someone else's.
                _statusSnapshotTaken = true;
                try { _statusBeforeUs = SteamFriends.GetRichPresence(KeyStatus); } catch { _statusBeforeUs = null; }
            }

            Set(KeyStatus, string.IsNullOrEmpty(status) ? (_statusBeforeUs ?? "") : status);
            Set(KeyCoop, Plugin.PluginVersion);
            Set(KeyLobby, lobbyId != 0UL ? lobbyId.ToString() : "");
            Set(KeyCrew, crew);
            Set(KeyRole, role);
            Set(KeyCaptain, captain);
            Set(KeyAsk, _askLobby != 0UL ? _askLobby.ToString() : "");

            _publishedSignature = sig;
        }

        private static void Set(string key, string value)
        {
            // Steam treats an empty value as "remove this key", which is exactly what we want for the
            // fields that only apply while in a lobby. Never pass null: it crosses the interop boundary as
            // a null pointer rather than an empty string.
            try { SteamFriends.SetRichPresence(key, value ?? ""); }
            catch (Exception e) { Plugin.Log.LogWarning("[Presence] could not set " + key + ": " + e.Message); }
        }

        /// <summary>Take our keys back down (on shutdown, or when co-op stops). Restores whatever "status"
        /// held before we first touched it instead of using ClearRichPresence, which would also wipe keys
        /// belonging to the game or another mod.</summary>
        public static void ClearPublished()
        {
            try
            {
                Set(KeyCoop, "");
                Set(KeyLobby, "");
                Set(KeyCrew, "");
                Set(KeyRole, "");
                Set(KeyCaptain, "");
                Set(KeyAsk, "");
                Set(KeyStatus, _statusSnapshotTaken ? (_statusBeforeUs ?? "") : "");
                _publishedSignature = null;
            }
            catch { }
        }

        // --- asking to join ------------------------------------------------------------------------------

        /// <summary>Publish "I would like to board {host}'s ship". Does not join anything and cannot: all it
        /// can do is make a prompt appear on the captain's screen, and only if they are a Steam friend of
        /// ours or of someone in their crew. Replacing an earlier ask is fine - one at a time is the whole
        /// model, since a request you have forgotten is a request you did not mean.</summary>
        public static void AskToJoin(SteamId hostId, ulong lobbyId, string hostName)
        {
            if (lobbyId == 0UL) return;
            if (Plugin.LobbyManager != null && Plugin.LobbyManager.IsInLobby) return;

            _askLobby = lobbyId;
            _askHostId = hostId;
            _askHostName = string.IsNullOrEmpty(hostName) ? "the captain" : hostName;
            _askExpiresAt = UnityEngine.Time.realtimeSinceStartup + AskLifetimeSeconds;
            _publishedSignature = null; _nextPublish = 0f;   // force a push this tick
            Publish();

            Plugin.Log.LogInfo("[Presence] asked to join lobby " + lobbyId + " (" + _askHostName + ")");
            Plugin.NotifyWithHint("Asked " + _askHostName + " to let you aboard.",
                "They will see it when they open their menu", 5f);
        }

        /// <summary>Withdraw our request.</summary>
        public static void CancelAsk()
        {
            if (_askLobby == 0UL) return;
            Plugin.Log.LogInfo("[Presence] withdrew the request to join lobby " + _askLobby);
            ClearAskState();
        }

        private static void ClearAskState()
        {
            if (_askLobby != 0UL)
            {
                _recentAskLobby = _askLobby;
                _recentAskUntil = UnityEngine.Time.realtimeSinceStartup + RequestGraceSeconds * 3f;
            }
            _askLobby = 0UL;
            _askHostId = default(SteamId);
            _askHostName = null;
            _askExpiresAt = 0f;
            _publishedSignature = null;
            _nextPublish = 0f;
            Publish();
        }

        private static void ExpireAsk()
        {
            if (_askLobby == 0UL) return;
            // Joining anything at all settles the question, however we got there.
            if (Plugin.LobbyManager != null && Plugin.LobbyManager.IsInLobby) { ClearAskState(); return; }
            if (UnityEngine.Time.realtimeSinceStartup >= _askExpiresAt)
            {
                Plugin.Log.LogInfo("[Presence] request to join lobby " + _askLobby + " expired unanswered");
                string who = _askHostName;
                ClearAskState();
                Plugin.Notify((string.IsNullOrEmpty(who) ? "The captain" : who) + " did not answer your request to come aboard.", 5f);
            }
        }

        // --- the sweep -----------------------------------------------------------------------------------

        private static void Sweep(float now)
        {
            var lm = Plugin.LobbyManager;
            bool isHost = lm != null && lm.IsHost;
            ulong ourLobby = (lm != null && lm.IsInLobby) ? lm.LobbyId.Value : 0UL;

            // Who is already aboard: never offer to invite, or show a request from, a current crewmate.
            var aboard = new HashSet<ulong>();
            if (lm != null && lm.IsInLobby)
                foreach (var m in lm.LobbyMembers) aboard.Add(m.Id.Value);

            _friends.Clear();
            var seenRequests = new HashSet<ulong>();

            foreach (var f in SteamFriends.GetFriends())
            {
                if (f.IsMe) continue;
                if (!f.IsPlayingThisGame) continue;

                var fp = new FriendPresence
                {
                    Id = f.Id,
                    Name = FriendlyName(f),
                };

                string coop = SafeRead(f, KeyCoop);
                fp.HasCoop = !string.IsNullOrEmpty(coop);

                ulong lobby;
                if (ulong.TryParse(SafeRead(f, KeyLobby), out lobby)) fp.LobbyId = lobby;
                fp.IsCaptain = SafeRead(f, KeyRole) == "host";
                fp.CaptainName = SafeRead(f, KeyCaptain);
                string crew = SafeRead(f, KeyCrew);
                fp.Crew = string.IsNullOrEmpty(crew) ? null : crew;

                // Are they asking to board OUR ship?
                ulong asking;
                if (isHost && ourLobby != 0UL && ulong.TryParse(SafeRead(f, KeyAsk), out asking) && asking == ourLobby
                    && !aboard.Contains(f.Id.Value))
                {
                    seenRequests.Add(f.Id.Value);
                    NoteRequest(f, now);
                    // Set this ONLY when a request row will actually be drawn for them. The screen skips
                    // any friend flagged here on the grounds that they are "shown above with their own
                    // actions" - so flagging someone we then declined to list made them vanish from the
                    // friends list entirely rather than merely losing their request row.
                    fp.IsAskingUs = _requests.ContainsKey(f.Id.Value);
                }

                _friends.Add(fp);
            }

            // A friend of a CREW MEMBER, not of ours, is invisible to SteamFriends.GetFriends() - so their
            // request would never be seen by the loop above. They are reachable through the lobby's own
            // member list only once they are in it, which they are not. This is a real limit of the design
            // and is stated in the UI rather than papered over: you can only be asked by someone you know.

            SortFriends(ourLobby);
            ExpireRequests(now, seenRequests, aboard);
        }

        private static void NoteRequest(Friend f, float now)
        {
            // Already answered, one way or the other. Their presence keeps advertising the request until
            // they either join or it times out, so without this an accepted asker is re-added on the very
            // next sweep, re-toasted, and shown a live "Let aboard" that sends a SECOND real Steam invite
            // every time it is clicked.
            if (IsRecentlyAnswered(_dismissedRequests, f.Id.Value, now)) return;
            if (IsRecentlyAnswered(_acceptedRequests, f.Id.Value, now)) return;

            JoinRequest req;
            if (!_requests.TryGetValue(f.Id.Value, out req))
            {
                req = new JoinRequest
                {
                    Id = f.Id,
                    Name = FriendlyName(f),
                    IsSteamFriend = f.IsFriend,
                    FirstSeen = now,
                };
                _requests[f.Id.Value] = req;
                RebuildRequestOrder();
            }
            req.LastSeen = now;
            req.Name = FriendlyName(f);

            if (_toastedRequests.Add(f.Id.Value))
            {
                Plugin.Log.LogInfo("[Presence] " + req.Name + " (" + req.Id + ") asked to join our crew.");
                Plugin.NotifyWithHint(req.Name + " asks to join your crew.", "Open the menu to let them aboard", 7f);
            }
        }

        private static void ExpireRequests(float now, HashSet<ulong> seen, HashSet<ulong> aboard)
        {
            List<ulong> drop = null;
            foreach (var kv in _requests)
            {
                bool stale = !seen.Contains(kv.Key) && (now - kv.Value.LastSeen) > RequestGraceSeconds;
                if (stale || aboard.Contains(kv.Key))
                {
                    if (drop == null) drop = new List<ulong>();
                    drop.Add(kv.Key);
                }
            }
            if (drop == null) return;
            foreach (var id in drop)
            {
                _requests.Remove(id);
                // Let the same person ask again later and be announced again; only the request, not the
                // person, has gone stale.
                _toastedRequests.Remove(id);
            }
            RebuildRequestOrder();
        }

        /// <summary>Host action: let them aboard. Sends a real Steam lobby invite, which also puts them on
        /// the admission list, so the join then runs the ordinary invited-guest path with no special case
        /// anywhere in the transport.</summary>
        public static void AcceptRequest(SteamId id)
        {
            var lm = Plugin.LobbyManager;
            if (lm == null || !lm.IsHost) return;

            JoinRequest req;
            _requests.TryGetValue(id.Value, out req);
            string name = req != null ? req.Name : id.ToString();

            if (lm.GetMemberCount() >= SteamLobbyManager.MaxPlayers)
            {
                Plugin.Notify("Your crew is full - there is no berth for " + name + ".", 5f);
                return;
            }

            if (lm.InviteFriend(id))
            {
                Plugin.Notify("Invited " + name + " aboard.", 5f);
                _requests.Remove(id.Value);
                _toastedRequests.Remove(id.Value);
                _acceptedRequests[id.Value] = UnityEngine.Time.realtimeSinceStartup;
                RebuildRequestOrder();
            }
            else
            {
                Plugin.Notify("Steam would not deliver the invite to " + name + ".", 6f);
            }
        }

        /// <summary>Host action: turn them down. Silent to them - Steam offers no way to say no, and a
        /// request that simply goes unanswered is the gentler outcome anyway. Remembered for this session
        /// so the same request does not reappear on every sweep.</summary>
        public static void DismissRequest(SteamId id)
        {
            JoinRequest req;
            if (_requests.TryGetValue(id.Value, out req))
                Plugin.Log.LogInfo("[Presence] dismissed " + req.Name + "'s request to join.");
            _requests.Remove(id.Value);
            _toastedRequests.Remove(id.Value);
            _dismissedRequests[id.Value] = UnityEngine.Time.realtimeSinceStartup;
            RebuildRequestOrder();
        }

        /// <summary>Called when we leave a lobby: the crew, the requests and the dismissals all belonged to
        /// that session.</summary>
        public static void OnLobbyEnded()
        {
            _requests.Clear();
            _requestsOrdered.Clear();
            _acceptedRequests.Clear();
            _dismissedRequests.Clear();
            _toastedRequests.Clear();
            _publishedSignature = null;
            _nextPublish = 0f;
            _dirty = true;
        }

        // --- helpers -------------------------------------------------------------------------------------

        /// <summary>True while an answer we already gave this person is still recent enough to stand.</summary>
        private static bool IsRecentlyAnswered(Dictionary<ulong, float> answered, ulong id, float now)
        {
            float when;
            if (!answered.TryGetValue(id, out when)) return false;
            if (now - when < RequestAnswerMemorySeconds) return true;
            answered.Remove(id);   // lapsed; a fresh ask is a fresh question
            return false;
        }

        private static string SafeRead(Friend f, string key)
        {
            try { return f.GetRichPresence(key) ?? ""; }
            catch { return ""; }
        }

        private static string FriendlyName(Friend f)
        {
            string n = f.Name;
            return (string.IsNullOrEmpty(n) || n == "[unknown]") ? "A sailor" : n;
        }

        /// <summary>Most useful first: people asking to board us, then captains with room, then anyone else
        /// sailing in co-op, then friends in the game without the mod. Alphabetical within each band so the
        /// list does not shuffle under the cursor between sweeps.</summary>
        private static void SortFriends(ulong ourLobby)
        {
            _friends.Sort((a, b) =>
            {
                int ra = Rank(a, ourLobby), rb = Rank(b, ourLobby);
                if (ra != rb) return ra.CompareTo(rb);
                return string.Compare(a.Name ?? "", b.Name ?? "", StringComparison.OrdinalIgnoreCase);
            });
        }

        private static int Rank(FriendPresence f, ulong ourLobby)
        {
            if (f.IsAskingUs) return 0;
            if (f.LobbyId != 0UL && f.LobbyId == ourLobby) return 1;  // our own crew
            if (f.IsCaptain) return 2;
            if (f.LobbyId != 0UL) return 3;
            if (f.HasCoop) return 4;
            return 5;
        }
    }
}
