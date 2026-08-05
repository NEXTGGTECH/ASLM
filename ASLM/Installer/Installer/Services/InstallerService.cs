// Copyright NEXTGGTECH. Apache License 2.0.

using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;

namespace ASLM.Installer;

/// <summary>
/// Installs the embedded ASLM payload using the dual-location strategy:
/// the Launcher and the payload archive go into the shared installation directory
/// chosen by the user, while the application itself (App\, Patcher\, Data\, …)
/// is installed into the current user's per-user application directory.
/// </summary>
public sealed class InstallerService
{
    private const string PayloadFileName = "aslm-payload.zip";
    private const string BaseArchiveFileName = "aslm-base.zip";
    private const string PayloadEnvironmentVariable = "ASLM_INSTALLER_PAYLOAD_PATH";
    private const string LauncherExeName = "ASLM.exe";
    private const string ManifestFileName = "install-manifest.json";
    private const string LegalAcceptanceFileName = "ASLM_LegalAcceptance.json";
    private const string AppName = "ASLM";

    // Default paths.

    /// <summary>
    /// Returns the default parent directory for the shared ASLM installation.
    /// </summary>
    public string GetDefaultInstallBasePath()
    {
        return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    }

    /// <summary>
    /// Returns the per-user application directory where the ASLM application will be installed.
    /// This path is fixed and not configurable by the user.
    /// </summary>
    public string GetUserAppDir()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppName);
    }


    // Path validation.

    /// <summary>
    /// Validates and normalizes the selected shared installation directory.
    /// </summary>
    public InstallPathValidation ValidateInstallPath(string basePath, string folderName)
    {
        // Reject empty parent directory or folder name inputs early.
        if (string.IsNullOrWhiteSpace(basePath))
        {
            return InstallPathValidation.Error("Choose an installation directory.");
        }

        if (string.IsNullOrWhiteSpace(folderName))
        {
            return InstallPathValidation.Error("Enter the folder name.");
        }

        var trimmedFolderName = folderName.Trim();
        if (trimmedFolderName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return InstallPathValidation.Error("The folder name contains characters that Windows cannot use in folder names.");
        }

        string fullBasePath;
        string installPath;
        try
        {
            fullBasePath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(basePath.Trim()));
            installPath = Path.GetFullPath(Path.Combine(fullBasePath, trimmedFolderName));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return InstallPathValidation.Error($"The installation path is not valid: {ex.Message}");
        }

        if (!Path.IsPathRooted(installPath))
        {
            return InstallPathValidation.Error("Use an absolute installation path.");
        }

        if (!IsChildPath(fullBasePath, installPath))
        {
            return InstallPathValidation.Error("The folder name must stay inside the selected installation directory.");
        }

        // Block installs into an existing non-empty target folder.
        if (Directory.Exists(installPath) && Directory.EnumerateFileSystemEntries(installPath).Any())
        {
            return InstallPathValidation.Error("The target folder already exists and is not empty.");
        }

        return InstallPathValidation.Success(installPath);
    }


    // Installation workflow.

    /// <summary>
    /// Installs ASLM using the dual-location strategy and returns the install manifest.
    /// </summary>
    /// <remarks>
    /// Three things happen:
    /// 1. The Launcher executable is placed in the shared installation directory.
    /// 2. The payload archive (aslm-base.zip) is placed next to the Launcher so that
    ///    additional users who run the Launcher can bootstrap their own application copy.
    /// 3. Everything else (App\, Patcher\, Data\, Engines\, Models\, Modules\) is placed
    ///    in the current user's per-user application directory (%LOCALAPPDATA%\ASLM).
    /// </remarks>
    public async Task<InstallManifest> InstallAsync(
        InstallOptions options,
        IProgress<InstallProgress> progress,
        CancellationToken cancellationToken)
    {
        var validation = ValidateInstallPath(options.BasePath, options.FolderName);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(validation.Message);
        }

        var sharedInstallDir = validation.InstallPath;
        var userAppDir = GetUserAppDir();
        var stagingPath = CreateStagingPath();

        progress.Report(new InstallProgress("Preparing installation directories...", 0));
        Directory.CreateDirectory(Path.GetDirectoryName(sharedInstallDir)!);
        Directory.CreateDirectory(stagingPath);

        try
        {
            // Extract the full payload into a temporary staging directory.
            cancellationToken.ThrowIfCancellationRequested();
            await ExtractPayloadToStagingAsync(stagingPath, progress, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(new InstallProgress("Installing launcher...", 88));

            // Place the Launcher in the shared installation directory.
            Directory.CreateDirectory(sharedInstallDir);
            var launcherSource = Path.Combine(stagingPath, LauncherExeName);
            var launcherDest = Path.Combine(sharedInstallDir, LauncherExeName);
            if (File.Exists(launcherSource))
            {
                File.Copy(launcherSource, launcherDest, overwrite: true);
            }

            // Copy the payload archive next to the Launcher for per-user bootstrapping.
            progress.Report(new InstallProgress("Copying payload archive...", 90));
            CopyPayloadArchive(sharedInstallDir);

            // Install the application content (everything except the root Launcher) per-user.
            progress.Report(new InstallProgress("Installing application files...", 92));
            InstallUserAppDir(stagingPath, userAppDir);

            // Write the install manifest into the shared directory.
            var manifest = new InstallManifest(
                App: AppName,
                Version: options.Version,
                InstalledAtUtc: DateTimeOffset.UtcNow,
                SharedInstallPath: sharedInstallDir,
                UserAppPath: userAppDir,
                AcceptedDocuments: options.AcceptedDocuments);

            await WriteManifestAsync(sharedInstallDir, manifest, cancellationToken);
            await WriteLegalAcceptanceAsync(userAppDir, options.AcceptedDocuments, cancellationToken);

            if (options.CreateDesktopShortcut)
            {
                progress.Report(new InstallProgress("Creating desktop shortcut...", 94));
                TryCreateShortcut(
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    "ASLM.lnk",
                    sharedInstallDir);
            }

            if (options.CreateStartMenuShortcut)
            {
                progress.Report(new InstallProgress("Creating Start menu shortcut...", 97));
                TryCreateShortcut(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"),
                    "ASLM.lnk",
                    sharedInstallDir);
            }

            progress.Report(new InstallProgress("ASLM has been installed.", 100));
            return manifest;
        }
        catch
        {
            TryDeleteDirectory(stagingPath);
            throw;
        }
        finally
        {
            TryDeleteDirectory(stagingPath);
        }
    }

    /// <summary>
    /// Starts ASLM from the shared installation directory.
    /// </summary>
    public void Launch(string sharedInstallPath)
    {
        var launcherPath = Path.Combine(sharedInstallPath, LauncherExeName);
        if (!File.Exists(launcherPath))
        {
            throw new FileNotFoundException("ASLM launcher was not found.", launcherPath);
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = launcherPath,
            WorkingDirectory = sharedInstallPath,
            UseShellExecute = true
        });
    }


    // Payload extraction.

    /// <summary>
    /// Extracts the full ASLM payload archive into a temporary staging directory.
    /// </summary>
    private static async Task ExtractPayloadToStagingAsync(
        string stagingPath,
        IProgress<InstallProgress> progress,
        CancellationToken cancellationToken)
    {
        progress.Report(new InstallProgress("Opening embedded ASLM package...", 4));

        await using var payloadStream = await OpenPayloadStreamAsync();
        using var archive = new ZipArchive(payloadStream, ZipArchiveMode.Read, leaveOpen: false);
        var entries = archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)).ToList();
        if (entries.Count == 0)
        {
            throw new InvalidOperationException("The embedded ASLM package is empty.");
        }

        for (var index = 0; index < entries.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entry = entries[index];
            var destinationPath = Path.GetFullPath(Path.Combine(stagingPath, entry.FullName));
            if (!IsChildPath(stagingPath, destinationPath))
            {
                throw new InvalidOperationException($"Package entry tries to write outside the staging directory: {entry.FullName}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

            await using var source = entry.Open();
            await using var destination = File.Create(destinationPath);
            await source.CopyToAsync(destination, cancellationToken);

            // Map archive progress into the installer wizard range (4–86 %).
            var percent = 6 + (int)Math.Round((index + 1d) / entries.Count * 80d);
            progress.Report(new InstallProgress($"Extracting {entry.FullName}", percent));
        }
    }

    /// <summary>
    /// Opens the payload from the bootstrapper environment variable, local files, or packaged assets.
    /// </summary>
    private static async Task<Stream> OpenPayloadStreamAsync()
    {
        var candidates = new List<string>();
        var environmentPath = Environment.GetEnvironmentVariable(PayloadEnvironmentVariable);

        AddCandidate(candidates, environmentPath);
        AddCandidate(candidates, Path.Combine(AppContext.BaseDirectory, PayloadFileName));
        AddCandidate(candidates, Path.Combine(Environment.CurrentDirectory, PayloadFileName));

        // Prefer the bootstrapper-provided payload path and local copies before packaged assets.
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return File.OpenRead(candidate);
            }
        }

        try
        {
            return await FileSystem.OpenAppPackageFileAsync(PayloadFileName);
        }
        catch (FileNotFoundException)
        {
            throw CreatePayloadMissingException(candidates);
        }
        catch (DirectoryNotFoundException)
        {
            throw CreatePayloadMissingException(candidates);
        }
    }

    /// <summary>
    /// Returns the filesystem path to the payload archive, or null if it cannot be resolved.
    /// </summary>
    private static string? ResolvePayloadArchivePath()
    {
        var environmentPath = Environment.GetEnvironmentVariable(PayloadEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(environmentPath) && File.Exists(environmentPath))
        {
            return environmentPath;
        }

        var localPath = Path.Combine(AppContext.BaseDirectory, PayloadFileName);
        if (File.Exists(localPath))
        {
            return localPath;
        }

        var cwdPath = Path.Combine(Environment.CurrentDirectory, PayloadFileName);
        if (File.Exists(cwdPath))
        {
            return cwdPath;
        }

        return null;
    }

    /// <summary>
    /// Adds one normalized payload candidate path if it is not already present in the list.
    /// </summary>
    private static void AddCandidate(List<string> candidates, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var fullPath = Path.GetFullPath(path);
        if (!candidates.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
        {
            candidates.Add(fullPath);
        }
    }

    /// <summary>
    /// Builds an error that lists all filesystem locations checked for the payload.
    /// </summary>
    private static FileNotFoundException CreatePayloadMissingException(IReadOnlyList<string> candidates)
    {
        var searchedPaths = candidates.Count == 0
            ? "No filesystem candidates were available."
            : string.Join(Environment.NewLine, candidates.Select(path => $" - {path}"));

        return new FileNotFoundException(
            $"ASLM payload archive was not found. Build ASLM/Installer/Installer-Bootstrapper in Visual Studio so {PayloadFileName} is generated and included.{Environment.NewLine}{searchedPaths}",
            PayloadFileName);
    }


    // Dual-location file placement.

    /// <summary>
    /// Copies the original payload archive to the shared installation directory as aslm-base.zip,
    /// enabling the Launcher to bootstrap new users without re-running the installer.
    /// </summary>
    private static void CopyPayloadArchive(string sharedInstallDir)
    {
        var sourcePath = ResolvePayloadArchivePath();
        if (sourcePath == null)
        {
            // Not fatal: the Launcher will still work for the installing user.
            // Additional users simply won't be able to auto-bootstrap.
            return;
        }

        var destPath = Path.Combine(sharedInstallDir, BaseArchiveFileName);
        File.Copy(sourcePath, destPath, overwrite: true);
    }

    /// <summary>
    /// Copies all staging content except the root-level Launcher executable into the
    /// per-user application directory.
    /// </summary>
    private static void InstallUserAppDir(string stagingPath, string userAppDir)
    {
        Directory.CreateDirectory(userAppDir);

        foreach (var dirPath in Directory.EnumerateDirectories(stagingPath, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(stagingPath, dirPath);
            Directory.CreateDirectory(Path.Combine(userAppDir, relative));
        }

        foreach (var filePath in Directory.EnumerateFiles(stagingPath, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(stagingPath, filePath);

            // Skip the root-level Launcher — it belongs in the shared install directory.
            if (IsRootLauncherFile(relative))
            {
                continue;
            }

            var destFile = Path.Combine(userAppDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
            File.Copy(filePath, destFile, overwrite: true);
        }
    }

    /// <summary>
    /// Returns true when the relative path represents the root-level Launcher executable.
    /// </summary>
    private static bool IsRootLauncherFile(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').Trim('/');
        return string.Equals(normalized, LauncherExeName, StringComparison.OrdinalIgnoreCase);
    }


    // Staging directory operations.

    /// <summary>
    /// Creates a unique temporary directory path for payload extraction.
    /// </summary>
    private static string CreateStagingPath()
    {
        return Path.Combine(
            Path.GetTempPath(),
            "ASLM",
            "InstallStaging",
            Guid.NewGuid().ToString("N"));
    }

    // Metadata persistence.

    /// <summary>
    /// Writes the install manifest into the shared installation directory.
    /// </summary>
    private static async Task WriteManifestAsync(
        string sharedInstallDir,
        InstallManifest manifest,
        CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(sharedInstallDir, ManifestFileName);

        await using var stream = File.Create(manifestPath);
        await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions.Default, cancellationToken);
    }

    /// <summary>
    /// Writes accepted legal documents into the per-user application data directory.
    /// </summary>
    private static async Task WriteLegalAcceptanceAsync(
        string userAppDir,
        IReadOnlyList<AcceptedLegalDocument> acceptedDocuments,
        CancellationToken cancellationToken)
    {
        var legalPath = Path.Combine(userAppDir, "Data", "App", LegalAcceptanceFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(legalPath)!);

        var payload = new { acceptedDocuments };
        await using var stream = File.Create(legalPath);
        await JsonSerializer.SerializeAsync(stream, payload, JsonOptions.Default, cancellationToken);
    }


    // Shortcut creation.

    /// <summary>
    /// Creates a Windows shortcut without failing an otherwise successful installation.
    /// </summary>
    private static void TryCreateShortcut(string folderPath, string shortcutFileName, string sharedInstallDir)
    {
        try
        {
            CreateShortcut(folderPath, shortcutFileName, sharedInstallDir);
        }
        catch
        {
            // Shortcut creation is optional after files are installed.
        }
    }

    /// <summary>
    /// Creates a Windows shell shortcut that points to the Launcher in the shared install directory.
    /// </summary>
    private static void CreateShortcut(string folderPath, string shortcutFileName, string sharedInstallDir)
    {
        Directory.CreateDirectory(folderPath);

        var launcherPath = Path.Combine(sharedInstallDir, LauncherExeName);
        var shortcutPath = Path.Combine(folderPath, shortcutFileName);
        if (!File.Exists(launcherPath))
        {
            return;
        }

        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null)
        {
            return;
        }

        var shell = Activator.CreateInstance(shellType);
        var shortcut = shellType.InvokeMember(
            "CreateShortcut",
            BindingFlags.InvokeMethod,
            binder: null,
            target: shell,
            args: [shortcutPath]);

        var shortcutType = shortcut!.GetType();
        shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, [launcherPath]);
        shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, [sharedInstallDir]);
        shortcutType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, ["ASLM"]);
        shortcutType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, [$"{launcherPath},0"]);
        shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, []);
    }


    // Path safety and cleanup.

    /// <summary>
    /// Checks whether a path is equal to or located under a parent path.
    /// </summary>
    private static bool IsChildPath(string parentPath, string childPath)
    {
        var normalizedParent = Path.GetFullPath(parentPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var normalizedChild = Path.GetFullPath(childPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        return normalizedChild.StartsWith(normalizedParent, StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                normalizedParent.TrimEnd(Path.DirectorySeparatorChar),
                normalizedChild.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Deletes a directory without masking the original installation error.
    /// </summary>
    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Cleanup is best-effort.
        }
    }
}


/// <summary>
/// Contains user-selected installation options.
/// </summary>
public sealed record InstallOptions(
    string BasePath,
    string FolderName,
    string Version,
    IReadOnlyList<AcceptedLegalDocument> AcceptedDocuments,
    bool CreateDesktopShortcut,
    bool CreateStartMenuShortcut);

/// <summary>
/// Records one accepted legal document.
/// </summary>
public sealed record AcceptedLegalDocument(
    string Id,
    string Title,
    string FileName,
    string Sha256,
    DateTimeOffset AcceptedAtUtc);


/// <summary>
/// Describes the completed ASLM installation.
/// </summary>
/// <param name="SharedInstallPath">
/// Directory containing the Launcher executable and the payload archive (shared across OS users).
/// </param>
/// <param name="UserAppPath">
/// Per-user directory containing the application, Patcher and data folders.
/// </param>
public sealed record InstallManifest(
    string App,
    string Version,
    DateTimeOffset InstalledAtUtc,
    string SharedInstallPath,
    string UserAppPath,
    IReadOnlyList<AcceptedLegalDocument> AcceptedDocuments);

/// <summary>
/// Reports installation progress to the wizard UI.
/// </summary>
public sealed record InstallProgress(string Message, int Percent);


/// <summary>
/// Describes the result of installation path validation.
/// </summary>
public sealed record InstallPathValidation(bool IsValid, string InstallPath, string Message)
{
    /// <summary>
    /// Creates a successful validation result.
    /// </summary>
    public static InstallPathValidation Success(string installPath) => new(true, installPath, string.Empty);

    /// <summary>
    /// Creates a failed validation result.
    /// </summary>
    public static InstallPathValidation Error(string message) => new(false, string.Empty, message);
}
