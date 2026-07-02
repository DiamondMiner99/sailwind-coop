using UnityEngine;

namespace SailwindCoop.UI
{
    /// <summary>
    /// Shared world-space menu helpers used by CoopPauseMenu. Co-op now lives ONLY in the in-game
    /// pause menu (CoopPauseMenu): both players must be in their loaded world for a join to work
    /// (a guest is teleported onto the host's boat), so the title main menu has no co-op button.
    /// Install/Tick/HandleClick are intentional no-ops kept so existing call sites still compile.
    /// </summary>
    public static class CoopMenu
    {
        public const string Primary = "coop_primary"; // retained for click-router back-compat (unused)

        public static void Install(MonoBehaviour startMenu) { /* no title-menu co-op button */ }
        public static void Tick() { }
        public static bool HandleClick(string name) { return false; }

        // --- shared helpers ---

        public static void EnsureButton(Transform panel, Transform template, string name, Vector3 localPos)
        {
            if (FindChild(panel, name) != null) return;
            var clone = Object.Instantiate(template.gameObject, panel); // keeps native StartMenuButton + layer 5
            clone.name = name;
            clone.transform.localRotation = template.localRotation;
            clone.transform.localScale = template.localScale;
            clone.transform.localPosition = localPos;
        }

        public static void SetLabel(Transform button, string text)
        {
            var t = button.Find("text");
            if (t == null) return;
            var tm = t.GetComponent<TextMesh>();
            if (tm != null) tm.text = text;
        }

        public static Transform FindChild(Transform root, string name)
        {
            var d = root.Find(name);
            if (d != null) return d;
            for (int i = 0; i < root.childCount; i++)
            {
                var r = FindChild(root.GetChild(i), name);
                if (r != null) return r;
            }
            return null;
        }
    }
}
