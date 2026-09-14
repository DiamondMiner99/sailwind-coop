using System.Collections.Generic;
using UnityEngine;
using Steamworks;
using SailwindCoop.Debug;
using SailwindPlayerModel;

namespace SailwindCoop.Player
{
    /// <summary>
    /// One networked crew member's visible avatar: capsule->humanoid body, name tag, the per-instance
    /// interpolation/animation state, and the bone refs that drive its procedural gait. The manager OWNS a
    /// dictionary of these (one per remote SteamId) and ticks each from LateUpdate. EVERY field that varies
    /// per crew member lives here - sharing any of these on the manager would make all avatars move/animate in
    /// lockstep (or T-pose).
    /// </summary>
    public class RemoteAvatar
    {
        private GameObject _remotePlayerObject;
        private SteamId _remotePlayerId;

        // Position interpolation
        private Vector3 _targetBoatRelativePos; // Boat-relative position (recalculated to world every frame)
        private Transform _targetBoat;          // Reference to boat for on-boat world position calculation
        private Vector3 _targetRealPosition;    // used for on-land case (offset-independent)
        private bool _remoteIsOnLand;           // Track if remote is on land vs on boat
        private Quaternion _targetRotation;
        private Vector3 _currentVelocity;
        private const float InterpolationSpeed = 15f;
        // On-boat interpolation kept as PERSISTENT deck-local state (not re-derived from world each frame),
        // so the velocity stays TRUE deck-relative instead of absorbing the boat's own motion.
        private Vector3 _smoothedLocalPos;
        private Vector3 _localVel;
        private Transform _smoothedLocalBoat;
        private float _animSpeedMps; // speed feeding the gait: deck-relative on a boat, world speed on land
        private GameObject _nameTagObject;
        private TextMesh _nameTag;

        // The visible humanoid body, built by the Sailwind Player Model mod. It owns the clone, the rig and
        // every pose written on it: the walk gait, the crouch squat and its leg IK, the torso look-lean and
        // the held-tool arm. This class feeds it three numbers and places its root - nothing about HOW a
        // crewmate moves lives here any more. That is the point: a crewmate's body and that same player's own
        // third-person body are now one piece of code, so they cannot drift apart the way two copies did.
        private SyntyBody _body;
        private bool _hasBody;
        private float _nextBodyRetry;

        // The name tag is a SIBLING of the body, so it does not inherit the crouch drop and used to hang at
        // standing height over a crouched crewmate. Placed once from the body's measured height, then kept in
        // step with SyntyBody.CrouchOffset every frame.
        private Vector3 _tagBaseLocalPos;
        private bool _tagPlaced;

        // Latest received crouch amount (0..1) and vertical look angle (degrees, positive = looking UP).
        // Both are eased inside SyntyBody, which is what lets a 20Hz byte-quantized stream look smooth.
        private float _targetCrouch01;
        private float _targetLookPitch;

        // Track which boat the remote player is on (null if on land)
        private Transform _currentBoat;
        // K-fix: last boat NAME the sender reported for this avatar ("" = on land / none). Used to detect
        // when the remote player walks off boat X onto boat Y (or to/from land) so the persistent deck-local
        // interpolation state can be reset and the capsule re-seeded in the NEW boat's frame, instead of
        // lingering with stale motion from the old boat.
        private string _lastBoatName = "";

        /// <summary>
        /// The boat this avatar is currently on, or null if on land.
        ///
        /// CAUTION: this is the boatMODEL child, not the boat root - FindBoatByName resolves through
        /// BoatRefs and returns refs.boatModel. Comparing it against a root transform (a BoatMass or
        /// SaveableObject transform, say) is always unequal, which is exactly how the host's remote crew
        /// weight silently did nothing for every published release. Compare CurrentBoatName instead.
        /// </summary>
        public Transform CurrentBoat => _currentBoat;

        /// <summary>
        /// (v0.3.0) The last boat-relative position received for this crewmate, straight off the wire and
        /// untouched: their FEET, in the VISUAL boat (boatModel) frame. That is the same frame and the same
        /// packet the avatar itself is placed from, so anything deriving physics from where a crewmate is
        /// standing should use this rather than re-deriving it from the avatar's world transform - the
        /// avatar has smoothing and a capsule-height offset applied on top.
        ///
        /// Add PlayerSyncManager.ControllerFeetGap() to reach the CONTROLLER ORIGIN, which is what vanilla's
        /// own centre-of-mass maths uses (Refs.observerMirror.transform.localPosition).
        /// </summary>
        public Vector3 BoatLocalFeetPosition => _targetBoatRelativePos;

        /// <summary>
        /// Root boat NAME the sender last reported ("" = on land / none). This is the sender's
        /// GameState.lastBoat.name (root SaveableObject name), i.e. the SAME key BoatTransformPacket and
        /// BoatUtility.FindBoatByName use - unlike CurrentBoat, which may resolve to the boatModel child.
        /// Used by the host's multi-boat transform streaming (BoatSyncManager) to find every boat that
        /// currently carries a remote crew member.
        /// </summary>
        public string CurrentBoatName => _lastBoatName;

        /// <summary>The SteamId this avatar represents.</summary>
        public SteamId PlayerId => _remotePlayerId;

        /// <summary>
        /// Time.unscaledTime of the most recent position packet received from THIS crew member.
        /// 0 until the first packet. Used by SleepSyncManager's guest-liveness watchdog to detect a
        /// frozen/hung crewmate that stops sending while it is still a connected peer (so the disconnect
        /// path never fires). Unscaled so a 16x sleep warp can't distort the staleness measurement.
        /// </summary>
        public float LastRemotePacketTime { get; private set; }

