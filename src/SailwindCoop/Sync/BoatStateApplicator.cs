using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using HarmonyLib;
using SailwindCoop.Networking.Packets;
using SailwindCoop.Patches;

namespace SailwindCoop.Sync
{
    public static class BoatStateApplicator
    {
        private static BoatWorldStatePacket _pendingPacket;
        private static Coroutine _applyCoroutine;

        // Access the private SpringJoint backing field on a mooring rope (mirrors ControlSyncManager.MooredToSpringRef).
        // E (moored-sink residual): after a guest MoorTo, vanilla sets SpringJoint.maxDistance from a LOCALLY-derived
        // rope length; we override it with the host's authoritative length so the spring geometry matches immediately.
        private static readonly AccessTools.FieldRef<PickupableBoatMooringRope, SpringJoint> MooredToSpringRef =
            AccessTools.FieldRefAccess<PickupableBoatMooringRope, SpringJoint>("mooredToSpring");

        /// <summary>
        /// Apply full boat world state received from host.
        /// Uses direct teleportation (no Recovery system needed).
        /// </summary>
        public static void ApplyWorldState(BoatWorldStatePacket packet)
        {
            Plugin.Log.LogInfo($"Applying boat world state: {packet.Boats.Length} boats, recovery={packet.IsRecovery}");

            // BS2: a RECOVERY resync is broadcast to ALL crew, but the heavy teleport-join coroutine (50m drop +
            // re-embark onto the host's boat) would YANK a guest who was ashore / on a different boat across the
            // map onto the recovered boat. Only run the teleport for a fresh JOIN, or for a recovery of a guest
            // actually riding the recovered boat (re-seat is harmless - they're already there). An ashore guest
            // during recovery keeps their position; the recovered boat's transform + reset damage reach them via
            // the continuous BoatTransform / 1Hz DamageState syncs.
            if (packet.IsRecovery &&
                BoatUtility.GetCurrentBoat()?.gameObject.name != packet.CurrentBoatName)
            {
                // D2 (CRITICAL): the RecoveryStarted handler set BoatSyncManager.IsJoinInProgress=true expecting
                // THIS path's coroutine to clear it. Skipping the coroutine without clearing the gate latches it
                // TRUE forever -> ApplyBoatTransform early-returns every frame -> the ashore guest's boat sync is
                // dead for the session (a permanent desync softlock, worse than the yank BS2 removed). Drop it
                // here (recovery-scoped). The continuous BoatTransform sync the comment relies on is exactly what
                // this gate disables, so the self-heal is impossible without this clear.
                BoatSyncManager.IsJoinInProgress = false;
                GameState.recovering = false; // defensive: never strand the guest in instant-FOM mode
                Plugin.Log.LogInfo($"[RECOVERY] Guest not on recovered boat '{packet.CurrentBoatName}'; skipping teleport, cleared join gate (boat state self-heals via periodic sync)");
                // While the gate was up, ApplyBoatTransform was dead but OnBoatTransformReceived kept the live
                // target fresh - the boat here can be many metres stale. Snap it to the host's live transform
                // NOW so the first post-clear correction sees ~0 error instead of velocity-dragging the boat.
                // No-op if no BoatTransform has been received yet.
                if (BoatSyncManager.Instance?.SnapBoatToLiveTarget() == true)
                    Plugin.Log.LogInfo("[RECOVERY] snapped ashore-guest boat to live host transform");
                return;
            }

            // Store packet and start coroutine via a MonoBehaviour
            _pendingPacket = packet;

            // Use Plugin's MonoBehaviour to run the coroutine
            if (_applyCoroutine != null)
            {
                // D3: Unity's StopCoroutine abandons the iterator WITHOUT running its finally blocks, so an
                // in-flight join's cleanup (D1's gate clears + the inner item-state finally) never runs. Restore
                // the leak-prone globals here, or a recovery/second-join arriving mid-join latches them for the
                // session (stuck join gate, loadingBoatLocalItems suppressing item caching, applying-remote-state
                // suppressing item echo packets = silent item-sync break).
                Plugin.Instance.StopCoroutine(_applyCoroutine);
                BoatSyncManager.IsJoinInProgress = false;
                GameState.recovering = false;
                GameState.loadingBoatLocalItems = false;
                ItemSyncManager.Instance?.SetApplyingRemoteState(false);
            }
            _applyCoroutine = Plugin.Instance.StartCoroutine(ApplyWorldStateWithRecovery(packet));
        }

