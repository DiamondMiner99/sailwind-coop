# ERRATA - Shipyard Expansion compat plan (2026-07-13)

Ground truth verified against: repo `C:/Users/justi/source/repos/sailwind-coop-r4` @ branch `crate-join-resync` (clean tree, Plugin.PluginVersion = "0.2.30"), the installed `BepInEx/plugins/ShipyardExpansion/ShipyardExpansion.dll` (SE 0.9.0, decompiled with ilspycmd + dumped with Mono.Cecil), and the game decompile reference `sailwind-coop/decomp_v038_proj`.

Read this alongside your task brief. Where the plan and this file disagree, THIS FILE WINS.

Repo-wide style rule (user memory): NO em dashes or en dashes in any code comment, log string, doc text, or commit message. Use "-".

---

## 1. BLOCKERS

### B1. `VerboseLogger.ShipyardSend/ShipyardRecv/ShipyardApply` have NO `throttle:` parameter (WILL NOT COMPILE)

- Plan says: `VerboseLogger.ShipyardSend(msg, throttle: true)` (and the same for Recv/Apply).
- Truth: only `ShipyardPoll` takes it.
  - `public static void ShipyardPoll(string message, bool throttle = false)` - Debug/VerboseLogger.cs:355
  - `public static void ShipyardSend(string message)` - :361
  - `public static void ShipyardRecv(string message)` - :366
  - `public static void ShipyardApply(string message)` - :371
- Write instead: `VerboseLogger.ShipyardSend(msg);` (no named arg). If you need throttling on a send/recv/apply path, call `VerboseLogger.ShipyardPoll(msg, throttle: true)` instead. Do not add an overload as a side quest.
- Passing `throttle:` to the other three yields CS1739 / CS1501.

### B2. `SERigStatePacket` goes in `ShipyardPackets.cs`, NOT `BoatPackets.cs`

- Plan says (Task 2 Files list + Task 2 Step 5 `git add`): add the struct to `src/SailwindCoop/Networking/Packets/BoatPackets.cs`.
- Truth: BoatPackets.cs contains ZERO shipyard structs. All three shipyard packets live in `src/SailwindCoop/Networking/Packets/ShipyardPackets.cs`: `ShipyardCustomizationPacket` (:6), `ShipyardStatePacket` (:31), `ShipyardOrderRequestPacket` (:38). (Likely source of the confusion: `NetworkSailData`, which ShipyardCustomizationPacket references, IS in BoatPackets.cs:16.)
- Write instead: put `SERigStatePacket` in ShipyardPackets.cs next to `ShipyardStatePacket`. That file already has `using System;` (:1) for `[Serializable]` and shares the `SailwindCoop.Networking.Packets` namespace with PacketSerializer, so no new usings anywhere. Match the house style verbatim:

```csharp
    [Serializable]
    public struct SERigStatePacket
    {
        public string BoatName;   // root SaveableObject.gameObject.name (matches BoatUtility.FindBoatByName)
        public string RigBlob;    // SE "SEboatSails.{sceneIndex}" modData value
    }
```

- Fix the Task 2 `Files:` list and the `git add` (staging BoatPackets.cs would stage an unmodified file).

### B3. Shipping PacketType 215 touches FOUR files, not three - the handler registration is mandatory

Inbound packets are dispatched by an explicit `RegisterHandler` in Plugin.cs; there is no auto-switch. Miss it and the packet serializes, sends, arrives, and is silently dropped.

1. `PacketType.cs` - add `SERigState = 215,` after `CargoWithdrawn = 214,` (:239). 215 is free; enum is `: byte` (:3), so headroom to 255.
2. `ShipyardPackets.cs` - the struct (see B2).
3. `PacketSerializer.cs` - `Write/ReadSERigState` pair inside `#region Shipyard Packets Serialization` (:1023), before the `#endregion` at :1140.
4. `Plugin.cs` - `RegisterHandler`, inserted at line 1397 (the blank line after the ShipyardState registration ends at :1396, before `// Mission sync packets` at :1398):

```csharp
            NetworkManager.RegisterHandler(PacketType.SERigState, (sender, reader) =>
            {
                var packet = PacketSerializer.ReadSERigState(reader);
                ShipyardSyncManager?.OnSERigStateReceived(packet, sender);
            });
```

Note the receiver form: use the Plugin static property `ShipyardSyncManager?.X(...)` (matches the two adjacent shipyard registrations at :1387 and :1395), NOT `ShipyardSyncManager.Instance?.X(...)`. The plan's `.Instance?.` form does compile (C# "Color Color" rule; the repo already relies on it via `WeatherSyncManager` at Plugin.cs:91 + :1113), but do not introduce the inconsistency.

### B4. `[BepInDependency]` does not exist in the repo and is LOAD-BEARING

- Plan implies an attribute block to extend. Truth: `grep -rn "BepInDependency|BepInProcess" src/` returns ZERO hits. Plugin.cs:14-17 carries exactly one attribute:

