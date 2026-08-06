using System.Collections.Generic;
using Steamworks;
using UnityEngine;

namespace SailwindCoop.UI
{
    /// <summary>
    /// (v0.3.0) Steam profile pictures as Unity textures, fetched once per player and kept.
    ///
    /// Steam hands avatars over asynchronously, and IMGUI runs many times a second - so the one thing this
    /// class must never do is start work from a draw call. <see cref="Get"/> is therefore a pure lookup: it
    /// returns the texture if we have it, null if we do not, and remembers that it has already asked so a
    /// friend whose avatar is still in flight (or who has none) costs one dictionary probe per frame rather
    /// than a fresh request per frame.
    ///
    /// THREADING. The await resumes on Unity's synchronization context - the same reason the lobby manager
    /// can await a join and then touch the game - so the Texture2D is built on the main thread, which is the
    /// only thread that may build one.
    ///
    /// ORIENTATION. Steam's buffer runs top-down; Unity's textures run bottom-up. Copying the rows straight
    /// across produces an upside-down face, which looks like a bug in the panel rather than in the copy, so
    /// the rows are reversed on the way in.
    /// </summary>
    public static class SteamAvatarCache
    {
        private static readonly Dictionary<ulong, Texture2D> _textures = new Dictionary<ulong, Texture2D>();
        private static readonly HashSet<ulong> _requested = new HashSet<ulong>();

        /// <summary>The avatar for this player, or null if it is not here (yet, or at all). Safe to call
        /// every frame from OnGUI; never blocks and never starts more than one fetch per player.</summary>
        public static Texture2D Get(SteamId id)
        {
            Texture2D tex;
            if (_textures.TryGetValue(id.Value, out tex)) return tex;
            if (_requested.Add(id.Value)) Fetch(id);
            return null;
        }

        private static async void Fetch(SteamId id)
        {
            try
            {
                var img = await new Friend(id).GetMediumAvatarAsync();
                if (!img.HasValue) return;
                var tex = ToTexture(img.Value);
                if (tex != null) _textures[id.Value] = tex;
            }
            catch (System.Exception e)
            {
                // A missing avatar is cosmetic. Leave the id marked as requested so a friend Steam has no
                // picture for does not generate a request on every sweep for the rest of the session.
                Plugin.Log.LogWarning("[Avatar] could not load avatar for " + id + ": " + e.Message);
            }
        }

        private static Texture2D ToTexture(Steamworks.Data.Image img)
        {
            int w = (int)img.Width, h = (int)img.Height;
            if (w <= 0 || h <= 0 || img.Data == null || img.Data.Length < w * h * 4) return null;

            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.hideFlags = HideFlags.HideAndDontSave;   // never let a UI texture leak into a scene or a save
            tex.wrapMode = TextureWrapMode.Clamp;

            var flipped = new byte[w * h * 4];
            int stride = w * 4;
            for (int row = 0; row < h; row++)
                System.Array.Copy(img.Data, row * stride, flipped, (h - 1 - row) * stride, stride);

            tex.LoadRawTextureData(flipped);
            tex.Apply(false, false);
            return tex;
        }
    }
}
