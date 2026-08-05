// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Models;
using ASLM.Tests.TestSupport;

namespace ASLM.Tests.Services;

public sealed class UpdateManagerInstallStateTests
{
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
