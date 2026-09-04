// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Models;
using ASLM.Tests.TestSupport;

namespace ASLM.Tests.Services;

/// <summary>
/// Verifies built-in settings validation, drafts, categories, and module visibility rules.
/// </summary>
public sealed class SettingsServiceTests
{
    /// <summary>
    /// Verifies module port drafts accept only the supported range.
    /// </summary>
    [Theory]
    [InlineData("20000", true)]
    [InlineData("abc", false)]
    [InlineData("99999", false)]
    [InlineData("1023", false)]
    public void TryParsePortStart_validates_range(string draft, bool expectedSuccess)
    {
        var result = SettingsService.TryParsePortStart(draft);

        result.Success.Should().Be(expectedSuccess);
        if (expectedSuccess)
        {
            result.ModulesStart.Should().Be(int.Parse(draft));
        }
        else
        {
            result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
        }
    }

    /// <summary>
    /// Verifies display names are trimmed and cannot be empty.
    /// </summary>
    [Theory]
    [InlineData(" Alice ", true, "Alice")]
    [InlineData("   ", false, "")]
    public void TryValidateDisplayName_trims_and_rejects_empty(string draft, bool expected, string expectedName)
    {
        var success = SettingsService.TryValidateDisplayName(draft, out var name, out var error);

        success.Should().Be(expected);
        name.Should().Be(expectedName);
        if (!expected)
        {
            error.Should().NotBeNullOrWhiteSpace();
        }
    }

    /// <summary>
    /// Verifies update drafts retain the fixed automatic-check period.
    /// </summary>
    [Fact]
    public void TryValidateAndBuildUpdateSettings_normalizes_fixed_check_period()
    {
        var draft = new UpdateBaseline(
            true,
            false,
            "release",
            "release");

        var success = SettingsService.TryValidateAndBuildUpdateSettings(draft, out var settings, out var error);

        success.Should().BeTrue();
        error.Should().BeEmpty();
        settings.AutoCheckPeriodHours.Should().Be(1);
        settings.ModuleDefaultMode.Should().Be("release");
    }

    /// <summary>
    /// Verifies legacy branch defaults are normalized to the only supported release mode.
    /// </summary>
    [Fact]
    public void AppUpdateSettings_normalize_forces_release_module_mode()
    {
        var settings = new AppUpdateSettings { ModuleDefaultMode = "branch" };

        settings.Normalize();

        settings.ModuleDefaultMode.Should().Be("release");
    }

    /// <summary>
    /// Verifies save summaries include deferred runtime settings.
    /// </summary>
    [Fact]
    public void BuildSaveMessage_describes_deferred_settings()
    {
        var message = SettingsService.BuildSaveMessage(
            true,
            false,
            ["Setting A", "Setting B"]);

        message.Should().Contain("could not be applied immediately");
        message.Should().Contain("Setting A");
    }

    /// <summary>
    /// Verifies persisted application data is copied into detached drafts.
    /// </summary>
    [Fact]
    public void BuildAslmDraftSnapshot_reads_app_data()
    {
        _ = new AslmFileSystemLayout();
        var store = new AppDataStore(TestLoggerFactory.Create<AppDataStore>());
        store.Data.User.Name = "Tester";
        store.Data.Ports.ModulesStart = 21000;
        store.Data.Navigation.RestoreLastPage = false;

        var draft = SettingsService.BuildAslmDraftSnapshot(store, apiServerEnabled: true);

        draft.UserName.Should().Be("Tester");
        draft.PortStart.Should().Be("21000");
        draft.ApiServerEnabled.Should().BeTrue();
        draft.RestoreLastPage.Should().BeFalse();
    }

    /// <summary>
    /// Verifies built-in drafts are copied into application data before persistence.
    /// </summary>
    [Fact]
    public void ApplyDraftsToAppData_persists_values_in_memory()
    {
        _ = new AslmFileSystemLayout();
        var store = new AppDataStore(TestLoggerFactory.Create<AppDataStore>());
        var console = new ConsoleBaseline(false, true, false);
        var updates = new AppUpdateSettings { AutoCheckPeriodHours = 12 };

        SettingsService.ApplyDraftsToAppData(
            store,
            "Bob",
            22000,
            console,
            updates,
            restoreLastPage: false,
            legalAutoAcceptUpdates: true);

        store.Data.User.Name.Should().Be("Bob");
        store.Data.Ports.ModulesStart.Should().Be(22000);
        store.Data.Consoles.ShowCompletedProcesses.Should().BeTrue();
        store.Data.Updates.AutoCheckPeriodHours.Should().Be(1);
        store.Data.Navigation.RestoreLastPage.Should().BeFalse();
    }

