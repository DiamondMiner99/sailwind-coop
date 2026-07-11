using System.Collections.Generic;
using UnityEngine;
using Steamworks;
using SailwindCoop.Debug;
using SailwindCoop.Networking.Packets;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Manages continuous boat synchronization between host and guest.
    /// Host sends boat transforms at 40Hz (primary boat) / 10Hz (secondary crewed boats),
    /// guest runs a per-boat physics correction toward the received state.
    /// </summary>
    public class BoatSyncManager : MonoBehaviour
    {
        public static BoatSyncManager Instance { get; private set; }

        private const float SyncInterval = 0.025f; // 40 Hz
        private float _lastSyncTime;

        // (v0.2.28 multi-boat) Secondary boats (crewed boats that are NOT the host's lastBoat) are
        // throttled to every 4th send tick (~10Hz) to keep the added bandwidth negligible.
        private const int SecondaryBoatSendDivider = 4;
        private int _sendTick;

        // (v0.2.28 multi-boat) Guest-side entries for boats we have not heard about in this long are
        // pruned (host stopped streaming them - e.g. the last remote player left that boat).
        private const float BoatStatePruneSeconds = 10f;

        /// <summary>
        /// Per-boat interpolation/correction state (guest only). (v0.2.28) Previously a single set of
        /// fields keyed by one _targetBoatName - so when the host streamed a SECOND boat (multi-boat
        /// streaming, "old ship rotates in place" fix) each packet clobbered the other boat's target and
        /// the correction chased a moving key. One entry per boat name lets multiple boats be corrected
        /// concurrently with fully independent teleport/staleness/integrator state.
        ///
        /// Positions are stored REAL (offset-independent) and converted to local on-demand; this prevents
        /// stale positions when the FloatingOriginManager offset changes during shifting.
        /// </summary>
        private class BoatSyncState
        {
            public Vector3 TargetRealPosition;
            public Quaternion TargetRotation;
            public Vector3 TargetVelocity;
            public Vector3 TargetAngularVelocity;
            public float LastPacketTime;
            // Staleness snap: set when a >2s receive stall is detected in OnBoatTransformReceived.
            // The next ApplyBoatTransform teleport-snaps instead of velocity-chasing a many-seconds-old
            // gap at huge correction speeds. Unscaled clock so 16x sleep timewarp can't inflate the
            // inter-packet gap and cause false snaps.
            public bool SnapOnNextApply;
            public float LastPacketUnscaledTime;
            // False until the first real target (packet or world-state) lands. A pre-armed entry created
            // by ForceSnapOnNextApply has no target yet - applying it would teleport the boat to a
            // zero-initialized position.
            public bool HasTarget;

            // Cached references to avoid GetComponent in hot paths
            public Transform CachedBoatTransform;
            public Rigidbody CachedBoatRb;

            // Previous-frame values, used to detect floating-origin shifts and position jumps
            public Vector3 PrevBoatPosition;
            public Vector3 PrevOffset;
            public bool HasPrevValues;
        }

        // Guest-side per-boat states, keyed by root SaveableObject name (the BoatTransformPacket key).
        private readonly Dictionary<string, BoatSyncState> _boatStates = new Dictionary<string, BoatSyncState>();
        // Scratch list for pruning while iterating (avoid per-frame allocation where possible).
        private readonly List<string> _pruneScratch = new List<string>();

        // PRIMARY boat name = the host's lastBoat (packet.IsPrimary). Preserves the pre-multi-boat
        // single-boat semantics for SnapBoatToLiveTarget and the join flow.
        private string _targetBoatName;
        private bool _hasReceivedState;

        // Cached references for the HOST's primary send path (avoid GetComponent in hot paths)
        private Transform _cachedBoatTransform;
        private Rigidbody _cachedBoatRb;
        private SaveableObject _cachedBoatSaveable;

        // (v0.2.28 Fix A) Host send path: reusable set of active boats per tick (lastBoat + every boat
        // carrying a remote crew member). Reused to avoid per-frame allocation.
        private readonly List<SaveableObject> _activeBoatsScratch = new List<SaveableObject>();
        private readonly HashSet<string> _activeBoatNamesScratch = new HashSet<string>();

        // Physics-based correction parameters
        // Higher values = faster correction but more "snappy", lower = smoother but may lag behind
        private const float PositionCorrectionStrength = 5f;  // How aggressively to correct position error
        private const float RotationCorrectionStrength = 5f;  // How aggressively to correct rotation error
        private const float VelocityCorrectionStrength = 2f;  // How aggressively to match host velocity
        private const float TeleportThreshold = 50f;          // Direct teleport if error exceeds this (meters)

        // Vertical (Y) correction is SOFTENED so LOCAL buoyancy owns the boat's height on the wave
        // surface. Hard-matching the host's exact Y every frame fights the local buoyancy solve and can
        // push the hull under the locally rendered surface in chop. The wave spectrum is seeded
        // deterministically on every client (WeatherPatches.OceanSpectrumSeedPatch), so the boat naturally
        // floats at the same height as the host; we only need a GENTLE Y pull to stop slow vertical drift
        // in extreme swell - not a hard snap. XZ correction is unchanged (host stays authoritative for
        // horizontal position).
        private const float VerticalCorrectionFactor = 0.35f; // 0=Y free (buoyancy only), 1=old hard Y match
        // If the boat diverges vertically by more than this, fall back to full Y correction (catches the
        // boat before it can clip badly through the deck/water; normal float jitter stays well under this).
        private const float VerticalHardCorrectThreshold = 3f; // meters

        /// <summary>
        /// Set to true during join/recovery to prevent physics sync from running.
        /// This avoids physics explosion when boat is teleported with mooring springs attached.
        ///
        /// N-player (Phase 3) - per-joining-peer by construction: this flag is read only by the GUEST's
        /// ApplyBoatTransform (it gates applying the host's boat transform to OUR boat). It is only ever
        /// set on the JOINING guest's own machine - by that guest's own BoatStateApplicator join
        /// coroutine and by the RecoveryStarted handler (a recovery WE receive). The host never sets it,
        /// and one guest's join can NOT set it on another guest's machine. So an already-settled guest is
        /// inherently NOT caught in a new guest's join window, and overlapping joins (which happen on
        /// different machines) never clobber each other. The only same-machine overlap is this guest's
        /// own join vs. a recovery it receives - a single local window, ordered by its own coroutine
        /// (ApplyWorldState StopCoroutine-restarts a prior apply rather than nesting). At N=1 this is
        /// the one guest setting then clearing it exactly as before. (Contrast IgnoreRemoteItemDestruction
        /// in ItemSyncManager, which IS host-side global across guests and was ref-counted in Phase 3.)
        /// </summary>
        public static bool IsJoinInProgress { get; set; }

        /// (v0.2.25) True once THIS machine has received a BoatWorldState join snapshot from the host.
        /// Distinct from _hasReceivedState, which per-packet BoatTransform updates also set: the guest
        /// join-state WATCHDOG (Plugin.GuestJoinWatchdog) needs proof the host actually ADMITTED us and
        /// sent the full snapshot, not merely that some transform packet leaked through. Never true on a
        /// host (only guests receive BoatWorldState). Cleared in Reset() so a later session re-arms it.
        public static bool HasReceivedWorldState { get; private set; }

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

            if (Plugin.IsHost)
            {
                SendBoatTransforms();
            }
            else
            {
                ApplyBoatTransforms();
            }

            Plugin.Profiler?.EndMeasureBoatSync();
        }

        /// <summary>
        /// Host: Send current boat transforms to all guests. (v0.2.28 Fix A, "old ship rotates in place")
        /// Previously only GameState.lastBoat was streamed - when the host boarded a newly bought boat,
        /// the OLD boat (still crewed by guests) got zero position sync: guest helm input reached the host
        /// via control sync, so the boat rotated in the host's sim but never translated on the guest's
        /// screen. Now the host streams EVERY active boat: lastBoat at the full rate plus any boat that
        /// currently carries a remote crew member at 1/4 rate (~10Hz).
        /// </summary>
        private void SendBoatTransforms()
        {
            // (v0.2.25) SyncInterval is scaled up (rate halved) while the host drives a co-op sleep:
            // Time.time runs 16x under the warp, so unscaled this channel alone floods guests and
            // (with the other high-freq channels) saturated a guest's packet budget - the backlog
            // delayed transforms ~3.5s and triggered the 175-200m SLEEP_SNAP crash chain.
            if (Time.time - _lastSyncTime < SyncInterval * SleepSyncManager.HostSleepSendIntervalScale) return;
            _lastSyncTime = Time.time;
            _sendTick++;

            // Symmetric to the guest's ApplyBoatTransforms shipyard guard. While the HOST has a boat
            // admitted to a shipyard, vanilla AdmitShip kinematically lifts THAT boat onto the cradle;
            // streaming its cradle-lifted transform would fight peers' view of it. (v0.2.28 multi-boat:
            // narrowed from a blanket whole-stream freeze to the ONE admitted boat, resolved from
            // currentShipyard.currentShip - other boats keep syncing while the host browses/edits.
            // Null currentShip while merely browsing the shipyard menu = nothing suppressed.)
            // NOTE: a boat a GUEST is editing in a cradle is deliberately NOT suppressed here - the host
            // stream stays authoritative and other peers keep seeing it in the water; the cosmetic cradle
            // lift on the editing guest's screen is not synced (see ShipyardSyncManager Fix C notes).
            var localShipyardBoatName = GetLocalShipyardBoatName();

            var lastBoat = GameState.lastBoat;
            var lastBoatName = lastBoat != null ? lastBoat.name : null;

            // Primary boat (lastBoat): full rate, cached refs (hot path, unchanged behavior).
            if (lastBoat != null && lastBoatName != localShipyardBoatName)
            {
                // Update cache if boat changed
                if (_cachedBoatTransform != lastBoat)
                {
                    _cachedBoatTransform = lastBoat;
                    _cachedBoatRb = lastBoat.GetComponent<Rigidbody>();
                    _cachedBoatSaveable = lastBoat.GetComponent<SaveableObject>();
                }

                if (_cachedBoatRb != null)
                {
                    SendTransformFor(lastBoat, _cachedBoatRb, _cachedBoatSaveable, isPrimary: true);
                }
            }

            // Secondary crewed boats: throttled to every 4th tick (~10Hz) - they are typically moored or
            // drifting, and the guest aboard still gets full-fidelity control sync; this only carries hull
            // translation/rotation. GetComponent at 10Hz is negligible, so no per-boat cache is kept.
            // The avatar scan lives inside the divider branch so it only runs on ticks that actually send.
            if ((_sendTick % SecondaryBoatSendDivider) == 0)
            {
                // Build the secondary set: every boat a remote crew member is on, minus the primary.
                _activeBoatsScratch.Clear();
                _activeBoatNamesScratch.Clear();

                var rpm = Player.RemotePlayerManager.Instance;
                if (rpm != null)
                {
                    foreach (var avatar in rpm.Avatars)
                    {
                        var crewedBoatName = avatar.CurrentBoatName;
                        if (string.IsNullOrEmpty(crewedBoatName)) continue;           // remote player ashore
                        if (crewedBoatName == lastBoatName) continue;                 // primary already streamed
                        if (!_activeBoatNamesScratch.Add(crewedBoatName)) continue;   // already queued this tick
                        var crewedBoat = BoatUtility.FindBoatByName(crewedBoatName);
                        if (crewedBoat != null) _activeBoatsScratch.Add(crewedBoat);
                    }
                }

                foreach (var boatSaveable in _activeBoatsScratch)
                {
                    if (boatSaveable.gameObject.name == localShipyardBoatName) continue; // host's admitted boat
                    var rb = boatSaveable.GetComponent<Rigidbody>();
                    if (rb == null) continue;
                    SendTransformFor(boatSaveable.transform, rb, boatSaveable, isPrimary: false);
                }
            }
        }

        /// <summary>
        /// Name of the boat currently admitted to the LOCAL machine's shipyard cradle (null when not at a
        /// shipyard, or browsing with no ship admitted). Vanilla Shipyard.GetCurrentBoat() returns the
        /// admitted boat root; prefer the SaveableObject name, the shared sync key.
        /// </summary>
        private static string GetLocalShipyardBoatName()
        {
            var shipyard = GameState.currentShipyard;
            if (shipyard == null) return null;
            var ship = shipyard.GetCurrentBoat();
            if (ship == null) return null;
            var saveable = ship.GetComponent<SaveableObject>();
            return saveable != null ? saveable.gameObject.name : ship.name;
        }

        /// <summary>
        /// Host: serialize and broadcast one boat's transform. Shared by the primary (lastBoat, 40Hz)
        /// and secondary (crewed, ~10Hz) send paths.
        /// </summary>
        private void SendTransformFor(Transform boat, Rigidbody rb, SaveableObject saveable, bool isPrimary)
        {
            // Convert to real world coordinates (subtract FloatingOriginManager offset)
            var offset = FloatingOriginManager.instance?.outCurrentOffset ?? Vector3.zero;
            var realPosition = boat.position - offset;

            var packet = new BoatTransformPacket
            {
                BoatName = boat.name,
                Position = realPosition,
                Rotation = boat.rotation,
                Velocity = rb.velocity,
                AngularVelocity = rb.angularVelocity,
                IsAnchored = BoatUtility.IsBoatAnchored(saveable),
                IsPrimary = isPrimary
            };

            VerboseLogger.BoatSend($"BoatTransform, boat={boat.name}, primary={isPrimary}, localPos={boat.position}, offset={offset}, realPos={realPosition}, vel={rb.velocity.magnitude:F2}m/s", throttle: true);

            Plugin.NetworkManager.SendToAllUnreliable(PacketType.BoatTransform, writer =>
            {
                PacketSerializer.WriteBoatTransform(writer, packet);
            });
        }

        /// <summary>
        /// Guest: Apply velocity-based correction to guide physics toward host's authoritative state,
        /// for EVERY boat the host streams (v0.2.28 multi-boat). Physics runs naturally (buoyancy, wind
        /// forces), we just nudge velocity to match host. Entries not refreshed for BoatStatePruneSeconds
        /// are pruned (host stopped streaming that boat).
        /// </summary>
        private void ApplyBoatTransforms()
        {
            if (!_hasReceivedState) return;

            // Skip physics sync during join/recovery - boat may have mooring springs attached
            if (IsJoinInProgress) return;

            // While the LOCAL player has a boat admitted to a shipyard, vanilla AdmitShip has
            // kinematically lifted it onto the cradle. The host doesn't enter the shipyard, so its copy
            // keeps bobbing in the water and broadcasts that transform; applying it here would force the
            // boat NON-kinematic again (the isKinematic re-enable below) and nudge it toward the host's
            // water position - fighting the cradle lift on the editing client. (v0.2.28 multi-boat:
            // narrowed from a blanket all-boats suppression to the ONE admitted boat, resolved from
            // currentShipyard.currentShip - other streamed boats keep correcting while we edit. Null
            // currentShip while browsing = nothing suppressed.) The lift releases (DischargeShip) and
            // normal sync resumes via the ForceSnapOnNextApply armed in OnLocalShipyardState.
            var localShipyardBoatName = GetLocalShipyardBoatName();

            _pruneScratch.Clear();
            foreach (var kvp in _boatStates)
            {
                var state = kvp.Value;

                // Prune boats the host stopped streaming (e.g. the last remote player left that boat).
                // Unscaled clock for the same 16x-sleep reason as the staleness snap.
                if (Time.unscaledTime - state.LastPacketUnscaledTime > BoatStatePruneSeconds)
                {
                    _pruneScratch.Add(kvp.Key);
                    continue;
                }

                if (kvp.Key == localShipyardBoatName) continue; // local cradle owns this boat

                ApplyBoatTransformFor(kvp.Key, state);
            }

            foreach (var staleName in _pruneScratch)
            {
                _boatStates.Remove(staleName);
                VerboseLogger.BoatApply($"Pruned stale boat sync state for '{staleName}' (no packet for {BoatStatePruneSeconds:F0}s)");
            }
        }

        /// <summary>
        /// Guest: per-boat correction step. This is the pre-v0.2.28 single-boat ApplyBoatTransform body
        /// operating on a BoatSyncState entry instead of instance fields - the teleport threshold snap,
        /// staleness snap, sleep-warp tracking, vertical softening and ALL integrator clamps (the
        /// pre-v0.2.19 357 m/s runaway lesson) are preserved exactly.
        /// </summary>
        private void ApplyBoatTransformFor(string boatName, BoatSyncState state)
        {
            // (v0.2.28 Fix C) NOTE: a boat that is shipyard-active on ANOTHER peer is NOT skipped here.
            // The host stream stays authoritative and this peer keeps seeing the boat in the water; the
            // cosmetic cradle lift on the editing machine's screen is deliberately not synced.

            // Pre-armed entry (ForceSnapOnNextApply) with no real target yet - nothing to apply.
            if (!state.HasTarget) return;

            // Look up boat by name instead of using GameState.lastBoat
            // This prevents applying transform to wrong boat when player steps on a different boat
            var boatSaveable = BoatUtility.FindBoatByName(boatName);
            if (boatSaveable == null) return;
            var boat = boatSaveable.transform;

            // Update cache if boat changed
            if (state.CachedBoatTransform != boat)
            {
                state.CachedBoatTransform = boat;
                state.CachedBoatRb = boat.GetComponent<Rigidbody>();
                Plugin.Log.LogInfo($"Guest: Cached new boat: {boat.name}");
            }

            var rb = state.CachedBoatRb;
            if (rb == null) return;

            // Ensure boat is NOT kinematic - we want physics to run
            if (rb.isKinematic)
            {
                Plugin.Log.LogInfo("Guest: Enabling physics on boat (was kinematic)");
                rb.isKinematic = false;
            }

            // Calculate local position on-demand using CURRENT offset
            var offset = FloatingOriginManager.instance?.outCurrentOffset ?? Vector3.zero;

            // Detect FOM offset changes (verbose diagnostics only)
            if (state.HasPrevValues)
            {
                var offsetDelta = (offset - state.PrevOffset).magnitude;
                if (offsetDelta > 0.1f)
                {
                    VerboseLogger.TeleportDebug($"FOM_SHIFT: offsetDelta={offsetDelta:F1}m, prevOffset={state.PrevOffset}, newOffset={offset}");
                }
            }

            // Extrapolate target position using velocity to smooth out gaps between packets
            float timeSincePacket = Mathf.Min(Time.time - state.LastPacketTime, 0.05f);
            var extrapolatedRealPosition = state.TargetRealPosition + state.TargetVelocity * timeSincePacket;
            var targetLocalPosition = extrapolatedRealPosition + offset;

            // Extrapolate rotation using angular velocity
            var extrapolatedRotation = state.TargetRotation * Quaternion.Euler(state.TargetAngularVelocity * Mathf.Rad2Deg * timeSincePacket);

            // Position error determines correction strategy
            Vector3 positionError = targetLocalPosition - boat.position;
            float errorMagnitude = positionError.magnitude;

            // While WE are in an active co-op sleep timewarp (eyes closed, black screen, timeScale up
            // to 16), the physics-correction integrator below is wildly unstable (5 * dt with
            // 16x-inflated deltaTime overshoots every frame and runs away). The player can't see the
            // boat anyway, so just track the host directly:
            // snap when meaningfully off, otherwise adopt the host's velocities and let buoyancy idle.
            // Resumes normal correction automatically on wake (state leaves Sleeping).
            if (SleepSyncManager.IsCoopSleepWarpActive)
            {
                if (errorMagnitude > 5f)
                {
                    VerboseLogger.TeleportDebug($"SLEEP_SNAP: error={errorMagnitude:F1}m during co-op sleep, snapping to host");
                    boat.position = targetLocalPosition;
                    boat.rotation = extrapolatedRotation;
                }
                rb.velocity = state.TargetVelocity;
                rb.angularVelocity = state.TargetAngularVelocity;
                state.SnapOnNextApply = false;

                state.PrevBoatPosition = boat.position;
                state.PrevOffset = offset;
                state.HasPrevValues = true;
                return;
            }

            if (errorMagnitude > TeleportThreshold || (state.SnapOnNextApply && errorMagnitude > 5f))
            {
                // === TELEPORT MODE ===
                // Error too large for velocity correction (join/recovery), or the stream stalled >2s and we
                // re-acquired far from target - teleport directly instead of a violent velocity chase.
                VerboseLogger.TeleportDebug($"TELEPORT: error={errorMagnitude:F1}m (threshold={TeleportThreshold}m, staleSnap={state.SnapOnNextApply}), " +
                    $"from={boat.position} to={targetLocalPosition}");

                boat.position = targetLocalPosition;
                boat.rotation = extrapolatedRotation;
                rb.velocity = state.TargetVelocity;
                rb.angularVelocity = state.TargetAngularVelocity;
                state.SnapOnNextApply = false;
            }
            else
            {
                state.SnapOnNextApply = false; // error is small again - no snap needed
                // === PHYSICS-BASED CORRECTION ===
                // Small error - apply velocity corrections to let physics run naturally

                // Soften the VERTICAL component of the position/velocity error so LOCAL
                // buoyancy owns the boat's height on the (now identical) wave surface. We keep full XZ
                // authority but only gently pull Y toward the host - unless vertical divergence grows past
                // VerticalHardCorrectThreshold, in which case we fully correct Y to catch a real desync
                // before the hull clips through the deck/water. Solo is never multiplayer so never reaches
                // here; this only runs guest-side.
                float verticalFactor = (Mathf.Abs(positionError.y) > VerticalHardCorrectThreshold)
                    ? 1f
                    : VerticalCorrectionFactor;
                positionError.y *= verticalFactor;

                // Position correction: add velocity toward target position
                Vector3 positionCorrection = positionError * PositionCorrectionStrength;

                // Velocity correction: blend toward host's velocity
                Vector3 velocityError = state.TargetVelocity - rb.velocity;
                // Don't force vertical velocity to match the host - that would override the local buoyancy
                // bob. Soften Y by the same factor (full only when far out of vertical sync).
                velocityError.y *= verticalFactor;
                Vector3 velocityCorrection = velocityError * VelocityCorrectionStrength;

                // Integrator stabilization: the explicit-Euler step `vel += k*error*dt` diverges when
                // k*dt >= 1 (k=5 -> dt >= 0.2s). Low-FPS guests and 16x-sleep frames blow past that,
                // turning the correction into an oscillating runaway. Clamp the step so one bad frame
                // can never overshoot the target.
                float dt = Mathf.Min(Time.deltaTime, 0.1f);

                // Clamp the commanded correction so a large-but-sub-teleport error (e.g. after a receive
                // stall) is chased gently instead of violently. No-op at normal errors (<5m -> <~35 m/s^2).
                Vector3 correction = Vector3.ClampMagnitude(positionCorrection + velocityCorrection, 30f);
                rb.velocity += correction * dt;

                // Hard speed ceiling relative to the host's authoritative speed - catches any residual
                // runaway regardless of source. No-op in normal play.
                float maxSpeed = state.TargetVelocity.magnitude + 8f;
                if (rb.velocity.magnitude > maxSpeed)
                    rb.velocity = rb.velocity.normalized * maxSpeed;

                // Rotation correction: apply angular velocity toward target rotation
                Quaternion rotationError = extrapolatedRotation * Quaternion.Inverse(boat.rotation);
                rotationError.ToAngleAxis(out float angle, out Vector3 axis);
                if (angle > 180f) angle -= 360f; // Normalize to [-180, 180]

                if (Mathf.Abs(angle) > 0.1f) // Only correct if error is significant
                {
                    Vector3 rotationCorrection = axis * (angle * Mathf.Deg2Rad) * RotationCorrectionStrength;

                    // Angular velocity correction: blend toward host's angular velocity
                    Vector3 angularVelocityError = state.TargetAngularVelocity - rb.angularVelocity;
                    Vector3 angularVelocityCorrection = angularVelocityError * VelocityCorrectionStrength;

                    rb.angularVelocity += (rotationCorrection + angularVelocityCorrection) * dt;
                }

                // Debug: Log significant position errors (but below teleport threshold)
                if (errorMagnitude > 5f)
                {
                    VerboseLogger.TeleportDebug($"POSITION_ERROR: {errorMagnitude:F1}m, " +
                        $"current={boat.position}, target={targetLocalPosition}, " +
                        $"velocity={rb.velocity.magnitude:F1}m/s, correction={positionCorrection.magnitude:F1}");
                }
            }

            // Store for next frame comparison
            state.PrevBoatPosition = boat.position;
            state.PrevOffset = offset;
            state.HasPrevValues = true;
        }

        /// <summary>
        /// Called when a BoatTransform packet is received from host.
        /// Updates interpolation targets for the guest, per boat (v0.2.28 multi-boat).
        /// </summary>
        public void OnBoatTransformReceived(BoatTransformPacket packet)
        {
            // Store real position directly, don't convert to local here;
            // local position is calculated on-demand in ApplyBoatTransformFor using the current offset
            VerboseLogger.BoatRecv($"BoatTransform, boat={packet.BoatName}, primary={packet.IsPrimary}, realPos={packet.Position}, vel={packet.Velocity.magnitude:F2}m/s", throttle: true);

            if (string.IsNullOrEmpty(packet.BoatName)) return;

            bool isNewEntry = !_boatStates.TryGetValue(packet.BoatName, out var state);
            if (isNewEntry)
            {
                state = new BoatSyncState();
                _boatStates[packet.BoatName] = state;
                // New-entry snap default: the FIRST apply for a boat we weren't tracking teleports to the
                // received pose instead of velocity-chasing an arbitrary local-sim gap. Covers first-ever
                // packets AND the prune-then-resume hole (a boat pruned after 10s of silence re-enters as
                // a fresh entry and snaps). Harmless for the join flow: SnapBoatToLiveTarget wants a snap
                // anyway, and the flag only fires when the error exceeds 5m.
                state.SnapOnNextApply = true;
            }
            else if (!state.HasTarget)
            {
                // Pre-armed entry from ForceSnapOnNextApply: treat this first packet as new data (skip the
                // jump/staleness diagnostics below, which would read zero-initialized targets).
                isNewEntry = true;
            }

            // Detect large jumps in received target position (> 20m); these would indicate
            // packet reordering, corruption, or a sender-side issue (verbose diagnostics only)
            if (!isNewEntry)
            {
                var targetJump = (packet.Position - state.TargetRealPosition).magnitude;
                if (targetJump > 20f)
                {
                    VerboseLogger.TeleportDebug($"TARGET_JUMP: boat={packet.BoatName}, delta={targetJump:F1}m, prevTarget={state.TargetRealPosition}, " +
                        $"newTarget={packet.Position}, vel={packet.Velocity.magnitude:F1}m/s, " +
                        $"timeSinceLastPacket={(Time.time - state.LastPacketTime):F3}s");
                }
            }

            // Staleness snap: a long receive stall leaves the target frozen while our boat sails on;
            // when the stream resumes, the correction would chase the huge gap at runaway speeds.
            // Flag a one-shot snap instead. UNSCALED clock: Time.time-based gaps inflate 16x during co-op
            // sleep timewarp and would false-positive on every normal packet interval. Secondary boats
            // stream at 1/4 rate (0.1s interval), still far below the 2s stall threshold.
            if (!isNewEntry && Time.unscaledTime - state.LastPacketUnscaledTime > 2f)
            {
                VerboseLogger.TeleportDebug($"STALE_STREAM: boat={packet.BoatName}, {Time.unscaledTime - state.LastPacketUnscaledTime:F1}s since last packet; will snap on next apply");
                state.SnapOnNextApply = true;
            }

            // Track the PRIMARY boat name (host's lastBoat) for SnapBoatToLiveTarget / join flow.
            if (packet.IsPrimary)
                _targetBoatName = packet.BoatName;

            state.TargetRealPosition = packet.Position;
            state.TargetRotation = packet.Rotation;
            state.TargetVelocity = packet.Velocity;
            state.TargetAngularVelocity = packet.AngularVelocity;
            state.LastPacketTime = Time.time;
            state.LastPacketUnscaledTime = Time.unscaledTime;
            state.HasTarget = true;
            _hasReceivedState = true;
        }

        /// <summary>
        /// (v0.2.28 Fix C) Force a one-shot teleport snap for a boat on the next apply, without waiting
        /// for the 50m threshold. Used when a shipyard discharge arrives: the boat was frozen in place
        /// here while the editing peer moved it (cradle lift + release teleport), so converge by snapping
        /// to the next authoritative transform instead of velocity-chasing the gap.
        /// </summary>
        public void ForceSnapOnNextApply(string boatName)
        {
            if (string.IsNullOrEmpty(boatName)) return;
            if (!_boatStates.TryGetValue(boatName, out var state))
            {
                // No entry yet (e.g. this peer never received a stream for the boat, or it was pruned):
                // create a pre-armed one instead of silently no-oping. HasTarget stays false, so nothing
                // is applied until the first packet lands; the fresh timestamps keep it from being pruned
                // before that packet arrives.
                state = new BoatSyncState
                {
                    LastPacketTime = Time.time,
                    LastPacketUnscaledTime = Time.unscaledTime
                };
                _boatStates[boatName] = state;
            }
            state.SnapOnNextApply = true;
        }

        /// <summary>
        /// Called when initial BoatWorldState packet is received from host.
        /// Applies full world state and initializes interpolation targets.
        /// </summary>
        public void OnBoatWorldStateReceived(BoatWorldStatePacket packet)
        {
            VerboseLogger.BoatRecv($"BoatWorldState, boats={packet.Boats.Length}, currentBoat={packet.CurrentBoatName}");

            // (v0.2.25) Mark the join snapshot as ARRIVED before applying it (apply may bail early if the
            // boat lookup fails, but arrival alone proves the host admitted us - the watchdog must stand down).
            HasReceivedWorldState = true;

            BoatStateApplicator.ApplyWorldState(packet);

            // Initialize interpolation target
            var currentBoat = BoatUtility.GetCurrentBoat();
            if (currentBoat != null)
            {
                // Store real position, not local:
                // convert the current local position back to real for consistent storage
                var offset = FloatingOriginManager.instance?.outCurrentOffset ?? Vector3.zero;

                var boatName = currentBoat.gameObject.name;
                if (!_boatStates.TryGetValue(boatName, out var state))
                {
                    state = new BoatSyncState();
                    _boatStates[boatName] = state;
                }
                state.TargetRealPosition = currentBoat.transform.position - offset;
                state.TargetRotation = currentBoat.transform.rotation;
                state.TargetVelocity = Vector3.zero; // Will be updated by next BoatTransform packet
                state.TargetAngularVelocity = Vector3.zero;
                state.LastPacketTime = Time.time;
                state.LastPacketUnscaledTime = Time.unscaledTime;
                state.HasTarget = true;

                _targetBoatName = boatName;
                _hasReceivedState = true;

                VerboseLogger.BoatApply($"WorldState applied, currentBoat={packet.CurrentBoatName}, realPos={state.TargetRealPosition}");
            }
        }

        /// <summary>
        /// At-sea join: snap the guest's boat to the host's LIVE transform + velocity right now.
        /// During a join the per-frame apply (ApplyBoatTransforms) is gated off by IsJoinInProgress, so the
        /// guest's boat sits at the multi-second-old join SNAPSHOT. OnBoatTransformReceived is NOT gated, so
        /// the primary boat's TargetRealPosition has tracked the host's live position the whole time. Called
        /// right before the guest is placed/embarked so the deck is where the host's boat ACTUALLY is (not
        /// where it was when the join started). Then when IsJoinInProgress clears, the first apply sees a
        /// tiny error -> smooth correction, instead of a >50m TELEPORT that yanks the deck out from under
        /// the just-embarked guest and strands them. No-op at a port (no BoatTransform streamed, or live
        /// target == snapshot). Returns true if it snapped. Mirrors the teleport branch exactly. Operates
        /// on the PRIMARY boat (host's lastBoat) only, matching pre-multi-boat behavior.
        /// </summary>
        public bool SnapBoatToLiveTarget()
        {
            if (!_hasReceivedState || string.IsNullOrEmpty(_targetBoatName)) return false;
            if (!_boatStates.TryGetValue(_targetBoatName, out var state)) return false;
            var boatSaveable = BoatUtility.FindBoatByName(_targetBoatName);
            if (boatSaveable == null) return false;
            var boat = boatSaveable.transform;
            var rb = boat.GetComponent<Rigidbody>();
            var offset = FloatingOriginManager.instance?.outCurrentOffset ?? Vector3.zero;

            boat.position = state.TargetRealPosition + offset;
            boat.rotation = state.TargetRotation;
            if (rb != null && !rb.isKinematic)
            {
                rb.velocity = state.TargetVelocity;
                rb.angularVelocity = state.TargetAngularVelocity;
            }
            // Prime cache + prev-frame values so the first post-join apply compares against THIS snapped
            // position (otherwise it would read a spurious one-frame apparent velocity from the old pos).
            state.CachedBoatTransform = boat;
            state.CachedBoatRb = rb;
            state.PrevBoatPosition = boat.position;
            state.PrevOffset = offset;
            state.HasPrevValues = true;
            Plugin.Log.LogInfo($"[JOIN] Snapped boat '{boat.name}' to LIVE host transform before embark: realPos={state.TargetRealPosition}, vel={state.TargetVelocity.magnitude:F1}m/s");
            return true;
        }

        /// <summary>
        /// Reset sync state when disconnecting from multiplayer session.
        /// </summary>
        public void Reset()
        {
            _hasReceivedState = false;
            _lastSyncTime = 0f;
            _sendTick = 0;
            _targetBoatName = null;
            _boatStates.Clear();
            IsJoinInProgress = false;
            HasReceivedWorldState = false; // (v0.2.25) re-arm the guest join-state watchdog for the next session

            // Clear cache
            _cachedBoatTransform = null;
            _cachedBoatRb = null;
            _cachedBoatSaveable = null;
            _activeBoatsScratch.Clear();
            _activeBoatNamesScratch.Clear();
        }

        /// <summary>
        /// Host: Send full boat world state to ALL guests. This is the RECOVERY path (the host re-syncs
        /// every crew member onto the recovered boat), so it legitimately broadcasts. The JOIN path uses
        /// the targeted SendBoatWorldStateTo below instead, to avoid re-running the ~15-20s teleport-join
        /// coroutine on already-settled guests.
        /// </summary>
        public void SendBoatWorldState()
        {
            if (!Plugin.IsHost)
            {
                Plugin.Log.LogWarning("Only host can send boat world state");
                return;
            }

            var packet = BoatStateCollector.CollectWorldState();
            packet.IsRecovery = true;  // mark as recovery so guests not on the recovered boat skip the teleport

            // On recovery the host is usually on the dock (not aboard), so the collected
            // CurrentBoatName is "" -> every guest's recovery guard mismatches and SKIPS the resync (-> floating
            // crates + vanished furniture). Fall back to the recovered boat's name HERE (recovery-only), NOT in the
            // shared CollectWorldState, so an ashore-host FRESH JOIN still sends "" and doesn't seat a land-spawned
            // guest's GameState.currentBoat. lastOwnedBoat is what vanilla Recovery actually recovers; lastBoat backs it.
            if (string.IsNullOrEmpty(packet.CurrentBoatName))
                packet.CurrentBoatName = GameState.lastOwnedBoat?.name ?? GameState.lastBoat?.name ?? "";

            VerboseLogger.BoatSend($"BoatWorldState (all, recovery), boats={packet.Boats.Length}, currentBoat={packet.CurrentBoatName}");

            Plugin.NetworkManager.SendToAllReliable(PacketType.BoatWorldState, writer =>
            {
                PacketSerializer.WriteBoatWorldState(writer, packet);
            });
        }

        /// <summary>
        /// Host: Send full boat world state to ONE joining guest (the heavy join teleport). N-player
        /// (Phase 3): targeted so a new join doesn't re-trigger the long join-teleport coroutine on every
        /// already-settled guest. At N=1 the target IS the only peer, so this == the old SendBoatWorldState
        /// broadcast - byte-identical. Mirrors the SendMapFullSyncToGuest targeted pattern.
        /// </summary>
        public void SendBoatWorldStateTo(SteamId target)
        {
            if (!Plugin.IsHost)
            {
                Plugin.Log.LogWarning("Only host can send boat world state");
                return;
            }

            var packet = BoatStateCollector.CollectWorldState();

            VerboseLogger.BoatSend($"BoatWorldState (to {target}), boats={packet.Boats.Length}, currentBoat={packet.CurrentBoatName}");

            Plugin.NetworkManager.SendReliable(target, PacketType.BoatWorldState, writer =>
            {
                PacketSerializer.WriteBoatWorldState(writer, packet);
            });
        }
    }
}
