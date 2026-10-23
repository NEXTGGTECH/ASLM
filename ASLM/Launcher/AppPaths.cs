// Copyright NEXTGGTECH. Apache License 2.0.

/// <summary>
/// Resolves installation paths for both the shared launcher location and the per-user application directory.
/// </summary>
/// <remarks>
/// Windows and Linux support two installation layouts:
/// - Monolithic (Debug): Launcher lives next to App\, Patcher\, Data\, etc. in one directory.
/// - Dual-location (Release): Launcher lives in a shared directory; the application and its data
///   live in the per-user local application data directory.
/// Linux portable builds keep App/ beside the launcher; installed builds bootstrap App/ in the XDG profile.
/// </remarks>
internal static class AppPaths
{
    private const string AppName = "ASLM";
    private const string AppFolderName = "App";

    /// <summary>
    /// Gets the native executable name shared by the launcher and application in different directories.
    /// </summary>
    public static string ExecutableName => OperatingSystem.IsWindows() ? "ASLM.exe" : "ASLM";

    /// <summary>
    /// Returns the directory that contains the currently running Launcher executable.
    /// In a Release installation this is the shared installation directory.
    /// </summary>
    public static string GetSharedInstallDir()
        => AppDomain.CurrentDomain.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);

    /// <summary>
    /// Returns the per-user application directory where App, Patcher and data folders are stored.
    /// On Windows: %LOCALAPPDATA%\ASLM
    /// On Linux:   ~/.local/share/ASLM  (XDG_DATA_HOME)
    /// On macOS:   ~/Library/Application Support/ASLM
    /// </summary>
    public static string GetUserAppDir()
    {
        if (OperatingSystem.IsLinux())
        {
            // Match AppRoot, including development overrides, for both installed and portable builds.
            var overridePath = Environment.GetEnvironmentVariable("ASLM_ROOT");
            if (!string.IsNullOrWhiteSpace(overridePath) && Path.IsPathFullyQualified(overridePath))
                return Path.TrimEndingDirectorySeparator(Path.GetFullPath(overridePath));

            var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            var dataRoot = !string.IsNullOrWhiteSpace(dataHome) && Path.IsPathFullyQualified(dataHome)
                ? dataHome
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            return Path.GetFullPath(Path.Combine(dataRoot, AppName));
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);
    }

    /// <summary>
    /// Returns the path of the payload archive used to bootstrap a new user's application directory.
    /// The archive is placed next to the Launcher during installation.
    /// </summary>
    public static string GetPayloadArchivePath(string sharedInstallDir)
        => Path.Combine(sharedInstallDir, OperatingSystem.IsLinux() ? "aslm-base.tar.gz" : "aslm-base.zip");

    /// <summary>
    /// Returns true when the Launcher is running from a monolithic (Debug) layout where
    /// App\ lives next to the Launcher itself. In this layout the per-user bootstrapping
    /// logic is skipped and everything runs from the Launcher's own directory.
    /// </summary>
    public static bool IsMonolithicLayout(string launcherDir)
        => File.Exists(Path.Combine(launcherDir, AppFolderName, ExecutableName));
}
