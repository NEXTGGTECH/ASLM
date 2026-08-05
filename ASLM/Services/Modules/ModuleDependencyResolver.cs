// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Models;

namespace ASLM.Services.Modules
{
    /// <summary>
    /// Resolves declared module-to-module dependencies from manifests.
    /// </summary>
    public static class ModuleDependencyResolver
    {
        /// <summary>
        /// Returns the selected modules plus transitive module dependencies in install order
        /// (dependencies before dependents).
        /// </summary>
        public static IReadOnlyList<ModuleConfig> ExpandInstallOrder(
            IEnumerable<ModuleConfig> selectedModules,
            IReadOnlyList<ModuleConfig> catalog)
        {
            var byId = catalog
                .Where(module => !string.IsNullOrWhiteSpace(module.Id))
                .GroupBy(module => module.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            var result = new List<ModuleConfig>();
            var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Visit(ModuleConfig module)
            {
                if (added.Contains(module.Id))
                {
                    return;
                }

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
                }

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

        /// <summary>
        /// Returns direct module dependency ids declared by one manifest.
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
