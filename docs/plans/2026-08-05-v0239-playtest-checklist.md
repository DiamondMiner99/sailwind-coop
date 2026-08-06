# v0.3.0 two-player playtest checklist

Generated 2026-08-05 from the v0.3.0 diff, during the two-machine playtest.
Already confirmed working this round: join with matched mods, appearance sync (join + live change),
the mod-set refusal path, pause menu, notifications.

# v0.3.0 two-player test checklist, ranked

Ranking is (chance this release broke it) x (how loud the breakage is in play). Everything below is code I read in `C:/Users/justi/source/repos/sailwind-coop-r4`. I mark **VERIFIED** (read it) vs **INFERRED** (deduced, not observed).

---

## Tier 1 - new code that only runs with two machines, so nothing before now has exercised it

### 1. Guest hull physics derivation (flooding, boat height, sinking)
**Why risky:** brand new and structurally untestable solo. `DamagePatches.cs:85` `ApplyGuestHullPhysics` is a hand transcription of the *second half* of vanilla `BoatDamage.UpdateWaterAndDrag` (decomp: `C:/Users/justi/source/repos/sailwind-coop/decomp_sw/BoatDamage.cs:212-268`), running on the guest every frame for **every hull in the scene**, writing `BoatProbes._forceMultiplier`, `Rigidbody.drag` and `boatCol.enabled`. The commit itself says "you are the first people to sail it" (`60cac08`). Kill switch `Coop.GuestHullPhysics` at `Plugin.cs:342-351`, read live per frame at `DamagePatches.cs:62`. **VERIFIED.** One transcription difference I confirmed: the mod clamps `waterLevel`/`hullDamage` *before* computing drag, vanilla clamps after (`decomp_sw/BoatDamage.cs:220-243`). Harmless in steady state; a one-frame difference on the frame a packet lands. **INFERRED.**

**Test with two players:**
1. Both aboard, calm water, moored. Guest and host each look at the waterline against the dock. Same height?
2. Host rams a rock or takes a few knocks to open a leak. Watch the boat settle over 30 to 60 seconds. Both screens should sink together and get sluggish together.
3. Guest works the **bilge pump** (guest-side pump drains `waterLevel` locally, `DamageSyncManager.cs:368` comment) and separately bails with a bucket (`DamageSyncManager.cs:219`). Boat should rise on both screens at the same rate, not just the pumper's.
4. Deliberately sink a boat with the guest present. Guest should see it go under, hull collider off, not float as a ghost. Then rejoin and confirm the wreck does not come back buoyant.
5. **The regression case that mattered:** guest leaves, rejoins the same host. Boat should be at the correct height immediately, and should not need the phantom save deleted.
6. Look at NPC boats and moored AI hulls on the guest screen. They now get this derivation too. Any of them riding high, sitting low, or twitching is this change.
7. If anything looks worse than v0.2.38, flip `Coop.GuestHullPhysics = false` in the guest's config (live, Configuration Manager) and see whether it goes away. That answer is the single most valuable thing this playtest can produce.

Log line to grab either way: `HULL_STATE` from `BoatSyncManager.cs:696` (once per second per boat, includes buoyancy, water level, drag, com-vs-keel, distToLand).

---

### 2. Ashore crewmate no longer wrenches the boat's center of mass
**Why risky:** the fix works by **nulling `GameState.currentBoat` for the duration of every `BoatMass.UpdateMass` call** and restoring it in the postfix (`BoatPhysicsPatches.cs:64-99`). That is a global the whole codebase reads. It is **not gated on `IsMultiplayer` or `IsHost`** (VERIFIED, `BoatPhysicsPatches.cs:65-88` has no such check), so it changes singleplayer and host behavior too. The "am I aboard" test is `observer.IsChildOf(boat.transform)` (`:78`).

