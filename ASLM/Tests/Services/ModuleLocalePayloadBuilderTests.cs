// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Tests.TestSupport;
using System.Text.Json;

namespace ASLM.Tests.Services;

public sealed class ModuleLocalePayloadBuilderTests
{
    [Fact]
    public void BuildJson_uses_resolved_system_default_before_user_selects_a_language()
    {
        _ = new AslmFileSystemLayout();
        var appData = new AppDataStore(TestLoggerFactory.Create<AppDataStore>());
        var builder = new ModuleLocalePayloadBuilder(appData, TestLoggerFactory.Create<ModuleLocalePayloadBuilder>());

        using var document = JsonDocument.Parse(builder.BuildJson());

        document.RootElement.GetProperty("language").GetString().Should().Be(AppLocalizationService.GetDefaultLanguage());
    }

    [Fact]
    public void BuildJson_serializes_active_language()
    {
        _ = new AslmFileSystemLayout();
        var appData = new AppDataStore(TestLoggerFactory.Create<AppDataStore>());
        appData.Data.Personalization.Language = "de";

        var builder = new ModuleLocalePayloadBuilder(appData, TestLoggerFactory.Create<ModuleLocalePayloadBuilder>());
        using var document = JsonDocument.Parse(builder.BuildJson());

        document.RootElement.GetProperty("language").GetString().Should().Be("de");
        document.RootElement.GetProperty("displayName").GetString().Should().NotBeNullOrWhiteSpace();
    }
}
