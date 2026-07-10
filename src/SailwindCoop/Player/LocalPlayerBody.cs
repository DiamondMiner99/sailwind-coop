using UnityEngine;
using Steamworks;
using HarmonyLib;
using SailwindCoop.Debug;

namespace SailwindCoop.Player
{
    /// <summary>
    /// Shows YOUR OWN humanoid body in third person (Sailwind's local player is bodyless). A clone of the
    /// shared body template (built from a shopkeeper) is placed at your feet, animated from your local
    /// movement, with your name tag - and HIDDEN in first person so it never clips the camera. Shown only when
    /// the ship-orbit camera (BoatCamera.on) is active - the only zoom-out view while aboard. Local-only and
    /// non-networked: the remote player still sees you via their own RemotePlayerManager avatar.
    /// </summary>
    public class LocalPlayerBody : MonoBehaviour
    {
        private GameObject _root;   // re-positioned each frame at the player's feet + yaw
        private GameObject _body;   // cloned humanoid, child of _root
        private Renderer[] _renderers;
        private GameObject _nameTagObject;
        private bool _has;
        private bool _needsFit;
        private float _nextRetry;
        private bool _visible = true;

        // Gait driven by deck-relative speed (the controller's local-position delta).
        private Vector3 _prevLocalPos;
        private bool _havePrev;
        private Transform _prevParent; // detect reparent (board/disembark/teleport) to rebaseline the gait
        private float _animSpeed;
        private float _gaitPhase;

        // Synty rig bones + bind rotations (same convention as RemotePlayerManager).
        private bool _rigReady;
        private Transform _bSpine, _bUpperLegL, _bUpperLegR, _bLowerLegL, _bLowerLegR, _bShoulderL, _bShoulderR, _bElbowL, _bElbowR;
        private Quaternion _qSpine, _qUpperLegL, _qUpperLegR, _qLowerLegL, _qLowerLegR, _qShoulderL, _qShoulderR, _qElbowL, _qElbowR;

        // Crouch (v0.2.25): this is the LOCAL player's own body, so crouch is read DIRECTLY from the vanilla
        // PlayerCrouching head-height lerp (no network) and eased into the same crouch-walk pose the remote
        // avatars use. _bodyBaseLocalPos is the body local position after the one-time feet-on-deck fit, so the
        // per-frame height drop is applied relative to it. Kept in sync with RemotePlayerManager's crouch math.
        private PlayerCrouching _crouching;
        private float _crouchStandH = -1f;
        private float _crouch01;
        private Vector3 _bodyBaseLocalPos;
        private bool _hasBodyBase;

        // LOOK-LEAN (local body): the vertical look pitch is read DIRECTLY from the vanilla MouseLook.rotationY
        // private field (no network) and eased into a torso pitch on the hips - same math as the remote avatars.
        // Cached ref-accessor + instance lookup; positive rotationY = looking UP. See SampleLocalLookPitchDeg.
        private static readonly AccessTools.FieldRef<MouseLook, float> MouseLookRotationYRef =
            AccessTools.FieldRefAccess<MouseLook, float>("rotationY");
        private MouseLook[] _cachedMouseLooks;
        // (v0.2.25) empty-scan throttle: earliest realtime a missed MouseLook re-scan may run again.
        private float _nextMouseLookScanTime;
        private const float MouseLookRescanInterval = 1.5f;
        private float _lookPitch;

        // Crouch leg IK (v0.2.25 squat) - identical math to RemotePlayerManager.RemoteAvatar. The body drop
        // lowers the hips, then per-leg 2-bone IK re-plants each ankle at its captured STANDING world target so
        // the feet stay on the deck at any depth. Bind data captured at fit time; aiming is axis-agnostic.
        private Transform _bFootL, _bFootR;
        private bool _legIkReady;
        private float _thighLenL, _shinLenL, _thighLenR, _shinLenR;
        private Vector3 _footLocalL, _footLocalR;
        private Vector3 _thighAimLocalL, _shinAimLocalL, _thighAimLocalR, _shinAimLocalR;

