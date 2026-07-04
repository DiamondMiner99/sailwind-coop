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

            if (Time.time - _lastSyncTime >= SyncInterval)
            {
                _lastSyncTime = Time.time;
                // Join-race: retry rope seeds that arrived before the guest boat's sail controllers existed.
                // MUST run BEFORE PollBoatControls: on an aboard-join the discovery poll would otherwise
                // broadcast DEFAULT rope lengths in the tick the controllers appear (clobbering the host's
                // trim via last-writer-wins + the 0.3s reliable settle terminal) before the seed applies.
                RetryPendingRopes();
                // Poll all controls on current boat (no patching needed)
                PollBoatControls();
                // Emit the debounced reliable terminal for any mooring rope that has settled (any client
                // that moved one), so a dropped final unreliable scroll packet self-heals.
                SweepMooringTerminals();
                // Host sweeps idle off-boat helm leases and sends a reliable terminal HelmState so
                // passengers converge to the final wheel angle even if the last unreliable relay was dropped.
                if (Plugin.IsHost) SweepStaleHelmLeases();
            }

            Plugin.Profiler?.EndMeasureControlSync();
        }

        // Cache previous values to only send on change
        // Use array index for cache key (unique within boat)
        private float[] _lastRopeLengths = new float[0];
        private float _lastHelmInput;

        // Rope settle terminal: parallel arrays sized like _lastRopeLengths track, per rope index, the time
        // of the last LOCAL change and whether a reliable terminal was already sent for the current settle.
        // PollBoatControls streams rope deltas UNRELIABLY (OnLocalRopeChanged isFinal=false) and stops when the
        // rope settles; if that LAST unreliable packet is dropped the off-host rope is stranded at the wrong
        // length with no resync. Mirror the helm SweepStaleHelmLeases pattern: a debounce after the last
        // change sends ONE reliable terminal so a dropped final self-heals. Echo-safe: a remotely-applied rope
        // updates _lastRopeLengths (OnRemoteRopeChanged), so the change-detector never trips for it - the terminal
        // only ever fires for ropes THIS client actually moved.
        private float[] _ropeLastChangeTime = new float[0];
        private bool[] _ropeFinalSent = new bool[0];
        // Length as of the last OPERATED send: the terminal must ship this captured value, not the live
        // currentLength at sweep time - by then unoperated drift (stick drift, reef forcing) may have moved
        // the rope again, and a reliable IsFinal would export that contamination to the whole crew.
        private float[] _ropeLastSentLength = new float[0];
        private const float RopeTerminalDebounce = 0.3f;

        // Cache anchor rope index per boat for debug logging
        private Dictionary<string, int> _anchorRopeIndices = new Dictionary<string, int>();
        private HashSet<string> _loggedBoatRopes = new HashSet<string>();

        // OPERATED-ROPE GATE (field report: sails "unfold as if holding W" on the OTHER machine, stopping
        // when the peer disconnects and resuming on rejoin): ropes are last-writer-wins with no lease, so
        // ANY guest-local rope movement (controller stick drift feeding a grabbed winch, load-time reef
        // forcing, join-race stale defaults on the first discovery tick) used to be broadcast at 10Hz plus
        // a reliable settle terminal and imposed on the whole crew. Only broadcast a rope change when THIS
        // machine's player is actually operating that rope: the GPButtonRopeWinch whose `rope` field drives
        // it is grabbed by the local pointer, or (anchor rope) the local player is carrying the anchor item,
        // which vanilla Anchor.ExtraFixedUpdate pays rope out for while held. The winch map and anchor are
        // cached per boat and rebuilt on the same trigger as the _lastRopeLengths resize (boat change).
        private readonly Dictionary<RopeController, GPButtonRopeWinch> _ropeWinchMap =
            new Dictionary<RopeController, GPButtonRopeWinch>();
        private Anchor _ropeCacheAnchor;
        private string _ropeCacheBoatName;

        private void PollBoatControls()
        {
            // Poll only current boat - original working approach
            var boat = BoatUtility.GetCurrentBoat();
            if (boat == null) return;

            var boatName = boat.gameObject.name;
            var ropes = BoatUtility.GetRopeControllers(boat);

            // One-time logging of rope discovery for this boat
            if (!_loggedBoatRopes.Contains(boatName))
            {
                _loggedBoatRopes.Add(boatName);
                LogRopeDiscovery(boatName, boat, ropes);
            }

            // Resize cache if needed. Also triggers on a boat CHANGE with the same rope count - the
            // rope->winch map must never alias another boat's winches, and the length cache is per-boat.
            if (_lastRopeLengths.Length != ropes.Length || _ropeCacheBoatName != boatName)
            {
                _lastRopeLengths = new float[ropes.Length];
                _ropeLastChangeTime = new float[ropes.Length];
                _ropeFinalSent = new bool[ropes.Length];
                _ropeLastSentLength = new float[ropes.Length];
                for (int j = 0; j < ropes.Length; j++)
                {
                    _lastRopeLengths[j] = -1f;
                    _ropeFinalSent[j] = true; // no pending terminal for a freshly-(re)discovered rope
                }
                BuildRopeWinchMap(boat, boatName);
            }

            for (int i = 0; i < ropes.Length; i++)
            {
                var rope = ropes[i];
                if (rope == null) continue;

                string ropeName = rope.gameObject.name;

                // Only send if changed
                if (Mathf.Abs(rope.currentLength - _lastRopeLengths[i]) > 0.001f)
                {
                    // ALWAYS stamp the change-detection cache, even for changes we won't send: a later
                    // grab must only diff movement made WHILE grabbed, never a stale accumulated delta.
                    _lastRopeLengths[i] = rope.currentLength;

                    // OPERATED-ROPE GATE: only broadcast changes the local player is actually making
                    // (winch grabbed / anchor carried). Unoperated local movement (stick drift, load-time
                    // reef forcing, join-race defaults) must never be imposed on the crew (see field docs).
                    if (!IsLocalOperatingRope(rope))
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
                        var anchor = BoatUtility.GetAnchor(boat);
                        var anchorRb = anchor?.GetComponent<Rigidbody>();
                        VerboseLogger.ControlLocal($"ANCHOR rope changed, boat={boatName}, idx={i}, name={ropeName}, len={rope.currentLength:F3}, anchorKinematic={anchorRb?.isKinematic}");
                    }

                    OnLocalRopeChanged(boatName, i, ropeName, rope.currentLength, false,
                        $"grabbed=true, run={GameInput.GetKey(InputName.Run)}");
                    _ropeLastChangeTime[i] = Time.time;  // arm the settle-terminal debounce
                    _ropeFinalSent[i] = false;
                    _ropeLastSentLength[i] = rope.currentLength;
                }
            }

            // Rope settle-terminal sweep. Once a rope has been idle for RopeTerminalDebounce since its
            // last LOCAL change, send ONE reliable terminal so a dropped final unreliable delta self-heals.
            // Only fires for ropes this client SENT while operating them: the debounce is armed exclusively
            // by the operated-send branch above (remote applies stamp _lastRopeLengths and suppressed local
            // changes skip the arm), so an unoperated rope never earns a terminal either.
            for (int i = 0; i < ropes.Length; i++)
            {
                if (_ropeFinalSent[i]) continue;
                if (Time.time - _ropeLastChangeTime[i] < RopeTerminalDebounce) continue;
                var settledRope = ropes[i];
                if (settledRope == null) { _ropeFinalSent[i] = true; continue; }
                _ropeFinalSent[i] = true;
                // Ship the length captured at the last operated send, NOT the live value (see field docs).
                OnLocalRopeChanged(boatName, i, settledRope.gameObject.name, _ropeLastSentLength[i], true,
                    $"grabbed={IsLocalOperatingRope(settledRope)}, run={GameInput.GetKey(InputName.Run)}");
            }

            // Poll steering wheel
            var wheel = boat.GetComponentInChildren<GPButtonSteeringWheel>();
            if (wheel != null)
            {
                // Poll helm input
                if (Mathf.Abs(wheel.currentInput - _lastHelmInput) > 0.001f)
                {
                    _lastHelmInput = wheel.currentInput;
                    OnLocalHelmChanged(boatName, wheel.currentInput, false);
                }

                // Poll helm lock state (host only - broadcast when lock changes via game UI)
                if (Plugin.IsHost)
                {
                    bool currentLocked = LockedRef(wheel);
                    if (currentLocked != _lastHelmLocked)
                    {
                        _lastHelmLocked = currentLocked;
                        BroadcastHelmLock(boatName, currentLocked);
                    }
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
                    _anchorRopeIndices[boatName] = i;
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

        // === Operated-rope detection (see _ropeWinchMap field docs) ===

        /// <summary>
        /// Rebuild the rope->winch map and cached Anchor for the current boat. Called on the same trigger
        /// as the _lastRopeLengths resize (rope count OR boat change), so a stale map can never alias
        /// another boat's winches. GPButtonRopeWinch.rope is the public vanilla field pointing at the
        /// RopeController the winch drives.
        /// </summary>
        private void BuildRopeWinchMap(SaveableObject boat, string boatName)
        {
            _ropeWinchMap.Clear();
            _ropeCacheBoatName = boatName;
            var winches = boat.GetComponentsInChildren<GPButtonRopeWinch>(true);
            foreach (var winch in winches)
            {
                if (winch != null && winch.rope != null && !_ropeWinchMap.ContainsKey(winch.rope))
                    _ropeWinchMap[winch.rope] = winch;
            }
            // BoatUtility.GetAnchor, NOT GetComponentInChildren: vanilla Anchor.Awake reparents the
            // anchor out of the boat hierarchy, so a child search is always null after Awake.
            _ropeCacheAnchor = BoatUtility.GetAnchor(boat);
            VerboseLogger.ControlLocal($"Rope winch map rebuilt for {boatName}: {_ropeWinchMap.Count} winches, anchor={(_ropeCacheAnchor != null)}");
        }

        /// <summary>
        /// True if THIS machine's local player is currently operating <paramref name="rope"/>: the winch
        /// driving it is grabbed by the local pointer (same read-only vanilla grab test as
        /// IsHostSteeringWheel - stickyClickedBy/isClicked/rotHandle are only ever set by the LOCAL
        /// GoPointer), or the rope is the anchor rope and the local player is carrying the anchor item
        /// (vanilla Anchor.ExtraFixedUpdate pays rope out while held; PickupableItem.held is likewise
        /// local-pointer-only). A rope with no winch (map miss) is never operated - unoperated ropes must
        /// never broadcast.
        /// </summary>
        private bool IsLocalOperatingRope(RopeController rope)
        {
            if (_ropeWinchMap.TryGetValue(rope, out var winch) && winch != null)
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
                if (_ropeCacheAnchor == null)
                {
                    var boat = BoatUtility.GetCurrentBoat();
                    if (boat != null && boat.gameObject.name == _ropeCacheBoatName)
                        _ropeCacheAnchor = BoatUtility.GetAnchor(boat);
                }
                if (_ropeCacheAnchor != null && _ropeCacheAnchor.held != null)
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
            foreach (var kvp in _pendingRopes)
            {
                var p = kvp.Value;
                if (now < p.NextTry) continue;
                p.NextTry = now + PendingRopeRetryInterval;
                if (TryApplyRopePacket(p.Packet, logMiss: false))
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

            float prevLength = rope.currentLength;
            rope.currentLength = packet.Length;
            rope.changed = true;

            // Update local cache to prevent echo feedback - ONLY when this packet is for the host's CURRENT
            // boat. _lastRopeLengths is a single array keyed to GetCurrentBoat() by PollBoatControls;
            // writing it by raw index for a DIFFERENT boat (host not standing on the steered boat, 3+ players)
            // cross-aliases the poll's change detection, spuriously re-sending or SUPPRESSING the host's own
            // boat's rope changes. Mirror the guard the helm path uses (see OnRemoteHelmInput).
            if (appliedIndex >= 0 && appliedIndex < _lastRopeLengths.Length &&
                BoatUtility.GetCurrentBoat()?.gameObject.name == packet.BoatName)
            {
                _lastRopeLengths[appliedIndex] = packet.Length;
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
        /// Stale reef on join: rope/reef state is sent on-change only, so a client that wasn't watching the
        /// shared boat when a sail was reefed/angled never receives the current rope lengths - it joins with sails
        /// in the default position. Mirror ResendHelmForCurrentBoat: on a guest join the host re-sends EVERY current
        /// rope length for the shared boat as a reliable terminal RopeState, so the joiner converges to the real
        /// sail trim. Reuses the EXISTING RopeState packet (one per rope), reliable (IsFinal=true) so a dropped seed
        /// can't strand a rope. Host-only; indices come from the stable-sorted GetRopeControllers, so they match the
        /// guest's array. Idempotent on already-settled crew - they simply re-apply the same lengths.
        /// </summary>
        public void ResendRopeForCurrentBoat()
        {
            if (!Plugin.IsHost) return;
            var boat = BoatUtility.GetCurrentBoat();
            if (boat == null) return;

            var boatName = boat.gameObject.name;
            var ropes = BoatUtility.GetRopeControllers(boat);
            for (int i = 0; i < ropes.Length; i++)
            {
                var rope = ropes[i];
                if (rope == null) continue;
                OnLocalRopeChanged(boatName, i, rope.gameObject.name, rope.currentLength, true);
            }
            VerboseLogger.ControlSend($"ResendRopeForCurrentBoat: re-seeded {ropes.Length} rope lengths for {boatName}");
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
        /// Guest sends helm input delta to host
        /// </summary>
        public void OnLocalHelmInput(string boatName, float inputDelta)
        {
            if (!Plugin.IsMultiplayer) return;
            // Only guest sends input to host
            if (Plugin.IsHost) return;

            VerboseLogger.ControlSend($"HelmInput, boat={boatName}, delta={inputDelta:F3}");

            var packet = new HelmInputPacket
            {
                BoatName = boatName,
                InputDelta = inputDelta
            };

            // Send unreliable for low latency (high frequency input)
            Plugin.NetworkManager.SendToAllUnreliable(PacketType.HelmInput, w =>
                PacketSerializer.WriteHelmInput(w, packet));
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

            VerboseLogger.ControlRecv($"HelmInput, boat={packet.BoatName}, delta={packet.InputDelta:F3}, from={sender}");

            // Apply the input delta
            wheel.currentInput += packet.InputDelta;

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
            // OTHER guests even when the host is NOT standing on the steered boat (PollBoatControls only
            // sends HelmState for the boat the host is on). UNRELIABLE - helm is high-frequency. Also stamp
            // _lastHelmInput so the host's own PollBoatControls path doesn't re-send the same state again.
            Plugin.NetworkManager.SendToAllExcept(sender, PacketType.HelmState, w =>
                PacketSerializer.WriteHelmState(w, new HelmStatePacket
                {
                    BoatName = packet.BoatName,
                    Input = wheel.currentInput,
                    IsFinal = false
                }), reliable: false);
            // Stamp the host's current-boat helm baseline ONLY when the steered boat IS the host's current
            // boat. _lastHelmInput is a single scalar keyed to PollBoatControls' GetCurrentBoat(); stamping
            // it with a DIFFERENT boat's value (host off the steered boat) would cross-boat-alias the poll's
            // change detection. On the shared boat this dedups the redundant poll send; off-boat it's skipped.
            if (BoatUtility.GetCurrentBoat()?.gameObject.name == packet.BoatName)
                _lastHelmInput = wheel.currentInput;

            VerboseLogger.ControlApply($"HelmInput applied, boat={packet.BoatName}, newInput={wheel.currentInput:F3}");
        }

        // === Helm Lock Sync ===

        // Access private locked field
        private static readonly AccessTools.FieldRef<GPButtonSteeringWheel, bool> LockedRef =
            AccessTools.FieldRefAccess<GPButtonSteeringWheel, bool>("locked");

        // Track last lock state for polling
        private bool _lastHelmLocked = false;

        /// <summary>
        /// Guest sends helm lock toggle request to host
        /// </summary>
        public void OnLocalHelmLockToggle(string boatName)
        {
            if (!Plugin.IsMultiplayer) return;

            // Guest sends toggle request to host
            if (!Plugin.IsHost)
            {
                VerboseLogger.ControlSend($"HelmLock toggle request, boat={boatName}");

                // Get current local state and invert for optimistic UI
                var boats = BoatUtility.FindAllBoats();
                if (boats.TryGetValue(boatName, out var boat))
                {
                    var wheel = boat.GetComponentInChildren<GPButtonSteeringWheel>();
                    if (wheel != null)
                    {
                        bool currentLocked = LockedRef(wheel);
                        bool newLocked = !currentLocked;

                        // Optimistic local update
                        LockedRef(wheel) = newLocked;

                        // Play sound locally for immediate feedback
                        Juicebox.juice.PlaySoundAt("lock unlock", wheel.transform.position, 0f, 0.66f, newLocked ? 0.88f : 1f);

                        VerboseLogger.ControlLocal($"Guest helm lock optimistic: {currentLocked} -> {newLocked}");
                    }
                }

                // Send packet to host (host will broadcast authoritative state)
                var packet = new HelmLockPacket
                {
                    BoatName = boatName,
                    IsLocked = true  // Value doesn't matter - host will toggle
                };

                Plugin.NetworkManager.SendToAllReliable(PacketType.HelmLock, w =>
                    PacketSerializer.WriteHelmLock(w, packet));
            }
            else
            {
                // Host: toggle locally and broadcast
                var boats = BoatUtility.FindAllBoats();
                if (boats.TryGetValue(boatName, out var boat))
                {
                    var wheel = boat.GetComponentInChildren<GPButtonSteeringWheel>();
                    if (wheel != null)
                    {
                        bool currentLocked = LockedRef(wheel);
                        bool newLocked = !currentLocked;
                        LockedRef(wheel) = newLocked;
                        _lastHelmLocked = newLocked;

                        // Play sound
                        Juicebox.juice.PlaySoundAt("lock unlock", wheel.transform.position, 0f, 0.66f, newLocked ? 0.88f : 1f);

                        VerboseLogger.ControlLocal($"Host helm lock toggled: {currentLocked} -> {newLocked}");

                        // Broadcast to guest
                        BroadcastHelmLock(boatName, newLocked);
                    }
                }
            }
        }

        /// <summary>
        /// Host receives helm lock toggle from guest - toggle and broadcast
        /// </summary>
        public void OnRemoteHelmLockToggle(HelmLockPacket packet)
        {
            if (!Plugin.IsHost) return;

            VerboseLogger.ControlRecv($"HelmLock toggle request, boat={packet.BoatName}");

            var boats = BoatUtility.FindAllBoats();
            if (!boats.TryGetValue(packet.BoatName, out var boat)) return;

            var wheel = boat.GetComponentInChildren<GPButtonSteeringWheel>();
            if (wheel == null) return;

            // Toggle lock state
            bool currentLocked = LockedRef(wheel);
            bool newLocked = !currentLocked;
            LockedRef(wheel) = newLocked;
            _lastHelmLocked = newLocked;

            // Play sound on host
            Juicebox.juice.PlaySoundAt("lock unlock", wheel.transform.position, 0f, 0.66f, newLocked ? 0.88f : 1f);

            VerboseLogger.ControlApply($"Host helm lock set from guest request: {newLocked}");

            // Broadcast authoritative state to all (including back to guest for confirmation)
            BroadcastHelmLock(packet.BoatName, newLocked);
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
        {
            if (!Plugin.IsMultiplayer) return;

            VerboseLogger.ControlSend($"MooringState, boat={boatName}, rope={ropeIndex}, moored={isMoored}, dockPos={dockPosition}");

            var packet = new MooringStatePacket
            {
                BoatName = boatName,
                RopeIndex = ropeIndex,
                IsMoored = isMoored,
                DockPosition = dockPosition,
                LengthSquared = lengthSquared
            };

            Plugin.NetworkManager.SendToAllReliable(PacketType.MooringState, w =>
                PacketSerializer.WriteMooringState(w, packet));
        }

        public void OnRemoteMooringChanged(MooringStatePacket packet, SteamId sender = default)
        {
            VerboseLogger.ControlRecv($"MooringState, boat={packet.BoatName}, rope={packet.RopeIndex}, moored={packet.IsMoored}");

            // STAR host-relay: a guest's mooring change is a request; the host applies + relays the
            // authoritative result to the other guests. At N=1 SendToAllExcept(sender) is a no-op.
            if (Plugin.IsHost)
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
                    rope.ResetRopePos();
                    VerboseLogger.ControlApply($"Force-released locally-held rope {packet.RopeIndex} (remote mooring state wins)");
                    Plugin.Notify("Mooring rope taken by a crewmate");
                }

                if (packet.IsMoored)
                {
                    var dock = FindClosestDockMooring(packet.DockPosition, out float nearestMissDist);
                    if (dock != null)
                    {
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
                    }
                    else
                    {
                        // Dock resolve MISS. Do NOT leave the rope diverged (sender: moored, us: half-applied) -
                        // that is the "rope stretched kilometers to a horizon-sunk island" class. Stow it
                        // deterministically; guarded so an already-stowed rope is untouched.
                        Plugin.Log.LogWarning($"Mooring FAILED: no dock near {packet.DockPosition} (5m XZ radius, " +
                                              $"nearest candidate {(float.IsPositiveInfinity(nearestMissDist) ? "none" : nearestMissDist.ToString("F1") + "m")}); stowing rope {packet.RopeIndex} instead of leaving it diverged");
                        BoatStateApplicator.StowRopeIfDisplaced(rope, $"Rope {packet.RopeIndex} ({packet.BoatName})");
                    }
                }
                else
                {
                    var ropeTraverse = Traverse.Create(rope);

                    // Get initial transform values for returning rope to boat
                    var initialParent = ropeTraverse.Field("initialParent").GetValue<Transform>();
                    var initialPos = ropeTraverse.Field("initialPos").GetValue<Vector3>();
                    var initialRot = ropeTraverse.Field("initialRot").GetValue<Quaternion>();

                    // Call Unmoor() - this disconnects the SpringJoint (but doesn't destroy it!)
                    // and re-parents rope to initialParent
                    rope.Unmoor();

                    // Unmoor() re-parents to initialParent but doesn't reset local position/rotation
                    // Force the rope back to its hanger position
                    if (initialParent != null)
                    {
                        rope.transform.localPosition = initialPos;
                        rope.transform.localRotation = initialRot;
                    }

                    // Force RopeEffect to update
                    var ropeEffect = ropeTraverse.Field("rope").GetValue<RopeEffect>();
                    if (ropeEffect != null)
                    {
                        ropeEffect.enabled = false;
                        ropeEffect.enabled = true;
                    }

                    VerboseLogger.ControlApply($"Unmoored rope {packet.RopeIndex}, boat={packet.BoatName}");
                }
            }
            finally
            {
                IsApplyingRemoteState = false;
            }
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
            _lastHelmInput = 0f;
            _lastRopeLengths = new float[0];
            _ropeLastChangeTime = new float[0];   // drop rope settle-terminal tracking
            _ropeFinalSent = new bool[0];
            _ropeLastSentLength = new float[0];
            _ropeWinchMap.Clear();                // per-boat winch/anchor cache dies with the session
            _ropeCacheAnchor = null;
            _ropeCacheBoatName = null;
            _anchorRopeIndices.Clear();
            _loggedBoatRopes.Clear();
            _helmLeaseHolder.Clear();
            _helmLeaseLastInput.Clear();
            _helmDeniedUntil.Clear();
            _lastHelmDeniedSent.Clear();
            _recentNetworkMooringChanges.Clear(); // per-session map; stale rope-instanceId keys must not
                                                  // bleed into the next session.
            _mooringTerminals.Clear();            // drop any pending debounced mooring terminals
            _pendingRopes.Clear();                // join-race deferred rope seeds die with the session
            _applyingPerPacket = false;           // clear both apply guards so neither sticks across sessions
            _applyingJoinState = false;
        }
    }
}
