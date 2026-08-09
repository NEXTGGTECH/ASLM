// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Models;
using ASLM.Tests.TestSupport;

namespace ASLM.Tests.Services;

/// <summary>
/// Verifies bindable module settings behavior independently from MAUI control creation.
/// </summary>
public sealed class ModuleSettingsPageViewModelTests
{
    /// <summary>
    /// Verifies a bound editor updates only its detached draft before persistence.
    /// </summary>
    [Fact]
    public void Editor_change_updates_detached_draft_without_mutating_manifest()
    {
        var setting = CreateSetting("caption", "string", "original");
        var module = CreateModule(setting);
        var page = CreatePage(new ModuleSettingsDraft(module));
        var item = page.Sections.SelectMany(static section => section.Settings).Single();

        item.TextValue = "edited";

        item.Draft.Value.Should().Be("edited");
        item.Draft.HasChanges.Should().BeTrue();
        setting.Value.Should().Be("original");
    }

    /// <summary>
    /// Verifies dependency changes reuse existing sections and rows while updating visibility in place.
    /// </summary>
    [Fact]
    public void Dependency_change_refreshes_visibility_without_rebuilding_presenters()
    {
        var controller = CreateSetting("enabled", "bool", false);
        var dependent = CreateSetting("details", "string", "value");
        dependent.DependsOn = controller.Key;
        var page = CreatePage(new ModuleSettingsDraft(CreateModule(controller, dependent)));
        var section = page.Sections.Single();
        var controllerItem = section.Settings.Single(item => item.Draft.Setting.Key == controller.Key);
        var dependentItem = section.Settings.Single(item => item.Draft.Setting.Key == dependent.Key);

        dependentItem.IsVisible.Should().BeFalse();
        controllerItem.BooleanValue = true;

        page.Sections.Single().Should().BeSameAs(section);
        section.Settings.Single(item => item.Draft.Setting.Key == dependent.Key).Should().BeSameAs(dependentItem);
        dependentItem.IsVisible.Should().BeTrue();
        section.IsVisible.Should().BeTrue();
    }

    /// <summary>
    /// Verifies managed automatic mode retains the user's custom value between mode switches.
    /// </summary>
    [Fact]
    public void Managed_editor_preserves_custom_value_behind_automatic_value()
    {
        var setting = CreateSetting("runtime_path", "path", "C:/custom");
        var draft = new ModuleSettingDraft(setting);
        draft.LoadRuntimeValue("C:/automatic", "C:/automatic", isReadOnly: false);
        var item = CreateItem(draft);

        item.TextValue.Should().Be("C:/automatic");
        item.UseCustomValue = true;
        item.TextValue.Should().Be("C:/custom");
        item.TextValue = "D:/override";
        item.UseCustomValue = false;
        item.TextValue.Should().Be("C:/automatic");
        item.UseCustomValue = true;

        item.TextValue.Should().Be("D:/override");
        draft.Value.Should().Be("D:/override");
    }

    /// <summary>
    /// Verifies manifest metadata selects the expected reusable XAML editor template.
    /// </summary>
    [Fact]
    public void Editor_kind_matches_setting_contract()
    {
        CreateItem(new ModuleSettingDraft(CreateSetting("flag", "bool", false))).EditorKind
            .Should().Be(ModuleSettingEditorKind.Boolean);
        CreateItem(new ModuleSettingDraft(CreateSetting("count", "int", 2))).EditorKind
            .Should().Be(ModuleSettingEditorKind.Numeric);
        CreateItem(new ModuleSettingDraft(CreateSetting("secret", "password", "value"))).EditorKind
            .Should().Be(ModuleSettingEditorKind.Password);

        var choice = CreateSetting("mode", "string", "one");
        choice.AllowedValues = ["one", "two"];
        CreateItem(new ModuleSettingDraft(choice)).EditorKind
            .Should().Be(ModuleSettingEditorKind.Choice);

        var engineDraft = new ModuleSettingDraft(CreateSetting("runtime", "engine", false));
        engineDraft.LoadRuntimeValue(true, automaticValue: null, isReadOnly: true);
        CreateItem(engineDraft).EditorKind.Should().Be(ModuleSettingEditorKind.EngineStatus);
        CreateItem(new ModuleSettingDraft(CreateSetting("runtime_path", "path", "path"))).EditorKind
            .Should().Be(ModuleSettingEditorKind.Managed);
    }

