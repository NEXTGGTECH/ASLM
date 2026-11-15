// Copyright NEXTGGTECH. Apache License 2.0.

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ASLM.Installer.Tests.TestSupport;
using ASLM.Patcher;

namespace ASLM.Installer.Tests.Patcher
{
    /// <summary>
    /// Verifies how the patcher updates the shared installation folder of the Linux dual-location layout: the folder
    /// that an installer without a manifest set up, the replacement of the launcher, the base archive, the icon and the
    /// manifest with rollback, the shared folder helper that pkexec starts with the arguments the patcher builds, and
    /// the handling of the helper's output and exit code. The Linux folder layout runs here in temp folders.
    /// </summary>
    public sealed class PatcherLinuxSharedInstallationTests : IDisposable
    {
        // Fixture

        /// <summary>
        /// Four-part version of the release that the staged update carries.
        /// </summary>
        private const string UpdateVersion = "0.12.0.5";

        /// <summary>
        /// Temp scope that holds the shared folder, the per-user folder and the staged update.
        /// </summary>
        private readonly TempScope _scope = new();

        /// <summary>
        /// Deletes the folders of the test.
        /// </summary>
        public void Dispose()
        {
            _scope.Dispose();
        }

        /// <summary>
        /// Shared installation folder, which stands in for <c>/opt/ASLM</c>.
        /// </summary>
        private string SharedDir => Path.Combine(_scope.Dir, "opt", "ASLM");

        /// <summary>
        /// Per-user application folder, which is the data root of the patcher.
        /// </summary>
        private string UserDir => Path.Combine(_scope.Dir, "home", "user", ".local", "share", "ASLM");

        /// <summary>
        /// Staged payload of the update below the updater's staging folder.
        /// </summary>
        private string StagingPath =>
            Path.Combine(UserDir, ".aslm-update", "staging", "v0.12.0.5", "extract", "ASLM-linux-amd64");

        /// <summary>
        /// Downloaded release archive below the updater's staging folder.
        /// </summary>
        private string ArchivePath => Path.Combine(UserDir, ".aslm-update", "staging", "v0.12.0.5", "ASLM-linux-amd64.tar.gz");

        /// <summary>
        /// Work folder of the shared folder helper inside the shared folder.
        /// </summary>
        private string WorkDir => Path.Combine(SharedDir, ".aslm-shared-update");


        // Shadow copy cleanup

        /// <summary>
        /// Verifies that cleanup accepts only generated helpers directly below the requested profile's update folder.
        /// </summary>
        [Fact]
        public void Shadow_cleanup_only_accepts_the_requested_profiles_generated_helper()
        {
            var name = Guid.NewGuid().ToString("N");
            var helper = Path.Combine(UserDir, ".aslm-update", "helpers", name);
            Directory.CreateDirectory(helper);
            PatcherRunner.IsLinuxHelperDirectory(helper, UserDir).Should().BeTrue();
            PatcherRunner.IsLinuxHelperDirectory(helper, SharedDir).Should().BeFalse();
            PatcherRunner.IsLinuxHelperDirectory(Path.Combine(UserDir, "Patcher"), UserDir).Should().BeFalse();
            PatcherRunner.IsLinuxHelperDirectory(Path.Combine(UserDir, ".aslm-update", "helpers", "installed"), UserDir)
                .Should().BeFalse();
            PatcherRunner.IsLinuxHelperDirectory(Path.Combine(helper, name), UserDir).Should().BeFalse();
        }


        // Test data helpers

        /// <summary>
        /// Lays out the shared folder as the first Linux installers left it: the launcher, the base archive and the
        /// icon, without an install manifest.
        /// </summary>
        private void SeedSharedFolderWithoutManifest()
        {
            Directory.CreateDirectory(SharedDir);
            File.WriteAllText(Path.Combine(SharedDir, "ASLM"), "old launcher");
            File.WriteAllText(Path.Combine(SharedDir, "aslm-base.tar.gz"), "old archive");
            File.WriteAllText(Path.Combine(SharedDir, "appicon.svg"), "old icon");
        }

