using System.Reflection;
using Crest;
using HarmonyLib;
using UnityEngine;

namespace SailwindCoop.Patches
{
    /// <summary>
    /// (v0.2.25) Config-gated restore of vanilla item buoyancy (Coop.RestoreItemBuoyancy, default ON).
    /// Items floating is REAL vanilla behavior: older builds' ItemRigidbody.ToggleCollider did
    /// `floater.enabled = state` (decomp_sw ItemRigidbody.cs:287), so any free in-range item floated.
    /// The current v0.38 build REGRESSED this to a hard-coded `floater.enabled = false;` (verified in
    /// the live Assembly-CSharp IL; nothing in the whole assembly ever re-enables it, and the change
    /// sits next to newly-added Debugger.disableItemRigidbody* hooks - it looks like shipped debug
    /// leftovers). Since FixedUpdate calls ToggleCollider EVERY fixed frame, the SimpleFloatingObject
    /// that ItemRigidbody.Start carefully creates (with the item's authored floaterHeight) never gets
    /// a single frame to act: dropped items/crates sink to the OceanBottom plane (~y=-19), even in
    /// SINGLEPLAYER. This postfix restores the old-build behavior, but ONLY for items that are
    /// genuinely loose in the water column's reach:
    ///   - state == true            (the collider-off paths - inventory, disableCol - stay floaterless)
    ///   - not held / not stowed    (a held or boxed/inventoried item must follow the hand/slot, not waves)
    ///   - not resting on a boat    (onBoat items ride the deck via the walkCol frames; a floater there
    ///                               would fight MoveItemToWalkColRigidbody and the co-op settle logic)
    ///   - rigidbody non-kinematic  (settled/terminal items are frozen on purpose - in co-op the
    ///                               ItemSyncManager drop-settle terminal relies on the resting pose,
    ///                               so a kinematic body is left exactly as-is)
    ///   - OceanRenderer.Instance   (no live Crest ocean = nothing to float on; also guards menus)
    /// LOCAL-ONLY: no wire change - each machine applies (or not) to its own physics; loose-item
    /// positions are host-relayed as usual, so crews mixing this setting just see the host's poses.
    /// With the config OFF this postfix returns on its first line: zero behavior change by default,
    /// and the ItemSyncManager settle-terminal tolerance (~1490-1620) is untouched.
    /// </summary>
    [HarmonyPatch(typeof(ItemRigidbody), "ToggleCollider")]
    public static class ItemBuoyancyRestorePatch
    {
        // Private vanilla fields, resolved once (this runs every fixed frame per item - keep it cheap).
        // `floater` is typed as the game's SimpleFloatingObject, a name that collides between
        // Assembly-CSharp and Crest (see FishingSyncManager), so it is read reflectively as a Behaviour
        // instead of referencing the type.
        private static readonly FieldInfo FloaterField = AccessTools.Field(typeof(ItemRigidbody), "floater");
        private static readonly FieldInfo ItemField = AccessTools.Field(typeof(ItemRigidbody), "item");
        private static readonly FieldInfo OnBoatField = AccessTools.Field(typeof(ItemRigidbody), "onBoat");
        private static readonly FieldInfo RigidbodyField = AccessTools.Field(typeof(ItemRigidbody), "rigidbody");
        private static readonly FieldInfo CurrentBoxField = AccessTools.Field(typeof(ItemRigidbody), "currentBox");
        private static readonly FieldInfo InvSlotField = AccessTools.Field(typeof(ItemRigidbody), "currentInventorySlot");

        [HarmonyPostfix]
        public static void Postfix(ItemRigidbody __instance, bool state)
        {
            // Config gate FIRST: off (default) = this postfix does nothing at all.
            if (Plugin.RestoreItemBuoyancyConfig == null || !Plugin.RestoreItemBuoyancyConfig.Value) return;

            // Collider-off calls (inventory/stove/disableCol) keep the vanilla floater-off too.
            if (!state) return;

            // No live Crest ocean = nothing to float on (menus, loading).
            if (OceanRenderer.Instance == null) return;

            var floater = FloaterField?.GetValue(__instance) as Behaviour;
            if (floater == null) return;

            var item = ItemField?.GetValue(__instance) as ShipItem;
            if (item == null || item.held != null) return;                       // held: follow the hand
            if (OnBoatField != null && (bool)OnBoatField.GetValue(__instance)) return; // on deck: ride the boat
            if (InvSlotField?.GetValue(__instance) as Transform != null) return; // stowed in inventory
            if (CurrentBoxField?.GetValue(__instance) as Transform != null) return;   // parked in a box

            // Settled/terminal items are kinematic on purpose (vanilla sleep AND the co-op drop-settle
            // terminal) - forces would be ignored anyway, so leave them exactly as vanilla left them.
            var rb = RigidbodyField?.GetValue(__instance) as Rigidbody;
            if (rb == null || rb.isKinematic) return;

            // DEPTH GUARD: never wake the floater on an item already deep underwater (pre-sunk items
            // from before the config was on, or bodies that never went kinematic at the bottom). The
            // Crest floater's buoyancy scales ~3*depth^3 - at 20m down that force would launch the
            // item out of the sea like a missile. -3m still catches a fast-dropped crate mid-plunge.
            if (__instance.transform.position.y < -3f) return;

            if (!floater.enabled)
                floater.enabled = true;
        }
    }
}
