# Roadmap

The one place that says where things stand, for Sailwind Co-op and the Sailwind Player Model mod it
depends on. Update it at every release and whenever an item changes state. If this file and another
doc disagree, this file wins; the older plan docs keep their design detail but not their status.

Last updated: 2026-11-24.

## Released

| Mod | Version | Date | What |
|-----|---------|------|------|
| Co-op | v0.6.1 | 2026-11-24 | Sailwind 0.41: boat paint, floating items at sea, dirt and cleaning on every boat surface, and ropes auto-tied to a bollard reach the crew; save guard for the lost-saves report; crate unseal and load-timeout fixes. New packet 249, 239 to 248 still undefined. Never played. Bundles Player Model v0.2.1 |
| Player Model | v0.2.1 | 2026-11-24 | Patterns from pictures in a folder (up to 16), Hand Over Hand haul pose (inactive without that mod), Sailwind 0.41. main and tag `b1d0ef9`, DLL md5 `b8a925c1`. Not played |
| Co-op | v0.6.0 | 2026-11-14 | Postal Expansion, Sail Damage, MusicMod, Viking Horn, HooksHangMore and FurstMate compat, anchor carry sync, stale carry recovery, frame-rate independent needs, fishing bobber, invites in the Friends screen. Sailwind 0.40.1. Never played. Bundles Player Model v0.2.0 |
| Player Model | v0.2.0 | 2026-11-14 | Character screen redesign, any color per part, 16 patterns with hover preview, stubble, paper pause menu, linear-space shaders, at-sea body, bed lying, faster body retry. main and tag `612ed27`, DLL md5 `efecc24f`, zip md5 `b45b0c63` 264658 bytes. Screen, colors and patterns checked in game; bed, at-sea, stubble and the menu not played |
| Co-op | v0.5.0 | 2026-11-04 | Houses, Sailwind 0.39 and 0.40, shared sea, placement bucking, missing items, hooks, belt lanterns, stream-in with full state. Never played. Bundles Player Model v0.1.7 (GitHub zip repacked, only the model DLL changed) |
| Player Model | v0.1.7 | 2026-11-04 | Hotfix: steering from a seat uses the hand nearer the wheel (dogeggz). main `28815ea`, DLL md5 `a9aa9097`. Not played |
| Player Model | v0.1.6 | 2026-11-04 | Saved outfits, one-handed seated wheel, gear and glove rows, arms on tables, zoom keys, clone stance, low-deck camera, follow camera orbits a standing player. Built against Sailwind 0.40. First cut pulled within minutes (camera aboard); the re-cut's fixes have not been played |
| Co-op | v0.4.0 | 2026-09-15 | Crewmates working the ship and sitting, weather, market, invites screen, CoopStatus |
| Player Model | v0.1.5 | 2026-09-18 | Seated helm controls, third person key V and look lock H, item use in third person, follow camera and seating fixes. Released without an in-game test pass; the co-op v0.4.0 zip was repacked with it |
| Player Model | v0.1.4 | 2026-09-17 | Follow camera jumping in and out while the inventory or the needs bars show. The co-op v0.4.0 zip on GitHub was repacked with it (only the model DLL changed) |
| Player Model | v0.1.3 | 2026-09-15 | Interaction and item poses, sitting, the follow camera, the seated first person body |
| Co-op | v0.3.2 | 2026-08-09 | Guest-held items vanishing on the host |
| Player Model | v0.1.2 | 2026-09-14 | Character clone fix, undress guard, live sole offset, color options |

## Co-op v0.6.0 (published 2026-11-14)

Branch `crate-join-resync`: anchors and HooksHangMore `ba4e4ae`, mod compat `601b213`, frame-rate needs fix `7c82944`,
fishing bobber `f83e22a`, Viking Horn `0c82d64`, Friends screen invites tab `c350c91`, review fixes `d84b002`. Code review
done 2026-11-14 (always-on code, then each mod compat). Built for Sailwind 0.40.1 against Player Model v0.2.0 (DLL md5
`efecc24f`), which the zip bundles. Never run in game.

