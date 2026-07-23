using HarmonyLib;
using SailwindCoop.Debug;
using SailwindCoop.Networking.Packets;
using SailwindCoop.Sync;

namespace SailwindCoop.Patches
{
    /// <summary>
    /// Harmony patches for survival stat synchronization.
    /// Disables guest stat decay and PassOut, captures consumption deltas.
    /// </summary>
    public static class SurvivalPatches
    {
        #region Disable Guest Stat Decay

        /// <summary>
        /// Disable PlayerNeeds.LateUpdate on guest to prevent local stat decay.
        /// Host is authoritative for all stat changes.
        /// </summary>
        [HarmonyPatch(typeof(PlayerNeeds), "LateUpdate")]
        public static class PlayerNeedsLateUpdatePatch
        {
            // BED REST (v0.2.29): snapshot taken in the prefix while the local player is in bed but
            // awake (waiting-for-crew window); the postfix restores the non-sleep vitals to the
            // snapshot (freeze) and applies a capped slow sleep regen. Snapshot/restore instead of
            // counter-adding vanilla's drain rates, so this cannot drift if those rates change, and a
            // frozen stat can never cross 0 mid-update and PassOut.
            private const float BedRestSleepCap = 60f;   // resting never gets you past 60/100 - real sleep keeps its role
            private const float BedRestRegenPerSec = 2f; // 25% of real sleep's 8/s (both x timescale)

            private static bool _resting;
            private static float _preSleep, _preFood, _preWater, _preProtein, _preVitamins;

            // (v0.2.37) WARP-CLIP REST TOP-UP: Time.realtimeSinceStartup at the previous frame's postfix
            // while a co-op sleep warp is active on this GUEST. 0 when not in a warp (re-baselines on entry
            // so the first frame credits nothing). realtimeSinceStartup, NOT unscaledDeltaTime: the latter is
            // ALSO subject to the maximumDeltaTime clamp we are compensating for, so it cannot measure it.
            private static float _lastWarpRealtime;

            [HarmonyPrefix]
            public static bool Prefix()
            {
                _resting = false;

                // GUEST FAINT: while a local faint is mid-blackout, suppress vanilla decay so stats
                // can't be pushed back to 0 and re-fire PassOut during the fade (re-entrancy storm).
                if (PlayerNeedsPassOutPatch.SuppressDecay) return false;

                if (Plugin.IsMultiplayer && Plugin.BedRestConfig.Value
                    && SleepSyncManager.Instance != null && SleepSyncManager.Instance.IsLocalPlayerRestingInBed)
                {
                    _resting = true;
                    _preSleep = PlayerNeeds.sleep;
                    _preFood = PlayerNeeds.food;
                    _preWater = PlayerNeeds.water;
                    _preProtein = PlayerNeeds.protein;
                    _preVitamins = PlayerNeeds.vitamins;
                }

                // INDEPENDENT NEEDS: each player ticks their OWN needs decay (food/water/sleep).
                // Previously the guest's LateUpdate was suppressed and stats were mirrored from the
                // host. Now we ALWAYS run the vanilla LateUpdate so every client decays locally.
                // (Sun.sun.timescale is synced, so both sides decay at the same rate.)
                return true;
            }

            [HarmonyPostfix]
            public static void Postfix()
            {
                TopUpClampedWarpRest();

                if (!_resting) return;
                _resting = false;

                // Freeze the non-sleep vitals (alcohol deliberately keeps draining - sobering up in bed is fine)
                PlayerNeeds.food = _preFood;
                PlayerNeeds.water = _preWater;
                PlayerNeeds.protein = _preProtein;
                PlayerNeeds.vitamins = _preVitamins;

                // Cancel the awake sleep drain, then regen slowly up to the cap. Never reduce a value
                // vanilla left higher (e.g. an eat/drink effect this same frame).
                float s = UnityEngine.Mathf.Max(PlayerNeeds.sleep, _preSleep);
                if (s < BedRestSleepCap)
                    s = UnityEngine.Mathf.Min(s + UnityEngine.Time.deltaTime * Sun.sun.timescale * BedRestRegenPerSec, BedRestSleepCap);
                PlayerNeeds.sleep = s;
            }

