// Copyright NEXTGGTECH. Apache License 2.0.

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ASLM.Installer.Tests.TestSupport;
using ASLM.Patcher;
using Microsoft.Win32;

namespace ASLM.Installer.Tests.Patcher
{
    /// <summary>
    /// Verifies how the patcher updates the shared installation folder of the Windows dual-location layout: when an
    /// update is needed, the replacement with rollback, the entry in the installed applications, the elevation
    /// hand-off arguments, and the per-user copy that never receives the shared files.
    /// </summary>
    public sealed class PatcherSharedInstallationTests : IDisposable
    {
        // Fixture

        /// <summary>
        /// Temp scope that holds the shared folder, the per-user folder and the staged update.
        /// </summary>
        private readonly TempScope _scope = new();

        /// <summary>
        /// Registry key that stands in for the list of installed applications.
        /// </summary>
        private readonly RegistryScope _registry = new();

        /// <summary>
        /// Deletes the folders and the registry key of the test.
        /// </summary>
        public void Dispose()
        {
            _registry.Dispose();
            _scope.Dispose();
        }

        /// <summary>
        /// Shared installation folder.
        /// </summary>
        private string SharedDir => Path.Combine(_scope.Dir, "Program Files", "ASLM");

        /// <summary>
        /// Per-user application folder, which is the data root of the patcher.
        /// </summary>
        private string UserDir => Path.Combine(_scope.Dir, "Users", "current", "AppData", "Local", "ASLM");

        /// <summary>
        /// Staged payload of the update below the updater's staging folder.
        /// </summary>
        private string StagingPath => Path.Combine(UserDir, ".aslm-update", "staging", "v9", "extract", "ASLM-windows-amd64");

        /// <summary>
        /// Downloaded update archive below the updater's staging folder.
        /// </summary>
        private string ArchivePath => Path.Combine(UserDir, ".aslm-update", "staging", "v9", "ASLM-windows-amd64.zip");


        // Test data helpers

        /// <summary>
        /// Returns the four-part file version of the native executable that stands in for the new launcher.
        /// </summary>
        private static string StandInLauncherVersion()
        {
            var info = FileVersionInfo.GetVersionInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"));
            return $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}.{info.FilePrivatePart}";
        }

        /// <summary>
        /// Lays out the shared folder as the installer leaves it, with the given manifest version.
        /// </summary>
        private void SeedSharedFolder(string manifestVersion)
        {
            Directory.CreateDirectory(SharedDir);
            File.WriteAllText(Path.Combine(SharedDir, "ASLM.exe"), "old launcher");
            File.WriteAllText(Path.Combine(SharedDir, "uninstall.exe"), "old uninstaller");
            File.WriteAllText(Path.Combine(SharedDir, "aslm-base.zip"), "old archive");
            WriteManifest(manifestVersion);
        }

        /// <summary>
        /// Writes the shared install manifest in the installer's camel-case format.
        /// </summary>
        private void WriteManifest(string version)
        {
            var manifest = new { app = "ASLM", version, sharedInstallPath = SharedDir, userAppPath = UserDir };
            File.WriteAllText(Path.Combine(SharedDir, "install-manifest.json"), JsonSerializer.Serialize(manifest));
        }

