# Roadmap

The one place that says where things stand, for Sailwind Co-op and the Sailwind Player Model mod it
depends on. Update it at every release and whenever an item changes state. If this file and another
doc disagree, this file wins; the older plan docs keep their design detail but not their status.

Last updated: 2026-09-15.

## Released

| Mod | Version | Date | What |
|-----|---------|------|------|
| Co-op | v0.3.2 | 2026-08-09 | Guest-held items vanishing on the host |
| Player Model | v0.1.2 | 2026-09-14 | Character clone fix, undress guard, live sole offset, color options |

## Staged for release

**Co-op 0.4.0 was published and pulled on 2026-09-14.** Release, tag and wiki rows are gone and public main
is back on the v0.3.2 tree. The work is intact on the dev branch and still deployed locally.

Pulled because the held-tool pose was the wrong feature. The point of that work was to pose the body at
the ship's wheel, the winches and halyards, the bilge pump and on the mooring ropes. What shipped only
posed carryable items, only for crewmates, and drove the player model's code from co-op, so solo players
got nothing in the orbit camera. It does not go back out until the interaction poses below exist.

Also in 0.4.0 and ready: weather sync, market sync, crate firewood and ghost fixes, rod hook state, NPC
boats to 10 km, the player model split, colors on the wire, tools/pack.py.

Added to 0.4.0 since the pull, not yet tested: an Invites button on the pause and title menus (the solo
"Join (name)" button is gone; every invite is listed, one per sender, with Join and Not now), and
`SailwindCoop.CoopStatus`, a plain-typed read-only session view for other mods.

## Open bugs, blocked on logs from a real session

- Fishing: two fish from one catch. Guarded and logged, cause unknown.
- Grab collisions.
- Guest boat vertical divergence, median 5.7 m. Instrumented.

## Next up, in order

1. **Interaction poses (Player Model), for your own body first.** Built and tested solo on the dhow and brig
   (fifth pass, 2026-09-15), including modded items (Realistic Skies, Climate). The player model mod reads what the local player is doing and poses both arms: the
   ship's wheel, winches, the anchor winch, the bilge pump, sail pushers, mooring ropes, and every carried
   item by hold type. No networking; works solo in the orbit camera. Ships as a Player Model release.
2. **Interaction poses for crewmates (co-op).** Built, untested (needs a second player). Packet 226 says who
   is using which fixed object and feeds the same player model code; mooring rope holds feed it from the
   existing rope sync. Carried items, eating and drinking read through the held item's synced motion.
3. Re-release co-op 0.4.0 with both of the above, bundling that Player Model release.
4. **Sitting (Player Model, then co-op).** Decided 2026-09-14, part of the Sailwind Player Model mod with its own
   on/off switch, not a separate mod. Right-click a placed chair to sit (the game's beds already work this
   way); a sit key for any level surface at seat height (gunwales, deck edges, crates, benches, other mods'
   props), legs dangling over a drop, the key again swings the legs to the other side; any movement key
   stands you back where you sat from. Held items and free look work while seated. First person: the camera
   settles to seated height, and the body shows from the chest down (smooth chest fade needs a shader built
   in Unity 2019.1.10f1, else a dithered fallback); optional dim "(seated)" label. Lying pose in the game's
   beds. Co-op syncs the seated state from the player model. Built and tested solo: chairs, tables, bed edges,
   the floor, gunwales, beds, spars (bowsprit, crosstrees, straddled or sat on sideways), and the animated
   leg swing, floor poses (legs out, cross-legged, one knee up, knees hugged). In testing: docks, sitting on
   the edge you stand on (roofs, wall tops, dock ends), and the lit-stove easter egg (smoke, then your pants
   catch fire; rain slows it and steams), the world's tavern chairs and benches, and the first-person view of
   your own body from the chest down (a shader built with Unity 2019.1.10f1, embedded in the DLL). Still to do:
   a crewmate's view of all of it (needs a second player).
