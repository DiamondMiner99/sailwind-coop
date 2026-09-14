using UnityEngine;

namespace SailwindCoop.UI
{
    /// <summary>
    /// The screen a guest sees while joining, covering the stretch where the join has taken their body
    /// away from them.
    ///
    /// WHAT IT REPLACES. A joining guest used to watch their own world load, then stand frozen on a perch
    /// 50m above the sea for anything from three seconds to half a minute while terrain streamed in, then
    /// get snapped onto a boat. All of that is working as designed and all of it reads as a hang. This is a
    /// blinder over it, with an honest progress bar.
    ///
    /// WHERE IT STARTS, AND WHY NOT EARLIER. Tracking begins the moment the guest enters the lobby, but the
    /// BLACKOUT does not go up until Step.Placing, which is the first moment BoatStateApplicator has taken
    /// player control away. Blacking out any earlier means a player who is still fully mobile standing
    /// behind an opaque screen: the wait for the host's snapshot is sanctioned to run up to 45 seconds, and
    /// a guest holding W through that would walk blind off their own pier and drown. Covering only the
    /// window where control is already gone gets the same blinder with none of that, and needs no
    /// control-restore path of its own to go wrong.
    ///
    /// PROGRESS IS MEASURED, NOT TIMED. The join is a sequence of named steps (STEP 1-7 in
    /// BoatStateApplicator), so the bar advances when a step genuinely completes. The CHECKPOINTS are real;
    /// the motion between them need not be. Each step owns a slice weighted by what it typically costs, and
    /// terrain gets most of the bar because it is most of the wait. On reaching a step the bar SNAPS to that
    /// step's floor, then eases toward the next while asymptoting short of it, so a stalled step creeps and
    /// slows instead of freezing. It never runs backwards and never claims to be finished early.
    ///
    /// EVERYTHING HERE RUNS ON UNSCALED TIME. This is not a preference. The co-op world replicates the
    /// HOST's timeScale onto the guest, so a host who is paused, at a port, or asleep runs this guest at
    /// timeScale 0 - and that is a completely normal moment to join. Vanilla's own Blackout.FadeTo lerps on
    /// scaled Time.deltaTime, which is why BoatStateApplicator's perch comment rejected it: at timeScale 0
    /// that fade never completes and leaves the player staring at a permanently black screen, which is
    /// strictly worse than the fall it was hiding. Same reasoning applies to every timer below.
    /// </summary>
    public static class JoinProgressScreen
    {
        /// <summary>The join's real checkpoints, in order.</summary>
        public enum Step
        {
            WaitingForWorld,   // in the lobby, waiting for the host's snapshot
            Placing,           // STEP 1-2: destination known, teleported to the perch
            LoadingTerrain,    // STEP 3: waiting for islands to stream in
            ApplyingBoats,     // STEP 4: applying the host's boat states and spawning their items
            ApplyingWorld,     // STEP 5: current boat, wind, weather
            Aboard,            // STEP 6-7: final placement
        }

        /// <summary>
        /// Slice floors. A step eases toward the NEXT step's floor without reaching it.
        ///
        /// WEIGHTED FROM REAL JOINS, not from guesswork. Two measured, one 2-player and one 3-player:
        ///
        ///   waiting for snapshot   4.8s (68%)   14.2s (82%)
        ///   placing                0.0s          0.0s
        ///   loading terrain        2.0s          0.0s   (already resident on the rejoin)
        ///   applying boats         0.3s          2.0s
        ///   applying world         0.0s          1.1s
        ///   TOTAL                  7.1s         17.3s
        ///
        /// The snapshot wait dominates in both, so it owns most of the bar. An earlier cut of this file
        /// gave it 40% and the bar crawled to a third over fourteen seconds and then did the rest in
        /// three. Terrain keeps a real slice regardless of measuring 0.0s on a rejoin, because it is
        /// bounded by a 30s timeout and dominates on a cold world or a slower disk.
        /// </summary>
        private static readonly float[] StepFloor = { 0.00f, 0.60f, 0.63f, 0.88f, 0.95f, 0.98f };
        private const float Done = 1.00f;

        private static readonly string[] StepLabel =
        {
            "Waiting for the captain's world",
            "Finding the ship",
            "Loading the islands",
            "Placing the boats",
            "Setting the scene",
            "Bringing you aboard",
        };

