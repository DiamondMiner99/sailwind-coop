using HarmonyLib;
using UnityEngine;
using SailwindCoop.Debug;
using SailwindCoop.Sync;

namespace SailwindCoop.Patches
{
    /// <summary>
    /// Harmony patches for synchronized sleep.
    /// </summary>
    public static class SleepPatches
    {
        #region EnterBed Patch

        /// <summary>
        /// Intercept bed entry to trigger network flow.
        /// </summary>
        [HarmonyPatch(typeof(Sleep), "EnterBed")]
        public static class SleepEnterBedPatch
        {
            [HarmonyPostfix]
            public static void Postfix(Transform bed)
            {
                if (!Plugin.HasConnectedGuest) return;

                bool isTavern = GameState.sleepingInTavern;
                // Use the SHARED boat for the moored check, not the local player's current boat
                bool isMoored = SleepSyncManager.Instance?.IsSharedBoatMoored() ?? false;

                SleepSyncManager.Instance?.OnLocalEnterBed(isTavern, isMoored);
            }
        }

        #endregion

        #region LeaveBed Patch

        /// <summary>
        /// Intercept bed leave to notify partner.
        /// </summary>
        [HarmonyPatch(typeof(Sleep), "LeaveBed")]
        public static class SleepLeaveBedPatch
        {
            [HarmonyPrefix]
            public static void Prefix()
            {
                if (!Plugin.HasConnectedGuest) return;

                SleepSyncManager.Instance?.OnLocalLeaveBed();
            }
        }

        #endregion

        #region FallAsleep Patch

        /// <summary>
        /// Block FallAsleep until both players are in bed.
        /// On guest, block entirely - host drives sleep.
        /// </summary>
        [HarmonyPatch(typeof(Sleep), "FallAsleep")]
        public static class SleepFallAsleepPatch
        {
            [HarmonyPrefix]
            public static bool Prefix()
            {
                if (!Plugin.HasConnectedGuest) return true;

                var syncManager = SleepSyncManager.Instance;
                if (syncManager == null) return true;

                // Host recovery (faint/Recover) legitimately calls FallAsleep for the blackout + control-lock;
                // it is NOT a co-op both-in-bed sleep, so the Sleeping gate below must not block it.
                // Recovery.DoRecoverPlayer sets GameState.recovering=true just before its FallAsleep() call, and
                // the guest never runs RecoverPlayer (it faints locally), so this only fires on the host during
                // a real recovery.
                if (GameState.recovering) return true;

                // Guest: never run local FallAsleep - wait for host's cycle states
                if (!Plugin.IsHost)
                {
                    VerboseLogger.SleepLocal("FallAsleep blocked on guest - waiting for host");
                    return false;
                }

                // Host: only proceed if state is SLEEPING (both in bed)
                if (syncManager.CurrentState != SleepSyncManager.SleepState.Sleeping)
                {
                    VerboseLogger.SleepLocal($"FallAsleep blocked - state is {syncManager.CurrentState}, not SLEEPING");
                    return false;
                }

                return true;
            }
        }

        #endregion

        #region StartSleepTimeWarp Patch

        /// <summary>
        /// When host starts time warp, send cycle state to guest.
        /// </summary>
        [HarmonyPatch(typeof(Sleep), "StartSleepTimeWarp")]
        public static class SleepStartSleepTimeWarpPatch
        {
            [HarmonyPostfix]
            public static void Postfix()
            {
                if (!Plugin.HasConnectedGuest) return;
                if (!Plugin.IsHost) return;

                // Send initial eyes-closed state after the 3s delay
                // The coroutine waits 3s then sets Time.timeScale = 16
                // We need to send this to guest
                Plugin.Instance.StartCoroutine(SendCycleStateAfterDelay());
            }

            private static System.Collections.IEnumerator SendCycleStateAfterDelay()
            {
                // Wait for the 3s fade to complete (matching Sleep.StartSleepTimeWarp)
                yield return new WaitForSeconds(3.1f);

                // (v0.2.34) The sleep may have ended during the fade (fast wake/abort) - don't warp-bound
                // an already-awake host (SendSleepCycleState is internally gated the same way).
                if (!SleepSyncManager.IsCoopSleepWarpActive) yield break;

                // (v0.2.34) Bound the HOST's own physics catch-up during the warp, mirroring the guest's
                // v0.2.19 bound (min(vanilla maximumDeltaTime, fixedDeltaTime*2)). No-op at Sailwind's
                // configured 0.1, but closes the host FixedUpdate-spiral hole if that config ever changes.
                SleepSyncManager.BoundMaxDeltaTimeForWarp();

                // Sleep values: timeScale=16, fixedDeltaTime=0.02222*10=0.2222
                SleepSyncManager.Instance?.SendSleepCycleState(
                    eyesClosed: true,
                    timeScale: 16f,
                    fixedDeltaTime: 0.2222f,
                    fadeTarget: 1f,
                    fadeDuration: 0f  // Already faded
                );
            }
        }