        /// <summary>
        /// Writes the install manifest that the installer writes, with one more field that an update must keep.
        /// </summary>
        private void WriteManifest(string version)
        {
            var manifest = new { app = "ASLM", version, sharedInstallPath = SharedDir, installedAtUtc = "kept" };
            File.WriteAllText(Path.Combine(SharedDir, "install-manifest.json"), JsonSerializer.Serialize(manifest));
        }

        /// <summary>
        /// Stages the Linux release payload with its launcher, application, patcher and icon, plus the downloaded
        /// archive.
        /// </summary>
        private void SeedStagedUpdate()
        {
            Directory.CreateDirectory(Path.Combine(StagingPath, "App", "hicolor", "scalable", "apps"));
            Directory.CreateDirectory(Path.Combine(StagingPath, "Patcher"));
            File.WriteAllText(Path.Combine(StagingPath, "ASLM"), "new launcher");
            File.WriteAllText(Path.Combine(StagingPath, "App", "ASLM"), "new app");
            File.WriteAllText(Path.Combine(StagingPath, "App", "hicolor", "scalable", "apps", "appicon.svg"), "new icon");
            File.WriteAllText(Path.Combine(StagingPath, "Patcher", "aslm-patcher"), "new patcher");
            File.WriteAllText(ArchivePath, "new archive");
        }

        /// <summary>
        /// Builds the update description of the staged update.
        /// </summary>
        private PatcherRunner.LinuxSharedInstallationUpdate CreateUpdate() =>
            new(SharedDir, StagingPath, ArchivePath, UpdateVersion);

        /// <summary>
        /// Reads the install manifest of the shared folder.
        /// </summary>
        private JsonNode ReadManifest() =>
            JsonNode.Parse(File.ReadAllText(Path.Combine(SharedDir, "install-manifest.json")))!;

        /// <summary>
        /// Asserts that the shared folder holds the files of the staged update and records its version, and that the
        /// work folder is gone.
        /// </summary>
        private void AssertSharedFolderUpdated()
        {
            File.ReadAllText(Path.Combine(SharedDir, "ASLM")).Should().Be("new launcher");
            File.ReadAllText(Path.Combine(SharedDir, "aslm-base.tar.gz")).Should().Be("new archive");
            File.ReadAllText(Path.Combine(SharedDir, "appicon.svg")).Should().Be("new icon");
            ReadManifest()["version"]!.GetValue<string>().Should().Be(UpdateVersion);
            Directory.Exists(WorkDir).Should().BeFalse();
        }


        // Shared folder detection

        /// <summary>
        /// Verifies that in the Linux layout a launcher folder without a manifest counts as the shared folder when it
        /// holds the launcher and the base archive, that the Windows layout requires a manifest, and that a launcher
        /// folder that is the updated folder itself, as in the portable layout, is never the shared folder.
        /// </summary>
        [Fact]
        public void ResolveSharedInstallDir_accepts_a_linux_folder_without_a_manifest()
        {
            SeedSharedFolderWithoutManifest();
            string[] args = ["--root", UserDir, "--launcher", Path.Combine(SharedDir, "ASLM")];

            PatcherRunner.ResolveSharedInstallDir(UserDir, UserDir, args, linuxLayout: true).Should().Be(SharedDir);
            PatcherRunner.ResolveSharedInstallDir(UserDir, UserDir, args, linuxLayout: false).Should().BeNull();
            PatcherRunner.ResolveSharedInstallDir(UserDir, SharedDir, args, linuxLayout: true).Should().BeNull();

            File.Delete(Path.Combine(SharedDir, "aslm-base.tar.gz"));
            PatcherRunner.ResolveSharedInstallDir(UserDir, UserDir, args, linuxLayout: true).Should().BeNull(
                "a launcher without the base archive is no installation folder");
        }

