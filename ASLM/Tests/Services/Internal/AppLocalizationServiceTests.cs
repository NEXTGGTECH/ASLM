// Copyright NEXTGGTECH. Apache License 2.0.

using System.Globalization;
using ASLM.Models;
using ASLM.Resources.Strings;
using ASLM.Tests.TestSupport;

namespace ASLM.Tests.Services.Internal
{
    /// <summary>
    /// Verifies system language resolution, culture application, localized resource completeness and the language
    /// names offered by <see cref="AppLocalizationService"/>.
    /// </summary>
    public sealed class AppLocalizationServiceTests
    {
        // Test data

        /// <summary>
        /// Theory data with the code of every language the UI ships, taken from the service's supported list.
        /// </summary>
        public static IEnumerable<object[]> SupportedCultures =>
            AppLocalizationService.SupportedLanguages.Select(language => new object[] { language.Id });


        // Language resolution

        /// <summary>
        /// Verifies that operating system language tags (regional, script, underscore-separated, padded or
        /// mixed-case) map to a shipped locale and that unknown, malformed, empty and null tags fall back to English.
        /// </summary>
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

        /// <summary>
        /// Verifies that every supported language, written in upper case, is resolved back to its canonical resource
        /// code both as a system language and as a persisted language setting.
        /// </summary>
        [Theory]
        [MemberData(nameof(SupportedCultures))]
        public void Supported_language_keeps_its_canonical_resource_code(string language)
        {
            AppLocalizationService.ResolveSystemLanguage(language.ToUpperInvariant()).Should().Be(language);
            AppPersonalizationConfig.NormalizeLanguage(language.ToUpperInvariant()).Should().Be(language);
        }

        /// <summary>
        /// Verifies that the default personalization language comes from the operating system language and is not
        /// affected by the UI culture that ASLM itself has applied.
        /// </summary>
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


        // Culture application

        /// <summary>
        /// Verifies that applying the persisted language switches the resource culture and the default thread cultures,
        /// so background tasks started without the caller's execution context also see it, and that applying it again
        /// reports no change.
        /// </summary>
        [Fact]
        public async Task Applying_language_sets_resources_and_culture_for_background_callbacks()
        {
            // Arrange: a store whose persisted language is Ukrainian; remember every global culture setting to restore.
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
                // Act: apply the persisted language.
                localization.ApplyCulture();

                // Assert: the service, the resources and the localized strings all use Ukrainian.
                localization.GetCurrentLanguage().Should().Be("uk");
                AppResources.Culture.Name.Should().Be("uk");
                localization.GetString(ASLM.Localization.LocalizationKeys.Loading_Text).Should().Be(
                    AppResources.ResourceManager.GetString(ASLM.Localization.LocalizationKeys.Loading_Text, CultureInfo.GetCultureInfo("uk")));

                // A task started with a suppressed execution context cannot inherit the caller's culture,
                // so it only sees the default thread cultures.
                Task<(string UiLanguage, string FormattingLanguage)> backgroundCultures;
                using (ExecutionContext.SuppressFlow())
                {
                    backgroundCultures = Task.Run(() => (CultureInfo.CurrentUICulture.Name, CultureInfo.CurrentCulture.Name));
                }

                (await backgroundCultures).Should().Be(("uk", "uk"));

                // Applying the same language again is not a change.
                localization.ApplyCulture().Should().BeFalse();
            }
            finally
            {
                // Restore the global culture state so other tests are not affected.
                CultureInfo.DefaultThreadCurrentCulture = originalDefaultCulture;
                CultureInfo.DefaultThreadCurrentUICulture = originalDefaultUiCulture;
                CultureInfo.CurrentCulture = originalCulture;
                CultureInfo.CurrentUICulture = originalUiCulture;
                AppResources.Culture = originalResourcesCulture;
            }
        }


        // Localized resources

        /// <summary>
        /// Verifies that the "Disable Home page" setting title exists in every shipped locale, carries the WIP marker,
        /// and is translated (differs from the English text) in every locale other than English.
        /// </summary>
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


        // Language names

        /// <summary>
        /// Verifies that the display name of a language code contains the culture's own name for the language.
        /// </summary>
        [Theory]
        [InlineData("en", "English")]
        [InlineData("ru", "русский")]
        public void GetDisplayName_returns_native_culture_name(string code, string expectedFragment)
        {
            AppLocalizationService.GetDisplayName(code).Should().Contain(expectedFragment);
        }

        /// <summary>
        /// Verifies that the list of supported languages offers English.
        /// </summary>
        [Fact]
        public void SupportedLanguages_contains_english_entry()
        {
            AppLocalizationService.SupportedLanguages
                .Should()
                .Contain(option => option.Id.Equals("en", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Verifies that the language picker label for English contains the word "English".
        /// </summary>
        [Fact]
        public void GetPickerDisplayName_includes_native_name_for_english()
        {
            AppLocalizationService.GetPickerDisplayName("en").Should().Contain("English");
        }


        // English titles

        /// <summary>
        /// Verifies that both the navigation entry and the page title of the downloads page use the plural English
        /// text "Downloads".
        /// </summary>
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
}
