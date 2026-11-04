// Copyright NEXTGGTECH. Apache License 2.0.

using System.Text.Json;
using ASLM.Installer;
using ASLM.Installer.Tests.TestSupport;

namespace ASLM.Installer.Tests.Services
{
    /// <summary>
    /// Verifies the dual-location installation logic without any UI involvement.
    /// </summary>
    public sealed class InstallerServiceTests
    {
        // Constants and test data helpers

        /// <summary>
        /// Environment variable through which the installer finds the payload archive; mirrors the service's own
        /// constant.
        /// </summary>
        private const string PayloadEnvironmentVariable = "ASLM_INSTALLER_PAYLOAD_PATH";

        /// <summary>
        /// Builds a realistic payload archive: root launcher, App and Patcher program folders, and data folders.
        /// </summary>
        /// <param name="directory">Directory in which <c>aslm-payload.zip</c> is created.</param>
        /// <param name="extraEntries">Additional entry names that are added with placeholder content.</param>
        /// <returns>The full path of the created archive.</returns>
        private static string WritePayloadZip(string directory, params string[] extraEntries)
        {
            var payloadPath = Path.Combine(directory, "aslm-payload.zip");
            using var stream = File.Create(payloadPath);
            using var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create);

            // Adds one text entry to the archive.
            void Add(string entryName, string content)
            {
                var entry = archive.CreateEntry(entryName);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(content);
            }

            // Root launcher, program folders and data folders as the real payload lays them out.
            Add("ASLM.exe", "launcher binary");
            Add("App/ASLM.dll", "app assembly");
            Add("App/ASLM.runtimeconfig.json", "{}");
            Add("Patcher/ASLM Patcher.exe", "patcher binary");
            Add("Patcher/ASLM Patcher.dll", "patcher assembly");
            Add("Data/App/ASLM_Data.json", """{"user":{"name":"kept"}}""");
            Add("Engines/ollama/engine.bin", "engine");
            foreach (var extra in extraEntries)
            {
                Add(extra, "extra");
            }

            return payloadPath;
        }

        /// <summary>
        /// Creates an installer whose user directory and shortcut sink are redirected into the test scope.
        /// </summary>
        private static InstallerService CreateInstaller(TempScope scope, string userDir,
            Action<string, string, string>? shortcutSink = null,
            Func<Task<Stream>>? packagedPayload = null)
        {
            return new InstallerService(
                userAppDir: () => userDir,
                packagedPayload: packagedPayload,
                createShortcut: shortcutSink);
        }

        /// <summary>
        /// Creates install options with one accepted legal document and the given shortcut choices.
        /// </summary>
        private static InstallOptions Options(string basePath, string folderName,
            bool desktop = false, bool startMenu = false) =>
            new(basePath, folderName, "v0.11.0",
                [new AcceptedLegalDocument("eula", "EULA", "eula.md", "abc123", DateTimeOffset.UtcNow)],
                desktop, startMenu);


        // Path validation

        /// <summary>
        /// Verifies that an empty or whitespace base directory is rejected with a message asking for a directory.
        /// </summary>
        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void ValidateInstallPath_rejects_missing_base_directory(string basePath)
        {
            var validation = new InstallerService().ValidateInstallPath(basePath, "ASLM");

            validation.IsValid.Should().BeFalse();
            validation.Message.Should().Contain("installation directory");
        }

        /// <summary>
        /// Verifies that a whitespace-only folder name is rejected with a message asking for the folder name.
        /// </summary>
        [Fact]
        public void ValidateInstallPath_rejects_missing_folder_name()
        {
            var validation = new InstallerService().ValidateInstallPath(Path.GetTempPath(), " ");

            validation.IsValid.Should().BeFalse();
            validation.Message.Should().Contain("folder name");
        }

        /// <summary>
        /// Verifies that a folder name with a character that Windows forbids in file names is rejected.
        /// </summary>
        [Fact]
        public void ValidateInstallPath_rejects_characters_windows_cannot_use()
        {
            var validation = new InstallerService().ValidateInstallPath(Path.GetTempPath(), "AS<LM");

            validation.IsValid.Should().BeFalse();
            validation.Message.Should().Contain("characters");
        }