**Test:**
1. Guest walks off onto the dock and stands 50 to 100m inland while the host watches the boat. Boat should sit level and still. Before this fix it librated.
2. Now do the reverse: **host** goes ashore while the guest watches.
3. Both aboard, both walk to the same rail on a small boat (sloop or ketch). Should heel a little, not flip. `Coop.CrewMemberWeight` defaults to 90kg (`BoatPhysicsPatches.cs:121`).
4. **The suspicious case:** aboard but *not parented under the boat*. Sit in the helm seat, get in a bunk, start a sleep, enter the shipyard, climb the rigging. If your weight drops out of the trim in any of those, you will see the boat visibly change trim the moment you sit down. **INFERRED risk**, worth ten seconds of looking.
5. Confirm the moored/anchored skip still holds (`:116`): moored at a dock, drive the throttle forward with both players aboard, deck should not go under and flood.

---

### 3. Mooring rope carry sync (new packet 221)
**Why risky:** new wire type, new receiver-side patch that **suppresses vanilla's `OnTriggerEnter` entirely** for remotely held ropes (`ControlPatches.cs:546-563`, `MooringRopeHoldSync.cs:26-31`). If the suppression latches wrong, mooring stops working for real. Commit says "UNTESTED live; the rope sync needs two machines" (`10bfd5d`). **VERIFIED.**

**Test:**
1. Guest picks up a bow rope and walks it to a dock cleat while the host watches. Host should see the rope in the guest's hands, moving with them, then moored.
2. Same in reverse (host carries, guest watches).
3. Guest picks up a rope and **drops it without mooring**. On the host it should snap back to its parked position (`MooringRopeHoldSync.cs:105` calls `ResetRopePos()`), not hang in the air.
4. **Phantom moor check:** guest picks up a rope and walks *past* several dock cleats without mooring, then walks away from the dock. The host must not see a rope moor itself. This is the exact failure the trigger suppression exists to stop.
5. **Both players grab ropes at once** on the same boat, walk to two different cleats, moor both. Ropes are addressed by `(boatName, ropeIndex)` (`MooringRopeHoldSync.cs:154-165`), so index confusion would show as the wrong rope moving.
6. **The gap I found by reading:** `MooringRopeHoldSync.Clear()` is only called on full lobby teardown (`Plugin.cs:864`). There is no per-peer cleanup. So: **guest picks up a rope, then Alt+F4 or leaves the lobby while still holding it.** On the host, `LateUpdate` hits a null capsule and `continue`s (`MooringRopeHoldSync.cs:138`), which leaves the rope frozen wherever the guest was standing. Check whether the host can still pick it up and moor normally. **VERIFIED code path, INFERRED symptom.**
7. Guest picks up a rope while the boat is being unmoored/departing, so the rope goes out of range and vanilla force-drops it (`decomp_sw/PickupableBoatMooringRope.cs:132-136`). Host should see it released.

---

### 4. Out-of-world rescue watchdog
**Why risky:** brand new, teleports a live player, and its boat-resolution was *already wrong once in this same release* (fixed at `2ed1050`: `GameState.currentBoat` is cleared when you enter the water, so the rescue would have said "no ship" while the ship was moored nearby). Now falls back to `GameState.lastBoat` then crew boat by name (`PlayerSyncManager.cs:231` `ResolveRescueBoat`). Bands: y < -300 or y > 2000, 3s dwell, 10s cooldown (`PlayerSyncManager.cs:124-128`). **VERIFIED.**

**Test:**
1. Guest jumps overboard and swims around for a minute. The rescue must **not** fire. This is the false-positive case the review pass was worried about.
2. Guest dives deep, climbs the mast, stands on a mountain top. No rescue.
3. Force the real case: guest joins and, if you can reproduce the fall at all, let them fall. Otherwise use noclip/console if you have one, or sink the boat under them. Expect "You fell out of the world. Back aboard." after ~3s and the **ship not to move** (only the player is repositioned).
4. Rescue while the host is ashore and the boat is moored somewhere else. Does the guest land on the boat or on the host?
5. Trigger it twice in ten seconds if you can, to confirm the cooldown stops a teleport loop.
6. Host has no boat at all (sold it, standing on a dock) and the guest falls: expect "there is no ship to return you to" rather than a null-ref spam (`PlayerSyncManager.cs:187-190`).

---

