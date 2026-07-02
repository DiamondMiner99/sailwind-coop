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
