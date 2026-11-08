// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Models;
using ASLM.Tests.TestSupport;

namespace ASLM.Tests.Services.Internal
{
    /// <summary>
    /// Verifies how <see cref="UpdateManager"/> decides which module release or branch to install and whether the
    /// recorded installation already matches it, together with the module info page and catalog presentation rules
    /// that depend on the same update configuration.
    /// </summary>
    public sealed class UpdateManagerInstallStateTests
    {
        // Release selection

        /// <summary>
        /// Verifies that a pinned release tag selects exactly that release (and nothing when it is missing), while
        /// the latest selection takes the first available release.
        /// </summary>
        [Fact]
        public void Installation_respects_pinned_release_instead_of_falling_back_to_the_newest_version()
        {
            var module = CreateReleaseModule();
            var latest = new UpdateCandidate { ReleaseTag = "v2.0" };
            var pinned = new UpdateCandidate { ReleaseTag = "v1.0" };
            module.Update.SelectedReleaseTag = "v1.0";
            UpdateManager.SelectModuleReleaseInstallTarget(module, [latest, pinned]).Should().BeSameAs(pinned);
            UpdateManager.SelectModuleReleaseInstallTarget(module, [latest]).Should().BeNull();
            module.Update.SelectedReleaseTag = "latest";
            UpdateManager.SelectModuleReleaseInstallTarget(module, [latest, pinned]).Should().BeSameAs(latest);
            UpdateManager.SelectModuleReleaseInstallTarget(module, []).Should().BeNull();
        }

        /// <summary>
        /// Verifies that the release picker of the module info page lists the virtual latest entry first, then the
        /// available releases without duplicates, and adds a saved explicit tag that is not available instead of
        /// switching it.
        /// </summary>
        [Fact]
        public void Module_info_release_options_keep_latest_and_an_explicit_target_without_silently_switching_it()
        {
            var available = new UpdateCandidate { ReleaseTag = "v2.0", DisplayName = "Version 2.0" };
            var options = ASLM.Pages.ModuleInfo.BuildReleaseOptions([available, available], "v1.0");
            options.Should().HaveCount(3);
            options[0].IsVirtualLatest.Should().BeTrue();
            options[1].Should().BeSameAs(available);
            options[2].ReleaseTag.Should().Be("v1.0");
            ASLM.Pages.ModuleInfo.BuildReleaseOptions([], "v1.0").Should()
                .Contain(option => option.ReleaseTag == "v1.0");
            ASLM.Pages.ModuleInfo.BuildReleaseOptions([], "latest").Should()
                .ContainSingle(option => option.IsVirtualLatest);
        }


        // Catalog eligibility

        /// <summary>
        /// Verifies that a module can be downloaded only from a GitHub source with a repository of exactly the form
        /// owner/name, without path traversal or query fragments.
        /// </summary>
        /// <param name="source">Source type of the module.</param>
        /// <param name="repo">Repository text of the module source.</param>
        /// <param name="expected">Whether the module is expected to be downloadable.</param>
        [Theory]
        [InlineData("github", "NEXTGGTECH/ASLM-Chat", true)]
        [InlineData("GitHub", "owner/repository", true)]
        [InlineData("github", "", false)]
        [InlineData("github", "owner/../repository", false)]
        [InlineData("github", "../repository", false)]
        [InlineData("github", "owner/repository?redirect=1", false)]
        [InlineData("file", "owner/repository", false)]
        public void Catalog_download_requires_a_supported_repository(string source, string repo, bool expected)
        {
            var module = CreateReleaseModule(configure: item => item.Source = new() { Type = source, Repo = repo });
            UpdateManager.CanDownloadModule(module).Should().Be(expected);
        }


        // Module info presentation

        /// <summary>
        /// Verifies that the module info page shows public metadata (engine versions, libraries, models and the
        /// repository link) but never the author, repository text, local paths, commands or setting values.
        /// </summary>
        [Fact]
        public void Module_info_shows_public_metadata_but_never_commands_paths_or_setting_values()
        {
            // Arrange: a module that carries both public metadata and private values.
            var module = CreateReleaseModule(configure: item =>
            {
                item.Author = "Module author";
                item.Source = new() { Type = "github", Repo = "owner/repository" };
                item.Category.Add("chat");
                item.SourcePath = "private/local/path/ASLM_Module.json";
                item.Commands.Run.Add(new() { Exec = "secret-internal-command" });
                item.Settings.Add(new() { Key = "secret-setting", Value = "secret-value" });
                item.Dependencies.Engines.Add(new() { Id = "python", Libraries = ["package"] });
                item.Dependencies.Models.Add("text-generation");
            });

            // Act: build the info fields and join their values.
            var fields = ASLM.Pages.ModuleInfo.CreateFields(module, [],
                [new() { Id = "python", Name = "Python", Version = "3.12" }]);
            var content = string.Join("\n", fields.Select(field => field.Value));

            // Assert: public data is present, private data is absent, and no field is blank.
            content.Should().NotContain("Module author").And.Contain("Python v.: 3.12").And.Contain("package")
                .And.Contain("text-generation").And.NotContain("secret-").And.NotContain("private/local");
            content.Should().NotContain("owner/repository").And.NotContain("Official module");
            ASLM.Pages.ModuleInfo.GetSourceUrl(module).Should().Be("https://github.com/owner/repository");
            fields.Should().OnlyContain(field => !string.IsNullOrWhiteSpace(field.Value));
        }