        /// <summary>
        /// True once this crew member has streamed at least ONE real position packet (i.e. finished loading
        /// into the world and PlayerSyncManager started its 20Hz position stream). False from spawn through
        /// the ENTIRE join/load window - including after NoteJoinStateSent re-baselines LastRemotePacketTime.
        /// The sleep quorum (AllCrewInBed/AllCrewRested) and the guest-liveness watchdog use this to EXCLUDE a
        /// still-loading joiner: a peer that has never streamed cannot have reported in-bed/rested and is not a
        /// "frozen" peer (it simply isn't present in the world yet), so it must neither gate the crew's sleep
        /// nor trip the unresponsive-crewmate abort. This is DELIBERATELY distinct from LastRemotePacketTime,
        /// which is baselined to a positive value at spawn (ctor) and at join-send (NoteJoinStateSent) so the
        /// watchdog's silence clock is well-defined - that positive baseline is exactly why LastRemotePacketTime
        /// cannot tell "loaded and streaming" from "spawned but never streamed" (gating the quorum on
        /// TryGetPeerSilence does not work: it returns true the instant the avatar exists).
        /// </summary>
        public bool HasStreamed { get; private set; }

        /// <summary>
        /// Re-baseline this avatar's liveness clock when the host finishes sending it a (possibly deferred)
        /// join state. The guest still won't stream position until its load completes, so this bounds the
        /// silence the sleep guest-liveness watchdog sees to the post-teleport load rather than the whole join.
        /// </summary>
        public void NoteJoinStateSent() => LastRemotePacketTime = Time.unscaledTime;

        /// <summary>
        /// Build the avatar (capsule placeholder + name tag) and attempt to upgrade it to a real humanoid body.
        /// If no shopkeeper is loaded yet (e.g. at sea), the capsule stays and Tick retries.
        /// </summary>
        public RemoteAvatar(SteamId playerId, string playerName)
        {
            _remotePlayerId = playerId;
            LastRemotePacketTime = Time.unscaledTime; // baseline so the liveness watchdog doesn't fire before the first packet

            // Create capsule placeholder
            _remotePlayerObject = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            _remotePlayerObject.name = $"RemotePlayer_{playerName}";

            // Remove collider to prevent physics interference
            var collider = _remotePlayerObject.GetComponent<Collider>();
            if (collider != null) Object.Destroy(collider);

            // Set material color (blue-ish) and disable occlusion culling
            var renderer = _remotePlayerObject.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.material = new Material(Shader.Find("Standard"));
                renderer.material.color = new Color(0.2f, 0.6f, 1f, 0.9f);
                renderer.allowOcclusionWhenDynamic = false;
            }

            // Scale to approximate player size
            _remotePlayerObject.transform.localScale = new Vector3(0.5f, 0.9f, 0.5f);

            // Layer 0 is Default
            _remotePlayerObject.layer = 0;

            // Create name tag
            _nameTagObject = new GameObject("NameTag");
            _nameTagObject.transform.SetParent(_remotePlayerObject.transform);
            _nameTagObject.transform.localPosition = new Vector3(0f, 1.2f, 0f);

            _nameTag = _nameTagObject.AddComponent<TextMesh>();
            _nameTag.text = playerName;
            _nameTag.fontSize = 32;
            _nameTag.characterSize = 0.04f;
            _nameTag.anchor = TextAnchor.MiddleCenter;
            _nameTag.alignment = TextAlignment.Center;
            _nameTag.color = Color.yellow;

            // Initialize at local player position (will be updated by network)
            if (Refs.charController != null)
            {
                _remotePlayerObject.transform.position = Refs.charController.transform.position + Vector3.right * 2f;
                // Initialize as on-land until first packet arrives
                _remoteIsOnLand = true;
                var offset = FloatingOriginManager.instance?.outCurrentOffset ?? Vector3.zero;
                _targetRealPosition = _remotePlayerObject.transform.position - offset;
            }

            // Try to upgrade the capsule placeholder to a real cloned humanoid body.
            // If no shopkeeper is loaded yet (e.g. at sea), the capsule stays and Tick retries.
            if (!TryAttachBody())
            {
                _nextBodyRetry = Time.time + 1.5f;
            }

            VerboseLogger.PlayerEvent($"Spawned remote player: {playerName}");
        }

        /// <summary>Update the displayed name (e.g. when a spawn is requested for an existing avatar).</summary>
        public void SetName(string playerName)
        {
            if (_remotePlayerObject != null)
                _remotePlayerObject.name = $"RemotePlayer_{playerName}";
            if (_nameTag != null)
                _nameTag.text = playerName;
        }

        // ---- Real humanoid body (cloned from a shopkeeper) ----------------------------------

        /// <summary>
        /// Re-dress this avatar in place after their appearance changed.
        ///
        /// Deliberately does NOT destroy and rebuild the body. Rebuilding is throttled to ~1.5s, so a
        /// crewmate would VANISH for over a second every time an appearance packet arrived - and since any
        /// peer can send those, that would be a way to keep the rest of the crew bodiless. It would also
        /// re-run the leg-IK bind capture, which is documented above as unsafe to repeat. Restyling in
        /// place is instant, allocation-free and leaves the rig bind untouched.
        /// </summary>
        public void RefreshAppearance()
        {
            if (_body != null) _body.RefreshAppearance(AppearanceRegistry.For(_remotePlayerId));
        }

