# HMS Leopard + four-mod compatibility (v0.2.32)

Date: 2026-07-14
Status: approved, ready for planning
Supersedes nothing. Extends the Shipyard Expansion compat work shipped in v0.2.31
(see `2026-07-13-shipyard-expansion-compat-design.md`), which is the architectural template.

## Goal

Make the HMS Leopard playable as a first-class co-op ship, and make co-op safe alongside its
dependency stack.

In scope, six mods:

| Mod | GUID | Role for the Leopard |
|---|---|---|
| Shipyard Expansion | `com.nandbrew.shipyardexpansion` | required (already done, v0.2.31) |
| Sail Collision Fix | `com.nandbrew.sailcollisionfix` | required |
| HMS Leopard | `com.winter.leopard` | the ship itself |
| NAND Tweaks | `com.nandbrew.nandtweaks` | recommended |
| Deep Ports | `com.winter.deepports` | recommended |
| Towable Boats | `com.nandbrew.towableboats` | recommended (the Leopard's cutter is a real second boat) |

"First-class" means the ship's distinctive systems work in crew play: gunports and the flooding
they drive, the deployable cutter, the oars, the bell. Not just "the hull does not explode".

## Verified ground truth

Everything below was read from source, or from the shipped v1.4.0 asset bundle via UnityPy. It is
not inferred.

### The Leopard's two boats are ordinary boats to co-op

Both prefab roots (`BOAT LEOPARD (207)` and `BOAT CUTTER (212)`, instantiated with a `(Clone)`
suffix) carry `BoatRefs`, `SaveableObject`, `Rigidbody`, `BoatMooringRopes`, `BoatDamage`,
`BoatLocalItems`, `BoatMass`, `BoatKeel`, `BoatProbes`, `BoatHorizon`, `SaveableBoatCustomization`
and `PurchasableBoat`.

Consequences:

* `BoatUtility.FindAllBoats()` discovers a boat by "is in `SaveLoadManager.GetCurrentObjects()` and
  has `BoatRefs`". Both Leopard boats satisfy this. Existing hull, item, damage and customization
  sync applies unchanged.
* Names are deterministic and distinct, so the name-keyed wire protocol is safe. (The boat map is
  `boats[obj.gameObject.name] = obj`, a silent last-write-wins overwrite on collision, so this
  mattered.)
* The bundle contains **no custom `RopeController` subclass** (only `RopeControllerAnchor`; the sail
  rope controllers are created at runtime by the vanilla sail system). `BoatUtility.GetStableRopeKey`
  therefore does not need extending. This was a suspected blocker and it is not one.

### The cutter has no anchor

The bundle has exactly one `Anchor`, on the Leopard. The cutter's `BoatMooringRopes.anchor` is null.
Every vanilla boat has an anchor, so co-op has never met an anchorless boat.

### The Leopard ships its own towing cleats

Towable Boats only instantiates cleat prefabs for known vanilla hull indices (10, 20, 40, 50, 70, 80,
90). The Leopard is 207 and the cutter is 212, so that path skips them. But `TowingSet.AddCleats()`
first checks for a `towing set` child already on the prefab, and the Leopard bundle ships one:
`towing set` containing `towing_cleat_{left,right}_{front,mid 1,mid 2,back}` plus `towing rear`.
So Towable Boats works with the Leopard through the prefab-child path, and the cleat names are
deterministic, which makes them usable as a wire key.

### Towing is the vanilla mooring SpringJoint with a moving bollard

Towable Boats contains no joint creation, no `AddForce`, no `FixedUpdate`, no physics math. A tow is
the vanilla `PickupableBoatMooringRope.MoorTo` path: a `SpringJoint` living on the cleat GameObject
(a child of the *towing* boat) whose `connectedBody` is the *towed* boat's hull rigidbody. The
coupling is one-way: the cleat's rigidbody is kinematic, so Unity discards the reaction force and the
towing boat feels nothing.

Two facts that drive the design:

* `spring.spring = towedBoat.mass * 6f` is baked at `MoorTo` time and never recomputed. Boat mass is
  client-dependent: `BoatMass` adds 160 kg and shifts the centre of mass when the *local* player is
  standing on that boat. A guest replaying `MoorTo` derives a different spring constant than the host.
* `maxDistance = sqrt(GetCurrentDistanceSquared())` is derived from where the two hulls happen to be
  at the instant of the call, on that machine.

So the guest must never derive tow parameters. They must be replicated.

### Deep Ports fails silently

It swaps `Terrain.terrainData` **and `TerrainCollider.terrainData`** on Gold Rock, Fort (Aestrin) and
Dragon Cliffs, and moves two collider meshes at Dragon Cliffs. If its `deepports` asset bundle is
missing, it logs an error, does nothing, and the plugin still registers as loaded. A GUID-and-version
handshake would report "installed" on a peer sailing vanilla shoals.

### NAND Tweaks: two files in the repo are dead code

`LadderPatch.cs` and `StoreFoodPatches.cs` are entirely commented out. There is no ladder patch and no
crate-stacking patch in the shipped DLL, whatever the README says. Do not budget work for them.

## Pre-existing co-op defects this work surfaces

These are real defects in co-op today. Three of them are independent of the Leopard.

**P1. NAND Tweaks silently breaks bailing, right now.**
NAND Tweaks patches `BoatDamageWaterButton.OnItemClick` with a prefix that returns `false`
(`BoatDamagePatches.cs:54`). Co-op patches the same method at `Patches/DamagePatches.cs:187` with a
prefix that does not declare `bool __runOriginal`. Harmony skips co-op's prefix once another prefix
returns false, so `__state` defaults to `0f`, the postfix early-returns, and `SendBailRequest` is
never sent. A guest running both mods bails water locally while the host never hears about it, and the
next authoritative water sync snaps it back.

**P2. `BoatUtility.ClearCaches()` has zero call sites.**
`_cachedBoats` is built lazily on first use and lives for the whole process, across lobby leave and
rejoin. Any boat spawned after the first `FindAllBoats()` call is invisible to co-op forever. The
cutter is deployed mid-session, so this is mandatory, not cosmetic.

**P3. Anchorless boats are a null-reference crash.**
`BoatUtility.GetAnchor()` can return null; `BoatUtility.IsBoatAnchored()` calls `anchor.IsSet()`
directly on the result. The cutter is co-op's first anchorless boat.

**P4. Boats with nobody aboard are never streamed.**
`BoatSyncManager` streams `lastBoat` plus boats with a *remote crew member* standing on them, and the
guest prunes a boat's state after 10 s of silence. A deployed-but-empty cutter, or a towed boat,
drifts independently on every machine.

## Design

### 1. `Compat/CompatRegistry.cs`

`SECompat.ModSignature` is currently read at nine call sites in `Plugin.cs` plus
`SteamLobbyManager.cs:240`. That does not extend to six mods.

Introduce `Compat/CompatRegistry.cs` which composes one opaque token from per-mod modules in a fixed,
deterministic order, and repoint every one of those call sites at it. Each new mod gets a
`Compat/<X>Compat.cs` built to the `SECompat` template:

* soft `BepInDependency` on the GUID (attributes stack, so all six go on `Plugin`)
* `BepInEx.Bootstrap.Chainloader.PluginInfos` probe for presence and version
* assembly and member resolution by reflection, with an `AppDomain` fallback for load order
* reflection-only. Co-op must still build and run with none of these installed.
* fail closed: an unresolvable third-party internal disables that mod's data path but keeps
  `IsInstalled` true, so the handshake still refuses mismatched crews.
* `Init()` called from `Plugin.Awake` before Steam init, so the token is ready before any lobby
  exists.

The composed token stays **opaque**: compared with `==`, never parsed. This is the existing contract
and it is load-bearing (the token carries suffixes like `/noSync` precisely so those cases mismatch
and get refused).

The refusal messages in `Plugin.cs` are currently Shipyard-Expansion-specific prose. They must
generalise to name the actual mismatching mod, or a Deep Ports mismatch will tell the user it is a
Shipyard Expansion problem.

### 2. The parity gate (tiered)

**Hard parity.** Presence and version must match across the crew, or the join is refused:
HMS Leopard, Sail Collision Fix, Deep Ports, Towable Boats, and Shipyard Expansion (as today).

Justification per mod, so this is not cargo-culted:

* *Leopard* spawns two boats into the world unconditionally. A peer without it has a world missing two
  boats and cannot resolve their names.
* *Sail Collision Fix* changes which sails may be installed and widens `colAngleMin`/`colAngleMax`, so
  the same sail at the same input reaches a different angle and produces different thrust. Note its
  `Ignore sail collision` option **defaults to true**, so a user who installs the DLL and never opens
  the config already diverges from a vanilla peer.
* *Deep Ports* replaces collision heightfields.
* *Towable Boats* adds cleat GameObjects to boat hulls, changing the hierarchy that path-keyed sync
  depends on, and changes which boats run full `BoatProbes` physics.

**Deep Ports carries a bundle hash** in its token, in addition to presence and version, because of the
silent-failure mode above. Hash the `deepports` bundle file with SHA-256 and take the first 8 hex
characters (collision risk is irrelevant here; this guards against accident, not attack). Compute it
once in `Init()`, never per-join, since the file is ~tens of MB. If the bundle is absent, the token
records that explicitly (for example `DP=0.3/nobundle`) so an installed-but-broken peer mismatches a
working one rather than passing as equal. This also catches a peer running a different-vintage bundle
under the same version number, which version alone cannot.

**Sail Collision Fix carries its three config bools** (`ignoreSailsCollision`, `ignoreObstructed`,
`ignoreAngleLimits`) in its token.

**Towable Boats carries `smallBoats`** in its token. It decides whether cog, dhow and kakam get a
`towing set` child at all, so a mismatch means the peers have structurally different boat hierarchies.
It is applied at boat `Awake` and cannot be reconciled at runtime.

It does **not** carry `performanceMode`; see section 5.

**NAND Tweaks is gated on behaviour, not presence.** Its token is a normalised vector of exactly the
six simulation-affecting options, where a peer *without* the mod has the vanilla vector:

| Option | Default | Why it is simulation-affecting |
|---|---|---|
| `Bailing Tweaks` | true | replaces the bail routine, writes `BoatDamage.waterLevel` directly |
| `Drunken Sleep` | true | drains `PlayerNeeds.sleep` scaled by `Sun.sun.timescale`; changes how much world time a sleep consumes |
| `Wheel centering` | false | writes `GPButtonSteeringWheel.currentInput` inside `ExtraFixedUpdate` |
| `Albacore Area` | true | injects a `LocalFishesRegion` and a fish prefab into `OceanFishes` |
| `Save and load ship state` | true | writes `modData`, restores `Rigidbody.velocity`, sail reef/angle and wheel state on load |
| `Include doors` | true | fires `GPButtonTrapdoor.OnActivate()` on load |

Everything else in NAND Tweaks (outlines, camera, UI width, decals, box labels, water text, save
thumbnails, keybinds, item hold distance, chip-log readout, mission text, mooring colour, tavern and
port-office interior triggers) is cosmetic and stays free per player.

Normalising against vanilla is what buys the friendliness: a crewmate with the mod and every sim
option off matches a crewmate without the mod at all.

Be clear-eyed about what this does **not** buy, because it is counterintuitive. NAND Tweaks' defaults
are *not* the vanilla vector (four of the six default to true). So "host has NAND Tweaks at defaults,
guest has no NAND Tweaks" is a genuine simulation difference and **will be refused**. That is correct
and honest: those peers really would bail water, sleep and spawn fish differently. The tiered gate buys
two specific things, and only these two: cosmetic settings may differ freely within a crew that all run
the mod, and a peer who deliberately turns every sim option off can sail with a vanilla peer. The
refusal message must therefore say *which* sim options differ, or users will read the refusal as a bug.

`AllowVersionMismatch` currently bypasses both the version gate and the SE mod gate with one flag. With
six mods that is too blunt. Split it: keep `AllowVersionMismatch` for co-op's own version, add
`AllowModMismatch` for the compat token. Both default false.

### 3. Fix the pre-existing defects

* **P1**: add `bool __runOriginal` to co-op's `BoatDamageWaterButton.OnItemClick` prefix, and an
  explicit `HarmonyPriority` so ordering is not left to chance. `__runOriginal` is the robust fix
  because Harmony patch order between two third-party assemblies is not guaranteed by load order.
* **P2**: wire up `BoatUtility.ClearCaches()`. Invalidate on lobby join, on lobby leave, and whenever a
  compat module spawns or despawns a boat.
* **P3**: null-guard `IsBoatAnchored()` and `GetAnchorLength()` for boats with no anchor. Return "not
  anchored" and length 0.
* **P4**: let compat modules register a boat as "always stream". Register the deployed cutter, and any
  boat in a tow chain. This keeps the change scoped rather than rewriting the streaming heuristic.

### 4. The Leopard

**Trapdoors, generic.** New `TrapdoorState` packet keyed by `(boatName, relative transform path from
the boat root)`, carrying **absolute open/closed state**, not a click to replay. Mod parity guarantees
identical hierarchies on every peer, so the path is a safe key.

This is deliberately generic. Co-op syncs no `GoPointerButton` of any kind today, so doors, hatches
and gratings desync on *vanilla* boats right now. The Leopard has 60 `GPButtonTrapdoor`s. One
mechanism fixes both and is reusable for the next modded ship.

Apply rule: if local `IsOpen()` differs from the desired state, invoke `OnActivate()` once. Never
replay clicks.

**Gunports need a Leopard-aware adapter on top**, because Leopard's `GPButtonTrapdoor.OnActivate`
prefix does two hostile things: one click fans out to every sibling port in the group, and the water
masks and the nine overflow emitters are toggled with `!activeSelf`.

* Host side: emit **one group-level packet** per player action, not one per sibling. Detect the fan-out
  via Leopard's own `Gunports.recursive` static (read by reflection) so co-op's postfix does not fire N
  times.
* Guest side: apply by invoking `OnActivate` on a **single** port of the group, reproducing the host's
  exact code path including the mask toggles. Then **force** the masks and the nine overflow emitters
  to the absolute state implied by the group's open flag. This is the part that matters: without it, a
  guest whose mask state has drifted for any reason will invert its flooding on the next toggle, and
  `Gunports.recursive` is a plain static that offers no protection against network replay.

Accepted wrinkle: Leopard's gunport code calls `AudioMixers.instance` indoor/outdoor snapshot
transitions, so a remote player's gunport toggle briefly touches the local player's audio. Co-op
re-asserts the local player's correct snapshot after applying a remote gunport change. Not worth
fighting harder than that.

**Cutter.** New `CutterState` packet, `{ active, position, rotation }`, host-authoritative.

* Guest click on `CutterController` / `CutterRopeController` is denied locally and sent as intent.
  Guests must not run the gates: the deploy gate reads the Leopard's rigidbody velocity (which on a
  guest is host-driven and interpolated), and the recover gate reads live item-container child count.
