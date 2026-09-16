// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Models;
using ASLM.Tests.TestSupport;

namespace ASLM.Tests.Services;

public sealed class AppDataStoreTests
{
    /// <summary>
    /// Verifies that missing application data is recreated with defaults.
    /// </summary>
    [Fact]
    public async Task LoadAsync_creates_defaults_when_file_missing()
    {
        var layout = new AslmFileSystemLayout();
        layout.ResetDataAppDirectory();
        var store = new AppDataStore(TestLoggerFactory.Create<AppDataStore>());

        await store.LoadAsync();

        store.IsFirstRun.Should().BeTrue();
        store.Data.Navigation.DisableHomePage.Should().BeTrue();
        store.Data.Navigation.RestoreLastPage.Should().BeTrue();
        store.Data.Navigation.LastPage.Should().Be(ShellNavigationRoute.Home);
        store.Data.Personalization.Appearance.Should().Be("System");
        store.Data.Personalization.Language.Should().Be(AppLocalizationService.GetDefaultLanguage());
        File.Exists(layout.AppDataFilePath).Should().BeTrue("LoadAsync persists defaults when the file is missing");
    }

    /// <summary>
    /// Verifies that ordinary application data survives a save and reload.
    /// </summary>
    [Fact]
    public async Task SaveAsync_and_LoadAsync_round_trip()
    {
        var layout = new AslmFileSystemLayout();
        var store = new AppDataStore(TestLoggerFactory.Create<AppDataStore>());
        await store.LoadAsync();

        store.Data.FirstRunCompleted = true;
        store.Data.User.Name = "RoundTrip";
        store.Data.Navigation.DisableHomePage = false;
        store.Data.Navigation.RestoreLastPage = false;
        store.Data.Navigation.LastPage = ShellNavigationRoute.ForModule("aslm-chat");
        await store.SaveAsync();

        var reloaded = new AppDataStore(TestLoggerFactory.Create<AppDataStore>());
        await reloaded.LoadAsync();

        reloaded.IsFirstRun.Should().BeFalse();
        reloaded.Data.Navigation.DisableHomePage.Should().BeFalse();
        reloaded.Data.User.Name.Should().Be("RoundTrip");
        reloaded.Data.Navigation.RestoreLastPage.Should().BeFalse();
        reloaded.Data.Navigation.LastPage.Should().Be("module::aslm-chat");
        File.Exists(layout.AppDataFilePath).Should().BeTrue();
    }

    /// <summary>
    /// Verifies that data written before navigation persistence existed receives safe enabled defaults.
    /// </summary>
    [Fact]
    public async Task LoadAsync_adds_navigation_defaults_to_legacy_data()
    {
        var layout = new AslmFileSystemLayout();
        layout.WriteAppDataJson("""
            {
              "firstRunCompleted": true,
              "user": {
                "name": "Legacy"
              }
            }
            """);
        var store = new AppDataStore(TestLoggerFactory.Create<AppDataStore>());

        await store.LoadAsync();

        store.Data.Navigation.DisableHomePage.Should().BeTrue();
        store.Data.Navigation.RestoreLastPage.Should().BeTrue();
        store.Data.Navigation.LastPage.Should().Be(ShellNavigationRoute.Home);
        store.Data.Personalization.Appearance.Should().Be("System");
    }

    /// <summary>
    /// Verifies that an unsupported persisted appearance falls back to the system theme.
    /// </summary>
    [Fact]
    public async Task LoadAsync_normalizes_unknown_appearance_to_system_theme()
    {
        var layout = new AslmFileSystemLayout();
        layout.WriteAppDataJson("""
            { "personalization": { "appearance": "Unknown" } }
            """);
        var store = new AppDataStore(TestLoggerFactory.Create<AppDataStore>());

        await store.LoadAsync();

        store.Data.Personalization.Appearance.Should().Be("System");
    }