        /// <summary>
        /// Verifies that the folder name <c>..</c> is rejected because it would leave the selected base directory.
        /// </summary>
        [Fact]
        public void ValidateInstallPath_rejects_folder_names_escaping_the_base_directory()
        {
            var validation = new InstallerService().ValidateInstallPath(Path.GetTempPath(), "..");

            validation.IsValid.Should().BeFalse();
            validation.Message.Should().Contain("inside");
        }

        /// <summary>
        /// Verifies that an existing target folder that already contains entries is rejected.
        /// </summary>
        [Fact]
        public void ValidateInstallPath_rejects_existing_non_empty_target()
        {
            using var scope = new TempScope();
            var installPath = Path.Combine(scope.Dir, "occupied");
            Directory.CreateDirectory(Path.Combine(installPath, "content"));

            var validation = new InstallerService().ValidateInstallPath(scope.Dir, "occupied");

            validation.IsValid.Should().BeFalse();
            validation.Message.Should().Contain("not empty");
        }

        /// <summary>
        /// Verifies that environment variables in the base path are expanded and that surrounding whitespace in the
        /// folder name is trimmed.
        /// </summary>
        [Fact]
        public void ValidateInstallPath_expands_variables_and_trims_the_folder_name()
        {
            using var env = new EnvironmentScope("ASLM_TEST_BASE", Path.GetTempPath());
            var validation = new InstallerService().ValidateInstallPath("%ASLM_TEST_BASE%", "  AslmFolder  ");

            validation.IsValid.Should().BeTrue();
            validation.InstallPath.Should().EndWith("AslmFolder");
        }


        // Installation placement

        /// <summary>
        /// Verifies that the launcher and the base payload archive are placed in the shared directory, that everything
        /// else goes to the per-user directory without the launcher, and that the staging directory is cleaned up.
        /// </summary>
        [Fact]
        public async Task InstallAsync_places_launcher_and_base_archive_shared_and_everything_else_per_user()
        {
            // Arrange: payload provided through the environment variable, user directory inside the test scope.
            using var scope = new TempScope();
            var payloadPath = WritePayloadZip(scope.Dir);
            using var env = new EnvironmentScope(PayloadEnvironmentVariable, payloadPath);
            var userDir = Path.Combine(scope.Dir, "user-app");
            var installer = CreateInstaller(scope, userDir);
            var progress = new CollectProgress();

            // Act.
            var manifest = await installer.InstallAsync(Options(scope.Dir, "ASLM-Shared"), progress, CancellationToken.None);

            // Assert: the shared directory holds the launcher and an exact copy of the payload archive.
            var sharedDir = manifest.SharedInstallPath;
            sharedDir.Should().Be(Path.Combine(scope.Dir, "ASLM-Shared"));
            File.ReadAllText(Path.Combine(sharedDir, "ASLM.exe")).Should().Be("launcher binary");
            File.ReadAllBytes(Path.Combine(sharedDir, "aslm-base.zip")).Should().Equal(File.ReadAllBytes(payloadPath));

            // The per-user copy receives program and data folders but never the shared root launcher.
            File.ReadAllText(Path.Combine(userDir, "App", "ASLM.dll")).Should().Be("app assembly");
            File.ReadAllText(Path.Combine(userDir, "Patcher", "ASLM Patcher.exe")).Should().Be("patcher binary");
            File.ReadAllText(Path.Combine(userDir, "Data", "App", "ASLM_Data.json")).Should().Contain("kept");
            File.ReadAllText(Path.Combine(userDir, "Engines", "ollama", "engine.bin")).Should().Be("engine");
            File.Exists(Path.Combine(userDir, "ASLM.exe")).Should().BeFalse();

            // Only the emptied staging parent may survive a successful installation.
            AssertStagingCleaned(scope);
        }


        // Staging assertions

        /// <summary>
        /// Asserts that no staging working directory survives under the redirected staging root.
        /// </summary>
        private static void AssertStagingCleaned(TempScope scope)
        {
            Directory.EnumerateDirectories(scope.StagingRoot).Should().BeEmpty();
        }


        // Installation results

