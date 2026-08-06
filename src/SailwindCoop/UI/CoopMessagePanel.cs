using System.Collections.Generic;
using UnityEngine;

namespace SailwindCoop.UI
{
    /// <summary>
    /// (v0.3.0) A plain, readable IMGUI panel for messages that are too important or too long for the
    /// vanilla notification scroll.
    ///
    /// WHY THIS EXISTS. Co-op's two most information-dense messages - "your join was refused because these
    /// mods differ" and "these mods differ but you are in anyway" - were both being pushed through
    /// Plugin.Notify, i.e. the game's diegetic notification ticker. Field report, verbatim: "it's a fucking
    /// horrible screen because you can't even read it, it all runs off the screen and it's black text with no
    /// background except for the notification scroll at the top which is miniscule in comparison". That is an
    /// accurate description of the failure: the ticker is sized for "Aboard the host's ship!", not for a list
    /// of six mods with version numbers, and it clips rather than wraps.
    ///
    /// These messages are also the ONE case where the player is stuck and needs to act (install a mod, change
    /// a version, edit a setting), so they are exactly the wrong thing to render as ambient flavour. A plain
    /// grey box with wrapped dark-on-light text is not pretty, and that is the point - it should read as the
    /// game telling you something went wrong, not as part of the world.
    ///
    /// Deliberately NOT modal and NOT pausing: a refused guest is being quit by other code on a timer, and
    /// blocking input during that would be worse than the problem. It draws on top, wraps, scrolls when long,
    /// and can be dismissed.
    /// </summary>
    public class CoopMessagePanel : MonoBehaviour
    {
        private static CoopMessagePanel _instance;

        private string _title;
        private readonly List<string> _lines = new List<string>();
        private string _footer;
        private bool _visible;
        private Vector2 _scroll;
        private bool _stylesBuilt;
        private float _autoHideAt;                  // realtime deadline, 0 = never
        private bool _stickyThroughTeardown;        // refusals survive the teardown that triggered them
        private CursorLockMode _prevLockState;
        private bool _prevCursorVisible;
        private bool _prevInCursorMenu;
        private bool _cursorCaptured;

        private GUIStyle _panelStyle, _titleStyle, _bodyStyle, _footerStyle, _buttonStyle;
        private Texture2D _panelTex, _titleTex;

        /// <summary>
        /// Show the panel. Safe to call from anywhere at any time: it self-instantiates on a
        /// DontDestroyOnLoad object, so it does not depend on Plugin's init order or survive-scene rules.
        /// Never throws - a UI failure must not take down a join path.
        /// </summary>
        /// <param name="autoHideSeconds">
        /// 0 = stays until dismissed, for the REFUSAL cases where the player is stuck and must act.
        /// Anything above 0 auto-expires, which is what informational shows must use - otherwise a purely
        /// cosmetic mod difference leaves a grey box over the horizon for the rest of the voyage.
        /// </param>
        /// <param name="stickyThroughTeardown">
        /// Whether the message must survive the lobby teardown happening in the same call stack. Defaults to
        /// "yes if it has no auto-hide", which is right for refusals - they are shown BECAUSE the session is
        /// ending. Pass false for a message that is only meaningful inside a session the player is still in,
        /// so leaving the lobby takes it away instead of carrying it into singleplayer.
        /// </param>
        public static void Show(string title, IEnumerable<string> lines, string footer = null,
            float autoHideSeconds = 0f, bool? stickyThroughTeardown = null)
        {
            try
            {
                if (_instance == null)
                {
                    var go = new GameObject("SailwindCoop_MessagePanel");
                    DontDestroyOnLoad(go);
                    _instance = go.AddComponent<CoopMessagePanel>();
                }
                _instance.SetContent(title, lines, footer, autoHideSeconds, stickyThroughTeardown);
            }
            catch (System.Exception e)
            {
                // Fall back to the ticker rather than losing the message entirely. Worse presentation beats
                // no information, which is the state this class exists to end.
                Plugin.Log.LogWarning("[UI] Message panel failed, falling back to notification: " + e.Message);
                Plugin.Notify(title, 12f);
            }
        }

