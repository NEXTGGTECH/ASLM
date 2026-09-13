// Copyright NEXTGGTECH. Apache License 2.0.


namespace ASLM.Tests.Services;

public sealed class AppLocalizationServiceTests
{
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
