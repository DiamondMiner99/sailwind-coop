using System;
using System.IO;
using HarmonyLib;
using SailwindCoop.Debug;
using UnityEngine;

namespace SailwindCoop
{
    /// <summary>
    /// PHANTOM CO-OP SAVE. A guest in co-op is on the HOST's shared boat, so any write to one of the
    /// guest's six real solo slots would clobber their solo progress with the host's world. Instead we
    /// reserve a slot id the 6-slot menu never lists (<see cref="PhantomSlot"/> = 99) and Harmony-patch
    /// <c>SaveSlots</c> so every path for slot 99 resolves to a distinct hidden file
    /// (<c>coop_session.save</c> + backups). With <c>SaveSlots.currentSlot == 99</c> for the whole
    /// session, EVERY vanilla save path (autosave timer, sleep-save, Quit-save, our explicit save)
    /// structurally lands on the phantom file - it is impossible for a write to touch slots 0..5.
    ///
    /// The guest's co-op needs (food/water/sleep) therefore persist across sessions in that one hidden
    /// file via the normal vanilla Load/Save. A freshly-created phantom is bootstrapped by FILE-COPYing
    /// the guest's most-recent solo .save (a READ of solo + WRITE of phantom - the solo slot is never
    /// written), then needs are reset to a clean rested/fed baseline so the guest never starts co-op
    /// starving from their solo state.
    /// </summary>
    public static class CoopSave
    {
        /// <summary>Reserved slot id the 6-slot save menu (slots 0..5) never enumerates.</summary>
        public const int PhantomSlot = 99;

        // Distinct on-disk names for the phantom slot. The SaveSlots patch rewrites slot-99 paths to
        // these so they can never collide with "/slot{0..5}.save".
        private const string PhantomFileName = "coop_session.save";
        private const string PhantomBackupPrefix = "coop_session_backup"; // + index + ".save"

        // Idempotency guard so a single guest-leave doesn't write the phantom twice (the EndGuest path
        // fires both OnLobbyLeft and OnApplicationQuit). Reset when a new phantom context is entered.
        private static bool _savedThisSession;

        // NOTE: the load-bearing code uses the patch-INDEPENDENT PhantomSavePath()/PhantomBackupFilePath()
        // below. We deliberately do NOT expose convenience accessors that route through SaveSlots
        // (e.g. GetSlotSavePath(99)), because those only resolve to the phantom file once the Harmony
        // patches are applied - a foot-gun if ever called before patching.

        // --- SaveSlots path redirection (invoked from SaveSlotsPatches) -------------------------------

        /// <summary>True for the reserved phantom slot id. Used by the SaveSlots Harmony postfixes.</summary>
        public static bool IsPhantom(int slot) => slot == PhantomSlot;

        /// <summary>
        /// True only while an active GUEST co-op save context is live (set in EnterCoopSaveContext, cleared by
        /// ClearContext on teardown). The SaveSlots path redirect is gated on this so that a stale, lingering
        /// SaveSlots.currentSlot==99 (e.g. left set by a BepInEx hot-reload mid-session, where the delayed
        /// Application.Quit coroutine on the destroyed Plugin never fires) can NOT redirect a later, non-co-op
        /// solo save into the phantom file and contaminate it.
        /// </summary>
        public static bool ContextActive { get; private set; }

        /// <summary>Clear the guest co-op save context (called on teardown, e.g. OnDestroy/hot-reload), so a
        /// lingering currentSlot==99 can no longer redirect saves to the phantom file.</summary>
        public static void ClearContext() => ContextActive = false;

        /// <summary>Distinct file path for the phantom save (replaces "/slot99.save").</summary>
        public static string PhantomSavePath() => Path.Combine(Application.persistentDataPath, PhantomFileName);

        /// <summary>Distinct file path for a phantom backup (replaces "/slot99_backup{N}.save").</summary>
        public static string PhantomBackupFilePath(int backupIndex) =>
            Path.Combine(Application.persistentDataPath, PhantomBackupPrefix + backupIndex + ".save");

        // --- Session bootstrap -----------------------------------------------------------------------

