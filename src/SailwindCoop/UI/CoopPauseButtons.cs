using System.Collections.Generic;
using SailwindPlayerModel;
using Steamworks;
using UnityEngine;

namespace SailwindCoop.UI
{
    /// <summary>
    /// Co-op's contributions to the shared parchment pause menu, which lives in the Sailwind Player Model
    /// mod: the Host and Invite/Join buttons and the crew roster on its own little scroll
    /// beside the button column.
    ///
    /// The menu itself is not ours any more. That matters for more than tidiness: two mods each cloning
    /// their own parchment would both answer Escape and draw two scrolls over each other. Registering into
    /// one menu is the only arrangement that survives a player installing both.
    /// </summary>
    public static class CoopPauseButtons
    {
        public const string Host      = "coop_pause_host";
        public const string Secondary = "coop_pause_secondary"; // Invite Friend (host) / Join or Friends (not in lobby)
        const string RowPrefix = "coop_pause_player_";

        // The crew roster lives on its OWN small parchment scroll to the right of the main menu, rather than
        // as chips floating in space. The chips are CHILDREN of it, so they move and scale with it as one
        // unit. The layout below is in that scroll's LOCAL space, which is the same coordinate system as the
        // button column since it is the same scroll mesh.
        const float CrewScrollScale = 0.7f;
        static readonly Vector3 CrewScrollPos = new Vector3(1.25f, 0f, 0f);
        const float CrewTopY = 1.02f;
        const float CrewStep = 0.245f;
        const float CrewChipZ = 0.037f;
        // Vertical extent the roster may occupy. Rows are FIT to this the way the button column is: the
        // roster length is the crew size, 1 to MaxPlayers, so a top-anchored fixed-step layout either wasted
        // the lower half of the parchment at two players or ran off it at eight.
        const float CrewSpan = CrewStep * 5f;
        // Hard ceiling on NAMED rows before falling back to a "+K more" summary.
        const int MaxCrewChips = 6;

        static GameObject _crewScroll;
        static string _lastRosterSig;

        /// <summary>Register co-op's buttons and hooks. Call once, from Plugin.Awake.</summary>
        public static void Install()
        {
            ModPauseMenu.Register(new ModPauseMenu.Entry
            {
                Id = Host,
                Order = 100,
                // A GUEST does not get this button at all, because for a guest it was a lie: the leave path
                // ends in EndGuestSessionAndQuit, so "Leave Lobby" and "Quit Game" both closed the game and
                // only one of them said so. That is not fixed by renaming - a guest is inside the HOST's
                // world on a phantom save, and with no return-to-menu in this game there is nowhere to put
                // them, so quitting really is the only honest exit. Offering it twice, once under a label
                // that implies otherwise, invites a player to lose their session to the wrong click.
                Visible = () => !Plugin.IsMultiplayer || Plugin.IsHost,
                Label = () => !Plugin.IsMultiplayer ? "Host Co-op" : "Close Lobby",
                OnClick = () =>
                {
                    if (!Plugin.IsMultiplayer)
                    {
                        if (Plugin.EnsureCoopReady()) Plugin.LobbyManager.CreateLobby();
                    }
                    else
                    {
                        // MENU-FLY-AWAY fix: Close Lobby must UNPAUSE first rather than keep the panel open.
                        // The panel is a world-space parchment held in front of the camera only by a
                        // per-frame re-pin that is gated on being in a lobby. LeaveLobby flips that false
                        // synchronously, so the re-pin stops while the never-frozen world keeps moving, and
                        // the panel sails off. SettingsToGame restores timescale, look and cursor and its
                        // postfix hides the panel, dropping cleanly to gameplay; THEN leave the lobby.
                        ModPauseMenu.InvokeStartMenu("SettingsToGame");
                        Plugin.LobbyManager.LeaveLobby();
                    }
                },
            });

            ModPauseMenu.Register(new ModPauseMenu.Entry
            {
                Id = Secondary,
                Order = 200,
                Visible = () => true,
                Label = SecondaryLabel,
                OnClick = () =>
                {
                    if (!Plugin.EnsureCoopReady()) return; // surface the reason instead of a dead button
                    // Solo WITH an invite waiting: take it in one click. Everything else opens the friends
                    // screen, which is where inviting, asking and declining all live.
                    if (!Plugin.IsMultiplayer && Plugin.LobbyManager?.CurrentInvite != null)
                    {
                        // Unpause FIRST. Joining from a frozen world stalls the join coroutine until the
                        // player happens to unpause, and the watchdog quits before that.
                        ModPauseMenu.ResumeGame();
                        Plugin.LobbyManager.AcceptPendingInvite();
                    }
                    else
                    {
                        FriendsScreen.Open();
                    }
                },
            });

            // Only the captain may recover the SHARED boat. A guest running vanilla recovery would teleport
            // the shared boat locally and desync from the host. The button itself is the player model's.
            ModPauseMenu.SetVisible(ModPauseMenu.Recover, () => !(Plugin.IsMultiplayer && !Plugin.IsHost));

            // In a lobby the menu must NOT freeze the world: a host pausing would stop simulating the shared
            // boat, and a time stop desyncs both sides.
            ModPauseMenu.KeepWorldRunning = () => Plugin.IsMultiplayer;
            // Our own screens hide the parchment as they open, which would otherwise stop the re-pin and let
            // the still-active logo and scroll sail away behind them.
            ModPauseMenu.KeepPinned = () => FriendsScreen.IsOpen;
            // Our screens own the pause key while they are up, the same way the character screen does.
            SailwindPlayerModel.PauseMenuPatches.ExtraPauseKeyClaim = () =>
                FriendsScreen.IsOpen || FriendsScreen.ConsumedPauseKeyThisFrame
                || CoopMessagePanel.IsShowing || CoopMessagePanel.ConsumedPauseKeyThisFrame;
            // The character screen cannot know whether a multiplayer mod is installed; tell it.
            CharacterScreen.StatusLine = () => Plugin.IsMultiplayer
                ? "Your crew sees this character too."
                : "Your crew will see this character when you sail together.";

            ModPauseMenu.PanelBuilt += BuildCrewScroll;
            ModPauseMenu.Refreshing += RefreshCrew;
            ModPauseMenu.Hiding += OnMenuHidden;
            // Crew chips have their StartMenuButton stripped so they cannot be clicked, but swallow their
            // names anyway rather than letting an unrecognized click fall through to a vanilla action.
            ModPauseMenu.ClickFallback = name => name != null && name.StartsWith(RowPrefix);
        }