        /// <summary>
        /// How long a step may run before the screen says so out loud. Not a timeout - nothing is aborted
        /// here - purely the point at which silence starts to mislead.
        ///
        /// Each of these must sit BELOW its step's real ceiling or the message can never fire. Terrain is
        /// the one that matters: its step is a 2s realtime wait plus a 30s timeout, so ~32s is the most it
        /// can ever last, and a 35s patience would have made the one stall worth reporting unreportable.
        /// </summary>
        private static readonly float[] StepPatience = { 20f, 6f, 12f, 15f, 6f, 6f };

        /// <summary>
        /// Coming OUT is a fade, because by then there is a world worth easing into. Going in is a cut:
        /// see Begin.
        /// </summary>
        private const float FadeOutSeconds = 0.85f;

        /// <summary>
        /// Ease rate, per second. Deliberately slow. At 1.1 a slice was 96% consumed within three seconds,
        /// so the bar was visually dead for the remaining tens of seconds of every long step, which is the
        /// opposite of what an asymptotic ease is for.
        /// </summary>
        private const float EaseRate = 0.18f;

        /// <summary>
        /// Rate at which the bar CATCHES UP to a checkpoint it is behind, as opposed to the slow creep
        /// within a slice. Steps that complete instantly (three of the six measured at 0.0s) used to snap
        /// the bar from one floor to the next in a single frame, which read as a series of jumps rather
        /// than progress. At this rate the gap closes in roughly half a second, so a completed checkpoint
        /// looks like the bar accelerating into it.
        /// </summary>
        private const float CatchUpRate = 6f;

        /// <summary>Clamp on a single frame's delta before easing. One long hitch (a scene load can stall a
        /// frame for seconds, and unscaledDeltaTime is not bounded by Time.maximumDeltaTime) would
        /// otherwise swallow an entire slice in one step.</summary>
        private const float MaxEaseDelta = 0.1f;

        /// <summary>
        /// Backstop, measured PER STEP and re-based every time a real checkpoint lands.
        ///
        /// A whole-join budget was wrong: the join's own sanctioned worst case is already a 45s snapshot
        /// wait plus a 2s settle plus a 30s terrain timeout plus however long applying a large world takes,
        /// so any single number big enough to be safe was too big to be useful, and any number small enough
        /// to be useful would cut off a join that was merely slow. Per-step fires only on a step that has
        /// genuinely stopped, which is the thing worth catching.
        /// </summary>
        private const float StepHardCapSeconds = 75f;

        private static bool _active;
        private static bool _fadingOut;
        private static Step _step;
        private static float _displayed;      // 0..1, what the bar is showing
        private static float _fade;           // 0..1 blackout alpha
        private static float _beganAt;        // unscaled
        private static float _stepBeganAt;    // unscaled
        private static float _fadeOutBeganAt;
        private static int _terrainPeak;

        private static Texture2D _white, _barBg, _barFill;
        private static GUIStyle _title, _label, _small;
        private static bool _stylesBuilt;

        public static bool IsActive { get { return _active; } }

        private static bool Visible { get { return _active; } }

        /// <summary>Whether WE took the player's controls away, so only we hand them back.</summary>
        private static bool _frozeControl;

        /// <summary>
        /// Freeze the guest for the blacked-out wait.
        ///
        /// REQUIRED BY THE BLACKOUT, not a nicety. The screen goes up the moment the guest enters the
        /// lobby, but nothing in the join takes control away until STEP 2, which cannot run until the
        /// host's snapshot arrives - measured at 4.8s on a local join and sanctioned to run up to 45s.
        /// Without this, a player holding W would walk blind off their own pier and drown behind an opaque
        /// screen. Paired with RestoreControl in the same file so the two cannot drift apart.
        ///
        /// Guarded because Refs.SetPlayerControl dereferences charController/ovrController directly
        /// (vanilla Refs.cs), which are not guaranteed to exist on every path that can reach a lobby join.
        /// </summary>
        private static void FreezeControl()
        {
            if (_frozeControl) return;
            try
            {
                Refs.SetPlayerControl(false);
                MouseLook.ToggleMouseLook(false);
                _frozeControl = true;
            }
            catch (System.Exception e)
            {
                // Fail open: no freeze is survivable, a half-applied one is not.
                _frozeControl = false;
                Plugin.Log.LogWarning("[JoinScreen] Could not freeze the player for the join: " + e.Message);
            }
        }

