using HarmonyLib;
using PsychoticLab;
using UnityEngine;

namespace SailwindCoop.Patches
{
    /// <summary>
    /// (v0.2.39) Makes vanilla's Synty character part activation total instead of assuming every entry in
    /// a part group is a single skinned mesh.
    ///
    /// THE BUG, in vanilla:
    ///
    ///     private void ActivateItem(GameObject go)
    ///     {
    ///         go.SetActive(true);
    ///         enabledObjects.Add(go);
    ///         go.GetComponent&lt;SkinnedMeshRenderer&gt;().material = mat;   // unguarded
    ///     }
    ///
    /// Sailwind's rig has one group where that assumption is false. All_02_Head_Attachment contains two
    /// raw Synty import FOLDERS rather than meshes - "Hair" (empty) and "Helmet" (13 helmet-crest meshes,
    /// 11 of them left self-active). Selecting the second one switches on eleven stacked helmets and THEN
    /// dereferences a SkinnedMeshRenderer the folder does not have, so the NRE lands after the meshes are
    /// already visible and aborts UpdateModel half-built. The result on screen is a magenta thicket of
    /// overlapping helmet crests.
    ///
    /// Vanilla never hit it because every shopkeeper in the game ships with that field serialized to -1.
    /// The co-op character screen is the first code to ever write a non-negative value there.
    ///
    /// Two changes, both strictly wider than vanilla and safe for singleplayer:
    ///   - Null-safe: a group entry with no renderer of its own no longer throws, so one odd entry cannot
    ///     abandon the rest of the model half-dressed.
    ///   - Dress the whole SUBTREE, and every material slot. Vanilla only ever assigns the root node's own
    ///     renderer and only slot 0, which is why the helmet meshes nested one level down were the only
    ///     renderers on the avatar never to receive the region material.
    /// </summary>
    [HarmonyPatch(typeof(CharacterCustomizer), "ActivateItem")]
    public static class CharacterCustomizerActivateItemPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(CharacterCustomizer __instance, GameObject go)
        {
            if (go == null) return false;   // nothing to activate; vanilla would NRE on the SetActive

            try
            {
                go.SetActive(true);

                var enabled = Traverse.Create(__instance).Field("enabledObjects")
                    .GetValue<System.Collections.Generic.List<GameObject>>();
                if (enabled != null) enabled.Add(go);

                var mat = Traverse.Create(__instance).Field("mat").GetValue<Material>();
                if (mat != null)
                {
                    // includeInactive: true - a variant can legitimately hold child meshes that vanilla
                    // leaves switched off, and dressing them now costs nothing while leaving them undressed
                    // shows as magenta the moment anything else enables one.
                    var renderers = go.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                    for (int i = 0; i < renderers.Length; i++)
                    {
                        var r = renderers[i];
                        if (r == null) continue;
                        var slots = r.sharedMaterials;
                        if (slots == null || slots.Length == 0) { r.sharedMaterial = mat; continue; }
                        for (int s = 0; s < slots.Length; s++) slots[s] = mat;
                        r.sharedMaterials = slots;
                    }
                }
            }
            catch (System.Exception e)
            {
                // A cosmetic part must never take down a character build.
                Plugin.Log.LogWarning("[Appearance] ActivateItem guard: " + e.Message);
            }

            return false;   // fully replaces vanilla
        }
    }
}
