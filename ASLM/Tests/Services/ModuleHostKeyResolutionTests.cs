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
        var githubStore = CreateGitHubStore(appData);
        using var sunriseService = new SunriseService(
            NullLogger<SunriseService>.Instance,
            appData);
        await sunriseService.InitializeAsync();
        using var runner = CreateRunner(appData, githubStore, sunriseService);
        var module = CreateOfficialModule();

        runner.GetResolvedSettingValue(
                module,
                new ModuleSetting { Key = "key-aslm", Type = "key-aslm" })
            .Should().Be("aslm-refresh-token");
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
        await sunriseService.InitializeAsync();
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
        await sunriseService.InitializeAsync();
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