        private void LateUpdate()
        {
            try
            {
                if (!GameState.playing) { Teardown(); return; }
                var cc = Refs.charController;
                if (cc == null) return;

                if (!_has)
                {
                    if (Time.time < _nextRetry) return;
                    _nextRetry = Time.time + 1.5f;
                    TryBuild();
                    if (!_has) return;
                }

                // Follow the player position, facing the controller's yaw (NOT camera pitch/orbit).
                // Aboard a boat the CharacterController lives in PHYSICS space (~Y200 underway)
                // while the orbit camera and the deck render in VISUAL space, so positioning the body from the
                // controller would put it far off-screen in the ship-orbit cam. Source
                // from Refs.observerMirror (the visual-frame controller mirror) when aboard - the same visual
                // frame PlayerSyncManager/BoatStateCollector use and the same frame the orbit camera renders. On
                // land the two frames coincide, so keep the controller (proven, no regression).
                var observer = Refs.observerMirror != null ? Refs.observerMirror.transform : null;
                bool onBoatVisual = cc.transform.parent != null && cc.transform.parent.name != "_shifting world"
                                    && GameState.currentBoat != null && observer != null;
                if (onBoatVisual)
                {
                    _root.transform.position = observer.position;
                    _root.transform.rotation = Quaternion.Euler(0f, observer.eulerAngles.y, 0f);
                }
                else
                {
                    _root.transform.position = cc.transform.position;
                    _root.transform.rotation = Quaternion.Euler(0f, cc.transform.eulerAngles.y, 0f);
                }

                // Deck-relative gait speed: delta of the controller's LOCAL position (excludes boat motion when
                // parented to a boat). Ignore huge deltas (floating-origin shifts / teleports).
                // localPosition is relative to the CURRENT parent; a reparent (boarding/disembarking, join or
                // sleep/recovery teleport, floating-origin) remaps the same world spot to a different
                // localPosition, injecting a spurious gait blip. Rebaseline on a parent change instead.
                var par = cc.transform.parent;
                if (par != _prevParent) { _havePrev = false; _prevParent = par; }
                Vector3 lp = cc.transform.localPosition;
                if (_havePrev)
                {
                    float dt = Mathf.Max(Time.deltaTime, 1e-4f);
                    float inst = (lp - _prevLocalPos).magnitude / dt;
                    if (inst > 15f) inst = 0f; // FOM shift / teleport, not walking
                    _animSpeed = Mathf.Lerp(_animSpeed, inst, 1f - Mathf.Exp(-8f * dt));
                }
                _prevLocalPos = lp; _havePrev = true;

                if (_needsFit) FitBodyAndTag();
                DriveAnimation();

                // Show the body only when the ship-orbit ("third person") camera is on - the only zoom-out
                // view Sailwind has while aboard a boat. Hidden in first person so it never clips the camera,
                // and in the shipyard (orbit cam is also on there) so it doesn't float over the edited ship.
                // (A camera-distance heuristic would mis-fire during falls/fast moves and pop the body in.)
                SetVisible(BoatCamera.on && GameState.currentShipyard == null);

                if (_visible && _nameTagObject != null && Camera.main != null)
                    _nameTagObject.transform.rotation = Camera.main.transform.rotation;
            }
            catch (System.Exception e) { Plugin.Log.LogError($"[LocalBody] {e}"); }
        }

        private void TryBuild()
        {
            var template = RemotePlayerManager.GetBodyTemplate();
            if (template == null) return; // no shopkeeper loaded yet (e.g. out at sea)

            _root = new GameObject("LocalPlayerBodyRoot");
            DontDestroyOnLoad(_root);

            _body = Instantiate(template);
            _body.name = "LocalBody";
            foreach (var sk in _body.GetComponentsInChildren<Shopkeeper>(true)) { sk.enabled = false; Destroy(sk); }
            foreach (var col in _body.GetComponentsInChildren<Collider>(true)) { col.enabled = false; Destroy(col); }
            foreach (var rb in _body.GetComponentsInChildren<Rigidbody>(true)) Destroy(rb);
            _body.transform.SetParent(_root.transform, false);
            _body.transform.localRotation = Quaternion.identity;
            _body.transform.localPosition = new Vector3(0f, -0.9f, 0f);
            _body.SetActive(true);
            foreach (var smr in _body.GetComponentsInChildren<SkinnedMeshRenderer>(true)) { smr.enabled = true; smr.allowOcclusionWhenDynamic = false; }
            _renderers = _body.GetComponentsInChildren<Renderer>(true);
            // Put the clone on the Default layer (0) the orbit camera renders - the visible remote
            // body proves layer 0 is in the orbit cam's culling mask. The clone otherwise keeps the shopkeeper
            // template's layer, which the ship-orbit camera may not render.
            SetLayerRecursive(_body.transform, 0);

            _nameTagObject = new GameObject("LocalNameTag");
            _nameTagObject.transform.SetParent(_root.transform);
            _nameTagObject.transform.localPosition = new Vector3(0f, 1.1f, 0f);
            var tag = _nameTagObject.AddComponent<TextMesh>();
            // SteamClient.Name throws an NRE if the mod's Steam client never initialized (e.g. a client
            // missing steam_api64.dll), and TryBuild runs every ~1.5s, so an unguarded read would spam the
            // log every retry. Fall back to a placeholder when Steam isn't up; the body still builds cleanly.
            tag.text = SteamClient.IsValid ? SteamClient.Name : "Player";
            tag.fontSize = 32; tag.characterSize = 0.04f;
            tag.anchor = TextAnchor.MiddleCenter; tag.alignment = TextAlignment.Center;
            tag.color = Color.yellow;

            SetupRig(_body);
            _has = true; _needsFit = true; _havePrev = false; _visible = true; _hasBodyBase = false;
            VerboseLogger.PlayerEvent("Local third-person body built");
        }