        /// <summary>
        /// World state application with direct teleportation for guest joining host.
        /// Replaces old Recovery-based approach that required a port.
        /// </summary>
        private static IEnumerator ApplyWorldStateWithRecovery(BoatWorldStatePacket packet)
        {
            // D1: the whole coroutine body is wrapped in try/finally so the join gates (IsJoinInProgress,
            // GameState.recovering) and the coroutine handle are cleared on EVERY exit. Without it, ANY throw
            // mid-join (a null charController on reparent, an FOM/terrain step, a bad item spawn) latched both
            // gates TRUE forever -> BoatSyncManager.ApplyBoatTransform early-returns every frame (permanent
            // boat-sync desync softlock) and FloatingOriginManager stays in instant-shift mode + the sleep/wake
            // gates that test !GameState.recovering misbehave. try/finally around `yield return` is legal (no catch).
            try
            {
            // Block physics sync during join to prevent mooring spring explosion
            BoatSyncManager.IsJoinInProgress = true;
            Debug.VerboseLogger.LobbyEvent("Join physics gate: IsJoinInProgress=true (blocking sync during teleport)");
            Plugin.Log.LogInfo("[JOIN] Starting direct teleport join");

            // === STEP 1: Calculate rough destination ===
            // Find host's current boat position from packet (real/offset-independent coords)
            Vector3 roughDestination;
            bool foundHostBoat = false;
            Vector3 hostBoatPosition = Vector3.zero;
            foreach (var b in packet.Boats)
            {
                if (b.Name == packet.CurrentBoatName)
                {
                    foundHostBoat = true;
                    hostBoatPosition = b.Position;
                    break;
                }
            }

            var guestOffset = FloatingOriginManager.instance?.outCurrentOffset ?? Vector3.zero;
            if (foundHostBoat)
            {
                // Teleport above host's boat position
                roughDestination = hostBoatPosition + guestOffset + new Vector3(0, 50f, 0);
                Plugin.Log.LogInfo($"[JOIN] Rough destination: boat '{packet.CurrentBoatName}' at {hostBoatPosition} + offset {guestOffset} + 50m up");
            }
            else
            {
                // Fallback: use host player position
                roughDestination = packet.HostPlayerPosition + guestOffset + new Vector3(0, 50f, 0);
                Plugin.Log.LogInfo($"[JOIN] Rough destination: host player pos {packet.HostPlayerPosition} + offset {guestOffset} + 50m up");
            }

            // === STEP 2: Direct teleport to trigger terrain loading ===
            GameState.recovering = true;  // Enable instant FloatingOriginManager shifts

            // FOM FIX: Reparent player to _shifting world BEFORE teleporting.
            // This prevents FOM infinite loop when player is unparented to root at far position.
            // ExitBoat() sets parent=null which causes FOM shifts to not move the player.
            var shiftingWorld = GameObject.Find("_shifting world")?.transform;
            if (shiftingWorld != null)
            {
                Refs.charController.transform.SetParent(shiftingWorld, true);
                Plugin.Log.LogInfo($"[JOIN] Reparented charController to _shifting world");
            }

            // Move charController
            Refs.charController.transform.position = roughDestination;

            // Must move ovrController too - FOM uses it as shifterObject for world shifting.
            // Use reflection to avoid compile-time dependency on Oculus.VR assembly.
            var ovrField = typeof(Refs).GetField("ovrController", BindingFlags.Public | BindingFlags.Static);
            var ovrControllerStep2 = ovrField?.GetValue(null) as Component;
            if (ovrControllerStep2 != null)
            {
                if (shiftingWorld != null)
                    ovrControllerStep2.transform.SetParent(shiftingWorld, true);
                ovrControllerStep2.transform.position = roughDestination;
            }

            // Move observerMirror too - Recovery.cs moves this FIRST before anything else
            if (Refs.observerMirror != null)
            {
                if (shiftingWorld != null)
                    Refs.observerMirror.transform.SetParent(shiftingWorld, true);
                Refs.observerMirror.transform.position = roughDestination;
                Plugin.Log.LogInfo($"[JOIN] Moved observerMirror to {roughDestination}");
            }

            // NOTE: Don't move Camera.main directly - it's parented to OVRCameraRig/TrackingSpace
            // and setting its world position messes up its local position.
            // The camera will follow observerMirror through the parent hierarchy.

            Plugin.Log.LogInfo($"[JOIN] Teleported to {roughDestination}, waiting for terrain...");

            // === STEP 2b: collapse the cross-region floating-origin storm (CTD fix) ===
            // On a CROSS-REGION join the rough destination is a huge local coordinate (the guest's offset is
            // for a different region than the host's). With recovering=true the FloatingOriginManager runs in
            // INSTANT mode, and on its next Update it re-centers via `while (pos > shiftDistance) Shift(-1,0)`
            // - i.e. one grid-step (512m) per iteration. From ~100k out that's ~270 shifts IN ONE FRAME, and
            // every Shift -> NewShift does a full-scene Object.FindObjectsOfType<ShapeGerstnerBatched>() + a
            // whole-world Translate + a Crest ocean re-origin. ~270 of those in a single frame freezes/CTDs
            // the client (the crash log dies exactly here, at "waiting for terrain"). Fix: do that re-centering
            // ONCE now, as a single big shift of the same total grid-step count, BEFORE the wait - so by the
            // time FOM.Update runs the shifter is already near origin and its while-loops are no-ops. Shift()
            // multiplies its (x,z) ints by shiftDistance, so one call with the full step count == the loop's
            // net effect. instantShifting is forced true so NewShift's translate/offset run synchronously
            // (it otherwise yields before moving anything). Near-origin (same-region) joins skip this entirely.
            try
            {
                var fom = FloatingOriginManager.instance;
                var shifter = fom != null ? fom.shifterObject : null;
                float sd = fom != null ? fom.shiftDistance : 0f;
                // Review-required guards (M1): only re-center if the shifter is actually CARRIED by NewShift's
                // `foreach (Transform item in base.transform) item.Translate(...)` - i.e. it's under fom.transform.
                // Otherwise shifting the world would move everything EXCEPT the shifter, corrupting outCurrentOffset
                // and still leaving the shifter far out (the storm fires anyway). Also require the reparent guard
                // (shiftingWorld != null) that STEP 2 used, so this stays self-consistent with the teleport.
                if (fom != null && shifter != null && sd > 0f && shiftingWorld != null && shifter.IsChildOf(fom.transform))
                {
                    Vector3 sp = shifter.position;
                    int nx = Mathf.FloorToInt(sp.x / sd);
                    int nz = Mathf.FloorToInt(sp.z / sd);
                    if (nx != 0 || nz != 0)
                    {
                        // Review-required (m1): capture the private members so a future rename SURFACES (logs an
                        // error) instead of silently no-opping and reverting to the CTD.
                        var ft = BindingFlags.NonPublic | BindingFlags.Instance;
                        var instField = typeof(FloatingOriginManager).GetField("instantShifting", ft);
                        var shiftMethod = typeof(FloatingOriginManager).GetMethod("Shift", ft);
                        if (instField == null || shiftMethod == null)
                        {
                            Plugin.Log.LogError($"[JOIN] FOM re-center BYPASSED - reflection lookup failed (instantShifting={(instField != null)}, Shift={(shiftMethod != null)}); cross-region CTD fix INACTIVE, FOM.Update will storm");
                        }
                        else
                        {
                            // instantShifting=true makes NewShift's translate + offset run SYNCHRONOUSLY (else it
                            // yields first). Shift(-nx,-nz) translates the world by (-nx,-nz)*shiftDistance, carrying
                            // the shifter into [-shiftDistance, shiftDistance] of origin in ONE shift (== the net of
                            // the ~270 single-step shifts FOM.Update would otherwise do in one frame, which CTDs).
                            instField.SetValue(fom, true);
                            shiftMethod.Invoke(fom, new object[] { -nx, -nz });
                            Plugin.Log.LogInfo($"[JOIN] FOM cross-region re-center: shifter was {sp} ({Mathf.Abs(nx) + Mathf.Abs(nz)} grid-steps) -> collapsed into 1 shift; offset now {fom.outCurrentOffset}, shifter now {shifter.position}");
                        }
                    }
                }
                else if (fom != null && shifter != null && sd > 0f &&
                         (Mathf.Abs(shifter.position.x) > sd || Mathf.Abs(shifter.position.z) > sd))
                {
                    Plugin.Log.LogWarning($"[JOIN] FOM re-center SKIPPED: shifter not carried by _shifting world (childOf={shifter.IsChildOf(fom.transform)}, world={(shiftingWorld != null)}); falling back to vanilla per-frame shifting");
                }
            }
            catch (System.Exception e) { Plugin.Log.LogWarning($"[JOIN] FOM re-center skipped: {e.Message}"); }

            // === STEP 3: Wait for terrain to load ===
            // Initial wait to trigger terrain loading. REALTIME (not WaitForSeconds): the co-op world replicates
            // the HOST's timeScale onto the guest, and a host that is paused/at a port runs at timeScale==0. A
            // scaled WaitForSeconds NEVER completes at timeScale 0, so the join HANGS here forever - the guest is
            // stranded on the +50m perch and never reaches STEP 6 (drop to ground) / STEP 7 (re-enable controls),
            // i.e. floats in the air with controls disabled. Realtime waits advance regardless of timeScale.
            yield return new WaitForSecondsRealtime(2f);

            // Then wait for all scenes to finish loading (like Recovery.cs does). Use UNSCALED time for the
            // timeout: Time.time is frozen at timeScale==0, so the 30s safety would never fire and this loop
            // would spin forever when the host is paused. Time.unscaledTime advances regardless.
            float loadingTimeout = 30f;
            float loadingStartTime = Time.unscaledTime;
            while (GameState.loadingScenes > 0 && (Time.unscaledTime - loadingStartTime) < loadingTimeout)
            {
                yield return new WaitForEndOfFrame();
            }
            if (GameState.loadingScenes > 0)
                Plugin.Log.LogWarning($"[JOIN] Terrain loading timed out, continuing anyway");

            // === STEP 4: Apply boat states ===
            // Prevent BoatLocalItems from caching/restoring items while we apply host state
            var wasLoadingBoatItems = GameState.loadingBoatLocalItems;
            GameState.loadingBoatLocalItems = true;

            Dictionary<string, SaveableObject> guestBoats;

            try
            {
                guestBoats = BoatUtility.FindAllBoats();

                // Set applying remote state BEFORE clearing orphans to prevent ItemDestroyed echo
                var itemSync = ItemSyncManager.Instance;
                if (itemSync != null)
                    itemSync.SetApplyingRemoteState(true);

                // Clear orphan items from guest's save before applying host's items
                ClearOrphanItems(packet);

                // Boat state application is split into two phases:
                // Phase A: Apply everything EXCEPT rope lengths (LoadData marks old ropes for Destroy)
                var boatDataPairs = new List<(SaveableObject boat, NetworkBoatData data)>();
                foreach (var hostBoat in packet.Boats)
                {
                    if (!guestBoats.TryGetValue(hostBoat.Name, out var boat))
                    {
                        Plugin.Log.LogWarning($"Boat {hostBoat.Name} not found in guest scene!");
                        continue;
                    }

                    // PER-BOAT ISOLATION (2026-07-02 rejoin lesson): one throwing item/boat previously
                    // aborted the whole join coroutine - the guest was left with an empty, purchasable,
                    // reefed ship and GuestJoinComplete never fired. A failed boat logs and is skipped;
                    // the rest of the world still applies and the post-join resyncs can heal the gap.
                    try
                    {
                        ApplyBoatStatePhaseA(boat, hostBoat);
                        boatDataPairs.Add((boat, hostBoat));
                    }
                    catch (System.Exception e)
                    {
                        Plugin.Log.LogError($"[JOIN] PhaseA FAILED for boat {hostBoat.Name}: {e}");
                    }
                }

                // ApplyBoatStatePhaseA resets IsApplyingRemoteState to false in its finally block.
                // Re-set it to true for SpawnWorldItems and the rest of the coroutine.
                if (itemSync != null)
                    itemSync.SetApplyingRemoteState(true);

                // Wait a frame for Destroy() to complete (removes old RopeControllers)
                Plugin.Log.LogInfo($"[JOIN] Waiting frame for sail Destroy() to complete...");
                yield return null;

                // Phase B: Apply rope lengths (now only new ropes exist). Per-boat isolation, same
                // lesson as Phase A.
                foreach (var (boat, data) in boatDataPairs)
                {
                    try { ApplyBoatStatePhaseB(boat, data); }
                    catch (System.Exception e)
                    {
                        Plugin.Log.LogError($"[JOIN] PhaseB FAILED for boat {data.Name}: {e}");
                    }
                }

                // Re-enable physics on all boats (was disabled in PhaseA for safe teleport)
                // Let physics settle. REALTIME waits, NOT WaitForFixedUpdate: at timeScale==0 (host paused)
                // FixedUpdate never runs, so WaitForFixedUpdate would HANG the join here. Realtime advances
                // regardless; at timeScale 0 the boats don't move anyway so there is nothing to settle.
                yield return new WaitForSecondsRealtime(0.05f);
                yield return new WaitForSecondsRealtime(0.05f);

                foreach (var (boat, data) in boatDataPairs)
                {
                    var rb = boat.GetComponent<Rigidbody>();
                    if (rb != null)
                    {
                        rb.isKinematic = false;
                        Plugin.Log.LogInfo($"[JOIN] Re-enabled physics on {boat.name}");
                    }
                }

                yield return new WaitForEndOfFrame();
                yield return new WaitForEndOfFrame();

                // === STEP 5: Set current boat ===
                Plugin.Log.LogInfo($"[JOIN] Looking for boat '{packet.CurrentBoatName}', IsHostOnBoat={packet.IsHostOnBoat}");

                if (!string.IsNullOrEmpty(packet.CurrentBoatName) &&
                    guestBoats.TryGetValue(packet.CurrentBoatName, out var currentBoat))
                {
                    var refs = currentBoat.GetComponent<BoatRefs>();
                    if (refs != null && refs.boatModel != null)
                    {
                        GameState.currentBoat = refs.boatModel;
                        GameState.lastBoat = currentBoat.transform;
                        SleepSyncManager.Instance?.SetSharedBoat(packet.CurrentBoatName);
                        Plugin.Log.LogInfo($"[JOIN] Set current boat to {packet.CurrentBoatName}");
                    }
                }

                // Sync wind and weather
                Wind.currentWind = packet.WindState;
                WeatherSyncManager.Instance?.OnWeatherStateReceived(packet.WeatherState);

                // === STEP 6: Teleport to exact position ===
                // AT-SEA JOIN FIX: boat sync has been gated off (IsJoinInProgress) for this whole multi-second
                // join, so GameState.currentBoat is still sitting at the STALE join snapshot. If the host has
                // been SAILING, the real boat is now tens-to-hundreds of metres away. Snap our boat to the
                // host's LIVE transform+velocity NOW (OnBoatTransformReceived kept the target fresh during the
                // join) so the guest is placed onto the boat where it ACTUALLY is, and the first post-join
                // correction is a small smooth nudge instead of a >50m teleport that strands the guest.
                // Harmless at a port: the host boat is stationary, so the live target == the join snapshot and
                // the snap moves nothing. Gated on IsHostOnBoat (on land there is no boat to ride).
                if (packet.IsHostOnBoat)
                    BoatSyncManager.Instance?.SnapBoatToLiveTarget();

                Plugin.Log.LogInfo($"[JOIN] Teleporting to exact position: {packet.HostPlayerPosition}, onBoat={packet.IsHostOnBoat}");
                TeleportPlayer(packet.HostPlayerPosition, packet.HostPlayerRotation, packet.IsHostOnBoat);

                // === STEP 7: Finish up ===
                GameState.recovering = false;

                // Re-enable player controls (Recovery.cs does this at end of recovery)
                Refs.SetPlayerControl(true);
                MouseLook.ToggleMouseLook(true);
                Plugin.Log.LogInfo("[JOIN] Re-enabled player controls");

                // POCKET-INHERIT FIX: deterministic clean slate for a fresh JOIN of the local guest.
                // Full survival bars + an emptied personal inventory so the joiner never starts a session
                // carrying over leftover/host items (the host's pocket items are now excluded by the
                // collector, but a guest who rejoins could still have stale local pocket items; this makes
                // the start state deterministic). LOCAL only - no packet. Restricted to a genuine fresh
                // join (NOT a recovery reseat, which keeps the guest's existing needs/inventory).
                if (!packet.IsRecovery)
                {
                    ResetLocalSurvivalAndInventory();
                }

                // Wait briefly then spawn world items. Realtime so a paused host (timeScale 0) can't hang it.
                yield return new WaitForSecondsRealtime(0.5f);
                try
                {
                    SpawnWorldItems(packet.WorldItems);
                    Plugin.Log.LogInfo($"[JOIN] Spawned {packet.WorldItems?.Length ?? 0} world items");
                }
                catch (System.Exception e)
                {
                    // A corrupt snapshot item must not abort the whole join: swallowing the throw lets the
                    // coroutine reach the GuestJoinComplete send below, and the host's mission-cargo resync
                    // re-sends what the failed apply lost.
                    Plugin.Log.LogError($"[JOIN] World item spawn threw mid-apply: {e}");
                }
            }
            finally
            {
                GameState.loadingBoatLocalItems = wasLoadingBoatItems;
                var itemSyncFinal = ItemSyncManager.Instance;
                if (itemSyncFinal != null)
                    itemSyncFinal.SetApplyingRemoteState(false);
            }

            // Request fresh economy state from host
            Plugin.Log.LogInfo($"[JOIN] Requesting economy re-sync from host");
            Plugin.NetworkManager.SendToAllReliable(Networking.Packets.PacketType.EconomySyncRequest, w => { });

            // Re-enable physics sync now that join is complete
            Debug.VerboseLogger.RecoveryApply("Recovery resync complete; guest re-boarded on recovered boat, join phase ended");
            BoatSyncManager.IsJoinInProgress = false;
            Plugin.Log.LogInfo($"[JOIN] Join complete!");

            // Tell the host the join coroutine is fully finished (every snapshot spawn applied or safely
            // skipped). The host replies with a targeted mission-cargo resync, so a join whose item spawns
            // were partially lost on apply cannot leave this joiner blind to mission crates the rest of the
            // crew sees. Sent ONLY here, after every snapshot spawn above: any earlier trigger races the
            // snapshot and would duplicate or drop crates. Not covered: a BoatWorldState packet lost
            // outright (this coroutine never runs) or a throw above that aborts the coroutine before this
            // line; those paths never send the signal.
            Plugin.NetworkManager.SendReliable(Plugin.LobbyManager.HostSteamId, PacketType.GuestJoinComplete, w =>
                PacketSerializer.WriteGuestJoinComplete(w, new GuestJoinCompletePacket()));
            }
            finally
            {
                // D1: clear the join gates + coroutine handle on EVERY exit (success OR an exception thrown
                // anywhere above). Idempotent with the happy-path clears. NOTE: a StopCoroutine abort does NOT
                // run this finally (Unity semantics) - that path is handled at the StopCoroutine call site (D3).
                GameState.recovering = false;
                BoatSyncManager.IsJoinInProgress = false;
                _applyCoroutine = null;
                // Defense-in-depth: if the coroutine threw before STEP 7, the player is left with controls
                // DISABLED -> no gravity, frozen wherever the teleport parked them (the floating-guest bug).
                // Hand control back on every exit so a mid-join failure can never strand the guest. Idempotent
                // with the STEP 7 enable (SetPlayerControl just sets a flag).
                try { Refs.SetPlayerControl(true); MouseLook.ToggleMouseLook(true); }
                catch (System.Exception e) { Plugin.Log.LogWarning($"[JOIN] control restore in finally failed: {e.Message}"); }
            }
        }