            /// <summary>
            /// (v0.2.37) WARP-CLIP REST TOP-UP (guest only). Credits back exactly the rest that Unity's
            /// maximumDeltaTime clamp refused to grant a low-framerate crewmate during a co-op sleep warp.
            ///
            /// THE BUG (reported: "if you get the slideshow while sleeping as a client you won't recover
            /// much... sleeping on dry land works perfectly"):
            ///  - Rest and the nap clock are the SAME integral: vanilla PlayerNeeds fills
            ///    `sleep += Time.deltaTime * 8 * Sun.timescale` while Sleep.Update accumulates
            ///    `currentSleepDuration += Time.deltaTime * Sun.timescale`. Ratio exactly 8, so a vanilla
            ///    4.5-game-hour boat nap is worth exactly 36 sleep points - and it is frame-rate INDEPENDENT
            ///    for whoever owns the clock, because both sides of the ratio take the same clamp.
            ///  - Unity computes Time.deltaTime as min(realFrameDelta, maximumDeltaTime) * timeScale, and
            ///    Sailwind configures maximumDeltaTime = 0.1. So above 10fps the 16x sleep warp is a true
            ///    16x, at 5fps it silently degrades to ~8x, at 2fps to ~3.2x.
            ///  - The GUEST does not own the clock: its Sleep.Update is skipped for the whole SLEEPING state
            ///    (SleepUpdateGuestPatch), so it has no currentSleepDuration and cannot end its own nap. It
            ///    sleeps until the HOST's WakeUp packet. At sea (unmoored) the host's cap is unsuppressed -
            ///    SleepPatches' waitingForCrew requires IsTimeskipEnabled, which is false there - so the host
            ///    wakes the crew after 4.5 of ITS OWN game-hours. A guest running at half the host's
            ///    effective rate has integrated half the rest by then, and nothing tops it up.
            ///    Net: the guest banks 36 * min(1, fpsGuest/10) / min(1, fpsHost/10) points.
            ///  - On land (tavern/moored) IsTimeskipEnabled is true, so the cap is suppressed until
            ///    AllCrewRested - every peer reporting 99.99 on its OWN machine - and everyone fills fully
            ///    regardless of frame rate. That single term is the whole "dry land works perfectly"
            ///    asymmetry the crew observed.
            ///
            /// THE FIX: replay, per frame, only the integration the clamp discarded -
            /// lost = max(0, realFrameDelta - maximumDeltaTime) * timeScale - through vanilla's own formula
            /// including the sleepDebt x0.2 rule. Above 10fps `lost` is identically 0, so this is a no-op for
            /// anyone whose framerate is fine. Deliberately GUEST-ONLY: the host's nap ends on the same
            /// clipped clock that fills it, so it always banks the full vanilla 36 and topping it up would
            /// over-credit it past vanilla. Also helps the moored/tavern case, where a slow guest could
            /// otherwise be force-marked rested by the per-peer deadline without having actually rested.
            /// No wire change. Needs-sync safe: this only ever RAISES the local player's own sleep/sleepDebt
            /// through vanilla's formula, and each client already owns and persists its own needs.
            /// </summary>
            private static void TopUpClampedWarpRest()
            {
                if (!Plugin.IsMultiplayer || Plugin.IsHost || !GameState.sleeping ||
                    !SleepSyncManager.IsCoopSleepWarpActive ||
                    UnityEngine.Time.timeScale <= 1f || Sun.sun == null)
                {
                    _lastWarpRealtime = 0f; // re-baseline: the next warp frame credits nothing
                    return;
                }

                float now = UnityEngine.Time.realtimeSinceStartup;
                float real = _lastWarpRealtime > 0f ? now - _lastWarpRealtime : 0f;
                _lastWarpRealtime = now;
                // real <= 0: first frame of this warp (or a clock hiccup). real >= 2: a load stall or
                // alt-tab, which vanilla would not have credited either - do not hand out free hours.
                if (real <= 0f || real >= 2f) return;

                float lost = (real - UnityEngine.Time.maximumDeltaTime) * UnityEngine.Time.timeScale;
                if (lost <= 0f) return; // frame was inside the clamp: vanilla already credited it in full

                // Vanilla's own formula (PlayerNeeds.LateUpdate sleep branch), applied to the lost slice.
                float num = lost * 8f * Sun.sun.timescale;
                if (GameState.sleepingInTavern) num *= 4f;
                if (PlayerNeeds.sleepDebt < 100f)
                {
                    PlayerNeeds.sleepDebt = UnityEngine.Mathf.Min(100f, PlayerNeeds.sleepDebt + num);
                    num *= 0.2f;
                }
                PlayerNeeds.sleep = UnityEngine.Mathf.Min(100f, PlayerNeeds.sleep + num);
            }
        }

