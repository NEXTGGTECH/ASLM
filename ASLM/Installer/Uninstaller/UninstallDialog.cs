// Copyright NEXTGGTECH. Apache License 2.0.

using System.Text;

namespace ASLM.Installer.Uninstaller
{
    /// <summary>
    /// Shows the uninstaller as Windows task dialogs: the choice for whom ASLM is removed, the programs that must
    /// close first, the progress, and the result. One removal runs in a single dialog that moves between its pages.
    /// </summary>
    internal sealed class UninstallDialog
    {
        // Constants

        /// <summary>
        /// Title of every uninstaller dialog.
        /// </summary>
        private const string Caption = "ASLM Uninstaller";

        /// <summary>
        /// Most entries listed per group on the result page; the rest are only counted.
        /// </summary>
        private const int MaxListedEntries = 30;

        /// <summary>
        /// Time the running programs together get to close on their own before they are terminated.
        /// </summary>
        private static readonly TimeSpan CloseGracePeriod = TimeSpan.FromSeconds(10);


        // Dependencies and state

        /// <summary>
        /// Service that plans and performs the removal.
        /// </summary>
        private readonly UninstallerService _service;

        /// <summary>
        /// Installation that is removed.
        /// </summary>
        private readonly AslmInstallation _installation;

        /// <summary>
        /// Whether the removal of the current dialog ran to its end.
        /// </summary>
        private bool _removed;


        // Initialization

        /// <summary>
        /// Creates the dialogs for one installation.
        /// </summary>
        public UninstallDialog(UninstallerService service, AslmInstallation installation)
        {
            _service = service;
            _installation = installation;
        }


        // Scope choice

        /// <summary>
        /// Asks for whom ASLM is removed.
        /// </summary>
        /// <param name="notice">Note below the choice, such as why the last attempt stopped; null for none.</param>
        /// <returns>The chosen scope, or null when the user cancelled.</returns>
        public UninstallScope? AskScope(string? notice)
        {
            // The current user's folder is shown so the user sees what "only for me" deletes.
            var userAppPath = _service.CreatePlan(_installation, UninstallScope.CurrentUser).Users[0].UserAppPath;
            var details = new StringBuilder()
                .AppendLine($"Installation folder: {_installation.SharedInstallPath}")
                .Append($"Your ASLM folder: {userAppPath ?? "none"}")
                .ToString();

            var onlyForMe = new TaskDialogCommandLinkButton(
                "Only for me",
                "Deletes your ASLM folder with your settings, accounts, engines, models and modules. ASLM stays "
                + "installed for the other users.");
            var forAllUsers = new TaskDialogCommandLinkButton(
                "For all users",
                "Deletes ASLM from this computer together with the ASLM folders of every user.")
            {
                ShowShieldIcon = !UninstallerLaunch.IsElevated()
            };

            var page = new TaskDialogPage
            {
                Caption = Caption,
                Heading = "Remove ASLM",
                Text = "Choose for whom ASLM is removed. Deleted data cannot be restored.",
                Buttons = { onlyForMe, forAllUsers, TaskDialogButton.Cancel },
                Expander = CreateDetails(details),
                Footnote = notice is null ? null : new TaskDialogFootnote(notice) { Icon = TaskDialogIcon.Warning },
                AllowCancel = true
            };

            var choice = TaskDialog.ShowDialog(page, TaskDialogStartupLocation.CenterScreen);
            if (choice == onlyForMe)
            {
                return UninstallScope.CurrentUser;
            }

            return choice == forAllUsers ? UninstallScope.AllUsers : null;
        }


        // Removal

        /// <summary>
        /// Removes ASLM for a scope: asks to close the programs that hold its files, then shows the progress and the
        /// result in the same dialog.
        /// </summary>
        /// <returns>True when the removal ran to its end, even when some entries wait for a restart.</returns>
        public bool Remove(UninstallScope scope)
        {
            var plan = _service.CreatePlan(_installation, scope);
            _removed = false;
            TaskDialog.ShowDialog(CreateCheckedPage(plan), TaskDialogStartupLocation.CenterScreen);
            return _removed;
        }