Listings (user's go 2026-11-15): v0.6.0 and Player Model v0.2.0 are listed as published, with no clash message (the
user's call; it waits for the next release). Thunderstore packages built from the published release zips, namespace
`DiamondMiner99`, Player Model a separate package that co-op depends on, BepInExPack `5.4.2305`. Package sources and
`pack_thunderstore.py` in `packaging/` here and in the Player Model repo; notes in `packaging/PACKAGING.md`. Upload
files and Nexus BBCode descriptions in `~/Downloads/sailwind-listings/`. Not uploaded yet: the user uploads, Player
Model first. The Player Model icon is a crop of the co-op icon, a placeholder until there is a sharper screenshot.

- **Review fixes** (`d84b002`). The food-debt refill used scaled time, so the sleep warp gave a sleeper ten times
  vanilla's rate; it now uses unscaled time and matches vanilla at 60 fps with or without the warp. Friends screen tab
  clicks apply on the next Layout event (switching mid-event threw a GUILayout error). FurstMate: the host's first world
  message always applies, so a pet a guest's own save built no longer stays frozen on deck when the host has none
  (a host without FurstMate still leaves it frozen). Sail Damage: a guest that gets no state from the host within 30
  seconds clears its own save's sail damage once.

- **Fishing bobber.** A crewmate's bobber stopped short and crept along, because a position was only sent after it moved
  0.5 m. A change of more than 0.05 m now also goes out, at most once a second, so a bobber that settles a little
  short of its last in-flight position is still sent where it came to rest.
- **Needs drain frame rate.** See "Guest needs draining" under v0.5.0 below. Committed `7c82944`.

From a Discord report, 2026-11-04. The reporter runs HooksHangMore. Committed `ba4e4ae`, never run in game. New packets 231 to 233, 236 (MusicMod) and 237 (Sail Damage). 234 and 235 are reserved for the parked table games; next free id is 239 (238 = FurstMate).

- **A guest's placed map stays in their hand on the host.** CAUSE NOT FOUND. No gate that skips the drop or rejects it
  is specific to maps; maps are wall-attachable as far as a v0.3.0 comment says, which was not checked against the
  assets. What changed: a viewer whose copy is still in a crewmate's hands while that crewmate's position stream
  reports other hands asks the carrier (packet 233) and the carrier re-sends the item's real state; after three
  unanswered asks, about 6 seconds, the viewer lets go of it where it is. Both sides log `[ITEM]` warnings with the
  item's state, so one log names the cause. Two real defects found on the way, either of which gives this symptom in
  some cases: a stale crate id silently skipped the drop, and a duplicate id applied the drop to the other copy.
- **Anchors.** The anchor is a `PickupableItem` but not a `ShipItem`, so no carry was ever synced, and `AnchorEvent` has
  no position. A crewmate could carry it ashore and set it while everyone else's froze in mid-air. `AnchorPoseSync`
  (packet 231) streams the carried pose, sends the pose on release and on set, and the host repeats set anchors every
  5 seconds, which also covers joins and stream-ins. On a remote carry nobody else can grab it, and the rope pays out
  by vanilla's own rule so the joint cannot pull the hull.
- **HooksHangMore.** Its hooks are a dictionary local to each machine and bypass `HangableItem`, so a rod on a hook fell
  to the deck everywhere else. `HooksHangMoreCompat` (packet 232) mirrors attach and detach for items and anchors, and
  replays them to a joiner. Reflected against 2.2.3; a miss disables it with a warning. Not part of the mod parity gate.

Known limits: a rod on a hook catches nothing in co-op, because a drop clears the rod's owner and `CatchFish` needs an
owner, and a hooked fish still escapes when the rod is hung. Neither is new. A hook that streams in after the join does
not get its anchor back until the next join. Two crewmates grabbing the anchor in the same instant each keep it until
one lets go.

## Player Model v0.2.0 (published 2026-11-14)

main and tag `612ed27` (squashed on v0.1.7 `28815ea`), dev `d05739d` on top of the pause menu commit `12eeade`. GitHub
pre-release with `SailwindPlayerModel-v0.2.0.zip`, DLL md5 `efecc24f`, deployed. Code review at high on 2026-11-14 found
nine issues, all fixed before release. The next co-op release must be rebuilt against this DLL; the co-op v0.5.0 zip on
GitHub still carries v0.1.7, which works with v0.2.0 installed over it.

- **Character screen.** Parts and Colors tabs, dropdowns, a docked color picker with hue, saturation and brightness
  sliders, any color on the whole body or on one part, Randomize with back and forward through the last 30 rolls, drag
  or hold to turn the preview, an idle pose on the mannequin. Cloth and leather palette entries named for their shade.
- **Patterns.** 16 kinds on cloth and trim with a scale, on the whole body or one part, drawn by a patterned copy of the
  game's character shader and placed on the rest pose each frame. The list shows swatches and previews on hover.
- **Stubble.** Full, Light or None under Facial hair (Hektor Riven, Discord).
- **Shaders.** Built in linear color space as Sailwind renders, with Mask_01 green and blue the right way round; the
  patterned and seated bodies had lit lighter than the game's own. Confirmed in game.
- **Pause menu** (`12eeade`, from the co-op session). Paper style by default, Scroll as an option (4. Menu /
  PauseMenuStyle), no more overlapping buttons at seven or eight. Not played.
- **At-sea body** (Keevi), **bed lying** (dogeggz), **quarter-second body retry on real time** (HerpDerp, Discord). Not
  played. If HerpDerp's missing body comes back, it was not the retry: ask whether he loaded at sea.
- **Co-op side.** `AppearanceSync` sends the full look string (packet 220) in `601b213`, so crewmates on the next co-op
  release see custom colors, patterns and stubble. On co-op v0.5.0 they see parts and the nearest palette colors.

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
- **Guest needs draining about 3x as fast.** Likely cause found 2026-11-13 by reading vanilla, no log: two needs drains depend on the
  frame rate, so players at different fps drain differently (a 2026-11-12 report had the HOST draining faster). The food-debt refill
  in PlayerNeeds.LateUpdate moves `timescale * 0.5` per FRAME (more fps, more food lost), and DrainEnergyFromMovement charges
  running/swimming from the squared per-frame displacement (per second it goes as 1/fps). FIXED IN CODE for co-op sessions
  (SurvivalPatches, config Coop.NeedsFrameRateFix, default on): both rescaled to vanilla's cost at 60 fps. Solo untouched.
  Alcohol's extra sleep drain is vanilla design, not part of this. Decided 2026-11-13: co-op does NOT scale supplies or needs to
  crew size; a crew carries more food and water.
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

main `73a94c0`, tag v0.1.6, DLL md5 `b4c1cb7e`, replaced in place with no announcement: floor seats work the wheel, winches and pump, and seated reach is longer (SeatedArmReach, SeatedLeanMax). Co-op's zip must bundle this DLL; v0.5.0 was published with `4d861ab2`. The first cut (`35681af`, DLL `c4a28acd`) was pulled within minutes because the follow camera (V) broke aboard: a boat's rough root capsule, which the head sits inside on deck, read as a low deck, so the view kept re-aiming at the head. The camera now skips those capsules, and `[Camera] low deck over the head` in the log names anything that still triggers it. Not yet played aboard. The camera orbits a player standing still, the body turns to it on moving, throwing or dropping, and the look lock is unbound on new installs, which leaves X and V as the mod's default keys. Checked against Sailwind 0.40
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
- Lost saves (Amelia, 2026-11-15): twice, the last hour or more of a session was not saved. Vanilla `DoSaveGame` has no
  try/finally, so one exception leaves `busy` set and every later save does nothing until a restart. A host-side audit
  of co-op found no path that throws there. Suspects: another mod's `SaveModData` hook, a backup file that could not be
  moved, or no failure at all (a different host next session). `SaveGuardPatches.cs` (config `Coop.SaveGuard`) logs
  the exception with its stack, clears `busy` and cleans the save list. Built, deployed and committed 2026-11-15,
  never run. Waiting on her log and on who hosted.
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

**Player Model: custom clothing and custom patterns (next item, asked 2026-11-14, not started).**
- Custom clothing as separate expansion mods. Player Model needs a public API that finds installed packs and reads them:
  new part variants per slot (hat, torso, legs and so on) that the Parts tab lists beside the game's own. Open questions
  to settle before building: the pack format (an asset bundle of meshes skinned to the Synty rig, plus a manifest),
  whether new meshes can use the game's palette and masks so colors and patterns work on them, how a look string names a
  pack part so co-op crewmates without the pack fall back to a stock part, and what a pack author needs (a template Unity
  project or a guide).
- Custom patterns, probably first since it is smaller: a pattern from an image file in a folder, tiled the way the
  built-in ones are placed. Same fallback question for crewmates who lack the image.

Clash check against pander33's SailwindCoop, done 2026-11-04 by reading its source (v0.7.1, MIT). Not run together.
It is already on Thunderstore as `pander33-SailwindCoop` (692 downloads), GUID `com.sailwind.coop`.

- **Manual installs overwrite each other.** Both unpack to `plugins/SailwindCoop/SailwindCoop.dll`, so the second install
  silently replaces the first. The Facepunch DLL beside it is byte-identical, so nothing fails to show it happened.
- **Mod manager installs would run both.** Separate folders and different GUIDs, and neither mod checks for the other.
  Both would patch the same game methods. Both assemblies are named `SailwindCoop`; what that does at load is unknown.
- **F8 only matters when both run.** Stopping that case removes the clash without moving our key.
- **No file or Steam clash found.** Config names, save files and P2P channels differ, and both tolerate Steam already being up.

Our name and install folder stay SailwindCoop (the user's decision, 2026-11-04). Theirs calls itself "Sailwind LAN Co-op" in
its plugin name and release titles. Co-op now declares `BepInIncompatibility("com.sailwind.coop")`, so BepInEx skips ours when
both are present, before either assembly loads; not run in game. The only trace is a line in the BepInEx log, because the
plugin that would show a message is the one skipped. Done for the listings: Player Model is a dependency package, and the
store README says the two cannot be used together or share a manual install. Still to do, next release: have Player Model
show the on-screen message by reading `Chainloader.DependencyErrors`, since it still loads. pander33's 0.8.0 (on
Thunderstore 2026-11-15) still uses GUID `com.sailwind.coop`. Whether Sailwind has a Nexus game page is unconfirmed.

The next release is fixes and mod compatibility, with Thunderstore and Nexus listings (decided 2026-11-10). The table games
are parked on branch `table-games` (commit `5c5b80f`, see Backburner). From a Discord thread, 2026-11-09: a modded crew found
Postal Expansion, MusicMod, "FirstMate" and Sail Damage broken in co-op. None of them is in the compat registry.

All four below are committed (`601b213`), not deployed, never run in game.

- **MusicMod** (Viking-D-Mike/MusicMod, MIT, source in sailwind-modrefs). BUILT 2026-11-10,
  never run in game (MusicMod is not installed here). `Compat/MusicModCompat.cs`, packet 236 `InstrumentNote`, host relays.
  The instruments are ordinary `ShipItem`s (prefab slots 20391-20394), so item sync carries them; only the notes were local.
  Lute and nakers: a postfix on `InstrumentoItem.Golpe(int)` sends each strike and the receiver calls `Golpe` on its copy.
  Flute and hurdy-gurdy: a postfix on `FlautaItem.LeerTeclas` sends play mode and the held note on change; on other machines
  a prefix forces `activo` true for a crewmate's instrument and a transpiler reroutes `Input.GetKey/GetKeyDown` inside
  `FlautaItem.LeerTeclas` to answer from that crewmate's state, so MusicMod's own crossfade, glide, drone and crank play it.
  Notes travel as indices and play through the receiver's binding for that index. A voice ends on a "not playing" packet,
  or when item sync no longer has the instrument in anyone's hands (2 s grace). Names checked in the v1.1.0 and v1.1.2
  release DLLs; a miss disables it with a warning. Not part of the mod parity gate. Known limits: a joiner hears nothing
  of a note already held when they arrive; strike loudness is rolled on each machine.
- **Sail Damage** (FluffyJesus/Sail-Damage, source in sailwind-modrefs). BUILT 2026-11-10, uncommitted, not deployed,
  never run in game (not installed here). `Compat/SailDamageCompat.cs`, packet 237 `SailDamage`. Every machine tore its own
  sails from its own physics. Now only the host accrues (`CheckSwingHit`, `ApplyOverload`, `ApplyStuddingOverload` are
  skipped on guests). Each machine diffs every sail's six values (`DamageModel.Snapshot`) every 0.5 s against the last
  synced ones, keyed by Sail Damage's own `SaveKey`; the host broadcasts changes of 1% or more, or any change once it
  stops moving; a guest's change (hand mending, test keys) goes to the host, which applies and relays it. A sail object
  seen for the first time takes the synced value for its key instead of counting as a change: the shipyard rebuilds every
  sail, and the machine that did not place the order rebuilds them with no damage, which would otherwise have been a
  free repair for the crew. A paid repair (`DamageModel.Mend`) runs on the rebuilt objects, so it is hooked and sent at
  once. Whole is applied through `Mend` so the torn canvas is released. Joiners get a replace-all (their own save's damage
  is cleared through `Deserialize("")`), including the host's not-yet-seen save entries, which go into the joiner's
  `pending`. A guest sends nothing before that arrives. Sailcloth used by sewing goes out as `ItemAmountChanged` with the
  scan. Names checked in the 1.0.5 release DLL. Not part of the mod parity gate. Known limits: a guest mending while the
  host's sails are still tearing can lose up to half a second of either side's change; removing a sail at the shipyard
  can shift the save keys of later sails of the same kind on that mast, so their damage can land on a neighbor; a guest
  is never woken by the storm bell's sleep check, since `Tearing` is set only where damage accrues.
- **Postal Expansion** (DogEggz01/PostalExpansion 1.1.3, installed here). BUILT 2026-11-12, uncommitted, not deployed, never
  run in a session. `Compat/PostalExpansionCompat.cs`, no new packet: `NetworkMissionData` gains PostalKind (urgent, golden,
  registered, anonymous) and the letter id, attached on guests through Postal Expansion's own MarkRuntime / Attach;
  board requests carry the tab and the host builds every tab with `PostalMissionUi.GetAvailableMissions`; accepts carry the
  offer's identity and the host finds it in a fresh list; `DeliverGoodRequest` gains a delivery kind, and the host only
  completes a letter at its own place within its hours (office hand-ins refused, closing a full-reward exploit); a guest
  never runs `Good.Deliver` for mission cargo; the delivery toast uses the special mail name; a mission whose cargo prefab
  is missing is skipped instead of aborting the whole mission sync. Co-op's guest board and PortDude prefixes run
  HarmonyBefore Postal Expansion, which now loads first. Mod-parity segment `PE=<version>`. Known limit: Postal Expansion's
  own page-count postfix may still recount from the guest's data on the first page.
- **Viking Horn** (Viking-D-Mike/VikingHorn 1.0.1 on Thunderstore, GUID `com.hornmod.vikinghorn`). BUILT 2026-11-14,
  uncommitted, not deployed, never run in game (not installed here). `Compat/VikingHornCompat.cs`, no new packet: kind 2 `Horn`
  on 236 `InstrumentNote`. The horn is a `ShipItem` at prefab slot 20390, so item sync carries it, and the raise to the mouth is
  a change of hold offsets that the held-item pose stream already shows. Only the sound was local. A prefix on
  `HornItem.ExtraLateUpdate` reads `usando` for the horn in our hands and sends blowing or stopped on change (and stopped when
  it leaves our hands); the receiver plays the horn's AudioSource once per blow and skips `ExtraLateUpdate` while the crewmate
  blows, since that method fades out any horn not in a hand there. On stop it runs again and fades itself. Not part of the mod
  parity gate. Known limit: a joiner does not hear a blow already in progress.
- **FurstMate** (Dixiecapt/FurstMate, v0.9.6-beta1, GUID `com.roy.furstmate`). NOT a crew mod: ship's pets (a dog bought at
  Bone Isle, a tavern cat, a wild booby). No source and no license in the repo; read from a decompile kept in the session
  scratchpad only, never in a repo. About 22k lines. Pets are plain MonoBehaviours with their own AI (deck maps, stairs,
  hatches, perches, flight), not ShipItems, and they follow the LOCAL player (`GameState.currentBoat`, `Camera.main`). State
  is in each machine's own save (`modData` keys `FurstMate.dog`, `.spots`, `.homes`). So in co-op each player sees only
  their own save's pets, each running its own AI; the dog's 400 crown price comes off the local wallet (a guest's is a
  mirror); the booby's fish is a raw fish item spawned locally; the cat's slower spoilage runs wherever her cat lives.
  BUILT 2026-11-10, uncommitted, not deployed, never run in game (FurstMate not installed here). `Compat/FurstMateCompat.cs`,
  packet 238 with a sub-kind byte. The host's pets are the crew's: a 1 Hz world message (FurstMate's own save lines, food,
  cat tamed/sunk, booby stage/food/size, home centers) makes guests build the same dog, cat and booby with FurstMate's own
  Spawn / CatKeeper.Spawn / a hand-built BirdFlight; guests skip DogTick, SpawnFromSave, CatKeeper.Tick/FromSave,
  BirdKeeper.Tick, BirdFlight.Tick and DropFish, and their ShepherdDog/ShipCat.Update and BirdKeeper.LateUpdate run a puppet
  driver from a 10 Hz pose (boat-model-local pose, animator State, WalkSpeed, flags; the booby adds mode, body and clip hash).
  Sounds go as events (Say, Bark, howl, pounce, booby calls, splash, fish in the bill); wing beats come from the animator on
  each machine. Guest interactions run locally for their notes and item use, and go to the host: commands, sleep spot,
  petting, feeding (as the food gained), Here, buying the dog (paid on the host's wallet), taming the cat, the booby's
  feeding/taming and begging. On the host, "you" widens to the nearest crewmate aboard when the host is not
  (DeckWalker.PlayerAboard/PlayerFeet, GameBird.PlayerAboard/PlayerFeet), so pets heel, come on deck and land for guests.
  The booby's dropped fish is spawned on the host through item sync. Known limits: names, coat colors, size and volume
  config stay per machine; the booby only comes down for fish the HOST is holding; a pet's procedural pitch follows the
  guest's own config.

Crewmates' hands on tables (`SetSeatTable`, added by Player Model 0.1.6) moves with the table games.

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
- **Economy Overhaul** (DogEggz01/EconomyOverhaul). Not supported, asked 2026-11-14. It replaces the market co-op syncs
  with its own daily simulation and adds visible NPC trader ships, loans, ship renting, a trade office with hired traders,
  cargo water damage and fragile goods, new items (tarps, poles, planks, pallets) and reworked cargo missions. All of it
  would run separately on each machine today. Needs the host to own and stream the whole economy state, the trader ships
  synced like NPC boats, and guest actions (loans, rentals, hires, bulk trades) routed to the host. Larger than FurstMate
  and Postal Expansion together. Read its source before scoping.
- HMS Leopard cannon fire. Cosmetic-only broadcast, same shape as the bell.
- Cold-launch invite auto-join. Wants its own release.
- Player Model on Thunderstore, so r2modman users stop importing by hand.
- Player Model: a config switch to force the shopkeeper as the clone source.
- **Player Model: sitting in hammocks.** Asked about by players, deferred 2026-11-14. Every hammock is a vanilla bed
  (`GPButtonBed` or `ShipItemBed`), so lying in one already works. Sitting with X does not: only the Dhow's hammock is solid
  in the boat's walking collision, the Sanbuq's and Brig's are click triggers only, the Cog's and Kakam's swing on a
  hinge, and Hanging Hammocks (FluffyJesus, not solid unless `SolidWhenHung`) and Shipyard Expansion's are the same. Plan:
  X on a hammock bed seats you across it at the low point of the sag, facing out, knees over the long edge, hips raised a
  little against the rigid cloth, and the seat follows a swinging hammock. Hanging Hammocks found by plugin GUID and read
  by reflection, optional. Check also whether the lying body follows a swinging hammock.
- Shoving crewmates (co-op easter egg). Any player can shove; the host can switch it off for the session in
  settings. T with empty hands shoves the crewmate in front of you, with the same animation as throwing; T while
  holding anything is always the game's own throw and never shoves. Hit hard enough or often enough in a short
  time, they fall over using the Player Model's downed state (Next up, item 5).

## Backburner

- **Table games, parked 2026-11-10** on branch `table-games` (`5c5b80f`, off `ba4e4ae`): the user judged them in need of a lot
  more work and moved them behind the compat release. Packets 234 TableAction and 235 TableState stay reserved. The notes
  as they stood:

Decided 2026-11-04: four games at launch, built in
this order: Liar's Dice, Ship Captain and Crew, Twenty-One, Solitaire. Chips only: the host keeps each player's stack for the
session, nothing is saved, and a free fixed buy-in appears when you are out. Tying the buy-in to per-player wallets waits for
a later Quartermaster update. Tune for crews of 2 to 4. Poker, Dominoes and two-player games are for later.

Liar's Dice, uncommitted. The user confirmed the screen version works, with bots, 2026-11-05. Rules, chips and table layout are
tested outside the game (`tools/GameRulesTests`). Design, agreed 2026-11-05: games are physical kits, played by interacting with
items on the table, with panels removed. Built in three stages, playable at each:

1. Done: the dice cup item (`Games/GameKit.cs`), and props showing the game's state (`Games/TableProps.cs`, `Games/TableMeshes.cs`).
   The user ran it 2026-11-05: cup sides were inside out (fixed), no in-world controls, and pulling the table away left the
   props floating in the air.
2. Done 2026-11-05, deployed, never run: all mouse, shown in the game's own two-line hint with the player's bindings. Right
   click the cup to join, buy in and start; left click your cup to look at your dice, right click it to leave; scroll the count
   marker and face die, left click either to bid; left click the last bidder's cup to call. A sand glass runs by the cup whose turn
   it is. Laid out on what the cup stands on: toward the edges of a table item with a box collider, in a circle sized to the crew
   anywhere else (`Games/TableSurface.cs`). During a game the cup and the table it is on cannot be picked up; if the table goes
   anyway the host puts the game away and returns the antes. The pause screen only shows with `Debug.TableBots` on.
   The user ran it 2026-11-05: unplayable from standing distance. Their Sailwind control hints are off (registry
   `controlsHints` = 0), so the game's own hint never shows; props far too small; pieces crowded the middle; no bodies at the
   table. Decided: a table view.
3. Done 2026-11-05, deployed, never run: the table view (`UI/TableView.cs`), like the chart table. Right click the cup to join and
   the view moves in over the table from your side with the cursor free; left click does what the label by the cursor says,
   right click or Esc steps back. Labels and a status line at the foot are always shown, whatever the hint setting. Names, chips
   and dice over every cup. Your own dice show face up while you look. Every radius has a floor of 0.27 m so nothing crowds.
   The main camera is moved for each frame's render only and put back after it.
   The user ran it 2026-11-06: still unplayable. Their own cup, lifted to show their dice, blocked the view and hid the dice;
   nothing read as clickable; the crosshair's last "dice cup" label stayed on screen; no bodies at the table. They asked to
   play from on-screen UI and watch the game in 3D, with the camera moving from player to player.
4. Done 2026-11-06, deployed, never run: the table view is played from a panel along the bottom (your dice, how many and which
   face, Bid, Call liar, Start, Buy in, Leave, Step back), a player list top left and a call-out for each bid, call and win.
   Nothing on the table is clicked any more; a click on it in first person only opens the view. The camera eases between
   shots in the table's own frame: over your shoulder on your turn, from across the table at whoever is bidding, high over the
   middle while the cups are up. The crosshair dot and look label are hidden while the view is up. Bots get Player Model bodies,
   seated on an invisible chair at the table's edge with hands on the table, or cross-legged round a kit on the floor. The
   table's height comes from the local player's feet, because a deck has no collider a ray can find.
5. Next: whatever the user finds, then Ship Captain and Crew on the same table.

A turn lasts 15 seconds; running out makes the smallest legal bid, or a call if no raise is left, and three misses in a row puts
that player out of the game. `Debug.TableBots` (0 to 3, default 0) adds bots for testing alone. Kits never register to be saved,
because the game indexes its item list with no range check when loading, so a saved kit could break a save once the mod is removed.
They need their own save data once they can be bought. For now `Debug.GameKitOnDeck` (default on) spawns one on the host's deck;
kits will be sold in shops, and that setting must then default to off. A kit is held still with `attached`, not `nailed`: the game
stops looking at nailed items other than crates, bottles and beds.
Crewmates' hands on tables (`SetSeatTable`, added by Player Model 0.1.6) go in that release too.

- Poses for other mods' controls: HMS Leopard gun carriages (push while running out), the Realistic Skies
  observatory telescope, a short reach-and-press for single-click controls (cannons, bell, ladders, the
  game's doors and hatches). Leopard oars wait on sitting.
- Onsen exploit fix on its own. Folds into the quartermaster spend audit.

## Not doing

- Multi-boat. Decided 2026-07-10: the mod is about crewing one ship together. This means separate crews sailing
  apart. Crewmates on several boats near each other works (every crewed boat streams since v0.2.28, every boat's ropes
  since v0.3.1). The host runs every boat's physics but loads islands only within 1.8 km of itself
  (`IslandHorizon.islandLoadDistance`), and lowers them for curvature from 1 km, so a boat near land the host is far from
  loses its docks and moorings on the host.
- Ghost markers and wind streaks in Rig Balance. Built, then removed by decision.
