// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Models;
using ASLM.Tests.TestSupport;
using System.Text.Json;

namespace ASLM.Tests.Services;

public sealed class ModuleThemePayloadBuilderTests
{
    [Fact]
    public void BuildJson_includes_appearance_and_palette_keys()
    {
        _ = new AslmFileSystemLayout();
        var appData = new AppDataStore(TestLoggerFactory.Create<AppDataStore>());
        appData.Data.Personalization.Appearance = "Dark";

        var themes = new CustomThemesStore(TestLoggerFactory.Create<CustomThemesStore>());
        var builder = new ModuleThemePayloadBuilder(appData, themes, TestLoggerFactory.Create<ModuleThemePayloadBuilder>());

        using var document = JsonDocument.Parse(builder.BuildJson());

        document.RootElement.GetProperty("appearance").GetString().Should().Be("Dark");
        document.RootElement.GetProperty("theme").GetString().Should().Be("dark");
        document.RootElement.TryGetProperty("colors", out var colors).Should().BeTrue();
        colors.EnumerateObject().Should().NotBeEmpty();
    }
}
