using System;
using System.Collections.Generic;
using SailwindPlayerModel;
using Steamworks;

namespace SailwindCoop.Player
{
    /// <summary>
    /// (v0.3.0) Who looks like what, keyed by SteamId. The single source of truth for appearance on this
    /// machine: the network layer writes into it, the avatar builders read from it.
    ///
    /// It is deliberately a REGISTRY rather than a field on the avatar, because the two events - "we learned
    /// what this player looks like" and "we managed to build a body for them" - arrive in either order and
    /// neither is guaranteed. A body cannot be built at all until a shopkeeper has loaded to clone, which
    /// out at sea may be never; meanwhile the appearance packet arrives on join. Keeping the data here and
    /// having the (lazily retrying) body builder read it means no re-send is ever needed and no ordering
    /// has to be enforced.
    ///
    /// An unknown player is NOT an error: they resolve to a deterministic look derived from their SteamId,
    /// so a peer on an older build, or one whose packet has not arrived yet, still shows up as a plausible
    /// individual rather than a clone of everyone else.
    /// </summary>
    public static class AppearanceRegistry
    {
        private static readonly Dictionary<ulong, PlayerAppearance> _byPlayer = new Dictionary<ulong, PlayerAppearance>();

        public static void Set(ulong steamId, PlayerAppearance appearance)
        {
            if (steamId == 0UL) return;
            _byPlayer[steamId] = appearance;
        }

        public static bool Has(ulong steamId)
        {
            return steamId != 0UL && _byPlayer.ContainsKey(steamId);
        }

        /// <summary>Never fails: falls back to the deterministic per-id look.</summary>
        public static PlayerAppearance For(ulong steamId)
        {
            PlayerAppearance a;
            if (steamId != 0UL && _byPlayer.TryGetValue(steamId, out a)) return a;
            return PlayerAppearance.DeterministicFor(steamId);
        }

        /// <summary>Drop everything on session teardown - appearances are per-session knowledge, and a
        /// stale entry would make a rejoining player wear the look of whoever last held that id slot.</summary>
        public static void Clear() { _byPlayer.Clear(); }

        /// <summary>Every id we hold an EXPLICIT appearance for (not the deterministic fallback). Used by
        /// the host to replay the roster to a joiner.</summary>
        public static List<ulong> KnownPlayers()
        {
            return new List<ulong>(_byPlayer.Keys);
        }
    }

    /// <summary>
    /// (v0.3.0) Wire layer for <see cref="PlayerAppearance"/> - packet 220.
    ///
    /// SHAPE: authorId (u64), slotCount (byte), slotCount variant bytes. Author-keyed rather than
    /// sender-keyed because the host RELAYS other players' appearances, so "who sent this" and "who this
    /// describes" are genuinely different questions on a guest's receive path.
    ///
    /// TRUST: none. Two independent guards, because either alone would be a hole.
    ///   - The host refuses to relay a packet whose author is not its sender. Without this any guest could
    ///     restyle any other player, since the author id is just a number in the body. This is a real gap
    ///     in the existing author-keyed packets, not a hypothetical one.
    ///   - Every receiver clamps indices against its OWN live part lists at apply time, so even an
    ///     appearance that got through cannot index out of range - the worst it can do is look silly.
    /// The host relays the SANITIZED struct it parsed, never the bytes it received, so a malformed body
    /// cannot be laundered through the host to the rest of the crew.
    /// </summary>
    public static class AppearanceSync
    {
        /// <summary>Tell the crew what we look like. Safe to call when not in a session (no-op).</summary>
        public static void BroadcastLocal()
        {
            try
            {
                if (!Plugin.IsMultiplayer || Plugin.NetworkManager == null) return;
                ulong me = SteamClient.SteamId.Value;
                var mine = Plugin.LocalAppearance;
                AppearanceRegistry.Set(me, mine);   // be authoritative about ourselves locally too
                Plugin.NetworkManager.SendToAllReliable(Networking.Packets.PacketType.PlayerAppearance,
                    w => Write(w, me, mine));
            }
            catch (Exception e) { Plugin.Log.LogWarning("[Appearance] Broadcast failed: " + e.Message); }
        }

