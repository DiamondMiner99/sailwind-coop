using System;
using System.Collections.Concurrent;
using UnityEngine;

namespace GameInspector
{
    public class MainThreadDispatcher : MonoBehaviour
    {
        private static MainThreadDispatcher _instance;
        private readonly ConcurrentQueue<Action> _actions = new ConcurrentQueue<Action>();

        public static MainThreadDispatcher Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("GameInspector_MainThreadDispatcher");
                    DontDestroyOnLoad(go);
                    _instance = go.AddComponent<MainThreadDispatcher>();
                }
                return _instance;
            }
        }

        public void Enqueue(Action action)
        {
            _actions.Enqueue(action);
        }

        private void Update()
        {
            while (_actions.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    GameInspectorPlugin.Log.LogError($"MainThreadDispatcher error: {ex}");
                }
            }
        }

        private void OnDestroy()
        {
            _instance = null;
        }
    }
}
