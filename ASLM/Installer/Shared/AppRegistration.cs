// Copyright NEXTGGTECH. Apache License 2.0.

using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace ASLM.Installer
{
    /// <summary>
    /// Writes, updates and removes the ASLM entry in the Windows list of installed applications. The installer
    /// creates the entry, the patcher keeps its version current after self-updates, and the uninstaller removes it;
    /// all three compile this file, so they share one set of names and values.
    /// </summary>
    internal static class AppRegistration
    {
        // Registry layout

        /// <summary>
        /// Registry path, below HKEY_LOCAL_MACHINE, of the list of installed applications.
        /// </summary>
        public const string UninstallRegistryPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

        /// <summary>
        /// Name of the ASLM key in the list of installed applications.
        /// </summary>
        public const string KeyName = "ASLM";

        /// <summary>
        /// Full name of the ASLM key, shown to the user when it cannot be removed.
        /// </summary>
        public const string DisplayKeyPath = @"HKEY_LOCAL_MACHINE\" + UninstallRegistryPath + @"\" + KeyName;

        /// <summary>
        /// Value that holds the shared installation folder the entry belongs to.
        /// </summary>
        private const string InstallLocationValueName = "InstallLocation";

        /// <summary>
        /// Value that holds the version shown in the list of installed applications.
        /// </summary>
        private const string DisplayVersionValueName = "DisplayVersion";


        // Application facts

        /// <summary>
        /// Name shown in the list of installed applications.
        /// </summary>
        private const string DisplayName = "ASLM";

        /// <summary>
        /// Publisher shown in the list of installed applications.
        /// </summary>
        private const string Publisher = "NEXTGGTECH";

        /// <summary>
        /// Project page linked from the list of installed applications.
        /// </summary>
        private const string AboutUrl = "https://github.com/NEXTGGTECH/ASLM";

        /// <summary>
        /// File name of the Launcher in the shared folder, whose icon the entry shows.
        /// </summary>
        private const string LauncherExeName = "ASLM.exe";

        /// <summary>
        /// File name of the uninstaller in the shared folder, which the entry starts.
        /// </summary>
        public const string UninstallerExeName = "uninstall.exe";

        /// <summary>
        /// Program folders of a per-user copy that count towards the size shown in the list.
        /// </summary>
        private static readonly string[] UserProgramFolderNames = ["App", "Patcher"];


        // Entry access

        /// <summary>
        /// Opens the 64-bit list of installed applications; writing to it needs administrator rights.
        /// </summary>
        /// <returns>The opened key, or null when the list does not exist.</returns>
        [SupportedOSPlatform("windows")]
        public static RegistryKey? OpenUninstallRoot(bool writable)
        {
            using var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            return localMachine.OpenSubKey(UninstallRegistryPath, writable);
        }

        /// <summary>
        /// Creates or replaces the ASLM entry for a shared installation folder.
        /// </summary>
        /// <param name="uninstallRoot">Writable list of installed applications.</param>
        /// <param name="sharedInstallDir">Shared folder that holds the Launcher and the uninstaller.</param>
        /// <param name="version">Version shown in the list; an empty version is left out.</param>
        /// <param name="estimatedSizeKb">Size shown in the list, in kilobytes.</param>
        [SupportedOSPlatform("windows")]
        public static void Register(
            RegistryKey uninstallRoot,
            string sharedInstallDir,
            string version,
            long estimatedSizeKb)
        {
            using var entry = uninstallRoot.CreateSubKey(KeyName, writable: true);

            // Identity and location: Windows starts the uninstaller and shows the Launcher icon.
            entry.SetValue("DisplayName", DisplayName);
            entry.SetValue("Publisher", Publisher);
            entry.SetValue(InstallLocationValueName, sharedInstallDir);
            entry.SetValue("DisplayIcon", $"{Path.Combine(sharedInstallDir, LauncherExeName)},0");
            entry.SetValue("UninstallString", $"\"{Path.Combine(sharedInstallDir, UninstallerExeName)}\"");
            entry.SetValue("URLInfoAbout", AboutUrl);
            entry.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(version))
            {
                entry.SetValue(DisplayVersionValueName, version);
            }

            // The uninstaller is the only maintenance action, so Windows hides Modify and Repair.
            entry.SetValue("NoModify", 1, RegistryValueKind.DWord);
            entry.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            entry.SetValue("EstimatedSize", (int)Math.Clamp(estimatedSizeKb, 0, int.MaxValue), RegistryValueKind.DWord);
        }

        /// <summary>
        /// Sets the version shown for an existing ASLM entry.
        /// </summary>
        [SupportedOSPlatform("windows")]
        public static void SetVersion(RegistryKey uninstallRoot, string version)
        {
            using var entry = uninstallRoot.OpenSubKey(KeyName, writable: true);
            entry?.SetValue(DisplayVersionValueName, version);
        }

        /// <summary>
        /// Returns the shared folder recorded in the ASLM entry.
        /// </summary>
        /// <returns>The recorded folder, or null when there is no entry or it records no folder.</returns>
        [SupportedOSPlatform("windows")]
        public static string? ReadInstallLocation(RegistryKey uninstallRoot)
        {
            using var entry = uninstallRoot.OpenSubKey(KeyName);
            return entry?.GetValue(InstallLocationValueName) as string;
        }

        /// <summary>
        /// Deletes the ASLM entry; the caller first checks that the entry belongs to its installation.
        /// </summary>
        [SupportedOSPlatform("windows")]
        public static void Delete(RegistryKey uninstallRoot)
        {
            uninstallRoot.DeleteSubKeyTree(KeyName, throwOnMissingSubKey: false);
        }


        // Size estimate

        /// <summary>
        /// Estimates the program size shown in the list: the shared folder and the program folders of one per-user
        /// copy. User data such as models is left out, because it is not part of the program.
        /// </summary>
        /// <returns>The size in kilobytes; folders that cannot be read count as empty.</returns>
        public static long EstimateSizeKb(string sharedInstallDir, string userAppDir)
        {
            var folders = UserProgramFolderNames
                .Select(name => Path.Combine(userAppDir, name))
                .Prepend(sharedInstallDir);

            long bytes = 0;
            foreach (var folder in folders.Where(Directory.Exists))
            {
                try
                {
                    bytes += new DirectoryInfo(folder)
                        .EnumerateFiles("*", SearchOption.AllDirectories)
                        .Sum(file => file.Length);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The size is only informational; an unreadable folder must not fail the operation.
                }
            }

            return bytes / 1024;
        }
    }
}
