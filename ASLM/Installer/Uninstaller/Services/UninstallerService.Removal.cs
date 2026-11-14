// Copyright NEXTGGTECH. Apache License 2.0.

using System.Runtime.InteropServices;
using System.Security;

namespace ASLM.Installer.Uninstaller
{
    /// <summary>
    /// Removal part of the uninstaller service: deletes the shortcuts, the per-user folders with their preferences
    /// and temporary files, the shared folder and the registration, and leaves locked entries to the next restart.
    /// </summary>
    public sealed partial class UninstallerService
    {
        // Removal constants

        /// <summary>
        /// Number of attempts to delete one entry before it counts as locked.
        /// </summary>
        private const int DeleteAttempts = 3;

        /// <summary>
        /// <c>MoveFileEx</c> flag that defers the operation until the next restart; with no destination the entry is
        /// deleted then.
        /// </summary>
        private const uint MoveFileDelayUntilReboot = 0x4;


        // Removal workflow

        /// <summary>
        /// Removes everything in the plan. Entries that stay locked are scheduled for deletion at the next restart
        /// when Windows allows it, which needs administrator rights; otherwise they are reported as not removed.
        /// </summary>
        /// <param name="plan">Plan returned by <see cref="CreatePlan"/>.</param>
        /// <param name="progress">Receives monotonic progress updates from 0 to 100 percent.</param>
        /// <returns>The entries that wait for the next restart and the entries that could not be removed.</returns>
        public UninstallResult Uninstall(UninstallPlan plan, IProgress<UninstallProgress> progress)
        {
            var log = new RemovalLog();
            var steps = BuildSteps(plan, log);

            // Run the steps in order and spread them over the whole progress range.
            progress.Report(new UninstallProgress("Preparing removal...", 0));
            for (var index = 0; index < steps.Count; index++)
            {
                progress.Report(new UninstallProgress(steps[index].Message, index * 100 / steps.Count));
                steps[index].Run();
            }

            progress.Report(new UninstallProgress("ASLM has been removed.", 100));
            return log.ToResult();
        }

        /// <summary>
        /// Splits the plan into steps, so the progress moves while large per-user folders are removed.
        /// </summary>
        private List<RemovalStep> BuildSteps(UninstallPlan plan, RemovalLog log)
        {
            var steps = new List<RemovalStep>();

            // Shortcuts go first, so nothing starts the Launcher while its files disappear.
            if (plan.Shortcuts.Count > 0)
            {
                steps.Add(new RemovalStep("Removing shortcuts...", () => RemoveShortcuts(plan.Shortcuts, log)));
            }

            foreach (var user in plan.Users)
            {
                // Each top-level entry of a per-user folder is a step of its own; the emptied folder follows.
                if (user.UserAppPath is { } userAppPath && Directory.Exists(userAppPath))
                {
                    foreach (var entry in ListEntriesOrEmpty(new DirectoryInfo(userAppPath)))
                    {
                        steps.Add(new RemovalStep(
                            $"Removing {entry.Name} of {user.UserName}...",
                            () => DeleteEntry(entry, log, report: true)));
                    }

                    steps.Add(new RemovalStep(
                        $"Removing the ASLM folder of {user.UserName}...",
                        () => DeleteEntry(new DirectoryInfo(userAppPath), log, report: true)));
                }

                steps.Add(new RemovalStep(
                    $"Removing preferences and temporary files of {user.UserName}...",
                    () =>
                    {
                        RemoveLegacyPreferences(user.LocalAppDataPath, log);
                        RemoveTemporaryFiles(user.TempPath);
                    }));
            }

            // The shared folder and the registration go only when ASLM is removed for every user.
            if (plan.Scope == UninstallScope.AllUsers)
            {
                steps.Add(new RemovalStep(
                    "Removing the installation folder...",
                    () => RemoveSharedInstallation(plan.Installation, log)));
                steps.Add(new RemovalStep(
                    "Removing ASLM from the installed applications...",
                    () => RemoveRegistration(plan.Installation, log)));
            }

            return steps;
        }


        // Installation parts