        private void Teardown()
        {
            if (_root != null) Destroy(_root);
            _root = _body = _nameTagObject = null;
            _renderers = null;
            _has = false; _rigReady = false; _havePrev = false; _hasBodyBase = false; _crouch01 = 0f; _lookPitch = 0f; _legIkReady = false;
        }

        private void SetVisible(bool on)
        {
            if (on == _visible) return;
            _visible = on;
            if (_renderers != null) foreach (var r in _renderers) if (r != null) r.enabled = on;
            if (_nameTagObject != null) _nameTagObject.SetActive(on);
        }

        private void FitBodyAndTag()
        {
            if (_renderers == null || _renderers.Length == 0) { _needsFit = false; return; }
            Bounds wb = _renderers[0].bounds;
            for (int i = 1; i < _renderers.Length; i++) wb.Encapsulate(_renderers[i].bounds);
            if (wb.size.y < 0.5f) return; // bounds not ready yet
            var t = _root.transform;
            const float feetTarget = -0.9f;
            float feet = t.InverseTransformPoint(new Vector3(wb.center.x, wb.min.y, wb.center.z)).y;
            float head = t.InverseTransformPoint(new Vector3(wb.center.x, wb.max.y, wb.center.z)).y;
            float shift = feetTarget - feet;
            _body.transform.localPosition += new Vector3(0f, shift, 0f);
            if (_nameTagObject != null) _nameTagObject.transform.localPosition = new Vector3(0f, head + shift + 0.25f, 0f);
            _bodyBaseLocalPos = _body.transform.localPosition; // base for the per-frame crouch drop
            _hasBodyBase = true;
            CaptureLegIkBind(); // capture the standing leg-IK bind now that the body is planted (root scale = 1)
            _needsFit = false;
        }

        /// <summary>
        /// Capture the standing bind data the crouch leg IK needs, once the body is planted. Forces the legs to
        /// bind pose first so a mid-gait fit frame can't pollute the capture. See RemoteAvatar.CaptureLegIkBind.
        /// </summary>
        private void CaptureLegIkBind()
        {
            _legIkReady = false;
            if (_bUpperLegL == null || _bUpperLegR == null || _bLowerLegL == null || _bLowerLegR == null)
            {
                Plugin.Log.LogWarning("[Coop] Crouch IK (local): leg bones missing; feet will not be IK-planted (body drop only).");
                return;
            }
            var root = _root.transform;
            _bUpperLegL.localRotation = _qUpperLegL; _bUpperLegR.localRotation = _qUpperLegR;
            _bLowerLegL.localRotation = _qLowerLegL; _bLowerLegR.localRotation = _qLowerLegR;
            bool okL = CaptureOneLeg(root, _bUpperLegL, _bLowerLegL, _bFootL,
                out _thighLenL, out _shinLenL, out _footLocalL, out _thighAimLocalL, out _shinAimLocalL);
            bool okR = CaptureOneLeg(root, _bUpperLegR, _bLowerLegR, _bFootR,
                out _thighLenR, out _shinLenR, out _footLocalR, out _thighAimLocalR, out _shinAimLocalR);
            _legIkReady = okL && okR;
            if (_bFootL == null || _bFootR == null)
                Plugin.Log.LogWarning("[Coop] Crouch IK (local): foot/ankle bone not found; ankle approximated from the shin.");
        }

