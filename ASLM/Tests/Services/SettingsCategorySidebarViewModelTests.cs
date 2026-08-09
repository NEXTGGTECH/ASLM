// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Models;

namespace ASLM.Tests.Services;

/// <summary>
/// Verifies declarative sidebar grouping, selection, and command behavior.
/// </summary>
public sealed class SettingsCategorySidebarViewModelTests
{
    /// <summary>
    /// Verifies built-in and module categories are exposed through separate bindable collections.
    /// </summary>
    [Fact]
    public void Load_groups_categories_and_marks_initial_selection()
    {
        var categories = CreateCategories();
        var sidebar = new SettingsCategorySidebarViewModel(static _ => { });

        sidebar.Load(categories, category => $"Title:{category.Id}", "ASLM", "Modules", "module::demo");

        sidebar.AslmCategories.Select(static item => item.Category.Id)
            .Should().Equal("aslm", "aslm-updates");
        sidebar.ModuleCategories.Select(static item => item.Category.Id)
            .Should().Equal("module::demo");
        sidebar.ModuleCategories.Single().IsActive.Should().BeTrue();
        sidebar.HasModuleCategories.Should().BeTrue();
        sidebar.AslmHeader.Should().Be("ASLM");
        sidebar.ModulesHeader.Should().Be("Modules");
    }

    /// <summary>
    /// Verifies selection changes update existing item instances rather than rebuilding the sidebar.
    /// </summary>
    [Fact]
    public void SetActive_updates_existing_items_in_place()
    {
        var sidebar = new SettingsCategorySidebarViewModel(static _ => { });
        sidebar.Load(CreateCategories(), static category => category.Title, "ASLM", "Modules", "aslm");
        var first = sidebar.AslmCategories[0];
        var second = sidebar.AslmCategories[1];

        sidebar.SetActive(second.Category.Id);

        sidebar.AslmCategories[0].Should().BeSameAs(first);
        sidebar.AslmCategories[1].Should().BeSameAs(second);
        first.IsActive.Should().BeFalse();
        second.IsActive.Should().BeTrue();
    }

    /// <summary>
    /// Verifies the XAML-bound command forwards the selected category to page navigation.
    /// </summary>
    [Fact]
    public void Selector_command_forwards_selected_category()
    {
        SettingsCategory? selected = null;
        var sidebar = new SettingsCategorySidebarViewModel(category => selected = category);
        sidebar.Load(CreateCategories(), static category => category.Title, "ASLM", "Modules", null);
        var expected = sidebar.ModuleCategories.Single().Category;

        sidebar.ModuleCategories.Single().SelectCommand.Execute(null);

        selected.Should().BeSameAs(expected);
    }

    /// <summary>
    /// Verifies repeated discovery replaces stale selectors and hides an empty module group.
    /// </summary>
    [Fact]
    public void Reload_replaces_collections_without_duplicate_or_stale_modules()
    {
        var sidebar = new SettingsCategorySidebarViewModel(static _ => { });
        sidebar.Load(CreateCategories(), static category => category.Title, "ASLM", "Modules", "module::demo");
        var builtInOnly = CreateCategories()
            .Where(static category => category.Kind != SettingsCategoryKind.Module)
            .ToList();

        sidebar.Load(builtInOnly, static category => category.Title, "Application", "Extensions", "aslm-updates");

        sidebar.AslmCategories.Should().HaveCount(2);
        sidebar.ModuleCategories.Should().BeEmpty();
        sidebar.HasModuleCategories.Should().BeFalse();
        sidebar.AslmCategories.Single(item => item.Category.Id == "aslm-updates").IsActive.Should().BeTrue();
        sidebar.AslmHeader.Should().Be("Application");
        sidebar.ModulesHeader.Should().Be("Extensions");
    }

    /// <summary>
    /// Creates a deterministic category set spanning both sidebar groups.
    /// </summary>
    private static IReadOnlyList<SettingsCategory> CreateCategories() =>
    [
        new("aslm", "ASLM", string.Empty, SettingsCategoryKind.Aslm, null, true),
        new("aslm-updates", "Updates", string.Empty, SettingsCategoryKind.Updates, null, true),
        new(
            "module::demo",
            "Demo",
            string.Empty,
            SettingsCategoryKind.Module,
            new ModuleConfig { Id = "demo", Name = "Demo" },
            false)
    ];
}
