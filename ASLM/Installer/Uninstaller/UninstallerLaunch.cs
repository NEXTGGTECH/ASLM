// Copyright NEXTGGTECH. Apache License 2.0.

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using System.Text;

namespace ASLM.Installer.Uninstaller
{
    /// <summary>
    /// Reads the uninstaller command line and starts the elevated copy that removes ASLM for every user.
    /// </summary>
    /// <remarks>
    /// The uninstaller in the shared folder cannot delete itself while it runs, so the removal for every user runs
    /// from a copy in <c>%TEMP%\ASLM\Uninstaller\&lt;id&gt;</c>. The copy gets administrator rights, waits until the
    /// original process has exited, and receives the shared folder through <c>--install-dir</c>.
    /// </remarks>
    internal static class UninstallerLaunch
    {
        // Command line

        /// <summary>
        /// Argument that names the shared installation folder; the uninstaller's own folder is used without it.
        /// </summary>
        private const string InstallDirArgument = "--install-dir";

        /// <summary>
        /// Argument that requests the removal for every user.
        /// </summary>
        private const string AllUsersArgument = "--all-users";

        /// <summary>
        /// Argument naming the process id of the original uninstaller, which the copy waits for.
        /// </summary>
        private const string WaitProcessArgument = "--wait-process";

        /// <summary>
        /// Argument carrying the start time (UTC ticks) of the awaited process, used to detect a reused process id.
        /// </summary>
        private const string WaitStartArgument = "--wait-start";


        // Temporary copy

        /// <summary>
        /// Name of the ASLM folder in the temporary folder.
        /// </summary>
        private const string AslmTempFolderName = "ASLM";

        /// <summary>
        /// Folder below the ASLM temporary folder that holds the uninstaller copies.
        /// </summary>
        private const string CopiesFolderName = "Uninstaller";

        /// <summary>
        /// Win32 error code that the shell reports when the user declines the UAC prompt (<c>ERROR_CANCELLED</c>).
        /// </summary>
        private const int ErrorCancelled = 1223;

        /// <summary>
        /// Longest time the copy waits for the original uninstaller to exit.
        /// </summary>
        private static readonly TimeSpan ParentExitTimeout = TimeSpan.FromSeconds(60);


        // Command-line parsing

        /// <summary>
        /// Parses the uninstaller command line.
        /// </summary>
        /// <param name="args">Command-line arguments of the uninstaller.</param>
        /// <param name="defaultInstallDir">Shared folder used when <c>--install-dir</c> is missing.</param>
        /// <returns>The parsed arguments.</returns>
        /// <exception cref="ArgumentException">An argument is unknown, lacks a value or has a bad one.</exception>
        public static UninstallerArguments Parse(IReadOnlyList<string> args, string defaultInstallDir)
        {
            var installDir = defaultInstallDir;
            var allUsers = false;
            int? waitProcessId = null;
            long? waitStartTicks = null;

            for (var index = 0; index < args.Count; index++)
            {
                switch (args[index].ToLowerInvariant())
                {
                    case InstallDirArgument:
                        installDir = ReadValue(args, ref index);
                        break;

                    case AllUsersArgument:
                        allUsers = true;
                        break;

                    case WaitProcessArgument:
                        waitProcessId = int.TryParse(ReadValue(args, ref index), NumberStyles.None,
                            CultureInfo.InvariantCulture, out var processId) && processId > 0
                            ? processId
                            : throw new ArgumentException("The process id to wait for is not valid.");
                        break;

                    case WaitStartArgument:
                        waitStartTicks = long.TryParse(ReadValue(args, ref index), NumberStyles.None,
                            CultureInfo.InvariantCulture, out var ticks)
                            ? ticks
                            : throw new ArgumentException("The start time of the process to wait for is not valid.");
                        break;

                    default:
                        throw new ArgumentException($"Unknown argument: {args[index]}");
                }
            }

            return new UninstallerArguments(installDir, allUsers, waitProcessId, waitStartTicks);
        }