        /// <summary>
        /// Apply state to a single boat - Phase A: Everything EXCEPT rope lengths.
        /// Split into two phases because LoadData() uses Destroy(), which is deferred; we need to
        /// wait a frame between ApplyCustomization and ApplyRopeLengths.
        /// </summary>
        public static void ApplyBoatStatePhaseA(SaveableObject boat, NetworkBoatData data)
        {
            Plugin.Log.LogDebug($"Applying state to boat {data.Name} (Phase A)");

            // CRITICAL: Prevent Harmony patches from sending packets while we apply world state
            var controlSync = ControlSyncManager.Instance;
            var itemSync = ItemSyncManager.Instance;
            if (controlSync != null)
                controlSync.SetApplyingRemoteState(true);
            if (itemSync != null)
                itemSync.SetApplyingRemoteState(true);

            try
            {
                // 0. Unmoor dock ropes first (must happen before moving boat)
                var mooringRopes = boat.GetComponent<BoatMooringRopes>();
                if (mooringRopes != null)
                {
                    mooringRopes.UnmoorAllRopes();
                    Plugin.Log.LogDebug($"Unmoored dock ropes from {data.Name}");
                }

                var rb = boat.GetComponent<Rigidbody>();

                // 1. Make kinematic for safe modification
                if (rb != null) rb.isKinematic = true;

                // 2. Apply transform
                // Convert from real (offset-independent) to local (shifted) coordinates
                var offset = FloatingOriginManager.instance?.outCurrentOffset ?? Vector3.zero;
                var localPosition = data.Position + offset;
                // Add small Y offset so boat drops naturally onto water (avoids physics glitches)
                localPosition.y += 1f;
                boat.transform.position = localPosition;
                boat.transform.rotation = data.Rotation;

                // Clear velocities to prevent physics chaos
                if (rb != null)
                {
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }

                // Reset anchor (like Recovery.cs does)
                var anchorController = mooringRopes?.GetAnchorController();
                if (anchorController != null)
                {
                    anchorController.ResetAnchor();
                }

                // 3. Apply customization (this destroys old sails/ropes and creates new ones)
                // NOTE: Destroy() is deferred until end of frame, so old ropes still exist here
                ApplyCustomization(boat, data);

                // 4. Clear existing items and spawn host's items
                ClearBoatItems(boat);
                SpawnItems(boat, data.Items);

                // 5. Apply damage state
                var damage = boat.GetComponent<BoatDamage>();
                if (damage != null)
                {
                    damage.waterLevel = data.WaterLevel;
                    damage.hullDamage = data.HullDamage;
                    damage.oakum = data.Oakum;
                    damage.sunk = false;  // Ensure not marked as sunk
                    damage.enabled = true;  // Recovery.cs does this
                }

                // 6. Apply anchor state
                ApplyAnchorState(boat, data.IsAnchored, data.AnchorRopeLength);

                // 7. Apply mooring rope states (re-moor after boat is in position)
                if (mooringRopes != null && data.MooringRopes != null)
                {
                    ApplyMooringRopes(mooringRopes, data.MooringRopes);
                }

                // 8. Apply ownership state (sync extraSetting AND update the "for sale" UI). Shared with
                // the runtime BoatOwnershipChanged path so a live purchase and a join-snapshot apply
                // touch identical fields.
                ApplyOwnership(boat, data.IsOwned);

                // 9. Apply dirt texture. Lookup MUST go through SaveableObject.GetCleanable(): the
                // CleanableObject lives on the hull-mesh child (RequireComponent HullPlayerCollider +
                // Renderer), never on the boat root, so GetComponent on the root silently returns null
                // and the guest keeps a spotless hull while the host sees dirt (2026-07-02 report).
                // Mirrors the collector (BoatStateCollector) and vanilla SaveableObject.Load.
                if (data.DirtTexture != null && data.DirtTexture.Length > 0)
                {
                    var cleanable = boat.GetCleanable();
                    if (cleanable == null)
                        Plugin.Log.LogWarning($"[CLEANING:APPLY] No CleanableObject on {boat.gameObject.name}; dirt texture dropped");
                    else
                    {
                        try
                        {
                            var texture = new Texture2D(128, 128, TextureFormat.ARGB32, false);
                            texture.LoadImage(data.DirtTexture);
                            cleanable.LoadTexture(texture);
                            Plugin.Log.LogInfo($"[CLEANING:APPLY] Applied dirt texture to {boat.gameObject.name}");
                        }
                        catch (System.Exception ex)
                        {
                            Plugin.Log.LogWarning($"[CLEANING:APPLY] Failed to apply dirt texture to {boat.gameObject.name}: {ex.Message}");
                        }
                    }
                }

                Plugin.Log.LogDebug($"Boat {data.Name} Phase A complete, owned={data.IsOwned}");
            }
            finally
            {
                // Reset flags - will be set again in Phase B if needed
                if (controlSync != null)
                    controlSync.SetApplyingRemoteState(false);
                if (itemSync != null)
                    itemSync.SetApplyingRemoteState(false);
            }
        }

        /// <summary>
        /// Apply state to a single boat - Phase B: Rope lengths only.
        /// Called after waiting a frame so Destroy() has completed.
        /// </summary>
        public static void ApplyBoatStatePhaseB(SaveableObject boat, NetworkBoatData data)
        {
            Plugin.Log.LogDebug($"Applying state to boat {data.Name} (Phase B - rope lengths)");

            // Invalidate rope cache - old RopeControllers are now actually destroyed
            BoatUtility.InvalidateRopeCache(boat);

            // Apply rope lengths (now only new ropes exist)
            ApplyRopeLengths(boat, data.RopeLengths);
        }

        /// <summary>
        /// Apply boat ownership (extraSetting) and refresh the "for sale" UI exactly as the
        /// join-snapshot path does. Shared by the snapshot apply (step 8) and the runtime
        /// BoatOwnershipChanged handler so a live host purchase and a fresh-join snapshot converge on the
        /// same fields. LoadAsPurchased() sets extraSetting=true AND hides the purchase UI; it is the
        /// vanilla method PurchasableBoat itself uses on save-load.
        /// </summary>
        public static void ApplyOwnership(SaveableObject boat, bool isOwned)
        {
            if (boat == null) return;

            boat.extraSetting = isOwned;
            var purchasable = boat.GetComponent<PurchasableBoat>();
            if (purchasable != null && isOwned)
            {
                purchasable.LoadAsPurchased();
            }
        }

        /// <summary>
        /// Apply customization data to a boat (masts, sails, part options).
        /// </summary>
        public static void ApplyCustomization(SaveableObject boat, NetworkBoatData data)
        {
            var customization = boat.GetComponent<SaveableBoatCustomization>();
            if (customization == null)
            {
                Plugin.Log.LogWarning($"No SaveableBoatCustomization on {boat.gameObject.name}");
                return;
            }

            // Build SaveBoatCustomizationData from network data
            var saveData = new SaveBoatCustomizationData
            {
                masts = data.MastsEnabled ?? new bool[30],
                sails = data.Sails?.Select(s => new SaveSailData
                {
                    prefabIndex = s.PrefabIndex,
                    mastIndex = s.MastIndex,
                    installHeight = s.InstallHeight,
                    minAngle = s.MinAngle,
                    maxAngle = s.MaxAngle,
                    health = s.Health,
                    sailColor = s.Color,
                    scaleY = s.ScaleY,  // BS1: restore custom sail scale (Mast.LoadSail calls LoadScale when scaleY!=0)
                    scaleZ = s.ScaleZ
                }).ToList() ?? new List<SaveSailData>(),
                partActiveOptions = data.PartActiveOptions?.ToList() ?? new List<int>()
            };

            customization.LoadData(saveData);
        }

        /// <summary>
        /// Remove all ship items from a boat before spawning host's items.
        /// </summary>
        public static void ClearBoatItems(SaveableObject boat)
        {
            var prefabs = boat.GetComponentsInChildren<SaveablePrefab>();
            int count = 0;

            foreach (var prefab in prefabs)
            {
                // Only destroy ship items, not boat parts
                if (prefab.GetComponent<ShipItem>() != null)
                {
                    Object.Destroy(prefab.gameObject);
                    count++;
                }
            }

            // DUPLICATE-DEFAULT-ITEMS FIX (issue #3): the joiner's OWN phantom-save copies of the default
            // boat items (map, compass, lantern, scroll, mug, table...) are NOT instantiated at clear time -
            // vanilla BoatLocalItems holds them as serialized SavePrefabData in `cachedItems` and streams them
            // in lazily on player proximity, AFTER this clear + the host's SpawnItems have run. They then
            // spawn on top of the host's authoritative copies = duplicates the host can't authorize (grab for
            // ~1s then force-dropped, because they carry the joiner's own instanceIds). The dead
            // GameState.loadingBoatLocalItems guard never prevented this. Flush the boat's cache so nothing
            // re-streams: null cachedItems + mark itemsLoaded so BoatLocalItems.Update's spawn branch is
            // inert. Autocache stays on, so the HOST's items still stream/re-cache normally under host ids.
            var localItems = boat.GetComponent<BoatLocalItems>();
            if (localItems != null)
            {
                try
                {
                    Traverse.Create(localItems).Field("cachedItems").SetValue(null);
                    localItems.SetItemsLoaded(true);
                }
                catch (System.Exception e)
                {
                    Plugin.Log.LogWarning($"[JOIN] Could not flush BoatLocalItems cache on {boat.gameObject.name}: {e.Message}");
                }
            }

            Plugin.Log.LogDebug($"Cleared {count} items from {boat.gameObject.name}");
        }

        /// <summary>
        /// Clear ALL sold world items (not parented to boats) from guest's scene.
        /// These will be replaced by host's items via SpawnWorldItems.
        /// Boat items are handled separately by ClearBoatItems.
        /// </summary>
        public static void ClearOrphanItems(BoatWorldStatePacket packet)
        {
            // Get all boats to check parenting
            var boats = BoatUtility.FindAllBoats();
            var boatTransforms = new HashSet<Transform>(boats.Values.Select(b => b.transform));

            // Find all ShipItems in scene
            var allItems = Object.FindObjectsOfType<ShipItem>();
            int clearedCount = 0;
            var clearedNames = new List<string>();

            foreach (var item in allItems)
            {
                // Skip vendor items (sold=false) - they have different IDs per client
                if (!item.sold) continue;

                // Skip items parented to boats - handled by ClearBoatItems
                if (IsParentedToBoat(item.transform, boatTransforms)) continue;

                var prefab = item.GetComponent<SaveablePrefab>();
                if (prefab == null) continue;

                int instanceId = prefab.instanceId;
                // Do NOT destroy an item the host just sent us. A live host/guest spawn
                // (market BUY, mission good) broadcast AFTER the host's join snapshot but received DURING this
                // guest's pre-guard terrain wait was created + marked recently-synced by OnRemoteItemSpawned; it
                // is absent from packet.WorldItems (the snapshot predates it) and the host won't resend it
                // (_syncedItemIds), so destroying it here would lose it permanently on this guest. (Mirrors the
                // IsRecentlySynced guard already used by ClearBoatItems.)
                if (ItemSyncManager.Instance?.IsRecentlySynced(instanceId) == true) continue;
                clearedNames.Add($"{item.name}(id={instanceId})");
                Object.Destroy(item.gameObject);
                clearedCount++;
            }

            // Log results
            int worldItemCount = packet.WorldItems?.Length ?? 0;
            Plugin.Log.LogInfo($"[ITEM-VERIFY] Cleared {clearedCount} guest world items, host sending {worldItemCount}");
            if (clearedCount > 0)
            {
                Plugin.Log.LogInfo($"[ITEM-VERIFY] Cleared items: {string.Join(", ", clearedNames)}");
            }
        }

