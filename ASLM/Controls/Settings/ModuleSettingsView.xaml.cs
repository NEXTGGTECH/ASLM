// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Services.Internal;

namespace ASLM.Controls.Settings
{
    /// <summary>
    /// Renders module setting sections through XAML bindable layouts.
    /// </summary>
    public partial class ModuleSettingsView : ContentView
    {
        /// <summary>
        /// Creates the module settings templates declared in XAML.
        /// </summary>
        public ModuleSettingsView()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Finds the rendered card that belongs to one stable section view model.
        /// </summary>
        public SettingsSectionView? FindSectionView(ModuleSettingsSectionViewModel section)
        {
            foreach (var child in SectionsLayout.Children)
            {
                if (child is SettingsSectionView sectionView &&
                    ReferenceEquals(sectionView.BindingContext, section))
                {
                    return sectionView;
                }
            }

            return null;
        }

        /// <summary>
        /// Enumerates visible section cards in their rendered order for scroll-position tracking.
        /// </summary>
        public IEnumerable<(ModuleSettingsSectionViewModel Section, SettingsSectionView View)> GetVisibleSectionViews()
        {
            foreach (var child in SectionsLayout.Children)
            {
                if (child is SettingsSectionView
                    {
                        IsVisible: true,
                        BindingContext: ModuleSettingsSectionViewModel section
                    } sectionView)
                {
                    yield return (section, sectionView);
                }
            }
        }
    }
}
