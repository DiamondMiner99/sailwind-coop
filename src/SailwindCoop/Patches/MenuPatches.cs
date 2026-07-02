using System;
using HarmonyLib;
using UnityEngine;

namespace SailwindCoop.Patches
{
    /// <summary>
    /// Injects the title-menu Host button (CoopMenu) and the custom in-game pause menu (CoopPauseMenu)
    /// into the game's world-space StartMenu, and wires the pause lifecycle.
    /// </summary>
    public static class MenuPatches
    {
        /// <summary>The live StartMenu (captured at Start). Used by TitleJoinManager to drive a vanilla
        /// save-load when a guest accepts a co-op invite from the title screen.</summary>
        public static StartMenu ActiveStartMenu { get; private set; }

        // Build both menus once at boot (both panels exist by Start; the title panel is already active).
        [HarmonyPatch(typeof(StartMenu), "Start")]
        public static class StartMenuStartPatch
        {
            static void Postfix(StartMenu __instance)
            {
                ActiveStartMenu = __instance;
                SailwindCoop.UI.CoopMenu.Install(__instance);
                SailwindCoop.UI.CoopPauseMenu.Install(__instance);
            }
        }

        // In-game pause opens via GameToSettings (it does all the pause bookkeeping + opens the vanilla
        // settings panel). Postfix: hide that and show our custom pause panel instead.
        [HarmonyPatch(typeof(StartMenu), "GameToSettings")]
        public static class GameToSettingsPatch
        {
            static void Postfix(StartMenu __instance)
            {
                SailwindCoop.UI.CoopPauseMenu.OnPauseOpened(__instance);
            }
        }

        // Any unpause (Resume button, Esc, settings-Back-to-game) routes through SettingsToGame. The
        // StartMenu root never deactivates, so we must hide our panel here or it lingers during gameplay.
        [HarmonyPatch(typeof(StartMenu), "SettingsToGame")]
        public static class SettingsToGamePatch
        {
            static void Postfix()
            {
                SailwindCoop.UI.CoopPauseMenu.Hide();
                SailwindCoop.UI.CoopPauseMenu.SubPageFromPause = false;
            }
        }

        // While our panel (or our settings sub-page) is open, route Esc to Resume / back-to-pause instead
        // of the vanilla open-settings path (which doesn't recognize our panel and would re-pause).
        [HarmonyPatch(typeof(StartMenu), "LateUpdate")]
        public static class StartMenuLateUpdatePatch
        {
            static bool Prefix(StartMenu __instance)
            {
                if (!GameState.playing) return true;
                if (!(Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.F10) || Input.GetKeyDown(KeyCode.JoystickButton6)))
                    return true;
                // OnEscape returns true if it handled it (resume / settings-back-to-pause) -> skip vanilla.
                if (SailwindCoop.UI.CoopPauseMenu.OnEscape(__instance)) return false;

                // QoL (vanilla parity): EnablePortMissionUI arms a 1.1s closeCooldown that makes vanilla's
                // Esc->DisablePortMissionUI a no-op for the first ~second, so the port mission list feels like
                // it can only be closed by the parchment's Back button. Esc NEVER opens the list, so clearing
                // that cooldown here just lets the vanilla Esc-close in this same LateUpdate fire immediately.
                // Narrowly gated on inPortMissionList; trade/currency menus (no cooldown) and pause are untouched.
                if (GameState.inPortMissionList)
                    HarmonyLib.Traverse.Create(typeof(MissionListUI)).Field("closeCooldown").SetValue(0f);

                return true; // run vanilla LateUpdate (closes the open cursor-menu, or opens pause)
            }
        }