        /// <summary>
        /// Check if a transform is parented to any boat.
        /// </summary>
        private static bool IsParentedToBoat(Transform transform, HashSet<Transform> boatTransforms)
        {
            var current = transform.parent;
            while (current != null)
            {
                if (boatTransforms.Contains(current)) return true;
                current = current.parent;
            }
            return false;
        }

        /// <summary>
        /// Spawn all items from the host's boat.
        /// Two-pass approach: spawn items first, then insert into crates.
        /// </summary>
        public static void SpawnItems(SaveableObject boat, NetworkSaveData[] items)
        {
            if (items == null || items.Length == 0) return;

            // DEBUG: Log all items being synced to identify crate mismatch issues
            Plugin.Log.LogInfo($"[SYNC-DEBUG] SpawnItems: {items.Length} items to spawn");
            var crateIds = new HashSet<int>();
            foreach (var item in items)
            {
                if (item.CrateId > 0) crateIds.Add(item.CrateId);
                Plugin.Log.LogDebug($"[SYNC-DEBUG] Item id={item.InstanceId}, prefab={item.PrefabIndex}, crateId={item.CrateId}");
            }
            Plugin.Log.LogInfo($"[SYNC-DEBUG] Items reference {crateIds.Count} unique crate IDs: {string.Join(", ", crateIds)}");

            // Sort items: crates first (CrateId == 0), then items in crates (CrateId > 0)
            // This ensures crates exist before we try to insert items into them
            var sortedItems = items.OrderBy(i => i.CrateId > 0 ? 1 : 0).ToArray();
            var spawnedInstances = new Dictionary<int, GameObject>();
            var itemIds = new List<int>();

            // DUPLICATE-MANUAL FIX: set of all host snapshot item ids for this boat. The dedup in
            // SpawnItem destroys a near, same-prefab LOCAL only when its id is NOT in this set, so a
            // genuinely-distinct local that carries a host id (e.g. two identical stacked items) is never
            // destroyed - only a true guest-generated orphan (whose id is absent from the snapshot) is.
            var hostItemIds = new HashSet<int>();
            foreach (var item in items)
            {
                if (item.InstanceId > 0) hostItemIds.Add(item.InstanceId);
            }

            // First pass: spawn all items. PER-ITEM ISOLATION (2026-07-02 rejoin lesson): a single
            // throwing spawn (the kettle UpdateMaterial NRE) previously aborted the whole join at item
            // 27/226 - log and continue, one corrupt item must never take the boat state down with it.
            foreach (var item in sortedItems)
            {
                GameObject instance = null;
                try { instance = SpawnItem(boat, item, hostItemIds); }
                catch (System.Exception e)
                {
                    Plugin.Log.LogError($"[SYNC-DEBUG] SpawnItem FAILED for id={item.InstanceId}, prefab={item.PrefabIndex}: {e}");
                }
                if (instance != null && item.InstanceId > 0)
                {
                    // Check for duplicate IDs
                    if (spawnedInstances.ContainsKey(item.InstanceId))
                    {
                        Plugin.Log.LogWarning($"[SYNC-DEBUG] DUPLICATE ID! {item.InstanceId} already spawned, new prefab={item.PrefabIndex}");
                    }
                    spawnedInstances[item.InstanceId] = instance;
                    itemIds.Add(item.InstanceId);
                }
            }

            // DEBUG: Check if referenced crates were actually spawned
            foreach (var crateId in crateIds)
            {
                if (!spawnedInstances.TryGetValue(crateId, out var crateObj))
                {
                    Plugin.Log.LogWarning($"[SYNC-DEBUG] Crate {crateId} NOT in spawnedInstances!");
                }
                else
                {
                    var prefabName = crateObj.name;
                    var hasCrateInv = crateObj.GetComponent<CrateInventory>() != null;
                    Plugin.Log.LogInfo($"[SYNC-DEBUG] Crate {crateId} -> {prefabName}, hasCrateInventory={hasCrateInv}");
                }
            }

            // No manual InsertItem loop here - the game's ShipItem.LoadAfterDelay() handles crate
            // insertion automatically after 2 frames when currentCrateId is set, which gives time
            // for ShipItemCrate.OnLoad() to add the CrateInventory component.

            // Register boat items with ItemSyncManager on guest side
            ItemSyncManager.Instance?.RegisterBoatItems(itemIds);

            Plugin.Log.LogDebug($"Spawned {items.Length} items on {boat.gameObject.name}");
        }

        /// <summary>
        /// Spawn all world items (not parented to any boat).
        /// Similar to SpawnItems but for world-space items.
        /// </summary>
        public static void SpawnWorldItems(NetworkSaveData[] items)
        {
            if (items == null || items.Length == 0)
            {
                Plugin.Log.LogInfo("[WORLD-ITEMS] No world items to spawn");
                return;
            }

            Plugin.Log.LogInfo($"[WORLD-ITEMS] Spawning {items.Length} world items");

            // DEBUG: Log all items being synced to identify crate mismatch issues
            var crateIds = new HashSet<int>();
            foreach (var item in items)
            {
                if (item.CrateId > 0) crateIds.Add(item.CrateId);
                Plugin.Log.LogDebug($"[WORLD-SYNC-DEBUG] Item id={item.InstanceId}, prefab={item.PrefabIndex}, crateId={item.CrateId}");
            }
            if (crateIds.Count > 0)
            {
                Plugin.Log.LogInfo($"[WORLD-SYNC-DEBUG] Items reference {crateIds.Count} unique crate IDs: {string.Join(", ", crateIds)}");
            }

            // Sort items: crates first (CrateId == 0), then items in crates (CrateId > 0)
            var sortedItems = items.OrderBy(i => i.CrateId > 0 ? 1 : 0).ToArray();
            var spawnedInstances = new Dictionary<int, GameObject>();
            var itemIds = new List<int>();

            // POCKET/WORLD DEDUP (issue #1 x disconnect-drop): a RETURNING guest keeps their persisted pocket
            // items (issue #1), but the host also drops a guest's pocketed items into the WORLD when that guest
            // disconnects - so the authoritative snapshot can re-spawn an item the guest STILL has pocketed,
            // leaving two objects sharing one instanceId (ambiguous FindItemByInstanceId, a physical dupe). The
            // host snapshot is authoritative, so before spawning, destroy any LOCAL pocket copy of an id that
            // the snapshot places in the world. Only matters for a reused phantom (a fresh phantom already had
            // its pockets cleared by ResetLocalSurvivalAndInventory).
            if (!CoopSave.PhantomWasFreshlyCreated)
            {
                // (v0.2.25) Track the snapshot item's PREFAB per id, not just the id: instanceIds are
                // random per-session, so a pocket item can collide with an UNRELATED world item's id
                // from the host's session. Only destroy the pocket copy when the prefab matches too -
                // on mismatch it's an id collision, keep the pocket item (vanishing odds, free guard).
                var worldIds = new Dictionary<int, int>(); // instanceId -> PrefabIndex
                foreach (var it in items) if (it.InstanceId > 0) worldIds[it.InstanceId] = it.PrefabIndex;

                var slots = GPButtonInventorySlot.inventorySlots;
                if (slots != null && worldIds.Count > 0)
                {
                    var itemSync = ItemSyncManager.Instance;
                    bool restoreApplying = false;
                    if (itemSync != null && !itemSync.IsApplyingRemoteState) { itemSync.SetApplyingRemoteState(true); restoreApplying = true; }
                    try
                    {
                        int deduped = 0;
                        foreach (var slot in slots)
                        {
                            if (slot == null || slot.currentItem == null) continue;
                            var pfab = slot.currentItem.GetComponent<SaveablePrefab>();
                            if (pfab == null || !worldIds.TryGetValue(pfab.instanceId, out var snapPrefab)) continue;
                            if (snapPrefab != pfab.prefabIndex)
                            {
                                // (v0.2.25) Same id, DIFFERENT prefab: cross-session id collision, not a dupe.
                                Plugin.Log.LogInfo($"[JOIN] Pocket/world dedup: id {pfab.instanceId} matches a snapshot world item but prefab differs (pocket={pfab.prefabIndex}, world={snapPrefab}) - id collision, keeping the pocket item");
                                continue;
                            }
                            var dupItem = slot.currentItem;
                            slot.currentItem = null;   // free the slot; the authoritative world copy will spawn below
                            dupItem.DestroyItem();     // vanilla path: Unregister + Destroy (no broadcast under the guard)
                            deduped++;
                        }
                        if (deduped > 0)
                            Plugin.Log.LogInfo($"[JOIN] Pocket/world dedup: dropped {deduped} pocketed item(s) that the host snapshot places in the world (issue #1 anti-dupe)");
                    }
                    finally { if (restoreApplying && itemSync != null) itemSync.SetApplyingRemoteState(false); }
                }
            }

            // First pass: spawn all items (per-item isolation - see SpawnItems; one bad item must not
            // abort the join).
            foreach (var item in sortedItems)
            {
                GameObject instance = null;
                try { instance = SpawnWorldItem(item); }
                catch (System.Exception e)
                {
                    Plugin.Log.LogError($"[WORLD-SYNC-DEBUG] SpawnWorldItem FAILED for id={item.InstanceId}, prefab={item.PrefabIndex}: {e}");
                }
                if (instance != null && item.InstanceId > 0)
                {
                    // Check for duplicate IDs
                    if (spawnedInstances.ContainsKey(item.InstanceId))
                    {
                        Plugin.Log.LogWarning($"[WORLD-SYNC-DEBUG] DUPLICATE ID! {item.InstanceId} already spawned, new prefab={item.PrefabIndex}");
                    }
                    spawnedInstances[item.InstanceId] = instance;
                    itemIds.Add(item.InstanceId);
                }
            }

            // DEBUG: Check if referenced crates were actually spawned correctly
            foreach (var crateId in crateIds)
            {
                if (!spawnedInstances.TryGetValue(crateId, out var crateObj))
                {
                    Plugin.Log.LogWarning($"[WORLD-SYNC-DEBUG] Crate {crateId} NOT in spawnedInstances!");
                }
                else
                {
                    var prefabName = crateObj.name;
                    var hasCrateInv = crateObj.GetComponent<CrateInventory>() != null;
                    Plugin.Log.LogInfo($"[WORLD-SYNC-DEBUG] Crate {crateId} -> {prefabName}, hasCrateInventory={hasCrateInv}");
                }
            }

            // No manual InsertItem loop here - the game's ShipItem.LoadAfterDelay() handles crate
            // insertion automatically after 2 frames when currentCrateId is set, which gives time
            // for ShipItemCrate.OnLoad() to add the CrateInventory component.

            // Register world items with ItemSyncManager
            ItemSyncManager.Instance?.RegisterBoatItems(itemIds);

            Plugin.Log.LogInfo($"[WORLD-ITEMS] Spawned {items.Length} world items");
        }