```
14  namespace SailwindCoop
15  {
16      [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
17      public class Plugin : BaseUnityPlugin
```

- Write instead: ADD a fresh line between :16 and :17:

```csharp
    [BepInDependency("com.nandbrew.shipyardexpansion", BepInDependency.DependencyFlags.SoftDependency)]
```

`using BepInEx;` is already present at Plugin.cs:1.

- This is NOT cosmetic. Installed BepInEx is 5.4.23.5, where soft deps participate in the topological load sort. Without it SE can load AFTER Co-op, `Chainloader.PluginInfos` will not contain SE at Co-op's `Awake`, and `SECompat.Init()` silently reports "SE not installed" - the whole feature no-ops with no error.

### B5. SE's own Harmony postfix will CLOBBER a rig blob applied in the wrong order

- Plan assumes Co-op is the only writer of `GameState.modData["SEboatSails.{sceneIndex}"]`.
- Truth (SE decompile): `ShipyardExpansion.Patches` postfixes vanilla `SaveableBoatCustomization`:
  - `[HarmonyPatch(typeof(SaveableBoatCustomization), "LoadData")] Postfix` -> calls `SailDataManager.LoadSailConfig(refs)` and then IMMEDIATELY `SailDataManager.SaveSailConfig(refs)`.
  - `[HarmonyPatch(typeof(SaveableBoatCustomization), "GetData")] Postfix` -> calls `SailDataManager.SaveSailConfig(refs)` whenever `GameState.currentShipyard != null && refs.GetComponent<PurchasableBoat>().isPurchased()`.
  - `SaveCleaner.CleanSaveOld(...)` does `GameState.modData.Remove("SEboatSails." + sceneIndex)` - it DELETES the key.
- Consequence: if you write the blob into `modData` and THEN apply structural customization via `LoadData`, SE's LoadData postfix re-saves `modData` from the still-default live sails and your blob is gone.
- Write instead: `ApplyRigBlob` MUST run strictly AFTER any `SaveableBoatCustomization.LoadData` on that boat. Verified against SE IL: `LoadSailConfig` only MUTATES existing sails (walks `BoatRefs.masts -> Mast.sails`, applies scaler/texture); it never instantiates sails. So the plan's ordering (vanilla LoadData rebuilds the sail objects first, THEN apply the SE blob in Phase B / after packet 43) is correct - just make the ordering explicit in code and in a comment, and never write the blob before a customization apply.

### B6. The mast-change diff in `HasCustomizationChanged` is DEAD CODE - "add a mast" may never broadcast

- Plan says `SaveableBoatCustomization.GetData()` returns "the live `bool[128]`" of enabled masts, so no collector change is needed.
- Truth, on two counts:
  1. Vanilla `SaveBoatCustomizationData.masts` is `public bool[] masts = new bool[30];` and vanilla `GetData()` (SaveableBoatCustomization.cs:22-56) NEVER assigns `masts`. It only fills `sails` and `partActiveOptions`.
  2. The 128 comes from SE postfixing the `SaveBoatCustomizationData` CONSTRUCTOR (`__instance.masts = new bool[128];`), not `GetData`. Nothing in vanilla or SE ever writes `true` into it. It is a length-only field whose sole effect is `LoadData`'s `RemoveAllSails()` loop bound (so SE masts 30-127 get cleared).
- The plan's CONCLUSION still holds (no `BoatStateCollector` change: the wire is length-prefixed at PacketSerializer.cs:242-247 / :345-350, so a 128-length array round-trips intact). Do not change `MastsEnabled = customData?.masts ?? new bool[30]` at BoatStateCollector.cs:176, and leave the identical fallbacks at BoatStateApplicator.cs:641 and ShipyardSyncManager.cs:197/246/287/331/349 alone.
- But the FUNCTIONAL hazard is real: `ShipyardSyncManager.HasCustomizationChanged` (:191) compares `masts` at :197-202. Since `masts` is all-false on both sides always, that comparison can NEVER fire. A structural mast add/remove is only detected via sail count/details or via `partActiveOptions`. If SE can add a bare mast with no sails on it and no part-option change, `PollForChanges` will not broadcast it.
- Action: Task 7 checklist item #1 must be "ADD A MAST WITH NO SAILS, live, and confirm the guest sees it". If it fails, the fix is to include SE mast structure in the diff, not to touch the sail-extras blob.
- Also note the ctor patch's blast radius: it fires on Co-op's own `new SaveBoatCustomizationData { ... }` initializers (BoatStateApplicator.cs:639-655, ShipyardSyncManager.cs:349). The object initializer immediately overwrites masts with the wire value, so it is harmless today - but if the wire value ever falls back to `new bool[30]`, `LoadData` will only clear masts 0-29 and leave SE masts 30-127 holding stale sails.

---

## 2. Anchor corrections

Plan-cited line -> reality. "EXACT" = zero drift, use as written.

