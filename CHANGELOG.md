# Changelog

This is a fork of [**Pillumz**'s `sailwind-coop`](https://github.com/Pillumz/sailwind-coop)
(MIT) - the Steam-P2P co-op networking and state-sync foundation that makes a shared boat work.
This fork rebuilds the co-op UX (menu-driven lobby, humanoid bodies, independent survival, crew
sleep, join-from-title), scales it from 2 players to a full crew, and runs a large
review/hardening pass on top. (Everything in this changelog is this fork's work; the upstream
foundation it builds on lives in Pillumz's repo.)

Format loosely follows [Keep a Changelog](https://keepachangelog.com/).

> ⚠️ **Everything below is alpha.** Back up your saves.
>
> Where a release is marked **"all players must update"**, the network format changed:
> every crew member must install that version (or newer) or sessions will fail/desync.

## v0.3.0 - 2026-08-06

> **All players must update.** The network format changed: three new message types carry your
> character's appearance, who is carrying a mooring rope, and who is adjusting one's length, and
> steering now sends the helmsman's wheel angle rather than a running total of nudges. A crew on
> mixed versions is refused at the handshake as usual.
>
> The version jumps from 0.2.x because this release changes how you get into a session at all: you
> can ask to come aboard rather than waiting to be invited, you can join from the main menu without
> loading a world first, and the join itself is now something you watch rather than something you
> sit through.

### Added

- **A loading screen for joining.** Joining used to mean watching your own world load, then standing
  frozen 50 meters above the sea while the islands around the captain's ship streamed in, then being
  snapped onto their deck. All of that was working correctly and all of it looked like the game had
  hung. There is now a proper loading screen over it, and the bar moves on the join's real progress
  rather than on a timer: it advances when a step genuinely finishes, the label names the step you
  are on, and the islands step reports actual progress because that is the one the game can measure.
  It never claims to be finished early, and it only covers the part of the join where you had no
  control anyway, so it can never black out a screen you could still be walking around behind.
  If a step takes longer than it should, the screen says so and keeps counting, naming what it is
  waiting on. That is deliberate. A loading screen makes a working join feel finished and a broken
  one invisible, so this one is built to tell you which of the two you are looking at, and to tell
  me where it stopped if you send a log.
- **A character screen.** Open the pause menu and pick "Character". You set build,
  head, hair, eyebrows, facial hair, torso, hips, legs and a hat, drawn from the same parts library the game dresses its own shopkeepers from. A live model beside the controls rotates, so you can
  see the back of a hat. Your crew sees your choices, and the look is saved for future sessions.
  There is also a Randomize button.
- **Friends playing now, and asking to come aboard.** Until now a session could only start with an invite from the
  host. If you knew your friend was out sailing there was nothing in the game
  to click, and you had to go ask them somewhere else. The pause menu now opens a list of your Steam
  friends who are in Sailwind right now, with their avatars, who is captaining what, and how full
  each crew is. If they are sailing and have room, you can ask to come aboard; the captain gets a
  prompt and decides. Sessions stay invite-only; a request still needs the captain's approval.
- **Inviting works with the Steam overlay switched off.** The old Invite button opened the Steam
  overlay, which does nothing when the overlay is disabled, and reports no error. Inviting from the
  friends list goes straight through Steam's lobby invite instead, so it works either way.
- **An Accept Invite button.** When someone invites you, the pause menu offers to take it. The
  previous version announced the invite and then left you to find it in Steam's own interface, which
  shows nothing when the sender is not on your friends list or your overlay is off. Players were
  told they had been invited with no way to accept.
- **You can see when a crewmate is carrying a mooring rope.** The rope appears in their hands on
  everyone's screen instead of lying on the dock while they walk away holding nothing.
- **A readable panel for the things that stop a join.** Version and mod mismatches used to scroll
  past in the corner notification ticker, clipped, in the seconds before the game closed. They now
  get a panel that stays up until you close it, with one line per mod and per setting: which mod,
  which option, which side has it switched on, which section of the config file it lives under, and
  whether changing it needs a restart. Mods outside the compatibility check that still differ between
  you are listed as well, so a mismatch you have to go and fix is a list you can work down rather
  than a paragraph to decipher.
- **A Friends button on the title menu.** Co-op used to live only in the in-game pause menu, on the
  reasoning that both players need a loaded world before a join can work. That stopped being true
  when joining from the title screen was added, and what was left was a dead end: an invite arriving
  at the main menu told you to open the menu to join, and there was no menu to open. The title screen
  now carries the same friends list the pause menu does, so you can see who is sailing, ask to come
  aboard, and accept an invite without loading a world first.
- **You can see when a crewmate is adjusting a mooring rope's length.** Pressing R on a moored rope
  hands you a separate rope and coil to haul on, and none of that was shown to anyone else, so the
  rope tightened by itself with nobody visibly doing it. The length was already synced; what was
  missing was the person. Hauling on the rope at the dock cleat and hauling on the one at the ship
  look different from each other, and now they look different to the crew as well.

### Fixed

- **A ship could sit at two different heights for two different people.** A guest never ran the game's
  own hull-physics step, on the reasoning that the captain should own how flooded a boat is. That step
  turns out to be two jobs in one: it tracks the water coming in, and it works out what that water does
  to the hull. Skipping both froze the guest's copy of every boat at whatever buoyancy it happened to
  have, permanently. Take on water in a storm and the boat grew heavy and sluggish for the captain while
  staying light for everyone else, so it floated at one height on one screen and another height on the
  next, and no amount of correction could settle it. The version that survived a restart was worse: if
  your hidden co-op save ever held a sunk boat, that hull loaded with zero buoyancy and nothing would
  ever give it back. A rejoin did not help, and neither did a new session. Deleting the save was the only cure anyone found.
  Crew now work out hull physics from the captain's water level instead of not at all, so a boat that
  loads wrong rights itself on the next frame. This one only runs on a crew member's machine, so it could not be tested before release and you are the
  first people to sail it. If boats start
  behaving worse than they did before, `Coop.GuestHullPhysics` in the config turns it off and restores the old behavior, without waiting for a new build. Please tell me if you need it.
- **Standing on the dock dragged your ship's balance toward the island.** The game adds your weight to
  the boat you are on, at a distance measured from the deck. That distance is only meaningful while you
  are actually aboard, which in the unmodified game is guaranteed. Co-op broke the guarantee: a
  crewmate ashore still counted as aboard, and their "distance from the deck" became the whole way to
  where they were standing. In one recorded session that was 379 meters of leverage, where the game expects
  a couple. The hull rocked and would not settle.
- **A crewmate who fell out of the world had no way back.** The game's own out-of-bounds rescue is
  switched off for crew, and crew are separately not allowed to recover the ship, since that is the
  captain's call. Together those two rules meant that if you ended up under the seabed, the only
  way out was closing the game. Two players hit this on their first evening.
  You are now returned to the ship after a few seconds of being nowhere. Only your position changes; the ship is left alone.
- **Joining could drop you in open sea with the ship nowhere in sight.** If the captain's boat was not
  present in your world, the join said nothing and carried on, placing you against whatever boat your
  own save happened to have left selected, or, failing that, at coordinates that meant nothing. You are
  now told, by name, which ship could not be found.
- **Co-op did not work when installed through a mod manager.** One native Steam file has to sit next to
  the game's executable, and a mod manager package cannot put a file there. Everything else
  loaded fine, so the failure surfaced as "Steam did not start" and the workaround people found was
  launching the game a different way, which loaded a separate copy of the mod instead. The file is
  now loaded from beside the mod itself. A copy next to the game still takes priority, so ordinary
  installs are unchanged.
- **Refusals named settings that do not exist.** The mod-matching check reads each mod's settings by
  their internal names, and those are not what your config file calls them. It would tell you
  `topsailPatch` differed; the line in the file says "Link topmasts". One crew searched their configs
  for a word that was not in them, reasonably concluded whole files were the thing to match, and spent
  an evening copying files at each other. Refusals now name the setting as the file names it, say which
  section it is under, say whether a restart is needed, and keep that separate from "update the mod" and
  from "this mod is broken on one of your machines".
- **Your sailor floated a few inches above the deck.** Two separate causes stacked: the body was
  planted using a hardcoded height instead of the one the mod already measures, and both ends fitted
  the model to the bottom of its render bounds, which hangs below the soles. Feet now sit on the deck and on the ground.
- **Crouching produced a kneel instead of a squat.** The thighs swung to a right angle and the sailor sank into
  a seiza. The hips now travel backward as they drop, by an amount solved at runtime from the actual
  bone lengths of your model, so the pose reads correctly across different builds.
- **Crouching made the whole model flicker.** Introduced while fixing the above: the setback was
  being solved at the live crouch depth, which is discontinuous partway down.
- **Your name tag stayed where your head used to be when you crouched.**
- **The join-refusal panel had never drawn a frame.** Leaving the lobby fires its own handler
  immediately, and that handler hid the panel inside the same call that had just shown it. So a refused player saw no explanation.
- **One unloadable boat could wreck an entire join.** A guest whose Shipyard Expansion install was
  broken would fail partway through rebuilding a boat, after its sails had already been removed,
  leaving a sailless winchless wreck and taking the rest of the join down with it. Boats are applied
  independently now, and a bad one is reported instead of being fatal.
- **A refused join reported success, and could close your game.** Steam's answer to "did we get in"
  was being discarded, so a stale invite, a closed session and a full crew all looked like a
  successful join. The mod then read an empty lobby, concluded your mods differed from the host's,
  and on the title-screen path quit the game. Steam re-sends pending invites on every launch, so stale invites are common. Joins now say what actually happened: the
  session no longer exists, the crew is full, or the invite is no longer valid.
- **The character screen failed on every click,** on a null reference inside the game's own
  part-swapping routine, triggered by two headwear entries that contain no model.
- **Guests no longer get a "Leave Lobby" button.** For a guest it did the same thing as Quit Game,
  without saying so.

#### Found in a two-machine playtest

- **A refused join looked like a broken one.** When a captain's mod set did not match yours, their
  game decided correctly and told you so, and you never heard it. The refusal is sent and the
  connection closed in the same frame, and closing a Steam connection discards whatever is still
  waiting to go out on it, so the message died on the way. What you got instead was forty-five
  seconds of nothing followed by a note suggesting you check whether you were Steam friends, which
  had nothing to do with it. The connection is now held open long enough for the refusal to leave,
  while your packets are already being ignored.
- **Starting a session from the pause menu left the captain's world frozen.** The world is meant to
  keep running in co-op, and the check that decides that ran a moment before the session existed.
  Everything the captain owed the crew, the entire join included, was waiting on a clock that was
  not moving.
- **Crouching on a dock buried you in it.** Crouching in this game moves the camera and nothing
  else, and the position sent to your crew was worked out from the camera, so crouching subtracted
  the crouch a second time and your crewmates watched you sink through the planks. It only happened
  ashore, because the version used aboard a ship was already measured from the body.
- **The green placement outline appeared on everyone's screen.** Aiming a wall-mountable item drew a
  translucent copy of it on every other display, hanging where the item was not. It is an aiming
  guide for whoever is holding the thing, and it stays with them now.
- **Crew weight never reached the captain's ship.** A crewmate walking to the rail heeled the boat on
  their own screen and did nothing on the captain's, which is the one that decides how the ship
  behaves. The check matching a crewmate to a boat compared two different parts of the same ship, so
  it never matched anyone and no crew weight was ever added. That has been true in every released
  version, not only this one. Crew weight also switched off entirely while moored or anchored, to
  stop a docked ship flooding when driven against its own mooring springs. That risk lasts a single
  moment, when the rope is first made fast, so it is handled there instead, and your crew now trim
  the boat whether you are tied up or under way.
- **Steering as a crewmate jumped half a second after you stopped.** The captain's game kept a
  running total of your steering nudges, arrived at separately from your own, over a connection
  allowed to drop messages. The two drifted apart, and when you let go the captain's total was sent
  back as the authoritative one. Everyone now uses the helmsman's actual wheel angle, so there is no
  second opinion to drift from and a dropped message costs one frame instead of staying wrong.
- **Mooring ropes went missing, stuck, or refused to be tied.** Throwing a rope to a cleat made it
  vanish for half a second on every other screen while it flew, because the throw was reported as a
  release and everyone put the rope away. Fixing that introduced the opposite problem: a throw that
  missed was never released at all, so the crew carried it indefinitely, and a machine that believed
  someone else held a rope would not let that rope be moored, which left two players passing an
  untieable rope back and forth. All of it is handled now. The throw itself is shown, a throw that
  misses stows the rope shortly after, and picking a rope up clears any belief that someone else has
  it. A crewmate who quits while carrying one leaves it stowed rather than hanging in the air.
- **A message you had to read closed the game before you could read it.** The panel explaining why a
  session ended waited for a click, and the game quit six seconds later regardless, so a refusal
  listing several mods vanished mid-sentence into the desktop. It waits now. While it is up the mouse
  no longer turns the camera behind it, and it grows to fit its text instead of showing a scrollbar
  for two lines.
- **Accepting an invite from the title screen froze the game on a menu that stopped responding.** The
  accept ran the in-game unpause, which restores the speed stored when you paused. Nothing had been
  stored at the title screen, so the clock was set to zero and the world load then waited on a timer
  that never advanced. The menu stopped taking clicks because the game counts a load as an animation
  in progress. A load that fails now hands the menu back and explains itself.
- **An invite stopped working after the first time you used it.** Steam re-offers a pending invite on
  every launch, and the check that stops it nagging about a dead one was discarding the invite rather
  than staying quiet about it. After joining a captain once, their invites no longer appeared
  anywhere, and accepting through Steam launched the game and then did nothing. The invite is kept
  now, so it waits in the friends list, and an invite to a session someone is demonstrably still
  sailing in is announced again rather than treated as stale.
- **Pause menu and crew list layout.** The button column was anchored at its top while the number of
  buttons varies, so a crewmate's shorter menu sat high on the parchment and a captain's did not. The
  crew list capped at four names and a "+4 more" on a full crew. Both fit themselves to the parchment
  now, and long notifications wrap inside the scroll instead of running off both edges.

#### Found in a code review of this release

- **The friends list on the main menu let clicks through to the menu behind it.** The list is drawn
  over the title parchment rather than being part of it, and drawing something over the parchment
  does not stop the parchment being clicked. The game aims at menu buttons with a ray cast from the
  mouse, and that ray does not care what is painted on top of them. A click meant for a friend's name
  could land on Continue and start loading your solo save behind the list, or open the quit prompt,
  or tear the menu down for a new game. The buttons underneath are switched off while the list is up
  and switched back on when it closes.
- **Throwing a mooring rope let everyone else's game tie it up first.** The game hides a thrown rope
  from cleats for the length of the throw, which is what makes it catch only the one you aimed at.
  The copy of the throw played back on other screens was missing that, so every other machine tied
  the rope to the first cleat the flight passed over and reported it to the crew as though that
  player had tied it. With two of you the result was a dockline slightly too tight or too slack. With
  three or more, each machine reported its own rope length and no two agreed, and a throw that missed
  on your screen could leave one crewmate's game alone believing the ship was tied to a bollard. The
  played-back rope is now kept off cleats for the length of the throw, the same way the game already
  does it for whoever threw it, so only their game decides what it catches.
- **Clicking a wheel without steering it jolted the ship and took the helm from whoever was
  steering.** Letting go of a wheel sends the captain the angle you finished on, which is what keeps
  a lost message from leaving the captain a nudge behind. That was sent for any release at all,
  including a wheel you only clicked. Someone who is not steering has no angle of their own to
  report, only a stale one their game has been drifting since anyone last touched the wheel, so an
  idle click handed the captain that number and put it on the rudder. The same click counted as
  asking for the helm, which the person actually steering then lost for up to half a second. A wheel
  you did not turn reports nothing now.
- **Two crewmates taking the same rope-length adjuster stuck it to one of them.** Nothing stops both
  of you grabbing it, and the second grab did not clear the belief that the first player still had
  it. Your own adjuster was dragged to their hands every frame, and when they let go it put yours
  away while you were still holding it, after which it stopped answering the drop key. Whoever has it
  in their own hands now keeps it, whichever of you reached for it first.
- **A refused join left no record of what did not match.** The panel told you the full list was in
  the log, and it was not. That list was only ever written out for players who were being let in.

## v0.2.38 - 2026-07-28

> Everyone must update (the version handshake refuses mixed crews as usual), but there is no
> network-format change - this is presentation, physics-timing and invite-handling work only.

### Fixed

- **Mission cargo stayed lit up for everyone except the person who picked it up.** The game turns the
  highlight on when cargo is registered to a mission and turns it off in exactly one place: the pickup
  handler, which only ever runs on the machine of whoever grabbed the crate. Every other crewmate kept
  the glow forever, and the host kept it even on crates the host picked up itself. The highlight is
  now cleared on every machine when the crate is picked up, and restored when it is dropped. One case
  is still open: a crewmate who joins mid-voyage sees loose mission crates on the dock outlined until
  someone handles them.
- **Withdrawing cargo from a hold made the crate visibly flash and shove nearby items around, on every
  screen except the taker's.** The game's own withdraw takes the item into your hands *before* it
  leaves the storage slot; the co-op version of that step skipped the hand-off, so for one round trip
  to the host the item existed as a loose, solid, physics-driven object sitting inside whatever else
  was on deck. It is now held still until the pickup lands, with a five second safety release so an
  item can never be left frozen if a message goes missing. The item may still be briefly visible in
  place; it no longer has physics while it is.
- **Every fish you caught wrote an error into the log.** The co-op fishing patch left the game's own
  collect routine holding nothing, and two frames later that routine tripped over it. Harmless in
  play, but it buried the useful lines in `LogOutput.log`. That routine is no longer started in this
  branch, which matches what actually happened once the error fired.

### Added

- **You can now ignore co-op invites from a specific Steam account.** The v0.2.36 de-dupe only
  suppressed repeat invites carrying the same lobby, which does nothing against someone who keeps
  making new ones. Add their 64-bit Steam ID to `Coop.IgnoredInviters` in the config, or to
  `~/.sailwind-coop/ignored-inviters.txt` (one per line, `#` starts a comment, and that file survives
  a config reset), and their invites are dropped silently. A pasted
  `steamcommunity.com/profiles/<id>` URL works too, which matters because a private profile gives you
  nothing else to go on. Any new invite logs its sender's ID, so you can copy it straight out of
  `BepInEx/LogOutput.log`. The list is read on the first invite after launch, so edits apply next
  launch.

## v0.2.37 - 2026-07-23

> Everyone must update (the version handshake refuses mixed crews as usual), but there is no
> network-format change - this is local timing, local audio and send-rate work only.

### Fixed

- **Crewmates barely recovered any sleep from a nap at sea.** Sleeping on dry land worked, sleeping
  underway did not. Two things combined: an at-sea sleep runs the game at 16x, and Unity caps how much
  simulated time one frame may carry (Sailwind's cap is 0.1s), so once your frame rate drops below
  ~10fps the 16x warp silently degrades - at 5fps you are effectively resting at 8x, at 2fps at ~3x.
  Meanwhile the nap ends when the **host's** clock says 4.5 hours have passed, so a crewmate on a
  slower machine got dragged out of bed part-rested. The lost rest is now credited back on the
  crewmate's side, using the game's own formula, so a nap is worth the same whatever your frame rate.
  Nothing changes for anyone already running above ~10fps.
- **Muffled "asleep" audio stuck on after waking at sea.** The game applies the muffled mix when its
  sleep overlay appears and only un-muffles when that same overlay goes away - but the overlay is
  taken down mid-sleep whenever your eyes are not yet fully closed or a menu is open, so a wake landing
  in that window left the muffle on permanently, with no way back except sleeping again and getting
  lucky. The mod now (a) closes the window on crewmates by marking eyes closed as soon as the sleep
  starts instead of ~3 seconds later, and (b) re-asserts the normal mix itself on every wake, abort,
  timeout and disconnect path.
- **Crewmates being yanked out of a sleep at sea by a wave.** In that same ~3 second window the
  guard that stops the game's physics-driven wake-ups (a wave slap or hull bump firing `WakeUp`) was
  inactive on crewmates, and one crewmate waking broadcasts a wake to the whole crew. Now closed.

### Changed

- **Much less work per frame while the crew sleeps.** Cooking state was polled and broadcast on a
  timer that reads *warped* time, so a "twice a second" full-galley snapshot actually went out once per
  host frame during a sleep, and each one made the receiving crewmate run a full-scene search **per
  food item**. The poll is now warp-corrected, and applying a snapshot does one search per item type
  instead of one per item - together roughly two orders of magnitude less work on a sleeping crewmate.
  Player-position and NPC-boat streaming had the same warp-inflated timers and are now corrected too
  (these were the residuals left over from the v0.2.35 pass).
- **New optional setting `Coop.GuestSleepPhysicsRelief` (default off).** The remaining "slideshow"
  while sleeping at sea is the game itself running physics at 72 steps a second instead of 45 for the
  duration of the warp. Turning this on halves that on **your** machine while you sleep as a crewmate.
  It is off by default because the physics step is global - it also coarsens deck cargo, ropes and
  your own body for those ~35 seconds. It does not affect how much rest you get, and it has no effect
  on the host, on solo play, or on anyone else's game.

## v0.2.36 - 2026-07-22

> Everyone must update (the version handshake refuses mixed crews as usual), but there is no
> network-format change - this only touches how received co-op invites are shown.

### Fixed

- **Repeated co-op invite pop-ups from one old/stale invite.** Steam re-delivers a pending co-op
  invite every time you launch the game (it fires the instant you reach the menu, for a lobby that
  may be long dead), so a single old invite nagged you day after day. Invites are now de-duplicated
  by lobby and remembered across launches (`~/.sailwind-coop/seen-invites.txt`), so each invite is
  shown **once, ever** - a stale re-delivery of one you've already seen is suppressed silently, while
  a genuinely new invite still shows.

### Changed

- **Simpler invite toast.** The received-invite pop-up now just reads `"<name> invited you to
  co-op."` instead of the longer note about Steam prompts and friends lists. (The sender's SteamID
  is still written to `LogOutput.log` so an unwanted invite can be traced to an exact account and
  blocked in Steam.)

## v0.2.35 - 2026-07-22

> Everyone must update (the version handshake refuses mixed crews as usual), but there is no
> network-format change - the fix is entirely host-side send timing.

### Fixed

- **Crewmate hard-crashes / freezes when sleeping (had to force-close and rejoin).** v0.2.34 fixed
  the *host* side (the host now wakes correctly and is no longer stuck), but the crewmate still
  froze. Root cause, confirmed from host + client logs: during a sleep the host streams updates on a
  timer measured in *game* time, which runs at 16x while sleeping - so a "10 per second" channel
  actually fired ~80 per second. On an **unmoored** sleep the moving ship pushes the rudder, so the
  wheel drifts every frame and the host sent a steering-wheel update *every* frame, flooding the
  crewmate until their game locked up. The sleep-time send throttle now actually cancels the 16x
  speed-up (so the real rate matches normal play instead of ~8x it), and the steering-wheel stream -
  which is invisible on the sleep screen anyway - is suppressed entirely while asleep and resynced on
  waking. Moored/tavern sleeps were less affected because the ship (and rudder) sit still.

## v0.2.34 - 2026-07-21

> **All players must update.** Built for the Sailwind **0.38.1** hotfix, and two existing
> packets changed meaning (not shape): the steering-wheel lock is now an absolute state instead
> of a "flip it" toggle, and live boat-spawned items (fish cutlets, catches) encode their
> position in the same frame the receiver reads. Mixed versions were already refused by the
> version handshake, so the whole crew is moved to this version together.
>
> Game compatibility: rebuilt against Sailwind 0.38.1. No behavior change was needed for the
> update itself; everything below is fixes for issues players reported on 0.2.32.

### Fixed

- **Anchor rope stretched to the horizon (crewmate), and the boat tearing itself apart at the
  windlass.** A crewmate would see the anchor rope run off endlessly, causing steering drift, and
  raising or lowering the anchor could make the ship clip into itself, shake and sink. The anchor
  is a physics object that lives outside the boat and was never moved when the boat was
  repositioned over the network, and its "dropped" state was even saved into the crewmate's local
  co-op save (which is why restarting never helped). Now the anchor is carried with the boat on
  every reposition, neutralized when the co-op save loads, and the winch can no longer wrench the
  hull even if the anchor is momentarily out of place. The old workaround (delete `coop.save` and
  have the host hold the anchor while joining) is no longer needed.
- **Sleeping a long time froze the crewmate and left the host stuck on the black sleep screen.**
  A long co-op sleep could hard-freeze a crewmate (force-close only), and the host would then be
  stuck on the sleep screen even after the crewmate quit or the lobby closed - and after
  re-inviting, the host stayed permanently "asleep". The wake/teardown path is now self-sufficient
  (it fully restores time, control and the screen even when the game's own wake routine can't run),
  the sleep state is reset at the start of every session (fixing the permanent-sleep-after-reinvite
  case), and a stale/rewound time packet can no longer re-fire a day's worth of world updates
  mid-sleep (which was amplifying the freeze). Sleep diagnostics now also write to the main log, so
  future reports arrive with usable detail even when nothing "errored".
- **Rudder stuck for a crewmate; twisting the wheel did nothing, and re-grabbing snapped it hard
  over.** The steering-wheel lock was synced as a blind toggle, so a single dropped packet could
  permanently flip the lock out of agreement between host and crewmate, leaving the crewmate
  "locked" (with all steering input silently ignored) while the host thought it was unlocked.
  Clicking a locked wheel to unlock it also never told the host. The lock is now an absolute state,
  clicking to unlock is synced, and locking a wheel a crewmate is holding now releases them from it
  (as it does in single-player).
- **Cart (cargo) service was unreliable for a crewmate:** crates could fly off or stay put and be
  non-interactable until a save reload. A crewmate's withdraw is now honored without a second
  round-trip that could be wrongly rejected (which had silently dropped the crate at a stale spot),
  a missed cart update retries instead of diverging until reload, and withdrawing with full hands
  is handled the way the game does.
- **Fish cutlets (and other freshly-made items aboard a ship) were invisible to the crew until the
  host touched one.** Live item spawns aboard a boat were sent in the wrong coordinate frame, so
  they materialized displaced for everyone else. They now appear immediately at the right spot.
- **Crates sometimes showed empty for the host or a crewmate** (usually correcting on the next
  load). A guard meant to suppress redundant crate updates while boats stream in was too broad and
  could eat a real crate change; it now only suppresses the mechanical re-adds it was meant for.
- **Refilling a lantern with a candle didn't carry over.** A crewmate could refill and light a
  lantern locally, but the host's copy stayed dead and needed a second candle. The lantern's fuel
  is now synced with the refill.
- **A returning crewmate's needs (hunger, thirst, sleep) were preserved on join, then snapped back
  to full a few seconds later.** Only a first-ever join to a host now starts from a clean slate; a
  returning crewmate keeps the needs and pocket items their co-op save restored, matching how
  single-player persists them.

## v0.2.33 - 2026-07-21

> Compatible with v0.2.32 - no network format change, so crew do not need to update in
> lockstep. This is a diagnostics and wording fix only.

### Changed
- **Co-op invite toast wording.** The pop-up shown when someone invites you no longer
  promises a Steam invite that may never appear. Steam only surfaces its own invite
  notification for people on your friends list (and only with the Steam overlay enabled),
  but the co-op mod receives the invite callback either way - so the toast now tells you to
  accept in Steam *if* a prompt shows, and notes that no prompt usually means the sender is
  not on your Steam friends list.

### Diagnostics
- The received-invite log line now records the sender's **SteamID** alongside their persona
  name, and mirrors it to the main BepInEx log (`LogOutput.log`), not just the co-op verbose
  log. An unexpected or unwanted invite can now be traced to an exact account and blocked in
  Steam - persona names are user-changeable and can collide, so the name alone could not
  positively identify who sent it.

## v0.2.32 - 2026-07-14

> **All players must update.** The network format changed: mooring (including the join
> snapshot) now carries a reference to what a rope is actually tied to - a dock or a boat's
> own towing cleat - instead of a bare position, because a moving cleat can't be described
> that way. Four packets are new (door/trapdoor state, and three for the Leopard: cutter,
> oars, bell). This release also adds full co-op support for two more community mods -
> winterspices' **HMS Leopard** and nandbrew's **Towable Boats** - and replaces the old single-mod
> (Shipyard Expansion only) join gate with a proper compatibility gate that covers five
> mods at once, tiered by what actually needs to match.
>
> If nobody in your crew runs any of these mods, nothing changes for you beyond updating.

### Added
- **Tiered mod-compatibility gate.** The join-time mod check (added in v0.2.31 for Shipyard
  Expansion alone) is now a registry covering **HMS Leopard**, **Sail Collision Fix**,
  **Deep Ports**, **Towable Boats** and **NAND Tweaks**. Most of these are gated on hard
  parity - install and relevant settings must match exactly, because they change what a
  shipyard edit builds or swap the physics terrain itself (Deep Ports is checked down to a
  hash of its terrain bundle file, so a corrupted or missing bundle is caught too, not just
  a version number). NAND Tweaks is the exception: only its six simulation-affecting options
  (bailing, drunken sleep, wheel centering, the Albacore fishing area, save/load restore,
  door toggling) are gated - its cosmetic options (outlines, camera, UI, decals) stay free
  per player, and a peer without the mod at all is treated the same as one with every sim
  option switched off. Refusal messages name the specific mod (and for NAND Tweaks, the
  specific option) that differs. New config `Coop.AllowModMismatch`, split out of
  `Coop.AllowVersionMismatch`, is the escape hatch for this check specifically.
- **Generic door/hatch/trapdoor sync**, for the first time on every boat, not just modded
  ones. HMS Leopard's gunports are handled as a special case: one click fans out to a whole
  port group, and the flooding masks that come with it are forced to an absolute state on
  receivers (rather than toggled) so repeated open/close spam can never leave a guest's
  flooding inverted from the host's.
- **HMS Leopard co-op support.** The cutter (its rowable tender) can be deployed and
  recovered by host or guest - the host still runs the mod's own gates (speed limit to
  deploy, no recovering with anyone left aboard), a guest only sends the request. Rowing is
  host-authoritative (the host applies the oar force so its own boat-motion frame rate
  matches the mod's own frame-rate-dependent thrust), with everyone else seeing the oars
  animate. The bell and all three gunport groups (lower, upper, quarter) sync to the crew.
- **Towable Boats co-op support.** A tow is synced as a reference to the towing cleat it's
  attached to, not a world position - a bollard on a moving boat has no fixed position to
  send. Tow creation is host-authoritative (a guest dragging a loose rope near a cleat
  cannot start a tow on its own), and a towed hull keeps streaming to the whole crew even
  while nobody is standing on it. Guests now run the vanilla performance-mode decision
  instead of Towable Boats' tow-chain override, so guest-side hull physics no longer fights
  the host's authoritative transform stream with varying strength.

### Fixed
- **Bailing water did nothing for the rest of the crew when a guest had NAND Tweaks
  installed** - a live bug that predates this release. NAND Tweaks replaces the bailing
  routine with a prefix that skips the original method; the mod's own bail-broadcast hook
  lived in that skipped original, so it never ran on a guest's machine. The prefix now
  passes `__runOriginal` through correctly.
- **A boat built or spawned after the session started could be invisible to sync** (the
  Leopard's cutter, a purchased ship, a shipyard build). The boat-name lookup cache is now
  invalidated on lobby create, join and leave instead of being trusted for the rest of the
  session.
- **Boats with no anchor** (the cutter has none) no longer produce anchor-sync errors in the
  log; anchor paths are verified before use instead of assumed.
- **An empty deployed cutter, or a towed boat with nobody aboard, could be silently pruned**
  from the stream list as if it had gone out of relevance. Both now stay on an always-stream
  list so they keep syncing unmanned.

### Notes
- HMS Leopard, Sail Collision Fix, NAND Tweaks, Deep Ports and Towable Boats support is
  entirely optional and implemented as soft dependencies: with none of them installed, none
  of this code runs and behaviour is unchanged.
- Untested in live play, like every release here. If a Leopard or a tow looks wrong on one
  machine, grab `BepInEx/LogOutput.log` from everyone.

## v0.2.31 - 2026-07-13

> Adds compatibility with nandbrew's **Shipyard Expansion**. Custom rigs (extra masts,
> resized / rotated / flipped / retextured sails) now sync to the whole crew, at join and
> live while someone edits at a shipyard. New requirement when anyone uses it: either
> EVERY player installs the same Shipyard Expansion version, or nobody does - a mixed crew
> is refused at join instead of silently desyncing. If nobody in your crew runs Shipyard
> Expansion, nothing changes for you. The network format is additive, but the version
> handshake refuses mixed versions, so everyone updates as usual.

### Added
- **Shipyard Expansion rig sync.** Guests see and can operate the host's custom rigs.
  Structural changes (masts, sails, part options) already travelled with the existing
  shipyard packets; what was missing was Shipyard Expansion's own per-sail extras (scale,
  angle, flip, texture), which it stores separately from the game's boat data. Those now
  sync too, both in the join snapshot and live while a player edits at a shipyard. Edits
  made by a guest are relayed to the rest of the crew by the host, same as everything else.
- **Mod-set check at join.** The lobby and the handshake now carry a Shipyard Expansion
  signature (presence, version, and the Shipyard Expansion settings that change the rig
  itself). A mismatch is refused with a message naming the problem, in either direction -
  a player without the mod cannot even build the other players' rigs, so a half-modded
  crew was never going to work. `Coop.AllowVersionMismatch` bypasses this check too, and
  its description now says so.

### Notes
- Shipyard Expansion support is entirely optional and is implemented as a soft dependency:
  with the mod absent, none of this code runs and behaviour is unchanged.
- Untested in live play, like every release here. If a rig looks wrong on one machine,
  grab `BepInEx/LogOutput.log` from everyone.

## v0.2.30 - 2026-07-12

> Fixes the 2026-07-12 report (thanks Robin!): the host "losing" the mooring ropes when
> loading in with a ship moored - gone from the poles and the on-ship storage - while the
> client still saw the ship moored and it flew back and forth between the two positions
> until the client released the ropes. No network format change, but the version handshake
> refuses mixed versions - everyone updates as usual.

### Fixed
- **Host no longer loses its mooring ropes when the crew loads in moored.** Three-part fix:
  - The game calls "unmoor" speculatively in several places (loading a save unmoors every
    rope of every boat; boat recovery and the shipyard do it too), including on ropes that
    are not moored - where it silently does nothing. The mod broadcast every one of those
    no-ops as a real "crewmate unmoored rope N", which could quietly cut a real mooring on
    every other machine in the crew. Only actual moored-to-unmoored transitions are
    broadcast now.
  - Nothing a joining client's game does to its own placeholder world while it loads and
    syncs is broadcast anymore. Its moor/unmoor events were being sent to the host as if a
    player did them; the host's state is the only authority during a join.
  - When a remote unmoor was applied to a rope that was detached (the just-loaded state),
    the "put the rope back on its hanger" step could teleport the rope to a spot in the
    ocean near the world origin instead - a rope that exists but is nowhere anyone can see
    or reach, which is exactly "gone from both the poles and the storage". The rope is now
    always re-attached to its hanger first. This also explains why the client releasing the
    ropes brought them back: that forced a second, correctly-parented hanger reset.

## v0.2.29 - 2026-07-11

> Fixes both 2026-07-11 reports (thanks Jav1k!): crate contents being permanently destroyed
> for the whole crew, and the cargo transport hire desyncing everything a guest shipped.
> New packet types are additive, but the v0.2.28 version handshake refuses mixed versions
> anyway - everyone updates as usual.

### Added
- **Bed rest.** Lying in a bed while awake (waiting for the crew, or just going AFK) now
  slowly restores sleep up to 60/100 and freezes hunger/thirst/protein/vitamin drain, so
  an AFK host no longer passes out in bed. Real crew sleep is still the only way to rest
  fully. Per-player and local; toggle with `Coop.BedRest` (default on).
- **Cargo transport hire now works for guests.** It was completely unsynced: a guest
  loading cargo onto the cart paid a phantom fee (the shared wallet never saw it), the
  crates stayed in the world for everyone else, withdrawing the guest-only copies made
  them instantly vanish, and a duplicated mission crate could stop being accepted by the
  trader. Carrier transactions are now host-routed like shop trades: the host validates
  and charges the shared wallet, everyone's view of the cart updates, and rejoining
  players get the cart inventory replayed after the join snapshot.

### Fixed
- **Touching the anchor made the ship "go completely wild" (flipping, vibrations, diving)
  on the other player's screen.** The base game's local anchor simulation auto-releases a
  taut anchor and auto-sets a grounded one - and both machines ran it on the same synced
  anchor, each broadcasting its own "correction": the guest's stale anchor geometry kept
  releasing what the host kept re-setting, ~every 3 seconds, flipping the anchor's physics
  state against a taut rope each cycle. The host is now the anchor authority: a guest's
  automatic anchor transitions are blocked (their own hands-on anchor handling still works
  and still syncs), and the v0.2.27 tether relax now parks the anchor with real slack
  instead of near-taut, so the guest's simulation has nothing to fight.
- **Crates went empty / items on a boat were permanently destroyed for everyone, host
  included.** The base game silently despawns every item on a boat when that boat drifts
  past the horizon (it caches them and respawns them when you sail back - a purely local
  level-of-detail trick). The mod was broadcasting each of those despawns as a real
  "item destroyed" event, so the moment ONE player got far enough from a boat, its deck
  items and crate contents were deleted for the whole crew - and then from the host's
  save. Both sides are fixed: the stream-out despawn is no longer broadcast, and the
  stream-in respawn no longer re-broadcasts each crate item as a fresh insert (which
  had been shredding peers' crate inventories into the "looks empty but a slot still
  works" state).

## v0.2.28 - 2026-07-10

> **All players must update.** The network format changed (new shipyard packet + boat sync
> field), and this release adds a version handshake: the host now refuses joins from a
> mismatched mod version with a clear message on both screens, instead of letting the crew
> desync in confusing ways. Also fixes the 2026-07-10 second playtest report batch
> (thanks Jav1k!).

### Added
- **Version handshake at join.** Crews on different mod versions no longer silently
  half-work: the joiner is rejected with a message saying which side needs to update.
- **A second boat now stays in sync.** The host used to stream only the boat it was
  standing on, so after buying a new ship the old one froze (or "rotated in place") for
  everyone not on the host's boat. The host now streams every boat that has a crew member
  aboard, so moving the old ship while the host is on the new one works.

### Fixed
- **Lamps hung on hooks became untouchable for the rest of the crew** until the hanger
  took them down and dropped them on the floor. Hanging an item now restores its
  clickability on every machine, not just the hanger's.
- **Rejoining after a ship purchase violently yanked the new ship back to the shipyard
  dock, ropes attached.** A freshly bought ship silently kept its purchase-dock mooring in
  the join snapshot; the mooring is now measured against where the rope actually attaches
  before the spring is created, and an implausible moor is stowed instead of yanking.
- **Closing the shipyard teleported the ship and chipped its hull.** The shipyard release
  drops the ship back in the water instantly; everyone now snaps cleanly to the released
  position and impact damage is suppressed for a few seconds around the release. (The
  cradle lift itself is still only visible to the player using the shipyard - see
  KNOWN-ISSUES.)
- **A crewmate's sail handling could silently stop reaching the rest of the crew
  mid-session** (nothing moved on other screens until they rejoined). After any sail/
  shipyard change the mod now re-learns the new sail ropes immediately instead of
  watching destroyed ones, without cancelling an in-flight rope adjustment.

## v0.2.27 - 2026-07-10

> **Hotfix.** No network change - fully compatible with v0.2.25/v0.2.26 crews, but each fix
> runs on the machine that sees the bug, so the whole crew should update. Fixes the
> 2026-07-10 playtest report batch (thanks Robin!).

### Fixed
- **Violent lunge when a crewmate winches in an anchor someone else dropped** (e.g. host
  kedging, client hauling in). The anchor's dropped position is never sent over the network,
  so the non-host copy of the anchor freezes at a slightly wrong spot and drifts further off
  as the boat moves; the moment the rope was winched in, the local anchor joint yanked the
  boat toward that stale point. Guests now continuously keep their (non-authoritative) anchor
  tether slack so it can never fight the host's boat position - the boat's real motion always
  comes from the host.
- **Moor rope snapping back and vanishing for one player but not the other.** When a
  crewmate moored and the receiving machine could not match the dock (a transient miss during
  island streaming, or coordinate drift over a multi-hour session), the rope was stowed
  immediately and silently on that machine only. Dock matching now retries for a few seconds
  before giving up, and if the host does give up on a moor it tells the whole crew, so both
  players always agree on whether the rope is attached.
- **Sails looking furled to crewmates after changing sails at the shipyard** while the boat
  visibly sailed on ("phantom sailing"). The machine that changed the sails kept watching the
  OLD (destroyed) sail ropes internally, so unfurling and trimming the new sails was never
  broadcast until a full rejoin. The rope list now refreshes on every shipyard change, and
  the host re-sends all rope positions when leaving the shipyard.

## v0.2.26 - 2026-07-10

> **Hotfix.** No network change - fully compatible with v0.2.25 crews, but the fix runs on
> the machine that sees the bug, so anyone who saw it should update.

### Fixed
- **Anchor rope stretched endlessly toward a distant island for joining players** (the
  island seemed to change every restart or boat recovery), with the ship appearing to
  pivot or drift around the rope. If the host's anchor was down when a guest joined, the
  guest's copy of the anchor was frozen at the spot where the GUEST's own save last was -
  kilometers away - and the anchor joint physically tethered the boat to it. The anchor
  now snaps back to the hull before its state is applied. Thanks DarthDino92 for the
  report and for finding the host-holds-the-anchor workaround that confirmed the cause!

## v0.2.25 - 2026-07-10

> **Major update.** Additive network change: v0.2.24 players can still join, but several
> fixes and all new features only apply when everyone updates. Recommended: the whole crew
> installs v0.2.25. Fixes every open GitHub issue from the public reports plus two fresh
> playtest log batches (thanks Robin, Reto, and Fox!).

### Added
- **Crouching shows on player avatars.** Full squat pose driven by leg IK - hips drop, feet
  stay planted. Crouch depth and pose are tunable in-game via Configuration Manager.
- **Your inventory and vitals persist between sessions with the same host.** Log out, rejoin
  later, keep your stuff. Session state is kept per host, so different hosts' worlds no
  longer bleed into each other. (First join after updating starts fresh one time.)
- **Dropped items and crates float again** (`Coop.RestoreItemBuoyancy`, default on). Items
  floating is real vanilla behavior; the current Sailwind v0.38 build shipped a regression
  that disables every item's water floater each physics frame, so everything sank - even in
  singleplayer. This restores the pre-0.38 floating. Local-only; set false for the raw
  current-build behavior.

### Fixed
- **Joining a friend works reliably again.** Since v0.2.23 the host silently refused every
  guest (the admission list was never populated), leaving joiners half-connected in their own
  world - the cause of "client spawns at their own location", "inventory lost between
  sessions" showing as never-initialized state, and vitals that seemed to drain at different
  rates (they were never synced up at join; actual drain rates are exactly vanilla). Hosts
  now admit their Steam friends; strangers are still refused. If a join does fail, the guest
  now gets a clear warning and returns to their own game after 45s instead of silently
  playing a broken session - and the host ignores traffic from unadmitted senders entirely.
- **Guest freeze/crash during crew sleep.** During 16x sleep a packet backlog caused huge
  boat position snaps; the dunked boat then fired thousands of suppressed wake events per
  second, each written to disk with a synchronous flush - freezing then crashing the guest
  (reported "when we passed into aestrin-coloured waters": the storm there is what dunked
  the snapped boat). The log is now throttled, verbose logging is buffered, and the host
  thins its send rates during the sleep warp so the backlog never builds.
- **Duplicate uninteractable items for guests (map, compass, lantern, mug...).** The game
  lazily spawns each boat's default items from the guest's own local cache on top of the
  host's synced copies. The cache is now flushed at join, and any residual ghost item
  self-cleans after repeated failed grabs (guarded so it can never delete a real item).
- **Boats the host hasn't visited yet no longer appear empty (or ghost-duplicated) to
  guests** - the host now broadcasts items the game spawns lazily when walking up to a boat.
- **Mooring ropes no longer stretch across the ocean for joining guests.** A geometrically
  impossible moor (frame divergence at some docks) is stowed on that client instead.
- **Lantern hooks and hanging lanterns now sync to joining players**, including the light.
  (Hanging a lantern after a guest joined can still have an interaction quirk on the guest's
  side - still investigating, logs welcome on issue #4.)
- **Items picked up on land now sync correctly far from spawn.** Land pickups sent raw
  world coordinates, so the host's position matching could never succeed at distance - a
  long-standing source of "item denied"/desync reports.
- **Guests no longer permanently lose the connection after a brief network hiccup** (the
  host peer was dropped forever on a single failed-session callback).
- Verbose log files are capped and no longer hammer the disk every line.

### Notes for testers
- Robin's "host runs out of sleep twice as fast": measured from the logs, both players
  drained at exactly vanilla rates - the offset came from the broken join above (the
  guest's vitals started wherever their stale session save left them). Also: alcohol
  quadruples sleep drain in vanilla; drinkers tire fast.

## v0.2.24 - 2026-07-04

> Fix batch from the first public-player reports (thanks to both reporters!). No network
> format change: v0.2.22/v0.2.23 peers can still join, but everyone should update - the
> rope and anchor fixes only protect machines that run them.

### Fixed
- **Sails/winches "letting out as if W is held" for the whole crew.** Rope changes are now
  only broadcast when the local player is actually operating that winch (or carrying the
  anchor). Previously ANY local rope movement - including gamepad stick drift feeding a
  grabbed winch, load-time sail defaults, and a join race - was imposed on everyone at
  10Hz (ropes are last-writer-wins). This also stops a rejoining player's stale rig from
  clobbering the host's sail trim.
- **Gamepad stick drift amplified into winch/wheel input (vanilla bug).** The game adds
  controller stick input to the pointer path unscaled and the winch divides by frame time,
  so ~2% idle drift became full let-out. New `ControllerDeadzone` config (default 0.15,
  0 disables) filters sub-deadzone stick input.
- **Steering wheel visually turned while the rudder is straight (guest).** After a guest
  releases the wheel, a reliable authoritative helm state is now always sent (and applied
  even mid-grip when final), so the wheel cannot stay snapped to a stale angle until the
  host touches it.
- **Docking line stretched miles into the ocean / unusable.** Three-part fix: dock moorings
  are matched ignoring the earth-curvature-sunk island height (which made far docks resolve
  10km underwater), a failed dock resolve now safely stows the rope instead of leaving it
  diverged, and corrupted saves that already contain a seabed rope self-heal on load (this
  one is a latent vanilla bug that co-op made routine).
- **Anchor drop/raise never synced.** The anchor state channel had been silently dead in
  every session (the game reparents the anchor out of the boat hierarchy, so every lookup
  missed). Anchor set/release and join-time anchor state now actually replicate.

### Changed
- Verbose logs (F8) now use per-session timestamped filenames (keeping the newest 5;
  sessions can run 10-50MB each) instead of overwriting one file - the old behavior
  destroyed bug evidence twice.

## v0.2.23 - 2026-07-03

> Security fix + the 2026-07-03 playtest fix batch + two new crew features. Joining
> stays compatible with v0.2.22, but the new syncs (nailing, chip log, charting, spending
> feed) only work between updated players - **everyone should update.**

Security:

- **Strangers can no longer walk into your crew.** The lobby was created as Steam
  "friends-only", which lets friends of ANY crew member join directly - a guest's friend
  (a total stranger to the host) boarded a live session that way. The lobby is now
  **private** (invite-only), and by default the host only admits players the **host**
  personally invited; anyone else who slips in via a crew member's Steam-overlay invite is
  turned away with a notice to the host. Hosts who want the crew to bring friends can set
  `AllowCrewInvites = true` in `BepInEx/config/com.sailwindcoop.mod.cfg`.

Fixes:

- **"Not enough money" no longer spawns a giant unusable item.** A rejected stall buy
  re-shelved the item under the shop's (scaled) trigger volume, so it grew ~12x and could
  no longer be picked up or inspected. It now returns to its real shelf spot at normal size.
- **Crew get paid when they sell.** A guest's stall sale was credited to the wrong wallet
  slot on the host, and the wallet sync then erased the money the seller saw locally.
- **Stoves keep all 3 cooking slots.** Every crew member's game was echoing stove
  placements back to the host, and each duplicate permanently ate a cook slot (one kettle
  could consume all three) - it also cooked that food 2-3x too fast. Placements are now
  deduplicated and already-degraded stoves self-repair.
- **Buying an item and immediately carrying it aboard no longer flings the ship.** A newly
  purchased item picked up within the first split second could keep solid physics colliders,
  which then teleported into the hull with the carrier and violently shoved the boat
  (the "ship spaz" / hull damage while sweeping).
- **No more phantom fish.** Other players' games were rolling their own fish bites on your
  rod (each observer saw a different random fish while you saw none) - bites now come only
  from the rod's owner. Casts also no longer render as a line dangling straight down: the
  bobber's real position is now streamed to everyone.
- **Oakum can't be applied from the dock.** (It was silently repairing your last boat from
  shore - vanilla requires being aboard, and now so does the mod.)

New:

- **Nailing syncs.** Hammer + nails now hold items down for the whole crew (and for late
  joiners), and un-nailing syncs too.
- **The chip log syncs.** Everyone sees the chip fly out, the line pay out, and the
  speedometer read - not just the thrower.
- **Charting is visible to the crew.** When someone uses the charting kit, others standing
  at the table now see the kit set up on the map with a moving quill and ruler while they
  draw. (Drawn lines already synced; now the act of drawing does.)
- **Spending feed.** When a crew member buys or sells, everyone gets a quiet coin sound and
  a small corner notice ("Name bought X for Y") that fades after a few seconds - so the
  host can hear the shared wallet draining. Configurable via `SpendingFeed` and
  `SpendingFeedVolume` under `[Coop]` in the config.

## v0.2.22 - 2026-07-02

> **All players must update** (network format changed). The big fix batch from the
> 2026-07-02 evening playtest - thanks to the crew for the detailed reports and logs.

- **A crewmate no longer gets "left behind" by the ship after unmooring.** If the game's
  embark detection wedges (it could after repeated on/off cargo hops while moored), the mod
  now detects a player standing on the crew deck while detached and re-attaches them within
  about a second - no more jumping in the water and climbing the ladder to fix it.
- **Waves no longer make the ship dip underwater / float in the air for crew.** Wave
  timing was being snapped to the host's (stale) clock twice a second, teleporting the
  water surface under the hull; the guest's ocean clock now converges smoothly.
- **Shop purchases land in your hand** (or at your feet), not on the trader - and a cooked
  fish arrives cooked for everyone (item state now travels with the purchase).
- **Crew can buy oakum now.**
- **Inspecting a shop item no longer gives the other player a free copy** - inspection is
  local, exactly like vanilla.
- **Kettle and liquids respect crew actions**: filling the kettle from a bottle works for
  crew (not just the host), and bottle/tea/mug levels stay consistent for everyone.
- **Fishing works for crew**: cast lines appear immediately for others, catches sync, the
  rod stops "ghost bending" after a fish escapes, hooks no longer turn into phantoms after
  attaching one, and a bought rod's line no longer dangles through the earth while stowed.
- **Mooring ropes can't be held and moored at once**: if a crewmate moors while you hold
  the rope, it's released from your hand (with a notice) instead of leaving a phantom rope
  in mid-air.
- **Cargo no longer falls through the dock on the other player's screen** when taken off
  the boat and set down ashore.
- **The host's items in pockets sync properly** - the host's pipe (and similar pocketed
  items) now appear for the crew once taken out.
- **Ship dirt is shared**: a joining crew member now sees the boat's actual grime and can
  genuinely clean it.
- **Sleep while sailing matches vanilla time**: rest and travel distance are consistent
  again (underway naps give vanilla partial rest; moor or use a tavern for a full
  fast-forward sleep).
- New: **ping display** in the Shift+F8 debug overlay.

## v0.2.21 - 2026-07-02

> Network-compatible with v0.2.20 (no wire change), but as always the whole crew should
> update together.

Fixes from a live 2-player session earlier today (thanks for the logs!):

- **Fixed a crate set down on deck being able to drag a moored boat underwater.** An item
  that went through a crewmate's hands could get stuck "un-latched" from the boat (a stale
  internal boat reference blocked it from ever re-attaching to that boat), leaving it a
  world-anchored physics body pressing on the hull - enough to shove the deck under and
  flood a moored brig in seconds. Items now detach the way the vanilla game does, and the
  stale reference is cleared.
- **Fixed a phantom copy of a carried item appearing on the ground for a joining player.**
  The join snapshot (and the v0.2.20 mission-cargo resync) serialized an item the host was
  holding as a loose world item at the holder's hand position, under the real item's id -
  and picking the phantom up hijacked the real item (it could be silently yanked off the
  boat). In-hand items are no longer sent to joiners; they now sync automatically the
  moment the carrier sets them down (including when a carrier disconnects).
- **Dropped-item resting positions now stay in the right reference frame.** The v0.2.20
  settle broadcast re-derived "on a boat vs. on land" from where the *player* was standing
  seconds after the drop, so a deck drop could be re-sent as a land drop and pin the item
  to the world on other machines. The frame now comes from the item's own position.
- The host now denies a pickup that matches the "stale ground copy" signature (requester
  says the item is on land while it is actually latched to a boat) and resyncs the
  requester instead of letting the real item be grabbed through the stale copy.

## v0.2.20 - 2026-07-02

> **All players must update** (network format changed).

- **Fixed mission crates being invisible to a player who joined mid-session.** After a
  guest finishes joining, the host now re-sends every mission crate directly to them;
  crates that already arrived are simply skipped. A corrupted item in the join snapshot
  also no longer aborts the rest of the join.
- **Dropped items now come to rest in the same spot for everyone.** Previously each
  machine simulated the fall on its own, so an item could settle in a different position
  per player until the next pickup. The dropper now broadcasts the final resting
  position once the item settles, and everyone else snaps to it.
- Fixed a contested item grab being able to leave the losing player permanently out of
  sync about where the item ended up.

## v0.2.19 - 2026-07-01

> **All players must update** (network format changed). Thanks to our playtesters for
> the session logs behind this batch.

- **Fixed a hard freeze on guests after sleeping many in-game nights.** The boat position
  correction could run away at high time-warp on low framerates; it is now clamped and
  suspended during crew sleep.
- **Wave/ocean state now really syncs.** The previous ocean sync targeted a system the
  shipped game never uses; the live wave system's state is now sent, so swell height and
  direction match across the crew.
- **Fixed guests keeping their solo-save money after joining.** Join state is now applied
  step-by-step (one bad step no longer silently drops the rest), and the guest re-requests
  the economy sync until it actually lands.
- **Dock stall/shop purchases are now properly rejected** with a "Not enough money."
  notification when the crew wallet can't cover them; the item is restored and the buyer's
  wallet resynced. Success is only confirmed once the host really spawned the item.
- **The co-op session save is now reliably written when quitting to menu**, and a corrupted
  co-op save (e.g. leftovers from an uninstalled mod) now self-heals shortly after joining
  instead of erroring on every join.
- Fixed sail/rope trim being lost (or overwriting the host's) when joining while the boat
  was still loading.
- Reduced rubber-banding for players on slower PCs (stale-data snap after gaps + smarter
  packet coalescing).
- Boat recovery no longer drags the boat several meters when a guest is ashore.
- Fixed held items and remote players occasionally resolving to the wrong boat.
- Performance: fewer native Steam calls per frame in hot paths.

## v0.2.18 - 2026-06-30

- **Fixed guests being unable to buy from dock stalls**: the game was checking the guest's
  local (empty) wallet before the purchase ever reached the host; guest stall purchases are
  now routed to the host like market trades.
- **Guest wallets now reliably match the host's after joining** (removed a v0.2.17 change
  that zeroed the guest wallet and broke stall buying).
- Fixed a case where a guest already committed to sleep could get stuck waiting for the crew.

## v0.2.17 - 2026-06-30

- Hotfix: **remote players appearing off the boat / in the water** (regression introduced
  in v0.2.16).
- A guest stuck in the sleep handshake now times out on its own instead of hanging.

## v0.2.16 - 2026-06-30

> **All players must update** (network format changed).

- **Guests can push the boat off docks** (third push type, alongside hull and sails).
- **Guest steering now actually turns the boat** (the rudder is driven directly).
- Fixed sail trim getting swapped between masts (main vs. mizzen), and stale trim for a
  mid-voyage joiner.
- Anchor state (length + dropped/raised) now arrives correctly for late joiners; boats
  bought at the shipyard start with the anchor up.
- Remote players now attach to the boat named in their updates instead of snapping to the
  nearest boat.
- Third and fourth joiners now receive all world items (item sync is tracked per player).
- A guest who joins while ashore can now control the shared boat.
- **Boat purchases and shipyard orders sync**: both are routed through the host and the
  shared wallet, and the rest of the crew sees them live.
- NPC boats stay simulated when near *any* crew member (not just the host), and NPC boat
  damage/sinking now syncs, including for late joiners.

## v0.2.15 - 2026-06-30

> **All players must update** (network format changed).

- **Fixed picking up a shop item spawning the wrong item for other players** (a map could
  appear as a bookshelf, a barrel as a fish).
- **Fixed the crew sleep deadlock** and sleep bars filling almost imperceptibly while
  sailing; passing out can no longer interrupt crew sleep and trigger a boat recovery.
- Tavern sleep is no longer cancelled by stray keypresses and now actually rests you.
- Fresh joiners get a clean slate (full needs bars, empty pockets) instead of carrying
  items over from their solo save.
- Guest purchases at dock stalls are now spawned and validated by the host.
- Fixed the boat sinking at its mooring after a join or recovery (mooring spring settings
  are restored from the host).

## v0.2.14 - 2026-06-30

- A guest passing out while under way no longer wakes up dumped in the ocean.

## v0.2.13 - 2026-06-29

> **All players must update** (network format changed). Thanks to our playtesters.

- Fixed floating/teleporting crates and the ship "jumping around" on a crewmate's screen
  after a boat recovery.
- Fixed furniture vanishing for guests after a recovery.
- Fixed a moored ship being pulled under (and flooding) when crew stood aboard.
- Fixed a duplicate crate left behind on guests when the host delivered mission cargo on foot.
- Fixed dock-shop purchases charging the wrong currency (which rejected the trade and handed
  out a free item).
- Guests can no longer accidentally board passing NPC ships.
- Mission notifications on guests now match the host's ("Delivered", "Mission complete").
- Fixed guests spawning under the dock when joining; they now land on the deck.
- Joining now survives a corrupted save entry (e.g. leftovers from an uninstalled mod)
  instead of timing out.

## v0.2.12 - 2026-06-29

Item/crate sync hardening:

- Fixed a crate held by the **host** being stealable out of their hands by another player.
- Fixed items set down on a dock (while standing aboard a moored boat) riding the deck when
  the boat sailed away (regression from v0.2.10).
- A disconnecting player's items stowed in inventory are now recovered and dropped instead
  of vanishing.
- Fixed the crate-unsealing inventory UI getting lost at 3+ players, and unsealed crate
  contents being mis-registered against an active mission.

## v0.2.11 - 2026-06-29

- Fixed two crate desyncs: a contested grab could duplicate a crate, and a shared crate was
  deleted for everyone when one player walked out of range.
- An open captain's log now refreshes live when a mission is abandoned (both directions).

## v0.2.10 - 2026-06-29

- Fixed crates dropped on a moving boat being invisible to other players.

## v0.2.1 - v0.2.9 - 2026-06-26..28

Rapid-fire fixes from the first live sessions:

- **v0.2.9:** fixed guests floating in mid-air when joining a host whose time was paused.
- **v0.2.8:** the co-op load now survives a corrupt save entry instead of failing the join.
- **v0.2.7:** title-screen auto-join now always reaches the lobby.
- **v0.2.5/v0.2.6:** fixed the pause/settings/quit menus bobbing and drifting away from the
  camera on a moving boat.
- **v0.2.4:** fixed guests falling through the deck when the host was aboard.
- **v0.2.3:** fixed the at-sea join spawn position, an orbit-view avatar spin, and error
  spam when Steam wasn't running.
- **v0.2.2:** the release bundle now ships `steam_api64.dll`; hardened Steam init to surface
  failures in-game and retry.
- **v0.2.1:** the mod version now shows on the F8 debug overlay.

## v0.2.0-alpha - 2026-06-25 - crew (N-player) + stability hardening

> First release of the crew/N-player fork: scaled to a full crew on top of the 2-player
> foundation below, then ran a large code-review/hardening campaign.

### Crew - a full crew, not just two (N-player)
- **Scaled from 2 players to a full crew** (host plus guests), exposed as the configurable
  `MaxPlayers` value (default 8, the recommended crew size). Star topology: guests connect only
  to the host, who validates and relays guest→guest actions, so everyone sees everyone.
- **Per-crewmate state:** the single-guest avatar / sleep / push / held-item / helm
  model was refactored to per-player collections, so concurrent crew don't collide:
  two people pulling different ropes, multiple bodies, per-carrier held items, and
  one-controller helm arbitration.
- **Per-peer join/leave:** each guest gets a targeted state resync on join and clean
  teardown on leave, without disturbing the rest of the crew.
- **Crew-quorum sleep:** the whole crew sleeps/wakes together (all-in-bed to start,
  all-rested to wake); a still-loading joiner is correctly excluded from the quorum.
- **Crew roster** shows the whole crew on its own parchment scroll with a "+K more" overflow;
  the host can keep inviting until the crew is full.

### Networking resilience & sync hardening
- **Mooring-rope and sail-rope lengths** now send a reliable terminal value, so a
  dropped final adjustment no longer strands the rope at the wrong length on the other
  client.
- **Shipyard customization** (masts / sails / hull parts) a guest changes is now relayed
  to the *other* guests at 3+ players.
- Hardened the join snapshot against packets arriving mid-join, added an oversize-join
  diagnostic for very item-heavy worlds, and fixed rejoin-within-the-same-session cleanup.

### Economy & missions - live menus
- **Open trade & mission menus refresh live** when *any* crew member trades or takes a
  mission - in every direction - instead of going stale until close+reopen.
- Earlier economy fixes: per-currency wallets, no double-charge / double-log on trades,
  market results targeted to the buyer, free-tavern-sleep closed.

### UI polish
- Pause buttons fit the scroll; the crew roster has its own matching scroll; long
  notifications wrap; **Esc closes the mission menu instantly**; a lobby shows
  **"(online)"** instead of the misleading "(paused)".
- The co-op pause menu deliberately **does not freeze your movement**: the world can't
  pause in co-op, and disabling the character controller could strand you on a moving
  boat (an experiment that was tried and reverted).

### Review & testing
- Ran a sustained adversarial review campaign across the whole mod: wire/serialization,
  floating-origin, lifecycle, save, economy, damage/water, navigation, fishing/cooking,
  transport, embark, and the N-player paths; confirmed findings were fixed and re-reviewed.
- Verified compatible with **Sailwind v0.38**.

---

*Also part of v0.2.0-alpha: the earlier (pre-crew, 2-player-era) fork work that rebuilt the
co-op UX on top of Pillumz's networking foundation.*

## v0.2.0-alpha - earlier fork work (2-player era)

### Menus & UI
- **Removed the old lobby hotkey.** Co-op is now driven from the in-game menus:
  Host / Invite or Join Friend / Close Lobby, with a live crew list.
- **Custom in-game pause menu** styled like the title scroll
  (Resume / Host Co-op / Invite-or-Join / Settings / Recover Boat / Quit Game),
  with Settings opening as a nested sub-page that returns here on Back.
- The title menu is left **pure vanilla** (the Host button lives in the pause menu only).
- Hardened the pause/lobby lifecycle: never let a half-built panel go live, reworded and
  auto-cleared the recovery toast, top-anchored the button column so a hidden button closes
  the gap.
- The pause menu **follows the camera** and no longer freezes the world for the
  other player.

### Player avatars & third person
- Remote player is a **cloned in-game humanoid** instead of a capsule, with procedural
  idle/walk animation and a name tag.
- Fixed avatar position sourcing (body, not camera), giant-scale avatars, the
  walk-animation-while-sailing artifact, and auto-fit feet + nameplate height.
- **Your own body + name now show in third person** and hide in first person.

### Sleep
- Fixed the **co-op sleep deadlock** (solo-host gate, partner-gone timeouts, abort routes
  through wake-up).
- **Both-rested sleep:** each player's sleep bar fills independently; the crew
  auto-wakes once *everyone* is fully rested (or either player clicks to
  interrupt), with an on-screen note and a real-time backstop against wedges.

### Survival needs
- **Food / water / sleep are now independent per player** (previously pooled);
  money stays shared. Eating/drinking only feeds the eater.
- A guest passing out is a *local* blackout - the shared boat does **not** move; only a
  host faint runs the normal boat recovery.

### Tavern
- **One crew room:** co-op tavern sleep charges the shared wallet **once, at
  sleep-start** (not per click), aborts cleanly with a notice if the crew can't
  afford it, and never charges if the handshake is cancelled.

### Economy & items
- Sync **water-barrel + bottle liquid levels** across drink / empty / refill.
- Block the **merchant/trade screen** from popping when you walk near a remote
  player's avatar.

### Joining & session lifecycle
- **Join co-op from the title screen:** accepting an invite auto-loads a save and
  teleports the guest onto the host's boat.
- **Consent-based join** (no force-yank out of the guest's own game), guest
  **save-safety** (the guest never writes the host world to their own slot),
  on-deck spawn placement, and a parchment-chip crew list.
- **Guest warn-and-quit** when co-op ends (host left / lobby closed / connection drop /
  guest left) so the guest is never stranded on the host's ship.
- Throttled + named the co-op invite toast (no more "[unknown]").

### Controls
- **Guests can push the boat and sails**; the push is forwarded to the host with
  floating-origin correction.

### Recovery
- Host "Recover Boat" **keeps the guest connected** and re-syncs them onto the
  recovered boat; the guest's "Recover Boat" button is hidden/blocked.

### Stability / review passes
- **14 bugs** fixed from a per-subsystem adversarial review - including a critical
  guest-quit save-corruption, tavern charge/refund issues, and guest-disconnect-mid-push
  drift.
- **7 cross-system interaction bugs** fixed (faint × sleep, recovery × sleep,
  push × faint, tavern context-mismatch, orphaned fall-asleep time-warp).

## v0.1.x - initial fork work

- Early 2-player-era releases on top of the upstream foundation: install documentation and
  the first round of live-session bug fixes.