        static string SecondaryLabel()
        {
            if (!Plugin.IsMultiplayer)
            {
                // Solo, this slot is ACCEPT INVITE when there is one. The old button opened Steam's friends
                // overlay, which just dumped the player into a list to hunt through, and the lobby is
                // invite-only anyway so browsing to a friend was never going to let anyone in. With no
                // invite pending it opens our friends screen, which is where "ask to join" lives - the
                // answer to the other half of the problem, that co-op could only ever begin with the host
                // thinking of you first.
                var invite = Plugin.LobbyManager?.CurrentInvite;
                return invite != null ? "Join " + invite.SenderName : "Friends";
            }
            if (!Plugin.IsHost) return "Friends"; // a guest can neither invite nor go asking mid-voyage

            int members = Plugin.LobbyManager.GetMemberCount();
            bool hasRoom = members < SailwindCoop.Networking.SteamLobbyManager.MaxPlayers;
            int asking = SailwindCoop.Networking.CoopPresence.RequestCount;
            // Someone waiting to be let aboard is the one thing here worth interrupting for, so it takes the
            // label. Otherwise: invite, or say plainly that there is no berth left. The click itself is
            // gated, so a full-lobby click is a harmless no-op.
            if (asking > 0) return asking == 1 ? "1 wants aboard" : asking + " want aboard";
            return hasRoom ? "Invite Friend" : "Crew full";
        }

        // ---- crew roster --------------------------------------------------------------------------------

        /// <summary>Build the small crew-roster parchment once, beside the button column.</summary>
        static void BuildCrewScroll()
        {
            if (_crewScroll != null) return;
            _crewScroll = ModPauseMenu.CloneScroll("coop_crew_scroll", CrewScrollPos, CrewScrollScale);
        }

        static void OnMenuHidden()
        {
            ClearPlayerList();     // crew rows must never linger past the pause screen
            _lastRosterSig = null; // force a fresh rebuild on the next open
        }

        static void RefreshCrew()
        {
            if (_crewScroll == null) return;
            bool inLobby = Plugin.IsMultiplayer;
            _crewScroll.SetActive(inLobby); // the roster only means anything in a lobby

            // Gate the rebuild on a roster SIGNATURE (ids, names and host flag), not just member count: a
            // late-arriving Steam persona name changes the signature so the blank row refreshes, where count
            // alone would not. Also rebuild when rows are missing in a lobby, or present after LEAVING - the
            // signature is null both before and after a solo-to-solo no-op, so equality alone would not
            // clear them.
            string sig = inLobby ? BuildRosterSignature() : null;
            bool rowsPresent = MenuUtil.FindChild(_crewScroll.transform, RowPrefix + "0") != null;
            if (sig != _lastRosterSig || (inLobby && !rowsPresent) || (!inLobby && rowsPresent))
            {
                _lastRosterSig = sig;
                RebuildPlayerList();
            }
        }