        #endregion

        #region WakeUp Patch

        /// <summary>
        /// Intercept wake to sync with partner.
        /// </summary>
        [HarmonyPatch(typeof(Sleep), "WakeUp")]
        public static class SleepWakeUpPatch
        {
            // Set true by SleepUpdatePatch (host manual interrupt) and SleepSyncManager so the
            // both-rested gate lets an intentional host wake through even when wasManual==false.
            public static bool HostManualWake;

            // FREEZE-ON-DUNK fix: set true by SleepUpdateGuestPatch when the GUEST clicks to wake. Eyes
            // stay closed for the whole 16x warp, so eyes alone can't tell a real click from a vanilla
            // PHYSICS auto-wake (BoatDamage.Overflow / BoatImpactSounds firing when the deck submerges or
            // bumps the dock under the warp). Without this flag the guest tore itself out of the
            // synchronized sleep submerged and hung. The guest now wakes only on its own click or the
            // host's WakeUp packet; all other guest-side WakeUp calls during co-op sleep are suppressed.
            public static bool GuestManualWake;

            // True when THIS WakeUp call was suppressed by the gate. Read by the Postfix so a gated
            // (never-happened) wake doesn't broadcast the eyes-open/timeScale=1 cycle-state to the guest.
            private static bool _blockedThisCall;


            // Log throttles: the wake gate and duration-cap check run every frame during a warp (~40
            // lines/s). Throttle each to ~1 per 2s of REAL time (unscaledTime), matching the
            // [SLEEP:QUORUM] heartbeat throttle in SleepSyncManager. Diagnostics only - gate logic is
            // unaffected.
            private static float _lastBlockingLog;
            private static float _lastCapCheckLog;

            // (v0.2.25) Throttle for the GUEST wake-suppression log below. Crash evidence: when the
            // guest's boat lands submerged in storm seas during a 16x warp (SLEEP_SNAP after a packet
            // backlog), vanilla physics fires Sleep.WakeUp dozens of times PER FixedUpdate - the
            // suppression branch logged 5,280 lines in the final second before a hard freeze (each
            // line was then a synchronous disk flush, see VerboseLogger). Uses realtimeSinceStartup,
            // not Time.time: timeScale is 16x here and the point is capping REAL disk I/O.
            // Diagnostics only - the suppression itself is unthrottled.
            private static float _lastGuestSuppressLog;