        /// <summary>
        /// Verifies that the install manifest in the shared directory and the legal acceptance file in the per-user
        /// data folder contain the installed paths, the version, and the accepted documents.
        /// </summary>
        [Fact]
        public async Task InstallAsync_writes_manifest_and_legal_acceptance()
        {
            // Arrange: one accepted document with a known hash and a custom version.
            using var scope = new TempScope();
            var payloadPath = WritePayloadZip(scope.Dir);
            using var env = new EnvironmentScope(PayloadEnvironmentVariable, payloadPath);
            var userDir = Path.Combine(scope.Dir, "user-app");
            var acceptedAt = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
            var installer = CreateInstaller(scope, userDir);
            var options = new InstallOptions(scope.Dir, "ASLM-Shared", "v9.9.9",
                [new AcceptedLegalDocument("eula", "EULA", "eula.md", "deadbeef", acceptedAt)],
                CreateDesktopShortcut: false, CreateStartMenuShortcut: false);

            // Act.
            var manifest = await installer.InstallAsync(options, new CollectProgress(), CancellationToken.None);

            // Assert: the manifest file is camelCase JSON that mirrors the returned manifest.
            var manifestJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(manifest.SharedInstallPath, "install-manifest.json")));
            manifestJson.RootElement.GetProperty("app").GetString().Should().Be("ASLM");
            manifestJson.RootElement.GetProperty("version").GetString().Should().Be("v9.9.9");
            manifestJson.RootElement.GetProperty("sharedInstallPath").GetString().Should().Be(manifest.SharedInstallPath);
            manifestJson.RootElement.GetProperty("userAppPath").GetString().Should().Be(userDir);
            manifestJson.RootElement.GetProperty("acceptedDocuments")[0].GetProperty("sha256").GetString().Should().Be("deadbeef");

            // The legal acceptance file lists the accepted documents in the per-user data folder.
            var legalJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(userDir, "Data", "App", "ASLM_LegalAcceptance.json")));
            legalJson.RootElement.GetProperty("acceptedDocuments")[0].GetProperty("id").GetString().Should().Be("eula");

            manifest.InstalledAtUtc.Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(-5));
        }

        /// <summary>
        /// Verifies that reported progress starts at 0, ends at 100, never decreases, and stays within 0 to 100.
        /// </summary>
        [Fact]
        public async Task InstallAsync_reports_monotonic_progress_from_zero_to_hundred()
        {
            using var scope = new TempScope();
            using var env = new EnvironmentScope(PayloadEnvironmentVariable, WritePayloadZip(scope.Dir));
            var progress = new CollectProgress();

            await CreateInstaller(scope, Path.Combine(scope.Dir, "user-app"))
                .InstallAsync(Options(scope.Dir, "ASLM-Shared"), progress, CancellationToken.None);

            var percents = progress.Percentages;
            percents.First().Should().Be(0);
            percents.Last().Should().Be(100);
            percents.Should().BeInAscendingOrder("install progress must never move backwards");
            percents.Where(percent => percent is > 100 or < 0).Should().BeEmpty();
        }


        // Installation failures

        /// <summary>
        /// Verifies that a payload entry with a parent-directory path aborts the installation and still removes the
        /// staging directory.
        /// </summary>
        [Fact]
        public async Task InstallAsync_rejects_payload_entries_that_escape_staging_and_cleans_up()
        {
            // Arrange: a payload whose only entry would be extracted outside the staging directory.
            using var scope = new TempScope();
            var payloadPath = Path.Combine(scope.Dir, "aslm-payload.zip");
            using (var stream = File.Create(payloadPath))
            using (var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("../escaped.txt");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("outside");
            }

            using var env = new EnvironmentScope(PayloadEnvironmentVariable, payloadPath);

            // Act and assert: the installation fails with the staging error and leaves no staging directory behind.
            var install = () => CreateInstaller(scope, Path.Combine(scope.Dir, "user-app"))
                .InstallAsync(Options(scope.Dir, "ASLM-Shared"), new CollectProgress(), CancellationToken.None);

            (await install.Should().ThrowAsync<InvalidOperationException>())
                .Which.Message.Should().Contain("outside the staging directory");
            AssertStagingCleaned(scope);
        }

        /// <summary>
        /// Verifies that a payload archive without any file entry is rejected as empty.
        /// </summary>
        [Fact]
        public async Task InstallAsync_rejects_an_empty_payload_archive()
        {
            using var scope = new TempScope();
            var payloadPath = Path.Combine(scope.Dir, "aslm-payload.zip");
            using (var stream = File.Create(payloadPath))
            using (var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create))
            {
                // A valid archive that contains only a directory entry carries no installable files.
                archive.CreateEntry("empty/");
            }

            using var env = new EnvironmentScope(PayloadEnvironmentVariable, payloadPath);

            var install = () => CreateInstaller(scope, Path.Combine(scope.Dir, "user-app"))
                .InstallAsync(Options(scope.Dir, "ASLM-Shared"), new CollectProgress(), CancellationToken.None);

            (await install.Should().ThrowAsync<InvalidOperationException>())
                .Which.Message.Should().Contain("empty");
        }

        /// <summary>
        /// Verifies that an already cancelled token stops the installation with a cancellation exception and leaves
        /// no staging directory behind.
        /// </summary>
        [Fact]
        public async Task InstallAsync_cancellation_before_extraction_cleans_the_staging_directory()
        {
            using var scope = new TempScope();
            using var env = new EnvironmentScope(PayloadEnvironmentVariable, WritePayloadZip(scope.Dir));
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();

            var install = () => CreateInstaller(scope, Path.Combine(scope.Dir, "user-app"))
                .InstallAsync(Options(scope.Dir, "ASLM-Shared"), new CollectProgress(), cancelled.Token);

            await install.Should().ThrowAsync<OperationCanceledException>();
            AssertStagingCleaned(scope);
        }

        /// <summary>
        /// Verifies that a missing payload produces a file-not-found error whose message lists the searched
        /// locations, including the path from the environment variable.
        /// </summary>
        [Fact]
        public async Task InstallAsync_missing_payload_reports_every_location_that_was_checked()
        {
            using var scope = new TempScope();
            using var env = new EnvironmentScope(PayloadEnvironmentVariable, Path.Combine(scope.Dir, "missing-payload.zip"));

            // Packaged-asset source that does not contain the payload either.
            Task<Stream> PackagedMissing() => Task.FromException<Stream>(new FileNotFoundException());

            var install = () => CreateInstaller(scope, Path.Combine(scope.Dir, "user-app"), packagedPayload: PackagedMissing)
                .InstallAsync(Options(scope.Dir, "ASLM-Shared"), new CollectProgress(), CancellationToken.None);

            (await install.Should().ThrowAsync<FileNotFoundException>())
                .Which.Message.Should().Contain("missing-payload.zip", "the error must list the locations searched");
        }


        // Payload sources and shortcuts

        /// <summary>
        /// Verifies that a payload available only from the packaged assets installs successfully but leaves out the
        /// base archive copy, which needs a payload file on disk.
        /// </summary>
        [Fact]
        public async Task InstallAsync_with_only_a_packaged_payload_skips_the_base_archive_copy()
        {
            using var scope = new TempScope();

            // The payload exists only through the packaged-asset source: no filesystem candidate exists,
            // so the base archive cannot be copied for additional users, but the install still succeeds.
            var payloadZip = WritePayloadZip(scope.Dir);

            // Packaged-asset source that opens the payload file.
            Task<Stream> Packaged() => Task.FromResult<Stream>(File.OpenRead(payloadZip));
            var userDir = Path.Combine(scope.Dir, "user-app");

            var manifest = await CreateInstaller(scope, userDir, packagedPayload: Packaged)
                .InstallAsync(Options(scope.Dir, "ASLM-Shared"), new CollectProgress(), CancellationToken.None);

            File.Exists(Path.Combine(manifest.SharedInstallPath, "aslm-base.zip")).Should().BeFalse();
            File.ReadAllText(Path.Combine(manifest.SharedInstallPath, "ASLM.exe")).Should().Be("launcher binary");
            File.ReadAllText(Path.Combine(userDir, "App", "ASLM.dll")).Should().Be("app assembly");
        }

        /// <summary>
        /// Verifies that shortcuts are requested only for the selected kinds: none when both are off, and the desktop
        /// and Start menu shortcut pointing at the shared install directory when both are on.
        /// </summary>
        [Fact]
        public async Task InstallAsync_requests_shortcuts_only_for_the_selected_kinds()
        {
            using var scope = new TempScope();
            using var env = new EnvironmentScope(PayloadEnvironmentVariable, WritePayloadZip(scope.Dir));
            var requests = new List<(string Folder, string FileName, string InstallDir)>();

            // Shortcut sink that records requests instead of creating files.
            void Capture(string folder, string fileName, string installDir) => requests.Add((folder, fileName, installDir));

            var manifestNone = await CreateInstaller(scope, Path.Combine(scope.Dir, "user-a"), Capture)
                .InstallAsync(Options(scope.Dir, "share-none"), new CollectProgress(), CancellationToken.None);
            requests.Should().BeEmpty("both shortcut kinds are disabled");

            var manifestBoth = await CreateInstaller(scope, Path.Combine(scope.Dir, "user-b"), Capture)
                .InstallAsync(Options(scope.Dir, "share-both", desktop: true, startMenu: true), new CollectProgress(), CancellationToken.None);
            requests.Select(request => request.FileName).Should().Equal("ASLM.lnk", "ASLM.lnk");
            requests[0].Folder.Should().Be(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
            requests[1].Folder.Should().Be(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"));
            requests.Should().OnlyContain(request => request.InstallDir == manifestBoth.SharedInstallPath);
            manifestNone.SharedInstallPath.Should().NotBe(manifestBoth.SharedInstallPath);
        }


        // Shortcut, launcher and user directory

        /// <summary>
        /// Verifies that the real shortcut creation writes a non-trivial <c>.lnk</c> file for an existing launcher.
        /// </summary>
        [Fact]
        public void CreateShortcut_writes_a_real_windows_shortcut()
        {
            using var scope = new TempScope();
            var installDir = Path.Combine(scope.Dir, "shortcut-install");
            Directory.CreateDirectory(installDir);
            File.WriteAllText(Path.Combine(installDir, "ASLM.exe"), "launcher binary");
            var shortcutFolder = Path.Combine(scope.Dir, "shortcut-folder");

            InstallerService.CreateShortcut(shortcutFolder, "ASLM.lnk", installDir);

            var shortcut = Path.Combine(shortcutFolder, "ASLM.lnk");
            File.Exists(shortcut).Should().BeTrue();
            new FileInfo(shortcut).Length.Should().BeGreaterThan(100, "a .lnk contains shell metadata");
        }

        /// <summary>
        /// Verifies that launching from a directory without the launcher throws a file-not-found error naming the
        /// launcher executable.
        /// </summary>
        [Fact]
        public void Launch_throws_when_the_launcher_is_missing()
        {
            using var scope = new TempScope();
            var emptyDir = Path.Combine(scope.Dir, "empty");
            Directory.CreateDirectory(emptyDir);

            var launch = () => new InstallerService().Launch(emptyDir);

            launch.Should().Throw<FileNotFoundException>().Which.FileName.Should().EndWith("ASLM.exe");
        }

        /// <summary>
        /// Verifies that the default per-user application directory is <c>ASLM</c> inside the local app data folder.
        /// </summary>
        [Fact]
        public void GetUserAppDir_default_points_at_the_local_app_data_profile()
        {
            new InstallerService().GetUserAppDir().Should().Be(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ASLM"));
        }


        // Progress collector

        /// <summary>
        /// Progress sink that records every report synchronously, so tests can assert the exact sequence
        /// instead of depending on <see cref="Progress{T}"/> posting to a synchronization context.
        /// </summary>
        private sealed class CollectProgress : IProgress<InstallProgress>
        {
            /// <summary>
            /// All reports in the order in which they arrived.
            /// </summary>
            private readonly List<InstallProgress> _reports = [];

            /// <summary>
            /// Percent values of all reports so far, in arrival order.
            /// </summary>
            public IReadOnlyList<int> Percentages => _reports.Select(report => report.Percent).ToList();

            /// <summary>
            /// Records one progress report.
            /// </summary>
            public void Report(InstallProgress value) => _reports.Add(value);
        }
    }
}
