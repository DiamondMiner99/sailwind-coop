using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Steamworks;

namespace SailwindCoop.UI
{
    /// <summary>
    /// A custom in-game PAUSE menu that replaces "Esc = full settings screen". It's a clone of the
    /// title 'start UI' parchment scroll (mesh 'scroll_open'), parented under the StartMenu root so it
    /// inherits MoveMenuToPlayer's camera-facing orientation, with a clean button column:
    ///   Resume / Host Co-op (-> Close Lobby) / Invite or Join Friend / Settings / Recover Boat / Quit Game
    /// plus a live player list when in a lobby. "Settings" opens the real settings screen as a sub-page;
    /// its Back (and Esc) return here.
    ///
    /// Lifecycle (driven by MenuPatches):
    ///  - GameToSettings postfix -> OnPauseOpened(): the game already did all pause bookkeeping
    ///    (timescale=0, MoveMenuToPlayer, unpausedTimescale field, etc.) and opened the vanilla settings
    ///    panel; we hide that and show ours.
    ///  - SettingsToGame postfix -> Hide(): any unpause hides our panel (the StartMenu root never
    ///    deactivates, so we must hide ourselves).
    ///  - LateUpdate / ButtonClick prefixes -> Esc-resume and settings-Back-to-pause.
    /// </summary>
    public static class CoopPauseMenu
    {
        public const string Resume    = "coop_pause_resume";
        public const string Host      = "coop_pause_host";
        public const string Secondary = "coop_pause_secondary"; // Invite Friend (host) / Join Friend (not in lobby)
        public const string Appearance = "coop_pause_appearance"; // opens the character screen
        public const string Settings  = "coop_pause_settings";
        public const string Recover   = "coop_pause_recover";
        public const string Quit      = "coop_pause_quit";
        private const string RowPrefix = "coop_pause_player_";

        // Column layout for the button stack in the cloned scroll, top-anchored with a fixed step.
        // LayoutButtons() places only the VISIBLE buttons (so a hidden Secondary closes the gap instead
        // of leaving a hole). Tune Top/Step/X/Z by screenshot - far fewer knobs than per-button positions.
        const float ColX = 0f;
        const float ColZ = 0.037f;
        const float ColTopY = 1.02f; // low enough that the top (Resume) button clears the top scroll roll
        const float ColStep = 0.245f; // step sized so all 6 buttons fit the parchment (nothing spills off the
                                      // bottom). Still > button height, so no overlap.
        // Vertical extent the column is allowed to occupy, in the same local units as ColStep. Derived from
        // the original six-button layout so that case is pixel-identical; LayoutButtons shrinks the step to
        // stay inside this when there are more buttons.
        const float ColSpan = ColStep * 5f;
        static readonly string[] ColOrder = { Resume, Host, Secondary, Appearance, Settings, Recover, Quit };
        static Vector3 Slot(int i) => new Vector3(ColX, ColTopY - ColStep * i, ColZ);
        // The crew roster lives on its OWN small parchment scroll (_crewScroll) to the right of the main
        // menu, instead of as chips floating in space. The chips are CHILDREN of _crewScroll, so they move and
        // scale with it as one unit (no per-chip scale juggling). The layout below is in _crewScroll's LOCAL
        // space - the same coordinate system as the main button column, since it's the same scroll mesh.
        const float CrewScrollScale = 0.7f;                                // crew scroll size vs the main one
        static readonly Vector3 CrewScrollPos = new Vector3(1.25f, 0f, 0f); // its position, right of the main scroll
        const float CrewTopY = 1.02f;   // header row near the top of the crew scroll (matches the button column)
        const float CrewStep = 0.245f;  // crew row spacing (same as the button column, so up to ~6 rows fit)
        const float CrewChipZ = 0.037f; // chips sit just in front of the crew parchment
        static Vector3 CrewSlot(int i) => new Vector3(0f, CrewTopY - CrewStep * i, CrewChipZ);
        // Header takes row 0, members fill rows 1.. ; cap so header + members fit the scroll (~6 rows total).
        const int MaxCrewChips = 5;

        static GameObject _panel;
        static GameObject _crewScroll; // small parchment behind the crew roster (built once, shown in-lobby)
        static MonoBehaviour _startMenu;
        static string _lastRosterSig; // roster signature (ids+names+host) the player list was last built from