        /// <summary>
        /// Checks for running programs and returns the page that follows: the list of programs when some still run,
        /// otherwise the progress page that starts the removal.
        /// </summary>
        private TaskDialogPage CreateCheckedPage(UninstallPlan plan)
        {
            var blocking = _service.FindBlockingProcesses(plan);
            return blocking.Count > 0
                ? CreateProcessesPage(plan, blocking)
                : CreateProgressPage(plan);
        }

        /// <summary>
        /// Creates the page that lists the programs holding files of the installation, with actions to close them
        /// or to check again after the user closed them.
        /// </summary>
        private TaskDialogPage CreateProcessesPage(UninstallPlan plan, IReadOnlyList<BlockingProcess> blocking)
        {
            var closeButton = new TaskDialogButton("Close programs", allowCloseDialog: false);
            var retryButton = new TaskDialogButton("Check again", allowCloseDialog: false);
            var page = new TaskDialogPage
            {
                Caption = Caption,
                Heading = "Close ASLM first",
                Text = "These programs use files that are removed:" + Environment.NewLine
                    + string.Join(Environment.NewLine, blocking.Select(process => $"{process.Name} (process {process.Id})")),
                Icon = TaskDialogIcon.Warning,
                Buttons = { closeButton, retryButton, TaskDialogButton.Cancel },
                Expander = CreateDetails(string.Join(Environment.NewLine, blocking.Select(process => process.ExecutablePath))),
                AllowCancel = true
            };

            retryButton.Click += (_, _) => NavigateSafely(page, () => CreateCheckedPage(plan));

            // Closing can take the whole grace period, so it runs off the UI thread behind a waiting page.
            closeButton.Click += async (_, _) =>
            {
                var closingPage = new TaskDialogPage
                {
                    Caption = Caption,
                    Heading = "Closing programs...",
                    ProgressBar = new TaskDialogProgressBar(TaskDialogProgressBarState.Marquee),
                    Buttons = { new TaskDialogButton("Close", enabled: false) },
                    AllowCancel = false
                };
                page.Navigate(closingPage);

                try
                {
                    await Task.Run(() => _service.CloseProcesses(blocking, CloseGracePeriod));
                }
                catch (Exception ex)
                {
                    closingPage.Navigate(CreateErrorPage(ex.Message));
                    return;
                }

                NavigateSafely(closingPage, () => CreateCheckedPage(plan));
            };

            return page;
        }

        /// <summary>
        /// Creates the page that runs the removal once it is shown and then moves on to the result.
        /// </summary>
        private TaskDialogPage CreateProgressPage(UninstallPlan plan)
        {
            var progressBar = new TaskDialogProgressBar { Minimum = 0, Maximum = 100 };
            var page = new TaskDialogPage
            {
                Caption = Caption,
                Heading = "Removing ASLM...",
                Text = "Preparing removal...",
                ProgressBar = progressBar,
                Buttons = { new TaskDialogButton("Close", enabled: false) },
                AllowCancel = false
            };

            // The removal runs off the UI thread; its progress reports arrive on the UI thread. Reports still queued
            // when the removal ends are dropped, because the dialog has moved on to the next page by then.
            page.Created += async (_, _) =>
            {
                var finished = false;
                var progress = new Progress<UninstallProgress>(update =>
                {
                    if (finished)
                    {
                        return;
                    }

                    page.Text = update.Message;
                    progressBar.Value = update.Percent;
                });

                TaskDialogPage nextPage;
                try
                {
                    var result = await Task.Run(() => _service.Uninstall(plan, progress));
                    _removed = true;
                    nextPage = CreateResultPage(plan, result);
                }
                catch (Exception ex)
                {
                    nextPage = CreateErrorPage(ex.Message);
                }

                finished = true;
                page.Navigate(nextPage);
            };

            return page;
        }

