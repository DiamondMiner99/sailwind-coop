using Steamworks;
using UnityEngine;

namespace SailwindCoop.Player
{
    /// <summary>
    /// (2026-09-11) Pose preview: a copy of your own sailor that stands in front of you and does what you do,
    /// drawn through the SAME RemoteAvatar code your crewmates run for you - body, walk, crouch, look-lean and
    /// the held-tool arm. Toggle with PosePreviewConfig.PreviewKey (Home by default) and tune the pose live from the
    /// F1 Configuration Manager (Sailwind Player Model, section "2. Held Tool"). Works solo; nothing is sent over the network.
    ///
    /// The copy is a twin, not a mirror: it faces you, so its right hand appears on your left, exactly what a
    /// crewmate standing in front of you sees. Your held item is re-expressed relative to the copy's head the
    /// way it sits relative to yours, and shown with a render-only clone (no ShipItem, no colliders, nothing
    /// saved), so the copy "holds" what you hold.
    ///
    /// The avatar is deliberately NOT registered with RemotePlayerManager: everything that iterates the crew
    /// (sleep handshake, liveness, NPC relevance, disconnect cleanup) must never see it.
    ///
    /// The body is cloned from a shopkeeper, so like a real crewmate the copy stays a blue capsule until one
    /// has loaded. Preview in port.
    /// </summary>
    public class PosePreviewPuppet : MonoBehaviour
    {
        // An id no Steam account can have (real individual accounts are 7656119xxxxxxxxxx). Only used to
        // pick a deterministic look from the appearance registry.
        private static readonly SteamId PuppetId = new SteamId { Value = 0x5A11_0000_0000_0001UL };

        private RemoteAvatar _avatar;
        private GoPointer _pointer;
        private ShipItem _cloneSource;
        private GameObject _clone;

        private void Update()
        {
            if (PosePreviewConfig.PreviewKey == null || !PosePreviewConfig.PreviewKey.Value.IsDown()) return;
            if (_avatar != null) Stop("toggled off");
            else Begin();
        }

        private void Begin()
        {
            if (Refs.charController == null || Camera.main == null || !GameState.playing)
            {
                Plugin.Notify("Pose preview: load into the world first.", 4f);
                return;
            }
            _avatar = new RemoteAvatar(PuppetId, "Pose preview");
            Plugin.Notify($"Pose preview ON ({PosePreviewConfig.PreviewKey.Value} to hide). Tune it in F1 under Sailwind Player Model, section \"2. Held Tool\".", 6f);
            Plugin.Log.LogInfo("[POSE-PREVIEW] Started");
        }

        private void Stop(string why)
        {
            if (_avatar != null) _avatar.Destroy();
            _avatar = null;
            DestroyClone();
            Plugin.Log.LogInfo($"[POSE-PREVIEW] Stopped ({why})");
        }

        private void OnDisable() => Stop("disabled");

        private void LateUpdate()
        {
            if (_avatar == null) return;
            var cc = Refs.charController;
            var cam = Camera.main;
            if (cc == null || cam == null || !GameState.playing)
            {
                Stop("left the world");
                return;
            }

            try
            {
                Drive(cc, cam.transform);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogError($"[POSE-PREVIEW] {e}");
                Stop("error, see log");
            }
        }

