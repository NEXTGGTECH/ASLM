// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Models;
using ASLM.Tests.TestSupport;

namespace ASLM.Tests.Services;

public sealed class UpdateManagerInstallStateTests
{
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

    [Fact]
    public void Module_info_shows_public_metadata_but_never_commands_paths_or_setting_values()
    {
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
        var fields = ASLM.Pages.ModuleInfo.CreateFields(module, [],
            [new() { Id = "python", Name = "Python", Version = "3.12" }]);
        var content = string.Join("\n", fields.Select(field => field.Value));
        content.Should().NotContain("Module author").And.Contain("Python v.: 3.12").And.Contain("package")
            .And.Contain("text-generation").And.NotContain("secret-").And.NotContain("private/local");
        content.Should().NotContain("owner/repository").And.NotContain("Official module");
        ASLM.Pages.ModuleInfo.GetSourceUrl(module).Should().Be("https://github.com/owner/repository");
        fields.Should().OnlyContain(field => !string.IsNullOrWhiteSpace(field.Value));
    }

    [Fact]
    public void Info_groups_platforms_after_download_target_and_puts_engine_help_on_the_engine_line()
    {
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
        fields.Single(field => field.Key == "ModuleInfo_Platforms").Value.Should().Be("windows amd64/arm64\nmacos amd64/arm64");
        fields.ToList().FindIndex(field => field.Key == "ModuleInfo_Platforms").Should()
            .BeGreaterThan(fields.ToList().FindIndex(field => field.Key == "ModuleUpdate_ReleaseVersion"));
        fields.Should().NotContain(field => field.Key == "ModuleInfo_Version" || field.Key == "ModuleInfo_Author" || field.Key == "ModuleInfo_ProvidedEngines");
        fields.Single(field => field.Key == "Ruby packages").Should().Match<ASLM.Pages.ModuleInfo.InfoField>(field =>
            field.LiteralTitle && field.Value == "rails:8.1, webrick:1.9");
        fields.Single(field => field.Key == "Python packages").Value.Should().Be("virtualenv");
        var engines = fields.Single(field => field.Key == "ModuleInfo_Engines").Items;
        engines[0].Text.Should().Be("Ruby v.: 3.4.5");
        engines[0].Description.Should().Be("Embedded runtime");
        engines[1].Description.Should().BeNull();
    }

    [Theory]
    [InlineData("release", "release")]
    [InlineData("pre-release", "pre-release")]
    [InlineData("prerelease", "pre-release")]
    public void Catalog_default_selects_app_channel_but_never_overrides_installed_or_user_configured_modules(string setting, string expected)
    {
        var module = CreateReleaseModule();
        UpdateManager.ApplyCatalogDefaults(module, false, setting);
        module.Update.Mode.Should().Be(expected);
        module.Update.Channel.Should().Be(expected);
        module.Update.SelectedReleaseTag.Should().Be("latest");
        module.Update.UseDefaultChannel.Should().BeNull();
        module.Update.Mode = "branch";
        module.Update.UseDefaultChannel = false;
        module.Update.Branch = "dev";
        UpdateManager.ApplyCatalogDefaults(module, false, setting);
        module.Update.Mode.Should().Be("branch");
        module.Update.Branch.Should().Be("dev");
        module.Update.UseDefaultChannel = null;
        UpdateManager.ApplyCatalogDefaults(module, true, setting);
        module.Update.Mode.Should().Be("branch");
    }

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

    [Fact]
    public void HasRecordedRemoteSourceInstall_returns_false_for_release_module_without_installed_tag()
    {
        var module = CreateReleaseModule(version: "0.7.1.8");

        UpdateManager.HasRecordedRemoteSourceInstall(module).Should().BeFalse();
    }

    [Fact]
    public void HasRecordedRemoteSourceInstall_returns_true_when_installed_release_tag_is_set()
    {
        var module = CreateReleaseModule(
            version: "0.7.1.8",
            configure: m => m.Update.InstalledReleaseTag = "0.7.1.8");

        UpdateManager.HasRecordedRemoteSourceInstall(module).Should().BeTrue();
    }

    [Fact]
    public void HasRecordedRemoteSourceInstall_returns_false_for_branch_module_without_commit_sha()
    {
        var module = CreateBranchModule();

        UpdateManager.HasRecordedRemoteSourceInstall(module).Should().BeFalse();
    }

    [Fact]
    public void HasRecordedRemoteSourceInstall_returns_true_when_installed_commit_sha_is_set()
    {
        var module = CreateBranchModule(configure: m => m.Update.InstalledCommitSha = "abc123");

        UpdateManager.HasRecordedRemoteSourceInstall(module).Should().BeTrue();
    }

    [Fact]
    public void ShouldOfferReleaseInstallCandidate_returns_true_when_version_matches_but_source_not_recorded()
    {
        var module = CreateReleaseModule(version: "0.7.1.8");

        UpdateManager.ShouldOfferReleaseInstallCandidate(module, "0.7.1.8").Should().BeTrue();
    }

    [Fact]
    public void ShouldOfferReleaseInstallCandidate_returns_false_when_installed_release_tag_matches()
    {
        var module = CreateReleaseModule(
            version: "0.7.1.8",
            configure: m => m.Update.InstalledReleaseTag = "0.7.1.8");

        UpdateManager.ShouldOfferReleaseInstallCandidate(module, "0.7.1.8").Should().BeFalse();
    }

    [Fact]
    public void ShouldOfferReleaseInstallCandidate_returns_true_when_installed_release_tag_differs()
    {
        var module = CreateReleaseModule(
            version: "0.7.1.7",
            configure: m => m.Update.InstalledReleaseTag = "0.7.1.7");

        UpdateManager.ShouldOfferReleaseInstallCandidate(module, "0.7.1.8").Should().BeTrue();
    }

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
