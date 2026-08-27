// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Models
{
    /// <summary>
    /// Defines stable persisted routes for shell pages without storing transient controls, URLs, or ports.
    /// </summary>
    public static class ShellNavigationRoute
    {
        public const string Home = "home";
        public const string Consoles = "consoles";
        public const string Modules = "modules";
        public const string AslmApi = "aslm-api";

        private const string ModulePrefix = "module::";

        /// <summary>
        /// Creates a stable route for an installed module page.
        /// </summary>
        public static string ForModule(string? moduleId)
        {
            var normalizedId = moduleId?.Trim();
            return string.IsNullOrWhiteSpace(normalizedId)
                ? Home
                : $"{ModulePrefix}{normalizedId}";
        }

        /// <summary>
        /// Extracts a module identifier from a persisted module-page route.
        /// </summary>
        public static bool TryGetModuleId(string? route, out string moduleId)
        {
            moduleId = string.Empty;
            if (string.IsNullOrWhiteSpace(route))
            {
                return false;
            }

            var candidate = route.Trim();
            if (!candidate.StartsWith(ModulePrefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            moduleId = candidate[ModulePrefix.Length..].Trim();
            return !string.IsNullOrWhiteSpace(moduleId);
        }

        /// <summary>
        /// Canonicalizes supported routes and falls back to the home dashboard for unknown values.
        /// </summary>
        public static string Normalize(string? route)
        {
            var candidate = route?.Trim();
            if (string.Equals(candidate, Home, StringComparison.OrdinalIgnoreCase))
            {
                return Home;
            }

            if (string.Equals(candidate, Consoles, StringComparison.OrdinalIgnoreCase))
            {
                return Consoles;
            }

            if (string.Equals(candidate, Modules, StringComparison.OrdinalIgnoreCase))
            {
                return Modules;
            }

            if (string.Equals(candidate, AslmApi, StringComparison.OrdinalIgnoreCase))
            {
                return AslmApi;
            }

            return TryGetModuleId(candidate, out var moduleId)
                ? ForModule(moduleId)
                : Home;
        }
    }
}