        /// <summary>
        /// Spawn a single world item at world coordinates.
        /// </summary>
        private static GameObject SpawnWorldItem(NetworkSaveData item)
        {
            if (item.PrefabIndex <= 0 || item.PrefabIndex >= PrefabsDirectory.instance.directory.Length)
            {
                Plugin.Log.LogWarning($"SpawnWorldItem: Invalid prefab index: {item.PrefabIndex}");
                return null;
            }

            var prefab = PrefabsDirectory.instance.directory[item.PrefabIndex];
            if (prefab == null)
            {
                Plugin.Log.LogWarning($"SpawnWorldItem: Prefab at index {item.PrefabIndex} is null");
                return null;
            }

            // Convert offset-independent position to local coordinates
            var offset = FloatingOriginManager.instance?.outCurrentOffset ?? Vector3.zero;
            var worldPos = item.Position + offset;

            Plugin.Log.LogInfo($"[WORLD-SPAWN] id={item.InstanceId}, prefabIdx={item.PrefabIndex}, prefabName={prefab.name}, pos={worldPos}");

            // Create SavePrefabData with all fields
            // Pass actual crateId - game's auto-insert in ShipItem.LoadAfterDelay() handles
            // crate insertion after 2 frames. This gives time for crate's OnLoad() to add CrateInventory.
            var saveData = new SavePrefabData(
                worldPos,                        // pos (world coordinates)
                item.Rotation,                   // rot
                item.PrefabIndex,                // prefabIndex
                item.IsSold,                     // isSold
                false,                           // isNailed
                item.Health,                     // health
                item.Amount,                     // amount
                item.InventorySlot,              // slot
                item.CrateId,                    // crate - game's auto-insert handles after 2 frames
                item.MissionIndex,               // missionIndex
                item.ParentObject,               // parentObject
                item.DaysInStorage,              // daysInStorage
                item.InstanceId                  // instanceId
            );

            // Set extra values (not in constructor)
            saveData.extraValue0 = item.ExtraValue0;
            saveData.extraValue1 = item.ExtraValue1;
            saveData.extraValue2 = item.ExtraValue2;
            saveData.extraValue3 = item.ExtraValue3;
            saveData.extraValue4 = item.ExtraValue4;

            // Set chart data if present
            if (item.HasChartData)
            {
                saveData.chartData = ConvertToGameChartData(item.ChartData);
            }

            // Instantiate at correct position, not Vector3.zero:
            // Items at (0,0,0) are underwater and ItemRigidbody.FixedUpdate destroys them before Load() runs
            var instance = Object.Instantiate(prefab, worldPos, item.Rotation);
            var saveable = instance.GetComponent<SaveablePrefab>();

            if (saveable != null)
            {
                saveable.Load(saveData);
            }

            // Parent to _shifting world like gameplay spawn does for land items
            var shiftingWorld = GameObject.Find("_shifting world");
            instance.transform.SetParent(shiftingWorld?.transform);
            instance.transform.position = worldPos;
            instance.transform.rotation = item.Rotation;

            // Also set ItemRigidbody position and parent to prevent physics glitches
            var shipItem = instance.GetComponent<ShipItem>();
            if (shipItem?.itemRigidbodyC != null)
            {
                shipItem.itemRigidbodyC.transform.SetParent(shiftingWorld?.transform);
                shipItem.itemRigidbodyC.transform.position = worldPos;
                shipItem.itemRigidbodyC.transform.rotation = item.Rotation;

                // Make kinematic temporarily to prevent falling through unloaded terrain
                var rb = shipItem.itemRigidbodyC.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    rb.isKinematic = true;
                    // Will be re-enabled by ItemRigidbody when player interacts
                }
            }

            // Add to registry
            ItemSyncManager.Instance?.RegisterItem(item.InstanceId, item.PrefabIndex);

            // Mark as recently synced so destruction won't broadcast to host
            ItemSyncManager.Instance?.MarkAsRecentlySynced(item.InstanceId);

            return instance;
        }

        /// <summary>
        /// Spawn a single item on a boat using the game's prefab system.
        /// Returns the spawned GameObject for crate insertion tracking.
        /// </summary>
        public static GameObject SpawnItem(SaveableObject boat, NetworkSaveData item, HashSet<int> hostItemIds = null)
        {
            if (item.PrefabIndex <= 0 || item.PrefabIndex >= PrefabsDirectory.instance.directory.Length)
            {
                Plugin.Log.LogWarning($"Invalid prefab index: {item.PrefabIndex}");
                return null;
            }

            var prefab = PrefabsDirectory.instance.directory[item.PrefabIndex];
            if (prefab == null)
            {
                Plugin.Log.LogWarning($"Prefab at index {item.PrefabIndex} is null");
                return null;
            }

            // DEBUG: Log what prefab we're about to spawn
            Plugin.Log.LogDebug($"[SYNC-DEBUG] SpawnItem id={item.InstanceId}, prefabIdx={item.PrefabIndex}, prefabName={prefab.name}");

            // Create SavePrefabData with all fields from network data
            // Pass actual crateId - game's auto-insert in ShipItem.LoadAfterDelay() handles
            // crate insertion after 2 frames. This gives time for crate's OnLoad() to add CrateInventory.
            var saveData = new SavePrefabData(
                item.Position,                   // pos (boat-relative)
                item.Rotation,                   // rot
                item.PrefabIndex,                // prefabIndex
                item.IsSold,                     // isSold
                false,                           // isNailed
                item.Health,                     // health
                item.Amount,                     // amount
                item.InventorySlot,              // slot
                item.CrateId,                    // crate - game's auto-insert handles after 2 frames
                item.MissionIndex,               // missionIndex
                boat.sceneIndex,                 // parentObject
                item.DaysInStorage,              // daysInStorage
                item.InstanceId                  // instanceId
            );

            // Set extra values (not in constructor)
            saveData.extraValue0 = item.ExtraValue0;
            saveData.extraValue1 = item.ExtraValue1;
            saveData.extraValue2 = item.ExtraValue2;
            saveData.extraValue3 = item.ExtraValue3;
            saveData.extraValue4 = item.ExtraValue4;

            // Set chart data if present
            if (item.HasChartData)
            {
                saveData.chartData = ConvertToGameChartData(item.ChartData);
            }

            // Instantiate at correct position, not Vector3.zero:
            // Items at (0,0,0) are underwater and ItemRigidbody.FixedUpdate destroys them before Load() runs
            var worldPos = boat.transform.TransformPoint(item.Position);

            // DUPLICATE-MANUAL FIX: dedup by PREFAB + WORLD POSITION before spawning.
            // The guest's own scene/BoatLocalItems can instantiate default boat items (e.g. the Sailing
            // Manual) with a guest-generated instanceId. ClearBoatItems clears by ShipItem presence, but
            // items spawned/re-cached AFTER the clear (or that survive it) are NOT recognized as "the same"
            // as the host's snapshot item because dedup elsewhere keys on instanceId, not prefab+position.
            // That left the guest with two manuals (their orphan + host's copy) while the host had one.
            // We compare in WORLD space (the host position is SaveableObject-relative, but the local item is
            // parented to boatModel, so boat-local spaces differ) with a TIGHT threshold and a matching
            // prefab index, so distinct same-type items placed close together (e.g. stacked cargo) survive.
            var orphan = ItemSyncManager.FindItemByPrefabNearPosition(
                item.PrefabIndex, worldPos, boatName: "", isLocalPosition: false, maxDistance: 0.35f,
                logMissAsError: false); // a miss here is the EXPECTED no-orphan case -> [ITEM:DEDUP], not a scary [ITEM:CORRELATE] error
            if (orphan != null)
            {
                var orphanPrefab = orphan.GetComponent<SaveablePrefab>();
                int orphanId = orphanPrefab != null ? orphanPrefab.instanceId : 0;

                // Only destroy a TRUE guest orphan. Spare the matched local if:
                //  (a) its id IS one of the host snapshot ids for this boat - then it's a legit host item
                //      (e.g. two identical stacked items, only one of which is the orphan), OR
                //  (b) it was spawned earlier in THIS same pass (recently-synced) - same-prefab host items
                //      placed within the tight threshold would otherwise have the 2nd spawn eat the 1st, OR
                //  (c) we couldn't read its SaveablePrefab/id at all.
                // The orphan default manual has a guest-generated id absent from the snapshot, so it is still
                // removed; any same-prefab item present in the host snapshot can never be destroyed here.
                // hostItemIds is only supplied on the JOIN world-resend (the only place these duplicates
                // appear); the single-item resync caller passes null, so dedup is skipped there entirely.
                bool isHostItem = orphanPrefab != null && hostItemIds != null && hostItemIds.Contains(orphanId);
                bool isOurFreshSpawn = orphanPrefab != null &&
                    (ItemSyncManager.Instance?.IsRecentlySynced(orphanId) ?? false);
                if (orphanPrefab != null && hostItemIds != null && !isHostItem && !isOurFreshSpawn)
                {
                    Plugin.Log.LogInfo($"[ITEM-DEDUP] Destroying local orphan {orphan.name} (id={orphanId}, prefab={item.PrefabIndex}) near {worldPos} before spawning host item id={item.InstanceId}");
                    Object.Destroy(orphan.gameObject);
                }
            }

            var instance = Object.Instantiate(prefab, worldPos, item.Rotation);
            var saveable = instance.GetComponent<SaveablePrefab>();

            if (saveable != null)
            {
                saveable.Load(saveData);
                // Load() sets amount (cooked >= 1) but the material was baked in Awake pre-Load; refresh so
                // a cooked-state item doesn't render raw (crate-unseal precedent). MUST be exception-
                // guarded: CookableFoodKettle's private Awake HIDES CookableFood.Awake, leaving
                // renderer/materials/foodState null, so UpdateMaterial NREs on every KETTLE - unguarded,
                // that single item aborted the ENTIRE join coroutine (2026-07-02 empty-ship rejoin).
                ItemSyncManager.SafeRefreshCookedMaterial(instance.gameObject, instance.name);
            }

            // Also set ItemRigidbody position to prevent physics glitches
            var shipItem = instance.GetComponent<ShipItem>();
            if (shipItem?.itemRigidbodyC != null)
            {
                shipItem.itemRigidbodyC.transform.position = worldPos;
                shipItem.itemRigidbodyC.transform.rotation = item.Rotation;
            }

            // Add to registry for debug overlay (guest needs this too)
            ItemSyncManager.Instance?.RegisterItem(item.InstanceId, item.PrefabIndex);

            // Mark as recently synced so destruction won't broadcast to host
            ItemSyncManager.Instance?.MarkAsRecentlySynced(item.InstanceId);

            return instance;
        }

        /// <summary>
        /// Convert network chart data to game's ChartData format.
        /// </summary>
        private static ChartData ConvertToGameChartData(NetworkChartData networkData)
        {
            var chartData = new ChartData();
            chartData.lines = new System.Collections.Generic.List<ChartLine>();
            chartData.points = new System.Collections.Generic.List<ChartPoint>();

            if (networkData.Lines != null)
            {
                foreach (var line in networkData.Lines)
                {
                    chartData.lines.Add(new ChartLine
                    {
                        startX = line.StartX,
                        startY = line.StartY,
                        endX = line.EndX,
                        endY = line.EndY,
                        color = line.Color
                    });
                }
            }

            if (networkData.Points != null)
            {
                foreach (var point in networkData.Points)
                {
                    chartData.points.Add(new ChartPoint
                    {
                        posX = point.PosX,
                        posY = point.PosY
                    });
                }
            }

            return chartData;
        }

        /// <summary>
        /// Apply rope lengths to control sail deployment.
        /// </summary>
        public static void ApplyRopeLengths(SaveableObject boat, float[] lengths)
        {
            if (lengths == null || lengths.Length == 0)
            {
                Plugin.Log.LogWarning($"[ROPE:APPLY] boat={boat?.gameObject.name}, SKIPPED - lengths null or empty");
                return;
            }

            // Log incoming lengths for rope-sync troubleshooting
            var incomingStr = string.Join(", ", lengths.Select((l, i) => $"[{i}]={l:F2}"));
            Plugin.Log.LogInfo($"[ROPE:APPLY] boat={boat.gameObject.name}, incoming lengths={lengths.Length}: {incomingStr}");

            var ropes = BoatUtility.GetRopeControllers(boat);
            Plugin.Log.LogInfo($"[ROPE:APPLY] boat={boat.gameObject.name}, found ropes={ropes.Length}");

            for (int i = 0; i < Mathf.Min(ropes.Length, lengths.Length); i++)
            {
                var oldVal = ropes[i].currentLength;
                ropes[i].currentLength = lengths[i];
                ropes[i].changed = true; // Trigger visual update
                Plugin.Log.LogInfo($"[ROPE:APPLY] boat={boat.gameObject.name}, rope[{i}] {oldVal:F2} -> {lengths[i]:F2}");
            }

            // Verify after apply
            var afterStr = string.Join(", ", ropes.Select((r, i) => $"[{i}]={r.currentLength:F2}"));
            Plugin.Log.LogInfo($"[ROPE:APPLY] boat={boat.gameObject.name}, AFTER apply: {afterStr}");
        }