        /// <summary>
        /// Hand control back. Idempotent, and idempotent with the applicator's own STEP 7 / finally
        /// restores, which are unconditional - SetPlayerControl just sets two enabled flags.
        /// </summary>
        private static void RestoreControl()
        {
            if (!_frozeControl) return;
            _frozeControl = false;
            try
            {
                Refs.SetPlayerControl(true);
                MouseLook.ToggleMouseLook(true);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("[JoinScreen] Could not hand control back after the join: " + e.Message);
            }
        }

        /// <summary>
        /// The player has committed to a join. Raises the blackout immediately and freezes them behind it.
        ///
        /// Called from SteamLobbyManager.JoinLobby, BEFORE the round trip to Steam, and again from
        /// OnLobbyJoined as a fallback. The second call is a no-op while a join screen is already running,
        /// so the bar and the clocks are not rewound half a second into the join.
        ///
        /// No fade in. Any fade here shows the player the world this screen exists to replace, which is the
        /// whole complaint it is answering.
        /// </summary>
        public static void Begin()
        {
            try
            {
                if (_active && !_fadingOut) return;   // already running; do not restart the clocks
                _active = true;
                _fadingOut = false;
                _step = Step.WaitingForWorld;
                _displayed = 0f;
                _beganAt = Time.unscaledTime;
                _stepBeganAt = Time.unscaledTime;
                _terrainPeak = 0;
                _fade = 1f;
                FreezeControl();
                Plugin.Log.LogInfo("[JoinScreen] Join started - waiting for the host's world state.");
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("[JoinScreen] Begin failed: " + e.Message); }
        }

        /// <summary>
        /// A second genuine join snapshot replaced the one being applied. Re-arms the per-step clocks and
        /// lets the step move backwards once, WITHOUT rewinding the bar. Without this the screen keeps the
        /// abandoned attempt's label and patience clock, so it can sit at "Placing the boats" and 75% while
        /// the replacement coroutine is actually back at the start of a fresh 30s terrain wait, and then
        /// report a stall time accumulated across both attempts.
        /// </summary>
        public static void Restart()
        {
            try
            {
                if (!_active || _fadingOut) return;
                _step = Step.WaitingForWorld;
                _stepBeganAt = Time.unscaledTime;
                _terrainPeak = 0;
                Plugin.Log.LogInfo("[JoinScreen] A replacement join snapshot arrived; re-armed for the new attempt.");
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("[JoinScreen] Restart failed: " + e.Message); }
        }

        /// <summary>
        /// A real checkpoint completed. Snaps the bar to that step's floor and never moves the BAR
        /// backwards, so a repeated call cannot rewind it.
        /// </summary>
        public static void SetStep(Step step)
        {
            try
            {
                if (!_active || _fadingOut) return;
                bool changed = step != _step;
                _step = step;
                // Deliberately does NOT move the bar. A checkpoint RETARGETS it; Advance then accelerates
                // toward the new floor and settles into the slow creep beyond it. Snapping _displayed here
                // is what made the bar teleport between stages instead of speeding up into them.
                // Raise the blackout HERE, not on the next Tick. Tick runs in Update, but this is called
                // from the apply coroutine, which resumes AFTER Update - so waiting for Tick would leave
                // the screen clear for the one frame that contains STEP 2's teleport onto the perch, which
                // is precisely the frame worth hiding.
                if (Visible && !_fadingOut) _fade = 1f;
                if (changed)
                {
                    float held = Time.unscaledTime - _stepBeganAt;
                    _stepBeganAt = Time.unscaledTime;
                    Plugin.Log.LogInfo($"[JoinScreen] {step} (previous step took {held:F1}s, {Time.unscaledTime - _beganAt:F1}s total)");
                }
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("[JoinScreen] SetStep failed: " + e.Message); }
        }

        /// <summary>The join finished. Fills the bar and fades back to the world.</summary>
        public static void Finish()
        {
            try
            {
                if (!_active || _fadingOut) return;
                _fadingOut = true;
                _fadeOutBeganAt = Time.unscaledTime;
                Plugin.Log.LogInfo($"[JoinScreen] Join complete in {Time.unscaledTime - _beganAt:F1}s.");
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("[JoinScreen] Finish failed: " + e.Message); }
        }

