using UnityEngine;
using SailwindCoop.UI;

namespace SailwindCoop.Networking
{
    /// <summary>
    /// (v0.3.1) Guest-side watchdog for a transport that dies without saying so.
    ///
    /// WHAT IT COVERS. Every disconnect path the mod had before this hangs off a Steam callback:
    /// OnPlayerLeft raises "The host closed the co-op server.", the P2P drop path raises "Lost connection
    /// to the host.". Both require Steam to TELL us something ended. When the transport simply stops
    /// delivering, nothing is told to anyone: no peer leaves, no session closes, no callback fires, and
    /// the mod keeps cheerfully sending into a dead socket. The guest sees every crewmate frozen at their
    /// last known position and gets no explanation at all, because a frozen crewmate is exactly what a
    /// stationary crewmate looks like.
    ///
    /// Observed live 2026-08-06: the guest's Steam account was signed in from another machine, which
    /// invalidates this process's Steam session. Last packet in at 16:02:21, still sending at 16:03:39,
    /// and 78 seconds of silence produced not one line of diagnosis. The same hole covers a dropped
    /// network, a crashed host process, and a router that quietly stops forwarding.
    ///
    /// WHY A CLOCK IS THE ONLY DETECTOR THAT WORKS HERE. There is no event to subscribe to - the whole
    /// failure mode is the absence of events. Something has to watch a clock and notice nothing arrived.
    /// GuestJoinWatchdog already does exactly this for the join moment (waiting on a BoatWorldState that
    /// never comes); this is its mid-session twin, and it deliberately mirrors that structure.
    ///
    /// THE THRESHOLDS ARE MEASURED, NOT GUESSED. From 2.4 hours of the 2026-08-06 session logs, across
    /// sailing, mooring, a shipyard visit, sleep warps and menu time:
    ///
    ///   host -> guest packet rate      16.6 - 19.1 per second, sustained
    ///   gap between packets, median    0.044s
    ///   gap, 99th percentile           0.334s
    ///   gap, 99.9th percentile         0.571s
    ///   WORST gap in 8600 seconds      1.6s   (and ZERO gaps above 2s)
    ///
    /// So a real link is never quiet for even two seconds. The warning at 6s is ~4x the worst gap ever
    /// observed and the disconnect at 30s is ~19x it. Note the log only records packets whose category is
    /// verbose-enabled, so the true gaps are shorter than measured - the margin is wider than it looks.
    ///
    /// A HOST BEING BUSY IS NOT SILENCE. Worth stating because it is the obvious objection: a paused host,
    /// a host in a menu, and a host at a port all keep streaming (the mod replicates the host's timeScale
    /// rather than stopping its own sends), which is why the worst measured gap stayed at 1.6s through all
    /// of them. Silence really does mean the transport is gone.
    ///
    /// TWO STAGES, because the costs are lopsided. Ending a session wrongly is a real harm; showing a
    /// banner wrongly costs nothing and clears itself. So the first stage only tells the player what is
    /// happening (and disappears the instant a packet lands), and only sustained silence ends the session.
    ///
    /// GUEST-ONLY. The host has its own peer-timeout handling and must never quit itself because one guest
    /// went quiet.
    /// </summary>
    public static class HostLinkWatchdog
    {
        /// <summary>Silence before the player is told the link has stalled. Clears itself on any packet.</summary>
        private const float WarnSeconds = 6f;
        /// <summary>Silence before we conclude the transport is dead and run the normal guest leave path.</summary>
        private const float DisconnectSeconds = 30f;

        /// <summary>
        /// A frame gap longer than this is OUR stall, not their silence, and is credited to us rather than
        /// to the host. Without it a guest whose own process stops (a terrain hitch, a breakpoint, a laptop
        /// resuming from suspend, an alt-tab with "run in background" off) would return to a silence figure
        /// that had already sailed past the disconnect threshold and would eject itself for something that
        /// was never the host's fault.
        /// </summary>
        private const float SelfHitchFrameSeconds = 1f;

        // TIMING RUNS ON realtimeSinceStartup, NOT on accumulated unscaledDeltaTime. Two reasons, and the
        // second is the one that bites: unscaledDeltaTime is clamped by Time.maximumDeltaTime (0.333s by
        // default), so summing it UNDER-counts real elapsed time on any guest running below ~3 fps - the
        // exact condition a struggling connection tends to coincide with - and it can never report a frame
        // gap above the clamp, which would leave the self-hitch guard below permanently dead.
        // realtimeSinceStartup is wall clock, unclamped and unaffected by timeScale.
        private static float _lastAliveRealtime;
        private static float _lastTickRealtime;
        private static bool _warned;

