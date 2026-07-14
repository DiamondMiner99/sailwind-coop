# HMS Leopard + Four-Mod Compatibility (v0.2.32) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make HMS Leopard a first-class co-op ship and make co-op safe alongside Sail Collision Fix, NAND Tweaks, Deep Ports and Towable Boats, shipping as v0.2.32 (wire change).

**Architecture:** A `Compat/CompatRegistry` composes one opaque handshake token from six per-mod reflection-only modules (SE existing + five new), replacing the SE-only gate. Four pre-existing co-op defects are fixed first (they are prerequisites). A generic `TrapdoorState` sync covers all doors/hatches; a Leopard adapter layers gunport-group semantics, cutter deploy/recover, oar input relay and the bell on top; the `MooringState` wire format gains a target reference so vanilla-mooring sync covers Towable Boats tows.

**Tech Stack:** C# net472, BepInEx 5, HarmonyLib 2, Facepunch.Steamworks. No test harness exists for this Unity plugin; the automated gate for every task is a clean `dotnet build`, and runtime behavior is verified by the live playtest checklist written in the final task (this is the project's established practice - see the spec's Testing section).

**Spec:** `docs/superpowers/specs/2026-07-14-hms-leopard-modcompat-design.md` (approved 2026-07-14).

## Global Constraints

- Repo/worktree: all work happens in `C:/Users/justi/source/repos/sailwind-coop-r4` on branch `crate-join-resync` (the established dev worktree; do NOT create a new worktree or branch).
- Build command (run from the worktree root); this is the verification step of every task:
  `dotnet build src/SailwindCoop/SailwindCoop.csproj -c Release -p:GameDir="C:/Program Files (x86)/Steam/steamapps/common/Sailwind"`
  Expected: `Build succeeded.` with `0 Error(s)`.
