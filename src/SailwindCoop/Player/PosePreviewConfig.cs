using BepInEx.Configuration;
using UnityEngine;

namespace SailwindCoop.Player
{
    /// <summary>
    /// Config for the pose preview puppet (<see cref="PosePreviewPuppet"/>), which is a co-op tool: it shows
    /// you what your CREWMATES see, through the same avatar code they run for you.
    ///
    /// The pose itself is tuned in the Sailwind Player Model mod's own config (section "2. Held Tool"), since
    /// that is what actually poses the arm. These three are only about the preview window onto it, and they
    /// keep the old "Held Tool Pose" section name so a saved PreviewKey carries over.
    /// </summary>
    public static class PosePreviewConfig
    {
        private const string Section = "Held Tool Pose";

        public static ConfigEntry<KeyboardShortcut> PreviewKey { get; private set; }
        public static ConfigEntry<float> PreviewDistance { get; private set; }
        public static ConfigEntry<float> PreviewYaw { get; private set; }

        public static void Bind(ConfigFile cfg)
        {
            // Home, not an F key: F1-F11 are all bound by installed mods (co-op itself holds F7-F10), F12 is
            // Steam's screenshot key, and a modifier combo would still fire another mod's plain F-key check.
            PreviewKey = cfg.Bind(Section, "PreviewKey", new KeyboardShortcut(KeyCode.Home),
                "Toggles the pose preview: a copy of your own sailor stands in front of you and copies what you do, drawn exactly the way your crewmates see you. Local only, nothing is sent.");
            PreviewDistance = cfg.Bind(Section, "PreviewDistance", 2.0f,
                new ConfigDescription("Pose preview: how far in front of you the copy stands (meters).",
                    new AcceptableValueRange<float>(1f, 5f)));
            PreviewYaw = cfg.Bind(Section, "PreviewYaw", 0f,
                new ConfigDescription("Pose preview: turns the copy (degrees). 0 faces you; 90 or -90 shows its side.",
                    new AcceptableValueRange<float>(-180f, 180f)));
        }
    }
}
