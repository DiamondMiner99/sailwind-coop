# Roadmap

The one place that says where things stand, for Sailwind Co-op and the Sailwind Player Model mod it
depends on. Update it at every release and whenever an item changes state. If this file and another
doc disagree, this file wins; the older plan docs keep their design detail but not their status.

Last updated: 2026-11-04.

## Released

| Mod | Version | Date | What |
|-----|---------|------|------|
| Co-op | v0.5.0 | 2026-11-04 | Houses, Sailwind 0.39 and 0.40, shared sea, placement bucking, missing items, hooks, belt lanterns, stream-in with full state. Never played. Bundles Player Model v0.1.6 |
| Player Model | v0.1.6 | 2026-11-04 | Saved outfits, one-handed seated wheel, gear and glove rows, arms on tables, zoom keys, clone stance, low-deck camera, follow camera orbits a standing player. Built against Sailwind 0.40. First cut pulled within minutes (camera aboard); the re-cut's fixes have not been played |
| Co-op | v0.4.0 | 2026-09-15 | Crewmates working the ship and sitting, weather, market, invites screen, CoopStatus |
| Player Model | v0.1.5 | 2026-09-18 | Seated helm controls, third person key V and look lock H, item use in third person, follow camera and seating fixes. Released without an in-game test pass; the co-op v0.4.0 zip was repacked with it |
| Player Model | v0.1.4 | 2026-09-17 | Follow camera jumping in and out while the inventory or the needs bars show. The co-op v0.4.0 zip on GitHub was repacked with it (only the model DLL changed) |
| Player Model | v0.1.3 | 2026-09-15 | Interaction and item poses, sitting, the follow camera, the seated first person body |
| Co-op | v0.3.2 | 2026-08-09 | Guest-held items vanishing on the host |
| Player Model | v0.1.2 | 2026-09-14 | Character clone fix, undress guard, live sole offset, color options |

## Co-op v0.5.0 (published 2026-11-04, never played)

Went out with Player Model 0.1.6 (section below); the co-op zip bundles its DLL. Gambling and the Thunderstore and
Nexus listings wait for the next release. Rig Balance is separate. Everything in this section is what players are
the first test of.

### Sailwind 0.40 and player reports, 2026-11-03

The game updated to 0.40 (Steam build 26333220): foraging, tobacco packs, volumetric fog, bloom setting. 23 game
files changed and 8 are new. No patch target was lost and co-op builds against it. All causes come from reading
code. No log has confirmed that any of them is what the reporters hit.

From Discord, 10/12 and 10/26:

- **Missing placed items, surviving a relaunch.** A join could cache and delete the host's items on a boat still
  out of range of the guest's saved position. Guarded (`Coop.JoinCacheGuard`), and distances are re-measured as the
  join ends. Items destroyed by the join clear, the orphan cleanup and resync are now unregistered from the save
  list; before, a destroyed entry threw from the next save or the next stream-out.
- **Boat and house contents on stream-in.** A guest boat could never respawn its cache. The guest now discards the
  cache and asks the host, which answers with full save data on new packets 228 and 229, and sends the same unasked
  when its own copy streams in (`Coop.HostItemsOnStreamIn`). Crate membership, food, charts and nails now arrive.
- **Wall hooks and hung items.** `attached` now travels in the drop packet, so a hook stays on the wall on every
  machine. A hang whose item or hook is missing no longer strands the item hidden; it is re-armed, the missing part
  is requested (packet 230), and the hang is applied once both are ready. A hang arriving before the rigidbodies
  exist is queued too, which used to wedge the hook.
- **Ship bucking and damage on placement.** A crewmate's carried item is weighed at their position, only while they
  are aboard, and the center of mass eases when one is set down (`Coop.RemoteHeldMass`). Item subcolliders no longer
  count as hull damage. A guest's drop shoving the host's hull into a dock can still do real damage.