        /// <summary>Capture one leg's bind data (see RemoteAvatar.CaptureOneLeg).</summary>
        private static bool CaptureOneLeg(Transform root, Transform hip, Transform knee, Transform foot,
            out float thighLen, out float shinLen, out Vector3 footLocal, out Vector3 thighAimLocal, out Vector3 shinAimLocal)
        {
            Vector3 hp = hip.position, kp = knee.position;
            Vector3 ankle = foot != null ? foot.position : kp + (kp - hp);
            thighLen = Vector3.Distance(hp, kp);
            shinLen  = Vector3.Distance(kp, ankle);
            footLocal = root.InverseTransformPoint(ankle);
            Vector3 tAim = kp - hp;
            Vector3 sAim = ankle - kp;
            thighAimLocal = tAim.sqrMagnitude > 1e-8f ? hip.InverseTransformDirection(tAim.normalized) : Vector3.up;
            shinAimLocal  = sAim.sqrMagnitude > 1e-8f ? knee.InverseTransformDirection(sAim.normalized) : Vector3.up;
            return thighLen > 1e-3f && shinLen > 1e-3f;
        }

        /// <summary>Two-bone leg IK for one leg (see RemoteAvatar.SolveLegIk). Run each crouched frame AFTER the drop.</summary>
        private static void SolveLegIk(Transform root, Transform hip, Transform knee,
            float thighLen, float shinLen, Vector3 footLocal, Vector3 thighAimLocal, Vector3 shinAimLocal, float kneeForwardSign,
            Vector3 stepOffset)
        {
            if (hip == null || knee == null) return;
            float a = thighLen, b = shinLen;
            if (a < 1e-4f || b < 1e-4f) return;

            Vector3 H = hip.position;
            Vector3 F = root.TransformPoint(footLocal) + stepOffset;
            Vector3 hf = F - H;
            float d = Mathf.Clamp(hf.magnitude, Mathf.Abs(a - b) + 1e-3f, a + b - 1e-3f);
            Vector3 dir = hf.sqrMagnitude > 1e-8f ? hf.normalized : -root.up;

            float cosH = Mathf.Clamp((a * a + d * d - b * b) / (2f * a * d), -1f, 1f);
            float hipAngle = Mathf.Acos(cosH);

            Vector3 fwd = root.forward * kneeForwardSign;
            Vector3 pole = fwd - Vector3.Dot(fwd, dir) * dir;
            if (pole.sqrMagnitude < 1e-6f) pole = root.up - Vector3.Dot(root.up, dir) * dir;
            if (pole.sqrMagnitude < 1e-6f) { pole = Vector3.up; }
            pole.Normalize();

            Vector3 K = H + a * (Mathf.Cos(hipAngle) * dir + Mathf.Sin(hipAngle) * pole);
            Vector3 wantThigh = K - H;
            if (wantThigh.sqrMagnitude < 1e-10f) return;
            Vector3 worldAim = hip.TransformDirection(thighAimLocal);
            hip.rotation = Quaternion.FromToRotation(worldAim, wantThigh.normalized) * hip.rotation;

            Vector3 Kp = knee.position;
            Vector3 wantShin = F - Kp;
            if (wantShin.sqrMagnitude < 1e-10f) return;
            Vector3 worldAim2 = knee.TransformDirection(shinAimLocal);
            knee.rotation = Quaternion.FromToRotation(worldAim2, wantShin.normalized) * knee.rotation;
        }

        /// <summary>
        /// Local crouch amount 0..1, read from the vanilla PlayerCrouching head-height lerp (standing
        /// initialHeight -> crouched 0.2), matching PlayerSyncManager.SampleCrouch01. Local body only.
        /// </summary>
        private float SampleLocalCrouch01()
        {
            if (_crouching == null)
            {
                var rig = Refs.ovrCameraRig;
                if (rig != null) _crouching = rig.GetComponent<PlayerCrouching>();
                if (_crouching == null) return 0f;
                _crouchStandH = Traverse.Create(_crouching).Field("initialHeight").GetValue<float>();
            }
            if (_crouchStandH <= 0.3f) return 0f;
            float h = _crouching.GetCurrentHeadHeight();
            if (h < 0.1f) return 0f; // uninitialized band (starts at 0) reads as standing, not full crouch
            return Mathf.Clamp01(Mathf.InverseLerp(_crouchStandH, 0.2f, h));
        }