## Tier 2 - reported bugs whose fixes are live for the first time

### 5. Sail sync after any shipyard visit
**Why risky:** this is Andriy's reported bug (sail changes stop syncing **both ways** for the rest of the session after one shipyard visit). The fix replaces dead code (`GetCurrentBoat()` was always null there) with a coroutine that broadcasts **reliable rope-length terminals to the whole crew** one frame after shipyard exit (`ShipyardSyncManager.cs:465-510`, new `ControlSyncManager.ResendRopeForBoat` at `:640-660`). If the deferred frame is wrong, the crew gets the *doomed pre-rebuild* rope lengths as an uncorrectable terminal. The comment says so explicitly. **VERIFIED.**

**Test:**
1. Host enters the shipyard, changes nothing, exits. Then host trims sails with the winches. Guest must see the sails move.
2. Guest trims sails. Host must see it. (The bug killed *both* directions.)
3. Host enters the shipyard and **actually changes the rig** (add or remove a sail, change a mast), exits. Then check both directions again, and check the guest's sails are not stuck at rebuilt defaults or at pre-edit trim.
4. Repeat with the **guest** doing the shipyard visit.
5. Host log should now contain a "re-seeded N rope lengths" line after each visit. Its absence in Andriy's log is what proved the path was dead.

### 6. Sustained-divergence teleport escalation (new snap path)
**Why risky:** new way for a boat to teleport on a guest. Fires when error stays above 6m for 4 continuous seconds (`BoatSyncManager.cs:152-153`, `:606-617`). It deliberately excludes rope-moored boats (`:598-604`) because the teleport does not carry mooring joints. A false fire under a standing player is very visible. **VERIFIED.**

**Test:**
1. Long sail in weather with both aboard, guest watching the deck under their feet. Any sudden snap of the whole ship is this. Grep the guest log for `SUSTAINED_DIVERGENCE`.
2. Moored at a busy dock, host drives against the springs. The escalation must **not** fire (moored exclusion). If a moored boat teleports, that is the dangerous case.
3. Anchored, host drags anchor in wind. Escalation *is* allowed here, and `CarryAnchorWithBoatTeleport` is supposed to bring the anchor. Confirm the anchor does not stay behind.
4. Guest stands on deck while it fires, if you can catch it: do they get left behind or launched?

### 7. Held item behavior (smoothing, land poses, shipyard frame)
**Why risky:** three separate changes stacked. Remote held-item visuals moved from `Update` to a new `LateUpdate` (`ItemSyncManager.cs:781-806`), a `SmoothDamp` at 1/15s with discontinuity detection (`:908-919`, `SmoothTowardTarget`), and on-land poses now store **real** origin-independent position with the floating-origin offset added per frame (`:934-947`, `:1080-1092`). Plus a held-item frame fix for the `GameState.currentBoat == null while still parented` case reachable via `Shipyard.DischargeShip` (`PlayerSyncManager.cs:464-505`). **VERIFIED.**

**Test:**
1. Guest carries a crate around the deck while the host watches. Item should sit in the hand, not swim or stutter.
2. Guest carries a crate **on land**, walks a long way so a floating-origin shift happens. The item must stay in the hand, not tear off.
3. Guest carries a crate, walks from the dock **up the gangplank onto the boat** and back off. The discontinuity snap should step it, not slide it across the deck.
4. **The shipyard case:** hold an item, use the shipyard, release the ship. Item must not fly to a nonsense position on the other screen.
5. Both players carry items at once (per-carrier map, `_syncedHeldItems`), and swap: guest drops, host picks up the same crate.

### 8. NPC boats visible to the guest at docks
**Why risky:** the cache now rebuilds on miss every 2 seconds (`NPCBoatSyncManager.cs:618-640`), which is a `FindObjectsOfType` scene scan. Fixes invisible docked ships, but a genuinely unresolvable path now costs a scan every 2s forever. **VERIFIED.**

