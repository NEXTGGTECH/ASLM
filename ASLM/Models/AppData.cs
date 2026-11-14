// Copyright NEXTGGTECH. Apache License 2.0.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace ASLM.Models
{
    // Account mode

    /// <summary>
    /// Selects whether ASLM uses its local display-name profile or a SUNRISE cloud account.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter<AppAccountMode>))]
    public enum AppAccountMode
    {
        /// <summary>
        /// Uses the local display-name profile stored in the application data file.
        /// </summary>
        Local,

        /// <summary>
        /// Uses the profile of the signed-in SUNRISE cloud account.
        /// </summary>
        Cloud
    }


    // Application data

    /// <summary>
    /// Stores the persisted application data loaded from <c>Data/App/ASLM_Data.json</c>.
    /// </summary>
    public class AppData
    {
        // Persisted sections

        /// <summary>
        /// Tracks whether the initial setup flow has already finished.
        /// </summary>
        [JsonPropertyName("firstRunCompleted")]
        public bool FirstRunCompleted { get; set; }

        /// <summary>
        /// Stores user-facing profile information.
        /// </summary>
        [JsonPropertyName("user")]
        public AppUserData User { get; set; } = new();

        /// <summary>
        /// Stores the reserved application port ranges.
        /// </summary>
        [JsonPropertyName("ports")]
        public AppPortConfig Ports { get; set; } = new();

        /// <summary>
        /// Stores the local ASLM API mirror server settings.
        /// </summary>
        [JsonPropertyName("api")]
        public AppApiConfig Api { get; set; } = new();

        /// <summary>
        /// Stores console page preferences.
        /// </summary>
        [JsonPropertyName("consoles")]
        public AppConsoleConfig Consoles { get; set; } = new();

        /// <summary>
        /// Stores shell startup, last-page navigation and sidebar preferences.
        /// </summary>
        [JsonPropertyName("navigation")]
        public AppNavigationConfig Navigation { get; set; } = new();

        /// <summary>
        /// Stores ASLM and module update preferences.
        /// </summary>
        [JsonPropertyName("updates")]
        public AppUpdateSettings Updates { get; set; } = new();

        /// <summary>
        /// Stores UI personalization preferences (theme mode and custom theme selection).
        /// </summary>
        [JsonPropertyName("personalization")]
        public AppPersonalizationConfig Personalization { get; set; } = new();

        /// <summary>
        /// Stores legal document acceptance preferences.
        /// </summary>
        [JsonPropertyName("legal")]
        public AppLegalConfig Legal { get; set; } = new();

        /// <summary>
        /// Stores optional GitHub account credentials for authenticated API access.
        /// </summary>
        [JsonPropertyName("github")]
        public AppGitHubSettings GitHub { get; set; } = new();


        // Normalization

        /// <summary>
        /// Restores nested objects after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
            // Recreate the user section when it is missing from the persisted file.
            User ??= new();
            User.Normalize();

            // Recreate the ports section when it is missing from the persisted file.
            Ports ??= new();
            Ports.Normalize();

            // Recreate and normalize the API server section when it is absent.
            Api ??= new();
            Api.Normalize();

            // Recreate and normalize console preferences when the section is absent.
            Consoles ??= new();
            Consoles.Normalize();

            // Recreate and normalize shell navigation preferences when the section is absent.
            Navigation ??= new();
            Navigation.Normalize();

            // Recreate and normalize update preferences when the section is absent.
            Updates ??= new();
            Updates.Normalize();

            // Recreate and normalize personalization preferences when the section is absent.
            Personalization ??= new();
            Personalization.Normalize();

            // Recreate and normalize legal preferences when the section is absent.
            Legal ??= new();
            Legal.Normalize();

            // Recreate and normalize GitHub account settings when the section is absent.
            GitHub ??= new();
            GitHub.Normalize();
        }
    }


    // Shell navigation settings

    /// <summary>
    /// Stores the last stable shell page, whether ASLM should restore it on startup, and the sidebar state.
    /// </summary>
    public class AppNavigationConfig
    {
        // Persisted settings

        /// <summary>
        /// Restores the last stable page instead of always opening the home dashboard.
        /// </summary>
        [JsonPropertyName("restoreLastPage")]
        public bool RestoreLastPage { get; set; } = true;

        /// <summary>
        /// Keeps the sidebar expanded, with labels next to the navigation icons, when the shell opens.
        /// </summary>
        [JsonPropertyName("sidebarExpanded")]
        public bool SidebarExpanded { get; set; }

        /// <summary>
        /// Hides the unfinished home dashboard and uses Modules as the default page.
        /// </summary>
        [JsonPropertyName("disableHomePage")]
        public bool DisableHomePage { get; set; } = true;

        /// <summary>
        /// Uses a stable shell route rather than a transient local module URL.
        /// </summary>
        [JsonPropertyName("lastPage")]
        public string LastPage { get; set; } = ShellNavigationRoute.Home;


        // Startup route

        /// <summary>
        /// Resolves the startup route without restoring a disabled home page.
        /// </summary>
        public string GetInitialPage()
        {
            var route = RestoreLastPage ? ShellNavigationRoute.Normalize(LastPage) : ShellNavigationRoute.Home;
            return DisableHomePage && route == ShellNavigationRoute.Home ? ShellNavigationRoute.Modules : route;
        }

        /// <summary>
        /// Replaces missing or unsupported routes with the home dashboard.
        /// </summary>
        public void Normalize()
        {
            LastPage = ShellNavigationRoute.Normalize(LastPage);
        }
    }


    // Legal acceptance settings

    /// <summary>
    /// Stores preferences for legal document acceptance after the initial review.
    /// </summary>
    public class AppLegalConfig
    {
        /// <summary>
        /// Automatically accepts newly added legal documents without showing the review overlay.
        /// </summary>
        [JsonPropertyName("autoAcceptUpdates")]
        public bool AutoAcceptUpdates { get; set; } = true;

        /// <summary>
        /// Restores safe defaults after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
            // Boolean properties already carry their default values.
        }
    }


    // User data

    /// <summary>
    /// Stores the user profile values saved by the application.
    /// </summary>
    public class AppUserData
    {
        /// <summary>
        /// Stores the display name shown in the UI.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Preserves the local profile name while the active cloud profile owns <see cref="Name"/>.
        /// </summary>
        [JsonPropertyName("localName")]
        public string LocalName { get; set; } = string.Empty;

        /// <summary>
        /// Chooses between the existing local profile and a SUNRISE-backed cloud profile.
        /// </summary>
        [JsonPropertyName("accountMode")]
        public AppAccountMode AccountMode { get; set; } = AppAccountMode.Local;

        /// <summary>
        /// Restores string fields after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
            // Keep string properties safe for UI binding and serialization.
            Name ??= string.Empty;
            LocalName ??= string.Empty;

            // Unknown numeric enum values can be produced by manually edited files.
            if (!Enum.IsDefined(AccountMode))
            {
                AccountMode = AppAccountMode.Local;
            }

            // Files created before account modes existed only contain Name, which is local.
            if (string.IsNullOrWhiteSpace(LocalName) && AccountMode == AppAccountMode.Local)
            {
                LocalName = Name;
            }
        }
    }


    // API mirror server

    /// <summary>
    /// Stores preferences for the local ASLM API mirror server.
    /// </summary>
    public class AppApiConfig
    {
        /// <summary>
        /// Indicates whether the local mirror server should start with ASLM.
        /// </summary>
        [JsonPropertyName("serverEnabled")]
        public bool ServerEnabled { get; set; } = true;

        /// <summary>
        /// Restores safe defaults after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
            // The API port is allocated by PortRegistry from the shared module port pool.
        }
    }


    // Console page settings

    /// <summary>
    /// Stores preferences for the built-in consoles page.
    /// </summary>
    public class AppConsoleConfig
    {
        /// <summary>
        /// Indicates whether the consoles page is shown in the sidebar.
        /// </summary>
        [JsonPropertyName("sidebarVisible")]
        public bool SidebarVisible { get; set; } = true;

        /// <summary>
        /// Indicates whether completed process sessions remain visible in per-module console lists.
        /// </summary>
        [JsonPropertyName("showCompletedProcesses")]
        public bool ShowCompletedProcesses { get; set; } = false;

        /// <summary>
        /// Indicates whether per-process consoles are available in addition to unified module consoles.
        /// </summary>
        [JsonPropertyName("showIndividualModuleConsoles")]
        public bool ShowIndividualModuleConsoles { get; set; } = true;

        /// <summary>
        /// Restores safe defaults after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
            // Boolean properties already carry their default values.
        }
    }


    // Port allocation

    /// <summary>
    /// Defines the starting port for sequential module port allocation.
    /// </summary>
    public class AppPortConfig
    {
        /// <summary>
        /// First port used when allocating module and internal service listeners.
        /// </summary>
        [JsonPropertyName("modulesStart")]
        public int ModulesStart { get; set; } = 20000;

        /// <summary>
        /// Captures legacy port fields during JSON load for one-time migration.
        /// </summary>
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; set; }

        /// <summary>
        /// Migrates legacy official/third-party port settings and clamps the start port.
        /// </summary>
        public void Normalize()
        {
            // Adopt the legacy official start port once, then drop the captured legacy fields.
            if (ExtensionData != null)
            {
                if (!ExtensionData.ContainsKey("modulesStart") &&
                    ExtensionData.TryGetValue("officialStart", out var officialStartElement) &&
                    officialStartElement.TryGetInt32(out var officialStart) &&
                    officialStart >= 1024)
                {
                    ModulesStart = officialStart;
                }

                ExtensionData = null;
            }

            ModulesStart = Math.Clamp(ModulesStart, 1024, 65000);
        }
    }


    // Update settings

    /// <summary>
    /// Stores user preferences for update checks and automatic installation.
    /// </summary>
    public class AppUpdateSettings
    {
        // Update preferences

        /// <summary>
        /// Indicates whether ASLM checks for updates in the background.
        /// </summary>
        [JsonPropertyName("checkEnabled")]
        public bool CheckEnabled { get; set; } = true;

        /// <summary>
        /// Indicates whether updates found by the background check are installed without asking.
        /// </summary>
        [JsonPropertyName("autoUpdateEnabled")]
        public bool AutoUpdateEnabled { get; set; } = true;

        /// <summary>
        /// Number of hours between automatic update checks; <see cref="Normalize"/> pins it to one hour.
        /// </summary>
        [JsonPropertyName("autoCheckPeriodHours")]
        public int AutoCheckPeriodHours { get; set; } = 12;

        /// <summary>
        /// UTC time of the last automatic update check in round-trip ("o") format, or null if none ran yet.
        /// </summary>
        [JsonPropertyName("lastAutoCheckUtc")]
        public string? LastAutoCheckUtc { get; set; }

        /// <summary>
        /// Release channel used for ASLM itself: <c>release</c> or <c>pre-release</c>.
        /// </summary>
        [JsonPropertyName("appChannel")]
        public string AppChannel { get; set; } = "release";

        /// <summary>
        /// Release tag of the installed ASLM build, or null when it is not known.
        /// </summary>
        [JsonPropertyName("installedReleaseTag")]
        public string? InstalledReleaseTag { get; set; }

        /// <summary>
        /// ASLM update found by the last check and not installed yet, or null when none is pending.
        /// </summary>
        [JsonPropertyName("availableAppUpdate")]
        public PersistedUpdateCandidate? AvailableAppUpdate { get; set; }

        /// <summary>
        /// Ollama engine update found by the last check and not installed yet, or null when none is pending.
        /// </summary>
        [JsonPropertyName("availableOllamaUpdate")]
        public PersistedUpdateCandidate? AvailableOllamaUpdate { get; set; }

        /// <summary>
        /// Default update mode applied to modules; <see cref="Normalize"/> pins it to <c>release</c>.
        /// </summary>
        [JsonPropertyName("moduleDefaultMode")]
        public string ModuleDefaultMode { get; set; } = "release";

        /// <summary>
        /// Default release channel applied to modules: <c>release</c> or <c>pre-release</c>.
        /// </summary>
        [JsonPropertyName("moduleDefaultChannel")]
        public string ModuleDefaultChannel { get; set; } = "release";


        // Normalization

        /// <summary>
        /// Restores safe defaults and pins values that are not user-configurable (hourly check period, release mode).
        /// </summary>
        public void Normalize()
        {
            AutoCheckPeriodHours = 1;
            LastAutoCheckUtc = string.IsNullOrWhiteSpace(LastAutoCheckUtc) ? null : LastAutoCheckUtc;
            AppChannel = NormalizeChannel(AppChannel);
            InstalledReleaseTag = string.IsNullOrWhiteSpace(InstalledReleaseTag) ? null : InstalledReleaseTag.Trim();
            AvailableAppUpdate = NormalizeCandidate(AvailableAppUpdate, "app", "aslm");
            AvailableOllamaUpdate = NormalizeCandidate(AvailableOllamaUpdate, "engine", "ollama-service");
            ModuleDefaultMode = "release";
            ModuleDefaultChannel = NormalizeChannel(ModuleDefaultChannel);
        }

        /// <summary>
        /// Keeps only complete persisted candidates that match their expected update target.
        /// </summary>
        /// <param name="candidate">Persisted candidate read from the data file, if any.</param>
        /// <param name="expectedKind">
        /// Target kind the candidate must have, for example <c>app</c> or <c>engine</c>.
        /// </param>
        /// <param name="expectedId">Target identifier the candidate must have.</param>
        private static PersistedUpdateCandidate? NormalizeCandidate(
            PersistedUpdateCandidate? candidate,
            string expectedKind,
            string expectedId)
        {
            if (candidate == null ||
                !candidate.Normalize() ||
                !string.Equals(candidate.TargetKind, expectedKind, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(candidate.TargetId, expectedId, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return candidate;
        }

        /// <summary>
        /// Normalizes a release-channel value to one of the supported persisted names.
        /// </summary>
        private static string NormalizeChannel(string? value) =>
            string.Equals(value, "pre-release", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "prerelease", StringComparison.OrdinalIgnoreCase)
                ? "pre-release"
                : "release";
    }


    // Personalization

    /// <summary>
    /// Stores the selected appearance mode, UI language, and the active custom theme identifier.
    /// </summary>
    public class AppPersonalizationConfig
    {
        // Persisted settings

        /// <summary>
        /// One of: Dark, Light, System, Custom.
        /// </summary>
        [JsonPropertyName("appearance")]
        public string Appearance { get; set; } = "System";

        /// <summary>
        /// Supported BCP-47 language code. New profiles use the system UI language, or English if unsupported.
        /// </summary>
        [JsonPropertyName("language")]
        public string Language { get; set; } = AppLocalizationService.GetDefaultLanguage();

        /// <summary>
        /// Identifier of the selected custom theme when <see cref="Appearance"/> is "Custom".
        /// </summary>
        [JsonPropertyName("customThemeId")]
        public string? CustomThemeId { get; set; }


        // Normalization

        /// <summary>
        /// Restores safe defaults after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
            Appearance = NormalizeAppearance(Appearance);
            Language = string.IsNullOrWhiteSpace(Language)
                ? AppLocalizationService.GetDefaultLanguage()
                : NormalizeLanguage(Language);

            // A custom theme selection only makes sense while the Custom appearance is active.
            if (!string.Equals(Appearance, "Custom", StringComparison.OrdinalIgnoreCase))
            {
                CustomThemeId = null;
            }
        }


        // Supported values

        /// <summary>
        /// BCP-47 language codes the UI has translations for, matched case-insensitively.
        /// </summary>
        internal static readonly HashSet<string> SupportedLanguageCodes = new(StringComparer.OrdinalIgnoreCase)
        {
            "en",
            "zh-Hans", "es", "ar", "hi", "pt-BR", "ru", "ja", "de", "fr", "ko", "it",
            "zh-Hant", "pt", "tr", "pl", "uk", "id", "vi", "nl",
        };

        /// <summary>
        /// Returns the canonical language code, falling back to English for unknown values.
        /// </summary>
        public static string NormalizeLanguage(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "en";
            }

            var trimmed = value.Trim();
            return SupportedLanguageCodes.TryGetValue(trimmed, out var language) ? language : "en";
        }

        /// <summary>
        /// Returns the canonical appearance string, falling back to System for unknown values.
        /// </summary>
        public static string NormalizeAppearance(string? value)
        {
            if (string.Equals(value, "Dark", StringComparison.OrdinalIgnoreCase)) return "Dark";
            if (string.Equals(value, "Light", StringComparison.OrdinalIgnoreCase)) return "Light";
            if (string.Equals(value, "Custom", StringComparison.OrdinalIgnoreCase)) return "Custom";
            return "System";
        }
    }
}
