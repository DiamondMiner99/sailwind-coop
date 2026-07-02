using System;

namespace SailwindCoop.Debug
{
    /// <summary>
    /// Central debug mode toggle. When disabled, all debug features are no-ops.
    /// Toggle with F8 key.
    /// </summary>
    public static class DebugMode
    {
        /// <summary>
        /// When false, PatchProfiler, VerboseLogger, and overlay are disabled.
        /// F8 toggles this (background logging + a small corner indicator, no full overlay).
        /// </summary>
        public static bool Enabled { get; private set; } = false;

        /// <summary>
        /// Whether the FULL on-screen debug overlay (the HUD panel + floating item labels) is drawn.
        /// Independent of <see cref="Enabled"/>: F8 turns logging on with this OFF (clean screen),
        /// Shift+F8 toggles this while logging is enabled. Forced off when logging is disabled.
        /// </summary>
        public static bool ShowOverlay { get; set; } = false;

        /// <summary>
        /// Fired when debug mode is toggled. Parameter is new state.
        /// </summary>
        public static event Action<bool> OnToggled;

        /// <summary>
        /// Toggle debug mode on/off. Resets profiler stats when enabling.
        /// </summary>
        public static void Toggle()
        {
            Enabled = !Enabled;

            if (Enabled)
            {
                // Initialize verbose logger when enabling
                VerboseLogger.Initialize();
            }
            else
            {
                // Shutdown verbose logger when disabling
                VerboseLogger.Shutdown();
                ShowOverlay = false; // hide the full overlay whenever logging is turned off
            }

            OnToggled?.Invoke(Enabled);

            Plugin.Log.LogInfo($"[DEBUG] Debug mode {(Enabled ? "ENABLED" : "DISABLED")}");
        }

        /// <summary>
        /// Set debug mode to specific state.
        /// </summary>
        public static void SetEnabled(bool enabled)
        {
            if (Enabled != enabled)
            {
                Toggle();
            }
        }
    }
}