        /// <summary>
        /// Hide a TRANSIENT message on lobby teardown, so a co-op notice cannot follow the player into
        /// singleplayer.
        ///
        /// DELIBERATELY SKIPS REFUSALS, and that exception is the whole reason this method has a body worth
        /// reading. A refusal panel is shown precisely BECAUSE the join is being rejected, and every
        /// rejection path tears the lobby down in the same call stack - Show(...) is followed within a
        /// statement or two by LobbyManager.LeaveLobby(), which invokes OnLobbyLeft SYNCHRONOUSLY, which
        /// lands here. An unconditional Dismiss therefore destroyed the message before a single OnGUI pass
        /// could draw it: the panel was created and killed inside one frame, and what the player actually
        /// saw was the clipped notification ticker this class exists to replace. The guest-quit path gives
        /// them a 6-second window before Application.Quit, and this is what fills it.
        ///
        /// Refusals stay up until the player dismisses them with Escape or Close, which is exactly what
        /// "autoHideSeconds = 0" is documented on Show() to mean.
        /// </summary>
        public static void Hide()
        {
            if (_instance != null && !_instance._stickyThroughTeardown) _instance.Dismiss();
        }

        /// (v0.3.0) Whether a message is on screen right now. The guest-quit path polls this so it can wait
        /// for the player to finish reading a refusal instead of closing the game out from under them on a
        /// fixed timer - a refusal listing several mods is more than six seconds of reading, and having it
        /// vanish into a desktop mid-sentence reads as a crash.
        public static bool IsShowing
        {
            get { return _instance != null && _instance._visible; }
        }

        private int _keyConsumedFrame = -1;

        /// <summary>(v0.3.0) True if this panel swallowed a pause key this frame, so the same press does not
        /// also open the vanilla pause menu behind it. Same contract as CharacterScreen/FriendsScreen.</summary>
        public static bool ConsumedPauseKeyThisFrame
        {
            get { return _instance != null && _instance._keyConsumedFrame == Time.frameCount; }
        }

        private void SetContent(string title, IEnumerable<string> lines, string footer, float autoHideSeconds,
            bool? stickyThroughTeardown)
        {
            _title = title ?? "Co-op";
            _lines.Clear();
            if (lines != null) foreach (var l in lines) if (!string.IsNullOrEmpty(l)) _lines.Add(l);
            _footer = footer;
            _scroll = Vector2.zero;
            _autoHideAt = autoHideSeconds > 0f ? Time.realtimeSinceStartup + autoHideSeconds : 0f;
            // By default, no auto-hide means this is a REFUSAL - the player is stuck and has to act - so it
            // must outlive the lobby teardown happening in this very call stack. See Hide(). Callers can
            // override: a message that stays until clicked but is only about a session the player is still
            // IN should not follow them out of it.
            _stickyThroughTeardown = stickyThroughTeardown ?? (autoHideSeconds <= 0f);

            // TAKE THE CURSOR. Sailwind's gameplay state is cursor-LOCKED and cursor-HIDDEN
            // (MouseLook.ToggleMouseLookAndCursor sets lockState=Locked, visible=false on every entry to
            // gameplay). With the cursor locked the IMGUI pointer is pinned near screen centre and invisible,
            // so a Close button cannot be aimed at - the panel would be permanently welded to the screen, and
            // being DontDestroyOnLoad it would follow the player back into singleplayer. That is strictly
            // worse than the unreadable ticker this class replaced.
            if (!_visible)
            {
                _prevLockState = Cursor.lockState;
                _prevCursorVisible = Cursor.visible;
                try { _prevInCursorMenu = GameState.inCursorMenu; } catch { _prevInCursorMenu = true; }
                _cursorCaptured = true;
            }

            // (v0.3.0) STOP THE CAMERA TOO, not just the cursor. Freeing the cursor alone left the player
            // dragging the view around behind the panel: MouseLook.Update turns the camera whenever
            // `mouseLookEnabled && !cursorEnabled` (MouseLook.cs:66), and neither of those is touched by
            // writing Cursor.lockState. Calling vanilla's own ToggleMouseLookAndCursor(false) sets
            // cursorEnabled and GameState.inCursorMenu together, which is exactly the state every vanilla
            // menu puts the game into, so the world stops responding to the mouse the way a player expects.
            try { MouseLook.ToggleMouseLookAndCursor(false); } catch { /* title screen, or no MouseLook yet */ }
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            _visible = true;
        }

