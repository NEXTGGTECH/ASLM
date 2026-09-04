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

        page.Load(secondDraft, "Installed", "Missing");

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
        page.Load(draft, "Installed", "Missing");

        page.Sections.Single().Should().BeSameAs(section);
        page.Sections.Single().Settings.Single().Should().BeSameAs(item);
        item.TextValue.Should().Be("edited");

        draft.DiscardChanges();
        page.Load(draft, "Installed", "Missing");

        page.Sections.Single().Settings.Single().Should().BeSameAs(item);
        item.TextValue.Should().Be("saved");
    }

    /// <summary>
    /// Verifies navigation uses the module name for the default group and preserves manifest order.
    /// </summary>
    [Fact]
    public void Navigation_titles_default_to_module_name_and_follow_manifest_order()
    {
        var module = ModuleConfigBuilder.Create(configure: config =>
        {
            config.Name = "Demo Module";
            config.SettingCategories =
            [
                new ModuleSettingCategory { Id = "general", Name = "General" },
                new ModuleSettingCategory { Id = "advanced", Name = "Advanced" }
            ];

            var uncategorized = CreateSetting("plain", "string", "value");
            var general = CreateSetting("general", "string", "value");
            general.Category = "general";
            var advanced = CreateSetting("advanced", "string", "value");
            advanced.Category = "advanced";
            config.Settings = [uncategorized, general, advanced];
            config.Normalize();
        });

        var page = CreatePage(new ModuleSettingsDraft(module));

        page.Sections.Select(static section => section.NavigationTitle)
            .Should().Equal("Demo Module", "General", "Advanced");
        page.Sections.First().Title.Should().BeNull();
        page.Sections.First().IsActive.Should().BeTrue();
        page.HasSectionNavigation.Should().BeTrue();
    }

    /// <summary>
    /// Verifies a page with one populated section does not reserve navigation space.
    /// </summary>
    [Fact]
    public void Single_visible_section_hides_navigation()
    {
        var module = ModuleConfigBuilder.Create(configure: config =>
        {
            config.SettingCategories =
            [
                new ModuleSettingCategory { Id = "general", Name = "General" }
            ];

            var first = CreateSetting("first", "string", "one");
            first.Category = "general";
            var second = CreateSetting("second", "string", "two");
            second.Category = "general";
            config.Settings = [first, second];
            config.Normalize();
        });
        var page = CreatePage(new ModuleSettingsDraft(module));

        page.Sections.Should().ContainSingle();
        page.HasSectionNavigation.Should().BeFalse();
    }

    /// <summary>
    /// Verifies settings without declared categories remain one unlabelled section without navigation.
    /// </summary>
    [Fact]
    public void Module_without_categories_hides_navigation()
    {
        var page = CreatePage(new ModuleSettingsDraft(CreateModule(
            CreateSetting("first", "string", "one"),
            CreateSetting("second", "string", "two"))));

        page.Sections.Should().ContainSingle();
        page.Sections.Single().Title.Should().BeNull();
        page.HasSectionNavigation.Should().BeFalse();
    }

    /// <summary>
    /// Verifies dependency changes update navigation without rebuilding section presenters.
    /// </summary>
    [Fact]
    public void Dependency_visibility_recomputes_section_navigation()
    {
        var module = ModuleConfigBuilder.Create(configure: config =>
        {
            config.SettingCategories =
            [
                new ModuleSettingCategory { Id = "main", Name = "Main" },
                new ModuleSettingCategory { Id = "details", Name = "Details" }
            ];

            var controller = CreateSetting("enabled", "bool", false);
            controller.Category = "main";
            var dependent = CreateSetting("value", "string", "text");
            dependent.Category = "details";
            dependent.DependsOn = controller.Key;
            config.Settings = [controller, dependent];
            config.Normalize();
        });
        var page = CreatePage(new ModuleSettingsDraft(module));
        var sections = page.Sections.ToList();
        var controllerItem = sections[0].Settings.Single();

        sections[1].IsVisible.Should().BeFalse();
        page.HasSectionNavigation.Should().BeFalse();

        controllerItem.BooleanValue = true;

        page.Sections.Should().Equal(sections);
        sections[1].IsVisible.Should().BeTrue();
        page.HasSectionNavigation.Should().BeTrue();
    }

    /// <summary>
    /// Verifies a navigation command selects and forwards its existing section instance.
    /// </summary>
    [Fact]
    public void Navigation_command_selects_and_forwards_section()
    {
        var first = CreateSetting("first", "string", "one");
        var second = CreateSetting("second", "string", "two");
        second.Category = "second";
        var module = ModuleConfigBuilder.Create(configure: config =>
        {
            config.Name = "Demo";
            config.SettingCategories =
            [
                new ModuleSettingCategory { Id = "second", Name = "Second" }
            ];
            config.Settings = [first, second];
            config.Normalize();
        });
        ModuleSettingsSectionViewModel? selected = null;
        var page = new ModuleSettingsPageViewModel(static () => { }, section => selected = section);
        page.Load(new ModuleSettingsDraft(module), "Installed", "Missing");
        var expected = page.Sections[1];

        expected.SelectCommand.Execute(null);

        selected.Should().BeSameAs(expected);
        expected.IsActive.Should().BeTrue();
        page.Sections[0].IsActive.Should().BeFalse();
    }

    /// <summary>
    /// Verifies incremental materialization completes with every displayable row in manifest order.
    /// </summary>
    [Fact]
    public async Task Incremental_load_materializes_all_rows_and_completes()
    {
        var draft = new ModuleSettingsDraft(CreateModule(
            CreateSetting("first", "string", "one"),
            CreateSetting("second", "bool", true),
            CreateSetting("third", "path", "C:/runtime")));
        var page = new ModuleSettingsPageViewModel(static () => { });

        await page.LoadIncrementallyAsync(draft, "Installed", "Missing", CancellationToken.None);

        page.IsLoading.Should().BeFalse();
        page.IsFullyLoaded.Should().BeTrue();
        page.Sections.SelectMany(static section => section.Settings)
            .Select(static item => item.Draft.Setting.Key)
            .Should().Equal("first", "second", "third");
    }

    /// <summary>
    /// Verifies closing during materialization leaves a page restartable instead of caching a partial tree.
    /// </summary>
    [Fact]
    public async Task Canceled_incremental_load_can_restart_cleanly()
    {
        var draft = new ModuleSettingsDraft(CreateModule(
            CreateSetting("first", "string", "one"),
            CreateSetting("second", "string", "two")));
        var page = new ModuleSettingsPageViewModel(static () => { });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Func<Task> canceledLoad = () => page.LoadIncrementallyAsync(
            draft,
            "Installed",
            "Missing",
            cancellation.Token);

        await canceledLoad.Should().ThrowAsync<OperationCanceledException>();
        page.IsLoading.Should().BeFalse();
        page.IsFullyLoaded.Should().BeFalse();

        await page.LoadIncrementallyAsync(draft, "Installed", "Missing", CancellationToken.None);

        page.IsFullyLoaded.Should().BeTrue();
        page.Sections.SelectMany(static section => section.Settings)
            .Select(static item => item.Draft.Setting.Key)
            .Should().Equal("first", "second");
    }

    /// <summary>
    /// Verifies one completed getter updates only its existing editor while later getters remain pending.
    /// </summary>
    [Fact]
    public void Runtime_setting_refresh_does_not_wait_for_or_refresh_other_rows()
    {
        var draft = new ModuleSettingsDraft(CreateModule(
            CreateSetting("first", "string", "one"),
            CreateSetting("second", "string", "two")));
        var page = CreatePage(draft);
        var items = page.Sections.SelectMany(static section => section.Settings).ToList();
        draft.GetSetting("first").Value = "loaded-one";
        draft.GetSetting("second").Value = "loaded-two";

        page.RefreshSettingFromDraft("first", refreshDependencies: false);

        items[0].TextValue.Should().Be("loaded-one");
        items[1].TextValue.Should().Be("two");
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
        page.Load(draft, "Installed", "Missing");
        return page;
    }

    /// <summary>
    /// Creates one item presenter for focused editor contract checks.
    /// </summary>
    private static ModuleSettingItemViewModel CreateItem(ModuleSettingDraft draft) =>
        new(draft, "Installed", "Missing", static () => { });
}