    /// <summary>
    /// Verifies hidden host settings never enter bindable sections while supported host values do.
    /// </summary>
    [Fact]
    public void Page_filters_hidden_host_types_before_xaml_rendering()
    {
        var module = CreateModule(
            CreateSetting("runtime_path", "path", "path"),
            CreateSetting("runtime_data", "data", "data"),
            CreateSetting("runtime_models", "models", "models"),
            CreateSetting("port", "port", 5000),
            CreateSetting("theme", "theme", "{}"),
            CreateSetting("locale", "locale", "{}"));

        var renderedKeys = CreatePage(new ModuleSettingsDraft(module))
            .Sections
            .SelectMany(static section => section.Settings)
            .Select(static item => item.Draft.Setting.Key);

        renderedKeys.Should().Equal("runtime_path", "runtime_data", "runtime_models");
    }

    /// <summary>
    /// Verifies category reactivation replaces presenter rows instead of accumulating duplicates.
    /// </summary>
    [Fact]
    public void Reload_replaces_sections_without_duplicate_rows()
    {
        var firstDraft = new ModuleSettingsDraft(CreateModule(CreateSetting("first", "string", "one")));
        var secondDraft = new ModuleSettingsDraft(CreateModule(CreateSetting("second", "string", "two")));
        var page = CreatePage(firstDraft);

        page.Load(secondDraft, "Other", "Custom", "Installed", "Missing");

        page.Sections.Should().ContainSingle();
        page.Sections.Single().Settings.Should().ContainSingle();
        page.Sections.Single().Settings.Single().Draft.Setting.Key.Should().Be("second");
    }

    /// <summary>
    /// Verifies refreshing one module updates existing rows instead of rebuilding the XAML presentation.
    /// </summary>
    [Fact]
    public void Same_module_refresh_updates_values_in_place()
    {
        var module = CreateModule(CreateSetting("message", "string", "saved"));
        var draft = new ModuleSettingsDraft(module);
        var page = CreatePage(draft);
        var section = page.Sections.Single();
        var item = section.Settings.Single();

        draft.GetSetting("message").Value = "edited";
        page.Load(draft, "Other", "Custom", "Installed", "Missing");

        page.Sections.Single().Should().BeSameAs(section);
        page.Sections.Single().Settings.Single().Should().BeSameAs(item);
        item.TextValue.Should().Be("edited");

        draft.DiscardChanges();
        page.Load(draft, "Other", "Custom", "Installed", "Missing");

        page.Sections.Single().Settings.Single().Should().BeSameAs(item);
        item.TextValue.Should().Be("saved");
    }

    /// <summary>
    /// Creates a normalized module containing the requested settings.
    /// </summary>
    private static ModuleConfig CreateModule(params ModuleSetting[] settings) =>
        ModuleConfigBuilder.Create(configure: module =>
        {
            module.Settings = settings.ToList();
            module.Normalize();
        });

    /// <summary>
    /// Creates one setting definition with identical default and persisted values.
    /// </summary>
    private static ModuleSetting CreateSetting(string key, string type, object? value) =>
        new()
        {
            Key = key,
            Name = key,
            Type = type,
            Default = value,
            Value = value
        };

    /// <summary>
    /// Creates and loads a page presenter with stable test labels.
    /// </summary>
    private static ModuleSettingsPageViewModel CreatePage(ModuleSettingsDraft draft)
    {
        var page = new ModuleSettingsPageViewModel(static () => { });
        page.Load(draft, "Other", "Custom", "Installed", "Missing");
        return page;
    }

    /// <summary>
    /// Creates one item presenter for focused editor contract checks.
    /// </summary>
    private static ModuleSettingItemViewModel CreateItem(ModuleSettingDraft draft) =>
        new(draft, "Custom", "Installed", "Missing", static () => { });
}