        /// <summary>
        /// Deletes the shortcuts of the plan.
        /// </summary>
        private void RemoveShortcuts(IReadOnlyList<string> shortcuts, RemovalLog log)
        {
            foreach (var shortcut in shortcuts)
            {
                var file = new FileInfo(shortcut);
                if (file.Exists)
                {
                    DeleteEntry(file, log, report: true);
                }
            }
        }

        /// <summary>
        /// Deletes the files that the installer and the Launcher put into the shared folder, and the folder itself
        /// once nothing else is left in it. Files the user added there keep the folder and are not reported.
        /// </summary>
        private void RemoveSharedInstallation(AslmInstallation installation, RemovalLog log)
        {
            var sharedDir = new DirectoryInfo(installation.SharedInstallPath);
            if (!sharedDir.Exists)
            {
                return;
            }

            foreach (var fileName in SharedFileNames)
            {
                var file = new FileInfo(Path.Combine(sharedDir.FullName, fileName));
                if (file.Exists)
                {
                    DeleteEntry(file, log, report: true);
                }
            }

            // Foreign entries belong to the user, so the folder stays for them.
            var remaining = ListEntriesOrEmpty(sharedDir);
            if (remaining.Any(entry => !SharedFileNames.Contains(entry.Name, StringComparer.OrdinalIgnoreCase)))
            {
                return;
            }

            // Only locked installation files may be left; then the folder follows them at the next restart.
            if (remaining.Count > 0)
            {
                _scheduleDeleteOnReboot(sharedDir.FullName);
                return;
            }

            DeleteEntry(sharedDir, log, report: true);
        }

