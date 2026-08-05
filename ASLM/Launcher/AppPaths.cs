// Copyright NEXTGGTECH. Apache License 2.0.

/// <summary>
/// Resolves installation paths for both the shared launcher location and the per-user application directory.
/// </summary>
/// <remarks>
/// Two installation layouts are supported:
/// - Monolithic (Debug): Launcher lives next to App\, Patcher\, Data\, etc. in one directory.
/// - Dual-location (Release): Launcher lives in a shared directory; the application and its data
///   live in the per-user local application data directory.
/// All path helpers are cross-platform by design: they rely on the .NET SpecialFolder API and
/// Path combiners rather than hard-coded OS paths.
/// </remarks>
internal static class AppPaths
{
    private const string AppName = "ASLM";
    private const string AppFolderName = "App";
    private const string AppExeName = "ASLM.exe";

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
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppName);

    /// <summary>
    /// Returns the path of the payload archive used to bootstrap a new user's application directory.
    /// The archive is placed next to the Launcher during installation.
    /// </summary>
    public static string GetPayloadArchivePath(string sharedInstallDir)
        => Path.Combine(sharedInstallDir, "aslm-base.zip");

    /// <summary>
    /// Returns true when the Launcher is running from a monolithic (Debug) layout where
    /// App\ lives next to the Launcher itself. In this layout the per-user bootstrapping
    /// logic is skipped and everything runs from the Launcher's own directory.
    /// </summary>
    public static bool IsMonolithicLayout(string launcherDir)
        => File.Exists(Path.Combine(launcherDir, AppFolderName, AppExeName));
}
