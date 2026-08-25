// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Models;
using ASLM.Tests.TestSupport;

namespace ASLM.Tests.Services;

public sealed class CustomThemesStoreTests
{
    /// <summary>
    /// Verifies renaming keeps the selected theme id and resolves display-name collisions.
    /// </summary>
    [Fact]
    public void RenameTheme_allocates_unique_name_without_replacing_theme()
    {
        var store = new CustomThemesStore(TestLoggerFactory.Create<CustomThemesStore>());
        var first = store.CreateTheme("Ocean", "dark");
        var second = store.CreateTheme("Forest", "light");

        var renamed = store.RenameTheme(second.Id, first.Name);

        renamed.Should().BeSameAs(second);
        renamed!.Id.Should().Be(second.Id);
        renamed.Name.Should().Be("Ocean #2");
    }

    [Fact]
    public void AllocateUniqueDisplayName_appends_suffix_on_collision()
    {
        _ = new AslmFileSystemLayout();
        var store = new CustomThemesStore(TestLoggerFactory.Create<CustomThemesStore>());
        store.Root.Themes.Add(new CustomTheme { Id = "a", Name = "Ocean", BaseAppearance = "dark", Colors = [] });

        store.AllocateUniqueDisplayName("Ocean").Should().Be("Ocean #2");
    }

    [Fact]
    public async Task SaveAsync_and_LoadAsync_round_trip_theme()
    {
        _ = new AslmFileSystemLayout();
        var store = new CustomThemesStore(TestLoggerFactory.Create<CustomThemesStore>());
        var theme = store.CreateTheme("Sunset", "light");
        theme.Colors["SystemBlue"] = "#112233";
        await store.SaveAsync();

        var reloaded = new CustomThemesStore(TestLoggerFactory.Create<CustomThemesStore>());
        await reloaded.LoadAsync();

        reloaded.FindById(theme.Id).Should().NotBeNull();
        reloaded.FindById(theme.Id)!.Colors["SystemBlue"].Should().Be("#112233");
    }

    [Fact]
    public void ImportThemeCopy_assigns_new_id_and_unique_name()
    {
        _ = new AslmFileSystemLayout();
        var store = new CustomThemesStore(TestLoggerFactory.Create<CustomThemesStore>());
        var source = store.CreateTheme("Forest", "dark");

        var copy = store.ImportThemeCopy(source);

        copy.Id.Should().NotBe(source.Id);
        copy.Name.Should().Be("Forest #2");
    }

    [Fact]
    public void DeserializeImportedTheme_parses_single_theme_json()
    {
        _ = new AslmFileSystemLayout();
        var store = new CustomThemesStore(TestLoggerFactory.Create<CustomThemesStore>());
        var exported = store.SerializeThemeForExport(store.CreateTheme("Export", "dark"));

        var imported = store.DeserializeImportedTheme(exported);

        imported.Should().NotBeNull();
        imported!.Name.Should().Be("Export");
    }
}