        /// <summary>
        /// LOOK-LEAN: the local player's clamped vertical look angle in degrees (~[-60,60]; positive = looking
        /// UP), read from the vanilla MouseLook.rotationY private field. Up to two MouseLook instances exist
        /// (static look1/look2); only the VERTICAL one moves rotationY (the horizontal-only MouseX instance keeps
        /// it 0), so take the largest ABSOLUTE magnitude. Camera.main pitch is unusable in the ship-orbit cam
        /// (Camera.main is the orbit cam, not the head); MouseLook.rotationY is camera-mode-independent. Matches
        /// PlayerSyncManager.SampleLocalLookPitchDeg. Returns 0 if no MouseLook is loaded; re-finds when stale.
        /// </summary>
        private float SampleLocalLookPitchDeg()
        {
            if (_cachedMouseLooks == null || _cachedMouseLooks.Length == 0)
            {
                // (v0.2.25) EMPTY-SCAN THROTTLE: with no MouseLook loaded (menus/loading) this ran a
                // full-scene FindObjectsOfType EVERY call, allocating and scanning for nothing.
                // Cache the miss and rescan at most once per interval (realtime, load-lag immune).
                float now = Time.realtimeSinceStartup;
                if (now < _nextMouseLookScanTime) return 0f;
                _cachedMouseLooks = Object.FindObjectsOfType<MouseLook>();
                if (_cachedMouseLooks == null || _cachedMouseLooks.Length == 0)
                {
                    _nextMouseLookScanTime = now + MouseLookRescanInterval;
                    return 0f;
                }
            }

            float best = 0f, bestAbs = -1f;
            bool anyLive = false;
            for (int i = 0; i < _cachedMouseLooks.Length; i++)
            {
                var ml = _cachedMouseLooks[i];
                if (ml == null) continue; // destroyed on a scene change
                anyLive = true;
                float ry = MouseLookRotationYRef(ml);
                float a = Mathf.Abs(ry);
                if (a > bestAbs) { bestAbs = a; best = ry; }
            }
            if (!anyLive) { _cachedMouseLooks = null; return 0f; } // all stale -> re-find next call
            return best;
        }

        private static void SetLayerRecursive(Transform t, int layer)
        {
            t.gameObject.layer = layer;
            for (int i = 0; i < t.childCount; i++) SetLayerRecursive(t.GetChild(i), layer);
        }

        // ---- procedural rig + animation (same convention as RemotePlayerManager) ----
        private static Transform FindDeep(Transform root, string name)
        {
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++) { var r = FindDeep(root.GetChild(i), name); if (r != null) return r; }
            return null;
        }

        private void SetupRig(GameObject body)
        {
            var r = body.transform;
            _bSpine = FindDeep(r, "Spine_01");
            _bUpperLegL = FindDeep(r, "UpperLeg_L"); _bUpperLegR = FindDeep(r, "UpperLeg_R");
            _bLowerLegL = FindDeep(r, "LowerLeg_L"); _bLowerLegR = FindDeep(r, "LowerLeg_R");
            _bShoulderL = FindDeep(r, "Shoulder_L"); _bShoulderR = FindDeep(r, "Shoulder_R");
            _bElbowL = FindDeep(r, "Elbow_L"); _bElbowR = FindDeep(r, "Elbow_R");
            // Crouch IK ankle bones (Foot_L/R, else Ankle_L/R, else the LowerLeg's first child; approximated if none).
            _bFootL = FindDeep(r, "Foot_L") ?? FindDeep(r, "Ankle_L");
            _bFootR = FindDeep(r, "Foot_R") ?? FindDeep(r, "Ankle_R");
            if (_bFootL == null && _bLowerLegL != null && _bLowerLegL.childCount > 0) _bFootL = _bLowerLegL.GetChild(0);
            if (_bFootR == null && _bLowerLegR != null && _bLowerLegR.childCount > 0) _bFootR = _bLowerLegR.GetChild(0);
            _legIkReady = false; // captured once the body is planted (FitBodyAndTag -> CaptureLegIkBind)
            if (_bSpine != null) _qSpine = _bSpine.localRotation;
            if (_bUpperLegL != null) _qUpperLegL = _bUpperLegL.localRotation;
            if (_bUpperLegR != null) _qUpperLegR = _bUpperLegR.localRotation;
            if (_bLowerLegL != null) _qLowerLegL = _bLowerLegL.localRotation;
            if (_bLowerLegR != null) _qLowerLegR = _bLowerLegR.localRotation;
            if (_bShoulderL != null) _qShoulderL = _bShoulderL.localRotation;
            if (_bShoulderR != null) _qShoulderR = _bShoulderR.localRotation;
            if (_bElbowL != null) _qElbowL = _bElbowL.localRotation;
            if (_bElbowR != null) _qElbowR = _bElbowR.localRotation;
            _rigReady = _bUpperLegL != null && _bUpperLegR != null;
        }