| File | Plan cites | Reality | Action |
|---|---|---|---|
| Plugin.cs | 31 `PluginVersion` | EXACT. `public const string PluginVersion = "0.2.30";` | Value is 0.2.30. Must stay `System.Version`-parseable (no `-dev` suffix). |
| Plugin.cs | (implied) existing `[BepInDependency]` | DOES NOT EXIST. Only `[BepInPlugin(...)]` at :16 | Add attribute fresh between :16 and :17 (B4). |
| Plugin.cs | 155 loading log | EXACT. `Log.LogInfo($"{PluginName} v{PluginVersion} loading...");` | `SECompat.Init()` insertion point. |
| Plugin.cs | 709-737 version-mismatch gate | EXACT span, all 3 refusal paths as described | - |
| Plugin.cs | 763 guest handshake send | EXACT. `NetworkManager.SendReliable(hostId, PacketType.Handshake, w => w.Write(PluginVersion));` | `hostId` from :739. |
| Plugin.cs | ~859-909 Handshake handlers | EXACT. Handshake handler 859-897; HandshakeAck handler 899-909 | HandshakeAck wire = `string version` then `bool accepted`. |
| Plugin.cs | 1384 ShipyardCustomization RegisterHandler | EXACT (block 1383-1388). ShipyardState block is 1390-1396 | Insert the SERigState handler at line 1397 (blank line before `// Mission sync packets` at :1398). |
| Plugin.cs | 2213 `RunJoinStep("BoatWorldState", ...)` | EXACT | Insert `RunJoinStep("SERigState", () => ShipyardSyncManager?.SendAllRigBlobsTo(friend.Id));` between :2213 and the ResendHelm comment (`"ResendHelm"` is at :2218). `RunJoinStep` is a private STATIC method at :2262 taking `(string, System.Action)`; `friend` is the enclosing `SendJoinStateToGuest(Steamworks.Friend friend)` param at :2186. |
| PacketType.cs | 239 `CargoWithdrawn = 214,` | EXACT, and it IS the max value in the file | Append `SERigState = 215,`. |
| PacketSerializer.cs | ~1023 `#region Shipyard Packets Serialization` | EXACT at 1023; region `#endregion` at 1140 | Insert Write/Read pair before :1140. |
| SteamLobbyManager.cs | 236 `lobby.SetData("version", Plugin.PluginVersion);` | EXACT (right after `lobby.SetPrivate()` at :234) | Mods-key stamp goes alongside. |
| ShipyardSyncManager.cs | 24 `_lastPartOptions` | EXACT | Put `_lastRigBlob` after :24. Put the STATIC `_pendingRigBlobs` / `PendingRigBlobTtl` / `MaxRigBlobBytes` near :39-42 with the other statics, not after :24 (otherwise you wedge a static between two instance fields). |
| ShipyardSyncManager.cs | 127 `}` closing `if (atShipyard)` | **MOVED.** The block is 123-126; the brace is on **126**. Line **127 is BLANK**; :128 is `Plugin.Profiler?.EndMeasure("Shipyard");` | Insert the pending-blob retry sweep at line 127, i.e. between :126 and :128. It lands INSIDE the Profiler Start/End window (measured as "Shipyard") - acceptable, be aware. |
| ShipyardSyncManager.cs | 170 `if (!HasCustomizationChanged(data))` | Correct, but the replaceable tail STARTS at **169** (the `// Check if anything changed` comment) | Replace lines **169-189** wholesale. Preserve the head (157-167). The phantom-furled-sails comment is 182-187 and `BoatUtility.InvalidateRopeCache(boat);` is **188** (last statement of the method) - move comment + call together into the new `if (vanillaChanged)` guard. |
| ShipyardSyncManager.cs | 268 "SendCustomizationUpdate (line 268)" | 268 is the method's CLOSING BRACE (method spans 241-268). Line 269 is blank; `CacheCurrentState()` starts at 270 | Insert the new methods at 269. |
| ShipyardSyncManager.cs | 131 OnEnterShipyard | EXACT (131-136; `CacheCurrentState();` at :135) | Add the `_lastRigBlob = Compat.SECompat.GetRigBlob(BoatUtility.GetCurrentBoat());` seed after :135. |
| ShipyardSyncManager.cs | 138 OnExitShipyard | EXACT (138-153; clears `_lastPartOptions` at :144) | Add `_lastRigBlob = null;` after :144. |
| ShipyardSyncManager.cs | 457 Reset() | EXACT (457-466) | Add `_lastRigBlob = null;` and `_pendingRigBlobs.Clear();` before :466. |
| ShipyardSyncManager.cs | 306 OnCustomizationReceived (relay pattern) | EXACT; host star-relay is :315-317 `if (Plugin.IsHost) Plugin.NetworkManager.SendToAllExcept(sender, PacketType.ShipyardCustomization, w => PacketSerializer.WriteShipyardCustomization(w, packet));` | See "relay order" note below. |
| BoatStateApplicator.cs | 596-605 `ApplyBoatStatePhaseB` | EXACT span; the plan's quoted "before" body (LogDebug / InvalidateRopeCache / ApplyRopeLengths) is verbatim correct | Same namespace (`SailwindCoop.Sync`) as ShipyardSyncManager -> call `ShipyardSyncManager.TryConsumePendingRigBlob(...)` unqualified. |
| BoatStateCollector.cs | 176 `MastsEnabled = customData?.masts ?? new bool[30],` | EXACT | NO CHANGE (see B6 - right conclusion, wrong reason). |
| BoatPackets.cs | (target for SERigStatePacket) | WRONG FILE | Use ShipyardPackets.cs (B2). |