        /// <summary>
        /// Verifies that the latest entry of the release picker displays the actual tag and an invariant publish date
        /// while its stored selection stays virtual latest, and that the formatted label omits the date when none is
        /// known and is just "latest" without a release.
        /// </summary>
        [Fact]
        public void Latest_alias_displays_the_actual_tag_and_invariant_release_date()
        {
            var release = new UpdateCandidate { ReleaseTag = "v1.0.0.0", PublishedAt =
                new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero) };
            var option = ASLM.Pages.ModuleInfo.BuildReleaseOptions([release], "latest")[0];
            option.DisplayName.Should().Be("latest (v1.0.0.0 - 2026-10-11)");
            option.IsVirtualLatest.Should().BeTrue();
            option.ReleaseTag.Should().BeNull();
            release.PublishedAt = null;
            UpdateManager.FormatLatestRelease(release).Should().Be("latest (v1.0.0.0)");
            UpdateManager.FormatLatestRelease(null).Should().Be("latest");
        }

        /// <summary>
        /// Verifies that the module info page groups the supported architectures per operating system after the
        /// download target, omits version, author and provided-engine rows, lists the libraries of each engine, and
        /// shows the engine description on the engine line.
        /// </summary>
        [Fact]
        public void Info_groups_platforms_after_download_target_and_puts_engine_help_on_the_engine_line()
        {
            // Arrange: a module for four platforms with a provided Ruby engine and library dependencies.
            var module = CreateReleaseModule(configure: item =>
            {
                item.HasDeclaredUpdateConfig = true;
                item.Update.SelectedReleaseTag = "latest";
                item.SupportedPlatforms = [SupportedPlatform.FromKey("windows-amd64"), SupportedPlatform.FromKey("windows-arm64"),
                    SupportedPlatform.FromKey("macos-amd64"), SupportedPlatform.FromKey("macos-arm64")];
                item.Engines.Add(new() { Id = "ruby", Name = "Ruby", Version = "3.4.5", Description = "Embedded runtime" });
                item.Dependencies.Engines.Add(new() { Id = "ruby", Libraries = ["rails:8.1", "webrick:1.9"] });
                item.Dependencies.Engines.Add(new() { Id = "python", Libraries = ["virtualenv"] });
            });
            module.ResolveForPlatform("windows", "amd64");
            var fields = ASLM.Pages.ModuleInfo.CreateFields(module, [], [new() { Id = "python", Name = "Python", Version = "3.12" }]);

            // The platforms row groups architectures per OS and comes after the release version row.
            fields.Single(field => field.Key == "ModuleInfo_Platforms").Value.Should().Be("windows amd64/arm64\nmacos amd64/arm64");
            fields.ToList().FindIndex(field => field.Key == "ModuleInfo_Platforms").Should()
                .BeGreaterThan(fields.ToList().FindIndex(field => field.Key == "ModuleUpdate_ReleaseVersion"));
            fields.Should().NotContain(field => field.Key == "ModuleInfo_Version" || field.Key == "ModuleInfo_Author" || field.Key == "ModuleInfo_ProvidedEngines");

            // Each engine's libraries get their own row, and the engine description sits on the engine line.
            fields.Single(field => field.Key == "Ruby packages").Should().Match<ASLM.Pages.ModuleInfo.InfoField>(field =>
                field.LiteralTitle && field.Value == "rails:8.1, webrick:1.9");
            fields.Single(field => field.Key == "Python packages").Value.Should().Be("virtualenv");
            var engines = fields.Single(field => field.Key == "ModuleInfo_Engines").Items;
            engines[0].Text.Should().Be("Ruby v.: 3.4.5");
            engines[0].Description.Should().Be("Embedded runtime");
            engines[1].Description.Should().BeNull();
        }


        // Catalog defaults

