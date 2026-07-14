using System.Text;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// (v0.2.32) Relative-transform-path keys for wire messages. Mod parity (CompatRegistry gate)
    /// guarantees identical prefab hierarchies on every peer, so a path from the boat root is a
    /// stable cross-machine key for prefab-baked children (trapdoors, towing cleats). Unity allows
    /// duplicate sibling names, so each segment carries an occurrence index among SAME-NAMED
    /// siblings ("name" for the first, "name~2" for the second, ...). Prefab instantiation order is
    /// deterministic, so the occurrence index matches across machines for baked structure. NOT safe
    /// for runtime-rebuilt hierarchies (sails) - do not use it for anything a customization rebuild
    /// can reorder.
    /// </summary>
    public static class SyncPathUtil
    {
        public static string GetRelativePath(Transform root, Transform target)
        {
            if (root == null || target == null) return null;
            // A boat root is not addressable as its own child; callers must not mint an
            // unresolvable empty key (FindByRelativePath(root, "") returns null, not root).
            if (target == root) return null;
            var sb = new StringBuilder();
            var t = target;
            // Explicit flag instead of an sb.Length==0 proxy: Unity permits empty GameObject
            // names, and an empty first segment would leave sb.Length==0 after insertion,
            // silently dropping the separator and serializing root/hold/<empty> as "hold" -
            // which then resolves to the WRONG transform on the receiving end.
            bool first = true;
            while (t != null && t != root)
            {
                string seg = t.name;
                // The wire format cannot escape '~' (occurrence marker) or '/' (segment
                // separator); never mint a key that FindByRelativePath would mis-parse.
                // Sailwind's prefabs use Unity's " (1)" duplicate convention, so this never
                // fires in practice.
                if (seg.IndexOf('~') >= 0 || seg.IndexOf('/') >= 0) return null;
                int occ = OccurrenceAmongSameNamedSiblings(t);
                if (occ > 1) seg = seg + "~" + occ;
                sb.Insert(0, first ? seg : seg + "/");
                first = false;
                t = t.parent;
            }
            if (t != root) return null; // target is not under root
            return sb.ToString();
        }

        public static Transform FindByRelativePath(Transform root, string path)
        {
            if (root == null || string.IsNullOrEmpty(path)) return null;
            var t = root;
            foreach (var rawSeg in path.Split('/'))
            {
                string name = rawSeg;
                int occ = 1;
                int tilde = rawSeg.LastIndexOf('~');
                if (tilde > 0 && int.TryParse(rawSeg.Substring(tilde + 1), out int parsed))
                {
                    name = rawSeg.Substring(0, tilde);
                    occ = parsed;
                }
                Transform next = null;
                int seen = 0;
                for (int i = 0; i < t.childCount; i++)
                {
                    var c = t.GetChild(i);
                    if (c.name != name) continue;
                    seen++;
                    if (seen == occ) { next = c; break; }
                }
                if (next == null) return null;
                t = next;
            }
            return t;
        }

        private static int OccurrenceAmongSameNamedSiblings(Transform t)
        {
            var parent = t.parent;
            if (parent == null) return 1;
            int occ = 0;
            for (int i = 0; i < parent.childCount; i++)
            {
                var c = parent.GetChild(i);
                if (c.name != t.name) continue;
                occ++;
                if (c == t) return occ;
            }
            return 1;
        }
    }
}
