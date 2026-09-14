using UnityEngine;
using SailwindCoop.Player;

namespace SailwindCoop.UI
{
    /// <summary>
    /// (v0.3.0) The character screen: pick which Synty modular parts your avatar wears.
    ///
    /// OPENED FROM THE PAUSE MENU and nowhere else. CoopMenu (the title screen) is a documented no-op in
    /// this mod - co-op lives only in the in-game pause menu - and that constraint is convenient rather
    /// than limiting here: the avatar body template is CLONED FROM A SHOPKEEPER IN THE LOADED SCENE, so at
    /// the title screen there is no world, no template, and therefore nothing to preview or even enumerate.
    /// Being in-game is a precondition of this screen working at all.
    ///
    /// CURSOR: this panel deliberately does NO cursor bookkeeping whatsoever, and that is the design, not
    /// an omission. Vanilla's pause (StartMenu.GameToSettings) has already unlocked and shown the cursor
    /// and set GameState.inCursorMenu, so the cursor is free before we ever draw. The sibling
    /// CoopMessagePanel has to snapshot and restore cursor state because it can appear during gameplay,
    /// and that snapshot going stale is a real bug class - it can hand the player a free cursor while the
    /// camera still turns. Rather than repeat that machinery and its hazard, this screen simply REFUSES TO
    /// EXIST outside a cursor menu: it will not open unless GameState.inCursorMenu, and a per-frame
    /// watchdog closes it the moment that stops being true (resume, quit to menu, anything). A welded,
    /// cursor-stealing panel is then not merely handled, it is unreachable.
    ///
    /// ESCAPE: handled here and CONSUMED, because CoopPauseMenu.OnEscape would otherwise resume the game
    /// in the same frame - Update runs before StartMenu's LateUpdate, so both would fire on one keypress
    /// and Escape would close the screen AND unpause.
    ///
    /// THE PARCHMENT IS HIDDEN while this is open. Leaving it visible behind a screen-space IMGUI panel
    /// lets a click pass through to the world-space button underneath - and one of those is Quit Game.
    /// </summary>
    public static class CharacterScreen
    {
        private static bool _open;
        private static Vector2 _scroll;
        private static bool _stylesBuilt;
        private static bool _dirty;                 // choices changed since opening
        private static CoopAppearance _working;     // edited copy; only committed on Done
        private static GUIStyle _panel, _title, _label, _button, _small;
        private static Texture2D _panelTex, _titleTex;

        public static bool IsOpen { get { return _open; } }

        private static int _keyConsumedFrame = -1;

        /// <summary>
        /// True if this screen swallowed a pause key this frame. Read by the StartMenu.LateUpdate prefix:
        /// Plugin.Update runs BEFORE LateUpdate, so the latch is still fresh when the prefix checks it.
        /// Without it, the same keypress that closes this screen also reaches vanilla and unpauses the
        /// game - the doc's claim that one press does one thing was simply not true, because hiding the
        /// parchment had already disarmed CoopPauseMenu.OnEscape.
        /// </summary>
        public static bool ConsumedPauseKeyThisFrame { get { return _keyConsumedFrame == Time.frameCount; } }

        /// <summary>Open the screen. Refuses outside a cursor menu - see the class doc.</summary>
        public static void Open()
        {
            try
            {
                if (!GameState.inCursorMenu)
                {
                    Plugin.Log.LogWarning("[Character] Refused to open outside a cursor menu.");
                    return;
                }
                // Take a private COPY of the array. CoopAppearance is a struct holding a byte[], so a plain
                // assignment would share the array with Plugin's stored appearance and every registry
                // entry - editing here would mutate them in place behind their backs.
                _working = CoopAppearance.Deserialize(Plugin.LocalAppearance.Serialize());
                // Fold indices into this rig's real ranges up front, so the screen never shows a number
                // like "174 / 8" that the body is not actually wearing (Apply wraps, the display did not).
                var c0 = LocalPlayerBody.GetCustomizer();
                if (c0 != null)
                    for (int i = 0; i < CoopAppearance.SlotCount; i++)
                    {
                        int n = CoopAppearance.VariantCount(c0, i);
                        if (n > 0) _working[i] = CoopAppearance.NormalizeValue(i, n, _working[i]);
                    }
                _dirty = false;
                _scroll = Vector2.zero;
                _open = true;
                CoopPauseMenu.Hide();   // never leave clickable world-space buttons behind this panel
                CharacterPreviewStudio.Open();
                CharacterPreviewStudio.SetAppearance(_working);
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("[Character] Open failed: " + e.Message); }
        }