    /// <summary>
    /// Verifies built-in dirty checks detect values that differ from their baseline.
    /// </summary>
    [Fact]
    public void HasUnsaved_changes_detect_differences()
    {
        var baseline = new AslmBaseline("Alice", "20000", true);
        SettingsService.HasUnsavedAccountChanges("Bob", baseline).Should().BeTrue();
        SettingsService.HasUnsavedPortChanges("20001", baseline).Should().BeTrue();
        SettingsService.HasUnsavedApiServerChanges(false, baseline).Should().BeTrue();
    }

    /// <summary>
    /// Verifies host-only settings stay outside the user-facing editor.
    /// </summary>
    [Fact]
    public void ShouldDisplaySetting_hides_automatic_types()
    {
        SettingsService.ShouldDisplaySetting(new ModuleSetting { Type = "port" }).Should().BeFalse();
        SettingsService.ShouldDisplaySetting(new ModuleSetting { Type = "theme" }).Should().BeFalse();
        SettingsService.ShouldDisplaySetting(new ModuleSetting { Type = "text" }).Should().BeTrue();
    }

    /// <summary>
    /// Verifies launch reconciliation includes only values owned by ASLM or its managed runtimes.
    /// </summary>
    [Theory]
    [InlineData("port", true)]
    [InlineData("theme", true)]
    [InlineData("locale", true)]
    [InlineData("key-aslm", true)]
    [InlineData("key-gh", true)]
    [InlineData("engine", true)]
    [InlineData("path", true)]
    [InlineData("data", true)]
    [InlineData("models", true)]
    [InlineData("bool", false)]
    [InlineData("string", false)]
    [InlineData("select", false)]
    [InlineData("password", false)]
    public void Launch_synchronization_includes_only_host_controlled_types(string type, bool expected)
    {
        new ModuleSetting { Type = type }.IsSynchronizedOnLaunch.Should().Be(expected);
    }

