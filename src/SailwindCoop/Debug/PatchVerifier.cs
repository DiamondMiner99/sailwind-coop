using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace SailwindCoop.Debug
{
    /// <summary>
    /// Verifies that all expected Harmony patches were applied successfully.
    /// Logs warnings for any patches that failed to apply.
    /// </summary>
    public static class PatchVerifier
    {
        public static void Verify(Harmony harmony)
        {
            var patchedMethods = new HashSet<MethodBase>(harmony.GetPatchedMethods());
            var expectedPatches = CollectExpectedPatches();

            // Deduplicate by target method
            var uniqueTargets = new Dictionary<string, ExpectedPatch>();
            foreach (var patch in expectedPatches)
            {
                var key = $"{patch.TargetType.FullName}.{patch.MethodName}";
                if (patch.Parameters != null)
                    key += $"({string.Join(",", patch.Parameters.Select(p => p.Name))})";

                if (!uniqueTargets.ContainsKey(key))
                    uniqueTargets[key] = patch;
            }

            int missing = 0;
            foreach (var kvp in uniqueTargets)
            {
                var patch = kvp.Value;
                var method = AccessTools.Method(patch.TargetType, patch.MethodName, patch.Parameters);

                if (method == null)
                {
                    Plugin.Log.LogWarning($"[PatchVerifier] Method {patch.TargetType.Name}.{patch.MethodName} not found");
                    missing++;
                    continue;
                }

                if (!patchedMethods.Contains(method))
                {
                    Plugin.Log.LogWarning($"[PatchVerifier] Method {patch.TargetType.Name}.{patch.MethodName} exists but patch failed");
                    missing++;
                }
            }

            if (missing > 0)
            {
                Plugin.Log.LogWarning($"[PatchVerifier] {missing} patch(es) failed to apply");
            }
        }

        private static List<ExpectedPatch> CollectExpectedPatches()
        {
            var patches = new List<ExpectedPatch>();
            var assembly = Assembly.GetExecutingAssembly();

            foreach (var type in assembly.GetTypes())
            {
                // Only check types in Patches namespace
                if (type.Namespace == null || !type.Namespace.StartsWith("SailwindCoop.Patches"))
                    continue;

                CollectFromType(type, patches);

                // Check nested types (our patches are nested classes)
                foreach (var nestedType in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                {
                    CollectFromType(nestedType, patches);
                }
            }

            return patches;
        }

        private static void CollectFromType(Type type, List<ExpectedPatch> patches)
        {
            var harmonyPatchAttrs = type.GetCustomAttributes(typeof(HarmonyPatch), false);

            if (harmonyPatchAttrs.Length == 0)
                return;

            Type targetType = null;
            string methodName = null;
            Type[] parameters = null;

            foreach (HarmonyPatch attr in harmonyPatchAttrs)
            {
                // HarmonyPatch can be applied multiple times to build up the target
                if (attr.info.declaringType != null)
                    targetType = attr.info.declaringType;
                if (attr.info.methodName != null)
                    methodName = attr.info.methodName;
                if (attr.info.argumentTypes != null)
                    parameters = attr.info.argumentTypes;
            }

            if (targetType != null && methodName != null)
            {
                patches.Add(new ExpectedPatch
                {
                    TargetType = targetType,
                    MethodName = methodName,
                    Parameters = parameters
                });
            }
        }

        private struct ExpectedPatch
        {
            public Type TargetType;
            public string MethodName;
            public Type[] Parameters;
        }
    }
}