        /// <summary>
        /// Creates the page that reports the result and lists the entries that wait for a restart or stayed.
        /// </summary>
        private static TaskDialogPage CreateResultPage(UninstallPlan plan, UninstallResult result)
        {
            var forCurrentUser = plan.Scope == UninstallScope.CurrentUser;
            var page = new TaskDialogPage
            {
                Caption = Caption,
                Heading = forCurrentUser ? "ASLM has been removed for you" : "ASLM has been removed",
                Buttons = { TaskDialogButton.Close }
            };

            if (result.NotRemoved.Count > 0)
            {
                page.Icon = TaskDialogIcon.Warning;
                page.Heading = forCurrentUser ? "ASLM has been partly removed for you" : "ASLM has been partly removed";
                page.Text = "Some items could not be deleted. Close the programs that use them and delete the items "
                    + "listed in the details.";
            }
            else if (result.ScheduledForReboot.Count > 0)
            {
                page.Text = "Some files are in use. Windows deletes them when the computer restarts.";
            }

            if (!result.IsComplete)
            {
                page.Expander = CreateDetails(FormatEntries(result));
            }

            return page;
        }

        /// <summary>
        /// Creates the page that reports a removal that stopped with an error.
        /// </summary>
        private static TaskDialogPage CreateErrorPage(string message)
        {
            return new TaskDialogPage
            {
                Caption = Caption,
                Heading = "ASLM could not be removed",
                Text = message,
                Icon = TaskDialogIcon.Error,
                Buttons = { TaskDialogButton.Close }
            };
        }


        // Errors

        /// <summary>
        /// Shows an error that stops the uninstaller before any removal starts.
        /// </summary>
        public static void ShowError(string message)
        {
            TaskDialog.ShowDialog(
                new TaskDialogPage
                {
                    Caption = Caption,
                    Heading = "ASLM cannot be uninstalled",
                    Text = message,
                    Icon = TaskDialogIcon.Error,
                    Buttons = { TaskDialogButton.Close }
                },
                TaskDialogStartupLocation.CenterScreen);
        }


        // Page helpers

        /// <summary>
        /// Moves from one page to the page that a function creates; an error while creating it moves to the error
        /// page instead, so an event handler never ends with an unhandled exception.
        /// </summary>
        private static void NavigateSafely(TaskDialogPage current, Func<TaskDialogPage> createNext)
        {
            TaskDialogPage next;
            try
            {
                next = createNext();
            }
            catch (Exception ex)
            {
                next = CreateErrorPage(ex.Message);
            }

            current.Navigate(next);
        }

        /// <summary>
        /// Creates the collapsed details area of a page.
        /// </summary>
        private static TaskDialogExpander CreateDetails(string text)
        {
            return new TaskDialogExpander(text)
            {
                CollapsedButtonText = "Show details",
                ExpandedButtonText = "Hide details"
            };
        }

        /// <summary>
        /// Lists the entries that wait for a restart and the entries that stayed, each group cut to a readable length.
        /// </summary>
        private static string FormatEntries(UninstallResult result)
        {
            var builder = new StringBuilder();
            AppendGroup(builder, "Deleted when the computer restarts:", result.ScheduledForReboot);
            AppendGroup(builder, "Not deleted:", result.NotRemoved);
            return builder.ToString().TrimEnd();
        }

        /// <summary>
        /// Appends one titled group of entries; an empty group is left out.
        /// </summary>
        private static void AppendGroup(StringBuilder builder, string title, IReadOnlyList<string> entries)
        {
            if (entries.Count == 0)
            {
                return;
            }

            builder.AppendLine(title);
            foreach (var entry in entries.Take(MaxListedEntries))
            {
                builder.AppendLine(entry);
            }

            if (entries.Count > MaxListedEntries)
            {
                builder.AppendLine($"... and {entries.Count - MaxListedEntries} more");
            }

            builder.AppendLine();
        }
    }
}