        // Settings sub-page "Back": if it was opened from our pause menu, return to our pause panel
        // instead of unpausing. (__0 = the StartMenuButtonType argument, name-agnostic.)
        [HarmonyPatch(typeof(StartMenu), "ButtonClick", new[] { typeof(StartMenuButtonType) })]
        public static class ButtonClickPatch
        {
            static bool Prefix(StartMenu __instance, StartMenuButtonType __0)
            {
                // PHANTOM CO-OP SAVE - guest Quit reconciliation:
                // Vanilla ButtonClick(Quit) does `if (readyToSave) SaveGame(); Application.Quit();`.
                // Safety doesn't depend on readyToSave (currentSlot==99 routes any save to the phantom
                // file), but we still suppress the vanilla in-line save and run OUR phantom save inline
                // at click time instead, so exactly ONE write happens AND the DoSaveGame coroutine gets
                // to complete at end of frame (vanilla's own quit-save timing). Deferring to
                // Plugin.OnApplicationQuit does NOT work: SaveGame only STARTS a coroutine, which dies
                // at its first yield during app shutdown, so the phantom never actually committed.
                // If the save isn't committable right now (mid-sleep/busy/shipyard), SaveCoopSession
                // returns WITHOUT marking the session saved, so the OnApplicationQuit backstop retries.
                if (__0 == StartMenuButtonType.Quit && Plugin.IsGuest)
                {
                    SaveLoadManager.readyToSave = false;
                    SailwindCoop.CoopSave.SaveCoopSession(); // marks _savedThisSession only if it actually ran
                    SaveLoadManager.readyToSave = false;     // SaveCoopSession re-arms it; keep vanilla's OWN in-line save suppressed
                    SailwindCoop.Debug.VerboseLogger.LobbyEvent("Guest Quit: vanilla in-line save suppressed; phantom save started inline at click time");
                    return true; // let vanilla run; it now only quits (our phantom save commits end-of-frame)
                }
                return !SailwindCoop.UI.CoopPauseMenu.OnSettingsBack(__instance, __0);
            }
        }

        // Route clicks on our cloned buttons (title + pause). The StartMenuButton sits on a 'bg+trigger'
        // child; our coop marker is on an ANCESTOR, so walk up. A misread would fire the vanilla action.
        [HarmonyPatch(typeof(StartMenuButton), "OnActivate", new Type[0])]
        public static class StartMenuButtonOnActivatePatch
        {
            static bool Prefix(StartMenuButton __instance)
            {
                for (var t = __instance.transform; t != null; t = t.parent)
                {
                    if (SailwindCoop.UI.CoopMenu.HandleClick(t.name)) return false;
                    if (SailwindCoop.UI.CoopPauseMenu.HandleClick(t.name)) return false;
                }
                return true; // not ours -> run the vanilla action
            }
        }

        // The vanilla "(paused)" indicator (PauseNotifText) shows whenever Time.timeScale==0 OR
        // Sun.SunPaused() - i.e. in solo pause, and in co-op whenever a menu pauses the Sun. In co-op we keep
        // timeScale running (the shared world can't freeze), so "(paused)" is misleading. Rather than hide it,
        // SWAP the text to "(online)" while in a lobby (host or client) so it mirrors the solo "(paused)" but
        // tells you you're in a server; restore the original text when solo. The renderer is left to vanilla, so
        // it still appears only in the same situations (menus), just relabelled.
        static string _origPauseText;
        [HarmonyPatch(typeof(PauseNotifText), "Update")]
        public static class PauseNotifCoopPatch
        {
            static void Postfix(PauseNotifText __instance)
            {
                var tm = __instance.GetComponent<TextMesh>();
                if (tm == null)
                {
                    // Fallback: if the indicator isn't a TextMesh we can relabel, at least don't show the
                    // misleading "(paused)" in a lobby - hide the renderer instead.
                    if (Plugin.IsMultiplayer)
                    {
                        var r = __instance.GetComponent<Renderer>();
                        if (r != null && r.enabled) r.enabled = false;
                    }
                    return;
                }
                // Capture the vanilla text once (so the solo restore is exact, not hardcoded).
                if (_origPauseText == null && tm.text != "(online)") _origPauseText = tm.text;
                string want = Plugin.IsMultiplayer ? "(online)" : (_origPauseText ?? tm.text);
                if (tm.text != want) tm.text = want;
            }
        }
    }
}