        /// <summary>Close without committing. Safe to call at any time, including when already closed.</summary>
        public static void ForceClose()
        {
            if (!_open) return;
            _open = false;
            CharacterPreviewStudio.Close();   // the studio also self-destructs, but do not rely on that
            // Commit on EVERY exit path, not just Done/Escape. The preview has already changed how the
            // player looks locally, so an exit that skipped the commit (the watchdog firing because they
            // resumed, quit to menu, or were dropped from the lobby) would leave them wearing a look their
            // crewmates never heard about and that is gone next launch. Guarded by _dirty, so a look-only
            // visit costs nothing.
            Commit();
            // Hand the player back the parchment we hid on the way in - but ONLY while still in a cursor
            // menu. The watchdog path below fires precisely BECAUSE the player already left one (resumed,
            // quit to title), and re-showing a menu there would strand a parchment over a resumed game.
            try { if (GameState.inCursorMenu) CoopPauseMenu.Reopen(); }
            catch (System.Exception e) { Plugin.Log.LogWarning("[Character] Could not restore the pause menu: " + e.Message); }
        }

        /// <summary>Drive from Plugin.Update. Cheap no-op while closed.</summary>
        public static void Tick()
        {
            if (!_open) return;
            try
            {
                // The watchdog that makes a stranded panel impossible: if we are no longer in a cursor
                // menu, whatever took us out of it (resume, quit, a vanilla screen) owns the cursor now.
                if (!GameState.inCursorMenu) { ForceClose(); return; }

                // ALL THREE vanilla pause keys, not just Escape. Vanilla's StartMenu.LateUpdate also opens
                // on F10 and JoystickButton6, and the mod's interception in MenuPatches is armed by
                // CoopPauseMenu.IsOpen - which is FALSE while this screen is up, because opening it hid
                // the parchment. So an unhandled pause key fell straight through to vanilla, which saw no
                // active panel of its own (ours hidden, its settingsUI disabled by us) and re-entered
                // GameToSettings WHILE ALREADY PAUSED. That latches unpausedTimescale = Time.timeScale =
                // 0, and every later resume then writes 0 back: a permanent frozen world, unrecoverable
                // without restarting the game.
                if (Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.F10) ||
                    Input.GetKeyDown(KeyCode.JoystickButton6))
                {
                    _keyConsumedFrame = Time.frameCount;
                    ForceClose();   // commits; see ForceClose
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("[Character] Tick failed, closing: " + e.Message);
                ForceClose();
            }
        }

        private static void Commit()
        {
            if (!_dirty) return;
            _dirty = false;
            Plugin.SetLocalAppearance(_working);
            // Our own third-person body bakes its look at build time, so it has to be rebuilt to show the
            // change; the same is true for the copy every crewmate holds of us, hence the broadcast.
            SailwindCoop.Player.LocalPlayerBody.ApplyAppearanceLive();
            AppearanceSync.BroadcastLocal();
            Plugin.Log.LogInfo("[Character] Appearance committed: " + _working.Serialize());
        }

