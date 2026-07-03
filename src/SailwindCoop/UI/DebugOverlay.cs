using UnityEngine;
using SailwindCoop.Networking;
using SailwindCoop.Player;
using SailwindCoop.Sync;
using SailwindCoop.Debug;
using Steamworks;
using System.Collections.Generic;
using System.Linq;

namespace SailwindCoop.UI
{
    public class DebugOverlay : MonoBehaviour
    {
        private GUIStyle _boxStyle;
        private GUIStyle _labelStyle;
        private GUIStyle _headerStyle;
        private GUIStyle _itemLabelStyle;
        private GUIStyle _perfLabelStyle;
        private GUIStyle _perfValueStyle;
        private GUIStyle _perfImpactStyle;
        private bool _stylesInitialized;

        // Cache to avoid GetComponent in OnGUI
        private Transform _lastBoatTransform;
        private Rigidbody _cachedBoatRb;

        // Scroll position for overlay content
        private Vector2 _scrollPosition;

        // Item label cache (refreshed periodically)
        private float _lastItemScanTime;
        private const float ItemScanInterval = 0.5f; // Scan every 0.5s
        private const float ItemLabelRange = 15f; // Show labels within 15m
        private List<ItemLabelInfo> _itemLabels = new List<ItemLabelInfo>();
        private Dictionary<int, int> _idCounts = new Dictionary<int, int>(); // Track duplicate IDs

        private struct ItemLabelInfo
        {
            public Transform Transform;
            public int InstanceId;
            public int PrefabIndex;
            public string ItemName;
            public bool IsRegistered;
            public bool IsDuplicate;
        }

        // Per-peer ping lines ("ping <name>: <n> ms"), rebuilt at the ping-loop cadence (2s) so
        // OnGUI never allocates strings per frame. Host shows one line per guest; guest shows host.
        private readonly List<string> _pingLines = new List<string>();
        private float _lastPingLineTime;
        private const float PingLineInterval = 2f; // Matches Plugin's ping-send cadence

        // Baseline FPS tracking (when not in multiplayer)
        private float _baselineFps;
        private float _baselineFrameMs;
        private bool _hasBaseline;

        private void InitStyles()
        {
            if (_stylesInitialized) return;

            _boxStyle = new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(10, 10, 10, 10)
            };

            _labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 16,
                normal = { textColor = Color.white }
            };

