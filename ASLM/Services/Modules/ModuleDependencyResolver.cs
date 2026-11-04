// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Models;
using ASLM.Localization;

namespace ASLM.Services.Modules
{
    /// <summary>
    /// Resolves declared module-to-module dependencies from manifests.
    /// </summary>
    public static class ModuleDependencyResolver
    {
        // Install order

        /// <summary>
        /// Returns the selected modules plus transitive module dependencies in install order
        /// (dependencies before dependents).
        /// </summary>
        /// <param name="selectedModules">Modules the user chose to install.</param>
        /// <param name="catalog">All known modules; dependency ids are looked up here, the first entry of an id wins.</param>
        /// <exception cref="InvalidOperationException">Thrown with a localized message on a dependency cycle or a dependency missing from the catalog.</exception>
        public static IReadOnlyList<ModuleConfig> ExpandInstallOrder(
            IEnumerable<ModuleConfig> selectedModules,
            IReadOnlyList<ModuleConfig> catalog)
        {
            // Index the catalog by id (case-insensitive); the first module of a duplicated id is used.
            var byId = catalog
                .Where(module => !string.IsNullOrWhiteSpace(module.Id))
                .GroupBy(module => module.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            // "added" marks finished modules, "visiting" marks the dependency path currently being walked.
            var result = new List<ModuleConfig>();
            var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Depth-first walk: a module is appended only after all its dependencies, which gives the install order.
            void Visit(ModuleConfig module)
            {
                if (added.Contains(module.Id))
                {
                    return;
                }

                if (!visiting.Add(module.Id))
                    throw new InvalidOperationException(L.Get(LocalizationKeys.ModuleInfo_DependencyCycle, module.Name));

                // Blank dependency ids are ignored; every other id must exist in the catalog and is installed first.
                foreach (var dependency in module.Dependencies.Modules)
                {
                    var dependencyId = dependency.Id?.Trim();
                    if (string.IsNullOrEmpty(dependencyId))
                    {
                        continue;
                    }

                    if (byId.TryGetValue(dependencyId, out var dependencyModule))
                    {
                        Visit(dependencyModule);
                    }
                    else
                    {
                        throw new InvalidOperationException(L.Get(LocalizationKeys.ModuleInfo_MissingDependency, dependencyId));
                    }
                }

                // All dependencies are done: leave the active path and append the module itself.
                visiting.Remove(module.Id);
                if (added.Add(module.Id))
                {
                    result.Add(module);
                }
            }

            foreach (var module in selectedModules)
            {
                Visit(module);
            }

            return result;
        }


        // Direct dependencies

        /// <summary>
        /// Returns direct module dependency ids declared by one manifest.
        /// Ids are trimmed; blank ids are skipped and duplicates (ignoring case) are listed once.
        /// </summary>
        public static IReadOnlyList<string> GetDirectModuleDependencyIds(ModuleConfig module)
        {
            return module.Dependencies.Modules
                .Select(dependency => dependency.Id?.Trim())
                .Where(id => !string.IsNullOrEmpty(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(id => id!)
                .ToList();
        }
    }
}
