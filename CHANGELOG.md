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