        #endregion

        #region Guest PassOut (faint locally; don't move the shared boat)

        /// <summary>
        /// PASSOUT POLICY A: on the guest, a faint must NOT run vanilla Recovery (which teleports the
        /// SHARED boat to the last port). Instead the guest faints LOCALLY: fade to black, a notice,
        /// and a partial needs restore so they don't instantly re-faint. The host keeps vanilla
        /// recovery (handled by RecoveryRecoverPlayerPatch, which re-syncs the guest onto the boat).
        /// </summary>
        [HarmonyPatch(typeof(PlayerNeeds), "PassOut")]
        public static class PlayerNeedsPassOutPatch
        {
            // Re-entrancy guard: PlayerNeeds.LateUpdate re-fires PassOut every frame while a stat is
            // pinned at 0, which would otherwise spawn a new GuestFaint coroutine per frame.
            private static bool _fainting;

            // While true, the guest's PlayerNeeds.LateUpdate is suppressed (no decay/re-faint during
            // the blackout). Honored by PlayerNeedsLateUpdatePatch.Prefix.
            public static bool SuppressDecay;

            [HarmonyPrefix]
            public static bool Prefix(RecoveryReason reason)
            {
                // A-fix #1 (regression): a co-op sleep forces Sleep.timeskipSleep=true for EVERY co-op sleep
                // (incl. UNMOORED) so the sleep bar fast-fills. That drives Sun.sun.timescale=9x, which vanilla
                // PlayerNeeds.LateUpdate also uses to drain food/water/vitamins/protein (decomp PlayerNeeds.cs
                // 119/142/152/162) - NOT just the sleep fill. So an unmoored at-sea sleep started with low needs
                // can drain a need to 0 mid-warp and fire PassOut purely as a side effect of the crew sleeping;
                // unmoored sleep never triggered Recovery before. Co-op sleep must NEVER cause a faint/Recovery as
                // a side effect. On BOTH host and guest, swallow the PassOut while a co-op sleep is active AND floor
                // the drainable needs off 0, so (a) Recovery never runs mid-sleep (host: no boat teleport + gold
                // cost; guest: no blackout fighting the host-owned sleep), (b) vanilla LateUpdate can't re-fire
                // PassOut next frame (the need is now off its floor), and (c) nobody WAKES with a need at ~0 that
                // immediately re-faints them awake. Tavern sleep is already immune (vanilla zeros all needs while
                // sleepingInTavern), so this only matters for boat sleep, but the guard is reason/context-agnostic
                // and symmetric host<->guest. The sleep fast-fill is untouched (we never touch the sleep-fill
                // branch). Mirrors the guest's prior sleep-swallow, now applied to the host too and made non-fainting.
                if (Plugin.IsMultiplayer &&
                    SleepSyncManager.Instance?.CurrentState == SleepSyncManager.SleepState.Sleeping)
                {
                    FloorNeedsForSleep();
                    VerboseLogger.SurvivalEvent($"PassOut during co-op sleep (reason={reason}); swallowed + needs floored (sleep can't cause Recovery/faint)");
                    return false;
                }

                // Guest: faint locally, skip vanilla Recovery (which would move the shared boat).
                if (Plugin.IsMultiplayer && !Plugin.IsHost)
                {
                    if (_fainting) return false; // re-entrancy guard: ignore re-fires during the faint
                    _fainting = true;
                    Plugin.Log.LogWarning("Guest passed out - fainting locally (shared boat not recovered)");
                    Plugin.Instance.StartCoroutine(GuestFaint(reason));
                    return false;
                }

                // Host: normal recovery (RecoveryRecoverPlayerPatch handles the guest re-sync).
                VerboseLogger.RecoveryEvent("Host PassOut: running vanilla recovery (guest will re-sync onto boat)");
                return true;
            }

