// Copyright NEXTGGTECH. Apache License 2.0.

using System.Diagnostics;
using System.Text.Json;
using ASLM.Installer;
using ASLM.Installer.Tests.TestSupport;
using ASLM.Installer.Uninstaller;

namespace ASLM.Installer.Tests.Uninstaller
{
    /// <summary>
    /// Verifies the uninstaller logic against a fake computer: installation lookup, removal plans, running programs,
    /// and the removal itself, without any UI and without touching real profiles or the machine registry.
    /// </summary>
    public sealed class UninstallerServiceTests
    {
        // Installation lookup

        /// <summary>
        /// Verifies that a folder written by the real installer is accepted with its Launcher and per-user folder, and
        /// that removing it for all users leaves nothing of the installation behind, its registration included.
        /// </summary>
        [Fact]
        public async Task Installation_written_by_the_installer_is_found_and_removed_completely()
        {
            // Arrange: install with the real installer service into the fake computer and its registry.
            using var computer = new FakeComputer();
            var payloadPath = TestPayloadArchive.Write(computer.Root);
            using var env = new EnvironmentScope("ASLM_INSTALLER_PAYLOAD_PATH", payloadPath);
            var userAppDir = computer.UserAppDir(computer.CurrentProfile);
            var installer = new InstallerService(userAppDir: () => userAppDir, uninstallRoot: computer.Registry.OpenRoot);
            var options = new InstallOptions(
                Path.GetDirectoryName(computer.SharedDir)!, "ASLM", [],
                CreateDesktopShortcut: false, CreateStartMenuShortcut: false);
            await installer.InstallAsync(options, new Progress<InstallProgress>(), CancellationToken.None);
            var registeredBeforeRemoval = computer.IsRegistered();
            var service = computer.CreateService();

            // Act.
            var validation = service.ValidateInstallation(computer.SharedDir);
            var result = service.Uninstall(
                service.CreatePlan(validation.Installation!, UninstallScope.AllUsers),
                new CollectProgress());

            // Assert: the manifest described this folder, and both locations and the registration are gone.
            validation.IsValid.Should().BeTrue(validation.Message);
            validation.Installation!.LauncherPath.Should().Be(computer.LauncherPath);
            validation.Installation.UserAppPath.Should().Be(userAppDir);
            result.IsComplete.Should().BeTrue();
            registeredBeforeRemoval.Should().BeTrue();
            computer.IsRegistered().Should().BeFalse();
            Directory.Exists(computer.SharedDir).Should().BeFalse();
            Directory.Exists(userAppDir).Should().BeFalse();
        }

        /// <summary>
        /// Verifies that a portable copy, recognized by the application folder next to the Launcher, is refused with
        /// a message that tells the user to delete its folder.
        /// </summary>
        [Fact]
        public void ValidateInstallation_refuses_a_portable_copy()
        {
            using var computer = new FakeComputer();
            WriteFile(Path.Combine(computer.SharedDir, "App", "ASLM.exe"));

            var validation = computer.CreateService().ValidateInstallation(computer.SharedDir);

            validation.IsValid.Should().BeFalse();
            validation.Message.Should().Contain("portable");
        }

        /// <summary>
        /// Verifies that a folder without an install manifest is refused as containing no installation.
        /// </summary>
        [Fact]
        public void ValidateInstallation_refuses_a_folder_without_an_install_manifest()
        {
            using var computer = new FakeComputer();
            WriteFile(computer.LauncherPath);

            var validation = computer.CreateService().ValidateInstallation(computer.SharedDir);

            validation.IsValid.Should().BeFalse();
            validation.Message.Should().Contain("No ASLM installation");
        }

        /// <summary>
        /// Verifies that a manifest that records another shared folder is refused, so an uninstaller copied elsewhere
        /// removes nothing.
        /// </summary>
        [Fact]
        public void ValidateInstallation_refuses_a_manifest_of_another_folder()
        {
            using var computer = new FakeComputer();
            computer.WriteManifest(sharedInstallPath: Path.Combine(computer.Root, "Elsewhere", "ASLM"));

            var validation = computer.CreateService().ValidateInstallation(computer.SharedDir);

            validation.IsValid.Should().BeFalse();
            validation.Message.Should().Contain("another folder");
        }

