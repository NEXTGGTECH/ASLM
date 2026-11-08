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
        /// Returns true when the Windows "app theme" is set to dark. When the registry value cannot be read
        /// (or on other platforms), the result follows the MAUI requested theme and is dark unless it is light.
        /// </summary>
        public static bool IsWindowsAppDarkMode()
        {
#if WINDOWS
            // The app theme preference is stored as AppsUseLightTheme (0 = dark) under the current user.
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