        static void RebuildPlayerList()
        {
            ClearPlayerList();
            if (!Plugin.IsMultiplayer || _crewScroll == null) return;

            // Each crew member gets a parchment "chip", a non-interactive clone of a menu button, laid out on
            // the crew scroll. A "Crew:" header takes row 0; members fill rows 1 onward.
            var template = ModPauseMenu.FindButton(ModPauseMenu.Resume);
            if (template == null) return;

            var members = new List<Friend>(Plugin.LobbyManager.LobbyMembers);
            int total = members.Count;

            // When everyone fits, render one chip each. Over the cap, fill the first (MaxCrewChips - 1)
            // member slots and use the final slot for a "+K more" summary so nothing overflows the scroll.
            bool overflow = total > MaxCrewChips;
            int namedChips = overflow ? MaxCrewChips - 1 : total;
            int rows = 1 + namedChips + (overflow ? 1 : 0);

            // Fit the rows to the parchment rather than stepping at a fixed pitch from a fixed top. The
            // roster grows with the crew, so a constant step left a two-player scroll half empty and could
            // not have fitted eight at all. Shrinking the CHIPS by the same factor as the step keeps the
            // gap-to-plate ratio constant, so a full crew reads as a tighter list rather than as overlapping
            // plates.
            float step = rows > 1 ? Mathf.Min(CrewStep, CrewSpan / (rows - 1)) : CrewStep;
            float chipScale = Mathf.Clamp(step / CrewStep, 0.4f, 1f);
            // TOP-anchored, unlike the button column. A roster is a list that GROWS downward as people join,
            // so it should start under the heading and fill toward the bottom; centering it left a
            // two-player crew floating in the middle of the parchment with empty space above "Crew:".
            float top = CrewTopY;

            // The header is plain text on the parchment, not a plate. It labels the list rather than naming a
            // crewmate, and giving it the same button-shaped plate as the names read as though "Crew:" were
            // one of the crew.
            MakeChip(template, top, step, 0, chipScale, "Crew:", plate: false);

            for (int idx = 0; idx < namedChips; idx++)
            {
                var m = members[idx];
                bool isSelf = m.Id == SteamClient.SteamId;
                MakeChip(template, top, step, idx + 1, chipScale, m.Name + (isSelf ? " (you)" : ""));
            }

            if (overflow)
                MakeChip(template, top, step, namedChips + 1, chipScale, "+" + (total - namedChips) + " more");
        }

        /// <summary>One non-interactive crew chip at row idx on the crew scroll.</summary>
        static void MakeChip(Transform template, float top, float step, int idx, float scale, string label,
            bool plate = true)
        {
            var chip = Object.Instantiate(template.gameObject, _crewScroll.transform);
            chip.name = RowPrefix + idx;
            chip.transform.localRotation = template.localRotation; // same player-facing orientation as buttons
            chip.transform.localScale = Vector3.one * scale;       // the crew scroll is already uniform
            chip.transform.localPosition = new Vector3(0f, top - step * idx, CrewChipZ);
            // Make it a pure label: strip the button behavior and collider so it cannot be clicked.
            foreach (var b in chip.GetComponentsInChildren<StartMenuButton>(true)) Object.Destroy(b);
            foreach (var col in chip.GetComponentsInChildren<Collider>(true)) Object.Destroy(col);

            // plate:false leaves the text and hides the parchment plate behind it. A TextMesh draws through a
            // MeshRenderer of its own, so the plate cannot be found by component type - the renderers to keep
            // are exactly the ones sharing a GameObject with a TextMesh.
            if (!plate)
            {
                foreach (var r in chip.GetComponentsInChildren<MeshRenderer>(true))
                    if (r.GetComponent<TextMesh>() == null) r.enabled = false;
            }

            MenuUtil.SetLabel(chip.transform, label);
        }

        static void ClearPlayerList()
        {
            if (_crewScroll == null) return;
            for (int i = _crewScroll.transform.childCount - 1; i >= 0; i--)
            {
                var c = _crewScroll.transform.GetChild(i);
                if (c.name.StartsWith(RowPrefix)) Object.Destroy(c.gameObject);
            }
        }

        static string BuildRosterSignature()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(Plugin.IsHost ? 'H' : 'G').Append('|');
            foreach (var m in Plugin.LobbyManager.LobbyMembers)
                sb.Append(m.Id.Value).Append(':').Append(m.Name).Append(';');
            return sb.ToString();
        }
    }
}