        /// <summary>
        /// Returns the value that follows an argument and moves the index onto it.
        /// </summary>
        /// <exception cref="ArgumentException">The argument is the last one.</exception>
        private static string ReadValue(IReadOnlyList<string> args, ref int index)
        {
            if (index + 1 >= args.Count)
            {
                throw new ArgumentException($"The argument {args[index]} needs a value.");
            }

            index++;
            return args[index];
        }


        // Elevated copy

        /// <summary>
        /// Returns whether a folder is a copy folder made by <see cref="CreateTemporaryCopy"/>: a folder named by a
        /// GUID below <c>ASLM\Uninstaller</c>.
        /// </summary>
        public static bool IsTemporaryCopyFolder(string directory)
        {
            var copyFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            var copiesFolder = Path.GetDirectoryName(copyFolder);
            var aslmFolder = Path.GetDirectoryName(copiesFolder);

            return Guid.TryParseExact(Path.GetFileName(copyFolder), "N", out _) &&
                   string.Equals(Path.GetFileName(copiesFolder), CopiesFolderName, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(Path.GetFileName(aslmFolder), AslmTempFolderName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Copies the uninstaller into a new copy folder below the temporary folder. All files named like the
        /// executable are copied, so a framework-dependent development build runs from the copy as well.
        /// </summary>
        /// <param name="executablePath">Running uninstaller executable.</param>
        /// <param name="tempPath">Temporary folder of the current user.</param>
        /// <returns>The path of the copied executable.</returns>
        public static string CreateTemporaryCopy(string executablePath, string tempPath)
        {
            var copyFolder = Path.Combine(tempPath, AslmTempFolderName, CopiesFolderName, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(copyFolder);

            var sourceFolder = Path.GetDirectoryName(executablePath)!;
            var filePattern = Path.GetFileNameWithoutExtension(executablePath) + ".*";
            foreach (var file in Directory.EnumerateFiles(sourceFolder, filePattern))
            {
                File.Copy(file, Path.Combine(copyFolder, Path.GetFileName(file)));
            }

            return Path.Combine(copyFolder, Path.GetFileName(executablePath));
        }

        /// <summary>
        /// Deletes a copy folder that is no longer needed because the elevated copy never started.
        /// </summary>
        public static void DeleteTemporaryCopy(string copyPath)
        {
            try
            {
                Directory.Delete(Path.GetDirectoryName(copyPath)!, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A leftover copy in the temporary folder is harmless; the next removal for all users deletes it.
            }
        }

        /// <summary>
        /// Starts the copy that removes ASLM for every user, asking for administrator rights unless this process
        /// already has them.
        /// </summary>
        /// <param name="copyPath">Executable returned by <see cref="CreateTemporaryCopy"/>.</param>
        /// <param name="installDir">Shared installation folder to remove.</param>
        /// <returns>True when the copy started; false when the user declined the UAC prompt.</returns>
        public static bool TryStartAllUsersCopy(string copyPath, string installDir)
        {
            // The copy identifies this process by id and start time, so a reused id never delays it.
            using var current = Process.GetCurrentProcess();
            var startInfo = new ProcessStartInfo
            {
                FileName = copyPath,
                Arguments = BuildAllUsersArguments(
                    installDir,
                    current.Id,
                    current.StartTime.ToUniversalTime().Ticks),
                WorkingDirectory = Path.GetDirectoryName(copyPath)!,
                UseShellExecute = true,
                Verb = IsElevated() ? string.Empty : "runas"
            };

            try
            {
                using var copy = Process.Start(startInfo);
                return copy is not null;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
            {
                return false;
            }
        }

        /// <summary>
        /// Builds the command line of the copy that removes ASLM for every user.
        /// </summary>
        internal static string BuildAllUsersArguments(string installDir, int processId, long processStartTicks)
        {
            string[] arguments =
            [
                AllUsersArgument,
                InstallDirArgument,
                installDir,
                WaitProcessArgument,
                processId.ToString(CultureInfo.InvariantCulture),
                WaitStartArgument,
                processStartTicks.ToString(CultureInfo.InvariantCulture)
            ];

            return string.Join(" ", arguments.Select(QuoteArgument));
        }

        /// <summary>
        /// Quotes one argument with the Windows command-line rules, so the started process reads it back unchanged.
        /// </summary>
        internal static string QuoteArgument(string value)
        {
            // An empty argument still occupies a token; text without whitespace or quotes needs no quoting.
            if (value.Length == 0)
            {
                return "\"\"";
            }

            if (!value.Any(character => char.IsWhiteSpace(character) || character == '"'))
            {
                return value;
            }

            // Backslashes are literal unless they precede a quote, so they are written once the next character is
            // known: before a quote they are doubled and the quote is escaped.
            var builder = new StringBuilder(value.Length + 8);
            builder.Append('"');
            var backslashCount = 0;
            foreach (var character in value)
            {
                if (character == '\\')
                {
                    backslashCount++;
                    continue;
                }

                if (character == '"')
                {
                    builder.Append('\\', backslashCount * 2 + 1);
                    builder.Append('"');
                    backslashCount = 0;
                    continue;
                }

                builder.Append('\\', backslashCount);
                backslashCount = 0;
                builder.Append(character);
            }

            // Trailing backslashes would escape the closing quote, so they are doubled.
            builder.Append('\\', backslashCount * 2);
            builder.Append('"');
            return builder.ToString();
        }

        /// <summary>
        /// Waits until the original uninstaller has exited, so its executable in the shared folder can be deleted.
        /// </summary>
        /// <exception cref="TimeoutException">The original uninstaller did not exit in time.</exception>
        public static void WaitForParentExit(UninstallerArguments arguments)
        {
            if (arguments.WaitProcessId is not { } processId)
            {
                return;
            }

            try
            {
                // A different start time means the id now belongs to another process.
                using var parent = Process.GetProcessById(processId);
                if (arguments.WaitProcessStartTicks is { } startTicks &&
                    parent.StartTime.ToUniversalTime().Ticks != startTicks)
                {
                    return;
                }

                if (!parent.WaitForExit(ParentExitTimeout))
                {
                    throw new TimeoutException("The uninstaller that started this copy did not exit.");
                }
            }
            catch (ArgumentException)
            {
                // The original uninstaller already exited.
            }
            catch (InvalidOperationException)
            {
                // The original uninstaller exited before its start time could be read.
            }
        }

        /// <summary>
        /// Schedules the copy folder the uninstaller runs from for deletion at the next restart, because a running
        /// program cannot delete itself. The work folders around it go as well, which Windows does only when nothing
        /// else is left in them by then. A folder that is not a copy folder is never scheduled.
        /// </summary>
        /// <param name="copyFolder">Folder the uninstaller copy runs from.</param>
        /// <param name="schedule">Schedules one path; null uses the real restart scheduling.</param>
        public static void ScheduleCopyRemoval(string copyFolder, Func<string, bool>? schedule = null)
        {
            if (!IsTemporaryCopyFolder(copyFolder))
            {
                return;
            }

            // Files first: Windows deletes a folder at restart only when it is empty by then.
            schedule ??= UninstallerService.ScheduleDeleteOnReboot;
            var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(copyFolder));
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                schedule(file);
            }

            var copiesFolder = Path.GetDirectoryName(folder)!;
            schedule(folder);
            schedule(copiesFolder);
            schedule(Path.GetDirectoryName(copiesFolder)!);
        }

        /// <summary>
        /// Returns whether this process runs with administrator rights.
        /// </summary>
        public static bool IsElevated()
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }


    // Parsed command line

    /// <summary>
    /// Holds the parsed uninstaller command line.
    /// </summary>
    /// <param name="InstallDir">Shared installation folder to remove.</param>
    /// <param name="AllUsers">Whether ASLM is removed for every user.</param>
    /// <param name="WaitProcessId">Process id of the original uninstaller to wait for, or null.</param>
    /// <param name="WaitProcessStartTicks">Start time (UTC ticks) of that process, or null.</param>
    internal sealed record UninstallerArguments(
        string InstallDir,
        bool AllUsers,
        int? WaitProcessId,
        long? WaitProcessStartTicks);
}
