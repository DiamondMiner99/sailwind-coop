# Shipyard Expansion Compatibility - Design

Date: 2026-07-13
Status: Approved approach (soft-dependency integration), spec pending user review
Target: sailwind-coop v0.2.31 line (dev worktree sailwind-coop-r4)

## Goal

Full rig sync between Sailwind Co-op and nandbrew's Shipyard Expansion (SE, `com.nandbrew.shipyardexpansion`, v0.9.0): guests see and operate the host's custom rigs (extra masts, moved masts, resized/rotated/flipped/retextured sails, SE part options), synced at join and live during shipyard edits. SE is required on ALL players when any player has it, enforced at admission time.

## Non-goals

- Mixed-install support (SE on some players only). Refused at join instead.
- Syncing SE mods-of-mods or other rig mods.
- Reworking `PushStartPacket.SailIndex` raw-index keying (both sides have identical rigs after sync, so index order matches; revisit only if playtests show desync).

## Why the mods currently break together

1. SE resizes every boat's mast array to `bool[128]` and grows sails/part-option lists. Our arrays are already length-prefixed on the wire (verified in `PacketSerializer.WriteNetworkBoatData` / `WriteShipyardCustomization`), so the only fixed-size assumption is the cosmetic `new bool[30]` default in `BoatStateCollector.CollectBoatData`. No wire format change needed.
2. SE stores per-sail extras (scaleZ, scaleY, angle, flipped, textureIndex) OUTSIDE `SaveBoatCustomizationData`, as a serialized string in `GameState.modData["SEboatSails.{sceneIndex}"]`. Co-op never syncs it, so remote rigs rebuild structurally correct but with default sail size/angle/texture.
3. A player without SE cannot instantiate SE sail prefab indices 156-158 or SE masts/parts at all. Hence the hard requirement.

## Architecture

One new component in the coop plugin, no third DLL, no hard reference to SE:

### 1. `Compat/SECompat.cs` - soft-dependency reflection shim

- Detects SE at `Plugin.Awake` via `BepInEx.Bootstrap.Chainloader.PluginInfos` (GUID `com.nandbrew.shipyardexpansion`); caches `IsInstalled` and `Version`.
- Resolves via reflection, once, with null-tolerant fallbacks:
  - `ShipyardExpansion.SailDataManager.SaveSailConfig(BoatRefs)` (public static)
  - `ShipyardExpansion.SailDataManager.LoadSailConfig(BoatRefs)` (public static)
- Helpers:
  - `string GetRigBlob(BoatRefs refs)`: call `SaveSailConfig(refs)` (SE rewrites the modData entry from live state), then read `GameState.modData["SEboatSails." + sceneIndex]`. Returns null when SE absent or key missing.
  - `void ApplyRigBlob(BoatRefs refs, string blob)`: write blob into `GameState.modData` under the RECEIVER'S local sceneIndex for that boat (never trust the sender's index), then call `LoadSailConfig(refs)`.
- All reflection failures log once and degrade to "SE not installed" behavior; the coop mod must keep working unchanged when SE is absent or its internals moved.

### 2. Handshake extension - "mod set" gate (backward-tolerant, no wire break)

Signature string: `"SE=" + seVersion` when SE installed, `""` otherwise. Exact-match required (SE keys masts by `orderIndex` and textures by load-order index, so SE version skew is a silent-desync risk, not just cosmetic).