            /// <summary>
            /// A-fix #1: lift the drainable survival needs off their floor so a co-op sleep's 9x drain can't pin
            /// any of them at 0 (which would re-fire PassOut every frame and re-faint a crewmate awake on wake).
            /// Mirrors the floor GuestFaint already applies, minus the look/control side effects - this only
            /// touches the stats. Called from the in-sleep PassOut swallow on both host and guest.
            /// </summary>
            private static void FloorNeedsForSleep()
            {
                PlayerNeeds.food = UnityEngine.Mathf.Max(PlayerNeeds.food, 40f);
                PlayerNeeds.water = UnityEngine.Mathf.Max(PlayerNeeds.water, 40f);
                PlayerNeeds.vitamins = UnityEngine.Mathf.Max(PlayerNeeds.vitamins, 40f);
                PlayerNeeds.protein = UnityEngine.Mathf.Max(PlayerNeeds.protein, 40f);
                // foodDebt is the second-stage drain that actually fires the food faint (decomp PlayerNeeds.cs:131);
                // restore it too so a food=0 frame doesn't immediately bleed foodDebt to 0 again.
                PlayerNeeds.foodDebt = 100f;
            }

            // Local faint for the guest. Fully-qualify UnityEngine types - this file has no
            // `using UnityEngine;`. C# allows `yield return` inside a try that has only a finally.
            private static System.Collections.IEnumerator GuestFaint(RecoveryReason reason)
            {
                // If the guest faints UNDERWAY, capture their boat-relative deck spot (in the boat's
                // walkCol / PHYSICS frame, which is where the embarked CharacterController actually lives) so we
                // can re-plant them on wake. The guest stays EMBARKED through the faint (vanilla PassOut/Recovery
                // is skipped; only control toggles), but with control off the frozen controller can drift off the
                // moving deck during the ~4.5s blackout (gravity / the swim-detection disembark transient), so
                // they wake treading water BEHIND the boat. Re-planting the captured deck spot wakes them ON the
                // deck instead. Only engages when on a boat (GameState.currentBoat != null); a faint on land is
                // unchanged. (currentBoat == the boatModel; BoatRefs on its root carries walkCol; the SAME walkCol
                // is used for capture + restore, so the round-trip can't hit the boatModel-vs-walkCol 205m trap.)
                UnityEngine.Transform faintWalkCol = null;
                UnityEngine.Vector3 faintBoatLocalPos = UnityEngine.Vector3.zero;
                var faintBoat = GameState.currentBoat;
                if (faintBoat != null && Refs.charController != null)
                {
                    var boatRefs = faintBoat.GetComponentInParent<BoatRefs>();
                    if (boatRefs != null && boatRefs.walkCol != null)
                    {
                        faintWalkCol = boatRefs.walkCol;
                        faintBoatLocalPos = faintWalkCol.InverseTransformPoint(Refs.charController.transform.position);
                        VerboseLogger.SurvivalEvent($"Guest faint: on boat '{faintBoat.name}', captured deck-relative spot {faintBoatLocalPos} to restore on wake (no-ocean-dunk)");
                    }
                }

                // Freeze control + suppress decay FIRST so the guest can't walk off the deck while
                // blacked out and LateUpdate can't re-deplete stats and re-fire PassOut.
                Refs.SetPlayerControl(false);
                SuppressDecay = true;
                VerboseLogger.SurvivalEvent("Guest faint START: control disabled, stat decay suppressed during blackout");
                try
                {
                    // PARTIAL recovery BEFORE the fade: stats come off their floor immediately, so even
                    // if decay were running they wouldn't be back at 0 by the time the fade finishes.
                    PlayerNeeds.food = UnityEngine.Mathf.Max(PlayerNeeds.food, 40f);
                    PlayerNeeds.water = UnityEngine.Mathf.Max(PlayerNeeds.water, 40f);
                    PlayerNeeds.vitamins = UnityEngine.Mathf.Max(PlayerNeeds.vitamins, 40f);
                    PlayerNeeds.protein = UnityEngine.Mathf.Max(PlayerNeeds.protein, 40f);
                    PlayerNeeds.sleep = UnityEngine.Mathf.Max(PlayerNeeds.sleep, 40f);
                    PlayerNeeds.foodDebt = 100f;
                    PlayerNeeds.sleepDebt = UnityEngine.Mathf.Max(PlayerNeeds.sleepDebt, 50f);
                    VerboseLogger.SurvivalEvent($"Guest faint: stats restored from floor (food={PlayerNeeds.food:F0}, water={PlayerNeeds.water:F0}, sleep={PlayerNeeds.sleep:F0})");

                    // Fade to black (Blackout.FadeTo returns an IEnumerator - run it as a coroutine).
                    yield return Plugin.Instance.StartCoroutine(Blackout.FadeTo(1f, 1.5f));
                    Plugin.Notify("You passed out!", 4f);
                    // Hold the black for a beat, then fade back in.
                    yield return new UnityEngine.WaitForSecondsRealtime(1.5f);
                    yield return Plugin.Instance.StartCoroutine(Blackout.FadeTo(0f, 1.5f));
                }
                finally
                {
                    // A guest who fainted UNDERWAY can drift off the moving deck during the blackout; put
                    // them back on the boat at the spot they fell (walkCol has moved with the boat, so
                    // TransformPoint of the captured local spot is the deck's CURRENT world position) BEFORE
                    // re-enabling control, so they wake on the deck rather than in the ocean. Skip if they weren't
                    // on a boat or it's gone (recovered/sunk -> walkCol Unity-null). They stay EMBARKED the whole
                    // faint (PlayerEmbarkerNew never disarms), so re-planting the controller on the deck spot is
                    // enough - the still-armed per-frame walkCol reconcile holds them there once control resumes.
                    // (The write lands because the controller is still frozen here; it only resists writes while enabled.)
                    if (faintWalkCol != null && Refs.charController != null)
                    {
                        Refs.charController.transform.position = faintWalkCol.TransformPoint(faintBoatLocalPos);
                        VerboseLogger.SurvivalEvent("Guest faint underway: restored onto the boat deck (no ocean dunk)");
                    }

                    // FIX 1 (defense-in-depth): only re-enable control when NOT in bed, mirroring
                    // Sleep.WakeUp's guard, so a faint that runs while in bed can't re-enable control
                    // mid-sleep. Always clear SuppressDecay/_fainting.
                    if (!GameState.inBed) Refs.SetPlayerControl(true);
                    SuppressDecay = false;
                    _fainting = false;
                    VerboseLogger.SurvivalEvent($"Guest faint COMPLETE: control restored (inBed={GameState.inBed}), decay re-enabled, re-entrancy guard cleared");
                }
            }
        }

