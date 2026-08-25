// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Models;
using ASLM.Tests.TestSupport;

namespace ASLM.Tests.Services;

/// <summary>
/// Verifies detached settings drafts, baselines, reset, and commit behavior.
/// </summary>
public sealed class SettingsEditSessionTests
{
    /// <summary>
    /// Verifies that application drafts detect changes and can discard them without reloading stores.
    /// </summary>
    [Fact]
    public void Application_draft_tracks_and_discards_changes()
    {
        var draft = new ApplicationSettingsDraft();
        draft.LoadAslm(
            new AslmDraftSnapshot(
                "Alice",
                "20000",
                true,
                new ConsoleBaseline(true, false, true),
                new UpdateBaseline(true, false, "release", "release")),
            legalAutoAcceptUpdates: true);

        draft.UserName = "Bob";
        draft.PortStart = "21000";

        draft.HasAccountChanges.Should().BeTrue();
        draft.HasAslmChanges.Should().BeTrue();

        draft.DiscardAslm();

        draft.UserName.Should().Be("Alice");
        draft.PortStart.Should().Be("20000");
        draft.HasAccountChanges.Should().BeFalse();
        draft.HasAslmChanges.Should().BeFalse();
    }

    /// <summary>
    /// Verifies accepted cross-category values become the next discard target.
    /// </summary>
    [Fact]
    public void Application_draft_accepts_and_restores_all_shared_settings()
    {
        var draft = new ApplicationSettingsDraft();
        draft.LoadAslm(
            new AslmDraftSnapshot(
                "Alice",
                "20000",
                true,
                new ConsoleBaseline(true, false, true),
                new UpdateBaseline(true, false, "release", "release")),
            legalAutoAcceptUpdates: true);

        draft.ApiServerEnabled = false;
        draft.Console = new ConsoleBaseline(false, true, false);
        draft.Update = new UpdateBaseline(false, true, "pre-release", "pre-release");
        draft.LegalAutoAcceptUpdates = false;
        draft.AcceptAslm();

        draft.ApiServerEnabled = true;
        draft.Console = new ConsoleBaseline(true, true, true);
        draft.Update = new UpdateBaseline(true, false, "release", "release");
        draft.LegalAutoAcceptUpdates = true;
        draft.DiscardAslm();

        draft.ApiServerEnabled.Should().BeFalse();
        draft.Console.Should().Be(new ConsoleBaseline(false, true, false));
        draft.Update.Should().Be(new UpdateBaseline(false, true, "pre-release", "pre-release"));
        draft.LegalAutoAcceptUpdates.Should().BeFalse();
        draft.HasAslmChanges.Should().BeFalse();
    }

    /// <summary>
    /// Verifies that personalization drafts do not share mutable state with persisted app data.
    /// </summary>
    [Fact]
    public void Personalization_draft_is_detached_from_persisted_model()
    {
        var persisted = new AppPersonalizationConfig
        {
            Appearance = "Dark",
            Language = "en",
            CustomThemeId = null
        };
        var draft = new ApplicationSettingsDraft();
        draft.LoadPersonalization(persisted);

        draft.Personalization.Appearance = "Light";

        persisted.Appearance.Should().Be("Dark");
        draft.HasPersonalizationChanges.Should().BeTrue();
    }

    /// <summary>
    /// Verifies that editing a module draft does not mutate the manifest until commit.
    /// </summary>
    [Fact]
    public void Module_draft_delays_manifest_mutation_until_commit()
    {
        var module = CreateModuleWithSetting(new ModuleSetting
        {
            Key = "message",
            Name = "Message",
            Type = "string",
            Default = "default",
            Value = "saved"
        });
        var moduleDraft = new ModuleSettingsDraft(module);
        var settingDraft = moduleDraft.GetSetting("message");

        settingDraft.Value = "edited";

        module.Settings[0].Value.Should().Be("saved");
        moduleDraft.HasChanges.Should().BeTrue();

        moduleDraft.ApplyToModule();

        module.Settings[0].Value.Should().Be("edited");
    }

    /// <summary>
    /// Verifies that managed values retain their last custom value while host mode is active.
    /// </summary>
    [Fact]
    public void Managed_draft_preserves_custom_value_behind_host_value()
    {
        var module = CreateModuleWithSetting(new ModuleSetting
        {
            Key = "runtime_path",
            Name = "Runtime path",
            Type = "path",
            Default = "default-path",
            Value = "custom-path",
            UseCustomValue = false
        });
        var moduleDraft = new ModuleSettingsDraft(module);
        var settingDraft = moduleDraft.GetSetting("runtime_path");

        settingDraft.LoadRuntimeValue("host-path", "host-path", isReadOnly: false);

        settingDraft.Value.Should().Be("custom-path");
        settingDraft.EffectiveValue.Should().Be("host-path");

        settingDraft.UseCustomValue = true;

        settingDraft.EffectiveValue.Should().Be("custom-path");
        settingDraft.HasChanges.Should().BeTrue();
    }

