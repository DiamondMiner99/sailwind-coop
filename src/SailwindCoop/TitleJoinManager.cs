using System.Collections;
using HarmonyLib;
using SailwindCoop.Patches;
using Steamworks;
using UnityEngine;

namespace SailwindCoop
{
    /// <summary>
    /// Lets a guest accept a co-op invite from the TITLE MENU. Sailwind uses a SINGLE world scene, so
    /// "loading a save" just deserializes data into the already-live scene - no region scene to pick. We
    /// auto-load a save, satisfy the vanilla "press F to continue" disclaimer gate, then join the lobby.
    /// Doing the load BEFORE joining means the proven in-game join flow (host sends boat world state ->
    /// guest is teleported to the host's boat) runs against a fully-loaded guest instead of racing one
    /// that's still at the title. The existing join already teleports across regions in one coordinate
    /// space, so a guest whose save is in another region still ends up on the host's boat.
    /// </summary>
    public static class TitleJoinManager
    {
        private static bool _busy;

        /// <summary>True only while the guest's phantom world is being deserialized for a co-op join. While
        /// set, <see cref="Patches.SaveLoadResiliencePatches"/> swallows per-object load exceptions so ONE
        /// corrupt saveable (e.g. a boat customization that throws ArgumentOutOfRangeException) can't abort
        /// the whole vanilla load and leave GameState.playing false forever - which silently stranded a
        /// guest at "Load timed out" and never connected. The guest is teleported onto the HOST's boat
        /// anyway, so their own world's contents are cosmetic. Solo loads never set this, so they keep
        /// vanilla crash-on-corruption behavior.</summary>
        public static bool SuppressLoadErrors;

        /// <summary>Begin auto-load-then-join for a guest accepting an invite at the title screen.
        /// (v0.2.25) hostId keys the phantom co-op save per host; at the title screen we haven't
        /// entered the lobby yet (no Lobby.Owner), so callers pass the friend whose invite/game the
        /// guest is joining through (OnGameLobbyJoinRequested's friendId) - the host in practice.</summary>
        public static void Begin(SteamId lobbyId, SteamId hostId)
        {
            if (_busy) { Plugin.Log.LogInfo("[TitleJoin] already in progress, ignoring"); return; }
            if (Plugin.Instance == null) { Plugin.Log.LogWarning("[TitleJoin] no plugin instance"); return; }
            Plugin.Instance.StartCoroutine(AutoLoadThenJoin(lobbyId, hostId));
        }

