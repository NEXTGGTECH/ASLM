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
        /// Captures the row at the viewport top so preceding schema edits do not move the reading position.
        /// </summary>
        internal ModuleSettingsScrollAnchor CaptureScrollAnchor(double scrollY)
        {
            var anchor = new ModuleSettingsScrollAnchor(null, 0, scrollY);
            foreach (var row in GetSettingRows())
            {
                var top = GetRowOffset(row);
                if (top > scrollY) break;
                if (row.BindingContext is ModuleSettingItemViewModel item)
                    anchor = new(item.Draft.Setting.Key, scrollY - top, scrollY);
            }
            return anchor;
        }

        /// <summary>
        /// Resolves the same setting in the replacement tree, falling back to the former absolute offset if removed.
        /// </summary>
        internal double ResolveScrollOffset(ModuleSettingsScrollAnchor anchor)
        {
            foreach (var row in GetSettingRows())
                if (row.BindingContext is ModuleSettingItemViewModel item &&
                    string.Equals(item.Draft.Setting.Key, anchor.SettingKey, StringComparison.OrdinalIgnoreCase))
                    return Math.Max(0, GetRowOffset(row) + anchor.RowOffset);
            return anchor.AbsoluteOffset;
        }

        /// <summary>
        /// Enumerates only the visible editors in the unchanged section hierarchy.
        /// </summary>
        private IEnumerable<View> GetSettingRows()
        {
            foreach (var (_, card) in GetVisibleSectionViews())
                if (card.SectionContent is Layout rows)
                    foreach (var row in rows.Children.OfType<View>().Where(static row => row.IsVisible)) yield return row;
        }

        /// <summary>
        /// Resolves a row's existing layout position without forcing synchronous measurement.
        /// </summary>
        private double GetRowOffset(View row)
        {
            var offset = 0d;
            Element? element = row;
            while (element != null && !ReferenceEquals(element, this))
            {
                if (element is VisualElement visual) offset += visual.Y;
                element = element.Parent;
            }
            return offset;
        }

        /// <summary>
        /// Finds the rendered card that belongs to one stable section view model.
        /// </summary>
        /// <param name="section">Section view model whose card is searched by reference.</param>
        /// <returns>The matching card, or null when the section is not rendered.</returns>
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

    /// <summary>
    /// Retains a setting's position inside the viewport across replacement of its module's settings schema.
    /// </summary>
    /// <param name="SettingKey">Stable manifest key of the row nearest the viewport top.</param>
    /// <param name="RowOffset">Scrolled distance past the row's top.</param>
    /// <param name="AbsoluteOffset">Fallback offset when the row no longer exists.</param>
    internal sealed record ModuleSettingsScrollAnchor(string? SettingKey, double RowOffset, double AbsoluteOffset);

}