        /// <summary>
        /// Verifies that the application's default channel is applied to an unconfigured catalog module (release or
        /// pre-release, with the latest release selected), and that it never overrides a module the user configured
        /// or one that is already installed.
        /// </summary>
        /// <param name="setting">Default channel setting of the application.</param>
        /// <param name="expected">Channel and mode that the catalog module is expected to receive.</param>
        [Theory]
        [InlineData("release", "release")]
        [InlineData("pre-release", "pre-release")]
        [InlineData("prerelease", "pre-release")]
        public void Catalog_default_selects_app_channel_but_never_overrides_installed_or_user_configured_modules(string setting, string expected)
        {
            // An unconfigured module receives the default channel and the latest release.
            var module = CreateReleaseModule();
            UpdateManager.ApplyCatalogDefaults(module, false, setting);
            module.Update.Mode.Should().Be(expected);
            module.Update.Channel.Should().Be(expected);
            module.Update.SelectedReleaseTag.Should().Be("latest");
            module.Update.UseDefaultChannel.Should().BeNull();

            // A module that explicitly opted out of the default channel keeps its branch configuration.
            module.Update.Mode = "branch";
            module.Update.UseDefaultChannel = false;
            module.Update.Branch = "dev";
            UpdateManager.ApplyCatalogDefaults(module, false, setting);
            module.Update.Mode.Should().Be("branch");
            module.Update.Branch.Should().Be("dev");

            // An installed module is never changed either.
            module.Update.UseDefaultChannel = null;
            UpdateManager.ApplyCatalogDefaults(module, true, setting);
            module.Update.Mode.Should().Be("branch");
        }


        // Scroll bar geometry

        /// <summary>
        /// Verifies that the scroll bar thumb is hidden while the content fits, otherwise is as tall as the visible
        /// share of the content (at least 24 units) and is placed proportionally to the scroll offset.
        /// </summary>
        /// <param name="viewport">Visible height of the scrolled area.</param>
        /// <param name="content">Total height of the scrolled content.</param>
        /// <param name="offset">Current scroll offset.</param>
        /// <param name="height">Expected thumb height; zero when there is no thumb.</param>
        /// <param name="top">Expected thumb position.</param>
        [Theory]
        [InlineData(200, 100, 0, 0, 0)]
        [InlineData(200, 400, 0, 100, 0)]
        [InlineData(200, 400, 100, 100, 50)]
        [InlineData(200, 400, 400, 100, 100)]
        [InlineData(200, 10000, 9800, 24, 176)]
        public void Module_scrollbar_uses_settings_geometry(double viewport, double content, double offset, double height, double top)
        {
            var thumb = ASLM.Controls.AppScrollBar.CalculateThumb(viewport, content, offset);
            thumb.Height.Should().Be(height);
            thumb.Top.Should().Be(top);
        }


        // Recorded remote source installs

        /// <summary>
        /// Verifies that a release module without an installed release tag has no recorded remote source install.
        /// </summary>
        [Fact]
        public void HasRecordedRemoteSourceInstall_returns_false_for_release_module_without_installed_tag()
        {
            var module = CreateReleaseModule(version: "0.7.1.8");

            UpdateManager.HasRecordedRemoteSourceInstall(module).Should().BeFalse();
        }

        /// <summary>
        /// Verifies that a release module with an installed release tag has a recorded remote source install.
        /// </summary>
        [Fact]
        public void HasRecordedRemoteSourceInstall_returns_true_when_installed_release_tag_is_set()
        {
            var module = CreateReleaseModule(
                version: "0.7.1.8",
                configure: m => m.Update.InstalledReleaseTag = "0.7.1.8");

            UpdateManager.HasRecordedRemoteSourceInstall(module).Should().BeTrue();
        }

        /// <summary>
        /// Verifies that a branch module without an installed commit has no recorded remote source install.
        /// </summary>
        [Fact]
        public void HasRecordedRemoteSourceInstall_returns_false_for_branch_module_without_commit_sha()
        {
            var module = CreateBranchModule();

            UpdateManager.HasRecordedRemoteSourceInstall(module).Should().BeFalse();
        }

        /// <summary>
        /// Verifies that a branch module with an installed commit has a recorded remote source install.
        /// </summary>
        [Fact]
        public void HasRecordedRemoteSourceInstall_returns_true_when_installed_commit_sha_is_set()
        {
            var module = CreateBranchModule(configure: m => m.Update.InstalledCommitSha = "abc123");

            UpdateManager.HasRecordedRemoteSourceInstall(module).Should().BeTrue();
        }


        // Offering release candidates

