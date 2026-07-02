using System.Collections.Generic;
using UnityEngine;
using Steamworks;
using SailwindCoop.Debug;
using SailwindCoop.Networking.Packets;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Manages NPC boat synchronization between host and guest.
    /// Host sends transform+sails at 2Hz, guest interpolates toward received state.
    /// </summary>
    public class NPCBoatSyncManager : MonoBehaviour
    {
        public static NPCBoatSyncManager Instance { get; private set; }

        private const float SyncInterval = 0.2f; // 5 Hz (was 2Hz - too jerky)
        private const float InterpolationSpeed = 10f; // Lerp speed for smooth movement (was 5f)
        private float _lastSyncTime;

        // Guest: cached references and interpolation targets by hierarchy path
        private readonly Dictionary<string, NPCBoatController> _npcBoatCache = new Dictionary<string, NPCBoatController>();
        private readonly Dictionary<string, NPCBoatTarget> _npcBoatTargets = new Dictionary<string, NPCBoatTarget>();
        private readonly HashSet<string> _warnedMissingPaths = new HashSet<string>();
        private bool _cacheInitialized;

        // Host: skip sending an NPC boat state that hasn't meaningfully changed since we last sent it
        // (anchored/docked NPC boats at busy ports otherwise burn 5Hz x N packets for nothing). A
        // keepalive resend bounds staleness so a dropped unreliable packet on a boat that then stops
        // can't leave the guest with a permanently stale target. Moving boats exceed the threshold every
        // poll so they sync exactly as before; the guest interpolates toward the last target regardless.
        private struct SentNPCState { public Vector3 Position; public Quaternion Rotation; public float[] SailLengths; public float SentTime; }
        private readonly Dictionary<string, SentNPCState> _lastSentNPC = new Dictionary<string, SentNPCState>();
        private const float NPCPosThreshold = 0.1f;       // metres
        private const float NPCRotThreshold = 1.0f;       // degrees
        private const float NPCSailThreshold = 0.02f;     // rope length units
        private const float NPCKeepaliveInterval = 1.0f;  // force a resend at least this often

        // Host-side per-NPC last-sent DAMAGE state, so we only broadcast NPCBoatDamage on a
        // meaningful change or a sink transition (NPC BoatDamage runs only on the host; this carries its
        // authoritative water/hull/sunk to guests whose own NPC BoatDamage sim is disabled). Keyed by path.
        private struct SentNPCDamage { public float WaterLevel; public float HullDamage; public bool Sunk; }
        private readonly Dictionary<string, SentNPCDamage> _lastSentNPCDamage = new Dictionary<string, SentNPCDamage>();
        private const float NPCWaterThreshold = 0.02f;    // waterLevel units
        private const float NPCHullThreshold = 0.02f;     // hullDamage units

        // Host-side anti-spam for guest NPC-ram hit requests, mirroring vanilla BoatDamage.Impact's impactTimer
        // (1 impact/sec per boat). Without it a guest grinding against an NPC at 5Hz+ could pile damage far
        // faster than the single-player collision path ever could. Keyed by NPC hierarchy path; Time.time stamp
        // of the last APPLIED hit for that NPC.
        private readonly Dictionary<string, float> _lastNPCHitApplied = new Dictionary<string, float>();

        /// <summary>
        /// Interpolation target state for a single NPC boat.
        /// </summary>
        private struct NPCBoatTarget
        {
            public Transform Transform;  // Direct reference, no path lookup needed
            // Store REAL (offset-independent) position, calculate local on-demand
            public Vector3 RealPosition;
            public Quaternion Rotation;
            public float[] SailLengths;
            public Rigidbody Rigidbody;
            public RopeController[] Ropes;
            public bool HasReceivedState;
        }

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
            if (!Plugin.IsMultiplayer) return;

            Plugin.Profiler?.StartMeasure();

            if (Plugin.IsHost)
            {
                SendNPCBoatStates();
            }
            else
            {
                InterpolateNPCBoats();
            }

            Plugin.Profiler?.EndMeasure("NPCBoats");
        }

        /// <summary>
        /// Guest: Smoothly interpolate all NPC boats toward their target states.
        /// Runs every frame for smooth movement between 2Hz updates.
        /// </summary>
        private void InterpolateNPCBoats()
        {
            // Get current offset once per frame
            var offset = FloatingOriginManager.instance?.outCurrentOffset ?? Vector3.zero;

            foreach (var kvp in _npcBoatTargets)
            {
                var target = kvp.Value;

                if (!target.HasReceivedState) continue;
                if (target.Transform == null) continue;  // NPC was destroyed

                // Force kinematic every frame (something may reset it)
                if (target.Rigidbody != null && !target.Rigidbody.isKinematic)
                {
                    target.Rigidbody.isKinematic = true;
                    target.Rigidbody.velocity = Vector3.zero;
                    target.Rigidbody.angularVelocity = Vector3.zero;
                }

                // Calculate local position on-demand using current offset
                var targetLocalPosition = target.RealPosition + offset;

                // Smooth interpolation toward target
                target.Transform.position = Vector3.Lerp(
                    target.Transform.position,
                    targetLocalPosition,
                    Time.deltaTime * InterpolationSpeed
                );

                target.Transform.rotation = Quaternion.Slerp(
                    target.Transform.rotation,
                    target.Rotation,
                    Time.deltaTime * InterpolationSpeed
                );

                // Apply sail states (no interpolation needed - they change slowly)
                if (target.SailLengths != null && target.Ropes != null)
                {
                    for (int i = 0; i < target.SailLengths.Length && i < target.Ropes.Length; i++)
                    {
                        if (target.Ropes[i] != null)
                        {
                            target.Ropes[i].currentLength = target.SailLengths[i];
                        }
                    }
                }
            }
        }

        // Max distance to sync NPC boats (meters) - beyond this they're not visible anyway
        private const float MaxSyncDistance = 2000f;
        private const float MaxSyncDistanceSqr = MaxSyncDistance * MaxSyncDistance;

        /// <summary>
        /// Host: Send all visible NPC boat states at 5Hz.
        /// </summary>
        private void SendNPCBoatStates()
        {
            if (Time.time - _lastSyncTime < SyncInterval) return;
            _lastSyncTime = Time.time;

            var npcBoats = FindObjectsOfType<NPCBoatController>();
            if (npcBoats == null || npcBoats.Length == 0) return;

            var offset = FloatingOriginManager.instance?.outCurrentOffset ?? Vector3.zero;

            // N1: relevance-check NPC boats against the NEAREST of ALL players (host camera + every connected
            // guest avatar), not just the host camera. The host simulates an NPC boat over a wider range than the
            // 2000m sync cull (vanilla NPCBoatController gates sim on horizon.closeToPlayer), so there is a band
            // where the host MOVES a boat but the old host-only cull never streamed it to a guest who was actually
            // near it. Syncing within range of ANY crew member closes that band. (NOTE: a boat OUTSIDE the host's
            // closeToPlayer sim range entirely is frozen on the host and this only streams that frozen state - the
            // full fix for that case is a guest-side local-sim handoff, see N1 design notes; deferred pending a
            // live playtest.) Per-tick alloc of a tiny list at 5Hz is negligible.
            var playerPositions = new List<Vector3>();
            if (Camera.main != null) playerPositions.Add(Camera.main.transform.position);
            var rpm = SailwindCoop.Player.RemotePlayerManager.Instance;
            if (rpm != null)
                foreach (var avatar in rpm.Avatars)
                {
                    // Skip avatars whose body isn't spawned: GetLastKnownPosition returns Vector3.zero for them,
                    // and with floating origin the world hovers near (0,0,0), so an unspawned avatar would falsely
                    // mark an NPC near origin as "relevant to a crew member" and stream it every poll.
                    if (avatar.GetRemoteCapsule() == null) continue;
                    playerPositions.Add(avatar.GetLastKnownPosition());
                }
            if (playerPositions.Count == 0) playerPositions.Add(Vector3.zero);
            int sentCount = 0;

            foreach (var npc in npcBoats)
            {
                if (npc == null) continue;

                // Only sync boats within visible range of SOME player.
                float minDistSqr = float.MaxValue;
                for (int i = 0; i < playerPositions.Count; i++)
                {
                    float d = (npc.transform.position - playerPositions[i]).sqrMagnitude;
                    if (d < minDistSqr) minDistSqr = d;
                }
                if (minDistSqr > MaxSyncDistanceSqr) continue;

                // Broadcast authoritative NPC damage/sink on change or sink transition. Shares
                // this loop's crew-relative cull + 5Hz cadence, so a guest near a host-damaged/sunk NPC
                // reconciles it. Independent of the transform dedupe below (a moored NPC may be flooding
                // while transform-static).
                BroadcastNPCDamageIfChanged(npc);

                var packet = CollectNPCBoatState(npc, offset);

                // Dedupe: skip near-unchanged states unless the keepalive window elapsed.
                if (!NPCStateChanged(packet)) continue;

                Plugin.NetworkManager.SendToAllUnreliable(PacketType.NPCBoatState, writer =>
                {
                    PacketSerializer.WriteNPCBoatState(writer, packet);
                });
                _lastSentNPC[packet.HierarchyPath] = new SentNPCState
                {
                    Position = packet.Position,
                    Rotation = packet.Rotation,
                    SailLengths = packet.SailLengths,
                    SentTime = Time.time
                };

                sentCount++;
            }

            VerboseLogger.NPCBoatSend($"States sent, count={sentCount}", throttle: true);
        }

        /// <summary>
        /// Host dedup test: has this NPC boat's state changed enough to be worth resending, or has the
        /// keepalive window elapsed? Returns true (send) if we've never sent it, or pos/rot/sails moved
        /// past their thresholds, or it's been longer than NPCKeepaliveInterval since the last send.
        /// </summary>
        private bool NPCStateChanged(NPCBoatStatePacket packet)
        {
            if (!_lastSentNPC.TryGetValue(packet.HierarchyPath, out var last)) return true;
            if (Time.time - last.SentTime >= NPCKeepaliveInterval) return true;
            if ((packet.Position - last.Position).sqrMagnitude > NPCPosThreshold * NPCPosThreshold) return true;
            if (Quaternion.Angle(packet.Rotation, last.Rotation) > NPCRotThreshold) return true;
            var a = packet.SailLengths; var b = last.SailLengths;
            if (a == null || b == null || a.Length != b.Length) return true;
            for (int i = 0; i < a.Length; i++)
                if (Mathf.Abs(a[i] - b[i]) > NPCSailThreshold) return true;
            return false;
        }

        /// <summary>
        /// Host: Collect state from a single NPC boat.
        /// </summary>
        private NPCBoatStatePacket CollectNPCBoatState(NPCBoatController npc, Vector3 offset)
        {
            var transform = npc.transform;
            var ropes = npc.GetComponentsInChildren<RopeController>();

            var sailLengths = new float[ropes.Length];
            for (int i = 0; i < ropes.Length; i++)
            {
                sailLengths[i] = ropes[i]?.currentLength ?? 0f;
            }

            return new NPCBoatStatePacket
            {
                HierarchyPath = GetHierarchyPath(transform),
                Position = transform.position - offset,
                Rotation = transform.rotation,
                SailLengths = sailLengths
            };
        }

        /// <summary>
        /// Host: broadcast this NPC boat's authoritative damage/sink state if it changed
        /// meaningfully since the last send, or it just sank. NPC BoatDamage runs only on the host, so
        /// without this a host-side-damaged/sunk NPC stays alive on every guest. Reliable (sink/health is
        /// an event that must not be dropped). Keyed by hierarchy path. No-op for NPCs without BoatDamage.
        /// </summary>
        private void BroadcastNPCDamageIfChanged(NPCBoatController npc)
        {
            var damage = npc.GetComponent<BoatDamage>();
            if (damage == null) return;

            var path = GetHierarchyPath(npc.transform);

            bool send;
            if (!_lastSentNPCDamage.TryGetValue(path, out var last))
            {
                // Only start syncing once an NPC has actually taken some damage/water or sunk, so a port
                // full of pristine NPC boats doesn't each emit a baseline packet on first sight.
                send = damage.sunk || damage.waterLevel > NPCWaterThreshold || damage.hullDamage > NPCHullThreshold;
            }
            else
            {
                send = damage.sunk != last.Sunk
                    || Mathf.Abs(damage.waterLevel - last.WaterLevel) > NPCWaterThreshold
                    || Mathf.Abs(damage.hullDamage - last.HullDamage) > NPCHullThreshold;
            }

            if (!send) return;

            var packet = BuildNPCDamagePacket(npc, damage);

            VerboseLogger.NPCBoatSend($"Damage, path={path}, water={packet.WaterLevel:F3}, hull={packet.HullDamage:F3}, sunk={packet.Sunk}");

            Plugin.NetworkManager.SendToAllReliable(PacketType.NPCBoatDamage, w =>
                PacketSerializer.WriteNPCBoatDamage(w, packet));

            _lastSentNPCDamage[path] = new SentNPCDamage
            {
                WaterLevel = damage.waterLevel,
                HullDamage = damage.hullDamage,
                Sunk = damage.sunk
            };
        }

        /// <summary>
        /// Host: a guest reported ramming an NPC boat. Apply the impact to the NPC's
        /// BoatDamage authoritatively (mirrors vanilla BoatDamage.Impact's hull-damage formula, which the
        /// player-boat collision path never applies to NPC hulls). The resulting state is relayed to all
        /// guests on the next BroadcastNPCDamageIfChanged tick. Host-only.
        /// </summary>
        public void OnNPCBoatHitRequestReceived(NPCBoatHitRequestPacket packet)
        {
            if (!Plugin.IsHost) return;

            VerboseLogger.NPCBoatRecv($"HitRequest, path={packet.HierarchyPath}, force={packet.ImpactForce:F2}");

            var npc = FindNPCBoatByPathHost(packet.HierarchyPath);
            if (npc == null) return;

            var damage = npc.GetComponent<BoatDamage>();
            if (damage == null || damage.sunk) return;

            // Anti-spam: vanilla BoatDamage.Impact gates 1 applied impact per impactCooldown (default 1s) via
            // impactTimer. Mirror that here so a guest can't pile damage on an NPC faster than single-player by
            // streaming hit requests. Keyed by path; reject if we applied a hit to this NPC within impactCooldown.
            if (_lastNPCHitApplied.TryGetValue(packet.HierarchyPath, out var lastHit)
                && Time.time - lastHit < damage.impactCooldown)
            {
                VerboseLogger.NPCBoatRecv($"HitRequest on cooldown, path={packet.HierarchyPath}, no damage");
                return;
            }

            // Reject below-threshold hits, matching vanilla BoatDamage.Impact's minimumImpactVelocity gate.
            if (packet.ImpactForce < damage.minimumImpactVelocity) return;

            // Same formula as vanilla BoatDamage.Impact: force * impactDamageMult, clamped to maxDamagePerImpact.
            float dmg = packet.ImpactForce * damage.impactDamageMult;
            if (dmg > damage.maxDamagePerImpact) dmg = damage.maxDamagePerImpact;
            damage.hullDamage = Mathf.Min(1f, damage.hullDamage + dmg);
            _lastNPCHitApplied[packet.HierarchyPath] = Time.time; // arm the anti-spam cooldown for this NPC

            VerboseLogger.DamageEvent($"NPC hull damage from guest ram: +{dmg:F3}, boat={npc.name}, hull={damage.hullDamage:F3}");

            // Broadcast the new authoritative state immediately (don't wait up to a full poll).
            BroadcastNPCDamageIfChanged(npc);
        }

        /// <summary>
        /// Host-side path -> NPC lookup (the host has no guest interpolation cache). Linear scan over the
        /// scene's NPC boats; only used on the rare guest-hit-report event, not the per-frame send loop.
        /// </summary>
        private NPCBoatController FindNPCBoatByPathHost(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var npcBoats = FindObjectsOfType<NPCBoatController>();
            foreach (var npc in npcBoats)
            {
                if (npc == null) continue;
                if (GetHierarchyPath(npc.transform) == path) return npc;
            }
            return null;
        }

        /// <summary>
        /// Get full hierarchy path for a transform.
        /// </summary>
        private string GetHierarchyPath(Transform t)
        {
            var path = t.name;
            while (t.parent != null)
            {
                t = t.parent;
                path = t.name + "/" + path;
            }
            return path;
        }

        /// <summary>
        /// Public accessor so patches can compute the SAME path key this manager uses to identify
        /// an NPC boat across clients (e.g. the guest NPC-ram hit report). Thin wrapper over GetHierarchyPath.
        /// </summary>
        public string GetHierarchyPathPublic(Transform t) => t != null ? GetHierarchyPath(t) : null;

        /// <summary>
        /// Host: collect a snapshot of all visible NPC boats into a packet.
        /// </summary>
        private NPCBoatSnapshotPacket BuildSnapshotPacket()
        {
            var npcBoats = FindObjectsOfType<NPCBoatController>();
            var offset = FloatingOriginManager.instance?.outCurrentOffset ?? Vector3.zero;
            var cameraPos = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
            var states = new List<NPCBoatStatePacket>();

            foreach (var npc in npcBoats)
            {
                if (npc == null) continue;

                // Only sync boats within visible range
                var distSqr = (npc.transform.position - cameraPos).sqrMagnitude;
                if (distSqr > MaxSyncDistanceSqr) continue;

                states.Add(CollectNPCBoatState(npc, offset));
            }

            return new NPCBoatSnapshotPacket { Boats = states.ToArray() };
        }

        /// <summary>
        /// Host: Send full snapshot of all visible NPC boats to all guests.
        /// </summary>
        public void SendSnapshot()
        {
            if (!Plugin.IsHost) return;

            var packet = BuildSnapshotPacket();

            VerboseLogger.NPCBoatSend($"Snapshot, count={packet.Boats.Length}");

            Plugin.NetworkManager.SendToAllReliable(PacketType.NPCBoatSnapshot, writer =>
            {
                PacketSerializer.WriteNPCBoatSnapshot(writer, packet);
            });
        }

        /// <summary>
        /// Host: Send NPC-boat snapshot to ONE joining guest (N-player Phase 3 targeted join resync).
        /// Same payload as SendSnapshot, targeted so already-settled guests aren't re-synced.
        /// At N=1 the target is the only peer, so this == the broadcast.
        /// </summary>
        public void SendSnapshotTo(SteamId target)
        {
            if (!Plugin.IsHost) return;

            var packet = BuildSnapshotPacket();

            VerboseLogger.NPCBoatSend($"Snapshot (to {target}), count={packet.Boats.Length}");

            Plugin.NetworkManager.SendReliable(target, PacketType.NPCBoatSnapshot, writer =>
            {
                PacketSerializer.WriteNPCBoatSnapshot(writer, packet);
            });

            // MAJOR (NPC late-joiner damage): the snapshot above only carries transform+sails. The change-
            // triggered NPCBoatDamage broadcast (BroadcastNPCDamageIfChanged) is gated on the single host-global
            // _lastSentNPCDamage dict, so an NPC that was damaged/sunk long before this peer joined never
            // re-emits to it - the fresh joiner sees a pristine hull. Send a one-shot, TARGETED burst of the
            // existing NPCBoatDamage packet (reliable) for every NPC currently in a non-baseline state so the
            // joiner converges. Reuses the same packet builder; does NOT touch _lastSentNPCDamage (this is
            // per-peer catch-up, not the host's change-tracking baseline).
            SendDamageBurstTo(target);
        }

        /// <summary>
        /// MAJOR (host): send the current authoritative damage/sink state of every non-baseline NPC boat to ONE
        /// joining peer, reliably and targeted. Mirrors BroadcastNPCDamageIfChanged's "non-baseline" test and
        /// packet build, but emits directly to the joiner regardless of the host-global last-sent state, so a
        /// guest who joins after a sink still converges. Independent of the snapshot's transform stream.
        /// </summary>
        private void SendDamageBurstTo(SteamId target)
        {
            var npcBoats = FindObjectsOfType<NPCBoatController>();
            if (npcBoats == null || npcBoats.Length == 0) return;

            int sent = 0;
            foreach (var npc in npcBoats)
            {
                if (npc == null) continue;
                var damage = npc.GetComponent<BoatDamage>();
                if (damage == null) continue;

                // Only NPCs that have actually taken water/hull damage or sunk - a pristine port full of NPC
                // boats sends nothing, matching the baseline gate in BroadcastNPCDamageIfChanged's first-send.
                if (!(damage.sunk || damage.waterLevel > NPCWaterThreshold || damage.hullDamage > NPCHullThreshold))
                    continue;

                var packet = BuildNPCDamagePacket(npc, damage);
                Plugin.NetworkManager.SendReliable(target, PacketType.NPCBoatDamage, w =>
                    PacketSerializer.WriteNPCBoatDamage(w, packet));
                sent++;
            }

            if (sent > 0)
                VerboseLogger.NPCBoatSend($"Damage burst (to {target}), count={sent}");
        }

        /// <summary>
        /// Build the NPCBoatDamage packet for one NPC boat (shared by the change-triggered broadcast and the
        /// per-join targeted burst). Keyed by hierarchy path so guests resolve the same NPC.
        /// </summary>
        private NPCBoatDamagePacket BuildNPCDamagePacket(NPCBoatController npc, BoatDamage damage)
        {
            return new NPCBoatDamagePacket
            {
                HierarchyPath = GetHierarchyPath(npc.transform),
                WaterLevel = damage.waterLevel,
                HullDamage = damage.hullDamage,
                Sunk = damage.sunk
            };
        }

        /// <summary>
        /// Guest: Handle single NPC boat state update.
        /// </summary>
        public void OnNPCBoatStateReceived(NPCBoatStatePacket packet)
        {
            VerboseLogger.NPCBoatRecv($"State, path={packet.HierarchyPath}", throttle: true);

            var npc = FindNPCBoat(packet.HierarchyPath);
            if (npc == null)
            {
                // Log cache keys for debugging path mismatch
                if (_cacheInitialized && _npcBoatCache.Count > 0)
                {
                    var cacheKeys = string.Join(", ", _npcBoatCache.Keys);
                    Plugin.Log.LogWarning($"[NPCBoat] Path mismatch! Received: {packet.HierarchyPath}, Cache has: {cacheKeys}");
                }
                return;
            }

            ApplyNPCBoatState(npc, packet);
        }

        /// <summary>
        /// Guest: Handle full NPC boat snapshot.
        /// </summary>
        public void OnNPCBoatSnapshotReceived(NPCBoatSnapshotPacket packet)
        {
            VerboseLogger.NPCBoatRecv($"Snapshot, count={packet.Boats?.Length ?? 0}");

            if (packet.Boats == null) return;

            foreach (var state in packet.Boats)
            {
                var npc = FindNPCBoat(state.HierarchyPath);
                if (npc == null) continue;

                ApplyNPCBoatState(npc, state);
            }
        }

        /// <summary>
        /// Guest: apply authoritative NPC boat damage/sink from the host. The guest's own NPC
        /// BoatDamage sim is disabled (DamagePatches), so we write the fields directly and replicate the
        /// sink-side effects vanilla BoatDamage.UpdateWaterAndDrag would apply (zero buoyancy, disable the
        /// hull collider) so the host-sunk NPC actually reads as sunk here instead of a phantom floating hull.
        /// Idempotent: re-applying the same state is harmless.
        /// </summary>
        public void OnNPCBoatDamageReceived(NPCBoatDamagePacket packet)
        {
            if (Plugin.IsHost) return;

            VerboseLogger.NPCBoatRecv($"Damage, path={packet.HierarchyPath}, water={packet.WaterLevel:F3}, hull={packet.HullDamage:F3}, sunk={packet.Sunk}");

            var npc = FindNPCBoat(packet.HierarchyPath);
            if (npc == null) return;

            var damage = npc.GetComponent<BoatDamage>();
            if (damage == null) return;

            damage.waterLevel = packet.WaterLevel;
            damage.hullDamage = packet.HullDamage;

            // Mirror the sink transition. The streamed transform (BoatHorizon drop + host physics) carries
            // the visual sinking; here we keep the logical flag + hull collider consistent so the guest can't
            // collide/board a "sunk" NPC hull. Buoyancy (BoatProbes._forceMultiplier) is host-side only and the
            // guest forces the NPC rigidbody kinematic anyway, so we deliberately don't touch it here.
            var hullCol = npc.GetComponent<CapsuleCollider>();
            if (packet.Sunk && !damage.sunk)
            {
                damage.sunk = true;
                if (hullCol != null) hullCol.enabled = false;
                VerboseLogger.DamageEvent($"NPC boat {packet.HierarchyPath} sunk (from host state)");
            }
            else if (!packet.Sunk && damage.sunk)
            {
                damage.sunk = false;
                if (hullCol != null) hullCol.enabled = true;
                VerboseLogger.DamageEvent($"NPC boat {packet.HierarchyPath} un-sunk (from host state)");
            }

            VerboseLogger.NPCBoatApply($"Damage applied, path={packet.HierarchyPath}, sunk={damage.sunk}");
        }

        /// <summary>
        /// Find NPC boat by hierarchy path, using cache.
        /// </summary>
        private NPCBoatController FindNPCBoat(string path)
        {
            // Check cache first
            if (_npcBoatCache.TryGetValue(path, out var cached))
            {
                if (cached != null) return cached;
                // Cached reference is stale, remove it
                _npcBoatCache.Remove(path);
            }

            // Build cache if not done
            if (!_cacheInitialized)
            {
                BuildCache();
            }

            // Check cache again
            if (_npcBoatCache.TryGetValue(path, out cached) && cached != null)
            {
                return cached;
            }

            // Only warn once per missing path to avoid log spam
            if (_warnedMissingPaths.Add(path))
            {
                Plugin.Log.LogWarning($"[NPCBoat] Cannot find NPC boat: {path}");
            }
            return null;
        }

        /// <summary>
        /// Build cache of all NPC boats by hierarchy path.
        /// </summary>
        private void BuildCache()
        {
            _npcBoatCache.Clear();

            var npcBoats = FindObjectsOfType<NPCBoatController>();
            foreach (var npc in npcBoats)
            {
                if (npc == null) continue;
                var path = GetHierarchyPath(npc.transform);
                _npcBoatCache[path] = npc;
            }

            _cacheInitialized = true;
            VerboseLogger.NPCBoatApply($"Cache built, count={_npcBoatCache.Count}");
        }

        /// <summary>
        /// Store received state as interpolation target for NPC boat.
        /// Actual interpolation happens in Update loop.
        /// </summary>
        private void ApplyNPCBoatState(NPCBoatController npc, NPCBoatStatePacket packet)
        {
            // Store real position directly, don't convert to local here
            // Local position is calculated on-demand in InterpolateNPCBoats using current offset

            // Get or create target entry
            var target = new NPCBoatTarget
            {
                Transform = npc.transform,  // Store direct reference
                RealPosition = packet.Position,  // Store real position, not local
                Rotation = packet.Rotation,
                SailLengths = packet.SailLengths,
                Rigidbody = npc.GetComponent<Rigidbody>(),
                Ropes = npc.GetComponentsInChildren<RopeController>(),
                HasReceivedState = true
            };

            // Force kinematic immediately on first receive
            if (target.Rigidbody != null && !target.Rigidbody.isKinematic)
            {
                target.Rigidbody.isKinematic = true;
                target.Rigidbody.velocity = Vector3.zero;
                target.Rigidbody.angularVelocity = Vector3.zero;
            }

            _npcBoatTargets[packet.HierarchyPath] = target;

            VerboseLogger.NPCBoatApply($"Target updated, path={packet.HierarchyPath}, realPos={packet.Position}");
        }

        /// <summary>
        /// Reset state when leaving multiplayer.
        /// </summary>
        public void Reset()
        {
            _npcBoatCache.Clear();
            _npcBoatTargets.Clear();
            _warnedMissingPaths.Clear();
            _lastSentNPC.Clear();
            _lastSentNPCDamage.Clear();
            _lastNPCHitApplied.Clear();
            _cacheInitialized = false;
            _lastSyncTime = 0f;
        }
    }
}
