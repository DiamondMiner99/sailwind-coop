using UnityEngine;
using Steamworks;
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
            _has = true; _needsFit = true; _havePrev = false; _visible = true;
            VerboseLogger.PlayerEvent("Local third-person body built");
        }

        private void Teardown()
        {
            if (_root != null) Destroy(_root);
            _root = _body = _nameTagObject = null;
            _renderers = null;
            _has = false; _rigReady = false; _havePrev = false;
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
            _needsFit = false;
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
            float dt = Mathf.Max(Time.deltaTime, 1e-4f);
            float blend = Mathf.Clamp01(_animSpeed / WalkFullSpeed);
            _gaitPhase = Mathf.Repeat(_gaitPhase + _animSpeed * StrideRadPerM * dt, 2f * Mathf.PI);
            float s = Mathf.Sin(_gaitPhase), sOpp = Mathf.Sin(_gaitPhase + Mathf.PI);
            SetSwing(_bUpperLegL, _qUpperLegL, Vector3.up, LegAmp * blend * s);
            SetSwing(_bUpperLegR, _qUpperLegR, Vector3.up, LegAmp * blend * sOpp);
            SetSwing(_bLowerLegL, _qLowerLegL, Vector3.back, KneeAmp * blend * Mathf.Max(0f, Mathf.Sin(_gaitPhase + KneePhase)));
            SetSwing(_bLowerLegR, _qLowerLegR, Vector3.back, KneeAmp * blend * Mathf.Max(0f, Mathf.Sin(_gaitPhase + Mathf.PI + KneePhase)));
            SetSwing(_bShoulderL, _qShoulderL, Vector3.down, ArmAmp * blend * sOpp);
            SetSwing(_bShoulderR, _qShoulderR, Vector3.down, ArmAmp * blend * s);
            SetSwing(_bElbowL, _qElbowL, Vector3.down, ElbowAmp * blend * (0.5f + 0.5f * sOpp));
            SetSwing(_bElbowR, _qElbowR, Vector3.down, ElbowAmp * blend * (0.5f + 0.5f * s));
            SetSwing(_bSpine, _qSpine, Vector3.forward, Mathf.Sin(Time.time * BreatheHz * 2f * Mathf.PI) * BreatheAmp);
        }
    }
}
