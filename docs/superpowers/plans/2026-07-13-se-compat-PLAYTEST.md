# Shipyard Expansion compat - live playtest checklist (v0.2.31)

Everything below is UNTESTED LIVE. The build is clean and the code is reviewed, but no line of this
has ever run in the game. Collect `BepInEx/LogOutput.log` from EVERY machine on any failure.

Deploy: `src/SailwindCoop/bin/Release/net472/SailwindCoop.dll` -> the canonical plugin path.
SE 0.9.0 is already installed at `<Sailwind>/BepInEx/plugins/ShipyardExpansion/`.

## 0. Sanity (do this first, it is the cheapest signal)

- [ ] Launch with SE installed. Log shows `[SECompat] Shipyard Expansion v0.9.0 detected; SE rig sync enabled.`
      If it says "not installed", the BepInDependency soft dep did not order the load and the whole
      feature is silently off. Nothing else on this list will work.
- [ ] Launch WITHOUT SE (rename the plugin folder). Log shows `SE sync disabled`, and the mod behaves
      exactly as v0.2.30 did. This is the regression that matters most: nearly all users have no SE.

## 1. The gate (admission)

- [ ] Host SE, guest no SE -> guest is REFUSED, with a message naming Shipyard Expansion (not the version).
- [ ] Guest SE, host no SE -> REFUSED (the symmetric direction; this one is easy to get wrong).
- [ ] Both SE, identical settings -> ADMITTED normally. A false refusal here locks out every SE user.
- [ ] Both WITHOUT SE -> ADMITTED normally, no message, no behavior change.
- [ ] In SE's config, flip "Link topmasts" (or "Add lug sails") on ONE machine only -> REFUSED.
      This is new: those toggles change the rig contract, so a divergent crew must not be admitted.
- [ ] Note what the refusal message actually looks like if a peer ever shows a `/noSync` or
      `/noSailData` suffix - the raw token reaches the user and may read as gibberish.

## 2. The rig syncs (the feature)

- [ ] Host customizes a Sanbuq: extra mast, resized + rotated + FLIPPED + retextured sails.
      Guest joins -> identical rig on the guest.
- [ ] Both players can operate every rope and winch on that boat. (Checks that rope indices did not
      shift - a jib flip deactivates a rope object, which is exactly what could shift them.)
- [ ] Live edit by the HOST while a guest watches: a structural change (add a sail) AND a pure
      cosmetic change (angle / flip / texture only) both propagate.
- [ ] **UN-FLIP a flipped jib.** This is the bug the whole rebuild design exists for: SE can only ever
      turn a flip ON, so un-flipping is the case that would silently not propagate. Verify it does.
- [ ] Live edit by a GUEST while the host and a second guest watch (tests the host star relay).
- [ ] **Flip a jib, then close the shipyard within one second.** A ~800 ms blind window used to drop
      that edit permanently. It should propagate.
- [ ] **Add a mast with NO sails on it.** Genuinely uncertain: the mast-change diff may be dead code,
      in which case a bare mast may not broadcast at all. If it fails, that is a known open question
      (errata B6), not a surprise.
- [ ] Save, quit, reload the co-op session with SE rigs. Guest rejoins -> rig intact.

## 3. Rope trim (where the subtle bugs live)

Applying an SE rig rebuilds the sails, which destroys and recreates the rope controllers. A lot of
machinery exists to stop that from wiping sail trim. It has never run.

- [ ] After a guest joins a host with a customized rig: the sails are at the HOST's trim, not fully
      unfurled / at prefab default.
- [ ] While one player makes a cosmetic SE edit at the shipyard, the OTHER players' sails on that boat
      do not jump to default trim.
- [ ] One player winches the shipyard boat's sails WHILE another applies an SE edit to it. No rope
      should end up at the wrong length. (Accepted residual risk - a narrow same-frame window.)
- [ ] Raise the anchor / turn the helm at the moment an SE edit lands. Neither should get yanked back.

## 4. Performance (the one thing that could make it unplayable)

- [ ] Have someone HOLD a sail move or scale button at the shipyard (it repeats at 5 Hz) while a guest
      watches. Watch the guest's FPS. Guests in this project have been seen at 6-11 FPS, and each
      received edit can rebuild every sail on the boat.
- [ ] In the log, SE prints `attempting to save data` once per rebuild. During a drag you want to see
      roughly ONE per poll, not two. Two per poll means the rebuild-skip optimization is not firing
      (degraded but not wrong - it falls back to the old behavior).
- [ ] Look for a verbose line saying the SE rebuild was skipped as a no-op. If it NEVER appears during
      a drag, the optimization is not landing and the double-rebuild cost is live.

## 5. Known open items (not bugs to report, but things to watch)

- A malformed blob BODY (only reachable from a peer running modified code) can leave a rig
  half-applied. modData is rolled back; the live sails are not. Deliberately deferred - hardening it
  risked over-strict validation that would silently reject real blobs and disable the feature.
- `Coop.AllowVersionMismatch` also bypasses the SE gate. That is intentional and now documented in the
  config description.
- The rotate-and-hold path still costs two sail rebuilds per poll on receivers (drag and scale were
  reduced to one).