**Test:**
1. Both sail into a busy port (Al'Ankh or Emerald Archipelago). Guest should see the same docked ships the host sees. Count them.
2. Guest sails away 2km and comes back. Ships still there on both screens?
3. Watch the guest's frame rate at a busy port for a periodic hitch. That would be the rebuild scan.

---

## Tier 3 - lobby, presence and UX paths

### 9. Friends list, ask-to-join, accept-invite
**Why risky:** all new (`CoopPresence.cs`, `FriendsScreen.cs`, 599 + 357 lines), and the review pass already caught four defects in it after it was written (`2ed1050`: accepting a request re-armed it and sent a **second real Steam invite on every click**; a dismissed asker vanished from the list; an invite after an expired ask was swallowed by the seen-invite de-dupe). Presence is throttled to 1s publishes with forced pushes at three points (`CoopPresence.cs:222-229`). **VERIFIED.**

**Test (needs both Steam accounts to be friends, presence only replicates between friends):**
1. Host starts a session. On the guest, open pause menu, Friends. Host should appear as sailing, with avatar, ship name, and crew count.
2. Guest clicks "ask to come aboard". Host gets a prompt. Host clicks "Let aboard". Guest joins.
3. **The re-arm case:** after the host accepts, watch for a *second* invite arriving at the guest, or the request reappearing in the host's list on the next sweep.
4. Host clicks "Not now" instead. Guest should see it settle. Host's friends list should still show that person (the vanish bug).
5. Guest asks, then cancels before the host answers.
6. Guest asks, then the host answers *after* the ask expired. Should still work (de-dupe fix).
7. Crew count and "captaining what" should update within a second of someone joining or leaving.
8. **Steam overlay disabled** on the inviting machine. The friends-list invite path is supposed to work anyway (it uses `InviteUserToLobby` directly, not the overlay).

### 10. Stale invite / refused join reporting
**Why risky:** the old code discarded Steam's `EChatRoomEnterResponse` and could **quit the player's game** while blaming their mods. Now uses `Lobby.Join()` and reports the real reason (`SteamLobbyManager.cs`, commit `bb376e0`). Steam re-delivers pending invites on every launch, so this is the common path. **VERIFIED.**

**Test:**
1. Host invites the guest, then **closes the lobby before the guest accepts**. Guest accepts. Expect "session no longer exists", not a mod-mismatch message and not a game quit.
2. Guest restarts the game and accepts the same now-dead invite from the previous session.
3. Accept an invite from the **title screen** vs from the **pause menu**. The title path is the one that used to call `Application.Quit()`.

### 11. Joining and hosting from the pause menu (timescale)
**Why risky:** two separate timescale-freeze bugs found on the last two-machine test and fixed in this build (`9d1d3ca`: guest ran the whole join coroutine at `timeScale 0` and the 45s watchdog concluded refusal; `4a6ddb0`: host who started hosting from the pause menu stayed frozen and nothing it owed the crew ran). Fix is a re-assert while the menu is open (`CoopPauseMenu.cs`). **VERIFIED.**

**Test:**
1. Host starts hosting **from the pause menu** and **leaves the menu open**. Guest joins during that. Should complete, not time out at 45s.
2. Guest accepts an invite from an **open pause menu** and does not touch anything. Join should complete on its own.
3. Host opens and closes the pause menu repeatedly during a guest's join.
4. Watch that the world clock does not stay frozen after either menu closes.

### 12. Settings reconcile (guest borrows host config values)
**Why risky:** it writes into another mod's `ConfigEntry` and holds `SaveOnConfigSet = false` on that file for the **whole session** (`ConfigAdoption.cs:47-59`, released at `:66-76`). If teardown misses, the guest's own singleplayer settings get quietly rewritten by whatever the host had. Only matters if you both run NAND Tweaks / SCF / Shipyard Expansion. **VERIFIED.**

**Test:**
1. Note a NAND Tweaks or SCF value on the guest, set it *differently* from the host, join. Expect a join (not a refusal) plus a toast saying settings were borrowed.
2. Confirm the borrowed behavior actually applies in play.
3. Leave the lobby. Check the guest's config **file on disk** is unchanged and the in-game value is back.
4. Save the game during the session (SE's own `DoSaveGame` writes to the same file) and re-check step 3 after leaving.
5. `addSails` differing should still be a **hard refusal**, not an adoption.

### 13. Shipyard Expansion isolation (only if you run SE)
**Why risky:** five changes to the join apply order, and the cascade was **reconstructed from logs, never directly observed** (the commit says so, `336482c`). `ApplyOwnership` moved to the top of Phase A (`BoatStateApplicator.cs:679`), part options clamped to the local count (`:836-870`), `boatDataPairs.Add` moved above Phase A (`:368`), a per-boat guard added to the host-side collector (`BoatStateCollector.cs:32-42`), and SE now advertises bundle health as `/noBundles` (`SECompat.cs:151-160`). **VERIFIED.**

**Test:**
1. Both with a healthy SE: guest joins and boards. Boat should have sails, winches, barrel, lanterns, **no FOR SALE sign**, and grabbable mooring ropes.
2. Deliberately break the guest's SE bundles (rename `shipyard_expansion.assets`). Expect a **named refusal about asset bundles**, not a join into a wrecked ship.
3. Custom rig built in SE on the host, guest joins fresh. Rig should transfer or degrade to cosmetic loss, never take the join down.
4. Guest log check for `[JOIN] PhaseA FAILED for boat` with a stack. That line is the confirmation the whole diagnosis wanted.

---

## Tier 4 - smaller, quick to check

### 14. Dropped items float now (default flipped)
`Coop.RestoreItemBuoyancy` now defaults to **true** (`Plugin.cs:328-333`; it was false before this release). Local-only, no wire change, so a crew with mixed settings sees the host's poses for loose items. **VERIFIED.**
**Test:** both drop crates and barrels over the side. Do they float on both screens, and do they end up in the same place after settling? Then throw a crate overboard from the guest while the host watches it settle (the settle-terminal path).

### 15. Remote avatar look pitch
Was picked by "largest absolute `rotationY` across every `MouseLook` in the scene", which a disabled orbit camera won permanently and folded a crewmate's torso fully forward for a whole session. Now resolved by identity off `Refs.ovrCameraRig` (`PlayerSyncManager.cs:279-330`). **VERIFIED.**
**Test:** guest toggles to the **ship orbit camera** and moves it around, then back to first person. Host should see the guest's head keep the *player's* pitch throughout, not mirror the orbit camera and not stick folded over. Also check after sleeping in a bunk and after a shipyard visit (both use their own `MouseLook`).

### 16. "Aboard the host's ship!" toast timing
Used to fire on lobby entry, up to a minute early, and even when the host was ashore or had refused. Now fires on actual embark and branches on where the host is (`eb0627f`). **VERIFIED.**
**Test:** join while the host is aboard, join while the host is **ashore**, and get refused. Three different messages, each at the right moment.

### 17. Guest has no "Leave Lobby" button
For a guest it did the same thing as Quit Game without saying so. **VERIFIED** from CHANGELOG; check the guest's pause menu.

### 18. "Joining anyway" mod-mismatch panel is sticky
With `AllowModMismatch` on, the panel now waits for a click instead of expiring after 20s, but must still be taken down by leaving the lobby rather than following you into singleplayer (`CoopMessagePanel.cs`, `10bfd5d`; teardown hide at `Plugin.cs:858`). **VERIFIED.**
**Test:** enable `AllowModMismatch`, join with a deliberately different mod, ignore the panel, then leave the lobby. Panel must be gone in singleplayer, and the cursor must be back.

### 19. Fresh install with no save at all
`CoopSave.cs:131-155` now shows "Start a game before joining a crew" instead of failing silently. **VERIFIED.** Only reachable on a Sailwind install that has never started a game, so probably skip unless you have a clean box.

---

## Explicitly not on this list
The broken-mod detector was **reverted** before release (`8ec865f`, `Compat/BrokenModDetector.cs` deleted). Do not test it. Anchor Improvements 1.1.7 being broken on Sailwind 0.38 is now just a KNOWN-ISSUES note (`KNOWN-ISSUES.md:20-26`), not a feature.