        private static void SetSwing(Transform t, Quaternion bind, Vector3 axis, float deg)
        {
            if (t == null) return;
            t.localRotation = bind; t.Rotate(axis, deg, Space.Self);
        }

        private void DriveAnimation()
        {
            if (!_rigReady) return;
            const float WalkFullSpeed = 1.4f, StrideRadPerM = 4.2f, LegAmp = 28f, KneeAmp = 40f, KneePhase = 1.1f, ArmAmp = 22f, ElbowAmp = 16f, BreatheAmp = 2.2f, BreatheHz = 0.22f;
            // Crouch tuning (SQUAT via leg IK) - live from config, identical to RemotePlayerManager.DriveProceduralAnimation.
            const float CrouchSmooth = 12f;
            float CrouchDrop = Plugin.CrouchDropMetersConfig.Value, CrouchTorsoLean = Plugin.CrouchTorsoLeanDegConfig.Value, CrouchArmBend = Plugin.CrouchArmBendDegConfig.Value, CrouchStrideCut = Plugin.CrouchStrideCutConfig.Value;
            // LOOK-LEAN tuning (live from config, identical to RemotePlayerManager.DriveProceduralAnimation).
            float LookPitchScale = Plugin.LookPitchScaleConfig.Value, LookPitchMaxDeg = Plugin.LookPitchMaxDegConfig.Value;
            float dt = Mathf.Max(Time.deltaTime, 1e-4f);

            _crouch01 = Mathf.Lerp(_crouch01, SampleLocalCrouch01(), 1f - Mathf.Exp(-CrouchSmooth * dt));
            float crouch = _crouch01;

            // LOOK-LEAN: ease the freshly-sampled local look pitch (same rate as crouch) - the local body reads
            // MouseLook directly (no network). Converted to a torso pitch below, composed with the crouch fold.
            _lookPitch = Mathf.Lerp(_lookPitch, SampleLocalLookPitchDeg(), 1f - Mathf.Exp(-CrouchSmooth * dt));

            // Crouch-walk: gait continues at a reduced stride while crouched.
            float blend = Mathf.Clamp01(_animSpeed / WalkFullSpeed) * (1f - CrouchStrideCut * crouch);
            _gaitPhase = Mathf.Repeat(_gaitPhase + _animSpeed * StrideRadPerM * dt, 2f * Mathf.PI);
            float s = Mathf.Sin(_gaitPhase), sOpp = Mathf.Sin(_gaitPhase + Mathf.PI);

            // Legs / arms: WALK GAIT ONLY (crouch is added below as symmetric world-space pitches).
            SetSwing(_bUpperLegL, _qUpperLegL, Vector3.up, LegAmp * blend * s);
            SetSwing(_bUpperLegR, _qUpperLegR, Vector3.up, LegAmp * blend * sOpp);
            SetSwing(_bLowerLegL, _qLowerLegL, Vector3.back, KneeAmp * blend * Mathf.Max(0f, Mathf.Sin(_gaitPhase + KneePhase)));
            SetSwing(_bLowerLegR, _qLowerLegR, Vector3.back, KneeAmp * blend * Mathf.Max(0f, Mathf.Sin(_gaitPhase + Mathf.PI + KneePhase)));
            // Arm swing DAMPED while crouched (a full walk swing looks derpy in a crouch).
            float armBlend = blend * (1f - 0.85f * crouch);
            SetSwing(_bShoulderL, _qShoulderL, Vector3.down, ArmAmp * armBlend * sOpp);
            SetSwing(_bShoulderR, _qShoulderR, Vector3.down, ArmAmp * armBlend * s);
            SetSwing(_bElbowL, _qElbowL, Vector3.down, ElbowAmp * armBlend * (0.5f + 0.5f * sOpp));
            SetSwing(_bElbowR, _qElbowR, Vector3.down, ElbowAmp * armBlend * (0.5f + 0.5f * s));
            SetSwing(_bSpine, _qSpine, Vector3.forward, Mathf.Sin(Time.time * BreatheHz * 2f * Mathf.PI) * BreatheAmp);

            // CROUCH POSE (SQUAT via leg IK) - kept identical to RemotePlayerManager.DriveProceduralAnimation.
            // Arms bend to a ready stance + slight torso lean; the body drops; then per-leg 2-bone IK re-plants
            // the feet at their standing spot. All world-space / axis-agnostic.
            var root = _root.transform;

            // LOOK-LEAN: convert the eased look pitch to a torso pitch (identical to RemoteAvatar). The crouch
            // fold is +CrouchTorsoLean about root.right = FORWARD, and MouseLook.rotationY is positive when
            // looking UP, so NEGATE the pitch to make looking DOWN fold FORWARD (looking UP leans BACK). Flip the
            // whole direction live by making LookPitchScale negative. Clamped so the torso never over-bends.
            float lookLean = Mathf.Clamp(-_lookPitch * LookPitchScale, -LookPitchMaxDeg, LookPitchMaxDeg);

            if (crouch > 0.001f)
            {
                float armBend      = CrouchArmBend * crouch;
                float shoulderTuck = 0.35f * armBend;
                // Vector3.up (opposite the walk swing) so forearms come FORWARD/up into a ready stance.
                if (_bElbowL != null) _bElbowL.Rotate(Vector3.up, armBend, Space.Self);
                if (_bElbowR != null) _bElbowR.Rotate(Vector3.up, armBend, Space.Self);
                if (_bShoulderL != null) _bShoulderL.Rotate(Vector3.up, shoulderTuck, Space.Self);
                if (_bShoulderR != null) _bShoulderR.Rotate(Vector3.up, shoulderTuck, Space.Self);
            }

            // Spine world-pitch EVERY frame (standing included): the crouch FORWARD fold (0 when standing) plus
            // the look-lean, composed into ONE world-space rotate about root.right AFTER the breathe SetSwing on
            // the spine (the old crouch-only rotate was removed so it is not double-applied). The look-lean thus
            // pivots the whole upper body on the hips in standing/walking and adds to the crouch fold when crouched.
            float spinePitch = CrouchTorsoLean * crouch + lookLean;
            if (_bSpine != null && Mathf.Abs(spinePitch) > 0.001f)
                _bSpine.Rotate(root.right, spinePitch, Space.World);

            // Body drop: lower the hips/torso/head relative to the planted base. MUST run BEFORE the leg IK so
            // the IK reads the dropped hip joints.
            if (_hasBodyBase && _body != null)
                _body.transform.localPosition = _bodyBaseLocalPos - new Vector3(0f, CrouchDrop * crouch, 0f);

            // Leg IK: re-plant both ankles (knees forward = squat). Crouch-walk STEPS the ankle targets with the
            // gait (foot swings forward/back + lifts, alternating L/R, scaled by blend) so the legs stride while
            // crouched instead of freezing. Flip CrouchKneeForward to -1 if the knees bend backward.
            if (_legIkReady && crouch > 0.001f)
            {
                float kf = Plugin.CrouchKneeForwardConfig.Value;
                const float StepLen = 0.28f, StepLift = 0.12f;
                Vector3 stepL = root.forward * (StepLen * blend * s)    + root.up * (StepLift * blend * Mathf.Max(0f, s));
                Vector3 stepR = root.forward * (StepLen * blend * sOpp) + root.up * (StepLift * blend * Mathf.Max(0f, sOpp));
                SolveLegIk(root, _bUpperLegL, _bLowerLegL, _thighLenL, _shinLenL, _footLocalL, _thighAimLocalL, _shinAimLocalL, kf, stepL);
                SolveLegIk(root, _bUpperLegR, _bLowerLegR, _thighLenR, _shinLenR, _footLocalR, _thighAimLocalR, _shinAimLocalR, kf, stepR);
            }
        }
    }
}