        /// <summary>True while a sub-page (settings / recovery / quit-confirm) was opened FROM our pause
        /// menu, so its Back/Esc return to our panel instead of the vanilla settings screen.</summary>
        public static bool SubPageFromPause { get; set; }

        public static bool IsOpen => _panel != null && _panel.activeSelf;

        /// <summary>Build the custom pause panel once (from a StartMenu.Start postfix). Kept inactive.</summary>
        public static void Install(MonoBehaviour startMenu)
        {
            try
            {
                _startMenu = startMenu;
                if (_panel != null) return;

                var startUI = CoopMenu.FindChild(startMenu.transform, "start UI");
                if (startUI == null) { Plugin.Log.LogWarning("[CoopPause] 'start UI' not found"); return; }

                // Clone the parchment scroll and place it where start UI sits (so MoveMenuToPlayer aims it).
                _panel = Object.Instantiate(startUI.gameObject, startMenu.transform);
                _panel.name = "coop_pause_panel";
                _panel.transform.localPosition = startUI.localPosition;
                _panel.transform.localRotation = startUI.localRotation;
                _panel.transform.localScale = startUI.localScale;

                var template = CoopMenu.FindChild(_panel.transform, "button new game");
                if (template == null)
                {
                    // Don't leave a live clone of the title menu (its vanilla New Game/Quit buttons would
                    // fire real actions); destroy + null so OnPauseOpened can retry instead of caching it.
                    Plugin.Log.LogWarning("[CoopPause] template button not found");
                    Object.Destroy(_panel); _panel = null; return;
                }

                // Record the vanilla buttons (by their StartMenuButton component's parent GO) BEFORE we
                // add ours - robust to however they're nested under the scroll (name-based stripping
                // missed them, which left the originals overlapping our buttons).
                var vanilla = new List<GameObject>();
                foreach (var smb in _panel.GetComponentsInChildren<StartMenuButton>(true))
                {
                    var btn = (smb.transform.parent != null) ? smb.transform.parent.gameObject : smb.gameObject;
                    if (!vanilla.Contains(btn)) vanilla.Add(btn);
                }

                // Build our column (clones keep the native StartMenuButton + layer 5).
                CoopMenu.EnsureButton(_panel.transform, template, Resume, Slot(0));
                CoopMenu.EnsureButton(_panel.transform, template, Host, Slot(1));
                CoopMenu.EnsureButton(_panel.transform, template, Secondary, Slot(2));
                CoopMenu.EnsureButton(_panel.transform, template, Appearance, Slot(3));
                CoopMenu.EnsureButton(_panel.transform, template, Settings, Slot(4));
                CoopMenu.EnsureButton(_panel.transform, template, Recover, Slot(5));
                CoopMenu.EnsureButton(_panel.transform, template, Quit, Slot(6));

                // Static labels (Host/Secondary are set dynamically in Refresh).
                SetLabel(Resume, "Resume");
                SetLabel(Appearance, "Character");
                SetLabel(Settings, "Settings");
                SetLabel(Recover, "Recover Boat");
                SetLabel(Quit, "Quit Game");

                // Remove the original title buttons so only ours remain.
                foreach (var go in vanilla) Object.Destroy(go);

                LayoutButtons();
                BuildCrewScroll(startUI);
                _panel.SetActive(false);
                Plugin.Log.LogInfo("[CoopPause] pause panel built");
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogError($"[CoopPause] install failed: {e}");
                // A half-built panel (vanilla buttons not yet stripped) must never go live; drop it so we retry.
                if (_panel != null) { Object.Destroy(_panel); _panel = null; }
            }
        }

        /// <summary>Called from the GameToSettings postfix: hide the vanilla settings panel, show ours.</summary>
        public static void OnPauseOpened(MonoBehaviour startMenu)
        {
            try
            {
                _startMenu = startMenu;
                if (_panel == null) Install(startMenu);
                if (_panel == null) return;
                SubPageFromPause = false;
                InvokeStartMenu("DisableSettingsMenu");
                _panel.SetActive(true);
                _lastRosterSig = null;
                Refresh();

                // In multiplayer the menu must NOT freeze the world: the host pausing would stop simulating
                // the shared boat (the guest would see it freeze), and a time-stop desyncs both sides.
                // GameToSettings already set Time.timeScale=0; restore it while we're in a lobby. (Solo still
                // pauses normally - this only triggers when a server is open.)
                if (Plugin.IsMultiplayer)
                {
                    float ts = Traverse.Create(_startMenu).Field("unpausedTimescale").GetValue<float>();
                    Time.timeScale = ts > 0f ? ts : 1f;
                    Physics.autoSyncTransforms = true;
                    // NOTE: we deliberately DO NOT disable player control here. Freezing the player in MP means
                    // disabling charController, which kills gravity (you freeze mid-jump) and, on a moving ship,
                    // risks not tracking the deck (left behind at sea) - a far worse failure than walking in a
                    // menu. The world can't be frozen in co-op (timeScale must run), so the player stays mobile.
                }
            }
            catch (System.Exception e) { Plugin.Log.LogError($"[CoopPause] open failed: {e}"); }
        }