            // __state: true iff THIS WakeUp actually proceeded (Prefix returned true) AND it
            // was a tavern sleep. The Postfix uses it to re-enable MouseLook after the REAL vanilla WakeUp
            // ran - covering the host-auto / host-manual / guest-manual tavern wakes that go through
            // OnLocalWakeUp (which clears GameState.sleepingInTavern before vanilla WakeUp's tavern guard).
            // Stays false when the gate BLOCKED the wake (crew still sleeping -> must NOT toggle look on).
            [HarmonyPrefix]
            public static bool Prefix(out bool __state)
            {
                __state = false;
                _blockedThisCall = false;

                if (!Plugin.HasConnectedGuest) return true;

                var syncManager = SleepSyncManager.Instance;
                if (syncManager == null) return true;

                // If we're in SLEEPING state, this is a wake trigger
                if (syncManager.CurrentState == SleepSyncManager.SleepState.Sleeping)
                {
                    bool isTavern = GameState.sleepingInTavern;

                    // FREEZE-ON-DUNK (guest side, load-bearing): a GUEST must never wake itself during a
                    // co-op sleep from an AUTOMATIC wake. Failure mode: the guest's local boat dunks
                    // underwater at 16x warp, firing vanilla BoatDamage.Overflow ->
                    // Sleep.WakeUp on the guest, which (a) tears it out of the synchronized sleep submerged
                    // and (b) hits the boat-bunk control-restore gap. The host is authoritative for sleep
                    // timing; the guest wakes only on its OWN click (flagged GuestManualWake, since eyes
                    // stay closed all warp) or the host's WakeUp packet (which sets CurrentState=Awake
                    // first, so it bypasses this whole block). Suppress every other guest WakeUp here.
                    bool guestManual = !GameState.eyesFullyClosed || GuestManualWake;
                    GuestManualWake = false;
                    if (!Plugin.IsHost && !guestManual)
                    {
                        // (v0.2.25) Throttled to 1 line / 2s of REALTIME - this fires per physics
                        // contact while submerged at 16x and the unthrottled flood froze a guest.
                        if (Time.realtimeSinceStartup - _lastGuestSuppressLog > 2f)
                        {
                            _lastGuestSuppressLog = Time.realtimeSinceStartup;
                            VerboseLogger.SleepEvent("Guest auto/physics wake suppressed during co-op sleep (host drives wake)");
                        }
                        _blockedThisCall = true;
                        return false;
                    }

                    // Distinguish a GENUINE host manual click from the "host not yet full" proxy.
                    // The proxy wasManual (`!eyesFullyClosed || sleep < 99.99`) is TRUE during the host's
                    // entire slow-fill phase, so an AUTOMATIC cap-driven wake would read as "manual" and leak
                    // through the gate. A real manual wake is only: eyes open (the player can act and
                    // clicked), or an explicit host-click flag (HostManualWake, set in SleepUpdatePatch).
                    // The host filling slowly is NOT manual. We still record the proxy for OnLocalWakeUp's
                    // WasManual flag below so guest-facing behavior is unchanged.
                    bool wasManual = !GameState.eyesFullyClosed || PlayerNeeds.sleep < 99.99f;
                    bool isManualWake = !GameState.eyesFullyClosed || HostManualWake;

                    // WAKE GATE. Delay the host's AUTOMATIC wake (not a real manual click) until the crew
                    // SHOULD wake:
                    //  - BOAT sleep: until the WHOLE crew is fully rested (AllCrewRested = every connected
                    //    peer rested AND the host rested). The quorum must include the host itself - a
                    //    guest-only gate would let a slow-filling host be woken early out of its bunk.
                    //  - TAVERN sleep (tavern-time fix): rest is irrelevant. Vanilla taverns skip to the
                    //    7-10am morning window, not to "crew rested" (~1s of warp). crewRested is forced
                    //    false for tavern so ONLY the morning DurationCap (or a real manual wake) releases it.
                    // We must NOT swallow real manual wakes (isManualWake) or the genuine anti-oversleep
                    // duration caps (DurationCapReached, whose boat branch is suppressed while the crew is
                    // still resting - see below). recovering always passes through (recovery calls
                    // Sleep.WakeUp internally). The guest is !IsHost so this never blocks the guest.
                    // N-player (Phase 4): the boat quorum reads AllCrewRested instead of the single-guest
                    // GuestRested. At N=1 these are equivalent.
                    bool crewRested = !isTavern && syncManager.AllCrewRested;

                    if (Plugin.IsHost && !isManualWake &&
                        !crewRested && !DurationCapReached() && !GameState.recovering)
                    {
                        if (Time.unscaledTime - _lastBlockingLog > 2f)
                        {
                            _lastBlockingLog = Time.unscaledTime;
                            VerboseLogger.SleepEvent($"Wake gate BLOCKING auto-wake: tavern={isTavern}, isManualWake={isManualWake}, crewRested={crewRested}, AllCrewRested={syncManager.AllCrewRested}, DurationCapReached={DurationCapReached()}, host_sleep={PlayerNeeds.sleep:F1}%");
                        }
                        _blockedThisCall = true;
                        return false;
                    }
                    VerboseLogger.SleepEvent($"Wake gate RELEASING auto-wake: tavern={isTavern}, isManualWake={isManualWake}, crewRested={crewRested}, AllCrewRested={syncManager.AllCrewRested}, DurationCapReached={DurationCapReached()}, recovering={GameState.recovering}");
                    HostManualWake = false;

                    // Coverage gap: capture the tavern flag NOW, before OnLocalWakeUp ->
                    // TransitionToAwake clears GameState.sleepingInTavern. The Postfix restores look after
                    // the real vanilla WakeUp runs. Only set when the wake actually proceeds (return true).
                    __state = GameState.sleepingInTavern;

                    // Force eyesFullyClosed so WakeUp() doesn't early-return
                    // during the 3s fade period. Both host and guest handle wake symmetrically.
                    GameState.eyesFullyClosed = true;
                    syncManager.OnLocalWakeUp(wasManual);
                    return true; // Let wake proceed for both
                }

                return true;
            }

