# Sailwind Co-op - Playtest Guide

A hands-on checklist for validating the mod by **actually playing it**, in four escalating tiers:

1. **Solo (offline)**: mod loaded, *not* in a lobby. Proves the mod cleanly no-ops in single-player.
2. **Solo-online**: host alone with a lobby open, no guests yet. Proves the lobby/menu lifecycle.
3. **2-player**: the core sync matrix (host + one guest).
4. **3+ player**: the star-relay matrix and per-peer aggregation that **only** appears with a third party.

Each tier builds on the one before it. Don't move up until the tier below is green. Every box is
`do X → expect Y`; tick it, and when something misbehaves, capture it (see *Recording results* below) and
file it as a GitHub issue.

> ⚠️ **This is alpha. Back up every save slot before you start.** Steam Cloud counts - copy the save
> folder somewhere safe. A guest never writes the host's world to their own slot (a phantom slot 99 is
> used), but the host *is* playing on a real save.

---

## 0. Pre-flight (do this every session, on every machine)

- [ ] **Same mod build everywhere.** Re-extract the bundle, overwriting old DLLs. A stale DLL is the #1
      cause of fake "desyncs." The network format changes between releases and a mismatch can
      desync silently or break the session outright.
- [ ] **Same Sailwind version everywhere** (built/verified against **v0.38**).
- [ ] **BepInEx present**: `winhttp.dll` sits next to `Sailwind.exe`.
- [ ] **Turn on verbose logging: press `F8` after you're in-world, on BOTH machines.** A small corner
      indicator reads `● co-op debug logging ON`. It starts **disabled every launch**: if you forget,
      there's no verbose log to send. (`Shift+F8` = full on-screen overlay; `F7` = perf snapshot. Both
      require F8-logging to be on first.)
- [ ] Know where the logs are (collect *both* files from *every* machine after a session):
  - `BepInEx/LogOutput.log` - **always** written; the place errors land even without F8.
  - `BepInEx/SailwindCoop-verbose-<timestamp>.log` - **only while F8-logging is on**. One timestamped
    file per session (the newest file is the latest session); only the most recent ~10 are kept.

### Recording results