        private void Drive(CharacterController cc, Transform cam)
        {
            // ---- where the copy stands: in front of you, on your deck ----
            Vector3 flatFwd = Vector3.ProjectOnPlane(cam.forward, Vector3.up);
            if (flatFwd.sqrMagnitude < 1e-4f) flatFwd = Vector3.ProjectOnPlane(cam.up, Vector3.up);
            flatFwd.Normalize();

            float feetY = cc.bounds.min.y;
            Vector3 myFeet = new Vector3(cam.position.x, feetY, cam.position.z);
            Vector3 puppetFeet = myFeet + flatFwd * PosePreviewConfig.PreviewDistance.Value;
            Quaternion puppetYaw = Quaternion.LookRotation(-flatFwd, Vector3.up) * Quaternion.Euler(0f, PosePreviewConfig.PreviewYaw.Value, 0f);

            float crouch = Plugin.PlayerSyncManager != null ? Plugin.PlayerSyncManager.LocalCrouch01 : 0f;
            float pitch = Sync.PlayerSyncManager.SampleHeadLookPitchDeg();

            // Same frames the real stream uses: deck-local against the boat model when aboard (so the copy
            // rides the deck and does not "walk" while the boat sails), real coordinates ashore.
            string boatName = GameState.currentBoat != null ? GameState.currentBoat.name : null;
            Transform boatModel = !string.IsNullOrEmpty(boatName) ? RemotePlayerManager.FindBoatByName(boatName) : null;
            if (boatModel != null)
            {
                _avatar.UpdatePosition(boatModel.InverseTransformPoint(puppetFeet), puppetYaw, true, boatName, crouch, pitch);
            }
            else
            {
                var offset = FloatingOriginManager.instance != null ? FloatingOriginManager.instance.outCurrentOffset : Vector3.zero;
                _avatar.UpdatePosition(puppetFeet - offset, puppetYaw, false, "", crouch, pitch);
            }

            // ---- what the copy holds: your item, re-expressed against the copy's head ----
            if (_pointer == null) _pointer = FindObjectOfType<GoPointer>();
            var held = _pointer != null ? _pointer.GetHeldItem() as ShipItem : null;
            bool placeCloneOurselves = false;
            Vector3 itemPos = Vector3.zero;
            Quaternion itemRot = Quaternion.identity;

            if (held != null)
            {
                if (held != _cloneSource) BuildClone(held);

                // Your camera = yaw * pitch. The copy's "camera" = its yaw * your pitch, at your eye height above
                // its feet.
                Quaternion myYaw = Quaternion.Euler(0f, cam.eulerAngles.y, 0f);
                Quaternion pitchOnly = Quaternion.Inverse(myYaw) * cam.rotation;
                Quaternion puppetCamRot = puppetYaw * pitchOnly;
                Vector3 puppetCamPos = puppetFeet + Vector3.up * (cam.position.y - feetY);

                Quaternion camInv = Quaternion.Inverse(cam.rotation);
                itemPos = puppetCamPos + puppetCamRot * (camInv * (held.transform.position - cam.position));
                itemRot = puppetCamRot * (camInv * held.transform.rotation);

                if (_clone != null)
                {
                    _avatar.SetHeldItemPose(_clone.transform, null, itemPos, itemRot, held.big);
                    placeCloneOurselves = !_avatar.PlacesHeldItemInHand;
                }
            }
            else
            {
                DestroyClone();
            }

            _avatar.Tick();

            // In HandReachesItem/Off (or big items) the item floats where the holder's game put it, as it does
            // for a real crewmate; ItemSyncManager does this write in that case.
            if (placeCloneOurselves && _clone != null)
            {
                _clone.transform.position = itemPos;
                _clone.transform.rotation = itemRot;
            }
        }

        /// <summary>Render-only copy of an item: its meshes and materials, nothing else.</summary>
        private void BuildClone(ShipItem source)
        {
            DestroyClone();
            _cloneSource = source;

            var root = new GameObject("PosePreviewItem");
            var src = source.transform;
            root.transform.localScale = src.lossyScale;
            Vector3 srcScale = src.lossyScale;

            foreach (var mr in source.GetComponentsInChildren<MeshRenderer>(true))
            {
                var mf = mr.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;

                var go = new GameObject(mr.name);
                go.layer = 2; // Ignore Raycast: the copy's item must never catch your clicks
                go.transform.SetParent(root.transform, false);
                go.transform.localPosition = src.InverseTransformPoint(mr.transform.position);
                go.transform.localRotation = Quaternion.Inverse(src.rotation) * mr.transform.rotation;
                Vector3 ls = mr.transform.lossyScale;
                go.transform.localScale = new Vector3(
                    srcScale.x != 0f ? ls.x / srcScale.x : 1f,
                    srcScale.y != 0f ? ls.y / srcScale.y : 1f,
                    srcScale.z != 0f ? ls.z / srcScale.z : 1f);
                go.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
                go.AddComponent<MeshRenderer>().sharedMaterials = mr.sharedMaterials;
            }

            root.layer = 2;
            _clone = root;
        }

        private void DestroyClone()
        {
            if (_clone != null) Destroy(_clone);
            _clone = null;
            _cloneSource = null;
        }
    }
}