        /// <summary>
        /// Replace the capsule placeholder with a real humanoid body. Returns false if no clonable humanoid
        /// is loaded yet (e.g. out at sea) - the capsule stays and Tick retries. Each avatar builds its OWN
        /// clone of the shared template.
        /// </summary>
        private bool TryAttachBody()
        {
            if (_hasBody || _remotePlayerObject == null) return false;
            if (!BodyTemplate.Available) return false;   // no shopkeeper loaded yet (e.g. out at sea)

            // Build the body fully BEFORE touching the capsule, so any failure leaves the capsule placeholder
            // intact and visible.
            //
            // The registry never fails to answer: a peer we have not heard an appearance from yet (older
            // build, packet not arrived, or a body built before it lands) resolves to a deterministic look
            // from their SteamId, so they are still an individual rather than a duplicate of the rest of the
            // crew. Because THIS is where appearance is read, and this builder retries lazily until a
            // template exists, a late-arriving appearance needs no re-send - it is picked up whenever the
            // body finally builds.
            //
            // DO NOT "fix" the -0.9 plant. It is NOT a claim about the world: it is one half of a matched
            // pair with the three `capsuleHalfHeight = 0.9f` sites that raise this receiver origin to
            // feet + 0.9. The two cancel, so the value is arbitrary and only has to agree with them -
            // changing it here alone would move every crewmate's body vertically.
            var body = SyntyBody.TryBuild(_remotePlayerObject.transform,
                $"crewmate {_remotePlayerId.Value}",
                AppearanceRegistry.For(_remotePlayerId),
                _remotePlayerObject.layer,
                () => -0.9f);
            if (body == null) return false;

            // ---- Commit: swap the capsule visual for the body, now that the body is ready ----
            var capMr = _remotePlayerObject.GetComponent<MeshRenderer>();
            if (capMr != null) Object.Destroy(capMr);
            var capMf = _remotePlayerObject.GetComponent<MeshFilter>();
            if (capMf != null) Object.Destroy(capMf);
            // Remove the capsule's non-uniform scale so the body isn't distorted.
            _remotePlayerObject.transform.localScale = Vector3.one;
            // The name tag was parented while the root was scaled (0.5,0.9,0.5), which baked a compensating
            // (2,1.11,2) localScale into it; reset that and lift it to head height until the fit reports a
            // real one.
            if (_nameTagObject != null)
            {
                _nameTagObject.transform.localScale = Vector3.one;
                _tagBaseLocalPos = new Vector3(0f, 2.0f, 0f);
                _nameTagObject.transform.localPosition = _tagBaseLocalPos;
            }
            _tagPlaced = false;

            _body = body;
            _hasBody = true;
            VerboseLogger.PlayerEvent("Attached humanoid body to remote player");
            return true;
        }

        /// <summary>
        /// Float the name tag just above the measured head, once the body's one-time fit has run. Deferred
        /// because the skinned bounds are not valid on the frame the clone is built.
        /// </summary>
        private void PlaceTagOnce()
        {
            if (_tagPlaced || _nameTagObject == null || _body == null || !_body.Fitted) return;
            _tagBaseLocalPos = new Vector3(0f, _body.FittedFeetLocalY + _body.MeasuredHeight + 0.25f, 0f);
            _nameTagObject.transform.localPosition = _tagBaseLocalPos;
            _tagPlaced = true;
        }

        // ---- Held tool (forwarded to the body) --------------------------------------------------

        /// <summary>
        /// Hand in this frame's pose for the item this crewmate holds. See
        /// <see cref="SyntyBody.SetHeldItemPose"/>; a no-op until they have a body.
        /// </summary>
        public void SetHeldItemPose(Transform item, Transform follower, Vector3 worldPos, Quaternion worldRot, bool big)
        {
            if (_body != null) _body.SetHeldItemPose(item, follower, worldPos, worldRot, big);
        }

        /// <summary>
        /// True when the avatar draws the held item itself (in its hand), so ItemSyncManager must NOT write
        /// the item's transform. False without a body, which is the case that matters: if this said yes while
        /// nothing posed the item, it would freeze in mid-air.
        /// </summary>
        public bool PlacesHeldItemInHand
        {
            get { return _body != null && _body.PlacesHeldItemInHand; }
        }

        /// <summary>Destroy this avatar's GameObject (and parented body + name tag) and clear its state.</summary>
        public void Destroy()
        {
            if (_remotePlayerObject != null)
            {
                Object.Destroy(_remotePlayerObject); // also destroys the parented body + name tag
                _remotePlayerObject = null;
                _remotePlayerId = default;
                _currentBoat = null;
                // Destroying the root already destroys the clone; this also drops any pose claims held on it
                // and tells their owners, which a bare Object.Destroy would not.
                if (_body != null) _body.Destroy();
                _body = null;
                _hasBody = false;
                _tagPlaced = false;
                _smoothedLocalBoat = null;
                _localVel = Vector3.zero;
                _animSpeedMps = 0f;
                _targetCrouch01 = 0f;
                _targetLookPitch = 0f;   // LOOK-LEAN: clear so a reused/rebuilt avatar starts neutral
                VerboseLogger.PlayerEvent("Despawned remote player");
            }
        }

        /// <summary>
        /// Gets the transform of this avatar's capsule for item following.
        /// Returns null if the avatar has been destroyed.
        /// </summary>
        public Transform GetRemoteCapsule()
        {
            return _remotePlayerObject?.transform;
        }

