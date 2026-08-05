// Copyright NEXTGGTECH. Apache License 2.0.

#if WINDOWS
using Microsoft.Win32;
#endif

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Reads whether Windows apps are using dark mode, independent of the MAUI app theme.
    /// Used when resolving missing keys in custom themes so fallbacks follow the OS appearance.
    /// </summary>
    public static class OsAppThemeReader
    {
        /// <summary>
        /// Returns true when Windows "app theme" is set to dark, or when the registry value is unavailable.
        /// </summary>
        public static bool IsWindowsAppDarkMode()
        {
#if WINDOWS
            // Registry read
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                var raw = key?.GetValue("AppsUseLightTheme");
                if (raw is int i)
                {
                    return i == 0;
                }

                if (raw is long l)
                {
                    return l == 0;
                }
            }
            catch
            {
                // Fall through to MAUI requested theme.
            }
#endif
            return Application.Current?.RequestedTheme != AppTheme.Light;
        }
    }
}