        /// <summary>Hide and hand the cursor back exactly as we found it. Idempotent.</summary>
        private void Dismiss()
        {
            _visible = false;
            _stickyThroughTeardown = false;
            RestoreCursor();
        }

        /// <summary>
        /// Hand the cursor back - but ONLY if nothing else has claimed it since we took it.
        ///
        /// The snapshot can go stale while the panel is up. This object is DontDestroyOnLoad, and
        /// Sailwind's title menu and the world are the SAME scene (StartMenu just moves the observer), so a
        /// panel opened over a menu can still be on screen after the game has locked and hidden the cursor
        /// for mouse-look. Writing back a stale "unlocked and visible" snapshot on top of that hands the
        /// player a free OS cursor while the camera still turns with the mouse - vanilla only re-asserts
        /// cursor state when a menu next opens or closes, so it does not heal on its own.
        ///
        /// Testing for the values WE forced is the cheap, honest check: if they still hold, we are the last
        /// writer and the restore is correct; if they do not, someone else owns the cursor now and the kind
        /// thing to do is nothing.
        /// </summary>
        private void RestoreCursor()
        {
            if (!_cursorCaptured) return;
            _cursorCaptured = false;
            if (Cursor.lockState != CursorLockMode.None || !Cursor.visible) return; // someone else took it

            // (v0.3.0) If we interrupted GAMEPLAY (the game was not already in a cursor menu), hand look and
            // cursor back through the same vanilla call we took them with, so mouseLookEnabled/cursorEnabled
            // and GameState.inCursorMenu all end up consistent. Writing the raw Cursor fields back would
            // leave cursorEnabled set and the camera dead. If we opened over a MENU, do the opposite and
            // leave the menu's free cursor alone - re-locking there would be the bug in reverse.
            if (!_prevInCursorMenu)
            {
                try { MouseLook.ToggleMouseLookAndCursor(true); return; } catch { /* fall through */ }
            }
            Cursor.lockState = _prevLockState;
            Cursor.visible = _prevCursorVisible;
            try { GameState.inCursorMenu = _prevInCursorMenu; } catch { }
        }

        private void Update()
        {
            if (!_visible) return;
            // Keyboard dismissal, because a locked-cursor player may have no usable pointer at all.
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                // (v0.3.0) Claim the press, or the SAME Escape that closed this panel also reaches vanilla
                // and opens the pause menu behind it - which is what a player saw after dismissing a
                // refusal: the message goes away and a pause parchment appears for no reason, on a session
                // that is in the middle of ending. The other two screens already do this; this one did not,
                // and MenuPatches only knew to ask them.
                _keyConsumedFrame = Time.frameCount;
                Dismiss();
                return;
            }
            // Informational panels expire on their own; refusals (autoHide 0) stay until dismissed.
            if (_autoHideAt > 0f && Time.realtimeSinceStartup >= _autoHideAt) Dismiss();
        }

        private void OnDestroy()
        {
            // Never leave the game cursor-unlocked because our object went away.
            RestoreCursor();
        }

        private void BuildStyles()
        {
            _panelTex = MakeTex(new Color(0.86f, 0.86f, 0.84f, 0.97f));
            _titleTex = MakeTex(new Color(0.72f, 0.72f, 0.70f, 1f));

            _panelStyle = new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(18, 18, 14, 14),
                alignment = TextAnchor.UpperLeft,
            };
            _panelStyle.normal.background = _panelTex;