        /// <summary>
        /// Removes the ASLM entry from the list of installed applications when it belongs to this installation; an
        /// entry recorded for another folder stays.
        /// </summary>
        private void RemoveRegistration(AslmInstallation installation, RemovalLog log)
        {
            try
            {
                using var uninstallRoot = _openUninstallRoot();
                if (uninstallRoot is null)
                {
                    return;
                }

                // Without the recorded folder the entry cannot be attributed to this installation.
                var installLocation = AppRegistration.ReadInstallLocation(uninstallRoot);
                if (IsSamePath(installLocation, installation.SharedInstallPath))
                {
                    AppRegistration.Delete(uninstallRoot);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                log.AddNotRemoved(AppRegistration.DisplayKeyPath);
            }
        }

        /// <summary>
        /// Deletes the MAUI preferences that ASLM kept in the publisher folder of the local application data, and
        /// the publisher folder when nothing else is left in it.
        /// </summary>
        private void RemoveLegacyPreferences(string localAppData, RemovalLog log)
        {
            var publisherDir = new DirectoryInfo(Path.Combine(localAppData, LegacyPreferencesPublisherFolderName));
            var preferencesDir = new DirectoryInfo(Path.Combine(publisherDir.FullName, LegacyPreferencesFolderName));
            if (preferencesDir.Exists)
            {
                DeleteEntry(preferencesDir, log, report: true);
            }

            // Other applications built from the same template share the publisher folder.
            publisherDir.Refresh();
            if (publisherDir.Exists && ListEntriesOrEmpty(publisherDir).Count == 0)
            {
                DeleteEntry(publisherDir, log, report: true);
            }
        }

        /// <summary>
        /// Deletes the temporary folders of ASLM, its installer and its Launcher. Failures are not reported, because
        /// Windows cleans temporary folders itself and leftovers there do not affect the removal.
        /// </summary>
        private void RemoveTemporaryFiles(string tempPath)
        {
            var tempDir = new DirectoryInfo(tempPath);
            if (!tempDir.Exists)
            {
                return;
            }

            var ignoredFailures = new RemovalLog();
            foreach (var entry in ListEntriesOrEmpty(tempDir))
            {
                if (entry is DirectoryInfo && IsAslmTemporaryFolder(entry.Name))
                {
                    DeleteEntry(entry, ignoredFailures, report: false);
                }
            }
        }

        /// <summary>
        /// Returns whether a folder name in a temporary folder belongs to ASLM: the shared work folder, the download
        /// folder, the module work folders, or a copy from which the Launcher runs the patcher.
        /// </summary>
        internal static bool IsAslmTemporaryFolder(string name)
        {
            return string.Equals(name, AppName, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, DownloadsTempFolderName, StringComparison.OrdinalIgnoreCase) ||
                   HasGuidSuffix(name, PatcherCopyPrefix, "N") ||
                   HasGuidSuffix(name, ModuleSourceTempPrefix, "D") ||
                   HasGuidSuffix(name, ModuleInstallTempPrefix, "D");
        }

        /// <summary>
        /// Returns whether a name consists of the prefix followed by a GUID in the given format.
        /// </summary>
        private static bool HasGuidSuffix(string name, string prefix, string guidFormat)
        {
            return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                   Guid.TryParseExact(name[prefix.Length..], guidFormat, out _);
        }


        // File system removal

        /// <summary>
        /// Deletes one file, folder or link. A folder loses its contents first; a link or another reparse point is
        /// removed as it is, without touching its target. A locked entry is scheduled for deletion at the next
        /// restart or reported as not removed.
        /// </summary>
        /// <param name="entry">Entry to delete.</param>
        /// <param name="log">Collects the entries that wait for a restart or could not be removed.</param>
        /// <param name="report">Whether a failure goes into the result; false for best-effort cleanup.</param>
        /// <returns>True when the entry no longer exists.</returns>
        private bool DeleteEntry(FileSystemInfo entry, RemovalLog log, bool report)
        {
            // The folder the uninstaller runs from and everything inside it stay.
            if (IsSameOrChildPath(_protectedPath, entry.FullName))
            {
                return false;
            }

            // An entry that already failed was reported and scheduled once; a later step does not try it again.
            if (log.IsKept(entry.FullName))
            {
                return false;
            }

            entry.Refresh();
            if (!entry.Exists)
            {
                return true;
            }

            var isDirectory = entry is DirectoryInfo;
            if (isDirectory && !entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                // A folder whose contents cannot be listed cannot be emptied.
                if (!TryListEntries((DirectoryInfo)entry, out var children))
                {
                    log.AddFailure(entry.FullName, _scheduleDeleteOnReboot(entry.FullName), report);
                    return false;
                }

                var contentsRemoved = true;
                foreach (var child in children)
                {
                    contentsRemoved &= DeleteEntry(child, log, report);
                }

                // A folder that holds the protected path stays together with it.
                if (IsSameOrChildPath(entry.FullName, _protectedPath))
                {
                    return false;
                }

                // The contents that failed are already reported; the folder follows them at the next restart.
                if (!contentsRemoved)
                {
                    _scheduleDeleteOnReboot(entry.FullName);
                    log.AddKeptFolder(entry.FullName);
                    return false;
                }
            }

            if (TryDelete(entry, isDirectory))
            {
                return true;
            }

            log.AddFailure(entry.FullName, _scheduleDeleteOnReboot(entry.FullName), report);
            return false;
        }

        /// <summary>
        /// Deletes one file, empty folder or link, retrying while another program briefly holds it, for example an
        /// antivirus scan or the search indexer.
        /// </summary>
        /// <returns>True when the entry no longer exists.</returns>
        private bool TryDelete(FileSystemInfo entry, bool isDirectory)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    // Read-only entries cannot be deleted until the attribute is cleared.
                    entry.Refresh();
                    if (entry.Exists && entry.Attributes.HasFlag(FileAttributes.ReadOnly))
                    {
                        entry.Attributes &= ~FileAttributes.ReadOnly;
                    }

                    // Deleting a folder link removes only the link, never the folder it points to.
                    if (isDirectory)
                    {
                        Directory.Delete(entry.FullName, recursive: false);
                    }
                    else
                    {
                        File.Delete(entry.FullName);
                    }

                    return true;
                }
                catch (DirectoryNotFoundException)
                {
                    // Something else removed the entry meanwhile, which is the wanted outcome.
                    return true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (attempt >= DeleteAttempts)
                    {
                        return false;
                    }

                    Thread.Sleep(_retryDelay);
                }
            }
        }

        /// <summary>
        /// Lists the entries of a folder.
        /// </summary>
        /// <returns>False when the folder cannot be read.</returns>
        private static bool TryListEntries(DirectoryInfo directory, out List<FileSystemInfo> entries)
        {
            try
            {
                entries = directory.EnumerateFileSystemInfos().ToList();
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                entries = [];
                return false;
            }
        }

        /// <summary>
        /// Lists the entries of a folder, or returns an empty list when the folder cannot be read; the removal of an
        /// unreadable folder reports it.
        /// </summary>
        private static List<FileSystemInfo> ListEntriesOrEmpty(DirectoryInfo directory)
        {
            return TryListEntries(directory, out var entries) ? entries : [];
        }


        // Restart scheduling

        /// <summary>
        /// Schedules a file or a folder for deletion at the next restart, which needs administrator rights. Windows
        /// removes a folder then only when it is empty, so its contents must be scheduled before it.
        /// </summary>
        /// <returns>True when Windows accepted the request.</returns>
        internal static bool ScheduleDeleteOnReboot(string path)
        {
            return MoveFileEx(ToExtendedPath(path), newFileName: null, MoveFileDelayUntilReboot);
        }

        /// <summary>
        /// Prefixes an absolute path with the extended-length form, so the Win32 call accepts paths longer than 260
        /// characters.
        /// </summary>
        private static string ToExtendedPath(string path)
        {
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
            {
                return path;
            }

            return path.StartsWith(@"\\", StringComparison.Ordinal)
                ? @"\\?\UNC\" + path[2..]
                : @"\\?\" + path;
        }

        /// <summary>
        /// Moves or deletes a file or folder, here only deferred until the next restart (Unicode variant of the
        /// kernel32 <c>MoveFileEx</c>).
        /// </summary>
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MoveFileEx(string existingFileName, string? newFileName, uint flags);


        // Removal bookkeeping

        /// <summary>
        /// One step of the removal with the status text shown while it runs.
        /// </summary>
        /// <param name="Message">Status text shown next to the progress bar.</param>
        /// <param name="Run">Work of the step.</param>
        private sealed record RemovalStep(string Message, Action Run);

        /// <summary>
        /// Collects the entries that wait for the next restart and the entries that could not be removed, and
        /// remembers every entry that stayed, so later steps do not try it again.
        /// </summary>
        private sealed class RemovalLog
        {
            /// <summary>
            /// Entries that Windows deletes at the next restart.
            /// </summary>
            private readonly List<string> _scheduledForReboot = [];

            /// <summary>
            /// Entries that stay on the computer.
            /// </summary>
            private readonly List<string> _notRemoved = [];

            /// <summary>
            /// Every entry that stayed: failed entries, reported or not, and folders kept by their contents.
            /// </summary>
            private readonly HashSet<string> _keptPaths = new(StringComparer.OrdinalIgnoreCase);

            /// <summary>
            /// Records one entry whose deletion failed; unreported failures stay out of the result.
            /// </summary>
            /// <param name="path">Path of the entry.</param>
            /// <param name="scheduled">Whether the entry was scheduled for deletion at the next restart.</param>
            /// <param name="report">Whether the failure goes into the result.</param>
            public void AddFailure(string path, bool scheduled, bool report)
            {
                _keptPaths.Add(path);
                if (!report)
                {
                    return;
                }

                (scheduled ? _scheduledForReboot : _notRemoved).Add(path);
            }

            /// <summary>
            /// Records a folder that stays only because some of its contents stayed; it is not reported itself.
            /// </summary>
            public void AddKeptFolder(string path)
            {
                _keptPaths.Add(path);
            }

            /// <summary>
            /// Returns whether an entry already stayed in an earlier step.
            /// </summary>
            public bool IsKept(string path)
            {
                return _keptPaths.Contains(path);
            }

            /// <summary>
            /// Records one item that could not be removed and cannot be scheduled, such as a registry key.
            /// </summary>
            public void AddNotRemoved(string item)
            {
                _notRemoved.Add(item);
            }

            /// <summary>
            /// Returns the collected entries as the removal result.
            /// </summary>
            public UninstallResult ToResult()
            {
                return new UninstallResult(_scheduledForReboot.ToList(), _notRemoved.ToList());
            }
        }
    }
}