        /// <summary>
        /// Gets the last known world position of this avatar.
        /// Used for dropping items when a guest disconnects.
        /// </summary>
        public Vector3 GetLastKnownPosition()
        {
            if (_remotePlayerObject != null)
            {
                return _remotePlayerObject.transform.position;
            }
            return Vector3.zero;
        }

        /// <summary>
        /// Apply a received position/rotation update to this avatar (the body of the old
        /// UpdateRemotePosition). World position is recalculated every frame in Tick.
        /// </summary>
        public void UpdatePosition(Vector3 boatRelativePos, Quaternion rotation, bool isOnBoat, string boatName = "", float crouch01 = 0f, float lookPitchDeg = 0f)
        {
            LastRemotePacketTime = Time.unscaledTime; // guest-liveness heartbeat (see SleepSyncManager watchdog)
            HasStreamed = true; // first real position packet => this peer is loaded/live (see HasStreamed doc)
            VerboseLogger.PlayerRecv($"Position, relPos={boatRelativePos}, onBoat={isOnBoat}, boat={boatName}", throttle: true);

            _targetRotation = rotation;
            // Finite-guard the pose targets: Mathf.Clamp/Clamp01 pass NaN through, and Lerp toward a NaN
            // target permanently poisons the smoother (NaN bone rotations / body position). The byte-quantized
            // wire can't encode NaN today, so this is belt-and-suspenders against any future non-byte sender.
            _targetCrouch01 = float.IsNaN(crouch01) || float.IsInfinity(crouch01) ? 0f : Mathf.Clamp01(crouch01);
            _targetLookPitch = float.IsNaN(lookPitchDeg) || float.IsInfinity(lookPitchDeg) ? 0f : lookPitchDeg;
            if (_remotePlayerObject == null) return;

            // Check if LOCAL player is on a boat
            var localPlayerParent = Refs.charController?.transform.parent;
            bool localPlayerOnBoat = localPlayerParent != null && localPlayerParent.name != "_shifting world";

            // K-fix: the boat the remote is on this packet ("" when on land). When this differs from the
            // last packet's boat, the remote walked between boats (or boat<->land), so we must drop the
            // stale deck-local interpolation state before applying the new frame's coords.
            string newBoatName = isOnBoat ? (boatName ?? "") : "";
            bool boatChanged = newBoatName != _lastBoatName;

            if (isOnBoat)
            {
                // Store boat-relative position - world position recalculated every frame in Tick()
                // This prevents capsule "sliding" when boat moves between packets
                _remoteIsOnLand = false;

                // Unparent if was parented (the capsule follows the boat by per-frame TransformPoint, not parenting)
                if (_remotePlayerObject.transform.parent != null)
                    _remotePlayerObject.transform.SetParent(null, worldPositionStays: true);

                // K-fix AVATAR-BOAT-BY-NAME (strict): resolve the remote's boat SOLELY from the packet's
                // authoritative boatName via FindBoatByName, which routes through BoatRefs.boatModel (the SAME
                // frame the sender encoded its boat-relative coords against). We deliberately DROP the old
                // camera-distance gate (FindBoatNearCamera's ~100m) and the nearest-embark-collider fallback:
                // those placed the avatar on whatever hull was closest to THIS observer, so a far viewer drew
                // the crewmate ghosted onto the wrong deck. A strict name match places it on the correct boat
                // regardless of how far the observer is.
                var named = RemotePlayerManager.FindBoatByName(boatName);
                _targetBoatRelativePos = boatRelativePos;
                if (named != null)
                {
                    _targetBoat = named;
                    _currentBoat = named;
                }
                else
                {
                    // K-fix TRANSIENT-MISS: the packet says the remote IS on a boat, but FindBoatByName
                    // returned null (boat not yet in the lookup right at join, or a momentary name-resolution
                    // gap). Do NOT null _targetBoat - that would drop the boat-LOCAL coords into Tick's raw
                    // world-position fallback and yank the avatar to near world/FOM origin for the interval.
                    // Instead RETAIN the last successfully-resolved boat so the avatar stays put on its last
                    // known deck until the name resolves again. The genuine boat-CHANGE reset below still
                    // fires on a different non-null boat name or an actual off-boat transition; a transient
                    // miss with the SAME name leaves boatChanged false, so nothing is reset here either.
                    Plugin.Log.LogWarning($"[ON-BOAT] Could not resolve boat '{boatName}' by name (transient); keeping last known boat '{(_targetBoat != null ? _targetBoat.name : "<none>")}'.");
                }
            }
            else
            {
                // Remote on land
                _currentBoat = null;
                _remoteIsOnLand = true;

                // Unparent capsule when leaving "both on boat" state
                if (_remotePlayerObject.transform.parent != null)
                    _remotePlayerObject.transform.SetParent(null, worldPositionStays: true);

                // Remote sent REAL (offset-independent) position;
                // store it and calculate the local position on-demand in Tick
                const float capsuleHalfHeight = 0.9f;
                _targetRealPosition = boatRelativePos + new Vector3(0, capsuleHalfHeight, 0);
                VerboseLogger.PlayerApply($"Remote on land - realPos: {_targetRealPosition}");
            }

            // K-fix: on a boat change, reset the deck-local smoothing so the capsule doesn't linger on the old
            // boat or carry the old boat's motion into the new frame. Forcing _smoothedLocalBoat = null makes
            // Tick re-seed _smoothedLocalPos from the avatar's current world position in the NEW boat's frame
            // and zero _localVel, exactly as it does on a first board.
            if (boatChanged)
            {
                ResetBoatInterpolation();
                _lastBoatName = newBoatName;
            }
        }