            _titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 20,
                fontStyle = FontStyle.Bold,
                wordWrap = true,
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(10, 10, 6, 6),
            };
            _titleStyle.normal.textColor = new Color(0.08f, 0.08f, 0.10f);
            _titleStyle.normal.background = _titleTex;

            // wordWrap is the entire fix for "it all runs off the screen".
            _bodyStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 15,
                wordWrap = true,
                richText = false,
                padding = new RectOffset(4, 4, 3, 3),
            };
            _bodyStyle.normal.textColor = new Color(0.10f, 0.10f, 0.12f);

            _footerStyle = new GUIStyle(_bodyStyle) { fontStyle = FontStyle.Italic };
            _footerStyle.normal.textColor = new Color(0.30f, 0.30f, 0.34f);

            _buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = 15, padding = new RectOffset(14, 14, 6, 6) };

            _stylesBuilt = true;
        }

        /// <summary>
        /// (v0.3.0) Height the body needs at a given width, INCLUDING the vertical margin GUILayout puts
        /// between labels. CalcHeight alone reports only the text box, which is what left the old estimate
        /// short by a few pixels per line and forced a scrollbar onto a two-line message.
        /// </summary>
        private float MeasureBody(float width)
        {
            float h = 0f;
            for (int i = 0; i < _lines.Count; i++)
                h += _bodyStyle.CalcHeight(new GUIContent("- " + _lines[i]), width) + _bodyStyle.margin.vertical;
            if (!string.IsNullOrEmpty(_footer))
                h += 8f + _footerStyle.CalcHeight(new GUIContent(_footer), width) + _footerStyle.margin.vertical;
            return h;
        }

        private static Texture2D MakeTex(Color c)
        {
            var t = new Texture2D(1, 1);
            t.SetPixel(0, 0, c);
            t.Apply();
            t.hideFlags = HideFlags.HideAndDontSave;
            return t;
        }

        private void OnGUI()
        {
            if (!_visible) return;
            if (!_stylesBuilt) BuildStyles();

            // Sized as a fraction of the screen, capped, so it is readable at 1080p and not absurd at 4K.
            float w = Mathf.Min(Screen.width * 0.62f, 900f);
            float maxH = Screen.height * 0.8f;
            float x = (Screen.width - w) * 0.5f;
            float y = Screen.height * 0.14f;

            // (v0.3.0) MEASURE PROPERLY AND ONLY SCROLL WHEN WE MUST. The previous version guessed the
            // chrome at a flat "44 + 66", ignored the per-label margins GUILayout inserts, and then wrapped
            // the body in a scroll view unconditionally. A one-line message therefore came out a few pixels
            // short of its own contents and grew a scrollbar to reach the footer - reported, fairly, as
            // silly when the box could simply have been drawn tall enough. Worse, once a vertical scrollbar
            // appears it eats horizontal space, which re-wraps the text longer, which needs more height.
            //
            // So: derive every band from the styles themselves, and drop the scroll view entirely unless the
            // content genuinely exceeds the screen cap. A small overestimate here is the right error - it
            // spends empty parchment, which there is plenty of, instead of clipping the text.
            float innerW = w - _panelStyle.padding.horizontal;
            float scrollbarW = 20f;

            float titleH = _titleStyle.CalcHeight(new GUIContent(_title), innerW);
            float buttonH = _buttonStyle.CalcHeight(new GUIContent("Close  (Esc)"), 140f);
            float fixedH = _panelStyle.padding.vertical + titleH + 8f + 6f + buttonH + 6f;

            float bodyH = MeasureBody(innerW);
            float wantedH = fixedH + bodyH;
            bool needsScroll = wantedH > maxH;
            // Re-measure narrower when a scrollbar will steal width, so the cap is honest about what fits.
            if (needsScroll) bodyH = MeasureBody(innerW - scrollbarW);
            float h = needsScroll ? maxH : wantedH;

            GUI.depth = 0;
            GUILayout.BeginArea(new Rect(x, y, w, h), _panelStyle);

            GUILayout.Label(_title, _titleStyle);
            GUILayout.Space(8f);

            if (needsScroll) _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
            for (int i = 0; i < _lines.Count; i++)
                GUILayout.Label("- " + _lines[i], _bodyStyle);
            if (!string.IsNullOrEmpty(_footer))
            {
                GUILayout.Space(8f);
                GUILayout.Label(_footer, _footerStyle);
            }
            if (needsScroll) GUILayout.EndScrollView();
            else GUILayout.FlexibleSpace();

            GUILayout.Space(6f);
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Close  (Esc)", _buttonStyle, GUILayout.Width(140f))) Dismiss();
            GUILayout.EndHorizontal();

            GUILayout.EndArea();
        }
    }
}