        private static IEnumerator AutoLoadThenJoin(SteamId lobbyId, SteamId hostId)
        {
            _busy = true;
            try
            {
                var sm = MenuPatches.ActiveStartMenu;
                if (sm == null) sm = Object.FindObjectOfType<StartMenu>();
                if (sm == null)
                {
                    Plugin.Notify("Couldn't auto-load (menu not ready). Load a save, then accept the invite.", 7f);
                    yield break;
                }

                // PHANTOM CO-OP SAVE: enter the reserved slot-99 context. This either reuses an existing
                // hidden coop_session.save (carrying forward the guest's co-op needs) or seeds a new one
                // by copying the guest's most-recent solo .save (read solo / write phantom - the solo slot
                // is never written). On success SaveSlots.currentSlot is now 99, so the vanilla LoadGame
                // below loads the PHANTOM world (valid world + persisted needs) and every later save is
                // structurally redirected to the phantom file - never a real slot.
                if (!CoopSave.EnterCoopSaveContext(hostId.Value, out bool didCreate))
                {
                    Plugin.Notify("Start or load a save once before joining co-op.", 7f);
                    yield break;
                }
                Plugin.Log.LogInfo($"[TitleJoin] phantom co-op context ready (didCreate={didCreate}); loading phantom, then joining lobby {lobbyId}");
                Plugin.Notify("Loading your world...", 8f);

                // LoadGame early-returns while a menu animation is in flight; wait it out.
                float t0 = Time.realtimeSinceStartup;
                while (Anims(sm) > 0 && Time.realtimeSinceStartup - t0 < 10f) yield return null;

                // (v0.3.0) NEVER START THIS LOAD FROZEN. LoadGameAnimation's first two waits are
                // `WaitForSeconds` - scaled - and both sit BEFORE it sets GameState.playing, so at
                // timeScale 0 the coroutine parks on the first one and never comes back. Vanilla's own
                // animsPlaying counter is incremented by LoadGame and only decremented at the end of that
                // animation, and StartMenu.ButtonClick early-returns while it is above zero - so a frozen
                // load does not merely stall, it kills every button on the menu behind it. The player is
                // left staring at a dead title screen until the timeout below finally speaks.
                //
                // Three separate paths reached this frozen in v0.3.0 (a guest joining from the pause menu,
                // a host opening a session there, and accepting an invite from the title friends list), so
                // the guard belongs HERE, at the one place they all funnel through, rather than only at each
                // caller.
                if (Time.timeScale <= 0f)
                {
                    Plugin.Log.LogWarning("[TitleJoin] timeScale was 0 at load time - restoring it, or the load would park forever.");
                    Time.timeScale = 1f;
                    Physics.autoSyncTransforms = true;
                }

                // Run the full vanilla load coroutine (blackout, deserialize, enable controllers, set playing).
                // Guard the deserialize: a single corrupt saveable in the guest's phantom must not abort the
                // whole load (that left GameState.playing false -> our wait below timed out -> never joined).
                SuppressLoadErrors = true;
                Traverse.Create(sm).Method("LoadGame", new object[] { 0 }).GetValue();

                // LoadGameAnimation sets GameState.playing=true just before parking on the F-gate.
                t0 = Time.realtimeSinceStartup;
                while (!GameState.playing && Time.realtimeSinceStartup - t0 < 45f) yield return null;
                if (!GameState.playing)
                {
                    // (v0.3.0) HAND THE MENU BACK. Bailing out alone left the player with nothing to click:
                    // vanilla's LoadGame incremented animsPlaying and only LoadGameAnimation decrements it,
                    // and StartMenu.ButtonClick early-returns while it is above zero - so a load that never
                    // finished silently disabled every button on the title screen. Reported as "I think my
                    // game is stuck, I can't interact with the menu at all", which it was.
                    Plugin.Log.LogError("[TitleJoin] Phantom load did not reach GameState.playing within 45s - restoring the title menu.");
                    try
                    {
                        Traverse.Create(sm).Field("animsPlaying").SetValue(0);
                        Traverse.Create(sm).Method("EnableStartMenu").GetValue();
                    }
                    catch (System.Exception e) { Plugin.Log.LogWarning("[TitleJoin] could not restore the title menu: " + e.Message); }
                    // Drop the phantom context too, or the next thing this player saves goes to slot 99.
                    try { CoopSave.ClearContext(); } catch { }

                    UI.CoopMessagePanel.Show("Could not load your world",
                        new System.Collections.Generic.List<string>
                        {
                            "The co-op world did not finish loading, so the join was abandoned.",
                        },
                        "Load a save from the menu, then accept the invite again.");
                    yield break;
                }

                // (v0.3.0) The world is now on screen, and the join does not reach JoinLobby for another
                // couple of seconds: the F-gate spin below, then a deliberate 1.5s settle. Raising the join
                // screen only at JoinLobby therefore left the player looking at their own freshly loaded
                // world for that whole stretch before anything acknowledged the join, which is exactly the
                // "I still saw my world load first" report. Begin is idempotent, so JoinLobby's own call
                // further down is a harmless no-op after this one. Everything between here and JoinLobby is
                // guarded and non-throwing, so this cannot raise a blackout that never comes down.
                UI.JoinProgressScreen.Begin();

                // FRESH PHANTOM: a brand-new phantom was seeded from a solo save, which carries that
                // save's hunger/sleep state. Reset to a clean rested/fed baseline so the guest's first
                // co-op session doesn't start starving. (Done AFTER LoadGame, which calls LoadNeeds and
                // would otherwise overwrite these. Subsequent sessions reuse the phantom and skip this.)
                if (didCreate)
                {
                    CoopSave.ResetNeedsToBaseline();
                    Plugin.Log.LogInfo("[TitleJoin] fresh phantom: reset needs to baseline after load");
                }

                // (v0.2.34 stranded-anchor persistence) The phantom save carries each boat's ANCHOR as its
                // own SaveableObject (IsSet + rope length + world position; vanilla Anchor.OnLoad calls
                // SetAnchor, freezing the body kinematic wherever it deserialized). A session that ended
                // with a stranded SET anchor therefore reloads it stranded BEFORE any multiplayer
                // correction can exist - the reported restart-proof "rope to the horizon" that only
                // deleting coop.save cleared. The host's authoritative anchor state is re-applied during
                // the join, so a set anchor loaded from the phantom is never worth keeping: release + stow
                // every one now, while we are still pre-lobby (no peers, so nothing broadcasts).
                NeutralizeLoadedAnchors();

                // WALLET AUTHORITY: we deliberately do NOT touch PlayerGold.currency here. The vanilla
                // LoadGame above left the guest's rich SOLO-save balance in currency[], but the host's
                // authoritative CurrencySync overwrites it reliably on join: the guest's join coroutine
                // sends an EconomySyncRequest once settled, and the host replies with a TARGETED
                // SendCurrencySync (plus one delayed re-send) that element-wise replaces the wallet. Zeroing
                // the wallet here instead would break vanilla's local-wallet buy gate (stalls
                // reject a 0 wallet), so we use a reliable overwrite, not a local zero.

                // FLOATING-MENU FIX: We invoked LoadGame() directly (above), bypassing the button nav that
                // normally calls DisableStartMenu() -> FadeStartMenu(-1) -> startUI.SetActive(false). So the
                // world-space "start UI" parchment is left active, hanging in the world as a floating main menu
                // (Esc toggles it away). Vanilla LoadGameAnimation already hides 'logo', but never the startUI.
                // Hide it the same way vanilla ultimately does, but with a DIRECT SetActive(false) (not
                // FadeStartMenu, which increments/decrements animsPlaying and could unbalance that counter
                // while our F-gate loop below is still spinning on animsPlaying).
                HideStartUI(sm);

                // Auto-satisfy the "press F to continue" disclaimer (the load coroutine spins on private
                // fPressed). Keep setting it until the coroutine consumes it and animsPlaying drops to 0.
                t0 = Time.realtimeSinceStartup;
                while (Anims(sm) > 0 && Time.realtimeSinceStartup - t0 < 20f)
                {
                    // Guarded: a throw on this reflected field-set must NOT kill the coroutine before
                    // JoinLobby (that left title-screen joiners loaded-but-never-connected). It's only a
                    // cosmetic disclaimer auto-clear; if it fails we still join.
                    try { Traverse.Create(sm).Field("fPressed").SetValue(true); }
                    catch (System.Exception ex) { Plugin.Log.LogWarning($"[TitleJoin] fPressed set failed (non-fatal): {ex.Message}"); }
                    yield return null;
                }

                // Let the world settle, then join. The host's OnPlayerJoined sends boat world state and the
                // existing applicator teleports us to the host's boat. NOTE: everything between the world
                // load (GameState.playing) and here is now non-throwing (HideStartUI, Anims, fPressed all
                // guarded), so the coroutine is guaranteed to reach JoinLobby once the world has loaded.
                yield return new WaitForSecondsRealtime(1.5f);
                Plugin.Log.LogInfo("[TitleJoin] load complete; joining lobby");
                Plugin.LobbyManager.JoinLobby(lobbyId);
            }
            finally { _busy = false; SuppressLoadErrors = false; }
        }

