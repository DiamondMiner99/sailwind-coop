using System.Collections.Generic;
using UnityEngine;
using SailwindCoop.Debug;
using SailwindCoop.Networking.Packets;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Renders a "ghost" charting kit (ink set + quill + ruler) on a map while a REMOTE crewmate
    /// is drawing on it. Vanilla charting is a per-client camera-mode rig: MapTableCamera's
    /// kit/quill/ruler live on the map-cam-only layer 23 and only exist for the drawing player, so
    /// bystanders see an empty map with lines appearing from nowhere. Driven by ChartSession
    /// (start/stop + kit placement, reliable) and ChartCursor (chart-local cursor, unreliable 10Hz);
    /// the ruler start point and ink color come from the already-synced tempLine, and ruler
    /// scale/variant are read from the observer's own MapChart fields (no wire needed).
    /// Observer-side transforms are valid because MapChart.LateUpdate keeps the chart under its
    /// original foldable parent whenever the local currentMap is null.
    /// </summary>
    public class ChartKitGhostManager : MonoBehaviour
    {
        public static ChartKitGhostManager Instance { get; private set; }

        private const float QuillLerpSpeed = 12f; // smooths the 5-10Hz cursor/tempLine stream

        private class GhostSet
        {
            public ulong UserSteamId;
            public ShipItemFoldable Foldable;
            public GameObject Root;
            public Transform Kit;
            public Transform Quill;
            public Transform Ruler;
            public GameObject RulerSmall; // null on primitive fallback
            public GameObject RulerLarge;
            public byte Tool;
            public float CursorX;
            public float CursorY;
            public bool HasCursor;
        }

        private readonly Dictionary<int, GhostSet> _ghosts = new Dictionary<int, GhostSet>();
        private readonly List<int> _removalScratch = new List<int>();

        private void Awake()
        {
            if (Instance != null)
            {
                Destroy(this);
                return;
            }
            Instance = this;
        }

        // === Session lifecycle (called by NavigationSyncManager) ===

        public void StartSession(int itemInstanceId, ulong userSteamId, int kitPos)
        {
            // Never ghost our own session (the real rig is on screen).
            if (userSteamId == (ulong)Steamworks.SteamClient.SteamId) return;

            var foldable = FindFoldableByInstanceId(itemInstanceId);
            if (foldable == null || foldable.mapChart == null)
            {
                VerboseLogger.NavApply($"ChartSession start FAILED: foldable {itemInstanceId} not found");
                return;
            }

            EndSession(itemInstanceId); // restart cleanly if a stale ghost lingers

            var ghost = BuildGhostSet(foldable);
            if (ghost == null) return;

            ghost.UserSteamId = userSteamId;
            PlaceKit(ghost, kitPos);
            _ghosts[itemInstanceId] = ghost;

            VerboseLogger.NavApply($"Chart ghost created, item={itemInstanceId}, user={userSteamId}, kitPos={kitPos}");
        }

        public void EndSession(int itemInstanceId)
        {
            if (!_ghosts.TryGetValue(itemInstanceId, out var ghost)) return;
            _ghosts.Remove(itemInstanceId);
            if (ghost.Root != null) Destroy(ghost.Root);
            VerboseLogger.NavApply($"Chart ghost destroyed, item={itemInstanceId}");
        }

        public void OnCursor(ChartCursorPacket packet)
        {
            if (!_ghosts.TryGetValue(packet.ItemInstanceId, out var ghost)) return;
            ghost.Tool = packet.Tool;
            ghost.CursorX = packet.CursorX;
            ghost.CursorY = packet.CursorY;
            ghost.HasCursor = true;
        }

        /// <summary>Drop all ghosts owned by a peer that left/dropped.</summary>
        public void OnPeerLeft(ulong leaverSteamId)
        {
            _removalScratch.Clear();
            foreach (var kvp in _ghosts)
                if (kvp.Value.UserSteamId == leaverSteamId) _removalScratch.Add(kvp.Key);
            foreach (var id in _removalScratch)
                EndSession(id);
            _removalScratch.Clear();
        }

        public void Reset()
        {
            _removalScratch.Clear();
            foreach (var kvp in _ghosts)
                if (kvp.Value.Root != null) Destroy(kvp.Value.Root);
            _ghosts.Clear();
        }

        // === Per-frame drive ===

        private void Update()
        {
            if (_ghosts.Count == 0) return;

            _removalScratch.Clear();
            foreach (var kvp in _ghosts)
            {
                var ghost = kvp.Value;
                // Foldable destroyed or folded mid-session: tear down (the drawer's own client
                // can't fold it while drawing, but a remote fold apply or item destroy can).
                if (ghost.Foldable == null || ghost.Foldable.mapChart == null || ghost.Foldable.amount >= 1f || ghost.Root == null)
                {
                    _removalScratch.Add(kvp.Key);
                    continue;
                }

                DriveGhost(ghost);
            }

            foreach (var id in _removalScratch)
                EndSession(id);
            _removalScratch.Clear();
        }

        private void DriveGhost(GhostSet ghost)
        {
            var mapChart = ghost.Foldable.mapChart;
            var chartTransform = mapChart.transform;
            var tempLine = mapChart.chartData != null ? mapChart.chartData.tempLine : null;

            // Cursor fallback: without any ChartCursor received yet, drive the quill from the
            // synced tempLine end so the stroke still animates.
            float cx, cy;
            bool haveTarget;
            if (ghost.HasCursor)
            {
                cx = ghost.CursorX;
                cy = ghost.CursorY;
                haveTarget = true;
            }
            else if (tempLine != null)
            {
                cx = tempLine.endX;
                cy = tempLine.endY;
                haveTarget = true;
            }
            else
            {
                cx = cy = 0f;
                haveTarget = false;
            }

            // Quill: visible while the drawer holds an ink tool or is mid-stroke.
            bool quillVisible = haveTarget && (ghost.Tool == 1 || (tempLine != null && tempLine.color > 0));
            if (ghost.Quill != null)
            {
                ghost.Quill.gameObject.SetActive(quillVisible);
                if (quillVisible)
                {
                    var target = chartTransform.TransformPoint(new Vector3(cx, cy, -0.005f));
                    ghost.Quill.position = Vector3.Lerp(ghost.Quill.position, target, Time.deltaTime * QuillLerpSpeed);
                }
            }

            // Ruler: visible while a stroke is in progress, spanning tempLine.start -> cursor
            // (mirrors MapTableCamera.UpdateRuler + UpdateRulerScale; scale/variant are local fields).
            bool rulerVisible = tempLine != null;
            if (ghost.Ruler != null)
            {
                ghost.Ruler.gameObject.SetActive(rulerVisible);
                if (rulerVisible)
                {
                    var worldStart = chartTransform.TransformPoint(new Vector3(tempLine.startX, tempLine.startY, 0f));
                    float ex = ghost.HasCursor ? ghost.CursorX : tempLine.endX;
                    float ey = ghost.HasCursor ? ghost.CursorY : tempLine.endY;
                    var worldEnd = chartTransform.TransformPoint(new Vector3(ex, ey, 0f));

                    ghost.Ruler.position = worldStart;
                    if (Vector3.SqrMagnitude(worldStart - worldEnd) > 0.0001f)
                        ghost.Ruler.LookAt(worldEnd);
                    ghost.Ruler.localScale = Vector3.one * mapChart.rulerScale;
                    if (ghost.RulerSmall != null) ghost.RulerSmall.SetActive(!mapChart.useLargeRuler);
                    if (ghost.RulerLarge != null) ghost.RulerLarge.SetActive(mapChart.useLargeRuler);
                }
            }
        }

        // === Ghost construction ===

        private GhostSet BuildGhostSet(ShipItemFoldable foldable)
        {
            var root = new GameObject("CoopChartKitGhost");
            root.transform.SetParent(foldable.transform, false);
            root.transform.localPosition = Vector3.zero;
            root.transform.localRotation = Quaternion.identity;
            root.transform.localScale = Vector3.one;

            var ghost = new GhostSet { Foldable = foldable, Root = root };

            try
            {
                var cam = MapTableCamera.instance;
                if (cam == null || cam.kit == null || cam.quill == null || cam.ruler == null)
                    throw new System.Exception("MapTableCamera rig unavailable");

                // Clone the real props. They live on the map-cam-only layer 23 with interactive
                // ChartKitButtons + colliders, so strip those and relayer to Default so the clones
                // render in the main camera and never intercept clicks/raycasts.
                var kit = Instantiate(cam.kit.gameObject);
                SanitizeClone(kit);
                kit.transform.SetParent(root.transform, false);
                kit.SetActive(true);
                ghost.Kit = kit.transform;

                var quill = Instantiate(cam.quill.gameObject);
                SanitizeClone(quill);
                quill.transform.SetParent(root.transform, false);
                quill.SetActive(false);
                ghost.Quill = quill.transform;

                var ruler = Instantiate(cam.ruler.gameObject);
                SanitizeClone(ruler);
                ruler.transform.SetParent(root.transform, false);
                ruler.SetActive(false);
                ghost.Ruler = ruler.transform;

                // Locate the cloned small/large variant children by the originals' names.
                if (cam.rulerSmallScale != null)
                    ghost.RulerSmall = FindChildByName(ruler.transform, cam.rulerSmallScale.name)?.gameObject;
                if (cam.rulerLargeScale != null)
                    ghost.RulerLarge = FindChildByName(ruler.transform, cam.rulerLargeScale.name)?.gameObject;
            }
            catch (System.Exception e)
            {
                // The exact kit/quill/ruler hierarchy is only knowable at runtime; if cloning fails
                // (missing rig, render-cam-bound shader, etc.) fall back to cheap primitive proxies -
                // the sync plumbing is identical.
                Plugin.Log.LogWarning($"[NAV] Chart ghost clone failed, using primitive proxies: {e.Message}");
                BuildPrimitiveFallback(ghost);
            }

            return ghost;
        }

        private void BuildPrimitiveFallback(GhostSet ghost)
        {
            // Wipe any partial clone results, keep the root.
            for (int i = ghost.Root.transform.childCount - 1; i >= 0; i--)
                Destroy(ghost.Root.transform.GetChild(i).gameObject);
            ghost.RulerSmall = null;
            ghost.RulerLarge = null;

            // Kit: flat box.
            var kit = GameObject.CreatePrimitive(PrimitiveType.Cube);
            kit.name = "GhostKit";
            SanitizeClone(kit);
            kit.transform.SetParent(ghost.Root.transform, false);
            kit.transform.localScale = new Vector3(0.15f, 0.03f, 0.1f);
            ghost.Kit = kit.transform;

            // Quill: thin capsule standing point-down (no cone primitive in Unity).
            var quill = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            quill.name = "GhostQuill";
            SanitizeClone(quill);
            quill.transform.SetParent(ghost.Root.transform, false);
            quill.transform.localScale = new Vector3(0.006f, 0.05f, 0.006f);
            quill.transform.localEulerAngles = new Vector3(30f, 0f, 0f);
            quill.SetActive(false);
            ghost.Quill = quill.transform;

            // Ruler: empty pivot at the start point (LookAt target = cursor), with a thin
            // stretched cube child spanning +z so root scale = MapChart.rulerScale sets its length.
            var rulerRoot = new GameObject("GhostRuler");
            rulerRoot.transform.SetParent(ghost.Root.transform, false);
            var bar = GameObject.CreatePrimitive(PrimitiveType.Cube);
            SanitizeClone(bar);
            bar.transform.SetParent(rulerRoot.transform, false);
            bar.transform.localPosition = new Vector3(0f, 0.002f, 0.5f);
            bar.transform.localScale = new Vector3(0.02f, 0.004f, 1f);
            rulerRoot.SetActive(false);
            ghost.Ruler = rulerRoot.transform;
        }

        private static void SanitizeClone(GameObject go)
        {
            foreach (var button in go.GetComponentsInChildren<ChartKitButton>(true))
                Destroy(button);
            foreach (var col in go.GetComponentsInChildren<Collider>(true))
                Destroy(col);
            SetLayerRecursively(go, 0);
        }

        private static void SetLayerRecursively(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform child in go.transform)
                SetLayerRecursively(child.gameObject, layer);
        }

        private static Transform FindChildByName(Transform root, string name)
        {
            if (root.name == name) return root;
            foreach (Transform child in root)
            {
                var found = FindChildByName(child, name);
                if (found != null) return found;
            }
            return null;
        }

        private void PlaceKit(GhostSet ghost, int kitPos)
        {
            if (ghost.Kit == null) return;
            var cam = MapTableCamera.instance;
            if (cam == null) return;

            // Mirrors MapTableCamera.Update: kit.position = currentMap.TransformPoint(kitPos*) with
            // kitRot* local to the map-aligned rig; the ghost root is foldable-aligned so plain
            // localPosition/localEulerAngles reproduce it (and ride the boat via the parent chain).
            if (kitPos == -1)
            {
                ghost.Kit.localPosition = cam.kitPosLeft;
                ghost.Kit.localEulerAngles = cam.kitRotLeft;
            }
            else if (kitPos == 1)
            {
                ghost.Kit.localPosition = cam.kitPosRight;
                ghost.Kit.localEulerAngles = cam.kitRotRight;
            }
            else
            {
                ghost.Kit.localPosition = cam.kitPosTop;
                ghost.Kit.localEulerAngles = cam.kitRotTop;
            }
        }

        private ShipItemFoldable FindFoldableByInstanceId(int instanceId)
        {
            var prefabs = FindObjectsOfType<SaveablePrefab>();
            foreach (var prefab in prefabs)
            {
                if (prefab.instanceId == instanceId)
                    return prefab.GetComponent<ShipItemFoldable>();
            }
            return null;
        }
    }
}
