using System;
using BepInEx;
using BepInEx.Logging;
using UnityEngine;

namespace GameInspector
{
    [BepInPlugin("com.sailwindcoop.gameinspector", "Game Inspector", "1.0.0")]
    public class GameInspectorPlugin : BaseUnityPlugin
    {
        public static ManualLogSource Log { get; private set; }
        public static GameInspectorPlugin Instance { get; private set; }

        private HttpServer _httpServer;

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            Log.LogInfo("Game Inspector loading...");

            // Initialize main thread dispatcher
            _ = MainThreadDispatcher.Instance;
            Log.LogInfo("Main thread dispatcher ready");

            // Start HTTP server
            try
            {
                _httpServer = new HttpServer(7890);
                _httpServer.Start();
            }
            catch (Exception ex)
            {
                Log.LogError($"Failed to start HTTP server: {ex}");
            }
        }

        private void OnDestroy()
        {
            _httpServer?.Dispose();
            Log.LogInfo("Game Inspector unloading...");
        }
    }
}