        /// <summary>
        /// (v0.2.34) Release + stow every SET anchor the phantom load just restored. Runs pre-lobby on the
        /// guest's title-join path only (the host's own solo load keeps vanilla behavior). SnapStrandedAnchor
        /// re-homes a far body; ReleaseAnchor un-freezes the kinematic state so the zeroed joint limit reels
        /// the body to the hull; ResetAnchor zeroes currentLength so the rope renders stowed. Non-throwing:
        /// a failure here must never break the join (worst case = pre-fix behavior).
        /// </summary>
        private static void NeutralizeLoadedAnchors()
        {
            int neutralized = 0;
            try
            {
                foreach (var boat in Sync.BoatUtility.FindAllBoats().Values)
                {
                    if (boat == null) continue;
                    var anchor = Sync.BoatUtility.GetAnchor(boat);
                    if (anchor == null || !anchor.IsSet()) continue;

                    var mooring = boat.GetComponent<BoatMooringRopes>();
                    var ropeCtrl = mooring != null ? mooring.GetAnchorController() : null;

                    // Re-home a far body first (works on the still-kinematic pose), then un-set it.
                    Sync.BoatStateApplicator.SnapStrandedAnchor(boat, anchor, ropeCtrl, isAnchored: false, ropeLength: 0f);
                    try { Traverse.Create(anchor).Method("ReleaseAnchor").GetValue(); }
                    catch (System.Exception ex) { Plugin.Log.LogWarning($"[TitleJoin] anchor release failed on '{boat.gameObject.name}': {ex.Message}"); }
                    ropeCtrl?.ResetAnchor();
                    neutralized++;
                }
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning($"[TitleJoin] anchor neutralize sweep failed (non-fatal): {ex.Message}");
            }
            if (neutralized > 0)
                Plugin.Log.LogInfo($"[TitleJoin] neutralized {neutralized} SET anchor(s) loaded from the phantom save (stranded-anchor guard; host state re-applies on join)");
        }