        /// <summary>
        /// HOST ONLY: replay every known crew appearance to a joining guest, so a late joiner does not see
        /// a boat full of fallback faces. Mirrors the chart-session replay in the join-state sequence.
        /// </summary>
        public static void SendRosterTo(SteamId target)
        {
            try
            {
                if (!Plugin.IsHost || Plugin.NetworkManager == null) return;
                int sent = 0;
                foreach (var id in AppearanceRegistry.KnownPlayers())
                {
                    if (id == target.Value) continue;   // they know their own
                    var a = AppearanceRegistry.For(id);
                    Plugin.NetworkManager.SendReliable(target, Networking.Packets.PacketType.PlayerAppearance,
                        w => Write(w, id, a));
                    sent++;
                }
                Plugin.Log.LogInfo($"[Appearance] Replayed {sent} crew appearance(s) to the joiner.");
            }
            catch (Exception e) { Plugin.Log.LogWarning("[Appearance] Roster replay failed: " + e.Message); }
        }

        private static void Write(System.IO.BinaryWriter w, ulong authorId, PlayerAppearance a)
        {
            w.Write(authorId);
            byte n = (byte)PlayerAppearance.SlotCount;
            w.Write(n);
            for (int i = 0; i < n; i++) w.Write(a[i]);
            // Colors follow the parts, same shape: a count, then that many bytes. A reader that predates
            // colors stops after the parts and never sees them.
            byte m = (byte)PlayerAppearance.ColorSlotCount;
            w.Write(m);
            for (int i = 0; i < m; i++) w.Write(a.GetColor(i));
        }

        /// <summary>Receive path. Never throws; a bad appearance packet must not disturb a session.</summary>
        public static void OnReceived(SteamId sender, System.IO.BinaryReader reader)
        {
            try
            {
                ulong authorId = reader.ReadUInt64();
                int n = reader.ReadByte();
                var a = PlayerAppearance.Default();
                for (int i = 0; i < n; i++)
                {
                    byte v = reader.ReadByte();
                    // Slots beyond what THIS build knows about are read (to keep the stream aligned) and
                    // discarded. A peer on a newer build with more slots is not an error.
                    if (i < PlayerAppearance.SlotCount) a[i] = v;
                }
                // Colors, if the sender's build has them. Absent on an older peer, which is not an error.
                if (reader.BaseStream.Position < reader.BaseStream.Length)
                {
                    int m = reader.ReadByte();
                    for (int i = 0; i < m; i++)
                    {
                        byte v = reader.ReadByte();
                        if (i < PlayerAppearance.ColorSlotCount) a.SetColor(i, v);
                    }
                }

                if (authorId == SteamClient.SteamId.Value) return;   // relay echo of our own

                if (Plugin.IsHost)
                {
                    // AUTHOR SPOOFING GUARD. The author id is attacker-controlled data; without this a
                    // guest could dress any other crew member however it liked, and the host would
                    // faithfully relay it to everyone.
                    if (authorId != sender.Value)
                    {
                        Plugin.Log.LogWarning($"[Appearance] Dropped an appearance from {sender.Value} " +
                            $"claiming to be {authorId} (author does not match sender).");
                        return;
                    }
                    // Relay what we PARSED, not what we received.
                    Plugin.NetworkManager.SendToAllExcept(sender, Networking.Packets.PacketType.PlayerAppearance,
                        w => Write(w, authorId, a));
                }
                else
                {
                    // GUEST SIDE. The host's author==sender check protects the relay, but a guest must not
                    // simply trust whatever reaches it either. In this star topology a guest peers ONLY
                    // with the host, so an appearance arriving from anyone else did not come through the
                    // host's validation and has no business being applied.
                    if (sender.Value != Plugin.LobbyManager.HostSteamId.Value)
                    {
                        Plugin.Log.LogWarning($"[Appearance] Ignored an appearance from non-host peer {sender.Value}.");
                        return;
                    }
                }

                AppearanceRegistry.Set(authorId, a);
                // The look is baked when a body clone is activated, so an already-built avatar has to be
                // rebuilt to show a change. Cheap and rare: this fires on join and when someone edits.
                Plugin.RemotePlayerManager?.RefreshAppearance(authorId);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[Appearance] Could not read an appearance packet: " + e.Message);
            }
        }
    }
}
