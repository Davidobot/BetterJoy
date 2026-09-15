using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace BetterJoyForCemu {
    // Program Files (where the app is installed) isn't writable without elevation, so all
    // runtime-generated state lives in AppData/ProgramData instead. Two possible locations:
    //  - Per-user (%AppData%\BetterJoy) - the default for the GUI, isolated per Windows account.
    //  - Shared (%ProgramData%\BetterJoy) - used by a Windows Service (which runs as SYSTEM, so
    //    its own %AppData% is a different, inaccessible profile entirely) and, opt-in, by the
    //    GUI too (see EnableServiceMode) so that settings changed in the GUI actually reach a
    //    running service instead of silently landing in a config file the service never reads.
    internal static class AppPaths {
        private const string ServiceModeFlagFileName = "service-mode.enabled";

        private static string dataDir;
        public static string DataDir {
            get {
                if (dataDir == null)
                    throw new InvalidOperationException("AppPaths.Initialize must run before DataDir is used.");
                return dataDir;
            }
        }

        private static string PerUserDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BetterJoy2");
        private static string SharedDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BetterJoy2");
        // Pre-v7.3.0 installs used "BetterJoy" instead of "BetterJoy2" for this folder name.
        private static string LegacyPerUserDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BetterJoy");
        private static string LegacySharedDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BetterJoy");

        // Must run once, before anything else touches DataDir (EntryPoint.Main does this first,
        // before even redirecting the config file). isServiceProcess is passed in explicitly
        // rather than detected here - by the time a Windows Service is running, its own %AppData%
        // resolves to SYSTEM's profile rather than the interactive user's, so "check for a flag
        // file under %AppData%" wouldn't find anything meaningful for it anyway; a service always
        // uses the shared location unconditionally.
        public static void Initialize(bool isServiceProcess) {
            MigrateLegacyDir(LegacyPerUserDir, PerUserDir);
            MigrateLegacyDir(LegacySharedDir, SharedDir);

            bool useShared = isServiceProcess || File.Exists(Path.Combine(PerUserDir, ServiceModeFlagFileName));
            dataDir = useShared ? SharedDir : PerUserDir;
            Directory.CreateDirectory(dataDir);

            if (useShared)
                EnsureSharedDirWritableByUsers(dataDir);
        }

        // Moves a pre-v7.3.0 "BetterJoy" data folder to its "BetterJoy2" replacement in place, so
        // an upgrading user's settings/profiles/calibration/logs aren't left behind under the old
        // name. Best-effort: if this fails (in use, permissions, whatever), just leave the legacy
        // folder where it is and let the caller create a fresh one under the new name - not worth
        // blocking startup over.
        private static void MigrateLegacyDir(string legacyDir, string newDir) {
            try {
                if (Directory.Exists(legacyDir) && !Directory.Exists(newDir))
                    Directory.Move(legacyDir, newDir);
            } catch {
            }
        }

        public static bool ServiceModeEnabled => File.Exists(Path.Combine(PerUserDir, ServiceModeFlagFileName));

        // Called from the GUI (see MainForm) when the user turns on "sync configuration with the
        // Windows Service" - copies whatever's already in the per-user location into the shared
        // one and drops the flag file, so this and every future launch (GUI or service) resolves
        // DataDir to the shared location. Doesn't touch the *current* process's already-resolved
        // DataDir - takes effect next launch.
        public static void EnableServiceMode() {
            Directory.CreateDirectory(SharedDir);
            EnsureSharedDirWritableByUsers(SharedDir);

            if (Directory.Exists(PerUserDir)) {
                foreach (string file in Directory.GetFiles(PerUserDir)) {
                    if (Path.GetFileName(file) == ServiceModeFlagFileName)
                        continue;

                    // Overwrite, don't skip, anything already at the destination - this is an
                    // explicit, confirmed user action ("push my current settings to be the
                    // shared ones"), and the service independently seeds the shared directory
                    // with its own defaults on its own first run regardless of whether this was
                    // ever clicked. A skip-if-exists copy here meant clicking this after the
                    // service had already run once silently copied nothing at all.
                    string dest = Path.Combine(SharedDir, Path.GetFileName(file));
                    File.Copy(file, dest, true);
                }
            }

            File.WriteAllText(Path.Combine(PerUserDir, ServiceModeFlagFileName), "");
        }

        // %ProgramData% is normally writable by regular users out of the box, but whichever
        // account (SYSTEM via the service, or a regular user via the GUI) happens to create a
        // given file first can end up owning it with tighter permissions than the other account
        // needs. Grant BUILTIN\Users modify rights on the folder itself (inherited by anything
        // created under it) so both sides can always read and write here regardless of creation
        // order. Best-effort: if this fails (e.g. insufficient privilege), whichever account does
        // have access still works fine, and a genuine mismatch surfaces as a clear exception on
        // save rather than failing silently.
        private static void EnsureSharedDirWritableByUsers(string dir) {
            try {
                var dirInfo = new DirectoryInfo(dir);
                DirectorySecurity security = dirInfo.GetAccessControl();
                var usersSid = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
                security.AddAccessRule(new FileSystemAccessRule(
                    usersSid,
                    FileSystemRights.Modify | FileSystemRights.Synchronize,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
                dirInfo.SetAccessControl(security);
            } catch {
                // See comment above - deliberately swallowed.
            }
        }
    }
}