Two deliberate deviations to KEEP (and comment, so a future reader does not "fix" them back):

- The plan's `OnSERigStateReceived` puts the host relay AFTER the null/empty and size checks, so malformed/oversized blobs are NOT forwarded. This inverts the file's stated "relay BEFORE FindBoatByName" convention (comment at ShipyardSyncManager.cs:310-314). It is correct by intent. Say so in the comment.
- `Sync.ShipyardSyncManager.TryConsumePendingRigBlob(...)` and the bare `ShipyardSyncManager.TryConsumePendingRigBlob(...)` BOTH compile from inside `namespace SailwindCoop.Sync`. Use the bare form.

---

## 3. API reference (exact signatures)

### Namespaces / usings

- `SailwindCoop.Sync` - BoatUtility, BoatSyncManager, BoatStateApplicator, BoatStateCollector, ShipyardSyncManager
- `SailwindCoop.Debug` - VerboseLogger (several call sites fully-qualify as `Debug.VerboseLogger.X(...)` to avoid clashing with `UnityEngine.Debug`)
- `SailwindCoop.Networking` - P2PNetworkManager, SteamLobbyManager
- `SailwindCoop.Networking.Packets` - PacketType, PacketSerializer, all packet structs
- New shim MUST be `namespace SailwindCoop.Compat` VERBATIM. Then bare `Compat.SECompat.X(...)` resolves from `SailwindCoop.Sync` and from `SailwindCoop` with NO using directive (C# walks enclosing namespaces). `SECompat` / a `Compat` namespace does NOT exist anywhere today - Task 1 creates it. `src/SailwindCoop/SailwindCoop.csproj` is SDK-style with default globbing, so a new `src/SailwindCoop/Compat/SECompat.cs` is picked up with NO csproj edit.
- **ShipyardSyncManager.cs has NO `using Steamworks;` and NO `using System;`.** Its usings (lines 1-6) are exactly: `System.Collections.Generic`, `System.Linq`, `HarmonyLib`, `UnityEngine`, `SailwindCoop.Debug`, `SailwindCoop.Networking.Packets`. Existing handlers write `Steamworks.SteamId sender = default` fully qualified (:306, :418). Keep the plan's fully-qualified `Steamworks.SteamId` in `SendAllRigBlobsTo(Steamworks.SteamId target)` / `OnSERigStateReceived(..., Steamworks.SteamId sender = default)` - do NOT shorten to `SteamId`.
- BoatStateApplicator.cs does NOT have `using SailwindCoop.Debug;`. Add it if you put VerboseLogger calls in Phase B.

### BoatUtility (`SailwindCoop.Sync`, src/SailwindCoop/Sync/BoatUtility.cs)

```csharp
public static Dictionary<string, SaveableObject> FindAllBoats()          // :20
public static SaveableObject      FindBoatByName(string name)            // :53
public static SaveableObject      GetCurrentBoat()                       // :64
public static Rigidbody           GetBoatRigidbody(SaveableObject boat)  // :78
public static Anchor              GetAnchor(SaveableObject boat)         // :93
public static bool                IsBoatAnchored(SaveableObject boat)    // :118
public static RopeController[]    GetRopeControllers(SaveableObject boat)// :143
public static string              GetStableRopeKey(RopeController rope)  // :170
public static void                InvalidateRopeCache(SaveableObject boat) // :236
public static void                ClearCaches()                          // :247
```

- `FindAllBoats()` key = boat ROOT `gameObject.name` (:38); value = the root `SaveableObject` (filtered to objects with a `BoatRefs` component). `foreach (var kv in ...)` with `kv.Key` / `kv.Value` is correct.
- **It returns the LIVE cache instance** (`_cachedBoats`, :10, returned directly at :24). Do NOT mutate it - copy first. It can also return an EMPTY dict when `SaveLoadManager.instance` is null / no boats registered, so early-load callers must tolerate zero boats.
- **`InvalidateRopeCache` takes a `SaveableObject`. There is NO string-name overload.** Passing a name / Transform / GameObject / BoatRefs will not compile. Resolve via `FindBoatByName(name)` first.
- `GetRopeControllers` returns a deterministic STABLE-SORTED array; wire rope indices index into THIS array. Never re-derive from a raw `GetComponentsInChildren`. Call `InvalidateRopeCache` after any sail/mast customization change or it hands back destroyed RopeControllers.
- `GetCurrentBoat()` = the boat the PLAYER IS STANDING ON (`GameState.currentBoat.parent`), which is NOT necessarily the boat on the shipyard cradle (that is `GameState.currentShipyard.GetCurrentBoat()`). `PollForChanges` already returns early when `GetCurrentBoat()` is null (:161); the new `_lastRigBlob` diff inherits this. `GetRigBlob(null)` must return null and the OnEnterShipyard seed can legitimately be null. Pre-existing behavior - do not "fix" it in this task.

### VerboseLogger (`SailwindCoop.Debug`, src/SailwindCoop/Debug/VerboseLogger.cs)

```csharp
public static void ShipyardPoll(string message, bool throttle = false)   // :355  <- ONLY one with throttle
public static void ShipyardSend(string message)                          // :361
public static void ShipyardRecv(string message)                          // :366
public static void ShipyardApply(string message)                         // :371
```

### P2PNetworkManager (`SailwindCoop.Networking`) - all INSTANCE methods, reached via `Plugin.NetworkManager`

```csharp
public void RegisterHandler(PacketType type, Action<SteamId, BinaryReader> handler)                       // :56
public void SendReliable(SteamId target, PacketType type, Action<BinaryWriter> writePayload)              // :105
public void SendUnreliable(SteamId target, PacketType type, Action<BinaryWriter> writePayload)            // :110
public void SendToAll(PacketType type, Action<BinaryWriter> writePayload, bool reliable = true)           // :115
public void SendToAllReliable(PacketType type, Action<BinaryWriter> writePayload)                         // :135
public void SendToAllUnreliable(PacketType type, Action<BinaryWriter> writePayload)                       // :140
public void SendToAllExcept(SteamId origin, PacketType type, Action<BinaryWriter> writePayload, bool reliable = true) // :148
public void AddPeer(SteamId peerId)     // :464
public void RemovePeer(SteamId peerId)  // :475
```

Gotchas: `SendReliable`'s FIRST param is the SteamId target (not the PacketType). `SendToAllExcept`'s FIRST param is the SteamId origin. There is NO `SendToHost` helper - a guest sends to the host with `SendReliable(hostSteamId, ...)`. Cheat sheet: host broadcast = `SendToAllReliable`; host relay of a guest-originated packet = `SendToAllExcept(sender, ...)`; host -> one peer = `SendReliable(id, ...)`.

### Plugin statics (src/SailwindCoop/Plugin.cs)

```csharp
public const  string             PluginGUID = "com.sailwindcoop.mod";     // :19
public const  string             PluginName = "Sailwind Coop";            // :20
public const  string             PluginVersion = "0.2.30";                // :31
public static ManualLogSource    Log { get; private set; }                // :34
public static ConfigEntry<bool>  AllowVersionMismatchConfig { get; private set; } // :83
public static SteamLobbyManager  LobbyManager => SteamLobbyManager.Instance;      // :85
public static P2PNetworkManager  NetworkManager { get; private set; }     // :86
public static WeatherSyncManager WeatherSyncManager { get; private set; } // :91
public static ShipyardSyncManager ShipyardSyncManager { get; private set; } // :97
public static bool               IsMultiplayer => LobbyManager.IsInLobby; // :111
public static bool               IsHost => LobbyManager.IsHost;           // :112
public static void               Notify(string message, float duration = 4f); // :427
private static void              EndGuestSessionAndQuit(string reason);   // :2031
private static void              RunJoinStep(string stepName, System.Action step); // :2262
```

- `Plugin.Log` is a `ManualLogSource`, NOT a method. `Plugin.Log("msg")` does not compile - use `Plugin.Log.LogInfo/LogWarning/LogError`.
- `Plugin.NetworkManager` can be NULL before Awake/lobby init - null-guard it (`Plugin.NetworkManager?.SendToAllReliable(...)`). `LobbyManager` is a lazily-created singleton and is never null, so reading `Plugin.IsHost` outside a lobby is safe (returns false).
- Caution at Plugin.cs:2213: `BoatSyncManager.SendBoatWorldStateTo(friend.Id)` is the ONLY join step WITHOUT a `?.`. Preserve or fix deliberately; do not change it accidentally.

### SteamLobbyManager (`SailwindCoop.Networking`) - instance methods via `Plugin.LobbyManager`

```csharp
public bool                IsHost           // :61
public SteamId             HostSteamId      // :69
public IEnumerable<Friend> LobbyMembers     // :99  (Steamworks.Friend -> .Id / .Name)
public void                LeaveLobby()     // :290
public void                RevokeAdmission(SteamId id) // :349
public string              GetLobbyData(string key)    // :360
public void                SetLobbyData(string key, string value) // :365
```

Host stamps `lobby.SetData("version", Plugin.PluginVersion);` at SteamLobbyManager.cs:236. Guest reads it at Plugin.cs:711.

### Other coop members

```csharp
public static bool BoatSyncManager.IsJoinInProgress { get; set; }         // BoatSyncManager.cs:123 - PUBLIC setter
public static ShipyardSyncManager ShipyardSyncManager.Instance { get; private set; } // ShipyardSyncManager.cs:16
public const int CoopSave.PhantomSlot = 99;                               // CoopSave.cs:27
public static void BoatStateApplicator.ApplyBoatStatePhaseB(SaveableObject boat, NetworkBoatData data) // :596
public static NetworkBoatData BoatStateCollector.CollectBoatData(SaveableObject boat) // :136
```

`IsJoinInProgress` is documented (BoatSyncManager.cs:110-122) as being set ONLY on the joining GUEST's own machine (join coroutine + RecoveryStarted). Read it freely; do NOT repurpose it as a host-side gate.

### PacketSerializer conventions (`SailwindCoop.Networking.Packets`, PacketSerializer.cs:13 `public static class`)

- Strings: `writer.Write(x ?? "")` on write, bare `reader.ReadString()` on read. There is NO WriteString/ReadString/SafeString helper. The `?? ""` is load-bearing (`BinaryWriter.Write(string)` throws on null).
- Arrays: length prefix (`writer.Write(packet.Arr?.Length ?? 0)`) + null-guarded foreach; reader allocates `new T[count]` and loops.
- Simple packets return via object initializer. Copy the `WriteShipyardState`/`ReadShipyardState` shape (:1106-1119).
- Write order MUST equal Read order.

### Game types (Assembly-CSharp, global namespace) - all verified live via SE's IL references

```csharp
public class GameState        { public static Dictionary<string,string> modData; public static Shipyard currentShipyard; }
public class SaveableObject : MonoBehaviour { public int sceneIndex; }
public class BoatRefs       : MonoBehaviour { public Transform walkCol; public Transform boatModel; public Mast[] masts; }
public class SaveableBoatCustomization : MonoBehaviour   // [RequireComponent(typeof(BoatRefs))]
{
    public SaveBoatCustomizationData GetData();          // :22
    public void LoadData(SaveBoatCustomizationData data);// :58
}
public class SaveBoatCustomizationData { public bool[] masts = new bool[30]; /* SE ctor-patches to new bool[128] */ ... }
```

- `GameState.modData` has ZERO existing uses in the coop repo - this is the first. Its load path only overwrites when non-null (SaveLoadManager.cs:489-491), so a pre-modData save leaves the PREVIOUS session's entries in place.
- `GameState.currentShipyard != null` = the player is in shipyard MODE (canonical; vanilla gates on it). It does NOT mean a boat is on the cradle.
- `BoatRefs` and `SaveableObject` both sit on the boat ROOT. `saveableObject.GetComponent<BoatRefs>()` is the established pattern.
- `NetworkBoatData` is a STRUCT (BoatPackets.cs:110); `Name` (:113) = `boat.gameObject.name` (set at BoatStateCollector.cs:171), which is EXACTLY the key `BoatUtility.FindBoatByName` looks up. Keying `SERigStatePacket.BoatName` off `gameObject.name` and re-deriving `sceneIndex` locally on the receiver is correct - `sceneIndex` is save-local, the name is the stable cross-machine key.

### SE 0.9.0 reflection targets (VERIFIED against the installed DLL - all five plan assumptions correct)

Installed at `C:/Program Files (x86)/Steam/steamapps/common/Sailwind/BepInEx/plugins/ShipyardExpansion/ShipyardExpansion.dll`. Assembly simple name: `ShipyardExpansion`.

```
[BepInPlugin("com.nandbrew.shipyardexpansion", "Shipyard Expansion", "0.9.0")]  on ShipyardExpansion.Plugin

ShipyardExpansion.SailDataManager   // public static class (public abstract sealed), TOP-LEVEL, not nested
    public static void SaveSailConfig(BoatRefs refs)   // single param, no overloads
    public static void LoadSailConfig(BoatRefs refs)   // single param, no overloads
```

- modData key literal is EXACTLY `"SEboatSails."` (capital S-E, lowercase "boat", capital S in "Sails", trailing dot) + `refs.GetComponent<SaveableObject>().sceneIndex`. Both SE methods build it identically.
- `SaveSailConfig` takes NO sceneIndex; it derives the key itself and writes `GameState.modData` directly. Blob format: per mast `{orderIndex}(` then per sail `{prefabIndex},{scaleZ},{scaleY},{angle},{flipped},{textureIndex}]` then `)`, with the literal suffix `"|0.9.0"`. All numbers InvariantCulture.
- Because there is exactly one overload of each, `sdm.GetMethod("SaveSailConfig", BindingFlags.Public | BindingFlags.Static)` + `Invoke(null, new object[]{ refs })` is unambiguous and correct.
- Resolve the type via `Chainloader.PluginInfos` -> `info.Instance.GetType().Assembly.GetType("ShipyardExpansion.SailDataManager")`. Works (SE's Plugin lives in that assembly). Requires B4's soft dependency.
- Version: use `info.Metadata.Version.ToString()` -> "0.9.0". Do NOT use `asm.GetName().Version` - the ASSEMBLY version is **0.0.0.0** and would poison `ModSignature`.
- Both SE methods do `((Component)refs).GetComponent<SaveableObject>().sceneIndex` with NO null check -> NRE if the BoatRefs GameObject has no SaveableObject. The plan's shim starts FROM the SaveableObject and does `boat.GetComponent<BoatRefs>()`, which guarantees both are on the same GameObject. This is correct - do NOT refactor it to start from BoatRefs.
- `LoadSailConfig` only MUTATES existing sails (never instantiates), so applying the blob after vanilla `LoadData` rebuilt the sails is the right ordering. It is a no-op (logs only) when the key is absent.

---

## 4. Confirmed (do not re-verify)

- `BoatUtility.FindAllBoats()` returns `Dictionary<string, SaveableObject>`, key = boat ROOT gameObject name; `kv.Key` / `kv.Value` as the plan writes them.
- `BoatUtility.FindBoatByName(string) -> SaveableObject`; `GetCurrentBoat() -> SaveableObject`.
- `BoatSyncManager.IsJoinInProgress` is `public static bool { get; set; }` - readable AND writable.
- `Plugin.IsHost` / `Plugin.Log` / `Plugin.Notify` / `Plugin.LobbyManager` / `Plugin.NetworkManager` all exist and are public static (with the `Log`-is-not-a-method caveat above).
- Send APIs take `Action<BinaryWriter>`.
- `PacketType` is `: byte`; `CargoWithdrawn = 214` is the highest existing value; **215 is free** and purely additive (and the v0.2.28 version handshake already refuses mismatched joins, so no old peer can receive an unknown byte).
- `#region Shipyard Packets Serialization` at PacketSerializer.cs:1023.
- `GameState.currentShipyard` is the canonical "in shipyard mode" flag and ShipyardSyncManager already uses it (:108, :329).
- `GameState.modData` = `public static Dictionary<string,string>`; `SaveableObject.sceneIndex` = `public int`. Both confirmed live in the SHIPPED Assembly-CSharp (SE's IL references them directly).
- `BoatStateApplicator.ApplyBoatStatePhaseB` at BoatStateApplicator.cs:596-605, body exactly as the plan quotes it.
- `BoatStateCollector.cs:176 MastsEnabled = customData?.masts ?? new bool[30]` needs NO change (the wire is length-prefixed, a 128-length array round-trips).
- The Task 3 handshake anchors (Plugin.cs 709-737 / 763 / 859-897 / 899-909, SteamLobbyManager.cs:236) are all EXACT.
- SDK-style csproj globs in a new `Compat/SECompat.cs` with no project-file edit.
- SE type name, both method signatures, the `"SEboatSails."` key literal, the GUID, and version 0.9.0 all match the plan exactly. The shim as drafted will compile and bind against the installed SE build.

---

## 5. Open risks

1. **SE's private `skipSailData` config silently defeats the whole feature.** `ShipyardExpansion.Plugin::skipSailData` is a `ConfigEntry<bool>` (SE config section "zDebug", key "skip sail data", default FALSE, INTERNAL/private static). Both `SaveSailConfig` and `LoadSailConfig` early-out on it. If one player enables it: `GetRigBlob` returns a STALE blob (not null) or nothing, and `ApplyRigBlob` returns true while SE applied nothing. `ModSignature` would still be "SE=0.9.0" on both peers so the handshake passes and the desync is SILENT. Mitigation: reflect the field (`BindingFlags.NonPublic | BindingFlags.Static`, then `.Value`) into `ModSignature` so a mismatched crew is refused, or at minimum add it to the Task 7 playtest checklist.
   - HANDLED IN TASK 1: `SECompat.SkipSailData` reflects the field; `ModSignature` appends `/noSailData` so a MIXED crew is refused; and the private `Enabled` gate (`_reflectionOk && !SkipSailData`) makes BOTH `GetRigBlob` and `ApplyRigBlob` hard no-op when the flag is on, which also covers an ALL-flagged crew (they pass the handshake). So `ApplyRigBlob == true` now DOES mean "SE applied it" - Tasks 3-5 may rely on that. Keep the flag on the Task 7 checklist anyway.
2. **`SaveSailConfig` has a silent abort path.** If ANY sail GameObject on the boat lacks a `SailScaler`, SE logs "No sail scaler component found! Aborting data for this boat" and returns WITHOUT writing - the pre-existing (stale) modData key survives. Same stale-blob consequence as (1).
   - HANDLED IN TASK 1: `GetRigBlob` PROBES rather than reads - it removes the key, calls `SaveSailConfig`, and treats "key still absent" as "SE aborted" (restoring the previous value and returning null). It therefore never hands a stale blob to the poller. Task 4 can trust a non-null `GetRigBlob` to be fresh.
3. **`LoadSailConfig` THROWS on a malformed blob, and modData is PERSISTED - so a bad blob is permanent.** (Corrected 2026-07-13 after the task-1 review re-checked the IL. The earlier claim here - that `LoadSailConfig` mutates the process-wide static `VersionManager.saveVersion2` as a side effect - is FALSE: `VersionManager.GetVersion` is pure, it returns a local `int[]` and never assigns `saveVersion2`; only `WriteSaveVersion`/`ReadSaveVersion` do. The recommendation stands, but for a stronger reason.) `LoadSailConfig` has many UNGUARDED throw sites on malformed input: `Convert.ToInt32(array2[0])` and `array2[1]` on the mast split, `Convert.ToSingle`/`bool.Parse` on the sail fields, and a raw `version[1] < 9 && version[2] < 94` index against an `int[]` that `GetVersion` sizes to the version tail's ACTUAL segment count (so a 2-segment tail like `|1.0` is an IndexOutOfRangeException). `GameState.modData` is serialized into the save (`SaveLoadManager.cs:293` writes it, `:491` reads it back) and SE re-parses this key on EVERY `SaveableBoatCustomization.LoadData` via its postfix - which calls `LoadSailConfig` BEFORE `SaveSailConfig`, so a throwing blob prevents the key from ever self-healing. One malformed packet from one peer = a permanently broken save. VALIDATE the blob shape before writing it into modData (require a trailing `|` with at least three ASCII-digit version segments), AND write it inside a try that rolls modData back to its exact prior state (restore the previous value, or Remove if there was no key) on any exception. Both halves are implemented in `Compat/SECompat.cs` - do not delete either.
   - REVISED IN THE TASK-1 REVIEW POLISH PASS (2026-07-13): "EXACTLY three" version segments was too strict - SE indexes only `version[1]` and `version[2]`, so a longer tail (e.g. a future `1.0.0.1`) parses fine and must not be rejected; the shape check now requires "at least three" segments (`parts.Length < 3`). Separately, "require a non-empty body" was WRONG: `SaveSailConfig` appends `"|0.9.0"` unconditionally, so a body-less blob (`"|0.9.0"`) is a LEGITIMATE output for a boat whose masts all carry zero sails, not corruption. `IsValidRigBlobShape` now accepts `sep == 0` as a valid shape, and `ApplyRigBlob` special-cases it as a benign no-op: it returns `true` WITHOUT writing to modData (nothing to apply; a stale receiver key self-heals via SE's own `LoadData` postfix) and logs at Info, not Warning. The guard that prevents a body-less blob from being WRITTEN over a real one is unchanged - only the return value and log level changed.
4. **Mast add/remove may not be detected at all** (see B6). Unproven either way. Task 7 must test it explicitly.
5. **Do NOT blanket-sync `GameState.modData`.** SE also writes `"com.nandbrew.shipyardexpansion.{BoatRefs}.partCounts"` (PartCountTracker) and `"com.nandbrew.shipyardexpansion"`-prefixed save-version keys (VersionManager). Both are LOCAL save-migration / stock-part bookkeeping, regenerated per machine at load. Blanket-syncing would clobber the guest's own bookkeeping and could trigger SE's `ConvertSave` migration against foreign data. Keep the sync narrowly scoped to `"SEboatSails.{sceneIndex}"` exactly as the plan does. Also expect `SaveCleaner.CleanSaveOld` to DELETE that key.
6. **SE_Bridge.dll (assembly `SE_Bridge`, v1.5.0.0) is not covered by this plan.** It ships alongside SE and carries `SE_LadderData` / `SE_PartData` / `SE_PartOptionData` MonoBehaviours on modded prefabs. Nothing in the planned shim needs it, but if structural part sync ever needs more than `SaveBoatCustomizationData`, that is where the state lives.
7. **Probe coverage gap:** the shipyard-sync probe did NOT verify the SE assembly, `GameState.modData` shape, `BoatRefs`, or `SaveableObject.sceneIndex`; the se-assembly and game-types probes covered those independently and AGREE with each other on every overlapping fact. No probe contradicted another. The only near-conflict was cosmetic: game-types says insert the Update() sweep "between 126 and 128", shipyard-sync says "at line 127 (blank)" - the same location. Everything in this errata is single-sourced only where noted.
8. **Not verified by anyone:** the SE assembly's behavior under a boat that is on the cradle but NOT the player's `GameState.currentBoat` (the `GetCurrentBoat()` vs `currentShipyard.GetCurrentBoat()` split). Pre-existing in `PollForChanges`; the new `_lastRigBlob` diff inherits it. Out of scope for this task, but it is where a "guest at the shipyard sees nothing" bug would come from.