    /// <summary>
    /// Verifies that resetting managed settings returns control to ASLM without losing host resolution.
    /// </summary>
    [Fact]
    public void Managed_draft_reset_returns_to_host_control()
    {
        var module = CreateModuleWithSetting(new ModuleSetting
        {
            Key = "runtime_models",
            Name = "Runtime models",
            Type = "models",
            Default = "default-models",
            Value = "custom-models",
            UseCustomValue = true
        });
        var moduleDraft = new ModuleSettingsDraft(module);
        var settingDraft = moduleDraft.GetSetting("runtime_models");
        settingDraft.AutomaticValue = "host-models";

        SettingsService.ResetModuleToDefaults(moduleDraft);

        settingDraft.UseCustomValue.Should().BeFalse();
        settingDraft.Value.Should().Be("default-models");
        settingDraft.EffectiveValue.Should().Be("host-models");
    }

    /// <summary>
    /// Verifies discard restores accepted custom and automatic values without runtime loading.
    /// </summary>
    [Fact]
    public void Module_draft_discard_restores_accepted_user_and_host_state()
    {
        var module = CreateModuleWithSetting(new ModuleSetting
        {
            Key = "runtime_path",
            Name = "Runtime path",
            Type = "path",
            Default = "default-path",
            Value = "saved-custom-path",
            UseCustomValue = false
        });
        var moduleDraft = new ModuleSettingsDraft(module);
        var settingDraft = moduleDraft.GetSetting("runtime_path");
        settingDraft.LoadRuntimeValue("host-path", "host-path", isReadOnly: false);

        settingDraft.Value = "edited-path";
        settingDraft.AutomaticValue = "changed-host-path";
        settingDraft.UseCustomValue = true;
        moduleDraft.DiscardChanges();

        settingDraft.Value.Should().Be("saved-custom-path");
        settingDraft.AutomaticValue.Should().Be("host-path");
        settingDraft.UseCustomValue.Should().BeFalse();
        settingDraft.EffectiveValue.Should().Be("host-path");
        moduleDraft.HasChanges.Should().BeFalse();
    }

    /// <summary>
    /// Verifies module reset leaves host-owned hidden setting types unchanged.
    /// </summary>
    [Theory]
    [InlineData("port")]
    [InlineData("theme")]
    [InlineData("locale")]
    public void Module_reset_preserves_hidden_host_setting(string type)
    {
        var module = CreateModuleWithSetting(new ModuleSetting
        {
            Key = $"host-{type}",
            Name = $"Host {type}",
            Type = type,
            Default = "default-value",
            Value = "host-value"
        });
        var moduleDraft = new ModuleSettingsDraft(module);

        SettingsService.ResetModuleToDefaults(moduleDraft);
        moduleDraft.ApplyToModule();

        module.Settings[0].Value.Should().Be("host-value");
        moduleDraft.HasChanges.Should().BeFalse();
    }

    /// <summary>
    /// Verifies that read-only engine status cannot be written into a manifest during commit.
    /// </summary>
    [Fact]
    public void Read_only_engine_draft_never_mutates_manifest()
    {
        var module = CreateModuleWithSetting(new ModuleSetting
        {
            Key = "runtime",
            Name = "Runtime",
            Type = "engine",
            Default = false,
            Value = false
        });
        var moduleDraft = new ModuleSettingsDraft(module);
        var settingDraft = moduleDraft.GetSetting("runtime");
        settingDraft.LoadRuntimeValue(true, automaticValue: null, isReadOnly: true);

        moduleDraft.ApplyToModule();

        module.Settings[0].Value.Should().Be(false);
        settingDraft.Value.Should().Be(true);
        settingDraft.HasChanges.Should().BeFalse();
    }

    /// <summary>
    /// Verifies visibility is derived from detached controller values without changing the manifest.
    /// </summary>
    [Fact]
    public void Dependency_visibility_uses_detached_draft_snapshot()
    {
        var controller = new ModuleSetting
        {
            Key = "feature-enabled",
            Name = "Feature enabled",
            Type = "bool",
            Default = false,
            Value = false
        };
        var dependent = new ModuleSetting
        {
            Key = "feature-value",
            Name = "Feature value",
            Type = "string",
            Default = "value",
            Value = "value",
            DependsOn = controller.Key
        };
        var module = ModuleConfigBuilder.Create(configure: candidate =>
        {
            candidate.Settings = [controller, dependent];
            candidate.Normalize();
        });
        var moduleDraft = new ModuleSettingsDraft(module);

        moduleDraft.GetSetting(controller.Key).Value = true;
        SettingsService.RefreshModuleDraftVisibility(moduleDraft);

        moduleDraft.GetSetting(dependent.Key).IsVisible.Should().BeTrue();
        controller.Value.Should().Be(false);

        moduleDraft.GetSetting(controller.Key).Value = false;
        SettingsService.RefreshModuleDraftVisibility(moduleDraft);

        moduleDraft.GetSetting(dependent.Key).IsVisible.Should().BeFalse();
        controller.Value.Should().Be(false);
    }

    /// <summary>
    /// Creates one normalized module whose visible setting is suitable for draft tests.
    /// </summary>
    private static ModuleConfig CreateModuleWithSetting(ModuleSetting setting) =>
        ModuleConfigBuilder.Create(configure: module =>
        {
            module.Settings = [setting];
            module.Normalize();
        });
}