        /// <summary>
        /// Verifies that a folder without a manifest has an unknown version, so a stable release updates it while a
        /// prerelease does not.
        /// </summary>
        [Fact]
        public void IsSharedUpdateNeeded_treats_a_folder_without_a_manifest_as_unknown()
        {
            SeedSharedFolderWithoutManifest();

            PatcherRunner.IsSharedUpdateNeeded(SharedDir, "v0.12.0.5", out var version).Should().BeTrue();
            version.Should().Be(UpdateVersion);
            PatcherRunner.IsSharedUpdateNeeded(SharedDir, "v0.12.0.5-rc1", out _).Should().BeFalse();
        }


        // Replacement

        /// <summary>
        /// Verifies that the launcher, the base archive and the icon are replaced, that a folder without a manifest
        /// gets one for itself with the new version, and that no work folder stays behind.
        /// </summary>
        [Fact]
        public void UpdateLinuxSharedInstallation_replaces_the_shared_files_and_writes_the_manifest()
        {
            SeedSharedFolderWithoutManifest();
            SeedStagedUpdate();

            PatcherRunner.UpdateLinuxSharedInstallation(CreateUpdate()).Should().BeTrue();

            AssertSharedFolderUpdated();
            ReadManifest()["app"]!.GetValue<string>().Should().Be("ASLM");
            ReadManifest()["sharedInstallPath"]!.GetValue<string>().Should().Be(SharedDir);
            PatcherRunner.IsSharedInstallDir(SharedDir, linuxLayout: false).Should().BeTrue(
                "the written manifest names this folder");
        }

        /// <summary>
        /// Verifies that an update of a folder with a manifest records the new version and keeps the other manifest
        /// fields.
        /// </summary>
        [Fact]
        public void UpdateLinuxSharedInstallation_keeps_the_other_manifest_fields()
        {
            SeedSharedFolderWithoutManifest();
            WriteManifest("0.11.2.3");
            SeedStagedUpdate();

            PatcherRunner.UpdateLinuxSharedInstallation(CreateUpdate()).Should().BeTrue();

            AssertSharedFolderUpdated();
            ReadManifest()["installedAtUtc"]!.GetValue<string>().Should().Be("kept");
        }

        /// <summary>
        /// Verifies that a failure after the launcher and the base archive were replaced puts the previous files back,
        /// leaves the folder without a manifest, and removes the work folder.
        /// </summary>
        [Fact]
        public void UpdateLinuxSharedInstallation_restores_the_previous_files_after_a_failure()
        {
            // Arrange: a folder in place of the icon makes the third replacement fail.
            SeedSharedFolderWithoutManifest();
            File.Delete(Path.Combine(SharedDir, "appicon.svg"));
            Directory.CreateDirectory(Path.Combine(SharedDir, "appicon.svg"));
            SeedStagedUpdate();

            // Act.
            var updated = PatcherRunner.UpdateLinuxSharedInstallation(CreateUpdate());

            // Assert.
            updated.Should().BeFalse();
            File.ReadAllText(Path.Combine(SharedDir, "ASLM")).Should().Be("old launcher");
            File.ReadAllText(Path.Combine(SharedDir, "aslm-base.tar.gz")).Should().Be("old archive");
            Directory.Exists(Path.Combine(SharedDir, "appicon.svg")).Should().BeTrue();
            File.Exists(Path.Combine(SharedDir, "install-manifest.json")).Should().BeFalse();
            Directory.Exists(WorkDir).Should().BeFalse();
        }

        /// <summary>
        /// Verifies that the work folder an interrupted run left behind is removed and the update then succeeds.
        /// </summary>
        [Fact]
        public void UpdateLinuxSharedInstallation_replaces_the_work_folder_of_an_interrupted_run()
        {
            SeedSharedFolderWithoutManifest();
            SeedStagedUpdate();
            Directory.CreateDirectory(Path.Combine(WorkDir, "backup"));
            File.WriteAllText(Path.Combine(WorkDir, "ASLM"), "partial launcher");
            File.WriteAllText(Path.Combine(WorkDir, "backup", "ASLM"), "older launcher");

            PatcherRunner.UpdateLinuxSharedInstallation(CreateUpdate()).Should().BeTrue();

            AssertSharedFolderUpdated();
        }