        #endregion

        #region Suppress needs-warning wake during co-op sleep

        /// <summary>
        /// INDEPENDENT NEEDS: each player's water/food can drop below 20 mid-sleep, and vanilla
        /// PlayerNeedsUI.PlayWarning(bar, wakeUp:true) then calls Sleep.WakeUp() - which would end the
        /// crew sleep prematurely (the guest's wake bypasses the both-rested gate). While the crew is
        /// asleep, force the wake flag off so the visual warning still plays but doesn't wake anyone.
        /// </summary>
        [HarmonyPatch(typeof(PlayerNeedsUI), "PlayWarning")]
        public static class PlayerNeedsUIPlayWarningPatch
        {
            [HarmonyPrefix]
            public static void Prefix(ref bool wakeUp)
            {
                if (Plugin.HasConnectedGuest &&
                    SleepSyncManager.Instance?.CurrentState == SleepSyncManager.SleepState.Sleeping)
                {
                    wakeUp = false; // keep the visual warning, drop the embedded WakeUp
                }
            }
        }

        #endregion

        #region Consumption Delta Capture

        // Static fields to capture stats before consumption
        // ThreadStatic for defensive thread safety even though Unity is single-threaded
        [System.ThreadStatic]
        private static float _preFood, _preWater, _preSleep, _preFoodDebt;
        [System.ThreadStatic]
        private static float _preSleepDebt, _preAlcohol, _preVitamins, _preProtein;