5. **Downed state (Player Model, then co-op).** First pass (capsule and fixed poses) tested 2026-09-15 and replaced
   the same day with a real ragdoll, not tested yet. `PlayerModel.GoDown(reason, impulse, seconds)` builds an
   eleven-part jointed ragdoll from your body as it stands and pushes it; the view rides its head, your bones copy
   it, and the arms cover the face, cover the head (mostly, when there is a drop to fall down), or fling out on long
   falls. No distance limit: it falls all the way down, a backstop catches it going through a deck, and landing in
   the sea hands you to the game's swimming. Getting up is animated from however you landed (face down: hands and
   knees, squat, stand; face up: sit up, squat, stand). Any fall that will last over a second in the air ragdolls
   on its own (setting RagdollLongFalls). Test key (TestKey, unbound by default) knocks you over from a random
   direction. Co-op sends the ragdoll's parts on the rest packet (227, states 3 downed and 4 getting up) up to 10
   times a second. A Player Model feature, not tied to Three Sheets. Later candidates: hard, fast hits, slipping.
   Tested good 2026-09-15 (limbs, reactions, get-up, mast, hatch, under way). Since then: ordinary jumps no longer
   ragdoll (only the way down counts, the game's gravity is measured, and the landing must be 2 m below takeoff);
   lying and getting-up time scale with how hard the body hit; the screen darkens at the impact with the game's
   vignette and a copy of its sleep stars, clearing within about 1.5 s of standing; in first person the view follows
   the fallen head and the mouse does nothing until you are up; landing in the sea floats the body 0.7 to 1.6 s
   before swimming.
6. Third person camera that follows the player (Player Model). Built and tested solo: the camera key cycles
   first person, following you, and (aboard) the game's ship view; works on land; walking turns you to the camera.
7. Throw and shove animation (Player Model). The T throw shoves the item out with both arms instead of the item
   leaving a still body. Short keyframed arm curve on top of the arm IK. The same shove plays on T with empty
   hands, which is also the key for shoving crewmates (below).
8. Swimming animation (Player Model, then co-op). Built 2026-09-15, not tested: treading water when still (sculling
   hands, pedaling legs), a crawl with a flutter kick when moving, diving the way you look under the surface.
   Crewmates show it too, worked out from where they are in the water, with nothing new sent.

## Planned, not scheduled

- Quartermaster. Host appoints one, default host, reverts on leave. Picks a split: shared, manual
  allowance, even split, percentage. Two ways in: per-player buckets behind the host wallet, or just
  the role plus a permission gate on big spends. Either way, first audit every spend and income path;
  the onsen bath (island 39, Emerald Archipelago) is a known unrouted spend, so a guest bathes free.
- Discord Rich Presence, as its own mod. Shows the boat and situation (moored, anchored, at sea) with no
  navigation info, and reads `CoopStatus` when co-op is installed for role and crew size. Being built in a
  separate session.
- HMS Leopard cannon fire. Cosmetic-only broadcast, same shape as the bell.
- Cold-launch invite auto-join. Wants its own release.
- Player Model on Thunderstore, so r2modman users stop importing by hand.
- Player Model: a config switch to force the shopkeeper as the clone source.
- Shoving crewmates (co-op easter egg). Any player can shove; the host can switch it off for the session in
  settings. T with empty hands shoves the crewmate in front of you, with the same animation as throwing; T while
  holding anything is always the game's own throw and never shoves. Hit hard enough or often enough in a short
  time, they fall over using the Player Model's downed state (Next up, item 5).

## Backburner

- Ship games: poker, liar's dice. Sitting exists now, so no longer blocked.
- Poses for other mods' controls: HMS Leopard gun carriages (push while running out), the Realistic Skies
  observatory telescope, a short reach-and-press for single-click controls (cannons, bell, ladders, the
  game's doors and hatches). Leopard oars wait on sitting.
- Onsen exploit fix on its own. Folds into the quartermaster spend audit.

## Not doing

- Multi-boat. Decided 2026-07-10: the mod is about crewing one ship together.
- Ghost markers and wind streaks in Rig Balance. Built, then removed by decision.
