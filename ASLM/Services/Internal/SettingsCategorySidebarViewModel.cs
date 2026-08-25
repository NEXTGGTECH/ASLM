// Copyright NEXTGGTECH. Apache License 2.0.

using System.Collections.ObjectModel;

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Exposes one selectable settings category to the declarative sidebar template.
    /// </summary>
    public sealed class SettingsCategorySelectorItemViewModel : SettingsBindableObject
    {
        private bool _isActive;

        /// <summary>
        /// Creates one selector item and its page-owned selection command.
        /// </summary>
        public SettingsCategorySelectorItemViewModel(
            SettingsCategory category,
            string title,
            Action<SettingsCategory> select)
        {
            Category = category;
            Title = title;
            SelectCommand = new Command(() => select(Category));
        }

        public SettingsCategory Category { get; }
        public string Title { get; }
        public Command SelectCommand { get; }

        public bool IsActive
        {
            get => _isActive;
            private set => SetProperty(ref _isActive, value);
        }

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
        private readonly Action<SettingsCategory> _select;
        private bool _hasModuleCategories;
        private string _aslmHeader = string.Empty;
        private string _modulesHeader = string.Empty;

        /// <summary>
        /// Creates the sidebar model with a callback into the settings navigation workflow.
        /// </summary>
        public SettingsCategorySidebarViewModel(Action<SettingsCategory> select)
        {
            _select = select;
        }

        public ObservableCollection<SettingsCategorySelectorItemViewModel> AslmCategories { get; } = new();
        public ObservableCollection<SettingsCategorySelectorItemViewModel> ModuleCategories { get; } = new();

        public bool HasModuleCategories
        {
            get => _hasModuleCategories;
            private set => SetProperty(ref _hasModuleCategories, value);
        }

        public string AslmHeader
        {
            get => _aslmHeader;
            private set => SetProperty(ref _aslmHeader, value);
        }

        public string ModulesHeader
        {
            get => _modulesHeader;
            private set => SetProperty(ref _modulesHeader, value);
        }

        /// <summary>
        /// Rebuilds category data while leaving all visual construction to XAML templates.
        /// </summary>
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