        /// <summary>
        /// Apply anchor state to sync anchor deployed/raised status (JOIN-snapshot path).
        ///
        /// S (anchor down-but-visually-up): the OLD body set anchorRb.isKinematic + joint.linearLimit.limit WITHOUT
        /// touching RopeControllerAnchor.currentLength. But the anchor rope's own Update() recomputes
        /// joint.linearLimit.limit = Lerp(0, maxLength, currentLength) every frame from currentLength, so the direct
        /// limit write was reverted within a physics frame and the VISUAL rope stayed wherever currentLength left it.
        /// And Anchor.ExtraFixedUpdate forces body.isKinematic=held whenever !set, so a raw isKinematic write without
        /// the authoritative `set` flag did not stick either -> a just-joined guest saw "anchor down" physics with
        /// the rope drawn UP (or vice-versa). FIX: drive the SAME coupled mechanism the runtime path
        /// (ControlSyncManager.OnRemoteAnchorChanged) uses - the vanilla private SetAnchor/ReleaseAnchor (so `set`,
        /// drag, isKinematic and audio all move together) - AND set RopeControllerAnchor.currentLength (the single
        /// source of truth that drives both the visual rope and the joint limit). currentLength is NORMALIZED
        /// (0..1); the wire carries the ABSOLUTE joint limit (BoatStateCollector.GetAnchorLength reads
        /// joint.linearLimit.limit), so convert via InverseLerp(0, maxLength, ropeLength). Never set one of
        /// {currentLength, set/isKinematic} without the other.
        /// </summary>
        public static void ApplyAnchorState(SaveableObject boat, bool isAnchored, float ropeLength)
        {
            var anchor = BoatUtility.GetAnchor(boat);
            if (anchor == null)
            {
                Plugin.Log.LogWarning($"ApplyAnchorState SKIPPED: no Anchor resolvable on boat '{boat?.gameObject.name}'");
                return;
            }

            // Resolve the anchor rope controller (the visual + joint-limit driver). Prefer BoatMooringRopes'
            // registered anchor controller; fall back to a child search.
            var ropeCtrl = boat.GetComponent<BoatMooringRopes>()?.GetAnchorController()
                           ?? boat.GetComponentInChildren<RopeControllerAnchor>();

            // Drive currentLength (normalized) so the rope's own Update() sets the matching joint.linearLimit.limit
            // and the visible rope length, keeping the visual coupled to the anchored state. The wire length is the
            // absolute joint limit; normalize it back through the rope's maxLength (default ~50).
            if (ropeCtrl != null)
            {
                float maxLen = ropeCtrl.maxLength > 0f ? ropeCtrl.maxLength : 50f;
                // Anchor UP must read fully retracted (currentLength 0) so the visual + joint limit can never be
                // left "down" on a just-purchased / for-sale boat; only an authoritatively anchored boat lowers it.
                float normalized = isAnchored && ropeLength > 0f
                    ? Mathf.Clamp01(Mathf.InverseLerp(0f, maxLen, ropeLength))
                    : 0f;
                ropeCtrl.currentLength = normalized;
            }

            // Drive the authoritative set/release through vanilla (private) so `set`, drag, isKinematic and audio
            // stay coupled - identical to the runtime relay path. Only transition when the state actually differs;
            // the AnchorSet/Release Harmony patches short-circuit on IsApplyingRemoteState (this whole join applies
            // under that guard) so invoking the vanilla methods here does NOT echo a packet back out.
            bool currentlySet = anchor.IsSet();
            if (isAnchored && !currentlySet)
                AccessTools.Method(typeof(Anchor), "SetAnchor")?.Invoke(anchor, null);
            else if (!isAnchored && currentlySet)
                AccessTools.Method(typeof(Anchor), "ReleaseAnchor")?.Invoke(anchor, null);
            else
            {
                // Already in the right set-state; keep isKinematic consistent with it. (Vanilla
                // ExtraFixedUpdate maintains this when !set, but assert it now so the very first post-join
                // frame is already coupled rather than waiting for a fixed step.)
                var anchorRb = anchor.GetComponent<Rigidbody>();
                if (anchorRb != null) anchorRb.isKinematic = isAnchored;
            }

            Plugin.Log.LogDebug($"Applied anchor state (coupled): anchored={isAnchored}, ropeLength={ropeLength:F2}, normalized currentLength set on rope={ropeCtrl != null}");
        }

        /// <summary>
        /// Teleport the player to a specific position and rotation.
        /// Used to sync guest with host's position on join.
        ///
        /// SIMPLIFIED APPROACH: Put player in water near boat, not on it.
        /// This avoids complex parenting issues during cross-region teleport.
        /// Player can swim to boat naturally after join.
        /// </summary>
        public static void TeleportPlayer(Vector3 position, Quaternion rotation, bool isOnBoat)
        {
            var player = Refs.charController;
            if (player == null)
            {
                Plugin.Log.LogWarning("Cannot teleport: charController is null");
                return;
            }

            // Keep player under _shifting world during teleport to prevent FOM freeze
            var shiftingWorld = GameObject.Find("_shifting world")?.transform;

            Vector3 worldPosition;
            Quaternion worldRotation = rotation;

            Debug.VerboseLogger.PlayerEvent($"Guest spawn decision: isOnBoat={isOnBoat} ({(isOnBoat ? "on deck" : "in water")}), boat={(GameState.lastBoat != null ? GameState.lastBoat.name : "fallback")}");

            if (isOnBoat)
            {
                // Spawn the guest ON the boat (centered, dropped from just above the hull origin) so they land
                // on the deck. The old "+5m to the side at water level" put them in the dock/structure when the
                // host was moored (the guest clipped into the pier concrete at Fort Aestrin). 1.5m up is a
                // first-pass deck height - tune if they float or clip the hull.
                // `position` is the host's BOAT-RELATIVE spot, encoded by the collector as
                // GameState.currentBoat.transform.InverseTransformPoint(host) (visualBoat = GameState.currentBoat).
                // Decode in the SAME frame - GameState.currentBoat - not GameState.lastBoat (that is the
                // SaveableObject transform, a DIFFERENT frame than the boatModel the host encoded with). A
                // mismatched frame places the guest at a slightly-off deck spot (cosmetic - PlayerEmbarkerNew
                // re-syncs to walkCol); matching the encode frame is consistent with the 20Hz PlayerSyncManager.
                var boatFrame = GameState.currentBoat != null ? GameState.currentBoat.transform : GameState.lastBoat;
                Plugin.Log.LogInfo($"[PLAYER:TELEPORT] isOnBoat=true, placing on the boat deck");

                if (boatFrame != null)
                {
                    // +1.5m is a small safety drop onto the deck (PlayerEmbarkerNew then continuously re-syncs to walkCol).
                    worldPosition = boatFrame.TransformPoint(position) + new Vector3(0f, 1.5f, 0f);
                    Plugin.Log.LogInfo($"[PLAYER:TELEPORT] Boat frame {boatFrame.name}, host-relative {position}, player on deck at {worldPosition}");
                }
                else
                {
                    // Truly degenerate (isOnBoat but neither currentBoat nor lastBoat resolved). `position` is a
                    // boat-LOCAL coord, so do NOT add the FOM offset (the old `position + offset` treated it as world
                    // and could fling the guest by the whole cross-region offset). Use it directly with a small drop.
                    worldPosition = position + new Vector3(0, 0.5f, 0);
                    Plugin.Log.LogWarning("[PLAYER:TELEPORT] No boat frame; using boat-local position directly (no FOM offset)");
                }
            }
            else
            {
                // Position is in real (offset-independent) coordinates
                var offset = FloatingOriginManager.instance?.outCurrentOffset ?? Vector3.zero;
                worldPosition = position + offset;

                // A host standing on a DOCK sends its raw foot-Y, which can sit INSIDE the pier collider;
                // placing the guest there would spawn them UNDER the dock. Snap onto the real
                // dock/terrain surface with a downward raycast from 5m above (solids
                // only, triggers ignored so embark/dock trigger volumes don't catch it), then drop the guest 1.5m
                // ABOVE the surface so they settle onto the deck - the same safety pattern the on-deck branch uses,
                // and "slightly-high self-corrects, slightly-low clips". Fall back to the old water-level clamp on a miss.
                // Exclude the boat-hull layer (12) so a boat moored at the same pier isn't snapped onto instead of
                // the dock, and require the hit be ABOVE water level so a submerged seabed near a shallow island
                // can't spawn the guest underwater (both flagged by review). Otherwise fall back to the water clamp.
                if (Physics.Raycast(worldPosition + Vector3.up * 5f, Vector3.down, out var groundHit, 65f,
                        Physics.DefaultRaycastLayers & ~(1 << 12), QueryTriggerInteraction.Ignore)
                    && groundHit.point.y > 0.5f)
                {
                    worldPosition.y = groundHit.point.y + 1.5f;
                    Plugin.Log.LogInfo($"[PLAYER:TELEPORT] On land, snapped to surface y={groundHit.point.y:F2} (+1.5m) on '{groundHit.collider.name}', worldPos={worldPosition}");
                }
                else
                {
                    worldPosition.y = Mathf.Max(worldPosition.y, 0.5f);  // At least at water level
                    Plugin.Log.LogInfo($"[PLAYER:TELEPORT] On land/water, no usable ground hit, realPos={position}, worldPos={worldPosition}");
                }
            }

            // Ensure player is under _shifting world (FOM fix)
            if (shiftingWorld != null)
            {
                player.transform.SetParent(shiftingWorld, true);
            }

            player.transform.position = worldPosition;
            player.transform.rotation = worldRotation;

            // Also move ovrController
            var ovrField = typeof(Refs).GetField("ovrController", BindingFlags.Public | BindingFlags.Static);
            var ovrController = ovrField?.GetValue(null) as Component;
            if (ovrController != null)
            {
                if (shiftingWorld != null)
                    ovrController.transform.SetParent(shiftingWorld, true);
                ovrController.transform.position = worldPosition;
            }

            // Also move observerMirror
            if (Refs.observerMirror != null)
            {
                if (shiftingWorld != null)
                    Refs.observerMirror.transform.SetParent(shiftingWorld, true);
                Refs.observerMirror.transform.position = worldPosition;
            }

            // EMBARK FIX: When the host is aboard (isOnBoat), the guest must end the join EMBARKED -
            // i.e. PARENTED INTO THE BOAT HIERARCHY, not left parented to "_shifting world".
            //
            // Why currentBoat alone is insufficient: a player only "rides" a boat by being a child of
            // the boat. FloatingOriginManager.NewShift translates only the DIRECT children of
            // "_shifting world"; a boat is such a child, so a player parented under the boat rides
            // along, but a player parented directly to "_shifting world" is shifted independently and
            // does NOT follow the boat when the host sails it in world space. GameState.currentBoat is
            // only a reference used by push/control sync - it does not reparent anything - so the guest
            // would be left behind in open water until they happened to walk through a deck/ladder
            // EmbarkCol and the vanilla trigger ran EnterBoat() for them.
            //
            // This runs at the very END of TeleportPlayer (after the exact-position teleport and after
            // terrain has loaded), while GameState.recovering is still true (cleared just after this in
            // the join coroutine). With recovering==true the FOM forces instantShifting, so reparenting
            // a transform that is already at its final world position is FOM-safe.
            //
            // The actual transfer lives in ForceEmbarkLocalPlayer (extracted for the cluster-A embark
            // self-heal watchdog in PlayerSyncManager) - GameState.lastBoat is the boat ROOT the join set
            // at ~208-209 (actualBoat.parent = SaveableObject root), which is exactly the helper's input.
            if (isOnBoat && GameState.currentBoat != null)
            {
                if (!ForceEmbarkLocalPlayer(GameState.lastBoat))
                {
                    // Could not resolve the boat root / walk collider; leave the player parented to
                    // _shifting world (no worse than the previous behaviour) and log so this is diagnosable.
                    Plugin.Log.LogWarning("[PLAYER:TELEPORT] isOnBoat but could not resolve walkCol; guest NOT embarked - they will be left behind if the host sails");
                }
            }

            // NOTE: Don't move Camera.main directly - it follows observerMirror through parent hierarchy.
            // Setting its world position directly messes up its local position.

            Plugin.Log.LogInfo($"[PLAYER:TELEPORT] Final: charController={player.transform.position}");
        }