        /// <summary>Any packet from the host. Resets the clock - this is the only proof the link is alive.</summary>
        public static void NotePacketFromHost()
        {
            _lastAliveRealtime = Time.realtimeSinceStartup;
            _warned = false;
        }

        /// <summary>Clears state on session start/end so a stale clock cannot fire into a new session.</summary>
        public static void Reset()
        {
            _lastAliveRealtime = Time.realtimeSinceStartup;
            _lastTickRealtime = Time.realtimeSinceStartup;
            _warned = false;
        }

        /// <summary>True while the link has been quiet long enough to tell the player about it.</summary>
        public static bool IsStalled => SilenceSeconds >= WarnSeconds;

        /// <summary>Seconds since the last packet from the host, for the banner and the log line.</summary>
        public static float SilenceSeconds => Time.realtimeSinceStartup - _lastAliveRealtime;

        /// <summary>
        /// Called every frame from Plugin.Update, AFTER ProcessIncomingPackets - so a packet that arrived
        /// this frame has already reset the counter and cannot be counted as silence.
        /// </summary>
        public static void Tick(bool joinedAsGuest, bool endingSession)
        {
            if (!Plugin.IsMultiplayer || Plugin.IsHost || !joinedAsGuest || endingSession)
            {
                Reset();
                return;
            }

            // Until the world state lands, the join is still in progress and GuestJoinWatchdog owns the
            // timeout (with a much longer, deliberately generous budget). Two watchdogs racing on the same
            // silence would produce two explanations for one failure.
            if (!Sync.BoatSyncManager.HasReceivedWorldState)
            {
                Reset();
                return;
            }

            float now = Time.realtimeSinceStartup;

            // How long OUR loop took to get back here. Any gap beyond a second means this process was not
            // running, so nothing could have been received no matter how healthy the host is - credit it to
            // us by sliding the liveness stamp forward rather than counting it against the host.
            float frameGap = now - _lastTickRealtime;
            _lastTickRealtime = now;
            if (frameGap > SelfHitchFrameSeconds)
            {
                // Clamped to now so repeated hitches cannot push the stamp into the future and leave the
                // silence figure negative, which would quietly buy the host extra grace it never earned.
                _lastAliveRealtime = Mathf.Min(now, _lastAliveRealtime + frameGap);
                return; // let the next tick, which has had a drain in front of it, do the judging
            }

            float silence = now - _lastAliveRealtime;

            if (!_warned && silence >= WarnSeconds)
            {
                _warned = true;
                Plugin.Log.LogWarning($"[Coop] No packet from the host for {silence:F1}s - link may have dropped.");
            }

            if (silence >= DisconnectSeconds)
            {
                Plugin.Log.LogError($"[Coop] Host link watchdog: nothing received from the host for " +
                                    $"{silence:F0}s. Treating the connection as lost.");
                Reset();
                Plugin.EndGuestSessionFromWatchdog(
                    "No response from the host.",
                    "Connection lost",
                    new System.Collections.Generic.List<string>
                    {
                        $"Nothing has been received from the host for {DisconnectSeconds:F0} seconds.",
                        "The crew you could see were frozen at their last known positions - the connection had already gone.",
                        "This usually means the host's game closed, your network dropped, or your Steam account was signed in somewhere else.",
                    },
                    "The game will now quit.");
            }
        }

        /// <summary>
        /// The stall banner. Deliberately plain and non-modal: it steals no input and blocks nothing, so a
        /// player who is mid-manoeuvre when the link hiccups is not also fighting a popup. It vanishes the
        /// moment a packet arrives.
        /// </summary>
        // Both are built once and reused. OnGUI is called several times per frame (Layout, then Repaint),
        // so allocating a Texture2D or a GUIStyle inside Draw would leak one of each per event for as long
        // as the banner is up - and the banner is up precisely when the game is already in trouble.
        private static Texture2D _bg;
        private static GUIStyle _style;

        public static void Draw()
        {
            if (!IsStalled) return;

            if (_bg == null) _bg = SailwindSkin.SolidTexture(new Color(0f, 0f, 0f, 0.72f));
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 15,
                    wordWrap = true,
                }.WithFont();
                _style.normal.textColor = SailwindSkin.Parchment;
            }

            const float w = 460f, h = 54f;
            var rect = new Rect((Screen.width - w) * 0.5f, 24f, w, h);
            GUI.DrawTexture(rect, _bg);
            GUI.Label(rect, $"No response from the host for {SilenceSeconds:F0}s\n" +
                            "Your crew will look frozen until the connection recovers.", _style);
        }
    }
}