- **Lanterns.** Toggles reach a lantern hidden on a crewmate's belt, and the crewmate carries a stand-in light while
  it burns (`Coop.BeltLanternLight`). A lantern already belted when someone joins was not verified.
- **Guest needs draining about 3x as fast.** NOT FIXED, no cause found. No guest-only multiplier exists in the code.
  Added a `[NEEDS]` log line every 30 seconds on both machines; comparing them separates a real multiplier from the
  conditional drains (host resting in bed, frame-rate-dependent running and swimming, alcohol).

New in 0.40:

- **Tobacco packs.** Cutting one is synced; before, everyone lost the pack and only the cutter got the tobacco.
- **Randomized forage spawners.** The host spawns these for the crew, because each machine picked its own spot.
  Scene data was not checked, since the patch does nothing for a spawner without a randomizer.

Known limits from review, left as they are:

- A guest alone on an island gets no randomized forage, because the host only spawns within 300 m of its own camera.
- If the host's island unloads, a guest's copies of its forage may stay, and picking one up is refused until purged.
  Assumes spawners live in island scenes; not verified.

### Sailwind 0.39 and earlier reports

Diagnosis and review detail: `docs/plans/2026-10-01-houses-and-user-reports.md`.

The game updated to 0.39 on 2026-09-25 (Steam build 25535729): houses you can buy and store items in,
a new large hull in Oasis, three new sails in Gold Rock City, economy rebalance, and fixes to inactive-boat
physics and the in-bed camera. 25 game types changed, 5 are new, and no method signature moved - so every
Harmony patch across our mods still applies and all eight mods still compile. The damage was all behavioral.

- **Buying.** Houses are `PurchasableBoat` objects with no `BoatRefs`, so every ownership path that went
  through the boat registry dropped them. Host and guest can both buy one, the sign clears and the door
  unlocks for everyone, and a joiner gets all six houses' ownership replayed in both directions. A joiner
  whose own save owned something the host does not now gets the FOR SALE sign back as well as the lock.
- **The purchase error.** 0.39's `PurchasableBoat.PurchaseBoat` walks a sparse `Mast[48]` with no null check
  and throws on every boat purchase, after the purchase has completed. The ownership broadcast moved to a
  Harmony finalizer, which swallows that one error and nothing else.
- **Storing items in a house.** An item a crewmate carries out of a house is un-filed from it on every
  machine (it used to stay filed under the house everywhere but the carrier's, and was swept back into the
  house and deleted 1 km later, wherever it had been put down). A remote drop is filed under the boat or
  house it lands in. Rotation travels in the same frame as position, so furniture keeps its facing in the
  five houses that are not square to the world. A house or parked boat that streams in from a crewmate
  first no longer spawns a second copy from the local cache. The far-away teardown runs for house items,
  and a joiner's own house cache is emptied.
- **Doors.** The new hinged door class (house front doors, the five cabin doors on the Large Sanbuq) syncs
  live and on join, on the existing door packet.
- **House sleep.** The host's own bed decides the kind of sleep: host in a house bed means the crew sleeps
  to the morning window; host in a bunk means an ordinary boat sleep, whoever else is in a house. House and
  bunk always pair. Guests follow the host's timeskip for any bed that is not on a boat, which 0.39 made
  vanilla. The notice says "Crew wakes in the morning" for house and tavern sleeps. A house night holds
  food and water where they were, the way a tavern night pins them: co-op blocks the thirst and hunger wake
  for both, and without the hold a long house night ran the needs down to the floor.
- **Game version.** The host stamps the game version into the lobby and a guest on a different one is
  refused with a plain message. The mod logs an error at load on a game older than 0.39.

Still open for houses, by choice or pending a session:
- A guest at the house while the host is over 1 km away cannot take items out. Heals when the host returns.
- Whether guests need the jib hinge reset 0.39 added to the purchase. Check a jib on a boat bought mid-session.

