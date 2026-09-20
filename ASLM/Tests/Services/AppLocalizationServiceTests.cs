// Copyright NEXTGGTECH. Apache License 2.0.


using System.Globalization;
using ASLM.Models;
using ASLM.Resources.Strings;
using ASLM.Tests.TestSupport;

namespace ASLM.Tests.Services;

public sealed class AppLocalizationServiceTests
{
    public static IEnumerable<object[]> SupportedCultures =>
        AppLocalizationService.SupportedLanguages.Select(language => new object[] { language.Id });

    [Theory]
    [InlineData("en-US", "en")]
    [InlineData("en-GB", "en")]
    [InlineData("ru-RU", "ru")]
    [InlineData("uk-UA", "uk")]
    [InlineData("es-MX", "es")]
    [InlineData("fr-CA", "fr")]
    [InlineData("ar-SA", "ar")]
    [InlineData("pt-BR", "pt-BR")]
    [InlineData("pt-PT", "pt")]
    [InlineData("pt-AO", "pt")]
    [InlineData("zh-CN", "zh-Hans")]
    [InlineData("zh-SG", "zh-Hans")]
    [InlineData("zh-TW", "zh-Hant")]
    [InlineData("zh-HK", "zh-Hant")]
    [InlineData("zh-MO", "zh-Hant")]
    [InlineData("zh-Hans-TW", "zh-Hans")]
    [InlineData("zh-Hant-CN", "zh-Hant")]
    [InlineData("zh", "zh-Hans")]
    [InlineData("pt_BR", "pt-BR")]
    [InlineData(" RU_ru ", "ru")]
    [InlineData("fi-FI", "en")]
    [InlineData("he-IL", "en")]
    [InlineData("not a locale!", "en")]
    [InlineData("", "en")]
    [InlineData(null, "en")]
    public void System_language_matches_shipped_locale_or_falls_back_to_english(string? systemLanguage, string expected)
    {
        AppLocalizationService.ResolveSystemLanguage(systemLanguage).Should().Be(expected);
    }

    [Theory]
    [MemberData(nameof(SupportedCultures))]
    public void Supported_language_keeps_its_canonical_resource_code(string language)
    {
        AppLocalizationService.ResolveSystemLanguage(language.ToUpperInvariant()).Should().Be(language);
        AppPersonalizationConfig.NormalizeLanguage(language.ToUpperInvariant()).Should().Be(language);
    }

    [Fact]
    public void Defaults_read_native_os_language_even_after_application_culture_changes()
    {
        var systemLanguage = global::Windows.System.UserProfile.GlobalizationPreferences.Languages.FirstOrDefault();
        var expected = AppLocalizationService.ResolveSystemLanguage(systemLanguage);
        var originalCulture = CultureInfo.CurrentUICulture;
        try
        {
            // The settings reset must not reuse the language the user selected inside ASLM.
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(expected == "ja" ? "de" : "ja");

            new AppPersonalizationConfig().Language.Should().Be(expected);
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalCulture;
        }
    }

    [Fact]
    public async Task Applying_language_sets_resources_and_culture_for_background_callbacks()
    {
        _ = new AslmFileSystemLayout();
        var store = new AppDataStore(TestLoggerFactory.Create<AppDataStore>());
        store.Data.Personalization.Language = "uk";
        var localization = new AppLocalizationService(store);
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        var originalDefaultCulture = CultureInfo.DefaultThreadCurrentCulture;
        var originalDefaultUiCulture = CultureInfo.DefaultThreadCurrentUICulture;
        var originalResourcesCulture = AppResources.Culture;
        try
        {
            localization.ApplyCulture();

            localization.GetCurrentLanguage().Should().Be("uk");
            AppResources.Culture.Name.Should().Be("uk");
            localization.GetString(ASLM.Localization.LocalizationKeys.Loading_Text).Should().Be(
                AppResources.ResourceManager.GetString(ASLM.Localization.LocalizationKeys.Loading_Text, CultureInfo.GetCultureInfo("uk")));

            Task<(string UiLanguage, string FormattingLanguage)> backgroundCultures;
            using (ExecutionContext.SuppressFlow())
            {
                backgroundCultures = Task.Run(() => (CultureInfo.CurrentUICulture.Name, CultureInfo.CurrentCulture.Name));
            }

            (await backgroundCultures).Should().Be(("uk", "uk"));
            localization.ApplyCulture().Should().BeFalse();
        }
        finally
        {
            CultureInfo.DefaultThreadCurrentCulture = originalDefaultCulture;
            CultureInfo.DefaultThreadCurrentUICulture = originalDefaultUiCulture;
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
            AppResources.Culture = originalResourcesCulture;
        }
    }

    [Theory]
    [MemberData(nameof(SupportedCultures))]
    public void Disable_home_page_label_is_translated_in_every_locale_including_wip(string cultureName)
    {
        var culture = System.Globalization.CultureInfo.GetCultureInfo(cultureName == "en" ? "" : cultureName);
        var resources = ASLM.Resources.Strings.AppResources.ResourceManager.GetResourceSet(culture, true, false);
        resources.Should().NotBeNull();
        var title = resources!.GetString(ASLM.Localization.LocalizationKeys.Settings_DisableHomePage_Title);

        title.Should().NotBeNullOrWhiteSpace().And.EndWith(" (WIP)");
        if (cultureName == "en")
            title.Should().Be("Disable Home page (WIP)");
        else
            title.Should().NotBe("Disable Home page (WIP)");
    }

    [Theory]
    [InlineData("en", "English")]
    [InlineData("ru", "русский")]
    public void GetDisplayName_returns_native_culture_name(string code, string expectedFragment)
    {
        AppLocalizationService.GetDisplayName(code).Should().Contain(expectedFragment);
    }

    [Fact]
    public void SupportedLanguages_contains_english_entry()
    {
        AppLocalizationService.SupportedLanguages
            .Should()
            .Contain(option => option.Id.Equals("en", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetPickerDisplayName_includes_native_name_for_english()
    {
        AppLocalizationService.GetPickerDisplayName("en").Should().Contain("English");
    }

    [Theory]
    [InlineData(ASLM.Localization.LocalizationKeys.AppShell_Nav_Download)]
    [InlineData(ASLM.Localization.LocalizationKeys.Downloads_Title)]
    public void Downloads_page_and_navigation_use_plural_english_title(string key)
    {
        ASLM.Resources.Strings.AppResources.ResourceManager
            .GetString(key, System.Globalization.CultureInfo.GetCultureInfo("en"))
            .Should().Be("Downloads");
    }
}