        /// <summary>
        /// Verifies that a manifest that is not valid JSON is refused with a message saying it cannot be read.
        /// </summary>
        [Fact]
        public void ValidateInstallation_refuses_an_unreadable_manifest()
        {
            using var computer = new FakeComputer();
            WriteFile(Path.Combine(computer.SharedDir, "install-manifest.json"), "{ not json");

            var validation = computer.CreateService().ValidateInstallation(computer.SharedDir);

            validation.IsValid.Should().BeFalse();
            validation.Message.Should().Contain("cannot be read");
        }


        // Removal plan

        /// <summary>
        /// Verifies that the plan for the current user takes only the current user's folder and shortcuts, even
        /// when other profiles use the same installation.
        /// </summary>
        [Fact]
        public void CreatePlan_for_the_current_user_takes_only_the_current_user()
        {
            // Arrange: the current user and another profile both use this Launcher and have a desktop shortcut.
            using var computer = new FakeComputer();
            var installation = computer.Install();
            var alice = computer.AddProfile("alice");
            computer.CreateUserAppDir(computer.CurrentProfile, computer.LauncherPath);
            computer.CreateUserAppDir(alice, computer.LauncherPath);
            var currentShortcut = computer.CreateShortcut(computer.Desktop(computer.CurrentProfile), computer.SharedDir);
            computer.CreateShortcut(computer.Desktop(alice), computer.SharedDir);

            // Act.
            var plan = computer.CreateService().CreatePlan(installation, UninstallScope.CurrentUser);

            // Assert.
            plan.Users.Select(user => user.UserAppPath).Should().Equal(computer.UserAppDir(computer.CurrentProfile));
            plan.Shortcuts.Should().Equal(currentShortcut);
        }

        /// <summary>
        /// Verifies that the plan for all users takes every folder of this Launcher once: the current user's, the
        /// bound profiles', and the folder the installer filled for a user no profile lists. Folders of another
        /// Launcher, and shortcuts that point elsewhere, stay out of the plan.
        /// </summary>
        [Fact]
        public void CreatePlan_for_all_users_takes_every_folder_of_this_launcher()
        {
            // Arrange: profiles bound to this Launcher, to another Launcher, and without ASLM.
            using var computer = new FakeComputer();
            var installer = Path.Combine(computer.ProfilesRoot, "installer");
            var installation = computer.Install(userAppPath: computer.UserAppDir(installer));
            computer.AddProfile(Path.GetFileName(computer.CurrentProfile));
            var alice = computer.AddProfile("alice");
            var bob = computer.AddProfile("bob");
            computer.AddProfile("carol");
            computer.CreateUserAppDir(computer.CurrentProfile, computer.LauncherPath);
            computer.CreateUserAppDir(alice, computer.LauncherPath);
            computer.CreateUserAppDir(bob, Path.Combine(computer.Root, "Other", "ASLM.exe"));
            computer.CreateUserAppDir(installer, launcherReference: null);

            // Shortcuts: alice's desktop one points here, her Start menu one elsewhere, and the manifest records one
            // on a redirected desktop.
            var foreignInstall = Path.Combine(computer.Root, "Other");
            WriteFile(Path.Combine(foreignInstall, "ASLM.exe"));
            var aliceDesktop = computer.CreateShortcut(computer.Desktop(alice), computer.SharedDir);
            computer.CreateShortcut(computer.StartMenu(alice), foreignInstall);
            var redirected = computer.CreateShortcut(Path.Combine(computer.Root, "OneDrive", "Desktop"), computer.SharedDir);
            installation = installation with { Shortcuts = [redirected] };

            // Act.
            var plan = computer.CreateService().CreatePlan(installation, UninstallScope.AllUsers);

            // Assert.
            plan.Users.Select(user => user.UserAppPath).Should().Equal(
                computer.UserAppDir(computer.CurrentProfile),
                computer.UserAppDir(alice),
                computer.UserAppDir(installer));
            plan.Shortcuts.Should().BeEquivalentTo(new[] { aliceDesktop, redirected });
        }


        // Removal

