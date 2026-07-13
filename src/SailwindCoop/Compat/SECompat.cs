using System;
using System.Globalization;
using System.Linq;
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
    ///
    /// GameState.modData is SERIALIZED INTO THE SAVE FILE, so every write here is a
    /// potentially permanent one: SE re-parses the "SEboatSails.{sceneIndex}" key on every
    /// SaveableBoatCustomization.LoadData (its Harmony postfix calls LoadSailConfig BEFORE
    /// SaveSailConfig), and LoadSailConfig has unguarded throw sites on malformed input. A
    /// blob that throws once would therefore throw on every subsequent load and never
    /// self-heal. Hence the shape guard plus the strict rollback in ApplyRigBlob.
    /// </summary>
    public static class SECompat
    {
        public const string SEGuid = "com.nandbrew.shipyardexpansion";
        private const string SEAssemblyName = "ShipyardExpansion";
        private const string ModDataKeyPrefix = "SEboatSails.";

        public static bool IsInstalled { get; private set; }
        public static string Version { get; private set; } = "";

        /// <summary>
        /// True when SE's own "zDebug / skip sail data" ConfigEntry is enabled on THIS machine.
        /// Both SaveSailConfig and LoadSailConfig early-out when it is set, so SE's data path does
        /// nothing here: GetRigBlob would otherwise hand back whatever was already in modData and
        /// ApplyRigBlob would report success while SE applied nothing (and still persist a foreign
        /// rig into this player's save, to be applied later once they turn the flag off). We
        /// therefore gate the whole data path on it (see Enabled) and advertise it in ModSignature.
        /// Reflected once in Init(); any reflection failure here is treated as false and never
        /// aborts SE detection.
        /// </summary>
        public static bool SkipSailData { get; private set; }

        /// <summary>
        /// (v0.2.31, C5) SE config entries OTHER than skipSailData that change the RIG CONTRACT the crew
        /// shares, folded into ModSignature as an OPAQUE suffix so a divergent crew is refused at the join
        /// instead of silently building different rigs from the identical packet. Nothing parses this - the
        /// handshake only ever compares it for exact equality and prints it.
        ///
        /// Why these two, and only these two (SE 0.9.0, read out of the decompile, not assumed):
        /// - "topsailPatch" (Settings / "Link topmasts", default TRUE, restart-scoped). SE's
        ///   TopsailPatch.Postfix on Mast.TopsailCheckAndAttach returns immediately when the flag is off;
        ///   when it is on it adds a SquareTopsailAngleMirror (slaving the topsail's angle to the square
        ///   sail below) and forces __result = true. Vanilla Mast.UpdateControllerAttachments uses exactly
        ///   that result to decide whether to SKIP attaching the sail's left/right angle winches
        ///   (leftAngleWinch[0]/rightAngleWinch[0].AttachToController), and UpdateControllerAttachments is
        ///   called from Mast.LoadSail - so it fires on EVERY sail rebuild, including the ones our own
        ///   ApplyRigBlob drives. Two peers with different settings therefore build a DIFFERENT RIG from the
        ///   identical packet: on one the topsail mirrors the sail below and has no winch of its own, on the
        ///   other it is independently winched. No warning, no log line.
        ///
        ///   PRECISELY: this does not shift the rope indices AS WE ADDRESS THEM, but NOT because the hierarchy
        ///   is the same on both peers - it is not. The controller SET is unchanged: an unattached winch only
        ///   has its Renderer/Collider .enabled toggled (vanilla GPButtonRopeWinch.ShowWinch, :55-67), never
        ///   gameObject.SetActive, so GetComponentsInChildren still finds every angle RopeController either
        ///   way. But GPButtonRopeWinch.AttachToController (:73) does
        ///   "controller.transform.parent = base.transform.parent", so with topsailPatch OFF the topsail's
        ///   angle controllers ARE reparented and the RAW GetComponentsInChildren ORDER genuinely differs
        ///   between peers. The indices line up only because BoatUtility.GetRopeControllers re-sorts the array
        ///   by GetStableRopeKey, which is derived from the sail's component links plus Mast.orderIndex and
        ///   NEVER from the hierarchy. DO NOT DELETE THAT SORT believing hierarchy order is safe - it is that
        ///   sort, not SE, that makes rope index i mean the same rope on every machine. What still diverges
        ///   across this flag is which rope a winch DRIVES and whether the topsail's angle is slaved: a real
        ///   rig divergence, which is why the flag is in the token.
        /// - "addSails" (Settings / "Add lug sails", default TRUE, restart-scoped). SE's SailAdderPatches
        ///   PREFIX on PrefabsDirectory.Start resizes PrefabsDirectory.sails to 512 UNCONDITIONALLY (SE
        ///   decompile :3764-3771), and only the POSTFIX - gated on the flag - actually builds the lug-sail
        ///   prefabs into slots 156-158. So a peer with addSails OFF has sails[156..158] == null, and vanilla
        ///   Mast.LoadSail (Mast.cs:113) does a raw Object.Instantiate(sails[prefabIndex]) with NO null check:
        ///   it THROWS. It does not merely fail to instantiate the sail. One crew member with the flag off
        ///   therefore takes an exception out of LoadData - i.e. out of our packet-43 apply AND out of the join
        ///   - the moment anyone installs a lug sail. Hence the flag is in the handshake signature and a
        ///   divergent crew is refused at the door rather than broken mid-session.
        ///
        /// Every other SE config was audited at its USE SITE and deliberately left out. None of them changes
        /// what an identical packet BUILDS on a receiver, which is the only thing this token has to protect:
        /// - vertLateens / vertFins / autoFit: ShipyardSailInstaller.AddNewSail patches. That is the shipyard
        ///   UI's install path, so it only ever runs on the EDITING machine, and its whole effect is to set
        ///   the new sail's scale/angle - which is then captured in the vanilla customization data and in the
        ///   blob. It travels as DATA, so the receiver reproduces it whatever its own flags say.
        /// - overrideScaling / combinedScale / percentSailNames: shipyard UI buttons and sail-name text.
        /// - unrollSails: a Shipyard.ResetOrder postfix that unfurls the sails on entering the shipyard. Also
        ///   editing-machine-only, and it moves rope LENGTHS, which are already synced authoritatively by
        ///   ControlSyncManager's RopeState - not by this blob.
        /// - climbSpeed: local movement speed.
        /// - cleanSave / cleanLoad / convertSave / starterSetFix: save-repair switches.
        ///
        /// Both are "(requires a restart)" in SE's own config description, so reading them ONCE in Init is
        /// correct - they cannot change under us mid-session.
        /// </summary>
        private static readonly string[] RigContractConfigs = { "topsailPatch", "addSails" };
        private static string _rigContractToken = "";

        /// <summary>
        /// Handshake token advertising SE presence/version. Deliberately deviates from the plain
        /// "SE=" + Version scheme for either of two "data path disabled" causes:
        /// - _reflectionOk is false (SE's internals did not resolve, or have not finished
        ///   initializing - see the Minor-1 load-order case in Init): we append "/noSync" because
        ///   this peer never sends and never applies, while healthy peers do. Without this suffix
        ///   it would advertise the SAME token as a fully working peer and silently half-sync the
        ///   crew.
        /// - SkipSailData is true (SE's own debug flag; only reachable when _reflectionOk IS true):
        ///   we append "/noSailData" instead.
        /// Both suffixes force a mismatch with any peer that does not share the same condition,
        /// refusing the join instead of letting a crew silently desync their rigs. "/noSync" is
        /// checked first since it also covers _reflectionOk == false regardless of SkipSailData.
        /// Keyed off IsInstalled, NOT Enabled: we must still advertise SE even when the local data
        /// path is off, because refusing a join is safer than half-synced rigs (a vanilla, no-SE
        /// peer must still mismatch and be refused too).
        /// User-approved deviation from the base plan's exact-match "SE=" + version contract
        /// (task 1 brief errata, 2026-07-13).
        ///
        /// (C5) _rigContractToken is appended last: the same trap as "/noSailData", closed for the OTHER SE
        /// configs that change what an identical packet builds on the receiver (see RigContractConfigs). It
        /// is EMPTY on the "/noSync" path by construction, which is correct - that peer neither sends nor
        /// applies, so its rig contract is moot, and every peer in the same state produces the same token.
        /// A crew all running SE 0.9.0 at its DEFAULTS produces one identical token and is admitted exactly
        /// as before; only a crew that actually disagrees is refused.
        /// </summary>
        public static string ModSignature
        {
            get
            {
                if (!IsInstalled) return "";
                if (!_reflectionOk) return "SE=" + Version + "/noSync";
                return "SE=" + Version + (SkipSailData ? "/noSailData" : "") + _rigContractToken;
            }
        }

        private static MethodInfo _saveSailConfig; // SailDataManager.SaveSailConfig(BoatRefs)
        private static MethodInfo _loadSailConfig; // SailDataManager.LoadSailConfig(BoatRefs)
        private static bool _reflectionOk;

        // Warn-once latch, keyed by boat name. GetRigBlob runs on ShipyardSyncManager's 5 Hz poll,
        // so a persistent failure (e.g. an SE part whose sail has no SailScaler) would otherwise
        // log 5 warnings a second for as long as the player stands in the shipyard and drown the
        // user-submitted logs we triage playtests from.
        private static string _lastWarnedBoat;

        /// <summary>
        /// The SE data path is usable: SE is present, its internals resolved, AND its own debug
        /// "skip sail data" flag is off. Both GetRigBlob and ApplyRigBlob gate on this, so a true
        /// return from ApplyRigBlob genuinely means "SE applied it".
        /// </summary>
        private static bool Enabled => _reflectionOk && !SkipSailData;

        public static void Init()
        {
            IsInstalled = false;
            Version = "";
            SkipSailData = false;
            _rigContractToken = "";
            _reflectionOk = false;
            _saveSailConfig = null;
            _loadSailConfig = null;
            _lastWarnedBoat = null;
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
                if (asm == null)
                {
                    // SE is registered with the chainloader but its plugin component has not been
                    // instantiated yet (load order). Fall back to the loaded assembly list rather
                    // than blaming SE's internals for what is a load-order problem.
                    asm = AppDomain.CurrentDomain.GetAssemblies()
                        .FirstOrDefault(a => a.GetName().Name == SEAssemblyName);
                    if (asm == null)
                    {
                        Plugin.Log.LogWarning($"[SECompat] Shipyard Expansion v{Version} is registered with BepInEx but " +
                            "not yet instantiated, and its assembly is not loaded, so we cannot resolve its internals. " +
                            "This is a plugin LOAD ORDER problem, not a missing Co-op update. SE rig sync DISABLED for " +
                            "this session; joins still require matching SE on every peer.");
                        return;
                    }
                }

                var sdm = asm.GetType("ShipyardExpansion.SailDataManager");
                _saveSailConfig = sdm != null ? sdm.GetMethod("SaveSailConfig", BindingFlags.Public | BindingFlags.Static) : null;
                _loadSailConfig = sdm != null ? sdm.GetMethod("LoadSailConfig", BindingFlags.Public | BindingFlags.Static) : null;
                _reflectionOk = _saveSailConfig != null && _loadSailConfig != null;

                // Best-effort read of SE's private "zDebug / skip sail data" ConfigEntry<bool>. A
                // failure here must never throw out of Init nor flip IsInstalled back off - it only
                // affects the Enabled gate, the ModSignature suffix and the warning below.
                try
                {
                    var sePluginType = asm.GetType("ShipyardExpansion.Plugin");
                    var field = sePluginType != null
                        ? sePluginType.GetField("skipSailData", BindingFlags.NonPublic | BindingFlags.Static)
                        : null;
                    var entry = field != null ? field.GetValue(null) as BepInEx.Configuration.ConfigEntry<bool> : null;
                    if (field != null && entry == null)
                    {
                        // The field EXISTS but is not yet bound to a ConfigEntry: SE binds it in its
                        // own Awake (Config.Bind for "skip sail data"), which has not run on this
                        // machine yet - this is the info.Instance == null / AppDomain-fallback path,
                        // a load-order problem, not changed SE internals. Treating an unbound field as
                        // "false" would re-open the exact bug the Enabled gate exists to close: SE's
                        // early-out would not have fired either, so ApplyRigBlob would report success
                        // while SE applied nothing, a foreign rig would persist into this player's
                        // save, and ModSignature would omit "/noSailData" so a mixed crew would NOT be
                        // refused. Fail closed instead: disable the whole SE data path for this
                        // session. IsInstalled stays true so we still advertise SE and refuse a
                        // vanilla (no-SE) peer.
                        SkipSailData = false;
                        _reflectionOk = false;
                        Plugin.Log.LogWarning($"[SECompat] Shipyard Expansion v{Version} has not finished " +
                            "initializing yet ('skip sail data' config is not bound - its Awake has not run). " +
                            "This is a plugin LOAD ORDER problem, not a missing Co-op update. SE rig sync " +
                            "DISABLED for this session; joins still require matching SE on every peer.");
                        return;
                    }
                    SkipSailData = entry != null && entry.Value;
                }
                catch (Exception e)
                {
                    SkipSailData = false;
                    Plugin.Log.LogWarning($"[SECompat] Could not read SE's 'skip sail data' debug config: {e.Message}. Assuming false.");
                }

                // (v0.2.31, C5) Read the OTHER SE configs that change the shared rig contract and fold them
                // into the handshake token. Same reflection path as 'skip sail data' above (SE declares all
                // of these as internal static ConfigEntry<T>, which BindingFlags.NonPublic covers), and the
                // same fail-closed rule for an UNBOUND entry: a field that exists but has no ConfigEntry
                // means SE's Awake has not run here (load order), so we cannot know the value and must not
                // guess it - guessing would let a divergent crew through the gate, which is the whole bug.
                //
                // An ABSENT field is different and must NOT fail closed: it means this SE build simply does
                // not have that config. Every peer on that SE build sees the same absence, the SE VERSION is
                // already part of the token, and a build without the config cannot diverge on it. We mark it
                // in the token ("?") and carry on - failing closed there would disable SE sync for an entire
                // future SE version for no gain.
                try
                {
                    var sePluginType = asm.GetType("ShipyardExpansion.Plugin");
                    var token = new System.Text.StringBuilder();
                    foreach (var name in RigContractConfigs)
                    {
                        var f = sePluginType != null
                            ? sePluginType.GetField(name, BindingFlags.NonPublic | BindingFlags.Static)
                            : null;
                        if (f == null)
                        {
                            token.Append('/').Append(name).Append('?');
                            Plugin.Log.LogWarning($"[SECompat] Shipyard Expansion v{Version} has no '{name}' config " +
                                "field; this SE build cannot diverge on it, so it is marked unknown in the handshake " +
                                "token rather than disabling SE sync. Check for a Sailwind Co-op update.");
                            continue;
                        }

                        var e = f.GetValue(null) as BepInEx.Configuration.ConfigEntry<bool>;
                        if (e == null)
                        {
                            _reflectionOk = false;
                            Plugin.Log.LogWarning($"[SECompat] Shipyard Expansion v{Version} has not finished " +
                                $"initializing yet ('{name}' config is not bound - its Awake has not run). This is a " +
                                "plugin LOAD ORDER problem, not a missing Co-op update. SE rig sync DISABLED for this " +
                                "session; joins still require matching SE on every peer.");
                            return;
                        }

                        token.Append('/').Append(name).Append(e.Value ? '1' : '0');
                    }
                    _rigContractToken = token.ToString();
                }
                catch (Exception e)
                {
                    // Cannot verify the rig contract -> we must not advertise a token that claims we can.
                    // Fail closed (same rule as an unbound entry): SE stays advertised, sync is off here.
                    _rigContractToken = "";
                    _reflectionOk = false;
                    Plugin.Log.LogWarning($"[SECompat] Could not read SE's rig-contract configs " +
                        $"({string.Join(", ", RigContractConfigs)}): {e.Message}. SE rig sync DISABLED for this " +
                        "session; joins still require matching SE on every peer.");
                    return;
                }

                if (SkipSailData)
                {
                    Plugin.Log.LogWarning("[SECompat] Shipyard Expansion's debug config 'skip sail data' (section " +
                        "zDebug) is ENABLED on this machine. SE rig sync is DISABLED here - sail scale/angle/flip/" +
                        "texture changes will not sync for this player, and peers without the flag will refuse the " +
                        "join. Disable 'skip sail data' in Shipyard Expansion's config file.");
                }

                if (Enabled)
                    Plugin.Log.LogInfo($"[SECompat] Shipyard Expansion v{Version} detected; SE rig sync enabled. " +
                        $"Handshake token [{ModSignature}] - every peer must match it exactly (SE's 'Link topmasts' " +
                        "and 'Add lug sails' settings change the rig, so the whole crew must agree on them).");
                else if (_reflectionOk)
                    Plugin.Log.LogInfo($"[SECompat] Shipyard Expansion v{Version} detected; SE rig sync DISABLED by SE's " +
                        "own 'skip sail data' debug config (see the warning above).");
                else
                    // Still advertise SE in ModSignature: refusing joins is safer than half-synced rigs.
                    Plugin.Log.LogWarning($"[SECompat] Shipyard Expansion v{Version} detected but its internals changed " +
                        "(SailDataManager.Save/LoadSailConfig not found). SE rig sync DISABLED; joins still require " +
                        "matching SE. Check for a Sailwind Co-op update.");
            }
            catch (Exception e)
            {
                _reflectionOk = false;
                if (IsInstalled)
                    // SE IS present - we just could not resolve its internals. Do not let this read as
                    // "SE not detected": joins STILL require matching SE (ModSignature keys off
                    // IsInstalled), so this is the log line that explains a "why was I refused from the
                    // lobby" report. Full exception + stack: this branch means SE changed shape.
                    Plugin.Log.LogError($"[SECompat] Shipyard Expansion v{Version} WAS detected, but resolving its " +
                        "internals threw - SE's internals have likely changed. SE rig sync DISABLED; joins still " +
                        "require matching SE on every peer. Check for a Sailwind Co-op update. Exception: " + e);
                else
                    Plugin.Log.LogWarning("[SECompat] SE detection threw before SE could be identified; SE sync disabled. " +
                        "Exception: " + e);
            }
        }

        private static string ModDataKey(SaveableObject boat) => ModDataKeyPrefix + boat.sceneIndex;

        /// <summary>
        /// MethodInfo.Invoke wraps whatever SE threw in a TargetInvocationException whose Message is
        /// the useless "Exception has been thrown by the target of an invocation." Unwrap it so the
        /// log names the real IndexOutOfRange/Format/NullReference failure.
        /// </summary>
        private static Exception Unwrap(Exception e) => (e as TargetInvocationException)?.InnerException ?? e;

        private static void WarnOnce(SaveableObject boat, string message)
        {
            string name = boat != null ? boat.gameObject.name : "<null>";
            if (_lastWarnedBoat == name) return;
            _lastWarnedBoat = name;
            Plugin.Log.LogWarning(message);
        }

        private static void ClearWarnLatch(SaveableObject boat)
        {
            string name = boat != null ? boat.gameObject.name : "<null>";
            if (_lastWarnedBoat == name) _lastWarnedBoat = null;
        }

        /// <summary>
        /// Shape-level validation for a received rig blob, before it is ever written into
        /// GameState.modData or handed to SE's LoadSailConfig.
        ///
        /// PUBLIC because the HOST calls it on the receive path (OnSERigStateReceived) to refuse to
        /// star-relay a malformed blob to the rest of the crew: the host must not amplify garbage
        /// into N-1 peers' saves. ApplyRigBlob still calls it too - that is deliberate defence in
        /// depth, NOT redundancy: a blob can also reach ApplyRigBlob straight from the host (no relay
        /// gate in front of it) or from the join (the tail of Phase A), so this must stay the last gate
        /// before modData as well as the first gate before the relay. Do not delete either call site.
        ///
        /// This guard exists because LoadSailConfig has many UNGUARDED throw sites on malformed
        /// input (Convert.ToInt32/ToSingle/bool.Parse on split fragments, plus a raw version[1] /
        /// version[2] index), and because modData is persisted into the save and SE re-parses this
        /// key on EVERY SaveableBoatCustomization.LoadData. A blob that throws is therefore not a
        /// one-off: it would throw again on every load. (ApplyRigBlob's rollback is the second half
        /// of that defence - do not delete either one.)
        ///
        /// Cheap enough for the receive path: one LastIndexOf plus a split of a short version tail.
        ///
        /// SE's real blob is "{body}|{major}.{minor}.{patch}", e.g. "...|0.9.0". We require AT
        /// LEAST three version segments because SE reads version[1] and version[2] with no length
        /// check against an int[] that VersionManager.GetVersion sizes to the tail's ACTUAL segment
        /// count, so a 2-segment tail like "1.0" makes SE throw IndexOutOfRangeException. A longer
        /// tail (e.g. a future "1.0.0.1") indexes those same two slots fine, so it must still pass.
        /// We deliberately do not hard-code the version DIGITS either, so a future SE version
        /// still passes.
        ///
        /// sep == 0 (a body-less blob like "|0.9.0") IS a valid shape: SaveSailConfig appends the
        /// version suffix unconditionally, so a boat whose masts all carry zero sails produces
        /// exactly this - it is a legitimate SE output, not corruption. ApplyRigBlob special-cases
        /// sep == 0 as a benign no-op (nothing to apply); it must never be WRITTEN to modData over a
        /// real blob, but the shape check here must accept it so that no-op path can be reached.
        /// </summary>
        public static bool IsValidRigBlob(string blob)
        {
            if (string.IsNullOrEmpty(blob)) return false;
            int sep = blob.LastIndexOf('|');
            if (sep < 0 || sep == blob.Length - 1) return false;

            string tail = blob.Substring(sep + 1);
            string[] parts = tail.Split('.');
            if (parts.Length < 3) return false;

            foreach (string part in parts)
            {
                // ASCII digits only, bounded length. char.IsDigit would accept non-ASCII digits
                // (e.g. Arabic-Indic), which SE's Convert.ToInt32 throws FormatException on, and an
                // 11-digit segment throws OverflowException. Both would land in SE, not here.
                if (part.Length == 0 || part.Length > 9) return false;
                foreach (char c in part)
                {
                    if (c < '0' || c > '9') return false;
                }
                if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _)) return false;
            }
            return true;
        }

        /// <summary>
        /// Extract the current SE sail-extras blob for a boat. Calls SE's SaveSailConfig first so
        /// the modData entry reflects LIVE state, then reads it. Null when unavailable.
        /// </summary>
        public static string GetRigBlob(SaveableObject boat)
        {
            if (!Enabled || boat == null || GameState.modData == null) return null;
            var refs = boat.GetComponent<BoatRefs>();
            if (refs == null) return null;

            string key = ModDataKey(boat);
            bool had = GameState.modData.TryGetValue(key, out var prev);
            try
            {
                // Probe, do not just read: SaveSailConfig returns WITHOUT writing when it aborts (any
                // sail on the boat missing a SailScaler - it logs "No sail scaler component found!
                // Aborting data for this boat"), leaving the PREVIOUS key in place. A plain read would
                // then hand a STALE blob back to the poller as if it were fresh and broadcast it.
                // Removing the key first makes "key absent afterwards" mean exactly "SE aborted".
                // Safe because this is all main-thread and synchronous - nothing observes the gap.
                GameState.modData.Remove(key);
                _saveSailConfig.Invoke(null, new object[] { refs });

                if (GameState.modData.TryGetValue(key, out var blob))
                {
                    ClearWarnLatch(boat);
                    return blob;
                }

                // SE aborted. Put the local save's copy back - we must not destroy it just by looking.
                if (had) GameState.modData[key] = prev;
                WarnOnce(boat, $"[SECompat] SE wrote no rig data for '{boat.gameObject.name}' (SaveSailConfig aborts when " +
                    "a sail has no SailScaler). Rig not synced for this boat.");
                return null;
            }
            catch (Exception e)
            {
                if (had) GameState.modData[key] = prev; else GameState.modData.Remove(key);
                var inner = Unwrap(e);
                WarnOnce(boat, $"[SECompat] GetRigBlob failed for '{boat.gameObject.name}': " +
                    $"{inner.GetType().Name}: {inner.Message}. modData left unchanged.");
                return null;
            }
        }

        /// <summary>
        /// Apply a received SE blob: write it under OUR local sceneIndex for this boat (never trust
        /// the sender's index), then have SE apply it to the sails.
        /// Caller MUST invalidate the rope cache afterwards (see ApplyRigBlobNow) - this method
        /// destroys and recreates the boat's RopeControllers.
        ///
        /// (F7) We do NOT hand the blob straight to SE's LoadSailConfig against the LIVE sails. SE never
        /// does that itself: its only caller is a Harmony postfix on SaveableBoatCustomization.LoadData
        /// (CustomizationCleaner), which runs immediately after vanilla rebuilt every sail from its prefab,
        /// so LoadSailConfig is written to assume an UNFLIPPED baseline:
        ///
        ///     if (array4.Length >= 5 &amp;&amp; bool.Parse(array4[4])) component.FlipJib(inv: true);
        ///
        /// There is no else. It can only ever turn a flip ON, which against a live, already-configured sail
        /// makes it a ONE-WAY LATCH: a peer who un-flips a jib sends a blob with the flag False, and the
        /// receiver's jib stays flipped forever. Worse, the receiver's next GetData poll fires SE's OWN
        /// GetData postfix (CustomizationCleaner2), which SaveSailConfigs the still-flipped LIVE sail back
        /// to True - so the wrong flip is persisted into that player's save and survives a reload. And it
        /// is not cosmetic: FlipJib(inv: true) SetActive(false)s a RopeEffect GameObject and the mast reef
        /// attachment extension, while BoatUtility.GetRopeControllers builds its array from
        /// GetComponentsInChildren, which EXCLUDES inactive GameObjects - so a peer stuck in the wrong flip
        /// state addresses every wire rope index differently from the editor.
        ///
        /// FlipJib(inv: false) is NOT a fix: SE's staysail rope-object deactivation sits inside
        /// "if (inv &amp;&amp; sail.category == staysail)" with no restore branch, so an unflip does not
        /// re-activate what the flip disabled. Restoring SE's own precondition is the only reliable route,
        /// so we rebuild the sails from the boat's CURRENT vanilla customization and let SE's own postfix do
        /// the applying - the one path SE is actually built for. Flips then apply in BOTH directions.
        ///
        /// The vanilla data is captured with GetData() BEFORE the blob is written to modData, and that order
        /// is load-bearing: SE postfixes GetData too (CustomizationCleaner2) and re-derives modData from the
        /// LIVE sails whenever GameState.currentShipyard != null on a purchased boat. Writing our blob first
        /// would let that postfix overwrite it with the receiver's own rig, and the apply would silently
        /// become a no-op on exactly the co-editing peers this feature exists for.
        /// </summary>
        public static bool ApplyRigBlob(SaveableObject boat, string blob)
        {
            if (!Enabled || boat == null || string.IsNullOrEmpty(blob)) return false;
            if (GameState.modData == null) return false;
            if (!IsValidRigBlob(blob))
            {
                WarnOnce(boat, $"[SECompat] Rejected malformed SE rig blob for '{boat.gameObject.name}' " +
                    "(expected a body plus a trailing '|<major>.<minor>.<patch>' tail); not applied.");
                return false;
            }

            // A body-less blob ("|0.9.0") is a LEGITIMATE SE output, not corruption: SaveSailConfig
            // appends the version suffix unconditionally, so a boat whose masts all carry zero
            // sails produces exactly this. There is nothing to apply, so we report success WITHOUT
            // writing to modData: writing it over a real blob would wipe the receiver's sail extras
            // (SE's LoadData postfix would no-op LoadSailConfig on the empty body, then
            // SaveSailConfig the live defaults straight back). If the receiver's own key is stale it
            // self-heals via that same postfix on its own next load - we do not need to touch it.
            if (blob.LastIndexOf('|') == 0)
            {
                ClearWarnLatch(boat);
                Plugin.Log.LogInfo($"[SECompat] Received a body-less SE rig blob for '{boat.gameObject.name}' " +
                    "(sender boat has zero sails); nothing to apply, modData left unchanged.");
                return true;
            }

            var refs = boat.GetComponent<BoatRefs>();
            if (refs == null) return false;

            // RequireComponent puts BoatRefs and SaveableBoatCustomization on the same boat root, so this is
            // non-null for every real boat. The null path is the fallback documented below, not the norm.
            var customization = boat.GetComponent<SaveableBoatCustomization>();

            string key = ModDataKey(boat);
            // Captured BEFORE anything below runs, including the GetData() call: SE's GetData postfix can
            // itself write modData[key], so this is the only capture that can restore the state we were
            // ACTUALLY handed. On any throw, nothing we did survives.
            bool had = GameState.modData.TryGetValue(key, out var prev);
            try
            {
                // 1. Read the boat's CURRENT vanilla customization first. This must precede the modData
                //    write (see the ordering note in the doc comment): SE's CustomizationCleaner2 postfix on
                //    GetData re-derives modData from the live sails while we are at a shipyard, so a blob
                //    written before this call would be overwritten by the receiver's own rig.
                SaveBoatCustomizationData vanillaData = customization != null ? customization.GetData() : null;

                // 2. Now the blob is safe to write: nothing else reads or rewrites modData until LoadData.
                GameState.modData[key] = blob;

                if (vanillaData != null)
                {
                    // 3. Vanilla rebuilds every sail from its prefab (an UNFLIPPED baseline), and SE's
                    //    CustomizationCleaner postfix on LoadData then runs LoadSailConfig on those fresh
                    //    sails, applying the blob we just wrote - flips included, in BOTH directions - and
                    //    SaveSailConfig re-derives the canonical modData entry from the result. This is the
                    //    exact sequence SE performs on a normal save load, which is why it is correct.
                    //    The data is the boat's OWN live vanilla state, so the structure is unchanged: this
                    //    rebuilds the same sails, it does not apply the sender's vanilla layout (packet 43
                    //    already did that, and it is sent first - see the order note in PollForChanges).
                    customization.LoadData(vanillaData);
                }
                else
                {
                    // Fallback: no SaveableBoatCustomization (or it handed back nothing). Apply straight to
                    // the live sails, which is what we did everywhere before F7. Still correct on the join
                    // path, where Phase A's vanilla LoadData has just rebuilt the sails for us; elsewhere it
                    // keeps the one-way flip latch, which is strictly better than not applying the rig at all.
                    _loadSailConfig.Invoke(null, new object[] { refs });
                }

                ClearWarnLatch(boat);
                return true;
            }
            catch (Exception e)
            {
                // Roll back to the EXACT prior state. modData is serialized into the save, and SE
                // re-parses this key on every SaveableBoatCustomization.LoadData (its postfix calls
                // LoadSailConfig BEFORE SaveSailConfig, so the key never self-heals), which is also the
                // path Co-op's own BoatStateApplicator drives. Leaving a throwing blob in place would
                // permanently break the save for one malformed packet from one peer.
                //
                // This catch now also covers vanilla LoadData and SE's Harmony postfixes on GetData/LoadData
                // (a postfix throw propagates out of the patched method, so it lands here exactly like the
                // reflected LoadSailConfig throw always did). Whatever threw, modData ends up as we found it
                // and nothing escapes into Unity.
                if (had) GameState.modData[key] = prev; else GameState.modData.Remove(key);
                var inner = Unwrap(e);
                WarnOnce(boat, $"[SECompat] ApplyRigBlob failed for '{boat.gameObject.name}': " +
                    $"{inner.GetType().Name}: {inner.Message}. Blob rolled back " +
                    (had ? "to the previous value." : "(modData key removed)."));
                return false;
            }
        }
    }
}