        /// <summary>(v0.2.39) Re-show the pause parchment after one of our own sub-screens closes. Without
        /// this, closing the character screen would leave the player paused in a cursor menu with nothing
        /// on screen at all - our panel hidden, and vanilla's settings panel already suppressed by us.</summary>
        public static void Reopen()
        {
            if (_panel != null && _startMenu != null) OnPauseOpened(_startMenu);
        }

        public static void Hide()
        {
            if (_panel != null && _panel.activeSelf) _panel.SetActive(false);
            ClearPlayerList();     // crew rows must never linger past the pause screen
            _lastRosterSig = null; // force a fresh rebuild on the next open
        }

        /// <summary>Refresh labels + player list while open. Called every frame from Plugin.Update.</summary>
        public static void Tick()
        {
            try
            {
                if (!IsOpen) return;
                Refresh();
            }
            catch (System.Exception e) { Plugin.Log.LogError($"[CoopPause] tick failed: {e}"); }
        }

        /// <summary>Re-pin the world-space menu to the camera. MUST run from LateUpdate, NOT Update. The world
        /// doesn't pause in co-op, so the boat sails this world-space menu out of view; MoveMenuToPlayer
        /// re-aims it at Camera.main - but Camera.main only finishes following the bobbing observerMirror in
        /// LateUpdate, so re-pinning in Update used LAST frame's camera pose and the menu lagged one frame,
        /// bobbing/drifting on screen as the boat moved. Keyed on Time.timeScale > 0 (MENU-FLY-AWAY defense: a
        /// panel left open across a multiplayer->solo transition keeps tracking instead of flying off); solo
        /// pause has timeScale == 0 so the panel correctly stays put.</summary>
        public static void LatePin()
        {
            try
            {
                // Re-pin while EITHER our co-op panel is open OR a sub-page we opened from it (Settings /
                // Recover / Quit-confirm) is showing. Those sub-pages are vanilla startMenu children that ALSO
                // drift while the world keeps running; our panel is HIDDEN while a sub-page is up, so gating
                // only on IsOpen left the sub-pages un-pinned (they floated off at sea). MoveMenuToPlayer
                // re-aims the whole startMenu root, so re-pinning here covers the panel and the sub-pages.
                bool pinning = IsOpen || (SubPageFromPause && AnySubPageActive());
                if (!pinning) return;
                if (_startMenu != null && Time.timeScale > 0f)
                    Traverse.Create(_startMenu).Method("MoveMenuToPlayer").GetValue();
            }
            catch (System.Exception e) { Plugin.Log.LogError($"[CoopPause] late-pin failed: {e}"); }
        }