        /// <summary>
        /// Enter the phantom save context: from here on EVERY save resolves to the phantom file because
        /// we set <c>SaveSlots.currentSlot = 99</c>. If no phantom file exists yet, bootstrap one by
        /// file-copying the guest's most-recent ACTIVE solo .save (read solo, write phantom - the solo
        /// slot is never written) so the phantom is a valid, loadable world.
        /// </summary>
        /// <param name="didCreate">True if a brand-new phantom was just bootstrapped (caller should then
        /// reset needs to baseline). False if a persisted phantom already existed.</param>
        /// <returns>True if the phantom context is ready (currentSlot set to 99); false if there is no
        /// phantom and no solo save to seed from (caller must block the join with a notify).</returns>
        public static bool EnterCoopSaveContext(out bool didCreate)
        {
            didCreate = false;

            _savedThisSession = false; // new session: allow one phantom save on this leave/quit

            string phantomPath = PhantomSavePath();
            if (File.Exists(phantomPath))
            {
                // A persisted phantom already exists; reuse it (carries forward the guest's co-op needs).
                SaveSlots.currentSlot = PhantomSlot;
                ContextActive = true; // arm the redirect ONLY for the live guest session
                Plugin.Log.LogInfo($"[CoopSave] Reusing existing phantom save ({phantomPath}); currentSlot=99");
                VerboseLogger.LobbyEvent("Phantom save: reusing existing coop_session.save (currentSlot=99)");
                return true;
            }

            // No phantom yet: seed one from the guest's most-recent solo slot (READ solo / WRITE phantom).
            int soloSlot = PickSoloSlot();
            if (soloSlot < 0)
            {
                Plugin.Log.LogWarning("[CoopSave] No phantom and no solo save to seed from; cannot enter co-op save context");
                return false;
            }

            try
            {
                string soloPath = SaveSlots.GetSlotSavePath(soloSlot); // soloSlot is 0..5, so this is the REAL solo file
                File.Copy(soloPath, phantomPath, overwrite: true);     // read solo, write phantom; solo is never modified
                didCreate = true;
                SaveSlots.currentSlot = PhantomSlot;
                ContextActive = true; // arm the redirect for the live guest session
                Plugin.Log.LogInfo($"[CoopSave] Seeded new phantom save from solo slot {soloSlot} ({soloPath} -> {phantomPath}); currentSlot=99");
                VerboseLogger.LobbyEvent($"Phantom save: created from solo slot {soloSlot} (currentSlot=99, will reset needs to baseline)");
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[CoopSave] Failed to seed phantom save from solo slot {soloSlot}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Persist the guest's current co-op needs to the phantom file. ONLY runs while the phantom
        /// context is active (currentSlot == 99), so it can never write a real slot. Bypasses the
        /// suppressed autosave/save-on-sleep flags by invoking the vanilla save directly.
        /// </summary>
        public static void SaveCoopSession()
        {
            if (SaveSlots.currentSlot != PhantomSlot)
            {
                Plugin.Log.LogWarning($"[CoopSave] SaveCoopSession skipped: currentSlot={SaveSlots.currentSlot} (not phantom)");
                return;
            }

            if (_savedThisSession)
            {
                // Already persisted on this leave (e.g. OnLobbyLeft ran, then OnApplicationQuit). Skip the
                // duplicate so we get EXACTLY ONE phantom write per guest session end.
                Plugin.Log.LogInfo("[CoopSave] SaveCoopSession skipped: already saved this session");
                return;
            }

            var slm = SaveLoadManager.instance;
            if (slm == null)
            {
                Plugin.Log.LogWarning("[CoopSave] SaveCoopSession skipped: no SaveLoadManager");
                return;
            }

            // SaveGame() guards on SaveLoadManager.readyToSave; make sure it is set so an explicit
            // co-op save still runs even if a guest-quit path cleared it. currentSlot==99 guarantees the
            // write lands on the phantom file regardless.
            SaveLoadManager.readyToSave = true;

            // COMMITTABILITY GUARD: vanilla SaveGame() silently NO-OPs unless
            // (!busy && !GameState.inBed && readyToSave && !GameState.currentShipyard) holds
            // (see SaveLoadManager.SaveGame). If a guest leaves mid-sleep (GameState.inBed) or
            // mid-shipyard or while a save is already running (busy), the call would do nothing - yet we'd
            // mark the session saved and the later OnApplicationQuit retry would early-return, losing the
            // needs for that session. So only mark _savedThisSession when the save will ACTUALLY run; if
            // not committable, return WITHOUT setting it so a later SaveCoopSession can retry.
            // (busy is a private field -> read via Traverse, the mod's established pattern.)
            bool busy = false;
            try { busy = Traverse.Create(slm).Field("busy").GetValue<bool>(); }
            catch { busy = false; } // if reflection fails, don't block the save; worst case it no-ops harmlessly
            // Use Unity's implicit-bool (true == live object), exactly mirroring vanilla's
            // !GameState.inBed / !GameState.currentShipyard guards.
            bool inBed = (bool)GameState.inBed;
            bool inShipyard = (bool)GameState.currentShipyard;
            if (busy || inBed || inShipyard)
            {
                Plugin.Log.LogWarning($"[CoopSave] SaveCoopSession deferred: not committable (busy={busy}, inBed={inBed}, shipyard={inShipyard}); will retry on a later leave/quit");
                return; // do NOT set _savedThisSession; a subsequent call (e.g. OnApplicationQuit) retries
            }

            slm.SaveGame(compressed: true);
            _savedThisSession = true;
            // HONEST LOG: SaveGame only STARTS the DoSaveGame coroutine; the actual file write commits
            // at end of frame. Don't claim "saved" here - if the frame never finishes (e.g. app already
            // tearing down), the phantom was NOT written.
            Plugin.Log.LogInfo($"[CoopSave] Phantom save STARTED (async, commits end-of-frame) -> {PhantomSavePath()}");
            VerboseLogger.LobbyEvent("Phantom save: SaveCoopSession started DoSaveGame for coop_session.save (currentSlot=99, commits end-of-frame)");
        }

        /// <summary>
        /// SELF-HEAL for a corrupt phantom save. Contract: call ONCE, well after a guest join has settled
        /// (~60s; the caller in Plugin wires the invocation - this class never schedules it). If the load
        /// of the phantom had to skip corrupt saveables (<see cref="Patches.SaveLoadResiliencePatches.CorruptSkipsOccurredThisLoad"/>),
        /// a committed re-save is CLEAN (vanilla DoSaveGame regenerates from live state and only iterates
        /// live objects), purging removed-mod leftovers so subsequent joins load fully. No-op in solo,
        /// outside the phantom context, or when the load was clean. Does NOT consume the one
        /// end-of-session save (_savedThisSession is restored).
        /// </summary>
        public static void TrySelfHealSave()
        {
            if (!ContextActive || SaveSlots.currentSlot != PhantomSlot) return;
            if (!Patches.SaveLoadResiliencePatches.CorruptSkipsOccurredThisLoad) return;

            bool prev = _savedThisSession;
            _savedThisSession = false; // allow this mid-session heal write through the idempotency guard
            SaveCoopSession();         // reuses the existing busy/inBed/shipyard committability guards
            bool committed = _savedThisSession;
            _savedThisSession = prev;  // don't consume the end-of-session write

            if (committed)
            {
                Patches.SaveLoadResiliencePatches.CorruptSkipsOccurredThisLoad = false;
                Plugin.Log.LogInfo("[CoopSave] Self-heal: clean phantom rewrite started (purges corrupt/removed-mod saveables for future joins)");
                VerboseLogger.LobbyEvent("Phantom save: self-heal rewrite started after corrupt-saveable skips this load");
            }
            else
            {
                Plugin.Log.LogInfo("[CoopSave] Self-heal deferred: save not committable right now (flag kept; caller may retry)");
            }
        }

        /// <summary>
        /// Reset the guest's needs to a clean rested/fed baseline. Used ONCE, right after a brand-new
        /// phantom is bootstrapped, so the guest doesn't start their first co-op session carrying the
        /// hunger/sleep state baked into the solo save they were seeded from.
        /// </summary>
        public static void ResetNeedsToBaseline()
        {
            // Fed + watered + rested, with all debts cleared (debt fields are "100 = no debt" in vanilla;
            // see PlayerNeeds.Start / LoadNeeds / DrainEnergyFromMovement).
            PlayerNeeds.food = 100f;
            PlayerNeeds.water = 100f;
            PlayerNeeds.sleep = 100f;
            PlayerNeeds.foodDebt = 100f;
            PlayerNeeds.sleepDebt = 100f;
            PlayerNeeds.vitamins = 100f;
            PlayerNeeds.protein = 100f;
            PlayerNeeds.alcohol = 0f;
            Plugin.Log.LogInfo("[CoopSave] Reset needs to clean baseline (food/water/sleep=100, debts cleared)");
            VerboseLogger.LobbyEvent("Phantom save: reset needs to baseline (fresh phantom)");
        }

        // --- helpers ---------------------------------------------------------------------------------

        /// <summary>
        /// Most-recently-modified ACTIVE solo slot (0..5), or -1 if none. Mirrors the old
        /// TitleJoinManager.PickSaveSlot logic; used only to seed a brand-new phantom.
        /// </summary>
        private static int PickSoloSlot()
        {
            if (SaveSlots.slotsActive == null) return -1;
            int best = -1;
            DateTime bestTime = DateTime.MinValue;
            // slotsActive is length 6 (slots 0..5); we never index it with 99.
            for (int i = 0; i < SaveSlots.slotsActive.Length; i++)
            {
                if (!SaveSlots.slotsActive[i]) continue;
                DateTime ft;
                try { ft = File.GetLastWriteTime(SaveSlots.GetSlotSavePath(i)); }
                catch { ft = DateTime.MinValue; }
                if (best < 0 || ft > bestTime) { best = i; bestTime = ft; }
            }
            return best;
        }
    }
}