        /// <summary>
        /// K-fix: drop all persistent deck-local interpolation/smoothing state and unparent the capsule so a
        /// boat change (or boat&lt;-&gt;land transition) starts clean. Tick re-seeds the deck-local position in
        /// the new boat's frame on the next frame (because _smoothedLocalBoat no longer matches _targetBoat).
        /// </summary>
        private void ResetBoatInterpolation()
        {
            if (_remotePlayerObject != null && _remotePlayerObject.transform.parent != null)
                _remotePlayerObject.transform.SetParent(null, worldPositionStays: true);
            _smoothedLocalBoat = null;   // force Tick to re-seed _smoothedLocalPos in the new boat frame
            _localVel = Vector3.zero;    // drop old-boat deck-relative velocity
            _currentVelocity = Vector3.zero; // drop world-space velocity (land/fallback paths)
            _animSpeedMps = 0f;          // don't carry old motion into the gait
        }

        /// <summary>
        /// Ensure capsule is parented to _shifting world so it shifts with FloatingOriginManager.
        /// </summary>
        private void EnsureParentedToShiftingWorld()
        {
            if (_remotePlayerObject == null) return;

            // Find _shifting world (the root object that shifts with FloatingOriginManager)
            var shiftingWorld = GameObject.Find("_shifting world");
            if (shiftingWorld != null && _remotePlayerObject.transform.parent != shiftingWorld.transform)
            {
                // Parent while keeping world position
                _remotePlayerObject.transform.SetParent(shiftingWorld.transform, worldPositionStays: true);
                VerboseLogger.PlayerApply($"Parented capsule to _shifting world");
            }
        }

