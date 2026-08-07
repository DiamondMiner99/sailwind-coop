using System.Collections.Generic;
using UnityEngine;
using HarmonyLib;
using Steamworks;
using SailwindCoop.Debug;
using SailwindCoop.Networking.Packets;
using SailwindCoop.Patches;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Manages synchronization of boat controls (ropes, helm, anchor, mooring).
    /// Polls active controls at 10Hz instead of using per-frame patches.
    /// </summary>
    public class ControlSyncManager : MonoBehaviour
    {
        public static ControlSyncManager Instance { get; private set; }

        private const float SyncInterval = 0.1f; // 10 Hz
        private float _lastSyncTime;

        // Access private field for mooring spring joint
        private static readonly AccessTools.FieldRef<PickupableBoatMooringRope, SpringJoint> MooredToSpringRef =
            AccessTools.FieldRefAccess<PickupableBoatMooringRope, SpringJoint>("mooredToSpring");

        // Two separate apply guards: the join coroutine (BoatStateApplicator) holds a LONG-LIVED guard
        // across its yields via SetApplyingRemoteState, while the per-packet OnRemote* handlers
        // (anchor/mooring) set a SHORT-LIVED guard in their own try/finally. Sharing ONE backing field
        // would mean a control packet (a relayed guest anchor/mooring change at N>=3, or the host toggling
        // its own anchor) arriving mid-join runs a handler whose finally clears the join's guard, opening
        // an echo window for the rest of the join. So the per-packet handlers only touch _applyingPerPacket
        // and the coroutine only touches _applyingJoinState; the getter is true when EITHER is set, so
        // neither can clear the other.
        private bool _applyingPerPacket;
        private bool _applyingJoinState;

        /// <summary>
        /// True while applying remote state - Harmony patches check this before sending packets to avoid echo.
        /// The property setter (used by the per-packet OnRemote* handlers) only touches _applyingPerPacket.
        /// </summary>
        public bool IsApplyingRemoteState
        {
            get => _applyingPerPacket || _applyingJoinState;
            private set => _applyingPerPacket = value;
        }

        /// <summary>
        /// Long-lived apply guard for the BoatStateApplicator join/world-state coroutine (held across yields).
        /// Goes to a SEPARATE backing flag so a per-packet handler toggling the property mid-join can't clear it.
        /// </summary>
        public void SetApplyingRemoteState(bool value)
        {
            _applyingJoinState = value;
        }

        /// <summary>
        /// Tracks ropes that were recently changed by network to prevent auto-mooring feedback.
        /// Key: rope instance ID, Value: time when network change was applied
        /// </summary>
        private Dictionary<int, float> _recentNetworkMooringChanges = new Dictionary<int, float>();
        private const float MooringDebounceTime = 1.0f; // Ignore local events for 1 second after network change

        /// <summary>
        /// Check if a mooring rope was recently changed by network (should ignore local events)
        /// </summary>
        public bool WasRecentlyChangedByNetwork(PickupableBoatMooringRope rope)
        {
            if (rope == null) return false;
            int id = rope.GetInstanceID();
            if (_recentNetworkMooringChanges.TryGetValue(id, out float changeTime))
            {
                if (Time.time - changeTime < MooringDebounceTime)
                {
                    return true;
                }
                // Clean up old entry
                _recentNetworkMooringChanges.Remove(id);
            }
            return false;
        }

        private void MarkRopeAsNetworkChanged(PickupableBoatMooringRope rope)
        {
            if (rope == null) return;
            _recentNetworkMooringChanges[rope.GetInstanceID()] = Time.time;
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
            if (!Plugin.IsMultiplayer) return;

            Plugin.Profiler?.StartMeasure();

            // (v0.2.25) Rate halved on the HOST during a co-op sleep (scale is 1 on guests/awake):
            // Time.time runs 16x under the warp, so this 10Hz poll became ~160Hz real and helped
            // saturate a guest's packet budget (the SLEEP_SNAP crash chain). Frequency only.
            if (Time.time - _lastSyncTime >= SyncInterval * SleepSyncManager.HostSleepSendIntervalScale)
            {
                _lastSyncTime = Time.time;
                // Join-race: retry rope seeds that arrived before the guest boat's sail controllers existed.
                // MUST run BEFORE PollBoatControls: on an aboard-join the discovery poll would otherwise
                // broadcast DEFAULT rope lengths in the tick the controllers appear (clobbering the host's
                // trim via last-writer-wins + the 0.3s reliable settle terminal) before the seed applies.
                RetryPendingRopes();
                // Poll all controls on current boat (no patching needed)
                PollBoatControls();
                // (v0.3.1) AFTER the poll, and deliberately OUTSIDE it: a rope armed this tick reads
                // now - RopeLastChangeTime == 0 and so cannot fire early, while a rope armed just before
                // the player stepped ashore still gets its terminal even though PollBoatControls has
                // returned early at `boat == null` ever since. Shaped like SweepMooringTerminals below.
                FlushArmedRopeTerminals();
                // Emit the debounced reliable terminal for any mooring rope that has settled (any client
                // that moved one), so a dropped final unreliable scroll packet self-heals.
                SweepMooringTerminals();
                // Host sweeps idle off-boat helm leases and sends a reliable terminal HelmState so
                // passengers converge to the final wheel angle even if the last unreliable relay was dropped.
                if (Plugin.IsHost) SweepStaleHelmLeases();
                // Host heals drifting unoccupied boats: a slow round-robin re-seed of each boat's rope set,
                // plus an immediate re-seed when a crewmate boards one (kills "whoever boards a stale boat
                // first wins"). Inside the 10Hz gate so it inherits the sleep-rate scale on top of its own
                // hard warp skip.
                if (Plugin.IsHost) ReconcileRopesRoundRobin();
                // Guest keeps its frozen anchor body inside the joint limit so the local
                // ConfigurableJoint can never fight the host's authoritative position stream.
                if (!Plugin.IsHost) RelaxGuestAnchorTether();
            }

            Plugin.Profiler?.EndMeasureControlSync();
        }

        // === Per-boat control-sync state (v0.3.1 all-boats refactor) ===
        //
        // The poll used to cover ONLY GetCurrentBoat(), backed by single-boat instance fields. Evidence
        // from the 2026-08-06 playtest: 399 RopeState sends in one session, every one for the boat under
        // the player's feet, zero for the other six - and boarding a stale boat then broadcast its wrong
        // state as truth. Mirrors the per-boat BoatSyncState refactor BoatSyncManager shipped in v0.2.28.
        private class BoatControlState
        {
            // Change-detection baseline, stamped on EVERY detected change (sent or suppressed) so a later
            // grab diffs only movement made while grabbed, never a stale accumulated delta.
            public float[] LastRopeLengths = new float[0];

            // Rope settle terminal: parallel arrays sized like LastRopeLengths track, per rope index, the
            // time of the last LOCAL change and whether a reliable terminal was already sent for the current
            // settle. The poll streams rope deltas UNRELIABLY (OnLocalRopeChanged isFinal=false) and stops
            // when the rope settles; if that LAST unreliable packet is dropped the off-host rope is stranded
            // at the wrong length with no resync. Mirror the helm SweepStaleHelmLeases pattern: a debounce
            // after the last change sends ONE reliable terminal so a dropped final self-heals. Echo-safe: a
            // remotely-applied rope updates LastRopeLengths (TryApplyRopePacket), so the change-detector
            // never trips for it - the terminal only ever fires for ropes THIS client actually moved.
            public float[] RopeLastChangeTime = new float[0];
            public bool[] RopeFinalSent = new bool[0];
            // Length as of the last OPERATED send: the terminal must ship this captured value, not the live
            // currentLength at sweep time - by then unoperated drift (stick drift, reef forcing) may have
            // moved the rope again, and a reliable IsFinal would export that contamination to the whole crew.
            public float[] RopeLastSentLength = new float[0];

            // OPERATED-ROPE GATE caches (field report: sails "unfold as if holding W" on the OTHER machine,
            // stopping when the peer disconnects and resuming on rejoin): ropes are last-writer-wins with no
            // lease, so ANY local rope movement (controller stick drift feeding a grabbed winch, load-time
            // reef forcing, join-race stale defaults on the first discovery tick) used to be broadcast at
            // 10Hz plus a reliable settle terminal and imposed on the whole crew. Only broadcast a rope
            // change when THIS machine's player is actually operating that rope: the GPButtonRopeWinch whose
            // `rope` field drives it is grabbed by the local pointer, or (anchor rope) the local player is
            // carrying THIS boat's anchor item, which vanilla Anchor.ExtraFixedUpdate pays rope out for
            // while held. Rebuilt on the same triggers as the per-rope arrays.
            public readonly Dictionary<RopeController, GPButtonRopeWinch> WinchMap =
                new Dictionary<RopeController, GPButtonRopeWinch>();
            public Anchor CachedAnchor;
            // Cached in BuildBoatControlCaches rather than resolved at the poll site so a multi-boat poll
            // does not pay a deep hierarchy walk per boat per tick; the use site re-resolves on null.
            public GPButtonSteeringWheel CachedWheel;

            // Rope-array identity the caches were built against. BoatUtility.GetRopeControllers returns the
            // SAME cached array until InvalidateRopeCache (fired on ANY sail change: shipyard sync, boat
            // state apply - the v0.2.25/v0.2.27 rope-cache invalidation story) forces a fresh allocation. A
            // sail rebuild destroys and recreates RopeController instances; with an unchanged count the
            // count trigger below never fires and the winch map stays keyed on destroyed ropes, so
            // IsLocalOperatingRope misses on EVERY rope and all local rope broadcasts are silently
            // suppressed until rejoin. Array identity catches exactly those rebuilds.
            public RopeController[] CachedRopeArrayRef;

            // Helm baselines, per boat. The single scalars these replace forced three receive paths to
            // carry "only stamp for the current boat" guards against cross-boat aliasing; per-boat entries
            // make those stamps exact instead.
            public float LastHelmInput;
            public bool LastHelmLocked;

            // Time.time of the last rope send OR receive touching this boat (stamped in the
            // OnLocalRopeChanged/OnRemoteRopeChanged choke points, so every sender path is covered - the
            // unreliable delta, the settle terminal and ResendRopeForBoat's own loop alike). The host
            // reconcile treats recent activity as "someone is authoring this boat, stand down".
            public float LastRopeActivityTime = -999f;
            // realtimeSinceStartup of the host's last authoritative re-seed of this boat. Realtime, not
            // Time.time: a 16x sleep warp would otherwise expire every boat's interval at once and dump the
            // whole world's rope set as reliable packets into the fragile post-wake window.
            public float LastReconcileTime = -999f;
            // Backoff for non-current boats whose rope scan comes back empty (a zero-rope boat like the
            // deployed Leopard cutter, or a mid-rebuild window). GetRopeControllers refuses to cache an
            // empty scan, so re-calling it every visit would pay a full hierarchy walk plus a LINQ sort at
            // 10Hz forever.
            public float NextEmptyRescanTime;
            // Boarding assert latch: set when a crewmate boarding this boat has been considered for a
            // re-seed, cleared when the boat has no remote crew aboard. Bounds a dock->deck->dock bounce to
            // one assert attempt per occupancy instead of a 38-packet reliable burst per crossing.
            public bool OccupancyAsserted;
        }

        private readonly Dictionary<string, BoatControlState> _controlStates =
            new Dictionary<string, BoatControlState>();

        // Reused scratch lists (the BoatSyncManager pattern) - this tick's poll set, index-aligned. Also
        // the reconcile's iteration set, so it shares the driver's FindAllBoats snapshot instead of
        // re-deriving one.
        private readonly List<string> _pollNamesScratch = new List<string>();
        private readonly List<SaveableObject> _pollBoatsScratch = new List<SaveableObject>();
        private readonly List<string> _controlPruneScratch = new List<string>();
        private int _pollTick;

        // Boats whose per-boat poll threw (logged once per boat per session, then skipped-not-fatal, per
        // the BoatStateCollector precedent: one boat that throws must not take down the rest).
        private readonly HashSet<string> _pollErrorLogged = new HashSet<string>();

        // Divider for the EXPENSIVE per-boat work on non-current boats (rope-array re-derives and cache
        // rebuild triggers). The change-detection loop itself runs every tick for every active boat: a rope
        // hauled and released between two divider samples would otherwise have its length stamped by the
        // unconditional baseline write while the operated gate misses the released winch, swallowing the
        // change entirely - and the reconcile would then revert the haul. Boats are staggered by poll-set
        // index so a global ClearCaches (cutter deploy/stow) re-derives one boat's rope array per tick
        // instead of all seven in one frame.
        private const int SecondaryBoatPollDivider = 4;
        // How long to leave a non-current boat alone after its rope scan came back empty.
        private const float EmptyRopeScanBackoff = 5f;

        private const float RopeTerminalDebounce = 0.3f;

        private HashSet<string> _loggedBoatRopes = new HashSet<string>();

        private BoatControlState GetOrCreateControlState(string boatName)
        {
            if (!_controlStates.TryGetValue(boatName, out var state))
            {
                state = new BoatControlState();
                _controlStates[boatName] = state;
            }
            return state;
        }

        /// <summary>
        /// (v0.3.1) Rope settle-terminal sweep, over every boat's state entry. Once a rope has been idle for
        /// RopeTerminalDebounce since its last LOCAL change, send ONE reliable terminal so a dropped final
        /// unreliable delta self-heals. Only fires for ropes this client SENT while operating them: the
        /// debounce is armed exclusively by the operated-send branch in PollBoatControlsFor (remote applies
        /// stamp LastRopeLengths, and suppressed local changes skip the arm), so an unoperated rope never
        /// earns a terminal either.
        ///
        /// HOISTED OUT OF PollBoatControls, which is the actual fix. It used to sit below
        /// `if (boat == null) return;`, so stepping off a boat within the 0.3s debounce meant the sweep
        /// simply never ran again for those ropes and the terminal was lost - trim a sail, walk onto the
        /// dock, and the crew keeps whatever the last unreliable delta happened to be. Resolving the boat by
        /// the state entry's name rather than from GetCurrentBoat() is what lets it finish the job after the
        /// player has left. Boat gone entirely (destroyed, streamed out): latch the slots and send nothing.
        /// </summary>
        private void FlushArmedRopeTerminals()
        {
            if (_controlStates.Count == 0) return;
            foreach (var kvp in _controlStates)
                FlushArmedRopeTerminalsFor(kvp.Key, kvp.Value, force: false);
        }

        /// <summary>
        /// force=true skips the debounce and is used when a boat's per-rope arrays are about to be wiped for
        /// a rope-count change, where the choice is flush now or discard.
        /// </summary>
        private void FlushArmedRopeTerminalsFor(string boatName, BoatControlState state, bool force)
        {
            if (state.RopeFinalSent.Length == 0) return;

            // Cheap early-out: the overwhelmingly common case is nothing armed, and this keeps the sweep
            // from paying a boat lookup (and its rope re-scan) on every idle tick.
            bool anyArmed = false;
            for (int i = 0; i < state.RopeFinalSent.Length; i++)
                if (!state.RopeFinalSent[i]) { anyArmed = true; break; }
            if (!anyArmed) return;

            var boat = BoatUtility.FindBoatByName(boatName);
            var ropes = boat != null ? BoatUtility.GetRopeControllers(boat) : null;

            for (int i = 0; i < state.RopeFinalSent.Length; i++)
            {
                if (state.RopeFinalSent[i]) continue;
                if (!force && Time.time - state.RopeLastChangeTime[i] < RopeTerminalDebounce) continue;

                if (ropes == null || i >= ropes.Length || ropes[i] == null)
                {
                    state.RopeFinalSent[i] = true; // rope or boat is gone - nothing meaningful left to terminate
                    continue;
                }

                // Defensive TTL. Armed slots outlive the boat the player is standing on, so a slot whose
                // terminal somehow never fired could otherwise fire arbitrarily later and reliably
                // broadcast a long-stale length as authoritative. Past this age, drop it silently instead.
                if (!force && Time.time - state.RopeLastChangeTime[i] > StaleTerminalSeconds)
                {
                    state.RopeFinalSent[i] = true;
                    continue;
                }

                state.RopeFinalSent[i] = true;
                // Ship the length captured at the last operated send, NOT the live value (see field docs).
                OnLocalRopeChanged(boatName, i, ropes[i].gameObject.name, state.RopeLastSentLength[i], true,
                    $"grabbed={IsLocalOperatingRope(state, boat, ropes[i])}, run={GameInput.GetKey(InputName.Run)}");
            }
        }

        /// <summary>Age past which an armed rope terminal is dropped rather than sent as authoritative.</summary>
        private const float StaleTerminalSeconds = 5f;

        private void PollBoatControls()
        {
            _pollTick++;

            // The boat underfoot is resolved LIVE (GameState.currentBoat.parent) and forced into the poll
            // set below whether or not the boat cache knows it. FindAllBoats latches its first non-empty
            // scan for the whole session, and a PARTIAL latch (a scan that lands mid world-load) is
            // possible, so the cache must never be the only door: losing rope/helm sync for the boat the
            // player is standing on would be strictly worse than the single-boat poll this replaces.
            var currentBoat = BoatUtility.GetCurrentBoat();
            string currentBoatName = currentBoat != null ? currentBoat.gameObject.name : null;

            // ONE FindAllBoats call per tick, shared with the prune and the reconcile. When the cache is
            // cold this call is a full world scan, so nothing else in the tick may repeat it.
            var boats = BoatUtility.FindAllBoats();

            _pollNamesScratch.Clear();
            _pollBoatsScratch.Clear();

            foreach (var kvp in boats)
            {
                var b = kvp.Value;
                // activeInHierarchy is mandatory, not tidiness: GetRopeControllers refuses to cache an
                // empty scan, so an inactive boat (stowed cutter) would pay a full hierarchy walk plus a
                // LINQ sort on every visit, forever.
                if (b == null || !b.gameObject.activeInHierarchy) continue;
                _pollNamesScratch.Add(kvp.Key);
                _pollBoatsScratch.Add(b);
            }

            if (currentBoat != null && currentBoat.gameObject.activeInHierarchy
                && !_pollNamesScratch.Contains(currentBoatName))
            {
                _pollNamesScratch.Add(currentBoatName);
                _pollBoatsScratch.Add(currentBoat);
            }

            for (int i = 0; i < _pollNamesScratch.Count; i++)
            {
                string name = _pollNamesScratch[i];
                var boat = _pollBoatsScratch[i];
                bool isCurrent = name == currentBoatName;
                // The current boat does its cache work every tick (today's behavior). Non-current boats do
                // the expensive cache work on a staggered divider tick; the change-detection loop still
                // runs for them every tick against the cached array.
                bool cacheTick = isCurrent || ((_pollTick + i) % SecondaryBoatPollDivider) == 0;
                try
                {
                    PollBoatControlsFor(name, boat, GetOrCreateControlState(name), isCurrent, cacheTick);
                }
                catch (System.Exception e)
                {
                    // One boat that throws must not take down the poll for the rest (the join snapshot
                    // learned this the hard way). Once per boat per session - at 10Hz anything more is a
                    // log flood.
                    if (_pollErrorLogged.Add(name))
                        Plugin.Log.LogWarning($"[ControlSync] Poll failed for boat '{name}' (logged once): {e}");
                }
            }

            // Prune states for boats that no longer exist at all (left the world, modded despawn). Entries
            // for INACTIVE boats are kept - a stowed cutter's state is harmless, and pruning it would churn
            // armed terminals on every deploy/stow cycle. Never prune the boat underfoot: with a partially
            // latched boat cache it can be missing from FindAllBoats while genuinely live. No time-based
            // prune on purpose: these entries are scene-derived, so silence means nothing (unlike
            // BoatSyncManager's packet-minted entries, where silence means the host stopped streaming).
            if ((_pollTick % SecondaryBoatPollDivider) == 0 && _controlStates.Count > boats.Count)
            {
                _controlPruneScratch.Clear();
                foreach (var kvp in _controlStates)
                {
                    if (kvp.Key == currentBoatName) continue;
                    if (!boats.ContainsKey(kvp.Key)) _controlPruneScratch.Add(kvp.Key);
                }
                foreach (var stale in _controlPruneScratch)
                    _controlStates.Remove(stale);
            }
        }

        private void PollBoatControlsFor(string boatName, SaveableObject boat, BoatControlState state,
            bool isCurrent, bool cacheTick)
        {
            RopeController[] ropes;
            if (cacheTick)
            {
                // Zero-rope backoff (non-current boats): an active boat with no RopeControllers (the
                // deployed Leopard cutter is oar-driven) or one inside a sail-rebuild window returns a
                // FRESH empty array from every GetRopeControllers call - BoatUtility deliberately never
                // caches an empty scan. Without the backoff the identity trigger below would read that
                // fresh array as a rebuild every visit and re-derive caches forever. The current boat
                // skips the backoff so a transient empty scan while standing aboard heals next tick,
                // exactly as before.
                if (!isCurrent && Time.time < state.NextEmptyRescanTime) return;

                ropes = BoatUtility.GetRopeControllers(boat);
                if (ropes.Length == 0)
                {
                    // "We looked at a bad moment", never "this boat has no ropes" (BoatUtility's contract).
                    // Leave the per-rope arrays untouched so an armed terminal survives a rebuild window.
                    if (!isCurrent) state.NextEmptyRescanTime = Time.time + EmptyRopeScanBackoff;
                    return;
                }

                // One-time rope-discovery logging. Full per-rope dump only for the boat underfoot;
                // background boats get one summary line each, or the first all-boats tick would dump the
                // whole world's rope tables (~100 lines) at once.
                if (!_loggedBoatRopes.Contains(boatName))
                {
                    _loggedBoatRopes.Add(boatName);
                    if (isCurrent) LogRopeDiscovery(boatName, boat, ropes);
                    else VerboseLogger.ControlLocal($"Rope discovery for {boatName}: {ropes.Length} ropes (background boat)");
                }

                if (state.LastRopeLengths.Length != ropes.Length)
                {
                    // (v0.3.1) FLUSH BEFORE THE WIPE. The wipe below latches RopeFinalSent[j] = true for
                    // every slot, which CANCELS any terminal still armed against the old rope set. Trim a
                    // sail into a rebuild that changes the rope count and the reliable terminal that exists
                    // to heal a dropped unreliable delta would be discarded - the v0.2.24 "sail stuck at an
                    // intermediate position" shape.
                    FlushArmedRopeTerminalsFor(boatName, state, force: true);

                    // FULL wipe: different rope count (or first discovery) - none of the per-rope send
                    // state is meaningful against the new rope set.
                    state.LastRopeLengths = new float[ropes.Length];
                    state.RopeLastChangeTime = new float[ropes.Length];
                    state.RopeFinalSent = new bool[ropes.Length];
                    state.RopeLastSentLength = new float[ropes.Length];
                    for (int j = 0; j < ropes.Length; j++)
                    {
                        state.LastRopeLengths[j] = -1f;
                        state.RopeFinalSent[j] = true; // no pending terminal for a freshly-(re)discovered rope
                    }
                    state.CachedRopeArrayRef = ropes;
                    BuildBoatControlCaches(state, boat, boatName);
                }
                else if (!ReferenceEquals(ropes, state.CachedRopeArrayRef))
                {
                    // IDENTITY-ONLY rebuild: same rope count, but GetRopeControllers handed back a fresh
                    // array - the rope cache was invalidated (fires on ANY customization/sail change,
                    // possibly mid-winch-operation) and the RopeController instances may have been
                    // recreated, so the winch map must be rebuilt or it stays keyed on destroyed objects
                    // and every local rope broadcast is silently suppressed. Crucially, PRESERVE the
                    // per-rope arrays (LastRopeLengths/RopeFinalSent/RopeLastChangeTime/RopeLastSentLength):
                    // wiping them here would cancel a pending IsFinal rope terminal (the reliable packet
                    // that heals a dropped final delta) and recreate the v0.2.24 "sail stuck at
                    // intermediate position" class. Rope ORDER is stable for a same-count invalidation
                    // (GetRopeControllers rebuilds from the same component scan); worst case a reordered
                    // index produces one spurious length delta, which is self-healing.
                    state.CachedRopeArrayRef = ropes;
                    BuildBoatControlCaches(state, boat, boatName);
                }
            }
            else
            {
                // Non-cache tick: run change detection against the cached array so a haul on ANY boat is
                // sampled at the full 10Hz (the operated gate must be evaluated at the rate the rope
                // moves, or a grab-and-release between divider samples is stamped but never sent).
                // Destroyed controllers read as null and are skipped; the next cache tick re-derives.
                ropes = state.CachedRopeArrayRef;
                if (ropes == null || ropes.Length == 0) return;
                if (state.LastRopeLengths.Length != ropes.Length) return; // wait for this boat's cache tick
            }

            for (int i = 0; i < ropes.Length; i++)
            {
                var rope = ropes[i];
                if (rope == null) continue;

                // Only send if changed
                if (Mathf.Abs(rope.currentLength - state.LastRopeLengths[i]) > 0.001f)
                {
                    // (v0.3.1) Read the name INSIDE the change branch. Unity's Object.name is a native
                    // property that allocates a fresh managed string per read, and this used to run for
                    // every rope on every tick while being consumed only in here. Across the whole world's
                    // rope set that is ~100 throwaway strings 10 times a second, ~99% of them on ticks
                    // where nothing moved.
                    string ropeName = rope.gameObject.name;

                    // ALWAYS stamp the change-detection cache, even for changes we won't send: a later
                    // grab must only diff movement made WHILE grabbed, never a stale accumulated delta.
                    state.LastRopeLengths[i] = rope.currentLength;

                    // OPERATED-ROPE GATE: only broadcast changes the local player is actually making
                    // (winch grabbed / anchor carried). Unoperated local movement (stick drift, load-time
                    // reef forcing, join-race defaults) must never be imposed on the crew (see field docs).
                    if (!IsLocalOperatingRope(state, boat, rope))
                    {
                        // DebugMode gate here, not just inside the logger: sustained drift hits this at
                        // 10Hz per rope and the interpolation would allocate every tick.
                        if (DebugMode.Enabled)
                            VerboseLogger.ControlLocal($"Rope change SUPPRESSED (not operated locally), boat={boatName}, idx={i}, name={ropeName}, len={rope.currentLength:F3}, run={GameInput.GetKey(InputName.Run)}");
                        continue;
                    }

                    // Extra logging for anchor rope
                    bool isAnchor = rope is RopeControllerAnchor;
                    if (isAnchor)
                    {
                        var anchorRb = state.CachedAnchor != null ? state.CachedAnchor.GetComponent<Rigidbody>() : null;
                        VerboseLogger.ControlLocal($"ANCHOR rope changed, boat={boatName}, idx={i}, name={ropeName}, len={rope.currentLength:F3}, anchorKinematic={anchorRb?.isKinematic}");
                    }

                    OnLocalRopeChanged(boatName, i, ropeName, rope.currentLength, false,
                        $"grabbed=true, run={GameInput.GetKey(InputName.Run)}");
                    // A genuine local haul is the strongest possible evidence this machine's copy of the
                    // boat is being actively authored - it may vouch for it again.
                    LiftRopeTrust(boatName, "local operated haul");
                    state.RopeLastChangeTime[i] = Time.time;  // arm the settle-terminal debounce
                    state.RopeFinalSent[i] = false;
                    state.RopeLastSentLength[i] = rope.currentLength;
                }
            }

            // Helm and helm-lock stay scoped to the boat GENUINELY underfoot - never the whole poll set.
            // An unmanned boat's rudder is water-pushed, so wheel.currentInput drifts every frame; polling
            // six unmanned wheels would re-create the v0.2.35 HelmState flood (~80x/sec at 16x warp) at
            // steady state. The BASELINES are still per-boat so the receive paths can stamp any boat
            // exactly (see OnRemoteHelmInput / OnRemoteHelmLockToggle).
            if (!isCurrent) return;

            var wheel = state.CachedWheel;
            if (wheel == null)
            {
                // Re-resolve on null (never cached yet, or destroyed by a rebuild). A boat with no wheel
                // re-walks its hierarchy each tick, which is exactly what the pre-refactor poll did every
                // tick for every boat.
                wheel = boat.GetComponentInChildren<GPButtonSteeringWheel>();
                state.CachedWheel = wheel;
                if (wheel == null) return;
            }

            // Poll helm input. SUPPRESSED during a co-op sleep warp (v0.2.35): the wheel/rudder is
            // invisible on the guest's black sleep screen, but on an UNMOORED sleep the moving boat
            // pushes the rudder so the wheel drifts every frame - at 16x that fired an on-change
            // HelmState ~80x/sec (confirmed in a guest crash log), the dominant packet flood that froze
            // the guest. Nothing on the guest needs it while asleep (its boat is host-snapped, not
            // rudder-driven). We deliberately do NOT update LastHelmInput while asleep, so the first
            // post-wake poll sees the accumulated drift and sends ONE catch-up HelmState to resync the
            // guest's wheel.
            if (!SleepSyncManager.IsCoopSleepWarpActive &&
                Mathf.Abs(wheel.currentInput - state.LastHelmInput) > 0.001f)
            {
                state.LastHelmInput = wheel.currentInput;
                OnLocalHelmChanged(boatName, wheel.currentInput, false);
            }

            // Poll helm lock state (host only - broadcast when lock changes via game UI)
            if (Plugin.IsHost)
            {
                bool currentLocked = LockedRef(wheel);
                if (currentLocked != state.LastHelmLocked)
                {
                    state.LastHelmLocked = currentLocked;
                    BroadcastHelmLock(boatName, currentLocked);
                }
            }
        }

        // === Rope Discovery Logging ===

        private void LogRopeDiscovery(string boatName, SaveableObject boat, RopeController[] ropes)
        {
            VerboseLogger.ControlLocal($"Rope discovery for {boatName}: {ropes.Length} ropes found");
            for (int i = 0; i < ropes.Length; i++)
            {
                var rope = ropes[i];
                if (rope == null)
                {
                    VerboseLogger.ControlLocal($"  [{i}] NULL");
                    continue;
                }

                string ropeType = rope.GetType().Name;
                string ropeName = rope.gameObject.name;
                if (rope is RopeControllerAnchor)
                {
                    var anchor = BoatUtility.GetAnchor(boat);
                    var anchorRb = anchor?.GetComponent<Rigidbody>();
                    var joint = anchor?.GetComponent<ConfigurableJoint>();
                    VerboseLogger.ControlLocal($"  [{i}] {ropeType} name={ropeName} (ANCHOR) len={rope.currentLength:F3}, jointLimit={joint?.linearLimit.limit:F2}, kinematic={anchorRb?.isKinematic}");
                }
                else
                {
                    VerboseLogger.ControlLocal($"  [{i}] {ropeType} name={ropeName} len={rope.currentLength:F3}");
                }
            }
        }

        // === Operated-rope detection (see the BoatControlState.WinchMap field docs) ===

        /// <summary>
        /// Rebuild the rope->winch map, cached Anchor and cached steering wheel for one boat's state entry.
        /// Called on the same triggers as the per-rope array resize (rope count OR array identity change),
        /// so a stale map can never stay keyed on destroyed ropes. GPButtonRopeWinch.rope is the public
        /// vanilla field pointing at the RopeController the winch drives.
        /// </summary>
        private void BuildBoatControlCaches(BoatControlState state, SaveableObject boat, string boatName)
        {
            state.WinchMap.Clear();
            var winches = boat.GetComponentsInChildren<GPButtonRopeWinch>(true);
            foreach (var winch in winches)
            {
                if (winch != null && winch.rope != null && !state.WinchMap.ContainsKey(winch.rope))
                    state.WinchMap[winch.rope] = winch;
            }
            // BoatUtility.GetAnchor, NOT GetComponentInChildren: vanilla Anchor.Awake reparents the
            // anchor out of the boat hierarchy, so a child search is always null after Awake.
            state.CachedAnchor = BoatUtility.GetAnchor(boat);
            state.CachedWheel = boat.GetComponentInChildren<GPButtonSteeringWheel>();
            VerboseLogger.ControlLocal($"Rope winch map rebuilt for {boatName}: {state.WinchMap.Count} winches, anchor={(state.CachedAnchor != null)}");
        }

        /// <summary>
        /// True if THIS machine's local player is currently operating <paramref name="rope"/> on
        /// <paramref name="boat"/>: the winch driving it is grabbed by the local pointer (same read-only
        /// vanilla grab test as IsHostSteeringWheel - stickyClickedBy/isClicked/rotHandle are only ever set
        /// by the LOCAL GoPointer), or the rope is the anchor rope and the local player is carrying THAT
        /// BOAT's anchor item (vanilla Anchor.ExtraFixedUpdate pays rope out while held; PickupableItem.held
        /// is likewise local-pointer-only). A rope with no winch (map miss) is never operated - unoperated
        /// ropes must never broadcast.
        ///
        /// (v0.3.1) The anchor resolves from the state entry that OWNS the rope, closing the hazard the old
        /// single-boat field carried: it held the CURRENT boat's anchor and tested it against any rope with
        /// no boat check, so under an all-boats poll a player merely CARRYING one anchor would have read as
        /// operating the anchor rope of every boat in the world, broadcasting all of them at 10Hz. Per-boat
        /// resolution also deliberately enables a new case: carrying boat B's anchor while standing on a
        /// dock or another boat now broadcasts boat B's anchor payout, which is correct - the player really
        /// is paying that rope out (watch it alongside the overnight-anchor report, playtest section 9).
        /// </summary>
        private bool IsLocalOperatingRope(BoatControlState state, SaveableObject boat, RopeController rope)
        {
            if (state.WinchMap.TryGetValue(rope, out var winch) && winch != null)
            {
                if (HelmStickyClickedByRef(winch) != null
                    || HelmIsClickedRef(winch)
                    || (winch.rotHandle != null && winch.rotHandle.IsGrabbed()))
                    return true;
            }
            if (rope is RopeControllerAnchor)
            {
                // Lazy re-resolve: the anchor may not be resolvable at map-build time on a freshly
                // spawned boat (BoatMooringRopes.anchor unset + RopeControllerAnchor not yet registered).
                if (state.CachedAnchor == null && boat != null)
                    state.CachedAnchor = BoatUtility.GetAnchor(boat);
                if (state.CachedAnchor != null && state.CachedAnchor.held != null)
                    return true;
            }
            return false;
        }

        // === Rope Sync ===

        // Use index as primary identifier (consistent within same boat instance)
        // Name is sent for debugging and potential future use
        public void OnLocalRopeChanged(string boatName, int ropeIndex, string ropeName, float length, bool isFinal, string diag = null)
        {
            if (!Plugin.IsMultiplayer) return;

            // Reconcile quiet window: any rope send touching a boat marks it active, so the host's periodic
            // re-seed stands down while anyone (this machine included - the re-seed itself funnels through
            // here) is authoring it. This choke point covers every sender path: the unreliable delta, the
            // settle terminal, and ResendRopeForBoat's loop.
            GetOrCreateControlState(boatName).LastRopeActivityTime = Time.time;

            VerboseLogger.ControlSend($"RopeState, boat={boatName}, idx={ropeIndex}, name={ropeName}, len={length:F3}, final={isFinal}{(diag != null ? ", " + diag : "")}");

            var packet = new RopeStatePacket
            {
                BoatName = boatName,
                RopeIndex = ropeIndex,
                RopeName = ropeName,
                Length = length,
                IsFinal = isFinal
            };

            if (isFinal)
            {
                Plugin.NetworkManager.SendToAllReliable(PacketType.RopeState, w =>
                    PacketSerializer.WriteRopeState(w, packet));
            }
            else
            {
                Plugin.NetworkManager.SendToAllUnreliable(PacketType.RopeState, w =>
                    PacketSerializer.WriteRopeState(w, packet));
            }
        }

        public void OnRemoteRopeChanged(RopeStatePacket packet, SteamId sender = default)
        {
            VerboseLogger.ControlRecv($"RopeState, boat={packet.BoatName}, idx={packet.RopeIndex}, name={packet.RopeName}, len={packet.Length:F3}");

            // Reconcile quiet window (see OnLocalRopeChanged): a peer authoring a rope on this boat means
            // the host's periodic re-seed must stand down for it.
            GetOrCreateControlState(packet.BoatName).LastRopeActivityTime = Time.time;

            // STAR host-relay: a rope change from a guest is a REQUEST. The host applies
            // it below (authoritative) and forwards the resulting state to all OTHER guests, so a rope a
            // peer-guest pulled is visible to the rest of the crew. ROPE CONTENTION: last-writer-wins (the
            // most recent guest's length is applied and relayed) - chosen over a per-rope lease as the
            // lower-risk option; a winch settles on the last sender's value rather than fighting. At N=1
            // SendToAllExcept(sender) targets no one (the sender is the only peer), so this is a no-op and
            // behavior is identical to before.
            if (Plugin.IsHost)
            {
                Plugin.NetworkManager.SendToAllExcept(sender, PacketType.RopeState,
                    w => PacketSerializer.WriteRopeState(w, packet), reliable: packet.IsFinal);
            }

            string pendingKey = packet.BoatName + "|" + packet.RopeIndex;
            if (TryApplyRopePacket(packet, logMiss: true))
            {
                // A peer re-authored a rope on this boat and it landed: our copy now carries crew truth for
                // it, which is the signal the trust gate waits for when a trim restore never completed.
                LiftRopeTrust(packet.BoatName, "peer rope apply");
                // LATEST WINS: a newly-applied value supersedes any older queued seed for the same rope.
                _pendingRopes.Remove(pendingKey);
                return;
            }

            // JOIN-RACE DEFERRAL (guest): the host's one-shot rope seed can arrive BEFORE the guest boat's
            // sail rope controllers exist (the join customization apply rebuilds sails a few frames later),
            // so the lookup misses and the guest would board with default trim. Queue the packet (latest
            // wins per (boat,rope)) and retry from the 10Hz tick for up to PendingRopeTtl realtime seconds.
            if (Plugin.IsHost) return;
            _pendingRopes[pendingKey] = new PendingRope
            {
                Packet = packet,
                Deadline = Time.realtimeSinceStartup + PendingRopeTtl,
                NextTry = Time.realtimeSinceStartup + PendingRopeRetryInterval
            };
        }

        // Pending rope seeds that missed their controller at apply time (join race). Keyed boat|ropeIndex,
        // latest wins; bounded by the per-entry realtime TTL and cleared in Reset().
        private class PendingRope { public RopeStatePacket Packet; public float Deadline; public float NextTry; }
        private readonly Dictionary<string, PendingRope> _pendingRopes = new Dictionary<string, PendingRope>();
        // 0: retry every 10Hz tick so the seed applies in the SAME tick the controllers appear (RetryPendingRopes
        // runs before PollBoatControls, so the discovery poll stamps the seeded values into its cache instead of
        // treating them as local changes; the operated-rope gate suppresses any broadcast either way). TTL bounds it.
        private const float PendingRopeRetryInterval = 0f;
        private const float PendingRopeTtl = 15f;

        private void RetryPendingRopes()
        {
            if (_pendingRopes.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            List<string> done = null;
            // Per-tick, per-BOAT failure memo. An all-boats join seed can park ~100 entries here while the
            // joiner's customization rebuild is still destroying and recreating controllers, and every miss
            // on a mid-rebuild boat pays that boat's UNCACHED rope scan (GetRopeControllers refuses to
            // cache an empty result) - so without the memo one unresolvable boat's 38 entries cost 38 full
            // hierarchy walks per tick, on the machine that is simultaneously running the join. One probe
            // per boat per tick bounds the cost; siblings retry next tick, which still satisfies the
            // ordering rule (the poll's discovery tick absorbs an applied seed the same tick it lands).
            HashSet<string> missedBoats = null;
            foreach (var kvp in _pendingRopes)
            {
                var p = kvp.Value;
                if (now < p.NextTry) continue;

                bool applied = false;
                if (missedBoats == null || !missedBoats.Contains(p.Packet.BoatName))
                {
                    p.NextTry = now + PendingRopeRetryInterval;
                    applied = TryApplyRopePacket(p.Packet, logMiss: false);
                    if (!applied)
                    {
                        if (missedBoats == null) missedBoats = new HashSet<string>();
                        missedBoats.Add(p.Packet.BoatName);
                    }
                }

                if (applied)
                {
                    VerboseLogger.ControlApply($"RopeState deferred apply OK, boat={p.Packet.BoatName}, idx={p.Packet.RopeIndex}, len={p.Packet.Length:F3}");
                    if (done == null) done = new List<string>();
                    done.Add(kvp.Key);
                }
                else if (now >= p.Deadline)
                {
                    Plugin.Log.LogWarning($"[ControlSync] RopeState deferred apply gave up after {PendingRopeTtl:F0}s, boat={p.Packet.BoatName}, idx={p.Packet.RopeIndex}, name={p.Packet.RopeName}");
                    if (done == null) done = new List<string>();
                    done.Add(kvp.Key);
                }
            }
            if (done != null)
                foreach (var key in done) _pendingRopes.Remove(key);
        }

        /// <summary>
        /// Locate and apply a RopeState packet. Returns false if the boat or rope controller doesn't exist
        /// yet (caller may defer). On success runs the SAME echo-guard bookkeeping as the live path so the
        /// poll loop can't re-broadcast a remotely-applied length.
        /// </summary>
        private bool TryApplyRopePacket(RopeStatePacket packet, bool logMiss)
        {
            // Find the boat by name (there's only one boat of each type per world)
            var boat = BoatUtility.FindBoatByName(packet.BoatName);
            if (boat == null)
            {
                if (logMiss) VerboseLogger.ControlApply($"RopeState SKIP: boat not found, name={packet.BoatName}");
                return false;
            }

            var ropes = BoatUtility.GetRopeControllers(boat);
            RopeController rope = null;
            int appliedIndex = -1;

            // Use index as primary lookup (consistent within boat)
            if (packet.RopeIndex >= 0 && packet.RopeIndex < ropes.Length)
            {
                rope = ropes[packet.RopeIndex];
                appliedIndex = packet.RopeIndex;
            }

            // Fallback to name if index fails
            if (rope == null && !string.IsNullOrEmpty(packet.RopeName))
            {
                for (int i = 0; i < ropes.Length; i++)
                {
                    if (ropes[i] != null && ropes[i].gameObject.name == packet.RopeName)
                    {
                        rope = ropes[i];
                        appliedIndex = i;
                        VerboseLogger.ControlApply($"RopeState fallback to name, idx={packet.RopeIndex} invalid, found by name at idx={i}");
                        break;
                    }
                }
            }

            if (rope == null)
            {
                if (logMiss) VerboseLogger.ControlApply($"RopeState FAILED: rope not found, boat={packet.BoatName}, idx={packet.RopeIndex}, name={packet.RopeName}{(Plugin.IsHost ? "" : " (queued for deferred retry)")}");
                return false;
            }

            // (v0.3.1) MID-HAUL PROTECTION: if THIS machine has a settle terminal armed for this exact rope
            // (operated locally, final not yet sent), skip the apply - a remote value landing mid-haul
            // yanks the rope out of the local player's hands. This matters once the host's periodic
            // reconcile exists: a re-assert could otherwise catch a haul the quiet window missed. Ordinary
            // two-players-on-one-winch contention still converges last-writer-wins - the skip window closes
            // with the local terminal (~0.3s after the last local movement), and the other machine's final
            // applies after that. Returns true: the packet is handled, not deferrable.
            var st = GetOrCreateControlState(packet.BoatName);
            if (appliedIndex < st.RopeFinalSent.Length && !st.RopeFinalSent[appliedIndex])
            {
                VerboseLogger.ControlApply($"RopeState SKIPPED (local haul in progress), boat={packet.BoatName}, idx={appliedIndex}, len={packet.Length:F3}");
                return true;
            }

            float prevLength = rope.currentLength;
            rope.currentLength = packet.Length;
            rope.changed = true;

            // (v0.2.31, C1b) This is an AUTHORITATIVE value from a peer. Stamp the rope so a Shipyard
            // Expansion rope-trim restore that is mid-flight (SE's rig apply rebuilds the sails at prefab
            // default lengths and puts the pre-rebuild trim back a frame later) cannot overwrite it with the
            // stale pre-rebuild length. A terminal RopeState is the LAST packet for that rope - nothing would
            // ever re-send it - so a clobber here is permanent for the session: the v0.2.25 "phantom furled
            // sails" bug. Gated on SE being installed, so without SE this is one static bool test and the
            // rope-sync path is byte-for-byte what it was.
            //
            // (P3) MarkRopeAuthoritative itself early-outs unless a restore is actually in flight, so even WITH
            // SE this costs one static bool test on the overwhelming majority of rope packets. Do not hoist that
            // condition up here: it is the callee's own invariant (it owns _trimRestorePending) and duplicating
            // it would just be a second thing to keep in sync.
            if (Compat.SECompat.IsInstalled)
                ShipyardSyncManager.MarkRopeAuthoritative(packet.BoatName, rope);

            // Update the change-detection cache to prevent echo feedback - an EXACT per-boat stamp now. The
            // single-array era had to skip this for any boat the player was not standing on, which was only
            // safe because the poll ignored those boats; under the all-boats poll an unstamped remote apply
            // would read as a local change on the very next tick (caught by the operated gate, but the
            // stamp is what makes the guard exact instead of conditional). MUST use appliedIndex, not
            // packet.RopeIndex: the name fallback above can move them apart precisely on the machines
            // whose rope arrays disagreed enough to need it.
            if (appliedIndex >= 0 && appliedIndex < st.LastRopeLengths.Length)
            {
                st.LastRopeLengths[appliedIndex] = packet.Length;
            }

            // Extra logging for anchor rope
            if (rope is RopeControllerAnchor)
            {
                var anchor = BoatUtility.GetAnchor(boat);
                var anchorRb = anchor?.GetComponent<Rigidbody>();
                var joint = anchor?.GetComponent<ConfigurableJoint>();

                VerboseLogger.ControlApply($"ANCHOR rope recv, boat={packet.BoatName}, idx={appliedIndex}, " +
                    $"prevLen={prevLength:F3}, newLen={packet.Length:F3}, " +
                    $"jointLimit={joint?.linearLimit.limit:F2}, anchorKinematic={anchorRb?.isKinematic}");
            }
            else
            {
                VerboseLogger.ControlApply($"Rope set, boat={packet.BoatName}, idx={appliedIndex}, len={packet.Length:F3}");
            }
            return true;
        }

        // === Helm Sync ===

        // Access private field for rotationAngleLimit
        private static readonly AccessTools.FieldRef<GPButtonSteeringWheel, float> RotationAngleLimitRef =
            AccessTools.FieldRefAccess<GPButtonSteeringWheel, float>("rotationAngleLimit");

        /// <summary>
        /// JOIN seed: re-broadcast the current helm angle of the active boat so a guest that joins while
        /// the host is holding the wheel at a steady angle ends the join with the correct rudder. HelmState
        /// is edge-triggered (sent only when currentInput changes), so without this a held-steady wheel
        /// would never emit a packet and the guest's rudder would sit at default. Host-only; reliable send.
        /// </summary>
        public void ResendHelmForCurrentBoat()
        {
            if (!Plugin.IsHost) return;
            var boat = BoatUtility.GetCurrentBoat();
            if (boat == null) return;
            var wheel = boat.GetComponentInChildren<GPButtonSteeringWheel>();
            if (wheel == null) return;
            OnLocalHelmChanged(boat.gameObject.name, wheel.currentInput, true);
        }

        /// <summary>
        /// (v0.3.0) Authoritative rope re-seed for one EXPLICIT boat: the host re-sends every current rope
        /// length as a reliable terminal RopeState. Reuses the EXISTING RopeState packet (one per rope),
        /// reliable (IsFinal=true) so a dropped seed can't strand a rope. Host-only; indices come from the
        /// stable-sorted GetRopeControllers, so they match every peer's array. Idempotent on already-settled
        /// crew - they simply re-apply the same lengths. Explicit boat because the shipyard-exit caller
        /// cannot use GetCurrentBoat(): vanilla DischargeShip nulls GameState.currentBoat in the same call
        /// that ends shipyard mode. (v0.3.1) Also the workhorse of the periodic reconcile and the boarding
        /// assert, hence the trust gate.
        /// </summary>
        public void ResendRopeForBoat(SaveableObject boat)
        {
            if (!Plugin.IsHost) return;
            if (boat == null) return;

            var boatName = boat.gameObject.name;
            // Trust gate: never broadcast a boat whose live rope lengths this machine cannot vouch for (a
            // peer's customization/rig packet rebuilt its sails and the trim restore has not verifiably
            // completed). This is the ONE rope send path with no operated gate, so without the check it
            // would convert one machine's prefab defaults into crew-wide truth - and the reconcile would
            // re-assert them every cycle.
            if (IsRopeTrustSuspended(boatName))
            {
                VerboseLogger.ControlSend($"ResendRopeForBoat SKIPPED (rope trust suspended): {boatName}");
                return;
            }

            var ropes = BoatUtility.GetRopeControllers(boat);
            for (int i = 0; i < ropes.Length; i++)
            {
                var rope = ropes[i];
                if (rope == null) continue;
                OnLocalRopeChanged(boatName, i, rope.gameObject.name, rope.currentLength, true);
            }
            VerboseLogger.ControlSend($"ResendRopeForBoat: re-seeded {ropes.Length} rope lengths for {boatName}");
        }

        /// <summary>
        /// (v0.3.1) JOIN seed, all boats, TARGETED to the joining peer. The old join step re-broadcast only
        /// the host's current boat, which is why a rejoin fixed a stale background boat for the rejoiner
        /// and nobody else. Targeted rather than SendToAllReliable for the same reason the surrounding join
        /// steps are: a crew-wide broadcast would convert this machine's copy of every unoccupied boat into
        /// everyone's truth on every join. The existing crew converges through the periodic reconcile,
        /// which carries the trust and quiet gates.
        /// </summary>
        public void ResendRopeForAllBoatsTo(SteamId target)
        {
            if (!Plugin.IsHost) return;

            var boats = BoatUtility.FindAllBoats();
            int boatsSeeded = 0, ropesSeeded = 0;
            foreach (var kvp in boats)
            {
                var boat = kvp.Value;
                if (boat == null || !boat.gameObject.activeInHierarchy) continue;
                var boatName = kvp.Key;
                if (ShipyardSyncManager.IsBoatShipyardActive(boatName)) continue;
                if (ShipyardSyncManager.IsTrimRestorePending(boatName)) continue;
                if (IsRopeTrustSuspended(boatName)) continue;

                var ropes = BoatUtility.GetRopeControllers(boat);
                for (int i = 0; i < ropes.Length; i++)
                {
                    var rope = ropes[i];
                    if (rope == null) continue;
                    var packet = new RopeStatePacket
                    {
                        BoatName = boatName,
                        RopeIndex = i,
                        RopeName = rope.gameObject.name,
                        Length = rope.currentLength,
                        IsFinal = true
                    };
                    Plugin.NetworkManager.SendReliable(target, PacketType.RopeState, w =>
                        PacketSerializer.WriteRopeState(w, packet));
                    ropesSeeded++;
                }
                if (ropes.Length > 0) boatsSeeded++;
            }
            VerboseLogger.ControlSend($"ResendRopeForAllBoatsTo: seeded {ropesSeeded} rope lengths across {boatsSeeded} boats to {target}");
        }

        // === Host rope reconcile (v0.3.1) ===
        //
        // Detection alone cannot heal a boat nobody is standing on: the changes that drift an unoccupied
        // boat (SE rig rebuilds, load-time reef forcing, join-race defaults) are exactly what the
        // operated-rope gate suppresses, so a missed edge used to be permanent until someone boarded and
        // hauled - and whoever boarded first then broadcast the drifted state as truth. The host therefore
        // re-asserts each boat's rope set on a slow cycle, standing down whenever anyone is actively
        // authoring the boat or its local copy cannot be vouched for.

        // The kill switch: raising this disables the healing cadence without removing code. At 20s per boat
        // the playtest world costs ~102 reliable RopeState per 20s per peer (~5/sec, ~300 B/s).
        private const float RopeReconcileInterval = 20f;
        // A boat with rope traffic inside this window is being actively authored - stand down. Against the
        // 0.3s settle debounce this also makes "reconcile fires while a terminal is armed for the same
        // rope" structurally impossible (two reliable finals racing to decide one rope's value).
        private const float RopeReconcileQuiet = 2f;
        private int _reconcileCursor;

        // Last boat name each remote crew member was seen on ("" = ashore). Seed-only on first sighting: a
        // first sighting is a join, and the join snapshot plus the targeted rope seed already covered them.
        // Bounded by crew size; cleared in Reset().
        private readonly Dictionary<ulong, string> _peerBoatNames = new Dictionary<ulong, string>();
        private readonly HashSet<string> _occupiedBoatsScratch = new HashSet<string>();

        // === Rope trust suspension ===
        //
        // A boat whose sails THIS machine rebuilt from a peer's packet (customization 43 / SE rig 215) sits
        // at prefab-default rope lengths until the trim restore verifiably completes. Any authoritative
        // broadcast of that boat inside the window (reconcile, boarding assert, join seed) would convert
        // the defaults into crew-wide truth, re-asserted every cycle - the "slowly reverting sails every
        // 20 seconds" failure. Suspended on every peer-driven rebuild (ShipyardSyncManager stamps it);
        // lifted when the restore completes, when a peer's rope apply lands on the boat, or when the local
        // player hauls one of its ropes. Static because the shipyard stamp sites are static; per-session,
        // cleared in Reset().
        private static readonly Dictionary<string, float> _ropeTrustSuspended = new Dictionary<string, float>();

        /// <summary>A peer-driven sail rebuild just made this machine's copy of the boat's rope lengths untrustworthy.</summary>
        public static void SuspendRopeTrust(string boatName, string reason)
        {
            if (string.IsNullOrEmpty(boatName)) return;
            _ropeTrustSuspended[boatName] = Time.realtimeSinceStartup;
            VerboseLogger.ControlEvent($"Rope trust suspended for {boatName} ({reason})");
        }

        /// <summary>This machine's copy of the boat's rope lengths is trustworthy again.</summary>
        public static void LiftRopeTrust(string boatName, string reason)
        {
            if (string.IsNullOrEmpty(boatName)) return;
            if (_ropeTrustSuspended.Remove(boatName))
                VerboseLogger.ControlEvent($"Rope trust restored for {boatName} ({reason})");
        }

        /// <summary>True while this machine's live rope lengths for the boat must not be broadcast as authoritative.</summary>
        public static bool IsRopeTrustSuspended(string boatName)
        {
            return !string.IsNullOrEmpty(boatName) && _ropeTrustSuspended.ContainsKey(boatName);
        }

        /// <summary>
        /// Host, from the 10Hz tick: advance ONE boat per tick around the poll set; re-seed a boat's whole
        /// rope set when its interval has elapsed and nothing else is touching it. One boat per tick visits
        /// a 7-boat world every 0.7s, so the 20s interval, not the cursor, sets the traffic. Skipped
        /// outright during a join (the join sends its own targeted seed) and during a sleep warp (Time.time
        /// runs 16x there, and the post-wake window is the most packet-fragile stretch the mod has).
        /// </summary>
        private void ReconcileRopesRoundRobin()
        {
            if (!Plugin.IsHost) return;
            if (BoatSyncManager.IsJoinInProgress) return;
            if (SleepSyncManager.IsCoopSleepWarpActive) return;

            ReconcileOnCrewBoarding();

            int count = _pollNamesScratch.Count;
            if (count == 0) return;
            _reconcileCursor = (_reconcileCursor + 1) % count;
            string boatName = _pollNamesScratch[_reconcileCursor];
            var boat = _pollBoatsScratch[_reconcileCursor];
            var state = GetOrCreateControlState(boatName);

            float nowReal = Time.realtimeSinceStartup;
            if (nowReal - state.LastReconcileTime < RopeReconcileInterval) return;
            if (Time.time - state.LastRopeActivityTime < RopeReconcileQuiet) return;
            if (!IsBoatEligibleForRopeAssert(boatName, boat)) return;

            state.LastReconcileTime = nowReal;
            ResendRopeForBoat(boat);
        }

        /// <summary>
        /// Host: when a crewmate steps onto a boat, pull that boat's reconcile forward so their possibly
        /// stale copy is corrected BEFORE they start hauling ropes on it - this is what actually kills
        /// "whoever boards a drifted boat first wins"; the round-robin alone leaves a window up to the full
        /// interval. Fires at most once per occupancy (the latch clears when the boat empties of remote
        /// crew) AND at most once per reconcile interval per boat, so a crewmate bouncing dock->deck->dock
        /// while loading crates cannot re-fire a whole-boat reliable burst on every crossing.
        /// </summary>
        private void ReconcileOnCrewBoarding()
        {
            var rpm = Player.RemotePlayerManager.Instance;
            if (rpm == null) return;

            _occupiedBoatsScratch.Clear();
            foreach (var avatar in rpm.Avatars)
            {
                if (avatar == null) continue;
                ulong id = avatar.PlayerId.Value;
                string boatName = avatar.CurrentBoatName ?? "";
                if (!string.IsNullOrEmpty(boatName)) _occupiedBoatsScratch.Add(boatName);

                if (!_peerBoatNames.TryGetValue(id, out var prev))
                {
                    _peerBoatNames[id] = boatName; // first sighting = join; the targeted seed covered them
                    continue;
                }
                if (boatName == prev) continue;
                _peerBoatNames[id] = boatName;
                if (string.IsNullOrEmpty(boatName)) continue; // stepped ashore

                var state = GetOrCreateControlState(boatName);
                if (state.OccupancyAsserted) continue;
                // Latch on the ATTEMPT, not the fire: a boarder inside the cooldown means the boat was
                // asserted recently, and retrying on every crossing is the packet-volume failure this
                // latch exists to prevent.
                state.OccupancyAsserted = true;

                float nowReal = Time.realtimeSinceStartup;
                if (nowReal - state.LastReconcileTime < RopeReconcileInterval) continue;
                if (Time.time - state.LastRopeActivityTime < RopeReconcileQuiet) continue;

                var boat = BoatUtility.FindBoatByName(boatName);
                if (!IsBoatEligibleForRopeAssert(boatName, boat)) continue;

                state.LastReconcileTime = nowReal;
                VerboseLogger.ControlSend($"Boarding assert: {boatName} boarded by {id}, re-seeding its rope set");
                ResendRopeForBoat(boat);
            }

            // Clear the boarding-assert latch for boats with no remote crew aboard.
            foreach (var kvp in _controlStates)
            {
                if (kvp.Value.OccupancyAsserted && !_occupiedBoatsScratch.Contains(kvp.Key))
                    kvp.Value.OccupancyAsserted = false;
            }
        }

        private bool IsBoatEligibleForRopeAssert(string boatName, SaveableObject boat)
        {
            if (boat == null || !boat.gameObject.activeInHierarchy) return false;
            if (ShipyardSyncManager.IsBoatShipyardActive(boatName)) return false;
            if (ShipyardSyncManager.IsTrimRestorePending(boatName)) return false;
            if (IsRopeTrustSuspended(boatName)) return false;
            return true;
        }

        /// <summary>
        /// Host sends helm state to guest (state sync, not input)
        /// </summary>
        public void OnLocalHelmChanged(string boatName, float input, bool isFinal)
        {
            if (!Plugin.IsMultiplayer) return;
            // Only host sends helm state (guest sends input, not state)
            if (!Plugin.IsHost) return;

            VerboseLogger.ControlSend($"HelmState, boat={boatName}, input={input:F3}, final={isFinal}");

            var packet = new HelmStatePacket
            {
                BoatName = boatName,
                Input = input,
                IsFinal = isFinal
            };

            if (isFinal)
            {
                Plugin.NetworkManager.SendToAllReliable(PacketType.HelmState, w =>
                    PacketSerializer.WriteHelmState(w, packet));
            }
            else
            {
                Plugin.NetworkManager.SendToAllUnreliable(PacketType.HelmState, w =>
                    PacketSerializer.WriteHelmState(w, packet));
            }
        }

        /// <summary>
        /// Guest receives helm state from host - set spring target, let physics smooth it
        /// </summary>
        public void OnRemoteHelmChanged(HelmStatePacket packet)
        {
            // Only guest applies remote helm state
            if (Plugin.IsHost) return;

            VerboseLogger.ControlRecv($"HelmState, boat={packet.BoatName}, input={packet.Input:F3}");

            var boats = BoatUtility.FindAllBoats();
            if (!boats.TryGetValue(packet.BoatName, out var boat)) return;

            var wheel = boat.GetComponentInChildren<GPButtonSteeringWheel>();
            if (wheel == null) return;

            // HELM VISUAL-FIGHT: if THIS machine's local player is currently grabbing this wheel,
            // trust its own local prediction and ignore the incoming correction - otherwise the host's
            // relayed state yanks the wheel away from the local steerer's hands. IsHostSteeringWheel reads
            // the vanilla LOCAL-pointer grab fields (stickyClickedBy/isClicked/rotHandle.IsGrabbed), which
            // on ANY machine reflect the LOCAL pointer, so this correctly detects the local guest's grab.
            // BUT only trust local prediction if we actually HOLD the lease. A guest grabbing a wheel
            // another crew member is steering is denied by the host; in that case apply the correction so its
            // wheel follows the authoritative rudder instead of diverging for the whole grab.
            // AUTHORITATIVE SETTLE: a terminal HelmState (IsFinal=true - lease sweep / join seed) always
            // applies, even while this machine's player is grabbing the wheel. Guest prediction runs on
            // unreliable HelmInput deltas and can drift; the reliable final absolute is the value everyone
            // must converge to, so it beats local prediction. Non-final corrections stay suppressed while
            // steering so the stream doesn't yank the wheel out of the local steerer's hands.
            if (!packet.IsFinal && IsHostSteeringWheel(wheel) && !IsHelmDenied(packet.BoatName)) return;

            // Set the currentInput value for game logic
            wheel.currentInput = packet.Input;

            // Set HingeJoint spring target - physics will smooth the rudder movement
            // Game's ExtraLateUpdate will read rudder.currentAngle and update wheel visual
            if (wheel.attachedRudder != null)
            {
                float rotationAngleLimit = RotationAngleLimitRef(wheel);
                float springTarget = wheel.attachedRudder.limits.max * (packet.Input / rotationAngleLimit);
                var spring = wheel.attachedRudder.spring;
                spring.targetPosition = springTarget;
                wheel.attachedRudder.spring = spring;
            }

            VerboseLogger.ControlApply($"Helm spring set, boat={packet.BoatName}, input={packet.Input:F3}");
        }

        /// <summary>
        /// Guest sends its helm input to the host.
        ///
        /// (v0.3.0) Reports the ABSOLUTE resulting wheel angle as well as the delta, and is called AFTER
        /// the local prediction has run so the absolute is the angle this machine is actually showing. While
        /// this guest holds the helm lease the host adopts that number outright, which is what makes the
        /// person steering authoritative over their own wheel.
        /// </summary>
        public void OnLocalHelmInput(string boatName, float inputDelta, float absolute)
        {
            if (!Plugin.IsMultiplayer) return;
            // Only guest sends input to host
            if (Plugin.IsHost) return;

            VerboseLogger.ControlSend($"HelmInput, boat={boatName}, delta={inputDelta:F3}, abs={absolute:F3}");

            var packet = new HelmInputPacket
            {
                BoatName = boatName,
                InputDelta = inputDelta,
                Absolute = absolute
            };

            // Send unreliable for low latency (high frequency input)
            Plugin.NetworkManager.SendToAllUnreliable(PacketType.HelmInput, w =>
                PacketSerializer.WriteHelmInput(w, packet));
        }

        /// <summary>
        /// (v0.3.0) Guest: one RELIABLE final angle at the moment the wheel is let go.
        ///
        /// The steering stream is unreliable, which is right for something sent every frame - but it means
        /// the LAST packet of a turn is as droppable as any other, and that one matters. If it is lost the
        /// host settles one input short of where the helmsman actually stopped, and half a second later
        /// SweepStaleHelmLeases sends that slightly-stale angle back as a reliable terminal, which the guest
        /// applies. Adopting the helmsman's absolute removed the accumulated drift; this removes what was
        /// left, which is exactly one dropped packet's worth.
        ///
        /// Sent with a zero delta: nothing integrates it any more, and a phantom delta would be wrong for
        /// any peer that still did.
        /// </summary>
        public void SendFinalHelmAbsolute(string boatName, float absolute)
        {
            if (!Plugin.IsMultiplayer || Plugin.IsHost) return;
            if (Plugin.NetworkManager == null || string.IsNullOrEmpty(boatName)) return;
            if (float.IsNaN(absolute) || float.IsInfinity(absolute)) return;

            VerboseLogger.ControlSend($"HelmInput FINAL (wheel released), boat={boatName}, abs={absolute:F3}");

            Plugin.NetworkManager.SendToAllReliable(PacketType.HelmInput, w =>
                PacketSerializer.WriteHelmInput(w, new HelmInputPacket
                {
                    BoatName = boatName,
                    InputDelta = 0f,
                    Absolute = absolute
                }));
        }

        // === Helm single-controller lease ===
        // The host grants ONE helm-controller lease per boat, keyed by SteamId. The FIRST crew member to
        // feed HelmInput while no lease is held becomes the holder; the host then APPLIES HelmInput only
        // from that holder and IGNORES everyone else (no tug-of-war). The lease is released when the holder
        // stops steering (no input for HelmLeaseTimeout -> also frees a frozen holder) or disconnects.
        // At N=1 the lone guest is the first and only grabber, so it auto-holds the lease and every one of
        // its inputs applies - identical to the old "apply whatever the single guest sends" behavior.
        private readonly Dictionary<string, SteamId> _helmLeaseHolder = new Dictionary<string, SteamId>();
        private readonly Dictionary<string, float> _helmLeaseLastInput = new Dictionary<string, float>();
        private const float HelmLeaseTimeout = 0.5f; // release the wheel if the holder sends no input for this long

        // A guest grabbing a wheel another crew member is steering must NOT diverge. The host tells a
        // rejected guest (no lease) via HelmDenied; that guest then stops local prediction and accepts the
        // host's corrections instead of suppressing them. Guest tracks a per-boat denial window; host throttles
        // the signal so a continuously-turning denied guest doesn't trigger a packet storm.
        private readonly Dictionary<string, float> _helmDeniedUntil = new Dictionary<string, float>();   // guest: boat -> Time.time until which we're denied
        private readonly Dictionary<string, float> _lastHelmDeniedSent = new Dictionary<string, float>(); // host: boat -> last HelmDenied send time
        private const float HelmDeniedWindow = 1.0f;   // guest stays "denied" this long after the last HelmDenied
        private const float HelmDeniedThrottle = 0.4f; // host sends at most one HelmDenied per boat per this interval

        /// <summary>Guest: true while this machine's helm input for the boat is being rejected (another crew holds the lease).</summary>
        public bool IsHelmDenied(string boatName) =>
            _helmDeniedUntil.TryGetValue(boatName, out var until) && Time.time < until;

        /// <summary>Guest: host told us our helm input was rejected; stay "denied" briefly so we stop predicting and accept corrections.</summary>
        public void OnHelmDeniedReceived(HelmDeniedPacket packet)
        {
            if (Plugin.IsHost) return;
            _helmDeniedUntil[packet.BoatName] = Time.time + HelmDeniedWindow;
            VerboseLogger.ControlRecv($"HelmDenied, boat={packet.BoatName} (another crew member holds the wheel)");
        }

        /// <summary>Host: tell a guest its helm input was rejected (throttled per boat+target so two contending
        /// guests on the same wheel each still get their own denials).</summary>
        private void SendHelmDenied(SteamId target, string boatName)
        {
            string key = boatName + "|" + target.Value;
            float last = _lastHelmDeniedSent.TryGetValue(key, out var t) ? t : -999f;
            if (Time.time - last < HelmDeniedThrottle) return;
            _lastHelmDeniedSent[key] = Time.time;
            Plugin.NetworkManager.SendReliable(target, PacketType.HelmDenied, w =>
                PacketSerializer.WriteHelmDenied(w, new HelmDeniedPacket { BoatName = boatName }));
        }

        /// <summary>
        /// Host: try to grant/refresh the helm lease for <paramref name="boatName"/> to <paramref name="sender"/>.
        /// Returns true if the sender currently HOLDS the lease (input should be applied), false if another
        /// crew member holds it (input ignored).
        /// </summary>
        private bool TryAcquireHelmLease(string boatName, SteamId sender)
        {
            // Release a stale lease whose holder has gone quiet (let go / froze), so the wheel never sticks.
            if (_helmLeaseHolder.TryGetValue(boatName, out var holder))
            {
                if (holder != sender)
                {
                    float last = _helmLeaseLastInput.TryGetValue(boatName, out var t) ? t : 0f;
                    if (Time.time - last <= HelmLeaseTimeout)
                        return false; // someone else actively holds the wheel -> ignore this sender
                    // Holder idle past the timeout -> lease is free; fall through and grant to sender.
                    VerboseLogger.ControlEvent($"Helm lease on {boatName} timed out for {holder}, regranting");
                }
            }

            // Grant (or refresh) the lease to sender.
            if (!_helmLeaseHolder.TryGetValue(boatName, out var cur) || cur != sender)
            {
                _helmLeaseHolder[boatName] = sender;
                VerboseLogger.ControlEvent($"Helm lease on {boatName} granted to {sender}");
            }
            _helmLeaseLastInput[boatName] = Time.time;
            return true;
        }

        /// <summary>
        /// Host: periodically release helm leases whose holder has gone idle past HelmLeaseTimeout. The lazy
        /// release inside TryAcquireHelmLease only fires when ANOTHER sender's input arrives; a passive
        /// passenger never triggers it, so a dropped final unreliable relay would leave passengers on a stale
        /// wheel angle until someone steers that wheel again. On expiry we ALWAYS send ONE reliable terminal
        /// HelmState{IsFinal=true} - the host's own current boat included: the ex-steering guest there
        /// predicted locally off unreliable HelmInput deltas and may have drifted, and PollBoatControls only
        /// re-sends when currentInput CHANGES, so without this terminal no authoritative absolute ever
        /// reaches it after release (the stuck-diverged-wheel visual). OnRemoteHelmChanged lets IsFinal
        /// packets through even on a locally-held wheel. The lease is then dropped (the next input simply
        /// re-grants from scratch).
        /// </summary>
        private void SweepStaleHelmLeases()
        {
            if (_helmLeaseHolder.Count == 0) return;

            List<string> expired = new List<string>();
            foreach (var kvp in _helmLeaseLastInput)
                if (Time.time - kvp.Value > HelmLeaseTimeout) expired.Add(kvp.Key);
            if (expired.Count == 0) return;

            foreach (var boatName in expired)
            {
                var boats = BoatUtility.FindAllBoats();
                if (boats.TryGetValue(boatName, out var boat))
                {
                    var wheel = boat.GetComponentInChildren<GPButtonSteeringWheel>();
                    if (wheel != null)
                    {
                        float finalInput = wheel.currentInput;
                        Plugin.NetworkManager.SendToAllReliable(PacketType.HelmState, w =>
                            PacketSerializer.WriteHelmState(w, new HelmStatePacket
                            {
                                BoatName = boatName,
                                Input = finalInput,
                                IsFinal = true
                            }));
                    }
                }
                _helmLeaseHolder.Remove(boatName);
                _helmLeaseLastInput.Remove(boatName);
                VerboseLogger.ControlEvent($"Helm lease on {boatName} swept (idle > {HelmLeaseTimeout}s); sent terminal HelmState");
            }
        }

        /// <summary>
        /// Release any helm lease held by <paramref name="peer"/> (on disconnect). Other boats' leases held
        /// by other crew are untouched. At N=1 this frees the single lease the lone guest held.
        /// </summary>
        public void ReleaseHelmLeasesForPeer(SteamId peer)
        {
            var toRelease = new List<string>();
            foreach (var kvp in _helmLeaseHolder)
                if (kvp.Value == peer) toRelease.Add(kvp.Key);
            foreach (var boatName in toRelease)
            {
                _helmLeaseHolder.Remove(boatName);
                _helmLeaseLastInput.Remove(boatName);
                VerboseLogger.ControlEvent($"Helm lease on {boatName} released (peer {peer} disconnected)");
            }
        }

        // Read-only refs to the vanilla local-grab state on the steering wheel. These are GoPointerButton
        // fields driven ONLY by the LOCAL pointer's StickyClick/Click; a remote guest's HelmInput packet
        // never sets them. So on the HOST, a non-null stickyClickedBy / isClicked / a grabbed rotHandle means
        // the HOST'S OWN local player is steering this wheel (the exact signal vanilla ExtraLateUpdate and
        // SteeringWheelGuestPatch use). We only READ these to suppress guest input - we never write them.
        private static readonly AccessTools.FieldRef<GoPointerButton, GoPointer> HelmStickyClickedByRef =
            AccessTools.FieldRefAccess<GoPointerButton, GoPointer>("stickyClickedBy");
        private static readonly AccessTools.FieldRef<GoPointerButton, bool> HelmIsClickedRef =
            AccessTools.FieldRefAccess<GoPointerButton, bool>("isClicked");
        private static readonly AccessTools.FieldRef<GPButtonSteeringWheel, TouchRotateHandle> HelmRotHandleRef =
            AccessTools.FieldRefAccess<GPButtonSteeringWheel, TouchRotateHandle>("rotHandle");

        /// <summary>
        /// True if the HOST's own local player is actively steering <paramref name="wheel"/> right now.
        /// Read-only: mirrors vanilla's "(bool)stickyClickedBy || isClicked || rotHandle.IsGrabbed()" grab
        /// test. Only meaningful on the host (the only local pointer there is the host's player).
        /// </summary>
        private static bool IsHostSteeringWheel(GPButtonSteeringWheel wheel)
        {
            if (wheel == null) return false;
            var rotHandle = HelmRotHandleRef(wheel);
            return HelmStickyClickedByRef(wheel) != null
                || HelmIsClickedRef(wheel)
                || (rotHandle != null && rotHandle.IsGrabbed());
        }

        /// <summary>
        /// Host receives helm input from guest - apply to wheel ONLY if the guest holds the helm lease AND
        /// the host's own local player is not currently steering this wheel.
        /// </summary>
        public void OnRemoteHelmInput(SteamId sender, HelmInputPacket packet)
        {
            // Only host applies input from guest
            if (!Plugin.IsHost) return;

            var boats = BoatUtility.FindAllBoats();
            if (!boats.TryGetValue(packet.BoatName, out var boat)) return;

            var wheel = boat.GetComponentInChildren<GPButtonSteeringWheel>();
            if (wheel == null) return;

            // HOST-STEERING GUARD: if the HOST's own local player is steering this
            // wheel, the host is authoritative over it - ignore the guest's input entirely (do NOT grant or
            // refresh the lease, do NOT apply) so the two never fight over wheel.currentInput. This only
            // SUPPRESSES guest input; it never touches the host's vanilla steering writes. At N=1 the host is
            // only ever steering when no guest is, so a lone guest is unaffected and behaves exactly as before.
            if (IsHostSteeringWheel(wheel))
            {
                // HELM LEASE-STEAL guard: while the host temporarily overrides the wheel, the genuine
                // lease holder is still actively steering - refresh its liveness so its lease doesn't go
                // stale and get re-granted to a different crew member when the host lets go.
                if (_helmLeaseHolder.TryGetValue(packet.BoatName, out var h) && h == sender)
                    _helmLeaseLastInput[packet.BoatName] = Time.time;
                // Host-driver case: the host's own player is driving and wins. Tell the grabbing guest it's
                // denied so it stops predicting and follows the host's authoritative wheel instead of diverging
                // for the whole grab. This is the MOST COMMON driven-wheel case at N=2 (host steers, a guest also
                // grabs) - the lease-rejection path below only covers another GUEST holding the lease.
                SendHelmDenied(sender, packet.BoatName);
                VerboseLogger.ControlRecv($"HelmInput IGNORED (host steering), boat={packet.BoatName}, from={sender}");
                return;
            }

            // SINGLE-CONTROLLER LEASE: ignore input from any crew member who doesn't hold the wheel.
            if (!TryAcquireHelmLease(packet.BoatName, sender))
            {
                // Tell the rejected guest so it stops predicting locally and accepts our corrections,
                // instead of steering its own diverged wheel for the whole grab. Throttled per boat.
                SendHelmDenied(sender, packet.BoatName);
                VerboseLogger.ControlRecv($"HelmInput IGNORED (no lease), boat={packet.BoatName}, from={sender}");
                return;
            }

            VerboseLogger.ControlRecv($"HelmInput, boat={packet.BoatName}, delta={packet.InputDelta:F3}, abs={packet.Absolute:F3}, from={sender}");

            // (v0.3.0) THE HELMSMAN OWNS THEIR OWN WHEEL. The host used to integrate the deltas
            // (`currentInput += InputDelta`) and therefore held its own opinion of the angle, arrived at by a
            // different route from the guest's local prediction. Deltas ride an UNRELIABLE channel, so the two
            // drifted apart on every dropped packet, and 0.5s after the guest stopped turning,
            // SweepStaleHelmLeases sent a reliable terminal carrying the HOST's number - which the guest
            // applies unconditionally. That is the snap that made steering feel clunky: not a correction
            // while you steer, but a jolt half a second after you stop.
            //
            // Adopting the helmsman's absolute removes both halves at once. There is no second opinion to
            // drift from, and the terminal the sweep later sends is the guest's own value coming back, so it
            // lands as a no-op. A dropped packet now costs one stale frame instead of permanent divergence,
            // because the next absolute is self-correcting where a missed delta was lost forever.
            //
            // Authority is unchanged: the wheel angle is an INPUT to the boat's physics, which the host still
            // runs and still streams. Only the origin of this one scalar moves.
            if (!float.IsNaN(packet.Absolute) && !float.IsInfinity(packet.Absolute))
                wheel.currentInput = packet.Absolute;
            else
                wheel.currentInput += packet.InputDelta; // pre-v0.3.0 sender, or a truncated packet

            // Apply rotation limit (same logic as game)
            float rotationAngleLimit = RotationAngleLimitRef(wheel);
            if (wheel.currentInput > rotationAngleLimit)
                wheel.currentInput = rotationAngleLimit;
            if (wheel.currentInput < -rotationAngleLimit)
                wheel.currentInput = -rotationAngleLimit;

            // Calculate rudder angle
            float rudderAngle = wheel.currentInput / wheel.gearRatio;

            // Apply to rudder spring (same as game's ApplyRudderRotation)
            float targetPosition = wheel.attachedRudder.limits.max * (wheel.currentInput / rotationAngleLimit);
            var spring = wheel.attachedRudder.spring;
            spring.targetPosition = targetPosition;
            wheel.attachedRudder.spring = spring;

            // ALSO directly set the rudder transform AND currentAngle field
            // - Transform: for physics continuity
            // - currentAngle: so ApplyWheelRotationFromRudder reads correct value immediately
            //   (otherwise it only updates at FixedUpdate rate = choppy wheel visual)
            var rudder = wheel.attachedRudder.GetComponent<Rudder>();
            if (rudder != null)
            {
                var euler = rudder.transform.localEulerAngles;
                rudder.transform.localEulerAngles = new Vector3(euler.x, rudderAngle, euler.z);
                rudder.currentAngle = rudderAngle;
            }

            // HELM RELAY: the host re-broadcasts the authoritative wheel state so it propagates to the
            // OTHER guests even when the host is NOT standing on the steered boat (the poll only sends
            // HelmState for the boat the host is on). UNRELIABLE - helm is high-frequency. Also stamp the
            // helm baseline so the host's own poll doesn't re-send the same state again - an EXACT per-boat
            // stamp now (the single-scalar era had to skip it whenever the host stood elsewhere, forfeiting
            // the dedup for every off-boat steer).
            Plugin.NetworkManager.SendToAllExcept(sender, PacketType.HelmState, w =>
                PacketSerializer.WriteHelmState(w, new HelmStatePacket
                {
                    BoatName = packet.BoatName,
                    Input = wheel.currentInput,
                    IsFinal = false
                }), reliable: false);
            GetOrCreateControlState(packet.BoatName).LastHelmInput = wheel.currentInput;

            VerboseLogger.ControlApply($"HelmInput applied, boat={packet.BoatName}, newInput={wheel.currentInput:F3}");
        }

        // === Helm Lock Sync ===

        // Access private locked field
        private static readonly AccessTools.FieldRef<GPButtonSteeringWheel, bool> LockedRef =
            AccessTools.FieldRefAccess<GPButtonSteeringWheel, bool>("locked");

        /// <summary>
        /// (v0.2.34) Set the shared wheel's lock to an ABSOLUTE desired state (host-authoritative). This
        /// used to be a blind TOGGLE request - the guest sent a value-less "flip it" and the host flipped
        /// its OWN state - so a single dropped/reordered HelmLock permanently INVERTED guest-vs-host lock
        /// parity. Once inverted, a guest could sit {locked=true} while the host thought unlocked, and the
        /// steering prefix gates all rudder input behind !locked, so the guest's wheel input went dead (a
        /// confirmed cause of the stuck-rudder report). Absolute-set is idempotent and self-correcting: the
        /// host re-broadcasts the authoritative value, so at worst one action is lost, never inverted.
        /// desiredLocked is the caller's intended NEW lock state.
        /// </summary>
        public void OnLocalHelmLockSet(string boatName, bool desiredLocked)
        {
            if (!Plugin.IsMultiplayer) return;

            var boats = BoatUtility.FindAllBoats();
            if (!boats.TryGetValue(boatName, out var boat)) return;
            var wheel = boat.GetComponentInChildren<GPButtonSteeringWheel>();
            if (wheel == null) return;

            if (Plugin.IsHost)
            {
                LockedRef(wheel) = desiredLocked;
                // Per-boat exact baseline stamp - the single-flag era had to skip non-current boats here to
                // avoid cross-boat aliasing the poll.
                GetOrCreateControlState(boatName).LastHelmLocked = desiredLocked;
                Juicebox.juice.PlaySoundAt("lock unlock", wheel.transform.position, 0f, 0.66f, desiredLocked ? 0.88f : 1f);
                VerboseLogger.ControlLocal($"Host helm lock set: {desiredLocked}, boat={boatName}");
                BroadcastHelmLock(boatName, desiredLocked);
            }
            else
            {
                // Optimistic local set + absolute request; the host echoes the authoritative state.
                LockedRef(wheel) = desiredLocked;
                Juicebox.juice.PlaySoundAt("lock unlock", wheel.transform.position, 0f, 0.66f, desiredLocked ? 0.88f : 1f);
                SendGuestHelmLockRequest(boatName, desiredLocked);
            }
        }

        /// <summary>
        /// Alt-click toggle entry: reads the current lock and requests its inverse as an absolute value.
        /// </summary>
        public void OnLocalHelmLockToggle(string boatName)
        {
            if (!Plugin.IsMultiplayer) return;
            var boats = BoatUtility.FindAllBoats();
            if (!boats.TryGetValue(boatName, out var boat)) return;
            var wheel = boat.GetComponentInChildren<GPButtonSteeringWheel>();
            if (wheel == null) return;
            OnLocalHelmLockSet(boatName, !LockedRef(wheel));
        }

        /// <summary>
        /// (v0.2.34, GAP B) Guest vanilla click-to-unlock sync. Vanilla GPButtonSteeringWheel.OnActivate
        /// calls the private Unlock() when you click a LOCKED wheel, clearing the LOCAL locked flag with NO
        /// packet - so the host kept the wheel locked and the guest's steering stayed gated behind the stale
        /// lock. Vanilla already did the local set + sound, so this only sends the absolute request.
        /// Guest-only (the host's own unlock is caught by the lock poll in PollBoatControls).
        /// </summary>
        public void OnLocalHelmClickUnlock(string boatName)
        {
            if (!Plugin.IsMultiplayer || Plugin.IsHost) return;
            SendGuestHelmLockRequest(boatName, false);
        }

        private void SendGuestHelmLockRequest(string boatName, bool desiredLocked)
        {
            VerboseLogger.ControlSend($"Guest helm lock request (absolute): {desiredLocked}, boat={boatName}");
            var packet = new HelmLockPacket { BoatName = boatName, IsLocked = desiredLocked };
            Plugin.NetworkManager.SendToAllReliable(PacketType.HelmLock, w =>
                PacketSerializer.WriteHelmLock(w, packet));
        }

        /// <summary>
        /// Host receives a guest's ABSOLUTE helm-lock request - SET (not toggle) and broadcast. The blind
        /// toggle it replaced inverted parity permanently on any dropped request (see OnLocalHelmLockSet).
        /// </summary>
        public void OnRemoteHelmLockToggle(HelmLockPacket packet)
        {
            if (!Plugin.IsHost) return;

            VerboseLogger.ControlRecv($"HelmLock request, boat={packet.BoatName}, locked={packet.IsLocked}");

            var boats = BoatUtility.FindAllBoats();
            if (!boats.TryGetValue(packet.BoatName, out var boat)) return;

            var wheel = boat.GetComponentInChildren<GPButtonSteeringWheel>();
            if (wheel == null) return;

            if (LockedRef(wheel) != packet.IsLocked)
            {
                LockedRef(wheel) = packet.IsLocked;
                Juicebox.juice.PlaySoundAt("lock unlock", wheel.transform.position, 0f, 0.66f, packet.IsLocked ? 0.88f : 1f);
                VerboseLogger.ControlApply($"Host helm lock set from guest request: {packet.IsLocked}");
            }
            // Per-boat exact baseline stamp (the single-flag era had to skip non-current boats to avoid
            // cross-boat aliasing; the dedup now also covers a boat the host boards later).
            GetOrCreateControlState(packet.BoatName).LastHelmLocked = packet.IsLocked;
            // Re-broadcast authoritative state so every peer (incl. the requester) converges.
            BroadcastHelmLock(packet.BoatName, packet.IsLocked);
        }

        /// <summary>
        /// Host broadcasts helm lock state to all guests
        /// </summary>
        private void BroadcastHelmLock(string boatName, bool isLocked)
        {
            VerboseLogger.ControlSend($"HelmLock broadcast, boat={boatName}, locked={isLocked}");

            var packet = new HelmLockPacket
            {
                BoatName = boatName,
                IsLocked = isLocked
            };

            Plugin.NetworkManager.SendToAllReliable(PacketType.HelmLock, w =>
                PacketSerializer.WriteHelmLock(w, packet));
        }

        /// <summary>
        /// Guest receives helm lock state from host
        /// </summary>
        public void OnRemoteHelmLockState(HelmLockPacket packet)
        {
            if (Plugin.IsHost) return;

            VerboseLogger.ControlRecv($"HelmLock state, boat={packet.BoatName}, locked={packet.IsLocked}");

            var boats = BoatUtility.FindAllBoats();
            if (!boats.TryGetValue(packet.BoatName, out var boat)) return;

            var wheel = boat.GetComponentInChildren<GPButtonSteeringWheel>();
            if (wheel == null) return;

            bool currentLocked = LockedRef(wheel);
            if (currentLocked != packet.IsLocked)
            {
                LockedRef(wheel) = packet.IsLocked;
                VerboseLogger.ControlApply($"Guest helm lock set: {packet.IsLocked}");

                // (v0.2.34, GAP A) A remote LOCK must release the LOCAL steerer, or a guest holding the wheel
                // when another crew member locks it is left {holding, locked=true} -> the steering prefix
                // gates all input behind !locked, so the guest's wheel input goes dead until it manually lets
                // go (a confirmed cause of the stuck-rudder report). Vanilla Lock() only UnStickyClicks (the
                // keyboard/VR sticky grab); we ALSO Unclick() so a MOUSE steerer (isClicked) is freed too -
                // otherwise the "input dead on remote lock" symptom would persist for the mouse-steer case.
                // UnStickyClick self-guards on stickyClickedBy != null (GoPointerButton.cs:127) and Unclick is
                // an unconditional field clear (GoPointerButton.cs:111-115); both are safe no-ops when not
                // held. Only on the unlocked->locked transition (skipped when the guest already matched the
                // state for its OWN lock).
                if (packet.IsLocked)
                {
                    wheel.UnStickyClick();
                    wheel.Unclick();
                }
            }
        }

        // === Anchor Sync ===

        public void OnLocalAnchorChanged(string boatName, bool isSet, float ropeLength)
        {
            if (!Plugin.IsMultiplayer) return;

            VerboseLogger.ControlSend($"AnchorEvent, boat={boatName}, set={isSet}, ropeLen={ropeLength:F2}");

            var packet = new AnchorEventPacket
            {
                BoatName = boatName,
                IsSet = isSet,
                RopeLength = ropeLength
            };

            Plugin.NetworkManager.SendToAllReliable(PacketType.AnchorEvent, w =>
                PacketSerializer.WriteAnchorEvent(w, packet));
        }

        /// <summary>
        /// Kedging-winch lunge (Robin report, v0.2.25): the anchor's dropped WORLD position is never on
        /// the wire (AnchorEventPacket = IsSet + RopeLength only), so a guest's anchor freezes kinematic
        /// at whatever pose its LOCAL sim happened to have - metres to tens of metres from the host's true
        /// drop point, and drifting further as the host boat kedges while the guest boat is streamed after
        /// it. The moment anyone winches in, the guest's own RopeControllerAnchor shrinks the LOCAL
        /// ConfigurableJoint limit below the boat<->stale-anchor distance and the hard constraint yanks the
        /// streamed hull toward the wrong point - the violent lunge (guest screen only). v0.2.26's
        /// SnapStrandedAnchor is gated to impossible >maxLen+50m geometry, so a normal kedge divergence
        /// never trips it. Guests never author boat physics, so their anchor joint has no authority:
        /// each control tick, if the set (kinematic) anchor sits outside the current joint limit (+2m
        /// slack), drag the frozen body back along the same bearing to just inside the limit. The joint
        /// then never builds a corrective impulse; the visible boat motion stays whatever the host streams.
        /// Direction is preserved so the rendered anchor rope still points at the kedge, and a body pinned
        /// AT the hawse falls back to straight down.
        /// </summary>
        private void RelaxGuestAnchorTether()
        {
            var boat = BoatUtility.GetCurrentBoat();
            if (boat == null) return;

            var anchor = BoatUtility.GetAnchor(boat);
            if (anchor == null || !anchor.IsSet()) return; // only a frozen (kinematic) anchor can be stale

            var joint = anchor.GetComponent<ConfigurableJoint>();
            var rb = anchor.GetComponent<Rigidbody>();
            if (joint == null || rb == null) return;

            var hawse = boat.GetComponent<BoatMooringRopes>()?.GetAnchorController()?.transform.position
                        ?? boat.transform.position;
            float limit = joint.linearLimit.limit;
            var delta = anchor.transform.position - hawse;
            float span = delta.magnitude;
            if (span <= limit + 2f) return; // inside the constraint - nothing to relax

            var dir = span > 0.05f ? delta / span : Vector3.down;
            // 70% of the limit, not limit-1 (v0.2.29): parking the body NEAR-taut let ordinary boat
            // drift re-tauten the joint between control ticks, and vanilla's taut-release condition
            // (Anchor.ExtraFixedUpdate, force > unsetResistance at <60 deg) fired on the guest one
            // fixed frame after every remote set - half of the 0711 "ship spazzes out when anyone
            // touches the anchor" ping-pong (the other half is the guest auto-transition block in
            // ControlPatches). Real slack keeps joint force at zero so the local sim never fights.
            float relaxedSpan = Mathf.Max(limit * 0.7f, 0.5f);
            var relaxed = hawse + dir * relaxedSpan;
            anchor.transform.position = relaxed;
            rb.position = relaxed; // transform writes alone don't reliably move the physics pose
            VerboseLogger.ControlApply($"Anchor tether relaxed: span {span:F1}m > limit {limit:F1}m on '{boat.gameObject.name}'; " +
                                       $"frozen anchor body pulled to {relaxedSpan:F1}m to keep the local joint slack");
        }

        public void OnRemoteAnchorChanged(AnchorEventPacket packet, SteamId sender = default)
        {
            VerboseLogger.ControlRecv($"AnchorEvent, boat={packet.BoatName}, set={packet.IsSet}, ropeLen={packet.RopeLength:F2}");

            // STAR host-relay: a guest's anchor change is a request; the host applies + relays the
            // authoritative result to the other guests. At N=1 SendToAllExcept(sender) is a no-op.
            if (Plugin.IsHost)
            {
                Plugin.NetworkManager.SendToAllExcept(sender, PacketType.AnchorEvent,
                    w => PacketSerializer.WriteAnchorEvent(w, packet));
            }

            var boats = BoatUtility.FindAllBoats();
            if (!boats.TryGetValue(packet.BoatName, out var boat))
            {
                Plugin.Log.LogWarning($"AnchorEvent DROPPED: no boat named '{packet.BoatName}' " +
                    $"({boats.Count} boats known) - sender/receiver name mismatch?");
                return;
            }

            var anchor = BoatUtility.GetAnchor(boat);
            if (anchor == null)
            {
                Plugin.Log.LogWarning($"AnchorEvent DROPPED: no Anchor resolvable on boat '{packet.BoatName}'");
                return;
            }

            var rb = anchor.GetComponent<Rigidbody>();
            var joint = anchor.GetComponent<ConfigurableJoint>();

            // Prevent feedback loop - don't let Harmony patches send packets while applying remote state
            IsApplyingRemoteState = true;
            try
            {
                // Drive the anchored state through vanilla SetAnchor/ReleaseAnchor (private, via reflection)
                // so the authoritative private `set` flag, drag, isKinematic AND audio all match. Just writing
                // rb.isKinematic was reverted within a physics frame by the guest's own Anchor.ExtraFixedUpdate
                // (line ~131 forces isKinematic=held whenever !set), so the relayed anchor never actually
                // set/released on a guest. The AnchorSet/Release patches short-circuit on IsApplyingRemoteState,
                // so invoking the vanilla methods here does NOT echo. Only transition when the state differs.
                // Stranded-anchor guard: never let SetAnchor freeze the body kinematic at an impossible
                // position (a stale pre-teleport pose left over from a bad join). See SnapStrandedAnchor.
                var anchorRopeCtrl = boat.GetComponent<BoatMooringRopes>()?.GetAnchorController();
                BoatStateApplicator.SnapStrandedAnchor(boat, anchor, anchorRopeCtrl, packet.IsSet, packet.RopeLength);

                bool currentlySet = anchor.IsSet();
                if (packet.IsSet && !currentlySet)
                    AccessTools.Method(typeof(Anchor), "SetAnchor")?.Invoke(anchor, null);
                else if (!packet.IsSet && currentlySet)
                    AccessTools.Method(typeof(Anchor), "ReleaseAnchor")?.Invoke(anchor, null);
                else if (rb != null)
                    rb.isKinematic = packet.IsSet; // already in the right set-state; keep kinematic consistent

                if (joint != null)
                {
                    var limit = joint.linearLimit;
                    limit.limit = packet.RopeLength;
                    joint.linearLimit = limit;
                }

                VerboseLogger.ControlApply($"Anchor set={packet.IsSet}, boat={packet.BoatName}, ropeLen={packet.RopeLength:F2}");
            }
            finally
            {
                IsApplyingRemoteState = false;
            }
        }

        // === Mooring Sync ===

        public void OnLocalMooringChanged(string boatName, int ropeIndex, bool isMoored,
            Vector3 dockPosition, float lengthSquared)
            => OnLocalMooringChanged(boatName, ropeIndex, isMoored, dockPosition, lengthSquared,
                MooringTargetKind.Dock, null, null);

        public void OnLocalMooringChanged(string boatName, int ropeIndex, bool isMoored,
            Vector3 dockPosition, float lengthSquared,
            MooringTargetKind targetKind, string towBoatName, string cleatPath)
        {
            if (!Plugin.IsMultiplayer) return;

            VerboseLogger.ControlSend($"MooringState, boat={boatName}, rope={ropeIndex}, moored={isMoored}, kind={targetKind}, dockPos={dockPosition}, towBoat={towBoatName}, cleat={cleatPath}");

            var packet = new MooringStatePacket
            {
                BoatName = boatName,
                RopeIndex = ropeIndex,
                IsMoored = isMoored,
                TargetKind = targetKind,
                DockPosition = dockPosition,
                LengthSquared = lengthSquared,
                TowBoatName = towBoatName ?? "",
                CleatPath = cleatPath ?? ""
            };

            Plugin.NetworkManager.SendToAllReliable(PacketType.MooringState, w =>
                PacketSerializer.WriteMooringState(w, packet));
        }

        // Dock-resolve retry ledger (Robin report, v0.2.25 "moor rope snapped back then vanished for the
        // host only"): FindClosestDockMooring reconstructs the dock from realPos + THIS client's floating-
        // origin offset; over a multi-hour session the peers' reconstructions drift, and island streaming
        // can leave dock objects momentarily inactive - both make the 5m match miss TRANSIENTLY or by a few
        // metres while the moor is perfectly real on the sender. Stowing on the first miss deleted the rope
        // here while the sender kept it. Retry the same packet a few times before giving up.
        private readonly Dictionary<string, int> _moorRetryCounts = new Dictionary<string, int>();
        private const int MoorResolveMaxAttempts = 4;      // 1 immediate + 3 retries over ~3s
        private const float MoorResolveRetryDelay = 1.0f;
        // Generation stamp per boat|rope: every NON-retry mooring packet bumps it, so a pending retry of
        // an older packet aborts instead of re-applying a moor the sender has since unmoored/re-moored.
        private readonly Dictionary<string, int> _moorPacketGen = new Dictionary<string, int>();

        private System.Collections.IEnumerator RetryMoorAfterDelay(MooringStatePacket packet, SteamId sender, string retryKey, int expectedGen)
        {
            yield return new WaitForSeconds(MoorResolveRetryDelay);
            if (!_moorPacketGen.TryGetValue(retryKey, out int gen) || gen != expectedGen)
            {
                _moorRetryCounts.Remove(retryKey);
                VerboseLogger.ControlApply($"Moor retry for {retryKey} superseded by a newer mooring packet; dropped");
                yield break;
            }
            OnRemoteMooringChanged(packet, sender, isRetry: true);
        }

        // Consistency backstop: when this machine is the HOST and its guards had to abandon a guest's moor
        // (stretch guard, or dock resolve still missing after retries), the abandonment used to happen under
        // IsApplyingRemoteState, so the Unmoor postfix never broadcast it - host and originator silently
        // diverged ("rope gone for host, still there for the client"). Send an explicit authoritative
        // unmoor to EVERYONE (originator included) so the whole crew converges on the conservative state.
        private void BroadcastCorrectiveUnmoor(MooringStatePacket packet, string reason)
        {
            if (!Plugin.IsHost) return;
            Plugin.Log.LogWarning($"Rope {packet.RopeIndex} ({packet.BoatName}): host abandoning relayed moor ({reason}); " +
                                  "broadcasting corrective unmoor so the crew converges");
            OnLocalMooringChanged(packet.BoatName, packet.RopeIndex, false, Vector3.zero, 0f);
        }

        public void OnRemoteMooringChanged(MooringStatePacket packet, SteamId sender = default, bool isRetry = false)
        {
            VerboseLogger.ControlRecv($"MooringState, boat={packet.BoatName}, rope={packet.RopeIndex}, moored={packet.IsMoored}, retry={isRetry}");

            // STAR host-relay: a guest's mooring change is a request; the host applies + relays the
            // authoritative result to the other guests. At N=1 SendToAllExcept(sender) is a no-op.
            // A local retry re-enters this method for the APPLY only - never re-relay it.
            if (Plugin.IsHost && !isRetry)
            {
                Plugin.NetworkManager.SendToAllExcept(sender, PacketType.MooringState,
                    w => PacketSerializer.WriteMooringState(w, packet));
            }

            var boats = BoatUtility.FindAllBoats();
            if (!boats.TryGetValue(packet.BoatName, out var boat))
            {
                VerboseLogger.ControlApply($"Mooring FAILED: boat '{packet.BoatName}' not found");
                return;
            }

            var mooringRopes = boat.GetComponent<BoatMooringRopes>();
            if (mooringRopes == null || mooringRopes.ropes == null) return;
            if (packet.RopeIndex < 0 || packet.RopeIndex >= mooringRopes.ropes.Length) return;

            var rope = mooringRopes.ropes[packet.RopeIndex];

            string retryKey = packet.BoatName + "|" + packet.RopeIndex;
            if (!isRetry)
            {
                // A fresh authoritative packet supersedes any pending dock-miss retry of an older one.
                _moorPacketGen.TryGetValue(retryKey, out int g);
                _moorPacketGen[retryKey] = g + 1;
                _moorRetryCounts.Remove(retryKey);
            }

            // Mark rope as network-changed to prevent feedback from local patches
            MarkRopeAsNetworkChanged(rope);

            IsApplyingRemoteState = true;
            try
            {
                // Contested-grab guard: if the LOCAL player is holding this rope, force-release it
                // before applying the authoritative remote state. Vanilla enforces "held ropes are
                // never moored" (OnPickup unmoors, OnTriggerEnter requires !held); applying MoorTo
                // to a held rope breaks that invariant and GoPointer.Update then drags the moored
                // rope to the player's hand every frame (the mid-air phantom rope). Covers BOTH
                // branches: moor (invariant restore) and unmoor (stops GoPointer fighting the
                // forced hanger reset below). rope.held is only ever set by the local GoPointer,
                // so this cannot touch remote avatars. DropItem() fires no Harmony-patched mooring
                // methods and does NOT call OnDrop, so no echo/throw-back is generated.
                if (rope.held != null)
                {
                    // NOTE: DropItem() fires the mod's GoPointer drop prefix, but the rope is a
                    // PickupableItem (not a ShipItem), so OnLocalDrop's ShipItem cast bails and nothing
                    // broadcasts - if the drop patch is ever broadened past ShipItem, this force-release
                    // must gain an explicit suppression (review finding).
                    var pointer = rope.held;
                    pointer.DropItem();
                    // Parent-safe hanger restore (bare ResetRopePos() writes LOCAL position - on a
                    // detached parent==null rope that is a WORLD-space write near the origin). The helper
                    // re-parents first and clears the was-moored save flag; a held rope is never moored
                    // (vanilla OnPickup unmoors), so the helper's IsMoored bail can't skip this reset.
                    BoatStateApplicator.StowRopeIfDisplaced(rope, $"Rope {packet.RopeIndex} force-release ({packet.BoatName})");
                    VerboseLogger.ControlApply($"Force-released locally-held rope {packet.RopeIndex} (remote mooring state wins)");
                    Plugin.Notify("Mooring rope taken by a crewmate");
                }

                if (packet.IsMoored)
                {
                    GPButtonDockMooring dock;
                    float nearestMissDist = float.PositiveInfinity;
                    if (packet.TargetKind == MooringTargetKind.BoatCleat)
                    {
                        // (v0.2.32) Cleat reference: resolve towing boat by name, cleat by path. The
                        // rest of the moor apply (Unmoor-before-remoor, authoritative lenSq +
                        // spring.maxDistance overwrite, 50m stretch guard, retry ledger) is SHARED
                        // with docks - the cleat is just a GPButtonDockMooring that happens to move.
                        // Guests must never re-derive spring params: spring = towedMass * 6 is baked
                        // at MoorTo time and mass differs per client (+160 kg local-player term).
                        dock = ResolveCleat(packet.TowBoatName, packet.CleatPath);
                    }
                    else
                    {
                        dock = FindClosestDockMooring(packet.DockPosition, out nearestMissDist);
                    }

                    if (dock != null)
                    {
                        // NOTE: the retry ledger is NOT cleared here. The cleat branch of the stretch
                        // guard below RE-USES it (a resolved cleat whose span is still bad = the towed
                        // hull has not streamed yet), and clearing on resolve would reset its attempt
                        // count every retry - an infinite retry loop. Cleared once the moor is settled,
                        // below the guard.
                        // Release any prior dock SpringJoint before re-mooring. Vanilla MoorTo
                        // never clears an existing spring (only Unmoor does), so a re-moor that resolves a
                        // DIFFERENT dock instance than the one currently held would leave a leaked second spring
                        // pulling the hull toward two anchors at once (the "phantom rope into the earth" class).
                        if (rope.IsMoored()) rope.Unmoor();
                        rope.MoorTo(dock);
                        rope.currentRopeLengthSquared = packet.LengthSquared;

                        // Moored-sink residual: vanilla MoorTo (decomp PickupableBoatMooringRope.cs:247-262)
                        // sets SpringJoint.maxDistance from a LOCALLY-derived rope length (GetCurrentDistanceSquared
                        // off THIS client's boat geometry + floating-origin offset). We overwrite the
                        // currentRopeLengthSquared FIELD with the authoritative value above, but the physics
                        // constraint (SpringJoint.maxDistance) still carries MoorTo's local guess - so the spring
                        // holds the wrong slack and drags the hull bow-first until the next length sync. Restore the
                        // authoritative maxDistance now, identical to OnRemoteMooringRopeLengthChanged (~line 1227).
                        var springJoint = MooredToSpringRef(rope);
                        if (springJoint != null)
                            springJoint.maxDistance = Mathf.Sqrt(packet.LengthSquared);

                        VerboseLogger.ControlApply($"Moored rope {packet.RopeIndex} to {dock.name}, restored authoritative maxDistance={(springJoint != null ? Mathf.Sqrt(packet.LengthSquared) : 0f):F2}");

                        // VISUAL-STRETCH GUARD (issue #5): the X/Z match can resolve a dock that, on THIS client,
                        // is not co-located with the boat (the dock cleat inherits the horizon-sunk island Y while
                        // the boat floats at sea level; cross-region/floating-origin can also separate them). MoorTo
                        // then renders a LineRenderer raking across the ocean even though the moor is "logically"
                        // correct. The rope's max length is ~30m (cleat->hull anchor); this measures cleat->boat
                        // origin (+ up to a hull), so 50m clears any legit near-dock moor while catching a divergent
                        // frame -> unmoor + stow rather than draw a kilometre-long dockline.
                        var stretchRb = rope.GetBoatRigidbody();
                        if (stretchRb != null && Vector3.Distance(rope.transform.position, stretchRb.transform.position) > 50f)
                        {
                            // Undo the bad spring first either way - it must never survive this frame.
                            rope.Unmoor();

                            if (packet.TargetKind == MooringTargetKind.BoatCleat)
                            {
                                // (v0.2.32 review) A tow target is a MOVING boat: on a guest, the towed
                                // hull's always-stream pin was created on the host at the same instant as
                                // this moor, so our local copy may still be drifted when the first apply
                                // lands and the span check fails spuriously. Retry like a dock miss - one
                                // second later the pinned boat's transform has snapped and the moor holds.
                                // Docks keep the immediate stow (they are static; a failed span there is real).
                                _moorRetryCounts.TryGetValue(retryKey, out int spanAttempts);
                                spanAttempts++;
                                if (spanAttempts < MoorResolveMaxAttempts)
                                {
                                    _moorRetryCounts[retryKey] = spanAttempts;
                                    Plugin.Log.LogWarning($"Cleat moor span implausible for rope {packet.RopeIndex} ({packet.BoatName}) " +
                                                          $"to cleat '{packet.CleatPath}' on '{packet.TowBoatName}' (towed hull likely not streamed to its host position yet); " +
                                                          $"unmoored + retry {spanAttempts}/{MoorResolveMaxAttempts - 1} in {MoorResolveRetryDelay:F0}s");
                                    StartCoroutine(RetryMoorAfterDelay(packet, sender, retryKey,
                                        _moorPacketGen.TryGetValue(retryKey, out int spanGen) ? spanGen : 0));
                                    return;
                                }
                                // Retries exhausted: the span is real. Fall through to the conservative stow.
                                _moorRetryCounts.Remove(retryKey);
                            }

                            BoatStateApplicator.StowRopeIfDisplaced(rope, $"Rope {packet.RopeIndex} ({packet.BoatName})");
                            VerboseLogger.ControlApply($"Rope {packet.RopeIndex}: post-moor span implausible; stowed instead of a stretched dockline");
                            // Host + originator must not diverge: tell the crew the moor was abandoned.
                            BroadcastCorrectiveUnmoor(packet, "post-moor span implausible");
                        }

                        // Target resolved and the span was either fine or terminally abandoned: this
                        // packet is done with the retry ledger. (A cleat span retry returned above and
                        // deliberately keeps its count.)
                        _moorRetryCounts.Remove(retryKey);

                        // (v0.2.32, P4) The attach/detach patches are suppressed while applying remote
                        // state, so the host must maintain the tow pin here for guest-initiated changes.
                        BoatUtility.UpdateTowStreamPin(boat);
                    }
                    else
                    {
                        // (v0.2.32) A cleat can be momentarily unresolvable too (island/boat streaming,
                        // Towable Boats' deferred cleat instantiation), so it shares the retry ledger -
                        // only the miss DESCRIPTION differs (DockPosition is zero for cleats).
                        string missDesc = packet.TargetKind == MooringTargetKind.BoatCleat
                            ? $"no cleat '{packet.CleatPath}' on tow boat '{packet.TowBoatName}'"
                            : $"no dock near {packet.DockPosition} (5m XZ radius, nearest candidate " +
                              $"{(float.IsPositiveInfinity(nearestMissDist) ? "none" : nearestMissDist.ToString("F1") + "m")})";

                        _moorRetryCounts.TryGetValue(retryKey, out int attempts);
                        attempts++;
                        if (attempts < MoorResolveMaxAttempts)
                        {
                            // Dock resolve MISS - often transient (island streaming, floating-origin offset
                            // drift). Leave the rope untouched and retry the same packet shortly instead of
                            // stowing on the first miss (which deleted a real moor on this side only - the
                            // "rope vanished for the host but not the client" report).
                            _moorRetryCounts[retryKey] = attempts;
                            Plugin.Log.LogWarning($"Mooring resolve miss for rope {packet.RopeIndex} ({packet.BoatName}): {missDesc}; " +
                                                  $"retry {attempts}/{MoorResolveMaxAttempts - 1} in {MoorResolveRetryDelay:F0}s");
                            StartCoroutine(RetryMoorAfterDelay(packet, sender, retryKey,
                                _moorPacketGen.TryGetValue(retryKey, out int gen) ? gen : 0));
                            return;
                        }
                        // Still unresolved after retries. Do NOT leave the rope diverged (sender: moored,
                        // us: half-applied) - that is the "rope stretched kilometers to a horizon-sunk
                        // island" class. Stow it deterministically; guarded so an already-stowed rope is
                        // untouched, and (host) broadcast the abandonment so the originator agrees.
                        _moorRetryCounts.Remove(retryKey);
                        Plugin.Log.LogWarning($"Mooring FAILED after {MoorResolveMaxAttempts} attempts: {missDesc}; " +
                                              $"stowing rope {packet.RopeIndex} instead of leaving it diverged");
                        BoatStateApplicator.StowRopeIfDisplaced(rope, $"Rope {packet.RopeIndex} ({packet.BoatName})");
                        if (!rope.IsMoored())
                            BroadcastCorrectiveUnmoor(packet, "dock unresolved after retries");
                    }
                }
                else
                {
                    // Call Unmoor() - this disconnects the SpringJoint (but doesn't destroy it!)
                    // and re-parents rope to initialParent. It NO-OPS entirely (no re-parent) on an
                    // already-unmoored rope - and a rope in the detached was-moored save-restore state
                    // has parent==null, so a bare localPosition write here would be a WORLD-space write
                    // hurling the rope to hanger-local coords near the world origin ("ropes gone from
                    // both the poles and the storage", Robin 0711).
                    rope.Unmoor();

                    // Parent-safe hanger restore: re-parents a detached rope first, resets local pos/rot,
                    // and clears the was-moored save flag so the detached state can't re-persist into the
                    // next save (vanilla Unmoor only clears it when a spring actually existed).
                    BoatStateApplicator.StowRopeIfDisplaced(rope, $"Rope {packet.RopeIndex} ({packet.BoatName})");

                    // Force RopeEffect to update
                    var ropeEffect = Traverse.Create(rope).Field("rope").GetValue<RopeEffect>();
                    if (ropeEffect != null)
                    {
                        ropeEffect.enabled = false;
                        ropeEffect.enabled = true;
                    }

                    VerboseLogger.ControlApply($"Unmoored rope {packet.RopeIndex}, boat={packet.BoatName}");

                    // (v0.2.32, P4) The attach/detach patches are suppressed while applying remote
                    // state, so the host must maintain the tow pin here for guest-initiated changes.
                    BoatUtility.UpdateTowStreamPin(boat);
                }
            }
            finally
            {
                IsApplyingRemoteState = false;
            }
        }

        /// <summary>(v0.2.32) Resolve a tow-cleat mooring target from its wire reference.</summary>
        private GPButtonDockMooring ResolveCleat(string towBoatName, string cleatPath)
        {
            var towBoat = BoatUtility.FindBoatByName(towBoatName);
            if (towBoat == null) return null;
            var cleatT = SyncPathUtil.FindByRelativePath(towBoat.transform, cleatPath);
            // TowingCleat IS-A GPButtonDockMooring, so the vanilla component fetch covers both.
            return cleatT != null ? cleatT.GetComponent<GPButtonDockMooring>() : null;
        }

        // Matches on X/Z ONLY: island (and thus dock) Y is VIEW-DEPENDENT - vanilla
        // IslandHorizon.ApplyNewHorizon rewrites far island roots' Y every LateUpdate for earth-curvature
        // rendering (~-10km at 100km range, camera-height dependent even locally), so a 3D match misses
        // any far dock. X/Z are stable and unique enough within a 5m radius.
        private GPButtonDockMooring FindClosestDockMooring(Vector3 realPosition, out float nearestMissDist)
        {
            // Convert from real (offset-independent) to local coordinates
            // Sender subtracted their offset, we add ours to get correct local position
            var offset = FloatingOriginManager.instance?.outCurrentOffset ?? Vector3.zero;
            var localPosition = realPosition + offset;

            var docks = FindObjectsOfType<GPButtonDockMooring>();
            GPButtonDockMooring closest = null;
            float closestDist = 5f; // Max 5m search radius
            nearestMissDist = float.PositiveInfinity;

            foreach (var dock in docks)
            {
                var delta = dock.transform.position - localPosition;
                delta.y = 0f; // horizon-sunk island Y is meaningless; match in the horizontal plane only
                var dist = delta.magnitude;
                if (dist < nearestMissDist) nearestMissDist = dist;
                if (dist < closestDist)
                {
                    closestDist = dist;
                    closest = dock;
                }
            }

            return closest;
        }

        // === Mooring Rope Length Sync ===

        // Resilience: MooringRopeLength alone would be UNRELIABLE-only with no reliable terminal and no periodic
        // resync, so a dropped/reordered FINAL scroll packet permanently stranded the receiver's
        // SpringJoint.maxDistance (the moored boat sat at the wrong slack/tension for the rest of the session,
        // since the only full mooring snapshot is the join/recovery BoatWorldState). Mirror the RopeState/HelmState
        // terminal pattern: per-scroll updates stay unreliable, but a short debounce after the last change sends
        // ONE reliable terminal carrying the settled length (relayed reliably too), so a lost final self-heals.
        private class MooringTerminal { public string Boat; public int Rope; public float LenSq; public float LastChange; public bool FinalSent; }
        private readonly Dictionary<string, MooringTerminal> _mooringTerminals = new Dictionary<string, MooringTerminal>();
        private const float MooringTerminalDebounce = 0.3f; // send the reliable settled length 0.3s after the last scroll tick

        public void OnLocalMooringRopeLengthChanged(string boatName, int ropeIndex, float lengthSquared, bool isFinal = false)
        {
            if (!Plugin.IsMultiplayer) return;

            VerboseLogger.ControlSend($"MooringRopeLength, boat={boatName}, rope={ropeIndex}, lenSq={lengthSquared:F2}, final={isFinal}");

            var packet = new MooringRopeLengthPacket
            {
                BoatName = boatName,
                RopeIndex = ropeIndex,
                LengthSquared = lengthSquared,
                IsFinal = isFinal
            };

            if (isFinal)
            {
                // Debounced settled value: reliable so a dropped final can't strand the spring distance.
                Plugin.NetworkManager.SendToAllReliable(PacketType.MooringRopeLength, w =>
                    PacketSerializer.WriteMooringRopeLength(w, packet));
            }
            else
            {
                // Continuous mid-scroll updates: unreliable (high frequency). Track for a debounced terminal.
                Plugin.NetworkManager.SendToAllUnreliable(PacketType.MooringRopeLength, w =>
                    PacketSerializer.WriteMooringRopeLength(w, packet));

                string key = boatName + "|" + ropeIndex;
                if (!_mooringTerminals.TryGetValue(key, out var t))
                {
                    t = new MooringTerminal { Boat = boatName, Rope = ropeIndex };
                    _mooringTerminals[key] = t;
                }
                t.LenSq = lengthSquared;
                t.LastChange = Time.time;
                t.FinalSent = false;
            }
        }

        /// <summary>
        /// Once a mooring rope has been idle for MooringTerminalDebounce since its last unreliable scroll
        /// update, emit ONE reliable terminal with the settled length so a lost/reordered final converges. Driven
        /// from the 10Hz sync tick. The isFinal=true send does NOT re-enter the tracking dict (so this foreach is
        /// iteration-safe, and FinalSent latches PER SETTLE - exactly one terminal per scroll-burst). FinalSent is
        /// reset only when the SAME rope is scrolled AGAIN (a new adjustment, which correctly earns its own
        /// terminal). The dict is bounded by the distinct (boat,rope) pairs ever moored this session - entries are
        /// reused by key, never duplicated - and is cleared in Reset(), so it does not grow unbounded.
        /// </summary>
        private void SweepMooringTerminals()
        {
            if (_mooringTerminals.Count == 0) return;
            float now = Time.time;
            foreach (var t in _mooringTerminals.Values)
            {
                if (t.FinalSent) continue;
                if (now - t.LastChange < MooringTerminalDebounce) continue;
                t.FinalSent = true;
                OnLocalMooringRopeLengthChanged(t.Boat, t.Rope, t.LenSq, isFinal: true);
            }
        }

        public void OnRemoteMooringRopeLengthChanged(MooringRopeLengthPacket packet, SteamId sender = default)
        {
            VerboseLogger.ControlRecv($"MooringRopeLength, boat={packet.BoatName}, rope={packet.RopeIndex}, lenSq={packet.LengthSquared:F2}");

            // STAR host-relay: a guest's mooring-length change is a request; the host applies + relays to the
            // other guests. Relay reliable on the debounced terminal (packet.IsFinal) so a dropped/reordered
            // final can't strand a peer guest's spring distance either; mid-scroll updates stay unreliable. At
            // N=1 this is a no-op.
            if (Plugin.IsHost)
            {
                Plugin.NetworkManager.SendToAllExcept(sender, PacketType.MooringRopeLength,
                    w => PacketSerializer.WriteMooringRopeLength(w, packet), reliable: packet.IsFinal);
            }

            var boats = BoatUtility.FindAllBoats();
            if (!boats.TryGetValue(packet.BoatName, out var boat)) return;

            var mooringRopes = boat.GetComponent<BoatMooringRopes>();
            if (mooringRopes == null || mooringRopes.ropes == null) return;
            if (packet.RopeIndex < 0 || packet.RopeIndex >= mooringRopes.ropes.Length) return;

            var rope = mooringRopes.ropes[packet.RopeIndex];

            // Update the rope length
            rope.currentRopeLengthSquared = packet.LengthSquared;

            // Update physics constraint (SpringJoint.maxDistance)
            // This is what actually pulls the boat on the host
            var springJoint = MooredToSpringRef(rope);
            if (springJoint != null)
            {
                springJoint.maxDistance = Mathf.Sqrt(packet.LengthSquared);
            }

            VerboseLogger.ControlApply($"MooringRopeLength set, boat={packet.BoatName}, rope={packet.RopeIndex}, len={Mathf.Sqrt(packet.LengthSquared):F2}");
        }

        // Force sync moved to PushSyncManager (event-based, not polling)

        public void Reset()
        {
            _lastSyncTime = 0f;
            _controlStates.Clear();               // every per-boat rope/helm state dies with the session
            _pollNamesScratch.Clear();
            _pollBoatsScratch.Clear();
            _controlPruneScratch.Clear();
            _pollErrorLogged.Clear();
            _pollTick = 0;
            _reconcileCursor = 0;
            _peerBoatNames.Clear();               // boarding-assert peer map is per-session
            _occupiedBoatsScratch.Clear();
            _ropeTrustSuspended.Clear();          // trust suspensions must not bleed into the next session
            _loggedBoatRopes.Clear();
            _helmLeaseHolder.Clear();
            _helmLeaseLastInput.Clear();
            _helmDeniedUntil.Clear();
            _lastHelmDeniedSent.Clear();
            // (v0.3.0) The guest steering patch holds a wheel reference and a "did we actually steer this
            // grab" latch across frames. Both are per-grab state and neither should outlive the session or a
            // world reload.
            Patches.ControlPatches.SteeringWheelGuestPatch.ResetHelmEdgeState();
            _recentNetworkMooringChanges.Clear(); // per-session map; stale rope-instanceId keys must not
                                                  // bleed into the next session.
            _mooringTerminals.Clear();            // drop any pending debounced mooring terminals
            _pendingRopes.Clear();                // join-race deferred rope seeds die with the session
            _applyingPerPacket = false;           // clear both apply guards so neither sticks across sessions
            _applyingJoinState = false;
        }
    }
}
