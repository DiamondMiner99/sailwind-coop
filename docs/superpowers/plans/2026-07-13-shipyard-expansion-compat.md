# Shipyard Expansion Compatibility Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Full rig sync with nandbrew's Shipyard Expansion (SE): guests see/operate the host's custom rigs (extra masts, resized/rotated/flipped/retextured sails), enforced SE-on-all via the handshake.

**Architecture:** Soft-dependency reflection shim (`SECompat`) - no hard reference to SE. One new additive packet `SERigState = 215` carrying SE's per-boat `SEboatSails` modData blob, keyed by boat NAME on the wire (each side maps to its own local sceneIndex). Handshake and lobby data gain a mod-signature string (backward-tolerant read; no wire break). Spec: `docs/superpowers/specs/2026-07-13-shipyard-expansion-compat-design.md`.

**Tech Stack:** C# net472 BepInEx plugin, HarmonyX, Facepunch.Steamworks. NO unit-test project exists (everything references game DLLs); verification per task = `dotnet build` clean + targeted code review; final task = live playtest checklist.

## Global Constraints

- Build command (run from repo root): `dotnet build src/SailwindCoop/SailwindCoop.csproj -c Release -p:GameDir="C:\Program Files (x86)\Steam\steamapps\common\Sailwind"` - must exit 0 with 0 warnings-as-errors.
- SE GUID: `com.nandbrew.shipyardexpansion`. SE modData key format: `"SEboatSails." + SaveableObject.sceneIndex`.
- Mod signature format: `"SE=" + <SE plugin version>` when SE installed, `""` otherwise. EXACT string match required between peers.
- No em dashes or en dashes in any code comment, log string, or doc text. Use "-".
- Packet 215 is ADDITIVE; do not renumber or modify any existing packet's serialized fields.
- When SE is absent, every new code path must no-op; solo (non-multiplayer) behavior must be untouched.
- Commit after each task; do NOT push (release flow is separate).

---

### Task 1: SECompat reflection shim

**Files:**
- Create: `src/SailwindCoop/Compat/SECompat.cs`
- Modify: `src/SailwindCoop/Plugin.cs` (call `SECompat.Init()` in `Awake`, right after the `Log.LogInfo($"{PluginName} v{PluginVersion} loading...")` line at ~155)

**Interfaces:**
- Produces (used by Tasks 3-5):
  - `static void SECompat.Init()`
  - `static bool SECompat.IsInstalled`
  - `static string SECompat.Version`
  - `static string SECompat.ModSignature` (`"SE=0.9.0"` or `""`)
  - `static string SECompat.GetRigBlob(SaveableObject boat)` (null when SE absent / no blob / reflection broken)
  - `static bool SECompat.ApplyRigBlob(SaveableObject boat, string blob)` (false on any failure; never throws)

- [ ] **Step 1: Write `SECompat.cs`**

