using System.Collections.Generic;
using Steamworks;
using UnityEngine;
using SailwindCoop.Networking;

namespace SailwindCoop.UI
{
    /// <summary>
    /// (v0.3.0) "Friends playing now" - who else is at sea, and the two things you can do about it:
    /// ask to come aboard, or let someone aboard.
    ///
    /// WHY THIS EXISTS RATHER THAN THE STEAM OVERLAY. The host's Invite button used to call
    /// SteamFriends.OpenGameInviteOverlay, which is fine right up until the overlay is switched off - and
    /// then it does nothing at all, silently, which is exactly what several players reported. Inviting from
    /// here calls Lobby.InviteFriend directly, so it works with the overlay disabled, and it shows who is
    /// actually in Sailwind instead of the player's entire friends list.
    ///
    /// THE ASYMMETRY IT FIXES. Until now co-op could only ever start with the host thinking of you first;
    /// there was no way to say "I would like to sail with you" from inside the game. Rich presence provides
    /// that without reopening the lobby to strangers - see <see cref="CoopPresence"/> for why the lobby
    /// stays private and what is and is not published.
    ///
    /// The screen borrows CharacterScreen's rules wholesale, because they were the hard-won ones: it refuses
    /// to exist outside a cursor menu, closes itself the moment that stops being true, consumes all three
    /// vanilla pause keys, and hides the world-space parchment while it is up so a click cannot fall through
    /// to Quit Game underneath.
    /// </summary>
    public static class FriendsScreen
    {
        private static bool _open;
        /// <summary>Whether this screen was opened from the PAUSE menu (and so should put it back on close),
        /// rather than from the title menu, where there is nothing behind us to restore.</summary>
        private static bool _reopenPauseOnClose;
        private static Vector2 _scroll;
        private static bool _stylesBuilt;
        private static GUIStyle _panel, _title, _label, _small, _button, _band, _name;
        private static Texture2D _panelTex, _titleTex, _bandTex;
        private static int _keyConsumedFrame = -1;
        /// <summary>Title-menu buttons we switched off while this screen covers them. See SuppressMenuButtons.</summary>
        private static readonly List<GoPointerButton> _suppressed = new List<GoPointerButton>();

        public static bool IsOpen { get { return _open; } }

        /// <summary>True if this screen swallowed a pause key this frame - see CharacterScreen for the
        /// frozen-world failure this prevents.</summary>
        public static bool ConsumedPauseKeyThisFrame { get { return _keyConsumedFrame == Time.frameCount; } }

        public static void Open()
        {
            try
            {
                if (!GameState.inCursorMenu)
                {
                    Plugin.Log.LogWarning("[Friends] Refused to open outside a cursor menu.");
                    return;
                }
                _scroll = Vector2.zero;
                _open = true;
                // (v0.3.0) Remember whether we are covering the PAUSE parchment, so closing restores only
                // what was actually there. See ForceClose.
                _reopenPauseOnClose = CoopPauseMenu.IsOpen;
                CoopPauseMenu.Hide();
                SuppressMenuButtons();
                // The presence sweep skips its work when nothing would read it, and this screen opening is
                // precisely that "something" - so ask for a fresh one now rather than showing a list up to
                // one heartbeat out of date.
                CoopPresence.RequestSweep();
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("[Friends] Open failed: " + e.Message); }
        }

        /// <summary>
        /// (v0.3.0) Restores the pause parchment ONLY if this screen was covering it.
        ///
        /// This used to reopen it whenever GameState.inCursorMenu was true, which was a sound proxy for
        /// "the player came from the pause menu" for exactly as long as the pause menu was the only way in.
        /// The title menu is also a cursor menu, so once a Friends button appeared there, closing this
        /// screen drew the in-game pause parchment on top of the main menu - two menus at once, neither of
        /// which the player asked for.
        /// </summary>
        public static void ForceClose()
        {
            if (!_open) return;
            _open = false;
            bool restore = _reopenPauseOnClose;
            _reopenPauseOnClose = false;
            RestoreMenuButtons();
            try { if (restore && GameState.inCursorMenu) CoopPauseMenu.Reopen(); }
            catch (System.Exception e) { Plugin.Log.LogWarning("[Friends] Could not restore the pause menu: " + e.Message); }
        }