        private static void CapturePreStats()
        {
            _preFood = PlayerNeeds.food;
            _preWater = PlayerNeeds.water;
            _preSleep = PlayerNeeds.sleep;
            _preFoodDebt = PlayerNeeds.foodDebt;
            _preSleepDebt = PlayerNeeds.sleepDebt;
            _preAlcohol = PlayerNeeds.alcohol;
            _preVitamins = PlayerNeeds.vitamins;
            _preProtein = PlayerNeeds.protein;
        }

        private static void SendDeltaIfChanged()
        {
            // INDEPENDENT NEEDS: eating/drinking now only feeds the LOCAL eater (the vanilla method
            // already mutates the local PlayerNeeds statics). We no longer ship consumption deltas to
            // the host. Early-return disables the delta send; item-health sync (OnLocalItemHealthChanged)
            // calls in the postfixes are untouched.
            return;

#pragma warning disable CS0162 // unreachable code (kept intentionally; CapturePreStats is harmless)
            if (!Plugin.IsMultiplayer || Plugin.IsHost) return;

            var delta = new ConsumptionDeltaPacket
            {
                DeltaFood = PlayerNeeds.food - _preFood,
                DeltaWater = PlayerNeeds.water - _preWater,
                DeltaSleep = PlayerNeeds.sleep - _preSleep,
                DeltaFoodDebt = PlayerNeeds.foodDebt - _preFoodDebt,
                DeltaSleepDebt = PlayerNeeds.sleepDebt - _preSleepDebt,
                DeltaAlcohol = PlayerNeeds.alcohol - _preAlcohol,
                DeltaVitamins = PlayerNeeds.vitamins - _preVitamins,
                DeltaProtein = PlayerNeeds.protein - _preProtein
            };

            SurvivalSyncManager.Instance?.SendConsumptionDelta(delta);
#pragma warning restore CS0162
        }

        /// <summary>
        /// Capture food consumption (ShipItemFood.EatFood).
        /// </summary>
        [HarmonyPatch(typeof(ShipItemFood), "EatFood")]
        public static class ShipItemFoodEatFoodPatch
        {
            [HarmonyPrefix]
            public static void Prefix()
            {
                if (Plugin.IsMultiplayer && !Plugin.IsHost)
                    CapturePreStats();
            }

            [HarmonyPostfix]
            public static void Postfix(ShipItemFood __instance)
            {
                SendDeltaIfChanged();

                // Sync item health change
                if (Plugin.IsMultiplayer)
                {
                    Plugin.ItemSyncManager?.OnLocalItemHealthChanged(__instance, forceSync: true);
                }
            }
        }