        private static void BuildStyles()
        {
            // Sailwind's own parchment palette and typeface - see SailwindSkin for where the values come
            // from and why the font is usable at all (the game has no TextMeshPro, so its real
            // UnityEngine.Font assets can be assigned straight onto a GUIStyle).
            _panelTex = SailwindSkin.SolidTexture(SailwindSkin.Parchment);
            _titleTex = SailwindSkin.SolidTexture(SailwindSkin.ParchmentDark);

            _panel = new GUIStyle(GUI.skin.box) { padding = new RectOffset(18, 18, 14, 14), alignment = TextAnchor.UpperLeft };
            _panel.normal.background = _panelTex;

            _title = new GUIStyle(GUI.skin.label)
            {
                fontSize = 24, fontStyle = FontStyle.Bold, wordWrap = true,
                alignment = TextAnchor.MiddleLeft, padding = new RectOffset(12, 12, 8, 8),
            }.WithFont();
            _title.normal.textColor = SailwindSkin.Parchment;   // light on the dark title band
            _title.normal.background = _titleTex;

            _label = new GUIStyle(GUI.skin.label)
            {
                fontSize = 19, wordWrap = false, padding = new RectOffset(4, 4, 6, 6),
            }.WithFont();
            _label.normal.textColor = SailwindSkin.InkColor;

            _small = new GUIStyle(_label) { fontSize = 16, fontStyle = FontStyle.Italic, wordWrap = true };
            _small.normal.textColor = SailwindSkin.InkFaint;

            _button = new GUIStyle(GUI.skin.button)
            {
                fontSize = 19, padding = new RectOffset(10, 10, 5, 5),
            }.WithFont();
            _button.normal.textColor = SailwindSkin.InkColor;
            _button.hover.textColor = SailwindSkin.ParchmentDark;

            _stylesBuilt = true;
        }

        /// <summary>Draw. Called from a MonoBehaviour OnGUI (see CoopMessagePanel's host object).</summary>
        public static void Draw()
        {
            if (!_open) return;
            if (!_stylesBuilt) BuildStyles();

            // The live customizer on our own body is the only honest source of "how many variants does
            // THIS machine have". No template yet (out at sea, nothing cloned) means no counts, and the
            // screen says so rather than offering choices that would silently do nothing.
            var customizer = LocalPlayerBody.GetCustomizer();

            float w = Mathf.Min(Screen.width * 0.44f, 640f);
            float h = Mathf.Min(Screen.height * 0.8f, 660f);
            float x = Screen.width * 0.06f;
            float y = (Screen.height - h) * 0.5f;

            GUI.depth = 0;

            // The character, to the RIGHT of the controls. This is the studio mannequin, not the player's
            // real body - see CharacterPreviewStudio for why the real one cannot be used (its renderers are
            // disabled by LocalPlayerBody while this screen is open, and in co-op the world is not paused).
            var preview = CharacterPreviewStudio.Texture;
            if (preview != null)
            {
                float pw = Mathf.Min(Screen.width * 0.26f, 380f);
                float ph = pw * ((float)preview.height / preview.width);
                if (ph > h) { ph = h; pw = ph * ((float)preview.width / preview.height); }
                float px = x + w + Screen.width * 0.03f;
                float py = y + (h - ph) * 0.5f;

                GUI.Box(new Rect(px - 8f, py - 8f, pw + 16f, ph + 16f), GUIContent.none, _panel);
                GUI.DrawTexture(new Rect(px, py, pw, ph), preview, ScaleMode.ScaleToFit, false);

                // Turn controls, so a hat can be judged from behind as well as in front.
                float by = py + ph + 12f;
                if (GUI.Button(new Rect(px, by, pw * 0.48f, 30f), "< Turn", _button)) CharacterPreviewStudio.Turn(-25f);
                if (GUI.Button(new Rect(px + pw * 0.52f, by, pw * 0.48f, 30f), "Turn >", _button)) CharacterPreviewStudio.Turn(25f);
            }

            GUILayout.BeginArea(new Rect(x, y, w, h), _panel);
            GUILayout.Label("Character", _title);
            GUILayout.Space(8f);

            if (customizer == null)
            {
                GUILayout.Label("Your character model has not loaded yet. This needs a shopkeeper somewhere " +
                    "in the loaded world to build from, so it may be unavailable far out at sea. Come back " +
                    "within sight of a port.", _small);
            }
            else
            {
                // Say something the player cannot see for themselves. The preview panel to the right makes
                // "your changes show up" self-evident; that your CREW sees this is the non-obvious part.
                GUILayout.Label(Plugin.IsMultiplayer
                    ? "Your crew sees this character too."
                    : "Your crew will see this character when you sail together.", _small);
                GUILayout.Space(6f);
                _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));

