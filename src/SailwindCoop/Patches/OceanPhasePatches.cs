using Crest;
using HarmonyLib;
using UnityEngine;

namespace SailwindCoop.Patches
{
    /// <summary>
    /// SHARED SEA. Makes the swell the same surface on every machine in a session.
    ///
    /// Crest evaluates each Gerstner component as sin(k * (dot(dir, scenePos) + c*t) + phase). Sailwind's
    /// floating origin keeps that continuous across a world shift by adding k * dot(-shift, dir) to every
    /// phase (ShapeGerstnerBatched.SetOrigin, called from FloatingOriginManager.MoveOriginOcean). While a
    /// wave's direction never changes, the accumulated phase is seedPhase - k * dot(totalOffset, dir), a pure
    /// function of where the machine is, and two machines agree.
    ///
    /// The two inertia waves do change direction: OceanUpdaterCrest.DCTInertiaNewCycle hands the wave that is
    /// fading in a new _windDirectionAngle every cycle and leaves its phases alone. From then on its phases
    /// carry every shift made under every earlier direction. That is each machine's own voyage: a host that
    /// sailed there, and a guest whose world loaded somewhere else and jumped. Same spectrum, same clock,
    /// crests in different places. The vertical boat corrector then pulled a guest's hull toward a height
    /// that was right on the host's water and metres under the guest's (the "half submerged for one player"
    /// reports, and the standing vertical error measured in the 2026-08-06 playtest).
    ///
    /// The fix writes the phase as that pure function whenever doing so is invisible or already implied:
    /// after every origin shift (identical to what SetOrigin just did for a wave whose direction held),
    /// when a wave starts fading in (weight 0), and when the mod writes the host's direction on a guest.
    /// Once per session every wave is rewritten, which is one visible change of the swell as a lobby opens.
    /// Switch: Coop.SharedSea.
    /// </summary>
    public static class OceanPhasePatches
    {
        private const double TwoPi = System.Math.PI * 2.0;

        private static bool Active =>
            Plugin.IsMultiplayer && (Plugin.SharedSeaConfig == null || Plugin.SharedSeaConfig.Value);

        // The offset the OCEAN has been shifted by. FloatingOriginManager.outCurrentOffset is only updated a
        // physics step or two after MoveOriginOcean runs, so it is briefly one shift behind the phases.
        private static int _trackedFomId;
        private static Vector3 _trackedOffset;

        private static int _canonicalOceanId;
        private static float _nextAttempt;

        public static void ResetSession()
        {
            _canonicalOceanId = 0;
            _nextAttempt = 0f;
        }

        /// <summary>The floating-origin offset the wave phases currently correspond to.</summary>
        public static Vector3 OceanOffset()
        {
            var fom = FloatingOriginManager.instance;
            if (fom == null) return Vector3.zero;
            if (_trackedFomId == fom.GetInstanceID()) return _trackedOffset;
            return new Vector3(fom.outCurrentOffset.x, 0f, fom.outCurrentOffset.z);
        }

        /// <summary>Once per ocean per session: rewrite every wave. Cheap to call every frame.</summary>
        public static void EnsureCanonical()
        {
            if (!Active) return;
            var ocean = OceanRenderer.Instance;
            if (ocean == null || FloatingOriginManager.instance == null) return;

            int id = ocean.GetInstanceID();
            if (id == _canonicalOceanId || Time.unscaledTime < _nextAttempt) return;
            _nextAttempt = Time.unscaledTime + 1f;

            if (CanonicalizeAll(OceanOffset()))
            {
                _canonicalOceanId = id;
                Plugin.Log.LogInfo($"[Ocean] Wave phases aligned to real position for this session (offset {OceanOffset()})");
            }
        }

        public static bool CanonicalizeAll(Vector3 offset)
        {
            bool all = true;
            foreach (var w in Object.FindObjectsOfType<ShapeGerstnerBatched>())
                all &= Canonicalize(w, offset);
            return all;
        }

        /// <summary>
        /// Write one wave's phases as seedPhase - k * dot(offset, direction). Returns false only when the
        /// wave has no phases or wavelengths yet (vanilla has not initialized it), so the caller retries.
        /// </summary>
        public static bool Canonicalize(ShapeGerstnerBatched w, Vector3 offset)
        {
            if (w == null) return true;
            try
            {
                var ph = w._phases;
                var wl = w._wavelengths;
                var ang = w._angleDegs;
                int cpo = w._componentsPerOctave;
                if (ph == null || wl == null || ang == null) return false;
                if (cpo <= 0 || ph.Length != cpo * 14 || wl.Length != ph.Length || ang.Length != ph.Length) return true;

                var saved = Random.state;
                Random.InitState(w._randomSeed); // the same draws, in the same order, as ShapeGerstnerBatched.InitPhases
                try
                {
                    for (int i = 0; i < 14; i++)
                    {
                        for (int j = 0; j < cpo; j++)
                        {
                            int n = i * cpo + j;
                            double seed = (float)System.Math.PI * 2f * (((float)j + Random.value) / (float)cpo);
                            if (wl[n] <= 0f) continue;
                            double a = (w._windDirectionAngle + ang[n]) * Mathf.Deg2Rad;
                            double p = seed - (TwoPi / wl[n]) * (offset.x * System.Math.Cos(a) + offset.z * System.Math.Sin(a));
                            ph[n] = (float)(p - TwoPi * System.Math.Floor(p / TwoPi));
                        }
                    }
                }
                finally
                {
                    Random.state = saved;
                }
                return true;
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("[Ocean] could not align wave phases: " + e.Message);
                return true;
            }
        }

        [HarmonyPatch(typeof(FloatingOriginManager), "MoveOriginOcean")]
        public static class MoveOriginOceanPatch
        {
            [HarmonyPostfix]
            public static void Postfix(FloatingOriginManager __instance, Vector3 newOrigin)
            {
                // NewShift calls this with -shiftVector and adds shiftVector to outCurrentOffset afterwards.
                int id = __instance.GetInstanceID();
                Vector3 before = _trackedFomId == id
                    ? _trackedOffset
                    : new Vector3(__instance.outCurrentOffset.x, 0f, __instance.outCurrentOffset.z);
                _trackedOffset = before - new Vector3(newOrigin.x, 0f, newOrigin.z);
                _trackedFomId = id;

                if (Active) CanonicalizeAll(_trackedOffset);
            }
        }

        [HarmonyPatch(typeof(OceanUpdaterCrest), "DCTInertiaNewCycle")]
        public static class NewCyclePatch
        {
            private static readonly AccessTools.FieldRef<OceanUpdaterCrest, ShapeGerstnerBatched[]> Waves =
                AccessTools.FieldRefAccess<OceanUpdaterCrest, ShapeGerstnerBatched[]>("inertiaWaves");
            private static readonly AccessTools.FieldRef<OceanUpdaterCrest, int> Up =
                AccessTools.FieldRefAccess<OceanUpdaterCrest, int>("wavesUp");

            [HarmonyPostfix]
            public static void Postfix(OceanUpdaterCrest __instance)
            {
                if (!Active) return;
                // The wave that just started fading in got a new direction. Its weight is 0 this frame.
                var waves = Waves(__instance);
                int up = Up(__instance);
                if (waves != null && up >= 0 && up < waves.Length) Canonicalize(waves[up], OceanOffset());
            }
        }
    }
}