```csharp
using System;
using System.Reflection;

namespace SailwindCoop.Compat
{
    /// <summary>
    /// Soft-dependency bridge to nandbrew's Shipyard Expansion (SE). All access is via
    /// reflection so this plugin builds and runs without SE installed. SE persists its
    /// per-sail extras (scale/angle/flip/texture) as a string in
    /// GameState.modData["SEboatSails.{sceneIndex}"] and exposes public static
    /// SailDataManager.SaveSailConfig/LoadSailConfig(BoatRefs) to extract/apply it.
    /// We ship that blob over the wire keyed by boat NAME and re-key to the local
    /// sceneIndex on the receiver (indices can differ between saves).
    /// </summary>
    public static class SECompat
    {
        public const string SEGuid = "com.nandbrew.shipyardexpansion";
        private const string ModDataKeyPrefix = "SEboatSails.";

        public static bool IsInstalled { get; private set; }
        public static string Version { get; private set; } = "";

        /// <summary>Exact-match handshake token. Empty when SE is not installed.</summary>
        public static string ModSignature => IsInstalled ? "SE=" + Version : "";

        private static MethodInfo _saveSailConfig; // SailDataManager.SaveSailConfig(BoatRefs)
        private static MethodInfo _loadSailConfig; // SailDataManager.LoadSailConfig(BoatRefs)
        private static bool _reflectionOk;

        public static void Init()
        {
            IsInstalled = false;
            _reflectionOk = false;
            try
            {
                if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(SEGuid, out var info) || info == null)
                {
                    Plugin.Log.LogInfo("[SECompat] Shipyard Expansion not installed; SE sync disabled.");
                    return;
                }
                IsInstalled = true;
                Version = info.Metadata.Version.ToString();

                var asm = info.Instance != null ? info.Instance.GetType().Assembly : null;
                var sdm = asm != null ? asm.GetType("ShipyardExpansion.SailDataManager") : null;
                _saveSailConfig = sdm != null ? sdm.GetMethod("SaveSailConfig", BindingFlags.Public | BindingFlags.Static) : null;
                _loadSailConfig = sdm != null ? sdm.GetMethod("LoadSailConfig", BindingFlags.Public | BindingFlags.Static) : null;
                _reflectionOk = _saveSailConfig != null && _loadSailConfig != null;

                if (_reflectionOk)
                    Plugin.Log.LogInfo($"[SECompat] Shipyard Expansion v{Version} detected; SE rig sync enabled.");
                else
                    // Still advertise SE in ModSignature: refusing joins is safer than half-synced rigs.
                    Plugin.Log.LogWarning($"[SECompat] Shipyard Expansion v{Version} detected but its internals changed " +
                        "(SailDataManager.Save/LoadSailConfig not found). SE rig sync DISABLED; joins still require " +
                        "matching SE. Check for a Sailwind Co-op update.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[SECompat] SE detection failed: {e.Message}. SE sync disabled.");
            }
        }

        private static string ModDataKey(SaveableObject boat) => ModDataKeyPrefix + boat.sceneIndex;

        /// <summary>
        /// Extract the current SE sail-extras blob for a boat. Calls SE's SaveSailConfig first so
        /// the modData entry reflects LIVE state, then reads it. Null when unavailable.
        /// </summary>
        public static string GetRigBlob(SaveableObject boat)
        {
            if (!_reflectionOk || boat == null || GameState.modData == null) return null;
            var refs = boat.GetComponent<BoatRefs>();
            if (refs == null) return null;
            try
            {
                _saveSailConfig.Invoke(null, new object[] { refs });
                return GameState.modData.TryGetValue(ModDataKey(boat), out var blob) ? blob : null;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[SECompat] GetRigBlob failed for '{boat.gameObject.name}': {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Apply a received SE blob: write it under OUR local sceneIndex for this boat (never trust
        /// the sender's index), then let SE re-read and apply it to the live sails.
        /// Caller must invalidate the rope cache afterwards.
        /// </summary>
        public static bool ApplyRigBlob(SaveableObject boat, string blob)
        {
            if (!_reflectionOk || boat == null || string.IsNullOrEmpty(blob)) return false;
            if (GameState.modData == null) return false;
            var refs = boat.GetComponent<BoatRefs>();
            if (refs == null) return false;
            try
            {
                GameState.modData[ModDataKey(boat)] = blob;
                _loadSailConfig.Invoke(null, new object[] { refs });
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[SECompat] ApplyRigBlob failed for '{boat.gameObject.name}': {e.Message}");
                return false;
            }
        }
    }
}
```

- [ ] **Step 2: Call `Init` from `Plugin.Awake`**

In `src/SailwindCoop/Plugin.cs`, directly after the `Log.LogInfo($"{PluginName} v{PluginVersion} loading...");` line (~155), add:

```csharp
            // (v0.2.31) Shipyard Expansion soft-detect: must run in Awake so the lobby data and
            // handshake mod signature are ready before any lobby is created or joined.
            Compat.SECompat.Init();
```

NOTE: BepInEx chainloader plugin load order is not guaranteed. If testing shows SE is not yet in `Chainloader.PluginInfos` during our `Awake`, add `[BepInDependency(Compat.SECompat.SEGuid, BepInDependency.DependencyFlags.SoftDependency)]` on the `Plugin` class (this forces SE to load first when present and is a no-op otherwise). Include the attribute now; it is the documented BepInEx pattern and costs nothing:

```csharp
    [BepInDependency("com.nandbrew.shipyardexpansion", BepInDependency.DependencyFlags.SoftDependency)]
```
placed directly under the existing `[BepInPlugin(...)]` attribute.

- [ ] **Step 3: Build**

Run: `dotnet build src/SailwindCoop/SailwindCoop.csproj -c Release -p:GameDir="C:\Program Files (x86)\Steam\steamapps\common\Sailwind"`
Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/SailwindCoop/Compat/SECompat.cs src/SailwindCoop/Plugin.cs
git commit -m "feat(se-compat): soft-dependency reflection shim for Shipyard Expansion"
```

---

### Task 2: SERigState packet (type 215) + serializer

**Files:**
- Modify: `src/SailwindCoop/Networking/Packets/PacketType.cs` (append after `CargoWithdrawn = 214,` at line 239)
- Modify: `src/SailwindCoop/Networking/Packets/BoatPackets.cs` (add struct)
- Modify: `src/SailwindCoop/Networking/Packets/PacketSerializer.cs` (add Write/Read pair, in the "Shipyard Packets Serialization" region ~1023)

**Interfaces:**
- Produces (used by Tasks 4-5):
  - `PacketType.SERigState = 215`
  - `struct SERigStatePacket { string BoatName; string RigBlob; }`
  - `static void PacketSerializer.WriteSERigState(BinaryWriter, SERigStatePacket)`
  - `static SERigStatePacket PacketSerializer.ReadSERigState(BinaryReader)`

- [ ] **Step 1: Add the enum member**

In `PacketType.cs`, after `CargoWithdrawn = 214,`:

```csharp
        // Shipyard Expansion rig sync (215, v0.2.31): SE stores per-sail extras (scaleZ/scaleY/
        // angle/flipped/textureIndex) OUTSIDE SaveBoatCustomizationData, as a string in
        // GameState.modData["SEboatSails.{sceneIndex}"]. Structural rig (masts/sails/options)
        // already travels in ShipyardCustomization/BoatWorldState; this carries the SE extras.
        // Boat keyed by NAME on the wire; each side re-keys to its own local sceneIndex.
        // Sent per boat after the join snapshot and on live shipyard edits; host star-relays.
        // Additive wire; only sent when SE is installed (handshake enforces SE parity anyway).
        SERigState = 215,                // Editing peer -> all / host -> joiner: SE sail-extras blob for one boat