            _headerStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 18,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.cyan }
            };

            _itemLabelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Color.white }
            };
            // Add background for readability
            _itemLabelStyle.normal.background = MakeTexture(2, 2, new Color(0, 0, 0, 0.7f));
            _itemLabelStyle.padding = new RectOffset(4, 4, 2, 2);

            // Performance table styles
            _perfLabelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                normal = { textColor = Color.white },
                alignment = TextAnchor.MiddleLeft
            };

            _perfValueStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                normal = { textColor = Color.yellow },
                alignment = TextAnchor.MiddleRight
            };

            _perfImpactStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                normal = { textColor = Color.green },
                alignment = TextAnchor.MiddleRight
            };

            _stylesInitialized = true;
        }

        private Texture2D MakeTexture(int width, int height, Color color)
        {
            var pixels = new Color[width * height];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = color;
            var tex = new Texture2D(width, height);
            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F8))
            {
                // F8 = toggle debug LOGGING (background) + a small corner indicator.
                // Shift+F8 = toggle the full on-screen overlay (only while logging is on).
                bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                if (shift)
                {
                    if (DebugMode.Enabled) DebugMode.ShowOverlay = !DebugMode.ShowOverlay;
                }
                else
                {
                    DebugMode.Toggle();
                }
            }

            if (Input.GetKeyDown(KeyCode.F7) && DebugMode.Enabled)
            {
                LogPerformanceSnapshot();
                Plugin.Log.LogInfo("[DEBUG] Performance snapshot logged (check BepInEx log)");
            }

            // Track baseline FPS when NOT in multiplayer
            if (!Plugin.IsMultiplayer && DebugMode.Enabled)
            {
                var profiler = PerformanceProfiler.Instance;
                if (profiler != null)
                {
                    float fps = profiler.GetFps();
                    if (fps > 10) // Valid reading
                    {
                        _baselineFps = fps;
                        _baselineFrameMs = profiler.GetAvgFrameMs();
                        _hasBaseline = true;
                    }
                }
            }

            // Scan items periodically ONLY when the full overlay is shown (the labels it feeds are hidden
            // in soft-debug mode, so skip the work entirely there).
            if (DebugMode.Enabled && DebugMode.ShowOverlay && Plugin.IsMultiplayer && Time.time - _lastItemScanTime > ItemScanInterval)
            {
                ScanNearbyItems();
                _lastItemScanTime = Time.time;
            }
        }

        private void ScanNearbyItems()
        {
            _itemLabels.Clear();
            _idCounts.Clear();

            var cam = Camera.main;
            if (cam == null) return;

            var camPos = cam.transform.position;
            var allItems = Object.FindObjectsOfType<ShipItem>();

            // First pass: count IDs to detect duplicates
            foreach (var item in allItems)
            {
                if (item == null) continue;
                var prefab = item.GetComponent<SaveablePrefab>();
                if (prefab == null || prefab.instanceId == 0) continue;

                if (!_idCounts.ContainsKey(prefab.instanceId))
                    _idCounts[prefab.instanceId] = 0;
                _idCounts[prefab.instanceId]++;
            }

            // Second pass: build label info for nearby items
            foreach (var item in allItems)
            {
                if (item == null) continue;

                var dist = Vector3.Distance(camPos, item.transform.position);
                if (dist > ItemLabelRange) continue;

                var prefab = item.GetComponent<SaveablePrefab>();
                if (prefab == null) continue;

                var instanceId = prefab.instanceId;
                var isRegistered = ItemSyncManager.Instance?.IsItemRegistered(instanceId) ?? false;
                var isDuplicate = _idCounts.TryGetValue(instanceId, out var count) && count > 1;

                _itemLabels.Add(new ItemLabelInfo
                {
                    Transform = item.transform,
                    InstanceId = instanceId,
                    PrefabIndex = prefab.prefabIndex,
                    ItemName = item.name,
                    IsRegistered = isRegistered,
                    IsDuplicate = isDuplicate
                });
            }
        }

        private void LogPerformanceSnapshot()
        {
            var profiler = PerformanceProfiler.Instance;
            if (profiler == null)
            {
                Plugin.Log.LogInfo("[PERF] Profiler not initialized");
                return;
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("\n========== PERFORMANCE SNAPSHOT ==========");
            sb.AppendLine($"Multiplayer: {Plugin.IsMultiplayer}, IsHost: {Plugin.IsHost}");
            sb.AppendLine($"Overlay: {(DebugMode.Enabled ? "ON" : "OFF")}");

            // Current FPS
            float fps = profiler.GetFps();
            float avgMs = profiler.GetAvgFrameMs();
            float maxMs = profiler.GetMaxFrameMs();
            sb.AppendLine($"\nFPS: {fps:F1} ({avgMs:F2}ms avg, {maxMs:F2}ms max)");

            // Baseline comparison
            if (_hasBaseline)
            {
                float fpsLoss = _baselineFps - fps;
                float msOverhead = avgMs - _baselineFrameMs;
                sb.AppendLine($"Baseline: {_baselineFps:F1} FPS ({_baselineFrameMs:F2}ms)");
                sb.AppendLine($"FPS Loss: {fpsLoss:F1} FPS ({msOverhead:F2}ms overhead)");
            }
            else
            {
                sb.AppendLine("Baseline: Not recorded (enable overlay in singleplayer first)");
            }

            // Sync manager times
            var systemTimes = profiler.GetSystemTimes();
            if (systemTimes.Count > 0)
            {
                sb.AppendLine("\n--- Sync Managers (ms/frame) ---");
                float syncTotal = 0f;
                foreach (var kvp in systemTimes.OrderByDescending(x => x.Value))
                {
                    sb.AppendLine($"  {kvp.Key}: {kvp.Value:F3}ms");
                    syncTotal += kvp.Value;
                }
                sb.AppendLine($"  SUBTOTAL: {syncTotal:F3}ms");
            }

            // Patch profiler times
            var patchStats = PatchProfiler.GetDisplayStats().ToList();
            if (patchStats.Count > 0)
            {
                sb.AppendLine("\n--- Harmony Patches (ms/frame) ---");
                float patchTotal = 0f;
                foreach (var stat in patchStats.OrderByDescending(s => s.TotalMs))
                {
                    sb.AppendLine($"  {stat.Name}: {stat.TotalMs:F3}ms ({stat.CallCount} calls/f, {stat.AvgMicroseconds:F1}µs/call)");
                    patchTotal += stat.TotalMs;
                }
                sb.AppendLine($"  SUBTOTAL: {patchTotal:F3}ms");
            }

            // Combined totals
            float measuredTotal = profiler.GetCombinedTotalMs();
            sb.AppendLine($"\n--- Totals ---");
            sb.AppendLine($"Measured overhead: {measuredTotal:F2}ms");

            if (_hasBaseline && Plugin.IsMultiplayer)
            {
                float actualOverhead = avgMs - _baselineFrameMs;
                float unmeasured = System.Math.Max(0, actualOverhead - measuredTotal);
                sb.AppendLine($"Actual overhead: {actualOverhead:F2}ms");
                sb.AppendLine($"UNMEASURED: {unmeasured:F2}ms ({(unmeasured / actualOverhead * 100):F0}% of overhead)");
            }

            sb.AppendLine("============================================");

            Plugin.Log.LogInfo(sb.ToString());
        }

        private void OnGUI()
        {
            // ALWAYS-ON warning (no F8 needed): if the mod's Steam never initialized, co-op is dead and
            // every pause-menu lobby button silently no-ops. Tell the player why, persistently, in-game,
            // so it can never again look like an unexplained dead button.
            if (!Plugin.SteamReady && GameState.playing)
            {
                InitStyles();
                var prevWarn = GUI.color;
                GUI.color = new Color(1f, 0.4f, 0.4f, 0.95f);
                GUI.Label(new Rect(10f, Screen.height - 48f, 820f, 22f),
                    "[!] Co-op disabled: Steam didn't initialize for the mod - re-extract the FULL mod zip and relaunch via Steam.",
                    _labelStyle);
                GUI.color = prevWarn;
            }

            if (!DebugMode.Enabled) return;

            InitStyles();

            // Logging-only mode: logging is on but the full overlay is hidden - draw only a small corner
            // indicator so the screen stays clean during playtests. Shift+F8 brings up the full overlay.
            if (!DebugMode.ShowOverlay)
            {
                var prevColor = GUI.color;
                GUI.color = new Color(1f, 0.55f, 0.55f, 0.85f);
                GUI.Label(new Rect(10f, Screen.height - 26f, 380f, 22f),
                    "● co-op debug logging ON  (Shift+F8: overlay)", _labelStyle);
                GUI.color = prevColor;
                return;
            }

            Plugin.Profiler?.StartMeasure();

            var lobbyManager = Plugin.LobbyManager;
            var networkManager = Plugin.NetworkManager;

            float width = 340;
            float maxHeight = Screen.height - 40; // Leave margin at top and bottom
            float x = Screen.width - width - 10;
            float y = 10;

            GUILayout.BeginArea(new Rect(x, y, width, maxHeight), _boxStyle);

            GUILayout.Label($"Sailwind Coop v{Plugin.PluginVersion} - Debug", _headerStyle);
            GUILayout.Space(5);

            // Scrollable content area
            _scrollPosition = GUILayout.BeginScrollView(_scrollPosition, GUILayout.Width(width - 20), GUILayout.Height(maxHeight - 80));

            if (lobbyManager == null)
            {
                GUILayout.Label("Steam: Not initialized", _labelStyle);
            }
            else
            {
                GUILayout.Label(lobbyManager.IsInitialized
                    ? "Steam: Connected"
                    : $"Steam: NOT INITIALIZED - {lobbyManager.LastInitError}", _labelStyle);
                GUILayout.Label($"Lobby: {(lobbyManager.IsInLobby ? "In lobby" : "None")}", _labelStyle);

                if (lobbyManager.IsInLobby)
                {
                    GUILayout.Label($"Role: {(lobbyManager.IsHost ? "Host" : "Guest")}", _labelStyle);
                    GUILayout.Label($"Members: {lobbyManager.GetMemberCount()}/{SteamLobbyManager.MaxPlayers}", _labelStyle);

                    if (networkManager != null)
                    {
                        GUILayout.Label($"P2P Peers: {networkManager.ConnectedPeers.Count}", _labelStyle);

                        // Per-peer ping (fed by the 2s PingRequest/PingReply loop in Plugin.Update).
                        // Rebuild the cached lines at the same cadence; draw from cache every frame.
                        if (Time.realtimeSinceStartup - _lastPingLineTime > PingLineInterval)
                        {
                            _lastPingLineTime = Time.realtimeSinceStartup;
                            RebuildPingLines(networkManager);
                        }
                        for (int i = 0; i < _pingLines.Count; i++)
                        {
                            GUILayout.Label(_pingLines[i], _labelStyle);
                        }
                    }

                    var remoteManager = RemotePlayerManager.Instance;
                    if (remoteManager != null)
                    {
                        GUILayout.Label($"Remote Players: {remoteManager.RemotePlayerCount}", _labelStyle);
                    }
                }
            }

            // Boat info (condensed)
            if (GameState.currentBoat != null)
            {
                var boat = GameState.lastBoat;
                if (boat != null)
                {
                    if (_lastBoatTransform != boat)
                    {
                        _lastBoatTransform = boat;
                        _cachedBoatRb = boat.GetComponent<Rigidbody>();
                    }
                    var vel = _cachedBoatRb?.velocity.magnitude ?? 0f;
                    GUILayout.Label($"Boat: {vel:F1}m/s | Kinematic: {_cachedBoatRb?.isKinematic}", _labelStyle);
                }
            }

            // Performance section
            GUILayout.Space(10);
            DrawPerformanceSection();

            GUILayout.EndScrollView();

            GUILayout.Label("F7: Snapshot | Shift+F8: Hide overlay | F8: Stop logging", _labelStyle);

            GUILayout.EndArea();

            // Draw floating item labels
            if (Plugin.IsMultiplayer)
            {
                DrawItemLabels();
            }

            Plugin.Profiler?.EndMeasure("OnGUI");
        }

        private void RebuildPingLines(P2PNetworkManager networkManager)
        {
            _pingLines.Clear();
            foreach (var peer in networkManager.ConnectedPeers)
            {
                // Friend.Name reads "[unknown]" until Steam loads that user's persona (same caveat
                // as SteamLobbyManager's invite toast) - fall back to the raw SteamId digits.
                string name = new Friend(peer).Name;
                if (string.IsNullOrEmpty(name) || name == "[unknown]") name = peer.Value.ToString();

                _pingLines.Add(NetworkStats.PingMs.TryGetValue(peer, out var ms)
                    ? $"ping {name}: {ms:F0} ms"
                    : $"ping {name}: -- ms");
            }
        }

        private void DrawPerformanceSection()
        {
            var profiler = PerformanceProfiler.Instance;
            if (profiler == null)
            {
                GUILayout.Label("Profiler: Not initialized", _labelStyle);
                return;
            }

            float fps = profiler.GetFps();
            float avgMs = profiler.GetAvgFrameMs();
            float maxMs = profiler.GetMaxFrameMs();

            GUILayout.Label($"Performance ({fps:F0} FPS, {avgMs:F1}ms avg)", _headerStyle);

            // === Sync Managers Section ===
            var systemTimes = profiler.GetSystemTimes();
            if (systemTimes.Count > 0)
            {
                GUILayout.Label("Sync Managers:", _labelStyle);

                // Draw table header
                GUILayout.BeginHorizontal();
                GUILayout.Label("System", _perfLabelStyle, GUILayout.Width(100));
                GUILayout.Label("ms/f", _perfValueStyle, GUILayout.Width(50));
                GUILayout.EndHorizontal();

                // Sort by time descending, show top 5
                var sorted = systemTimes.OrderByDescending(kvp => kvp.Value).Take(5).ToList();
                foreach (var kvp in sorted)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(kvp.Key, _perfLabelStyle, GUILayout.Width(100));
                    GUILayout.Label($"{kvp.Value:F2}", _perfValueStyle, GUILayout.Width(50));
                    GUILayout.EndHorizontal();
                }

                float syncTotal = profiler.GetTotalModMs();
                GUILayout.BeginHorizontal();
                GUILayout.Label("  Subtotal", _perfLabelStyle, GUILayout.Width(100));
                GUILayout.Label($"{syncTotal:F2}", _perfValueStyle, GUILayout.Width(50));
                GUILayout.EndHorizontal();
            }

            // === Harmony Patches Section ===
            float patchMs = profiler.GetPatchOverheadMs();
            if (patchMs > 0)
            {
                GUILayout.Space(5);
                GUILayout.Label("Harmony Patches:", _labelStyle);

                // Draw table header
                GUILayout.BeginHorizontal();
                GUILayout.Label("Patch", _perfLabelStyle, GUILayout.Width(100));
                GUILayout.Label("ms/f", _perfValueStyle, GUILayout.Width(50));
                GUILayout.EndHorizontal();

                // Show top patch offenders
                var patchStats = PatchProfiler.GetDisplayStats().Take(3).ToList();
                foreach (var stat in patchStats)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"  {stat.Name}", _perfLabelStyle, GUILayout.Width(100));
                    GUILayout.Label($"{stat.TotalMs:F2}", _perfValueStyle, GUILayout.Width(50));
                    GUILayout.EndHorizontal();
                }

                GUILayout.BeginHorizontal();
                GUILayout.Label("  Subtotal", _perfLabelStyle, GUILayout.Width(100));
                GUILayout.Label($"{patchMs:F2}", _perfValueStyle, GUILayout.Width(50));
                GUILayout.EndHorizontal();
            }

            // === Totals ===
            GUILayout.Space(5);
            float measuredMs = profiler.GetCombinedTotalMs();

            // Calculate actual overhead from baseline
            float currentFps = fps;
            float actualFpsLoss = 0f;
            float actualOverheadMs = 0f;
            float unmeasuredMs = 0f;

            if (_hasBaseline && Plugin.IsMultiplayer && _baselineFps > currentFps)
            {
                actualFpsLoss = _baselineFps - currentFps;
                actualOverheadMs = avgMs - _baselineFrameMs;
                unmeasuredMs = Mathf.Max(0, actualOverheadMs - measuredMs);
            }

            var origColor = _perfValueStyle.normal.textColor;

            // Measured total
            GUILayout.BeginHorizontal();
            GUILayout.Label("Measured", _perfLabelStyle, GUILayout.Width(100));
            GUILayout.Label($"{measuredMs:F1}ms", _perfValueStyle, GUILayout.Width(50));
            GUILayout.EndHorizontal();

            // Unmeasured (Harmony interception overhead)
            if (unmeasuredMs > 0.5f)
            {
                _perfValueStyle.normal.textColor = Color.red;
                GUILayout.BeginHorizontal();
                GUILayout.Label("Unmeasured", _perfLabelStyle, GUILayout.Width(100));
                GUILayout.Label($"{unmeasuredMs:F1}ms", _perfValueStyle, GUILayout.Width(50));
                GUILayout.EndHorizontal();
                _perfValueStyle.normal.textColor = origColor;
            }

            // Actual total and FPS impact
            if (_hasBaseline && Plugin.IsMultiplayer)
            {
                _perfValueStyle.normal.textColor = actualOverheadMs > 10f ? Color.red : (actualOverheadMs > 5f ? Color.yellow : Color.green);

                GUILayout.BeginHorizontal();
                GUILayout.Label("ACTUAL", _perfLabelStyle, GUILayout.Width(100));
                GUILayout.Label($"{actualOverheadMs:F1}ms", _perfValueStyle, GUILayout.Width(50));
                GUILayout.Label($"-{actualFpsLoss:F0}fps", _perfImpactStyle, GUILayout.Width(50));
                GUILayout.EndHorizontal();

                // Show baseline for reference
                _perfValueStyle.normal.textColor = Color.gray;
                GUILayout.Label($"(baseline: {_baselineFps:F0} FPS)", _perfLabelStyle);
            }
            else if (!Plugin.IsMultiplayer)
            {
                GUILayout.Label("(recording baseline...)", _perfLabelStyle);
            }

            _perfValueStyle.normal.textColor = origColor;
        }

        private void DrawItemLabels()
        {
            var cam = Camera.main;
            if (cam == null) return;

            foreach (var info in _itemLabels)
            {
                if (info.Transform == null) continue;

                // Get screen position (above the item)
                var worldPos = info.Transform.position + Vector3.up * 0.5f;
                var screenPos = cam.WorldToScreenPoint(worldPos);

                // Skip if behind camera
                if (screenPos.z < 0) continue;

                // Convert to GUI coordinates (Y is flipped)
                screenPos.y = Screen.height - screenPos.y;

                // Determine color based on status
                Color labelColor;
                string statusIcon;
                if (info.IsDuplicate)
                {
                    labelColor = new Color(1f, 0.5f, 0f); // Orange for duplicates
                    statusIcon = "DUP";
                }
                else if (info.IsRegistered)
                {
                    labelColor = Color.green;
                    statusIcon = "OK";
                }
                else
                {
                    labelColor = Color.red;
                    statusIcon = "NO";
                }

                // Build label text
                var shortId = info.InstanceId % 100000; // Show last 5 digits for readability
                var labelText = $"[{statusIcon}] {shortId}\n{info.ItemName}";

                // Calculate label size
                var content = new GUIContent(labelText);
                var size = _itemLabelStyle.CalcSize(content);

                // Draw label centered at screen position
                var rect = new Rect(screenPos.x - size.x / 2, screenPos.y - size.y / 2, size.x, size.y);

                // Set color and draw
                var origColor = GUI.color;
                GUI.color = labelColor;
                GUI.Label(rect, labelText, _itemLabelStyle);
                GUI.color = origColor;
            }
        }
    }
}