        /// <summary>
        /// (v0.3.0) Switch off the world-space menu buttons underneath this screen.
        ///
        /// An IMGUI window is drawn, not raycast. MouseButtonPointer.FixedUpdate keeps casting the mouse at
        /// layer 5 for as long as GameState.inCursorMenu is true, and inCursorMenu is exactly the condition
        /// this screen requires to open, so nothing about drawing a panel over the parchment stops a click
        /// from reaching what is behind it. Vanilla's only suppressor is GoPointerButton.unclickable.
        ///
        /// From the pause menu, hiding the co-op panel was enough, because that panel IS the parchment there.
        /// The title menu is a different situation: CoopPauseMenu.Hide() is a no-op, and vanilla's own
        /// 'start UI' stays live behind us. A click on a friend row landing over Continue starts loading the
        /// player's solo save behind a Friends panel that is still drawn; over Quit Game it opens the confirm
        /// prompt; over New Game it tears the title menu down.
        ///
        /// Only buttons that are ACTIVE AND ALREADY CLICKABLE are recorded, so restoring is unambiguously
        /// "put these back to clickable" - vanilla suppresses these itself during menu animations, and a
        /// blanket restore would fight that. We stay off the GameObject's active state entirely: vanilla
        /// FadeStartMenu owns it, and TitleJoinManager.HideStartUI deliberately deactivates it during a join.
        /// </summary>
        private static void SuppressMenuButtons()
        {
            if (_suppressed.Count > 0) return; // already covering something; don't double-record
            try
            {
                var sm = Object.FindObjectOfType<StartMenu>();
                if (sm == null) return;
                foreach (var b in sm.GetComponentsInChildren<GoPointerButton>(false))
                {
                    if (b == null || b.unclickable) continue;
                    b.unclickable = true;
                    _suppressed.Add(b);
                }
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("[Friends] Could not suppress the menu underneath: " + e.Message); }
        }

        private static void RestoreMenuButtons()
        {
            if (_suppressed.Count == 0) return;
            try
            {
                for (int i = 0; i < _suppressed.Count; i++)
                    if (_suppressed[i] != null) _suppressed[i].unclickable = false;
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("[Friends] Could not restore the menu underneath: " + e.Message); }
            finally { _suppressed.Clear(); }
        }

        /// <summary>Drive from Plugin.Update. Cheap no-op while closed.</summary>
        public static void Tick()
        {
            if (!_open) return;
            try
            {
                if (!GameState.inCursorMenu) { ForceClose(); return; }
                if (Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.F10) ||
                    Input.GetKeyDown(KeyCode.JoystickButton6))
                {
                    _keyConsumedFrame = Time.frameCount;
                    ForceClose();
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("[Friends] Tick failed, closing: " + e.Message);
                ForceClose();
            }
        }