For anything that misbehaves, capture:
- **Tier / player count / who** (host or which guest) did what.
- **Observed vs. expected.**
- **Both log files from both machines** (grep them for the lines in the [Appendix](#appendix-b--log-lines-worth-grepping)).
- Whether it's **reproducible**.

Then file a GitHub issue with the log files attached (at minimum `BepInEx/LogOutput.log` from
both machines; the session's `SailwindCoop-verbose-<timestamp>.log` too if F8 logging was on).

---

## Tier 1 - Solo, offline (mod loaded, no lobby)

**Goal:** prove the mod is invisible in single-player. Every patch is supposed to no-op when you're not
in a lobby (`!Plugin.IsMultiplayer`). This tier is a **regression check on vanilla behavior**: if any of
these changed, a patch is leaking into solo play.

Load a normal save, **do not** open a lobby.

- [ ] **The game plays normally.** Sail, dock, walk, no new errors spamming `LogOutput.log`.
- [ ] **Survival needs work normally.** Eat food / drink water → *your* food/water/sleep bars move as in
      vanilla. (Needs sync is disabled by design; the solo path must be untouched.)
- [ ] **Fainting is vanilla.** Let a need bottom out → you pass out and the boat does the normal vanilla
      recovery. (The "guest faints locally, boat doesn't move" policy must NOT apply in solo.)
- [ ] **Sleep is vanilla.** Sleep in a bunk → normal time-skip, no "waiting for crew" prompt.
- [ ] **Trade / missions are vanilla.** Buy/sell, take and deliver a mission, no co-op round-trips.
- [ ] **Recover Boat works** from `Esc → Recover Boat` (solo shows the button).
- [ ] **The pause tag reads `(paused)`** at the bottom when a menu pauses the world - *not* `(online)`.
- [ ] **Saving writes your real slot** normally on quit.

**QoL changes that DO apply in solo (intentional, verify they're harmless):**
- [ ] `Esc` **closes the port mission board immediately** (vanilla makes you wait ~1s / use Back). This
      is a deliberate QoL fix and is on in solo too.
- [ ] Long co-op notifications wrap (you won't see many solo, but the wrap logic is always on).

> If anything here differs from a clean, mod-free game, that's a **solo-safety regression**: log it and
> stop before testing online.

---

## Tier 2 - Solo-online (host alone, lobby open, no guest)

**Goal:** the lobby/menu lifecycle, before anyone joins. Load a save, board your boat, then:

### Lobby lifecycle & menu labels
- [ ] `Esc` opens the **custom parchment pause menu** (not the vanilla settings panel). Column, top→bottom:
      **Resume / Host Co-op / Join Friend / Settings / Recover Boat / Quit Game**.
- [ ] All six buttons fit the scroll - **Resume isn't clipped by the top roll, Quit isn't cut off the bottom**.
- [ ] Click **Host Co-op** → the button relabels to **Close Lobby**, and the second button becomes
      **Invite Friend**.
- [ ] Click **Invite Friend** → the **Steam game-invite overlay** opens. (Don't invite yet, or do - your
      call. With no one joined this just confirms the overlay opens.)
- [ ] A **"Crew:" roster scroll** appears next to the pause menu (its own parchment), empty/just you for now.
- [ ] The bottom tag reads **`(online)`**, not `(paused)`, while the lobby is open.

### "World doesn't pause" behaviors (co-op can't freeze a shared world)
- [ ] With the **pause menu open**, you can still **walk with WASD**. The world clock keeps running.
- [ ] Same for the **mission board** and the **log** menu - movement stays enabled.
- [ ] The **trade/market screen still freezes you** and reads `(paused)` - this is vanilla and expected
      (it only opens at a stationary port).
- [ ] **Jump, then open the pause menu mid-air → you keep falling / can move.** You must NOT freeze in
      mid-air. (This is the reverted charController-freeze hazard; a frozen jump = regression.)

### Notifications / UI
- [ ] Trigger a long co-op notification (e.g. open the lobby) → the banner text **wraps onto multiple
      lines** instead of running off both edges of the scroll.

### Close-down & save safety
- [ ] Click **Close Lobby** → the menu **does not "fly away"** (it unpauses first, then leaves the lobby),
      the world keeps running, and the tag goes back to `(paused)`.
- [ ] Quit and reload → **your save is intact** (host plays on its real slot; closing a lobby doesn't
      corrupt anything).

### Logs (sanity)
- [ ] `LogOutput.log` shows a clean boot: `Sailwind Coop vX loading...`, `Crew cap (MaxPlayers): N`,
      `Harmony patches applied successfully`, `P2PNetworkManager initialized`. **No exception spam.**

---

## Tier 3 - 2 players (host + one guest)

**Goal:** the core sync surface. Everything here is exercisable with exactly two people. This is the big
tier; pace it. Both players: **F8 on**, same build, host loads the world and boards the boat first.

### A. Joining
- [ ] **Join from in-game:** guest is already in their own world → host invites → guest accepts the Steam
      invite → guest is teleported onto the host's boat (no manual button).
- [ ] **Join from the title screen:** guest sits at the main menu → accepts the invite → sees
      `Loading your world...`, auto-loads, the "press F to continue" disclaimer auto-clears, and the guest
      lands on the host's boat.
- [ ] **Join via Join Friend:** guest (solo, in-game) clicks `Esc → Join Friend` → Steam friends overlay →
      picks the host.
- [ ] Guest **spawns on the deck**, not in the water or at the dock.
- [ ] Guest's join is clean: the boat doesn't physics-explode, no endless teleport loop. (Boat sync is
      suppressed during join on purpose.)

### B. Bodies & movement
- [ ] Each player sees the other's **avatar** move on deck and on land, **glued to the deck** as the boat
      sails (not sliding off, not lagging meters behind).
- [ ] **Standing still on a sailing boat does NOT animate as walking** (the phantom-walk-while-sailing bug).
- [ ] **Name tags** show the correct Steam names above each avatar.
- [ ] At sea the remote avatar may be a **blue capsule**; arriving at a port **upgrades it to a humanoid body**.
- [ ] Remote body **rotates to look direction** (yaw), stays upright.

### C. Steering, sails, ropes, anchor, mooring
- [ ] **Host steers** → guest sees the wheel turn and the boat respond.
- [ ] **Guest steers** → host (and the boat) follow the guest's input.
- [ ] **Sails:** either player raises/lowers/trims a sail → the other sees that sail match.
- [ ] **Rope terminal value:** pull a sail rope and **let go** → the rope settles to the **same final
      length** on both clients (no "stuck slightly open" after the last adjustment). Repeat a few times;
      this exercises the dropped-final-packet self-heal.
- [ ] **Anchor:** drop/raise → both see it set/release with the right sound; scroll anchor rope length →
      both match.
- [ ] **Mooring attach/detach:** moor to a dock cleat → both see it attach to the **same** dock; unmoor →
      returns to the hanger.
- [ ] **Mooring rope length terminal:** scroll a moored rope's slack and **let go** → both clients show the
      **same** final slack/tension (the boat doesn't sit tight on one screen and slack on the other).

### D. Pushing
- [ ] **Guest pushes the hull** off a dock → host applies the force, the boat moves, and the motion syncs
      back to the guest.
- [ ] **Guest pushes a sail** (sail pusher) → the **correct sail's boom** swings, not the whole hull.

### E. Economy - trades & missions (live refresh)
- [ ] **Trade live-refresh, host→guest:** guest has a market screen **open**; host buys/sells at the same
      port → **the guest's open screen updates** (prices/stock) without close+reopen.
- [ ] **Trade live-refresh, guest→host:** reverse it - host's open screen updates when the **guest** trades.
- [ ] **Currency:** buy with a non-local currency where allowed → correct wallet is charged; both wallets
      match (the wallet is **shared**).
- [ ] **Shopkeeper stall:** guest buys an item → it's **removed from the stall for both**, currency charged
      once (no double-count), day-log entry appears for both.
- [ ] **Mission board live-refresh:** with a board open, the other player accepts a mission → the consumed
      listing **drops live** from the open board.
- [ ] **Mission accept/deliver/abandon:** accepted missions appear in **both** logs (shared); deliver cargo
      at the destination → both see the count tick and the reward credited **once**; abandon removes it for both.

### F. Fishing & cooking
- [ ] **Fishing:** owner casts, reels, hooks a fish → non-owner sees the cast, the line length, the fish on
      the line, and the catch. Pick up someone else's rod → ownership transfers (their hooked fish escapes).
      Landed fish **item appears for both** (host spawns it).
- [ ] **Cooking - stove fire:** insert fuel + light → both see it lit and heat accrue.
- [ ] **Cooking - food/soup/kettle:** place food on a stove / make soup / brew a kettle → the other sees
      the slot occupied and the heat/preservation/soup/tea state match.
- [ ] **Cut / salt:** guest cuts food with a knife → slices appear for both (host spawns them); salt → both
      see the salted state.

### G. Survival & containers (per-player vs. shared)
- [ ] **Needs are independent:** one player eats/drinks → **only that player's** bars move.
- [ ] **Containers are shared:** refill a bottle from a barrel / pour between containers → both see the
      **liquid level and type** match in both the clicked and held container.

### H. Sleep & tavern
- [ ] **Crew sleep quorum:** one gets in a bunk → a **"Waiting for crew (n/total)"** notice shows; once
      **both** are in bed the crew fades out and time-warps **together**.
- [ ] **All-rested wake:** the crew auto-wakes once **both** are fully rested; either player can click to
      wake early; the other wakes too (no one left warped at 16×).
- [ ] **Tavern shared room:** rent a room, both sleep in tavern beds → the **shared wallet is charged once**
      at sleep-start (not per click); can't afford → handshake cancels with a notice; sleep runs to the
      morning gate.

### I. Damage & repair
- [ ] **Bilge pump:** guest pumps → the shared boat's water level drops for both.
- [ ] **Bail** with a bucket/bottle → water level drops for both.
- [ ] **Oakum** a hull leak → repair applies, oakum amount + hull damage sync back.
- [ ] **Damage state:** take hull damage / water → water level, hull damage, oakum, and the **sunk flag**
      match (and *un-sunk* after a recovery propagates too).

### J. Items
- [ ] **Carry:** pick up an item → it follows you on the other screen; **drop** → lands at the synced spot.
- [ ] **Spawn/destroy:** buy/spawn → appears for both; consume/destroy → removed for both.
- [ ] **Hang on hook / crate insert-remove / crate unseal / lantern on-off / pipe fill** → each mirrors to
      the other player.

### K. Customization, cleaning, navigation, world
- [ ] **Shipyard:** add/remove masts, swap/recolor/resize sails, toggle parts → the other sees the rig
      change (while at the shipyard).
- [ ] **Cleaning:** scrub the hull with a broom → the dirt clears at matching spots; shipyard full-clean
      mirrors.
- [ ] **Navigation:** open a watch lid / quadrant / spyglass zoom / compass dial / fold a map → state
      mirrors. **Map drawing:** one draws a line → the other sees it (temp line + committed line). Two try
      to draw the same map → the second is told **"Map is being used by another player."**
- [ ] **Time & weather:** day/clock advances together; sail into a storm/wind shift → the guest's
      wind/storm/waves match (buoyancy agrees, no one in calm while the other's in a gale).
- [ ] **NPC boats:** sail near AI vessels → guest sees them at matching positions with matching sails.

### L. Recovery, faint, disconnect
- [ ] **Host Recover Boat:** host presses Recover → guest is re-synced onto the recovered boat (not left at
      sea); any co-op sleep force-wakes first. The **guest has no Recover button** in a lobby.
- [ ] **Guest faint:** guest's needs bottom out → **local blackout only**, the shared boat does **not**
      move. (A host faint *does* run recovery.)
- [ ] **Guest leave / quit:** guest uses `Esc → Leave Lobby` (or Quit) → returned with a warning and the
      game closes; **guest's own save is untouched** (phantom slot 99).
- [ ] **Host close:** host `Esc → Close Lobby` → guest gets *"The host closed the co-op server"* and is
      returned to safety; host keeps playing.
- [ ] **Rejoin in the same session:** after a leave, the guest can **rejoin** the still-open lobby cleanly
      (session-2 lifecycle).

### M. Watch the logs (2-player)
- [ ] **No `OVERSIZE BoatWorldState`** on join (a real, item-heavy save is the test). If you see it, the
      join snapshot blew the ~1 MB reliable limit - log it; that's the trigger for chunking (not yet built).
- [ ] No sustained `POSITION_ERROR` / `TELEPORT` / `TARGET_JUMP` spam while sailing normally (occasional on
      join/recovery is fine).
- [ ] No `DUPLICATE item` / `[ITEM:REGISTRY] Mismatch` / `[ITEM:RESYNC]` errors.

---

## Tier 4 - 3+ players (host + two or more guests)

**Goal:** the things a 2-player test **physically cannot catch.** This mod is a **star**: guests talk only
to the host, who **relays** guest→guest. With two people the host is the only observer, so every relay and
every per-peer aggregate is a no-op. A third party is the only way to test them.

The recurring question for almost every check below: **"Does what guest A does show up for guest B?"**
(not just for the host). Set up **Host H + Guest A + Guest B** and have **B watch** while **A acts**.

### A. Guest→guest visibility (relays)
- [ ] **A walks → B sees A move** (not frozen at spawn). Held items A carries also move for B.
- [ ] **A and B each carry a different item at once** → on B's screen, A's item follows **A** (not snapped
      onto B's own item, not collapsed onto one avatar). This is the per-carrier slot keyed by author.
- [ ] **A customizes at the shipyard → B sees the rig change.** (This relay was historically the one
      missing piece - verify B doesn't silently keep the old rig.)
- [ ] **A drops/spawns/destroys/hangs an item → B sees it.** Same for crate insert/remove, lantern, pipe.
- [ ] **A's shop trade → B sees the shared currency change AND B's open market screen refreshes;** A is
      **not double-charged.**
- [ ] **A accepts/abandons a mission → B's open board drops/updates the listing** and B's shared log reflects it.
- [ ] **A draws on a map → B sees the lines** (temp + committed).

### B. Contention / arbitration (two guests, one resource)
- [ ] **Helm lease:** A grabs the wheel and steers; **B grabs the same wheel** → B is **denied** (stops
      predicting, follows the authoritative rudder). No tug-of-war / rudder oscillation. A keeps control
      until idle ~0.5s, then B can take it.
- [ ] **Helm relay to a passenger:** host stands **on land/another boat**; A steers a boat with B aboard as
      passenger → **B sees the wheel/rudder move**, and after A lets go B isn't stuck on a stale angle.
- [ ] **Item pickup race:** A and B grab the **same** world item at once → exactly **one** gets it, the
      other is denied (no duplicate, no silent steal).
- [ ] **Map draw lock:** A is drawing → B gets *"Map is being used by another player."* Then **A
      disconnects mid-draw** → B's lock frees (B can draw; no stuck "in use forever", no dangling temp line).

### C. Per-peer aggregation (forces must SUM, not overwrite)
- [ ] **Two pushers:** A and B both shove the same hull (or same sail) → forces **add** (boat moves faster
      than one pusher); when A stops, **B's push continues** (A's stop doesn't cancel B).
- [ ] **Two pumpers:** A and B both work bilge pumps on the same flooding boat → water drains at the
      **combined** rate; one stopping doesn't zero the other; no phantom drain after both stop.

### D. Sleep quorum with a partial crew
- [ ] **Partial crew gating:** H + A get in bed, **B stays awake** → sleep does **NOT** start (no one gets
      warped to 16× while B walks around). The "(n/total)" count is correct for everyone, including B.
- [ ] **Full crew starts:** B then gets in bed → all three sleep together.
- [ ] **Loading-joiner exclusion:** H + A in bed, **B joins and is still loading (~15-20s)** → H + A can
      still **start sleeping** (B is excluded from the quorum until B has streamed). The crew must NOT wedge
      in "Waiting for crew..." waiting on a peer that hasn't finished loading.
- [ ] **All-rested across the whole crew:** all three asleep, filling at different rates → the crew wakes
      only when **everyone** is rested (the first-rested guest doesn't cut the others short; no one is left
      warped if one's SleepRested is late).
- [ ] **Bystander relay:** A initiates sleep while **B is a bystander** → B sees "crew is waiting to sleep"
      and an accurate count; if A then cancels (leaves bed) or manually wakes, **B is released too** (B is
      never left warped asleep after A wakes).
- [ ] **Quorum completion by a leaver:** H + B waiting in bed for **A**, then **A leaves the lobby** → the
      remaining crew **starts sleeping** (instead of hanging until the 90s timeout). Symmetric: sleeping
      crew waiting on un-rested A who leaves → remaining crew **wakes**.

### E. Disconnect cleanup with crew remaining
- [ ] **A disconnects while B stays active** (A mid-push / mid-pump / holding the helm / carrying items /
      mid-map-draw / in bed) → **only A's** state is cleaned up; **B's push/pump/helm/items/draw are
      untouched.**
- [ ] **No A ghosts:** the boat isn't still shoved by A's phantom push, B can still sleep (A isn't stuck
      "in bed"), A's held items don't hang in the air forever.

### F. Scale & capacity
- [ ] **Roster overflow:** invite up to the cap (**8** by default: host + 7). The "Crew:" scroll shows the
      crew with a **"+K more"** overflow once it's full; **Invite Friend** relabels to **Crew full** and
      stops inviting at capacity.
- [ ] **Busy-crew throughput:** with 3+ all moving/steering at once, watch for rubber-banding avatars or
      delayed updates on a guest, and check `LogOutput.log` for `Processed N packets on channel` riding the
      cap (intake budget starvation under a large relayed stream).
- [ ] **OVERSIZE on a late join:** a third/fourth guest joining a **long, item-heavy** session is the most
      likely place to trip `OVERSIZE BoatWorldState` (state accumulates with crew size + time). Grep for it
      after any join where a guest lands in a half-empty/incomplete world.

---

## Appendix A - Desync symptom → likely cause

| What you see | Likely cause |
|---|---|
| Boat hard-snaps or drifts on a guest | Lost/reordered `BoatTransform` (`TELEPORT`/`TARGET_JUMP`/`POSITION_ERROR`); steady drift w/ no error lines = packets stopped (dropped peer / budget starvation) |
| Mooring/anchor/sail rope stuck at wrong length on one side | Dropped final unreliable `RopeState` and the reliable settle-terminal didn't land - **or a mod version mismatch** between the two machines |
| Ghost / duplicate items, item on one client only | `DUPLICATE item` / `[ITEM:REGISTRY] Mismatch` / failed `[ITEM:RESYNC]`; a wrong/missing item after join = a **dropped/oversize join snapshot** |
| Guest joins into an empty/incomplete world | `OVERSIZE BoatWorldState` - Steam silently dropped the >1 MB join packet |
| Avatar frozen / rubber-banding / T-pose | `PlayerPosition` stream stalled or peer dropped; check `Failed to send`, `Processed N packets` |
| Player frozen mid-jump / left behind on a moving boat | The reverted charController-freeze hazard - a **regression** if it reappears |
| One side fast-forwards (16×) while the other doesn't | Sleep/time-warp quorum stalled (a silent/loading peer blinded the watchdog) |
| Guest kicked to desktop | `EndGuestSessionAndQuit` (host left / P2P drop) - see `P2P connection lost/failed` |

## Appendix B - Log lines worth grepping

After a bad session, search **both** `LogOutput.log` and the session's `SailwindCoop-verbose-<timestamp>.log` on **both** machines:

| Line | Severity | Meaning |
|---|---|---|
| `OVERSIZE` | 🔴 error | Reliable packet (almost always the join `BoatWorldState`) exceeded ~1 MB; **Steam dropped it** → broken join. **#1 line to check after any bad join.** |
| `Large {type} packet:` | 🟠 warn | Reliable packet ≥256 KB, trending toward the 1 MB ceiling. |
| `Failed to send` | 🟠 warn | `SendP2PPacket` returned false for a peer - dead/over-capacity session or oversize send. Repeats = that peer is desyncing. |
| `P2P connection failed` / `P2P connection lost` | 🔴 error | Transport session died; on a guest this cascades to a forced quit. |
| `TELEPORT: error=` / `TARGET_JUMP: delta=` / `POSITION_ERROR:` | 🟠 warn | Boat-position correction events. Expected on join/recovery; **frequent during normal sailing = real desync.** |
| `DUPLICATE item id=` / `[ITEM:REGISTRY] Mismatch` / `[ITEM:RESYNC]` | 🟠 warn | Item desync; an `[ITEM:RESYNC]` **error** means the repair itself failed. |
| `Processed {N} packets on channel` | 🟠 warn | Bursty intake; if N rides the cap (800) the per-frame budget is saturated (a desync cause under big crews). |
| `Packet types received:` / `Packets SENT:` | ⚪ info | Transport census - compare the two clients; a wildly asymmetric class = a failing channel. |
| `[RECOVERY] Host recovering` | ⚪ info | Boat sync intentionally paused during recovery; a fresh `BoatWorldState` should follow. If it doesn't, the crew is stranded - escalate. |

## Appendix C - Known limits to expect (not bugs)

- **No world pause in co-op**: pause/mission/log menus let you walk; only the vanilla trade screen freezes.
- **Recover-under-pier:** after a recovery a player may end up at the dock instead of on deck in some cases
  (deferred; confirm whether it happens).
- **Shared clock pauses while the *host* is in a menu**: cosmetic; confirm it's not annoying in play.
- **Same build + same game version required** on every machine (network format isn't versioned yet).

See [KNOWN-ISSUES.md](KNOWN-ISSUES.md) for the full list of known limitations.
