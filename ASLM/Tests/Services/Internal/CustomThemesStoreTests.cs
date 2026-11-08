// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Models;
using ASLM.Tests.TestSupport;

namespace ASLM.Tests.Services.Internal
{
    /// <summary>
    /// Verifies creating, renaming, naming, persisting, importing and exporting of user-defined themes in
    /// <see cref="CustomThemesStore"/>.
    /// </summary>
    public sealed class CustomThemesStoreTests
    {
        // Creation

        /// <summary>
        /// Verifies saving a detached theme preserves its values and assigns an ID only to the stored copy.
        /// </summary>
        [Fact]
        public async Task Create_complete_theme_saves_all_values_and_allocates_a_unique_id()
        {
            using var layout = new AslmFileSystemLayout();
            var store = new CustomThemesStore(TestLoggerFactory.Create<CustomThemesStore>());
            var draft = new CustomTheme { Name = "Ocean", BaseAppearance = "light", Colors = new() { ["SystemBlue"] = "#123456" } };
            var saved = await store.CreateThemeAsync(draft);
            saved.Id.Should().NotBeNullOrWhiteSpace();
            draft.Id.Should().BeEmpty();

            // Reload from disk instead of checking only the store's in-memory registry.
            var reloaded = new CustomThemesStore(TestLoggerFactory.Create<CustomThemesStore>());
            await reloaded.LoadAsync();
            reloaded.FindById(saved.Id)!.Colors["SystemBlue"].Should().Be("#123456");
        }

        /// <summary>
        /// Verifies a failed atomic save preserves existing themes and removes the incomplete registry entry.
        /// </summary>
        [Fact]
        public async Task Failed_create_preserves_existing_file_and_rolls_back_the_registry_entry()
        {
            using var layout = new AslmFileSystemLayout();
            var store = new CustomThemesStore(TestLoggerFactory.Create<CustomThemesStore>());
            await store.CreateThemeAsync(new CustomTheme { Name = "Existing" });
            var original = await File.ReadAllTextAsync(layout.CustomThemesFilePath);

            // Force replacement to fail without modifying permissions or deleting the original file.
            using (var locked = new FileStream(layout.CustomThemesFilePath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var attempt = () => store.CreateThemeAsync(new CustomTheme { Name = "New theme" });
                var error = (await attempt.Should().ThrowAsync<Exception>()).Which;
                (error is IOException or UnauthorizedAccessException).Should().BeTrue();
            }

            store.Root.Themes.Should().ContainSingle(t => t.Name == "Existing");
            (await File.ReadAllTextAsync(layout.CustomThemesFilePath)).Should().Be(original);
            Directory.GetFiles(layout.DataAppDir, "*.tmp").Should().BeEmpty();
        }


        // Unique names

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

        /// <summary>
        /// Verifies that a desired display name already used by another theme receives the numeric suffix " #2".
        /// </summary>
        [Fact]
        public void AllocateUniqueDisplayName_appends_suffix_on_collision()
        {
            _ = new AslmFileSystemLayout();
            var store = new CustomThemesStore(TestLoggerFactory.Create<CustomThemesStore>());
            store.Root.Themes.Add(new CustomTheme { Id = "a", Name = "Ocean", BaseAppearance = "dark", Colors = [] });

            store.AllocateUniqueDisplayName("Ocean").Should().Be("Ocean #2");
        }


        // Persistence

        /// <summary>
        /// Verifies that a created theme and its color values survive saving and loading with a new store.
        /// </summary>
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


        // Import and export

        /// <summary>
        /// Verifies that importing a theme adds a copy with a new id and a display name that does not collide with
        /// the source theme.
        /// </summary>
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

        /// <summary>
        /// Verifies that the JSON produced by the export serialization can be parsed back into a theme with the same
        /// name.
        /// </summary>
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
}