    /// <summary>
    /// Verifies an older navigation object receives the new default without losing its saved page.
    /// </summary>
    [Fact]
    public async Task LoadAsync_adds_home_visibility_default_to_existing_navigation()
    {
        var layout = new AslmFileSystemLayout();
        layout.WriteAppDataJson("""
            { "navigation": { "restoreLastPage": true, "lastPage": "module::aslm-chat" } }
            """);
        var store = new AppDataStore(TestLoggerFactory.Create<AppDataStore>());

        await store.LoadAsync();

        store.Data.Navigation.DisableHomePage.Should().BeTrue();
        store.Data.Navigation.GetInitialPage().Should().Be("module::aslm-chat");
    }

    /// <summary>
    /// Verifies that an hourly check result remains actionable after ASLM restarts inside the same window.
    /// </summary>
    [Fact]
    public async Task SaveAsync_and_LoadAsync_preserve_available_update_candidates()
    {
        var layout = new AslmFileSystemLayout();
        var store = new AppDataStore(TestLoggerFactory.Create<AppDataStore>());
        await store.LoadAsync();
        store.Data.Updates.AvailableAppUpdate = new PersistedUpdateCandidate
        {
            TargetKind = "app",
            TargetId = "aslm",
            Name = "ASLM",
            RemoteVersion = "v9.0.0",
            ReleaseTag = "v9.0.0",
            DownloadUrl = "https://example.invalid/aslm.zip"
        };

        await store.SaveAsync();

        var reloaded = new AppDataStore(TestLoggerFactory.Create<AppDataStore>());
        await reloaded.LoadAsync();

        reloaded.Data.Updates.AvailableAppUpdate.Should().NotBeNull();
        reloaded.Data.Updates.AvailableAppUpdate!.ReleaseTag.Should().Be("v9.0.0");
        reloaded.Data.Updates.AvailableAppUpdate.DownloadUrl.Should().Be("https://example.invalid/aslm.zip");
    }

    /// <summary>
    /// Verifies that invalid persisted JSON falls back to safe defaults.
    /// </summary>
    [Fact]
    public async Task LoadAsync_recreates_defaults_on_invalid_json()
    {
        var layout = new AslmFileSystemLayout();
        layout.WriteAppDataJson("{ not valid json");

        var store = new AppDataStore(TestLoggerFactory.Create<AppDataStore>());
        await store.LoadAsync();

        store.Data.User.Name.Should().BeEmpty();
        store.IsFirstRun.Should().BeTrue();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"personalization\":null}")]
    [InlineData("{\"personalization\":{}}")]
    [InlineData("{\"personalization\":{\"language\":null}}")]
    [InlineData("{\"personalization\":{\"language\":\"\"}}")]
    public async Task Missing_language_uses_system_default_and_survives_save(string json)
    {
        var layout = new AslmFileSystemLayout();
        layout.WriteAppDataJson(json);
        var store = new AppDataStore(TestLoggerFactory.Create<AppDataStore>());
        var expected = AppLocalizationService.GetDefaultLanguage();

        await store.LoadAsync();
        store.Data.Personalization.Language.Should().Be(expected);
        await store.SaveAsync();

        var reloaded = new AppDataStore(TestLoggerFactory.Create<AppDataStore>());
        await reloaded.LoadAsync();
        reloaded.Data.Personalization.Language.Should().Be(expected);
    }

    [Theory]
    [InlineData("en", "en")]
    [InlineData("de", "de")]
    [InlineData("PT-br", "pt-BR")]
    [InlineData("zh-hant", "zh-Hant")]
    [InlineData("unsupported", "en")]
    public async Task Saved_language_takes_precedence_over_system_default(string savedLanguage, string expected)
    {
        var layout = new AslmFileSystemLayout();
        layout.WriteAppDataJson($$"""
            { "firstRunCompleted": true, "personalization": { "language": "{{savedLanguage}}" } }
            """);
        var store = new AppDataStore(TestLoggerFactory.Create<AppDataStore>());

        await store.LoadAsync();

        store.Data.Personalization.Language.Should().Be(expected);
        new AppLocalizationService(store).GetCurrentLanguage().Should().Be(expected);
    }
}