- NEVER use em dashes or en dashes anywhere (code comments, docs, commit messages, this plan's outputs). Use "-" or reword.
- Reflection-only for third-party mods: no new .csproj references, no vendored mod DLLs. Every third-party access mirrors the `SECompat` pattern (Chainloader probe, `AppDomain` fallback, fail-closed, never throw out of Init).
- Handshake tokens are OPAQUE: compared with `==` only. `CompatRegistry.DescribeMismatch` may split them for DISPLAY only, never for the gate decision.
- Wire discipline: every serializer's Write order MUST equal its Read order. New packet IDs are 216-219 (215 = current max, byte enum, ceiling 255).
- Version: `PluginVersion` becomes `0.2.32` in the FINAL task only (repo rule: every shipped zip carries a unique version; the bump commits last so intermediate builds are identifiable).
- Comment style: heavy "why" comments citing the third-party source (file:line of the mod repo or decomp) - match the house style visible in `SECompat.cs`.
- Commit after every task with the message given in the task. Do not push.
- Reference clones of all mod sources are at `C:/Users/justi/source/repos/sailwind-modrefs/` (HMSLeopard, SailCollisionFix, NANDTweaks, Deep-Ports, TowableBoats, ShipyardExpansion). Vanilla decompiles are at `C:/Users/justi/source/repos/sailwind-coop/decomp_sw/`.

## Plan-time errata vs the spec (surfaced for review, both accepted at plan review)

1. **Spec P3 overstated.** `BoatUtility.IsBoatAnchored` ([BoatUtility.cs:118-127](../../src/SailwindCoop/Sync/BoatUtility.cs)) and `BoatStateCollector.GetAnchorLength` (:506-513) already null-guard, and `BoatStateApplicator.ApplyAnchorState` (:1319-1324) warns-and-returns on a null anchor. Task 3 is therefore a verification pass plus one log demotion, not a crash fix.
2. **Oars: local prediction kept instead of suppress+lease.** The spec said "guest's local force path is suppressed" plus an ownership lease. Suppressing the force without killing the oar animation requires a transpiler on third-party IL (fragile) or duplicating the mod's whole animation block. Instead: the rower's machine runs the mod's vanilla `ExtraLateUpdate` unchanged (local prediction + animation, reconciled by the existing boat-transform correction exactly like local wind/buoyancy forces are), forwards held-key bits to the host, and the HOST also applies the force to its authoritative copy. Multiple simultaneous rowers add force - which is exactly what the unmodified mod does locally - so no lease is needed. Observers get a small animation-only adapter.

## Verified ground-truth constants used throughout (do not re-derive)

| Fact | Value |
|---|---|
| Leopard boat root names (runtime) | `BOAT LEOPARD (207)(Clone)`, `BOAT CUTTER (212)(Clone)` |
| Leopard GUID / assembly / version | `com.winter.leopard` / `Leopard` / 1.4.0 |
| SCF GUID / plugin type / config fields | `com.nandbrew.sailcollisionfix` / `SailCollisionFix.Main` / `ignoreSailsCollision`, `ignoreObstructed`, `ignoreAngleLimits` (all `internal static ConfigEntry<bool>`) |
| NT GUID / plugin type / sim config fields | `com.nandbrew.nandtweaks` / `NANDTweaks.Plugin` / `bailingTweaks`, `drunkenSleep`, `wheelCenter`, `albacoreArea`, `saveLoadState`, `toggleDoors` (all `internal static ConfigEntry<bool>`) |
| DP GUID / plugin type / bundle path | `com.winter.deepports` / `Deep_Ports.PortPatcher` / `Paths.PluginPath + "\\Deep Ports\\deepports"` |
| TB GUID / plugin type / config fields | `com.nandbrew.towableboats` / `TowableBoats.Plugin` / `smallBoats` (`internal static ConfigEntry<bool>`), cleat type `TowableBoats.TowingCleat : GPButtonDockMooring` |
| Leopard reflection surface | `Leopard.Patches` statics: `ship`, `boat`, `cutterActive`; `Leopard.Controllers.Gunports` statics: `recursive`, `lowerGunports`, `upperGunports`, `quarterGunports`, `overflows`; controller types `Leopard.Controllers.CutterController` (`OnActivate(GoPointer)`), `CutterRopeController` (`OnActivate()`), `OarController` (fields `forceAmount`, `turnForce`; private `leftOar`, `rightOar`; private method `SetOars(bool)`), `LeopardBellInteract` (`OnActivate(GoPointer)`) |
| Gunport prefab baseline (all ports CLOSED) | `mask water half` INACTIVE, `mask water full` ACTIVE, `overflow particles lower 1..4` INACTIVE, `overflow particles upper 1..5` ACTIVE, `interior trigger 2` ACTIVE, `interior trigger 3` ACTIVE |
| Gunport absolute mapping (lowerOpen) | `mask water half` active==lowerOpen; `mask water full` active==!lowerOpen; overflow lower active==lowerOpen; overflow upper active==!lowerOpen; `interior trigger 2` active==!lowerOpen |
| Gunport absolute mapping (upperOpen) | `interior trigger 3` active==!upperOpen |
| GPButtonTrapdoor facts | `open` private, `IsOpen()` public; `OnActivate()` (no-arg override) no-ops while private `inMotion`; the coroutine sets `open` synchronously before first yield, so a postfix reads the NEW state |
| Leopard gunport fan-out | `Patch_OnActivate` prefix on `GPButtonTrapdoor.OnActivate`: sets `Gunports.recursive=true`, calls `OnActivate()` on every sibling in the group, toggles masks/overflows/audio with `!activeSelf` (lower group), resets `recursive=false` before the clicked port's original body runs |
| Tow mechanics | vanilla `PickupableBoatMooringRope.MoorTo(GPButtonDockMooring)` SpringJoint; cleat sits under `<towing boat root>/towing set/...`; TB renames the container to exactly `towing set` (no Clone suffix); Leopard ships its own baked `towing set` with 8 cleats |
| Existing manager creation | `Plugin.cs:311-329` (`gameObject.AddComponent<...>` block); lobby events `OnLobbyJoined` `Plugin.cs:701`, `OnLobbyCreated` `Plugin.cs:883`, `OnLobbyLeft` `Plugin.cs:586`; join steps `Plugin.cs:2291-2333`; handler registration `Plugin.RegisterPacketHandlers` (SERigState precedent at `Plugin.cs:1466-1474`) |

---

## Phase 1: pre-existing defects (no wire change)

### Task 1: P1 - NAND Tweaks bail-prefix collision fix

**Files:**
- Modify: `src/SailwindCoop/Patches/DamagePatches.cs:187-231`

**Interfaces:**
- Consumes: nothing new.
- Produces: unchanged public surface; the guest bail RPC now fires even when NANDTweaks' skipping prefix runs.

Background: NANDTweaks patches `BoatDamageWaterButton.OnItemClick` with a prefix that returns `false` (`NANDTweaks/Patches/BoatDamagePatches.cs:54`). In Harmony 2, once any prefix returns false, later prefixes are SKIPPED unless they declare `__runOriginal`. Co-op's prefix (`DamagePatches.cs:190`) declares no `__runOriginal`, so with NANDTweaks first, `__state` stays 0 and the postfix never sends `SendBailRequest`. Postfixes always run, so only the prefix needs the parameter. NANDTweaks' replacement mutates `bottle.amount/health` the same way vanilla does (`BoatDamagePatches.cs:88-89`), so the postfix's `amount == 9f` check still holds.

- [ ] **Step 1: Edit the patch class**

Replace the `BoatDamageWaterButtonOnItemClickPatch` class body at `DamagePatches.cs:187-231` with:

```csharp
        [HarmonyPatch(typeof(BoatDamageWaterButton), "OnItemClick")]
        public static class BoatDamageWaterButtonOnItemClickPatch
        {
            // (v0.2.32, P1) __runOriginal + HarmonyBefore: NANDTweaks patches this same method with a
            // PREFIX THAT RETURNS FALSE (its bailingTweaks full replacement, NANDTweaks
            // BoatDamagePatches.cs:54). In Harmony 2, once any prefix returns false, later prefixes are
            // SKIPPED unless they declare __runOriginal - so without it, __state stayed 0, the postfix
            // early-returned, and a guest running NANDTweaks bailed water locally while the host never
            // received SendBailRequest (silent water-level divergence, snapped back by the next
            // authoritative damage sync). HarmonyBefore additionally orders us first when both are
            // present; __runOriginal is the belt-and-braces for any other mod that skips this method.
            // The capture itself only reads bottle state, so it is safe to run whether or not the
            // original (or NANDTweaks' replacement) executes.
            [HarmonyPrefix]
            [HarmonyBefore("com.nandbrew.nandtweaks")]
            public static void Prefix(PickupableItem heldItem, out float __state, bool __runOriginal)
            {
                __state = 0f;

                if (!Plugin.IsMultiplayer || Plugin.IsHost) return;
                if (heldItem == null || heldItem.GetType() != typeof(ShipItemBottle)) return;

                var bottle = (ShipItemBottle)heldItem;

                // Store remaining capacity before bailing (this is what will be bailed)
                float remainingCapacity = bottle.GetRemainingCapacity();
                float capacity = bottle.GetCapacity();

                // Apply same cap as game: non-buckets max 5 units
                if (remainingCapacity > 5f && capacity != 9f)
                {
                    remainingCapacity = 5f;
                }

                __state = remainingCapacity;
            }

            [HarmonyPostfix]
            public static void Postfix(PickupableItem heldItem, float __state)
            {
                if (!Plugin.IsMultiplayer || Plugin.IsHost) return;
                if (__state <= 0f) return;

                if (heldItem == null || heldItem.GetType() != typeof(ShipItemBottle)) return;

                var bottle = (ShipItemBottle)heldItem;
                var prefab = bottle.GetComponent<SaveablePrefab>();
                if (prefab == null) return;

                // If bottle now has sea water, bailing occurred (true for vanilla AND for NANDTweaks'
                // bailingTweaks replacement, which writes amount/health the same way).
                if (bottle.amount == 9f && bottle.health > 0f)
                {
                    DamageSyncManager.Instance?.SendBailRequest(prefab.instanceId, __state);
                }
            }
        }
```

- [ ] **Step 2: Build**

Run the global build command. Expected: `Build succeeded.`

- [ ] **Step 3: Commit**

```bash
git add src/SailwindCoop/Patches/DamagePatches.cs
git commit -m "fix(compat): P1 - guest bail RPC survives NANDTweaks' skipping prefix (__runOriginal + HarmonyBefore)"
```

### Task 2: P2 - boat-cache invalidation

**Files:**
- Modify: `src/SailwindCoop/Sync/BoatUtility.cs:16-19` (comment), `src/SailwindCoop/Plugin.cs` (three lobby event handlers)

**Interfaces:**
- Consumes: existing `BoatUtility.ClearCaches()` (`BoatUtility.cs:247`, currently zero call sites).
- Produces: the boat map is rebuilt on every lobby create/join/leave. Later tasks (cutter) additionally call `ClearCaches()` when a boat's active state changes.

- [ ] **Step 1: Wire ClearCaches into the lobby lifecycle**

In `Plugin.cs`, add as the FIRST line inside each of these three handlers (exact anchors given):

(a) inside `LobbyManager.OnLobbyJoined += lobby =>` (opens at `Plugin.cs:701`, body starts line ~702):

```csharp
                // (v0.2.32, P2) Boat-map rebuild: _cachedBoats was built once per PROCESS and never
                // invalidated (ClearCaches had zero call sites), so a boat spawned after the first
                // FindAllBoats() call - e.g. HMS Leopard's runtime-deployed cutter - stayed invisible
                // to every name-keyed sync forever, and a leave/rejoin kept stale SaveableObject refs.
                Sync.BoatUtility.ClearCaches();
```

(b) inside `LobbyManager.OnLobbyCreated += lobby =>` (opens at `Plugin.cs:883`), same line + short comment `// (v0.2.32, P2) fresh session = fresh boat map`:

```csharp
                Sync.BoatUtility.ClearCaches(); // (v0.2.32, P2) fresh session = fresh boat map
```

(c) inside `LobbyManager.OnLobbyLeft += () =>` (opens at `Plugin.cs:586`), same one-liner as (b).

- [ ] **Step 2: Correct the stale comments in BoatUtility**

At `BoatUtility.cs:9` replace `// Cache boats - boats don't change during gameplay, only refresh on scene load` with:

```csharp
        // Cache boats. (v0.2.32) Boats CAN change during gameplay - modded boats (HMS Leopard's
        // cutter) are spawned/activated at runtime - so the cache is invalidated on every lobby
        // create/join/leave (Plugin lobby handlers) and whenever a compat module spawns or toggles
        // a boat (LeopardSyncManager.ApplyCutterState).
```

At `BoatUtility.cs:16-19` replace the `<summary>` with:

```csharp
        /// <summary>
        /// Find all boats in the scene (cached until ClearCaches is called - lobby lifecycle events
        /// and boat spawn/activation invalidate it; see the field comment above).
        /// </summary>
```

- [ ] **Step 3: Build**

Run the global build command. Expected: `Build succeeded.`

- [ ] **Step 4: Commit**

```bash
git add src/SailwindCoop/Sync/BoatUtility.cs src/SailwindCoop/Plugin.cs
git commit -m "fix(compat): P2 - invalidate the boat-name cache on lobby create/join/leave"
```

### Task 3: P3 - anchorless-boat verification pass

**Files:**
- Modify: `src/SailwindCoop/Sync/BoatStateApplicator.cs:1319-1324`
- Verify (read-only): every `BoatUtility.GetAnchor(` / `GetAnchorController()` call site

**Interfaces:** none new.

The cutter (sceneIndex 212) is the first boat co-op meets with NO `Anchor` and a null `BoatMooringRopes.anchor`. Verified at plan time: `IsBoatAnchored` (`BoatUtility.cs:121`), `GetAnchorLength` (`BoatStateCollector.cs:509`) and `ApplyAnchorState` (`BoatStateApplicator.cs:1320`) already null-guard. This task re-verifies every site and demotes the join-time warning (which would otherwise log once per join for a legitimately anchorless boat).

- [ ] **Step 1: Enumerate and verify all anchor call sites**

Run:

```bash
grep -rn "BoatUtility.GetAnchor(\|GetAnchorController()" src/SailwindCoop --include=*.cs
```

Expected sites (verify each handles a null result without dereferencing; all did at plan time):
`BoatStateApplicator.cs:546, 1319, 1328`, `ControlSyncManager.cs:259, 332, 364, 394, 588, 1200, 1207, 1249, 1271`, `BoatUtility.cs:102`, `BoatStateCollector.cs:508`. If any site dereferences a possibly-null anchor/controller without a guard, insert `if (anchor == null) return;` (or the site's equivalent skip) with a `// (v0.2.32, P3) anchorless boat (Leopard cutter) guard` comment. Record the verdict per site in the commit message body.

- [ ] **Step 2: Demote the ApplyAnchorState warning for anchorless boats**

At `BoatStateApplicator.cs:1319-1324` replace:

```csharp
            var anchor = BoatUtility.GetAnchor(boat);
            if (anchor == null)
            {
                Plugin.Log.LogWarning($"ApplyAnchorState SKIPPED: no Anchor resolvable on boat '{boat?.gameObject.name}'");
                return;
            }
```

with:

```csharp
            var anchor = BoatUtility.GetAnchor(boat);
            if (anchor == null)
            {
                // (v0.2.32, P3) Anchorless boats are legitimate now: HMS Leopard's cutter (212) has no
                // Anchor and a null BoatMooringRopes.anchor. Info, not warning - this fires once per
                // boat per join and is expected for the cutter; a WARN here would train users to ignore
                // real anchor-resolve failures on vanilla boats.
                Plugin.Log.LogInfo($"ApplyAnchorState skipped: no Anchor on boat '{boat?.gameObject.name}' (anchorless boats are valid, e.g. the Leopard cutter)");
                return;
            }
```

- [ ] **Step 3: Build**

Run the global build command. Expected: `Build succeeded.`

- [ ] **Step 4: Commit**

```bash
git add -A src/SailwindCoop
git commit -m "fix(compat): P3 - verify anchor paths for anchorless boats; demote join-time anchor log"
```

(Include the per-site verification verdicts in the commit body.)

### Task 4: P4 - always-stream boat registry

**Files:**
- Modify: `src/SailwindCoop/Sync/BoatSyncManager.cs` (fields near line 86; host send loop lines 213-240)

**Interfaces:**
- Produces: `public static void RegisterAlwaysStream(string boatName)` and `public static void UnregisterAlwaysStream(string boatName)` on `BoatSyncManager`. Later tasks call them: Task 10 (cutter deploy/recover) and Task 13 (tow attach/detach).

Background: the host streams only `lastBoat` plus boats carrying a REMOTE crew member (`BoatSyncManager.cs:213-240`); an empty deployed cutter or an unmanned towed boat gets zero position sync and guests prune it after 10s. The registry lets compat code pin specific boats into the secondary (10Hz) send set.

- [ ] **Step 1: Add the registry**

After the `_activeBoatNamesScratch` field (`BoatSyncManager.cs:86`), add:

```csharp
        // (v0.2.32, P4) Boats that must stream even with NOBODY aboard. The send loop below only
        // covers lastBoat + boats carrying a remote crew member, so an empty deployed cutter or an
        // unmanned towed boat got zero sync and guests pruned it after BoatStatePruneSeconds.
        // Compat modules pin such boats here: LeopardSyncManager (deployed cutter) and the tow
        // attach/detach path (towed boats). Host-side only; name = root SaveableObject name.
        private static readonly HashSet<string> _alwaysStreamBoats = new HashSet<string>();

        public static void RegisterAlwaysStream(string boatName)
        {
            if (string.IsNullOrEmpty(boatName)) return;
            if (_alwaysStreamBoats.Add(boatName))
                Plugin.Log.LogInfo($"[BOAT] Always-stream registered: {boatName}");
        }

        public static void UnregisterAlwaysStream(string boatName)
        {
            if (string.IsNullOrEmpty(boatName)) return;
            if (_alwaysStreamBoats.Remove(boatName))
                Plugin.Log.LogInfo($"[BOAT] Always-stream unregistered: {boatName}");
        }
```

- [ ] **Step 2: Fold the registry into the secondary send set**

Inside the `if ((_sendTick % SecondaryBoatSendDivider) == 0)` block, after the existing `rpm.Avatars` foreach (ends `BoatSyncManager.cs:231`) and before the `foreach (var boatSaveable in _activeBoatsScratch)` send loop, add:

```csharp
                // (v0.2.32, P4) Pinned boats: stream even with nobody aboard (deployed cutter, towed
                // hulls). Same dedup rules as the crewed set; the primary is already excluded above.
                foreach (var pinnedName in _alwaysStreamBoats)
                {
                    if (pinnedName == lastBoatName) continue;
                    if (!_activeBoatNamesScratch.Add(pinnedName)) continue;
                    var pinned = BoatUtility.FindBoatByName(pinnedName);
                    // An inactive boat (recovered cutter) still resolves but must not stream.
                    if (pinned != null && pinned.gameObject.activeInHierarchy) _activeBoatsScratch.Add(pinned);
                }
```

- [ ] **Step 3: Build**

Run the global build command. Expected: `Build succeeded.`

- [ ] **Step 4: Commit**

```bash
git add src/SailwindCoop/Sync/BoatSyncManager.cs
git commit -m "feat(compat): P4 - always-stream registry so unmanned boats (cutter, tows) keep syncing"
```

---

## Phase 2: compat registry + tiered parity gate (no wire change)

### Task 5: SyncPathUtil + four simple compat modules + CompatRegistry

**Files:**
- Create: `src/SailwindCoop/Sync/SyncPathUtil.cs`
- Create: `src/SailwindCoop/Compat/SCFCompat.cs`
- Create: `src/SailwindCoop/Compat/NANDTweaksCompat.cs`
- Create: `src/SailwindCoop/Compat/DeepPortsCompat.cs`
- Create: `src/SailwindCoop/Compat/TowableBoatsCompat.cs`
- Create: `src/SailwindCoop/Compat/CompatRegistry.cs`

**Interfaces:**
- Produces:
  - `SyncPathUtil.GetRelativePath(Transform root, Transform target) : string` and `SyncPathUtil.FindByRelativePath(Transform root, string path) : Transform` (used by Tasks 8, 13).
  - `SCFCompat` / `NANDTweaksCompat` / `DeepPortsCompat` / `TowableBoatsCompat`: each `public static class` with `Init()`, `IsInstalled`, `Version`, `ModSignature`. `TowableBoatsCompat` additionally exposes `TowingCleatType : System.Type` and `IsTowingCleat(Component c) : bool` (used by Tasks 13, 14).
  - `CompatRegistry.ModSignature : string` (composed token) and `CompatRegistry.DescribeMismatch(string hostToken, string ourToken) : string` (used by Task 7).
- Consumes: `Compat.SECompat` (existing, unchanged).

Token contract (opaque; segments joined with `;` in this FIXED order, empty segments dropped, NT always present):
`SE=...` (existing) `;SCF=<ver>/c<0|1>o<0|1>a<0|1>` `;NT=b_s_w_f_v_d_` (or `NT=?` fail-closed) `;DP=<ver>/<hash8|nobundle>` `;TB=<ver>/sb<0|1>` `;LEO=<ver>[/noSync]`.
A peer WITHOUT NANDTweaks emits the vanilla vector `NT=b0s0w0f0v0d0`, so all-cosmetic NT differences and "sim options all off" vs "not installed" both pass the gate (the spec's tiered design).

- [ ] **Step 1: Write SyncPathUtil.cs**

```csharp
using System.Text;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// (v0.2.32) Relative-transform-path keys for wire messages. Mod parity (CompatRegistry gate)
    /// guarantees identical prefab hierarchies on every peer, so a path from the boat root is a
    /// stable cross-machine key for prefab-baked children (trapdoors, towing cleats). Unity allows
    /// duplicate sibling names, so each segment carries an occurrence index among SAME-NAMED
    /// siblings ("name" for the first, "name~2" for the second, ...). Prefab instantiation order is
    /// deterministic, so the occurrence index matches across machines for baked structure. NOT safe
    /// for runtime-rebuilt hierarchies (sails) - do not use it for anything a customization rebuild
    /// can reorder.
    /// </summary>
    public static class SyncPathUtil
    {
        public static string GetRelativePath(Transform root, Transform target)
        {
            if (root == null || target == null) return null;
            var sb = new StringBuilder();
            var t = target;
            while (t != null && t != root)
            {
                string seg = t.name;
                int occ = OccurrenceAmongSameNamedSiblings(t);
                if (occ > 1) seg = seg + "~" + occ;
                sb.Insert(0, sb.Length == 0 ? seg : seg + "/");
                t = t.parent;
            }
            if (t != root) return null; // target is not under root
            return sb.ToString();
        }

        public static Transform FindByRelativePath(Transform root, string path)
        {
            if (root == null || string.IsNullOrEmpty(path)) return null;
            var t = root;
            foreach (var rawSeg in path.Split('/'))
            {
                string name = rawSeg;
                int occ = 1;
                int tilde = rawSeg.LastIndexOf('~');
                if (tilde > 0 && int.TryParse(rawSeg.Substring(tilde + 1), out int parsed))
                {
                    name = rawSeg.Substring(0, tilde);
                    occ = parsed;
                }
                Transform next = null;
                int seen = 0;
                for (int i = 0; i < t.childCount; i++)
                {
                    var c = t.GetChild(i);
                    if (c.name != name) continue;
                    seen++;
                    if (seen == occ) { next = c; break; }
                }
                if (next == null) return null;
                t = next;
            }
            return t;
        }

        private static int OccurrenceAmongSameNamedSiblings(Transform t)
        {
            var parent = t.parent;
            if (parent == null) return 1;
            int occ = 0;
            for (int i = 0; i < parent.childCount; i++)
            {
                var c = parent.GetChild(i);
                if (c.name != t.name) continue;
                occ++;
                if (c == t) return occ;
            }
            return 1;
        }
    }
}
```

- [ ] **Step 2: Write SCFCompat.cs**

```csharp
using System;
using System.Linq;
using System.Reflection;

namespace SailwindCoop.Compat
{
    /// <summary>
    /// (v0.2.32) Soft-dependency bridge to NANDbrew's Sail Collision Fix. SCF has no runtime data to
    /// sync - its three config bools decide WHICH rigs are legal (IsCollidingWithSail/IsObstructed
    /// forced false) and how far sails sheet (colAngleMin/Max reset to the full range on
    /// OnTriggerEnter; SCF SailCollisionFix.cs:14-43). All three change what an identical shipyard
    /// edit BUILDS, so they ride the handshake token and a divergent crew is refused at the door.
    /// Note "Ignore sail collision" DEFAULTS TO TRUE: a peer who installed the DLL and never opened
    /// the config already diverges from a vanilla peer, which is exactly why presence alone gates.
    /// </summary>
    public static class SCFCompat
    {
        public const string SCFGuid = "com.nandbrew.sailcollisionfix";
        private const string SCFAssemblyName = "SailCollisionFix";
        private static readonly string[] ConfigFields = { "ignoreSailsCollision", "ignoreObstructed", "ignoreAngleLimits" };
        private static readonly string[] ConfigTags = { "c", "o", "a" };

        public static bool IsInstalled { get; private set; }
        public static string Version { get; private set; } = "";
        private static string _configToken; // computed lazily; null until first read

        public static string ModSignature
        {
            get
            {
                if (!IsInstalled) return "";
                if (_configToken == null) _configToken = ReadConfigToken();
                return "SCF=" + Version + _configToken;
            }
        }

        public static void Init()
        {
            IsInstalled = false;
            Version = "";
            _configToken = null;
            try
            {
                if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(SCFGuid, out var info) || info == null)
                {
                    Plugin.Log.LogInfo("[SCFCompat] Sail Collision Fix not installed.");
                    return;
                }
                IsInstalled = true;
                Version = info.Metadata.Version.ToString();
                Plugin.Log.LogInfo($"[SCFCompat] Sail Collision Fix v{Version} detected; its three config " +
                    "options join the handshake token (they change rig legality and sail angle limits).");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[SCFCompat] Detection threw; treating as not installed. " + e);
            }
        }

        // Lazy: SCF binds its configs in ITS Awake. The soft BepInDependency in Plugin.cs orders SCF
        // before us, but the AppDomain fallback below covers exotic load orders anyway, and lazy
        // evaluation (first lobby create/join) runs long after every plugin's Awake.
        private static string ReadConfigToken()
        {
            try
            {
                var asm = ResolveAssembly();
                var mainType = asm?.GetType("SailCollisionFix.Main");
                if (mainType == null) return "/cfg?";
                var sb = new System.Text.StringBuilder("/");
                for (int i = 0; i < ConfigFields.Length; i++)
                {
                    var f = mainType.GetField(ConfigFields[i], BindingFlags.NonPublic | BindingFlags.Static);
                    var entry = f?.GetValue(null) as BepInEx.Configuration.ConfigEntry<bool>;
                    if (entry == null) return "/cfg?"; // absent or unbound: fail closed into a distinct token
                    sb.Append(ConfigTags[i]).Append(entry.Value ? '1' : '0');
                }
                return sb.ToString();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[SCFCompat] Could not read SCF configs: " + e.Message);
                return "/cfg?";
            }
        }

        private static Assembly ResolveAssembly()
        {
            BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(SCFGuid, out var info);
            var asm = info?.Instance != null ? info.Instance.GetType().Assembly : null;
            return asm ?? AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == SCFAssemblyName);
        }
    }
}
```

- [ ] **Step 3: Write NANDTweaksCompat.cs**

```csharp
using System;
using System.Linq;
using System.Reflection;

namespace SailwindCoop.Compat
{
    /// <summary>
    /// (v0.2.32) NAND Tweaks is gated on BEHAVIOR, not presence: most of it is cosmetic (outlines,
    /// camera, UI, decals, thumbnails, keybinds - free per player), but six options change the
    /// simulation and must match across the crew:
    ///   b bailingTweaks  - replaces the bail routine, writes BoatDamage.waterLevel directly
    ///   s drunkenSleep   - drains PlayerNeeds.sleep scaled by Sun timescale (sleep duration)
    ///   w wheelCenter    - writes GPButtonSteeringWheel.currentInput in ExtraFixedUpdate
    ///   f albacoreArea   - injects a LocalFishesRegion + prefab into OceanFishes
    ///   v saveLoadState  - writes modData, restores Rigidbody.velocity/sails/wheel on load
    ///   d toggleDoors    - fires GPButtonTrapdoor.OnActivate() on load
    /// A peer WITHOUT the mod has the VANILLA vector (all zeros), so cosmetic-only installs and
    /// "everything off" both match a vanilla peer. NOTE the mod's DEFAULTS are not vanilla (four of
    /// six default true), so host-at-defaults vs no-mod-guest is a REAL sim difference and is
    /// correctly refused - the refusal message (CompatRegistry.DescribeMismatch) names the vector so
    /// users can see which options differ. Values snapshot at token time (lobby create/join);
    /// mid-session config flips are not re-gated, same as SE.
    /// </summary>
    public static class NANDTweaksCompat
    {
        public const string NTGuid = "com.nandbrew.nandtweaks";
        private const string NTAssemblyName = "NANDTweaks";
        private static readonly string[] SimConfigFields = { "bailingTweaks", "drunkenSleep", "wheelCenter", "albacoreArea", "saveLoadState", "toggleDoors" };
        private static readonly string[] SimConfigTags = { "b", "s", "w", "f", "v", "d" };
        public const string VanillaVector = "NT=b0s0w0f0v0d0";

        public static bool IsInstalled { get; private set; }
        public static string Version { get; private set; } = "";
        private static string _token;

        public static string ModSignature
        {
            get
            {
                if (!IsInstalled) return VanillaVector;
                if (_token == null) _token = ReadSimVector();
                return _token;
            }
        }

        public static void Init()
        {
            IsInstalled = false;
            Version = "";
            _token = null;
            try
            {
                if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(NTGuid, out var info) || info == null)
                {
                    Plugin.Log.LogInfo("[NTCompat] NAND Tweaks not installed; advertising the vanilla sim vector.");
                    return;
                }
                IsInstalled = true;
                Version = info.Metadata.Version.ToString();
                Plugin.Log.LogInfo($"[NTCompat] NAND Tweaks v{Version} detected; gating on its six " +
                    "simulation-affecting options (cosmetic options stay free per player).");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[NTCompat] Detection threw; treating as not installed. " + e);
            }
        }

        private static string ReadSimVector()
        {
            try
            {
                BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(NTGuid, out var info);
                var asm = info?.Instance != null ? info.Instance.GetType().Assembly : null;
                asm = asm ?? AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == NTAssemblyName);
                var pluginType = asm?.GetType("NANDTweaks.Plugin");
                if (pluginType == null) return "NT=?";
                var sb = new System.Text.StringBuilder("NT=");
                for (int i = 0; i < SimConfigFields.Length; i++)
                {
                    var f = pluginType.GetField(SimConfigFields[i], BindingFlags.NonPublic | BindingFlags.Static);
                    var entry = f?.GetValue(null) as BepInEx.Configuration.ConfigEntry<bool>;
                    if (entry == null) return "NT=?"; // fail closed: unreadable sim vector must not pass as vanilla
                    sb.Append(SimConfigTags[i]).Append(entry.Value ? '1' : '0');
                }
                return sb.ToString();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[NTCompat] Could not read NAND Tweaks sim configs: " + e.Message);
                return "NT=?";
            }
        }
    }
}
```

- [ ] **Step 4: Write DeepPortsCompat.cs**

```csharp
using System;
using System.IO;
using System.Security.Cryptography;

namespace SailwindCoop.Compat
{
    /// <summary>
    /// (v0.2.32) Deep Ports swaps Terrain.terrainData AND TerrainCollider.terrainData on Gold Rock,
    /// Fort (Aestrin) and Dragon Cliffs, plus two collider meshes (Deep-Ports Patches.cs:23-38) - the
    /// physics heightfield itself. A peer without it runs aground on shoals the host dredged away, so
    /// presence + version is hard-gated. Version alone is NOT enough: the heightfields live entirely
    /// in the "deepports" AssetBundle, the DLL loads whatever bundle sits next to it, and a MISSING
    /// bundle fails SILENTLY (Patches.cs:50-54 logs and does nothing while the plugin still registers
    /// as loaded). So the token carries an 8-hex SHA-256 prefix of the bundle FILE - computed once in
    /// Init (the file is ~30 MB; never hash per-join) - and an installed-but-broken peer advertises
    /// "/nobundle", which mismatches every working peer instead of passing as equal.
    /// </summary>
    public static class DeepPortsCompat
    {
        public const string DPGuid = "com.winter.deepports";

        public static bool IsInstalled { get; private set; }
        public static string Version { get; private set; } = "";
        private static string _bundleTag = "nobundle";

        public static string ModSignature => IsInstalled ? $"DP={Version}/{_bundleTag}" : "";

        public static void Init()
        {
            IsInstalled = false;
            Version = "";
            _bundleTag = "nobundle";
            try
            {
                if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(DPGuid, out var info) || info == null)
                {
                    Plugin.Log.LogInfo("[DPCompat] Deep Ports not installed.");
                    return;
                }
                IsInstalled = true;
                Version = info.Metadata.Version.ToString();

                // Exact path Deep Ports itself loads (Patches.cs:50): PluginPath\Deep Ports\deepports
                string bundlePath = Path.Combine(BepInEx.Paths.PluginPath, "Deep Ports", "deepports");
                if (File.Exists(bundlePath))
                {
                    using (var sha = SHA256.Create())
                    using (var fs = File.OpenRead(bundlePath))
                    {
                        var hash = sha.ComputeHash(fs);
                        _bundleTag = BitConverter.ToString(hash, 0, 4).Replace("-", "").ToLowerInvariant();
                    }
                    Plugin.Log.LogInfo($"[DPCompat] Deep Ports v{Version} detected; terrain bundle hash {_bundleTag} " +
                        "joins the handshake token (every peer must run the identical bundle).");
                }
                else
                {
                    Plugin.Log.LogWarning($"[DPCompat] Deep Ports v{Version} is installed but its 'deepports' asset " +
                        "bundle is MISSING - Deep Ports will silently run VANILLA terrain on this machine. Joins " +
                        "with working Deep Ports peers will be refused until the bundle is restored.");
                }
            }
            catch (Exception e)
            {
                // Keep IsInstalled true when we got that far: refusing is safer than passing as vanilla.
                Plugin.Log.LogWarning("[DPCompat] Bundle hash failed; advertising /nobundle. " + e);
            }
        }
    }
}
```

- [ ] **Step 5: Write TowableBoatsCompat.cs**

```csharp
using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace SailwindCoop.Compat
{
    /// <summary>
    /// (v0.2.32) Towable Boats reuses the vanilla mooring SpringJoint with the bollard on a MOVING
    /// boat (a TowingCleat : GPButtonDockMooring under "<boat root>/towing set/"). Presence + version
    /// is hard-gated (it adds cleat GameObjects to hulls and flips which boats run full BoatProbes
    /// physics), and its "Small boats can tow" config joins the token: it decides whether cog/dhow/
    /// kakam get a towing set child AT ALL (TowingSet.cs:141-143, applied at Awake, restart-scoped) -
    /// a hierarchy difference that breaks path-keyed sync. Its "Performance mode" config is
    /// deliberately NOT in the token: guests neutralize BoatPerformanceSwitcher for remote hulls
    /// (BoatPhysicsPatches), which makes that config irrelevant to them.
    /// </summary>
    public static class TowableBoatsCompat
    {
        public const string TBGuid = "com.nandbrew.towableboats";
        private const string TBAssemblyName = "TowableBoats";

        public static bool IsInstalled { get; private set; }
        public static string Version { get; private set; } = "";
        /// <summary>TowableBoats.TowingCleat, resolved once; null when TB absent or reflection failed.</summary>
        public static Type TowingCleatType { get; private set; }
        private static string _configToken;

        public static string ModSignature
        {
            get
            {
                if (!IsInstalled) return "";
                if (_configToken == null) _configToken = ReadConfigToken();
                return "TB=" + Version + _configToken;
            }
        }

        public static bool IsTowingCleat(Component c)
            => c != null && TowingCleatType != null && TowingCleatType.IsInstanceOfType(c);

        /// <summary>True when this GameObject (a collider hit / mooring target) carries a TowingCleat.</summary>
        public static bool HasTowingCleat(GameObject go)
            => go != null && TowingCleatType != null && go.GetComponent(TowingCleatType) != null;

        public static void Init()
        {
            IsInstalled = false;
            Version = "";
            TowingCleatType = null;
            _configToken = null;
            try
            {
                if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(TBGuid, out var info) || info == null)
                {
                    Plugin.Log.LogInfo("[TBCompat] Towable Boats not installed.");
                    return;
                }
                IsInstalled = true;
                Version = info.Metadata.Version.ToString();

                var asm = info.Instance != null ? info.Instance.GetType().Assembly : null;
                asm = asm ?? AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == TBAssemblyName);
                TowingCleatType = asm?.GetType("TowableBoats.TowingCleat");
                if (TowingCleatType == null)
                    Plugin.Log.LogWarning($"[TBCompat] Towable Boats v{Version} detected but TowingCleat did not " +
                        "resolve - tow-target detection disabled (tows will sync as unresolvable moors). " +
                        "Check for a Sailwind Co-op update.");
                else
                    Plugin.Log.LogInfo($"[TBCompat] Towable Boats v{Version} detected; tow-aware mooring sync enabled.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[TBCompat] Detection threw. " + e);
            }
        }

        private static string ReadConfigToken()
        {
            try
            {
                BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(TBGuid, out var info);
                var asm = info?.Instance != null ? info.Instance.GetType().Assembly : null;
                asm = asm ?? AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == TBAssemblyName);
                var pluginType = asm?.GetType("TowableBoats.Plugin");
                var f = pluginType?.GetField("smallBoats", BindingFlags.NonPublic | BindingFlags.Static);
                var entry = f?.GetValue(null) as BepInEx.Configuration.ConfigEntry<bool>;
                if (entry == null) return "/cfg?";
                return "/sb" + (entry.Value ? '1' : '0');
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[TBCompat] Could not read smallBoats config: " + e.Message);
                return "/cfg?";
            }
        }
    }
}
```

- [ ] **Step 6: Write CompatRegistry.cs**

```csharp
using System.Collections.Generic;
using System.Linq;

namespace SailwindCoop.Compat
{
    /// <summary>
    /// (v0.2.32) Composes ONE opaque mod-set token from every per-mod compat module, in a FIXED
    /// deterministic order, for the lobby-data pre-check and the P2P handshake. The composed token
    /// obeys the same contract as the old SE-only token: compare with == only, never parse for the
    /// GATE decision. DescribeMismatch splits it for the refusal MESSAGE only, so users learn WHICH
    /// mod (and for NAND Tweaks, which sim vector) differs instead of a generic "mismatch".
    /// Segment order: SE, SCF, NT, DP, TB, LEO. Empty segments (mod absent) are dropped EXCEPT NT,
    /// which always emits (a vanilla peer advertises the vanilla sim vector - that equivalence is
    /// the whole tiered-gate design).
    /// </summary>
    public static class CompatRegistry
    {
        private static string _cached;

        public static string ModSignature
        {
            get
            {
                if (_cached != null) return _cached;
                var parts = new List<string>
                {
                    SECompat.ModSignature,
                    SCFCompat.ModSignature,
                    NANDTweaksCompat.ModSignature,   // always non-empty (vanilla vector when absent)
                    DeepPortsCompat.ModSignature,
                    TowableBoatsCompat.ModSignature,
                    LeopardCompat.ModSignature,
                };
                _cached = string.Join(";", parts.Where(p => !string.IsNullOrEmpty(p)));
                Plugin.Log.LogInfo($"[MODS] Composed mod-set token: [{_cached}]");
                return _cached;
            }
        }

        /// <summary>
        /// Human-readable diff of two composed tokens, for refusal messages ONLY (the gate itself
        /// stays exact string equality). Groups segments by their prefix before '='.
        /// </summary>
        public static string DescribeMismatch(string hostToken, string ourToken)
        {
            var host = Segments(hostToken);
            var ours = Segments(ourToken);
            var keys = new List<string>(host.Keys);
            foreach (var k in ours.Keys) if (!keys.Contains(k)) keys.Add(k);

            var diffs = new List<string>();
            foreach (var k in keys)
            {
                host.TryGetValue(k, out var h);
                ours.TryGetValue(k, out var o);
                if (h == o) continue;
                string name = FriendlyName(k);
                if (h == null) diffs.Add($"{name}: you have [{o}], the host does not have it");
                else if (o == null) diffs.Add($"{name}: the host has [{h}], you do not have it");
                else diffs.Add($"{name}: host [{h}] vs you [{o}]");
            }
            return diffs.Count == 0 ? "mod tokens differ" : string.Join("; ", diffs);
        }

        private static Dictionary<string, string> Segments(string token)
        {
            var map = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(token)) return map;
            foreach (var seg in token.Split(';'))
            {
                int eq = seg.IndexOf('=');
                map[eq > 0 ? seg.Substring(0, eq) : seg] = seg;
            }
            return map;
        }

        private static string FriendlyName(string key)
        {
            switch (key)
            {
                case "SE": return "Shipyard Expansion";
                case "SCF": return "Sail Collision Fix";
                case "NT": return "NAND Tweaks (sim options)";
                case "DP": return "Deep Ports";
                case "TB": return "Towable Boats";
                case "LEO": return "HMS Leopard";
                default: return key;
            }
        }
    }
}
```

Note: `LeopardCompat` does not exist until Task 6. To keep this task independently buildable, ALSO create a minimal placeholder `src/SailwindCoop/Compat/LeopardCompat.cs` in THIS task containing only detection (Task 6 replaces it with the full module):

```csharp
using System;

namespace SailwindCoop.Compat
{
    /// <summary>(v0.2.32) HMS Leopard detection. Full reflection surface lands in the next commit.</summary>
    public static class LeopardCompat
    {
        public const string LeopardGuid = "com.winter.leopard";

        public static bool IsInstalled { get; private set; }
        public static string Version { get; private set; } = "";

        public static string ModSignature => IsInstalled ? "LEO=" + Version : "";

        public static void Init()
        {
            IsInstalled = false;
            Version = "";
            try
            {
                if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(LeopardGuid, out var info) || info == null)
                {
                    Plugin.Log.LogInfo("[LeopardCompat] HMS Leopard not installed.");
                    return;
                }
                IsInstalled = true;
                Version = info.Metadata.Version.ToString();
                Plugin.Log.LogInfo($"[LeopardCompat] HMS Leopard v{Version} detected.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[LeopardCompat] Detection threw. " + e);
            }
        }
    }
}
```

- [ ] **Step 7: Build**

Run the global build command. Expected: `Build succeeded.`

- [ ] **Step 8: Commit**

```bash
git add src/SailwindCoop/Sync/SyncPathUtil.cs src/SailwindCoop/Compat
git commit -m "feat(compat): CompatRegistry + SCF/NANDTweaks/DeepPorts/TowableBoats compat modules (detection + tokens)"
```

### Task 6: LeopardCompat full reflection surface

**Files:**
- Modify (replace): `src/SailwindCoop/Compat/LeopardCompat.cs`

**Interfaces:**
- Produces (consumed by Tasks 8-12):
  - `LeopardCompat.IsInstalled`, `Version`, `SyncEnabled : bool`, `ModSignature` (now `LEO=<ver>[/noSync]`)
  - `LeopardCompat.Init()` (extended), `LeopardCompat.ApplyPatches(HarmonyLib.Harmony harmony)` (called from Plugin.Awake AFTER `PatchAll`; manual patches on the four Leopard controller types land in Tasks 10-12 and register themselves here)
  - `LeopardCompat.LeopardRootName = "BOAT LEOPARD (207)(Clone)"`, `CutterRootName = "BOAT CUTTER (212)(Clone)"` (const strings)
  - `LeopardCompat.LeopardShip : GameObject` (reads `Leopard.Patches.ship`), `CutterBoat : GameObject` (reads `Leopard.Patches.boat`)
  - `LeopardCompat.GetCutterActive() : bool` / `SetCutterActive(bool)` (reads/writes `Leopard.Patches.cutterActive`)
  - `LeopardCompat.IsGunportFanoutInProgress : bool` (reads `Leopard.Controllers.Gunports.recursive`)
  - `LeopardCompat.GunportGroupOf(string trapdoorName) : string` (returns `"lower"`/`"upper"`/`"quarter"`/`null`)
  - `LeopardCompat.GetGunportGroupOpen(string group) : bool?` (IsOpen() of the group's first port)
  - `LeopardCompat.ApplyGunportGroup(string group, bool open) : bool` (invoke `OnActivate()` on the group's first port when state differs; returns whether a toggle was issued)
  - `LeopardCompat.ForceGunportAbsolutes(string group, bool open)` (SetActive per the verified prefab mapping)
  - Controller `System.Type`s: `CutterControllerType`, `CutterRopeControllerType`, `OarControllerType`, `BellInteractType`
  - `OarController` accessors: `GetOarForceAmount(Component oar) : float`, `GetOarTurnForce(Component oar) : float`, `GetOarLeft(Component oar) : GameObject`, `GetOarRight(Component oar) : GameObject`, `InvokeSetOars(Component oar, bool set)`

- [ ] **Step 1: Replace LeopardCompat.cs with the full module**

```csharp
using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace SailwindCoop.Compat
{
    /// <summary>
    /// (v0.2.32) Soft-dependency bridge to winterspices' HMS Leopard. The Leopard spawns two full
    /// vanilla-component boats at runtime (roots "BOAT LEOPARD (207)(Clone)" and
    /// "BOAT CUTTER (212)(Clone)", instantiated by a FloatingOriginManager.Start prefix,
    /// HMSLeopard Patches.cs:29-150), so hull/item/damage/customization sync applies to them
    /// unchanged. What this module bridges is the MOD-OWNED state co-op must sync:
    ///   - Patches.cutterActive (public static bool) - the deployed-rowboat flag, the ONLY thing
    ///     the mod persists (SaveLoadManager.SaveModData postfix, key "com.winter.leopard")
    ///   - Gunports statics (recursive flag + the three port group lists) - one click fans out to
    ///     the whole group and toggles flooding masks with !activeSelf (Patch_OnActivate.cs), so
    ///     the wire carries GROUP intent and receivers force ABSOLUTE mask state afterwards
    ///   - the four controller types, for manual Harmony patches (attribute patches cannot target
    ///     types that may be absent at load).
    /// All reflection resolves once in Init; any failure sets SyncEnabled=false, appends "/noSync"
    /// to the token (so mixed crews still refuse) and disables every data path - the exact
    /// fail-closed contract SECompat established.
    /// </summary>
    public static class LeopardCompat
    {
        public const string LeopardGuid = "com.winter.leopard";
        private const string LeopardAssemblyName = "Leopard";

        public const string LeopardRootName = "BOAT LEOPARD (207)(Clone)";
        public const string CutterRootName = "BOAT CUTTER (212)(Clone)";

        public static bool IsInstalled { get; private set; }
        public static string Version { get; private set; } = "";
        public static bool SyncEnabled { get; private set; }

        public static string ModSignature
        {
            get
            {
                if (!IsInstalled) return "";
                return "LEO=" + Version + (SyncEnabled ? "" : "/noSync");
            }
        }

        // Leopard.Patches statics
        private static FieldInfo _fShip;          // GameObject ship (the Leopard clone)
        private static FieldInfo _fBoat;          // GameObject boat (the cutter clone)
        private static FieldInfo _fCutterActive;  // bool cutterActive
        // Leopard.Controllers.Gunports statics
        private static FieldInfo _fRecursive;     // bool recursive
        private static FieldInfo _fLower;         // List<Transform> lowerGunports
        private static FieldInfo _fUpper;         // List<Transform> upperGunports
        private static FieldInfo _fQuarter;       // List<Transform> quarterGunports
        // Controller types (manual patch targets; patches registered by LeopardSyncManager tasks)
        public static Type CutterControllerType { get; private set; }
        public static Type CutterRopeControllerType { get; private set; }
        public static Type OarControllerType { get; private set; }
        public static Type BellInteractType { get; private set; }
        // OarController members
        private static FieldInfo _fOarForce;      // public float forceAmount
        private static FieldInfo _fOarTurn;       // public float turnForce
        private static FieldInfo _fOarLeft;       // private GameObject leftOar
        private static FieldInfo _fOarRight;      // private GameObject rightOar
        private static MethodInfo _mSetOars;      // private void SetOars(bool)

        public static GameObject LeopardShip => SyncEnabled ? _fShip.GetValue(null) as GameObject : null;
        public static GameObject CutterBoat => SyncEnabled ? _fBoat.GetValue(null) as GameObject : null;

        public static bool GetCutterActive() => SyncEnabled && _fCutterActive.GetValue(null) is bool b && b;
        public static void SetCutterActive(bool value) { if (SyncEnabled) _fCutterActive.SetValue(null, value); }

        public static bool IsGunportFanoutInProgress => SyncEnabled && _fRecursive.GetValue(null) is bool r && r;

        public static float GetOarForceAmount(Component oar) => (float)_fOarForce.GetValue(oar);
        public static float GetOarTurnForce(Component oar) => (float)_fOarTurn.GetValue(oar);
        public static GameObject GetOarLeft(Component oar) => _fOarLeft.GetValue(oar) as GameObject;
        public static GameObject GetOarRight(Component oar) => _fOarRight.GetValue(oar) as GameObject;
        public static void InvokeSetOars(Component oar, bool set) => _mSetOars.Invoke(oar, new object[] { set });

        public static void Init()
        {
            IsInstalled = false;
            Version = "";
            SyncEnabled = false;
            try
            {
                if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(LeopardGuid, out var info) || info == null)
                {
                    Plugin.Log.LogInfo("[LeopardCompat] HMS Leopard not installed.");
                    return;
                }
                IsInstalled = true;
                Version = info.Metadata.Version.ToString();

                var asm = info.Instance != null ? info.Instance.GetType().Assembly : null;
                asm = asm ?? AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == LeopardAssemblyName);
                if (asm == null)
                {
                    Plugin.Log.LogWarning($"[LeopardCompat] HMS Leopard v{Version} registered but its assembly is " +
                        "not loaded (load order). Leopard sync DISABLED; joins still require matching Leopard.");
                    return;
                }

                var patches = asm.GetType("Leopard.Patches");
                _fShip = patches?.GetField("ship", BindingFlags.Public | BindingFlags.Static);
                _fBoat = patches?.GetField("boat", BindingFlags.Public | BindingFlags.Static);
                _fCutterActive = patches?.GetField("cutterActive", BindingFlags.Public | BindingFlags.Static);

                var gunports = asm.GetType("Leopard.Controllers.Gunports");
                _fRecursive = gunports?.GetField("recursive", BindingFlags.Public | BindingFlags.Static);
                _fLower = gunports?.GetField("lowerGunports", BindingFlags.Public | BindingFlags.Static);
                _fUpper = gunports?.GetField("upperGunports", BindingFlags.Public | BindingFlags.Static);
                _fQuarter = gunports?.GetField("quarterGunports", BindingFlags.Public | BindingFlags.Static);

                CutterControllerType = asm.GetType("Leopard.Controllers.CutterController");
                CutterRopeControllerType = asm.GetType("Leopard.Controllers.CutterRopeController");
                OarControllerType = asm.GetType("Leopard.Controllers.OarController");
                BellInteractType = asm.GetType("Leopard.Controllers.LeopardBellInteract");

                _fOarForce = OarControllerType?.GetField("forceAmount", BindingFlags.Public | BindingFlags.Instance);
                _fOarTurn = OarControllerType?.GetField("turnForce", BindingFlags.Public | BindingFlags.Instance);
                _fOarLeft = OarControllerType?.GetField("leftOar", BindingFlags.NonPublic | BindingFlags.Instance);
                _fOarRight = OarControllerType?.GetField("rightOar", BindingFlags.NonPublic | BindingFlags.Instance);
                _mSetOars = OarControllerType?.GetMethod("SetOars", BindingFlags.NonPublic | BindingFlags.Instance);

                SyncEnabled = _fShip != null && _fBoat != null && _fCutterActive != null
                    && _fRecursive != null && _fLower != null && _fUpper != null && _fQuarter != null
                    && CutterControllerType != null && CutterRopeControllerType != null
                    && OarControllerType != null && BellInteractType != null
                    && _fOarForce != null && _fOarTurn != null && _fOarLeft != null && _fOarRight != null
                    && _mSetOars != null;

                if (SyncEnabled)
                    Plugin.Log.LogInfo($"[LeopardCompat] HMS Leopard v{Version} detected; Leopard sync enabled. " +
                        $"Handshake token [{ModSignature}].");
                else
                    Plugin.Log.LogWarning($"[LeopardCompat] HMS Leopard v{Version} detected but its internals " +
                        "changed (reflection surface incomplete). Leopard sync DISABLED; joins still require " +
                        "matching Leopard. Check for a Sailwind Co-op update.");
            }
            catch (Exception e)
            {
                SyncEnabled = false;
                if (IsInstalled)
                    Plugin.Log.LogError($"[LeopardCompat] HMS Leopard v{Version} WAS detected but resolving its " +
                        "internals threw. Leopard sync DISABLED; joins still require matching Leopard. " + e);
                else
                    Plugin.Log.LogWarning("[LeopardCompat] Detection threw before Leopard was identified. " + e);
            }
        }

        /// <summary>
        /// Manual Harmony patches on Leopard's own types (attribute patches cannot reference types
        /// that may be absent). Called from Plugin.Awake AFTER PatchAll, only when SyncEnabled.
        /// The actual patch registrations are added here by the cutter/oar/bell tasks.
        /// </summary>
        public static void ApplyPatches(HarmonyLib.Harmony harmony)
        {
            if (!SyncEnabled) return;
            // Patch registrations land in Tasks 10-12 (cutter, oars, bell).
        }

        // === Gunport group helpers (used by TrapdoorSyncManager) ===

        /// <summary>"lower"/"upper"/"quarter" for a Leopard gunport trapdoor name, else null.</summary>
        public static string GunportGroupOf(string trapdoorName)
        {
            if (!SyncEnabled || trapdoorName == null || !trapdoorName.Contains("gunport")) return null;
            if (trapdoorName.Contains("lower")) return "lower";
            if (trapdoorName.Contains("upper")) return "upper";
            if (trapdoorName.Contains("quarter")) return "quarter";
            return null;
        }

        private static System.Collections.Generic.List<Transform> GroupList(string group)
        {
            FieldInfo f = group == "lower" ? _fLower : group == "upper" ? _fUpper : group == "quarter" ? _fQuarter : null;
            return f?.GetValue(null) as System.Collections.Generic.List<Transform>;
        }

        public static bool? GetGunportGroupOpen(string group)
        {
            var list = GroupList(group);
            if (list == null || list.Count == 0 || list[0] == null) return null;
            var td = list[0].GetComponent<GPButtonTrapdoor>();
            return td != null ? td.IsOpen() : (bool?)null;
        }

        /// <summary>
        /// Toggle the whole group by invoking OnActivate() on ONE port - the Leopard's own prefix
        /// (Patch_OnActivate) then fans out to the siblings and runs the mask/audio/overflow logic,
        /// i.e. the receiver reproduces the sender's exact code path. Returns true when a toggle was
        /// issued (state differed). The caller MUST hold TrapdoorSyncManager.IsApplyingRemoteState
        /// so co-op's own postfix does not echo, and MUST call ForceGunportAbsolutes afterwards.
        /// </summary>
        public static bool ApplyGunportGroup(string group, bool open)
        {
            var list = GroupList(group);
            if (list == null || list.Count == 0 || list[0] == null) return false;
            var td = list[0].GetComponent<GPButtonTrapdoor>();
            if (td == null || td.IsOpen() == open) return false;
            td.OnActivate(); // no-ops while inMotion; TrapdoorSyncManager retries on divergence
            return true;
        }

        /// <summary>
        /// Force the flooding masks / overflow emitters / interior triggers to the ABSOLUTE state
        /// for a group. The mod toggles all of these with !activeSelf (Patch_OnActivate.cs:35-45,
        /// Gunports.ToggleOverflows), so any missed/echoed toggle would INVERT a guest's flooding
        /// forever. Mapping verified against the shipped v1.4.0 prefab's baked m_IsActive states
        /// (closed baseline: half-mask OFF, full-mask ON, lower overflows OFF, upper overflows ON,
        /// both interior triggers ON). Quarter ports have no side effects.
        /// </summary>
        public static void ForceGunportAbsolutes(string group, bool open)
        {
            var ship = LeopardShip;
            if (ship == null) return;
            if (group == "lower")
            {
                SetActivePath(ship.transform, "boat leopard/mask water half", open);
                SetActivePath(ship.transform, "boat leopard/mask water full", !open);
                for (int i = 1; i <= 4; i++)
                    SetActivePath(ship.transform, $"overflow particles lower {i}", open);
                for (int i = 1; i <= 5; i++)
                    SetActivePath(ship.transform, $"overflow particles upper {i}", !open);
                SetActivePath(ship.transform, "boat leopard/structure_container/interior trigger 2", !open);
            }
            else if (group == "upper")
            {
                SetActivePath(ship.transform, "boat leopard/structure_container/interior trigger 3", !open);
            }
            // quarter: fan-out only, no masks/audio/overflows
        }

        private static void SetActivePath(Transform root, string path, bool active)
        {
            var t = root.Find(path);
            if (t != null && t.gameObject.activeSelf != active) t.gameObject.SetActive(active);
        }
    }
}
```

- [ ] **Step 2: Build**

Run the global build command. Expected: `Build succeeded.`

- [ ] **Step 3: Commit**

```bash
git add src/SailwindCoop/Compat/LeopardCompat.cs
git commit -m "feat(compat): LeopardCompat full reflection surface (cutter flag, gunport groups, controller types)"
```

### Task 7: rewire the parity gate to the composed token

**Files:**
- Modify: `src/SailwindCoop/Plugin.cs` (soft dependencies :16-18, Awake init :158-160, config :264-265 plus one new bind, guest layer-1 gate :743-772, handshake send :799-806, host gate :902-956, ack handler :958-977)
- Modify: `src/SailwindCoop/Networking/SteamLobbyManager.cs:236-240`

**Interfaces:**
- Consumes: `CompatRegistry.ModSignature`, `CompatRegistry.DescribeMismatch`, all six modules' `Init()`.
- Produces: `Plugin.AllowModMismatchConfig : ConfigEntry<bool>`; the whole gate now compares the composed token.

- [ ] **Step 1: Stack the soft dependencies**

At `Plugin.cs:16-18`, add the five new attributes under the existing SE one (soft BepInDependency also guarantees those plugins' Awakes run before ours, so their configs are bound by our Init/lazy reads):

```csharp
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    [BepInDependency(Compat.SECompat.SEGuid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(Compat.SCFCompat.SCFGuid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(Compat.NANDTweaksCompat.NTGuid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(Compat.DeepPortsCompat.DPGuid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(Compat.TowableBoatsCompat.TBGuid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(Compat.LeopardCompat.LeopardGuid, BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
```

- [ ] **Step 2: Init all modules in Awake**

Replace `Plugin.cs:158-160` (`// (v0.2.31) Shipyard Expansion soft-detect...` + `Compat.SECompat.Init();`) with:

```csharp
            // (v0.2.32) Mod-compat soft-detect: all six modules must Init in Awake so the composed
            // lobby-data / handshake token (CompatRegistry.ModSignature) is ready before any lobby
            // is created or joined. Order matches the token's segment order for log readability.
            Compat.SECompat.Init();
            Compat.SCFCompat.Init();
            Compat.NANDTweaksCompat.Init();
            Compat.DeepPortsCompat.Init();
            Compat.TowableBoatsCompat.Init();
            Compat.LeopardCompat.Init();
```

- [ ] **Step 3: Add AllowModMismatch and update AllowVersionMismatch's description**

Add the property next to `AllowVersionMismatchConfig` (`Plugin.cs:84`):

```csharp
        // (v0.2.32) Mod-set gate escape hatch, split out of AllowVersionMismatch: one flag unlocking
        // BOTH the version gate and the mod gate was too blunt with six gated mods. Off by default.
        // Checked on BOTH sides, so both peers must enable it to actually play mismatched.
        public static ConfigEntry<bool> AllowModMismatchConfig { get; private set; }
```

Replace the `AllowVersionMismatchConfig` bind at `Plugin.cs:264-265` with (description loses the SE sentence) and add the new bind after it:

```csharp
            AllowVersionMismatchConfig = Config.Bind("Coop", "AllowVersionMismatch", false,
                "Let players on a DIFFERENT mod version join anyway (both sides get a warning instead of a refusal). The network format is not versioned - mixed builds can desync silently or corrupt a session, so leave this off unless you know the two builds are wire-compatible. Both the host and the mismatched guest must enable it. Gameplay-mod differences are gated separately by Coop.AllowModMismatch.");

            AllowModMismatchConfig = Config.Bind("Coop", "AllowModMismatch", false,
                "Let players whose GAMEPLAY MOD SET differs from the host's join anyway (warning instead of refusal). Covers Shipyard Expansion, Sail Collision Fix, NAND Tweaks simulation options, Deep Ports (including its terrain bundle), Towable Boats and HMS Leopard. Mixed mod sets desync physics, terrain and rigs - leave this off unless you know exactly what differs. Both the host and the mismatched guest must enable it.");
```

- [ ] **Step 4: Guest layer-1 gate (lobby data)**

Replace `Plugin.cs:743-772` (the whole `(v0.2.31) MOD-SET GATE` block) with:

```csharp
                    // (v0.2.32) MOD-SET GATE, guest side (layer 1): the composed CompatRegistry token
                    // covers SE + SCF + NAND Tweaks sim vector + Deep Ports (bundle-hashed) + Towable
                    // Boats + HMS Leopard. Refuse before P2P, symmetric in both directions. The token
                    // is OPAQUE for the gate (exact equality); DescribeMismatch splits it for the
                    // MESSAGE only so the user learns which mod differs.
                    var hostMods = LobbyManager.GetLobbyData("mods") ?? "";
                    var ourMods = Compat.CompatRegistry.ModSignature;
                    if (hostMods != ourMods)
                    {
                        string modsMsg = "Mod set mismatch - " +
                            Compat.CompatRegistry.DescribeMismatch(hostMods, ourMods) +
                            ". Everyone must run the same gameplay mods (and the same settings for the flagged ones).";
                        Log.LogError($"[MODS] {modsMsg}");
                        if (AllowModMismatchConfig != null && AllowModMismatchConfig.Value)
                        {
                            Notify(modsMsg + "\n(Coop.AllowModMismatch is on - joining anyway; expect desyncs.)", 10f);
                        }
                        else if (SaveSlots.currentSlot == CoopSave.PhantomSlot)
                        {
                            _joinedAsGuest = true;
                            EndGuestSessionAndQuit(modsMsg);
                            return;
                        }
                        else
                        {
                            Notify(modsMsg, 12f);
                            LobbyManager.LeaveLobby();
                            return;
                        }
                    }
```

- [ ] **Step 5: Handshake send, host gate, ack**

(a) `Plugin.cs:805`: `w.Write(Compat.SECompat.ModSignature);` becomes `w.Write(Compat.CompatRegistry.ModSignature);` (update the comment above it from "version + mod signature" to "version + composed mod-set token").

(b) Host handler: at `:910` and `:918` and `:937` replace `Compat.SECompat.ModSignature` with `Compat.CompatRegistry.ModSignature`. Replace the `allow` line at `:920` with the split gate:

```csharp
                bool allow = (versionMatch || (AllowVersionMismatchConfig != null && AllowVersionMismatchConfig.Value))
                          && (modsMatch || (AllowModMismatchConfig != null && AllowModMismatchConfig.Value));
```

Replace the `what` / `fix` refusal prose at `:928-933` with:

```csharp
                    string what = !versionMatch
                        ? $"is on mod v{version} (you run v{PluginVersion})"
                        : "has a different mod set - " + Compat.CompatRegistry.DescribeMismatch(
                              Compat.CompatRegistry.ModSignature, guestMods);
                    string fix = !versionMatch
                        ? $"Everyone must run v{PluginVersion}."
                        : "Everyone must match the host's gameplay mods.";
```

Note the argument order: on the HOST, "host" = our token, "you" in DescribeMismatch's output refers to the second argument = the guest; the Notify prose reads correctly because the host is the reader ("the host has X / you do not" is inverted for the host's screen - so for the HOST-side message pass `(ourToken, guestToken)` and accept that "you" names the guest in the host's toast; keep it, the log line at `:937` carries the raw tokens for triage).

(c) `:945` (ack trailing field): `w.Write(Compat.CompatRegistry.ModSignature);`

(d) Guest ack handler `:970-976`: replace the SE-specific `reason` else-branch with:

```csharp
                    string reason = version != PluginVersion
                        ? $"Mod version mismatch: the host runs v{version}, you run v{PluginVersion}. Everyone must install the same version."
                        : "Mod set mismatch - " + Compat.CompatRegistry.DescribeMismatch(
                              hostMods, Compat.CompatRegistry.ModSignature) +
                          ". Everyone must run the same gameplay mods.";
```

- [ ] **Step 6: Lobby data**

`SteamLobbyManager.cs:236-240`: replace the SE comment + `SetData` with:

```csharp
                // (v0.2.32) Composed mod-set token (SE + SCF + NAND Tweaks sim vector + Deep Ports
                // bundle hash + Towable Boats + HMS Leopard). Guests pre-check this BEFORE opening
                // P2P, exactly like the version stamp above. Opaque - compare for equality only.
                lobby.SetData("mods", SailwindCoop.Compat.CompatRegistry.ModSignature);
```

- [ ] **Step 7: Build**

Run the global build command. Expected: `Build succeeded.`

- [ ] **Step 8: Commit**

```bash
git add src/SailwindCoop/Plugin.cs src/SailwindCoop/Networking/SteamLobbyManager.cs
git commit -m "feat(compat): tiered mod-parity gate - composed token at all 9 call sites, AllowModMismatch split, per-mod refusal messages"
```

---

## Phase 3: generic trapdoor sync (wire ADDITION, packet 216)

### Task 8: TrapdoorState packet + TrapdoorSyncManager

**Files:**
- Create: `src/SailwindCoop/Networking/Packets/ModCompatPackets.cs`
- Create: `src/SailwindCoop/Sync/TrapdoorSyncManager.cs`
- Create: `src/SailwindCoop/Patches/TrapdoorPatches.cs`
- Modify: `src/SailwindCoop/Networking/Packets/PacketType.cs` (append 216), `PacketSerializer.cs` (append pair), `Plugin.cs` (manager property + AddComponent + handler + join step)

**Interfaces:**
- Produces:
  - `TrapdoorStatePacket { string BoatName; string Key; bool IsOpen; bool IsGunportGroup; }` (`Key` = SyncPathUtil-independent trapdoor key `"{name}~{occ}"`, or the group name `"lower"/"upper"/"quarter"` when `IsGunportGroup`)
  - `PacketSerializer.WriteTrapdoorState/ReadTrapdoorState`
  - `TrapdoorSyncManager` (MonoBehaviour): `Instance`, `bool IsApplyingRemoteState`, `void OnLocalTrapdoorActivated(GPButtonTrapdoor td, bool stateChanged)`, `void OnRemoteTrapdoorState(TrapdoorStatePacket p, SteamId sender)`, `void SendAllStatesTo(SteamId target)`
  - `Plugin.TrapdoorSyncManager` static property.
- Consumes: `SyncPathUtil` is NOT used for the trapdoor key (trapdoors reparent in Awake to `importedActualBoat`; a name+occurrence key over the boat-wide component enumeration is stabler) - see the key functions below. `LeopardCompat.GunportGroupOf/IsGunportFanoutInProgress` (gunport branch stubs in this task; full adapter behavior verified in Task 9).

Semantics (matches the rope/mooring precedent): peer-origin events, host star-relays, receivers apply ABSOLUTE open state (`if (IsOpen() != desired) OnActivate()`), never replay clicks. `GPButtonTrapdoor.OnActivate()` silently no-ops while its private `inMotion` is true, so the applier retries (10 x 0.3s) until the state matches. Join replay sends every trapdoor's state on every boat (the guest's NAND Tweaks `toggleDoors` restore runs from the guest's own phantom save before the snapshot arrives; the host's authoritative states then win).

- [ ] **Step 1: PacketType 216**

Append to `PacketType.cs` after `SERigState = 215,`:

```csharp
        // Trapdoor/door/hatch sync (216, v0.2.32): vanilla GPButtonTrapdoor open/close is purely
        // local (no co-op sync existed for ANY door), and HMS Leopard drives its flooding through 60
        // gunport trapdoors. Carries ABSOLUTE open state keyed by boat name + a name~occurrence key
        // over the boat's trapdoor set (prefab-baked, so the occurrence order matches cross-machine).
        // For Leopard gunports the key is the GROUP ("lower"/"upper"/"quarter") and the receiver
        // reproduces the mod's own fan-out then forces the flooding masks absolute
        // (LeopardCompat.ForceGunportAbsolutes). Peer-origin; host star-relays.
        TrapdoorState = 216,             // Any peer -> all: absolute trapdoor/gunport-group open state
```

- [ ] **Step 2: ModCompatPackets.cs (packet structs for 216-218; 217/218 used by later tasks)**

```csharp
using System;
using UnityEngine;

namespace SailwindCoop.Networking.Packets
{
    /// <summary>(v0.2.32) Absolute trapdoor/door/hatch state. See PacketType.TrapdoorState.</summary>
    [Serializable]
    public struct TrapdoorStatePacket
    {
        public string BoatName;       // root SaveableObject.gameObject.name
        public string Key;            // "{trapdoorName}~{occurrence}" - or the gunport group name when IsGunportGroup
        public bool IsOpen;
        public bool IsGunportGroup;   // true = Key is "lower"/"upper"/"quarter" on the Leopard
    }

    /// <summary>
    /// (v0.2.32) HMS Leopard cutter deploy/recover. IsRequest=true: guest -> host intent (host runs
    /// the mod's own gates by invoking its controller). IsRequest=false: host -> all authoritative
    /// result. Position is REAL (floating-origin-independent) coords.
    /// </summary>
    [Serializable]
    public struct CutterStatePacket
    {
        public bool Active;
        public Vector3 RealPosition;
        public Quaternion Rotation;
        public bool IsRequest;
    }

    /// <summary>
    /// (v0.2.32) Held-key bits from whoever is rowing the Leopard cutter. bit0=MoveUp, bit1=MoveDown,
    /// bit2=MoveLeft, bit3=MoveRight. Host applies force; everyone else animates the oars.
    /// </summary>
    [Serializable]
    public struct OarInputPacket
    {
        public byte KeyBits;
        public ulong AuthorId;
    }
}
```

- [ ] **Step 3: Serializer pair (append at the tail of PacketSerializer.cs, after ReadSERigState)**

```csharp
        // (v0.2.32) Trapdoor state. Write order MUST equal Read order.
        public static void WriteTrapdoorState(BinaryWriter writer, TrapdoorStatePacket packet)
        {
            writer.Write(packet.BoatName ?? "");
            writer.Write(packet.Key ?? "");
            writer.Write(packet.IsOpen);
            writer.Write(packet.IsGunportGroup);
        }

        public static TrapdoorStatePacket ReadTrapdoorState(BinaryReader reader)
        {
            return new TrapdoorStatePacket
            {
                BoatName = reader.ReadString(),
                Key = reader.ReadString(),
                IsOpen = reader.ReadBoolean(),
                IsGunportGroup = reader.ReadBoolean()
            };
        }
```

- [ ] **Step 4: TrapdoorSyncManager.cs**

```csharp
using System.Collections;
using System.Collections.Generic;
using SailwindCoop.Networking.Packets;
using Steamworks;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// (v0.2.32) Generic trapdoor/door/hatch sync - co-op synced NO GoPointerButton of any kind
    /// before this, so doors desynced on vanilla boats too. Design mirrors the mooring sync:
    /// peer-origin local events (TrapdoorPatches postfix) broadcast ABSOLUTE open state, the host
    /// star-relays, receivers converge by invoking OnActivate() only when their local IsOpen()
    /// differs (never a click replay). GPButtonTrapdoor.OnActivate() silently no-ops while its
    /// private inMotion coroutine runs, so the applier retries until the state matches.
    ///
    /// Trapdoor key: "{name}~{occurrence}" over GetComponentsInChildren enumeration order. Trapdoors
    /// are prefab-baked hull children (never part of the runtime-rebuilt sail hierarchy), so the
    /// enumeration order is prefab-deterministic and identical cross-machine - the property the rope
    /// sync had to build GetStableRopeKey to recover, available here for free.
    ///
    /// Leopard gunports ride the SAME packet with IsGunportGroup=true and Key = the group name:
    /// the mod fans one click out to the whole group and toggles the flooding masks with !activeSelf
    /// (HMSLeopard Patch_OnActivate.cs), so per-port packets would multiply and any drift would
    /// INVERT a guest's flooding. Group intent + ForceGunportAbsolutes keeps it convergent.
    /// </summary>
    public class TrapdoorSyncManager : MonoBehaviour
    {
        public static TrapdoorSyncManager Instance { get; private set; }

        public bool IsApplyingRemoteState { get; private set; }

        private const int ApplyRetryAttempts = 10;
        private const float ApplyRetryDelay = 0.3f;

        private void Awake()
        {
            Instance = this;
        }

        // === Key derivation (both directions; MUST stay symmetric) ===

        public static string KeyFor(SaveableObject boat, GPButtonTrapdoor td)
        {
            if (boat == null || td == null) return null;
            var all = boat.GetComponentsInChildren<GPButtonTrapdoor>(true);
            int occ = 0;
            foreach (var t in all)
            {
                if (t == null || t.name != td.name) continue;
                occ++;
                if (t == td) return td.name + "~" + occ;
            }
            return null; // not under this boat
        }

        public static GPButtonTrapdoor FindByKey(SaveableObject boat, string key)
        {
            if (boat == null || string.IsNullOrEmpty(key)) return null;
            int tilde = key.LastIndexOf('~');
            if (tilde <= 0 || !int.TryParse(key.Substring(tilde + 1), out int wantOcc)) return null;
            string name = key.Substring(0, tilde);
            int occ = 0;
            foreach (var t in boat.GetComponentsInChildren<GPButtonTrapdoor>(true))
            {
                if (t == null || t.name != name) continue;
                occ++;
                if (occ == wantOcc) return t;
            }
            return null;
        }

        /// <summary>Resolve the owning boat root for a trapdoor (trapdoors reparent to
        /// importedActualBoat in Awake, which stays inside the boat hierarchy).</summary>
        public static SaveableObject BoatOf(GPButtonTrapdoor td)
            => td != null ? td.GetComponentInParent<SaveableObject>() : null;

        // === Send path (called from TrapdoorPatches postfix) ===

        public void OnLocalTrapdoorActivated(GPButtonTrapdoor td, bool stateChanged)
        {
            if (!Plugin.IsMultiplayer || td == null) return;
            if (IsApplyingRemoteState) return;
            if (!stateChanged) return; // inMotion no-op: nothing to sync

            // PHANTOM-LOAD GATE (same trio as MooringAttachPatch, ControlPatches.cs:479-480): a
            // guest's own load (incl. NAND Tweaks' toggleDoors restore, which fires OnActivate on
            // load from the guest's PHANTOM save) must never broadcast as authoritative.
            if (TitleJoinManager.SuppressLoadErrors || BoatSyncManager.IsJoinInProgress
                || (!Plugin.IsHost && !BoatSyncManager.HasReceivedWorldState)) return;

            var boat = BoatOf(td);
            if (boat == null) return;

            // Leopard gunport? Fan-out sibling calls are suppressed (recursive flag), the ORIGINATING
            // click sends ONE group packet.
            var group = Compat.LeopardCompat.GunportGroupOf(td.name);
            if (group != null && boat.gameObject.name == Compat.LeopardCompat.LeopardRootName)
            {
                if (Compat.LeopardCompat.IsGunportFanoutInProgress) return; // sibling call, not the click
                Send(new TrapdoorStatePacket
                {
                    BoatName = boat.gameObject.name,
                    Key = group,
                    IsOpen = td.IsOpen(),
                    IsGunportGroup = true
                });
                return;
            }

            // Degraded-mode guard: with Leopard INSTALLED but SyncEnabled=false (reflection failed on
            // a future Leopard build), GunportGroupOf returns null and gunports would fall through to
            // per-port sync - and the mod's fan-out would then emit ~24 packets per click whose
            // receiver-side OnActivate re-fans-out. Never sync gunports per-port; drop them here.
            // (Both peers fail reflection identically on the same Leopard version, so both drop.)
            if (Compat.LeopardCompat.IsInstalled && !Compat.LeopardCompat.SyncEnabled
                && td.name.Contains("gunport")) return;

            string key = KeyFor(boat, td);
            if (key == null) return;
            Send(new TrapdoorStatePacket
            {
                BoatName = boat.gameObject.name,
                Key = key,
                IsOpen = td.IsOpen(),
                IsGunportGroup = false
            });
        }

        private void Send(TrapdoorStatePacket packet)
        {
            VerboseLogger.ControlSend($"TrapdoorState, boat={packet.BoatName}, key={packet.Key}, open={packet.IsOpen}, group={packet.IsGunportGroup}");
            Plugin.NetworkManager.SendToAllReliable(PacketType.TrapdoorState, w =>
                PacketSerializer.WriteTrapdoorState(w, packet));
        }

        // === Receive path ===

        public void OnRemoteTrapdoorState(TrapdoorStatePacket packet, SteamId sender)
        {
            VerboseLogger.ControlRecv($"TrapdoorState, boat={packet.BoatName}, key={packet.Key}, open={packet.IsOpen}, group={packet.IsGunportGroup}");

            // STAR host-relay, identical to MooringState (ControlSyncManager.cs:1364-1368).
            if (Plugin.IsHost)
            {
                Plugin.NetworkManager.SendToAllExcept(sender, PacketType.TrapdoorState,
                    w => PacketSerializer.WriteTrapdoorState(w, packet));
            }

            StartCoroutine(ApplyWithRetry(packet));
        }

        private IEnumerator ApplyWithRetry(TrapdoorStatePacket packet)
        {
            for (int attempt = 0; attempt < ApplyRetryAttempts; attempt++)
            {
                if (TryApply(packet)) yield break;
                // inMotion (a door animating on this machine) blocks OnActivate; wait it out.
                yield return new WaitForSecondsRealtime(ApplyRetryDelay);
            }
            Plugin.Log.LogWarning($"[TRAPDOOR] Apply gave up after {ApplyRetryAttempts} attempts: " +
                $"boat={packet.BoatName}, key={packet.Key}, open={packet.IsOpen}");
        }

        /// <summary>One apply attempt. True = local state now matches (or target unresolvable-fatal).</summary>
        private bool TryApply(TrapdoorStatePacket packet)
        {
            var boat = BoatUtility.FindBoatByName(packet.BoatName);
            if (boat == null)
            {
                VerboseLogger.ControlApply($"TrapdoorState FAILED: boat '{packet.BoatName}' not found");
                return true; // fatal, no point retrying
            }

            IsApplyingRemoteState = true;
            try
            {
                if (packet.IsGunportGroup)
                {
                    var current = Compat.LeopardCompat.GetGunportGroupOpen(packet.Key);
                    if (current == null) return true; // Leopard sync off / lists empty: fatal
                    if (current == packet.IsOpen)
                    {
                        // Converged (or already there) - but the masks may have drifted independently;
                        // absolutes are idempotent, force them every time.
                        Compat.LeopardCompat.ForceGunportAbsolutes(packet.Key, packet.IsOpen);
                        return true;
                    }
                    Compat.LeopardCompat.ApplyGunportGroup(packet.Key, packet.IsOpen);
                    bool converged = Compat.LeopardCompat.GetGunportGroupOpen(packet.Key) == packet.IsOpen;
                    if (converged) Compat.LeopardCompat.ForceGunportAbsolutes(packet.Key, packet.IsOpen);
                    return converged;
                }

                var td = FindByKey(boat, packet.Key);
                if (td == null)
                {
                    Plugin.Log.LogWarning($"[TRAPDOOR] No trapdoor '{packet.Key}' on '{packet.BoatName}'");
                    return true; // fatal
                }
                if (td.IsOpen() == packet.IsOpen) return true;
                td.OnActivate(); // no-op while inMotion -> retried by caller
                return td.IsOpen() == packet.IsOpen;
            }
            finally
            {
                IsApplyingRemoteState = false;
            }
        }

        // === Join replay (host -> joiner) ===

        /// <summary>
        /// Send the authoritative state of EVERY trapdoor on every boat to a joining guest. The
        /// guest's own phantom save may have restored doors via NAND Tweaks' toggleDoors before the
        /// snapshot arrived; these reliable, ordered sends win. Leopard gunports collapse to 3 group
        /// packets instead of 24 per-port ones.
        /// </summary>
        public void SendAllStatesTo(SteamId target)
        {
            if (!Plugin.IsMultiplayer) return;
            int sent = 0;
            foreach (var kv in BoatUtility.FindAllBoats())
            {
                var boat = kv.Value;
                bool isLeopard = boat.gameObject.name == Compat.LeopardCompat.LeopardRootName;

                foreach (var td in boat.GetComponentsInChildren<GPButtonTrapdoor>(true))
                {
                    if (td == null) continue;
                    if (isLeopard && Compat.LeopardCompat.GunportGroupOf(td.name) != null) continue; // grouped below
                    string key = KeyFor(boat, td);
                    if (key == null) continue;
                    var p = new TrapdoorStatePacket { BoatName = boat.gameObject.name, Key = key, IsOpen = td.IsOpen(), IsGunportGroup = false };
                    Plugin.NetworkManager.SendReliable(target, PacketType.TrapdoorState, w => PacketSerializer.WriteTrapdoorState(w, p));
                    sent++;
                }

                if (isLeopard)
                {
                    foreach (var group in new[] { "lower", "upper", "quarter" })
                    {
                        var open = Compat.LeopardCompat.GetGunportGroupOpen(group);
                        if (open == null) continue;
                        var p = new TrapdoorStatePacket { BoatName = boat.gameObject.name, Key = group, IsOpen = open.Value, IsGunportGroup = true };
                        Plugin.NetworkManager.SendReliable(target, PacketType.TrapdoorState, w => PacketSerializer.WriteTrapdoorState(w, p));
                        sent++;
                    }
                }
            }
            Plugin.Log.LogInfo($"[TRAPDOOR] Join replay: sent {sent} trapdoor states to {target}");
        }
    }
}
```

- [ ] **Step 5: TrapdoorPatches.cs**

```csharp
using HarmonyLib;
using SailwindCoop.Sync;

namespace SailwindCoop.Patches
{
    /// <summary>
    /// (v0.2.32) Local trapdoor events -> TrapdoorSyncManager. Prefix captures IsOpen() so the
    /// postfix can tell a real toggle from an inMotion no-op (vanilla OnActivate returns silently
    /// while the open/close coroutine runs, GPButtonTrapdoor decomp). The no-arg overload is the one
    /// GPButtonTrapdoor overrides; the coroutine flips `open` synchronously before its first yield,
    /// so the postfix already reads the NEW state.
    /// </summary>
    [HarmonyPatch(typeof(GPButtonTrapdoor), "OnActivate", new System.Type[0])]
    public static class TrapdoorOnActivatePatch
    {
        [HarmonyPrefix]
        public static void Prefix(GPButtonTrapdoor __instance, out bool __state)
        {
            __state = __instance != null && __instance.IsOpen();
        }

        [HarmonyPostfix]
        public static void Postfix(GPButtonTrapdoor __instance, bool __state)
        {
            if (!Plugin.IsMultiplayer || __instance == null) return;
            bool stateChanged = __instance.IsOpen() != __state;
            TrapdoorSyncManager.Instance?.OnLocalTrapdoorActivated(__instance, stateChanged);
        }
    }
}
```

- [ ] **Step 6: Plugin wiring**

(a) Static property next to `ShipyardSyncManager` (`Plugin.cs:98`):

```csharp
        public static TrapdoorSyncManager TrapdoorSyncManager { get; private set; }
```

(b) AddComponent next to the existing block (`Plugin.cs:329`):

```csharp
                TrapdoorSyncManager = gameObject.AddComponent<TrapdoorSyncManager>();
```

(c) Handler registration in `RegisterPacketHandlers`, after the SERigState block (`Plugin.cs:1466-1474` region):

```csharp
            // Trapdoor/door/hatch absolute state (216, v0.2.32). Peer-origin; host star-relays inside
            // the manager. Applies with an inMotion retry (vanilla OnActivate no-ops mid-animation).
            NetworkManager.RegisterHandler(PacketType.TrapdoorState, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadTrapdoorState(reader);
                TrapdoorSyncManager?.OnRemoteTrapdoorState(packet, sender);
            });
```

(d) Join step, immediately after the `SERigState` join step (`Plugin.cs:2297`):

```csharp
            // (v0.2.32) Authoritative door/hatch/gunport states: the guest's phantom load may have
            // restored ITS OWN door states (NAND Tweaks toggleDoors); the host's reliable sends win.
            RunJoinStep("TrapdoorStates", () => TrapdoorSyncManager?.SendAllStatesTo(friend.Id));
```

- [ ] **Step 7: Build**

Run the global build command. Expected: `Build succeeded.`

- [ ] **Step 8: Commit**

```bash
git add src/SailwindCoop/Networking/Packets src/SailwindCoop/Sync/TrapdoorSyncManager.cs src/SailwindCoop/Patches/TrapdoorPatches.cs src/SailwindCoop/Plugin.cs
git commit -m "feat(sync): generic trapdoor/door/hatch sync (packet 216) - absolute state, host relay, join replay, gunport-group aware"
```

---

## Phase 4: Leopard adapter (wire ADDITION, packets 217-219)

### Task 9: gunport adapter verification + hardening

**Files:**
- Modify (only if gaps found): `src/SailwindCoop/Sync/TrapdoorSyncManager.cs`, `src/SailwindCoop/Compat/LeopardCompat.cs`

The gunport behavior was BUILT in Tasks 6+8 (GunportGroupOf / fan-out suppression / group packet / ApplyGunportGroup / ForceGunportAbsolutes). This task is a focused desk-check of the four failure modes the spec calls out, against the actual code as committed:

- [ ] **Step 1: Echo trace.** Walk the code path: guest clicks a lower gunport -> Leopard prefix fans out (recursive=true; sibling postfixes suppressed via `IsGunportFanoutInProgress`) -> clicked port's postfix sends ONE group packet -> host `OnRemoteTrapdoorState` relays to others and applies under `IsApplyingRemoteState` (so the host's own postfix during `ApplyGunportGroup`'s `OnActivate()` is suppressed) -> no echo back. Confirm `IsApplyingRemoteState` is set BEFORE `ApplyGunportGroup` runs and that the Leopard prefix's sibling `OnActivate()` calls during a remote apply are covered by the SAME flag (they are: the flag is instance state on the manager, checked first in `OnLocalTrapdoorActivated`). Document the trace in the commit message.
- [ ] **Step 2: Inversion audit.** Confirm every mask/overflow/trigger write in `ForceGunportAbsolutes` matches the plan's verified prefab-baseline table (Global Constraints section). Confirm `TryApply` forces absolutes on BOTH the toggle path and the already-converged path (drift healing).
- [ ] **Step 3: Audio wrinkle.** The Leopard's `ToggleAudio` transitions `AudioMixers.instance` snapshots on the applying machine (remote toggle briefly touches local audio - accepted in the spec). Confirm `ForceGunportAbsolutes` does NOT try to re-fire audio (it must not: `interior trigger 2/3` SetActive is the state; the snapshot transition self-corrects when the local player next crosses a trigger). Add a code comment in `ForceGunportAbsolutes` noting this accepted wrinkle if not already present.
- [ ] **Step 4: inMotion divergence.** Confirm the retry loop covers a guest clicking a gunport at the same moment a remote group packet arrives (last reliable packet wins; both sides converge on the final absolute state because applies compare-then-toggle). If any of steps 1-4 found a real gap, fix it here with the same patterns; otherwise commit the verification note:

```bash
git add -A src/SailwindCoop
git commit -m "chore(leopard): gunport adapter echo/inversion/audio/race desk-check (fixes if any)" --allow-empty
```

### Task 10: CutterState (packet 217)

**Files:**
- Create: `src/SailwindCoop/Sync/LeopardSyncManager.cs`
- Modify: `PacketType.cs` (217), `PacketSerializer.cs` (pair), `Compat/LeopardCompat.cs` (`ApplyPatches` body + apply helper), `Plugin.cs` (property, AddComponent, `LeopardCompat.ApplyPatches(_harmony)` call, handler, join step)

**Interfaces:**
- Produces: `LeopardSyncManager` (MonoBehaviour): `Instance`, `RequestCutter(bool deploy)`, `OnCutterState(CutterStatePacket p, SteamId sender)`, `BroadcastCutterState()`, `SendCutterStateTo(SteamId)`, `ApplyCutterState(CutterStatePacket p)`; `Plugin.LeopardSyncManager` property.
- Consumes: `LeopardCompat` (Task 6), `BoatSyncManager.RegisterAlwaysStream/Unregister` (Task 4), `BoatUtility.ClearCaches()` (Task 2), `CutterStatePacket` (Task 8's ModCompatPackets.cs).

- [ ] **Step 1: PacketType 217**

```csharp
        // HMS Leopard cutter (217, v0.2.32): the mod deploys/recovers a SECOND full boat with purely
        // local gates (Leopard rigidbody velocity, items-left-aboard child count) that read state a
        // guest does not own. Guests send intent (IsRequest); the host runs the mod's OWN controller
        // (gates included) and broadcasts the authoritative result. Sent to joiners after the world
        // snapshot - the mod persists cutterActive in modData, which co-op does NOT transfer.
        CutterState = 217,               // Guest -> host (IsRequest) / host -> all (authoritative)
```

- [ ] **Step 2: Serializer pair (append)**

```csharp
        // (v0.2.32) Leopard cutter state. Write order MUST equal Read order.
        public static void WriteCutterState(BinaryWriter writer, CutterStatePacket packet)
        {
            writer.Write(packet.Active);
            WriteVector3(writer, packet.RealPosition);
            WriteQuaternion(writer, packet.Rotation);
            writer.Write(packet.IsRequest);
        }

        public static CutterStatePacket ReadCutterState(BinaryReader reader)
        {
            return new CutterStatePacket
            {
                Active = reader.ReadBoolean(),
                RealPosition = ReadVector3(reader),
                Rotation = ReadQuaternion(reader),
                IsRequest = reader.ReadBoolean()
            };
        }
```

(If `ReadVector3`/`ReadQuaternion` private helpers do not exist in PacketSerializer, use the same inline `new Vector3(reader.ReadSingle(), ...)` pattern the file already uses - check `ReadBoatTransform` at :674 and match it.)

- [ ] **Step 3: LeopardSyncManager.cs**

```csharp
using System.Reflection;
using SailwindCoop.Networking.Packets;
using Steamworks;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// (v0.2.32) HMS Leopard runtime sync: cutter deploy/recover (217), oar input (218), bell (219).
    /// Everything here hard no-ops when LeopardCompat.SyncEnabled is false. The cutter is a real
    /// second boat (root "BOAT CUTTER (212)(Clone)"): activating it must invalidate the boat-name
    /// cache (P2) and pin it into the host's always-stream set (P4) or an empty cutter drifts and is
    /// pruned on guests.
    /// </summary>
    public class LeopardSyncManager : MonoBehaviour
    {
        public static LeopardSyncManager Instance { get; private set; }

        private void Awake()
        {
            Instance = this;
        }

        // === Cutter (217) ===

        /// <summary>Guest -> host intent. Called by the guest-side controller prefixes.</summary>
        public void RequestCutter(bool deploy)
        {
            if (!Plugin.IsMultiplayer || Plugin.IsHost) return;
            var p = new CutterStatePacket { Active = deploy, IsRequest = true };
            Plugin.NetworkManager.SendReliable(Plugin.LobbyManager.HostSteamId, PacketType.CutterState,
                w => PacketSerializer.WriteCutterState(w, p));
            VerboseLogger.ControlSend($"CutterState REQUEST deploy={deploy}");
        }

        public void OnCutterState(CutterStatePacket packet, SteamId sender)
        {
            if (!Compat.LeopardCompat.SyncEnabled) return;

            if (packet.IsRequest)
            {
                if (!Plugin.IsHost) return; // requests are host-only business
                // Run the MOD'S OWN controller so its gates (velocity <= 1.5 m/s; items-left-aboard
                // child count) execute on authoritative host state. CutterController.OnActivate never
                // reads its GoPointer parameter (HMSLeopard CutterController.cs:22-56), so null is safe.
                HostRunCutterController(packet.Active);
                // Broadcast whatever ACTUALLY happened (a refused gate = state unchanged; guests
                // converge on the truth either way).
                BroadcastCutterState();
                return;
            }

            // Authoritative state from the host.
            if (Plugin.IsHost) return; // host originated it
            ApplyCutterState(packet);
        }

        /// <summary>Host: invoke the Leopard's own deploy/recover controller (gates included).</summary>
        private void HostRunCutterController(bool deploy)
        {
            var ship = Compat.LeopardCompat.LeopardShip;
            if (ship == null) return;
            if (deploy)
            {
                var t = ship.transform.Find("boat leopard/structure_container/Wooden Rowboat");
                var comp = t != null ? t.GetComponent(Compat.LeopardCompat.CutterControllerType) : null;
                // public override void OnActivate(GoPointer) - parameter unused by the mod.
                Compat.LeopardCompat.CutterControllerType
                    .GetMethod("OnActivate", new[] { typeof(GoPointer) })
                    ?.Invoke(comp, new object[] { null });
            }
            else
            {
                var t = ship.transform.Find("boat leopard/structure_container/rowboat rope");
                var comp = t != null ? t.GetComponent(Compat.LeopardCompat.CutterRopeControllerType) : null;
                // public override void OnActivate() - the no-arg overload.
                Compat.LeopardCompat.CutterRopeControllerType
                    .GetMethod("OnActivate", System.Type.EmptyTypes)
                    ?.Invoke(comp, null);
            }
        }

        /// <summary>Host: broadcast the cutter's current authoritative state to everyone.</summary>
        public void BroadcastCutterState()
        {
            if (!Plugin.IsHost || !Plugin.IsMultiplayer || !Compat.LeopardCompat.SyncEnabled) return;
            var p = CaptureCutterState();
            Plugin.NetworkManager.SendToAllReliable(PacketType.CutterState,
                w => PacketSerializer.WriteCutterState(w, p));
            VerboseLogger.ControlSend($"CutterState BROADCAST active={p.Active}, realPos={p.RealPosition}");
            SyncHostSideEffects(p.Active);
        }

        /// <summary>Host: targeted join replay (modData does not travel; without this the guest's
        /// phantom-save cutterActive silently diverges from the first frame).</summary>
        public void SendCutterStateTo(SteamId target)
        {
            if (!Plugin.IsHost || !Compat.LeopardCompat.SyncEnabled) return;
            var p = CaptureCutterState();
            Plugin.NetworkManager.SendReliable(target, PacketType.CutterState,
                w => PacketSerializer.WriteCutterState(w, p));
        }

        private CutterStatePacket CaptureCutterState()
        {
            var cutter = Compat.LeopardCompat.CutterBoat;
            var offset = FloatingOriginManager.instance?.outCurrentOffset ?? Vector3.zero;
            return new CutterStatePacket
            {
                Active = cutter != null && cutter.activeSelf,
                RealPosition = cutter != null ? cutter.transform.position - offset : Vector3.zero,
                Rotation = cutter != null ? cutter.transform.rotation : Quaternion.identity,
                IsRequest = false
            };
        }

        /// <summary>Guest: apply the host's authoritative cutter state.</summary>
        public void ApplyCutterState(CutterStatePacket packet)
        {
            var cutter = Compat.LeopardCompat.CutterBoat;
            var ship = Compat.LeopardCompat.LeopardShip;
            if (cutter == null || ship == null) return;

            VerboseLogger.ControlApply($"CutterState active={packet.Active}, realPos={packet.RealPosition}");

            if (packet.Active)
            {
                cutter.SetActive(true);
                var offset = FloatingOriginManager.instance?.outCurrentOffset ?? Vector3.zero;
                cutter.transform.SetPositionAndRotation(packet.RealPosition + offset, packet.Rotation);
                // Same instant horizon refresh the mod does on deploy (CutterController.cs:46-49).
                var horizon = cutter.transform.Find("boat cutter")?.GetComponent<BoatHorizon>();
                if (horizon != null)
                    HarmonyLib.AccessTools.Field(typeof(BoatHorizon), "updateCooldown")?.SetValue(horizon, 0f);
            }
            else
            {
                cutter.SetActive(false);
            }

            // Deck prop flip, exactly as the mod does on deploy/recover.
            var rowboatProp = ship.transform.Find("boat leopard/structure_container/Wooden Rowboat");
            var rowboatRope = ship.transform.Find("boat leopard/structure_container/rowboat rope");
            if (rowboatProp != null) rowboatProp.gameObject.SetActive(!packet.Active);
            if (rowboatRope != null) rowboatRope.gameObject.SetActive(packet.Active);

            Compat.LeopardCompat.SetCutterActive(packet.Active);
            SyncHostSideEffects(packet.Active);
        }

        /// <summary>Cache + streaming bookkeeping shared by host broadcast and guest apply.</summary>
        private void SyncHostSideEffects(bool active)
        {
            // (P2) The cutter just entered/left the playable world: rebuild the boat-name map.
            BoatUtility.ClearCaches();
            // (P4, host-only inside the registry) Pin the deployed cutter into the 10Hz stream.
            if (active) BoatSyncManager.RegisterAlwaysStream(Compat.LeopardCompat.CutterRootName);
            else BoatSyncManager.UnregisterAlwaysStream(Compat.LeopardCompat.CutterRootName);
        }
    }
}
```

- [ ] **Step 4: Manual controller patches in LeopardCompat.ApplyPatches**

Replace the `ApplyPatches` body in `LeopardCompat.cs`:

```csharp
        public static void ApplyPatches(HarmonyLib.Harmony harmony)
        {
            if (!SyncEnabled) return;
            try
            {
                // Cutter deploy: guests send intent instead of running the local gates (the velocity
                // gate reads a host-driven interpolated rigidbody; the recover gate reads live item
                // child counts - neither is guest-authoritative). Host runs vanilla + broadcasts.
                harmony.Patch(
                    CutterControllerType.GetMethod("OnActivate", new[] { typeof(GoPointer) }),
                    prefix: new HarmonyLib.HarmonyMethod(typeof(LeopardPatchImpl), nameof(LeopardPatchImpl.CutterDeployPrefix)),
                    postfix: new HarmonyLib.HarmonyMethod(typeof(LeopardPatchImpl), nameof(LeopardPatchImpl.CutterAnyPostfix)));
                harmony.Patch(
                    CutterRopeControllerType.GetMethod("OnActivate", Type.EmptyTypes),
                    prefix: new HarmonyLib.HarmonyMethod(typeof(LeopardPatchImpl), nameof(LeopardPatchImpl.CutterRecoverPrefix)),
                    postfix: new HarmonyLib.HarmonyMethod(typeof(LeopardPatchImpl), nameof(LeopardPatchImpl.CutterAnyPostfix)));
                Plugin.Log.LogInfo("[LeopardCompat] Cutter controller patches applied.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("[LeopardCompat] Manual patching failed; Leopard sync degraded. " + e);
            }
        }

        /// <summary>Manual-patch bodies (attributes can't target maybe-absent types).</summary>
        internal static class LeopardPatchImpl
        {
            public static bool CutterDeployPrefix()
            {
                if (!Plugin.IsMultiplayer || Plugin.IsHost) return true;
                Sync.LeopardSyncManager.Instance?.RequestCutter(true);
                return false; // guest never runs the local gates
            }

            public static bool CutterRecoverPrefix()
            {
                if (!Plugin.IsMultiplayer || Plugin.IsHost) return true;
                Sync.LeopardSyncManager.Instance?.RequestCutter(false);
                return false;
            }

            // Host clicked deploy/recover itself: vanilla ran (gates included); broadcast the result.
            public static void CutterAnyPostfix()
            {
                if (!Plugin.IsMultiplayer || !Plugin.IsHost) return;
                Sync.LeopardSyncManager.Instance?.BroadcastCutterState();
            }
        }
```

- [ ] **Step 5: Plugin wiring**

(a) Property: `public static LeopardSyncManager LeopardSyncManager { get; private set; }`
(b) AddComponent next to TrapdoorSyncManager: `LeopardSyncManager = gameObject.AddComponent<LeopardSyncManager>();`
(c) After `PatchVerifier.Verify(_harmony);` (`Plugin.cs:271`), add:

```csharp
                // (v0.2.32) Manual patches on HMS Leopard's own controller types (attribute patches
                // cannot reference maybe-absent types). Hard no-op when Leopard is absent.
                Compat.LeopardCompat.ApplyPatches(_harmony);
```

(d) Handler:

```csharp
            // Leopard cutter deploy/recover (217, v0.2.32).
            NetworkManager.RegisterHandler(PacketType.CutterState, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadCutterState(reader);
                LeopardSyncManager?.OnCutterState(packet, sender);
            });
```

(e) Join step, after the TrapdoorStates step:

```csharp
            // (v0.2.32) Cutter deployed/stowed + live transform: the mod persists cutterActive in
            // modData, which co-op does NOT transfer - without this send host and guest diverge on
            // the second boat from the first frame.
            RunJoinStep("CutterState", () => LeopardSyncManager?.SendCutterStateTo(friend.Id));
```

- [ ] **Step 6: Build**

Run the global build command. Expected: `Build succeeded.`

- [ ] **Step 7: Commit**

```bash
git add src/SailwindCoop
git commit -m "feat(leopard): cutter deploy/recover sync (packet 217) - guest intent, host runs the mod's own gates, join replay"
```

### Task 11: OarInput (packet 218)

**Files:**
- Modify: `PacketType.cs` (218), `PacketSerializer.cs` (pair), `Compat/LeopardCompat.cs` (ApplyPatches: oar postfix), `Sync/LeopardSyncManager.cs` (send/apply/animate), `Plugin.cs` (handler)

**Interfaces:**
- Produces: `LeopardSyncManager.OnOarInput(OarInputPacket p, SteamId sender)`, `LeopardSyncManager.SampleAndSendOarInput(Component oarController, bool grabbed)`.
- Consumes: `OarInputPacket` (Task 8), `LeopardCompat` oar accessors (Task 6).

Design (plan-time errata #2): the rower's machine runs the mod's `ExtraLateUpdate` unchanged (local prediction + animation). A postfix samples the held-key bits at 10Hz and broadcasts; the HOST applies the same forces to its authoritative cutter each frame while bits are fresh (<0.5s), and every NON-rower machine drives the oar animation from the bits. Multiple rowers are additive, exactly like the unmodified mod. The cutter is streamed whenever someone is aboard (crewed) or deployed (always-stream pin), so the resulting motion reaches everyone.

- [ ] **Step 1: PacketType 218**

```csharp
        // Leopard oars (218, v0.2.32): OarController.ExtraLateUpdate applies AddForce/AddTorque from
        // LOCAL WASD to the cutter rigidbody. The rower keeps its local prediction (reconciled by the
        // boat-transform correction, like wind/buoyancy); these key bits let the HOST apply the same
        // force to the authoritative hull and let everyone else animate the oars. Unreliable, 10Hz,
        // zero-bits sent once on release; host relays to other guests.
        OarInput = 218,                  // Rower -> all (host relays): held-key bits for the cutter oars
```

- [ ] **Step 2: Serializer pair (append)**

```csharp
        // (v0.2.32) Leopard oar input. Write order MUST equal Read order.
        public static void WriteOarInput(BinaryWriter writer, OarInputPacket packet)
        {
            writer.Write(packet.KeyBits);
            writer.Write(packet.AuthorId);
        }

        public static OarInputPacket ReadOarInput(BinaryReader reader)
        {
            return new OarInputPacket
            {
                KeyBits = reader.ReadByte(),
                AuthorId = reader.ReadUInt64()
            };
        }
```

- [ ] **Step 3: Oar postfix in LeopardCompat.ApplyPatches**

Add to the `try` block in `ApplyPatches` (after the cutter patches):

```csharp
                // Oars: sample the rower's held keys AFTER the mod's own frame logic ran. The
                // GoPointerButton grab fields are protected - read via FieldRefAccess on the base.
                harmony.Patch(
                    OarControllerType.GetMethod("ExtraLateUpdate"),
                    postfix: new HarmonyLib.HarmonyMethod(typeof(LeopardPatchImpl), nameof(LeopardPatchImpl.OarPostfix)));
```

And to `LeopardPatchImpl`:

```csharp
            private static readonly HarmonyLib.AccessTools.FieldRef<GoPointerButton, GoPointer> StickyClickedByRef =
                HarmonyLib.AccessTools.FieldRefAccess<GoPointerButton, GoPointer>("stickyClickedBy");
            private static readonly HarmonyLib.AccessTools.FieldRef<GoPointerButton, bool> IsClickedRef =
                HarmonyLib.AccessTools.FieldRefAccess<GoPointerButton, bool>("isClicked");

            public static void OarPostfix(object __instance)
            {
                if (!Plugin.IsMultiplayer) return;
                var button = __instance as GoPointerButton;
                if (button == null) return;
                bool grabbed = StickyClickedByRef(button) != null || IsClickedRef(button);
                Sync.LeopardSyncManager.Instance?.SampleAndSendOarInput(button, grabbed);
            }
```

- [ ] **Step 4: Send/apply/animate in LeopardSyncManager**

Add these members to `LeopardSyncManager`:

```csharp
        // === Oars (218) ===

        private const float OarSendInterval = 0.1f;   // 10 Hz while rowing
        private const float OarFreshSeconds = 0.5f;   // received bits older than this are ignored
        private float _lastOarSend;
        private byte _lastSentBits;
        private Component _oarController;             // cached from the postfix (the paddles button)

        // Received remote input, keyed by author. Additive application matches the unmodified mod
        // (it lets two players grab the same oars and both push).
        private readonly System.Collections.Generic.Dictionary<ulong, (byte bits, float at)> _remoteOars
            = new System.Collections.Generic.Dictionary<ulong, (byte, float)>();
        private float _oarAnimTime; // observer-side animation phase

        private const byte OarUp = 1, OarDown = 2, OarLeft = 4, OarRight = 8;

        /// <summary>Called from the oar ExtraLateUpdate postfix on EVERY machine, every frame.</summary>
        public void SampleAndSendOarInput(Component oarController, bool grabbed)
        {
            _oarController = oarController;

            byte bits = 0;
            if (grabbed)
            {
                if (GameInput.GetKey(InputName.MoveUp)) bits |= OarUp;
                if (GameInput.GetKey(InputName.MoveDown)) bits |= OarDown;
                if (GameInput.GetKey(InputName.MoveLeft)) bits |= OarLeft;
                if (GameInput.GetKey(InputName.MoveRight)) bits |= OarRight;
            }

            // 10Hz while non-zero; one zero-bits packet on release so remotes stop promptly.
            bool due = Time.unscaledTime - _lastOarSend >= OarSendInterval;
            if ((bits != 0 && due) || (bits == 0 && _lastSentBits != 0))
            {
                _lastOarSend = Time.unscaledTime;
                _lastSentBits = bits;
                var p = new OarInputPacket { KeyBits = bits, AuthorId = SteamClient.SteamId.Value };
                Plugin.NetworkManager.SendToAllUnreliable(PacketType.OarInput,
                    w => PacketSerializer.WriteOarInput(w, p));
            }
        }

        public void OnOarInput(OarInputPacket packet, SteamId sender)
        {
            if (!Compat.LeopardCompat.SyncEnabled) return;
            if (packet.AuthorId == SteamClient.SteamId.Value) return; // our own relay echo

            if (Plugin.IsHost)
            {
                Plugin.NetworkManager.SendToAllExcept(sender, PacketType.OarInput,
                    w => PacketSerializer.WriteOarInput(w, packet));
            }
            _remoteOars[packet.AuthorId] = (packet.KeyBits, Time.unscaledTime);
        }

        private void Update()
        {
            if (!Plugin.IsMultiplayer || !Compat.LeopardCompat.SyncEnabled) return;
            if (_remoteOars.Count == 0) return;

            byte combined = 0;
            foreach (var kv in _remoteOars)
                if (Time.unscaledTime - kv.Value.at <= OarFreshSeconds) combined |= kv.Value.bits;

            // HOST: apply the same per-frame forces the mod applies locally (OarController.cs:53-118
            // uses ForceMode.Force in LateUpdate; Update matches that per-render-frame cadence closer
            // than FixedUpdate would). Additive per rower is vanilla-mod behavior.
            if (Plugin.IsHost && combined != 0 && _oarController != null)
            {
                var cutter = Compat.LeopardCompat.CutterBoat;
                var rb = cutter != null ? cutter.GetComponent<Rigidbody>() : null;
                if (rb != null && cutter.activeInHierarchy)
                {
                    float force = Compat.LeopardCompat.GetOarForceAmount(_oarController);
                    float turn = Compat.LeopardCompat.GetOarTurnForce(_oarController);
                    foreach (var kv in _remoteOars)
                    {
                        if (Time.unscaledTime - kv.Value.at > OarFreshSeconds) continue;
                        byte b = kv.Value.bits;
                        if ((b & OarUp) != 0) rb.AddForce(rb.transform.forward * force, ForceMode.Force);
                        if ((b & OarDown) != 0) rb.AddForce(-rb.transform.forward * force, ForceMode.Force);
                        if ((b & OarLeft) != 0) rb.AddTorque(Vector3.up * -force * turn, ForceMode.Force);
                        if ((b & OarRight) != 0) rb.AddTorque(Vector3.up * force * turn, ForceMode.Force);
                    }
                }
            }

            // EVERY non-rower machine: animate the oars from the bits (mirrors OarController's math;
            // rowing phase is cosmetic so an approximation is fine).
            AnimateRemoteOars(combined);
        }

        private void AnimateRemoteOars(byte bits)
        {
            if (_oarController == null) return;
            var left = Compat.LeopardCompat.GetOarLeft(_oarController);
            var right = Compat.LeopardCompat.GetOarRight(_oarController);
            if (left == null || right == null) return;

            bool rowing = bits != 0;
            // Reflected private SetOars(bool): swaps the static paddle mesh for the animated oars.
            Compat.LeopardCompat.InvokeSetOars(_oarController, rowing);
            if (!rowing) return;

            // Same constants as OarController (timeIncrease=3, forwardAngle=30, upAngle=20).
            _oarAnimTime += Time.deltaTime * 3f * (((bits & OarDown) != 0 && (bits & OarUp) == 0) ? -1f : 1f);
            float zAngle = Mathf.Sin(_oarAnimTime) * 30f;
            float xAngle = -(Mathf.Sin(_oarAnimTime + 1.5f) * 20f);
            left.transform.localRotation = Quaternion.Euler(xAngle, 0f, zAngle);
            right.transform.localRotation = Quaternion.Euler(-xAngle, 0f, -zAngle);
        }
```

Also add `using Steamworks;` if not present (it is, from the Task 10 code).

Note: the local ROWER's machine also receives no packets for itself (author check), and its own vanilla animation wins; `AnimateRemoteOars` only runs when someone REMOTE rows. Add one guard at the top of `Update`'s animate call: if the local player currently grabs the oars (`_lastSentBits != 0`), skip `AnimateRemoteOars` (local vanilla animation owns the transforms):

```csharp
            if (_lastSentBits == 0) AnimateRemoteOars(combined);
```

- [ ] **Step 5: Plugin handler**

```csharp
            // Leopard oar input (218, v0.2.32). Unreliable stream; manager relays + applies.
            NetworkManager.RegisterHandler(PacketType.OarInput, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadOarInput(reader);
                LeopardSyncManager?.OnOarInput(packet, sender);
            });
```

- [ ] **Step 6: Build**

Run the global build command. Expected: `Build succeeded.`

- [ ] **Step 7: Commit**

```bash
git add src/SailwindCoop
git commit -m "feat(leopard): oar input relay (packet 218) - host applies authoritative force, observers animate"
```

### Task 12: BellRing (packet 219)

**Files:**
- Modify: `PacketType.cs` (219), `Compat/LeopardCompat.cs` (bell postfix + PlayBell), `Sync/LeopardSyncManager.cs` (OnBellRing), `Plugin.cs` (handler)

**Interfaces:**
- Produces: `LeopardSyncManager.OnBellRing(SteamId sender)`, `LeopardCompat.PlayBell()`.

- [ ] **Step 1: PacketType 219**

```csharp
        // Leopard bell (219, v0.2.32): one-shot "ring the bell" audio event. Receiver plays the
        // bell's own AudioSource directly (never OnActivate - no echo possible). Empty body except
        // the author id; host relays.
        BellRing = 219,                  // Ringer -> all (host relays): play the Leopard's bell
```

- [ ] **Step 2: Bell postfix + PlayBell in LeopardCompat**

In `ApplyPatches`'s try block:

```csharp
                // Bell: broadcast the ring; receivers play the AudioSource directly.
                harmony.Patch(
                    BellInteractType.GetMethod("OnActivate", new[] { typeof(GoPointer) }),
                    postfix: new HarmonyLib.HarmonyMethod(typeof(LeopardPatchImpl), nameof(LeopardPatchImpl.BellPostfix)));
```

In `LeopardPatchImpl`:

```csharp
            public static void BellPostfix()
            {
                if (!Plugin.IsMultiplayer) return;
                ulong self = Steamworks.SteamClient.SteamId.Value;
                Plugin.NetworkManager.SendToAllReliable(
                    Networking.Packets.PacketType.BellRing, w => w.Write(self));
            }
```

New public method on `LeopardCompat`:

```csharp
        /// <summary>Play the Leopard's bell AudioSource (remote ring). Never calls OnActivate.</summary>
        public static void PlayBell()
        {
            var ship = LeopardShip;
            var bell = ship != null ? ship.transform.Find("boat leopard/structure_container/bell") : null;
            var audio = bell != null ? bell.GetComponent<AudioSource>() : null;
            if (audio != null) audio.Play();
        }
```

- [ ] **Step 3: Handler + manager method**

`LeopardSyncManager`:

```csharp
        // === Bell (219) ===

        public void OnBellRing(ulong authorId, SteamId sender)
        {
            if (!Compat.LeopardCompat.SyncEnabled) return;
            if (authorId == SteamClient.SteamId.Value) return; // relay echo of our own ring
            if (Plugin.IsHost)
                Plugin.NetworkManager.SendToAllExcept(sender, PacketType.BellRing, w => w.Write(authorId));
            Compat.LeopardCompat.PlayBell();
        }
```

`Plugin.RegisterPacketHandlers`:

```csharp
            // Leopard bell (219, v0.2.32).
            NetworkManager.RegisterHandler(PacketType.BellRing, (sender, reader) =>
            {
                var authorId = reader.ReadUInt64();
                LeopardSyncManager?.OnBellRing(authorId, sender);
            });
```

- [ ] **Step 4: Build, commit**

Run the global build command (expected: `Build succeeded.`), then:

```bash
git add src/SailwindCoop
git commit -m "feat(leopard): bell ring broadcast (packet 219)"
```

---

## Phase 5: Towable Boats (wire RESHAPE of MooringState + snapshot)

### Task 13: MooringState + NetworkMooringData target reference

**Files:**
- Modify: `src/SailwindCoop/Networking/Packets/ControlPackets.cs:44-51` (MooringStatePacket + new enum)
- Modify: `src/SailwindCoop/Networking/Packets/BoatPackets.cs:8-13` (NetworkMooringData)
- Modify: `src/SailwindCoop/Networking/Packets/PacketSerializer.cs:792-817` (MooringState pair) and the NetworkBoatData mooring block (write ~:295-306, read :388-403)
- Modify: `src/SailwindCoop/Patches/ControlPatches.cs:462-512` (MooringAttachPatch)
- Modify: `src/SailwindCoop/Sync/ControlSyncManager.cs:1299-1528` (OnLocalMooringChanged signature + OnRemoteMooringChanged cleat branch)
- Modify: `src/SailwindCoop/Sync/BoatStateCollector.cs:515-559` (CollectMooringRopes)
- Modify: `src/SailwindCoop/Sync/BoatStateApplicator.cs:1730-1830` (ApplyMooringRopes cleat branch)

**Interfaces:**
- Produces:
  - `public enum MooringTargetKind : byte { Dock = 0, BoatCleat = 1 }` (in ControlPackets.cs)
  - `MooringStatePacket` gains `MooringTargetKind TargetKind; string TowBoatName; string CleatPath;`
  - `NetworkMooringData` gains the same three fields.
  - `ControlSyncManager.OnLocalMooringChanged(string boatName, int ropeIndex, bool isMoored, Vector3 dockPosition, float lengthSquared, MooringTargetKind targetKind, string towBoatName, string cleatPath)` (the old 5-arg overload REMAINS, delegating with `Dock, null, null` - three existing callers at `ControlSyncManager.cs:1354` and `ControlPatches.cs:504,567` keep working; the attach patch switches to the 8-arg form).
- Consumes: `TowableBoatsCompat.HasTowingCleat/IsTowingCleat` (Task 5), `SyncPathUtil` (Task 5).

Key facts encoded here: `spring.spring = towedMass * 6` is baked at MoorTo time and boat mass is client-dependent (+160 kg when the local player stands aboard), so guests must never derive tow parameters - the packet's `LengthSquared` keeps being applied over `currentRopeLengthSquared` AND `spring.maxDistance`, which the existing code already does for docks; the cleat branch reuses it unchanged.

- [ ] **Step 1: Packet structs**

Replace `ControlPackets.cs:44-51` with:

```csharp
    /// <summary>(v0.2.32) Where a moored rope is attached. Towable Boats reuses the vanilla mooring
    /// SpringJoint with the bollard (TowingCleat) on a MOVING boat, so a world-space dock position
    /// is wrong the instant the towing boat moves - boat targets travel as (towBoatName, cleatPath)
    /// references instead.</summary>
    public enum MooringTargetKind : byte { Dock = 0, BoatCleat = 1 }

    [Serializable]
    public struct MooringStatePacket
    {
        public string BoatName;
        public int RopeIndex;     // Index in BoatMooringRopes.ropes[]
        public bool IsMoored;
        public MooringTargetKind TargetKind;
        public Vector3 DockPosition;  // Dock targets only (real coords); zero for boat cleats
        public float LengthSquared;
        public string TowBoatName;    // BoatCleat targets: towing boat root name ("" for docks)
        public string CleatPath;      // BoatCleat targets: SyncPathUtil path from the tow boat root ("" for docks)
    }
```

Replace `BoatPackets.cs:8-13` with:

```csharp
    [Serializable]
    public struct NetworkMooringData
    {
        public bool IsMoored;
        public MooringTargetKind TargetKind;   // (v0.2.32) see MooringStatePacket
        public Vector3 DockPosition;
        public float LengthSquared;
        public string TowBoatName;
        public string CleatPath;
    }
```

- [ ] **Step 2: Serializers (WIRE CHANGE - both places, matching order)**

`WriteMooringState` / `ReadMooringState` (`PacketSerializer.cs:792-817`) become:

```csharp
        public static void WriteMooringState(BinaryWriter writer, MooringStatePacket packet)
        {
            writer.Write(packet.BoatName);
            writer.Write(packet.RopeIndex);
            writer.Write(packet.IsMoored);
            writer.Write((byte)packet.TargetKind);
            writer.Write(packet.DockPosition.x);
            writer.Write(packet.DockPosition.y);
            writer.Write(packet.DockPosition.z);
            writer.Write(packet.LengthSquared);
            writer.Write(packet.TowBoatName ?? "");
            writer.Write(packet.CleatPath ?? "");
        }

        public static MooringStatePacket ReadMooringState(BinaryReader reader)
        {
            return new MooringStatePacket
            {
                BoatName = reader.ReadString(),
                RopeIndex = reader.ReadInt32(),
                IsMoored = reader.ReadBoolean(),
                TargetKind = (MooringTargetKind)reader.ReadByte(),
                DockPosition = new Vector3(
                    reader.ReadSingle(),
                    reader.ReadSingle(),
                    reader.ReadSingle()
                ),
                LengthSquared = reader.ReadSingle(),
                TowBoatName = reader.ReadString(),
                CleatPath = reader.ReadString()
            };
        }
```

In the NetworkBoatData WRITE block (`PacketSerializer.cs` ~:295-306, inside `WriteNetworkBoatData`'s mooring foreach), after `writer.Write(mooring.LengthSquared);` add:

```csharp
                    writer.Write((byte)mooring.TargetKind);
                    writer.Write(mooring.TowBoatName ?? "");
                    writer.Write(mooring.CleatPath ?? "");
```

In the READ block (:388-403), extend the object initializer:

```csharp
                boat.MooringRopes[i] = new NetworkMooringData
                {
                    IsMoored = reader.ReadBoolean(),
                    DockPosition = new Vector3(
                        reader.ReadSingle(),
                        reader.ReadSingle(),
                        reader.ReadSingle()
                    ),
                    LengthSquared = reader.ReadSingle(),
                    TargetKind = (MooringTargetKind)reader.ReadByte(),
                    TowBoatName = reader.ReadString(),
                    CleatPath = reader.ReadString()
                };
```

(Note the snapshot's field order differs from the live packet's - each serializer is self-consistent, which is all the wire requires. Keep each Write/Read pair internally matched.)

- [ ] **Step 3: Attach patch detects cleats**

In `ControlPatches.MooringAttachPatch.Postfix` (`ControlPatches.cs:466-511`), replace the block from `// Convert dock position...` (`:497`) to the `OnLocalMooringChanged` call (`:510`) with:

```csharp
                // (v0.2.32) Tow-aware target: a TowingCleat (Towable Boats; also baked into the
                // Leopard prefab) is a GPButtonDockMooring ON A MOVING BOAT - a world position is
                // stale the moment the tow boat moves, so boat targets travel as a
                // (towBoatName, cleatPath) reference instead.
                var offset = FloatingOriginManager.instance?.outCurrentOffset ?? Vector3.zero;
                if (SailwindCoop.Compat.TowableBoatsCompat.IsTowingCleat(mooring))
                {
                    var towBoat = mooring.GetComponentInParent<SaveableObject>();
                    var cleatPath = towBoat != null
                        ? Sync.SyncPathUtil.GetRelativePath(towBoat.transform, mooring.transform) : null;
                    if (towBoat != null && !string.IsNullOrEmpty(cleatPath))
                    {
                        VerboseLogger.ControlLocal($"Mooring attached to CLEAT, boat={boat.gameObject.name}, rope={ropeIndex}, towBoat={towBoat.gameObject.name}, cleat={cleatPath}");
                        // (P4) Pin the TOWED boat into the stream - an unmanned towed hull otherwise
                        // drifts on every machine (host-side registry; no-op on guests).
                        if (Plugin.IsHost) Sync.BoatSyncManager.RegisterAlwaysStream(boat.gameObject.name);
                        ControlSyncManager.Instance?.OnLocalMooringChanged(
                            boat.gameObject.name, ropeIndex, true, Vector3.zero,
                            __instance.currentRopeLengthSquared,
                            Networking.Packets.MooringTargetKind.BoatCleat,
                            towBoat.gameObject.name, cleatPath);
                        return;
                    }
                    Plugin.Log.LogWarning($"Cleat moor could not derive a path (towBoat={(towBoat != null ? towBoat.gameObject.name : "null")}); falling back to dock-position sync");
                }

                var realDockPos = mooring.transform.position - offset;

                VerboseLogger.ControlLocal($"Mooring attached, boat={boat.gameObject.name}, rope={ropeIndex}, dock={mooring?.name}, realDockPos={realDockPos}");

                ControlSyncManager.Instance?.OnLocalMooringChanged(
                    boat.gameObject.name,
                    ropeIndex,
                    true,
                    realDockPos,
                    __instance.currentRopeLengthSquared
                );
```

Also in `MooringDetachPatch.Postfix` (after the existing `OnLocalMooringChanged(false)` call at `:567-573`), add the always-stream release:

```csharp
                // (v0.2.32, P4) A detached tow releases the always-stream pin (no-op for dock unmoors
                // and for boats never pinned).
                if (Plugin.IsHost) Sync.BoatSyncManager.UnregisterAlwaysStream(boat.gameObject.name);
```

(Unregistering a still-crewed boat is safe: the crewed-avatar scan re-adds it every tick, and a deployed cutter stays pinned via CutterState's own registration.)

- [ ] **Step 4: OnLocalMooringChanged 8-arg overload**

Replace `ControlSyncManager.OnLocalMooringChanged` (`:1299-1317`) with:

```csharp
        public void OnLocalMooringChanged(string boatName, int ropeIndex, bool isMoored,
            Vector3 dockPosition, float lengthSquared)
            => OnLocalMooringChanged(boatName, ropeIndex, isMoored, dockPosition, lengthSquared,
                MooringTargetKind.Dock, null, null);

        public void OnLocalMooringChanged(string boatName, int ropeIndex, bool isMoored,
            Vector3 dockPosition, float lengthSquared,
            MooringTargetKind targetKind, string towBoatName, string cleatPath)
        {
            if (!Plugin.IsMultiplayer) return;

            VerboseLogger.ControlSend($"MooringState, boat={boatName}, rope={ropeIndex}, moored={isMoored}, kind={targetKind}, dockPos={dockPosition}, towBoat={towBoatName}, cleat={cleatPath}");

            var packet = new MooringStatePacket
            {
                BoatName = boatName,
                RopeIndex = ropeIndex,
                IsMoored = isMoored,
                TargetKind = targetKind,
                DockPosition = dockPosition,
                LengthSquared = lengthSquared,
                TowBoatName = towBoatName ?? "",
                CleatPath = cleatPath ?? ""
            };

            Plugin.NetworkManager.SendToAllReliable(PacketType.MooringState, w =>
                PacketSerializer.WriteMooringState(w, packet));
        }
```

- [ ] **Step 5: OnRemoteMooringChanged cleat branch**

In `OnRemoteMooringChanged` (`:1424`), replace the single line `var dock = FindClosestDockMooring(packet.DockPosition, out float nearestMissDist);` with:

```csharp
                    GPButtonDockMooring dock;
                    float nearestMissDist = float.PositiveInfinity;
                    if (packet.TargetKind == MooringTargetKind.BoatCleat)
                    {
                        // (v0.2.32) Cleat reference: resolve towing boat by name, cleat by path. The
                        // rest of the moor apply (Unmoor-before-remoor, authoritative lenSq +
                        // spring.maxDistance overwrite, 50m stretch guard, retry ledger) is SHARED
                        // with docks - the cleat is just a GPButtonDockMooring that happens to move.
                        // Guests must never re-derive spring params: spring = towedMass * 6 is baked
                        // at MoorTo time and mass differs per client (+160 kg local-player term).
                        dock = ResolveCleat(packet.TowBoatName, packet.CleatPath);
                    }
                    else
                    {
                        dock = FindClosestDockMooring(packet.DockPosition, out nearestMissDist);
                    }
```

And add the resolver method next to `FindClosestDockMooring` (`:1534`):

```csharp
        /// <summary>(v0.2.32) Resolve a tow-cleat mooring target from its wire reference.</summary>
        private GPButtonDockMooring ResolveCleat(string towBoatName, string cleatPath)
        {
            var towBoat = BoatUtility.FindBoatByName(towBoatName);
            if (towBoat == null) return null;
            var cleatT = SyncPathUtil.FindByRelativePath(towBoat.transform, cleatPath);
            // TowingCleat IS-A GPButtonDockMooring, so the vanilla component fetch covers both.
            return cleatT != null ? cleatT.GetComponent<GPButtonDockMooring>() : null;
        }
```

The dock-miss retry path (`:1470-1496`) works unchanged for cleats (a cleat can be momentarily unresolvable during island streaming / TB's deferred cleat instantiation; the message interpolates `packet.DockPosition`, which is zero for cleats - extend the warning strings to also print `packet.TowBoatName`/`packet.CleatPath` when `TargetKind == BoatCleat`).

- [ ] **Step 6: Collector fills the reference (join snapshot)**

In `BoatStateCollector.CollectMooringRopes` (`:515-559`), add a `MooredToSpringRef` FieldRef at class scope (mirror `BoatStateApplicator.cs:20-21`):

```csharp
        private static readonly HarmonyLib.AccessTools.FieldRef<PickupableBoatMooringRope, UnityEngine.SpringJoint> MooredToSpringRef =
            HarmonyLib.AccessTools.FieldRefAccess<PickupableBoatMooringRope, UnityEngine.SpringJoint>("mooredToSpring");
```

and replace the loop body's moored branch with:

```csharp
                var rope = mooringRopes.ropes[i];
                bool isMoored = rope.IsMoored();
                Vector3 dockPos = Vector3.zero;
                var targetKind = MooringTargetKind.Dock;
                string towBoatName = "";
                string cleatPath = "";

                if (isMoored)
                {
                    // (v0.2.32) Tow-aware: a rope moored to a TowingCleat serializes a boat+path
                    // reference; a world position on a MOVING bollard would be stale immediately.
                    var spring = MooredToSpringRef(rope);
                    if (spring != null && Compat.TowableBoatsCompat.HasTowingCleat(spring.gameObject))
                    {
                        var towBoat = spring.GetComponentInParent<SaveableObject>();
                        var path = towBoat != null ? SyncPathUtil.GetRelativePath(towBoat.transform, spring.transform) : null;
                        if (towBoat != null && !string.IsNullOrEmpty(path))
                        {
                            targetKind = MooringTargetKind.BoatCleat;
                            towBoatName = towBoat.gameObject.name;
                            cleatPath = path;
                        }
                    }

                    if (targetKind == MooringTargetKind.Dock)
                    {
                        dockPos = rope.transform.position - offset;
                        dockPos.y = boat.transform.position.y - offset.y;
                    }
                }

                data[i] = new NetworkMooringData
                {
                    IsMoored = isMoored,
                    TargetKind = targetKind,
                    DockPosition = dockPos,
                    LengthSquared = rope.currentRopeLengthSquared,
                    TowBoatName = towBoatName,
                    CleatPath = cleatPath
                };
```

(Keep the existing Y-sanitization comment block with the dock branch.)

- [ ] **Step 7: Join applier resolves the reference**

In `BoatStateApplicator.ApplyMooringRopes` (`:1730`), replace `var dock = FindClosestDockMooring(data.DockPosition, out float nearestMissDist);` (`:1749`) with:

```csharp
                    GPButtonDockMooring dock;
                    float nearestMissDist = float.PositiveInfinity;
                    if (data.TargetKind == MooringTargetKind.BoatCleat)
                    {
                        var towBoat = BoatUtility.FindBoatByName(data.TowBoatName);
                        var cleatT = towBoat != null ? SyncPathUtil.FindByRelativePath(towBoat.transform, data.CleatPath) : null;
                        dock = cleatT != null ? cleatT.GetComponent<GPButtonDockMooring>() : null;
                    }
                    else
                    {
                        dock = FindClosestDockMooring(data.DockPosition, out nearestMissDist);
                    }
```

The pre-moor 35m span check (`:1771-1784`) and the authoritative lenSq/maxDistance overwrite (`:1788-1810`) run unchanged for cleats. Add `using SailwindCoop.Networking.Packets;` if the file lacks it (it has it - `NetworkMooringData` is already used).

- [ ] **Step 8: Build**

Run the global build command. Expected: `Build succeeded.`

- [ ] **Step 9: Commit**

```bash
git add src/SailwindCoop
git commit -m "feat(towable): MooringState + join snapshot carry a target reference (dock | boat cleat) - WIRE CHANGE"
```

### Task 14: host-authoritative cleat mooring (trigger suppression + self-tow guard)

**Files:**
- Modify: `src/SailwindCoop/Patches/ControlPatches.cs` (new patch class, after MooringRopeLengthPatch ~:609)

**Interfaces:** none new.

- [ ] **Step 1: Add the OnTriggerEnter guard**

```csharp
        // === TOW-CLEAT TRIGGER GUARD (v0.2.32) ===
        // Vanilla auto-moors an unheld, displaced rope to ANY GPButtonDockMooring collider it touches
        // (decomp PickupableBoatMooringRope.cs:223-233). Towable Boats makes cleats-on-hulls such
        // targets, so a loose rope brushing a passing boat spontaneously creates a TOW on whichever
        // peers happen to run the trigger - including during load, where the save-restore overlap is
        // the mod's (order-dependent, non-deterministic) tow resurrection path. Tows must be
        // host-authoritative: guests never create one locally; the host's MoorTo broadcast
        // (MooringAttachPatch cleat branch) re-creates it for them. Dock triggers keep their existing
        // (playtested) local semantics. Also blocks SELF-tows on every machine: the mod's own guard
        // only covers OnItemClick (TowingCleat.cs:13), not the trigger path, and a SpringJoint whose
        // connectedBody is its own hull is undefined/explosive PhysX.
        [HarmonyPatch(typeof(PickupableBoatMooringRope), "OnTriggerEnter")]
        public static class MooringCleatTriggerGuardPatch
        {
            [HarmonyPrefix]
            public static bool Prefix(PickupableBoatMooringRope __instance, Collider other)
            {
                if (other == null) return true;
                if (!SailwindCoop.Compat.TowableBoatsCompat.HasTowingCleat(other.gameObject)) return true;

                // Self-tow guard (all machines, singleplayer included - it is a real mod bug).
                var cleatBoat = other.GetComponentInParent<SaveableObject>();
                var ropeBoat = __instance.GetBoatRigidbody()?.GetComponent<SaveableObject>();
                if (cleatBoat != null && cleatBoat == ropeBoat)
                {
                    VerboseLogger.ControlLocal($"Blocked SELF-tow trigger moor on {cleatBoat.gameObject.name}");
                    return false;
                }

                if (!Plugin.IsMultiplayer) return true;
                if (Plugin.IsHost) return true; // host is the tow authority

                // Guest: never trigger-moor to a cleat locally; the host's broadcast re-creates real tows.
                VerboseLogger.ControlLocal($"Suppressed guest trigger-moor to cleat {other.name}");
                return false;
            }
        }
```

- [ ] **Step 2: Verify the UnmoorAllRopes double-broadcast is already emergent**

Desk-check (no code): TB's `BoatMooringRopes.UnmoorAllRopes` postfix additionally unmoors FOREIGN ropes tied to this boat's cleats. Each such `Unmoor()` fires co-op's `MooringDetachPatch`, which resolves the rope's OWN boat via `GetBoatRigidbody()` and broadcasts for THAT boat - so "one call changes boat B's state" already broadcasts for B. The phantom-load gates (`:542-543`) suppress it during load on the guest, which is correct (the host's snapshot is authoritative there). Record this trace in the commit message.

- [ ] **Step 3: Build, commit**

Run the global build command (expected: `Build succeeded.`), then:

```bash
git add src/SailwindCoop/Patches/ControlPatches.cs
git commit -m "feat(towable): host-authoritative tow creation - guest cleat trigger-moor suppressed, self-tow blocked"
```

### Task 15: neutralize BoatPerformanceSwitcher on guests

**Files:**
- Modify: `src/SailwindCoop/Patches/BoatPhysicsPatches.cs` (append the new patch class)

**Interfaces:** none new.

Background: TB prefixes `BoatPerformanceSwitcher.Update` (returns false) to force full `BoatProbes` physics on tow-chain boats, keyed off `GameState.lastBoat` - which differs on EVERY client, so host and guests disagree about which hulls run full physics and guest integration fights the authoritative stream unpredictably. On guests, replicate the VANILLA decision and skip both TB's prefix and the original; this also makes TB's `performanceMode` config irrelevant to guests (why it is not in the parity token).

- [ ] **Step 1: Append the patch**

```csharp
        // (v0.2.32) Towable Boats neutralization, guest side. TB's own prefix on
        // BoatPerformanceSwitcher.Update (BoatPerformancePatches.cs:28-42, returns false) forces full
        // BoatProbes physics for boats in the tow chain, derived from GameState.lastBoat - a value
        // that DIFFERS on every client. Host and guests would therefore disagree about which hulls
        // run full physics, and a guest's local integration fights the authoritative transform
        // stream with varying strength. On guests we replicate the VANILLA decision (lastBoat or
        // sunk = full physics, everything else = performance mode) and return false, which also
        // skips TB's prefix (it declares no __runOriginal; HarmonyBefore orders us first). Host and
        // singleplayer keep TB's behavior untouched - the host IS the physics authority.
        [HarmonyPatch(typeof(BoatPerformanceSwitcher), "Update")]
        public static class BoatPerformanceSwitcherGuestPatch
        {
            private static readonly System.Reflection.MethodInfo SetPerformanceMode =
                AccessTools.Method(typeof(BoatPerformanceSwitcher), "SetPerformanceMode");

            [HarmonyPrefix]
            [HarmonyBefore("com.nandbrew.towableboats")]
            public static bool Prefix(BoatPerformanceSwitcher __instance)
            {
                if (!Plugin.IsMultiplayer || Plugin.IsHost) return true;
                if (!SailwindCoop.Compat.TowableBoatsCompat.IsInstalled) return true;

                var damage = __instance.GetComponent<BoatDamage>();
                bool wantFullPhysics = GameState.lastBoat == __instance.transform
                    || (damage != null && damage.sunk);
                bool perfOn = __instance.performanceModeIsOn();

                if (wantFullPhysics && perfOn)
                    SetPerformanceMode?.Invoke(__instance, new object[] { false });
                else if (!wantFullPhysics && !perfOn)
                    SetPerformanceMode?.Invoke(__instance, new object[] { true });

                return false; // skip TB's prefix AND vanilla (we just ran the vanilla decision)
            }
        }
```

- [ ] **Step 2: Build, commit**

Run the global build command (expected: `Build succeeded.`), then:

```bash
git add src/SailwindCoop/Patches/BoatPhysicsPatches.cs
git commit -m "feat(towable): guests run the vanilla performance-mode decision, neutralizing TB's per-client physics divergence"
```

---

## Phase 6: release surface

### Task 16: version bump, changelog, README, playtest checklist

**Files:**
- Modify: `src/SailwindCoop/Plugin.cs:22-32` (version), `CHANGELOG.md`, `README.md` (Compatibility section)
- Create: `docs/plans/2026-07-14-v0.2.32-playtest-checklist.md`

- [ ] **Step 1: Bump the version**

`Plugin.cs:32`: `public const string PluginVersion = "0.2.32";` and update the trailing sentence of the comment block above it to: `// This is the v0.2.32 build (HMS Leopard + mod-compat pass); shows as 0.2.32.`

- [ ] **Step 2: CHANGELOG entry**

Prepend to `CHANGELOG.md` (match the existing entry style; summarize: WIRE CHANGE - MooringState reshape + packets 216-219, all crew must update; tiered mod-parity gate for SCF/NANDTweaks/DeepPorts/TowableBoats/HMSLeopard; generic door/hatch sync; Leopard gunports/cutter/oars/bell; tow-aware mooring; P1-P4 fixes including the live NANDTweaks bailing bug; new config Coop.AllowModMismatch).

- [ ] **Step 3: README Compatibility rows**

In the README's Compatibility section (added in v0.2.31 for SE), add rows for the five mods: HMS Leopard (full support incl. gunports/cutter/oars/bell; hard parity), Sail Collision Fix (hard parity incl. its three options), NAND Tweaks (sim options must match; cosmetics free), Deep Ports (hard parity incl. the terrain bundle itself), Towable Boats (hard parity incl. "Small boats can tow"). Note `Coop.AllowModMismatch` as the escape hatch.

- [ ] **Step 4: Playtest checklist doc**

Create `docs/plans/2026-07-14-v0.2.32-playtest-checklist.md` with the spec's Testing section expanded into concrete steps. It MUST include, verbatim as items:

```markdown
# v0.2.32 live playtest checklist

Two machines minimum (HOST + GUEST). "Refused" means the guest gets the mod-set refusal
message naming the differing mod, and the host sees the refusal toast.

## Parity gate
- [ ] Join refusal both directions for each hard-parity mod (install on one side only):
      HMS Leopard, Sail Collision Fix, Deep Ports, Towable Boats, Shipyard Expansion
- [ ] NAND Tweaks sim-vector mismatch refused (host defaults, guest without the mod) and the
      refusal message names the NT vector
- [ ] NAND Tweaks cosmetic-only difference ACCEPTED (both installed, one with noOutlines/camera
      tweaks flipped, sim options identical)
- [ ] NAND Tweaks "all six sim options off" vs "not installed" ACCEPTED (the tiered-gate promise)
- [ ] Deep Ports bundle-hash mismatch refused (rename/replace one peer's deepports bundle)
- [ ] Deep Ports installed-but-bundle-missing refused (delete the bundle file on one peer)
- [ ] Coop.AllowModMismatch=true on BOTH sides admits a mismatched crew with warnings
## P-fixes
- [ ] Bailing on any boat as a GUEST with NAND Tweaks installed: host water level drops (P1 -
      this is the live pre-0.2.32 bug)
- [ ] Leave lobby, rejoin same host: boats resolve (no stale-cache silent failures) (P2)
- [ ] Guest aboard the CUTTER alone (nobody else on it): no anchor errors in either log (P3),
      cutter position stays synced (P4)
## Trapdoors
- [ ] Open/close a vanilla boat door from host and from guest: other side converges
- [ ] Spam-click a door while a remote state arrives: converges (inMotion retry)
- [ ] Join with doors open on the host: joiner sees them open (NANDTweaks toggleDoors on the
      guest must NOT reintroduce its phantom-save door states)
## Leopard
- [ ] Gunports (lower/upper/quarter) from host and from guest: all ports of the group move on
      both machines; flooding masks/overflow emitters match after REPEATED toggles (no inversion)
- [ ] Water ingress with lower ports open matches host vs guest (flooding rate)
- [ ] Cutter deploy from host; from guest; deploy attempt while Leopard >1.5 m/s is refused
      IDENTICALLY on both (guest gets no local deploy)
- [ ] Cutter recover with items left aboard refused on both; after clearing items, recover works
- [ ] Guest rows the cutter (WASD): motion on host matches, oars animate on all three machines
      (3P: host + rower + observer)
- [ ] Bell rings on all machines
- [ ] Leopard discharge from both shipyards (Al'Ankh + Aestrin) with a guest watching
## Towable Boats
- [ ] Tow the cutter behind the Leopard (rope to a Leopard towing cleat): tow syncs, towed
      cutter tracks on all machines (streams while unmanned - P4)
- [ ] Change tow rope length under way: leash length matches on all machines
- [ ] Unmoor the tow; UnmoorAllRopes path (host loads a save while towing): both boats' rope
      states converge
- [ ] Guest drags a loose mooring rope against a passing boat's cleat: NO local tow forms on
      the guest (host-authoritative)
- [ ] Save, quit, host reloads, guest rejoins with the cutter deployed AND under tow
## Deep Ports + Leopard shared island (the flagged patch-order risk)
- [ ] With BOTH Deep Ports and Leopard installed: approach Gold Rock, anchor near the Leopard
      spawn, verify host and guest agree on grounding/anchor grab (FloatingOriginManager.Start
      prefix order between the two mods is not guaranteed - this is the live test for it)
- [ ] Sleep while a tow is attached (timewarp branch, kinematic interplay) - watch for fights
      between coop sleep sync and TB's non-kinematic tow-chain hold
```

- [ ] **Step 5: Build (final)**

Run the global build command. Expected: `Build succeeded.` Confirm the produced DLL is `src/SailwindCoop/bin/Release/net472/SailwindCoop.dll`.

- [ ] **Step 6: Commit**

```bash
git add src/SailwindCoop/Plugin.cs CHANGELOG.md README.md docs/plans/2026-07-14-v0.2.32-playtest-checklist.md
git commit -m "chore: bump to v0.2.32 - HMS Leopard + mod-compat pass (WIRE CHANGE; all crew must update)"
```

DO NOT deploy to the local game install (the user's install is vanilla on purpose), DO NOT zip, DO NOT push, DO NOT tag - release packaging is a separate user-driven step per the repo's operational rules.

---

## Post-plan notes for the executor

- **Shipyard discharge needs NO code** (verified at plan time): Leopard's `Shipyard.DischargeShip` prefix runs only on the editing machine, `GameState.currentShipyard` is null elsewhere and its try/catch eats the NRE; co-op's ShipyardState freeze/snap already covers the visual. It is on the playtest checklist only.
- **Rope sort key needs NO extension** (verified from the shipped bundle): the Leopard adds no custom `RopeController` subclass.
- If any Harmony patch target fails to resolve at runtime, `PatchVerifier` logs a warning on startup - check `BepInEx/LogOutput.log` after the first launch with the mods installed.
- The `NT=?` / `/cfg?` fail-closed tokens are DELIBERATE mismatches; do not "fix" them to pass.