        /// <summary>
        /// A (guest-world-pinned): force the LOCAL player into fully-embarked state on the given boat
        /// ROOT (SaveableObject, the BoatRefs holder). Extracted VERBATIM from the join force-embark
        /// block in TeleportPlayer so the embark self-heal watchdog (PlayerSyncManager) reuses the
        /// exact same dual-frame transfer instead of forking the most bug-prone code in the mod.
        ///
        /// We replicate vanilla PlayerEmbarkDisembarkTrigger.EnterBoat (decomp lines 174-194):
        ///   playerController.parent = boatWalkCollider   (here: charController/ovrController transform)
        ///   playerObserver.parent   = actualBoat=boatModel (here: observerMirror transform)
        /// both with worldPositionStays:true so the caller's on-deck world pose is preserved (the join
        /// teleport just set it; the watchdog heals in place, so it is a visual no-op). charController
        /// and ovrController are the SAME GameObject (PlayerControllerMirror wires both off one
        /// transform), so reparenting player.transform carries the OVR controller along - matching
        /// vanilla, which reparents playerController exactly once. We also set the static
        /// PlayerEmbarkDisembarkTrigger.embarked flag (EnterBoat sets this, decomp line 185): it gates
        /// the later on-land disembark in LateUpdate (decomp line 138), so without it vanilla ExitBoat
        /// would never fire when the guest walks ashore. We do NOT touch the player layer or
        /// BoatEmbarkCollider.ToggleBoatCapsuleCol: EnterBoat itself changes neither, and the player is
        /// already standing on the deck rather than crossing the EmbarkCol that would have zeroed the
        /// capsule radius, so there is nothing to restore.
        ///
        /// Returns false (having changed nothing) when boatModel/walkCol cannot be resolved.
        /// </summary>
        internal static bool ForceEmbarkLocalPlayer(Transform boatRoot)
        {
            var player = Refs.charController;
            if (player == null || boatRoot == null) return false;

            var boatRefs = boatRoot.GetComponent<BoatRefs>();
            if (boatRefs == null) return false;

            // actualBoat (visual boat / mesh parent). BoatRefs.boatModel is the authoritative reference;
            // at join it is the same transform GameState.currentBoat was set to (~208-209), so join
            // behavior is unchanged. Fall back to GameState.currentBoat only if the ref is unwired.
            Transform boatModel = boatRefs.boatModel != null ? boatRefs.boatModel : GameState.currentBoat;

            // Resolve walkCol the way the rest of the mod does (ItemSyncManager ~415-418).
            var embarkCol = boatRefs.GetComponentInChildren<BoatEmbarkCollider>();
            Transform walkCol = embarkCol != null && embarkCol.walkCollider != null
                ? embarkCol.walkCollider
                : boatRefs.walkCol;

            if (boatModel == null || walkCol == null) return false;

            // FALL-THROUGH FIX: boatModel (the VISUAL hull, sea level) and walkCol (the PHYSICS walk
            // collider) are authored ~205m apart in WORLD space but share the SAME boat-LOCAL coordinate
            // for a given deck spot. The OLD line did SetParent(walkCol, worldPositionStays:TRUE), which
            // KEPT the sea-level world position and baked in the ~205m gap -> the controller stood 205m
            // UNDER the deck collider and fell through whenever the host was aboard a boat. Vanilla
            // EnterBoat transfers by LOCAL coordinate instead: express the on-deck world pose as a
            // boatModel-local coord, then re-apply that SAME local coord under walkCol -> the controller
            // lands ON the physics deck. (At a dock the two frames coincide, so dock/land joins worked.)
            {
                var pc = player.transform;
                // worldPositionStays:TRUE is load-bearing - it PRESERVES the current WORLD pose and
                // captures it as a boatModel-LOCAL coord. With FALSE, Unity keeps the stale
                // _shifting-world-local numbers and reinterprets them under boatModel = garbage (flings
                // the controller hundreds of metres off). This makes the transfer byte-identical to
                // vanilla EnterBoat / PlayerEmbarkerNew.SyncToWalkCol.
                pc.SetParent(boatModel, true);                                    // capture on-deck world pose as boatModel-local
                var deckLocalPos = pc.localPosition; var deckLocalRot = pc.localRotation;
                pc.SetParent(walkCol, true);                                      // into the physics-deck frame (world pose preserved)
                pc.localPosition = deckLocalPos; pc.localRotation = deckLocalRot;  // re-apply SAME local coord -> on the deck collider
            }
            // playerObserver -> actualBoat (boatModel)
            if (Refs.observerMirror != null)
                Refs.observerMirror.transform.SetParent(boatModel, true);
            // Mirror EnterBoat's embarked=true so on-land ExitBoat can later fire.
            PlayerEmbarkDisembarkTrigger.embarked = true;

            // Restore the boat root's capsule collider (review finding): the WATCHDOG caller's player
            // typically DID cross the EmbarkCol during the on/off cycles that wedged the vanilla
            // machines, and OnTriggerEnter zeroed the boat capsule radius; vanilla's own EnterBoat call
            // site restores it (PlayerEmbarkDisembarkTrigger.cs:146), and with embarked forced true the
            // vanilla restore branch can never run. Harmless no-op when the radius was never zeroed
            // (the join-path caller).
            var embarkColForCapsule = boatRefs.GetComponentInChildren<BoatEmbarkCollider>();
            if (embarkColForCapsule != null)
            {
                try { embarkColForCapsule.ToggleBoatCapsuleCol(newState: true); }
                catch (System.Exception e) { Plugin.Log.LogWarning($"[EMBARK] could not restore boat capsule collider: {e.Message}"); }
            }

            // GUEST-UNDER-MAP fix: the reparent above matches vanilla PlayerEmbark(), but the
            // component that CONTINUOUSLY positions the controller - PlayerEmbarkerNew - is never
            // told it's embarked. Its private currentBoat stays null, so its LateUpdate runs
            // SyncToWorld() (charController.position = observerMirror.position) instead of
            // SyncToWalkCol(). Combined with PlayerControllerMirror's raw local-number copy across
            // the walkCol vs boatModel frames (~200m apart while underway), that bakes in a large,
            // stable offset and the guest's first-person camera ends up ~200m under the deck (the
            // physics frame; orbit cam uses the visual boat so it looked fine). Arm PlayerEmbarkerNew
            // exactly like vanilla PlayerEmbark() (currentBoat = EmbarkBoat(worldBoat, walkCol),
            // embarked, frameDelay) so SyncToWalkCol() reconciles the frames every frame. Pre-existing
            // bug (only bites when the boat is underway/helmed at join, so walkCol != boatModel);
            // dock joins coincided the frames and worked.
            var embarker = player.GetComponent<PlayerEmbarkerNew>()
                           ?? UnityEngine.Object.FindObjectOfType<PlayerEmbarkerNew>();
            if (embarker != null)
            {
                var et = HarmonyLib.Traverse.Create(embarker);
                et.Field("currentBoat").SetValue(new EmbarkBoat(boatModel, walkCol)); // worldBoat=visual boat, walkCol=physics
                et.Field("embarked").SetValue(true);
                et.Field("frameDelay").SetValue(0);   // 0 not 1: SyncToWalkCol must run the very next LateUpdate (no gravity window)
                // Drive one reconcile NOW so the controller is in the walkCol frame before the first
                // PlayerControllerMirror copy. Guarded: a vanilla-field change must never break the caller.
                try { et.Method("SyncToWalkCol").GetValue(); }
                catch (System.Exception e) { Plugin.Log.LogWarning($"[PLAYER:TELEPORT] one-shot SyncToWalkCol failed (non-fatal): {e.Message}"); }
                Plugin.Log.LogInfo("[PLAYER:TELEPORT] Armed PlayerEmbarkerNew (SyncToWalkCol) so the guest stays on deck");
            }
            else
            {
                Plugin.Log.LogWarning("[PLAYER:TELEPORT] PlayerEmbarkerNew not found; guest may sit in the physics frame (under map)");
            }

            // Keep GameState coherent with the embark (vanilla EnterBoat sets currentBoat=actualBoat and
            // lastBoat=actualBoat.parent). At join these were already set to exactly these values before
            // this ran, so the join path is unchanged; on a watchdog heal they may be stale/null and the
            // rest of the mod (push/control sync, PlayerSyncManager's boat-frame send) reads them.
            GameState.currentBoat = boatModel;
            GameState.lastBoat = boatRoot;

            Plugin.Log.LogInfo($"[PLAYER:TELEPORT] Embarked guest onto boat (walkCol={walkCol.name}, boatModel={boatModel.name})");
            return true;
        }

        /// <summary>
        /// POCKET-INHERIT FIX: deterministic clean slate for the LOCAL guest on a fresh JOIN.
        /// Sets the joiner's OWN survival bars to full and empties their OWN personal inventory pockets,
        /// so a joiner never starts a session with leftover/host items or half-empty needs. This is purely
        /// local (no packet, no broadcast) and is only called for a fresh join (not a recovery reseat).
        /// </summary>
        private static void ResetLocalSurvivalAndInventory()
        {
            // 1. Survival bars to full (mirrors PlayerNeeds.Reset/defaults: 100 = full, debts 100 = no debt,
            //    alcohol 0 = sober). Same static-field API the rest of the survival code uses
            //    (SurvivalSyncManager / SurvivalPatches operate on these exact statics).
            PlayerNeeds.food = 100f;
            PlayerNeeds.water = 100f;
            PlayerNeeds.sleep = 100f;
            PlayerNeeds.vitamins = 100f;
            PlayerNeeds.protein = 100f;
            PlayerNeeds.foodDebt = 100f;
            PlayerNeeds.sleepDebt = 100f;
            PlayerNeeds.alcohol = 0f;
            if (PlayerNeeds.instance != null)
                PlayerNeeds.instance.eatCooldown = 0f;
            Plugin.Log.LogInfo("[JOIN] Clean slate: reset local survival bars to full");

            // 2. Empty the local player's five personal inventory pockets. GPButtonInventorySlot.inventorySlots
            //    is the static array of pocket slots; each slot's currentItem is the ShipItem it holds. We
            //    DESTROY held items (a true clean slate) rather than drop them to the world. Guard with
            //    SetApplyingRemoteState(true) so the ShipItem.DestroyItem Harmony patch early-returns (decomp
            //    ItemPatches.OnDestroyItem checks IsApplyingRemoteState) and does NOT broadcast an ItemDestroyed
            //    (these are local-only stale/ghost items, not shared world items).
            //
            // B-BLOCKER (save corruption) fix: destroy via the VANILLA ShipItem.DestroyItem() rather than a raw
            //    UnityEngine.Object.Destroy(gameObject). DestroyItem() calls SaveablePrefab.Unregister() (decomp
            //    ShipItem.cs:442-455), which removes the prefab from SaveLoadManager.currentPrefabs and
            //    existingInstanceIds. A raw Object.Destroy skips that Unregister, leaving a Unity-destroyed
            //    SaveablePrefab dangling in currentPrefabs; the next CoopSave.SaveGame -> SaveLoadManager.SaveGame
            //    iterates currentPrefabs with NO null check (decomp ~243-246) and calls PrepareSaveData() on the
            //    destroyed object -> MissingReferenceException aborts the entire save.
            //    Mission goods are handled correctly by this path too: Unregister() de-lists the prefab, and the
            //    only consumer of Mission.spawnedGoods (Mission.AbandonMission, decomp Mission.cs:177-183)
            //    null-checks each entry, so a destroyed good is safely skipped there; no separate Good/PlayerMissions
            //    deregistration is needed, and mission save data never iterates the destroyed reference.
            // INVENTORY PERSISTENCE (issue #1): only clean-slate the pockets on a FRESH phantom (first-ever
            // join to this host). A RETURNING guest's phantom coop_session.save already round-trips their pocket
            // items (vanilla SaveablePrefab persists each item + its inventorySlot; the join LoadGame restores
            // them), and destroying them here is what lost the client's inventory between sessions. The original
            // "don't carry over the host's items" purpose is now handled on the SENDER side (BoatStateCollector
            // excludes the host's own pocket items), so this destroy is only needed to zap a genuinely fresh
            // phantom's leftover solo pockets. Needs are still reset to full every join (above), unchanged.
            if (!CoopSave.PhantomWasFreshlyCreated)
            {
                Plugin.Log.LogInfo("[JOIN] Reusing phantom - keeping the inventory it just restored (issue #1)");
                return;
            }

            var itemSync = ItemSyncManager.Instance;
            bool restoreApplying = false;
            if (itemSync != null && !itemSync.IsApplyingRemoteState)
            {
                itemSync.SetApplyingRemoteState(true);
                restoreApplying = true;
            }
            try
            {
                int cleared = 0;
                var slots = GPButtonInventorySlot.inventorySlots;
                if (slots != null)
                {
                    foreach (var slot in slots)
                    {
                        if (slot == null || slot.currentItem == null) continue;
                        var item = slot.currentItem;
                        slot.currentItem = null; // detach so the slot reads empty even before destroy resolves
                        item.DestroyItem();       // vanilla path: ExitBoat + SaveablePrefab.Unregister + Destroy(gameObject)
                        cleared++;
                    }
                }
                Plugin.Log.LogInfo($"[JOIN] Clean slate (fresh phantom): cleared {cleared} item(s) from local inventory pockets");
            }
            finally
            {
                if (restoreApplying && itemSync != null)
                    itemSync.SetApplyingRemoteState(false);
            }
        }