            /// <summary>
            /// Mirrors the two vanilla auto-wake caps from Sleep.Update so the both-rested gate can let
            /// them through (preventing an unbounded warp when the guest never reaches full rest).
            /// </summary>
            private static bool DurationCapReached()
            {
                if (Sleep.instance == null) return false;
                float dur = Traverse.Create(Sleep.instance).Field("currentSleepDuration").GetValue<float>();
                if (GameState.sleepingInTavern)
                {
                    bool tavernCap = Sun.sun != null && Sun.sun.localTime > 7f && Sun.sun.localTime < 10f && dur > 3.3f;
                    if (Time.unscaledTime - _lastCapCheckLog > 2f)
                    {
                        _lastCapCheckLog = Time.unscaledTime;
                        VerboseLogger.SleepEvent($"Duration cap check: tavern={GameState.sleepingInTavern}, dur={dur:F2}s, result={tavernCap}");
                    }
                    return tavernCap;
                }
                // MOORED (timeskip) sleep only: the vanilla boat cap (dur > 4.5f) is crossed in ~3.7 real
                // seconds during the time-warp - long before slower-resting crewmates fill. Suppress the
                // boat cap while ANY crewmate is still filling so it can't release the wake gate early. The
                // SleepingBackstop (real-time, in SleepSyncManager, bidirectional) still force-wakes an
                // AFK/never-resting crew, so the crew can never be wedged asleep at 16x.
                // UNMOORED sleep (no timeskip): the cap is the vanilla 4.5-game-hour nap boundary (~36s of
                // real 16x warp) and MUST fire - clock, physics and rest run consistently at 16x, so the
                // nap ends at the vanilla partial-rest point; the AllCrewRested early-wake still applies if
                // everyone fills sooner. Do not suppress it, or an unmoored crew warps far past vanilla.
                // N-player: "waiting" is the whole-crew quorum (!AllCrewRested), which already
                // folds in the host-rested requirement - so it covers a slow HOST as well as a slow guest.
                // At N=1 these are equivalent.
                var sm = SleepSyncManager.Instance;
                // Suppress the vanilla 4.5h boat cap while the crew is not yet all-rested, so a moored crew
                // sleeps to FULL rest (the v0.2.15 intent) instead of the vanilla partial-rest point. This is
                // gated purely on !AllCrewRested with NO real-time ceiling: AllCrewRested waits for the
                // slowest ALIVE crewmate correctly (frame-rate independent - each fills on its own machine
                // and reports at its own 99.99%), a truly FROZEN (non-streaming) crewmate is caught by the
                // 12s silence watchdog, and a genuine stall by the 60s/90s backstops. (v0.2.34 briefly added
                // a fixed wall-clock ceiling here; adversarial verification showed it force-woke a
                // legitimately-slow-but-progressing crewmate mid-fill on a low-end PC, because the warp
                // degrades with frame rate while the ceiling did not - so it was removed.)
                bool waitingForCrew = Plugin.HasConnectedGuest && sm != null &&
                    sm.CurrentState == SleepSyncManager.SleepState.Sleeping &&
                    sm.IsTimeskipEnabled && !sm.AllCrewRested;
                bool boatCap = dur > 4.5f && !GameState.recovering && !waitingForCrew;
                if (Time.unscaledTime - _lastCapCheckLog > 2f)
                {
                    _lastCapCheckLog = Time.unscaledTime;
                    VerboseLogger.SleepEvent($"Duration cap check: tavern={GameState.sleepingInTavern}, dur={dur:F2}s, waitingForCrew={waitingForCrew}, result={boatCap}");
                }
                return boatCap;
            }

            [HarmonyPostfix]
            public static void Postfix(bool __state)
            {
                // The REAL vanilla WakeUp has now run. If this was a tavern wake that
                // actually proceeded (__state), re-enable MouseLook - vanilla's `if (sleepingInTavern)` guard
                // was already cleared by OnLocalWakeUp -> TransitionToAwake, so it skipped the toggle. Covers
                // host-auto / host-manual / guest-manual tavern wakes. Runs for guest too (before the !IsHost
                // return below). __state is false when the gate BLOCKED the wake, so a still-sleeping crew is
                // never toggled. Guard against re-enabling while in a bed (boat case rides LeaveBed).
                if (__state && !GameState.inBed)
                {
                    MouseLook.ToggleMouseLook(newState: true);
                    VerboseLogger.SleepEvent("Re-enabled MouseLook after tavern wake (WakeUp Postfix)");
                }

                // Do NOT broadcast a wake that was gated (it never actually happened). Sending the
                // eyes-open/timeScale=1 cycle-state every gated frame would drag the guest out of warp.
                if (_blockedThisCall) { _blockedThisCall = false; return; }

                if (!Plugin.HasConnectedGuest) return;
                if (!Plugin.IsHost) return;

                // Host woke up - send final cycle state (eyes open, normal time)
                SleepSyncManager.Instance?.SendSleepCycleState(
                    eyesClosed: false,
                    timeScale: 1f,
                    fixedDeltaTime: 0.02222f,
                    fadeTarget: 0f,
                    fadeDuration: 5.05f
                );
            }
        }

