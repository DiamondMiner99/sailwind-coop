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
            // (v0.2.39) Set while we have temporarily hidden GameState.currentBoat from vanilla's own
            // center-of-mass math; see the prefix below.
            private static Transform _suppressedCurrentBoat;

            /// <summary>
            /// (v0.2.39) Stop an ASHORE player from wrenching a boat's center of mass halfway to the island.
            ///
            /// Vanilla adds the local player's weight at a lever arm of
            /// <c>Refs.observerMirror.transform.localPosition</c>, guarded by "is GameState.currentBoat one of
            /// mine". That reads as a boat-LOCAL offset of a few metres, and in vanilla it always is, because
            /// PlayerDisembark nulls GameState.currentBoat and reparents the observer to the shifting world in
            /// the same breath. The two facts are one fact.
            ///
            /// This mod separates them. A joining crewmate has GameState.currentBoat seated on the shared boat
            /// unconditionally, so that the join lands them on the right hull even when the host is ashore at
            /// snapshot time - but the observer is only reparented under the boat when they are actually
            /// aboard. Stand on the dock and vanilla's guard still passes while localPosition has become a
            /// position in the shifting world: in one captured session, a lever arm of 379 metres where the
            /// formula expects single digits. The center of mass then swings metres off the keel and the hull
            /// librates, which is a boat that will not sit still and cannot be corrected into sitting still.
            ///
            /// Rather than reimplement vanilla's mass math, this hides currentBoat for the duration of the
            /// call when the observer is not in fact parented under this boat - which makes vanilla's own
            /// guard answer the question it was actually asking. Restored in the postfix, unconditionally.
            /// </summary>
            [HarmonyPrefix]
            public static void Prefix(BoatMass __instance)
            {
                _suppressedCurrentBoat = null;
                try
                {
                    var current = GameState.currentBoat;
                    if (current == null || current.parent != __instance.transform) return;

                    var observer = Refs.observerMirror != null ? Refs.observerMirror.transform : null;
                    if (observer == null) return;

                    // Aboard means the observer actually hangs off this boat. IsChildOf covers the walkCol
                    // and boatModel frames alike without caring which one embarking happened to use.
                    if (observer.IsChildOf(__instance.transform)) return;

                    _suppressedCurrentBoat = current;
                    GameState.currentBoat = null;
                }
                catch (System.Exception e)
                {
                    _suppressedCurrentBoat = null;
                    Plugin.Log.LogWarning("[BoatMass] could not check whether the player is aboard: " + e.Message);
                }
            }

            [HarmonyPostfix]
            public static void Postfix(BoatMass __instance, Rigidbody ___body, float ___selfMass, float ___leverageMult)
            {
                // Put it back FIRST and unconditionally - every early return below must not leak the
                // suppression into the rest of the frame, where currentBoat means "the boat I am on".
                if (_suppressedCurrentBoat != null)
                {
                    GameState.currentBoat = _suppressedCurrentBoat;
                    _suppressedCurrentBoat = null;
                }

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
    }
}
