// Copyright NEXTGGTECH. Apache License 2.0.

using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace ASLM.Installer.Uninstaller
{
    // Uninstaller service

    /// <summary>
    /// Removes an ASLM installation made by the installer: the shared folder with the Launcher, the per-user
    /// application folders, their shortcuts, and the entry in the list of installed applications.
    /// </summary>
    /// <remarks>
    /// Only what can be attributed to the installation is removed: the shared folder must hold the install manifest,
    /// a per-user folder of another user must belong to the Launcher of that installation, and a shortcut must point
    /// at that Launcher. This part finds the installation and builds the removal plan; the process check and the
    /// removal itself live in the other parts of the class.
    /// </remarks>
    public sealed partial class UninstallerService
    {
        // Installation layout

        /// <summary>
        /// Product name used for the per-user application folder and recorded in the install manifest.
        /// </summary>
        private const string AppName = "ASLM";

        /// <summary>
        /// File name of the install manifest that the installer writes into the shared folder.
        /// </summary>
        private const string ManifestFileName = "install-manifest.json";

        /// <summary>
        /// File name of the Launcher executable in the shared folder.
        /// </summary>
        private const string LauncherExeName = "ASLM.exe";

        /// <summary>
        /// File name of the payload archive from which the Launcher sets up new users.
        /// </summary>
        private const string BaseArchiveFileName = "aslm-base.zip";

        /// <summary>
        /// Name under which the patcher writes a new payload archive before it replaces the old one; a leftover
        /// exists only when an update stopped halfway.
        /// </summary>
        private const string BaseArchiveTempFileName = "aslm-base.zip.tmp";

        /// <summary>
        /// File name of the diagnostic log that the Launcher writes next to itself.
        /// </summary>
        private const string LauncherLogFileName = "Launcher.log";

        /// <summary>
        /// File name of the reference in a per-user folder that names the Launcher using that folder.
        /// </summary>
        private const string LauncherRefFileName = "launcher-ref.json";

        /// <summary>
        /// Name of the application folder; a folder that holds it next to the Launcher is a portable copy.
        /// </summary>
        private const string AppFolderName = "App";

        /// <summary>
        /// File name of the shortcuts that the installer creates.
        /// </summary>
        private const string ShortcutFileName = "ASLM.lnk";

        /// <summary>
        /// Files that the installer and the Launcher put into the shared folder, in deletion order. The install
        /// manifest comes last because it is what identifies the folder as an installation.
        /// </summary>
        private static readonly string[] SharedFileNames =
        [
            LauncherExeName,
            AppRegistration.UninstallerExeName,
            BaseArchiveFileName,
            BaseArchiveTempFileName,
            LauncherLogFileName,
            ManifestFileName
        ];


        // Preferences and temporary files

        /// <summary>
        /// Publisher folder under the local application data in which ASLM versions that stored settings through MAUI
        /// preferences kept them; the name is the placeholder publisher of the Windows application manifest template.
        /// </summary>
        private const string LegacyPreferencesPublisherFolderName = "User Name";

        /// <summary>
        /// Folder of the MAUI preferences inside the publisher folder, named after the ASLM application id.
        /// </summary>
        private const string LegacyPreferencesFolderName = "com.nextggtech.aslm";

        /// <summary>
        /// Temporary folder in which the app downloads catalog content.
        /// </summary>
        private const string DownloadsTempFolderName = "ASLM_Downloads";

        /// <summary>
        /// Prefix of the temporary copies from which the Launcher runs the patcher; a GUID in the "N" format follows.
        /// </summary>
        private const string PatcherCopyPrefix = "Patcher_";

        /// <summary>
        /// Prefix of the temporary folders into which the app extracts module sources; a GUID in the "D" format
        /// follows.
        /// </summary>
        private const string ModuleSourceTempPrefix = "ASLM_ModuleSrc_";

        /// <summary>
        /// Prefix of the temporary folders in which the app installs modules; a GUID in the "D" format follows.
        /// </summary>
        private const string ModuleInstallTempPrefix = "ASLM_Module_Install_";


        // User profiles

        /// <summary>
        /// Registry path, below HKEY_LOCAL_MACHINE, of the user profiles known to Windows.
        /// </summary>
        private const string ProfileListRegistryPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

        /// <summary>
        /// Prefix of the security identifiers of local and domain user accounts; system and service accounts differ.
        /// </summary>
        private const string UserAccountSidPrefix = "S-1-5-21-";


        // Injected dependencies

        /// <summary>
        /// Resolves the local application data folder of the current user; replaced in tests.
        /// </summary>
        private readonly Func<string> _getLocalAppData;

        /// <summary>
        /// Resolves the temporary folder of the current user; replaced in tests.
        /// </summary>
        private readonly Func<string> _getTempPath;

        /// <summary>
        /// Lists the user profiles of this computer; replaced in tests so real profiles are never read.
        /// </summary>
        private readonly Func<IReadOnlyList<UserProfileInfo>> _getUserProfiles;

        /// <summary>
        /// Lists the folders in which the installer creates shortcuts for the current user; replaced in tests.
        /// </summary>
        private readonly Func<IReadOnlyList<string>> _getShortcutFolders;

        /// <summary>
        /// Opens the writable list of installed applications, or returns null when it is missing; replaced in tests.
        /// </summary>
        private readonly Func<RegistryKey?> _openUninstallRoot;

        /// <summary>
        /// Schedules one file or empty folder for deletion at the next restart and returns whether that worked;
        /// replaced in tests.
        /// </summary>
        private readonly Func<string, bool> _scheduleDeleteOnReboot;

        /// <summary>
        /// Folder the uninstaller runs from; neither it nor anything inside it is ever deleted.
        /// </summary>
        private readonly string _protectedPath;

        /// <summary>
        /// Pause between two attempts to delete an entry that another program holds.
        /// </summary>
        private readonly TimeSpan _retryDelay;


        // Initialization

        /// <summary>
        /// Creates an uninstaller for the real profiles, registry, and folders of this computer.
        /// </summary>
        public UninstallerService()
            : this(localAppData: null)
        {
        }

        /// <summary>
        /// Creates an uninstaller with test-injected folders, profiles, registry, and restart scheduling.
        /// </summary>
        /// <param name="localAppData">Overrides the current user's local application data folder.</param>
        /// <param name="tempPath">Overrides the current user's temporary folder.</param>
        /// <param name="userProfiles">Overrides the list of user profiles read from the registry.</param>
        /// <param name="shortcutFolders">Overrides the current user's desktop and Start menu folders.</param>
        /// <param name="uninstallRoot">Overrides the writable list of installed applications.</param>
        /// <param name="scheduleDeleteOnReboot">Overrides the scheduling of locked entries for a restart.</param>
        /// <param name="protectedPath">Overrides the folder that is never deleted; null uses the own folder.</param>
        /// <param name="retryDelay">Overrides the pause between attempts to delete a held entry.</param>
        internal UninstallerService(
            Func<string>? localAppData = null,
            Func<string>? tempPath = null,
            Func<IReadOnlyList<UserProfileInfo>>? userProfiles = null,
            Func<IReadOnlyList<string>>? shortcutFolders = null,
            Func<RegistryKey?>? uninstallRoot = null,
            Func<string, bool>? scheduleDeleteOnReboot = null,
            string? protectedPath = null,
            TimeSpan? retryDelay = null)
        {
            _getLocalAppData = localAppData ?? DefaultLocalAppData;
            _getTempPath = tempPath ?? Path.GetTempPath;
            _getUserProfiles = userProfiles ?? ReadUserProfiles;
            _getShortcutFolders = shortcutFolders ?? DefaultShortcutFolders;
            _openUninstallRoot = uninstallRoot ?? (() => AppRegistration.OpenUninstallRoot(writable: true));
            _scheduleDeleteOnReboot = scheduleDeleteOnReboot ?? ScheduleDeleteOnReboot;
            _protectedPath = NormalizePath(protectedPath ?? AppContext.BaseDirectory);
            _retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(150);
        }


        // Default environment

        /// <summary>
        /// Returns the local application data folder of the current user.
        /// </summary>
        private static string DefaultLocalAppData()
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }

        /// <summary>
        /// Returns the desktop and the Start menu programs folder of the current user, where the installer creates
        /// its shortcuts.
        /// </summary>
        private static IReadOnlyList<string> DefaultShortcutFolders()
        {
            return
            [
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs")
            ];
        }

        /// <summary>
        /// Reads the profiles of local and domain user accounts from the registry; profiles whose folder does not
        /// exist are skipped.
        /// </summary>
        private static IReadOnlyList<UserProfileInfo> ReadUserProfiles()
        {
            using var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var profileList = localMachine.OpenSubKey(ProfileListRegistryPath);
            if (profileList is null)
            {
                return [];
            }

            var profiles = new List<UserProfileInfo>();
            foreach (var sid in profileList.GetSubKeyNames())
            {
                // System and service accounts never run ASLM; ".bak" entries are stale copies of a profile.
                if (!sid.StartsWith(UserAccountSidPrefix, StringComparison.OrdinalIgnoreCase) ||
                    sid.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // The profile path is stored with environment variables, which the registry API expands.
                using var profileKey = profileList.OpenSubKey(sid);
                if (profileKey?.GetValue("ProfileImagePath") is string profilePath &&
                    Path.IsPathFullyQualified(profilePath) &&
                    Directory.Exists(profilePath))
                {
                    var fullPath = NormalizePath(profilePath);
                    profiles.Add(new UserProfileInfo(Path.GetFileName(fullPath), fullPath));
                }
            }

            return profiles;
        }


        // Installation lookup

        /// <summary>
        /// Checks that a folder holds an ASLM installation made by the installer and reads its install manifest.
        /// </summary>
        /// <param name="sharedInstallDir">Shared installation folder that should contain the install manifest.</param>
        /// <returns>A successful result with the installation, or a failed result with a user message.</returns>
        public InstallationValidation ValidateInstallation(string sharedInstallDir)
        {
            // Normalize the folder; malformed input surfaces as a message instead of an exception.
            string sharedPath;
            try
            {
                sharedPath = NormalizePath(sharedInstallDir);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return InstallationValidation.Error($"The installation path is not valid: {ex.Message}");
            }

            // Without the manifest the folder is either a portable copy or no ASLM at all.
            var manifestPath = Path.Combine(sharedPath, ManifestFileName);
            if (!File.Exists(manifestPath))
            {
                return File.Exists(Path.Combine(sharedPath, AppFolderName, LauncherExeName))
                    ? InstallationValidation.Error("This is a portable copy of ASLM. Delete its folder to remove it.")
                    : InstallationValidation.Error($"No ASLM installation was found in {sharedPath}.");
            }

            // Read the manifest; an unreadable one cannot prove what belongs to the installation.
            InstallManifestData? manifest;
            try
            {
                using var stream = File.OpenRead(manifestPath);
                manifest = JsonSerializer.Deserialize<InstallManifestData>(stream);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return InstallationValidation.Error($"The ASLM install manifest cannot be read: {ex.Message}");
            }

            // The manifest must describe ASLM in exactly this folder, so an uninstaller copied elsewhere removes
            // nothing that does not belong to it.
            if (manifest is null ||
                !string.Equals(manifest.App, AppName, StringComparison.Ordinal) ||
                !IsSamePath(manifest.SharedInstallPath, sharedPath))
            {
                return InstallationValidation.Error($"The install manifest in {sharedPath} describes another folder.");
            }

            // Keep only per-user and shortcut paths of the expected shape; anything else is never deleted.
            var userAppPath = IsUserAppDirPath(manifest.UserAppPath)
                ? NormalizePath(manifest.UserAppPath!)
                : null;
            var shortcuts = (manifest.Shortcuts ?? [])
                .Where(IsShortcutPath)
                .Select(NormalizePath)
                .ToList();

            return InstallationValidation.Success(new AslmInstallation(
                sharedPath,
                Path.Combine(sharedPath, LauncherExeName),
                userAppPath,
                shortcuts));
        }


        // Removal plan

        /// <summary>
        /// Collects everything that is removed for the chosen scope.
        /// </summary>
        /// <param name="installation">Installation returned by <see cref="ValidateInstallation"/>.</param>
        /// <param name="scope">Whether ASLM is removed only for the current user or for every user.</param>
        /// <returns>The users, folders and shortcuts to remove.</returns>
        public UninstallPlan CreatePlan(AslmInstallation installation, UninstallScope scope)
        {
            // The current user's folders come from Windows itself, so redirected folders are found as well. Every
            // folder is normalized, so the paths derived from it compare as plain strings.
            var localAppData = NormalizePath(_getLocalAppData());
            var currentUserAppDir = Path.Combine(localAppData, AppName);
            var currentUser = new UserRemovalTarget(
                Environment.UserName,
                localAppData,
                NormalizePath(_getTempPath()),
                Directory.Exists(currentUserAppDir) ? currentUserAppDir : null);

            var users = new List<UserRemovalTarget>();
            var shortcutCandidates = _getShortcutFolders()
                .Select(folder => Path.Combine(folder, ShortcutFileName))
                .ToList();

            if (scope == UninstallScope.CurrentUser)
            {
                // All installations share the one per-user folder, so it is taken whichever Launcher wrote it.
                users.Add(currentUser);
            }
            else
            {
                // Every folder that belongs to this installation is taken: the current user's, those of the user
                // profiles, and the folder the installer filled even when no profile lists it.
                AddBoundUser(users, currentUser, installation);
                foreach (var profile in _getUserProfiles())
                {
                    var profilePath = NormalizePath(profile.ProfilePath);
                    var profileLocalAppData = Path.Combine(profilePath, "AppData", "Local");
                    if (AddBoundUser(users, CreateProfileTarget(profile.UserName, profileLocalAppData), installation))
                    {
                        shortcutCandidates.AddRange(GetDefaultShortcutPaths(profilePath));
                    }
                }

                if (installation.UserAppPath is { } installerUserAppDir)
                {
                    // The per-user folder lies in "<profile>\AppData\Local", which gives the profile of its owner.
                    var installerLocalAppData = Path.GetDirectoryName(installerUserAppDir)!;
                    var installerProfile = Path.GetDirectoryName(Path.GetDirectoryName(installerLocalAppData))
                        ?? installerLocalAppData;
                    var installerUser = CreateProfileTarget(Path.GetFileName(installerProfile), installerLocalAppData);
                    if (AddBoundUser(users, installerUser, installation))
                    {
                        shortcutCandidates.AddRange(GetDefaultShortcutPaths(installerProfile));
                    }
                }

                // The installer records where it created shortcuts, which also covers redirected desktops.
                shortcutCandidates.AddRange(installation.Shortcuts);
            }

            return new UninstallPlan(
                scope,
                installation,
                users,
                FindLauncherShortcuts(shortcutCandidates, installation.LauncherPath));
        }

        /// <summary>
        /// Builds the removal target of a user profile from its local application data folder.
        /// </summary>
        private static UserRemovalTarget CreateProfileTarget(string userName, string localAppData)
        {
            return new UserRemovalTarget(
                userName,
                localAppData,
                Path.Combine(localAppData, "Temp"),
                Path.Combine(localAppData, AppName));
        }

        /// <summary>
        /// Adds a user whose per-user folder belongs to the installation, unless that folder is already listed.
        /// </summary>
        /// <returns>True when the user was added.</returns>
        private static bool AddBoundUser(
            List<UserRemovalTarget> users,
            UserRemovalTarget candidate,
            AslmInstallation installation)
        {
            if (candidate.UserAppPath is not { } userAppPath ||
                !IsBoundToInstallation(userAppPath, installation) ||
                users.Any(user => IsSamePath(user.UserAppPath, userAppPath)))
            {
                return false;
            }

            users.Add(candidate);
            return true;
        }

        /// <summary>
        /// Returns whether a per-user folder belongs to the installation: the installer filled it, or the Launcher of
        /// the installation recorded itself in it.
        /// </summary>
        private static bool IsBoundToInstallation(string userAppDir, AslmInstallation installation)
        {
            if (!Directory.Exists(userAppDir))
            {
                return false;
            }

            return IsSamePath(userAppDir, installation.UserAppPath) ||
                   IsSamePath(ReadLauncherReference(userAppDir), installation.LauncherPath);
        }

        /// <summary>
        /// Reads the Launcher path that the Launcher records in a per-user folder on every start.
        /// </summary>
        /// <returns>The recorded Launcher path, or null when the reference is missing or unreadable.</returns>
        private static string? ReadLauncherReference(string userAppDir)
        {
            var referencePath = Path.Combine(userAppDir, LauncherRefFileName);
            try
            {
                if (!File.Exists(referencePath))
                {
                    return null;
                }

                using var document = JsonDocument.Parse(File.ReadAllText(referencePath));
                return document.RootElement.ValueKind == JsonValueKind.Object &&
                       document.RootElement.TryGetProperty("launcherPath", out var element) &&
                       element.ValueKind == JsonValueKind.String
                    ? element.GetString()
                    : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // An unreadable reference cannot tie the folder to this installation, so the folder is left alone.
                return null;
            }
        }


        // Shortcut lookup

        /// <summary>
        /// Returns the shortcut paths that the installer uses in a user profile: the desktop and the Start menu.
        /// </summary>
        private static IEnumerable<string> GetDefaultShortcutPaths(string profilePath)
        {
            yield return Path.Combine(profilePath, "Desktop", ShortcutFileName);
            yield return Path.Combine(
                profilePath, "AppData", "Roaming", "Microsoft", "Windows", "Start Menu", "Programs", ShortcutFileName);
        }

        /// <summary>
        /// Returns the existing shortcuts among the candidates that point at the Launcher, without duplicates.
        /// Shortcuts that point elsewhere belong to the user and stay.
        /// </summary>
        private static IReadOnlyList<string> FindLauncherShortcuts(IEnumerable<string> candidates, string launcherPath)
        {
            var existing = candidates
                .Select(NormalizePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(File.Exists)
                .ToList();
            if (existing.Count == 0)
            {
                return [];
            }

            // Read the targets through late-bound COM calls to WScript.Shell, as the installer does to create them.
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                return [];
            }

            var shell = Activator.CreateInstance(shellType);
            try
            {
                return existing
                    .Where(path => IsSamePath(ReadShortcutTarget(shellType, shell, path), launcherPath))
                    .ToList();
            }
            finally
            {
                ReleaseComObject(shell);
            }
        }

        /// <summary>
        /// Reads the target path of one shortcut, or returns null when the shortcut cannot be read.
        /// </summary>
        private static string? ReadShortcutTarget(Type shellType, object? shell, string shortcutPath)
        {
            object? shortcut = null;
            try
            {
                shortcut = shellType.InvokeMember(
                    "CreateShortcut",
                    BindingFlags.InvokeMethod,
                    binder: null,
                    target: shell,
                    args: [shortcutPath]);

                return shortcut?.GetType().InvokeMember(
                    "TargetPath",
                    BindingFlags.GetProperty,
                    binder: null,
                    target: shortcut,
                    args: []) as string;
            }
            catch (Exception ex) when (ex is TargetInvocationException or COMException)
            {
                // A damaged shortcut cannot be attributed to the Launcher, so it is left alone.
                return null;
            }
            finally
            {
                ReleaseComObject(shortcut);
            }
        }

        /// <summary>
        /// Releases a COM object created by the late-bound shortcut calls.
        /// </summary>
        private static void ReleaseComObject(object? instance)
        {
            if (instance is not null && Marshal.IsComObject(instance))
            {
                Marshal.FinalReleaseComObject(instance);
            }
        }


        // Path helpers

        /// <summary>
        /// Returns the full form of a path without a trailing separator and, when the path exists, with short (8.3)
        /// names expanded. Windows reports one location in either form: the temporary folder of a user whose name is
        /// not ASCII is short, while profiles, shortcuts and processes report long names.
        /// </summary>
        internal static string NormalizePath(string path)
        {
            var fullPath = Path.GetFullPath(path);
            var buffer = new StringBuilder(MaxExtendedPathLength);
            var length = GetLongPathName(fullPath, buffer, (uint)buffer.Capacity);
            var longPath = length > 0 && length < buffer.Capacity
                ? buffer.ToString(0, (int)length)
                : fullPath;

            return Path.TrimEndingDirectorySeparator(longPath);
        }

        /// <summary>
        /// Returns whether two paths name the same location, ignoring case, trailing separators and short names.
        /// </summary>
        private static bool IsSamePath(string? left, string? right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                return false;
            }

            try
            {
                return string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // A malformed path names no location, so it matches nothing.
                return false;
            }
        }

        /// <summary>
        /// Returns whether a path is equal to or located under a parent path. Both paths are compared as complete
        /// segments, so a sibling folder with the same name prefix does not count as a child. Both paths must be in
        /// the form <see cref="NormalizePath"/> returns or derived from such a path.
        /// </summary>
        private static bool IsSameOrChildPath(string parentPath, string childPath)
        {
            var normalizedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parentPath))
                + Path.DirectorySeparatorChar;
            var normalizedChild = Path.TrimEndingDirectorySeparator(Path.GetFullPath(childPath))
                + Path.DirectorySeparatorChar;

            return normalizedChild.StartsWith(normalizedParent, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Returns whether a path can be a per-user ASLM folder: an absolute path whose last segment is the product
        /// name.
        /// </summary>
        private static bool IsUserAppDirPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            {
                return false;
            }

            var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            return string.Equals(Path.GetFileName(fullPath), AppName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Returns whether a path can be a shortcut created by the installer: an absolute path to the ASLM shortcut.
        /// </summary>
        private static bool IsShortcutPath(string? path)
        {
            return !string.IsNullOrWhiteSpace(path) &&
                   Path.IsPathFullyQualified(path) &&
                   string.Equals(Path.GetFileName(path), ShortcutFileName, StringComparison.OrdinalIgnoreCase);
        }


        // Path interop

        /// <summary>
        /// Length of the longest path Windows supports, used as the buffer size for paths returned by Win32 calls.
        /// </summary>
        private const int MaxExtendedPathLength = 32767;

        /// <summary>
        /// Converts an existing path to its long form (Unicode variant of the kernel32 <c>GetLongPathName</c>).
        /// </summary>
        /// <returns>The length of the long path, the needed buffer size when the buffer is too small, or 0 on
        /// failure.</returns>
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetLongPathName(string shortPath, StringBuilder longPath, uint bufferLength);


        // Install manifest

        /// <summary>
        /// Fields of the install manifest that the uninstaller reads; the installer writes them in camel case.
        /// </summary>
        private sealed class InstallManifestData
        {
            /// <summary>
            /// Application name; ASLM in every manifest the installer writes.
            /// </summary>
            [JsonPropertyName("app")]
            public string? App { get; set; }

            /// <summary>
            /// Shared folder that holds the Launcher; it must be the folder the manifest was read from.
            /// </summary>
            [JsonPropertyName("sharedInstallPath")]
            public string? SharedInstallPath { get; set; }

            /// <summary>
            /// Per-user folder that the installer filled for the user who ran it.
            /// </summary>
            [JsonPropertyName("userAppPath")]
            public string? UserAppPath { get; set; }

            /// <summary>
            /// Shortcuts that the installer created; missing in the manifests of older installers.
            /// </summary>
            [JsonPropertyName("shortcuts")]
            public List<string>? Shortcuts { get; set; }
        }
    }


    // Removal scope and plan

    /// <summary>
    /// Selects for whom ASLM is removed.
    /// </summary>
    public enum UninstallScope
    {
        /// <summary>
        /// Removes the current user's ASLM folder, preferences, temporary files and shortcuts; the shared folder and
        /// the other users stay.
        /// </summary>
        CurrentUser,

        /// <summary>
        /// Removes the shared folder, the registration, and the ASLM folders of every user of the installation.
        /// </summary>
        AllUsers
    }

    /// <summary>
    /// Describes an ASLM installation found through its install manifest.
    /// </summary>
    /// <param name="SharedInstallPath">Shared folder that holds the Launcher and the install manifest.</param>
    /// <param name="LauncherPath">Launcher executable in the shared folder.</param>
    /// <param name="UserAppPath">Per-user folder that the installer filled, or null when the manifest has none.</param>
    /// <param name="Shortcuts">Shortcuts that the installer recorded; empty for older installers.</param>
    public sealed record AslmInstallation(
        string SharedInstallPath,
        string LauncherPath,
        string? UserAppPath,
        IReadOnlyList<string> Shortcuts);

    /// <summary>
    /// Describes the result of the installation lookup.
    /// </summary>
    /// <param name="IsValid">Whether the folder holds an installation that can be removed.</param>
    /// <param name="Installation">The installation; null when the lookup failed.</param>
    /// <param name="Message">User-facing explanation of the failure; empty when the lookup succeeded.</param>
    public sealed record InstallationValidation(bool IsValid, AslmInstallation? Installation, string Message)
    {
        /// <summary>
        /// Creates a successful lookup result.
        /// </summary>
        public static InstallationValidation Success(AslmInstallation installation) => new(true, installation, string.Empty);

        /// <summary>
        /// Creates a failed lookup result.
        /// </summary>
        public static InstallationValidation Error(string message) => new(false, null, message);
    }

    /// <summary>
    /// Describes one user profile of this computer.
    /// </summary>
    /// <param name="UserName">Name shown to the user; the name of the profile folder.</param>
    /// <param name="ProfilePath">Absolute path of the profile folder.</param>
    public sealed record UserProfileInfo(string UserName, string ProfilePath);

    /// <summary>
    /// Describes what is removed for one user.
    /// </summary>
    /// <param name="UserName">Name shown to the user.</param>
    /// <param name="LocalAppDataPath">Local application data folder that holds the legacy MAUI preferences.</param>
    /// <param name="TempPath">Temporary folder that holds the ASLM temporary folders.</param>
    /// <param name="UserAppPath">Per-user ASLM folder, or null when the user has none.</param>
    public sealed record UserRemovalTarget(
        string UserName,
        string LocalAppDataPath,
        string TempPath,
        string? UserAppPath);

    /// <summary>
    /// Describes everything one removal deletes.
    /// </summary>
    /// <param name="Scope">Whether ASLM is removed only for the current user or for every user.</param>
    /// <param name="Installation">Installation the removal belongs to.</param>
    /// <param name="Users">Users whose ASLM folders, preferences and temporary files are removed.</param>
    /// <param name="Shortcuts">Shortcuts that point at the Launcher of the installation.</param>
    public sealed record UninstallPlan(
        UninstallScope Scope,
        AslmInstallation Installation,
        IReadOnlyList<UserRemovalTarget> Users,
        IReadOnlyList<string> Shortcuts);


    // Removal results

    /// <summary>
    /// Describes a running program that holds files of the installation.
    /// </summary>
    /// <param name="Id">Process id.</param>
    /// <param name="Name">Executable file name shown to the user.</param>
    /// <param name="ExecutablePath">Full path of the executable.</param>
    public sealed record BlockingProcess(int Id, string Name, string ExecutablePath);

    /// <summary>
    /// Reports removal progress to the uninstaller UI.
    /// </summary>
    /// <param name="Message">Status text shown next to the progress bar.</param>
    /// <param name="Percent">Overall progress from 0 to 100.</param>
    public sealed record UninstallProgress(string Message, int Percent);

    /// <summary>
    /// Describes the outcome of a removal.
    /// </summary>
    /// <param name="ScheduledForReboot">Locked entries that Windows deletes at the next restart.</param>
    /// <param name="NotRemoved">Entries that could not be removed and stay on the computer.</param>
    public sealed record UninstallResult(IReadOnlyList<string> ScheduledForReboot, IReadOnlyList<string> NotRemoved)
    {
        /// <summary>
        /// Gets whether everything was removed right away.
        /// </summary>
        public bool IsComplete => ScheduledForReboot.Count == 0 && NotRemoved.Count == 0;
    }
}