        static void Refresh()
        {
            bool inLobby = Plugin.IsMultiplayer, isHost = Plugin.IsHost;

            // The crew scroll only makes sense in a lobby (it holds the roster) - hide it solo.
            if (_crewScroll != null) _crewScroll.SetActive(inLobby);

            // (v0.2.39) A GUEST no longer gets this button at all, because for a guest it was a lie: the
            // leave path ends in EndGuestSessionAndQuit, so "Leave Lobby" and "Quit Game" both closed the
            // game and only one of them said so. That is not a bug to fix by renaming - a guest is inside
            // the HOST's world on a phantom save, and with no return-to-menu in this game there is nowhere
            // to put them, so quitting really is the only honest exit. Offering it twice, once under a
            // label that implies otherwise, just invites a player to lose their session to the wrong click.
            // Hosts keep it: "Close Lobby" genuinely ends the session and leaves them sailing their own
            // world. Dropping a button for guests also gives the column back some breathing room.
            var hostBtn = CoopMenu.FindChild(_panel.transform, Host);
            bool showHostButton = !inLobby || isHost;
            if (hostBtn != null) SetActive(hostBtn, showHostButton);
            if (showHostButton) SetLabel(Host, !inLobby ? "Host Co-op" : "Close Lobby");

            // Secondary: Join when solo, Invite while the HOST has room for more crew, hidden otherwise.
            // N-player: the host can keep inviting until the lobby hits the crew cap (was: single-guest
            // only, gated on !HasConnectedGuest). At N=1 the cap is far above 1 member so this still
            // shows "Invite Friend" exactly as before; a non-host never sees Invite (Join only).
            var sec = CoopMenu.FindChild(_panel.transform, Secondary);
            if (sec != null)
            {
                // (v0.2.39) Solo, this slot is now ACCEPT INVITE rather than "Join Friend". The old button
                // called SteamFriends.OpenOverlay("friends"), which just dumped the player into their
                // friends list to hunt for someone - and the lobby is invite-only anyway, so browsing to a
                // friend was never going to let anyone in. What players actually need is a way to say yes
                // to an invite they were already sent, which until now had no in-game answer at all.
                // No invite pending means no button, rather than a button that goes nowhere useful.
                //
                // (v0.2.39, second pass) When there is NO invite to accept, this slot opens the friends
                // screen instead of disappearing. That screen is where "ask to join" lives, which is the
                // answer to the other half of the problem: an invite-only lobby meant co-op could only ever
                // begin with the host thinking of you first, and a player who knew their friend was sailing
                // had nothing to click. The slot is reused rather than adding an eighth button - the column
                // is already tight enough that seven made the buttons shrink.
                if (!inLobby)
                {
                    var invite = Plugin.LobbyManager?.CurrentInvite;
                    SetActive(sec, true);
                    CoopMenu.SetLabel(sec, invite != null ? $"Join {invite.SenderName}" : "Friends");
                }
                else if (isHost)
                {
                    int members = Plugin.LobbyManager.GetMemberCount();
                    bool hasRoom = members < SailwindCoop.Networking.SteamLobbyManager.MaxPlayers;
                    int asking = SailwindCoop.Networking.CoopPresence.RequestCount;
                    SetActive(sec, true);
                    // Someone waiting to be let aboard is the one thing here worth interrupting for, so it
                    // takes the label. Otherwise: invite, or say plainly that there is no berth left. The
                    // click itself is gated in HandleClick, so a full-lobby click is a harmless no-op.
                    CoopMenu.SetLabel(sec, asking > 0
                        ? (asking == 1 ? "1 wants aboard" : $"{asking} want aboard")
                        : (hasRoom ? "Invite Friend" : "Crew full"));
                }
                // A guest can neither invite nor go asking elsewhere mid-voyage, but seeing who else is out
                // there costs nothing and is half the appeal of a friends list.
                else { SetActive(sec, true); CoopMenu.SetLabel(sec, "Friends"); }
            }

            // Gate the rebuild on a roster SIGNATURE (ids+names+host), not just member count: a late-arriving
            // Steam persona name changes the signature so the blank row refreshes, where count alone wouldn't.
            // Also rebuild when rows are missing in a lobby, or present after LEAVING (else they linger - the
            // signature is null both before and after a solo->solo no-op, so equality alone wouldn't clear them).
            string sig = inLobby ? BuildRosterSignature() : null;
            bool rowsPresent = _crewScroll != null && CoopMenu.FindChild(_crewScroll.transform, RowPrefix + "0") != null;
            if (sig != _lastRosterSig || (inLobby && !rowsPresent) || (!inLobby && rowsPresent))
            {
                _lastRosterSig = sig;
                RebuildPlayerList();
            }

            // Only the captain (host) may recover the SHARED boat. A guest running vanilla recovery
            // would teleport the shared boat locally and desync from the host, so hide it for guests.
            var rec = CoopMenu.FindChild(_panel.transform, Recover);
            if (rec != null) SetActive(rec, !(inLobby && !isHost));

            LayoutButtons(); // re-stack visible buttons so a hidden Secondary doesn't leave a gap
        }