    /// <summary>
    /// Verifies a closed settings page can stop a pending runtime-value load before any module work starts.
    /// </summary>
    [Fact]
    public async Task LoadSettingValueAsync_honors_cancellation()
    {
        var service = new SettingsService(null!, null!, null!);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Func<Task> action = () => service.LoadSettingValueAsync(
            ModuleConfigBuilder.Create(),
            new ModuleSetting { Key = "value", Type = "string" },
            cancellation.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>
    /// Verifies explicit dependencies accept only user-editable boolean controllers.
    /// </summary>
    [Fact]
    public void Explicit_dependency_uses_only_user_bool_controller()
    {
        var controller = new ModuleSetting { Key = "enabled", Type = "bool" };
        var child = new ModuleSetting { Key = "url", Type = "string", DependsOn = "enabled" };
        var settings = new[] { controller, child };

        SettingsService.ShouldRenderSetting(
                child,
                settings,
                new Dictionary<string, object?> { ["enabled"] = false })
            .Should().BeFalse();

        SettingsService.ShouldRenderSetting(
                child,
                settings,
                new Dictionary<string, object?> { ["enabled"] = true })
            .Should().BeTrue();
    }

    /// <summary>
    /// Verifies explicit boolean dependencies control every displayed special setting type.
    /// </summary>
    [Theory]
    [InlineData("engine")]
    [InlineData("path")]
    [InlineData("data")]
    [InlineData("models")]
    public void Explicit_dependency_controls_displayed_special_types(string type)
    {
        var controller = new ModuleSetting { Key = "enabled", Type = "bool" };
        var child = new ModuleSetting { Key = $"runtime-{type}", Type = type, DependsOn = "enabled" };
        var settings = new[] { controller, child };

        SettingsService.ShouldRenderSetting(
                child,
                settings,
                new Dictionary<string, object?> { ["enabled"] = false })
            .Should().BeFalse();
    }

    /// <summary>
    /// Verifies dependent visibility includes the complete parent chain.
    /// </summary>
    [Fact]
    public void Explicit_dependency_honors_parent_visibility_recursively()
    {
        var root = new ModuleSetting { Key = "root", Type = "bool" };
        var nested = new ModuleSetting { Key = "nested", Type = "bool", DependsOn = "root" };
        var child = new ModuleSetting { Key = "value", Type = "string", DependsOn = "nested" };
        var settings = new[] { root, nested, child };

        SettingsService.ShouldRenderSetting(
                child,
                settings,
                new Dictionary<string, object?> { ["root"] = false, ["nested"] = true })
            .Should().BeFalse();
    }

    /// <summary>
    /// Verifies dependency cycles fail open instead of hiding settings permanently.
    /// </summary>
    [Fact]
    public void Explicit_dependency_cycle_is_fail_open()
    {
        var first = new ModuleSetting { Key = "first", Type = "bool", DependsOn = "second" };
        var second = new ModuleSetting { Key = "second", Type = "bool", DependsOn = "first" };
        var settings = new[] { first, second };

        SettingsService.ShouldRenderSetting(
                first,
                settings,
                new Dictionary<string, object?> { ["first"] = false, ["second"] = false })
            .Should().BeTrue();
    }

    /// <summary>
    /// Verifies category and dependency metadata applies to every visible setting type.
    /// </summary>
    [Theory]
    [InlineData("engine", true)]
    [InlineData("path", true)]
    [InlineData("data", true)]
    [InlineData("models", true)]
    [InlineData("bool", true)]
    [InlineData("string", true)]
    [InlineData("port", false)]
    [InlineData("theme", false)]
    [InlineData("locale", false)]
    [InlineData("key-aslm", false)]
    [InlineData("key-gh", false)]
    public void Settings_metadata_applies_to_visible_types(string type, bool expected)
    {
        SettingsService.IsSettingsMetadataEligible(new ModuleSetting { Type = type })
            .Should().Be(expected);
    }

    /// <summary>
    /// Verifies that reset drafts restore manifest defaults when committed.
    /// </summary>
    [Fact]
    public void ResetModuleToDefaults_restores_manifest_defaults()
    {
        var module = ModuleConfigBuilder.Create(configure: m =>
        {
            m.Settings =
            [
                new ModuleSetting
                {
                    Key = "flag",
                    Type = "bool",
                    Default = "false",
                    Value = "true",
                    UseCustomValue = true
                }
            ];
        });

        var moduleDraft = new ModuleSettingsDraft(module);

        SettingsService.ResetModuleToDefaults(moduleDraft);
        moduleDraft.ApplyToModule();

        Convert.ToString(module.Settings[0].Value).Should().Be("False");
    }

    /// <summary>
    /// Verifies module categories are assigned to the module sidebar group.
    /// </summary>
    [Fact]
    public void GetCategoryGroup_maps_module_kind()
    {
        var moduleCategory = new SettingsCategory(
            "module::x",
            "X",
            "desc",
            SettingsCategoryKind.Module,
            ModuleConfigBuilder.Create(),
            false);

        SettingsPresentationBuilder.GetCategoryGroup(moduleCategory).Should().Be(SettingsCategoryGroup.Modules);
    }

    /// <summary>
    /// Verifies only installed and initialized modules expose settings categories.
    /// </summary>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    public void IsModuleEligibleForSettings_requires_installed_first_run_and_displayable_settings(
        bool installed,
        bool firstRunCompleted,
        bool expected)
    {
        var module = ModuleConfigBuilder.Create(configure: module =>
        {
            module.Status.Installed = installed;
            module.Status.FirstRunCompleted = firstRunCompleted;
            module.Settings =
            [
                new ModuleSetting
                {
                    Key = "flag",
                    Type = "text",
                    Default = "false"
                }
            ];
        });

        SettingsService.IsModuleEligibleForSettings(module).Should().Be(expected);
    }

    /// <summary>
    /// Verifies modules containing only host settings do not expose empty categories.
    /// </summary>
    [Fact]
    public void IsModuleEligibleForSettings_excludes_modules_without_displayable_settings()
    {
        var module = ModuleConfigBuilder.Create(configure: module =>
        {
            module.Status.Installed = true;
            module.Status.FirstRunCompleted = true;
            module.Settings =
            [
                new ModuleSetting
                {
                    Key = "http",
                    Type = "port",
                    Default = "0"
                }
            ];
        });

        SettingsService.IsModuleEligibleForSettings(module).Should().BeFalse();
    }

    /// <summary>
    /// Verifies sidebar construction excludes modules that cannot expose settings yet.
    /// </summary>
    [Fact]
    public void BuildCategories_includes_only_eligible_modules()
    {
        var eligible = ModuleConfigBuilder.Create(
            id: "ready-module",
            name: "Ready Module",
            configure: module =>
            {
                module.Status.Installed = true;
                module.Status.FirstRunCompleted = true;
                module.Settings =
                [
                    new ModuleSetting
                    {
                        Key = "flag",
                        Type = "text",
                        Default = "false"
                    }
                ];
            });

        var stub = ModuleConfigBuilder.Create(
            id: "stub-module",
            name: "Stub Module",
            configure: module =>
            {
                module.Status.Installed = false;
                module.Status.FirstRunCompleted = false;
                module.Settings =
                [
                    new ModuleSetting
                    {
                        Key = "flag",
                        Type = "text",
                        Default = "false"
                    }
                ];
            });

        var categories = SettingsPresentationBuilder.BuildCategories([eligible, stub]);

        categories
            .Where(category => category.Kind == SettingsCategoryKind.Module)
            .Select(category => category.Module!.Id)
            .Should()
            .Equal("ready-module");
    }
}
