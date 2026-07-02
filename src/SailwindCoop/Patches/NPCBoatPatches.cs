using HarmonyLib;
using UnityEngine;
using SailwindCoop.Debug;
using SailwindCoop.Networking.Packets;
using SailwindCoop.Sync;

namespace SailwindCoop.Patches
{
    /// <summary>
    /// Disables NPC boat AI simulation on guest.
    /// Guest receives authoritative state from host.
    /// </summary>
    public static class NPCBoatPatches
    {
        /// <summary>
        /// Skip NPC boat Update (waypoint navigation) on guest.
        /// </summary>
        [HarmonyPatch(typeof(NPCBoatController), "Update")]
        public static class NPCBoatControllerUpdatePatch
        {
            [HarmonyPrefix]
            public static bool Prefix()
            {
                PatchProfiler.Begin("NPCBoat.Update");
                if (!Plugin.IsMultiplayer)
                {
                    PatchProfiler.End("NPCBoat.Update");
                    return true;
                }
                PatchProfiler.End("NPCBoat.Update");
                return Plugin.IsHost;
            }
        }

        /// <summary>
        /// Skip NPC boat FixedUpdate (physics forces) on guest.
        /// </summary>
        [HarmonyPatch(typeof(NPCBoatController), "FixedUpdate")]
        public static class NPCBoatControllerFixedUpdatePatch
        {
            [HarmonyPrefix]
            public static bool Prefix()
            {
                PatchProfiler.Begin("NPCBoat.FixedUpdate");
                if (!Plugin.IsMultiplayer)
                {
                    PatchProfiler.End("NPCBoat.FixedUpdate");
                    return true;
                }
                PatchProfiler.End("NPCBoat.FixedUpdate");
                return Plugin.IsHost;
            }
        }

        /// <summary>
        /// Skip NPCFishingBoat time-based position changes on guest.
        /// NPCFishingBoat extends NPCBoatController but has its own Update.
        /// </summary>
        [HarmonyPatch(typeof(NPCFishingBoat), "Update")]
        public static class NPCFishingBoatUpdatePatch
        {
            [HarmonyPrefix]
            public static bool Prefix()
            {
                PatchProfiler.Begin("NPCFishingBoat.Update");
                if (!Plugin.IsMultiplayer)
                {
                    PatchProfiler.End("NPCFishingBoat.Update");
                    return true;
                }
                PatchProfiler.End("NPCFishingBoat.Update");
                return Plugin.IsHost;
            }
        }

        /// <summary>
        /// On the GUEST, an NPC ship is host-authoritative and frozen at its
        /// scene-load position (NPC AI is disabled on guests above, and NPC boats are NOT part of the synced
        /// boat world state). When the host's NPC ship moors next to the shared player boat, the guest's frozen
        /// copy overlaps the dock/shared boat and the guest's vanilla PlayerEmbarkerNew can pick the NPC hull's
        /// embark collider instead of the shared boat -> "jump onto my own ship, teleport onto the NPC ship;
        /// jump off, snap back to the dock." Guests never pilot or legitimately board NPC boats, so disable the
        /// embark trigger on every NPC boat on the guest. Host + solo are unaffected.
        /// </summary>
        [HarmonyPatch(typeof(BoatEmbarkCollider), "Awake")]
        public static class NPCBoatEmbarkColliderGuestPatch
        {
            [HarmonyPostfix]
            public static void Postfix(BoatEmbarkCollider __instance)
            {
                if (!Plugin.IsMultiplayer || Plugin.IsHost) return;
                var npc = __instance.GetComponentInParent<NPCBoatController>();
                if (npc == null) return; // only NPC hulls carry NPCBoatController; player/purchasable boats don't
                var col = __instance.GetComponent<Collider>();
                if (col != null)
                {
                    col.enabled = false;
                    Plugin.Log.LogInfo($"[NPCBoat] Guest: disabled embark collider on NPC boat '{npc.name}' (prevents boarding a frozen, unsynced NPC hull)");
                }
            }
        }

        /// <summary>
        /// Crew-relative NPC simulation on the HOST. Vanilla BoatHorizon.DistanceCheck only
        /// marks a boat closeToPlayer (which gates NPCBoatController AI/physics) within 1000m of the HOST's
        /// observer. So an NPC near a GUEST but far from the host is FROZEN on the host, and the crew-relative
        /// stream then only carries that frozen state. Widen closeToPlayer for NPC boats to ANY crew member:
        /// if the host marked it far but a remote crew avatar is within the same 1000m radius, force it close
        /// so the host actually simulates it. Clears the long random updateCooldown vanilla set when it went
        /// far, so sim resumes promptly. Host-only, NPC-only; solo + the player's own boat are untouched.
        /// </summary>
        [HarmonyPatch(typeof(BoatHorizon), "DistanceCheck")]
        public static class BoatHorizonCrewRelativeNPCPatch
        {
            private const float CloseRadius = 1000f;               // matches vanilla DistanceCheck threshold
            private const float CloseRadiusSqr = CloseRadius * CloseRadius;
            // We force closeToPlayer=true for a host-far/guest-near NPC, but we must NOT zero updateCooldown:
            // vanilla's Update only runs DistanceCheck when updateCooldown<=0, so zeroing it makes DistanceCheck
            // (plus this foreach over crew avatars) run EVERY frame for each such NPC - next frame DistanceCheck
            // flips closeToPlayer back to false and sets a fresh 10-20s cooldown, we re-force true and re-zero,
            // forever. Set a small POSITIVE re-evaluation interval instead so it re-checks periodically.
            private const float ReevalCooldown = 0.75f;            // seconds between re-evaluations while held close

