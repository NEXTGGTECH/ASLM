// Copyright NEXTGGTECH. Apache License 2.0.

using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;

namespace ASLM.Installer.Bootstrapper;

/// <summary>
/// Extracts the embedded installer UI and starts it with the embedded ASLM payload.
/// </summary>
internal static class Program
{
    private const string InstallerZipResourceName = "installer-ui.zip";
    private const string InstallerExecutableName = "ASLM-Installer.exe";
    private const string PayloadFileName = "aslm-payload.zip";
    private const string PayloadEnvironmentVariable = "ASLM_INSTALLER_PAYLOAD_PATH";

    // Process startup.

    /// <summary>
    /// Prepares a temporary installer runtime and returns the UI process exit code.
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        return Run(args, OpenAssemblyResource, ShowError);
    }

    /// <summary>
    /// Opens an embedded resource from the executing bootstrapper assembly.
    /// </summary>
    internal static Stream? OpenAssemblyResource(string resourceName)
    {
        return Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
    }

    /// <summary>
    /// Runs the bootstrapper against an injectable resource source so the flow can be tested headlessly.
    /// </summary>
    internal static int Run(string[] args, Func<string, Stream?> openResource, Action<Exception>? showError = null)
    {
        var extractionPath = Path.Combine(
            Path.GetTempPath(),
            "ASLM",
            "Installer",
            $"{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(extractionPath);

            // Unpack the MAUI installer UI and the ASLM payload into the temp runtime folder.
            using var installerZip = OpenRequiredResource(InstallerZipResourceName, openResource);
            ExtractInstallerUi(installerZip, extractionPath);
            var payloadPath = ExtractEmbeddedPayload(extractionPath, openResource);

            var installerPath = Path.Combine(extractionPath, InstallerExecutableName);
            if (!File.Exists(installerPath))
            {
                throw new FileNotFoundException("The embedded installer UI executable was not found.", installerPath);
            }

            if (!File.Exists(payloadPath))
            {
                throw new FileNotFoundException("The embedded ASLM payload could not be extracted.", payloadPath);
            }

            // Keep the payload path out of command-line arguments; the UI reads it from the environment.
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = installerPath,
                Arguments = string.Join(" ", args.Select(QuoteArgument)),
                WorkingDirectory = extractionPath,
                UseShellExecute = false,
                Environment =
                {
                    [PayloadEnvironmentVariable] = payloadPath
                }
            });

            process?.WaitForExit();
            return process?.ExitCode ?? 0;
        }
        catch (Exception ex)
        {
            (showError ?? ShowError).Invoke(ex);
            return 1;
        }
        finally
        {
            TryDeleteDirectory(extractionPath);
        }
    }


    // Resource extraction.

    /// <summary>
    /// Extracts the packaged MAUI installer UI into the temporary runtime directory.
    /// </summary>
    internal static void ExtractInstallerUi(Stream archiveStream, string extractionPath)
    {
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);

        foreach (var entry in archive.Entries)
        {
            // Skip directory-only zip entries.
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            var destinationPath = Path.GetFullPath(Path.Combine(extractionPath, entry.FullName));
            if (!IsChildPath(extractionPath, destinationPath))
            {
                throw new InvalidOperationException($"Installer resource entry tries to write outside the extraction directory: {entry.FullName}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            entry.ExtractToFile(destinationPath, overwrite: true);
        }
    }

    /// <summary>
    /// Writes the embedded ASLM payload archive next to the extracted UI.
    /// </summary>
    internal static string ExtractEmbeddedPayload(string extractionPath, Func<string, Stream?> openResource)
    {
        var payloadPath = Path.Combine(extractionPath, PayloadFileName);

        using var payload = OpenRequiredResource(PayloadFileName, openResource);
        using var output = File.Create(payloadPath);
        payload.CopyTo(output);

        return payloadPath;
    }

    /// <summary>
    /// Opens an embedded resource or reports the available resource names.
    /// </summary>
    internal static Stream OpenRequiredResource(string resourceName, Func<string, Stream?> openResource)
    {
        return openResource(resourceName)
            ?? throw new InvalidOperationException(
                $"The installer bootstrapper does not contain {resourceName}. Build ASLM/Installer/Installer-Bootstrapper in Visual Studio first. Embedded resources: {string.Join(", ", Assembly.GetExecutingAssembly().GetManifestResourceNames())}");
    }


    // Argument and path helpers.

    /// <summary>
    /// Quotes one command-line argument for forwarding to the installer UI.
    /// </summary>
    internal static string QuoteArgument(string argument)
    {
        return argument.Contains(' ') || argument.Contains('"')
            ? $"\"{argument.Replace("\"", "\\\"")}\""
            : argument;
    }

    /// <summary>
    /// Checks that an extracted archive entry stays under the intended directory.
    /// </summary>
    internal static bool IsChildPath(string parentPath, string childPath)
    {
        var normalizedParent = Path.GetFullPath(parentPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var normalizedChild = Path.GetFullPath(childPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        return normalizedChild.StartsWith(normalizedParent, StringComparison.OrdinalIgnoreCase);
    }


    // Error reporting and cleanup.

    /// <summary>
    /// Shows startup errors in a GUI-friendly message box.
    /// </summary>
    private static void ShowError(Exception ex)
    {
        try
        {
            System.Windows.Forms.MessageBox.Show(
                ex.Message,
                "ASLM Installer",
                System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Error);
        }
        catch
        {
            // WinExe applications have no console by default, so secondary reporting is optional.
        }
    }

    /// <summary>
    /// Removes the temporary extraction directory when the UI exits.
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
            // The UI process may leave files locked briefly; cleanup is best-effort.
        }
    }
}
