// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Models
{
    /// <summary>
    /// Defines stable persisted routes for shell pages without storing transient controls, URLs, or ports.
    /// </summary>
    public static class ShellNavigationRoute
    {
        // Routes

        /// <summary>
        /// Route of the home dashboard; also the fallback for unknown routes.
        /// </summary>
        public const string Home = "home";

        /// <summary>
        /// Route of the built-in consoles page.
        /// </summary>
        public const string Consoles = "consoles";

        /// <summary>
        /// Route of the modules page.
        /// </summary>
        public const string Modules = "modules";

        /// <summary>
        /// Route of the ASLM API page.
        /// </summary>
        public const string AslmApi = "aslm-api";

        /// <summary>
        /// Prefix of module page routes; the module identifier follows it.
        /// </summary>
        private const string ModulePrefix = "module::";


        // Route conversion

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
            // Match the fixed page routes case-insensitively and return their canonical spelling.
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

            // Anything else must be a well-formed module route, otherwise fall back to home.
            return TryGetModuleId(candidate, out var moduleId)
                ? ForModule(moduleId)
                : Home;
        }
    }
}