        #endregion

        #region Sleep Update Patch (Cycle Detection)

        /// <summary>
        /// Monitor Sleep.Update to detect cycle transitions and send to guest.
        /// eyesFullyClosed stays true for the whole warp (it only flips false on wake); the real manual
        /// wake signal is the host's click, captured below as HostManualWake.
        /// </summary>
        [HarmonyPatch(typeof(Sleep), "Update")]
        public static class SleepUpdatePatch
        {
            // Edge detector for GameState.eyesFullyClosed. MUST be re-baselined on every frame this postfix
            // does NOT own the flag (see ReleaseEdgeDetector) or it goes stale and fires a phantom edge.
            private static bool _lastEyesFullyClosed;

            /// <summary>
            /// (v0.2.37) Keep the eyes edge detector in sync on frames where this postfix is not the one
            /// driving the sleep cycle. Without this, _lastEyesFullyClosed is a process-lifetime static that
            /// only ever gets written on the edge branch below - and the eyes-true -> false edge is INVISIBLE
            /// here on every automatic wake, because those wakes leave CurrentState == Awake before the
            /// postfix runs (an unmoored nap ends inside vanilla Sleep.Update itself, decomp Sleep.cs:79-81,
            /// and the all-rested / per-peer-deadline / backstop / OnPeerLeft wakes call WakeUp from outside
            /// Sleep.Update entirely). So it stayed stuck at TRUE after the first such wake, for the rest of
            /// the process.
            /// The consequence landed on the NEXT sleep: there is always a >=3s window with
            /// CurrentState == Sleeping and eyes still false (FallAsleep is gated here; vanilla only sets eyes
            /// after WaitForSeconds(3f)), so the first frame of it read as a false edge and broadcast
            /// SendSleepCycleState(eyesClosed: false, timeScale: 1, fade 0 over 5.05s) to the crew. On the
            /// guest that cleared the eyes flag, undid the v0.2.37 eyes assertion in TransitionToSleeping -
            /// reopening the fade-window physics-wake hole where a wave slap tears the whole crew out of the
            /// sleep - and faded the black screen back toward clear mid-fall-asleep.
            /// Re-baselining converges to the true value (awake => false) and cannot suppress the real
            /// eyes-true edge at warp start, which is what carries the timeScale=16 cycle state.
            /// </summary>
            private static void ReleaseEdgeDetector()
            {
                _lastEyesFullyClosed = GameState.eyesFullyClosed;
                PatchProfiler.End("Sleep.Update.Host");
            }

            [HarmonyPostfix]
            public static void Postfix()
            {
                PatchProfiler.Begin("Sleep.Update.Host");

                if (!Plugin.HasConnectedGuest)
                {
                    ReleaseEdgeDetector();
                    return;
                }
                if (!Plugin.IsHost)
                {
                    ReleaseEdgeDetector();
                    return;
                }

                var syncManager = SleepSyncManager.Instance;
                if (syncManager == null)
                {
                    ReleaseEdgeDetector();
                    return;
                }
                if (syncManager.CurrentState != SleepSyncManager.SleepState.Sleeping)
                {
                    ReleaseEdgeDetector();
                    return;
                }

                // FIX: host manual interrupt. Once the host's own sleep is full, a click reads as a
                // rest-full (non-manual) wake and would be swallowed by the both-rested gate. Detect the
                // press here and flag HostManualWake so SleepWakeUpPatch lets it through.
                // Vanilla TAVERN sleep is NON-interruptible - it runs to the 7-10am morning gate and
                // a keypress can't wake you. Only BUNK/SHIP sleep allows a click to wake. Gate this interrupt
                // behind !tavern so a stray Activate/PickUp can't yank the whole crew out of a tavern sleep
                // (the tavern DurationCap alone would only DELAY that; the interrupt must be suppressed).
                // syncManager.IsTavernSleep is reliable on the host (set in OnLocalEnterBed); the morning-wake
                // cap stays untouched in DurationCapReached.
                if (!syncManager.IsTavernSleep &&
                    (GameInput.GetKeyDown(InputName.Activate) || GameInput.GetKeyDown(InputName.PickUp)))
                {
                    SleepWakeUpPatch.HostManualWake = true;
                    Sleep.instance?.WakeUp();
                }

                // Detect eyes state change
                if (GameState.eyesFullyClosed != _lastEyesFullyClosed)
                {
                    _lastEyesFullyClosed = GameState.eyesFullyClosed;

                    if (GameState.eyesFullyClosed)
                    {
                        // Eyes closing - entering time warp
                        syncManager.SendSleepCycleState(
                            eyesClosed: true,
                            timeScale: 16f,
                            fixedDeltaTime: 0.2222f,
                            fadeTarget: 1f,
                            fadeDuration: 3f
                        );
                    }
                    else
                    {
                        // Eyes opening - can wake now
                        syncManager.SendSleepCycleState(
                            eyesClosed: false,
                            timeScale: 1f,
                            fixedDeltaTime: 0.02222f,
                            fadeTarget: 0f,
                            fadeDuration: 5.05f
                        );
                    }
                }

                PatchProfiler.End("Sleep.Update.Host");
            }
        }

