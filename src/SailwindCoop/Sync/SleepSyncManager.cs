using System.Collections;
using System.Collections.Generic;
using Steamworks;
using UnityEngine;
using SailwindCoop.Debug;
using SailwindCoop.Networking.Packets;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Manages synchronized sleep between host and guest.
    /// State machine: AWAKE -> WAITING -> SLEEPING -> AWAKE
    /// </summary>
    public class SleepSyncManager : MonoBehaviour
    {
        public static SleepSyncManager Instance { get; private set; }

        public enum SleepState
        {
            Awake,
            Waiting,   // One player in bed, waiting for partner
            Sleeping   // Both in bed, time warp active
        }

        public SleepState CurrentState { get; private set; } = SleepState.Awake;

        /// <summary>
        /// True while the LOCAL player is in an active co-op sleep (eyes closed / black screen, timeScale
        /// possibly warped to 16x). BoatSyncManager reads this to suspend the physics-correction integrator
        /// (unstable at warped deltaTimes - froze guests on unmoored sleeps) and track the host directly.
        /// False in solo (manager only drives state in multiplayer) and outside the SLEEPING state.
        /// </summary>
        public static bool IsCoopSleepWarpActive =>
            Instance != null && Instance.CurrentState == SleepState.Sleeping;

        /// <summary>
        /// (v0.2.25) Multiplier applied to the HOST's high-frequency send-interval gates (boat
        /// transform, control, time, damage) while a co-op sleep is active. Those gates compare
        /// Time.time, which runs 16x during the sleep warp - so a "1Hz" channel really sends at ~16Hz
        /// of REAL time, and together they saturated a guest's 200-packet/frame budget, delaying
        /// BoatTransform ~3.5s and firing 175-200m SLEEP_SNAPs into storm seas (the crash chain).
        /// Halving the send rate during sleep is safe: guests are passive (controls locked, host
        /// authoritative) and only need enough stream to track the warp. Send FREQUENCY only - no
        /// wire/packet change. 1 everywhere except the sleeping host.
        /// </summary>
        public static float HostSleepSendIntervalScale =>
            Plugin.IsHost && IsCoopSleepWarpActive ? 2f : 1f;

        /// <summary>
        /// True when the CURRENT co-op sleep is a TAVERN sleep. Exposed read-only so SleepPatches can
        /// gate the keypress-wake blocks (vanilla tavern sleep is non-interruptible). Reliable on BOTH host and
        /// guest: the host sets it in OnLocalEnterBed, the guest in OnSleepApprovedReceived - unlike
        /// GameState.sleepingInTavern, which the guest never sets during a relayed tavern sleep.
        /// </summary>
        public bool IsTavernSleep => _isTavernSleep;

        /// <summary>
        /// True when the CURRENT co-op sleep is a TIMESKIP sleep (moored/tavern - the vanilla 9x Sun
        /// fast-forward). Exposed read-only so SleepPatches can scope the waitingForCrew suppression of the
        /// vanilla 4.5-game-hour boat cap to timeskip sleeps only: an UNMOORED nap must keep the vanilla cap
        /// (clock=physics=rest all at 16x, partial rest by design). Reliable on BOTH host and guest: the host
        /// sets it in TransitionToSleeping, the guest in OnSleepApprovedReceived (SleepApproved.IsTimeskip).
        /// </summary>
        public bool IsTimeskipEnabled => _isTimeskipEnabled;

        // Context tracking
        private bool _isTavernSleep;
        private bool _isTimeskipEnabled;
        private bool _localPlayerInBed;

        /// <summary>
        /// (v0.2.29 bed rest) True while the LOCAL player is committed to a bed but real sleep has not
        /// started - i.e. the whole waiting-for-crew window. SurvivalPatches uses this to freeze vital
        /// drains + slowly restore sleep so an in-bed AFK player (typically a lone host, who can never
        /// sleep solo under the all-crew rule) does not pass out.
        /// </summary>
        public bool IsLocalPlayerRestingInBed => _localPlayerInBed && !GameState.sleeping;

        // N-player (Phase 4): per-peer in-bed set, replacing the single _remotePlayerInBed. The host owns
        // the quorum: an entry per connected peer (guest) that has reported it is in bed. At N=1 this holds
        // at most the single guest, so AllCrewInBed == (that one peer in bed) == the old _remotePlayerInBed.
        private readonly HashSet<SteamId> _inBedPeers = new HashSet<SteamId>();
        private string _sharedBoatName;  // moored checks use the SHARED boat, not the local player's current boat
        private float _waitingSince;     // Time.time when the current WAITING state began (for the timeout)
        private const float WaitingTimeout = 90f; // auto-cancel a stuck WAITING handshake after this many seconds
        private float _sleepingSince;    // Time.time when the current SLEEPING state began (host backstop)
        private const float SleepingBackstop = 60f; // real-time safety: release the gate if the crew never reports rested
        // Crew-sleep absolute backstop: Time.unscaledTime when the crew first left AWAKE for the CURRENT
        // sleep attempt (WAITING or SLEEPING). Unlike _sleepingSince this is NOT re-stamped on a
        // Sleeping<->Waiting bounce, so a handshake that ping-pongs between the two states can never keep the
        // backstop clock pinned at "just started". The host force-resolves the whole crew-sleep if it persists
        // past this absolute real-time bound without reaching all-rested, covering the WAITING wedge (a host/peer
        // dragged into WAITING with _localPlayerInBed=false has no other escape valve) AND a SLEEPING desync.
        private float _crewSleepStartedAt;
        private const float CrewSleepBackstop = 90f; // real-time absolute cap on any single crew-sleep attempt
        // GUEST-FREEZE (client-side self-timeout): the whole crew-sleep escape design is HOST-authoritative -
        // CrewSleepBackstop, SleepingBackstop, the per-peer rested deadline, and the orphan-timewarp self-heal
        // are all gated on Plugin.IsHost. On the GUEST the only SLEEPING escape valves are the 12s host-SILENCE
        // watchdog (useless if the host is alive-but-wedged and STILL streaming position), a full disconnect,
        // and the host's reliable WakeUp. So a host that is alive but wedged (still streaming, never broadcasts
        // WakeUp) leaves the guest stuck asleep with Time.timeScale warped - a client freeze. This is the
        // guest-only mirror of the host CrewSleepBackstop: an absolute real-time cap on how long the guest will
        // stay SLEEPING before self-recovering via AbortSleep. Deliberately LONGER than CrewSleepBackstop so it
        // NEVER pre-empts a legit host-driven wake in normal operation (the host resolves first at 90s); this is
        // a pure last-resort safety valve, not part of the normal wake path. Measured with Time.unscaledTime
        // against _sleepingSince (stamped on the guest in TransitionToSleeping, bounce-guarded, real time).
        private const float GuestCommittedSelfTimeout = CrewSleepBackstop + 30f; // ~120s guest-only committed cap (WAITING superset)
        // Guest SLEEPING self-timeout: a flat last-resort valve for a guest whose host is alive-but-wedged
        // (still streaming position -> the 12s silence watchdog never fires; not disconnected; never
        // broadcasts WakeUp). 75s sits ABOVE the host's own primary resolution (SleepingBackstop 60s) so a
        // healthy host always resolves and broadcasts WakeUp FIRST in normal operation (host-authoritative
        // wake preserved), and BELOW CrewSleepBackstop(90s). Was ~120s; this frees a truly-wedged-host guest
        // ~45s sooner. The WAITING/committed valve keeps the longer GuestCommittedSelfTimeout because its
        // host counterpart WaitingTimeout is still 90s. (v0.2.34 dropped the tight timeskip hard-cap this
        // used to be pegged to - see the NOTE in Update() - because a fixed wall-clock cap cut legit
        // slow-machine fills short; the layered watchdog/backstops are the safety net.)
        private const float GuestSleepingSelfTimeout = 75f; // guest-only SLEEPING last-resort cap
        // GUEST-FREEZE (WAITING window): the guest SLEEPING self-timeout above only rescues a guest already in
        // SLEEPING (_sleepingSince, stamped in TransitionToSleeping). The earlier wedge is a guest that has
        // COMMITTED to a co-op sleep - OnLocalEnterBed set _localPlayerInBed=true and sent its SleepRequest - yet
        // never reaches SLEEPING because the host's SleepApproved never arrives (host wedged / packet lost). The
        // guest arm of the handshake is a pure "wait for SleepApproved" no-op, so it sits in WAITING with NO
        // self-recovery valve: the 12s host-SILENCE watchdog only trips if the host STOPS streaming position, and
        // the SLEEPING self-timeout can never apply because _sleepingSince is still 0. Result: the guest is frozen
        // on the sleep/bed screen. _committedToSleepAt is the guest's ABSOLUTE (Time.unscaledTime) "I committed to
        // this co-op sleep" anchor, stamped the moment it enters bed (OnLocalEnterBed) and NOT re-stamped on a
        // flicker; it covers the WHOLE commit (WAITING and SLEEPING), unlike _sleepingSince which only starts at
        // the WAITING->SLEEPING edge. Cleared in TransitionToAwake with the other sleep clocks.
        private float _committedToSleepAt;
        private float _lastQuorumHeartbeatLog; // Time.unscaledTime of the last [SLEEP:QUORUM] heartbeat (throttle)
        // (v0.2.34) Main-log heartbeat throttle. The verbose [SLEEP:QUORUM] heartbeat is dark for normal
        // players (VerboseLogger is F8-gated), which is exactly why every sleep-freeze report arrived with
        // "no errors in the logs". A 5s copy of the heartbeat goes to the BepInEx main log unconditionally
        // during any co-op sleep so the NEXT report carries usable state without needing F8.
        private float _lastSleepInfoLog;
        // Per-peer rested deadline: Time.unscaledTime by which EACH peer must have reported rested once the
        // crew is SLEEPING. One peer that fills very slowly (or never reports) must not wedge the AllCrewRested
        // quorum indefinitely; if a peer blows past this real-time deadline the host force-marks it rested so the
        // remaining (already-rested) crew can auto-wake. Keyed per peer, seeded when SLEEPING begins / a peer joins.
        private readonly Dictionary<SteamId, float> _restedDeadline = new Dictionary<SteamId, float>();
        private const float PerPeerRestedTimeout = 75f; // real-time grace for one peer to fill before it's force-rested
        // HOST-STUCK-ON-GUEST-DROP: if a guest freezes/hangs (or is mid-rejoin) it stops sending packets
        // while HasConnectedGuest stays true, so the disconnect->AbortSleep path never fires and the host
        // is freed only by the slow 90s WAITING valve. Abort the handshake if the partner goes silent this
        // long. Safely above the worst observed legitimate WAITING->SLEEPING handshake (~6.3s) and P2P jitter.
        private const float GuestSilenceTimeout = 12f;
        // FREEZE-ON-DUNK (control-lock safety net): seconds the local player has been stuck control-locked
        // in a boat bunk after waking (vanilla restores control only on the next click via LeaveBed).
        private float _stuckInBedSince;
        // Time.unscaledTime when we last transitioned OUT of a co-op SLEEPING state. The boat-bunk
        // control-restore watchdog only runs briefly after a real co-op wake, so it can never fire in
        // single-player or during the vanilla pre-sleep cooldown (both of which also leave you in bed,
        // control-locked, in the Awake state). 0 until the first co-op wake.
        private float _wokeFromCoopSleepAt;
        private const float WokeWindow = 6f; // how long after a co-op wake the unstick watchdog stays armed

        // INDEPENDENT NEEDS (all-rested auto-wake): each player's sleep fills independently. The host
        // must wait for the WHOLE crew to be fully rested before auto-waking everyone.
        // N-player (Phase 4): per-peer rested set, replacing the single _guestRested. The host adds a peer
        // when it reports SleepRested. At N=1 this holds at most the single guest, so AllCrewRested ==
        // (that one guest rested AND host rested) == the old _guestRested (host auto-wake only fires when
        // the host itself is already rested).
        private readonly HashSet<SteamId> _restedPeers = new HashSet<SteamId>();
        private bool _sentRested;        // guest: true once we've told the host we're rested (send once)

        /// <summary>
        /// N-player (Phase 4): true when EVERY currently-connected peer has reported in-bed AND the host
        /// is in bed. The host owns this quorum. At N=1 it reduces to "the single guest is in bed AND the
        /// host is in bed", i.e. the old _localPlayerInBed && _remotePlayerInBed pair.
        /// </summary>
        public bool AllCrewInBed
        {
            get
            {
                if (!_localPlayerInBed) return false; // host (the local quorum owner) must be in bed too
                var peers = Plugin.NetworkManager?.ConnectedPeers;
                if (peers == null || peers.Count == 0) return false;
                var rpm = SailwindCoop.Player.RemotePlayerManager.Instance;
                foreach (var id in peers)
                {
                    // A peer mid-join (AddPeer ran, but it's still loading and can't have reported
                    // in-bed) must not gate the crew - mirror the watchdog's per-peer JoinPending skip, else
                    // a fresh joiner blocks the already-assembling crew until the 60s backstop.
                    if (Plugin.IsJoinPendingFor(id)) continue;
                    // JoinPending alone is not enough: it is set ONLY on the host-busy DEFERRED join and clears
                    // the moment the host SENDS the join state - a guest that joined while the host was AWAKE
                    // (the common case) loads its world for ~15-20s with NO skip and would wrongly gate the
                    // crew. So also skip any peer that has NEVER streamed a position packet (still loading; it
                    // cannot have reported in-bed yet). HasStreamedPacket flips true only on the first real
                    // position packet, which is the correct "finished loading" signal (peer-silence timers are
                    // baselined positive at avatar spawn and cannot distinguish a loader). The watchdog below
                    // has the matching skip.
                    if (rpm != null && !rpm.HasStreamedPacket(id)) continue;
                    if (!_inBedPeers.Contains(id)) return false;
                }
                return true;
            }
        }

        /// <summary>
        /// N-player (Phase 4): true when EVERY currently-connected peer has reported fully rested AND the
        /// host is fully rested (PlayerNeeds.sleep >= 99.99, mirroring the existing host-rested check). The
        /// host owns this quorum; it drives the both-rested auto-wake gate in SleepWakeUpPatch.
        /// At N=1 it reduces to "the single guest rested AND host rested", matching the old _guestRested
        /// gate (the host only auto-wakes once it is itself rested).
        /// </summary>
        public bool AllCrewRested
        {
            get
            {
                if (PlayerNeeds.sleep < 99.99f) return false; // host must be rested too
                var peers = Plugin.NetworkManager?.ConnectedPeers;
                if (peers == null || peers.Count == 0) return false;
                var rpm = SailwindCoop.Player.RemotePlayerManager.Instance;
                foreach (var id in peers)
                {
                    // A peer mid-join (AddPeer ran, but it's still loading and can't have reported
                    // rested) must not gate the crew - mirror the watchdog's per-peer JoinPending skip, else
                    // a fresh joiner blocks the sleeping crew's auto-wake until the 60s SleepingBackstop.
                    if (Plugin.IsJoinPendingFor(id)) continue;
                    // Also skip a peer still loading after an AWAKE-host join (see AllCrewInBed) - it loads
                    // ~15-20s before it can report rested. HasStreamedPacket (first real position packet) is
                    // the correct "finished loading" signal. Watchdog below has the matching skip.
                    if (rpm != null && !rpm.HasStreamedPacket(id)) continue;
                    if (!_restedPeers.Contains(id)) return false;
                }
                return true;
            }
        }

        /// <summary>
        /// Compatibility alias kept for any caller that still reads "is the crew rested?". Points at the
        /// N-player AllCrewRested quorum so single-guest and N-crew share one gate.
        /// </summary>
        public bool GuestRested => AllCrewRested;

        /// <summary>
        /// Set the shared boat name for moored checks.
        /// Called from BoatStateApplicator when guest joins.
        /// </summary>
        public void SetSharedBoat(string boatName)
        {
            _sharedBoatName = boatName;
            VerboseLogger.SleepEvent($"Shared boat set to: {boatName}");
        }

        /// <summary>
        /// Check if the SHARED boat is moored. Used by SleepPatches instead of checking the local
        /// player's current boat, so host and guest agree on the moored/timeskip decision.
        /// </summary>
        public bool IsSharedBoatMoored()
        {
            var sharedBoat = BoatUtility.FindBoatByName(_sharedBoatName);
            return sharedBoat != null &&
                   sharedBoat.GetComponent<BoatMooringRopes>()?.AnyRopeMoored() == true;
        }

        private void Awake()
        {
            if (Instance != null)
            {
                Destroy(this);
                return;
            }
            Instance = this;
        }

        private void Update()
        {
            // Orphaned vanilla StartSleepTimeWarp backstop. Sleep.FallAsleep arms a coroutine that
            // yields ~3s real then sets Time.timeScale=16 unconditionally. If the crew wakes/aborts during
            // that fade, the orphan fires afterward and wedges the host at 16x. Self-heal: when we're AWAKE
            // (no co-op sleep), the host is not vanilla-sleeping, yet timeScale is still warped, reset it.
            // No false positives: a real co-op warp has CurrentState==Sleeping; recovery's legit warp has
            // GameState.sleeping==true; a normal awake host has timeScale==1.
            // (v0.2.34) Also runs when NOT in a session at all (post-lobby-close, either former role): the
            // old Plugin.IsHost gate went false the moment the lobby closed, so a residual 16x from a wedged
            // sleep survived "close lobby" unhealed - one of the reported stuck-host states. An in-session
            // GUEST stays excluded (its timeScale legitimately follows host cycle-state packets).
            // timeScale > 1f (NOT != 1f): a real orphan warp is always 16x (>1); the vanilla SOLO PAUSE menu
            // sets timeScale = 0 (<1), and SleepSyncManager.Update runs in solo too - a != 1f test would
            // false-fire on every solo pause and un-freeze the menu (review BLOCKER 2). >1f targets only warps.
            if (CurrentState == SleepState.Awake && (Plugin.IsHost || !Plugin.IsMultiplayer) &&
                !GameState.sleeping && Time.timeScale > 1f)
            {
                Plugin.Log.LogWarning($"[SLEEP] Orphan vanilla timewarp detected (Awake+!sleeping, timeScale={Time.timeScale}); self-healed: timeScale=1, fixedDeltaTime=0.02222, eyesFullyClosed=false, timeskipSleep=false");
                Time.timeScale = 1f;
                Time.fixedDeltaTime = 0.02222f;
                if (_vanillaMaxDeltaTime.HasValue) Time.maximumDeltaTime = _vanillaMaxDeltaTime.Value; // undo the 16x catch-up bound (GLOBAL - restore the game's own value on every un-warp path)
                GameState.eyesFullyClosed = false;
                Sleep.timeskipSleep = false;
            }

            // FREEZE-ON-DUNK (Layer 3, boat-bunk control-restore safety net). Vanilla Sleep.WakeUp restores
            // control only `if (!GameState.inBed)`; a boat bunk leaves GameState.inBed set, so after waking
            // you stay control-locked until you press a button (vanilla LeaveBed). If that click is missed
            // (the dunk/warp transition, a hitch), you're stuck in bed unable to move. When we're AWAKE but
            // still inBed with the character controller disabled and no active sleep/recovery, auto-exit the
            // bed after a short grace. Gated to a connected co-op session AND a short window after a real
            // co-op wake, so it can NEVER fire in solo or during the vanilla pre-sleep cooldown (both of which
            // also sit Awake + inBed + control-off, and would otherwise eject the sleeper before FallAsleep).
            if (Plugin.HasConnectedGuest && CurrentState == SleepState.Awake && GameState.inBed != null &&
                !GameState.sleeping && !GameState.recovering &&
                Time.unscaledTime - _wokeFromCoopSleepAt < WokeWindow &&
                Refs.charController != null && !Refs.charController.enabled)
            {
                _stuckInBedSince += Time.unscaledDeltaTime;
                if (_stuckInBedSince > 2.5f && Sleep.instance != null)
                {
                    VerboseLogger.SleepEvent("Stuck control-locked in bed after wake; auto-leaving bed (freeze safety net)");
                    _stuckInBedSince = 0f;
                    Sleep.instance.LeaveBed();
                }
            }
            else
            {
                _stuckInBedSince = 0f;
            }

            // GUEST-FREEZE (WAITING window, client-side self-timeout): the SLEEPING self-timeout below
            // only rescues a guest already in SLEEPING (keyed on _sleepingSince). The earlier wedge is a guest
            // that COMMITTED to a co-op sleep (OnLocalEnterBed set _localPlayerInBed + sent its SleepRequest) yet
            // never reaches SLEEPING because the host's SleepApproved never arrives (host wedged / packet lost) -
            // the guest arm is a pure "wait for SleepApproved" no-op. Such a guest sits in WAITING with no escape:
            // the 12s host-SILENCE watchdog only trips if the host STOPS streaming position (alive-but-wedged host
            // keeps streaming), and _sleepingSince is still 0 so the SLEEPING timeout can't apply - the guest is
            // frozen on the sleep/bed screen. This is the guest-only absolute committed-to-sleep valve covering the
            // WAITING (and, as a superset, Sleeping) window: once we've been committed past GuestCommittedSelfTimeout
            // (~120s, longer than the host's 90s WaitingTimeout/CrewSleepBackstop so it never pre-empts a legit wake),
            // self-recover via AbortSleep, which routes through vanilla Sleep.WakeUp + TransitionToAwake, restoring
            // Time.timeScale=1/fixedDeltaTime/eyes and clearing the bed/black fade. Placed BEFORE the Awake
            // early-return (this fires in WAITING and the state is not Awake); the separate SLEEPING timeout below
            // still coexists as the tighter _sleepingSince-anchored valve for the warp case. No wire change.
            if (!Plugin.IsHost && CurrentState != SleepState.Sleeping && _localPlayerInBed &&
                _committedToSleepAt > 0f && Time.unscaledTime - _committedToSleepAt > GuestCommittedSelfTimeout)
            {
                Plugin.Log.LogWarning($"[SLEEP] GUEST committed self-timeout ({GuestCommittedSelfTimeout}s) in {CurrentState}; host never approved/woke (alive-but-wedged: no SleepApproved, no disconnect). Self-recovering via AbortSleep. committedAge={Time.unscaledTime - _committedToSleepAt:F0}s, timeScale={Time.timeScale:F1}");
                ShowNotification("Sleep timed out", 3f);
                AbortSleep(); // vanilla WakeUp + TransitionToAwake: restores timeScale/fixedDeltaTime/eyes, clears bed/fade
                return;
            }

            if (CurrentState == SleepState.Awake) return;

            // Host fast-forward, single-set: Sun's 9x timeskip (moored/tavern only) is asserted ONCE in
            // TransitionToSleeping (Sleep.timeskipSleep=true) and Sun.Update re-reads that flag every frame, so
            // it holds for the whole co-op SLEEPING with no per-frame re-assert needed. (Decomp Sleep.FallAsleep ONLY sets
            // timeskipSleep=TRUE, and only for moored/tavern; for the unmoored boat case it leaves the flag
            // UNTOUCHED. Nothing clears it mid-sleep - only Sleep.WakeUp does, on wake.) Cleared on every wake
            // by TransitionToAwake (and vanilla WakeUp).

            // DELIBERATE diagnostics: [SLEEP:QUORUM] heartbeat, throttled (~2s), verbose-log only. Shows whether
            // the crew is wedged in WAITING vs SLEEPING, the in-bed/rested quorum counts, the Sun timescale that
            // actually drives the sleep fill (Time.timeScale vs Sun.sun.timescale mismatches stall the fill),
            // and the age of the crew-sleep so a stalled fill is visible. unscaledTime so the throttle is real time.
            if (Time.unscaledTime - _lastQuorumHeartbeatLog > 2f)
            {
                _lastQuorumHeartbeatLog = Time.unscaledTime;
                int peerCount = Plugin.NetworkManager?.ConnectedPeers.Count ?? 0;
                float sunTs = Sun.sun != null ? Sun.sun.timescale : -1f;
                float crewAge = _crewSleepStartedAt > 0f ? Time.unscaledTime - _crewSleepStartedAt : 0f;
                float sleepAge = (CurrentState == SleepState.Sleeping && _sleepingSince > 0f) ? Time.unscaledTime - _sleepingSince : 0f;
                string heartbeat =
                    $"[SLEEP:QUORUM] state={CurrentState} host={Plugin.IsHost} peers={peerCount} " +
                    $"inBed={_inBedPeers.Count} rested={_restedPeers.Count} localInBed={_localPlayerInBed} " +
                    $"timeScale={Time.timeScale:F1} sunTimescale={sunTs:F2} timeskipSleep={Sleep.timeskipSleep} " +
                    $"timeskipEnabled={_isTimeskipEnabled} tavern={_isTavernSleep} host_sleep={PlayerNeeds.sleep:F1}% " +
                    $"allInBed={(Plugin.IsHost ? AllCrewInBed.ToString() : "n/a")} allRested={(Plugin.IsHost ? AllCrewRested.ToString() : "n/a")} " +
                    $"crewSleepAge={crewAge:F0}s sleepingAge={sleepAge:F0}s" +
                    // Triage note: on a NON-host, inBed/rested only count packets this client happened to see
                    // relayed - the HOST owns the quorum. inBed=0 with localInBed=True here is NORMAL, not a bug.
                    (Plugin.IsHost ? "" : " (quorum host-owned; tallies are relay-counts)");
                VerboseLogger.SleepEvent(heartbeat);
                // (v0.2.34) Unconditional 5s copy to the BepInEx main log: every sleep-freeze report so far
                // arrived with "no errors in the logs" because ALL sleep diagnostics were F8-verbose-gated.
                if (Time.unscaledTime - _lastSleepInfoLog > 5f)
                {
                    _lastSleepInfoLog = Time.unscaledTime;
                    Plugin.Log.LogInfo(heartbeat);
                }
            }

            // (v0.2.34) NOTE - an earlier cut of this fix added a host TIMESKIP HARD-CAP and a
            // TimeskipCapSuppressionCeiling (both fixed REAL-time bounds). Adversarial verification proved
            // them WRONG: sleep-fill DURATION scales inversely with frame rate (below ~10fps the held
            // maximumDeltaTime=0.1 clamp degrades the 16x warp toward 8x, roughly doubling the real fill
            // time), so a fixed wall-clock cap force-woke a legitimately-slow-but-progressing crewmate
            // mid-fill (~44-58% rested) on a low-end PC. They were also REDUNDANT: a slow-but-alive guest
            // is handled by AllCrewRested (it keeps streaming and eventually reports rested, and the quorum
            // waits for it - frame-rate-correct); a truly FROZEN (non-streaming) guest is caught by the 12s
            // silence watchdog below; and a stalled host / rested-then-frozen guest is caught by the 60s
            // SleepingBackstop and 90s CrewSleepBackstop. The TimeSync stale-packet guard (this release)
            // removes the day-event double-fire that was the actual freeze amplifier. So the tight caps were
            // dropped; the layered valves below remain the safety net.

            // HOST-STUCK-ON-GUEST-DROP: guest-liveness watchdog. If we're in a sleep handshake and have
            // heard NOTHING from the partner for GuestSilenceTimeout, the partner is frozen/hung or
            // mid-rejoin (HasConnectedGuest is still true, so the disconnect self-heal can't fire). Abort
            // so we aren't stuck in "Waiting for partner..." or warped asleep until the slow 90s/60s valves.
            // Skip during recovery and join (packets legitimately pause then). The DEFERRED-join skip
            // is PER-PEER (Plugin.IsJoinPendingFor inside the loop below), where a freshly-joined guest
            // streams no position for ~15-20s while it loads (without this the watchdog aborts a healthy
            // host sleep on every mid-sleep join) - so a DIFFERENT crewmate freezing is still caught.
            if (Plugin.HasConnectedGuest && !GameState.recovering &&
                !BoatSyncManager.IsJoinInProgress)
            {
                // N-player: scan each connected crewmate for a frozen partner. On the HOST (which owns the
                // quorum) skip a peer that already did its part ONLY while SLEEPING - a rested-then-frozen peer
                // must not tear the crew out of a warp the 60s SleepingBackstop will end anyway. The skip
                // is NOT applied in WAITING - there is no backstop for WAITING and the _localPlayerInBed
                // timeout gate denies a non-bedded bystander its own 90s valve, so a frozen IN-BED author would
                // otherwise strand every bystander (host or guest) in "Waiting for crew..." forever (only an
                // actual disconnect, via OnPeerLeft, would free them). Letting the silence check below catch a
                // frozen in-bed author is the missing escape. A GUEST never applies the skip at all: its only
                // peer is the host (star topology), and the host gets added to _inBedPeers the instant its
                // SleepRequest arrives even though the guest is still WAITING for SleepApproved - skipping it
                // there would blind the guest to a host that freezes in that gap, losing the 12s abort. So the
                // guest always monitors the host, matching the original 2-player watchdog's coverage.
                var rpm = SailwindCoop.Player.RemotePlayerManager.Instance;
                var livePeers = Plugin.NetworkManager?.ConnectedPeers;
                var satisfied = CurrentState == SleepState.Sleeping ? _restedPeers : _inBedPeers;
                if (rpm != null && livePeers != null)
                {
                    foreach (var peer in livePeers)
                    {
                        if (Plugin.IsHost && CurrentState == SleepState.Sleeping && satisfied.Contains(peer)) continue;
                        // Per-peer JoinPending: a deferred join blinds the watchdog only for the
                        // JOINING peer (which streams no position for ~15-20s while it loads), not the
                        // whole crew - so a DIFFERENT crewmate freezing is still caught.
                        if (Plugin.IsJoinPendingFor(peer)) continue;
                        // A peer that has never streamed a position packet is still LOADING (awake-join
                        // or post-deferred-join), NOT frozen - exclude it from the freeze-abort exactly as the
                        // quorum getters do. Its LastRemotePacketTime is the positive spawn baseline, so its
                        // TryGetPeerSilence silence grows monotonically and would otherwise trip a FALSE
                        // "unresponsive" abort ~12s into any sleep started during a join window. Once it
                        // streams, HasStreamed flips and it is monitored normally; a peer that streamed THEN
                        // froze still has HasStreamed=true.
                        if (!rpm.HasStreamedPacket(peer)) continue;
                        if (!rpm.TryGetPeerSilence(peer, out float silence) || silence <= GuestSilenceTimeout)
                            continue;
                        Plugin.Log.LogWarning($"[SLEEP] Crewmate silent {silence:F0}s during {CurrentState}; aborting sleep (frozen/rejoining crewmate)");
                        ShowNotification("Crewmate unresponsive - sleep cancelled", 3f);
                        // From SLEEPING, SleepCancelled is IGNORED by the crew (it only acts in WAITING),
                        // which would free only us and leave the rest wedged at 16x. Broadcast WakeUp instead
                        // (OnWakeUpReceived handles it in SLEEPING); reserve SleepCancelled for the WAITING case.
                        if (CurrentState == SleepState.Sleeping)
                            Plugin.NetworkManager.SendToAllReliable(PacketType.WakeUp, w =>
                                PacketSerializer.WriteWakeUp(w, new WakeUpPacket { WasManual = true }));
                        else
                            Plugin.NetworkManager.SendToAllReliable(PacketType.SleepCancelled, w =>
                                PacketSerializer.WriteSleepCancelled(w, new SleepCancelledPacket { AuthorId = SteamClient.SteamId.Value }));
                        AbortSleep();
                        return;
                    }
                }
            }

            // INDEPENDENT NEEDS: while sleeping, the guest's own sleep stat fills locally (its
            // Sleep.Update is blocked, but PlayerNeeds.LateUpdate still runs). Once fully rested, the
            // guest tells the host so the host's both-rested gate can release the auto-wake. Send once.
            if (CurrentState == SleepState.Sleeping && Plugin.HasConnectedGuest && !Plugin.IsHost &&
                !_sentRested && PlayerNeeds.sleep >= 99.99f)
            {
                Plugin.NetworkManager.SendToAllReliable(PacketType.SleepRested, w => { });
                _sentRested = true;
                VerboseLogger.SleepSend("SleepRested (guest fully rested)");
            }

            // Per-peer rested deadline: a SINGLE slow/never-reporting peer must not pin the AllCrewRested
            // quorum until the crew-wide 60s SleepingBackstop. For BOAT sleep only (tavern runs to the morning
            // gate regardless of rest), force-mark any peer that blows past its per-peer real-time deadline as
            // rested, then re-evaluate the auto-wake: if that was the last hold-out, OnGuestRested's all-rested
            // path would normally fire it, but it's event-driven, so re-check here. Host owns the quorum.
            if (Plugin.IsHost && CurrentState == SleepState.Sleeping && !_isTavernSleep)
            {
                var peers = Plugin.NetworkManager?.ConnectedPeers;
                var rpmDl = SailwindCoop.Player.RemotePlayerManager.Instance;
                if (peers != null)
                {
                    bool forcedAny = false;
                    foreach (var id in peers)
                    {
                        if (_restedPeers.Contains(id)) continue;
                        if (Plugin.IsJoinPendingFor(id)) continue;
                        // Mirror the AllCrewRested getter's late-joiner guard. A peer that has NEVER
                        // streamed a position packet is still LOADING (awake-host join, or post-deferred-join) and
                        // cannot have reported rested yet; the quorum getter skips it, so the deadline loop MUST too.
                        // Without this we'd seed a deadline for a mid-sleep non-JoinPending joiner and force-add it
                        // as rested PerPeerRestedTimeout(75s) later - wrongly completing the quorum off a still-loading
                        // crewmate. JoinPending alone doesn't cover this (it clears the instant the host sends the
                        // join state, while the guest then loads its world for ~15-20s with no skip).
                        if (rpmDl != null && !rpmDl.HasStreamedPacket(id)) continue;
                        // Late joiner (added to ConnectedPeers after SLEEPING began) won't have a deadline yet -
                        // seed one now so it still gets a bounded grace before being force-rested.
                        if (!_restedDeadline.TryGetValue(id, out float deadline))
                        {
                            deadline = Time.unscaledTime + PerPeerRestedTimeout;
                            _restedDeadline[id] = deadline;
                            continue;
                        }
                        if (Time.unscaledTime < deadline) continue;
                        VerboseLogger.SleepEvent($"Per-peer rested deadline blown for {id} ({PerPeerRestedTimeout}s); force-marking it rested so it can't wedge the quorum");
                        _restedPeers.Add(id);
                        forcedAny = true;
                    }
                    // Forcing a laggard rested may have just completed the quorum - actively wake the crew (the
                    // host must also be rested for AllCrewRested; if the host is the hold-out the 60s/90s backstops
                    // below still cover it via HostManualWake).
                    if (forcedAny && AllCrewRested && Sleep.instance != null)
                    {
                        VerboseLogger.SleepEvent("Per-peer deadline completed the all-rested quorum; waking crew");
                        bool wasTavernPP = _isTavernSleep;
                        GameState.eyesFullyClosed = true;
                        Sleep.instance.WakeUp();
                        RestoreLookAfterTavernWake(wasTavernPP);
                        // WakeUp -> OnLocalWakeUp -> TransitionToAwake has already left SLEEPING this
                        // frame; return so we don't fall through into the absolute/SLEEPING backstops below the
                        // same frame (matching the crew-sleep backstop and SleepingBackstop, which also return).
                        return;
                    }
                }
            }

            // Crew-sleep ABSOLUTE backstop: host-side, NOT gated on _localPlayerInBed. The
            // WAITING wedge is a host/peer dragged into WAITING with _localPlayerInBed=false: the only WAITING
            // valve (below) requires _localPlayerInBed, so such a bystander soft-locks until leavers are pruned.
            // This absolute clock (NOT re-stamped on a Sleeping<->Waiting bounce) force-resolves ANY crew-sleep
            // attempt - WAITING or SLEEPING - that persists past CrewSleepBackstop without reaching all-rested.
            // Because a guest desynced into SLEEPING ignores SleepCancelled alone (OnSleepCancelledReceived only
            // acts in WAITING), broadcast BOTH SleepCancelled AND WakeUp, then wake locally. Tavern is included:
            // a tavern sleep that never reaches the morning gate (Sun stalled by an open UI) is just as wedged.
            if (Plugin.IsHost && _crewSleepStartedAt > 0f &&
                Time.unscaledTime - _crewSleepStartedAt > CrewSleepBackstop &&
                !(CurrentState == SleepState.Sleeping && AllCrewRested))
            {
                Plugin.Log.LogWarning($"[SLEEP] CREW-SLEEP absolute backstop timed out ({CrewSleepBackstop}s) in {CurrentState}; force-resolving. localInBed={_localPlayerInBed}, allCrewRested={(CurrentState == SleepState.Sleeping ? AllCrewRested.ToString() : "n/a")}, host_sleep={PlayerNeeds.sleep:F1}%");
                ShowNotification("Sleep timed out", 3f);
                // Broadcast BOTH: SleepCancelled frees anyone stuck in WAITING; WakeUp frees anyone desynced into
                // SLEEPING (which ignores SleepCancelled). Belt-and-suspenders so no peer is left wedged.
                Plugin.NetworkManager.SendToAllReliable(PacketType.SleepCancelled, w =>
                    PacketSerializer.WriteSleepCancelled(w, new SleepCancelledPacket { AuthorId = SteamClient.SteamId.Value }));
                Plugin.NetworkManager.SendToAllReliable(PacketType.WakeUp, w =>
                    PacketSerializer.WriteWakeUp(w, new WakeUpPacket { WasManual = true }));
                // Mark all peers rested + flag a manual wake so the gate can't re-block, then wake locally.
                MarkAllConnectedPeersRested();
                bool wasTavernBackstop = _isTavernSleep; // capture before AbortSleep clears it
                if (CurrentState == SleepState.Sleeping && GameState.sleeping && Sleep.instance != null)
                {
                    Patches.SleepPatches.SleepWakeUpPatch.HostManualWake = true;
                    GameState.eyesFullyClosed = true;
                    Sleep.instance.WakeUp();
                    RestoreLookAfterTavernWake(wasTavernBackstop);
                }
                AbortSleep(); // resets state, time scale, _crewSleepStartedAt (via TransitionToAwake)
                return;
            }

            // GUEST-FREEZE (client-side self-timeout): the GUEST mirror of the host backstops above.
            // Every host backstop is Plugin.IsHost-gated, so a guest whose host is alive-but-wedged (still
            // streaming position -> the 12s host-SILENCE watchdog never fires, not disconnected -> the
            // AbortSleep-on-disconnect path never fires) and which never receives the reliable WakeUp is stuck
            // SLEEPING with Time.timeScale warped - a client freeze. Give the guest its own ABSOLUTE real-time
            // cap: once it has been SLEEPING past GuestSleepingSelfTimeout (75s - ABOVE the host's primary 60s
            // SleepingBackstop so a healthy host always resolves and broadcasts WakeUp FIRST, and BELOW the 90s
            // CrewSleepBackstop; v0.2.34 tightened it from ~120s), self-recover via AbortSleep - which routes
            // through vanilla Sleep.WakeUp + TransitionToAwake, restoring Time.timeScale=1, fixedDeltaTime,
            // restoring Time.timeScale=1, fixedDeltaTime, eyesFullyClosed and clearing the black fade.
            // _sleepingSince is the guest's real-time anchor: stamped in TransitionToSleeping with
            // Time.unscaledTime, bounce-guarded (survives a Sleeping<->Waiting flicker), reset only in
            // TransitionToAwake - so this clock can't be spuriously restarted mid-attempt. Pure safety valve;
            // does NOT change the normal host-driven wake and no wire change.
            if (!Plugin.IsHost && CurrentState == SleepState.Sleeping && _sleepingSince > 0f &&
                Time.unscaledTime - _sleepingSince > GuestSleepingSelfTimeout)
            {
                Plugin.Log.LogWarning($"[SLEEP] GUEST self-timeout ({GuestSleepingSelfTimeout}s) while SLEEPING; host alive-but-wedged (no WakeUp, no disconnect). Self-recovering via AbortSleep. sleepingAge={Time.unscaledTime - _sleepingSince:F0}s, timeScale={Time.timeScale:F1}");
                ShowNotification("Sleep timed out", 3f);
                AbortSleep(); // vanilla WakeUp + TransitionToAwake: restores timeScale/fixedDeltaTime/eyes, clears fade
                return;
            }

            // Host-side SLEEPING backstop. Real-time safety so the crew can
            // never stay wedged asleep at 16x. BIDIRECTIONAL: fires whether a GUEST or the HOST is the
            // laggard - AllCrewRested requires the host rested too, so an AFK/slow HOST could otherwise wedge
            // the crew (the old guest-only backstop missed that). For tavern this also bails out if the Sun
            // stalls before morning (UI open). A manual click already escapes; this is the last-resort valve.
            // N-player (Phase 4): mark EVERY outstanding connected peer as rested so the AllCrewRested quorum
            // can no longer block, AND force the wake past the symmetric gate via HostManualWake even if the
            // host itself hasn't filled (host-laggard case). At N=1 this reduces to the old single-guest backstop.
            if (Plugin.IsHost && CurrentState == SleepState.Sleeping &&
                (_isTavernSleep || !AllCrewRested) &&
                Time.unscaledTime - _sleepingSince > SleepingBackstop)
            {
                Plugin.Log.LogWarning($"[SLEEP] SLEEPING backstop timed out ({SleepingBackstop}s); force-waking crew. allCrewRested={AllCrewRested}, host_sleep={PlayerNeeds.sleep:F1}%");
                MarkAllConnectedPeersRested(); // unblock the quorum gate for the guest-laggard case
                if (Sleep.instance != null)
                {
                    bool wasTavern = _isTavernSleep; // capture before WakeUp->TransitionToAwake clears it
                    // Force the wake past the symmetric gate even if the host hasn't filled (host-laggard
                    // case): HostManualWake makes SleepWakeUpPatch treat this as a manual wake and release.
                    Patches.SleepPatches.SleepWakeUpPatch.HostManualWake = true;
                    GameState.eyesFullyClosed = true;
                    Sleep.instance.WakeUp();
                    RestoreLookAfterTavernWake(wasTavern);
                }
            }

            // Partner gone: covers EVERY disconnect path (HasConnectedGuest flips false the moment the
            // remote player is despawned). Self-clears a WAITING/SLEEPING state with no partner left.
            if (!Plugin.HasConnectedGuest)
            {
                AbortSleep();
                return;
            }

            // WAITING timeout: never hang forever if a SleepRequest/Approved/Cancelled was dropped.
            // Only a player ACTUALLY IN BED self-cancels. The relayed handshake can drag a not-in-bed
            // bystander into WAITING (a passive "crew is assembling" display); its 90s timer must NOT cancel
            // everyone's pending sleep - the genuinely-bedded party's own timeout governs the abort.
            if (CurrentState == SleepState.Waiting && _localPlayerInBed && Time.time - _waitingSince > WaitingTimeout)
            {
                VerboseLogger.SleepEvent($"WAITING state timed out after {WaitingTimeout}s; partner unreachable on sleep handshake, aborting.");
                ShowNotification("Sleep timed out", 3f);
                // Tell the partner to clear their WAITING too, then abort locally.
                Plugin.NetworkManager.SendToAllReliable(PacketType.SleepCancelled, w =>
                    PacketSerializer.WriteSleepCancelled(w, new SleepCancelledPacket { AuthorId = SteamClient.SteamId.Value }));
                AbortSleep();
            }
        }

        #region Local Events (called by patches)

        /// <summary>
        /// Called when local player enters a bed.
        /// </summary>
        public void OnLocalEnterBed(bool isTavern, bool isMoored)
        {
            if (!Plugin.HasConnectedGuest) return;

            _localPlayerInBed = true;
            // GUEST-FREEZE (WAITING window): stamp the absolute "committed to this co-op sleep" clock the moment
            // we enter bed. Bounce-guarded (<= 0f) so a flicker (re-enter while already committed) can't restart
            // it, keeping the guest's WAITING self-timeout measuring true elapsed real time. Cleared only in
            // TransitionToAwake, i.e. when the whole attempt ends. Stamped on host too (harmless: only the guest
            // arm of the Update() self-timeout consumes it), so the field simply tracks "when we entered bed".
            if (_committedToSleepAt <= 0f) _committedToSleepAt = Time.unscaledTime;
            _isTavernSleep = isTavern;

            VerboseLogger.SleepLocal($"Entered bed, tavern={isTavern}, moored={isMoored}, state={CurrentState}");

            // Send request to other player
            var packet = new SleepRequestPacket
            {
                IsTavern = isTavern,
                IsMoored = isMoored,
                AuthorId = SteamClient.SteamId.Value
            };

            Plugin.NetworkManager.SendToAllReliable(PacketType.SleepRequest, w =>
                PacketSerializer.WriteSleepRequest(w, packet));

            VerboseLogger.SleepSend($"SleepRequest, tavern={isTavern}, moored={isMoored}");

            // Update state based on whether the rest of the crew is already waiting.
            // N-player (Phase 4): the host starts sleep only once EVERY connected peer is also in bed
            // (AllCrewInBed). A guest never owns this decision - it just signals and waits for SleepApproved.
            // At N=1 AllCrewInBed reduces to "the single guest is in bed too", identical to _remotePlayerInBed.
            if (Plugin.IsHost ? AllCrewInBed : _inBedPeers.Count > 0)
            {
                // Rest of crew already in bed - transition to sleeping
                if (Plugin.IsHost)
                {
                    TransitionToSleeping();
                }
                // Guest waits for SleepApproved from host
            }
            else
            {
                // We're first (or still waiting on others) - enter waiting state
                TransitionToWaiting(isLocalWaiting: true);
            }
        }

        /// <summary>
        /// Called when local player leaves bed (during WAITING state).
        /// </summary>
        public void OnLocalLeaveBed()
        {
            if (!Plugin.HasConnectedGuest) return;
            if (!_localPlayerInBed) return;

            _localPlayerInBed = false;

            VerboseLogger.SleepLocal($"Left bed, state={CurrentState}");

            if (CurrentState == SleepState.Waiting)
            {
                // Send cancellation
                Plugin.NetworkManager.SendToAllReliable(PacketType.SleepCancelled, w =>
                    PacketSerializer.WriteSleepCancelled(w, new SleepCancelledPacket { AuthorId = SteamClient.SteamId.Value }));

                VerboseLogger.SleepSend("SleepCancelled");

                TransitionToAwake();
            }
        }

        /// <summary>
        /// Called when local player triggers wake (click during eyes-open or auto at 99.99%).
        /// </summary>
        public void OnLocalWakeUp(bool wasManual)
        {
            if (!Plugin.HasConnectedGuest) return;
            if (CurrentState != SleepState.Sleeping) return;

            VerboseLogger.SleepLocal($"WakeUp triggered, manual={wasManual}");

            var packet = new WakeUpPacket { WasManual = wasManual };

            Plugin.NetworkManager.SendToAllReliable(PacketType.WakeUp, w =>
                PacketSerializer.WriteWakeUp(w, packet));

            VerboseLogger.SleepSend($"WakeUp, manual={wasManual}");

            // Both host and guest transition locally (symmetric handling). vanillaWakeFollows: this is
            // called from SleepWakeUpPatch.Prefix, which returns true right after -> vanilla Sleep.WakeUp
            // runs next and does the full teardown (fade, justWokeUp grace, etc.); we must not pre-clear
            // eyesFullyClosed here or it early-returns.
            TransitionToAwake(vanillaWakeFollows: true);
        }

        #endregion

        #region Network Handlers

        /// <summary>
        /// Called when remote player enters bed.
        /// </summary>
        public void OnSleepRequestReceived(SleepRequestPacket packet, SteamId sender = default)
        {
            // N-player: AUTHOR = the crew member who actually entered bed (survives the host relay; falls back
            // to the transport sender on a direct send). The host re-broadcasts to the OTHER guests so they see
            // the crew assembling and get an accurate count, since guests peer only with the host.
            SteamId author = packet.AuthorId != 0 ? (SteamId)packet.AuthorId : sender;
            if (Plugin.IsHost)
                Plugin.NetworkManager.SendToAllExcept(sender, PacketType.SleepRequest,
                    w => PacketSerializer.WriteSleepRequest(w, packet));

            VerboseLogger.SleepRecv($"SleepRequest, tavern={packet.IsTavern}, moored={packet.IsMoored}");

            _inBedPeers.Add(author);

            // Context mismatch check
            if (_localPlayerInBed && _isTavernSleep != packet.IsTavern)
            {
                // Mismatch - show warning, don't track remote as in bed for sleep purposes
                _inBedPeers.Remove(author);
                // Only clear the pending tavern charge when the LOCAL player is the one on the BOAT
                // (!_isTavernSleep). This branch rejects the REMOTE crewmate's differing context - if the LOCAL
                // player is the genuine tavern sleeper (_isTavernSleep), its pending room charge + sleepingInTavern
                // are still valid and must be preserved; clearing them here would let the host sleep for FREE once
                // a matching tavern crewmate later assembled (TryChargePendingTavern would find nothing). Genuine
                // aborts still clear via TransitionToAwake.
                if (!_isTavernSleep)
                {
                    TradingSyncManager.ClearPendingTavern();
                    GameState.sleepingInTavern = false;
                }
                string msg = packet.IsTavern
                    ? "Crewmate is in a tavern bed"
                    : "Crewmate is on the boat";
                ShowNotification(msg, 3f);
                VerboseLogger.SleepEvent($"Context mismatch: local tavern={_isTavernSleep}, remote tavern={packet.IsTavern}");
                return;
            }

            if (_localPlayerInBed)
            {
                // N-player (Phase 4): the host starts sleep only once the WHOLE crew is in bed. A guest
                // (the host can't be the sender here) just keeps waiting for SleepApproved. At N=1 the host
                // is the only one who reaches this with one peer, and AllCrewInBed == "that peer + host".
                if (Plugin.IsHost)
                {
                    if (AllCrewInBed)
                    {
                        TransitionToSleeping();
                    }
                    else if (CurrentState == SleepState.Awake)
                    {
                        // Local already in bed, but some crew still out: stay/become WAITING for the rest.
                        TransitionToWaiting(isLocalWaiting: false);
                    }
                }
                // Guest waits for SleepApproved
            }
            else
            {
                // Remote is waiting for us
                TransitionToWaiting(isLocalWaiting: false);
            }
        }

        /// <summary>
        /// Called when we're notified someone is waiting.
        /// </summary>
        public void OnSleepWaitingReceived(SleepWaitingPacket packet, SteamId sender = default)
        {
            VerboseLogger.SleepRecv($"SleepWaiting, tavern={packet.IsTavern}");

            // N-player: AUTHOR = the waiting crew member; host re-broadcasts to the OTHER guests (star topology).
            SteamId author = packet.AuthorId != 0 ? (SteamId)packet.AuthorId : sender;
            if (Plugin.IsHost)
                Plugin.NetworkManager.SendToAllExcept(sender, PacketType.SleepWaiting,
                    w => PacketSerializer.WriteSleepWaiting(w, packet));

            // Mirror the context-mismatch guard from OnSleepRequestReceived. A stale/follow-up
            // SleepWaiting with the wrong context must not clobber our tavern/boat context and undo a
            // prior rejection. Only adopt the remote's context when we are NOT in bed.
            if (_localPlayerInBed && _isTavernSleep != packet.IsTavern)
            {
                ShowNotification(packet.IsTavern ? "Crewmate is in a tavern bed" : "Crewmate is on the boat", 3f);
                return;
            }
            _inBedPeers.Add(author);
            if (!_localPlayerInBed) _isTavernSleep = packet.IsTavern;

            if (CurrentState == SleepState.Awake)
            {
                ShowNotification("Crew is waiting to sleep", 10f);
            }
        }

        /// <summary>
        /// Called when sleep is approved (both in bed). Guest only.
        /// </summary>
        public void OnSleepApprovedReceived(SleepApprovedPacket packet)
        {
            if (Plugin.IsHost) return; // Host doesn't receive this

            VerboseLogger.SleepRecv($"SleepApproved, tavern={packet.IsTavern}, timeskip={packet.IsTimeskip}");

            _isTavernSleep = packet.IsTavern;
            _isTimeskipEnabled = packet.IsTimeskip;

            // INDEPENDENT NEEDS: the guest's FallAsleep is blocked, so it never sets Sleep.timeskipSleep
            // itself. Without it, Sun.Update keeps the guest clock at 1x while the host runs 9x (moored),
            // so the guest barely fills. Mirror the host's timeskip locally. Vanilla WakeUp resets it,
            // and TransitionToAwake also clears it defensively.
            if (packet.IsTimeskip)
            {
                Sleep.timeskipSleep = true;
            }

            TransitionToSleeping();
        }

        /// <summary>
        /// Called when waiting player leaves bed.
        /// </summary>
        public void OnSleepCancelledReceived(SleepCancelledPacket packet, SteamId sender = default)
        {
            VerboseLogger.SleepRecv("SleepCancelled");

            // N-player: AUTHOR = the crew member who left bed; host re-broadcasts to the OTHER guests so they
            // also drop that peer and abort a WAITING handshake (without it a 3rd guest stays stuck "Waiting").
            SteamId author = packet.AuthorId != 0 ? (SteamId)packet.AuthorId : sender;
            if (Plugin.IsHost)
                Plugin.NetworkManager.SendToAllExcept(sender, PacketType.SleepCancelled,
                    w => PacketSerializer.WriteSleepCancelled(w, packet));

            _inBedPeers.Remove(author);

            // N-player (Phase 4): if anyone in the crew backs out during the handshake, the whole sleep is
            // off (the host can't start a partial-crew warp). At N=1 the lone peer leaving bed cancels the
            // sleep, identical to the old _remotePlayerInBed=false + abort.
            if (CurrentState == SleepState.Waiting)
            {
                ShowNotification("Crewmate left bed", 3f);
                TransitionToAwake();
                // Sleep-cancel softlock guard: TransitionToAwake sets _localPlayerInBed=false even
                // though THIS player may still be physically in its bunk, which permanently breaks
                // AllCrewInBed for the next sleep. Re-sync the flag to physical reality (scoped to the
                // cancel path so other TransitionToAwake callers are unaffected).
                _localPlayerInBed = (GameState.inBed != null);
            }
        }

        /// <summary>
        /// Called during boat sleep to sync visual state. Guest only.
        /// </summary>
        public void OnSleepCycleStateReceived(SleepCycleStatePacket packet)
        {
            if (Plugin.IsHost) return;

            // Ignore stale packets that arrive after we've already woken up
            if (CurrentState != SleepState.Sleeping)
            {
                VerboseLogger.SleepRecv($"SleepCycleState ignored - already in state {CurrentState}");
                return;
            }

            VerboseLogger.SleepRecv($"SleepCycleState, eyesClosed={packet.EyesClosed}, timeScale={packet.TimeScale}");

            ApplySleepCycleState(packet);
        }

        /// <summary>
        /// Called when sleep ends (either player triggered wake).
        /// </summary>
        public void OnWakeUpReceived(WakeUpPacket packet, SteamId sender = default)
        {
            // Guard against duplicate processing (e.g., both sides wake simultaneously)
            if (CurrentState != SleepState.Sleeping)
            {
                VerboseLogger.SleepRecv($"WakeUp ignored - already in state {CurrentState}");
                return;
            }

            // Star-relay: a guest's manual wake must reach the OTHER guests, or they stay wedged
            // asleep at timeScale=16. Host re-broadcasts to everyone except the originator (reliable).
            if (Plugin.IsHost)
                Plugin.NetworkManager.SendToAllExcept(sender, PacketType.WakeUp, w =>
                    PacketSerializer.WriteWakeUp(w, packet));

            VerboseLogger.SleepRecv($"WakeUp, manual={packet.WasManual}");

            bool wasTavern = _isTavernSleep; // capture before TransitionToAwake clears it

            // Force eyesFullyClosed so WakeUp() doesn't early-return
            // during the 3s fade period
            GameState.eyesFullyClosed = true;

            // Must transition state BEFORE calling WakeUp so the patch allows it through. vanillaWakeFollows:
            // this leaves GameState.sleeping/eyes intact so vanilla WakeUp below does the richer teardown
            // (smooth fade, justWokeUp grace) - pre-clearing them here would make WakeUp early-return AND make
            // the `&& GameState.sleeping` guard below skip it entirely (review BLOCKER 1).
            TransitionToAwake(vanillaWakeFollows: true);

            // Actually wake up the game (triggers fade-in, exits bed, etc.)
            // The WakeUp patch prefix checks CurrentState - since we're now AWAKE, it will proceed
            if (Sleep.instance != null && GameState.sleeping)
            {
                Sleep.instance.WakeUp();
            }
            else if (GameState.sleeping || GameState.eyesFullyClosed)
            {
                // Half-torn fallback: vanilla WakeUp couldn't run (no Sleep.instance / flag desync), so
                // the vanillaWakeFollows skip above would leave the black screen up - force it awake now.
                ForceVanillaAwakeState();
            }
            RestoreLookAfterTavernWake(wasTavern); // vanilla WakeUp skipped this (flag already cleared)
        }

        /// <summary>
        /// Host: a crewmate reported it is fully rested (zero-payload SleepRested packet). Records that
        /// peer; once the WHOLE crew is rested (AllCrewRested) the auto-wake gate in SleepWakeUpPatch opens.
        /// </summary>
        // N-player (Phase 4): `sender` is added to _restedPeers and the quorum recomputed. At N=1 the only
        // peer is the single guest, so AllCrewRested flips true exactly when that guest reports rested AND
        // the host is rested - identical to the old single-guest gate.
        public void OnGuestRested(SteamId sender = default)
        {
            _restedPeers.Add(sender);
            VerboseLogger.SleepEvent($"Crewmate {sender} reported fully rested; rested {_restedPeers.Count}/{(Plugin.NetworkManager?.ConnectedPeers.Count ?? 0)} peers. host_sleep={PlayerNeeds.sleep:F1}%, all_crew_rested={AllCrewRested}");
            VerboseLogger.SleepRecv("SleepRested (crewmate fully rested)");

            // TAVERN-TIME: a tavern sleep must run to the vanilla 7-10am morning gate, NOT wake the
            // instant the crew is rested (~1s of warp left players stuck mid-night). Just record the rest and
            // let the warp continue; the morning DurationCap releases the wake gate. Boat sleep keeps the
            // active "wake the moment the WHOLE crew is fully rested" behavior below.
            if (_isTavernSleep) return;

            // INDEPENDENT NEEDS: for BOAT sleep, actively wake the crew the instant EVERYONE is rested,
            // rather than relying on the vanilla boat cap re-firing (the window can be missed during a long
            // warp). Routes through SleepWakeUpPatch; the gate passes now that AllCrewRested==true.
            if (Plugin.IsHost && CurrentState == SleepState.Sleeping &&
                AllCrewRested && Sleep.instance != null)
            {
                bool wasTavern = _isTavernSleep; // capture before WakeUp->TransitionToAwake clears it
                GameState.eyesFullyClosed = true; // so Sleep.WakeUp doesn't early-return mid-fade
                Sleep.instance.WakeUp();
                RestoreLookAfterTavernWake(wasTavern);
            }
        }

        #endregion

        /// <summary>
        /// Re-enable mouse-look after a TAVERN wake. Vanilla Sleep.WakeUp only re-enables look
        /// inside `if (GameState.sleepingInTavern)`, but our wake paths clear that flag (via
        /// TransitionToAwake) before/around the vanilla WakeUp, so the guard is false and look stays
        /// stuck (boat sleep is unaffected because LeaveBed re-enables it). Call this AFTER the vanilla
        /// Sleep.WakeUp on every tavern-ending path, passing the tavern flag captured BEFORE it was
        /// cleared. Guarded so it never runs while in a bed (boat case re-enables via LeaveBed).
        /// Mirrors Recovery.cs, which is why "Recover Boat" happened to unstick the camera.
        /// </summary>
        private static void RestoreLookAfterTavernWake(bool wasTavern)
        {
            if (!wasTavern) return;
            if (GameState.inBed) return;
            MouseLook.ToggleMouseLook(newState: true);
            VerboseLogger.SleepEvent("Re-enabled MouseLook after tavern wake");
        }

        #region State Transitions

        private void TransitionToWaiting(bool isLocalWaiting)
        {
            // Stamp the absolute crew-sleep clock the FIRST time this attempt leaves AWAKE, and only then.
            // A Sleeping<->Waiting bounce (e.g. a peer re-entering the handshake) must NOT reset it, or the
            // absolute backstop never accumulates. TransitionToAwake zeroes it when the whole attempt ends.
            if (_crewSleepStartedAt <= 0f) _crewSleepStartedAt = Time.unscaledTime;

            CurrentState = SleepState.Waiting;
            _waitingSince = Time.time;
            HideNotification();

            if (isLocalWaiting)
            {
                ShowNotification($"Waiting for crew{CrewInBedSuffix()}...", 60f);

                // Notify partner
                var packet = new SleepWaitingPacket { IsTavern = _isTavernSleep, AuthorId = SteamClient.SteamId.Value };
                Plugin.NetworkManager.SendToAllReliable(PacketType.SleepWaiting, w =>
                    PacketSerializer.WriteSleepWaiting(w, packet));

                VerboseLogger.SleepSend($"SleepWaiting, tavern={_isTavernSleep}");
            }
            else
            {
                ShowNotification("Crew is waiting to sleep", 60f);
            }

            VerboseLogger.SleepEvent($"State -> WAITING, localWaiting={isLocalWaiting}");
        }

        private void TransitionToSleeping()
        {
            CurrentState = SleepState.Sleeping;
            // Unscaled so the 60s backstop is REAL time, not 16x warp time.
            // Guard against a Sleeping<->Waiting bounce re-stamping the backstop clock. TransitionToAwake
            // zeroes _sleepingSince when the WHOLE attempt ends; until then a re-entry of TransitionToSleeping
            // (e.g. WAITING -> SLEEPING after a peer rejoined the handshake) must keep the original start so the
            // SleepingBackstop measures the true elapsed warp, not "just restarted".
            if (_sleepingSince <= 0f) _sleepingSince = Time.unscaledTime;
            // Ensure the absolute crew-sleep clock is stamped even on a direct AWAKE -> SLEEPING (the host
            // when AllCrewInBed was already true skips WAITING). Only on first entry of this attempt.
            if (_crewSleepStartedAt <= 0f) _crewSleepStartedAt = Time.unscaledTime;
            HideNotification();

            // Per-peer rested deadline: seed/extend a real-time deadline for every connected peer that has
            // not yet reported rested, so one slow peer can be force-rested before the crew-wide backstop fires.
            // Done on the HOST (quorum owner) for BOAT sleep; tavern runs to the morning gate, so no deadlines.
            if (Plugin.IsHost && !_isTavernSleep)
            {
                var dlPeers = Plugin.NetworkManager?.ConnectedPeers;
                if (dlPeers != null)
                    foreach (var id in dlPeers)
                        if (!_restedDeadline.ContainsKey(id))
                            _restedDeadline[id] = Time.unscaledTime + PerPeerRestedTimeout;
            }

            // INDEPENDENT NEEDS: on-screen note - either player can wake, crew auto-wakes once everyone
            // is fully rested. NotificationUi.text is a fixed-scale, non-wrapping world-space
            // TextMesh, so keep this short with explicit line breaks to fit inside the scroll graphic.
            ShowNotification(
                "Sleeping...\nClick to wake.\nCrew wakes when all rested.",
                12f);

            // Determine if timeskip is enabled.
            // Use the SHARED boat for the moored check, not the local player's current boat.
            var sharedBoat = BoatUtility.FindBoatByName(_sharedBoatName);
            bool isMoored = sharedBoat != null &&
                           sharedBoat.GetComponent<BoatMooringRopes>()?.AnyRopeMoored() == true;
            // VANILLA RATE-COUPLING (reverts the v0.2.15 "force timeskip on every co-op sleep" change):
            // the 9x Sun timeskip (Sleep.timeskipSleep -> Sun.sun.timescale = initialTimescale*9 in Sun.Update)
            // is only enabled where VANILLA has it - moored/tavern. The v0.2.15 rationale ("sleep need crawled
            // at 1x") misread PlayerNeeds.Update: `sleep += Time.deltaTime * 8f * Sun.sun.timescale` already
            // runs at the 16x warped deltaTime, so a vanilla UNMOORED nap fills at 16x too - clock, physics
            // and rest all consistent, capped at 4.5 game-hours (a deliberate partial-rest nap). Forcing the
            // 9x underway ran clock+rest at 144x while boat physics stayed at 16x: the bar filled in ~10s,
            // the AllCrewRested quorum auto-woke, and the boat had barely moved. _isTimeskipEnabled drives
            // the guest's Sleep.timeskipSleep (OnSleepApprovedReceived via SleepApproved.IsTimeskip) and the
            // SleepPatches boat-cap scoping (IsTimeskipEnabled).
            _isTimeskipEnabled = isMoored || _isTavernSleep;
            // Host: drive Sun's timescale immediately for the moored/tavern case. Vanilla FallAsleep also sets
            // it there (decomp Sleep.FallAsleep only sets timeskipSleep TRUE for moored/tavern and leaves it
            // UNTOUCHED otherwise - it never sets it false), but our FallAsleep timing is gated, so set it ONCE
            // here. Sun.Update re-reads the flag every frame, so this single set holds the 9x for the whole warp;
            // no per-frame re-assert is needed. NEVER set for an unmoored sleep (vanilla keeps Sun at 1x there).
            // Cleared on every wake by TransitionToAwake (and vanilla WakeUp).
            if (Plugin.IsHost && _isTimeskipEnabled) Sleep.timeskipSleep = true;

            VerboseLogger.SleepEvent($"State -> SLEEPING, tavern={_isTavernSleep}, moored={isMoored}, timeskip={_isTimeskipEnabled}");

            if (Plugin.IsHost)
            {
                // TAVERN (unified): charge the crew's room ONCE, now that sleep is actually starting (both
                // players committed). If the crew can't afford it, cancel the whole handshake instead of
                // sleeping for free. Only call FallAsleep after a successful (or no-op) charge.
                if (_isTavernSleep && !TradingSyncManager.TryChargePendingTavern())
                {
                    VerboseLogger.SleepEvent("Tavern charge failed at SLEEPING transition (insufficient funds); aborting handshake");
                    ShowNotification("Not enough money.", 3f);
                    Plugin.NetworkManager.SendToAllReliable(PacketType.SleepCancelled, w =>
                        PacketSerializer.WriteSleepCancelled(w, new SleepCancelledPacket { AuthorId = SteamClient.SteamId.Value }));
                    AbortSleep();
                    return;
                }

                // Send approval to guest
                var packet = new SleepApprovedPacket
                {
                    IsTavern = _isTavernSleep,
                    IsTimeskip = _isTimeskipEnabled
                };

                Plugin.NetworkManager.SendToAllReliable(PacketType.SleepApproved, w =>
                    PacketSerializer.WriteSleepApproved(w, packet));

                VerboseLogger.SleepSend($"SleepApproved, tavern={_isTavernSleep}, timeskip={_isTimeskipEnabled}");

                // Boat sleep: the game's Sleep.Update calls FallAsleep (player is inBed). Taverns have NO bed,
                // so trigger it explicitly now that state==SLEEPING (the FallAsleep patch will allow it).
                if (_isTavernSleep && Sleep.instance != null)
                {
                    GameState.sleepingInTavern = true;
                    Sleep.instance.FallAsleep();
                }

                // Host starts sleep - let Sleep.FallAsleep() run naturally (boat case)
                // The patch will send cycle states to guest
            }
            else
            {
                // Guest: immediately start fade to black to match host's 3s fade
                // This runs before host's cycle state arrives (which comes after 3.1s)
                GameState.sleeping = true;
                StartCoroutine(Blackout.FadeTo(1f, 3f));
                VerboseLogger.SleepApply("Guest starting fade to black");
            }
        }

        /// <param name="vanillaWakeFollows">True when the CALLER runs vanilla Sleep.WakeUp immediately
        /// after this (the normal wake paths: SleepWakeUpPatch.Prefix returns true right after
        /// OnLocalWakeUp; OnWakeUpReceived calls WakeUp next). In that case vanilla WakeUp does the proper
        /// teardown (smooth 5s fade, GameState.justWokeUp post-wake boat-damage grace, sleepCooldown,
        /// ocean-renderer kick) and we must NOT pre-clear GameState.eyesFullyClosed here - doing so makes
        /// vanilla WakeUp early-return (its `if (!eyesFullyClosed) return`) and skip ALL of that on every
        /// healthy wake (review BLOCKER 1). Only the abort/teardown paths (default false) force-clear.</param>
        private void TransitionToAwake(bool vanillaWakeFollows = false)
        {
            // Arm the boat-bunk control-restore watchdog only after a REAL co-op wake (leaving the
            // SLEEPING state), so it never fires in solo or during the vanilla pre-sleep cooldown.
            if (CurrentState == SleepState.Sleeping) _wokeFromCoopSleepAt = Time.unscaledTime;

            CurrentState = SleepState.Awake;
            _localPlayerInBed = false;
            _inBedPeers.Clear();
            _isTavernSleep = false;
            _isTimeskipEnabled = false;
            // TAVERN (unified): clear the tavern flag on EVERY abort/wake path (this is the single chokepoint
            // for WAITING timeout, context-mismatch, partner-left, disconnect, and leave-bed-while-waiting), so
            // a stale sleepingInTavern can't corrupt the next boat sleep. Also drop any pending (uncharged)
            // tavern room so a cancelled attempt isn't charged later.
            GameState.sleepingInTavern = false;
            TradingSyncManager.ClearPendingTavern();
            // INDEPENDENT NEEDS: reset the all-rested gate for the next sleep.
            _restedPeers.Clear();
            _sentRested = false;
            // End of the WHOLE crew-sleep attempt - zero both backstop clocks and the per-peer rested
            // deadlines so the NEXT attempt re-stamps fresh (the bounce guards key off <= 0f). This is the only
            // place they reset, which is exactly why an in-attempt Sleeping<->Waiting bounce preserves them.
            _crewSleepStartedAt = 0f;
            _sleepingSince = 0f;
            // GUEST-FREEZE (WAITING window): end of the whole commit - zero the guest's absolute committed clock
            // so the next enter-bed re-stamps fresh (the OnLocalEnterBed guard keys off <= 0f). This is the only
            // reset point, which is why an in-attempt bounce (leave/re-enter mid-handshake) preserves it.
            _committedToSleepAt = 0f;
            _restedDeadline.Clear();
            // Defensive: clear the guest-side timeskip mirror on wake. Vanilla WakeUp already
            // resets Sleep.timeskipSleep=false, but clear it here too in case wake routed around it.
            Sleep.timeskipSleep = false;
            // (v0.2.34) Stale wake-intent flags must not leak into the NEXT sleep: HostManualWake set by a
            // backstop whose WakeUp never reached the gate (half-torn state) would make the next sleep's
            // first AUTO wake read as "manual" and release the crew instantly.
            Patches.SleepPatches.SleepWakeUpPatch.HostManualWake = false;
            Patches.SleepPatches.SleepWakeUpPatch.GuestManualWake = false;
            HideNotification();

            // (v0.2.34) SELF-SUFFICIENT TEARDOWN. Every abort/cancel/disconnect path funnels here, but the
            // vanilla restore used to depend on Sleep.WakeUp having run AND succeeded - it early-returns
            // while "falling asleep" (!eyesFullyClosed), needs Sleep.instance, and some paths transition
            // without calling it at all (guest WAITING fake-sleep, exception mid-wake). The half-torn result
            // (mod state Awake, vanilla GameState.sleeping still true, black screen up) was permanent: the
            // old AbortSleep early-returned on CurrentState==Awake, so no disconnect, lobby close, or
            // re-invite could ever repair it - the reported "host permanently stuck sleeping". Force the
            // vanilla flags/screen awake here whenever they survived the wake path - UNLESS the caller is
            // about to run vanilla WakeUp itself (vanillaWakeFollows), which does the richer teardown.
            if (!vanillaWakeFollows && (GameState.sleeping || GameState.eyesFullyClosed))
            {
                ForceVanillaAwakeState();
            }

            VerboseLogger.SleepEvent("State -> AWAKE");

            // Restore normal time scale if we were sleeping
            Time.timeScale = 1f;
            Time.fixedDeltaTime = 0.02222f;
            if (_vanillaMaxDeltaTime.HasValue) Time.maximumDeltaTime = _vanillaMaxDeltaTime.Value; // undo the 16x catch-up bound (GLOBAL - restore the game's own value on every wake path)
        }

        /// <summary>
        /// (v0.2.34) Force the vanilla sleep flags + black screen awake WITHOUT relying on vanilla
        /// Sleep.WakeUp. Used only on paths where vanilla WakeUp cannot or did not run (abort/disconnect/
        /// session-start, or a wake path where Sleep.instance was null / WakeUp early-returned). NOT used on
        /// the healthy wake paths - there vanilla WakeUp does a richer teardown (smooth fade, justWokeUp
        /// grace, sleepCooldown), so pre-empting it here would degrade every co-op wake (review BLOCKER 1).
        /// </summary>
        private static void ForceVanillaAwakeState()
        {
            Plugin.Log.LogWarning($"[SLEEP] Vanilla sleep state survived the wake path (sleeping={GameState.sleeping}, eyes={GameState.eyesFullyClosed}); force-clearing");
            GameState.sleeping = false;
            GameState.eyesFullyClosed = false;
            GameState.sleepingInTavern = false;
            ForceClearBlackout();
            // Vanilla WakeUp restores control only when not in a bed (a bunk sleeper gets up manually
            // via LeaveBed); mirror that exactly.
            if (GameState.inBed == null) Refs.SetPlayerControl(true);
        }

        /// <summary>
        /// (v0.2.34) Kill the black sleep screen instantly, without vanilla's fade coroutine. Vanilla
        /// Blackout.FadeTo lerps with SCALED Time.deltaTime on the camera's OVRScreenFade - a fade started
        /// around a timeScale=0 pause (ESC menu) never completes, and on the abort paths we want the screen
        /// back NOW regardless of timescale. OVRScreenFade lives in Oculus.VR (not referenced at compile
        /// time), so resolve the component by name and invoke SetFadeLevel reflectively; failure is benign
        /// (worst case the vanilla fade finishes whenever it can).
        /// </summary>
        private static void ForceClearBlackout()
        {
            try
            {
                var cam = Camera.main;
                if (cam == null) return;
                var fade = cam.GetComponent("OVRScreenFade");
                if (fade == null) return;
                var method = fade.GetType().GetMethod("SetFadeLevel");
                method?.Invoke(fade, new object[] { 0f });
            }
            catch (System.Exception e)
            {
                VerboseLogger.SleepEvent($"ForceClearBlackout failed (benign): {e.Message}");
            }
        }

        #endregion

        #region Visual State (Guest)

        // Sailwind's own Time.maximumDeltaTime (0.1, NOT Unity's 0.3333 default), lazily captured before the
        // first warp override so every restore site can write back the game's true value. Static: survives
        // manager resets so a mid-session re-capture can never snapshot our own warped override.
        private static float? _vanillaMaxDeltaTime;

        /// <summary>
        /// (v0.2.34) Bound Time.maximumDeltaTime for a warp on the HOST, exactly as the guest's
        /// ApplySleepCycleState does: min(vanilla value, fixedDeltaTime*2). The guest got this bound in
        /// v0.2.19 (its FixedUpdate spiral froze it); the host never did - with the current 0.1 config the
        /// bound is a no-op there, but it costs nothing and protects the host if a game update or another
        /// mod ever loosens maximumDeltaTime. Restored on every un-warp path via _vanillaMaxDeltaTime.
        /// </summary>
        public static void BoundMaxDeltaTimeForWarp()
        {
            if (!_vanillaMaxDeltaTime.HasValue) _vanillaMaxDeltaTime = Time.maximumDeltaTime;
            Time.maximumDeltaTime = Mathf.Min(_vanillaMaxDeltaTime.Value, Time.fixedDeltaTime * 2f);
        }

        private void ApplySleepCycleState(SleepCycleStatePacket packet)
        {
            VerboseLogger.SleepApply($"Applying cycle state: eyesClosed={packet.EyesClosed}, timeScale={packet.TimeScale}, fixedDeltaTime={packet.FixedDeltaTime}");

            Time.timeScale = packet.TimeScale;
            Time.fixedDeltaTime = packet.FixedDeltaTime;
            // Bound how much scaled time one render frame may consume. At 16x a single slow frame
            // otherwise owes up to maximumDeltaTime/fixedDeltaTime physics steps, which snowballs into
            // ever-slower frames -> a guest HARD FREEZE on unmoored 16x sleeps.
            // Sailwind CONFIGURES Time.maximumDeltaTime to 0.1 (not Unity's 0.3333 default), so capture the
            // game's own value BEFORE our first override and only ever TIGHTEN it (never loosen: for Sailwind
            // min(0.1, fixedDeltaTime*2) keeps 0.1, already tight enough). RESTORE the captured value on every
            // un-warp path (here at 1x, TransitionToAwake, orphan-timewarp self-heal) since maximumDeltaTime
            // is GLOBAL and writing a wrong constant would corrupt solo play after co-op.
            if (!_vanillaMaxDeltaTime.HasValue) _vanillaMaxDeltaTime = Time.maximumDeltaTime;
            Time.maximumDeltaTime = packet.TimeScale > 1f
                ? Mathf.Min(_vanillaMaxDeltaTime.Value, packet.FixedDeltaTime * 2f)
                : _vanillaMaxDeltaTime.Value;

            // Trigger fade
            if (packet.FadeDuration > 0)
            {
                StartCoroutine(Blackout.FadeTo(packet.FadeTarget, packet.FadeDuration));
            }

            // Set eyes state
            GameState.eyesFullyClosed = packet.EyesClosed;
        }

        #endregion

        #region Host: Send Cycle State

        /// <summary>
        /// Called by patches when host's sleep cycle changes.
        /// </summary>
        public void SendSleepCycleState(bool eyesClosed, float timeScale, float fixedDeltaTime, float fadeTarget, float fadeDuration)
        {
            if (!Plugin.IsHost) return;
            if (CurrentState != SleepState.Sleeping) return;

            var packet = new SleepCycleStatePacket
            {
                EyesClosed = eyesClosed,
                TimeScale = timeScale,
                FixedDeltaTime = fixedDeltaTime,
                FadeTarget = fadeTarget,
                FadeDuration = fadeDuration
            };

            Plugin.NetworkManager.SendToAllReliable(PacketType.SleepCycleState, w =>
                PacketSerializer.WriteSleepCycleState(w, packet));

            VerboseLogger.SleepSend($"SleepCycleState, eyesClosed={eyesClosed}, timeScale={timeScale}, fixedDeltaTime={fixedDeltaTime}");
        }

        #endregion

        #region Notifications

        /// <summary>
        /// N-player (Phase 4): " (in-bed/total)" crew count for the waiting notice. Total crew = connected
        /// peers + this player. In-bed = tracked peers in bed + (this player if in bed). At N=1 (2-player)
        /// this reads "(1/2)" while one crewmate waits - a cosmetic enrichment of the old "Waiting for
        /// partner..." string. Returns "" if we somehow have no peers (defensive).
        /// </summary>
        private string CrewInBedSuffix()
        {
            // N-player: total crew = the whole lobby (host + guests). On a GUEST, ConnectedPeers is just {host}
            // (star topology), so use the lobby member count for the true crew size; on the host the two agree.
            // inBed counts the per-author in-bed set (now populated on guests too via the relayed handshake) + self.
            int total = Plugin.LobbyManager?.GetMemberCount() ?? ((Plugin.NetworkManager?.ConnectedPeers.Count ?? 0) + 1);
            if (total <= 1) return "";
            int inBed = _inBedPeers.Count + (_localPlayerInBed ? 1 : 0);
            return $" ({inBed}/{total})";
        }

        private void ShowNotification(string message, float duration)
        {
            if (NotificationUi.instance != null)
            {
                NotificationUi.instance.ShowNotification(message, duration);
            }
            else
            {
                Plugin.Log.LogInfo($"[SleepSync] {message}");
            }
        }

        private void HideNotification()
        {
            if (NotificationUi.instance != null)
            {
                // Set timer to 0 by showing empty notification - triggers hide on next Update
                NotificationUi.instance.ShowNotification("", 0f);
            }
        }

        #endregion

        #region Lifecycle

        public void Reset()
        {
            TransitionToAwake();
        }

        /// <summary>
        /// Force-end any in-progress sleep (WAITING or SLEEPING) and restore normal time/state.
        /// Safe to call repeatedly. Routes through the game's WakeUp so the bed is exited, the black
        /// fade clears and timeScale is restored - not just the mod flags.
        /// </summary>
        public void AbortSleep()
        {
            // (v0.2.34) Also proceed when the mod already thinks it is Awake but VANILLA sleep state
            // survived (half-torn wedge: black screen up, GameState.sleeping true). The old early-return
            // made that state permanent - no later disconnect/lobby-close/re-invite could repair it.
            if (CurrentState == SleepState.Awake && !GameState.sleeping && !GameState.eyesFullyClosed) return;

            Plugin.Log.LogInfo($"[SLEEP] AbortSleep from {CurrentState} (vanillaSleeping={GameState.sleeping})");

            bool wasTavern = _isTavernSleep; // capture before WakeUp/TransitionToAwake clears it

            // If mid-sleep, force eyes closed (so Sleep.WakeUp doesn't early-return) then wake, which
            // exits the bed and clears the fade. TransitionToAwake then resets flags + time scale.
            if (GameState.sleeping && Sleep.instance != null)
            {
                GameState.eyesFullyClosed = true;
                Sleep.instance.WakeUp();
            }
            RestoreLookAfterTavernWake(wasTavern);

            TransitionToAwake();
        }

        /// <summary>
        /// Host-only: force-end any in-progress co-op sleep AND tell the guest to wake. Used
        /// before recovery (host faint or manual Recover button). AbortSleep alone does NOT broadcast a
        /// WakeUp; OnLocalWakeUp alone does NOT call vanilla Sleep.WakeUp. This does BOTH.
        /// </summary>
        public void ForceWakeCrew()
        {
            // (v0.2.34) Same half-torn-state allowance as AbortSleep: a vanilla sleep that survived a
            // failed wake must still be forcible (the broadcast is harmless to awake peers - WakeUp is
            // ignored outside SLEEPING).
            if (CurrentState == SleepState.Awake && !GameState.sleeping && !GameState.eyesFullyClosed) return;

            VerboseLogger.SleepEvent($"ForceWakeCrew from {CurrentState}");

            // Broadcast WakeUp to the guest (same send OnLocalWakeUp uses).
            var packet = new WakeUpPacket { WasManual = true };
            Plugin.NetworkManager.SendToAllReliable(PacketType.WakeUp, w =>
                PacketSerializer.WriteWakeUp(w, packet));

            bool wasTavern = _isTavernSleep; // capture before WakeUp/TransitionToAwake clears it

            // Local vanilla wake so eyes/control/GameState.sleeping clear.
            if (GameState.sleeping && Sleep.instance != null)
            {
                GameState.eyesFullyClosed = true;
                Sleep.instance.WakeUp();
            }
            RestoreLookAfterTavernWake(wasTavern);

            TransitionToAwake();
        }

        /// <summary>
        /// N-player (Phase 4) backstop helper: mark EVERY currently-connected peer as rested so the
        /// AllCrewRested quorum can release (the host's own rested-ness is folded into AllCrewRested).
        /// At N=1 this adds the single guest, identical to the old `_guestRested = true`.
        /// </summary>
        private void MarkAllConnectedPeersRested()
        {
            var peers = Plugin.NetworkManager?.ConnectedPeers;
            if (peers == null) return;
            foreach (var id in peers) _restedPeers.Add(id);
        }

        /// <summary>
        /// N-player (Phase 4): per-peer leave cleanup. Drop a single departed peer from the in-bed and
        /// rested quorums so a leaver can never block AllCrewInBed / AllCrewRested for the remaining crew.
        /// Called from the disconnect paths for EACH leaving peer. If the crew is now empty
        /// (ConnectedPeers.Count==0) we fall back to a full sleep-state reset (OnDisconnect). Otherwise,
        /// removing the leaver may have just completed the quorum, so the host re-checks the wake.
        /// </summary>
        public void OnPeerLeft(SteamId peer)
        {
            bool wasTracked = _inBedPeers.Remove(peer) | _restedPeers.Remove(peer);
            VerboseLogger.SleepEvent($"OnPeerLeft {peer}: wasTracked={wasTracked}, remaining peers={Plugin.NetworkManager?.ConnectedPeers.Count ?? 0}, state={CurrentState}");

            // No crew left at all -> nothing to keep sleeping for; do the full reset (covers N=1: the lone
            // peer leaving behaves exactly like the old OnDisconnect/AbortSleep).
            if ((Plugin.NetworkManager?.ConnectedPeers.Count ?? 0) == 0)
            {
                OnDisconnect();
                return;
            }

            // Crew still present (N>2). Removing the leaver may have completed the quorum:
            // TAVERN-TIME: a tavern sleep must run to the vanilla 7-10am morning gate, so don't
            // force-wake the crew just because a leaver completed the rested quorum (mirrors the
            // _isTavernSleep guard in OnGuestRested; the morning DurationCap drives the tavern wake).
            if (CurrentState == SleepState.Sleeping && Plugin.IsHost && !_isTavernSleep && AllCrewRested && Sleep.instance != null)
            {
                VerboseLogger.SleepEvent("OnPeerLeft completed the all-rested quorum; waking remaining crew");
                bool wasTavern = _isTavernSleep; // capture before WakeUp->TransitionToAwake clears it
                GameState.eyesFullyClosed = true;
                Sleep.instance.WakeUp();
                RestoreLookAfterTavernWake(wasTavern);
            }
            // A leaver while the crew is WAITING needs symmetric handling on the host (the quorum owner),
            // or the relayed handshake + the _localPlayerInBed timeout gate strand the rest of the crew.
            else if (CurrentState == SleepState.Waiting && Plugin.IsHost)
            {
                // (#3) The departed crewmate was the last hold-out and the WHOLE remaining crew is now in bed.
                // AllCrewInBed re-checks the live ConnectedPeers set, but nothing re-evaluates it on a leave -
                // without this the ready crew hangs until the 90s WAITING timeout CANCELS them instead of
                // sleeping. AllCrewInBed already requires the host in bed, so this starts a warp everyone wants.
                // (Tavern is fine here: the morning DurationCap still governs the wake; no _isTavernSleep guard.)
                if (AllCrewInBed && Sleep.instance != null)
                {
                    VerboseLogger.SleepEvent("OnPeerLeft completed the all-in-bed quorum; starting sleep for remaining crew");
                    TransitionToSleeping();
                }
                // (#1/#2) The host (and the guests it relays to) are BYSTANDERS dragged into WAITING by a
                // relayed SleepRequest, and the only bedded crewmate(s) just left (_inBedPeers now empty). The
                // timeout gate (_localPlayerInBed) gives a bystander no escape valve, so without this they
                // wedge in WAITING forever. Broadcast a cancel so every relayed bystander drops the phantom,
                // then clear locally. A genuinely-bedded local player is untouched (!_localPlayerInBed), so its
                // own gated 90s timeout still governs - this is NOT the "a bystander cancels everyone" case the gate
                // guarded against, because here the bedded author is gone and there is no pending sleep to keep.
                else if (!_localPlayerInBed && _inBedPeers.Count == 0)
                {
                    VerboseLogger.SleepEvent("OnPeerLeft: last bedded crewmate gone while bystanders WAITING; cancelling handshake");
                    Plugin.NetworkManager.SendToAllReliable(PacketType.SleepCancelled, w =>
                        PacketSerializer.WriteSleepCancelled(w, new SleepCancelledPacket { AuthorId = peer.Value }));
                    TransitionToAwake();
                }
            }
        }

        /// <summary>
        /// Called on disconnect (lobby left, guest left, or P2P drop). Aborts any sleep so a partner
        /// vanishing mid-handshake can't wedge us at "WAITING FOR PARTNER" or stuck at timeScale 16.
        /// </summary>
        public void OnDisconnect()
        {
            AbortSleep();
        }

        #endregion
    }
}
