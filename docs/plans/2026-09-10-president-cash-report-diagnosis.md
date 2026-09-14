# President Cash report, 2026-09-10 (v0.3.2, no logs)

Four items reported over Discord across two sittings, with no logs attached. The reporter is the
HOST (they see storm visuals their crew does not, which is host-only behavior, see section 1).
Everything below is static analysis against the v0.3.2 tree plus `decomp_sw/`. Nothing here has been
reproduced locally.

Verbatim report is at the bottom of this file.

| # | Item | State |
|---|------|-------|
| 1 | Fog and storms do not sync | FIXED in code 2026-09-11 (1a, 1b, 1c, plus storm origin frame), deployed, NEEDS PLAYTEST |
| 2 | Cutting a crate open leaves the firewood invisible | FIXED in code 2026-09-11 (stale crate-window button), deployed, NEEDS PLAYTEST |
| 3 | Fishing drops two fish, one real | NOT DIAGNOSED; H1 closed and H2 now logged (2026-09-11), still needs logs |
| 4 | Grabbing collisions off for the crew | NOT CONFIRMED; one concrete crate-insert bug found and fixed (2026-09-11), may or may not be it |

Items 3 and 4 should not be guessed at. Ask for `SailwindCoop-verbose.log` (F8 pressed) plus
`LogOutput.log` from BOTH machines first. The 2026-08-05 round is the precedent: the guest log alone
supported two different wrong root causes and the host log settled it in one line.

---

## 1. Fog and storms do not sync. ROOT CAUSE FOUND, FIXED IN CODE.

Reported twice. Second time: "I could see a storm approaching and they could not, it would just start
raining and getting choppy. While the waves didn't sync fully, the choppiness sort of did."

Two separate causes. Both need fixing, neither fixes the other.

**FIXED 2026-09-11, not yet playtested.** 1a: `WeatherStatePacket.RegionName` carries the host's target
region on every 2Hz broadcast and in the join snapshot; the guest adopts it only while within 2km of the
host avatar (region is location-dependent, so a split crew keeps its own). 1b: the guest now runs the
emission block of `WanderingStorm.Update` on the storm's own timer; movement stays host-only. 1c:
`ActiveStormMask` mirrors the full active set. Found while fixing: storm positions were sent in the host's
floating-origin frame, so they now travel as real coords. WIRE CHANGE (two fields). What to look for in the
next report's LogOutput: `[WEATHER] Region set to the host's` on each guest shortly after join, and no
`Guest storm emission sync disabled` line.

### 1a. `HostRegionName` is collected, serialized, deserialized, and never applied

`BoatStateCollector.cs:107`:

```csharp
var currentRegion = RegionBlender.instance != null
    ? Traverse.Create(RegionBlender.instance).Field("currentTargetRegion").GetValue<Region>()
    : null;
var hostRegion = currentRegion?.gameObject.name ?? "";
```

`grep -rn "HostRegion" --include=*.cs src/` returns exactly four hits: the field declaration
(`BoatPackets.cs:170`, commented "BUG-018 fix: Host's current region name for forcing RegionBlender
switch"), the write (`PacketSerializer.cs:166`), the read (`PacketSerializer.cs:213`), and the
assignment above. **Nothing consumes the value.** The BUG-018 fix was half-landed: the wire half
exists, the apply half was never written.

Why that produces the reported symptom. `Weather.currentRegion` is the source of the four WeatherSets
(`clearWeather` / `cloudyWeather` / `rainWeather` / `stormWeather`), plus `stormRange` and
`stormCount`. Fog density rides on those presets:

```
Weather.UpdateWeather()  ->  OceanColorBlender.BlendPalettes(one, two, lerp)
                         ->  finalMix.fogDensity = prefinalMix.fogDensity
Weather.ApplyWeather()   ->  OceanColorBlender.instance.ApplyPalette(finalPalette)
                         ->  RenderSettings.fogDensity = palette.fogDensity   (OceanColorBlender.cs:80)
