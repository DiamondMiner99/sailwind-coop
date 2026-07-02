using HarmonyLib;

namespace SailwindCoop.Patches
{
    /// <summary>
    /// PHANTOM CO-OP SAVE - path redirection. Postfixes EVERY path-producing method on <c>SaveSlots</c>
    /// so that whenever the relevant slot is the reserved phantom id (99), the returned file path is the
    /// hidden co-op file (<c>coop_session.save</c> + backups) instead of "/slot99.save". With
    /// <c>SaveSlots.currentSlot == 99</c> for the whole guest session, this means every vanilla save
    /// (autosave, sleep-save, Quit-save) and our explicit SaveCoopSession structurally write the phantom
    /// file - it is impossible for any write to land on a real slot (0..5).
    ///
    /// Host and solo play are unaffected: their currentSlot is always 0..5, so <c>IsPhantom</c> is false
    /// and these postfixes return the path unchanged. The patches are pure path rewrites and never index
    /// <c>slotsActive[]</c> (which is length 6) - the menu/UI still only ever enumerates slots 0..5.
    /// </summary>
    public static class SaveSlotsPatches
    {
        // GetCurrentSavePath() uses the static SaveSlots.currentSlot. Redirect when the live slot is 99.
        [HarmonyPatch(typeof(SaveSlots), "GetCurrentSavePath")]
        public static class GetCurrentSavePathPatch
        {
            static void Postfix(ref string __result)
            {
                // Gate on ContextActive so a STALE lingering currentSlot==99 (e.g. left by a
                // BepInEx hot-reload mid guest session, where the delayed Application.Quit never fired) can't
                // redirect a later non-co-op solo save into the phantom file and contaminate it. During a live
                // guest session ContextActive is true, so behavior is unchanged. (Explicit-slot variants below
                // are NOT gated: a caller explicitly asking for slot 99 genuinely wants the phantom path.)
                if (CoopSave.ContextActive && CoopSave.IsPhantom(SaveSlots.currentSlot))
                    __result = CoopSave.PhantomSavePath();
            }
        }

        // GetSlotSavePath(int slot) takes an explicit slot. Redirect when THAT slot is 99.
        [HarmonyPatch(typeof(SaveSlots), "GetSlotSavePath")]
        public static class GetSlotSavePathPatch
        {
            static void Postfix(int slot, ref string __result)
            {
                if (CoopSave.IsPhantom(slot))
                    __result = CoopSave.PhantomSavePath();
            }
        }

        // GetBackupPath(int slot, int backupIndex). Redirect when THAT slot is 99. This covers both the
        // PushBackups rotation (GetBackupPath(currentSlot, ...)) and LoadGame's backup path.
        [HarmonyPatch(typeof(SaveSlots), "GetBackupPath")]
        public static class GetBackupPathPatch
        {
            static void Postfix(int slot, int backupIndex, ref string __result)
            {
                if (CoopSave.IsPhantom(slot))
                    __result = CoopSave.PhantomBackupFilePath(backupIndex);
            }
        }
    }
}
