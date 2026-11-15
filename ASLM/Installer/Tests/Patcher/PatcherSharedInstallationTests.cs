// Copyright NEXTGGTECH. Apache License 2.0.

using System.Text.Json;
using System.Text.Json.Nodes;
using ASLM.Installer.Tests.TestSupport;
using ASLM.Patcher;
using Microsoft.Win32;

namespace ASLM.Installer.Tests.Patcher
{
    /// <summary>
    /// Verifies how the patcher updates the shared installation folder of the Windows dual-location layout: which
    /// releases update it, the replacement with rollback, the entry in the installed applications, the elevation
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


        /// <summary>
        /// Four-part version of the release that the staged update carries.
        /// </summary>
        private const string UpdateVersion = "0.12.0.5";


        // Test data helpers

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
        /// Stages an update with a launcher, an uninstaller, the application and the downloaded archive.
        /// </summary>
        private void SeedStagedUpdate()
        {
            Directory.CreateDirectory(Path.Combine(StagingPath, "App"));
            File.WriteAllText(Path.Combine(StagingPath, "ASLM.exe"), "new launcher");
            File.WriteAllText(Path.Combine(StagingPath, "uninstall.exe"), "new uninstaller");
            File.WriteAllText(Path.Combine(StagingPath, "App", "ASLM.dll"), "new app");
            File.WriteAllText(ArchivePath, "new archive");
        }

        /// <summary>
        /// Writes the pending update descriptor of the staged update with the given release tag.
        /// </summary>
        private void WritePendingUpdate(string releaseTag)
        {
            var pending = new
            {
                kind = "app",
                version = releaseTag,
                stagingPath = StagingPath,
                targetRoot = UserDir,
                backupPath = Path.Combine(UserDir, ".aslm-update", "backup", "20260101000000"),
                archivePath = ArchivePath,
                preserve = new[] { "Data" }
            };
            File.WriteAllText(Path.Combine(UserDir, ".aslm-update", "pending.json"), JsonSerializer.Serialize(pending));
        }

        /// <summary>
        /// Builds the update description for the shared folder with a fresh backup folder.
        /// </summary>
        private PatcherRunner.SharedInstallationUpdate CreateUpdate() => new(
            SharedDir,
            StagingPath,
            ArchivePath,
            Path.Combine(UserDir, ".aslm-update", "backup", "now-shared"),
            UpdateVersion,
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
        /// Verifies that a stable release updates the shared folder when its global or sub version is newer than the
        /// recorded version or the manifest holds the placeholder of older installers, and that a release of the same
        /// global and sub version, an older release and a prerelease leave the folder as it is.
        /// </summary>
        [Theory]
        [InlineData("1.0", "v0.12.0.5", true)]
        [InlineData("0.11.2.3", "v0.12.0.5", true)]
        [InlineData("0.12.4.9", "v1.0.0.2", true)]
        [InlineData("0.12.0.1", "v0.12.3.40", false)]
        [InlineData("0.13.0.2", "v0.12.4.9", false)]
        [InlineData("1.2.0.7", "v0.13.0.1", false)]
        [InlineData("0.11.2.3", "v0.12.0.5-rc1", false)]
        [InlineData("1.0", "v0.12.0.5-rc1", false)]
        public void IsSharedUpdateNeeded_takes_stable_releases_of_a_newer_global_or_sub_version(
            string manifestVersion,
            string releaseTag,
            bool expected)
        {
            SeedSharedFolder(manifestVersion);

            PatcherRunner.IsSharedUpdateNeeded(SharedDir, releaseTag, out _).Should().Be(expected);
        }

        /// <summary>
        /// Verifies that a stable release tag gives its four-part version, an older three-part tag gets 0 as the last
        /// part, and a prerelease or any other text gives none.
        /// </summary>
        [Theory]
        [InlineData("v0.12.0.5", "0.12.0.5")]
        [InlineData(" 0.12.0.5 ", "0.12.0.5")]
        [InlineData("v0.9.1", "0.9.1.0")]
        [InlineData("v0.12.0.5-rc1", null)]
        [InlineData("v0.12", null)]
        [InlineData("v0.12.0.5.1", null)]
        [InlineData("v0.+12.0.5", null)]
        [InlineData("v0..0.5", null)]
        [InlineData("main@abc1234", null)]
        public void TryParseStableReleaseVersion_reads_only_stable_release_tags(string releaseTag, string? expected)
        {
            var parsed = PatcherRunner.TryParseStableReleaseVersion(releaseTag, out var version);

            parsed.Should().Be(expected is not null);
            (version?.ToString()).Should().Be(expected);
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

            File.ReadAllText(Path.Combine(SharedDir, "ASLM.exe")).Should().Be("new launcher");
            File.ReadAllText(Path.Combine(SharedDir, "uninstall.exe")).Should().Be("new uninstaller");
            File.ReadAllText(Path.Combine(SharedDir, "aslm-base.zip")).Should().Be("new archive");
            File.Exists(Path.Combine(SharedDir, "aslm-base.zip.tmp")).Should().BeFalse();
            Directory.Exists(update.BackupPath).Should().BeFalse();

            var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(SharedDir, "install-manifest.json")))!;
            manifest["version"]!.GetValue<string>().Should().Be(UpdateVersion);
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
            entry.GetValue("DisplayVersion").Should().Be(UpdateVersion);
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
            entry.GetValue("DisplayVersion").Should().Be(ownEntry ? UpdateVersion : "0.0.0.1");
        }


        // Per-user update and elevation hand-off

        /// <summary>
        /// Verifies that an update of the per-user folder in the dual-location layout never copies the shared files
        /// into it and removes copies an older patcher left there, while a shared folder that already records the same
        /// global and sub version stays as it is.
        /// </summary>
        [Fact]
        public async Task Per_user_update_keeps_the_shared_files_out_of_the_user_folder()
        {
            // Arrange: a per-user folder with a stale launcher copy and a pending update for it. The shared folder
            // already records the global and sub version of the update, so the run never touches it or the
            // machine-wide registry, even when the tests run with administrator rights.
            SeedSharedFolder("0.12.0.1");
            SeedStagedUpdate();
            Directory.CreateDirectory(Path.Combine(UserDir, "App"));
            File.WriteAllText(Path.Combine(UserDir, "App", "ASLM.dll"), "old app");
            File.WriteAllText(Path.Combine(UserDir, "ASLM.exe"), "stale launcher copy");
            WritePendingUpdate("v0.12.3.7");

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
        /// Verifies that the pending update asks for the elevated copy only when an existing shared folder takes it:
        /// a stable release of a newer global or sub version than the folder records.
        /// </summary>
        [Fact]
        public void RequiresSharedUpdate_needs_a_pending_update_that_the_shared_folder_takes()
        {
            string[] args = ["--root", UserDir, "--launcher", Path.Combine(SharedDir, "ASLM.exe")];
            SeedSharedFolder("1.0");
            SeedStagedUpdate();

            PatcherRunner.RequiresSharedUpdate(UserDir, args).Should().BeFalse("no update is pending");

            WritePendingUpdate("v0.12.0.5");
            PatcherRunner.RequiresSharedUpdate(UserDir, args).Should().BeTrue();

            WritePendingUpdate("v0.12.0.5-rc1");
            PatcherRunner.RequiresSharedUpdate(UserDir, args).Should().BeFalse("prereleases never update the shared folder");

            WritePendingUpdate("v0.12.0.5");
            WriteManifest("0.12.0.1");
            PatcherRunner.RequiresSharedUpdate(UserDir, args).Should().BeFalse(
                "the shared folder already records this global and sub version");
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
