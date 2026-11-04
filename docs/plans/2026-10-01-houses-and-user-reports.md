# Sailwind 0.39 houses and the 2026-09-22 player reports

Written 2026-10-01. Status lives in `docs/ROADMAP.md`; this file keeps the reasoning. Everything here is
static analysis against the working tree, the 0.38.1 and 0.39 decompiles and scene data read from `level24`.
Nothing has been run in a session.

## How it was reviewed

The uncommitted 0.39 diff was reviewed in five areas (purchase and ownership, items stored in a house, house
sleep, a house-side gap audit, a gap audit of everything else), each followed by a second pass that tried to
refute every finding against the code. The three player reports were then diagnosed one each.

## Houses: what was wrong beyond the first 0.39 round

| Problem | Cause | Fix |
|---|---|---|
| An item a crewmate carries out of a house is deleted on every other machine 1 km later, and a guest's carry moves it in the host's save | Vanilla un-files a house item only in `ShipItem.ExitHouse`, from the item's own trigger leaving the house while held. Other machines disable that trigger for the carry, Unity sends no exit for a disabled collider, and the drop apply never wrote `parentObject`. `BoatLocalItems.CacheCurrentItems` selects on that field alone | `ItemSyncManager.ReleaseHouseLatch` at every remote pickup and at a local pocket; the boat-drop apply files the item under the boat (vanilla `EnterBoat`'s job); the land-drop apply files it under the house that contains the drop position |
| Items set down in a house are turned 34 to 172 degrees on other machines | `SendDropPacket` sent `localRotation` for world drops. A held item in a house is parented to the house, and five of the six house triggers are yawed | Rotation is sent in the same frame as position: world for a world drop, boat-relative for a boat drop |
| House contents duplicate on a guest after leaving and returning | Host stream-in broadcast and the guest's own cache both spawn the same ids; the far-to-near check runs on each machine's own 10 to 20 second timer | `BoatLocalItemsJoinSpawnGuard.DropEntriesAlreadyLive` drops cached entries whose id is already registered |
| New hinged doors never sync | `GPButtonHouseDoor` is a sibling of `GPButtonTrapdoor`, not a subclass. Six house doors and five cabin doors on `BOAT dhow large (30)` | `HouseDoorOnActivatePatch` and the `housedoor|` key path in `TrapdoorSyncManager`, including the join replay |
| House and bunk pairing depended on who lay down last; a crewmate's house bed could switch off the crew's rested wake | The house flag was ORed in from any crewmate, while every host-side consumer really depends on the host's own `GameState.currentHouse` | `TransitionToSleeping` assigns the flag from the host's own bed; the house mismatch refusal is gone; the 60 s backstop covers house sleeps |
| Guests ran a different clock for a land bed outside a house | 0.39 `Sleep.FallAsleep` sets `timeskipSleep` whenever `currentBoat == null` | The host mirrors that rule in `_isTimeskipEnabled` |

Known and left: lossy stream-in for house contents (crate membership, food state, charts, nails), a guest at
the house with the host over 1 km away, the purchase-time jib
reset on guests, raw destroys in the join clear.

Three scene facts house sleep depends on and only the game can confirm: `GameState.currentHouse` stays set
while lying in a house bed, `GameState.currentBoat` is null inside a house, and a teleport out of a house
clears `currentHouse`. Vanilla prints `Current house:` to Player.log on every change.

## Report 1: the ship jerks and an explosion sound plays when a crewmate places an item

Reported on v0.4.0, on every machine but the placer's, on every placement.

Cause. `ItemSyncManager.UpdateRemoteHeldItemVisuals` hands the carrier's Player Model body the item and its
`ItemRigidbody` transform every `LateUpdate`. The body treats a pose as current for one more frame
(`SyntyBody.EffectiveInteraction`: `Time.frameCount - _heldFrame <= 1`) and writes both transforms. A drop is
applied in `Update`: the rigidbody is parented to the walk collider at the packet position, its colliders are
re-enabled and `ItemRigidbody` is switched back on. In that same frame's `LateUpdate` the body still sees a
carry and moves the re-armed rigidbody to the hand pose in the visual frame, on the real hull. The next
physics step pushes item and hull apart. `BoatImpactSounds.Impact` plays the crash. Introduced by 16b32f2
("crewmates hold their tools"); v0.3.2 had no body write.

Fix. `ItemSyncManager.ReleaseBodyHeldItem`, called from `ClearSyncedHeldItemById` and
`ClearSyncedHeldItemForCarrier`, clears the body's held item in `Update`, before any `LateUpdate`. Covers
drop, hang, crate insert, stove fuel, destroy and disconnect.

To confirm in a log (verbose, F8): `[BOAT:IMPACT] '<boat>' hit by item collider ...` appeared within a frame
or two of `ItemDropped` before the fix and should not after; `Released <id>'s body pose for item N` should
appear at each release.

## Report 2: one player sees the boat half submerged, the others see it normal

