using System;
using System.IO;

namespace SailwindCoop.Networking
{
    /// <summary>
    /// (v0.3.0) Why co-op could not start, said in a class that can still load when it could not.
    ///
    /// THE BUG THIS EXISTS TO FIX. This explanation used to live on SteamLobbyManager, which declares
    /// <c>private Lobby? _currentLobby</c> - a value-type field, so Mono has to resolve
    /// Steamworks.Data.Lobby out of Facepunch.Steamworks.Win64.dll merely to lay the class out in memory.
    /// If that assembly is missing, the whole type fails to load and every member on it throws before a
    /// single line runs. So the sentence reading "Facepunch.Steamworks.Win64.dll is missing" was sitting
    /// inside the one class that cannot exist without Facepunch.Steamworks.Win64.dll. It could never be
    /// shown to the only person who needed it.
    ///
    /// NOTHING HERE MAY REFERENCE A FACEPUNCH TYPE. Not a field, not a parameter, not a local. A single
    /// Steamworks type in a signature reintroduces exactly the dependency this class exists to escape, and
    /// the failure would be silent again: the code still compiles and still looks correct.
    ///
    /// It also stops guessing. The old version pattern-matched the exception's message text to decide which
    /// file to name, which is fragile and was wrong in the case it fired most often. Looking on disk for the
    /// two files answers the question directly.
    /// </summary>
    public static class SteamInitDiagnostics
    {
        private const string ManagedDll = "Facepunch.Steamworks.Win64.dll";
        private const string NativeDll = "steam_api64.dll";

        /// <summary>Message from the last FAILED Steam init, or null if the last attempt succeeded.</summary>
        public static string LastError { get; private set; }

        /// <summary>Record a failed init. Unwraps the inner exception, because a type-load failure arrives
        /// wrapped and the outer message is the useless half.</summary>
        public static void RecordFailure(Exception ex)
        {
            if (ex == null) { LastError = "Steam init failed"; return; }
            var inner = ex.InnerException;
            LastError = inner != null
                ? ex.GetType().Name + ": " + ex.Message + " (" + inner.Message + ")"
                : ex.GetType().Name + ": " + ex.Message;
        }

        /// <summary>Record a failed init from a plain message.</summary>
        public static void RecordFailure(string message)
        {
            LastError = string.IsNullOrEmpty(message) ? "Steam init failed" : message;
        }

        public static void RecordSuccess() { LastError = null; }

        /// <summary>The folder holding SailwindCoop.dll. Uses OUR OWN assembly, which is by definition
        /// loaded, so this works in exactly the situation where touching Facepunch would not.</summary>
        private static string ModFolder()
        {
            try
            {
                string loc = typeof(SteamInitDiagnostics).Assembly.Location;
                return string.IsNullOrEmpty(loc) ? null : Path.GetDirectoryName(loc);
            }
            catch { return null; }
        }

        private static bool Exists(string dir, string file)
        {
            try { return !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, file)); }
            catch { return false; }
        }

        /// <summary>True when the managed Facepunch assembly is not beside the mod. Reported rather than
        /// inferred - the exception text is not a reliable witness.</summary>
        public static bool ManagedLibraryMissing()
        {
            string dir = ModFolder();
            // No idea where we are: do not accuse a file of being missing on no evidence.
            return dir != null && !Exists(dir, ManagedDll);
        }

        /// <summary>True when the native Steam library is in neither place it can legally live.</summary>
        public static bool NativeLibraryMissing()
        {
            string exeDir = null;
            try { exeDir = AppDomain.CurrentDomain.BaseDirectory; } catch { }
            if (Exists(exeDir, NativeDll)) return false;
            string dir = ModFolder();
            if (dir == null) return false;   // cannot tell; stay quiet
            return !Exists(dir, NativeDll);
        }

        /// <summary>
        /// A player-facing explanation of why co-op cannot start, naming the file that is actually absent
        /// and where it belongs. Safe to call at any time, including when Facepunch never loaded.
        /// </summary>
        public static string Describe()
        {
            string reason = string.IsNullOrEmpty(LastError) ? "Steam not initialized" : LastError;

            string hint;
            if (NativeLibraryMissing())
                hint = NativeDll + " is missing. It has to sit either next to Sailwind.exe or next to " +
                       "SailwindCoop.dll - re-extract the full mod zip. If you installed through a mod " +
                       "manager, that is the file it cannot place for you.";
            else if (ManagedLibraryMissing())
                hint = ManagedDll + " is missing - re-extract the full mod zip so it sits next to " +
                       "SailwindCoop.dll.";
            else
                hint = "Both mod files are where they should be, so this is Steam's end: check that Steam " +
                       "is running and that you own Sailwind on this account, then relaunch.";

            return "Co-op can't start - Steam init failed (" + reason + "). " + hint;
        }
    }
}