        /// <summary>
        /// Apply mooring rope states to sync dock attachments.
        /// </summary>
        private static void ApplyMooringRopes(BoatMooringRopes mooringRopes, NetworkMooringData[] mooringData)
        {
            if (mooringRopes.ropes == null)
            {
                Plugin.Log.LogWarning("ApplyMooringRopes: ropes array is null");
                return;
            }

            Plugin.Log.LogInfo($"ApplyMooringRopes: {mooringData.Length} ropes in data, {mooringRopes.ropes.Length} ropes on boat");

            for (int i = 0; i < Mathf.Min(mooringRopes.ropes.Length, mooringData.Length); i++)
            {
                var rope = mooringRopes.ropes[i];
                var data = mooringData[i];

                Plugin.Log.LogInfo($"  Rope {i}: data.IsMoored={data.IsMoored}, dockPos={data.DockPosition}");

                if (data.IsMoored)
                {
                    var dock = FindClosestDockMooring(data.DockPosition, out float nearestMissDist);
                    if (dock != null)
                    {
                        Plugin.Log.LogInfo($"  Rope {i}: Found dock {dock.name} at {dock.transform.position}, mooring...");
                        if (rope.IsMoored()) rope.Unmoor(); // release any prior spring before re-moor (join/recovery re-apply) so a different-dock-instance resolve can't leak a second spring dragging the hull under
                        rope.MoorTo(dock);
                        rope.currentRopeLengthSquared = data.LengthSquared;

                        // E (moored-sink residual) FIX: vanilla MoorTo (decomp PickupableBoatMooringRope.cs:247-262)
                        // recomputes currentRopeLengthSquared = GetCurrentDistanceSquared() from the boat's CURRENT
                        // (guest-local, floating-origin-offset) geometry and sets SpringJoint.maxDistance from THAT
                        // local guess - so the just-moored guest spring holds a stale/wrong slack and drags the hull
                        // toward a bad anchor (bow-first sink) until a later runtime rope-length sync arrives. We just
                        // overwrote the currentRopeLengthSquared FIELD with the host's authoritative value above, but
                        // the actual physics constraint (SpringJoint.maxDistance) still carries MoorTo's local guess.
                        // Restore the authoritative maxDistance now, mirroring ControlSyncManager.OnRemoteMooringRope
                        // LengthChanged (~line 1227: springJoint.maxDistance = Mathf.Sqrt(packet.LengthSquared)), so
                        // the spring matches the host immediately instead of after the first scroll/settle sync.
                        // NOTE: the dock ANCHOR itself is NOT offset-wrong - MoorTo anchors to mooring.transform
                        // (the offset-correct local dock object) and the boat-local springAnchor, both offset-
                        // independent - so only maxDistance needs correcting; re-deriving the whole spring on the
                        // guest is unnecessary and would re-introduce the stale guess.
                        var springJoint = MooredToSpringRef(rope);
                        if (springJoint != null)
                        {
                            springJoint.maxDistance = Mathf.Sqrt(data.LengthSquared);
                            Plugin.Log.LogInfo($"  Rope {i}: restored authoritative spring maxDistance={springJoint.maxDistance:F2} from host snapshot (lenSq={data.LengthSquared:F2})");
                        }
                        else
                        {
                            Plugin.Log.LogWarning($"  Rope {i}: MoorTo did not produce a spring joint; could not restore authoritative maxDistance");
                        }

                        Plugin.Log.LogInfo($"  Rope {i}: MoorTo completed, IsMoored now={rope.IsMoored()}");

                        // VISUAL-STRETCH GUARD (issue #5): the X/Z dock match can resolve a dock that, on THIS
                        // client, is NOT co-located with where the boat ended up. The visible line is redrawn every
                        // LateUpdate from the rope end (pinned by MoorTo to the dock cleat) to the boat: the cleat
                        // inherits the horizon-sunk island Y (the loaded island scene is slaved to the IslandHorizon
                        // proxy) while the boat floats at true sea level, and cross-region/floating-origin state can
                        // also separate them horizontally - so the moor is "logically" correct (tied to the pier) yet
                        // renders a rope raking across/into the ocean. The rope's own max length is
                        // sqrt(maxLength=900)=30m (cleat->hull springAnchor); the check below measures cleat->boat
                        // ORIGIN, which adds at most a hull's worth (~15-20m on the largest boats), so 50m is a
                        // safe margin over any LEGIT near-dock moor while still catching a divergent frame -> unmoor
                        // + stow rather than leave a kilometre-long dockline. (v0.2.24 handled the dock-MATCH and the
                        // no-dock MISS; this is the "matched but impossible geometry on this client" branch.)
                        var stretchRb = rope.GetBoatRigidbody();
                        if (stretchRb != null)
                        {
                            float moorSpan = Vector3.Distance(rope.transform.position, stretchRb.transform.position);
                            if (moorSpan > 50f)
                            {
                                Plugin.Log.LogWarning($"  Rope {i}: post-moor span {moorSpan:F0}m implausible " +
                                    $"(dock/boat frames diverge on this client); unmooring + stowing to avoid a stretched dockline");
                                rope.Unmoor();
                                StowRopeIfDisplaced(rope, $"Rope {i}");
                            }
                        }
                    }
                    else
                    {
                        // Dock resolve MISS. Do NOT leave the rope diverged (host: moored, guest: half-applied
                        // mid-state) - that is how a guest ends up with a LineRenderer stretched kilometers to a
                        // horizon-sunk island. Deterministically stow the rope back on its hanger instead, but
                        // only if it is actually moored/out of place - never disturb an already-stowed rope.
                        Plugin.Log.LogWarning($"  Rope {i}: Could not find dock mooring near {data.DockPosition} (5m XZ radius, " +
                                              $"nearest candidate {(float.IsPositiveInfinity(nearestMissDist) ? "none" : nearestMissDist.ToString("F1") + "m")}); stowing rope instead of leaving it diverged");
                        StowRopeIfDisplaced(rope, $"Rope {i}");
                    }
                }
                else
                {
                    Plugin.Log.LogInfo($"  Rope {i}: Not moored in data, skipping");
                }
            }
        }

        /// <summary>
        /// Find the closest dock mooring point to a given position.
        /// Position is in real (offset-independent) coordinates.
        /// Matches on X/Z ONLY: the Y of both the serialized dockPos and the local dock transform is
        /// VIEW-DEPENDENT - vanilla IslandHorizon.ApplyNewHorizon rewrites every far island root's Y each
        /// LateUpdate for earth-curvature rendering (uncapped, ~-10km at 100km range, and shifts with the
        /// local camera height even for near islands). A 3D match therefore misses any far dock and is
        /// fragile even locally; X/Z are stable and unique enough within a 5m radius.
        /// </summary>
        private static GPButtonDockMooring FindClosestDockMooring(Vector3 realPosition, out float nearestMissDist)
        {
            // Convert from real to local coords for comparison with dock.transform.position
            var offset = FloatingOriginManager.instance?.outCurrentOffset ?? Vector3.zero;
            var localPosition = realPosition + offset;

            var docks = Object.FindObjectsOfType<GPButtonDockMooring>();
            GPButtonDockMooring closest = null;
            float closestDist = 5f; // Max 5m search radius
            nearestMissDist = float.PositiveInfinity;

            foreach (var dock in docks)
            {
                var delta = dock.transform.position - localPosition;
                delta.y = 0f; // horizon-sunk island Y is meaningless; match in the horizontal plane only
                var dist = delta.magnitude;
                if (dist < nearestMissDist) nearestMissDist = dist;
                if (dist < closestDist)
                {
                    closestDist = dist;
                    closest = dock;
                }
            }

            return closest;
        }

        /// <summary>
        /// Deterministic safe-failure stow for a mooring rope whose dock could not be resolved locally.
        /// NEVER unmoors a rope that is locally moored: a dock-resolve miss can be transient (dock objects
        /// inactive during island streaming, dockPos drift on a relay) and the local spring is more likely
        /// correct than the unresolvable packet - unmooring here would cut a correctly-moored boat loose.
        /// Only a rope that is NOT moored and NOT on its hanger (the diverged half-applied / detached save
        /// restore state) gets restowed, re-parenting first: a was-moored save restore leaves parent==null
        /// (decomp SaveableObject.cs Load), and ResetRopePos() writes LOCAL position (decomp
        /// PickupableBoatMooringRope.cs), so without the re-parent the rope would teleport to hull-local
        /// coords in WORLD space - i.e. near the world origin.
        /// </summary>
        internal static void StowRopeIfDisplaced(PickupableBoatMooringRope rope, string logTag)
        {
            if (rope == null) return;

            if (rope.IsMoored())
            {
                Plugin.Log.LogInfo($"{logTag}: rope is locally moored; leaving it in place (not unmooring on a dock-resolve miss)");
                return;
            }
            if (rope.IsAtInitialPos())
            {
                return; // already stowed on its hanger - nothing to do
            }

            // Re-attach to the hanger before ResetRopePos (which sets LOCAL pos/rot) so a detached rope
            // is restored relative to the boat, not the world origin.
            if (rope.transform.parent == null)
            {
                var initialParent = Traverse.Create(rope).Field("initialParent").GetValue<Transform>();
                if (initialParent != null) rope.transform.parent = initialParent;
            }
            rope.ResetRopePos();

            // Make sure the "was moored" persistence flag cannot survive a stow.
            var saveable = rope.GetComponent<SaveableObject>();
            if (saveable != null) saveable.extraSetting = false;

            Plugin.Log.LogInfo($"{logTag}: stowed displaced rope back on hanger");
        }
    }
}