                for (int i = 0; i < CoopAppearance.SlotCount; i++)
                {
                    int count = CoopAppearance.VariantCount(customizer, i);
                    if (count <= 1) continue;   // nothing to choose between on this rig; do not show a dead row
                    // Vanilla has no female facial hair, so the row would be a control that visibly does
                    // nothing. Hide it rather than offer a lie.
                    if (CoopAppearance.Slots[i].Key == "facialhair" && _working[0] == 2) continue;

                    GUILayout.BeginHorizontal();
                    GUILayout.Label(CoopAppearance.Slots[i].Label, _label, GUILayout.Width(130f));

                    if (GUILayout.Button("<", _button, GUILayout.Width(34f))) Step(i, -1, count);
                    GUILayout.Label(Describe(i, count), _label, GUILayout.Width(90f));
                    if (GUILayout.Button(">", _button, GUILayout.Width(34f))) Step(i, +1, count);

                    GUILayout.FlexibleSpace();
                    GUILayout.EndHorizontal();
                }

                GUILayout.EndScrollView();
            }

            GUILayout.Space(8f);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Randomize", _button, GUILayout.Width(120f)) && customizer != null)
            {
                // Draw each slot from its REAL range on this rig. The previous version reused
                // DeterministicFor, which is seeded for SteamId spread and emits 0..250 - values that Apply
                // wraps into range on the body but that the readout showed raw, hence rows reading
                // "241 / 23". Randomising within the live count keeps the number on screen equal to the
                // variant actually being worn. Slot 0 (gender) is deliberately left alone.
                for (int i = 1; i < CoopAppearance.SlotCount; i++)
                {
                    int n = CoopAppearance.VariantCount(customizer, i);
                    // NormalizeValue keeps each slot inside its OWN legal range, so only the slots that
                    // actually allow it can come out empty - a randomized character is never headless.
                    if (n > 0) _working[i] = CoopAppearance.NormalizeValue(i, n, Random.Range(0, n + 1));
                }
                _dirty = true;
                Preview();
            }
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Done  (Esc)", _button, GUILayout.Width(140f)))
            {
                Commit();
                ForceClose();
            }
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        private static string Describe(int slot, int count)
        {
            if (CoopAppearance.Slots[slot].Key == "gender")
                return _working[slot] == 2 ? "Female" : "Male";
            // 0 is "wear nothing" - vanilla's -1, which a byte cannot carry - and is offered ONLY on the
            // slots where it makes sense (Hat, Hair). On anatomy it deletes the body part: None on Torso
            // left a floating head, None on Head made the player disappear outright.
            int v = _working[slot];
            return v == 0 ? "None" : v + " / " + count;
        }

        private static void Step(int slot, int dir, int count)
        {
            _working[slot] = CoopAppearance.NormalizeValue(slot, count, _working[slot] + dir);
            _dirty = true;
            Preview();
        }

        /// <summary>
        /// Show the edit immediately on the player's own body. This rebuilds the body clone, because a look
        /// is baked at activation and cannot be changed on a live one - which is why the preview is driven
        /// per click rather than per frame.
        /// </summary>
        private static void Preview()
        {
            try
            {
                // VISUAL ONLY - deliberately does not touch the config. BepInEx saves a ConfigEntry to disk
                // on assignment, so persisting here would mean a file write on every single arrow click.
                var c = LocalPlayerBody.GetCustomizer();
                if (c != null) _working.ApplyLive(c);
                // The studio mannequin is what the player is actually looking at while the screen is open;
                // the real body may well have its renderers switched off by LocalPlayerBody right now.
                CharacterPreviewStudio.SetAppearance(_working);
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("[Character] Preview failed: " + e.Message); }
        }
    }
}