        private static void BuildStyles()
        {
            _panelTex = SailwindSkin.SolidTexture(SailwindSkin.Parchment);
            _titleTex = SailwindSkin.SolidTexture(SailwindSkin.ParchmentDark);
            _bandTex = SailwindSkin.SolidTexture(new Color(0f, 0f, 0f, 0.10f));

            _panel = new GUIStyle(GUI.skin.box) { padding = new RectOffset(18, 18, 14, 14), alignment = TextAnchor.UpperLeft };
            _panel.normal.background = _panelTex;

            _title = new GUIStyle(GUI.skin.label)
            {
                fontSize = 24, fontStyle = FontStyle.Bold, wordWrap = true,
                alignment = TextAnchor.MiddleLeft, padding = new RectOffset(12, 12, 8, 8),
            }.WithFont();
            _title.normal.textColor = SailwindSkin.Parchment;
            _title.normal.background = _titleTex;

            _label = new GUIStyle(GUI.skin.label) { fontSize = 19, padding = new RectOffset(4, 4, 6, 6) }.WithFont();
            _label.normal.textColor = SailwindSkin.InkColor;

            _name = new GUIStyle(_label) { fontStyle = FontStyle.Bold };

            _small = new GUIStyle(_label) { fontSize = 15, fontStyle = FontStyle.Italic, wordWrap = true };
            _small.normal.textColor = SailwindSkin.InkFaint;

            _button = new GUIStyle(GUI.skin.button) { fontSize = 17, padding = new RectOffset(10, 10, 5, 5) }.WithFont();
            _button.normal.textColor = SailwindSkin.InkColor;
            _button.hover.textColor = SailwindSkin.ParchmentDark;

            _band = new GUIStyle(GUI.skin.box) { padding = new RectOffset(10, 10, 8, 8), alignment = TextAnchor.UpperLeft };
            _band.normal.background = _bandTex;

            _stylesBuilt = true;
        }

        public static void Draw()
        {
            if (!_open) return;
            if (!_stylesBuilt) BuildStyles();

            float w = Mathf.Min(Screen.width * 0.52f, 720f);
            float h = Mathf.Min(Screen.height * 0.82f, 700f);
            float x = (Screen.width - w) * 0.5f;
            float y = (Screen.height - h) * 0.5f;

            GUI.depth = 0;
            GUILayout.BeginArea(new Rect(x, y, w, h), _panel);
            GUILayout.Label("Friends playing now", _title);
            GUILayout.Space(8f);

            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));

            DrawPendingInvite();
            DrawIncomingRequests();
            DrawOutstandingAsk();
            DrawFriendList();

            GUILayout.EndScrollView();

            GUILayout.Space(6f);
            GUILayout.BeginHorizontal();
            GUILayout.Label(FooterHint(), _small);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Done  (Esc)", _button, GUILayout.Width(140f))) ForceClose();
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        private static string FooterHint()
        {
            if (Plugin.IsHost) return "Only you can let anyone aboard.";
            if (Plugin.IsMultiplayer) return "The captain decides who comes aboard.";
            return "Co-op sessions are invite-only.";
        }

        // --- sections ------------------------------------------------------------------------------------

        private static void DrawPendingInvite()
        {
            var lm = Plugin.LobbyManager;
            var invite = lm != null ? lm.CurrentInvite : null;
            if (invite == null) return;

            GUILayout.BeginVertical(_band);
            GUILayout.Label(invite.SenderName + " invited you to co-op.", _name);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Join " + invite.SenderName, _button, GUILayout.Width(220f)))
            {
                ForceClose();
                // Unpause before joining. ForceClose puts the pause parchment back, which leaves the world
                // frozen, and a join that starts at timeScale 0 never finishes - see CoopPauseMenu.Resume.
                //
                // ONLY IN-GAME, and this is not a tidy-up. ResumeGame routes to vanilla SettingsToGame, which
                // is the UNPAUSE path and assumes a pause happened: it writes `Time.timeScale =
                // unpausedTimescale`, a field assigned nowhere but GameToSettings. At the title menu that
                // pause never happened, so the field is still its default 0 - and SettingsToGame also calls
                // DisableStartMenu. Accepting from the title screen therefore froze the game and hid the
                // menu, then TitleJoinManager's LoadGame parked forever on the first scaled WaitForSeconds
                // inside LoadGameAnimation, which sits before GameState.playing is ever set. The player was
                // left on a dead menu until the 45s "Load timed out".
                if (GameState.playing) CoopPauseMenu.ResumeGame();
                if (Plugin.EnsureCoopReady()) lm.AcceptPendingInvite();
            }
            if (GUILayout.Button("Not now", _button, GUILayout.Width(120f))) lm.DeclinePendingInvite();
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
            GUILayout.Space(10f);
        }