        public static bool HandleClick(string name)
        {
            switch (name)
            {
                case Resume:
                    InvokeStartMenu("SettingsToGame"); // unpause (its postfix hides our panel)
                    return true;
                case Appearance:
                    // Opening HIDES this parchment: a screen-space IMGUI panel drawn over live world-space
                    // buttons would let a click fall through to whatever sits beneath it, and one of those
                    // is Quit Game. CharacterScreen.Open does the hiding itself so the two cannot desync.
                    CharacterScreen.Open();
                    return true;
                case Host:
                    if (!Plugin.IsMultiplayer) { if (Plugin.EnsureCoopReady()) Plugin.LobbyManager.CreateLobby(); }
                    // MENU-FLY-AWAY fix: Close/Leave Lobby must UNPAUSE first, not keep the panel open.
                    // The co-op pause panel is a world-space parchment kept in front of the camera only by a
                    // per-frame re-pin gated on IsMultiplayer (Tick). LeaveLobby flips IsMultiplayer false
                    // synchronously, so the re-pin stops while the (never-frozen) world keeps moving -> the
                    // panel sails off ("flies away"). SettingsToGame restores Time.timeScale/MouseLook/cursor
                    // and its postfix calls Hide(), dropping cleanly to gameplay; THEN leave the lobby.
                    else { InvokeStartMenu("SettingsToGame"); Plugin.LobbyManager.LeaveLobby(); }
                    return true;
                case Secondary:
                    if (!Plugin.EnsureCoopReady()) return true; // surface the reason instead of a dead button
                    // Solo WITH an invite waiting: take it, in one click. RouteJoin is the exact path Steam's
                    // own "Join Game" drives, so joining from the title menu still loads a save first rather
                    // than dropping a world-less guest into the host's session. Everything else opens the
                    // friends screen, which is where inviting, asking and declining all live now.
                    if (!Plugin.IsMultiplayer && Plugin.LobbyManager?.CurrentInvite != null)
                        Plugin.LobbyManager.AcceptPendingInvite();
                    else
                        FriendsScreen.Open();
                    return true;
                case Settings:
                    Hide();
                    SubPageFromPause = true;
                    InvokeStartMenu("EnableSettingsMenu");
                    HideInSettingsRecoverQuit(); // our panel has dedicated Recover/Quit; hide the in-settings dupes
                    return true;
                case Recover:
                    Hide();
                    SubPageFromPause = true; // so Back/Esc out of the recovery confirm returns to our panel
                    InvokeStartMenu("EnableRecoveryMenu"); // vanilla recover-cost confirm screen
                    return true;
                case Quit:
                    Hide();
                    SubPageFromPause = true; // so cancelling the confirm returns to our panel
                    // Don't LeaveLobby here - that's premature if the user cancels. The confirm's "Yes"
                    // (type Quit) saves+quits; process exit ends the lobby for the guest.
                    InvokeButtonClick(StartMenuButtonType.QuitMenu); // vanilla confirm-quit dialog
                    return true;
            }
            return name != null && name.StartsWith(RowPrefix); // player rows: swallow
        }

        /// <summary>Esc while our panel (or our settings sub-page) is open. Returns true if consumed.</summary>
        public static bool OnEscape(MonoBehaviour startMenu)
        {
            _startMenu = startMenu;
            if (IsOpen) { InvokeStartMenu("SettingsToGame"); return true; } // resume
            // Esc out of a sub-page we opened (settings / recovery / quit-confirm) returns to our panel.
            if (SubPageFromPause && _panel != null && AnySubPageActive())
            {
                ReturnToPanelFromSubPage();
                return true;
            }
            return false;
        }

        /// <summary>Sub-page "Back" pressed: if opened from our pause menu, return here instead of unpausing.</summary>
        public static bool OnSettingsBack(MonoBehaviour startMenu, StartMenuButtonType button)
        {
            _startMenu = startMenu;
            if (button != StartMenuButtonType.Back) return false;
            if (!SubPageFromPause || _panel == null) return false;
            // The new-game-only sub-panels are off-limits (they shouldn't appear in-game anyway).
            if (SubPanelActive("chooseIslandUI") || SubPanelActive("saveSlotUI")) return false;
            if (!AnySubPageActive()) return false;
            ReturnToPanelFromSubPage();
            return true;
        }

        static bool AnySubPageActive() =>
            SubPanelActive("settingsUI") || SubPanelActive("recoveryUI") || SubPanelActive("confirmQuitUI");

