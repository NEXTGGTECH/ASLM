// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Installer.Uninstaller
{
    /// <summary>
    /// Removes ASLM from this computer: for the current user in this process, or for every user from an elevated
    /// temporary copy of the uninstaller.
    /// </summary>
    internal static class Program
    {
        // Process startup

        /// <summary>
        /// Shows the uninstaller.
        /// </summary>
        /// <returns>0 when ASLM was removed or the removal was handed to the elevated copy; 1 otherwise.</returns>
        [STAThread]
        private static int Main(string[] args)
        {
            // Task dialogs need visual styles, and their asynchronous page handlers continue on this thread through
            // the Windows Forms synchronization context.
            ApplicationConfiguration.Initialize();
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

            try
            {
                return Run(args) ? 0 : 1;
            }
            catch (Exception ex)
            {
                UninstallDialog.ShowError(ex.Message);
                return 1;
            }
        }

        /// <summary>
        /// Finds the installation, asks for whom ASLM is removed, and removes it here or hands the removal for every
        /// user to an elevated copy.
        /// </summary>
        /// <returns>True when ASLM was removed or the removal was handed to the elevated copy.</returns>
        private static bool Run(string[] args)
        {
            // The installation is the folder named on the command line, or the folder this program runs from.
            var arguments = UninstallerLaunch.Parse(args, AppContext.BaseDirectory);
            var service = new UninstallerService();
            var validation = service.ValidateInstallation(arguments.InstallDir);
            if (!validation.IsValid)
            {
                UninstallDialog.ShowError(validation.Message);
                return false;
            }

            var installation = validation.Installation!;
            var dialog = new UninstallDialog(service, installation);

            // The elevated copy removes ASLM for every user once the original uninstaller has exited, and leaves its
            // own folder to the next restart.
            var ownFolder = AppContext.BaseDirectory;
            if (arguments.AllUsers && UninstallerLaunch.IsTemporaryCopyFolder(ownFolder))
            {
                UninstallerLaunch.WaitForParentExit(arguments);
                try
                {
                    return dialog.Remove(UninstallScope.AllUsers);
                }
                finally
                {
                    UninstallerLaunch.ScheduleCopyRemoval(ownFolder);
                }
            }

            // A request for every user that does not come from a copy is handed to one like the choice below.
            var allUsersRequested = arguments.AllUsers;
            string? notice = null;
            while (true)
            {
                var scope = allUsersRequested ? UninstallScope.AllUsers : dialog.AskScope(notice);
                allUsersRequested = false;
                if (scope is null)
                {
                    return false;
                }

                if (scope == UninstallScope.CurrentUser)
                {
                    return dialog.Remove(UninstallScope.CurrentUser);
                }

                // The copy runs outside the shared folder, so it can delete this uninstaller as well.
                var copyPath = UninstallerLaunch.CreateTemporaryCopy(Environment.ProcessPath!, Path.GetTempPath());
                if (UninstallerLaunch.TryStartAllUsersCopy(copyPath, installation.SharedInstallPath))
                {
                    return true;
                }

                // The UAC prompt was declined: the copy is not needed, and the choice is offered again.
                UninstallerLaunch.DeleteTemporaryCopy(copyPath);
                notice = "Removing ASLM for all users needs administrator rights.";
            }
        }
    }
}
