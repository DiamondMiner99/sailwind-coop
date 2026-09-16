using System;
using Steamworks;

namespace SailwindCoop
{
    /// <summary>
    /// A read-only view of the co-op session for other mods, in plain types only. Read it by reflection
    /// (<c>Type.GetType("SailwindCoop.CoopStatus, SailwindCoop")</c>) so your mod does not need co-op
    /// installed. The members here keep their names and meanings across co-op versions; nothing else in this
    /// assembly makes that promise.
    ///
    /// No Steam library types appear in any signature, and every read catches, so this is safe to call even
    /// when Steam failed to start and the lobby manager cannot load. Each read may cost Steam interop, so
    /// poll it every few seconds, not every frame.
    /// </summary>
    public static class CoopStatus
    {
        /// <summary>Bumped only if a member below changes meaning or goes away.</summary>
        public const int ApiVersion = 1;

        /// <summary>True while in a co-op session, as captain or crew.</summary>
        public static bool InSession { get { return Read(() => Plugin.SteamReady && Plugin.IsMultiplayer, false); } }

        /// <summary>True while in a session as its captain (the host).</summary>
        public static bool IsCaptain { get { return Read(() => Plugin.SteamReady && Plugin.IsMultiplayer && Plugin.IsHost, false); } }

        /// <summary>People aboard including this player, or 0 when not in a session.</summary>
        public static int CrewCount { get { return Read(() => Plugin.SteamReady && Plugin.IsMultiplayer ? Plugin.LobbyManager.GetMemberCount() : 0, 0); } }

        /// <summary>The most people a session can hold.</summary>
        public static int MaxCrew { get { return Read(() => Networking.SteamLobbyManager.MaxPlayers, 8); } }

        /// <summary>The captain's Steam name, or "" when not in a session or not known yet.</summary>
        public static string CaptainName { get { return Read(ReadCaptainName, ""); } }

        private static string ReadCaptainName()
        {
            if (!Plugin.SteamReady || !Plugin.IsMultiplayer) return "";
            string name = Plugin.IsHost ? SteamClient.Name : new Friend(Plugin.LobbyManager.HostSteamId).Name;
            return string.IsNullOrEmpty(name) || name == "[unknown]" ? "" : name;
        }

        private static T Read<T>(Func<T> read, T fallback)
        {
            try { return read(); }
            catch { return fallback; }
        }
    }
}