- Lobby-data layer (`SteamLobbyManager`): host also sets `lobby.SetData("mods", modSig)`. Guest pre-check in `Plugin.OnLobbyJoined` compares alongside the existing `version` check; mismatch refuses before P2P with a message naming the missing/mismatched mod (e.g. "Host requires Shipyard Expansion v0.9.0").
- Handshake packet layer: guest appends `w.Write(modSig)` after the existing version string. Host reads the second string inside a try/catch (an older guest's packet ends after the version string; treat missing field as `""`). Fold into the existing `match`/`allow` decision; reuse the existing refuse path (`RevokeAdmission` + `RemovePeer`, guest `EndGuestSessionAndQuit`). `Coop.AllowVersionMismatch` also bypasses the mod gate (it is already the "I know what I'm doing" escape hatch).
- Gate is symmetric: refuse when host has SE and guest doesn't, AND when guest has SE and host doesn't (guest-side SE would still mutate local boats to 128-mast arrays and add prefabs; a half-modded session is the untestable case we're eliminating).

### 3. New additive packet: `SERigState = 215`

Fields: `string BoatName; string RigBlob;` (RigBlob = the `SEboatSails` string, boat keyed by NAME on the wire; each side maps name <-> its own sceneIndex).

Senders:
- **Join:** after the host sends `BoatWorldStatePacket`, when SE is installed it sends one `SERigState` per boat that has a non-null blob. Ordering within the Steam P2P channel is preserved, so these arrive after the world snapshot.
- **Live shipyard edits:** `ShipyardSyncManager.PollForChanges` already diffs customization at 5 Hz; extend the diff to also compare the current SE blob (string compare, cheap) and send `SERigState` alongside `ShipyardCustomization` when either changes. Host star-relays guest-originated `SERigState` to other guests, mirroring the existing relay for packet 43.

Receiver (`ShipyardSyncManager.OnSERigStateReceived`):
- Look up boat by name (`BoatUtility.FindBoatByName`); if not found, buffer the blob briefly (join race) keyed by name and retry when the boat resolves.
- If a join apply is in progress for that boat, defer to Phase B (below).
- Otherwise: `SECompat.ApplyRigBlob(refs, blob)` -> `BoatUtility.InvalidateRopeCache(boat)` -> if we are host, `ControlSyncManager.ResendRopeForCurrentBoat()` semantics as with packet 43 (rope lengths re-seed after rig rebuild).

### 4. Join apply ordering (`BoatStateApplicator`)

SE sail extras must apply AFTER the vanilla customization rebuild (which destroys/recreates sails) and BEFORE rope re-keying:

- Phase A: unchanged (`ApplyCustomization` runs `SaveableBoatCustomization.LoadData`; with SE installed, SE's own `LoadData` postfix runs but reads the local, stale modData - harmless, overwritten next step).
- Phase B start: if a buffered `SERigState` blob exists for this boat, `SECompat.ApplyRigBlob` FIRST, then the existing `InvalidateRopeCache` + `ApplyRopeLengths`. SailScaler changes don't add/remove `RopeController`s (scale/angle/flip/texture only), but invalidating after is the safe order regardless.
- The `MastsEnabled` collector default changes from `new bool[30]` to sizing from the boat's actual `GetData().masts` length (works for vanilla 30 and SE 128 alike).

### 5. Version/packaging

- Additive packets only -> no wire version break planned; bump plugin to 0.2.31. v0.2.30 peers can still join a non-SE host per existing rules; when SE is present the mod gate refuses pre-215 clients anyway (their handshake carries no modSig -> treated as "no SE" -> refused by the symmetric gate).
- README/wiki compatibility row: SE supported from v0.2.31, all players must run identical SE versions.

## Error handling

- SE detected but reflection resolution fails (SE internals changed): log a single loud warning, treat as "SE not installed" for sync purposes, but STILL advertise SE in the mod signature (rigs would desync otherwise - better to refuse joins than half-sync). Message tells the user to check for a coop update.
- Malformed/oversized RigBlob: length-cap (64 KB) and try/catch around `ApplyRigBlob`; on failure log and skip - vanilla-structural rig still applies, session survives.
- Boat name not resolvable after retry window (10 s): drop the blob with a log line.
- `GameState.modData` null (pre-load states): guard, retry next poll.

## Testing

- Unit-ish (in-repo harness patterns): serializer round-trip for `SERigState`; handshake modSig parse with legacy (short) handshake payload; blob buffer retry logic.
- Live matrix (playtest):
  1. Host+guest both SE: join with a customized Sanbuq (resized/rotated/flipped/retextured sails, extra mast) -> guest sees identical rig; ropes operable on both.
  2. Live shipyard edit by host while guest watches; and by guest while host watches (star relay).
  3. Host SE, guest no SE -> refused with clear message; reverse case refused too.
  4. Both no SE -> zero behavior change (regression guard).
  5. SE version mismatch -> refused.
  6. Save/load a coop session with SE rigs; rejoin.

## Open items

- `PushStartPacket.SailIndex` raw index: expected fine with identical rigs; verify in playtest 1.
- SE `textureIndex` load-order stability: identical SE versions + identical boats should produce identical texture lists; verify retexture sync in playtest 1.