        /// <summary>Close whichever sub-page (settings/recovery/quit-confirm) is open and re-show our panel.</summary>
        static void ReturnToPanelFromSubPage()
        {
            SubPageFromPause = false;
            if (SubPanelActive("settingsUI")) InvokeStartMenu("DisableSettingsMenu");
            if (SubPanelActive("recoveryUI")) InvokeStartMenu("DisableRecoveryMenu");
            var cq = (_startMenu != null) ? Traverse.Create(_startMenu).Field("confirmQuitUI").GetValue<GameObject>() : null;
            if (cq != null && cq.activeInHierarchy) cq.SetActive(false);
            if (_panel != null) { _panel.SetActive(true); _lastRosterSig = null; Refresh(); }
        }

        /// <summary>Build the small crew-roster parchment once - a scaled clone of the same scroll, parented
        /// under the panel and sat to its right. Crew chips become children of THIS, so they scale/move with it.</summary>
        static void BuildCrewScroll(Transform startUI)
        {
            try
            {
                if (_crewScroll != null || _panel == null || startUI == null) return;
                _crewScroll = Object.Instantiate(startUI.gameObject, _panel.transform);
                _crewScroll.name = "coop_crew_scroll";
                _crewScroll.transform.localPosition = CrewScrollPos;
                _crewScroll.transform.localRotation = Quaternion.identity; // inherit the panel's player-facing aim
                _crewScroll.transform.localScale = Vector3.one * CrewScrollScale;
                // Strip the cloned title buttons (same as the main panel) so only the parchment remains.
                foreach (var smb in _crewScroll.GetComponentsInChildren<StartMenuButton>(true))
                {
                    var btn = (smb.transform.parent != null) ? smb.transform.parent.gameObject : smb.gameObject;
                    Object.Destroy(btn);
                }
                _crewScroll.SetActive(false); // shown only in a lobby (Refresh)
            }
            catch (System.Exception e) { Plugin.Log.LogError($"[CoopPause] crew scroll build failed: {e}"); _crewScroll = null; }
        }

        // --- player list ---

        static void RebuildPlayerList()
        {
            ClearPlayerList();
            if (!Plugin.IsMultiplayer || _crewScroll == null) return;

            // Each crew member gets a parchment "chip" - a non-interactive clone of one of our buttons - laid out
            // ON the crew scroll. A "Crew:" header takes row 0; members fill rows 1.. .
            var template = CoopMenu.FindChild(_panel.transform, Resume);
            if (template == null) return;

            MakeChip(template, 0, "Crew:");

            // Buffer the roster so we know the total up front (to decide if the last slot is a real chip
            // or a "+K more" overflow summary). Order matches LobbyMembers (same as the title roster).
            var members = new List<Friend>(Plugin.LobbyManager.LobbyMembers);
            int total = members.Count;

            // When everyone fits, render one chip each. When over the cap, fill the first (MaxCrewChips-1)
            // member slots and use the final slot for a "+K more" summary so nothing overflows the scroll.
            bool overflow = total > MaxCrewChips;
            int namedChips = overflow ? MaxCrewChips - 1 : total;

            for (int idx = 0; idx < namedChips; idx++)
            {
                var m = members[idx];
                bool isSelf = m.Id == SteamClient.SteamId;
                string label = m.Name + (isSelf ? " (you)" : "");
                MakeChip(template, idx + 1, label); // +1: header occupies row 0
            }

            if (overflow)
            {
                int remaining = total - namedChips;
                MakeChip(template, namedChips + 1, $"+{remaining} more");
            }
        }

        /// <summary>Build a single non-interactive crew "chip" (a stripped clone of a button) at row `idx` ON the
        /// crew scroll. Full template scale - the chip reads at CrewScrollScale because its parent scroll is scaled.</summary>
        static void MakeChip(Transform template, int idx, string label)
        {
            var chip = Object.Instantiate(template.gameObject, _crewScroll.transform);
            chip.name = RowPrefix + idx;
            chip.transform.localRotation = template.localRotation; // same player-facing orientation as buttons
            chip.transform.localScale = template.localScale;       // parent scroll provides the shrink
            chip.transform.localPosition = CrewSlot(idx);
            // Make it a pure label: strip button behaviour + collider so it can't be clicked or highlighted.
            foreach (var b in chip.GetComponentsInChildren<StartMenuButton>(true)) Object.Destroy(b);
            foreach (var col in chip.GetComponentsInChildren<Collider>(true)) Object.Destroy(col);
            CoopMenu.SetLabel(chip.transform, label);
        }

