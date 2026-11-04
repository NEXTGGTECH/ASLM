// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Models;

namespace ASLM.Services.Internal
{
    // Sidebar categories

    /// <summary>
    /// Distinguishes the supported top-level settings groups.
    /// </summary>
    public enum SettingsCategoryGroup
    {
        /// <summary>
        /// Built-in ASLM categories listed first in the sidebar.
        /// </summary>
        Aslm,

        /// <summary>
        /// Per-module categories listed below the built-in ones.
        /// </summary>
        Modules
    }

    /// <summary>
    /// Distinguishes the supported settings category types in the selector.
    /// </summary>
    public enum SettingsCategoryKind
    {
        /// <summary>
        /// General ASLM application settings.
        /// </summary>
        Aslm,

        /// <summary>
        /// Account and sign-in settings.
        /// </summary>
        Accounts,

        /// <summary>
        /// Application and component update settings.
        /// </summary>
        Updates,

        /// <summary>
        /// Settings declared by one installed module.
        /// </summary>
        Module,

        /// <summary>
        /// Appearance settings such as themes.
        /// </summary>
        Personalization
    }

    /// <summary>
    /// Describes one selectable settings category shown in the sidebar.
    /// </summary>
    /// <param name="Id">Stable category identifier; module categories use <c>module::&lt;module id&gt;</c>.</param>
    /// <param name="Title">Default display title; the sidebar may substitute a localized title.</param>
    /// <param name="Kind">Type of category, which decides the page that is shown.</param>
    /// <param name="Module">Module that owns the category, or null for built-in categories.</param>
    /// <param name="SupportsAppRestart">Whether the category offers an application restart to apply its changes.</param>
    public sealed record SettingsCategory(
        string Id,
        string Title,
        SettingsCategoryKind Kind,
        ModuleConfig? Module,
        bool SupportsAppRestart);


    // Module settings sections

    /// <summary>
    /// Identifies the layout role of one module settings presentation section.
    /// </summary>
    public enum ModuleSettingsSectionKind
    {
        /// <summary>
        /// Section that groups the settings of one category declared in the module manifest.
        /// </summary>
        ManifestCategory,

        /// <summary>
        /// Leading section with the settings that belong to no declared category.
        /// </summary>
        Uncategorized
    }

    /// <summary>
    /// Describes one render-ready module settings section independently from MAUI controls.
    /// </summary>
    /// <param name="Kind">Layout role of the section.</param>
    /// <param name="Title">Section heading, or null when the section has none.</param>
    /// <param name="Description">Explanatory text shown under the heading, or null when there is none.</param>
    /// <param name="Settings">Setting drafts rendered in the section, in display order.</param>
    public sealed record ModuleSettingsSectionPresentation(
        ModuleSettingsSectionKind Kind,
        string? Title,
        string? Description,
        IReadOnlyList<ModuleSettingDraft> Settings);


    // Presentation building

    /// <summary>
    /// Converts module drafts and manifest metadata into deterministic presentation sections.
    /// </summary>
    public static class SettingsPresentationBuilder
    {
        // Sidebar categories

        /// <summary>
        /// Returns the top-level group that owns one sidebar category.
        /// </summary>
        public static SettingsCategoryGroup GetCategoryGroup(SettingsCategory category) =>
            category.Kind == SettingsCategoryKind.Module
                ? SettingsCategoryGroup.Modules
                : SettingsCategoryGroup.Aslm;

        /// <summary>
        /// Builds built-in and eligible module categories in deterministic sidebar order.
        /// </summary>
        public static IReadOnlyList<SettingsCategory> BuildCategories(IReadOnlyList<ModuleConfig> loadedModules)
        {
            // Built-in categories always come first, in this fixed order.
            var categories = new List<SettingsCategory>
            {
                new(
                    "aslm",
                    "ASLM",
                    SettingsCategoryKind.Aslm,
                    null,
                    true),
                new(
                    "aslm-updates",
                    "Updates",
                    SettingsCategoryKind.Updates,
                    null,
                    true),
                new(
                    "aslm-accounts",
                    "Accounts",
                    SettingsCategoryKind.Accounts,
                    null,
                    false),
                new(
                    "aslm-personalization",
                    "Personalization",
                    SettingsCategoryKind.Personalization,
                    null,
                    false)
            };

            // Modules follow built-in categories, sorted by name and filtered by the settings contract.
            categories.AddRange(
                loadedModules
                    .Where(SettingsService.IsModuleEligibleForSettings)
                    .OrderBy(static module => module.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(static module => new SettingsCategory(
                        $"module::{module.Id}",
                        module.Name,
                        SettingsCategoryKind.Module,
                        module,
                        false)));

            return categories;
        }


        // Module sections

        /// <summary>
        /// Builds visible module sections in the same order declared by the manifest contract.
        /// Settings hidden by unmet dependencies are skipped unless
        /// <paramref name="includeDependencyHiddenSettings"/> is set.
        /// </summary>
        public static IReadOnlyList<ModuleSettingsSectionPresentation> BuildModuleSections(
            ModuleSettingsDraft moduleDraft,
            bool includeDependencyHiddenSettings = false)
        {
            // Refresh dependency visibility first, then keep only settings that may be shown at all.
            SettingsService.RefreshModuleDraftVisibility(moduleDraft);

            var visibleSettings = moduleDraft.Settings
                .Where(draft =>
                    SettingsService.ShouldDisplaySetting(draft.Setting) &&
                    (includeDependencyHiddenSettings || draft.IsVisible))
                .ToList();
            var sections = new List<ModuleSettingsSectionPresentation>();

            // Category ids declared in the manifest decide which settings count as categorized.
            var declaredCategoryIds = moduleDraft.Module.SettingCategories
                .Select(static category => category.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // All settings without a declared category share one unlabelled leading block.
            AddSection(
                sections,
                ModuleSettingsSectionKind.Uncategorized,
                null,
                null,
                visibleSettings.Where(draft =>
                    !SettingsService.IsSettingsMetadataEligible(draft.Setting) ||
                    string.IsNullOrWhiteSpace(draft.Setting.Category) ||
                    !declaredCategoryIds.Contains(draft.Setting.Category)));

            // Manifest categories remain stable and render strictly in declaration order.
            foreach (var category in moduleDraft.Module.SettingCategories)
            {
                AddSection(
                    sections,
                    ModuleSettingsSectionKind.ManifestCategory,
                    category.Name,
                    category.Description,
                    visibleSettings.Where(draft =>
                        SettingsService.IsSettingsMetadataEligible(draft.Setting) &&
                        string.Equals(
                            draft.Setting.Category,
                            category.Id,
                            StringComparison.OrdinalIgnoreCase)));
            }

            return sections;
        }

        /// <summary>
        /// Adds a non-empty materialized section so rendering never receives placeholder groups.
        /// </summary>
        private static void AddSection(
            ICollection<ModuleSettingsSectionPresentation> sections,
            ModuleSettingsSectionKind kind,
            string? title,
            string? description,
            IEnumerable<ModuleSettingDraft> settings)
        {
            var materializedSettings = settings.ToList();
            if (materializedSettings.Count == 0)
            {
                return;
            }

            // Blank titles and descriptions are normalized to null so the view can skip those elements.
            sections.Add(new ModuleSettingsSectionPresentation(
                kind,
                string.IsNullOrWhiteSpace(title) ? null : title,
                string.IsNullOrWhiteSpace(description) ? null : description,
                materializedSettings));
        }
    }
}