        #endregion

        #region Guest: Block Sleep.Update Auto-Sleep

        /// <summary>
        /// On guest, prevent Sleep.Update from triggering FallAsleep or WakeUp automatically.
        /// </summary>
        [HarmonyPatch(typeof(Sleep), "Update")]
        public static class SleepUpdateGuestPatch
        {
            [HarmonyPrefix]
            public static bool Prefix()
            {
                PatchProfiler.Begin("Sleep.Update.Guest");

                if (!Plugin.HasConnectedGuest)
                {
                    PatchProfiler.End("Sleep.Update.Guest");
                    return true;
                }
                if (Plugin.IsHost)
                {
                    PatchProfiler.End("Sleep.Update.Guest");
                    return true;
                }

                var syncManager = SleepSyncManager.Instance;
                if (syncManager == null)
                {
                    PatchProfiler.End("Sleep.Update.Guest");
                    return true;
                }

                // During SLEEPING only, guest doesn't run sleep logic.
                // Sleep.Update is still allowed during WAITING so the guest can exit bed.
                if (syncManager.CurrentState == SleepSyncManager.SleepState.Sleeping)
                {
                    // INDEPENDENT NEEDS: the guest's Sleep.Update is blocked while sleeping, so it can't
                    // wake itself the vanilla way. Let a click interrupt: WakeUp routes through
                    // SleepWakeUpPatch which broadcasts WakeUp to both. GuestManualWake flags this as a
                    // GENUINE click so the freeze-on-dunk guest-suppression (which blocks physics
                    // auto-wakes during the warp, where eyes are always closed) lets it through.
                    // Vanilla TAVERN sleep is NON-interruptible, so suppress the guest keypress-wake
                    // for tavern too. Must use syncManager.IsTavernSleep, NOT GameState.sleepingInTavern: the
                    // guest never sets GameState.sleepingInTavern during a relayed tavern sleep (only the host
                    // does, in TransitionToSleeping), so that flag is FALSE here and would not block the wake.
                    // IsTavernSleep is set on the guest in OnSleepApprovedReceived. Only bunk/ship sleep wakes.
                    if (!syncManager.IsTavernSleep &&
                        (GameInput.GetKeyDown(InputName.Activate) || GameInput.GetKeyDown(InputName.PickUp))
                        && Sleep.instance != null)
                    {
                        // Set the flag only when we will actually call WakeUp, so it can't leak to a later
                        // automatic wake if Sleep.instance is momentarily null.
                        SleepWakeUpPatch.GuestManualWake = true;
                        Sleep.instance.WakeUp();
                    }

                    // Still need to update bed position though
                    if (GameState.inBed)
                    {
                        var sleep = Sleep.instance;
                        if (sleep != null)
                        {
                            // Update player position in bed
                            sleep.transform.position = GameState.inBed.GetChild(0).position + Vector3.down;
                            sleep.transform.rotation = GameState.inBed.GetChild(0).rotation;
                        }
                    }
                    PatchProfiler.End("Sleep.Update.Guest");
                    return false; // Skip rest of Update
                }

                PatchProfiler.End("Sleep.Update.Guest");
                return true;
            }
        }

        #endregion
    }
}
