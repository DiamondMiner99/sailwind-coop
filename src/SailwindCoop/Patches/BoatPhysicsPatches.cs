using HarmonyLib;
using UnityEngine;
using SailwindCoop.Debug;
using SailwindCoop.Player;

namespace SailwindCoop.Patches
{
    /// <summary>
    /// Disables boat physics simulation on guest.
    /// Guest receives authoritative state from host.
    /// </summary>
    public static class BoatPhysicsPatches
    {
        /// <summary>
        /// Wind forces now run on guest - we sync wind direction/speed, so forces match.
        /// Guest physics runs with velocity correction toward host's authoritative state.
        /// </summary>
        // [HarmonyPatch(typeof(BoatWind), "FixedUpdate")] - DISABLED: Let guest run physics
        // public static class BoatWindPatch { ... }

        /// <summary>
        /// Buoyancy now runs on guest - we sync FFT ocean time, so wave phase matches.
        /// Guest physics runs with velocity correction toward host's authoritative state.
        /// </summary>
        // [HarmonyPatch(typeof(Buoyancy), "FixedUpdate")] - DISABLED: Let guest run physics
        // public static class BuoyancyPatch { ... }

        // BoatMass.FixedUpdate patch REMOVED - was costing 1.41ms (10 boats × 50Hz × Traverse reflection)
        // Items are synced via ItemSyncManager, so mass calculation works naturally.
        // Remote player weight is added by UpdateMass Postfix below.

        /// <summary>
        /// After UpdateMass runs, add the weight of EVERY remote crew member standing on this boat.
        /// Uses same formula as host player: 160kg + center of mass offset, applied once per avatar.
        /// </summary>
        [HarmonyPatch(typeof(BoatMass), "UpdateMass")]
        public static class BoatMassGuestWeightPatch
        {
            [HarmonyPostfix]
            public static void Postfix(BoatMass __instance, Rigidbody ___body, float ___selfMass, float ___leverageMult)
            {
                // Only run on host in multiplayer
                if (!Plugin.IsMultiplayer || !Plugin.IsHost) return;

                var remoteManager = RemotePlayerManager.Instance;
                if (remoteManager == null || !remoteManager.HasRemotePlayer) return;

                // Skip crew weight while moored: a moored boat is held to the dock by a SpringJoint whose
                // stiffness is mass*6 (vanilla PickupableBoatMooringRope.MoorTo). Adding +160 per remote crew
                // inflates that spring enough that driving a still-moored boat heels the deck under the
                // waterline and WaveSplashZone.Overflow floods it with zero hull damage. While docked the boat
                // is held by the springs anyway, so the trim/center-of-mass contribution is cosmetic; crew
                // weight resumes the instant they cast off.
                // (Also skips while ANCHORED - AnyRopeMoored() is true when the anchor is set too; the anchor's
                // joint holds the boat, so the crew COM is likewise cosmetic there.)
                var moorRopes = __instance.GetComponentInParent<BoatMooringRopes>();
                if (moorRopes?.ropes != null && moorRopes.AnyRopeMoored()) return;

                // Per-crew weight (kg). Vanilla uses 160 for every person (host included); this is configurable
                // and lower by default so a crowd on one side of a small boat doesn't pile up enough heel to
                // flip it. HOST-ONLY value (this whole patch is host-gated above), so nothing to sync.
                float crewWeight = Plugin.CrewMemberWeightConfig != null ? Plugin.CrewMemberWeightConfig.Value : 90f;
                if (crewWeight <= 0f) return;

                // Add weight for each remote crew member currently on this boat.
                foreach (var avatar in remoteManager.Avatars)
                {
                    if (avatar.CurrentBoat != __instance.transform) continue;

                    var capsule = avatar.GetRemoteCapsule();
                    if (capsule == null) continue;

                    // Convert world position to boat-local
                    Vector3 guestLocalPos = __instance.transform.InverseTransformPoint(capsule.position);

                    // Add guest mass
                    ___body.mass += crewWeight;

                    // Center-of-mass offset (same formula as the host player in BoatMass.UpdateMass) - scales
                    // with the weight, so lowering crewWeight also lightens the heel this crew member induces.
                    float ratio = crewWeight / ___selfMass;
                    Vector3 offset = Quaternion.Euler(0f, -90f, 0f) * guestLocalPos * ratio * ___leverageMult;
                    ___body.centerOfMass += offset;
                }
            }
        }

        /// <summary>
        /// Skip kinematic state management on guest - we control it via BoatSyncManager.
        /// BoatHorizon.UpdateKinematic() sets isKinematic=false when player is close,
        /// which fights with our sync code that needs the boat kinematic.
        /// </summary>
        [HarmonyPatch(typeof(BoatHorizon), "UpdateKinematic")]
        public static class BoatHorizonKinematicPatch
        {
            [HarmonyPrefix]
            public static bool Prefix()
            {
                PatchProfiler.Begin("BoatHorizon.UpdateKinematic");

                // Only run on host or when not in multiplayer
                if (!Plugin.IsMultiplayer)
                {
                    PatchProfiler.End("BoatHorizon.UpdateKinematic");
                    return true;
                }

                PatchProfiler.End("BoatHorizon.UpdateKinematic");
                return Plugin.IsHost;
            }
        }
    }
}
