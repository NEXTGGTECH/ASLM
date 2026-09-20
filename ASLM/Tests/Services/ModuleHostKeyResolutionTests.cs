// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Models;
using ASLM.Services.Sunrise;
using ASLM.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace ASLM.Tests.Services;

/// <summary>
/// Verifies trusted module resolution for ASLM-managed account keys.
/// </summary>
[Collection("ModuleManifestDiscovery")]
public sealed class ModuleHostKeyResolutionTests
{
    /// <summary>
    /// Verifies official modules receive connected subsystem keys.
    /// </summary>
    [Fact]
    public async Task Official_module_receives_connected_host_keys()
    {
        using var layout = new AslmFileSystemLayout();
        await File.WriteAllTextAsync(
            Path.Combine(layout.DataAppDir, "SUNRISE_Tokens.json"),
            """
            {
              "fileVersion": 2,
              "jwt": {
                "tokenRefresh": "aslm-refresh-token",
                "tokenAccess": ""
              }
            }
            """);

        var appData = await CreateAppDataAsync();
        appData.Data.GitHub.PersonalAccessToken = "github-personal-token";
        appData.Data.User.AccountMode = AppAccountMode.Cloud;
        appData.Data.User.Name = "Cloud user";
        appData.Data.User.LocalName = "Local user";
        await appData.SaveAsync();
        var githubStore = CreateGitHubStore(appData);
        using var sunriseService = new SunriseService(
            NullLogger<SunriseService>.Instance,
            appData);
        if (SunriseService.IsEnabled)
        {
            await sunriseService.InitializeAsync();
        }
        else
        {
            // A disabled build must not read credentials or mutate them, even if a caller
            // accidentally invokes an account action. GitHub resolution below stays active.
            var savedFiles = Directory.GetFiles(layout.DataAppDir)
                .ToDictionary(path => path, File.ReadAllBytes);
            await Assert.ThrowsAsync<NotSupportedException>(() => sunriseService.InitializeAsync());
            await Assert.ThrowsAsync<NotSupportedException>(() => sunriseService.AuthenticateApplicationAsync());
            await Assert.ThrowsAsync<NotSupportedException>(() => sunriseService.SignOutAsync());
            await Assert.ThrowsAsync<NotSupportedException>(() => sunriseService.ClearTokensAsync());
            await Assert.ThrowsAsync<NotSupportedException>(() => sunriseService.ClearUserDataAsync());
            await Assert.ThrowsAsync<NotSupportedException>(() => sunriseService.SendAsync(
                SunriseService.AslmGetUserDataEndpoint, "GET"));
            var sync = await sunriseService.SynchronizeCloudAccountAsync();
            sync.Success.Should().BeTrue();
            sync.Skipped.Should().BeTrue();
            sunriseService.IsCloudAccount.Should().BeFalse();
            sunriseService.TryGetRefreshToken(out var token).Should().BeFalse();
            token.Should().BeEmpty();
            appData.Data.User.AccountMode.Should().Be(AppAccountMode.Cloud);
            appData.Data.User.Name.Should().Be("Cloud user");
            appData.Data.User.LocalName.Should().Be("Local user");
            Directory.GetFiles(layout.DataAppDir).Should().BeEquivalentTo(savedFiles.Keys);
            foreach (var (path, bytes) in savedFiles)
            {
                File.ReadAllBytes(path).Should().Equal(bytes);
            }
        }
        using var runner = CreateRunner(appData, githubStore, sunriseService);
        var module = CreateOfficialModule();

        runner.GetResolvedSettingValue(
                module,
                new ModuleSetting { Key = "key-aslm", Type = "key-aslm" })
            .Should().Be(SunriseService.IsEnabled ? "aslm-refresh-token" : "None");
        runner.GetResolvedSettingValue(
                module,
                new ModuleSetting { Key = "key-gh", Type = "key-gh" })
            .Should().Be("github-personal-token");
    }