        /// <summary>
        /// Advance this avatar one frame: body upgrade retry, position/rotation interpolation,
        /// name-tag billboard, body fit, and the procedural gait. This is the body of the old
        /// per-frame LateUpdate, for a single avatar. The Profiler wrapping is done by the manager.
        /// </summary>
        public void Tick()
        {
            // Run in LateUpdate to ensure boat position is updated first (BoatSyncManager uses Update)
            if (_remotePlayerObject == null) return;

            // Upgrade capsule -> real body once a shopkeeper becomes available (e.g. after reaching a port).
            if (!_hasBody && Time.time >= _nextBodyRetry)
            {
                _nextBodyRetry = Time.time + 1.5f;
                TryAttachBody();
            }

            // Interpolate position - use appropriate coordinate space for each case
            if (_remoteIsOnLand)
            {
                // On land: interpolate in world space with FOM offset
                var offset = FloatingOriginManager.instance?.outCurrentOffset ?? Vector3.zero;
                var targetPos = _targetRealPosition + offset;
                _remotePlayerObject.transform.position = Vector3.SmoothDamp(
                    _remotePlayerObject.transform.position,
                    targetPos,
                    ref _currentVelocity,
                    1f / InterpolationSpeed
                );
                _animSpeedMps = _currentVelocity.magnitude; // world speed = walking speed on land
                _smoothedLocalBoat = null; // re-init deck-local state when they next board
            }
            else if (_targetBoat != null)
            {
                // On boat: interpolate in BOAT-LOCAL space, kept as PERSISTENT state. Do NOT re-derive the
                // current local pos from the world position each frame (InverseTransformPoint of a world
                // point): the body isn't parented to the boat, so as the boat moves the world point falls
                // behind and the round-trip injects the boat's OWN motion into the velocity - which made the
                // gait "walk" the whole time the boat sailed. Tracking _smoothedLocalPos directly keeps
                // _localVel true deck-relative (≈0 when standing still on a moving deck) and removes chase-lag.
                const float capsuleHalfHeight = 0.9f;
                var targetLocalPos = _targetBoatRelativePos + new Vector3(0, capsuleHalfHeight, 0);
                if (_smoothedLocalBoat != _targetBoat)
                {
                    _smoothedLocalPos = _targetBoat.InverseTransformPoint(_remotePlayerObject.transform.position);
                    _localVel = Vector3.zero;
                    _smoothedLocalBoat = _targetBoat;
                }
                _smoothedLocalPos = Vector3.SmoothDamp(_smoothedLocalPos, targetLocalPos, ref _localVel, 1f / InterpolationSpeed);
                _remotePlayerObject.transform.position = _targetBoat.TransformPoint(_smoothedLocalPos);
                _animSpeedMps = _localVel.magnitude; // deck-relative speed; boat motion no longer counts
            }
            else
            {
                // Fallback: no boat reference yet, use raw position. We can't separate boat motion here,
                // so don't drive the walk (a static body beats phantom-walking while sailing).
                const float capsuleHalfHeight = 0.9f;
                var targetPos = _targetBoatRelativePos + new Vector3(0, capsuleHalfHeight, 0);
                _remotePlayerObject.transform.position = Vector3.SmoothDamp(
                    _remotePlayerObject.transform.position,
                    targetPos,
                    ref _currentVelocity,
                    1f / InterpolationSpeed
                );
                _smoothedLocalBoat = null;
                _animSpeedMps = 0f;
            }

            // Extract yaw-only rotation (ignore pitch/roll so capsule stays vertical)
            // Camera pitch makes player "look down" but shouldn't tilt the capsule
            var eulerAngles = _targetRotation.eulerAngles;
            var yawOnlyRotation = Quaternion.Euler(0, eulerAngles.y, 0);

            _remotePlayerObject.transform.rotation = Quaternion.Slerp(
                _remotePlayerObject.transform.rotation,
                yawOnlyRotation,
                Time.deltaTime * InterpolationSpeed
            );

            // Billboard: make name tag face camera
            if (_nameTagObject != null && Camera.main != null)
            {
                _nameTagObject.transform.rotation = Camera.main.transform.rotation;
            }

            // Feed the body what it animates from and let it pose itself. _animSpeedMps is deck-relative on
            // a boat (world speed on land), set above from the persistent deck-local interpolation, so
            // sailing motion does NOT read as walking.
            if (_hasBody && _body != null)
            {
                _body.SpeedMps = _animSpeedMps;
                _body.Crouch01Target = _targetCrouch01;
                _body.LookPitchDegTarget = _targetLookPitch;
                _body.Tick(Time.deltaTime);

                PlaceTagOnce();
                // The tag is a sibling of the body, so it has to follow the crouch drop by hand or it hangs
                // at standing height over a crouched crewmate.
                if (_nameTagObject != null && _tagPlaced)
                    _nameTagObject.transform.localPosition = _tagBaseLocalPos - _body.CrouchOffset;
            }
        }

    }

    public class RemotePlayerManager : MonoBehaviour
    {
        public static RemotePlayerManager Instance { get; private set; }

        // One avatar per remote crew member, keyed by their SteamId.
        private readonly Dictionary<SteamId, RemoteAvatar> _avatars = new Dictionary<SteamId, RemoteAvatar>();
        // Reused snapshot so LateUpdate is safe if a peer joins/leaves mid-frame (no alloc per frame).
        private readonly List<RemoteAvatar> _tickSnapshot = new List<RemoteAvatar>();

        /// <summary>True if at least one remote crew member's avatar is spawned.</summary>
        public bool HasRemotePlayer => _avatars.Count > 0;

        /// <summary>Number of remote crew avatars currently spawned.</summary>
        public int RemotePlayerCount => _avatars.Count;

        /// <summary>All spawned remote avatars.</summary>
        public IEnumerable<RemoteAvatar> Avatars => _avatars.Values;

        // The shared humanoid template (cloned from a shopkeeper, stripped, cached across scene unloads)
        // lives in the Sailwind Player Model mod now - see SailwindPlayerModel.BodyTemplate. So do the bone
        // helpers and the whole procedural pose. Co-op keeps what is actually co-op's: who is where, on which
        // boat, and how to interpolate between packets.

        private void Awake()
        {
            if (Instance != null)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }

        /// <summary>
        /// Spawn (or, if already present, update the name of) the avatar for a remote crew member.
        /// N-player: every member gets its OWN avatar - there is no single-avatar early-return.
        /// </summary>
        public void SpawnRemotePlayer(SteamId playerId, string playerName)
        {
            if (_avatars.TryGetValue(playerId, out var existing))
            {
                // Already spawned for this peer - just refresh the name (no duplicate avatar).
                existing.SetName(playerName);
                return;
            }

            _avatars[playerId] = new RemoteAvatar(playerId, playerName);
        }

        /// <summary>Despawn the avatar for one specific crew member.</summary>
        public void DespawnRemotePlayer(SteamId playerId)
        {
            if (_avatars.TryGetValue(playerId, out var avatar))
            {
                avatar.Destroy();
                _avatars.Remove(playerId);
            }
        }

        /// <summary>Despawn every avatar (teardown).</summary>
        public void DespawnAll()
        {
            foreach (var avatar in _avatars.Values)
            {
                avatar.Destroy();
            }
            _avatars.Clear();
        }

        /// <summary>Get the avatar for a crew member, or null if not spawned.</summary>
        /// <summary>(v0.3.0) Rebuild one crew member's body so a newly-received appearance shows. No-op if
        /// we have no avatar for them yet - their body will simply be built with the new look when it
        /// first appears, which is the same outcome by a shorter route.</summary>
        public void RefreshAppearance(ulong playerId)
        {
            RemoteAvatar avatar;
            if (_avatars.TryGetValue(playerId, out avatar)) avatar?.RefreshAppearance();
        }

        public RemoteAvatar GetAvatar(SteamId playerId)
        {
            return _avatars.TryGetValue(playerId, out var avatar) ? avatar : null;
        }

        /// <summary>
        /// Re-baseline one crew member's liveness clock (call after the host sends THEM a deferred join
        /// state). Per-peer so a single rejoining guest's load silence doesn't reset everyone's clock.
        /// </summary>
        public void NoteJoinStateSent(SteamId playerId)
        {
            if (_avatars.TryGetValue(playerId, out var avatar)) avatar.NoteJoinStateSent();
        }

        /// <summary>
        /// Unscaled seconds since this crew member last sent a position packet. Returns false if no avatar
        /// exists for the id yet, or it has never sent a packet (LastRemotePacketTime == 0). Used by the
        /// sleep guest-liveness watchdog to spot a specific frozen crewmate while it is still a connected peer.
        /// </summary>
        public bool TryGetPeerSilence(SteamId playerId, out float silence)
        {
            silence = 0f;
            if (_avatars.TryGetValue(playerId, out var avatar) && avatar.LastRemotePacketTime > 0f)
            {
                silence = Time.unscaledTime - avatar.LastRemotePacketTime;
                return true;
            }
            return false;
        }

        /// <summary>
        /// True once the given crew member has streamed at least one real position packet (i.e. finished
        /// loading and is live). False if no avatar exists yet OR the avatar exists but has never streamed
        /// (still loading after an awake-host or deferred join). The sleep quorum and liveness watchdog use
        /// this to exclude a still-loading joiner. See RemoteAvatar.HasStreamed for why LastRemotePacketTime
        /// (which is baselined positive at spawn) cannot serve this role.
        /// </summary>
        public bool HasStreamedPacket(SteamId playerId)
        {
            return _avatars.TryGetValue(playerId, out var avatar) && avatar.HasStreamed;
        }

        /// <summary>
        /// Route a received position update to the matching avatar. If no avatar exists yet for this
        /// id (a position arrived before the spawn event), create one on demand with a placeholder name.
        /// </summary>
        public void UpdateRemotePosition(SteamId playerId, Vector3 boatRelativePos, Quaternion rotation, bool isOnBoat, string boatName = "", float crouch01 = 0f, float lookPitchDeg = 0f)
        {
            if (!_avatars.TryGetValue(playerId, out var avatar))
            {
                // Create-on-demand: a position packet beat the join/spawn. Use a sensible placeholder name.
                avatar = new RemoteAvatar(playerId, "Crewmate");
                _avatars[playerId] = avatar;
            }
            avatar.UpdatePosition(boatRelativePos, rotation, isOnBoat, boatName, crouch01, lookPitchDeg);
        }

        /// <summary>
        /// Last known world position for one crew member (used for dropping items on disconnect).
        /// Returns Vector3.zero if no such avatar.
        /// </summary>
        public Vector3 GetLastKnownPosition(SteamId playerId)
        {
            return _avatars.TryGetValue(playerId, out var avatar) ? avatar.GetLastKnownPosition() : Vector3.zero;
        }

        private void LateUpdate()
        {
            // Run in LateUpdate to ensure boat position is updated first (BoatSyncManager uses Update)
            if (_avatars.Count == 0) return;

            Plugin.Profiler?.StartMeasure();

            // Iterate a snapshot so a peer joining/leaving mid-tick (avatar dict mutated) can't throw.
            _tickSnapshot.Clear();
            _tickSnapshot.AddRange(_avatars.Values);
            for (int i = 0; i < _tickSnapshot.Count; i++)
            {
                // Per-avatar guard: a throw in one avatar's Tick (procedural pose / IK on a transient bad
                // transform) must not abort the rest of the crew's tick or skip EndMeasurePlayerSync.
                try { _tickSnapshot[i].Tick(); }
                catch (System.Exception e) { Plugin.Log.LogError($"[RemotePlayer] Tick failed for one avatar: {e}"); }
            }

            Plugin.Profiler?.EndMeasurePlayerSync();
        }

        // ---- Boat-finding helpers (stateless utilities, shared) ----------------------------------

        /// <summary>
        /// Find boat by name - searches for the shifted visual boat that matches the sender's parent.
        /// </summary>
        internal static Transform FindBoatByName(string boatName)
        {
            if (string.IsNullOrEmpty(boatName))
            {
                return GameState.lastBoat;
            }

            // K-fix STRICT FRAME: resolve the VISUAL hull (boatModel) by name via BoatRefs - this is the SAME
            // frame the sender encoded its boat-relative coords against (sender uses GameState.currentBoat =
            // BoatRefs.boatModel). A raw GameObject.Find(boatName) can return a same-named WALK-COLLIDER subtree
            // authored ~205m up in the physics frame, and the player-parent / lastBoat fallbacks return the
            // saveable ROOT (boatModel's PARENT), not the model itself - any of those would apply the coords in
            // the wrong frame. Resolve boatModel directly and distance-independently so a far observer still
            // places the avatar on the correct deck.
            var saveable = SailwindCoop.Sync.BoatUtility.FindBoatByName(boatName);
            if (saveable != null)
            {
                var refs = saveable.GetComponent<BoatRefs>();
                if (refs != null && refs.boatModel != null)
                {
                    VerboseLogger.PlayerApply($"FindBoatByName: resolved '{boatName}' to boatModel via BoatRefs");
                    return refs.boatModel;
                }
            }

            // BOAT-NAME FRAME fallback: the primary lookup keys BoatUtility._cachedBoats by the
            // boat ROOT SaveableObject name. If it MISSED, the sender may still be encoding boatModel-LOCAL coords
            // but have labelled the packet with the boatMODEL name (an old/stale sender build). Scan every boat and
            // match boatName against BoatRefs.boatModel.name, returning that same boatModel. This resolves the
            // correct visual frame for EITHER a root-name (current sender) or a boatModel-name (old sender) BEFORE
            // any wrong-frame fallback below. We deliberately do NOT reinstate the nearest-embark-collider-to-viewer
            // fallback (the original K ghosting bug) and never return the walkCol (~205m up) or the saveable root
            // for boatModel-local coords.
            foreach (var kvp in SailwindCoop.Sync.BoatUtility.FindAllBoats())
            {
                var refs = kvp.Value != null ? kvp.Value.GetComponent<BoatRefs>() : null;
                if (refs != null && refs.boatModel != null && refs.boatModel.name == boatName)
                {
                    VerboseLogger.PlayerApply($"FindBoatByName: resolved '{boatName}' via boatModel-name scan (root '{kvp.Key}')");
                    return refs.boatModel;
                }
            }

            // First check if local player is on a boat with matching name
            var charController = Refs.charController;
            if (charController != null)
            {
                var playerParent = charController.transform.parent;
                if (playerParent != null && playerParent.name == boatName)
                {
                    // Local player is on the same boat - use their parent (most reliable)
                    return playerParent;
                }
            }

            // Search for the boat by exact name
            var found = GameObject.Find(boatName);
            if (found != null)
            {
                // Verify it's the shifted one (near the player)
                var playerPos = charController?.transform.position ?? Vector3.zero;
                var distance = Vector3.Distance(found.transform.position, playerPos);
                if (distance < 500f)
                {
                    VerboseLogger.PlayerApply($"Found boat by name: {boatName} at {found.transform.position}, dist={distance:F1}");
                    return found.transform;
                }
                else
                {
                    VerboseLogger.PlayerApply($"Found boat {boatName} but too far ({distance:F1}m), searching for shifted version");
                }
            }

            // If exact name not found or too far, the sender might be using a different naming
            // Try to find any boat visual near the player
            if (charController != null)
            {
                var playerParent = charController.transform.parent;
                if (playerParent != null && playerParent.name != "_shifting world")
                {
                    // Local player is on a boat - use that (both players should be on same boat)
                    VerboseLogger.PlayerApply($"Using local player's boat parent: {playerParent.name}");
                    return playerParent;
                }
            }

            // Last resort: fall back to GameState.lastBoat
            VerboseLogger.PlayerApply($"Could not find boat '{boatName}', using lastBoat as fallback");
            return GameState.lastBoat;
        }

        /// <summary>
        /// Find boat by name using CAMERA position for distance check (visual space).
        /// Used when local player is on boat and needs to find the visual representation.
        /// </summary>
        internal static Transform FindBoatNearCamera(string boatName)
        {
            if (string.IsNullOrEmpty(boatName) || Camera.main == null)
                return null;

            var camPos = Camera.main.transform.position;

            // Resolve the VISUAL hull (boatModel) via BoatRefs, NOT GameObject.Find(boatName): a boat has a
            // same-named WALK COLLIDER subtree authored ~205m up in the physics frame, and Find returns the
            // FIRST match - often that raised collider. That then fails the distance gate, blanks the on-boat
            // detection, and spams "[ON-BOAT] No named boat...". Going through BoatRefs.boatModel always gives
            // the sea-level visual hull the host encoded against.
            Transform visual = null;
            var saveable = SailwindCoop.Sync.BoatUtility.FindBoatByName(boatName);
            if (saveable != null)
            {
                var refs = saveable.GetComponent<BoatRefs>();
                if (refs != null && refs.boatModel != null) visual = refs.boatModel;
            }
            if (visual == null) // backstop: keep the old name lookup if BoatRefs didn't resolve
            {
                var found = GameObject.Find(boatName);
                if (found != null) visual = found.transform;
            }
            if (visual == null) return null;

            var distance = Vector3.Distance(visual.position, camPos);
            if (distance < 100f) // Tighter threshold since we're in visual space
            {
                VerboseLogger.PlayerApply($"FindBoatNearCamera: found {boatName} (boatModel) at {visual.position}, dist={distance:F1}m from camera");
                return visual;
            }
            VerboseLogger.PlayerApply($"FindBoatNearCamera: {boatName} at {visual.position} too far from camera ({distance:F1}m)");
            return null;
        }

        /// <summary>
        /// Find BoatEmbarkCollider near the camera - the canonical way to get walk surface.
        /// </summary>
        internal static BoatEmbarkCollider FindBoatEmbarkColliderNearCamera()
        {
            if (Camera.main == null) return null;

            var camPos = Camera.main.transform.position;
            var allEmbarkColliders = Object.FindObjectsOfType<BoatEmbarkCollider>();

            BoatEmbarkCollider nearest = null;
            float nearestDist = float.MaxValue;

            foreach (var collider in allEmbarkColliders)
            {
                var dist = Vector3.Distance(collider.transform.position, camPos);
                if (dist < nearestDist && dist < 200f) // Within reasonable range
                {
                    nearestDist = dist;
                    nearest = collider;
                }
            }

            return nearest;
        }

        /// <summary>
        /// Search a boat's descendants for the walk surface transform.
        /// Walk surfaces have "walk" in their name (case insensitive) and rotation 0.
        /// </summary>
        internal static Transform FindWalkSurfaceInDescendants(Transform boat)
        {
            if (boat == null) return null;

            // Get all descendants
            var allChildren = boat.GetComponentsInChildren<Transform>(includeInactive: true);

            Transform bestMatch = null;
            foreach (var child in allChildren)
            {
                var nameLower = child.name.ToLower();
                if (nameLower.Contains("walk"))
                {
                    // Prefer transforms with rotation close to 0 (walk surfaces are world-axis-aligned)
                    var rotY = child.rotation.eulerAngles.y;
                    if (rotY < 1f || rotY > 359f) // Close to 0
                    {
                        // This is likely the walk surface
                        Plugin.Log.LogInfo($"[FIND-WALK] Found '{child.name}' with rotY={rotY:F1}° (good match)");
                        return child;
                    }
                    else if (bestMatch == null)
                    {
                        // Keep as fallback if no rotation-0 walk surface found
                        bestMatch = child;
                        Plugin.Log.LogInfo($"[FIND-WALK] Found '{child.name}' with rotY={rotY:F1}° (fallback)");
                    }
                }
            }

            return bestMatch;
        }
    }
}
