# Known Issues & Limitations

Honest current state of the mod. This is **alpha**: it has had extensive *code review*
but only limited *live testing*. **Back up your saves before playing.**

Status legend: 🔴 blocker · 🟠 needs live testing · 🟡 known limitation / by design · ⚪ minor

---

## Testing status (read this first)

- 🟠 **Alpha, actively bug-fixed off live sessions.** The mod has been played in real
  2-5 player sessions, and each release fixes what those sessions surfaced - but plenty
  of paths are still lightly tested. The newest fixes in any given release are usually
  unvalidated until the next session; treat everything as experimental and
  **report what you hit** (see the bug-report steps in the README).

## Open / deferred

- 🟠 **Recovery placement ("recover-under-pier").** After a host boat recovery, the player
  may end up at the dock instead of re-embarked on the deck in some cases. The cause is
  understood but the fix needs a live v0.38 session to confirm; deferred until then.
- 🟠 **Very large join snapshots.** A world with hundreds of items can produce a join packet
  that exceeds the ~1 MB Steam reliable-P2P limit, which would fail the join. The host log
  now prints an `OVERSIZE` line if this happens. It hasn't been seen on a real save yet; if
  you hit it, that's the trigger to split the snapshot into chunks (not yet implemented).
- ⚪ **Shared clock pauses while the host is in a menu.** Opening a menu pauses the in-game
  Sun; the host broadcasts time, so the day/clock briefly pauses for everyone while the host
  is in a menu. Cosmetic for short menu visits; worth confirming it's not annoying in play.

## By design (may surprise you)

- 🟡 **The world does not pause in co-op.** Time can't stop for a shared boat, so the pause
  menu keeps the world running and **you can still move with WASD** while it's open. The tag
  reads **"(online)"** rather than "(paused)" to make this clear.
- 🟡 **The vanilla trade (market) screen still freezes your movement.** That's vanilla
  behavior and it only ever opens at a stationary port, so it's left as-is - even though
  other menus (pause / mission / log) let you move. (Intentional inconsistency.)
- 🟡 **Survival needs are per-player; money/economy is shared.** Food/water/sleep are
  independent; the wallet and world economy are shared and **host-authoritative** (a
  guest's purchase is validated against the host's wallet and rejected with a
  notification if the crew can't afford it).
- 🟡 **One shared boat.** The crew sails one vessel together; multiple crewed boats in a
  single session are not supported yet.

## Compatibility

- 🟡 **Everyone must run the same mod build AND the same Sailwind version.** The network
  format is not yet versioned, so a mismatch can desync silently. (A version handshake is a
  planned pre-release item.)
- 🟡 **Targets Sailwind v0.38.** Game updates can break the Harmony hooks; a new game patch
  may need a mod update.
- 🟡 **Heavy Harmony patching**: may conflict with other mods that patch the same systems.
- 🟡 **No dedicated servers.** Co-op is host-authoritative over Steam P2P via friend invites
  only; the host must stay connected.

## Save safety

- 🟢 A **guest never writes the host's world into their own save slot** (a phantom session
  save is used instead). Still - this is alpha; **back up your saves.**