The other mods' 0.39 fixes (Three Sheets, Rich Presence, Rig Balance, Ship Blueprints, Outboard) sit
uncommitted in their own repos.

### From player reports, 2026-09-22 to 09-24 (v0.4.0)

- **The ship jerks and booms when a crewmate sets something down.** FIXED. A v0.4.0 regression: a crewmate's
  body kept posing a held item, and its physics object, for one frame after release, which is the frame the
  drop has just re-armed it, so it was pushed into the hull. The pose is now released in the same step as
  the drop, hang or crate insert. A log line records any item collider striking a hull.
- **One player sees the boat half submerged.** FIXED IN CODE, the cause is proven from the game's code but
  not yet seen in a log. Each machine's swell carried its own floating-origin history in its wave phases, so
  host and guest had different water at the same spot, and the boat corrector pulled the guest's hull
  toward a height that was under the guest's water. Three changes behind `Coop.SharedSea` (default on):
  wave phases are a function of real position on every machine; the corrector no longer pulls a hull down
  into its own water and its snaps keep the local height; water level is sent for every boat that has any,
  not only the host's. This is very likely the vertical divergence measured on 2026-08-06 as well.
- **Invites button on the title screen.** REMOVED. It stays in the pause menu.
- **Camera about 0.3 m off its turning axis after walking into things.** FIXED IN CODE, in the Player Model
  working tree (uncommitted, on top of another session's work in progress there), without knowing which of
  two causes the reporters hit, so both are closed. The follow view held the eye exactly 0.3 m behind the head
  when squeezed, where it swung on a circle; with under 0.3 m of room the eye now goes onto the turning axis,
  eased in up to 0.6 m. And vanilla's head smoothing could latch a sideways offset on a heeled deck that
  nothing reset; a postfix zeroes it and logs the first one it finds.

## Player Model v0.1.6 (published 2026-11-04)

main `e9c6f9d`, tag v0.1.6, DLL md5 `4d861ab2`; the next co-op release's zip must bundle this DLL. The first cut (`35681af`, DLL `c4a28acd`) was pulled within minutes because the follow camera (V) broke aboard: a boat's rough root capsule, which the head sits inside on deck, read as a low deck, so the view kept re-aiming at the head. The camera now skips those capsules, and `[Camera] low deck over the head` in the log names anything that still triggers it. Not yet played aboard. The camera orbits a player standing still, the body turns to it on moving, throwing or dropping, and the look lock is unbound on new installs, which leaves X and V as the mod's default keys. Checked against Sailwind 0.40
(build 26333220) by code review and asset dumps only. Every patch target and reflected member still resolves on
0.40, and co-op's use of the public API still matches. No co-op change is required. Added to the API:
`TryGetLocalSeatTable`, `SyntyBody.SetSeatTable`, `PlayerAppearance.CoreSlotCount` and `Forget`, and `SlotCount`
grows from 9 to 17.

- From the 2026-10-01 work: a body cloned from a harbormaster with an odd stance (crossed arms, hands on hips; 5 of
  33 on 0.40), the camera at your hips under a low deck, Quit on the press-F screen, gear rows and separate gloves,
  arms on tables, zoom keys.
- Sitting at the wheel: one hand on the wheel from a seat (`SeatedWheelOneHand`), held with the mouse-steering
  settings as well as a click, and a ledge seat swings or turns toward a control behind it. Floor seats still stand up.
- Saved outfits: an Outfits column on the Character screen, no limit, kept in their own text file.
- Crouch key and sit key on the same key: crouch wins, with a one-time hint. Crouch cannot build up while seated.
- Seat hints and the (seated) label are measured to their text and carry no background.

Still open, no logs: stretched text at the bottom of the screen on 0.40 (M.I.D.N; Player Model's only bottom text is
hardened above, but it may be another mod), and dogeggz's "won't sit on the Sh'ba" (its colliders follow the stock
layout, nothing found). Crewmates' hands stay on the thighs at a table unless co-op sends the table.

