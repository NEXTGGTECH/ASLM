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
}