        /// <summary>
        /// Capture drink consumption (ShipItemBottle.Drink).
        /// </summary>
        [HarmonyPatch(typeof(ShipItemBottle), "Drink")]
        public static class ShipItemBottleDrinkPatch
        {
            [HarmonyPrefix]
            public static void Prefix()
            {
                if (Plugin.IsMultiplayer && !Plugin.IsHost)
                    CapturePreStats();
            }

            [HarmonyPostfix]
            public static void Postfix(ShipItemBottle __instance)
            {
                SendDeltaIfChanged();

                // Sync item health change
                if (Plugin.IsMultiplayer)
                {
                    Plugin.ItemSyncManager?.OnLocalItemHealthChanged(__instance, forceSync: true);
                }
            }
        }

        /// <summary>
        /// Capture barrel/bottle refills + pours: ShipItemBottle.OnItemClick transfers liquid between the
        /// clicked container and the held one, changing both their levels. Sync both. (OnLocalItemHealthChanged
        /// also broadcasts amount now, so the liquid type stays matched too.)
        /// </summary>
        [HarmonyPatch(typeof(ShipItemBottle), "OnItemClick")]
        public static class ShipItemBottleOnItemClickPatch
        {
            [HarmonyPostfix]
            public static void Postfix(ShipItemBottle __instance, PickupableItem heldItem)
            {
                if (!Plugin.IsMultiplayer) return;
                Plugin.ItemSyncManager?.OnLocalItemHealthChanged(__instance, forceSync: true); // clicked container (barrel/bottle)
                if (heldItem == null) return;
                var hb = heldItem.GetComponent<ShipItemBottle>();
                if (hb != null) Plugin.ItemSyncManager?.OnLocalItemHealthChanged(hb, forceSync: true); // held container
            }
        }

        /// <summary>
        /// Capture soup consumption (ShipItemSoup.DrinkOrSpill).
        /// Only applies when not spilling (spillOut=false).
        /// </summary>
        [HarmonyPatch(typeof(ShipItemSoup), "DrinkOrSpill")]
        public static class ShipItemSoupDrinkOrSpillPatch
        {
            [HarmonyPrefix]
            public static void Prefix(bool spillOut)
            {
                // Only capture when actually drinking, not spilling
                if (!spillOut && Plugin.IsMultiplayer && !Plugin.IsHost)
                    CapturePreStats();
            }

            [HarmonyPostfix]
            public static void Postfix(ShipItemSoup __instance, bool spillOut)
            {
                // Only send delta when actually drinking, not spilling
                if (!spillOut)
                    SendDeltaIfChanged();

                // Sync item health change (throttled for continuous consumption)
                if (!spillOut && Plugin.IsMultiplayer)
                {
                    Plugin.ItemSyncManager?.OnLocalItemHealthChanged(__instance, forceSync: false);
                }
            }
        }

        /// <summary>
        /// Capture elixir consumption (ShipItemElixir.OnAltActivate).
        /// </summary>
        [HarmonyPatch(typeof(ShipItemElixir), "OnAltActivate")]
        public static class ShipItemElixirOnAltActivatePatch
        {
            [HarmonyPrefix]
            public static void Prefix()
            {
                if (Plugin.IsMultiplayer && !Plugin.IsHost)
                    CapturePreStats();
            }

            [HarmonyPostfix]
            public static void Postfix()
            {
                SendDeltaIfChanged();
            }
        }

        /// <summary>
        /// Capture random elixir consumption (ShipItemRandomElixir.OnAltActivate).
        /// </summary>
        [HarmonyPatch(typeof(ShipItemRandomElixir), "OnAltActivate")]
        public static class ShipItemRandomElixirOnAltActivatePatch
        {
            [HarmonyPrefix]
            public static void Prefix()
            {
                if (Plugin.IsMultiplayer && !Plugin.IsHost)
                    CapturePreStats();
            }

