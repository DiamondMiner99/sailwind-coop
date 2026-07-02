using UnityEngine;
using Steamworks;
using SailwindCoop.Debug;
using SailwindCoop.Networking.Packets;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Manages continuous boat synchronization between host and guest.
    /// Host sends boat transform at 10Hz, guest interpolates received state.
    /// </summary>
    public class BoatSyncManager : MonoBehaviour
    {
        public static BoatSyncManager Instance { get; private set; }

        private const float SyncInterval = 0.025f; // 40 Hz
        private float _lastSyncTime;

        // Interpolation state (guest only)
        // Store REAL (offset-independent) position and calculate local on-demand; this prevents
        // stale positions when the FloatingOriginManager offset changes during shifting.
        private string _targetBoatName;  // which boat to apply the transform to
        private Vector3 _targetRealPosition;
        private Quaternion _targetRotation;
        private Vector3 _targetVelocity;
        private Vector3 _targetAngularVelocity;
        private float _lastPacketTime;
        private bool _hasReceivedState;
        // Staleness snap: set when a >2s receive stall is detected in OnBoatTransformReceived.
        // The next ApplyBoatTransform teleport-snaps instead of velocity-chasing a many-seconds-old gap
        // at huge correction speeds. Unscaled clock so 16x sleep timewarp can't inflate the inter-packet
        // gap and cause false snaps.
        private bool _snapOnNextApply;
        private float _lastPacketUnscaledTime;

        // Cached references to avoid GetComponent in hot paths
        private Transform _cachedBoatTransform;
        private Rigidbody _cachedBoatRb;
        private SaveableObject _cachedBoatSaveable;

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

        // Previous-frame values, used to detect floating-origin shifts and position jumps
        private Vector3 _prevBoatPosition;
        private Vector3 _prevOffset;
        private bool _hasPrevValues;

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
                SendBoatTransform();
            }
            else
            {
                ApplyBoatTransform();
            }

            Plugin.Profiler?.EndMeasureBoatSync();
        }

        /// <summary>
        /// Host: Send current boat transform to all guests at 10Hz.
        /// </summary>
        private void SendBoatTransform()
        {
            if (Time.time - _lastSyncTime < SyncInterval) return;
            _lastSyncTime = Time.time;

            // Symmetric to the guest's ApplyBoatTransform shipyard guard. While the HOST is
            // in a shipyard, vanilla AdmitShip kinematically lifts the shared boat onto the cradle, but the host
            // would keep streaming the cradle-lifted (or pre-lift water) transform - peers then see the boat
            // bobbing / fighting the host's edit. Freeze the stream for all peers during the host's shipyard
            // edit; it resumes when DischargeShip sets currentShipyard = null (the next ApplyBoatTransform on
            // each guest sees a large error and teleport-snaps back to the host). Solo is never multiplayer.
            if (GameState.currentShipyard != null) return;

            var boat = GameState.lastBoat;
            if (boat == null) return;

            // Update cache if boat changed
            if (_cachedBoatTransform != boat)
            {
                _cachedBoatTransform = boat;
                _cachedBoatRb = boat.GetComponent<Rigidbody>();
                _cachedBoatSaveable = boat.GetComponent<SaveableObject>();
            }

            if (_cachedBoatRb == null) return;

            // Convert to real world coordinates (subtract FloatingOriginManager offset)
            var offset = FloatingOriginManager.instance?.outCurrentOffset ?? Vector3.zero;
            var realPosition = boat.position - offset;

            var packet = new BoatTransformPacket
            {
                BoatName = boat.name,
                Position = realPosition,
                Rotation = boat.rotation,
                Velocity = _cachedBoatRb.velocity,
                AngularVelocity = _cachedBoatRb.angularVelocity,
                IsAnchored = BoatUtility.IsBoatAnchored(_cachedBoatSaveable)
            };

            VerboseLogger.BoatSend($"BoatTransform, boat={boat.name}, localPos={boat.position}, offset={offset}, realPos={realPosition}, vel={_cachedBoatRb.velocity.magnitude:F2}m/s", throttle: true);

            Plugin.NetworkManager.SendToAllUnreliable(PacketType.BoatTransform, writer =>
            {
                PacketSerializer.WriteBoatTransform(writer, packet);
            });
        }

        /// <summary>
        /// Guest: Apply velocity-based correction to guide physics toward host's authoritative state.
        /// Physics runs naturally (buoyancy, wind forces), we just nudge velocity to match host.
        /// </summary>
        private void ApplyBoatTransform()
        {
            if (!_hasReceivedState) return;

            // Skip physics sync during join/recovery - boat may have mooring springs attached
            if (IsJoinInProgress) return;

            // While the LOCAL player is in a shipyard, vanilla AdmitShip has
            // kinematically lifted GameState.currentBoat onto the cradle. The host doesn't enter the shipyard,
            // so its boat keeps bobbing in the water and broadcasts that transform; applying it here would
            // force this boat NON-kinematic again (the isKinematic re-enable below) and nudge it toward the
            // host's water position - fighting the cradle lift (boat bobs in the water on the modifying
            // client). Suppress the per-frame correction entirely while in the shipyard so the local cradle
            // owns the boat. The lift releases (DischargeShip sets currentShipyard = null) and normal sync
            // resumes - the next ApplyBoatTransform sees a large error and teleport-snaps back to the host.
            // DEFERRED: a full cross-peer cradle-kinematic sync (enter/exit packet so EVERY peer freezes the
            // boat on the cradle) is not implemented - the host and other guests still see the boat in the
            // water during the edit. This guard stops the MODIFYING client from fighting its own lift.
            if (GameState.currentShipyard != null) return;

            // Look up boat by name instead of using GameState.lastBoat
            // This prevents applying transform to wrong boat when player steps on a different boat
            var boatSaveable = BoatUtility.FindBoatByName(_targetBoatName);
            if (boatSaveable == null) return;
            var boat = boatSaveable.transform;

            // Update cache if boat changed
            if (_cachedBoatTransform != boat)
            {
                _cachedBoatTransform = boat;
                _cachedBoatRb = boat.GetComponent<Rigidbody>();
                Plugin.Log.LogInfo($"Guest: Cached new boat: {boat.name}");
            }

            if (_cachedBoatRb == null) return;

            // Ensure boat is NOT kinematic - we want physics to run
            if (_cachedBoatRb.isKinematic)
            {
                Plugin.Log.LogInfo("Guest: Enabling physics on boat (was kinematic)");
                _cachedBoatRb.isKinematic = false;
            }

            // Calculate local position on-demand using CURRENT offset
            var offset = FloatingOriginManager.instance?.outCurrentOffset ?? Vector3.zero;

            // Detect FOM offset changes (verbose diagnostics only)
            if (_hasPrevValues)
            {
                var offsetDelta = (offset - _prevOffset).magnitude;
                if (offsetDelta > 0.1f)
                {
                    VerboseLogger.TeleportDebug($"FOM_SHIFT: offsetDelta={offsetDelta:F1}m, prevOffset={_prevOffset}, newOffset={offset}");
                }
            }

            // Extrapolate target position using velocity to smooth out gaps between packets
            float timeSincePacket = Mathf.Min(Time.time - _lastPacketTime, 0.05f);
            var extrapolatedRealPosition = _targetRealPosition + _targetVelocity * timeSincePacket;
            var targetLocalPosition = extrapolatedRealPosition + offset;

            // Extrapolate rotation using angular velocity
            var extrapolatedRotation = _targetRotation * Quaternion.Euler(_targetAngularVelocity * Mathf.Rad2Deg * timeSincePacket);

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
                _cachedBoatRb.velocity = _targetVelocity;
                _cachedBoatRb.angularVelocity = _targetAngularVelocity;
                _snapOnNextApply = false;

                _prevBoatPosition = boat.position;
                _prevOffset = offset;
                _hasPrevValues = true;
                return;
            }

            if (errorMagnitude > TeleportThreshold || (_snapOnNextApply && errorMagnitude > 5f))
            {
                // === TELEPORT MODE ===
                // Error too large for velocity correction (join/recovery), or the stream stalled >2s and we
                // re-acquired far from target - teleport directly instead of a violent velocity chase.
                VerboseLogger.TeleportDebug($"TELEPORT: error={errorMagnitude:F1}m (threshold={TeleportThreshold}m, staleSnap={_snapOnNextApply}), " +
                    $"from={boat.position} to={targetLocalPosition}");

                boat.position = targetLocalPosition;
                boat.rotation = extrapolatedRotation;
                _cachedBoatRb.velocity = _targetVelocity;
                _cachedBoatRb.angularVelocity = _targetAngularVelocity;
                _snapOnNextApply = false;
            }
            else
            {
                _snapOnNextApply = false; // error is small again - no snap needed
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
                Vector3 velocityError = _targetVelocity - _cachedBoatRb.velocity;
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
                _cachedBoatRb.velocity += correction * dt;

                // Hard speed ceiling relative to the host's authoritative speed - catches any residual
                // runaway regardless of source. No-op in normal play.
                float maxSpeed = _targetVelocity.magnitude + 8f;
                if (_cachedBoatRb.velocity.magnitude > maxSpeed)
                    _cachedBoatRb.velocity = _cachedBoatRb.velocity.normalized * maxSpeed;

                // Rotation correction: apply angular velocity toward target rotation
                Quaternion rotationError = extrapolatedRotation * Quaternion.Inverse(boat.rotation);
                rotationError.ToAngleAxis(out float angle, out Vector3 axis);
                if (angle > 180f) angle -= 360f; // Normalize to [-180, 180]

                if (Mathf.Abs(angle) > 0.1f) // Only correct if error is significant
                {
                    Vector3 rotationCorrection = axis * (angle * Mathf.Deg2Rad) * RotationCorrectionStrength;

                    // Angular velocity correction: blend toward host's angular velocity
                    Vector3 angularVelocityError = _targetAngularVelocity - _cachedBoatRb.angularVelocity;
                    Vector3 angularVelocityCorrection = angularVelocityError * VelocityCorrectionStrength;

                    _cachedBoatRb.angularVelocity += (rotationCorrection + angularVelocityCorrection) * dt;
                }

                // Debug: Log significant position errors (but below teleport threshold)
                if (errorMagnitude > 5f)
                {
                    VerboseLogger.TeleportDebug($"POSITION_ERROR: {errorMagnitude:F1}m, " +
                        $"current={boat.position}, target={targetLocalPosition}, " +
                        $"velocity={_cachedBoatRb.velocity.magnitude:F1}m/s, correction={positionCorrection.magnitude:F1}");
                }
            }

            // Store for next frame comparison
            _prevBoatPosition = boat.position;
            _prevOffset = offset;
            _hasPrevValues = true;
        }

        /// <summary>
        /// Called when a BoatTransform packet is received from host.
        /// Updates interpolation targets for the guest.
        /// </summary>
        public void OnBoatTransformReceived(BoatTransformPacket packet)
        {
            // Store real position directly, don't convert to local here;
            // local position is calculated on-demand in ApplyBoatTransform using the current offset
            VerboseLogger.BoatRecv($"BoatTransform, boat={packet.BoatName}, realPos={packet.Position}, vel={packet.Velocity.magnitude:F2}m/s", throttle: true);

            // Detect large jumps in received target position (> 20m); these would indicate
            // packet reordering, corruption, or a sender-side issue (verbose diagnostics only)
            if (_hasReceivedState)
            {
                var targetJump = (packet.Position - _targetRealPosition).magnitude;
                if (targetJump > 20f)
                {
                    VerboseLogger.TeleportDebug($"TARGET_JUMP: delta={targetJump:F1}m, prevTarget={_targetRealPosition}, " +
                        $"newTarget={packet.Position}, vel={packet.Velocity.magnitude:F1}m/s, " +
                        $"timeSinceLastPacket={(Time.time - _lastPacketTime):F3}s");
                }
            }

            // Staleness snap: a long receive stall leaves the target frozen while our boat sails on;
            // when the stream resumes, the correction would chase the huge gap at runaway speeds.
            // Flag a one-shot snap instead. UNSCALED clock: Time.time-based gaps inflate 16x during co-op
            // sleep timewarp and would false-positive on every normal packet interval.
            if (_hasReceivedState && Time.unscaledTime - _lastPacketUnscaledTime > 2f)
            {
                VerboseLogger.TeleportDebug($"STALE_STREAM: {Time.unscaledTime - _lastPacketUnscaledTime:F1}s since last packet; will snap on next apply");
                _snapOnNextApply = true;
            }

            _targetBoatName = packet.BoatName;  // store boat name for lookup
            _targetRealPosition = packet.Position;
            _targetRotation = packet.Rotation;
            _targetVelocity = packet.Velocity;
            _targetAngularVelocity = packet.AngularVelocity;
            _lastPacketTime = Time.time;
            _lastPacketUnscaledTime = Time.unscaledTime;
            _hasReceivedState = true;
        }

        /// <summary>
        /// Called when initial BoatWorldState packet is received from host.
        /// Applies full world state and initializes interpolation targets.
        /// </summary>
        public void OnBoatWorldStateReceived(BoatWorldStatePacket packet)
        {
            VerboseLogger.BoatRecv($"BoatWorldState, boats={packet.Boats.Length}, currentBoat={packet.CurrentBoatName}");

            BoatStateApplicator.ApplyWorldState(packet);

            // Initialize interpolation target
            var currentBoat = BoatUtility.GetCurrentBoat();
            if (currentBoat != null)
            {
                // Store real position, not local:
                // convert the current local position back to real for consistent storage
                var offset = FloatingOriginManager.instance?.outCurrentOffset ?? Vector3.zero;
                _targetRealPosition = currentBoat.transform.position - offset;
                _targetRotation = currentBoat.transform.rotation;
                _targetVelocity = Vector3.zero; // Will be updated by next BoatTransform packet
                _targetAngularVelocity = Vector3.zero;
                _lastPacketTime = Time.time;
                _lastPacketUnscaledTime = Time.unscaledTime;
                _hasReceivedState = true;

                VerboseLogger.BoatApply($"WorldState applied, currentBoat={packet.CurrentBoatName}, realPos={_targetRealPosition}");
            }
        }

        /// <summary>
        /// At-sea join: snap the guest's boat to the host's LIVE transform + velocity right now.
        /// During a join the per-frame apply (ApplyBoatTransform) is gated off by IsJoinInProgress, so the
        /// guest's boat sits at the multi-second-old join SNAPSHOT. OnBoatTransformReceived is NOT gated, so
        /// _targetRealPosition has tracked the host's live position the whole time. Called right before the
        /// guest is placed/embarked so the deck is where the host's boat ACTUALLY is (not where it was when
        /// the join started). Then when IsJoinInProgress clears, the first ApplyBoatTransform sees a tiny
        /// error -> smooth correction, instead of a >50m TELEPORT that yanks the deck out from under the
        /// just-embarked guest and strands them. No-op at a port (no BoatTransform streamed, or live target ==
        /// snapshot). Returns true if it snapped. Mirrors the ApplyBoatTransform teleport branch exactly.
        /// </summary>
        public bool SnapBoatToLiveTarget()
        {
            if (!_hasReceivedState || string.IsNullOrEmpty(_targetBoatName)) return false;
            var boatSaveable = BoatUtility.FindBoatByName(_targetBoatName);
            if (boatSaveable == null) return false;
            var boat = boatSaveable.transform;
            var rb = boat.GetComponent<Rigidbody>();
            var offset = FloatingOriginManager.instance?.outCurrentOffset ?? Vector3.zero;

            boat.position = _targetRealPosition + offset;
            boat.rotation = _targetRotation;
            if (rb != null && !rb.isKinematic)
            {
                rb.velocity = _targetVelocity;
                rb.angularVelocity = _targetAngularVelocity;
            }
            // Prime cache + prev-frame values so the first post-join ApplyBoatTransform compares against THIS
            // snapped position (otherwise it would read a spurious one-frame apparent velocity from the old pos).
            _cachedBoatTransform = boat;
            _cachedBoatRb = rb;
            _prevBoatPosition = boat.position;
            _prevOffset = offset;
            _hasPrevValues = true;
            Plugin.Log.LogInfo($"[JOIN] Snapped boat '{boat.name}' to LIVE host transform before embark: realPos={_targetRealPosition}, vel={_targetVelocity.magnitude:F1}m/s");
            return true;
        }

        /// <summary>
        /// Reset sync state when disconnecting from multiplayer session.
        /// </summary>
        public void Reset()
        {
            _hasReceivedState = false;
            _lastSyncTime = 0f;
            _lastPacketTime = 0f;
            _lastPacketUnscaledTime = 0f;
            _snapOnNextApply = false;
            _targetBoatName = null;
            _targetVelocity = Vector3.zero;
            _targetAngularVelocity = Vector3.zero;
            IsJoinInProgress = false;

            // Clear cache
            _cachedBoatTransform = null;
            _cachedBoatRb = null;
            _cachedBoatSaveable = null;
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
