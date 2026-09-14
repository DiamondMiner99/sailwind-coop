using SailwindCoop.Player;
using UnityEngine;

namespace SailwindCoop.UI
{
    /// <summary>
    /// (v0.3.0) A private, offscreen "photo studio" that renders the player's character for the
    /// character screen: one mannequin, its own camera, its own lights, on their own layer, parked far
    /// below the world and drawn into a RenderTexture.
    ///
    /// WHY NOT JUST SHOW THE REAL BODY. That was the obvious approach and it cannot work. LocalPlayerBody
    /// forces SetVisible(BoatCamera.on &amp;&amp; GameState.currentShipyard == null) every frame, so on land or in
    /// first person the real body's renderers are DISABLED for the entire time the screen is open - which
    /// is exactly why the character was not visible next to the panel. Worse, in co-op the world is not
    /// even paused: CoopPauseMenu.OnPauseOpened restores timeScale whenever the session is multiplayer and
    /// deliberately leaves the player mobile, so "the body is behind the menu" is not a place it stays.
    ///
    /// WHY NOT A MANNEQUIN IN FRONT OF THE LIVE CAMERA. Cheaper, but it renders inside the world: occluded
    /// by bulkheads, unlit at night, washed out in fog, clipped by the near plane. For a screen whose only
    /// job is judging a face, "sometimes you can see it" is a failure.
    ///
    /// SO: a self-contained rig on layer 31 (verified unnamed and unused in this build), lit by its own
    /// lights whose culling masks admit nothing else, photographed by its own disabled camera that we
    /// render by hand. No other camera's cullingMask is touched and no global render state is modified,
    /// so there is nothing to restore and nothing to leak if this goes wrong.
    ///
    /// It also carries its own watchdog: if the screen is closed by ANY path - button, key, or the
    /// inCursorMenu watchdog - the rig deletes itself on the next frame without being told.
    /// </summary>
    public static class CharacterPreviewStudio
    {
        private const int StudioLayer = 31;                        // unnamed and unused in this build
        private static readonly Vector3 StudioOrigin = new Vector3(0f, -8000f, 0f);
        private const int TexWidth = 420, TexHeight = 620;

        private static GameObject _root;
        private static GameObject _mannequin;
        private static Camera _camera;
        private static RenderTexture _rt;
        private static float _yaw;
        private static int _settleFrames;
        private static bool _framedOnce;

        /// <summary>The latest frame, or null when the studio is not up. Safe to read every OnGUI.</summary>
        public static RenderTexture Texture { get { return _rt; } }

        public static bool IsUp { get { return _root != null; } }