```

So "foggy storm" versus "plain rain" is two machines running different region presets, which is the
reporter's description almost word for word.

`RegionBlender.SwitchRegion` is reachable only from `Region.OnTriggerEnter` on a collider tagged
"Player". A guest who joins by teleport into the middle of a region and then sails without crossing a
boundary keeps whatever `currentTargetRegion` their own save left them with.

It does not self-heal either. `RegionBlender.UpdateBlend`:

```csharp
float value = Vector3.Distance(player.position, currentTargetRegion.transform.position);
float num = Mathf.InverseLerp(45000f, 43000f, value);
```

If `currentTargetRegion` is a wrong, distant region, `num` is 0 and `blendedRegion` never converges
toward anything. The guest is frozen on the wrong presets, not slowly drifting toward the right ones.

Second problem with the same field: it lives only in `BoatWorldStatePacket`, the join snapshot. Even
once the apply side exists there is no ongoing correction if a guest's region diverges mid-session.

**Fix shape.** Apply the name on the guest by resolving the `Region` by GameObject name and calling
`RegionBlender.instance.SwitchRegion(region)`. Then add the region to the periodic weather broadcast
so it corrects continuously, not just at join. Guard against a name that resolves to null.

### 1b. Blocking `WanderingStorm.Update` also kills storm particle emission on guests

`WeatherPatches.WanderingStormUpdatePatch` prefix returns `Plugin.IsHost`, skipping the whole method
on guests. The stated intent is that storms should not drift independently, which is correct. But
vanilla `WanderingStorm.Update` does more than move:

```csharp
base.transform.Translate(Wind.currentWind.normalized * Time.deltaTime * 12f);
ApplyTotemAttraction();
if (timer <= 0f)
{
    timer = 1f;
    float num = Vector3.Distance(base.transform.position, Camera.main.transform.position);
    if (num > 12000f) UpdateActiveInRegion();
    if (num > 44000f) { /* reposition */ }
    if (num <= particlesDistance && active) SetEmission(state: true);
    else SetEmission(state: false);
}
```

`SetEmission` toggles `bottomParticles.emission.enabled`. Guests never call it, so the visible storm
column stays at whatever the prefab shipped with, forever. That is the "I could see a storm
approaching and they could not" half of the report, independent of the fog half.

**Fix shape.** Keep the movement host-authoritative and let the rest run everywhere. Either patch a
narrower target than `Update`, or run the distance and emission block on guests inside the existing
prefix before returning false.

### 1c. Related, lower priority: guest active-storm state is lossy

`WeatherSyncManager.CollectStormPositions` sets `activeStormIndex` only for the storm that is both
`currentStorm` and `.active`, and `ApplyStormPositions` then does:

```csharp
storm.active = (i == activeStormIndex);
```

Vanilla allows several storms active at once (`UpdateActiveInRegion` sets
`active = currentRegion.stormCount >= stormPriority`). Collapsing to one is mostly harmless because
`FindClosestStorm` picks the nearest anyway, but when `activeStormIndex` is -1 the guest deactivates
every storm, `FindClosestStorm` leaves `currentStormDistance` at its 1e8 sentinel, and `ApplyStorm`
snaps to clear weather while the host is in a storm.

### 1d. What DOES sync, and why the crew still got rain and some chop

Worth writing down so nobody re-investigates it. `Weather.Update` is not patched, so `ApplyParticles()`
runs on every machine and rain emission follows `finalParticles.rainDensity`. `WeatherSyncManager`
already syncs `Wind.currentWind`, storm positions, `WavesInertia` via `LoadInertia`, Crest `OceanTime`
(slewed, never snapped), and the `OceanUpdaterCrest` crossfade inputs. The guest's own
`DCTInertiaUpdate` deliberately recomputes the final weights locally so per-player `distanceToLand`
and `eyesFullyClosed` damping survives. That deliberate local recompute is a good candidate for why
the reporter got "the waves didn't sync fully, the choppiness sort of did", and it is not obviously a
bug.

---

## 2. Cutting a crate open leaves the firewood invisible. MECHANISM PROVEN, FIXED IN CODE.

**FIXED 2026-09-11, not yet playtested.** Went with the stale-button path below. `OnRemoteItemCrateInsert`
and `OnRemoteItemCrateRemove` now call `RefreshButtons()` when this machine's crate window is showing that
crate, and a new `CrateInventoryButton.PutItemBack` prefix refuses to hide any item that is no longer in
`containedItems` (it drops the stale reference instead). Multiplayer only. The "walk children" change
suggested at the end of this section was NOT made: vanilla's own `DropItem` is root-only too, so walking
children on every remote drop would overwrite prefab child layers (`ShipItemSubcollider` and friends) on
items that never touched a crate. What to look for: verbose lines `Refreshed the open crate window after a
remote change` and `crate window close: skipped hiding`. If the symptom recurs with neither present, check
for the weak candidate's `item or crate not found` warning.

Reported as "cutting boxes into wood makes the wood invisible until I pick it up and set it down
once".

Translation: unsealing a "crate of firewood". `sharedassets1.assets` carries the strings
`108 crate of firewood` and `firewood`, and the cut action is `CrateSealUI.Activate`, patched at
`ItemPatches.cs:498`.

### The invisible state is layer 26, `ItemInCrate`

Layer table recovered from `Sailwind_Data/globalgamemanagers`. The printable-string scan drops Unity's
three empty default slots (3, 6, 7); reinserting them gives a table confirmed against three
independent anchors in vanilla code:

| Layer | Name | Anchor |
|---|---|---|
| 8 | WalkCols | `ShipItemHammer.RaycastHitsFloor` treats layer 8 as "floor" |
| 16 | invis | `FishingRodFish.cs:138` checks `gameObject.layer != 16` |
| 26 | ItemInCrate | `CrateInventory.InsertItem` sets layer 26 |

Layer 26 is culled from the main camera. That is the mechanism by which crate contents hide while
still existing in the world.

Two facts make the report's exact wording fit:

1. `GoPointer.PickUpItem` sets root layer 2 and `DropItem` sets root layer 0. One pickup and one set
   down is precisely what restores a layer-26 item, which is what the reporter found by accident.
2. `GoPointer`'s raycast mask is `-604165`, which excludes layers 2, 11, 12, 13, 16 and 19. Layer 26
   is not excluded, so the firewood stays clickable while invisible. That is why they could pick up
   something they could not see.

### The mod only ever restores the ROOT layer

Vanilla re-layers the entire hierarchy in all four crate transitions (`CrateInventory.InsertItem`,
`CrateInventory.WithdrawItem`, `CrateInventoryButton.ShowItem`, `CrateInventoryButton.PutItemBack`),
each using `GetComponentsInChildren<Transform>(includeInactive: true)`.

Every restore in the mod is root-only:

- `ItemSyncManager.cs:711` (ItemResync): `go.layer = 0;`
- `ItemSyncManager.cs:2693` (remote drop): `item.gameObject.layer = 0;`
- `ItemSyncManager.cs:3384` (`RearmRemoteHeldItemPhysics`): `item.gameObject.layer = 0;`

Because a local pickup plus drop does fix it for the reporter, and those touch only the root, the
firewood's renderer is effectively on the root object. So root layer 26 alone is the invisible state,
and the child mismatch is a latent second bug rather than this one.

### Trigger path, still open

Strongest candidate: `OnRemoteItemCrateInsert` and `OnRemoteItemCrateRemove` mutate `containedItems`
without ever calling `CrateInventoryUI.RefreshButtons()`. If the local player has that crate's UI open
when a remote withdraw lands, a `CrateInventoryButton` keeps a stale `currentItem`. On the next
`HideInventory`, `PutItemBack` runs on that stale reference:

```csharp
public void PutItemBack(CrateInventory crate)
{
    if ((bool)currentItem)
    {
        currentItem.gameObject.layer = 26;
        currentItem.transform.position = crate.transform.position;
        ...
```

which sets a live item that is out in the world (or in somebody's hands) to layer 26 and teleports it
into the crate. That is the reported symptom, and it explains why it is intermittent rather than every
time.

Weaker candidate: `OnRemoteItemCrateRemove` hitting its "item or crate not found" early return, which
leaves the item at 26 and still in `containedItems` on that machine. Weaker because a subsequent
remote drop sets the root to 0 and would fix it on its own.

**Log lines that separate them:** `OnRemoteItemCrateRemove: item or crate not found` and
`OnRemoteItemCrateInsert: item or crate not found`. Present means the weak candidate is live. Absent,
with the symptom still occurring, points at the stale-button path.

**Fix shape either way.** Call `RefreshButtons()` from both remote crate handlers when
`CrateInventoryUI.instance.showingUI` and its `currentCrate` is that crate, and make `PutItemBack`
unreachable for an item that is no longer in `containedItems`. Separately, make the three root-only
layer restores walk children, to match what vanilla does.

---

## 3. Fishing drops two fish, one real. NOT DIAGNOSED.

**2026-09-11:** H1 is closed with a remote-owner-only gate (an unclaimed rod still collects) that logs
`[FISHING] Blocked a fish collect` if it ever fires. H2 now logs `[FISHING] FishCollectResponse for rod N:
rod not found` instead of returning silently. Neither changes behavior on the known-good path. If the report
recurs and neither line appears, both hypotheses are dead and the verbose log is the next step.

Reported as "drops 2 fish for them and 1 of them is real". Reporter is the host, so the doubling is
seen by the crew.

### Ruled out

- **Duplicate `ItemSpawned` for one id.** `OnRemoteItemSpawned` dedups on `FindItemByInstanceId` and
  falls back to `FindInactiveItemByInstanceId` for inventory-stashed copies.
- **Independent id collision.** `SaveablePrefab.AssignRandomInstanceId` is
  `Random.Range(1, int.MaxValue)` against an existing-id list, so two machines minting the same id is
  not plausible.
- **A second host-side spawn from vanilla `FishingRodFish.CollectFish`.** `OnCollectFishPrefix`
  returns false on both roles with `__result = null`.
- **The vanilla `ShipItemFishingRod.CollectFish` coroutine running as well.** `OnRodAltActivatePrefix`
  replaces it, and its `collectEntered` latch blocks re-entry on a throw.
- **Non-owner bite or escape broadcasts.** `OnCatchFishPostfix` and `OnReleaseFishPostfix` both check
  `__runOriginal` and re-check ownership.

### Two live hypotheses

**H1, the missing owner gate.** `OnCollectFishPrefix` calls `OnLocalFishCollect` with no
`IsLocalPlayerOwner` check, unlike every other fishing patch in the file, which all have one and
several of which call it out as defense in depth. I could not construct a path where a non-owner
reaches it, since `OnAltActivate` fires only for the local holder, but the asymmetry is real and the
gate is nearly free to add.

**H2, the phantom is not an item at all.** `OnFishCollectResponseReceived` early-returns on
`if (rod == null)` before it clears the rod's fish visuals:

```csharp
var rod = FindRodByInstanceId(packet.RodInstanceId);
if (rod == null) return;
// never reached: MeshFilter.sharedMesh = null; Renderer.enabled = false; currentFish = null;
```

A failed rod lookup on a viewer leaves a fish mesh hanging on the line while the real item drops
nearby. Two fish, one of them not real, which matches the wording better than a duplicate item does.

**Log lines that separate them:** count `FishCollectResponse` receives against `ItemSpawned` receives
per catch, and check for a `FishCollectResponse` with no following `FishCollect applied` (H2 would
show the gap).

---

## 4. Grabbing collisions off for the crew. NOT DIAGNOSED.

**2026-09-11, concrete bug found while tracing this, FIXED, not confirmed as the reported one.** Putting an
item INTO a crate from the crate window runs vanilla `InsertItem` then `DropItem`, and `OnLocalDrop` had
no skip for that (it skips inventory stows, hangs and destroyed items). Every insert also broadcast an
`ItemDropped`, which on receivers re-armed the item at the hand pose, colliders and `ItemRigidbody` live,
root layer 0, while it stayed in `containedItems`. The result was a visible shrunk item that
`CrateInventory.LateUpdate` snapped back onto the crate every frame, so grabbing it fought the crate. Fixed
by skipping the send when `currentCrateId != 0` and making `OnRemoteItemCrateInsert` release the item the
way `OnRemoteItemHung` does (clear tracking, release freeze, rearm, then insert).

Also ruled out on 2026-09-11: an item left ungrabbable by a drop path that forgets to re-enable its own
trigger. Every `_remoteHeldItems` removal is a drop, hang or disconnect (all restore) or a destroy (nothing
to restore).

Reported as "Grabbing collisions seem off for them sometimes".

### Where the game gates this

`GoPointer.cs:357` decides whether an item can be set down:

```csharp
if (heldItem.colChecker.collisions <= 0 || heldItem.colChecker.allowObstructedDropping || GameInput.GetKeyUp(InputName.Throw))
```

A stuck `collisions > 0` means the item refuses to be placed. `GoPointer.cs:293` and
`PickupableItemCollisionChecker.Update` drive the red outline off the same counter. Normally the
counter self-heals every frame:

```csharp
if (!item.held || item.GetCurrentInventorySlot() > -1) { collisions = 0; }
else { UpdateDecolDistance(); allowObstructedDropping = currentDecolDistance < 0.06f; }
```

### The lead

`DisarmRemoteHeldItemPhysics` fakes the held reference on every other machine:

```csharp
item.held = Object.FindObjectOfType<GoPointer>();
```

`item.held` non-null pushes the checker into the else-branch, so the per-frame `collisions = 0` reset
stops running for as long as anyone else is holding that item, while the mod teleports it every frame
with its colliders disabled. Any counter drift across that window has nothing left to clear it.

The same fake reference has a second consequence worth checking: `item.enableRedOutline` is computed
from the LOCAL player's `GameInput.GetKey(InputName.PickUp)`, so holding the pickup key can red-outline
an item in a crewmate's hands.

### Ruled out

- **Remote avatars counting as obstructions.** `PickupableItemCollisionChecker.OnTriggerEnter` counts
  any non-trigger collider not tagged "Boat", and the cloned bodies sit on layer 0, so this looked
  strong. It is dead: `TryAttachBody` destroys every `Collider` and every `Rigidbody` on the clone,
  and the capsule placeholder's collider is destroyed at construction.

### Related, already in the tree

The v0.2.38 `OnRodAltActivatePrefix` deliberately skips vanilla's `ShipItemFishingRod.CollectFish`
coroutine. That coroutine's only remaining work after the NRE was resetting the collected fish's
`PickupableItemCollisionChecker.collisions` to 0. The comment argues the pre-existing NRE already
prevented that reset, which is true, but it does mean a freshly collected fish can carry a nonzero
counter. That is the fishing-shaped instance of this same symptom and may be why items 3 and 4 were
reported together.

---

## Suggested order

1. Item 1 (both causes). Diagnosed, self-contained, no wire change needed for 1b and only an apply
   path plus a periodic field for 1a.
2. Item 2's `RefreshButtons` guard and the three root-only layer restores. Mechanism is proven even
   though the trigger is not.
3. Items 3 and 4 only once logs land.

Before cutting the release, run `/code-review ultra` on the branch. v0.3.2 exists only because
v0.3.1's chart-ghost stand-down overreached, and item 2 touches the same item-visibility surface.

---

## Verbatim report

Sitting one:

> President Cash - 18:14
> The mods amazing and pretty polished except fishing seems like its desyncing(drops 2 fish for them
> and 1 of them is real) Grabbing collisions seem off for them sometimes,  and cutting boxes into
> wood makes the wood invisible until I pick it up and set it down once.
>
> President Cash - 18:23
> Also fog or whatever doesn't seem to sync up
> I see a big foggy storm and approaching and they dont.  Either way awesome mod and thanks for
> making it.
>
> President Cash - 18:33
> they see normal rain atleast

Sitting two:

> President Cash - 11:08
> Hey is fog or whatever with storms not synced yet?
> I could see a storm approaching and they could not, it would just start raining and getting choppy
> While the waves didn't sync fully, the choppiness sort of did.