```

- [ ] **Step 2: Add the packet struct**

In `BoatPackets.cs`, after the `ShipyardCustomizationPacket` struct (search for it; if it lives in another packets file, put this next to `ShipyardStatePacket` instead - keep shipyard packets together):

```csharp
    /// <summary>(v0.2.31) Shipyard Expansion sail-extras blob for one boat. See PacketType.SERigState.</summary>
    [Serializable]
    public struct SERigStatePacket
    {
        public string BoatName;
        public string RigBlob;
    }
```

- [ ] **Step 3: Add serializer pair**

In `PacketSerializer.cs`, inside the Shipyard Packets Serialization region:

```csharp
        public static void WriteSERigState(BinaryWriter writer, SERigStatePacket packet)
        {
            writer.Write(packet.BoatName ?? "");
            writer.Write(packet.RigBlob ?? "");
        }

        public static SERigStatePacket ReadSERigState(BinaryReader reader)
        {
            return new SERigStatePacket
            {
                BoatName = reader.ReadString(),
                RigBlob = reader.ReadString()
            };
        }
```

- [ ] **Step 4: Build**

Run: `dotnet build src/SailwindCoop/SailwindCoop.csproj -c Release -p:GameDir="C:\Program Files (x86)\Steam\steamapps\common\Sailwind"`
Expected: Build succeeded, 0 errors.

- [ ] **Step 5: Commit**

```bash
git add src/SailwindCoop/Networking/Packets/PacketType.cs src/SailwindCoop/Networking/Packets/BoatPackets.cs src/SailwindCoop/Networking/Packets/PacketSerializer.cs
git commit -m "feat(se-compat): additive SERigState packet (215) + serializer"
```

---

### Task 3: Handshake and lobby mod-signature gate

**Files:**
- Modify: `src/SailwindCoop/Networking/SteamLobbyManager.cs:236` (host stamps lobby data)
- Modify: `src/SailwindCoop/Plugin.cs` guest lobby pre-check (~709-737), guest handshake send (~763), host `Handshake` handler (~859-897), guest `HandshakeAck` handler (~899-909)

**Interfaces:**
- Consumes: `SECompat.ModSignature`, `SECompat.IsInstalled` (Task 1)
- Produces: no new public API; wire behavior only. Handshake body becomes `(string version, string modSig)`; `HandshakeAck` body becomes `(string version, bool allow, string hostModSig)`. Both extra fields are read inside try/catch with `""` fallback so pre-0.2.31 payloads still parse.

- [ ] **Step 1: Host stamps lobby data**

In `SteamLobbyManager.cs` after `lobby.SetData("version", Plugin.PluginVersion);` (line 236):

```csharp
                // (v0.2.31) Mod-set signature (currently: Shipyard Expansion presence + version).
                // Guests pre-check this BEFORE opening P2P, exactly like the version stamp above.
                lobby.SetData("mods", SailwindCoop.Compat.SECompat.ModSignature);