            [HarmonyPostfix]
            public static void Postfix(BoatHorizon __instance)
            {
                if (!Plugin.IsMultiplayer || !Plugin.IsHost) return;
                if (!__instance.NPCBoat) return;          // only NPC hulls; player/purchasable boats unchanged
                if (__instance.closeToPlayer) return;     // already close to the host observer, nothing to do

                var rpm = SailwindCoop.Player.RemotePlayerManager.Instance;
                if (rpm == null) return;

                var npcPos = __instance.transform.position;
                foreach (var avatar in rpm.Avatars)
                {
                    // Skip avatars whose body isn't spawned: GetLastKnownPosition returns Vector3.zero for them,
                    // and floating origin keeps the world near (0,0,0), so an unspawned avatar would falsely force
                    // an NPC near origin closeToPlayer.
                    if (avatar.GetRemoteCapsule() == null) continue;

                    if ((avatar.GetLastKnownPosition() - npcPos).sqrMagnitude <= CloseRadiusSqr)
                    {
                        __instance.closeToPlayer = true;
                        // Don't zero updateCooldown (that re-runs DistanceCheck + this foreach every frame, see
                        // ReevalCooldown note). Use a small positive interval so sim resumes promptly near this
                        // guest yet only re-evaluates a few times a second instead of every frame.
                        Traverse.Create(__instance).Field("updateCooldown").SetValue(ReevalCooldown);
                        return;
                    }
                }
            }
        }

        /// <summary>
        /// Guest NPC-ram hit report. A guest can't damage an NPC locally (BoatDamage.Impact is
        /// host-authoritative and disabled on the guest), and vanilla BoatImpactSounds.Impact early-returns for
        /// any boat that isn't GameState.lastBoat (so NPC hulls never take collision damage anyway). When the
        /// guest's shared boat rams an NPC hard, report it to the host, which applies the impact to the NPC's
        /// BoatDamage and relays the authoritative state back to all. Prefix only OBSERVES; it never changes
        /// the original return, so vanilla behavior (sounds, etc.) is unaffected.
        /// </summary>
        [HarmonyPatch(typeof(BoatImpactSounds), "Impact")]
        public static class BoatImpactSoundsGuestNPCHitPatch
        {
            [HarmonyPrefix]
            public static void Prefix(BoatImpactSounds __instance, Collision collision)
            {
                if (!Plugin.IsMultiplayer || Plugin.IsHost) return;
                if (collision == null) return;
                if (Plugin.NetworkManager == null) return;

                // Mirror vanilla BoatImpactSounds.Impact's gate: only the boat the guest is actually aboard
                // (GameState.lastBoat) reports a collision. Without this we'd observe EVERY BoatImpactSounds.Impact
                // on the guest - including other boats' impacts - and report rams the local player didn't cause.
                if (GameState.lastBoat != __instance.transform.parent) return;

                // Is the thing we hit an NPC boat hull?
                var npc = collision.collider != null ? collision.collider.GetComponentInParent<NPCBoatController>() : null;
                if (npc == null) return;

                // Match vanilla's "strong impact" gate (private serialized field, default 3) so we only report
                // real rams, not light scrapes. Falls back to 3f if the field can't be read.
                float strongThreshold = 3f;
                try { strongThreshold = Traverse.Create(__instance).Field("strongImpactTreshold").GetValue<float>(); }
                catch { /* keep default */ }

                float force = collision.relativeVelocity.magnitude;
                if (force < strongThreshold) return;

                var path = NPCBoatSyncManager.Instance?.GetHierarchyPathPublic(npc.transform);
                if (string.IsNullOrEmpty(path)) return;

                var packet = new NPCBoatHitRequestPacket { HierarchyPath = path, ImpactForce = force };

                VerboseLogger.NPCBoatSend($"HitRequest, path={path}, force={force:F2}");

                Plugin.NetworkManager.SendToAllReliable(PacketType.NPCBoatHitRequest, w =>
                    PacketSerializer.WriteNPCBoatHitRequest(w, packet));
            }
        }
    }
}