Cause. Crest evaluates a Gerstner component as `sin(k * (dot(dir, scenePos) + c*t) + phase)`. On a floating
origin shift `ShapeGerstnerBatched.SetOrigin` adds `k * dot(-shift, dir)` to every phase, using the wave's
direction at that moment. `OceanUpdaterCrest.DCTInertiaNewCycle` gives the inertia wave that is fading in a new
direction every cycle and leaves its phases alone. So each inertia wave's phases encode every shift made under
every earlier direction: each machine's own voyage. Host and guest have the same spectrum and the same clock
with crests in different places. The guest's boat corrector then pulls the hull toward the host's Y, which is
a height on the host's water. Above 3 m of vertical error the softening is off, and the sustained-divergence
snap puts the hull at the host's Y outright. The water plane inside the hull follows `waterLevel` only, which
the host owns, but wave splash and the sea surface over the deck follow hull pose.

This also explains the 2026-08-06 measurement (matched heave, metres of standing vertical error, softening
off on 74 percent of logged frames). The comment in `WeatherSyncManager` that wave phases are deterministic
was the wrong assumption.

Fix, behind `Coop.SharedSea` (default on, read live):
- `Patches/OceanPhasePatches.cs` writes each wave's phases as `seedPhase - k * dot(offset, dir)`: after every
  origin shift, when a wave starts fading in, when a guest adopts the host's direction, and once per session.
  The weather packet now carries both inertia waves' actual angles so the directions are equal.
- `BoatSyncManager`: a downward vertical correction is scaled by `DownPullAllowance` (1 while buoyancy
  carries no more than the hull's weight, 0 at 1.5 times), and stall or escalation snaps are judged on and
  applied to XZ only. The 50 m join and recovery teleport is unchanged.
- `DamageSyncManager` sends water level for every boat that has any. It used to send only the host's own, so a
  boat the host was not standing on kept its join-time water on guests.

Risks to watch: one visible change of the swell as a lobby opens; a swell jump at an origin shift would mean
the phase formula is wrong for some wave and is the reason to switch `SharedSea` off.

To confirm in a guest's verbose log: `POSITION_ERROR` with `y` of several metres should become rare, and
`HULL_STATE` should show `freeboard` and `hostFreeboard` close to each other. A host's log alone cannot show
this; the corrector and the sea sample run on the guest.

On 0.38.1 only, a boat that was not the host's `lastBoat` floated on `BoatAlignNormal` on the host and on
`BoatProbes` on the guest. 0.39 always uses probes.

## Report 3: camera about 0.3 m off its turning axis, after walking into things

Fixed in the Player Model working tree the same day (both candidates below), uncommitted. Neither mod writes the first-person camera chain (`observer` > `head (cam)` >
`OVRCameraRig` > `TrackingSpace` > `CenterEyeAnchor`). Two candidates:

1. The Player Model follow view (V since v0.1.5) pins the eye exactly 0.3 m behind the head when something
   is beside the camera path (`PlayerOrbitCamera`, the `clear < 0.3f` floor). Turning then orbits a 0.3 m
   circle. One question to a reporter settles it: was the crosshair visible and could you click things.
   Workaround: V, or `FollowCamera = false` in the Player Model config.
2. Vanilla `PlayerHeadSmoothing` copies its mirror's whole local position onto `head (cam)` and only ever
   rewrites the mirror's world height. Aboard a heeled boat the controller is tilted, so a height change
   (stepping up, vanilla's unstick pop) leaves sideways offset in the mirror and nothing zeroes it. A
   simulation gives 1 to 12 cm per event, so 0.3 m needs several.

Applied: for the first, `PlayerOrbitCamera` now puts the eye on the turning axis when there is under 0.3 m
of room, eased in up to 0.6 m, in place of the 0.3 m floor. For the second, `HeadSmoothingOnAxis`: a postfix on `PlayerHeadSmoothing.LateUpdate`, registered through `GuardedPatches.Apply`, that
zeroes `walkColMirror.localPosition` x and z and copies the result to the head, logging the first offset over
2 cm. The scene has both at x/z 0, so 0 is what the game intends.

## Report 4: Invites button on the title screen

Removed (`CoopMenu.Install`). The pause menu keeps its Invites button.

## First session checklist

- Buy a house as host and as guest; sign and door on both machines; rejoin.
- Store an item in the house, have the other player carry it to the deck, sail 1.2 km. It stays on deck for
  everyone. Repeat with a pocket.
- Set furniture against a wall in house 203 or 204 and compare its facing on the other machine.
- Leave the house past 1 km and return three times; count items on the guest.
- Open a house door and each cabin door on the Large Sanbuq while the other player watches.
- Both players in house beds, not tired: wake in the morning window. Host in the house and guest in a bunk,
  and the reverse.
- Set items down on deck next to a crewmate: no jerk, no boom.
- Sail in a swell with F8 on for the guest and send the verbose log.