    /// <summary>
    /// Verifies modules from an official GitHub author receive connected subsystem keys.
    /// </summary>
    [Fact]
    public async Task Official_author_module_receives_connected_host_keys()
    {
        using var layout = new AslmFileSystemLayout();
        var appData = await CreateAppDataAsync();
        appData.Data.GitHub.PersonalAccessToken = "github-personal-token";
        var githubStore = CreateGitHubStore(appData);
        using var sunriseService = new SunriseService(
            NullLogger<SunriseService>.Instance,
            appData);
        if (SunriseService.IsEnabled)
        {
            await sunriseService.InitializeAsync();
        }
        using var runner = CreateRunner(appData, githubStore, sunriseService);
        var module = ModuleConfigBuilder.Create(
            id: "official-author-module",
            configure: config => config.Source.Repo = "NEXTGGTECH/Official-Author-Module");

        runner.GetResolvedSettingValue(
                module,
                new ModuleSetting { Key = "key-gh", Type = "key-gh" })
            .Should().Be("github-personal-token");
    }

    /// <summary>
    /// Verifies missing authorization is represented by the literal None value.
    /// </summary>
    [Fact]
    public async Task Missing_authorization_resolves_to_none()
    {
        using var layout = new AslmFileSystemLayout();
        var appData = await CreateAppDataAsync();
        var githubStore = CreateGitHubStore(appData);
        using var sunriseService = new SunriseService(
            NullLogger<SunriseService>.Instance,
            appData);
        if (SunriseService.IsEnabled)
        {
            await sunriseService.InitializeAsync();
        }
        using var runner = CreateRunner(appData, githubStore, sunriseService);
        var module = CreateOfficialModule();

        runner.GetResolvedSettingValue(
                module,
                new ModuleSetting { Key = "key-aslm", Type = "key-aslm" })
            .Should().Be("None");
        runner.GetResolvedSettingValue(
                module,
                new ModuleSetting { Key = "key-gh", Type = "key-gh" })
            .Should().Be("None");
    }

    /// <summary>
    /// Verifies unreviewed modules cannot resolve connected subsystem keys.
    /// </summary>
    [Fact]
    public async Task Unreviewed_module_does_not_receive_host_keys()
    {
        using var layout = new AslmFileSystemLayout();
        var appData = await CreateAppDataAsync();
        appData.Data.GitHub.PersonalAccessToken = "github-personal-token";
        var githubStore = CreateGitHubStore(appData);
        using var sunriseService = new SunriseService(
            NullLogger<SunriseService>.Instance,
            appData);
        if (SunriseService.IsEnabled)
        {
            await sunriseService.InitializeAsync();
        }
        using var runner = CreateRunner(appData, githubStore, sunriseService);
        var module = ModuleConfigBuilder.Create(
            id: "unreviewed-module",
            configure: config => config.Source.Repo = "unknown/unreviewed-module");

        runner.GetResolvedSettingValue(
                module,
                new ModuleSetting { Key = "key-gh", Type = "key-gh" })
            .Should().Be("None");
    }

    /// <summary>
    /// Creates initialized application data for account-backed key resolution.
    /// </summary>
    private static async Task<AppDataStore> CreateAppDataAsync()
    {
        var appData = new AppDataStore(NullLogger<AppDataStore>.Instance);
        await appData.InitializeAsync();
        return appData;
    }

    /// <summary>
    /// Creates the GitHub account store used by the module runner.
    /// </summary>
    private static GitHubAccountStore CreateGitHubStore(AppDataStore appData) =>
        new(
            appData,
            new GitHubRateLimitStore(NullLogger<GitHubRateLimitStore>.Instance),
            NullLogger<GitHubAccountStore>.Instance);

    /// <summary>
    /// Creates a module runner with the account and trust dependencies used by key resolution.
    /// </summary>
    private static ModuleRunner CreateRunner(
        AppDataStore appData,
        GitHubAccountStore githubStore,
        SunriseService sunriseService) =>
        new(
            null!,
            null!,
            new PortRegistry(appData),
            null!,
            new ModuleConsoleStore(),
            null!,
            null!,
            null!,
            new ModuleTrustService(NullLogger<ModuleTrustService>.Instance),
            githubStore,
            sunriseService,
            new ModuleInteropHostState(),
            null!,
            NullLogger<ModuleRunner>.Instance);

    /// <summary>
    /// Creates the official ASLM-Chat identity recognized by the trust service.
    /// </summary>
    private static ModuleConfig CreateOfficialModule() =>
        ModuleConfigBuilder.Create(
            id: "aslm-chat",
            configure: config => config.Source.Repo = "NEXTGGTECH/ASLM-Chat");
}