        /// <summary>
        /// Verifies that a release is offered when its version equals the manifest version but no install of the
        /// release source was recorded.
        /// </summary>
        [Fact]
        public void ShouldOfferReleaseInstallCandidate_returns_true_when_version_matches_but_source_not_recorded()
        {
            var module = CreateReleaseModule(version: "0.7.1.8");

            UpdateManager.ShouldOfferReleaseInstallCandidate(module, "0.7.1.8").Should().BeTrue();
        }

        /// <summary>
        /// Verifies that a release is not offered when the recorded installed release tag equals it.
        /// </summary>
        [Fact]
        public void ShouldOfferReleaseInstallCandidate_returns_false_when_installed_release_tag_matches()
        {
            var module = CreateReleaseModule(
                version: "0.7.1.8",
                configure: m => m.Update.InstalledReleaseTag = "0.7.1.8");

            UpdateManager.ShouldOfferReleaseInstallCandidate(module, "0.7.1.8").Should().BeFalse();
        }

        /// <summary>
        /// Verifies that a release is offered when the recorded installed release tag is a different version.
        /// </summary>
        [Fact]
        public void ShouldOfferReleaseInstallCandidate_returns_true_when_installed_release_tag_differs()
        {
            var module = CreateReleaseModule(
                version: "0.7.1.7",
                configure: m => m.Update.InstalledReleaseTag = "0.7.1.7");

            UpdateManager.ShouldOfferReleaseInstallCandidate(module, "0.7.1.8").Should().BeTrue();
        }


        // Install target comparison

        /// <summary>
        /// Verifies that a release candidate is not treated as already installed when no release source install was
        /// recorded, even though the manifest version equals its tag.
        /// </summary>
        [Fact]
        public void IsModuleAlreadyAtInstallTarget_returns_false_when_release_matches_but_source_not_recorded()
        {
            var module = CreateReleaseModule(version: "0.7.1.8");
            var candidate = new UpdateCandidate
            {
                Mode = "release",
                ReleaseTag = "0.7.1.8",
                Module = module
            };

            UpdateManager.IsModuleAlreadyAtInstallTarget(module, candidate).Should().BeFalse();
        }

        /// <summary>
        /// Verifies that a release candidate is treated as already installed when the recorded installed release tag
        /// equals its tag.
        /// </summary>
        [Fact]
        public void IsModuleAlreadyAtInstallTarget_returns_true_when_installed_release_tag_matches_candidate()
        {
            var module = CreateReleaseModule(
                version: "0.7.1.8",
                configure: m => m.Update.InstalledReleaseTag = "0.7.1.8");
            var candidate = new UpdateCandidate
            {
                Mode = "release",
                ReleaseTag = "0.7.1.8",
                Module = module
            };

            UpdateManager.IsModuleAlreadyAtInstallTarget(module, candidate).Should().BeTrue();
        }

        /// <summary>
        /// Verifies that a branch candidate is not treated as already installed when no installed commit was
        /// recorded.
        /// </summary>
        [Fact]
        public void IsModuleAlreadyAtInstallTarget_returns_false_for_branch_when_installed_commit_sha_missing()
        {
            var module = CreateBranchModule();
            var candidate = new UpdateCandidate
            {
                Mode = "branch",
                CommitSha = "abc123",
                Module = module
            };

            UpdateManager.IsModuleAlreadyAtInstallTarget(module, candidate).Should().BeFalse();
        }


        // Helpers

        /// <summary>
        /// Creates a module that tracks releases and declares an update configuration; the optional callback adjusts
        /// it further.
        /// </summary>
        /// <param name="version">Manifest version of the module.</param>
        /// <param name="configure">Optional callback that adjusts the module before it is returned.</param>
        private static ModuleConfig CreateReleaseModule(
            string version = "1.0.0",
            Action<ModuleConfig>? configure = null)
        {
            return ModuleConfigBuilder.Create(
                configure: module =>
                {
                    module.Version = version;
                    module.HasDeclaredUpdateConfig = true;
                    module.Update.Mode = "release";
                    configure?.Invoke(module);
                });
        }

        /// <summary>
        /// Creates a module that tracks the <c>main</c> branch and declares an update configuration; the optional
        /// callback adjusts it further.
        /// </summary>
        /// <param name="configure">Optional callback that adjusts the module before it is returned.</param>
        private static ModuleConfig CreateBranchModule(Action<ModuleConfig>? configure = null)
        {
            return ModuleConfigBuilder.Create(
                configure: module =>
                {
                    module.HasDeclaredUpdateConfig = true;
                    module.Update.Mode = "branch";
                    module.Update.Branch = "main";
                    configure?.Invoke(module);
                });
        }
    }
}
