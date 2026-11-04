// Copyright NEXTGGTECH. Apache License 2.0.

using System.Collections.ObjectModel;

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Exposes one selectable settings category to the declarative sidebar template.
    /// </summary>
    public sealed class SettingsCategorySelectorItemViewModel : SettingsBindableObject
    {
        // Fields

        /// <summary>
        /// Backing field of <see cref="IsActive"/>.
        /// </summary>
        private bool _isActive;


        // Initialization

        /// <summary>
        /// Creates one selector item and its page-owned selection command.
        /// </summary>
        /// <param name="category">Category represented by the item.</param>
        /// <param name="title">Localized title shown on the item.</param>
        /// <param name="select">Callback that opens the category when the item is chosen.</param>
        public SettingsCategorySelectorItemViewModel(
            SettingsCategory category,
            string title,
            Action<SettingsCategory> select)
        {
            Category = category;
            Title = title;
            SelectCommand = new Command(() => select(Category));
        }


        // Properties and commands

        /// <summary>
        /// Category that this item opens.
        /// </summary>
        public SettingsCategory Category { get; }

        /// <summary>
        /// Localized category title shown in the sidebar.
        /// </summary>
        public string Title { get; }

        /// <summary>
        /// Command that opens <see cref="Category"/> when the user taps the item.
        /// </summary>
        public Command SelectCommand { get; }

        /// <summary>
        /// Whether this item is the currently opened category, so the sidebar highlights it.
        /// </summary>
        public bool IsActive
        {
            get => _isActive;
            private set => SetProperty(ref _isActive, value);
        }


        // State changes

        /// <summary>
        /// Updates the active state consumed by XAML data triggers.
        /// </summary>
        public void SetActive(bool isActive)
        {
            IsActive = isActive;
        }
    }

    /// <summary>
    /// Owns the built-in and module category collections rendered by the settings sidebar.
    /// </summary>
    public sealed class SettingsCategorySidebarViewModel : SettingsBindableObject
    {
        // Fields

        /// <summary>
        /// Callback into the settings navigation workflow that opens a chosen category.
        /// </summary>
        private readonly Action<SettingsCategory> _select;

        /// <summary>
        /// Backing field of <see cref="HasModuleCategories"/>.
        /// </summary>
        private bool _hasModuleCategories;

        /// <summary>
        /// Backing field of <see cref="AslmHeader"/>.
        /// </summary>
        private string _aslmHeader = string.Empty;

        /// <summary>
        /// Backing field of <see cref="ModulesHeader"/>.
        /// </summary>
        private string _modulesHeader = string.Empty;


        // Initialization

        /// <summary>
        /// Creates the sidebar model with a callback into the settings navigation workflow.
        /// </summary>
        /// <param name="select">Callback that opens the category chosen by the user.</param>
        public SettingsCategorySidebarViewModel(Action<SettingsCategory> select)
        {
            _select = select;
        }


        // Bindable state

        /// <summary>
        /// Items of the built-in ASLM group shown at the top of the sidebar.
        /// </summary>
        public ObservableCollection<SettingsCategorySelectorItemViewModel> AslmCategories { get; } = new();

        /// <summary>
        /// Items of the module group shown below the built-in group.
        /// </summary>
        public ObservableCollection<SettingsCategorySelectorItemViewModel> ModuleCategories { get; } = new();

        /// <summary>
        /// Whether at least one module category exists, so the sidebar shows the module group and its header.
        /// </summary>
        public bool HasModuleCategories
        {
            get => _hasModuleCategories;
            private set => SetProperty(ref _hasModuleCategories, value);
        }

        /// <summary>
        /// Localized header of the built-in ASLM group.
        /// </summary>
        public string AslmHeader
        {
            get => _aslmHeader;
            private set => SetProperty(ref _aslmHeader, value);
        }

        /// <summary>
        /// Localized header of the module group.
        /// </summary>
        public string ModulesHeader
        {
            get => _modulesHeader;
            private set => SetProperty(ref _modulesHeader, value);
        }


        // Loading and selection

        /// <summary>
        /// Rebuilds category data while leaving all visual construction to XAML templates.
        /// </summary>
        /// <param name="categories">Categories to show, split into the two groups by their kind.</param>
        /// <param name="getTitle">Resolves the localized title of one category.</param>
        /// <param name="aslmHeader">Localized header of the built-in group.</param>
        /// <param name="modulesHeader">Localized header of the module group.</param>
        /// <param name="activeCategoryId">Id of the category to highlight, or null when none is active.</param>
        public void Load(
            IEnumerable<SettingsCategory> categories,
            Func<SettingsCategory, string> getTitle,
            string aslmHeader,
            string modulesHeader,
            string? activeCategoryId)
        {
            AslmHeader = aslmHeader;
            ModulesHeader = modulesHeader;
            AslmCategories.Clear();
            ModuleCategories.Clear();

            // Sort each category into its group's collection and mark the active one.
            foreach (var category in categories)
            {
                var item = new SettingsCategorySelectorItemViewModel(category, getTitle(category), _select);
                item.SetActive(string.Equals(category.Id, activeCategoryId, StringComparison.OrdinalIgnoreCase));
                var target = SettingsPresentationBuilder.GetCategoryGroup(category) == SettingsCategoryGroup.Modules
                    ? ModuleCategories
                    : AslmCategories;
                target.Add(item);
            }

            HasModuleCategories = ModuleCategories.Count > 0;
        }

        /// <summary>
        /// Updates selection data without recreating sidebar controls.
        /// </summary>
        public void SetActive(string? categoryId)
        {
            foreach (var item in AslmCategories.Concat(ModuleCategories))
            {
                item.SetActive(string.Equals(item.Category.Id, categoryId, StringComparison.OrdinalIgnoreCase));
            }
        }
    }
}
