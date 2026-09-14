# 3-player playtest, 2026-08-06 (v0.3.0, live)

Host plus two guests. This machine was a GUEST. Logs preserved in
`scratchpad/playtest-0806/` (two LogOutput captures plus both verbose logs, 6.5 MB).

Sailing `BOAT medi medium (50)`, which is the BRIG (38 ropes, the largest in the world).
`BOAT dhow medium (20)` is the sanbuq.

**STATUS as of 2026-08-06, post-session.** The five held UI fixes are deployed. Sections 5b and 10 are
FIXED. Section 8 has been measured, reframed and instrumented, but not fixed: the next session's logs
are what decide the fix. Everything else below is still unpatched.

| # | Item | State |
|---|------|-------|
| 10 | Silent transport death unnoticed | FIXED (HostLinkWatchdog) |
| 5b | Firewood inserts silently swallowed | FIXED (recount + reconcile) |
| 8 | Vertical divergence vs correction | MEASURED, instrumented, cause still open |
| 1 | Sail state on an unoccupied boat | FIXED in code (all-boats poll + host reconcile + boarding assert), deployed, NEEDS PLAYTEST |
| 2 | Shipyard blocks guest interaction | open, needs the deliberate repro |
| 6 | Fishing rod tackle unsynced | PARTLY FIXED c77c4c5 (unreleased): a live attach already synced via K4 (ItemHealthChanged, since v0.2.22); the save-divergence path (boat default rods deduped with each guest's own hook state) now takes the host's. Bobber-through-deck half not looked at |
| 7 | Sea state | superseded by 8, do not act before reading it |
| 4, 9 | Crate explosion, overnight anchor | open, undiagnosed |

---

## 1. Sail state on a boat nobody is standing on never syncs. CONFIRMED.

Reported as "his sails are all furled, half of mine on the brig are unfurled". Joined while the crew
was on the sanbuq; the brig was the desynced one. **A rejoin fixed it**, which confirms the mechanism.

ROOT CAUSE, `ControlSyncManager.PollBoatControls`:

```csharp
// Poll only current boat - original working approach
var boat = BoatUtility.GetCurrentBoat();
if (boat == null) return;
```

Ongoing rope and sail sync covers ONLY the boat the local player is standing on. Evidence from the
log: 399 RopeState sends and 134 receives, every single one for `BOAT medi medium (50)`, and zero for
any of the other six boats across the whole session.

NOT the join snapshot, which was explicitly ruled out: it carried all 7 boats and rope counts matched
exactly on every one (`incoming lengths` == `found ropes`: 3/31/7/38/11/7/5). A count mismatch would
have misaligned indices and produced the same "about half of them" symptom, so this was worth
eliminating. The apply also did real work, correcting ~20 ropes on the brig alone.

**WORSE THAN IT FIRST LOOKS.** The guest then boarded the stale brig and hauled its ropes, sending
399 RopeStates from a boat whose state was already wrong. So a stale non-current boat does not merely
fail to update - it becomes the version that gets broadcast once someone stands on it. Whoever
boards a drifted boat first wins, regardless of who is right.

Fixing this is not simply "poll every boat": `_lastRopeLengths` is a single array keyed to
`GetCurrentBoat()`, and `OnRemoteRopeChanged` already has a comment warning that writing it by raw
index for a different boat corrupts the echo guard. It wants per-boat cached state.

**FIXED 2026-08-06 (deployed, not yet playtested).** Per-boat `BoatControlState` poll over every
active boat, forced to include the boat underfoot even if the boat cache latched a partial scan; a
host round-robin reconcile (`ResendRopeForBoat` per boat, 20s interval, 2s quiet window, one boat
per tick); a boarding assert that re-seeds a boat when a crewmate steps onto it (once per occupancy,
once per interval); a targeted all-boats rope seed on join replacing the current-boat broadcast; and
a trust gate so a machine whose copy of a boat was rebuilt from a peer's shipyard packet never
broadcasts that boat until its trim restore completes or someone re-authors a rope on it.

Verify next session, from logs, not by inspection:

- host verbose log: `ResendRopeForBoat: re-seeded` lines cycling through all seven boat names
- NO re-seed lines for a boat while someone is hauling its ropes, or while a shipyard is open on it
- a guest hauling a winch FROM A DOCK produces `CONTROL:SEND` RopeState lines (today it produces
  none; this is the regression the reconcile would create if the poll were still current-boat-only)
- the deliberate repro: desync a boat's sails locally on a guest, board nothing, confirm it
  converges within ~20s without a rejoin
- guest RopeState receive counts spread across boats instead of 100% one boat

Expected log changes, not bugs: one `Rope discovery ... (background boat)` summary line per boat on
the first poll tick, `ControlRecv`/`ControlApply` RopeState pairs arriving on a ~20s cadence per
boat on guests (the reconcile), and `Rope trust suspended/restored` lines around shipyard edits.

## 2. Guest cannot interact with anything WHILE THE HOST IS IN A SHIPYARD. Corrected.

**READ THE CORRECTION AT THE BOTTOM OF THIS SECTION FIRST.** The "underway" framing below was wrong
and is kept only so the reasoning is auditable.

### The wrong version (disproven the same session)

Reported as items not picking up, then halyards not grabbing. Affected items AND ropes AND halyards,
so it is the pointer, not item sync. **Cleared completely once moored**, on land and on ship.

Established facts, both sides:

- Vanilla raycasts every pointer interaction from `GoPointer.FixedUpdate` -> `DoRaycast()`
  (decomp GoPointer.cs:215-218).
- The guest applies the host's boat transform from `BoatSyncManager.Update()` -> `ApplyBoatTransforms()`
  (BoatSyncManager.cs:197/209).

INFERENCE (unproven): while underway the guest's boat is being corrected every Update frame, and the
FixedUpdate raycast is working against a target that moved out from under it. Consistent with all
four observations: guest-only (the host never corrects itself), underway-only (corrections go to zero
when moored), everything attached to the boat, and oakum being briefly grabbable in a frame where
they happened to line up.

Note the apply-in-Update path predates v0.3.0 - the code calls it "the pre-v0.2.28 single-boat
ApplyBoatTransform body" - so this is likely long-standing rather than new.

### THE CORRECTION: it is the shipyard, not motion

Later in the SAME session, underway again with the host out of the shipyard, the guest reported no
trouble at all picking up items or working halyards and ropes. Motion is therefore NOT the variable.

What actually coincided with every failure was **the host having a boat admitted to a shipyard**. The
guest said "host is in shipyard editing now" during the window, and everything cleared once that
ended.

That fits the code far better. `BoatSyncManager` deliberately SUPPRESSES the transform stream for a
boat admitted to a shipyard, because vanilla `AdmitShip` kinematically lifts that boat onto the
cradle and streaming a cradle-lifted transform would fight peers' view of it. KNOWN-ISSUES separately
records that the cradle lift is visible ONLY to the player using the shipyard. So for that whole
window the guest was standing on a boat that was unlifted, unstreamed, and diverging from the host's
copy of it.

That explains every property the motion theory had to strain for: guest-only, whole-boat rather than
item-specific, cleared the moment the shipyard was done, and a dock cleat reachable from some angles
and not others - which is what a boat sitting somewhere its colliders are not looks like.

**Reproduce deliberately:** guest stands on a boat, host admits THAT boat to a shipyard, guest tries
to pick up an item, haul a halyard, and click a dock cleat. Then repeat with the host admitting a
DIFFERENT boat, which should stay clean, since the suppression was narrowed in v0.2.28 to the one
admitted boat.

LESSON: the first correlation offered by a live playtest is a coincidence until a second observation
separates the variables. Both wrong calls today - this and the `Sun.sun.timescale` freeze - came from
taking one co-occurrence as causal.

DO NOT confuse this with `Sun.sun.timescale`. The `[TIME:RECV] timescale=0.0` in the log is the
DAY/NIGHT clock, which stops at a port and is normal. It is NOT `Time.timeScale` and does not stop
FixedUpdate. I misread it as a freeze during the session; it is not one.

## 3. Guest-only violent boat slam when leaving a moored boat carrying a rope

Guest jumped off with the aft rope moored and the starboard bow rope in hand. The ship slammed
repeatedly into the water on the GUEST only, with lighting flicker; the other guest heard it, so it
was not purely visual. The host was calm throughout - authoritative BoatTransform never exceeded
5.12 m/s all session and read 0.11-0.17 m/s at the time.

Suspect the v0.3.0 crew-weight change. The old blanket "no crew weight while moored or anchored"
guard was removed on the reasoning that the spring risk lasts a single moment, at moor time, and is
handled by `MooringSpringCrewMassPatch`. That reasoning covers the mass at the moment `MoorTo` runs.
It does NOT cover crew mass CHANGING while already moored, and vanilla fixes the spring constant
once: `mooring.spring.spring = boatRigidbody.mass * 6f`. A crew member leaving an already-moored boat
therefore leaves a spring sized for a heavier vessel.

Unproven, and the guest also applies crew mass locally, so guest-only divergence is plausible.
Reproduce deliberately: moor with the whole crew aboard, then have everyone but one leave.

## 4. Crate contents exploded (oranges everywhere)

The crate was open before this guest joined. At join the log shows
`[ITEM-VERIFY] Cleared 7 guest world items, host sending 6`, and the cleared list included
`oranges(id=694013607)`. Suspect the reconcile respawns container contents at overlapping positions
and Unity depenetration flings them apart in one frame - the same mechanism that sank a moored brig
in the 2026-07-02 playtest. Test: rejoin near an already-open crate.

## 5b. Firewood: one of three inserts never sent. EVIDENCED.

Guest loaded three logs into the smoker. Guest sees three, the host and the other guest see two, and
the smoker lit on THEIR screens at a fuel count the guest does not have.

```
13:53:22.084 [COOKING:SEND] FuelInserted, fuel=340551740, stove=254583864
13:53:22.084 [COOKING:SEND] FuelInserted, fuel=201735966, stove=254583864
```

Exactly TWO sends for three local inserts, and both in the SAME millisecond. These are reliable
sends, so this is not packet loss - the third event never reached the send path. Two firing in one
frame with a third missing suggests same-frame collapsing (a dedupe, a per-frame guard, or one
insert not triggering the patch at all). Start at the FuelInserted patch and ask what makes it fire
once per frame rather than once per log.

Divergent fuel count is not cosmetic: it decides whether the stove lights, so the crew disagree about
whether cooking is happening.

## 5. Dock-cleat rope grab briefly impossible

Could not grab the starboard bow mooring rope from the DOCK CLEAT, while the stern rope and the same
rope from the SHIP end both worked. Resolved on its own.

Recurred later, and the follow-up detail is the important part: **"some angles I can select it, some
angles I can't"**, and it changed after boarding and leaving the boat. The boat was STATIONARY at the
time (0.04-0.07 m/s), so this is not the underway problem in section 2.

ANGLE DEPENDENCE RULES OUT THE OBVIOUS CANDIDATES. A disabled collider is unselectable from every
angle, so this is NOT vanilla's cleat-collider disable, even though that mechanism is real and worth
knowing: `MoorTo` does `mooring.GetComponent<Collider>().enabled = false` (decomp line 258) while
`Unmoor` re-enables only whatever `mooredToSpring` points at NOW (line 275), and
`OnRemoteMooringChanged` runs `if (IsMoored()) Unmoor(); MoorTo(dock);` on every remote update - rope
1 cycled moored/unmoored three times this session. That asymmetry can still strand a cleat and should
be checked, but it cannot produce angle dependence. Same reasoning rules out the layer-2
(Ignore Raycast) adjuster state that `GrabVisual` sets and only `ReleaseVisual` clears.

What angle dependence DOES mean is an invisible collider intercepting the ray from some directions.
Most likely a mooring rope left mispositioned with a live collider - v0.3.0 drives rope transforms
directly in `MooringRopeHoldSync.LateUpdate` and `DriveThrows` - or a remote avatar capsule. Note
section 3 happened on this same boat, where a rope was being carried when the guest jumped off.

Left no trace in either log: no `[MooringAdjust]` or `[MooringHold]` warnings, and
`MooringRopeAdjustSync` only logs on failure, so silence proves nothing either way. **Add verbose
logging to the rope hold/adjust files before hunting this**, and log rope world positions so a rope
parked somewhere impossible is visible in the log rather than only in the raycast.

## Fixed and BUILT, not deployed (session was live)

- **Long Steam names clipped the invite button.** `Salvatore da Monferrato` rendered as
  "n Salvatore da Monferrc" inside a fixed 220px button. `_name` style now wraps, the button uses
  `MinWidth`, and the name inside it is shortened on a word boundary.
- **Join progress bar reweighted.** The snapshot wait measured 68% of a 2-player join and **82%** of
  a 17.3s 3-player join, against the 40% of the bar I had given it. Now 60%.

## Confirmed working

SE **0.10.1** live: `SE rig sync enabled`, token `[SE=0.10.1/topsailPatch1/addSails1]`, handshake
accepted - the compatibility survey's "0.10.1 needs no patch" conclusion, verified in a real session.
Join screen ran clean at both 7.1s and 17.3s. Zero errors in either session.

## 6. Fishing rod TACKLE is not synced at all. CONFIRMED.

Guest A sees hooks on both stern rods. Guest B sees no hook AND no bobber on either.

The fishing packet range (120-129) is FishingStateSync, FishingLineLengthSync, FishBite,
RodOwnerChanged, FishingCast and the bobber stream (203). **There is no tackle packet**, and
`FishingSyncManager` contains no reference to hook or bait attachment. Tying a hook onto a rod
therefore has no wire representation and simply never leaves the machine that did it.

NOT the same as GitHub issue #4 ("lantern hook desync"). Hanging an item on a WALL hook does sync,
via ItemHung/ItemUnhung (packets 79/80) - confirmed live this session
(`[ITEM:APPLY] Hung item 1102919764 on hook 1271367798`). Rod tackle is a separate, unbuilt thing.

The missing BOBBERS are probably a second and milder problem. The bobber position streams only for
rods in `_castRods U _hookedRods`; for anything else the code's own comment says a viewer's bobber
"never leaves the rod - it dangles vertically through the deck", which also puts `floater.InWater`
true under the hull. So on the other guest's screen the bobbers may be below the deck rather than
absent. Worth having them look down through the deck to confirm which of the two it is.

Bobber sync itself demonstrably works for a CAST rod: 2281 fishing lines this session, with
`[FISHING:RECV] BobberSync, rod=3355559 ... inWater=1` applying steadily.

## 7. Sea state: the deck goes visually awash on a guest, without real flooding

Confirmed by the guest in session: the deck washes over visually but no water is actually taken on,
which is correct - `waterLevel` is host-authoritative (`BoatDamageUpdateWaterAndDragPatch` prefixes
the guest's `UpdateWaterAndDrag` to `return false`, and `ApplyGuestHullPhysics` never advances it).

So this is purely a WAVE FIELD divergence, and the fix is the input, not the physics model.

**Do not re-architect toward local physics with a nudge - that is already the design.**
`VerticalCorrectionFactor = 0.35f` deliberately lets local buoyancy own the hull's height on the
local wave surface while XZ stays host-authoritative, and the comment there says plainly that it is
absorbing a genuinely different wave field rather than smoothing a residual, and that whoever
revisits it should treat the wave field as the unfixed part.

**Cheapest high-leverage fix: replicate `GameState.distanceToLand`.** It is one float, it is the
dominant unreplicated term in wave amplitude, it spans 6.7x between "beside an island" and "deep
ocean", and `LoadGame` resets it to 999999 and reconverges slowly - so straight after a join a guest
can render open-ocean swell under a hull the host has moored in an island's lee. `eyesFullyClosed` is
the other unreplicated term and is probably minor by comparison.

Wind IS already synced (`WeatherSyncManager` replicates `Wind.currentWind` and drives
`Wind.instance.transform.rotation`, which `WavesInertia` reads for direction). Note there WAS a
deterministic spectrum seed, `WeatherPatches.OceanSpectrumSeedPatch`, added v0.2.16 and DELETED in
v0.2.19 when ocean sync was retargeted at Crest - worth reading that history before rebuilding one.

Once amplitude agrees, revisit whether 0.35 can be raised, because the residual would finally be a
residual.

## 8. A physics JOINT plus a position correction tears the boat apart. Guest-only. UNIFIES 3 AND THIS.

Two separate reports turn out to be one mechanism.

- Guest jumped off a moored boat carrying a rope: the ship slammed repeatedly into the water on the
  guest, host calm (section 3).
- Crew dropped anchor underway: the boat "freaked the fuck out" for BOTH guests, host fine.

EVIDENCE. The guest's boat runs a persistent multi-metre lag behind the host's target and is being
dragged forward every frame to close it:

```
[TELEPORT:DEBUG] POSITION_ERROR: 5.9m (y=0.04, vFactor=0.35),
    current=(457.3, -0.8, 480.6), target=(457.8, -0.8, 486.5), velPre=8.24m/s, velPost=8.28m/s
... 5.8m, 5.6m, 5.5m, 5.4m, 5.2m, 5.1m ...
```

730 correction lines in one session, error hovering around 5-6m at 8.24 m/s.

MECHANISM. A mooring spring or an anchor joint pins the hull to a FIXED WORLD POINT. The boat sync is
simultaneously hauling that same hull several metres to match the host. The joint resists, the
correction insists, and the hull convulses. It is guest-only for the obvious reason: the host never
corrects itself, so on the host there is only the joint.

That also explains why it presented as "both clients, host fine" for the anchor and guest-only for the
mooring: every guest gets corrections, the host gets none.

This reframes section 3. Crew mass and the moor-time spring constant may still be a real secondary
factor, but they are not needed to explain either event, and the correction-versus-joint fight
explains BOTH with one cause. Test that first, it is cheaper: watch POSITION_ERROR while dropping
anchor.

WHY THE LAG EXISTS AT ALL is the thing to fix. A steady 5-6m error at cruising speed is not jitter.
Whether that is interpolation lag, send rate, or the guest's own propulsion diverging from the host's
needs measuring before anyone changes the correction strength - raising correction strength would make
the joint fight WORSE, not better.

### MEASURED 2026-08-06 (after the session). The framing above is wrong.

All 4867 POSITION_ERROR lines from both verbose logs, parsed. **The error is not horizontal lag.**

```
vFactor=1.00  3606 (74.1%)      <- soft-Y override is DEFEATED on three quarters of frames
vFactor=0.35  1261 (25.9%)
horizontal error   median 1.30m
vertical error     median 5.70m, p90 10.00m, max 13.50m
vertical dominates on 3444 of 4867 frames (70.8%)
```

The sample above (`y=0.04`, a 5.9m Z error) is the unrepresentative quarter. The dominant term is
VERTICAL, and it routinely blows past `VerticalHardCorrectThreshold = 3f`, which flips `verticalFactor`
from 0.35 to 1.0. So the softening that exists specifically so local buoyancy can own the hull's height
is switched off exactly when the divergence is large enough to matter, and the corrector then hard-pulls
Y against buoyancy that is pushing back. That is a fight the code was written to avoid, and it does not
need a joint to start. Add a joint and you get the reported convulsions.

Two further facts constrain the cause:

- **The guest's hull sits ABOVE the host's on 88.3% of frames.** A one-directional bias, not noise. Two
  independently-simulated wave fields would give roughly 50/50.
- **In 21 of 24 sustained bursts, guest and host heave by the SAME amount** (stdev 2.23 vs 2.14, 1.24 vs
  1.24, 1.39 vs 1.35) while separated by a standing 5-10m. Matched heave with a constant offset is not a
  wave-amplitude difference.

RULED OUT: the floating origin. `outCurrentOffset` is a 512m XZ grid and its **Y is 0.0 in every one of
the 59 logged FOM_SHIFTs**, so it cannot contribute a vertical offset. Also ruled out: buoyancy scaling
(`forceMult` is a constant 25.000 throughout).

NOT YET EXPLAINED, and these logs cannot settle it: what puts the guest's hull metres above the host's
with their heave matched. Every quantity logged was the guest's own, so a guest floating too high on its
own sea and a host sitting too low on its are indistinguishable.

**INSTRUMENTED (v0.3.1).** The discriminator is the SEA SURFACE, which nothing was measuring. HULL_STATE
now samples the local Crest surface under the hull and prints:

```
seaY=<local sea surface>  freeboard=<our hull above OUR water>  hostFreeboard=<host's hull vs OUR water>
```

Read it like this on the next session:

- `freeboard` off nominal -> OUR buoyancy is wrong (mass, forceMult, flooding).
- `freeboard` nominal but `hostFreeboard` metres submerged -> the two machines disagree about where the
  SEA is, not about where the boat is. That is a wave-field fault and points at the one unreplicated
  term, since ocean TIME, wave direction, inertia, magnitude and the Crest crossfade inputs are all
  already synced by WeatherSyncManager - leaving only the per-player `distanceToLand`/`eyesFullyClosed`
  damping.
- both nominal -> the target itself is wrong, and the fault is upstream in what the host sends.

Note ocean phase is NOT a candidate: `OceanTime` is synced (slew-converged, never snapped).

CAVEAT ON THE NUMBERS: the diagnostic only prints when error > 5m, so these are the distribution of
EXCURSIONS, not of all frames. That is the right population for this question but it is not "the typical
error". The distanceToLand bucketing (below) rests on only 112 paired rows, because HULL_STATE is
throttled - it is suggestive, not conclusive.

CORRECTION TO THE PRIORITY ORDER: "measure why the guest is 5-6m behind" was the wrong first task,
because it is not behind. The first task is instrumenting the vertical axis on both machines.

RELATED, same session, same boat: `ANCHOR_CARRY: moved anchor of 'BOAT medi medium (50)' by 17.8m
with boat teleport`, immediately preceded by SEVEN `Blocked guest auto anchor release
(host-authoritative)` in 130ms. The guard held, but vanilla only auto-releases when it believes the
anchor state is invalid, and it wanted to seven times in a tenth of a second.

## 9. Anchored overnight, woke to rope length 0 and the boat drifting

Reported: anchored at night, on waking the anchor read 0 and the boat had drifted forward. Rope length
0 means not anchored, so the anchor did not hold across the sleep.

Not diagnosed. Sleep runs a 16x warp with its own send-rate scaling (see the v0.2.35/v0.2.37 sleep
work), and section 8's correction pressure does not stop while asleep, so an anchor joint under a warp
is a plausible place for the two to interact. Needs a deliberate reproduction: anchor, sleep, and
watch the anchor rope stream and POSITION_ERROR across the warp.

## 10. A silent transport death is never noticed. No watchdog for mid-session silence.

The guest's Steam account was logged into from another machine, which invalidates this process's
Steam session. Symptom: every crewmate frozen in place, no message, no indication anything was wrong.

```
16:02:21.390  last packet RECEIVED (BoatTransform)
16:03:39.748  still SENDING (PLAYER:SEND OnLand, position unchanged)
16:03:40.939  [SYSTEM:EVENT] Session ended
```

**Incoming stopped at 16:02:21 and the mod kept sending for 78 more seconds without noticing.** No
notice, no panel, no "lost connection". Crewmates froze because their last known positions were all
that remained, and nothing was watching for the silence. The main log for that window is nothing but
Profiler lines.

WHY IT SLIPS THROUGH. Disconnect handling hangs off Steam's P2P callbacks - "The host closed the
co-op server." from OnPlayerLeft, "Lost connection to the host." from the P2P drop path. When Steam
invalidates the session out from under the process, no peer "leaves": the transport simply stops
delivering, no callback fires, and nobody is checking a clock.

THE FIX ALREADY EXISTS IN THE RIGHT SHAPE, for a different moment. `GuestJoinWatchdog` polls for a
BoatWorldState that never arrives and, after GuestJoinSnapshotTimeoutSeconds, explains itself and runs
the normal guest leave path. There is no equivalent for mid-session silence.

Wants: track the last time ANY packet was received from the host, and past a threshold raise the
standard message panel and run the same leave path. Choose the threshold above whatever a legitimate
stall can be - note the host's own pauses and menu time do NOT stop packets, so silence really is
silence. Guest-side only; the host has its own peer-timeout handling.

This is worth doing regardless of how rare a stolen Steam session is, because it covers EVERY silent
transport death: a dropped network, a crashed host process, a router that stops forwarding. Today all
of those present as "everyone is standing frozen and the game will not tell you why".