        /// <summary>
        /// Verifies that removing for the current user deletes that user's folder, legacy preferences, ASLM
        /// temporary folders and shortcut, and keeps the shared folder, the registration, unrelated temporary
        /// folders, and the folders of other users.
        /// </summary>
        [Fact]
        public void Uninstall_for_the_current_user_keeps_the_shared_installation_and_other_users()
        {
            // Arrange: a registered installation used by the current user and by alice.
            using var computer = new FakeComputer();
            var installation = computer.Install();
            computer.Register(computer.SharedDir);
            var alice = computer.AddProfile("alice");
            var userAppDir = computer.CreateUserAppDir(computer.CurrentProfile, computer.LauncherPath);
            computer.CreateUserAppDir(alice, computer.LauncherPath);
            var shortcut = computer.CreateShortcut(computer.Desktop(computer.CurrentProfile), computer.SharedDir);
            var preferences = computer.CreateLegacyPreferences(computer.CurrentProfile);

            // Temporary folders of ASLM next to folders that only look similar.
            var temp = computer.TempDir(computer.CurrentProfile);
            string[] aslmTemp =
            [
                Path.Combine(temp, "ASLM", "Installer", "run"),
                Path.Combine(temp, "ASLM_Downloads", "item"),
                Path.Combine(temp, "Patcher_" + Guid.NewGuid().ToString("N")),
                Path.Combine(temp, "ASLM_ModuleSrc_" + Guid.NewGuid())
            ];
            string[] foreignTemp = [Path.Combine(temp, "Other"), Path.Combine(temp, "Patcher_backup")];
            foreach (var directory in aslmTemp.Concat(foreignTemp))
            {
                WriteFile(Path.Combine(directory, "file.txt"));
            }

            // Act.
            var service = computer.CreateService();
            var result = service.Uninstall(service.CreatePlan(installation, UninstallScope.CurrentUser), new CollectProgress());

            // Assert: the current user's traces are gone.
            result.IsComplete.Should().BeTrue();
            Directory.Exists(userAppDir).Should().BeFalse();
            File.Exists(shortcut).Should().BeFalse();
            Directory.Exists(preferences).Should().BeFalse();
            Directory.Exists(Path.GetDirectoryName(preferences)).Should().BeFalse("the publisher folder became empty");
            Directory.Exists(Path.Combine(temp, "ASLM")).Should().BeFalse();
            aslmTemp.Skip(1).Should().OnlyContain(directory => !Directory.Exists(directory));

            // Everything shared or foreign stays.
            foreignTemp.Should().OnlyContain(directory => Directory.Exists(directory));
            File.Exists(computer.LauncherPath).Should().BeTrue();
            computer.IsRegistered().Should().BeTrue();
            Directory.Exists(computer.UserAppDir(alice)).Should().BeTrue();
        }

        /// <summary>
        /// Verifies that removing for all users deletes the shared folder, the registration, every bound user's
        /// folder and shortcut, and the legacy preferences, while another application's preferences in the same
        /// publisher folder stay.
        /// </summary>
        [Fact]
        public void Uninstall_for_all_users_removes_every_trace_of_the_installation()
        {
            // Arrange: a registered installation used by the current user and by alice.
            using var computer = new FakeComputer();
            var installation = computer.Install();
            computer.Register(computer.SharedDir);
            var alice = computer.AddProfile("alice");
            var currentUserAppDir = computer.CreateUserAppDir(computer.CurrentProfile, computer.LauncherPath);
            var aliceUserAppDir = computer.CreateUserAppDir(alice, computer.LauncherPath);
            var shortcut = computer.CreateShortcut(computer.StartMenu(alice), computer.SharedDir);
            var preferences = computer.CreateLegacyPreferences(alice);
            var otherApplication = Path.Combine(Path.GetDirectoryName(preferences)!, "com.other.app");
            WriteFile(Path.Combine(otherApplication, "preferences.dat"));

            // Act.
            var service = computer.CreateService();
            var result = service.Uninstall(service.CreatePlan(installation, UninstallScope.AllUsers), new CollectProgress());

            // Assert.
            result.IsComplete.Should().BeTrue();
            Directory.Exists(computer.SharedDir).Should().BeFalse();
            computer.IsRegistered().Should().BeFalse();
            Directory.Exists(currentUserAppDir).Should().BeFalse();
            Directory.Exists(aliceUserAppDir).Should().BeFalse();
            File.Exists(shortcut).Should().BeFalse();
            Directory.Exists(preferences).Should().BeFalse();
            Directory.Exists(otherApplication).Should().BeTrue();
        }