        // Non-throwing: if the reflected field can't be read, return 0 ("no anims") so the F-gate loop
        // EXITS and the coroutine still reaches JoinLobby. An unguarded throw here previously killed the
        // whole title-join coroutine before it could connect (guest loaded their own world, never joined).
        private static int Anims(StartMenu sm)
        {
            try { return Traverse.Create(sm).Field("animsPlaying").GetValue<int>(); }
            catch { return 0; }
        }

        // NOTE: save-slot selection moved to CoopSave.EnterCoopSaveContext / CoopSave.PickSoloSlot as part
        // of the phantom co-op save. The title-join flow no longer points currentSlot at a real solo slot;
        // it points it at the reserved phantom slot (99) instead.

        /// <summary>
        /// Directly deactivate the StartMenu's world-space "start UI" parchment (and the title logo, which
        /// vanilla LoadGameAnimation already hides) so a title-screen join doesn't leave a floating menu in
        /// the world. Uses Traverse on the private [SerializeField] GameObject fields (startUI, logo) and a
        /// plain SetActive(false), avoiding FadeStartMenu so we never touch the animsPlaying counter.
        /// </summary>
        private static void HideStartUI(StartMenu sm)
        {
            foreach (var field in new[] { "startUI", "logo" })
            {
                try
                {
                    var go = Traverse.Create(sm).Field(field).GetValue<GameObject>();
                    if (go != null && go.activeSelf)
                    {
                        go.SetActive(false);
                        Plugin.Log.LogInfo($"[TitleJoin] Hid StartMenu.{field} (floating-menu fix)");
                    }
                }
                catch (System.Exception ex)
                {
                    Plugin.Log.LogWarning($"[TitleJoin] Could not hide StartMenu.{field}: {ex.Message}");
                }
            }
        }
    }
}
