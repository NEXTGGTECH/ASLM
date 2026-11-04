// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Resources.Strings;

namespace ASLM.Localization
{
    /// <summary>
    /// Static accessor for localized UI strings backed by <see cref="AppResources"/>.
    /// </summary>
    public static class L
    {
        /// <summary>
        /// Localization service set by <see cref="Initialize"/>, or null until the application has created it.
        /// </summary>
        private static AppLocalizationService? _service;

        /// <summary>
        /// Binds the localization service used by <see cref="Get(string)"/>.
        /// </summary>
        public static void Initialize(AppLocalizationService service) => _service = service;

        /// <summary>
        /// Returns a localized string for the given resource key. Before <see cref="Initialize"/> is called it
        /// reads the resources directly, and an unknown key is returned unchanged.
        /// </summary>
        public static string Get(string key)
        {
            if (_service != null)
            {
                return _service.GetString(key);
            }

            return AppResources.ResourceManager.GetString(key, AppResources.Culture) ?? key;
        }

        /// <summary>
        /// Returns the localized string for <paramref name="key"/> with <paramref name="args"/> substituted into its
        /// composite format; without arguments the string is returned as is.
        /// </summary>
        public static string Get(string key, params object[] args)
        {
            var format = Get(key);
            return args.Length == 0 ? format : string.Format(format, args);
        }
    }
}