        // Shared folder helper

        /// <summary>
        /// Verifies that pkexec starts the patcher with the helper arguments, and that the helper, given exactly those
        /// arguments, updates the shared folder and reports success.
        /// </summary>
        [Fact]
        public async Task Shared_folder_helper_applies_the_update_from_the_arguments_the_patcher_builds()
        {
            // Arrange.
            SeedSharedFolderWithoutManifest();
            SeedStagedUpdate();
            var patcherPath = Path.Combine(_scope.Dir, "tmp", "Patcher_0", "aslm-patcher");

            // Act.
            var startInfo = PatcherRunner.BuildSharedFolderHelperStartInfo("/usr/bin/pkexec", patcherPath, CreateUpdate());
            var exitCode = await PatcherRunner.RunAsync([.. startInfo.ArgumentList.Skip(1)], progress: null);

            // Assert: pkexec runs the patcher itself, and the helper took every path from its arguments.
            startInfo.FileName.Should().Be("/usr/bin/pkexec");
            startInfo.ArgumentList.Should().Equal(
                patcherPath,
                "--update-shared",
                "--shared-dir", SharedDir,
                "--staging", StagingPath,
                "--archive", ArchivePath,
                "--version", UpdateVersion);
            exitCode.Should().Be(0);
            AssertSharedFolderUpdated();
        }

        /// <summary>
        /// Verifies that the helper refuses a folder that is no ASLM installation, a relative path and a version
        /// without four parts, and changes nothing then.
        /// </summary>
        [Theory]
        [InlineData("not-installation")]
        [InlineData("relative-staging")]
        [InlineData("short-version")]
        public async Task Shared_folder_helper_refuses_invalid_arguments(string mode)
        {
            // Arrange: an empty folder stands in for a folder that is no installation.
            SeedSharedFolderWithoutManifest();
            SeedStagedUpdate();
            var otherDir = Path.Combine(_scope.Dir, "other");
            Directory.CreateDirectory(otherDir);
            var sharedDir = mode == "not-installation" ? otherDir : SharedDir;
            var stagingPath = mode == "relative-staging" ? Path.Combine("staging", "ASLM-linux-amd64") : StagingPath;
            var version = mode == "short-version" ? "0.12" : UpdateVersion;

            // Act.
            var exitCode = await PatcherRunner.RunAsync(
                [
                    "--update-shared",
                    "--shared-dir", sharedDir,
                    "--staging", stagingPath,
                    "--archive", ArchivePath,
                    "--version", version
                ],
                progress: null);

            // Assert.
            exitCode.Should().Be(1);
            Directory.EnumerateFileSystemEntries(otherDir).Should().BeEmpty();
            File.ReadAllText(Path.Combine(SharedDir, "ASLM")).Should().Be("old launcher");
            File.Exists(Path.Combine(SharedDir, "install-manifest.json")).Should().BeFalse();
        }

        /// <summary>
        /// Verifies that the patcher reads the output of the helper process on both streams while it runs, so a
        /// helper with more output than a pipe holds finishes, and returns its exit code, as pkexec reports a
        /// dismissed password dialog.
        /// </summary>
        [Fact]
        public async Task RunSharedFolderHelperProcess_reads_the_output_and_returns_the_exit_code()
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                Arguments = "/d /c \"(for /l %i in (1,1,2000) do @echo output line %i)& echo error line 1>&2& exit /b 126\""
            };

            // A helper blocked on a full pipe would never exit, so the wait is bounded.
            var exitCode = await Task.Run(() => PatcherRunner.RunSharedFolderHelperProcess(startInfo))
                .WaitAsync(TimeSpan.FromSeconds(60));

            exitCode.Should().Be(126);
        }
    }
}
