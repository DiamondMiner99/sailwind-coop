using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace SailwindCoop.UI
{
    /// <summary>
    /// The co-op entry point on the TITLE menu, plus the world-space menu helpers CoopPauseMenu shares.
    ///
    /// (v0.3.0) THIS USED TO BE A DELIBERATE NO-OP. The old comment said co-op lived only in the in-game
    /// pause menu because "both players must be in their loaded world for a join to work", and at the time
    /// that was true. It is not any more: TitleJoinManager loads the phantom world for a guest who accepts
    /// from the title screen, and that path is exercised every time someone accepts a Steam overlay invite
    /// from the main menu.
    ///
    /// So the only thing left of the old restriction was a dead end: a player sitting at the title screen
    /// was told "open the menu to join" by an invite toast, with no menu to open. This is that menu.
    /// </summary>
    public static class CoopMenu
    {
        public const string Primary = "coop_primary"; // retained for click-router back-compat (unused)
        public const string Friends = "coop_title_friends";

        private static readonly AccessTools.FieldRef<StartMenuButton, StartMenuButtonType> ButtonTypeRef =
            AccessTools.FieldRefAccess<StartMenuButton, StartMenuButtonType>("type");

        private static bool _installed;

        /// <summary>
        /// Add a Friends button to the title menu, between the existing entries rather than after them.
        ///
        /// The column is RE-LAID-OUT rather than extended. The title scroll is a fixed piece of parchment
        /// and the button count is not constant (Continue hides itself when there are no saves - vanilla
        /// StartMenuButton.Awake), so appending a fifth entry at the existing pitch would have pushed the
        /// bottom one through the scroll's lower roll on exactly the installs that have the most buttons.
        /// Fitting N+1 into the span the original N occupied keeps every case inside the parchment.
        /// </summary>
        public static void Install(MonoBehaviour startMenu)
        {
            if (_installed || startMenu == null) return;
            try
            {
                var startUI = FindChild(startMenu.transform, "start UI");
                if (startUI == null) { Plugin.Log.LogWarning("[CoopMenu] 'start UI' not found; no title Friends button"); return; }

                // The StartMenuButton sits on a 'bg+trigger' child; the PLATE is its parent, and the plate is
                // what carries the position and the text (vanilla's SetButtonText walks transform.parent).
                var rows = new List<Transform>();
                Transform settingsRow = null, template = null;
                foreach (var b in startUI.GetComponentsInChildren<StartMenuButton>(true))
                {
                    var row = b.transform.parent;
                    if (row == null || !row.gameObject.activeSelf) continue;
                    var type = ButtonTypeRef(b);
                    // Slot buttons live on the save-slot page, which is a different panel under this root.
                    if (type == StartMenuButtonType.Slot || type == StartMenuButtonType.LoadBackupSave) continue;
                    if (!rows.Contains(row)) rows.Add(row);
                    if (type == StartMenuButtonType.Settings) { settingsRow = row; template = row; }
                }

                if (rows.Count < 2 || settingsRow == null || template == null)
                {
                    Plugin.Log.LogWarning($"[CoopMenu] title layout not recognized ({rows.Count} rows, settings={settingsRow != null}); no Friends button");
                    return;
                }

                rows.Sort((a, b) => b.localPosition.y.CompareTo(a.localPosition.y)); // top-down

                float top = rows[0].localPosition.y;
                float bottom = rows[rows.Count - 1].localPosition.y;
                float span = top - bottom;
                float x = rows[0].localPosition.x;
                float z = rows[0].localPosition.z;

                var clone = Object.Instantiate(template.gameObject, template.parent);
                clone.name = Friends;
                clone.transform.localRotation = template.localRotation;
                clone.transform.localScale = template.localScale;
                SetLabel(clone.transform, "Friends");

                // No Invites row here. The title screen had one and players found it confusing; invites are
                // answered from the pause menu's Invites button once a world is loaded.

                // Directly above Settings: below New Game as asked, and unambiguous whether or not Continue
                // is present (an install with no saves hides Continue entirely).
                int at = rows.IndexOf(settingsRow);
                rows.Insert(at, clone.transform);

                // Same span, more entries. Equal spacing, ends unmoved.
                float step = span / (rows.Count - 1);
                for (int i = 0; i < rows.Count; i++)
                    rows[i].localPosition = new Vector3(x, top - step * i, z);
                FitPlateHeights(rows, step);

                _installed = true;
                Plugin.Log.LogInfo($"[CoopMenu] Title Friends button installed ({rows.Count} rows, step={step:F3})");
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogError("[CoopMenu] Could not install the title Friends button: " + e);
            }
        }

        /// <summary>
        /// Make the plates shorter when the column is packed tighter than a plate is tall, so neighbors do
        /// not overlap (the overlapping strips drew lighter). Only the plate (every child but "text") is shortened; the text
        /// keeps its size. Each row's plate is measured from its own meshes, so a column with room to spare
        /// (no Continue button on an install without saves) is left alone.
        /// </summary>
        private static void FitPlateHeights(List<Transform> rows, float step)
        {
            float tallest = 0f;
            foreach (var row in rows) tallest = Mathf.Max(tallest, PlateHeight(row));
            float target = step * 0.9f;
            if (tallest <= target || tallest <= 0f) return;

            float f = target / tallest;
            foreach (var row in rows)
            {
                if (row.GetComponent<MeshFilter>() != null)
                {
                    // The plate mesh is on the row itself: shorten the row and give the text its height back.
                    var rs = row.localScale;
                    row.localScale = new Vector3(rs.x, rs.y * f, rs.z);
                    var text = row.Find("text");
                    if (text != null)
                    {
                        var ts = text.localScale;
                        text.localScale = new Vector3(ts.x, ts.y / f, ts.z);
                    }
                    continue;
                }
                for (int i = 0; i < row.childCount; i++)
                {
                    var child = row.GetChild(i);
                    if (child.name == "text" || child.GetComponentInChildren<TextMesh>(true) != null) continue;
                    var s = child.localScale;
                    child.localScale = new Vector3(s.x, s.y * f, s.z);
                    var p = child.localPosition;
                    child.localPosition = new Vector3(p.x, p.y * f, p.z);
                }
            }
            Plugin.Log.LogInfo($"[CoopMenu] Title plates shortened to {f:P0} to fit a step of {step:F3} (plate was {tallest:F3} tall)");
        }

        /// <summary>A row's plate height in the column's own space, from every mesh under it except the text.</summary>
        private static float PlateHeight(Transform row)
        {
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (var mf in row.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null || mf.GetComponent<TextMesh>() != null) continue;
                Bounds b = mf.sharedMesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var corner = new Vector3(
                        (i & 1) == 0 ? b.min.x : b.max.x,
                        (i & 2) == 0 ? b.min.y : b.max.y,
                        (i & 4) == 0 ? b.min.z : b.max.z);
                    float y = row.parent.InverseTransformPoint(mf.transform.TransformPoint(corner)).y;
                    lo = Mathf.Min(lo, y);
                    hi = Mathf.Max(hi, y);
                }
            }
            return hi > lo ? hi - lo : 0f;
        }

        public static bool HandleClick(string name)
        {
            if (name != Friends) return false;
            try
            {
                // Same gate the pause menu uses: without Steam there is nothing to show, and the readiness
                // check is what surfaces WHY (missing native library, Steam not running) instead of opening
                // an empty list.
                if (!Plugin.EnsureCoopReady()) return true;
                FriendsScreen.Open();
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogError("[CoopMenu] Friends button failed: " + e);
            }
            return true;
        }

        // --- shared helpers ---
        // One copy, in the Sailwind Player Model mod, so a button this mod clones onto the title screen and
        // a button it registers on the shared pause menu are built exactly the same way.

        public static void EnsureButton(Transform panel, Transform template, string name, Vector3 localPos)
            => SailwindPlayerModel.MenuUtil.EnsureButton(panel, template, name, localPos);

        public static void SetLabel(Transform button, string text)
            => SailwindPlayerModel.MenuUtil.SetLabel(button, text);

        public static Transform FindChild(Transform root, string name)
            => SailwindPlayerModel.MenuUtil.FindChild(root, name);
    }
}