        private static void DrawIncomingRequests()
        {
            if (!Plugin.IsHost) return;
            var requests = CoopPresence.Requests;
            if (requests.Count == 0) return;

            // Accepting or dismissing removes the entry, and CoopPresence hands out its LIVE list rather than
            // a copy - so acting inside the loop would mutate the collection being enumerated and throw
            // mid-draw. Note what was clicked, act once the loop is done.
            SteamId accept = default(SteamId), dismiss = default(SteamId);
            bool doAccept = false, doDismiss = false;

            GUILayout.Label("Asking to come aboard", _name);
            GUILayout.Label("Only people on your own Steam friends list can ask - Steam does not let us "
                            + "see anyone else.", _small);
            for (int i = 0; i < requests.Count; i++)
            {
                var r = requests[i];
                GUILayout.BeginVertical(_band);
                GUILayout.BeginHorizontal();
                DrawAvatar(r.Id);
                GUILayout.BeginVertical();
                GUILayout.Label(r.Name, _name);
                // Everyone who can reach this prompt is on our own friends list, because Steam only
                // replicates rich presence between friends - so the "this person is a stranger to you"
                // warning that used to sit here could never render. The honest version of that thought is
                // the limitation itself, stated once at the top of the section rather than per row.
                GUILayout.Label("On your Steam friends list.", _small);
                GUILayout.EndVertical();
                GUILayout.FlexibleSpace();
                GUILayout.BeginVertical();
                GUILayout.Space(6f);
                if (GUILayout.Button("Let aboard", _button, GUILayout.Width(140f))) { accept = r.Id; doAccept = true; }
                if (GUILayout.Button("Not now", _button, GUILayout.Width(140f))) { dismiss = r.Id; doDismiss = true; }
                GUILayout.EndVertical();
                GUILayout.EndHorizontal();
                GUILayout.EndVertical();
                GUILayout.Space(6f);
            }
            if (doAccept) CoopPresence.AcceptRequest(accept);
            if (doDismiss) CoopPresence.DismissRequest(dismiss);
            GUILayout.Space(8f);
        }

        private static void DrawOutstandingAsk()
        {
            ulong asked = CoopPresence.OutstandingAskLobby;
            if (asked == 0UL) return;

            GUILayout.BeginVertical(_band);
            GUILayout.Label("Waiting on " + (CoopPresence.OutstandingAskHostName ?? "the captain") + ".", _name);
            GUILayout.Label("They will see your request when they next open their menu. If they say yes, " +
                            "the invite appears here and on the pause menu.", _small);
            if (GUILayout.Button("Withdraw", _button, GUILayout.Width(140f))) CoopPresence.CancelAsk();
            GUILayout.EndVertical();
            GUILayout.Space(10f);
        }

        private static void DrawFriendList()
        {
            var friends = CoopPresence.Friends;
            if (friends.Count == 0)
            {
                GUILayout.Label("None of your Steam friends are in Sailwind right now.", _label);
                GUILayout.Label("This list only shows friends who are in the game at this moment. Steam has " +
                                "no way to tell us about anyone else.", _small);
                return;
            }

            var lm = Plugin.LobbyManager;
            ulong ourLobby = (lm != null && lm.IsInLobby) ? lm.LobbyId.Value : 0UL;
            bool weHost = Plugin.IsHost;
            bool weAreSolo = !Plugin.IsMultiplayer;

            foreach (var f in friends)
            {
                if (f.IsAskingUs) continue;   // already shown, with its own actions, above

                GUILayout.BeginHorizontal();
                DrawAvatar(f.Id);

                GUILayout.BeginVertical();
                GUILayout.Label(f.Name, _name);
                GUILayout.Label(DescribeFriend(f, ourLobby), _small);
                GUILayout.EndVertical();

                GUILayout.FlexibleSpace();
                GUILayout.BeginVertical();
                GUILayout.Space(8f);
                DrawFriendAction(f, ourLobby, weHost, weAreSolo);
                GUILayout.EndVertical();
                GUILayout.EndHorizontal();
                GUILayout.Space(4f);
            }
        }