        /// <summary>Build the rig. Idempotent; a failure leaves nothing behind and simply means no preview.</summary>
        public static void Open()
        {
            if (_root != null) return;
            try
            {
                var template = RemotePlayerManager.GetBodyTemplate();
                if (template == null)
                {
                    Plugin.Log.LogInfo("[Character] No body template yet (no shopkeeper loaded); preview unavailable.");
                    return;
                }

                _root = new GameObject("SailwindCoop_CharacterStudio");
                Object.DontDestroyOnLoad(_root);
                _root.transform.position = StudioOrigin;
                _root.AddComponent<StudioTicker>();

                // The subject.
                _mannequin = Object.Instantiate(template);
                _mannequin.name = "StudioMannequin";
                foreach (var sk in _mannequin.GetComponentsInChildren<Shopkeeper>(true)) { sk.enabled = false; Object.Destroy(sk); }
                foreach (var col in _mannequin.GetComponentsInChildren<Collider>(true)) { col.enabled = false; Object.Destroy(col); }
                foreach (var rb in _mannequin.GetComponentsInChildren<Rigidbody>(true)) Object.Destroy(rb);
                _mannequin.transform.SetParent(_root.transform, false);
                _mannequin.transform.localPosition = Vector3.zero;
                _mannequin.transform.localRotation = Quaternion.identity;

                // Appearance must be written BEFORE activation - vanilla's Start() is what builds the mesh.
                Plugin.LocalAppearance.Apply(_mannequin);
                _mannequin.SetActive(true);
                foreach (var smr in _mannequin.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                { smr.enabled = true; smr.allowOcclusionWhenDynamic = false; }
                SetLayerRecursive(_mannequin.transform, StudioLayer);

                // The lights. Directional, so their position is irrelevant; the culling mask keeps them off
                // every other object in the world - this rig must not brighten the player's actual game.
                MakeLight(new Vector3(30f, -30f, 0f), 1.15f, new Color(1f, 0.97f, 0.92f));
                MakeLight(new Vector3(15f, 160f, 0f), 0.55f, new Color(0.75f, 0.82f, 0.95f));

                // The camera. Disabled, because we drive it by hand once per frame - an enabled camera
                // would render into the game's own frame as well.
                var camGo = new GameObject("StudioCamera");
                camGo.transform.SetParent(_root.transform, false);
                _camera = camGo.AddComponent<Camera>();
                _camera.enabled = false;
                _camera.cullingMask = 1 << StudioLayer;
                _camera.clearFlags = CameraClearFlags.SolidColor;
                _camera.backgroundColor = SailwindSkin.ParchmentDark;
                _camera.fieldOfView = 32f;
                _camera.nearClipPlane = 0.05f;
                _camera.farClipPlane = 20f;
                _camera.allowHDR = false;
                _camera.allowMSAA = false;

                _rt = new RenderTexture(TexWidth, TexHeight, 16) { antiAliasing = 2 };
                _rt.hideFlags = HideFlags.HideAndDontSave;
                _camera.targetTexture = _rt;

                _yaw = 0f;
                _framedOnce = false;
                _settleFrames = 12;   // Start() builds the mesh next frame; keep re-framing until it exists
                FrameSubject();
                Plugin.Log.LogInfo("[Character] Preview studio up.");
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("[Character] Could not build the preview studio: " + e.Message);
                Close();
            }
        }

        /// <summary>Tear the whole rig down. Idempotent, and safe to call from anywhere.</summary>
        public static void Close()
        {
            try
            {
                if (_camera != null) _camera.targetTexture = null;
                if (_rt != null) { _rt.Release(); Object.Destroy(_rt); _rt = null; }
                if (_root != null) Object.Destroy(_root);
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("[Character] Studio teardown: " + e.Message); }
            _root = null; _mannequin = null; _camera = null;
        }

        /// <summary>Re-dress the mannequin in place after an edit.</summary>
        public static void SetAppearance(CoopAppearance appearance)
        {
            if (_mannequin == null) return;
            try
            {
                var c = _mannequin.GetComponentInChildren<PsychoticLab.CharacterCustomizer>(true);
                if (c != null) appearance.ApplyLive(c);
                SetLayerRecursive(_mannequin.transform, StudioLayer); // a restyle can reveal new children
                _settleFrames = 8;                                    // re-frame: the silhouette may have changed
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("[Character] Studio restyle: " + e.Message); }
        }

        /// <summary>Spin the model, so the player can see the back of a hat.</summary>
        public static void Turn(float degrees)
        {
            _yaw += degrees;
            if (_mannequin != null) _mannequin.transform.localRotation = Quaternion.Euler(0f, _yaw, 0f);
        }

        /// <summary>
        /// Point the camera at whatever the mannequin ACTUALLY is, rather than at an assumed pose.
        ///
        /// The first version placed the camera at a hand-picked offset on the assumption the pivot sat at
        /// the soles and the model was about 1.8m tall. It ended up inside the torso. Worse, it was run on
        /// the frame the clone was created - but vanilla's CharacterCustomizer.Start(), which is what
        /// actually BUILDS the mesh, is deferred to the next frame, so at that moment there was frequently
        /// no geometry to aim at at all. That is why the preview appeared only sometimes.
        ///
        /// Measuring the renderer bounds and solving the distance from the camera's own field of view is
        /// immune to both: it does not care where the pivot is, how tall the rig is, or what scale the
        /// clone inherited. Re-run whenever the model changes.
        /// </summary>
        private static void FrameSubject()
        {
            if (_camera == null || _mannequin == null) return;
            try
            {
                bool any = false;
                Bounds bounds = new Bounds();
                foreach (var r in _mannequin.GetComponentsInChildren<SkinnedMeshRenderer>(false))
                {
                    if (r == null || !r.enabled) continue;
                    if (!any) { bounds = r.bounds; any = true; }
                    else bounds.Encapsulate(r.bounds);
                }
                if (!any) return;   // Start() has not built it yet; the ticker re-frames shortly

                float height = Mathf.Max(bounds.size.y, 0.1f);
                float width = Mathf.Max(Mathf.Max(bounds.size.x, bounds.size.z), 0.1f);

                // Solve the distance that fits the subject in BOTH axes, then stand back a bit further so
                // it is framed rather than filling the frame edge to edge.
                float fovV = _camera.fieldOfView * Mathf.Deg2Rad;
                float aspect = (float)TexWidth / TexHeight;
                float fovH = 2f * Mathf.Atan(Mathf.Tan(fovV * 0.5f) * aspect);
                float distV = (height * 0.5f) / Mathf.Tan(fovV * 0.5f);
                float distH = (width * 0.5f) / Mathf.Tan(fovH * 0.5f);
                float dist = Mathf.Max(distV, distH) * 1.35f;

                Vector3 center = bounds.center;
                Vector3 pos = center + new Vector3(0f, height * 0.04f, dist);
                _camera.transform.position = pos;
                _camera.transform.rotation = Quaternion.LookRotation(center - pos, Vector3.up);
                _camera.nearClipPlane = Mathf.Max(0.05f, dist - height * 3f);
                _camera.farClipPlane = dist + height * 6f;

                if (!_framedOnce)
                {
                    _framedOnce = true;
                    Plugin.Log.LogInfo($"[Character] Studio framed: subject {width:F2} x {height:F2} m, " +
                        $"camera {dist:F2} m back.");
                }
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("[Character] Studio framing: " + e.Message); }
        }

        private static void MakeLight(Vector3 euler, float intensity, Color color)
        {
            var go = new GameObject("StudioLight");
            go.transform.SetParent(_root.transform, false);
            go.transform.localRotation = Quaternion.Euler(euler);
            var l = go.AddComponent<Light>();
            l.type = LightType.Directional;
            l.intensity = intensity;
            l.color = color;
            l.shadows = LightShadows.None;
            l.cullingMask = 1 << StudioLayer;   // never touch anything the player can see
            go.layer = StudioLayer;
        }

        private static void SetLayerRecursive(Transform t, int layer)
        {
            t.gameObject.layer = layer;
            for (int i = 0; i < t.childCount; i++) SetLayerRecursive(t.GetChild(i), layer);
        }

        /// <summary>
        /// Renders the studio once per frame and, crucially, deletes the whole rig the moment the screen is
        /// no longer open - whichever of the several close paths got taken, including the emergency one.
        /// </summary>
        private class StudioTicker : MonoBehaviour
        {
            private void LateUpdate()
            {
                if (!CharacterScreen.IsOpen) { CharacterPreviewStudio.Close(); return; }

                // Keep re-framing for the first stretch after anything changes. Vanilla builds the mesh in
                // Start(), a frame after the clone is made, and a restyle can change the silhouette (a tall
                // hat, a longer coat), so a single frame-once at build time is exactly the thing that made
                // the preview appear only sometimes.
                if (_settleFrames > 0)
                {
                    _settleFrames--;
                    // Re-assert the layer as well as the framing. Vanilla's Start() runs a frame after the
                    // clone is built and re-activates parts from its own lists; anything it switches on
                    // that is NOT on the studio layer is invisible to the studio camera, which would show
                    // as a partial model - a head with no body, say - rather than as an error.
                    if (_mannequin != null) SetLayerRecursive(_mannequin.transform, StudioLayer);
                    FrameSubject();
                }

                if (_camera != null && _rt != null) _camera.Render();
            }
        }
    }
}
