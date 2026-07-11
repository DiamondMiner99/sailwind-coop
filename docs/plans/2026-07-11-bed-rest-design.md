# Bed Rest - design (2026-07-11)

Player request (Jav1k + one other): regenerate energy while lying in bed awake, so the host
can go AFK a few minutes without dying. In coop a lone host can never sleep solo (all-crew
rule), so a bed currently does nothing for them; meanwhile vanilla drains sleep at 5/s and
water/food/protein continuously, each of which can PassOut an AFK player.

## Behavior

While the LOCAL player is in the coop "in bed but not sleeping" state (from bed commit
through the whole waiting-for-crew window, until leaving the bed or real sleep starting,
i.e. `GameState.sleeping == false`):

- `sleep` regenerates at 2/s x `Sun.sun.timescale` (25% of real sleep's 8/s), capped at 60.
  At or above 60, bed rest only stops the drain (no regen past 60) - real crew sleep stays
  the only way to fully rest.
- `food`, `water`, `protein`, `vitamins` are frozen (no drain, no regen).
- `alcohol` keeps its vanilla behavior (sobering up in bed is allowed).

Per-machine and per-player (independent survival pillar). No wire change: vitals already
flow through the existing SurvivalStats sync; versions with/without the feature interoperate.

## Mechanism

Harmony prefix + postfix around `PlayerNeeds.Update`:
- prefix snapshots sleep/food/water/protein/vitamins when the resting state is active;
- postfix restores food/water/protein/vitamins to the snapshot and applies
  `sleep = min(max(post, pre) + regen, cap-or-pre)` (regen only below 60; never reduce an
  already-higher value).

Snapshot/restore rather than counter-adding vanilla's rates, so the patch cannot drift if
vanilla rates change. A frozen stat cannot cross 0 mid-Update, so no spurious PassOut.

In-bed detection reuses the sleep sync path's existing local in-bed tracking (the same
state the v0.2.18 "committed-but-waiting" valve uses).

## Config

`Coop.BedRest` (BepInEx config, default true). No rate/cap knobs unless players ask.

## Testing

Solo-host smoke test: commit to a bed with no other crew in bed; verify sleep climbs to 60
and stops, hunger/thirst/protein/vitamins hold still, drains resume on leaving the bed.