## Player Model v0.1.5, known and deferred

Released 2026-09-18 with no in-game test pass, so player reports are the first pass. Every feature has an off
switch in its config (Seating Enabled, FollowCamera, Interactions Enabled, LookFollowsCamera, CameraKeyFollows,
UIScale). Known at release: Quit on the press-F screen opens a dialog whose Quit does nothing (press F first), a
barrel drunk from cannot be shelved until dropped, and wheel-versus-tiller is decided from the boat attitude at
the first grab of a session. The repo has uncommitted work in progress from another session as of 2026-10-01.

## Open bugs, blocked on logs from a real session

- Fishing: two fish from one catch. Guarded and logged, cause unknown.
- Grab collisions.
- Guest boat vertical divergence, median 5.7 m. Now has a proven cause and a fix (shared sea, above); needs
  a guest's verbose log to confirm. 0.39 also removed the per-machine float mode that could offset a boat
  the host was not on.
- Guest needs draining about 3x as fast as the host (above). Needs `[NEEDS]` lines from both machines.
- Lead, no report yet: a boat's rough root capsule sits on layer 13, which Player Model found counted as a low
  overhead. Co-op's feet probe (`PlayerSyncManager.cs:280`) and join placement on land (`BoatStateApplicator.cs:1771`)
  do not exclude that layer. A guest on a dock under a moored boat's capsule could read as standing on the boat. Not
  verified that the capsule stops these rays.

## Next up, in order

The next release is the gambling update, with Thunderstore and Nexus listings. Check first for a clash with
pander33's SailwindCoop, which has a different GUID but the same `plugins/SailwindCoop/SailwindCoop.dll` path and also
claims F8. Crewmates' hands on tables (`SetSeatTable`, added by Player Model 0.1.6) go in that release too.

1. **Downed state (Player Model, then co-op).** Built, switched off in both. Blocker: a boat that brushes a
   fallen body launches it. Co-op sends the ragdoll's parts on the rest packet (227, states 3 and 4).
2. **Swimming animation (Player Model, then co-op).** Built 2026-09-15, switched off, not tested.
3. Throw and shove animation (Player Model). Not built.
4. A crewmate's view of sitting, poses and the follow camera has still never been seen by a second player.

## Planned, not scheduled

- Quartermaster. Host appoints one, default host, reverts on leave. Picks a split: shared, manual
  allowance, even split, percentage. Two ways in: per-player buckets behind the host wallet, or just
  the role plus a permission gate on big spends. Either way, first audit every spend and income path;
  the onsen bath (island 39, Emerald Archipelago) is a known unrouted spend, so a guest bathes free.
- HMS Leopard cannon fire. Cosmetic-only broadcast, same shape as the bell.
- Cold-launch invite auto-join. Wants its own release.
- Player Model on Thunderstore, so r2modman users stop importing by hand.
- Player Model: a config switch to force the shopkeeper as the clone source.
- Shoving crewmates (co-op easter egg). Any player can shove; the host can switch it off for the session in
  settings. T with empty hands shoves the crewmate in front of you, with the same animation as throwing; T while
  holding anything is always the game's own throw and never shoves. Hit hard enough or often enough in a short
  time, they fall over using the Player Model's downed state (Next up, item 5).

## Backburner

- Poses for other mods' controls: HMS Leopard gun carriages (push while running out), the Realistic Skies
  observatory telescope, a short reach-and-press for single-click controls (cannons, bell, ladders, the
  game's doors and hatches). Leopard oars wait on sitting.
- Onsen exploit fix on its own. Folds into the quartermaster spend audit.

## Not doing

- Multi-boat. Decided 2026-07-10: the mod is about crewing one ship together.
- Ghost markers and wind streaks in Rig Balance. Built, then removed by decision.