        /// <summary>
        /// Verifies that files the user added to the shared folder keep the folder, while every installation file in
        /// it is deleted and nothing is reported.
        /// </summary>
        [Fact]
        public void Uninstall_keeps_foreign_files_in_the_shared_folder()
        {
            using var computer = new FakeComputer();
            var installation = computer.Install();
            var notes = WriteFile(Path.Combine(computer.SharedDir, "notes.txt"));

            var service = computer.CreateService();
            var result = service.Uninstall(service.CreatePlan(installation, UninstallScope.AllUsers), new CollectProgress());

            result.IsComplete.Should().BeTrue();
            File.Exists(notes).Should().BeTrue();
            Directory.EnumerateFileSystemEntries(computer.SharedDir).Should().Equal(notes);
        }

        /// <summary>
        /// Verifies that a registration that records another shared folder stays.
        /// </summary>
        [Fact]
        public void Uninstall_keeps_a_registration_of_another_folder()
        {
            using var computer = new FakeComputer();
            var installation = computer.Install();
            computer.Register(Path.Combine(computer.Root, "Elsewhere", "ASLM"));

            var service = computer.CreateService();
            service.Uninstall(service.CreatePlan(installation, UninstallScope.AllUsers), new CollectProgress());

            computer.IsRegistered().Should().BeTrue();
        }

        /// <summary>
        /// Verifies that a folder junction inside a per-user folder is removed as a link and its target with its files
        /// stays.
        /// </summary>
        [Fact]
        public void Uninstall_removes_links_without_touching_their_targets()
        {
            // Arrange: a module folder that is a junction to a folder outside the installation.
            using var computer = new FakeComputer();
            var installation = computer.Install();
            var userAppDir = computer.CreateUserAppDir(computer.CurrentProfile, computer.LauncherPath);
            var target = Path.Combine(computer.Root, "outside");
            var kept = WriteFile(Path.Combine(target, "keep.txt"));
            CreateJunction(Path.Combine(userAppDir, "Modules", "linked"), target);

            // Act.
            var service = computer.CreateService();
            var result = service.Uninstall(service.CreatePlan(installation, UninstallScope.CurrentUser), new CollectProgress());

            // Assert.
            result.IsComplete.Should().BeTrue();
            Directory.Exists(userAppDir).Should().BeFalse();
            File.Exists(kept).Should().BeTrue();
        }

        /// <summary>
        /// Verifies that read-only files, such as git objects of installed modules, are deleted.
        /// </summary>
        [Fact]
        public void Uninstall_removes_read_only_files()
        {
            using var computer = new FakeComputer();
            var installation = computer.Install();
            var userAppDir = computer.CreateUserAppDir(computer.CurrentProfile, computer.LauncherPath);
            var readOnly = WriteFile(Path.Combine(userAppDir, "Modules", "Chat", ".git", "objects", "pack.idx"));
            File.SetAttributes(readOnly, FileAttributes.ReadOnly);

            var service = computer.CreateService();
            var result = service.Uninstall(service.CreatePlan(installation, UninstallScope.CurrentUser), new CollectProgress());

            result.IsComplete.Should().BeTrue();
            Directory.Exists(userAppDir).Should().BeFalse();
        }

