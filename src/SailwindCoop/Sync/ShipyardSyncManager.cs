using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using SailwindCoop.Debug;
using SailwindCoop.Networking.Packets;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Syncs boat customization changes while at shipyard.
    /// Polls at 5Hz when GameState.currentShipyard is non-null.
    /// </summary>
    public class ShipyardSyncManager : MonoBehaviour
    {
        public static ShipyardSyncManager Instance { get; private set; }

        private const float PollInterval = 0.2f; // 5 Hz
        private float _lastPollTime;

        // Cached state for change detection
        private bool[] _lastMastsEnabled;
        private NetworkSailData[] _lastSails;
        private int[] _lastPartOptions;
        // (v0.2.31) Last Shipyard Expansion sail-extras blob observed for the boat being edited.
        // NULL means "no information" - NOT "the rig is empty". SECompat.GetRigBlob returns null for
        // FOUR distinct reasons: SE is absent, SE's data path is disabled (reflection failed or SE's own
        // "skip sail data" debug flag is on), the boat has no BoatRefs, or SE aborted/threw during its
        // own save (e.g. a sail with no SailScaler). None of those mean the sails went away, so a null
        // must never be broadcast and must never overwrite a good cached blob. See PollForChanges.
        private string _lastRigBlob;
        private bool _wasAtShipyard;

        // (v0.2.28 Fix C) Cross-peer shipyard cradle tracking. Vanilla AdmitShip/DischargeShip is purely
        // local: only the editing machine lifts the boat kinematically onto the cradle; the discharge's
        // instant teleport + physics re-enable registered a >1.5 m/s BoatDamage.Impact. Every peer tracks
        // the set of boats currently in a cradle (fed by ShipyardState packets + the local Harmony
        // postfixes below). The set is purely BOOKKEEPING on receivers: it gates when the discharge-time
        // impact-suppression window may start. We deliberately do NOT freeze or otherwise touch the boat's
        // rigidbody on receivers - the host keeps streaming and stays authoritative; the cosmetic cradle
        // lift is simply not synced. DamagePatches skips Impact for a short window after release.
        // A stale entry (e.g. the editing peer disconnected mid-edit) is therefore low-harm - it only
        // gates when damage suppression may begin - so no per-peer disconnect tracking is kept; the set
        // is cleared on session start and on Reset().
        // Static: read from Harmony patches and DamagePatches without an Instance dance.
        private static readonly HashSet<string> _shipyardActiveBoats = new HashSet<string>();
        // Boat name -> Time.unscaledTime of the shipyard release; Impact is suppressed within the window.
        private static readonly Dictionary<string, float> _dischargeTimes = new Dictionary<string, float>();
        private const float ImpactSuppressionWindow = 3f; // seconds after discharge (covers the release
                                                          // teleport, the local depenetration settle AND
                                                          // the remote forced convergence snap)

        // (v0.2.31) SERigState blobs that arrived before they could be applied: either their boat was not
        // resolvable yet (join race - the blob outran the boat's own spawn/registration) or a join apply is
        // in flight.
        //
        // The join case is load-bearing, not just a race, and the mechanism is MANGLING, not overwriting.
        // SECompat.ApplyRigBlob applies a blob by rebuilding the boat from ITS OWN CURRENT vanilla
        // customization. Before join Phase A has run, that is the GUEST's PRE-JOIN structure - so an early
        // apply would push the HOST's blob through SE's LoadSailConfig against a rig it was never authored
        // for, and LoadSailConfig does not object: it ignores entries past mast.sails.Count (SE decompile
        // :501), breaks out on a missing entry (:504-508) and skips unknown masts (:496-499). SE's postfix then
        // SaveSailConfigs that misapplied/truncated result straight back into modData - so when Phase A's real
        // LoadData rebuilds the boat to the HOST's structure, its postfix re-applies the MANGLED blob, and the
        // host's extras are gone for good. Park it here instead; the join drains it via ApplyPendingRigBlob at
        // the TAIL OF PHASE A - after that boat's vanilla customization apply, before the frame-wait that
        // precedes the rope re-key (Update's sweep below is the fallback for the plain boat race).
        // Value = (blob, arrival Time.unscaledTime); entries expire after PendingRigBlobTtl.
        // Static so the join (BoatStateApplicator) can reach it without an Instance dance.
        private static readonly Dictionary<string, KeyValuePair<string, float>> _pendingRigBlobs =
            new Dictionary<string, KeyValuePair<string, float>>();
        private const float PendingRigBlobTtl = 10f;
        // Sanity cap on a received blob. Counted in CHARS (UTF-16), which is what we compare against, not
        // bytes. SE's real blobs are a few hundred chars for a full-rigged brig; 64 K chars is orders of
        // magnitude of headroom while still refusing to feed something absurd into GameState.modData
        // (which is serialized into the save file).
        private const int MaxRigBlobChars = 64 * 1024;

        // (v0.2.31) Blob-poll decimation. SE Harmony-postfixes SaveableBoatCustomization.GetData and calls
        // SaveSailConfig whenever GameState.currentShipyard != null on a purchased boat - which is ALWAYS
        // true inside PollForChanges - so the GetData() call there already drives a full SE SaveSailConfig
        // every poll, and GetRigBlob would drive a SECOND one. Each of those does an unconditional
        // Debug.Log (Unity captures a managed stack trace for every one) plus a full blob rebuild, so with
        // SE the idle shipyard would cost 10 SE log lines a second, forever, in the very logs we triage
        // playtests from. We therefore read the blob only every RigPollDivisor'th poll (about 1.25 Hz;
        // worst-case 800 ms latency on a pure angle/flip/texture edit - invisible for a cosmetic edit) AND
        // unconditionally on any poll where the vanilla data changed, which is REQUIRED for correctness,
        // not an optimization: the send guard's second clause needs a FRESH blob on exactly those polls.
        // Reset on shipyard entry so the first poll after entering always reads.
        private const int RigPollDivisor = 4;
        private int _rigPollCounter;

        // (v0.2.31, C4) The boat currently on this machine's shipyard cradle, remembered while we poll it.
        // OnExitShipyard CANNOT re-derive it: vanilla Shipyard.DischargeShip nulls GameState.currentShipyard
        // and GameState.currentBoat in the SAME call (Shipyard.cs:298-299), and those are the only two
        // assignments of currentShipyard in the whole game - so by the time Update sees "not at shipyard",
        // BoatUtility.GetCurrentBoat() (which derives from GameState.currentBoat) already reads null. That is
        // also why shipyard exit and DischargeShip are the SAME event, not two paths to cover.
        // A plain field write per poll: no allocation, and nothing reads it without SE.
        private SaveableObject _shipyardBoat;

        // (v0.2.31, C2/C3) Boats with a rope-trim restore coroutine in flight. ApplyRigBlobNow used to
        // snapshot and start a coroutine on EVERY apply, so two blobs for the same boat inside one restore
        // window (two 215s draining in one frame - the reliable channel is uncoalesced - or a 215 landing in
        // the frame between a rebuild and its coroutine's resumption) took a SECOND snapshot AFTER the first
        // rebuild. Unity defers Destroy() to end of frame, so that snapshot's rope array holds BOTH the
        // doomed old controllers AND the fresh ones; both produce the same stable key, OrderBy is a stable
        // sort so the NEW one is enumerated last, and SnapshotRopeTrim writes with the dictionary INDEXER -
        // so the new controller, sitting at its PREFAB DEFAULT length, wins every key. That second snapshot
        // is a map of defaults, and its coroutine (started later) overwrites the good restore. The sails then
        // sit at default trim for the rest of the session: rope trim only reconverges through
        // ControlSyncManager's edge-triggered per-rope broadcast, so nothing heals it.
        //
        // Fix: coalesce per boat. The FIRST apply of a burst takes the only pre-rebuild snapshot - the only
        // pre-rebuild truth there is - and its restore runs after the LAST rebuild of the burst, which is
        // exactly the one we want. Later applies in the window rebuild but neither snapshot nor start a
        // second coroutine.
        private static readonly HashSet<string> _trimRestorePending = new HashSet<string>();

        // (v0.2.31, C1b) Ropes an AUTHORITATIVE apply (a peer's RopeState) wrote, stamped with a monotonic
        // sequence number. Keyed by boat name + stable rope key, NOT by controller instance: inside the
        // restore window the rebuild's doomed-but-not-yet-destroyed controllers are still enumerable, so the
        // instance that took the authoritative write is not necessarily the instance that survives to the
        // restore - but the LOGICAL rope is the same either way, and that is what we must not clobber. (It is
        // also what makes the map bounded: one entry per logical rope per boat in the world, a few hundred at
        // most, rather than one per destroyed controller.)
        //
        // Without this, RestoreRopeTrim would write the STALE pre-rebuild length over an authoritative value
        // that landed inside its window. A terminal (IsFinal) is the LAST packet for that rope, so nothing
        // re-sends it and the rope stays wrong for the rest of the session - the exact v0.2.25 "phantom
        // furled sails" bug, re-introduced. Worse when a GUEST is the shipyard editor: the HOST is a receiver
        // too, so the host's own authoritative value would be reverted and then re-broadcast to the crew.
        //
        // A SEQUENCE, NOT Time.frameCount, and that distinction is load-bearing. Both the packet drain
        // (Plugin.Update) and the rig apply run inside one frame, so a frame number cannot order an
        // authoritative apply against the snapshot when they share a frame - and the two sub-cases need
        // OPPOSITE answers:
        //   - RopeState BEFORE the 215 in the same drain: the snapshot was taken after it, so the snapshot
        //     ALREADY HOLDS the authoritative value and the rope must be RESTORED from it (skipping would
        //     strand the rebuilt rope at its prefab default).
        //   - RopeState AFTER the 215 in the same drain: the snapshot predates it, holds the stale value, and
        //     the rope must be SKIPPED.
        // "stamp > snapshot sequence" separates them exactly; "stamp frame >= snapshot frame" gets the first
        // one wrong. Reset()/session-exit clear the map, and PruneAuthoritativeRopeStamps keeps it tidy.
        //
        // WE STORE THE VALUE, NOT JUST THE SEQUENCE, AND THE RESTORE WRITES IT. A bare "skip the rope" is NOT
        // enough, because of the same deferred-Destroy quirk that forces the restore to wait a frame at all:
        // ApplyRigBlobNow invalidates the rope cache immediately after the rebuild, so a RopeState arriving
        // LATER IN THE SAME FRAME re-derives a rope array holding BOTH the doomed old controllers and the
        // fresh ones, and can therefore apply its authoritative length to a controller that is about to be
        // destroyed. Skipping the logical rope would then leave the SURVIVING controller sitting at its prefab
        // default - the authoritative value lost outright. Writing the remembered value onto the live
        // controller is identical to a skip when the RopeState did hit the surviving instance (the epsilon
        // guard makes it a no-op) and repairs the case where it did not.
        private static readonly Dictionary<string, KeyValuePair<long, float>> _authoritativeRopeStamp =
            new Dictionary<string, KeyValuePair<long, float>>();
        private static long _ropeApplySeq;
        // Stamps this far behind the live sequence can never cause a skip again (a snapshot only ever compares
        // against stamps NEWER than itself), so dropping them is pure hygiene, not logic.
        private const long RopeStampRetention = 4096;

        /// <summary>
        /// (v0.2.31, C1b) A RopeState from a peer was just applied to this rope: stamp it so a rope-trim
        /// restore that is mid-flight cannot revert it to the pre-rebuild length. Call site is
        /// ControlSyncManager.TryApplyRopePacket, gated on SE being installed, so the no-SE path is a single
        /// static bool test and nothing here ever runs.
        ///
        /// (P3) ONLY WHILE A RESTORE IS ACTUALLY IN FLIGHT. A stamp taken while _trimRestorePending is EMPTY
        /// can never cause a skip at any FUTURE restore, so recording it is pure waste. Proof: a restore only
        /// consults a stamp through "auth.Key > snapshotSeq", snapshotSeq is read (as _ropeApplySeq) after that
        /// restore's snapshot and BEFORE _trimRestorePending.Add, the sequence only ever grows, and no packet
        /// can drain in between (ApplyRigBlobNow is fully synchronous from SnapshotRopeTrim through the Add -
        /// nothing in it pumps the network). So any stamp recorded while the set was empty predates every later
        /// snapshotSeq and compares as "not newer", i.e. the snapshot ALREADY HOLDS that authoritative value and
        /// the restore is right to write it. Conversely a RopeState landing while a restore IS pending is still
        /// stamped, in the same frame's drain or the next one's (the coroutine's "yield return null" resumes
        /// after Update, and the packet drain lives in Plugin.Update), so C1(b) is intact.
        ///
        /// The set is checked GLOBALLY, not per boat: that is deliberately conservative. A rope on boat B
        /// stamped only because boat A has a restore pending is harmless extra bookkeeping; the reverse (a
        /// missing stamp) is the bug we are protecting against, and it cannot happen.
        ///
        /// The win: this early-out precedes BoatUtility.GetStableRopeKey, so an SE-installed crew no longer
        /// pays its 2-3 GetComponent/GetComponentInParent walks plus two string allocations on EVERY received
        /// rope packet - only on the handful that land inside a rig-rebuild window. The no-SE path is unchanged
        /// (the call site's IsInstalled test already made it free).
        /// </summary>
        internal static void MarkRopeAuthoritative(string boatName, RopeController rope)
        {
            if (_trimRestorePending.Count == 0) return;
            if (string.IsNullOrEmpty(boatName) || rope == null) return;
            // Read the length back off the rope rather than taking it as a parameter: the caller has just
            // written it, so this is the value that actually landed, whatever the packet said.
            _authoritativeRopeStamp[AuthoritativeRopeKey(boatName, BoatUtility.GetStableRopeKey(rope))] =
                new KeyValuePair<long, float>(++_ropeApplySeq, rope.currentLength);
        }

        // A space is an unambiguous separator here: BoatUtility.GetStableRopeKey NEVER emits one (its keys are
        // "0~anchor", "0~helm", "9~null" or "1~m00~s00~<role>~<side>" with fixed alphanumeric roles/sides), so
        // no boat-name + rope-key pair can alias another one, however the boat is named.
        private static string AuthoritativeRopeKey(string boatName, string stableRopeKey) =>
            boatName + " " + stableRopeKey;

        // Session transition detection (Update early-returns while not in a session, so the first in-session
        // frame is the session start and the first out-of-session frame after one is the session end).
        private bool _wasInSession;

        /// <summary>True while ANY peer (including this machine) has this boat on a shipyard cradle.</summary>
        public static bool IsBoatShipyardActive(string boatName)
        {
            return !string.IsNullOrEmpty(boatName) && _shipyardActiveBoats.Contains(boatName);
        }

        /// <summary>
        /// True within a short window after the boat left a shipyard cradle. The discharge is an instant
        /// teleport + physics re-enable (vanilla MoveShip instantMove), and on non-editing peers a forced
        /// snap - either can depenetrate at >1.5 m/s and register phantom hull damage.
        /// </summary>
        public static bool IsImpactSuppressed(string boatName)
        {
            if (string.IsNullOrEmpty(boatName)) return false;
            if (!_dischargeTimes.TryGetValue(boatName, out var t)) return false;
            if (Time.unscaledTime - t < ImpactSuppressionWindow) return true;
            // Expired: drop the entry so HasActiveSuppression goes false again and DamagePatches'
            // cheap early-out keeps the common Impact path free of GetComponent lookups.
            _dischargeTimes.Remove(boatName);
            return false;
        }

        /// <summary>
        /// Cheap early-out for DamagePatches: false when no discharge window can possibly be live,
        /// so the common Impact path never pays a GetComponent lookup.
        /// </summary>
        public static bool HasActiveSuppression => _dischargeTimes.Count > 0;

        private void Awake()
        {
            if (Instance != null)
            {
                Destroy(this);
                return;
            }
            Instance = this;
        }

        private void Update()
        {
            if (!Plugin.IsMultiplayer)
            {
                // (v0.2.31) Session EXIT: drop any SE rig blob still parked in the buffer. Reset() covers the
                // clean teardown paths, but this manager also has session-start clears for
                // _shipyardActiveBoats/_dischargeTimes precisely because sessions can end WITHOUT a clean
                // teardown - and a pending blob that survives such an end is worse than a stale set entry:
                // the sweep below resolves the boat BEFORE checking the TTL (deliberately - see there), so on
                // the first in-session frame of the NEXT session it would find a same-named boat and APPLY a
                // blob from the previous session instead of letting the TTL expire it.
                //
                // Clearing here on EXIT rather than on session ENTRY is what keeps the load-bearing property
                // intact: a join blob can legitimately land and be buffered BEFORE this manager sees its first
                // in-session Update frame, and an entry-time clear would silently drop the joiner's rig.
                // Gated on _wasInSession so the solo/main-menu steady state stays a single bool test.
                //
                // (C1b/C2/C3) The SE trim-preservation state goes with it. _trimRestorePending must not
                // survive a session: an entry left behind would suppress trim preservation for that boat for
                // the whole NEXT session (any in-flight coroutine's finally is a no-op on an already-empty
                // set, so this cannot deadlock against it). _authoritativeRopeStamp holds stamps for boats
                // and ropes that mean nothing across a session boundary.
                //
                // _ropeApplySeq is deliberately NOT reset. It is monotonic, and a coroutine that survives the
                // transition still holds a snapshotSeq from the old session: leaving the counter running keeps
                // every future stamp strictly greater than that stale snapshotSeq, which is the conservative
                // answer (skip, do not clobber). Zeroing it would invert that comparison.
                //
                // (C4) _shipyardBoat goes with them, and it is NOT bookkeeping. Reset() clears it and covers the
                // clean lobby-exit path, so this only bites on the UNCLEAN end this block exists for: _wasAtShipyard
                // would stay true and _shipyardBoat would still point at the boat we were editing, so the first
                // in-session frame of the NEXT session takes the "!atShipyard && _wasAtShipyard" branch and
                // OnExitShipyard broadcasts an unintended 43 + 215 for that boat (if SE is installed and the object
                // is still alive) into a session that never asked for it.
                //
                // _wasAtShipyard is deliberately NOT cleared here. It also gates OnExitShipyard's rope-cache
                // invalidate and ResendRopeForCurrentBoat, which run OUTSIDE the SE gate and must stay
                // byte-for-byte identical without SE. Nulling _shipyardBoat alone is read only inside the
                // IsInstalled gate, so this is an SE-path-only change: without SE, OnExitShipyard still fires
                // and still does exactly what it always did.
                if (_wasInSession)
                {
                    _pendingRigBlobs.Clear();
                    _trimRestorePending.Clear();
                    _authoritativeRopeStamp.Clear();
                    _shipyardBoat = null;
                }
                _wasInSession = false;
                return;
            }

            // Session start: clear the static cradle/discharge tracking so nothing leaks in from a
            // previous session (Reset() also clears on teardown; this is belt-and-braces for paths
            // that start a new session without a clean teardown, e.g. a hot-reload race).
            // _pendingRigBlobs is deliberately NOT cleared here - see the session-exit clear above.
            if (!_wasInSession)
            {
                _wasInSession = true;
                _shipyardActiveBoats.Clear();
                _dischargeTimes.Clear();
            }

            Plugin.Profiler?.StartMeasure();

            bool atShipyard = GameState.currentShipyard != null;

            // Detect shipyard entry/exit
            if (atShipyard && !_wasAtShipyard)
            {
                OnEnterShipyard();
            }
            else if (!atShipyard && _wasAtShipyard)
            {
                OnExitShipyard();
            }

            _wasAtShipyard = atShipyard;

            // Poll for changes while at shipyard
            if (atShipyard)
            {
                PollForChanges();
            }

            // (v0.2.31) Retry buffered SE rig blobs whose boat was not resolvable when they landed (join
            // race). Free when the buffer is empty, which is the case for every session without SE and for
            // virtually every frame of a session with it. Held back entirely while a join apply is in
            // flight: applying now would push the host's blob through SE against the guest's PRE-JOIN rig and
            // mangle it (see _pendingRigBlobs), so those entries belong to the join, which drains them with
            // ApplyPendingRigBlob at the tail of Phase A. Entries expire after PendingRigBlobTtl so a blob for
            // a boat that never appears cannot leak.
            if (_pendingRigBlobs.Count > 0 && !BoatSyncManager.IsJoinInProgress)
            {
                List<string> done = null;
                foreach (var kv in _pendingRigBlobs)
                {
                    // Resolve FIRST, expire second. The TTL clock keeps running while a join holds this
                    // sweep back, so a long join could otherwise push a perfectly applicable blob past the
                    // deadline and we would throw away a rig we can plainly see the boat for. Only a blob
                    // whose boat still does not exist is allowed to expire.
                    var pendingBoat = BoatUtility.FindBoatByName(kv.Key);
                    if (pendingBoat != null)
                    {
                        ApplyRigBlobNow(pendingBoat, kv.Value.Key);
                        (done = done ?? new List<string>()).Add(kv.Key);
                        continue;
                    }

                    if (Time.unscaledTime - kv.Value.Value > PendingRigBlobTtl)
                    {
                        Plugin.Log.LogWarning($"[SECompat] Pending SERigState for '{kv.Key}' expired unapplied " +
                            $"after {PendingRigBlobTtl:F0}s (that boat never appeared here); dropped.");
                        (done = done ?? new List<string>()).Add(kv.Key);
                    }
                    // else: boat still not here and not expired - try again next frame.
                }
                if (done != null)
                {
                    foreach (var k in done) _pendingRigBlobs.Remove(k);
                }
            }

            Plugin.Profiler?.EndMeasure("Shipyard");
        }

        private void OnEnterShipyard()
        {
            VerboseLogger.ShipyardPoll("Entered shipyard mode");
            // Cache current state
            CacheCurrentState();
            // (C4) Remember the boat we are editing. GameState.currentBoat is nulled by DischargeShip in the
            // same call that ends shipyard mode, so this is the ONLY handle OnExitShipyard can still use.
            _shipyardBoat = BoatUtility.GetCurrentBoat();
            // (v0.2.31) Seed the SE blob cache from the rig as it stands on entry, so the first poll only
            // fires on an actual edit. Null (no SE, or GetRigBlob could not read it) is the correct seed:
            // the diff then stays dormant until a real, non-null blob shows up.
            _lastRigBlob = Compat.SECompat.GetRigBlob(_shipyardBoat);
            // Restart the blob-poll decimation phase so the FIRST poll of this shipyard visit reads the blob
            // (it may already differ from the seed above - e.g. a 215 landed in between).
            _rigPollCounter = 0;
        }

        private void OnExitShipyard()
        {
            VerboseLogger.ShipyardPoll("Exited shipyard mode");

            // (v0.2.31, C4) FINAL SE BLOB READ, BEFORE THE CACHE IS CLEARED. A pure SE edit (angle, flip,
            // texture) does not touch the vanilla customization data at all, so vanillaChanged is false for it
            // and PollForChanges only reads the blob every RigPollDivisor'th poll - up to 800 ms of blind
            // time. PollForChanges also only runs WHILE atShipyard, and on the frame the player leaves,
            // Update takes this branch INSTEAD of polling. So an SE-only edit made inside that last blind
            // interval was never broadcast - and never healed: the editor's next OnEnterShipyard re-seeds
            // _lastRigBlob from the local rig and adopts the unsent state as "already known", so the diff
            // never fires for it again. Flip a jib, close the shipyard within 800 ms, and the crew disagreed
            // about the rig permanently. Read once more here and send if it moved.
            //
            // Shipyard exit IS the DischargeShip path: vanilla assigns GameState.currentShipyard in exactly
            // two places, AdmitShip (= this) and DischargeShip (= null), so there is no second way out to
            // cover. It also nulls GameState.currentBoat in the same call, which is why we use the boat we
            // memorised rather than BoatUtility.GetCurrentBoat().
            //
            // Off the hot path (once per shipyard visit) and a hard no-op without SE: IsInstalled is false,
            // and GetRigBlob would return null on its first statement anyway.
            var edited = _shipyardBoat;
            _shipyardBoat = null;
            if (edited != null && Compat.SECompat.IsInstalled)
            {
                // ORDER IS LOAD-BEARING - 43 FIRST, 215 SECOND, exactly as in PollForChanges, and this block
                // mirrors that send guard deliberately rather than inventing a second one.
                //
                // The vanilla half is here for a reason, and it is NOT scope creep: PollForChanges is rate
                // limited to 5 Hz and the exit frame runs THIS branch instead of polling, so the last poll
                // interval is blind to a VANILLA edit too, not just an SE one. Sending the 215 alone in that
                // window would be actively worse than the C4 bug it fixes: ApplyRigBlob rebuilds the receiver
                // from the RECEIVER's OWN vanilla customization, so a blob describing a sail the receiver was
                // never told about lands on a rig that cannot hold it - and SE does NOT throw on that, it
                // SILENTLY MISAPPLIES. LoadSailConfig's inner loop is bounded by mast.sails.Count (SE decompile
                // :501), so entries for sails the receiver lacks are ignored; a sail with no entry logs and
                // breaks (:504-508); an unknown mast index is skipped (:496-499). Nothing rolls back, nothing
                // warns, and the receiver's angles/flips/textures end up attached to the WRONG sails. Sending
                // 43 first is what makes the 215 applicable.
                //
                // Everything here is inside the IsInstalled gate, so the SE-ABSENT path is untouched: without
                // SE this whole block is skipped and the 5 Hz vanilla blind spot behaves exactly as it always
                // has. This is deliberate - closing that for vanilla too would be a behavior change on a path
                // that must stay byte-for-byte identical.
                var customization = edited.GetComponent<SaveableBoatCustomization>();
                var finalData = customization != null ? customization.GetData() : null;

                bool vanillaChanged = finalData != null && HasCustomizationChanged(finalData);
                if (vanillaChanged) SendCustomizationUpdate(edited, finalData);

                string finalBlob = Compat.SECompat.GetRigBlob(edited);
                bool rigChanged = !string.IsNullOrEmpty(finalBlob) && finalBlob != _lastRigBlob;

                // Second clause mirrors PollForChanges, and so does its real justification: it is a REPAIR
                // BEACON, not a reset-recovery. A receiver applying the 43 does NOT lose its extras (SE's
                // LoadData postfix runs LoadSailConfig before SaveSailConfig, SE :3803-3811, so its own blob is
                // re-applied to the rebuilt sails); re-sending the current blob on every structural edit simply
                // heals any receiver whose blob has drifted. Cheap now that ApplyRigBlobNow skips the rebuild
                // when the live rig already encodes the blob.
                if (rigChanged || (vanillaChanged && !string.IsNullOrEmpty(finalBlob)))
                {
                    SendRigBlob(edited, finalBlob);
                    // Same reason PollForChanges invalidates on an SE-only change: a flip SetActive(false)s a
                    // RopeEffect GameObject, and GetRopeControllers excludes inactive objects, so the rope
                    // array (and every wire index after the flipped sail) just shifted on this machine.
                    BoatUtility.InvalidateRopeCache(edited);
                }
                // No CacheState/_lastRigBlob write: every cache this could seed is cleared immediately below.
            }

            // Clear cache
            _lastMastsEnabled = null;
            _lastSails = null;
            _lastPartOptions = null;
            _lastRigBlob = null;

            // Belt-and-braces for the phantom-furled-sails fix: a change landing in the same frame as the
            // exit would be cached-then-missed, so re-invalidate on the way out, and (host only) re-seed
            // every current rope length so crew whose sails were rebuilt to defaults by LoadData converge
            // immediately instead of waiting for the next host winch movement.
            var boat = BoatUtility.GetCurrentBoat();
            if (boat != null) BoatUtility.InvalidateRopeCache(boat);
            if (Plugin.IsHost) ControlSyncManager.Instance?.ResendRopeForCurrentBoat();
        }

        private void PollForChanges()
        {
            if (Time.time - _lastPollTime < PollInterval) return;
            _lastPollTime = Time.time;

            var boat = BoatUtility.GetCurrentBoat();
            if (boat == null) return;

            // (C4) Keep the exit handle current (the boat can only change by leaving and re-admitting, but a
            // plain field write costs nothing and makes the handle unconditionally right). No allocation.
            _shipyardBoat = boat;

            var customization = boat.GetComponent<SaveableBoatCustomization>();
            if (customization == null) return;

            var data = customization.GetData();
            if (data == null) return;

            // Check if anything changed
            bool vanillaChanged = HasCustomizationChanged(data);

            // (v0.2.31) Shipyard Expansion's sail extras (angle, flip, texture) do NOT touch vanilla
            // SaveBoatCustomizationData at all, so HasCustomizationChanged is blind to them: the blob needs
            // its own diff. With SE absent GetRigBlob returns null on the first statement of the method, so
            // rigBlob stays null, rigChanged stays false, nothing is allocated and no SERigState is ever
            // sent - vanilla/solo behavior is untouched.
            //
            // A null blob is "no information", never "the rig is now empty" (SE absent or its data path
            // disabled, no BoatRefs, SE aborted/threw mid-save - e.g. a sail with no SailScaler - or, since
            // the decimation below, simply "we did not look this poll"). So:
            //   - a null is never a change (we would otherwise broadcast an empty rig, and SendRigBlob's
            //     no-op guard is only the second line of defence), and
            //   - a null never overwrites the cache (clobbering a good _lastRigBlob with null would make
            //     the very next successful poll look like a change and re-broadcast the identical blob;
            //     with a flapping abort that is a re-send every other poll, forever). _lastRigBlob is
            //     assigned ONLY inside the send block below, which a skipped poll can never reach.
            //
            // ORDER IS LOAD-BEARING: vanillaChanged is computed FIRST (above) so the decimation can be
            // overridden on exactly the polls that need a fresh blob. readRig is true whenever the vanilla
            // data changed - the send guard's second clause re-sends the blob on those polls and MUST have
            // a current one - and otherwise only every RigPollDivisor'th poll, which halves SE's per-poll
            // SaveSailConfig cost and its unconditional 5 Hz Debug.Log. On a skipped poll rigBlob stays
            // null, so rigChanged is false and the send block is unreachable: a skip can never be mistaken
            // for "the rig became empty" and can never clobber the cache.
            bool readRig = vanillaChanged || _rigPollCounter == 0;
            _rigPollCounter = (_rigPollCounter + 1) % RigPollDivisor;

            string rigBlob = readRig ? Compat.SECompat.GetRigBlob(boat) : null;
            bool rigChanged = !string.IsNullOrEmpty(rigBlob) && rigBlob != _lastRigBlob;

            if (!vanillaChanged && !rigChanged)
            {
                VerboseLogger.ShipyardPoll($"No changes, masts={data.masts?.Count(m => m) ?? 0}, sails={data.sails?.Count ?? 0}", throttle: true);
                return;
            }

            if (vanillaChanged)
            {
                // Send update
                SendCustomizationUpdate(boat, data);

                // Update cache
                CacheState(data);
            }

            // ORDER IS LOAD-BEARING - DO NOT "TIDY" THIS BELOW THE InvalidateRopeCache OR ABOVE THE
            // SendCustomizationUpdate. The structural packet (43) goes out FIRST and the SE blob (215)
            // SECOND, on the same reliable channel, so receivers apply them in that order.
            //
            // The reason is MISAPPLICATION, not erasure. A blob is authored against the SENDER's rig, and
            // SECompat.ApplyRigBlob applies it by rebuilding from the RECEIVER's own vanilla customization -
            // so a 215 that lands BEFORE its 43 is applied against the receiver's OLD, pre-edit structure, and
            // SE's LoadSailConfig does NOT complain: its inner loop is bounded by mast.sails.Count (SE
            // decompile :501), so blob entries for sails the receiver does not have yet are simply IGNORED; a
            // sail with no blob entry hits "j >= array3.Length" and logs + breaks out of that mast (:504-508);
            // and a mast index the receiver does not have is skipped (:496-499). It SILENTLY misapplies or
            // truncates. SE's postfix then SaveSailConfigs that mangled result back into modData, and the 43
            // arriving next rebuilds the sails and re-applies the MANGLED blob to them. Landing 215 after 43
            // means the blob meets the structure it was actually authored for.
            //
            // The second clause re-sends an UNCHANGED blob whenever the vanilla data changed. It is NOT
            // needed to repair a receiver "reset to defaults" by the 43 - it cannot be, because SE's
            // CustomizationCleaner postfix calls LoadSailConfig BEFORE SaveSailConfig (SE :3803-3811), so a
            // receiver applying a 43 immediately gets its OWN blob re-applied to the rebuilt sails and keeps
            // its extras. What the clause actually does is re-broadcast the current, authoritative blob on
            // every structural edit, which incidentally REPAIRS any receiver whose blob has drifted (an
            // aborted SaveSailConfig, a dropped 215 on an earlier edit, a peer that joined mid-edit). Kept for
            // that repair-beacon value: with the rebuild skip at the top of ApplyRigBlobNow, a receiver that is
            // already correct now pays one packet plus one SaveSailConfig for it, and no rebuild at all.
            if (rigChanged || (vanillaChanged && !string.IsNullOrEmpty(rigBlob)))
            {
                SendRigBlob(boat, rigBlob);
                _lastRigBlob = rigBlob;
            }

            // (v0.2.31) An SE-only edit MUST invalidate too, and not merely as insurance: SE's
            // SailScaler.FlipJib SetActive(false)s a RopeEffect GameObject inside the sail hierarchy (and the
            // mast reef-attachment extension with it), while BoatUtility.GetRopeControllers builds its array
            // from GetComponentsInChildren<RopeController>(), which EXCLUDES inactive GameObjects. A flip can
            // therefore REMOVE an entry from the stable-sorted rope array and shift every wire rope index
            // after it - a stale cache would leave this machine addressing ropes by the pre-flip indices
            // while the rest of the crew uses the post-flip ones. The invalidate is a single dictionary
            // remove; if nothing actually changed the next GetRopeControllers re-derives the identical array.
            if (vanillaChanged || rigChanged)
            {
                // Phantom-furled sails (Robin report, v0.2.25): a LOCAL shipyard sail change destroys the old
                // sail GameObjects (and their RopeControllers) and instantiates new ones, but only the RECEIVER
                // path (ApplyCustomization) invalidated the rope cache. The changer's own machine kept polling
                // the stale cached RopeController[] (destroyed entries read null and are skipped; the NEW reef/
                // sheet ropes are absent), so its unfurl/trim changes were never broadcast and the crew saw the
                // sails furled forever while the boat visibly sailed. Invalidate on every detected local change.
                BoatUtility.InvalidateRopeCache(boat);
            }
        }

        private bool HasCustomizationChanged(SaveBoatCustomizationData data)
        {
            // First time - always changed
            if (_lastMastsEnabled == null) return true;

            // Check masts
            var currentMasts = data.masts ?? new bool[30];
            if (_lastMastsEnabled.Length != currentMasts.Length) return true;
            for (int i = 0; i < currentMasts.Length; i++)
            {
                if (_lastMastsEnabled[i] != currentMasts[i]) return true;
            }

            // Check sails count
            var currentSails = data.sails;
            if ((_lastSails?.Length ?? 0) != (currentSails?.Count ?? 0)) return true;

            // Check sail details
            if (currentSails != null && _lastSails != null)
            {
                for (int i = 0; i < currentSails.Count && i < _lastSails.Length; i++)
                {
                    var curr = currentSails[i];
                    var last = _lastSails[i];
                    if (curr.prefabIndex != last.PrefabIndex ||
                        curr.mastIndex != last.MastIndex ||
                        curr.sailColor != last.Color ||
                        Mathf.Abs(curr.installHeight - last.InstallHeight) > 0.01f ||
                        Mathf.Abs(curr.minAngle - last.MinAngle) > 0.01f ||
                        Mathf.Abs(curr.maxAngle - last.MaxAngle) > 0.01f ||
                        Mathf.Abs(curr.scaleY - last.ScaleY) > 0.001f ||      // BS1-live: detect a sail resize
                        Mathf.Abs(curr.scaleZ - last.ScaleZ) > 0.001f)
                        return true;
                }
            }

            // Check part options
            var currentParts = data.partActiveOptions;
            if ((_lastPartOptions?.Length ?? 0) != (currentParts?.Count ?? 0)) return true;
            if (currentParts != null && _lastPartOptions != null)
            {
                for (int i = 0; i < currentParts.Count; i++)
                {
                    if (_lastPartOptions[i] != currentParts[i]) return true;
                }
            }

            return false;
        }

        private void SendCustomizationUpdate(SaveableObject boat, SaveBoatCustomizationData data)
        {
            var packet = new ShipyardCustomizationPacket
            {
                BoatName = boat.gameObject.name,
                MastsEnabled = data.masts ?? new bool[30],
                Sails = data.sails?.Select(s => new NetworkSailData
                {
                    PrefabIndex = s.prefabIndex,
                    MastIndex = s.mastIndex,
                    InstallHeight = s.installHeight,
                    MinAngle = s.minAngle,
                    MaxAngle = s.maxAngle,
                    Health = s.health,
                    Color = s.sailColor,
                    ScaleY = s.scaleY,  // BS1-live
                    ScaleZ = s.scaleZ
                }).ToArray() ?? new NetworkSailData[0],
                PartActiveOptions = data.partActiveOptions?.ToArray() ?? new int[0]
            };

            VerboseLogger.ShipyardSend($"boat={packet.BoatName}, masts={packet.MastsEnabled.Count(m => m)}, sails={packet.Sails.Length}, parts={packet.PartActiveOptions.Length}");

            Plugin.NetworkManager.SendToAllReliable(PacketType.ShipyardCustomization, writer =>
            {
                PacketSerializer.WriteShipyardCustomization(writer, packet);
            });
        }

        /// <summary>
        /// (v0.2.31) Broadcast one boat's Shipyard Expansion sail-extras blob. No-op on a null/empty blob:
        /// null from GetRigBlob means "could not read it" (SE absent or disabled, no BoatRefs, SE aborted or
        /// threw), NEVER "the rig is empty", so it must not be shipped as if it were state. A boat with zero
        /// sails legitimately produces a body-less but NON-empty blob ("|0.9.0"), which does go out.
        /// </summary>
        private void SendRigBlob(SaveableObject boat, string rigBlob)
        {
            if (boat == null || string.IsNullOrEmpty(rigBlob)) return;

            var packet = new SERigStatePacket { BoatName = boat.gameObject.name, RigBlob = rigBlob };
            VerboseLogger.ShipyardSend($"SERigState, boat={packet.BoatName}, blobLen={rigBlob.Length}");
            Plugin.NetworkManager.SendToAllReliable(PacketType.SERigState, w =>
                PacketSerializer.WriteSERigState(w, packet));
        }

        /// <summary>
        /// (v0.2.31) HOST, join step: send every boat's SE rig blob to the joining guest, right after the
        /// BoatWorldState snapshot. Same reliable, ordered channel, so these land after the snapshot and while
        /// the guest's join apply is still running - the guest buffers them (IsJoinInProgress) and the join
        /// drains them with ApplyPendingRigBlob at the tail of Phase A, which is exactly the ordering SE needs
        /// (that boat's vanilla LoadData has just run and would otherwise erase the blob; see B5 in the plan
        /// errata). Hard no-op without SE, so a vanilla crew sends nothing.
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
        /// (v0.2.31) A SERigState packet arrived. The host star-relays it to the other guests, exactly like
        /// OnCustomizationReceived, so a guest's SE edit reaches the whole crew at 3+.
        ///
        /// DELIBERATE DEVIATION from the "relay BEFORE FindBoatByName" convention stated at
        /// OnCustomizationReceived above: here the relay sits AFTER the size AND SHAPE checks, so a malformed
        /// or oversized blob is dropped at the host and never forwarded. That is intentional, not an
        /// oversight - unlike a customization snapshot, this payload is written into GameState.modData, which
        /// is SERIALIZED INTO THE SAVE, and SE re-parses it (with unguarded throw sites) on every
        /// SaveableBoatCustomization.LoadData. One bad blob fanned out to the crew could permanently break
        /// several saves at once, so the host does not amplify garbage. Both checks are cheap enough to sit
        /// on the receive path (a length compare, one LastIndexOf and a split of a short version tail). The
        /// relay still runs before the boat LOOKUP, so a host that cannot resolve the boat locally still
        /// forwards a well-formed blob.
        /// </summary>
        public void OnSERigStateReceived(SERigStatePacket packet, Steamworks.SteamId sender = default)
        {
            VerboseLogger.ShipyardRecv($"SERigState, boat={packet.BoatName}, blobLen={packet.RigBlob?.Length ?? 0}");

            if (string.IsNullOrEmpty(packet.BoatName) || string.IsNullOrEmpty(packet.RigBlob)) return;
            if (packet.RigBlob.Length > MaxRigBlobChars)
            {
                Plugin.Log.LogWarning($"[SECompat] Oversized SERigState blob for '{packet.BoatName}' " +
                    $"({packet.RigBlob.Length} chars, cap {MaxRigBlobChars}) - dropped, not relayed.");
                return;
            }
            // Shape, not just size. SECompat.ApplyRigBlob would reject this blob on every receiver anyway,
            // but the host must refuse to RELAY it: the doc above promises the host does not amplify garbage,
            // and that promise is the whole justification for validating before relaying here.
            if (!Compat.SECompat.IsValidRigBlob(packet.RigBlob))
            {
                Plugin.Log.LogWarning($"[SECompat] Malformed SERigState blob for '{packet.BoatName}' " +
                    "(expected a body plus a trailing '|<major>.<minor>.<patch>' tail) - dropped, not relayed.");
                return;
            }

            if (Plugin.IsHost)
                Plugin.NetworkManager.SendToAllExcept(sender, PacketType.SERigState, w =>
                    PacketSerializer.WriteSERigState(w, packet));

            // Park the blob when it cannot be applied YET:
            // - boat == null: it outran its boat (join race). The Update sweep retries it.
            // - a join apply is in flight: applying now would re-derive this blob against the guest's PRE-JOIN
            //   rig and silently mangle it (see _pendingRigBlobs), so it must land AFTER Phase A's LoadData.
            //   The join drains it via ApplyPendingRigBlob at the tail of Phase A.
            var boat = BoatUtility.FindBoatByName(packet.BoatName);
            if (boat == null || BoatSyncManager.IsJoinInProgress)
            {
                _pendingRigBlobs[packet.BoatName] =
                    new KeyValuePair<string, float>(packet.RigBlob, Time.unscaledTime);
                return;
            }

            ApplyRigBlobNow(boat, packet.RigBlob);
        }

        /// <summary>
        /// (v0.2.31) Take the buffered SE rig blob for a boat, if one arrived during the join. Removes the
        /// entry, so the Update sweep will not re-apply it. Prefer ApplyPendingRigBlob over calling this
        /// directly - it is the funnel that cannot forget the rope-cache invalidate.
        /// </summary>
        public static bool TryConsumePendingRigBlob(string boatName, out string blob)
        {
            blob = null;
            if (string.IsNullOrEmpty(boatName)) return false;
            if (!_pendingRigBlobs.TryGetValue(boatName, out var entry)) return false;

            _pendingRigBlobs.Remove(boatName);
            blob = entry.Key;
            return true;
        }

        /// <summary>
        /// (v0.2.31) The join's ONE call: consume the blob buffered for this boat during the join (if any) and
        /// apply it. Exists so the join never has to hand-roll the apply and silently drop half of it -
        /// ApplyRigBlobNow's rope-cache invalidate and _lastRigBlob adoption are not optional, and the latter
        /// is private instance state the applicator could not touch at all. Safe to call for every boat: a
        /// no-op when nothing was buffered.
        ///
        /// FRAME ORDERING (F7): the apply now goes through a vanilla SaveableBoatCustomization.LoadData, so it
        /// DESTROYS and recreates this boat's RopeControllers exactly as a packet-43 apply does. Unity defers
        /// the destruction to the end of the frame, which is precisely why the join coroutine already waits a
        /// frame between Phase A's LoadData and the Phase B rope-length apply. Call this on the Phase A side of
        /// that wait (after the vanilla customization apply, before the yield), NEVER between the yield and
        /// ApplyRopeLengths: a rebuild in that window would leave the just-destroyed ropes in the freshly built
        /// RopeController[] and shift every index the rope lengths are matched against.
        /// </summary>
        public static void ApplyPendingRigBlob(SaveableObject boat)
        {
            if (boat == null) return;
            // (F9) Check the instance BEFORE consuming: TryConsumePendingRigBlob REMOVES the entry, so a
            // null Instance would silently eat the blob and neither the Update sweep nor anything else could
            // ever apply it. Unreachable today (the manager outlives any join), but this keeps the buffer's
            // invariant unconditional: an entry only ever leaves the buffer to be applied or to expire.
            var instance = Instance;
            if (instance == null) return;

            if (TryConsumePendingRigBlob(boat.gameObject.name, out var blob))
                instance.ApplyRigBlobNow(boat, blob);
        }

        /// <summary>
        /// (v0.2.31) Hand a blob to SE and refresh the rope cache. Callers MUST have already applied any
        /// pending vanilla customization for this boat (see the ordering note in PollForChanges / B5).
        ///
        /// Also the single place the sail TRIM is preserved across SE's rebuild (F2 below). Doing it here,
        /// in the SE-gated funnel, is what keeps the join coroutine untouched.
        /// </summary>
        private void ApplyRigBlobNow(SaveableObject boat, string blob)
        {
            if (boat == null || string.IsNullOrEmpty(blob)) return;

            string boatName = boat.gameObject.name;

            // (v0.2.31, P2) REBUILD SKIP: the live sails may ALREADY encode this blob, in which case applying it
            // is a provable no-op and the rebuild is pure cost. SaveSailConfig's entire contract is "serialize
            // the LIVE sails" (SE decompile :534-578 - it walks refs.masts, reads each sail's prefabIndex,
            // scaleZ/scaleY, SailScaler.Angle, SailScaler.Flipped and SailTextureChanger.textureIndex, and
            // appends "|0.9.0"). So if it emits EXACTLY the string we were sent, the live sails already carry
            // those extras and there is nothing for a rebuild to change.
            //
            // WHY THIS FIRES ON THE COMMON PATH - a structural shipyard edit ships 43 + 215 as a pair, and
            // before this the receiver did TWO full sail rebuilds for it (one from ApplyCustomization's
            // LoadData, one from ApplyRigBlob's). Holding a sail-move or sail-scale button drives that pair at
            // 5 Hz, i.e. ~10 rebuilds a second on every receiver. But SE's CustomizationCleaner postfix on
            // SaveableBoatCustomization.LoadData (SE decompile :3803-3811) calls LoadSailConfig BEFORE
            // SaveSailConfig, and LoadSailConfig READS this machine's own "SEboatSails.{sceneIndex}" key
            // (:467) and re-applies it to the just-rebuilt sails. So the 43 does NOT flatten the receiver's SE
            // extras - SE puts them straight back - and the 215 that follows finds the rig already correct:
            //   - DRAG (sail height/position): the blob does not change at all, so live == blob. Skip.
            //   - SCALE (SailScaleButton auto-repeats every 0.05 s while held, SE :754-769): the blob's scale
            //     fields DO change, so this is a genuinely new blob. But LoadSailConfig IGNORES scale for a
            //     0.9.0 blob - its version gate is "version[1] < 9 && version[2] < 94" (:516) and
            //     GetVersion("...|0.9.0") returns [0,9,0], so 9 < 9 is false and the LoadScale branch is DEAD.
            //     Scale reaches the receiver through the VANILLA packet instead (SaveSailData.scaleY/scaleZ,
            //     applied by Mast.LoadSail, vanilla Mast.cs:129-132). So after the 43 the receiver already has
            //     the new scale plus its unchanged angle/flip/texture, its re-derived blob equals the sender's,
            //     and the 215 skips. The scale path collapses to ONE rebuild.
            //   - ROTATE / FLIP / TEXTURE: live != blob, so we fall through and rebuild, which is exactly what
            //     the flip-latch fix (see SECompat.ApplyRigBlob) requires. Rotate-held still costs two rebuilds
            //     per poll; that is accepted.
            //
            // A FALSE POSITIVE IS IMPOSSIBLE unless SaveSailConfig misreports the live sails. A false NEGATIVE
            // (e.g. float-formatting drift between peers) is harmless: we fall through to exactly the behavior
            // this method had before. Cost of the check is one SaveSailConfig (a string build) against a full
            // sail rebuild.
            //
            // NULL IS NOT A MATCH. GetRigBlob returns null for four distinct reasons (SE absent, SE's data path
            // disabled, no BoatRefs, SE aborted/threw its own save) and none of them means "the live rig equals
            // this blob" - so a null MUST fall through to the rebuild path, where ApplyRigBlob re-checks the
            // same gates and returns false. Ordinal compare: this is a wire string, not text.
            //
            // Placed BEFORE the snapshot deliberately: a skipped 215 takes no trim snapshot and starts no
            // restore coroutine, which shrinks the same-frame double-apply window C1-C3 are about.
            string live = Compat.SECompat.GetRigBlob(boat);
            if (live != null && string.Equals(live, blob, System.StringComparison.Ordinal))
            {
                // Nothing was rebuilt, so nothing was destroyed - but keep the funnel's invariant anyway (F1).
                // It is free on the path that matters: the 43 that preceded this 215 already invalidated
                // (ApplyCustomization), so this is a remove of an absent key.
                BoatUtility.InvalidateRopeCache(boat);

                // Same baseline adoption as the applied path below: if we are standing at the shipyard editing
                // this very boat, the next poll must not read the (identical) rig as a local edit and echo it.
                if (GameState.currentShipyard != null && BoatUtility.GetCurrentBoat() == boat)
                    _lastRigBlob = blob;

                VerboseLogger.ShipyardApply($"SERigState no-op, boat={boatName}, blobLen={blob.Length} " +
                    "(live rig already encodes this blob - sail rebuild skipped)");
                return;
            }

            // (F2) SECompat.ApplyRigBlob applies the blob by REBUILDING the sails through vanilla LoadData,
            // so every recreated RopeController comes back at its PREFAB DEFAULT length: without this, an
            // applied 215 silently resets that boat's sail trim on the receiver - worst of all for a purely
            // COSMETIC SE edit (angle/flip/texture), which before F7 would not have rebuilt anything at all.
            // Nor does it heal on its own: rope trim only converges again through
            // ControlSyncManager.OnLocalRopeChanged, which is edge-triggered PER SINGLE ROPE INDEX, so one
            // host rope edit heals exactly one rope. Snapshot the trim here, restore it after the rebuild.
            //
            // JOIN GATE (load-bearing): during a join, Phase B applies the HOST's authoritative rope lengths
            // from the join snapshot one frame after Phase A's rebuild. A restore firing around that moment
            // would race it and could overwrite the host's lengths with this machine's stale pre-join
            // defaults. So during a join we neither snapshot nor restore - Phase B owns the rope lengths.
            //
            // The IsInstalled clause makes "SE absent starts no coroutine and allocates nothing" structural
            // rather than merely true-by-reachability. (It IS also unreachable without SE: every caller of
            // this method is downstream of a received SERigState, no peer without SE ever sends one, and the
            // handshake refuses a crew whose SE presence/version disagrees.)
            //
            // (C2/C3) COALESCED PER BOAT. A restore already in flight for this boat means its snapshot was
            // taken BEFORE the first rebuild of this burst - the only pre-rebuild truth that exists - and its
            // restore will run after the LAST rebuild of the burst, because a "yield return null" coroutine
            // resumes AFTER every Update (and the packet drain lives in Plugin.Update). So we rebuild, and
            // deliberately neither re-snapshot nor start a second coroutine. Re-snapshotting here would read
            // the rope array AFTER a rebuild, where Unity's end-of-frame Destroy() leaves the doomed OLD
            // controllers and the fresh ones side by side under the same stable keys - and the fresh,
            // PREFAB-DEFAULT ones win the dictionary indexer. See _trimRestorePending.
            bool preserveTrim = Compat.SECompat.IsInstalled
                && !BoatSyncManager.IsJoinInProgress
                && !_trimRestorePending.Contains(boatName);
            var trim = preserveTrim ? SnapshotRopeTrim(boat) : null;
            // (C1b) Everything an authoritative apply stamps AFTER this point is newer than the snapshot, and
            // must survive the restore. Read after the snapshot, deliberately: see _authoritativeRopeStamp.
            long snapshotSeq = _ropeApplySeq;

            bool applied = Compat.SECompat.ApplyRigBlob(boat, blob);

            // Invalidate UNCONDITIONALLY (F1). MANDATORY, not prudence: SECompat.ApplyRigBlob applies the
            // blob by rebuilding the sails through vanilla LoadData, which really does destroy and recreate
            // every RopeController on this boat. A FALSE return does NOT mean the cache is intact - SE's
            // CustomizationCleaner postfix runs AFTER vanilla LoadData's body, so a throw inside SE's
            // LoadSailConfig (unguarded Convert.ToInt32/bool.Parse sites that IsValidRigBlob does NOT cover -
            // it validates the version tail's shape, not the blob BODY) lands in ApplyRigBlob's catch AFTER
            // the sails were already destroyed and recreated. modData is rolled back; the object hierarchy is
            // not. This is the single funnel for all three callers (the live 215 receive, the pending-blob
            // retry sweep and the tail of join Phase A), so none of them can forget it.
            BoatUtility.InvalidateRopeCache(boat);

            // Restore on the SAME failure paths, and for the same reason: if the blob threw inside SE the
            // sails were still rebuilt to defaults, so the trim still needs putting back. If the rebuild
            // never happened (blob rejected before LoadData, SE's data path off), the snapshot matches the
            // live ropes and the restore writes nothing.
            //
            // The pending mark and the coroutine are started together, and the mark is cleared in the
            // coroutine's finally, so the two can never disagree about whether a restore is in flight.
            if (trim != null)
            {
                _trimRestorePending.Add(boatName);
                StartCoroutine(RestoreRopeTrimNextFrame(boatName, trim, snapshotSeq));
            }

            if (!applied) return; // SECompat already logged and rolled back

            // If we happen to be standing at the shipyard editing this very boat, adopt the applied blob as
            // our own baseline so the next poll does not see it as a local edit and echo it straight back.
            if (GameState.currentShipyard != null && BoatUtility.GetCurrentBoat() == boat)
                _lastRigBlob = blob;

            VerboseLogger.ShipyardApply($"SERigState applied, boat={boat.gameObject.name}, blobLen={blob.Length}");
        }

        /// <summary>
        /// (v0.2.31, F2) Capture this boat's current rope lengths keyed by the STABLE rope key, so they can
        /// be put back after SE's sail rebuild. Null when there is nothing to preserve.
        ///
        /// KEYED, NEVER POSITIONAL. SE's SailScaler.FlipJib(inv: true) SetActive(false)s a RopeEffect
        /// GameObject, and GetComponentsInChildren EXCLUDES inactive GameObjects, so the rope array's LENGTH
        /// can legitimately change across a flip toggle and every index after it shifts. A key survives that:
        /// a rope that vanished is simply never looked up again, and a rope that newly appeared is absent
        /// from the map and keeps its prefab default (the only honest answer - we have no trim for a rope
        /// that did not exist a frame ago).
        ///
        /// (C1a) The CONTROLLER INSTANCE is stored alongside the length, and that is not bookkeeping: it is
        /// how the restore tells a REBUILT rope from a SURVIVING one. Vanilla LoadData only runs
        /// Mast.RemoveAllSails / LoadSail, so it only ever destroys SAIL ropes - the anchor rope
        /// (RopeControllerAnchor, key "0~anchor") and the helm rope (RopeControllerSteeringWheel, key
        /// "0~helm") are not sail children and come through the rebuild as the SAME objects, at whatever
        /// length they now hold. They need no restoring, and the restore must not touch them: a player who
        /// raises the anchor or turns the helm inside the restore window would otherwise have it silently
        /// yanked back to the pre-rebuild value. Same instance = never replaced = skip.
        /// </summary>
        private static Dictionary<string, KeyValuePair<RopeController, float>> SnapshotRopeTrim(SaveableObject boat)
        {
            var ropes = BoatUtility.GetRopeControllers(boat);
            if (ropes.Length == 0) return null;

            var trim = new Dictionary<string, KeyValuePair<RopeController, float>>(ropes.Length);
            foreach (var rope in ropes)
            {
                if (rope == null) continue;
                // Indexer, not Add: GetStableRopeKey is documented as "should not collide, but defensive",
                // and a duplicate key must not throw out of a packet handler.
                trim[BoatUtility.GetStableRopeKey(rope)] =
                    new KeyValuePair<RopeController, float>(rope, rope.currentLength);
            }
            return trim.Count > 0 ? trim : null;
        }

        /// <summary>
        /// (v0.2.31, F2) Put the snapshotted trim back after SE's rebuild. Unity defers Destroy() to the end
        /// of the frame, so the recreated RopeControllers are only safely enumerable NEXT frame - hence the
        /// single yield. Nothing may throw out of a coroutine, so the whole body is guarded.
        ///
        /// (C2/C3) The finally is load-bearing, not tidiness: _trimRestorePending is what suppresses a second
        /// (defaults-carrying) snapshot for this boat, so an entry that never came out would suppress trim
        /// preservation for that boat for the rest of the session. finally around a yield is legal C# (the
        /// restriction is on yielding inside a try that has a CATCH), so the inner try/catch sits around the
        /// non-yielding call.
        /// </summary>
        private System.Collections.IEnumerator RestoreRopeTrimNextFrame(
            string boatName, Dictionary<string, KeyValuePair<RopeController, float>> trim, long snapshotSeq)
        {
            try
            {
                yield return null;

                try { RestoreRopeTrim(boatName, trim, snapshotSeq); }
                catch (System.Exception e)
                {
                    // Cosmetic-only failure: the boat keeps its rebuilt rig, the crew keeps syncing. Never throw.
                    Plugin.Log.LogWarning($"[SECompat] Restoring sail trim after the SE rig rebuild failed for " +
                        $"'{boatName}': {e.GetType().Name}: {e.Message}. Sails may sit at default trim until the " +
                        "next rope change.");
                }
            }
            finally
            {
                _trimRestorePending.Remove(boatName);
            }
        }

        private static void RestoreRopeTrim(
            string boatName, Dictionary<string, KeyValuePair<RopeController, float>> trim, long snapshotSeq)
        {
            // A join that STARTED during our frame-wait now owns this boat's rope lengths (Phase B applies
            // the host's authoritative snapshot). Stand down rather than race it - same reason we do not
            // snapshot during a join at all.
            if (BoatSyncManager.IsJoinInProgress) return;

            // Re-resolve: the boat can have been destroyed (stream-out, disconnect) across the wait.
            var boat = BoatUtility.FindBoatByName(boatName);
            if (boat == null) return;

            // Re-invalidate before reading. ApplyRigBlobNow invalidated LAST frame, but anything that called
            // GetRopeControllers between then and the end of that frame (e.g. ControlSyncManager's poll)
            // re-cached the array while the old controllers were Destroy()-marked but NOT yet null, so the
            // cache can hold exactly the doomed set. Rebuild it now that the destruction has actually landed.
            BoatUtility.InvalidateRopeCache(boat);

            var ropes = BoatUtility.GetRopeControllers(boat);
            int restored = 0;
            foreach (var rope in ropes)
            {
                if (rope == null) continue;

                string key = BoatUtility.GetStableRopeKey(rope);
                // Absent from the map = a rope that did not exist before the rebuild (e.g. an un-flipped jib
                // brought its RopeEffect back). Leave it at its prefab default.
                if (!trim.TryGetValue(key, out var snapshot)) continue;

                // (C1a) SURVIVED the rebuild - it is literally the same object we snapshotted, so its length
                // was never reset and there is nothing to put back. This is the ONLY correct test: an epsilon
                // compare protects a rope that did not MOVE, but a surviving rope that moved for a LEGITIMATE
                // reason inside the window (a player raising the anchor, turning the helm) is exactly the one
                // it would clobber. Only ropes the rebuild actually REPLACED get restored.
                if (ReferenceEquals(snapshot.Key, rope)) continue;

                // (C1b) An AUTHORITATIVE rope value (a peer's RopeState) landed on this logical rope AFTER the
                // snapshot was taken. It is newer than our pre-rebuild trim and it is the crew's shared truth
                // - and if it was a terminal, nothing will ever re-send it. Restoring the STALE pre-rebuild
                // length over it is precisely the v0.2.25 "phantom furled sails" bug. So the authoritative
                // value wins, and we WRITE it rather than merely skipping the rope: the RopeState may have
                // landed on a controller the rebuild was already destroying (see _authoritativeRopeStamp), in
                // which case the live one is still at its prefab default and a skip would strand it there. If
                // it did land on this live controller, the epsilon guard below turns this into a no-op.
                //
                // A stamp OLDER than the snapshot needs no special case: the snapshot was taken after it, so
                // snapshot.Value ALREADY IS that authoritative value.
                float length = snapshot.Value;
                if (_authoritativeRopeStamp.TryGetValue(AuthoritativeRopeKey(boatName, key), out var auth)
                    && auth.Key > snapshotSeq)
                {
                    length = auth.Value;
                }

                if (Mathf.Abs(rope.currentLength - length) <= 0.0001f) continue;

                rope.currentLength = length;
                rope.changed = true; // same coupled write as BoatStateApplicator.ApplyRopeLengths
                restored++;
            }

            PruneAuthoritativeRopeStamps();

            // Not broadcast, and must not be: ControlSyncManager only sends rope changes the local player is
            // OPERATING (IsLocalOperatingRope), so this restore of our own pre-rebuild trim stays local.
            if (restored > 0)
                VerboseLogger.ShipyardApply($"SE rig rebuild: restored {restored}/{ropes.Length} rope lengths " +
                    $"on '{boatName}' (trim preserved across the sail rebuild)");
        }

        /// <summary>
        /// (v0.2.31, C1b) Drop authoritative-rope stamps that no future restore can ever consult (a snapshot
        /// only ever skips a rope whose stamp is NEWER than itself, and the sequence only ever grows). Called
        /// only from the restore path, which is rare - once per SE rig-apply burst, never without SE. The map
        /// is already bounded by construction (boat name + logical rope key, not controller instance); this
        /// just stops it holding entries for boats nobody has touched in hours.
        /// </summary>
        private static void PruneAuthoritativeRopeStamps()
        {
            if (_authoritativeRopeStamp.Count == 0) return;

            long cutoff = _ropeApplySeq - RopeStampRetention;
            if (cutoff <= 0) return;

            List<string> stale = null;
            foreach (var kv in _authoritativeRopeStamp)
            {
                if (kv.Value.Key < cutoff) (stale = stale ?? new List<string>()).Add(kv.Key);
            }
            if (stale != null)
            {
                foreach (var k in stale) _authoritativeRopeStamp.Remove(k);
            }
        }

        private void CacheCurrentState()
        {
            var boat = BoatUtility.GetCurrentBoat();
            if (boat == null) return;

            var customization = boat.GetComponent<SaveableBoatCustomization>();
            if (customization == null) return;

            var data = customization.GetData();
            if (data != null)
            {
                CacheState(data);
            }
        }

        private void CacheState(SaveBoatCustomizationData data)
        {
            _lastMastsEnabled = (bool[])data.masts?.Clone() ?? new bool[30];
            _lastSails = data.sails?.Select(s => new NetworkSailData
            {
                PrefabIndex = s.prefabIndex,
                MastIndex = s.mastIndex,
                InstallHeight = s.installHeight,
                MinAngle = s.minAngle,
                MaxAngle = s.maxAngle,
                Health = s.health,
                Color = s.sailColor,
                ScaleY = s.scaleY,  // BS1-live
                ScaleZ = s.scaleZ
            }).ToArray() ?? new NetworkSailData[0];
            _lastPartOptions = data.partActiveOptions?.ToArray() ?? new int[0];
        }

        /// <summary>
        /// Called when receiving customization packet from other player.
        /// </summary>
        public void OnCustomizationReceived(ShipyardCustomizationPacket packet, Steamworks.SteamId sender = default)
        {
            VerboseLogger.ShipyardRecv($"boat={packet.BoatName}, masts={packet.MastsEnabled?.Count(m => m) ?? 0}, sails={packet.Sails?.Length ?? 0}, parts={packet.PartActiveOptions?.Length ?? 0}");

            // R4.8 N-player audit (star-relay): ShipyardCustomization was the ONE guest-originated state event
            // missing its host relay, so a guest's mast/sail/part change was invisible to OTHER guests at 3+ (and
            // the host folds the snapshot into its change-detection cache, so it never re-broadcasts either). The
            // packet is a full boat-keyed snapshot, so NO author field is needed - just forward it to the other
            // guests. Relay BEFORE FindBoatByName so it still forwards even if the host can't resolve the boat.
            if (Plugin.IsHost)
                Plugin.NetworkManager.SendToAllExcept(sender, PacketType.ShipyardCustomization, w =>
                    PacketSerializer.WriteShipyardCustomization(w, packet));

            var boat = BoatUtility.FindBoatByName(packet.BoatName);
            if (boat == null)
            {
                Plugin.Log.LogWarning($"ShipyardSync: Boat '{packet.BoatName}' not found");
                return;
            }

            ApplyCustomization(boat, packet);

            // Update our cache so we don't immediately re-send.
            //
            // (F8) ALL FOUR cached fields are guarded on "the packet targets the boat WE are standing on".
            // They describe that boat and nothing else, so folding a packet for a DIFFERENT boat into them
            // is not a stale-cache nuisance, it is an echo storm: peer A editing boat X at shipyard 1 would
            // have its cache overwritten with peer B's boat Y (shipyard 2), so A's next poll reads
            // vanillaChanged for X, re-broadcasts 43(X) AND (because the send guard forces a blob read on any
            // vanilla change) 215(X), clobbering B's cache symmetrically. Both peers then trade
            // correct-but-redundant 43+215 pairs at 5 Hz for as long as they both stand at a shipyard.
            var localBoat = GameState.currentShipyard != null ? BoatUtility.GetCurrentBoat() : null;
            if (localBoat != null && localBoat.gameObject.name == packet.BoatName)
            {
                _lastMastsEnabled = (bool[])packet.MastsEnabled?.Clone() ?? new bool[30];
                _lastSails = (NetworkSailData[])packet.Sails?.Clone() ?? new NetworkSailData[0];
                _lastPartOptions = (int[])packet.PartActiveOptions?.Clone() ?? new int[0];

                // (v0.2.31) The LoadData above just rebuilt this boat's sails to the packet's STRUCTURE, and
                // SE's CustomizationCleaner postfix (LoadData -> LoadSailConfig -> SaveSailConfig) re-derived
                // this machine's modData blob from the result. Note the sails are NOT reset to defaults - SE
                // re-applies our own blob to them first (LoadSailConfig runs BEFORE SaveSailConfig, SE decompile
                // :3803-3811) - but the re-derived blob still differs from our cached one whenever the structure
                // moved, because the body carries each sail's prefabIndex and scale (SE :558-566) and a sail we
                // no longer have simply drops out of it. Our cached blob is therefore stale BY CONSTRUCTION the
                // moment we apply a 43. Re-seed it from the live rig, or the next poll reads SE's own
                // re-derivation as a LOCAL edit and echoes it straight back to the whole crew.
                //
                // The 215 that normally follows a 43 overwrites this seed again via ApplyRigBlobNow, so this
                // is a no-op in the common case. It is load-bearing in the one case where no 215 follows: the
                // sender's GetRigBlob returned null (SE aborted its SaveSailConfig, e.g. a sail with no
                // SailScaler), so the send guard shipped the 43 alone.
                _lastRigBlob = Compat.SECompat.GetRigBlob(localBoat);
            }
        }

        private void ApplyCustomization(SaveableObject boat, ShipyardCustomizationPacket packet)
        {
            var customization = boat.GetComponent<SaveableBoatCustomization>();
            if (customization == null)
            {
                Plugin.Log.LogWarning($"ShipyardSync: No SaveableBoatCustomization on {boat.gameObject.name}");
                return;
            }

            // Build SaveBoatCustomizationData from packet
            var saveData = new SaveBoatCustomizationData
            {
                masts = packet.MastsEnabled ?? new bool[30],
                sails = packet.Sails?.Select(s => new SaveSailData
                {
                    prefabIndex = s.PrefabIndex,
                    mastIndex = s.MastIndex,
                    installHeight = s.InstallHeight,
                    minAngle = s.MinAngle,
                    maxAngle = s.MaxAngle,
                    health = s.Health,
                    sailColor = s.Color,
                    scaleY = s.ScaleY,  // BS1-live: apply the resized sail scale (Mast.LoadSail gates on scaleY!=0)
                    scaleZ = s.ScaleZ
                }).ToList() ?? new System.Collections.Generic.List<SaveSailData>(),
                partActiveOptions = packet.PartActiveOptions?.ToList() ?? new System.Collections.Generic.List<int>()
            };

            customization.LoadData(saveData);

            // Invalidate rope cache - LoadData destroys old RopeControllers and creates new ones
            BoatUtility.InvalidateRopeCache(boat);

            VerboseLogger.ShipyardApply($"boat={boat.gameObject.name}, applied masts/sails/parts");
        }

        /// <summary>
        /// (v0.2.28 Fix C) Local AdmitShip/DischargeShip happened on THIS machine (Harmony postfixes
        /// below): record it and announce to the crew. Guests send to the host, which relays.
        /// </summary>
        internal static void OnLocalShipyardState(string boatName, bool active)
        {
            // Multiplayer only: in solo every side effect here (set mutation, snap arming, packet send)
            // must stay off so vanilla shipyard behavior is 100% untouched.
            if (!Plugin.IsMultiplayer) return;
            if (string.IsNullOrEmpty(boatName)) return;

            if (active)
            {
                _shipyardActiveBoats.Add(boatName);
            }
            else
            {
                _shipyardActiveBoats.Remove(boatName);
                // Suppress Impact locally too: the editing machine's own instant release teleport +
                // physics re-enable can depenetrate against the water/dock at >1.5 m/s.
                _dischargeTimes[boatName] = Time.unscaledTime;
                // The editing machine is the peer whose boat pose diverges MOST from the host stream
                // (cradle lift + release teleport happened only here), and if it is a guest it never
                // receives its own relayed ShipyardState packet (the host relays SendToAllExcept sender).
                // Arm the one-shot convergence snap locally so the resumed stream teleports instead of
                // velocity-chasing the cradle-to-release gap.
                BoatSyncManager.Instance?.ForceSnapOnNextApply(boatName);
            }

            var packet = new ShipyardStatePacket { BoatName = boatName, Active = active };
            VerboseLogger.ShipyardSend($"ShipyardState, boat={boatName}, active={active}");
            Plugin.NetworkManager.SendToAllReliable(PacketType.ShipyardState, w =>
                PacketSerializer.WriteShipyardState(w, packet));
        }

        /// <summary>
        /// (v0.2.28 Fix C) A ShipyardState packet arrived from the editing peer. Host relays to the other
        /// guests (star topology, same pattern as OnCustomizationReceived). The receiver is by construction
        /// NOT the editing machine (the relay excludes the sender), so:
        /// - active=true: bookkeeping ONLY. Record the boat name; no rigidbody is touched on any receiver,
        ///   ever - the host stream stays authoritative and the boat keeps bobbing in the water here (the
        ///   cosmetic cradle lift is deliberately not synced).
        /// - active=false: start the impact-suppression window, zero velocities ONCE, and force a
        ///   snap-on-next-apply so this peer converges to the post-discharge pose without velocity-chasing.
        /// </summary>
        public void OnShipyardStateReceived(ShipyardStatePacket packet, Steamworks.SteamId sender = default)
        {
            VerboseLogger.ShipyardRecv($"ShipyardState, boat={packet.BoatName}, active={packet.Active}");

            if (Plugin.IsHost)
                Plugin.NetworkManager.SendToAllExcept(sender, PacketType.ShipyardState, w =>
                    PacketSerializer.WriteShipyardState(w, packet));

            if (string.IsNullOrEmpty(packet.BoatName)) return;

            if (packet.Active)
            {
                _shipyardActiveBoats.Add(packet.BoatName);
            }
            else
            {
                _shipyardActiveBoats.Remove(packet.BoatName);
                _dischargeTimes[packet.BoatName] = Time.unscaledTime;

                // One-time settle: kill any residual correction velocity so the convergence snap below
                // starts from rest (never continuous, never kinematic - just this single zeroing).
                var boat = BoatUtility.FindBoatByName(packet.BoatName);
                var rb = boat != null ? boat.GetComponent<Rigidbody>() : null;
                if (rb != null && !rb.isKinematic)
                {
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }

                // Converge to the editing peer's post-discharge pose via a one-shot teleport snap instead
                // of a violent velocity chase across the cradle-to-release-point gap.
                BoatSyncManager.Instance?.ForceSnapOnNextApply(packet.BoatName);
                Plugin.Log.LogInfo($"[SHIPYARD] '{packet.BoatName}' discharged on a remote peer; snapping to authoritative pose, impact suppressed {ImpactSuppressionWindow:F0}s");
            }
        }

        /// <summary>
        /// Reset sync state when disconnecting.
        /// </summary>
        public void Reset()
        {
            _lastPollTime = 0f;
            _lastMastsEnabled = null;
            _lastSails = null;
            _lastPartOptions = null;
            _lastRigBlob = null;
            _rigPollCounter = 0;
            _wasAtShipyard = false;
            _shipyardBoat = null;
            _shipyardActiveBoats.Clear();
            _dischargeTimes.Clear();
            // (C1b/C2/C3) SE trim-preservation state: see the matching clear on the session-EXIT transition
            // in Update. A stranded _trimRestorePending entry would silently disable trim preservation for
            // that boat, and rope stamps are meaningless across a session. _ropeApplySeq stays monotonic on
            // purpose - see the note at the session-exit clear.
            _trimRestorePending.Clear();
            _authoritativeRopeStamp.Clear();
            // Cleared here (the clean disconnect/teardown path) and on the session-EXIT transition in
            // Update, which covers a session that ends without a clean teardown. It is deliberately NOT
            // cleared on session ENTRY, unlike _shipyardActiveBoats/_dischargeTimes: a join blob can
            // legitimately arrive and be buffered before this manager sees its first in-session Update
            // frame, and clearing it there would silently drop the joiner's rig.
            _pendingRigBlobs.Clear();
        }
    }

    /// <summary>
    /// (v0.2.28 Fix C) Harmony patches announcing the local shipyard cradle lift/release to the crew.
    /// Lives here with the rest of the shipyard sync logic (no dedicated ShipyardPatches file exists).
    /// </summary>
    [HarmonyPatch(typeof(Shipyard), "AdmitShip")]
    public static class ShipyardAdmitShipPatch
    {
        [HarmonyPostfix]
        public static void Postfix(GameObject ship)
        {
            if (!Plugin.IsMultiplayer) return; // solo shipyard use stays 100% vanilla
            if (ship == null) return;
            // ship is the boat ROOT (it carries BoatRefs); prefer the SaveableObject name, the shared key.
            var saveable = ship.GetComponent<SaveableObject>();
            ShipyardSyncManager.OnLocalShipyardState(saveable != null ? saveable.gameObject.name : ship.name, active: true);
        }
    }

    [HarmonyPatch(typeof(Shipyard), nameof(Shipyard.DischargeShip))]
    public static class ShipyardDischargeShipPatch
    {
        // DischargeShip nulls currentShip inside the method, so capture the boat in a prefix.
        [HarmonyPrefix]
        public static void Prefix(Shipyard __instance, out GameObject __state)
        {
            __state = __instance.GetCurrentBoat();
        }

        [HarmonyPostfix]
        public static void Postfix(GameObject __state)
        {
            if (!Plugin.IsMultiplayer) return; // solo shipyard use stays 100% vanilla (no velocity zeroing)
            if (__state == null) return;

            // Vanilla MoveShip(instantMove: true) runs synchronously to completion inside DischargeShip
            // (t starts at 1, the lerp loop never yields), so by this postfix the boat has already been
            // teleported to shipReleasePosition and physics re-enabled. Zero the velocities NOW so the
            // water/dock depenetration on the editing machine cannot register a >1.5 m/s BoatDamage.Impact.
            var rb = __state.GetComponent<Rigidbody>();
            if (rb != null && !rb.isKinematic)
            {
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }

            var saveable = __state.GetComponent<SaveableObject>();
            ShipyardSyncManager.OnLocalShipyardState(saveable != null ? saveable.gameObject.name : __state.name, active: false);
        }
    }
}