            [HarmonyPostfix]
            public static void Postfix()
            {
                SendDeltaIfChanged();
            }
        }

        #endregion

        #region Recovery Sync

        /// <summary>
        /// When the host recovers the boat, keep the guest connected: tell them to pause boat sync, then once
        /// recovery finishes (boat now at the last port) resend world state so they're teleported onto the
        /// recovered boat via the normal join path. (Previously the guest was disconnected.)
        /// </summary>
        [HarmonyPatch(typeof(Recovery), "RecoverPlayer")]
        public static class RecoveryRecoverPlayerPatch
        {
            [HarmonyPrefix]
            public static bool Prefix(RecoveryReason reason)
            {
                // Backstop (the Recover button is also hidden for guests): a guest must never run vanilla
                // Recovery, which teleports the SHARED boat locally and desyncs the host. StartMenu.ButtonClick
                // set GameState.recovering=true before calling this, so reset it and skip the original.
                if (Plugin.IsMultiplayer && !Plugin.IsHost)
                {
                    Plugin.Notify("Only the captain can recover the boat.");
                    GameState.recovering = false;
                    return false;
                }

                if (Plugin.IsMultiplayer && Plugin.IsHost)
                {
                    // FIX 3: host faint OR the manual Recover button both funnel through here. If a co-op
                    // sleep is in progress, end it cleanly (broadcast WakeUp to the guest AND clear the
                    // local vanilla sleep) BEFORE recovery's internal FallAsleep/WakeUp run, so the guest
                    // isn't left blacked-out at 16x and recovery operates on a clean AWAKE co-op state.
                    var _sm = SleepSyncManager.Instance;
                    if (_sm != null && _sm.CurrentState != SleepSyncManager.SleepState.Awake)
                    {
                        VerboseLogger.RecoveryEvent($"Co-op sleep in progress at recovery; forcing wake before recovery teleport (state={_sm.CurrentState})");
                        _sm.ForceWakeCrew();
                    }

                    Plugin.Log.LogInfo($"[RECOVERY] Host recovery starting (reason={reason}); guest will re-sync onto the recovered boat");
                    Debug.VerboseLogger.RecoveryEvent($"Host recovery starting, reason={reason}");
                    VerboseLogger.RecoverySend($"RecoveryStarted packet, reason={reason}");

                    // Tell the guest to pause boat sync + show a notice (NO disconnect).
                    Plugin.NetworkManager.SendToAllReliable(PacketType.RecoveryStarted, w =>
                    {
                        w.Write((byte)reason);
                    });

                    Plugin.Instance.StartCoroutine(ResendWorldStateAfterRecovery());
                }

                return true;
            }

            // Wait for the host's recovery to finish (GameState.recovering goes true then false inside
            // Recovery.DoRecoverPlayer), then resend the boat/world state so the guest re-boards the boat at
            // its new location. Types fully-qualified - this file deliberately has no `using UnityEngine`.
            private static System.Collections.IEnumerator ResendWorldStateAfterRecovery()
            {
                float t0 = UnityEngine.Time.time;
                while (!GameState.recovering && UnityEngine.Time.time - t0 < 5f) yield return null; // wait for start
                while (GameState.recovering) yield return null;                                     // wait for finish
                yield return new UnityEngine.WaitForSeconds(0.5f);                                  // let physics settle
                Plugin.Log.LogInfo("[RECOVERY] Host recovery complete, resending world state to the guest");
                VerboseLogger.RecoverySend("Resending BoatWorldState after recovery; guest will re-sync onto recovered boat");
                Plugin.BoatSyncManager?.SendBoatWorldState();
                // INDEPENDENT NEEDS: do not re-seed the guest's stats; the guest keeps its own needs.
                Plugin.EconomySyncManager?.SendFullStateImmediate();
            }
        }

        #endregion
    }
}