        private static void DrawFriendAction(CoopPresence.FriendPresence f, ulong ourLobby, bool weHost, bool weAreSolo)
        {
            // Already sailing with us: nothing to offer.
            if (f.LobbyId != 0UL && f.LobbyId == ourLobby) { GUILayout.Label("Aboard", _small); return; }

            if (weHost)
            {
                if (Plugin.LobbyManager.GetMemberCount() >= SteamLobbyManager.MaxPlayers)
                {
                    GUILayout.Label("Crew full", _small);
                    return;
                }
                if (GUILayout.Button("Invite", _button, GUILayout.Width(140f)))
                {
                    if (Plugin.LobbyManager.InviteFriend(f.Id)) Plugin.Notify("Invited " + f.Name + " aboard.", 5f);
                    else Plugin.Notify("Steam would not deliver the invite to " + f.Name + ".", 6f);
                }
                return;
            }

            if (!weAreSolo) return;   // a guest cannot invite and should not be asking elsewhere mid-voyage

            if (f.LobbyId == 0UL) return;             // not in a session; there is nothing to ask to join
            if (!f.HasCoop) return;                   // no mod, no session

            if (CoopPresence.OutstandingAskLobby == f.LobbyId) { GUILayout.Label("Asked", _small); return; }
            if (CrewIsFull(f.Crew)) { GUILayout.Label("Crew full", _small); return; }

            if (GUILayout.Button("Ask to join", _button, GUILayout.Width(140f)))
                CoopPresence.AskToJoin(f.Id, f.LobbyId, f.Name);
        }

        private static string DescribeFriend(CoopPresence.FriendPresence f, ulong ourLobby)
        {
            if (!f.HasCoop) return "In Sailwind, without the co-op mod.";
            if (f.LobbyId == 0UL) return "In Sailwind, sailing alone.";
            if (f.LobbyId == ourLobby) return "In your crew.";
            string crew = string.IsNullOrEmpty(f.Crew) ? "" : " (" + f.Crew + ")";
            if (f.IsCaptain) return "Captain of a co-op voyage" + crew + ".";
            return "Sailing with " + (string.IsNullOrEmpty(f.CaptainName) ? "a co-op crew" : f.CaptainName) + crew + ".";
        }

        /// <summary>Read "3/8" back out of the presence string. A malformed or missing value is treated as
        /// room available: the host's own capacity check is the one that actually decides, and refusing to
        /// let someone ask on the strength of an unparsed string would be a worse failure than a request
        /// that gets politely turned down.</summary>
        private static bool CrewIsFull(string crew)
        {
            if (string.IsNullOrEmpty(crew)) return false;
            int slash = crew.IndexOf('/');
            if (slash <= 0 || slash >= crew.Length - 1) return false;
            int have, max;
            if (!int.TryParse(crew.Substring(0, slash), out have)) return false;
            if (!int.TryParse(crew.Substring(slash + 1), out max)) return false;
            return max > 0 && have >= max;
        }

        private const float AvatarSize = 44f;

        private static void DrawAvatar(SteamId id)
        {
            var rect = GUILayoutUtility.GetRect(AvatarSize, AvatarSize, GUILayout.Width(AvatarSize), GUILayout.Height(AvatarSize));
            var tex = SteamAvatarCache.Get(id);
            if (tex != null) GUI.DrawTexture(rect, tex, ScaleMode.ScaleToFit, true);
            else GUI.Box(rect, GUIContent.none, _band);   // a quiet placeholder while Steam fetches it
            GUILayout.Space(8f);
        }
    }
}