        /// <summary>
        /// Stages an update whose launcher is a real executable with a version resource, plus an uninstaller and the
        /// downloaded archive.
        /// </summary>
        private void SeedStagedUpdate()
        {
            Directory.CreateDirectory(Path.Combine(StagingPath, "App"));
            File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), Path.Combine(StagingPath, "ASLM.exe"));
            File.WriteAllText(Path.Combine(StagingPath, "uninstall.exe"), "new uninstaller");
            File.WriteAllText(Path.Combine(StagingPath, "App", "ASLM.dll"), "new app");
            File.WriteAllText(ArchivePath, "new archive");
        }

        /// <summary>
        /// Builds the update description for the shared folder with a fresh backup folder.
        /// </summary>
        private PatcherRunner.SharedInstallationUpdate CreateUpdate() => new(
            SharedDir,
            StagingPath,
            ArchivePath,
            Path.Combine(UserDir, ".aslm-update", "backup", "now-shared"),
            StandInLauncherVersion(),
            UserDir);

        /// <summary>
        /// Registers an ASLM entry for a folder in the stand-in list of installed applications.
        /// </summary>
        private void Register(string installLocation, string version)
        {
            using var root = _registry.OpenRoot()!;
            using var entry = root.CreateSubKey("ASLM");
            entry.SetValue("InstallLocation", installLocation);
            entry.SetValue("DisplayVersion", version);
        }

        /// <summary>
        /// Opens the ASLM entry of the stand-in list of installed applications, or returns null when it is missing.
        /// </summary>
        private RegistryKey? OpenRegistration()
        {
            using var root = _registry.OpenRoot()!;
            return root.OpenSubKey("ASLM");
        }


        // Update decision

        /// <summary>
        /// Verifies that the shared folder is updated when its manifest holds the placeholder of older installers or
        /// an older version, and not when it holds the same or a newer version or the update has no versioned
        /// launcher.
        /// </summary>
        [Theory]
        [InlineData("1.0", true)]
        [InlineData("0.11.1.22", true)]
        [InlineData("same", false)]
        [InlineData("99999.0.0.0", false)]
        public void IsSharedUpdateNeeded_compares_the_recorded_four_part_version(string manifestVersion, bool expected)
        {
            SeedSharedFolder(manifestVersion == "same" ? StandInLauncherVersion() : manifestVersion);
            SeedStagedUpdate();

            PatcherRunner.IsSharedUpdateNeeded(SharedDir, StagingPath, out var version).Should().Be(expected);
            version.Should().Be(StandInLauncherVersion());
        }

        /// <summary>
        /// Verifies that an update whose launcher has no version resource never replaces the shared folder.
        /// </summary>
        [Fact]
        public void IsSharedUpdateNeeded_ignores_an_update_without_a_versioned_launcher()
        {
            SeedSharedFolder("1.0");
            Directory.CreateDirectory(StagingPath);
            File.WriteAllText(Path.Combine(StagingPath, "ASLM.exe"), "no version resource");

            PatcherRunner.IsSharedUpdateNeeded(SharedDir, StagingPath, out _).Should().BeFalse();
        }

        /// <summary>
        /// Verifies that the shared folder is the launcher's folder only when its manifest names exactly that folder
        /// and it differs from the per-user folder.
        /// </summary>
        [Fact]
        public void ResolveSharedInstallDir_requires_a_manifest_for_the_launcher_folder()
        {
            SeedSharedFolder("1.0");
            string[] args = ["--root", UserDir, "--launcher", Path.Combine(SharedDir, "ASLM.exe")];

            PatcherRunner.ResolveSharedInstallDir(UserDir, UserDir, args).Should().Be(SharedDir);

            // A launcher next to the per-user folder is the portable layout, which has no shared folder.
            PatcherRunner.ResolveSharedInstallDir(SharedDir, SharedDir, args).Should().BeNull();

            // A manifest of another folder does not prove an installation here.
            File.WriteAllText(
                Path.Combine(SharedDir, "install-manifest.json"),
                JsonSerializer.Serialize(new { app = "ASLM", sharedInstallPath = Path.Combine(_scope.Dir, "Other") }));
            PatcherRunner.ResolveSharedInstallDir(UserDir, UserDir, args).Should().BeNull();
        }


        // Replacement

        /// <summary>
        /// Verifies that the shared launcher, uninstaller and payload archive are replaced, that the manifest
        /// records the new version, and that no temporary archive or backup stays behind.
        /// </summary>
        [Fact]
        public void UpdateSharedInstallation_replaces_the_shared_files_and_records_the_version()
        {
            SeedSharedFolder("1.0");
            SeedStagedUpdate();
            var update = CreateUpdate();

            PatcherRunner.UpdateSharedInstallation(update, _registry.OpenRoot).Should().BeTrue();

            File.ReadAllBytes(Path.Combine(SharedDir, "ASLM.exe")).Should().Equal(File.ReadAllBytes(Path.Combine(StagingPath, "ASLM.exe")));
            File.ReadAllText(Path.Combine(SharedDir, "uninstall.exe")).Should().Be("new uninstaller");
            File.ReadAllText(Path.Combine(SharedDir, "aslm-base.zip")).Should().Be("new archive");
            File.Exists(Path.Combine(SharedDir, "aslm-base.zip.tmp")).Should().BeFalse();
            Directory.Exists(update.BackupPath).Should().BeFalse();

            var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(SharedDir, "install-manifest.json")))!;
            manifest["version"]!.GetValue<string>().Should().Be(StandInLauncherVersion());
            manifest["userAppPath"]!.GetValue<string>().Should().Be(UserDir, "the other manifest fields stay");
        }

        /// <summary>
        /// Verifies that a failure after the files were replaced restores the previous shared files and removes the
        /// backup.
        /// </summary>
        [Fact]
        public void UpdateSharedInstallation_restores_the_previous_files_after_a_failure()
        {
            // Arrange: the manifest cannot be parsed, which fails the update after the executables were replaced.
            SeedSharedFolder("1.0");
            SeedStagedUpdate();
            File.WriteAllText(Path.Combine(SharedDir, "install-manifest.json"), "{ broken");
            var update = CreateUpdate();

            // Act.
            var updated = PatcherRunner.UpdateSharedInstallation(update, _registry.OpenRoot);

            // Assert.
            updated.Should().BeFalse();
            File.ReadAllText(Path.Combine(SharedDir, "ASLM.exe")).Should().Be("old launcher");
            File.ReadAllText(Path.Combine(SharedDir, "uninstall.exe")).Should().Be("old uninstaller");
            File.ReadAllText(Path.Combine(SharedDir, "aslm-base.zip")).Should().Be("old archive");
            File.Exists(Path.Combine(SharedDir, "aslm-base.zip.tmp")).Should().BeFalse();
            Directory.Exists(update.BackupPath).Should().BeFalse();
        }


        // Installed applications

        /// <summary>
        /// Verifies that an installation made before the installer registered itself gets a complete entry, which
        /// makes its uninstaller reachable from the Windows settings.
        /// </summary>
        [Fact]
        public void UpdateSharedInstallation_registers_an_installation_without_an_entry()
        {
            SeedSharedFolder("1.0");
            SeedStagedUpdate();

            PatcherRunner.UpdateSharedInstallation(CreateUpdate(), _registry.OpenRoot).Should().BeTrue();

            using var entry = OpenRegistration();
            entry.Should().NotBeNull();
            entry!.GetValue("InstallLocation").Should().Be(SharedDir);
            entry.GetValue("DisplayVersion").Should().Be(StandInLauncherVersion());
            entry.GetValue("UninstallString").Should().Be($"\"{Path.Combine(SharedDir, "uninstall.exe")}\"");
        }

        /// <summary>
        /// Verifies that the entry of this installation gets the new version, while an entry of another folder
        /// stays as it is.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void UpdateSharedInstallation_updates_only_the_entry_of_this_folder(bool ownEntry)
        {
            SeedSharedFolder("1.0");
            SeedStagedUpdate();
            var location = ownEntry ? SharedDir : Path.Combine(_scope.Dir, "Elsewhere", "ASLM");
            Register(location, "0.0.0.1");

            PatcherRunner.UpdateSharedInstallation(CreateUpdate(), _registry.OpenRoot).Should().BeTrue();

            using var entry = OpenRegistration();
            entry!.GetValue("InstallLocation").Should().Be(location);
            entry.GetValue("DisplayVersion").Should().Be(ownEntry ? StandInLauncherVersion() : "0.0.0.1");
        }


        // Per-user update and elevation hand-off

        /// <summary>
        /// Verifies that an update of the per-user folder in the dual-location layout never copies the shared files
        /// into it and removes copies an older patcher left there, while a shared folder that already has the new
        /// launcher stays as it is.
        /// </summary>
        [Fact]
        public async Task Per_user_update_keeps_the_shared_files_out_of_the_user_folder()
        {
            // Arrange: a per-user folder with a stale launcher copy and a pending update for it. The shared folder
            // already records the new version, so the run never touches it or the machine-wide registry, even when
            // the tests run with administrator rights.
            SeedSharedFolder(StandInLauncherVersion());
            SeedStagedUpdate();
            Directory.CreateDirectory(Path.Combine(UserDir, "App"));
            File.WriteAllText(Path.Combine(UserDir, "App", "ASLM.dll"), "old app");
            File.WriteAllText(Path.Combine(UserDir, "ASLM.exe"), "stale launcher copy");
            var pending = new
            {
                kind = "app",
                version = "v9",
                stagingPath = StagingPath,
                targetRoot = UserDir,
                backupPath = Path.Combine(UserDir, ".aslm-update", "backup", "20260101000000"),
                archivePath = ArchivePath,
                preserve = new[] { "Data" }
            };
            File.WriteAllText(Path.Combine(UserDir, ".aslm-update", "pending.json"), JsonSerializer.Serialize(pending));

            // Act: the elevated marker keeps the patcher from starting the fake launcher afterwards.
            var exitCode = await PatcherRunner.RunAsync(
                ["--root", UserDir, "--launcher", Path.Combine(SharedDir, "ASLM.exe"), "--elevated"],
                progress: null);

            // Assert: the per-user folder is updated without the shared files, and the shared folder is unchanged.
            exitCode.Should().Be(0);
            File.ReadAllText(Path.Combine(UserDir, "App", "ASLM.dll")).Should().Be("new app");
            File.Exists(Path.Combine(UserDir, "ASLM.exe")).Should().BeFalse();
            File.Exists(Path.Combine(UserDir, "uninstall.exe")).Should().BeFalse();
            File.ReadAllText(Path.Combine(SharedDir, "ASLM.exe")).Should().Be("old launcher");
        }

        /// <summary>
        /// Verifies that the pending update asks for the elevated copy only when it carries a newer launcher for an
        /// existing shared folder.
        /// </summary>
        [Fact]
        public void RequiresSharedUpdate_needs_a_pending_update_with_a_newer_launcher()
        {
            string[] args = ["--root", UserDir, "--launcher", Path.Combine(SharedDir, "ASLM.exe")];
            SeedSharedFolder("1.0");
            SeedStagedUpdate();

            PatcherRunner.RequiresSharedUpdate(UserDir, args).Should().BeFalse("no update is pending");

            File.WriteAllText(
                Path.Combine(UserDir, ".aslm-update", "pending.json"),
                JsonSerializer.Serialize(new { kind = "app", version = "v9", stagingPath = StagingPath, targetRoot = UserDir }));
            PatcherRunner.RequiresSharedUpdate(UserDir, args).Should().BeTrue();

            WriteManifest(StandInLauncherVersion());
            PatcherRunner.RequiresSharedUpdate(UserDir, args).Should().BeFalse("the shared folder already has this launcher");
        }

        /// <summary>
        /// Verifies that the elevated copy receives the same arguments with the elevated marker among the control
        /// arguments, quoted by the Windows rules.
        /// </summary>
        [Fact]
        public void BuildElevatedArguments_adds_the_marker_before_the_forwarded_arguments()
        {
            string[] args = ["--root", @"C:\Users\A B\AppData\Local\ASLM", "--wait-process", "42", "--", "--open", "x y"];

            var commandLine = PatcherRunner.BuildElevatedArguments(args);

            commandLine.Should().Be(
                "--root \"C:\\Users\\A B\\AppData\\Local\\ASLM\" --wait-process 42 --elevated -- --open \"x y\"");
        }
    }
}