        /// <summary>
        /// The join failed or was abandoned. Drops the blackout instantly rather than fading, because
        /// whatever comes next (a refusal panel, a quit notice, a watchdog message) has to be readable
        /// immediately - hiding a failure behind a leisurely fade is the one thing this screen must never
        /// do. Safe to call when nothing is showing, and a no-op once Finish has started, which is what
        /// lets the applicator's finally call it unconditionally on every exit including success.
        /// </summary>
        public static void Abort(string reason)
        {
            try
            {
                if (!_active || _fadingOut) return;
                _active = false;
                _fade = 0f;
                // Whatever went wrong, the player must not be left frozen in the dark with it.
                RestoreControl();
                Plugin.Log.LogWarning($"[JoinScreen] Join screen dropped after {Time.unscaledTime - _beganAt:F1}s at {_step}: {reason}");
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("[JoinScreen] Abort failed: " + e.Message); }
        }

        /// <summary>Drive from Plugin.Update. Cheap no-op while inactive.</summary>
        public static void Tick()
        {
            if (!_active && _fade <= 0f) return;
            try
            {
                float dt = Time.unscaledDeltaTime;

                // THE BACKSTOP. Nothing below this line is allowed to leave a black screen on a player's
                // monitor. If the join machinery ever fails to call Finish or Abort - a path nobody thought
                // of, a coroutine killed mid-flight, an exception upstream - this drops the screen anyway
                // and says so loudly. Only counts once the blackout is actually up: a long wait for the
                // host's snapshot is not a stall to cut off, and nothing is being hidden during it.
                if (Visible && !_fadingOut && Time.unscaledTime - _stepBeganAt > StepHardCapSeconds)
                {
                    Plugin.Log.LogError($"[JoinScreen] Hard cap: {_step} has been on screen for {StepHardCapSeconds:F0}s with no checkpoint. " +
                                        "Dropping the screen so the player can see the game. This is a bug in the join sequence, not in the screen.");
                    Abort("hard cap");
                    return;
                }

                if (Visible && !_fadingOut)
                {
                    _fade = 1f;
                    Advance(dt);
                }
                else if (_fadingOut)
                {
                    // Run the bar home during the fade rather than snapping it to full the instant the join
                    // finishes. The fade lasts long enough for the last stretch to be seen.
                    _displayed = Mathf.Lerp(_displayed, Done, 1f - Mathf.Exp(-CatchUpRate * Mathf.Min(dt, MaxEaseDelta)));
                    float t = (Time.unscaledTime - _fadeOutBeganAt) / FadeOutSeconds;
                    _fade = Mathf.Clamp01(1f - t);
                    if (t >= 1f)
                    {
                        _active = false; _fadingOut = false; _fade = 0f;
                        // Belt and braces. STEP 7 has already handed control back on the success path, and
                        // this is idempotent with it; it only matters if the join somehow completed without
                        // reaching that line.
                        RestoreControl();
                    }
                }
                else
                {
                    // Tracking but not yet visible, or dropped: make sure no residue is left on screen.
                    _fade = Mathf.Max(0f, _fade - dt / FadeOutSeconds);
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("[JoinScreen] Tick failed, dropping the screen: " + e.Message);
                _active = false; _fadingOut = false; _fade = 0f;
            }
        }

        /// <summary>
        /// Move the bar within the current step's slice.
        /// </summary>
        private static void Advance(float dt)
        {
            int i = (int)_step;
            float floor = StepFloor[i];
            float ceiling = (i + 1 < StepFloor.Length) ? StepFloor[i + 1] : Done;

            float step = Mathf.Min(dt, MaxEaseDelta);

            // Asymptotic ease: approaches the next floor without reaching it, which is what keeps the bar
            // off 100% until a real checkpoint says otherwise. Frame-rate independent, and clamped so one
            // hitched frame cannot consume the whole slice at once.
            float eased = Mathf.Lerp(_displayed, ceiling, 1f - Mathf.Exp(-EaseRate * step));

            // CATCH-UP. The bar is behind a checkpoint that has genuinely landed, so close that gap quickly
            // instead of creeping across it at the within-slice rate. This is what turns a completed step
            // into the bar accelerating rather than teleporting: three of the six steps finish in under a
            // tenth of a second, so without this the bar spent the join snapping between floors.
            if (_displayed < floor)
                eased = Mathf.Max(eased, Mathf.Lerp(_displayed, floor, 1f - Mathf.Exp(-CatchUpRate * step)));

            if (_step == Step.LoadingTerrain)
            {
                // The one step with a genuine fraction behind it - but only sometimes. loadingScenes counts
                // scenes still streaming, and the common join streams exactly ONE, so peak and live are both
                // 1 for the entire wait and the fraction is a flat zero right up until the loop exits. A
                // fraction that cannot discriminate is worse than no fraction, because it pins the longest
                // slice of the bar motionless. So use it only when there is actually a range to measure,
                // and let it PULL the bar forward rather than hold it back.
                int live = 0;
                try { live = Mathf.Max(0, GameState.loadingScenes); } catch { }
                if (live > _terrainPeak) _terrainPeak = live;
                if (_terrainPeak > 1)
                {
                    float frac = Mathf.Clamp01((_terrainPeak - live) / (float)_terrainPeak);
                    eased = Mathf.Max(eased, floor + frac * (ceiling - floor));
                }
            }

            _displayed = Mathf.Clamp(Mathf.Max(_displayed, eased), 0f, Done);
        }

        // --- drawing -------------------------------------------------------------------------------------

        /// <summary>Draw from Plugin.OnGUI, LAST, so it covers this mod's other IMGUI surfaces.</summary>
        public static void Draw()
        {
            if (_fade <= 0.001f) return;

            // Never cover a message panel. That panel is how a refusal, a lost connection or a closed
            // server gets explained, and those can land mid-join. Abort already drops the blackout on
            // every path that raises one, but this is the cheap guarantee that does not depend on having
            // enumerated them all correctly.
            try { if (CoopMessagePanel.IsShowing) return; }
            catch { }

            bool restore = false;
            var prevColor = GUI.color;
            try
            {
                BuildStyles();
                restore = true;

                float w = Screen.width, h = Screen.height;

                GUI.color = new Color(0f, 0f, 0f, _fade);
                GUI.DrawTexture(new Rect(0f, 0f, w, h), _white);
                GUI.color = new Color(1f, 1f, 1f, _fade);

                float barW = Mathf.Min(w * 0.44f, 620f);
                float barH = 10f;
                float cx = (w - barW) * 0.5f;
                float cy = h * 0.5f;

                GUI.Label(new Rect(0f, cy - 96f, w, 40f), "Joining the crew", _title);
                GUI.Label(new Rect(0f, cy - 44f, w, 28f), StepLabel[(int)_step], _label);

                GUI.DrawTexture(new Rect(cx, cy, barW, barH), _barBg);
                GUI.DrawTexture(new Rect(cx, cy, barW * Mathf.Clamp01(_displayed), barH), _barFill);

                // The diagnostic half. Silent while a step behaves; once it overruns what that step
                // normally costs, name the step and keep the clock running, so a stall is visible to the
                // player and reportable to me instead of looking like a finished-but-frozen game.
                if (_active && !_fadingOut)
                {
                    float inStep = Time.unscaledTime - _stepBeganAt;
                    if (inStep > StepPatience[(int)_step])
                    {
                        string note = $"still {StepLabel[(int)_step].ToLowerInvariant()} after {inStep:F0}s "
                                    + $"({Time.unscaledTime - _beganAt:F0}s since you joined)";
                        GUI.Label(new Rect(w * 0.1f, cy + barH + 16f, w * 0.8f, 44f), note, _small);
                    }
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("[JoinScreen] Draw failed, dropping the screen: " + e.Message);
                _active = false; _fadingOut = false; _fade = 0f;
            }
            finally
            {
                // Always, on the throwing path too. Leaving GUI.color tinted would discolor every IMGUI
                // surface drawn after this one for the rest of the session.
                if (restore) GUI.color = prevColor;
            }
        }

        private static void BuildStyles()
        {
            if (_stylesBuilt && _white != null && _barBg != null && _barFill != null && _title != null) return;

            // One white pixel, tinted by GUI.color, does both the blackout and the bar highlight.
            _white = SailwindSkin.SolidTexture(Color.white);
            _barBg = SailwindSkin.SolidTexture(new Color(1f, 1f, 1f, 0.16f));
            _barFill = SailwindSkin.SolidTexture(SailwindSkin.Parchment);

            _title = new GUIStyle(GUI.skin.label)
            {
                fontSize = 30,
                alignment = TextAnchor.MiddleCenter,
                wordWrap = false,
            }.WithFont();
            _title.normal.textColor = SailwindSkin.Parchment;

            _label = new GUIStyle(GUI.skin.label)
            {
                fontSize = 19,
                alignment = TextAnchor.MiddleCenter,
                wordWrap = false,
            }.WithFont();
            _label.normal.textColor = new Color(0.85f, 0.80f, 0.74f, 1f);

            _small = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                alignment = TextAnchor.UpperCenter,
                wordWrap = true,
            }.WithFont();
            _small.normal.textColor = new Color(0.72f, 0.66f, 0.60f, 1f);

            _stylesBuilt = true;
        }
    }
}
