# Roadmap

The one place that says where things stand, for Sailwind Co-op and the Sailwind Player Model mod it
depends on. Update it at every release and whenever an item changes state. If this file and another
doc disagree, this file wins; the older plan docs keep their design detail but not their status.

Last updated: 2026-09-14.

## Released

| Mod | Version | Date | What |
|-----|---------|------|------|
| Co-op | v0.4.0 | 2026-09-14 | Weather and market sync, held tools, split onto the Player Model mod |
| Co-op | v0.3.2 | 2026-08-09 | Guest-held items vanishing on the host |
| Player Model | v0.1.2 | 2026-09-14 | Character clone fix, undress guard, live sole offset, color options |

## Staged for release

Nothing. Co-op 0.4.0 and Player Model 0.1.2 are both out.

Never exercised in a lobby, so watch for reports: the wind sound zero-frame guard (Player Model,
log noise only) and color sync between crewmates.

## Open bugs, blocked on logs from a real session

- Fishing: two fish from one catch. Guarded and logged, cause unknown.
- Grab collisions.
- Guest boat vertical divergence, median 5.7 m. Instrumented.

## Next up, in order

1. Held-tool arm poses (Player Model). Two-bone arm IK aimed at the held item's pose, which is
   already streamed. No wire change. The solver and bones exist from the crouch legs.
2. Downed state (Player Model, then co-op). A `GoDown(reason, impulse, seconds)` call on the pose
   claim system, then a networked downed flag so the crew sees it. Unblocks Three Sheets to the Wind.
3. Fixed-object interactions: helm, winch, pump, ladder. Needs one actor-keyed packet in co-op,
   because the boat control packets do not say who did it.

## Planned, not scheduled

- Quartermaster. Host appoints one, default host, reverts on leave. Picks a split: shared, manual
  allowance, even split, percentage. Two ways in: per-player buckets behind the host wallet, or just
  the role plus a permission gate on big spends. Either way, first audit every spend and income path;
  the onsen bath (island 39, Emerald Archipelago) is a known unrouted spend, so a guest bathes free.
- Mug drinking animation for crewmates. Food and barrels already read through held-item motion.
- HMS Leopard cannon fire. Cosmetic-only broadcast, same shape as the bell.
- Cold-launch invite auto-join. Wants its own release.
- Player Model on Thunderstore, so r2modman users stop importing by hand.
- Player Model: a config switch to force the shopkeeper as the clone source.

## Backburner

- Chair sitting. No sitting in vanilla; needs pose, interaction, camera and mount logic. The
  prerequisite for anything played at a table.
- Ship games: poker, liar's dice. Blocked on sitting.
- Onsen exploit fix on its own. Folds into the quartermaster spend audit.

## Not doing

- Multi-boat. Decided 2026-07-10: the mod is about crewing one ship together.
- Ghost markers and wind streaks in Rig Balance. Built, then removed by decision.