        static void ClearPlayerList()
        {
            // Chips live on the crew scroll, not the main panel.
            if (_crewScroll == null) return;
            for (int i = _crewScroll.transform.childCount - 1; i >= 0; i--)
            {
                var c = _crewScroll.transform.GetChild(i);
                if (c.name.StartsWith(RowPrefix)) Object.Destroy(c.gameObject);
            }
        }

        // --- helpers ---

        static string BuildRosterSignature()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(Plugin.IsHost ? 'H' : 'G').Append('|');
            foreach (var m in Plugin.LobbyManager.LobbyMembers)
                sb.Append(m.Id.Value).Append(':').Append(m.Name).Append(';');
            return sb.ToString();
        }

        /// <summary>Top-anchored fixed-step stack over only the VISIBLE buttons, so a hidden Secondary
        /// (guest, or host with a guest aboard) closes the gap instead of leaving a hole in the column.</summary>
        static void LayoutButtons()
        {
            if (_panel == null) return;
            // (v0.2.39) Count the visible buttons FIRST so the step can shrink to fit them. The step used
            // to be a fixed constant chosen so exactly six buttons filled the parchment, which meant adding
            // a seventh (Appearance) would have pushed the bottom one off the bottom of the scroll. Fitting
            // to a fixed SPAN instead keeps the column inside the parchment at any count, and leaves the
            // common cases visually identical (fewer than six still lays out at the original spacing).
            int visible = 0;
            foreach (var name in ColOrder)
            {
                var b = CoopMenu.FindChild(_panel.transform, name);
                if (b != null && b.gameObject.activeSelf) visible++;
            }
            float step = visible > 1 ? Mathf.Min(ColStep, ColSpan / (visible - 1)) : ColStep;

            // (v0.2.39) Scale the buttons down as the column fills up. The span is fixed, so each extra
            // button shrinks the step - seven buttons sit ~17% closer together than the six the spacing was
            // originally tuned for, which reads as crowded. Shrinking the buttons is the only lever that
            // changes the GAP-TO-BUTTON ratio: stretching the parchment vertically would squash these
            // children (they inherit its scale), and scaling it uniformly magnifies the crowding along
            // with everything else. Applied per button, uniformly, so nothing distorts.
            float scale = Mathf.Clamp(Plugin.CoopMenuButtonScaleConfig != null
                ? Plugin.CoopMenuButtonScaleConfig.Value : 0.9f, 0.5f, 1f);

            int i = 0;
            foreach (var name in ColOrder)
            {
                var b = CoopMenu.FindChild(_panel.transform, name);
                if (b == null || !b.gameObject.activeSelf) continue;
                b.localPosition = new Vector3(ColX, ColTopY - step * i, ColZ);
                b.localScale = Vector3.one * scale;
                i++;
            }
        }

        /// <summary>Hide the vanilla in-settings Recover/Quit buttons while our pause menu owns Settings, so the
        /// only Recover/Quit entry points are our dedicated buttons (otherwise Back out of recovery/quit-confirm
        /// skips the settings level). EnableSettingsMenu re-activates them, so call this right after it.</summary>
        static void HideInSettingsRecoverQuit()
        {
            if (_startMenu == null) return;
            Traverse.Create(_startMenu).Field("recoverButton").GetValue<GameObject>()?.SetActive(false);
            Traverse.Create(_startMenu).Field("quitButtonInSettings").GetValue<GameObject>()?.SetActive(false);
        }

        static void SetLabel(string buttonName, string text)
        {
            var b = CoopMenu.FindChild(_panel.transform, buttonName);
            if (b != null) CoopMenu.SetLabel(b, text);
        }

        static void SetActive(Transform t, bool on) { if (t.gameObject.activeSelf != on) t.gameObject.SetActive(on); }

        static void InvokeStartMenu(string method)
        {
            if (_startMenu == null) return;
            Traverse.Create(_startMenu).Method(method).GetValue();
        }

        static void InvokeButtonClick(StartMenuButtonType type)
        {
            if (_startMenu == null) return;
            Traverse.Create(_startMenu).Method("ButtonClick", new object[] { type }).GetValue();
        }

        static bool SubPanelActive(string field)
        {
            if (_startMenu == null) return false;
            var go = Traverse.Create(_startMenu).Field(field).GetValue<GameObject>();
            return go != null && go.activeInHierarchy;
        }
    }
}