* Host runs both gates, does the `SetPositionAndRotation`, sets `Patches.cutterActive` by reflection,
  and broadcasts.
* Guests apply `SetActive`, the transform, the rowboat-prop / rowboat-rope visibility flip, and
  `cutterActive`; then invalidate the boat cache (P2) so the cutter enters the boat map.
* Host sends current cutter state on join.

Note `Patches.cutterActive` is the only thing the Leopard persists to `modData`, and co-op does **not**
transfer `modData` on join (the guest's comes from its own phantom save). So the join-time
`CutterState` send is not optional; without it host and guest disagree from the first frame.

**Oars.** `OarController.ExtraLateUpdate` applies `AddForce`/`AddTorque` to the cutter's rigidbody
straight from `GameInput.GetKey`. This cannot be left client-side.

* Guest forwards held-key bits (`MoveUp`/`MoveDown`/`MoveLeft`/`MoveRight`) as an `OarInput` packet.
* Host applies the forces. The resulting motion rides the existing boat transform stream, which is why
  P4 is a prerequisite.
* Guest's local force path is suppressed (prefix returns false when not host); the oar animation still
  plays locally so rowing feels responsive.
* The mod lets two players `StickyClick` the same oars and both push. Oars get an ownership lease,
  modelled on the existing helm lease.

**Errata (implementation, 2026-07-14):** the shipped design keeps the rower's LOCAL prediction
(the mod's ExtraLateUpdate runs unchanged - suppressing only the force would need a transpiler on
third-party IL) and forwards held-key bits; the host applies the authoritative force and observers
animate. No lease: multiple rowers add force, which matches what the unmodified mod's physics does
with any one machine's input. Reconciliation rides the existing boat-transform correction, exactly
like local wind/buoyancy forces.

**Bell.** One-shot `BellRing` audio broadcast. Trivial, and crews use it.

**Shipyard discharge.** Leopard prefixes `Shipyard.DischargeShip` and mutates the shared
`ship release pos` transform based on `GameState.currentShipyard`, which is per-client. Co-op already
prefixes the same method. Ensure discharge stays host-routed so only the host's prefix moves the
release point; verify guests do not run it.

**Anchor.** Leopard's `Anchor.RegisterRopeController` postfix multiplies `unsetResistance` by 0.1 for
**every anchor in the game**, not just its own. Hard parity covers this. Its `Anchor.OnLoad` prefix
(which returns false for `leopard anchor`) does not collide with co-op, because co-op's
`ApplyAnchorState` never calls `OnLoad`: it sets `currentLength` and reflectively invokes
`SetAnchor`/`ReleaseAnchor`.

### 5. Towable Boats

**`MooringState` changes shape.** It currently carries a world-space dock position. With tow cleats the
bollard is on a moving rigidbody, so a world position is wrong the instant the towing boat moves.

New payload: a target *reference*.

* `targetKind`: `Dock` or `BoatCleat`
* dock case: the existing dock position
* boat case: `(towBoatName, cleatPath)`, resolvable because `TowingSet` renames the container to
  exactly `towing set` (no `(Clone)` suffix) and the Leopard's cleat names are baked in the prefab
* plus, in both cases, `spring.maxDistance` and `currentRopeLengthSquared`, replicated explicitly

Guests must not recompute the spring parameters, for the mass and position reasons given in the ground
truth section.

**Mooring becomes host-authoritative**, including the `OnTriggerEnter` auto-moor path. That path moors
an unheld rope to *any* `GPButtonDockMooring` collider it touches, and `TowingCleat` is a subclass of
it, so a loose rope brushing a passing boat spontaneously creates a tow on whichever peers happen to
run the trigger. Suppress it on guests.

It is also the mechanism by which tows survive a save/load, via a trigger overlap at load time, which
is order-dependent and non-deterministic. Do not rely on save round-trip to sync tows: the host
enumerates the tow graph after load and pushes it explicitly.

**`UnmoorAllRopes` widens.** Towable Boats postfixes it so that unmooring boat A also unmoors foreign
ropes tied to A's cleats, changing boat *B*'s state. Vanilla calls this during `SaveableObject.Load`.
Any co-op call must now broadcast `MooringState` for both boats.

**Neutralise `BoatPerformanceSwitcher` on guests** for host-authoritative hulls. `TowingSet.Physics` is
derived from `GameState.lastBoat`, which is different on every client, so even with identical mods and
identical config the host and guests disagree about which boats run full `BoatProbes` physics. Under
host authority that is tolerable, but it means guest local integration fights the incoming transforms
with varying strength. Suppressing the switcher on guests for remote hulls makes Towable Boats'
`performanceMode` config irrelevant to guests, which is why it stays out of the parity token.

**Sleep.** Towable Boats postfixes `Sleep.CurrentBoatIsMoored` to return false when the current boat is
under tow, flipping sleep from instant timeskip to real-time timewarp so the tow keeps working. It also
keeps tow-chain boats non-kinematic during `GameState.sleeping`. Both interact with co-op's sleep sync.
Parity guarantees the mod is present on all peers and the tow graph is synced, so the branch should
converge, but the host's decision must drive. Flag for live test.

### 6. Wire and version

New packets, appended (current max in use is 215; the enum is a byte, ceiling 255):

| ID | Packet | Direction |
|---|---|---|
| 216 | `TrapdoorState` | host authoritative, star-relayed |
| 217 | `CutterState` | host authoritative |
| 218 | `OarInput` | guest to host |
| 219 | `BellRing` | broadcast |

There is deliberately **no separate tow-graph packet**. `TowingSet.towedBy` and `TowingSet.towedBoats`
are both derived from `TowingCleat.towed`, which is itself derived purely from `MoorTo` / `Unmoor`. So
replaying per-rope mooring state reconstructs the entire tow graph, and a dedicated `TowState` would be
a second source of truth for the same facts. The host pushes the graph after load simply by sending
`MooringState` for every rope.

`MooringState` is **reshaped** (target reference instead of a world dock position). This is a **wire
change**, so all crew must update; the existing version handshake refuses mismatched joins
automatically.

Note that the same reshape must be applied in **two** places, not one: the live `MooringState` packet
*and* the `MooringRopes` field inside `NetworkBoatData`, which is the join snapshot. Changing only the
live packet would leave a joining guest reconstructing tows from stale world-space dock positions,
which is exactly the bug this section exists to prevent.

Ships as **v0.2.32**.

### 7. Known risk, to be settled by playtest not by reading

Deep Ports and HMS Leopard both prefix `FloatingOriginManager.Start`. Deep Ports replaces Gold Rock's
terrain data; the Leopard retags Gold Rock's `terrain_fix` child to `"Terrain"` so anchors can raycast
it. They act on different objects and should not conflict, but neither mod declares a Harmony priority,
so their relative order is not guaranteed by anything. This cannot be proven from source. It is a live
playtest item.

## Implementation phasing

This ships as one release (v0.2.32), but it must be *built* in dependency order, because later phases
are load-bearing on earlier ones. Each phase should be independently buildable and reviewable.

1. **Pre-existing defects (P1-P4).** No new mods, no wire change. P2 (cache invalidation) and P4
   (always-stream registration) are prerequisites for the cutter; P3 (null anchor) is a prerequisite
   for the cutter existing at all. P1 stands alone and is the one live bug users are hitting today.
2. **`CompatRegistry` + the six compat modules + the tiered parity gate.** Reflection-only, no
   gameplay behaviour, no wire change beyond the token's contents. Splitting `AllowVersionMismatch`
   into `AllowVersionMismatch` + `AllowModMismatch` lands here.
3. **Generic `TrapdoorState` sync** (packet 216), with vanilla boats as the test surface. This is the
   largest blast radius in the whole change, since it touches shared `GoPointerButton` behaviour in a
   heavily playtested codebase. It must be reviewable on its own.
4. **The Leopard adapter**: gunport group semantics on top of phase 3, plus `CutterState` (217),
   `OarInput` (218) and `BellRing` (219).
5. **Towable Boats**: the `MooringState` reshape (live packet *and* join snapshot), host-authoritative
   mooring including the trigger path, and the guest-side `BoatPerformanceSwitcher` suppression.

Phases 1 and 2 carry no wire change. The wire break is introduced in phase 4 (new packets) and phase 5
(the reshape), so if the release has to be cut short, phases 1-3 are shippable on their own.

## Testing

Per the project's established practice, this ships with a live playtest checklist rather than automated
tests (there is no test harness for a Unity BepInEx plugin in this repo).

Checklist must cover, at minimum:

* join refusal in both directions for each of the five hard-parity mods, and for a NAND Tweaks
  sim-vector mismatch
* join *acceptance* with a NAND Tweaks cosmetic-only difference (the point of the tiered gate)
* Deep Ports bundle-hash mismatch refused; Deep Ports installed-but-bundle-missing refused
* bailing on the Leopard with NAND Tweaks installed (P1)
* gunport open/close from host and from guest, checking guest flooding masks do not invert after
  repeated toggles
* cutter deploy and recover from host and from guest, including deploy-while-moving (gate must be
  refused identically on both)
* rowing the cutter as a guest
* towing the cutter behind the Leopard, including rope-length change under tow, and unmoor
* save, quit, rejoin with the cutter deployed and under tow
* Gold Rock approach with Deep Ports, checking host and guest agree on grounding
