// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Models;

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Distinguishes the supported top-level settings groups.
    /// </summary>
    public enum SettingsCategoryGroup
    {
        Aslm,
        Modules
    }

    /// <summary>
    /// Distinguishes the supported settings category types in the selector.
    /// </summary>
    public enum SettingsCategoryKind
    {
        Aslm,
        Accounts,
        Updates,
        Module,
        Personalization
    }

    /// <summary>
    /// Describes one selectable settings category shown in the sidebar.
    /// </summary>
    public sealed record SettingsCategory(
        string Id,
        string Title,
        string Description,
        SettingsCategoryKind Kind,
        ModuleConfig? Module,
        bool SupportsAppRestart);

    /// <summary>
    /// Identifies the layout role of one module settings presentation section.
    /// </summary>
    public enum ModuleSettingsSectionKind
    {
        HostManaged,
        ManifestCategory,
        Uncategorized
    }

    /// <summary>
    /// Describes one render-ready module settings section independently from MAUI controls.
    /// </summary>
    public sealed record ModuleSettingsSectionPresentation(
        ModuleSettingsSectionKind Kind,
        string? Title,
        string? Description,
        IReadOnlyList<ModuleSettingDraft> Settings);

    /// <summary>
    /// Converts module drafts and manifest metadata into deterministic presentation sections.
    /// </summary>
    public static class SettingsPresentationBuilder
    {
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
            var categories = new List<SettingsCategory>
            {
                new(
                    "aslm",
                    "ASLM",
                    "Core ASLM behavior, ports, API, and consoles.",
                    SettingsCategoryKind.Aslm,
                    null,
                    true),
                new(
                    "aslm-updates",
                    "Updates",
                    "Application and module update preferences.",
                    SettingsCategoryKind.Updates,
                    null,
                    true),
                new(
                    "aslm-accounts",
                    "Accounts",
                    "ASLM display name, GitHub and Ollama sign-in.",
                    SettingsCategoryKind.Accounts,
                    null,
                    false),
                new(
                    "aslm-personalization",
                    "Personalization",
                    "Theme mode, language, and custom theme settings.",
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
                        string.IsNullOrWhiteSpace(module.Description)
                            ? "Module-specific configuration."
                            : module.Description.Trim(),
                        SettingsCategoryKind.Module,
                        module,
                        false)));

            return categories;
        }

        /// <summary>
        /// Builds visible module sections in the same order declared by the manifest contract.
        /// </summary>
        public static IReadOnlyList<ModuleSettingsSectionPresentation> BuildModuleSections(
            ModuleSettingsDraft moduleDraft,
            bool includeDependencyHiddenSettings = false)
        {
            SettingsService.RefreshModuleDraftVisibility(moduleDraft);

            var visibleSettings = moduleDraft.Settings
                .Where(draft =>
                    SettingsService.ShouldDisplaySetting(draft.Setting) &&
                    (includeDependencyHiddenSettings || draft.IsVisible))
                .ToList();
            var sections = new List<ModuleSettingsSectionPresentation>();

            // Host-managed settings retain their legacy leading block and ignore category metadata.
            AddSection(
                sections,
                ModuleSettingsSectionKind.HostManaged,
                null,
                null,
                visibleSettings.Where(static draft =>
                    !SettingsService.IsSettingsMetadataEligible(draft.Setting)));

            // Manifest categories remain stable and render strictly in declaration order.
            var declaredCategoryIds = moduleDraft.Module.SettingCategories
                .Select(static category => category.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
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

            // Missing or unknown category ids intentionally fall back to one final unlabelled block.
            AddSection(
                sections,
                ModuleSettingsSectionKind.Uncategorized,
                null,
                null,
                visibleSettings.Where(draft =>
                    SettingsService.IsSettingsMetadataEligible(draft.Setting) &&
                    (string.IsNullOrWhiteSpace(draft.Setting.Category) ||
                     !declaredCategoryIds.Contains(draft.Setting.Category))));

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

            sections.Add(new ModuleSettingsSectionPresentation(
                kind,
                string.IsNullOrWhiteSpace(title) ? null : title,
                string.IsNullOrWhiteSpace(description) ? null : description,
                materializedSettings));
        }
    }
}