```

- [ ] **Step 2: Guest lobby pre-check**

In `Plugin.cs` `OnLobbyJoined`, inside the existing `if (!IsHost)` block (line 709), AFTER the version-mismatch `if` block closes (line 736) and before the closing brace of `if (!IsHost)`, add a parallel check. Reuse the exact same three refusal paths (allow-config warn / phantom-slot quit / mid-game leave):

```csharp
                    // (v0.2.31) MOD-SET GATE, guest side (layer 1): Shipyard Expansion changes boat
                    // rigs structurally (bool[128] masts, extra sail prefabs); a mixed crew cannot
                    // even instantiate each other's rigs, so refuse before P2P, symmetric in both
                    // directions. Empty string means "no SE on the host" (also what pre-0.2.31
                    // hosts report, since they never set the key - correct: they can't sync SE).
                    var hostMods = LobbyManager.GetLobbyData("mods") ?? "";
                    var ourMods = Compat.SECompat.ModSignature;
                    if (hostMods != ourMods)
                    {
                        string modsMsg = $"Shipyard Expansion mismatch: host has [{(hostMods == "" ? "none" : hostMods)}], you have [{(ourMods == "" ? "none" : ourMods)}]. Everyone must run the same SE version (or nobody).";
                        Log.LogError($"[MODS] {modsMsg}");
                        if (AllowVersionMismatchConfig != null && AllowVersionMismatchConfig.Value)
                        {
                            Notify(modsMsg + "\n(Coop.AllowVersionMismatch is on - joining anyway; expect desyncs.)", 10f);
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

- [ ] **Step 3: Guest handshake send**

Replace line 763:

```csharp
                    NetworkManager.SendReliable(hostId, PacketType.Handshake, w => w.Write(PluginVersion));
```

with:

```csharp
                    // (v0.2.31) Handshake body: version + mod signature. Older hosts read only the
                    // version string and ignore the trailing bytes (per-packet framing), so this is
                    // not a wire break.
                    NetworkManager.SendReliable(hostId, PacketType.Handshake, w =>
                    {
                        w.Write(PluginVersion);
                        w.Write(Compat.SECompat.ModSignature);
                    });
```

- [ ] **Step 4: Host handshake handler**

In the `PacketType.Handshake` handler (line 859), after `var version = reader.ReadString();` add the tolerant second read, and fold it into the decision. Replace lines 861-887 body accordingly:

```csharp
                var version = reader.ReadString();
                // (v0.2.31) Tolerant read: a pre-0.2.31 guest's handshake ends after the version
                // string; treat a missing field as "no SE" - the symmetric compare below then
                // refuses them exactly when this host runs SE (they could not sync SE anyway).
                string guestMods = "";
                try { guestMods = reader.ReadString(); } catch { /* legacy short payload */ }
                Log.LogInfo($"[VERSION] Handshake from {sender}: version {version} (ours {PluginVersion}), mods [{guestMods}] (ours [{Compat.SECompat.ModSignature}])");

                if (!IsHost) return;
                _versionHandshaked.Add(sender);

                bool versionMatch = version == PluginVersion;
                bool modsMatch = guestMods == Compat.SECompat.ModSignature;
                bool match = versionMatch && modsMatch;
                bool allow = match || (AllowVersionMismatchConfig != null && AllowVersionMismatchConfig.Value);

                string guestName = sender.ToString();
                foreach (var member in LobbyManager.LobbyMembers)
                    if (member.Id == sender) { guestName = member.Name; break; }

                if (!match)
                {
                    string what = !versionMatch
                        ? $"is on mod v{version} (you run v{PluginVersion})"
                        : $"has Shipyard Expansion [{(guestMods == "" ? "none" : guestMods)}] (you have [{(Compat.SECompat.ModSignature == "" ? "none" : Compat.SECompat.ModSignature)}])";
                    Notify(allow
                        ? $"{guestName} {what} - allowed by Coop.AllowVersionMismatch; expect desyncs."
                        : $"{guestName} {what} - refused. Everyone must match the host's mod set.", 10f);
                    Log.LogWarning($"[VERSION] {guestName} ({sender}) version {version} mods [{guestMods}] vs host {PluginVersion} [{Compat.SECompat.ModSignature}]: {(allow ? "ALLOWED by config" : "REFUSED")}");
                }

                // Ack BEFORE any revoke, or the refusal could never reach the guest.
                NetworkManager.SendReliable(sender, PacketType.HandshakeAck, w =>
                {
                    w.Write(PluginVersion);
                    w.Write(allow);
                    w.Write(Compat.SECompat.ModSignature); // (v0.2.31) trailing field, old guests ignore
                });
```

(The `if (!allow) { RevokeAdmission; RemovePeer; }` block at 889-896 stays unchanged.)

- [ ] **Step 5: Guest HandshakeAck handler**

Replace the handler body (lines 899-909):

```csharp
            NetworkManager.RegisterHandler(PacketType.HandshakeAck, (sender, reader) =>
            {
                var version = reader.ReadString();
                var accepted = reader.ReadBoolean();
                string hostMods = "";
                try { hostMods = reader.ReadString(); } catch { /* pre-0.2.31 host */ }
                Log.LogInfo($"[VERSION] Handshake response from {sender}: version {version}, mods [{hostMods}], accepted: {accepted}");

                // (v0.2.27) The host refused us - quit cleanly instead of playing a half-admitted
                // session (the host has already revoked our admission). (v0.2.31) Name the actual
                // mismatch: version when versions differ, otherwise the SE mod set.
                if (!IsHost && !accepted)
                {
                    string reason = version != PluginVersion
                        ? $"Mod version mismatch: the host runs v{version}, you run v{PluginVersion}. Everyone must install the same version."
                        : $"Shipyard Expansion mismatch: host has [{(hostMods == "" ? "none" : hostMods)}], you have [{(Compat.SECompat.ModSignature == "" ? "none" : Compat.SECompat.ModSignature)}]. Everyone must run the same SE version (or nobody).";
                    EndGuestSessionAndQuit(reason);
                }
            });
```

- [ ] **Step 6: Build**

Run: `dotnet build src/SailwindCoop/SailwindCoop.csproj -c Release -p:GameDir="C:\Program Files (x86)\Steam\steamapps\common\Sailwind"`
Expected: Build succeeded, 0 errors.

- [ ] **Step 7: Commit**

```bash
git add src/SailwindCoop/Networking/SteamLobbyManager.cs src/SailwindCoop/Plugin.cs
git commit -m "feat(se-compat): symmetric Shipyard Expansion gate in lobby data + handshake"
```

---

### Task 4: Live shipyard SE-blob sync (send, relay, receive, retry buffer)

**Files:**
- Modify: `src/SailwindCoop/Sync/ShipyardSyncManager.cs` (blob diff in poll, send, receive handler, pending-blob retry buffer, Reset)
- Modify: `src/SailwindCoop/Plugin.cs` (register the `SERigState` handler in `RegisterPacketHandlers`, next to the existing `ShipyardCustomization`/`ShipyardState` registrations - search `PacketType.ShipyardCustomization` in `RegisterPacketHandlers`)

**Interfaces:**
- Consumes: `SECompat.GetRigBlob/ApplyRigBlob` (Task 1), `SERigStatePacket` + `PacketSerializer.WriteSERigState/ReadSERigState` + `PacketType.SERigState` (Task 2)
- Produces (used by Task 5):
  - `void ShipyardSyncManager.OnSERigStateReceived(SERigStatePacket packet, Steamworks.SteamId sender = default)`
  - `void ShipyardSyncManager.SendAllRigBlobsTo(Steamworks.SteamId target)` (host, join step)
  - `static bool ShipyardSyncManager.TryConsumePendingRigBlob(string boatName, out string blob)` (join Phase B pulls the buffered blob)

- [ ] **Step 1: Add fields and buffer to `ShipyardSyncManager`**

After the `_lastPartOptions` field (line 24):

```csharp
        private string _lastRigBlob;          // (v0.2.31) SE sail-extras blob for the boat being edited

        // (v0.2.31) SERigState blobs that arrived before their boat was resolvable (join race) or
        // during a join apply (must land AFTER LoadData rebuilds the sails - consumed by join
        // Phase B via TryConsumePendingRigBlob). Value = (blob, arrival time); entries expire.
        private static readonly Dictionary<string, KeyValuePair<string, float>> _pendingRigBlobs =
            new Dictionary<string, KeyValuePair<string, float>>();
        private const float PendingRigBlobTtl = 10f;
        private const int MaxRigBlobBytes = 64 * 1024;
```

- [ ] **Step 2: Diff + send in `PollForChanges`**

In `PollForChanges` (line 155), the existing flow returns early when `!HasCustomizationChanged(data)`. SE blob changes (pure resize/rotate/flip/retexture) DO change `scaleY/scaleZ` in vanilla data in some cases but angle/flip/texture do NOT, so the blob needs its own diff that runs even when vanilla data is unchanged. Restructure the tail of `PollForChanges` (from the `if (!HasCustomizationChanged(data))` check through the end of the method) to:

```csharp
            bool vanillaChanged = HasCustomizationChanged(data);

            // (v0.2.31) SE sail extras (angle/flip/texture) change WITHOUT touching vanilla data,
            // so the blob gets its own diff. GetRigBlob returns null when SE is absent - the
            // string compare is then null==null and this stays dormant.
            string rigBlob = Compat.SECompat.GetRigBlob(boat);
            bool rigChanged = rigBlob != _lastRigBlob;

            if (!vanillaChanged && !rigChanged)
            {
                VerboseLogger.ShipyardPoll($"No changes, masts={data.masts?.Count(m => m) ?? 0}, sails={data.sails?.Count ?? 0}", throttle: true);
                return;
            }

            if (vanillaChanged)
            {
                SendCustomizationUpdate(boat, data);
                CacheState(data);
            }

            // Send the blob AFTER the structural packet: receivers apply 43 (LoadData rebuild)
            // first, then 215 lands on the freshly built sails. Reliable channel preserves order.
            if (rigChanged || (vanillaChanged && !string.IsNullOrEmpty(rigBlob)))
            {
                SendRigBlob(boat, rigBlob);
                _lastRigBlob = rigBlob;
            }

            if (vanillaChanged)
            {
                BoatUtility.InvalidateRopeCache(boat);
            }
```

(Keep the existing phantom-furled-sails comment block above the `InvalidateRopeCache` call - move it together with the call.)

- [ ] **Step 3: Add `SendRigBlob`, `SendAllRigBlobsTo`, `OnSERigStateReceived`, `TryConsumePendingRigBlob`, and pending-buffer retry**

Add after `SendCustomizationUpdate` (line 268):

```csharp
        /// <summary>(v0.2.31) Broadcast the SE sail-extras blob for one boat. No-op when null/empty.</summary>
        private void SendRigBlob(SaveableObject boat, string rigBlob)
        {
            if (string.IsNullOrEmpty(rigBlob)) return;
            var packet = new SERigStatePacket { BoatName = boat.gameObject.name, RigBlob = rigBlob };
            VerboseLogger.ShipyardSend($"SERigState, boat={packet.BoatName}, blobLen={rigBlob.Length}");
            Plugin.NetworkManager.SendToAllReliable(PacketType.SERigState, w =>
                PacketSerializer.WriteSERigState(w, packet));
        }

        /// <summary>
        /// (v0.2.31) HOST, join step: send every boat's SE blob to the joining guest, right after
        /// the BoatWorldState snapshot (same reliable channel, so ordering is preserved and the
        /// blobs arrive while the guest's join apply is buffering them for Phase B).
        /// </summary>
        public void SendAllRigBlobsTo(Steamworks.SteamId target)
        {
            if (!Compat.SECompat.IsInstalled) return;
            foreach (var kv in BoatUtility.FindAllBoats())
            {
                var blob = Compat.SECompat.GetRigBlob(kv.Value);
                if (string.IsNullOrEmpty(blob)) continue;
                var packet = new SERigStatePacket { BoatName = kv.Key, RigBlob = blob };
                VerboseLogger.ShipyardSend($"SERigState (join) -> {target}, boat={kv.Key}, blobLen={blob.Length}");
                Plugin.NetworkManager.SendReliable(target, PacketType.SERigState, w =>
                    PacketSerializer.WriteSERigState(w, packet));
            }
        }

        /// <summary>
        /// (v0.2.31) A SERigState packet arrived. Host star-relays (same pattern as
        /// OnCustomizationReceived). Apply immediately when the boat resolves and no join is in
        /// flight; otherwise buffer for join Phase B / the Update retry sweep.
        /// </summary>
        public void OnSERigStateReceived(SERigStatePacket packet, Steamworks.SteamId sender = default)
        {
            VerboseLogger.ShipyardRecv($"SERigState, boat={packet.BoatName}, blobLen={packet.RigBlob?.Length ?? 0}");

            if (string.IsNullOrEmpty(packet.BoatName) || string.IsNullOrEmpty(packet.RigBlob)) return;
            if (packet.RigBlob.Length > MaxRigBlobBytes)
            {
                Plugin.Log.LogWarning($"[SECompat] Oversized SERigState blob for '{packet.BoatName}' ({packet.RigBlob.Length} chars) - dropped.");
                return;
            }

            if (Plugin.IsHost)
                Plugin.NetworkManager.SendToAllExcept(sender, PacketType.SERigState, w =>
                    PacketSerializer.WriteSERigState(w, packet));

            // During a join apply, Phase A's LoadData would destroy whatever we apply now; park the
            // blob for Phase B (TryConsumePendingRigBlob).
            var boat = BoatUtility.FindBoatByName(packet.BoatName);
            if (boat == null || BoatSyncManager.IsJoinInProgress)
            {
                _pendingRigBlobs[packet.BoatName] =
                    new KeyValuePair<string, float>(packet.RigBlob, Time.unscaledTime);
                return;
            }

            ApplyRigBlobNow(boat, packet.RigBlob);
        }

        /// <summary>(v0.2.31) Join Phase B: pull the buffered blob for a boat, if any.</summary>
        public static bool TryConsumePendingRigBlob(string boatName, out string blob)
        {
            blob = null;
            if (string.IsNullOrEmpty(boatName)) return false;
            if (!_pendingRigBlobs.TryGetValue(boatName, out var entry)) return false;
            _pendingRigBlobs.Remove(boatName);
            blob = entry.Key;
            return true;
        }

        private void ApplyRigBlobNow(SaveableObject boat, string blob)
        {
            if (Compat.SECompat.ApplyRigBlob(boat, blob))
            {
                BoatUtility.InvalidateRopeCache(boat);
                // Keep our own outgoing diff quiet if we are at a shipyard on the same boat.
                if (GameState.currentShipyard != null && BoatUtility.GetCurrentBoat() == boat)
                    _lastRigBlob = blob;
                VerboseLogger.ShipyardApply($"SERigState applied, boat={boat.gameObject.name}");
            }
        }
```

- [ ] **Step 4: Retry sweep + cache lifecycle**

In `Update()`, after the `PollForChanges` block (after line 127 `}` of `if (atShipyard)`), add:

```csharp
            // (v0.2.31) Retry buffered SE blobs (join race: blob arrived before its boat resolved).
            // Cheap when empty. Entries expire after PendingRigBlobTtl.
            if (_pendingRigBlobs.Count > 0 && !BoatSyncManager.IsJoinInProgress)
            {
                List<string> done = null;
                foreach (var kv in _pendingRigBlobs)
                {
                    if (Time.unscaledTime - kv.Value.Value > PendingRigBlobTtl)
                    {
                        Plugin.Log.LogWarning($"[SECompat] Pending SERigState for '{kv.Key}' expired unapplied.");
                        (done = done ?? new List<string>()).Add(kv.Key);
                        continue;
                    }
                    var boat = BoatUtility.FindBoatByName(kv.Key);
                    if (boat == null) continue;
                    ApplyRigBlobNow(boat, kv.Value.Key);
                    (done = done ?? new List<string>()).Add(kv.Key);
                }
                if (done != null) foreach (var k in done) _pendingRigBlobs.Remove(k);
            }
```

In `OnEnterShipyard()` (line 131) add `_lastRigBlob = Compat.SECompat.GetRigBlob(BoatUtility.GetCurrentBoat());` after `CacheCurrentState();`. In `OnExitShipyard()` (line 138) add `_lastRigBlob = null;` next to the other cache clears. In `Reset()` (line 457) add `_lastRigBlob = null;` and `_pendingRigBlobs.Clear();`.

- [ ] **Step 5: Register the packet handler in `Plugin.RegisterPacketHandlers`**

Next to the existing `PacketType.ShipyardCustomization` registration (search for it in `RegisterPacketHandlers`), add:

```csharp
            // (v0.2.31) Shipyard Expansion sail-extras blob (see ShipyardSyncManager).
            NetworkManager.RegisterHandler(PacketType.SERigState, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadSERigState(reader);
                ShipyardSyncManager.Instance?.OnSERigStateReceived(packet, sender);
            });
```

- [ ] **Step 6: Build**

Run: `dotnet build src/SailwindCoop/SailwindCoop.csproj -c Release -p:GameDir="C:\Program Files (x86)\Steam\steamapps\common\Sailwind"`
Expected: Build succeeded, 0 errors.

- [ ] **Step 7: Commit**

```bash
git add src/SailwindCoop/Sync/ShipyardSyncManager.cs src/SailwindCoop/Plugin.cs
git commit -m "feat(se-compat): live SERigState sync with star relay and retry buffer"
```

---

### Task 5: Join-state integration (host send step + guest Phase B apply)

**Files:**
- Modify: `src/SailwindCoop/Plugin.cs` (~2213, the `RunJoinStep` ladder)
- Modify: `src/SailwindCoop/Sync/BoatStateApplicator.cs:596-605` (`ApplyBoatStatePhaseB`)

**Interfaces:**
- Consumes: `ShipyardSyncManager.SendAllRigBlobsTo`, `ShipyardSyncManager.TryConsumePendingRigBlob` (Task 4), `SECompat.ApplyRigBlob` (Task 1)

- [ ] **Step 1: Host join step**

In `Plugin.cs`, directly after `RunJoinStep("BoatWorldState", ...)` (line 2213):

```csharp
            // (v0.2.31) Shipyard Expansion sail extras: per-boat SERigState blobs, sent right after
            // the world snapshot on the same reliable channel. The guest's handler buffers them
            // while IsJoinInProgress; join Phase B consumes them after LoadData rebuilds the sails.
            RunJoinStep("SERigState", () => ShipyardSyncManager.Instance?.SendAllRigBlobsTo(friend.Id));
```

- [ ] **Step 2: Guest Phase B apply**

Replace `ApplyBoatStatePhaseB` (BoatStateApplicator.cs:596-605) with:

```csharp
        public static void ApplyBoatStatePhaseB(SaveableObject boat, NetworkBoatData data)
        {
            Plugin.Log.LogDebug($"Applying state to boat {data.Name} (Phase B - rope lengths)");

            // (v0.2.31) Shipyard Expansion sail extras FIRST: Phase A's LoadData rebuilt the sails
            // at default scale/angle; the buffered SERigState blob (if any) restores the custom
            // rig before ropes are re-keyed. SailScaler changes never add/remove RopeControllers,
            // but applying before the invalidate below keeps the ordering obviously safe.
            if (Sync.ShipyardSyncManager.TryConsumePendingRigBlob(data.Name, out var rigBlob))
            {
                if (Compat.SECompat.ApplyRigBlob(boat, rigBlob))
                    Plugin.Log.LogInfo($"[JOIN] SE rig extras applied to '{data.Name}'");
            }

            // Invalidate rope cache - old RopeControllers are now actually destroyed
            BoatUtility.InvalidateRopeCache(boat);

            // Apply rope lengths (now only new ropes exist)
            ApplyRopeLengths(boat, data.RopeLengths);
        }
```

(If `BoatStateApplicator` already has `using SailwindCoop.Sync;` / is in namespace `SailwindCoop.Sync`, drop the `Sync.` qualifier; it is in that namespace, so call `ShipyardSyncManager.TryConsumePendingRigBlob(...)` directly and `Compat.SECompat` resolves under the root namespace via `SailwindCoop.Compat`.)

- [ ] **Step 3: Build**

Run: `dotnet build src/SailwindCoop/SailwindCoop.csproj -c Release -p:GameDir="C:\Program Files (x86)\Steam\steamapps\common\Sailwind"`
Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/SailwindCoop/Plugin.cs src/SailwindCoop/Sync/BoatStateApplicator.cs
git commit -m "feat(se-compat): send SE rig blobs on join, apply in Phase B before rope re-key"
```

---

### Task 6: Version bump + docs + deploy build

**Files:**
- Modify: `src/SailwindCoop/Plugin.cs:31` (`PluginVersion`)
- Modify: `README.md` (compatibility note; edit with a UTF-8-safe tool ONLY - PowerShell round-trips mojibake the emoji)

- [ ] **Step 1: Bump version**

`Plugin.cs:31`: `public const string PluginVersion = "0.2.31";`

- [ ] **Step 2: README compatibility note**

Add under the existing feature/compatibility section (locate the mod description area; keep formatting consistent, no em dashes):

```markdown
### Shipyard Expansion compatibility (v0.2.31+)

Works with nandbrew's Shipyard Expansion: custom rigs (extra masts, resized/rotated/flipped/retextured sails) sync to the whole crew, at join and live while editing. Requirement: either EVERY player installs the same Shipyard Expansion version, or nobody does - mixed crews are refused at join.
```

- [ ] **Step 3: Build + deploy to both plugin paths for local testing**

Run the build, then copy `src/SailwindCoop/bin/Release/net472/SailwindCoop.dll` to the canonical plugin path (per docs/memory: single canonical path since v0.2.23; check `docs/` or memory `sailwind-coop-build-deploy` for the exact deploy destination on this machine).
Expected: game loads plugin v0.2.31 alongside ShipyardExpansion with no BepInEx errors in `BepInEx/LogOutput.log`, and the log shows `[SECompat] Shipyard Expansion v0.9.0 detected; SE rig sync enabled.`

- [ ] **Step 4: Commit**

```bash
git add src/SailwindCoop/Plugin.cs README.md
git commit -m "chore: bump to v0.2.31, document Shipyard Expansion compatibility"
```

---

### Task 7: Live playtest checklist (user-run, blocking release)

No code. Verify before any release:

- [ ] Host+guest both SE: customize a Sanbuq (extra mast, resized+rotated+flipped+retextured sails), guest joins -> identical rig on guest; both can operate all ropes/winches (checks stable rope keys AND `PushStartPacket.SailIndex` raw-index survival).
- [ ] Live edit by host at shipyard while guest watches: structural change AND pure angle/flip/texture change (the SE-blob-only diff path) both propagate.
- [ ] Live edit by GUEST while host + second guest watch (star relay of packet 215).
- [ ] Host SE, guest without SE -> refused with the SE-mismatch message (lobby pre-check or handshake).
- [ ] Guest SE, host without SE -> refused (symmetric direction).
- [ ] SE version mismatch (if two SE builds available) -> refused.
- [ ] Both WITHOUT SE -> full regression pass on join + shipyard edit (zero behavior change; SERigState never sent).
- [ ] Save, quit, reload the coop session with SE rigs; guest rejoins -> rig intact.
- [ ] Collect `BepInEx/LogOutput.log` from all machines on any failure.

---

## Self-review notes

- Spec coverage: SECompat shim (T1), packet 215 (T2), handshake/lobby gate incl. symmetric + tolerant reads + reflection-failure-still-advertises (T1/T3), live sync + relay + 64KB cap + retry/TTL (T4), join ordering Phase B + collector question (resolved: `new bool[30]` defaults only trigger when `data.masts` is null, which cannot happen with SE installed since GetData returns the live bool[128]; arrays are length-prefixed on the wire, so NO collector change is needed - deviation from spec noted), version/packaging (T6), test matrix (T7).
- The spec's "AllowVersionMismatch also bypasses the mod gate" is honored in both guest pre-check and host handler.
- Type consistency: `SERigStatePacket`, `SendAllRigBlobsTo(SteamId)`, `TryConsumePendingRigBlob(string, out string)`, `GetRigBlob(SaveableObject)`, `ApplyRigBlob(SaveableObject, string)` used identically across tasks.