        /// <summary>
        /// Verifies that a locked file is scheduled for deletion at the next restart and reported alone, that its
        /// folders are scheduled after it, and that everything else is deleted.
        /// </summary>
        [Fact]
        public void Uninstall_schedules_locked_files_for_the_next_restart()
        {
            // Arrange: a model file that another program keeps open without sharing.
            using var computer = new FakeComputer();
            var installation = computer.Install();
            var userAppDir = computer.CreateUserAppDir(computer.CurrentProfile, computer.LauncherPath);
            var model = WriteFile(Path.Combine(userAppDir, "Models", "model.bin"));
            var service = computer.CreateService();
            UninstallResult result;

            // Act while the file is locked.
            using (new FileStream(model, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                result = service.Uninstall(service.CreatePlan(installation, UninstallScope.CurrentUser), new CollectProgress());
            }

            // Assert: only the file is reported, and Windows would delete it before its folders.
            result.ScheduledForReboot.Should().Equal(model);
            result.NotRemoved.Should().BeEmpty();
            computer.Scheduled.Should().ContainInOrder(model, Path.Combine(userAppDir, "Models"), userAppDir);
            Directory.Exists(Path.Combine(userAppDir, "App")).Should().BeFalse();
        }

        /// <summary>
        /// Verifies that a locked file that cannot be scheduled, as without administrator rights, is reported as not
        /// removed.
        /// </summary>
        [Fact]
        public void Uninstall_reports_locked_files_that_cannot_be_scheduled()
        {
            using var computer = new FakeComputer { CanSchedule = false };
            var installation = computer.Install();
            var userAppDir = computer.CreateUserAppDir(computer.CurrentProfile, computer.LauncherPath);
            var model = WriteFile(Path.Combine(userAppDir, "Models", "model.bin"));
            var service = computer.CreateService();
            UninstallResult result;

            using (new FileStream(model, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                result = service.Uninstall(service.CreatePlan(installation, UninstallScope.CurrentUser), new CollectProgress());
            }

            result.NotRemoved.Should().Equal(model);
            result.ScheduledForReboot.Should().BeEmpty();
        }

        /// <summary>
        /// Verifies that the folder the uninstaller runs from stays with its parents, while the rest of the ASLM
        /// temporary folder around it is deleted and nothing is reported.
        /// </summary>
        [Fact]
        public void Uninstall_never_touches_the_folder_it_runs_from()
        {
            // Arrange: the uninstaller runs from a copy inside the ASLM temporary folder.
            using var computer = new FakeComputer();
            var installation = computer.Install();
            var temp = computer.TempDir(computer.CurrentProfile);
            var running = Path.GetDirectoryName(WriteFile(Path.Combine(temp, "ASLM", "Uninstaller", "copy", "uninstall.exe")))!;
            var installerRun = Path.GetDirectoryName(WriteFile(Path.Combine(temp, "ASLM", "Installer", "run", "file.txt")))!;
            computer.ProtectedPath = running;

            // Act.
            var service = computer.CreateService();
            var result = service.Uninstall(service.CreatePlan(installation, UninstallScope.CurrentUser), new CollectProgress());

            // Assert.
            result.IsComplete.Should().BeTrue();
            File.Exists(Path.Combine(running, "uninstall.exe")).Should().BeTrue();
            Directory.Exists(installerRun).Should().BeFalse();
            computer.Scheduled.Should().BeEmpty();
        }

        /// <summary>
        /// Verifies that reported progress starts at 0, ends at 100, never decreases, and stays within 0 to 100.
        /// </summary>
        [Fact]
        public void Uninstall_reports_monotonic_progress_from_zero_to_hundred()
        {
            using var computer = new FakeComputer();
            var installation = computer.Install();
            computer.CreateUserAppDir(computer.CurrentProfile, computer.LauncherPath);
            var progress = new CollectProgress();

            var service = computer.CreateService();
            service.Uninstall(service.CreatePlan(installation, UninstallScope.AllUsers), progress);

            var percents = progress.Percentages;
            percents.First().Should().Be(0);
            percents.Last().Should().Be(100);
            percents.Should().BeInAscendingOrder("removal progress must never move backwards");
            percents.Where(percent => percent is > 100 or < 0).Should().BeEmpty();
        }

        /// <summary>
        /// Verifies which temporary folder names count as ASLM folders: the exact work and download folder names and
        /// the prefixed names followed by a GUID in the format ASLM uses, but nothing that only looks similar.
        /// </summary>
        [Theory]
        [InlineData("ASLM", true)]
        [InlineData("ASLM_Downloads", true)]
        [InlineData("Patcher_030f5475ac324420890a612efd3e3154", true)]
        [InlineData("ASLM_ModuleSrc_6f1c1e0e-6a0c-4c5e-9f39-1b1a0c8f3a2d", true)]
        [InlineData("ASLM_Module_Install_6f1c1e0e-6a0c-4c5e-9f39-1b1a0c8f3a2d", true)]
        [InlineData("ASLM-Tests", false)]
        [InlineData("Patcher_backup", false)]
        [InlineData("Patcher_6f1c1e0e-6a0c-4c5e-9f39-1b1a0c8f3a2d", false)]
        [InlineData("ASLM_ModuleSrc_030f5475ac324420890a612efd3e3154", false)]
        public void IsAslmTemporaryFolder_accepts_only_the_folders_aslm_creates(string name, bool expected)
        {
            UninstallerService.IsAslmTemporaryFolder(name).Should().Be(expected);
        }


        // Running programs

        /// <summary>
        /// Verifies that a program started from a per-user folder is reported as blocking, that closing it stops it
        /// together with its child process, and that the folder can then be removed.
        /// </summary>
        [Fact]
        public void Programs_running_from_removed_folders_are_found_and_closed()
        {
            // Arrange: a native console program copied into an engine folder runs a long ping as its child.
            using var computer = new FakeComputer();
            var installation = computer.Install();
            var userAppDir = computer.CreateUserAppDir(computer.CurrentProfile, computer.LauncherPath);
            var stubPath = Path.Combine(userAppDir, "Engines", "Stub", "stub.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(stubPath)!);
            File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), stubPath);
            using var process = Process.Start(new ProcessStartInfo(stubPath)
            {
                ArgumentList = { "/c", "ping -n 60 127.0.0.1 >nul" },
                CreateNoWindow = true,
                UseShellExecute = false
            })!;

            try
            {
                var service = computer.CreateService();
                var plan = service.CreatePlan(installation, UninstallScope.CurrentUser);

                // Act.
                var blocking = service.FindBlockingProcesses(plan);
                service.CloseProcesses(blocking, TimeSpan.FromMilliseconds(500));
                var afterClosing = service.FindBlockingProcesses(plan);
                var result = service.Uninstall(plan, new CollectProgress());

                // Assert.
                blocking.Should().ContainSingle(found => found.Id == process.Id)
                    .Which.Name.Should().Be("stub.exe");
                process.WaitForExit(TimeSpan.FromSeconds(10)).Should().BeTrue();
                afterClosing.Should().BeEmpty();
                result.IsComplete.Should().BeTrue();
                Directory.Exists(userAppDir).Should().BeFalse();
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
        }


        // Test helpers

        /// <summary>
        /// Writes a file with placeholder content, creating its folder.
        /// </summary>
        /// <returns>The full path of the file.</returns>
        private static string WriteFile(string path, string content = "content")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        /// <summary>
        /// Creates a folder junction, which needs no administrator rights or developer mode.
        /// </summary>
        private static void CreateJunction(string linkPath, string targetPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
            using var mklink = Process.Start(new ProcessStartInfo("cmd.exe")
            {
                ArgumentList = { "/c", "mklink", "/J", linkPath, targetPath },
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true
            })!;
            mklink.StandardOutput.ReadToEnd();
            mklink.WaitForExit();
            mklink.ExitCode.Should().Be(0, "mklink must create the junction");
        }


        // Fake computer

        /// <summary>
        /// Fake computer for one test: a shared folder, user profiles with their local application data, temporary
        /// and shortcut folders, and a registry key that stands in for the list of installed applications.
        /// </summary>
        private sealed class FakeComputer : IDisposable
        {
            /// <summary>
            /// Temp scope that holds every folder of the fake computer.
            /// </summary>
            public TempScope Scope { get; } = new();

            /// <summary>
            /// Registry key that stands in for the list of installed applications.
            /// </summary>
            public RegistryScope Registry { get; } = new();

            /// <summary>
            /// User profiles that the service reads instead of the real profile list.
            /// </summary>
            public List<UserProfileInfo> Profiles { get; } = [];

            /// <summary>
            /// Paths the service asked to delete at the next restart, in request order.
            /// </summary>
            public List<string> Scheduled { get; } = [];

            /// <summary>
            /// Whether restart scheduling succeeds, as it does with administrator rights.
            /// </summary>
            public bool CanSchedule { get; set; } = true;

            /// <summary>
            /// Folder the uninstaller runs from; an unused folder of the fake computer when null.
            /// </summary>
            public string? ProtectedPath { get; set; }

            /// <summary>
            /// Root folder of the fake computer in the long form the service works with; the temp scope folder may
            /// carry short (8.3) names.
            /// </summary>
            public string Root { get; }

            /// <summary>
            /// Creates the fake computer and resolves its root folder.
            /// </summary>
            public FakeComputer()
            {
                Root = UninstallerService.NormalizePath(Scope.Dir);
            }

            /// <summary>
            /// Shared installation folder.
            /// </summary>
            public string SharedDir => Path.Combine(Root, "Program Files", "ASLM");

            /// <summary>
            /// Launcher in the shared installation folder.
            /// </summary>
            public string LauncherPath => Path.Combine(SharedDir, "ASLM.exe");

            /// <summary>
            /// Folder that holds the user profiles.
            /// </summary>
            public string ProfilesRoot => Path.Combine(Root, "Users");

            /// <summary>
            /// Profile of the user who runs the uninstaller.
            /// </summary>
            public string CurrentProfile => Path.Combine(ProfilesRoot, "current");

            /// <summary>
            /// Returns the local application data folder of a profile.
            /// </summary>
            public string LocalAppData(string profile) => Path.Combine(profile, "AppData", "Local");

            /// <summary>
            /// Returns the per-user ASLM folder of a profile.
            /// </summary>
            public string UserAppDir(string profile) => Path.Combine(LocalAppData(profile), "ASLM");

            /// <summary>
            /// Returns the temporary folder of a profile.
            /// </summary>
            public string TempDir(string profile) => Path.Combine(LocalAppData(profile), "Temp");

            /// <summary>
            /// Returns the desktop folder of a profile.
            /// </summary>
            public string Desktop(string profile) => Path.Combine(profile, "Desktop");

            /// <summary>
            /// Returns the Start menu programs folder of a profile.
            /// </summary>
            public string StartMenu(string profile) =>
                Path.Combine(profile, "AppData", "Roaming", "Microsoft", "Windows", "Start Menu", "Programs");

            /// <summary>
            /// Creates an uninstaller service that sees only the fake computer.
            /// </summary>
            public UninstallerService CreateService()
            {
                return new UninstallerService(
                    localAppData: () => LocalAppData(CurrentProfile),
                    tempPath: () => TempDir(CurrentProfile),
                    userProfiles: () => Profiles,
                    shortcutFolders: () => [Desktop(CurrentProfile), StartMenu(CurrentProfile)],
                    uninstallRoot: Registry.OpenRoot,
                    scheduleDeleteOnReboot: path =>
                    {
                        Scheduled.Add(path);
                        return CanSchedule;
                    },
                    protectedPath: ProtectedPath ?? Path.Combine(Root, "uninstaller-home"),
                    retryDelay: TimeSpan.Zero);
            }

            /// <summary>
            /// Lays out the shared folder as the installer and the Launcher leave it and returns the validated
            /// installation.
            /// </summary>
            /// <param name="userAppPath">Per-user folder of the manifest; the current user's one when null.</param>
            public AslmInstallation Install(string? userAppPath = null)
            {
                foreach (var fileName in new[] { "ASLM.exe", "uninstall.exe", "aslm-base.zip", "Launcher.log" })
                {
                    WriteFile(Path.Combine(SharedDir, fileName));
                }

                WriteManifest(SharedDir, userAppPath ?? UserAppDir(CurrentProfile));
                var validation = CreateService().ValidateInstallation(SharedDir);
                validation.IsValid.Should().BeTrue(validation.Message);
                return validation.Installation!;
            }

            /// <summary>
            /// Writes an install manifest into the shared folder in the installer's camel-case format.
            /// </summary>
            /// <param name="sharedInstallPath">Shared folder recorded in the manifest.</param>
            /// <param name="userAppPath">Per-user folder recorded in the manifest.</param>
            public void WriteManifest(string sharedInstallPath, string? userAppPath = null)
            {
                var manifest = new
                {
                    app = "ASLM",
                    version = "v0.11.1",
                    installedAtUtc = DateTimeOffset.UtcNow,
                    sharedInstallPath,
                    userAppPath = userAppPath ?? UserAppDir(CurrentProfile),
                    acceptedDocuments = Array.Empty<object>()
                };
                WriteFile(Path.Combine(SharedDir, "install-manifest.json"), JsonSerializer.Serialize(manifest));
            }

            /// <summary>
            /// Adds a user profile that the service lists.
            /// </summary>
            /// <returns>The profile folder.</returns>
            public string AddProfile(string name)
            {
                var profile = Path.Combine(ProfilesRoot, name);
                Directory.CreateDirectory(profile);
                Profiles.Add(new UserProfileInfo(name, profile));
                return profile;
            }

            /// <summary>
            /// Fills the per-user ASLM folder of a profile with program and data folders.
            /// </summary>
            /// <param name="profile">Profile that receives the folder.</param>
            /// <param name="launcherReference">Launcher recorded in launcher-ref.json; no reference when null.</param>
            /// <returns>The per-user folder.</returns>
            public string CreateUserAppDir(string profile, string? launcherReference)
            {
                var userAppDir = UserAppDir(profile);
                WriteFile(Path.Combine(userAppDir, "App", "ASLM.exe"));
                WriteFile(Path.Combine(userAppDir, "Patcher", "ASLM Patcher.exe"));
                WriteFile(Path.Combine(userAppDir, "Data", "App", "ASLM_Data.json"), "{}");
                WriteFile(Path.Combine(userAppDir, "Models", "LLM", "weights.bin"));
                WriteFile(Path.Combine(userAppDir, ".aslm-update", "logs", "Patcher.log"));
                if (launcherReference is not null)
                {
                    WriteFile(
                        Path.Combine(userAppDir, "launcher-ref.json"),
                        JsonSerializer.Serialize(new { launcherPath = launcherReference }));
                }

                return userAppDir;
            }

            /// <summary>
            /// Creates the MAUI preferences folder that ASLM leaves in the publisher folder of a profile.
            /// </summary>
            /// <returns>The preferences folder.</returns>
            public string CreateLegacyPreferences(string profile)
            {
                var preferences = Path.Combine(LocalAppData(profile), "User Name", "com.nextggtech.aslm");
                WriteFile(Path.Combine(preferences, "Settings", "preferences.dat"));
                return preferences;
            }

            /// <summary>
            /// Creates a real ASLM shortcut in a folder that points at the Launcher of an installation folder.
            /// </summary>
            /// <returns>The shortcut path.</returns>
            public string CreateShortcut(string folder, string installDir)
            {
                InstallerService.CreateShortcut(folder, "ASLM.lnk", installDir);
                var shortcut = Path.Combine(folder, "ASLM.lnk");
                File.Exists(shortcut).Should().BeTrue("the installer helper must create the shortcut");
                return shortcut;
            }

            /// <summary>
            /// Registers an ASLM entry for a shared folder in the stand-in list of installed applications.
            /// </summary>
            public void Register(string installLocation)
            {
                using var root = Registry.OpenRoot()!;
                using var entry = root.CreateSubKey("ASLM");
                entry.SetValue("InstallLocation", installLocation);
            }

            /// <summary>
            /// Returns whether the stand-in list of installed applications still holds the ASLM entry.
            /// </summary>
            public bool IsRegistered()
            {
                using var root = Registry.OpenRoot()!;
                using var entry = root.OpenSubKey("ASLM");
                return entry is not null;
            }

            /// <summary>
            /// Deletes the fake computer's folders and registry key.
            /// </summary>
            public void Dispose()
            {
                Registry.Dispose();
                Scope.Dispose();
            }
        }


        // Progress collector

        /// <summary>
        /// Progress sink that records every report synchronously, so tests can assert the exact sequence instead of
        /// depending on <see cref="Progress{T}"/> posting to a synchronization context.
        /// </summary>
        private sealed class CollectProgress : IProgress<UninstallProgress>
        {
            /// <summary>
            /// All reports in the order in which they arrived.
            /// </summary>
            private readonly List<UninstallProgress> _reports = [];

            /// <summary>
            /// Percent values of all reports so far, in arrival order.
            /// </summary>
            public IReadOnlyList<int> Percentages => _reports.Select(report => report.Percent).ToList();

            /// <summary>
            /// Records one progress report.
            /// </summary>
            public void Report(UninstallProgress value) => _reports.Add(value);
        }
    }
}
