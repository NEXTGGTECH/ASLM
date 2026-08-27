// Copyright NEXTGGTECH. Apache License 2.0.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace ASLM.Models
{
    /// <summary>
    /// Selects whether ASLM uses its local display-name profile or a SUNRISE cloud account.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter<AppAccountMode>))]
    public enum AppAccountMode
    {
        Local,
        Cloud
    }

    // Application data

    /// <summary>
    /// Stores the persisted application data loaded from <c>Data/App/ASLM_Data.json</c>.
    /// </summary>
    public class AppData
    {
        // Tracks whether the initial setup flow has already finished.
        [JsonPropertyName("firstRunCompleted")]
        public bool FirstRunCompleted { get; set; }

        // Stores user-facing profile information.
        [JsonPropertyName("user")]
        public AppUserData User { get; set; } = new();

        // Stores the reserved application port ranges.
        [JsonPropertyName("ports")]
        public AppPortConfig Ports { get; set; } = new();

        // Stores the local ASLM API mirror server settings.
        [JsonPropertyName("api")]
        public AppApiConfig Api { get; set; } = new();

        // Stores console page preferences.
        [JsonPropertyName("consoles")]
        public AppConsoleConfig Consoles { get; set; } = new();

        // Stores shell startup and last-page navigation preferences.
        [JsonPropertyName("navigation")]
        public AppNavigationConfig Navigation { get; set; } = new();

        // Stores ASLM and module update preferences.
        [JsonPropertyName("updates")]
        public AppUpdateSettings Updates { get; set; } = new();

        // Stores UI personalization preferences (theme mode and custom theme selection).
        [JsonPropertyName("personalization")]
        public AppPersonalizationConfig Personalization { get; set; } = new();

        // Stores legal document acceptance preferences.
        [JsonPropertyName("legal")]
        public AppLegalConfig Legal { get; set; } = new();

        // Stores optional GitHub account credentials for authenticated API access.
        [JsonPropertyName("github")]
        public AppGitHubSettings GitHub { get; set; } = new();

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
    /// Stores the last stable shell page and whether ASLM should restore it on startup.
    /// </summary>
    public class AppNavigationConfig
    {
        // Restores the last stable page instead of always opening the home dashboard.
        [JsonPropertyName("restoreLastPage")]
        public bool RestoreLastPage { get; set; } = true;

        // Uses a stable shell route rather than a transient local module URL.
        [JsonPropertyName("lastPage")]
        public string LastPage { get; set; } = ShellNavigationRoute.Home;

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
        // Automatically accepts newly added legal documents without showing the review overlay.
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
        // Stores the display name shown in the UI.
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        // Preserves the local profile name while the active cloud profile owns Name.
        [JsonPropertyName("localName")]
        public string LocalName { get; set; } = string.Empty;

        // Chooses between the existing local profile and a SUNRISE-backed cloud profile.
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
        // Indicates whether the local mirror server should start with ASLM.
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
        // Indicates whether the consoles page is shown in the sidebar.
        [JsonPropertyName("sidebarVisible")]
        public bool SidebarVisible { get; set; } = true;

        // Indicates whether completed process sessions remain visible in per-module console lists.
        [JsonPropertyName("showCompletedProcesses")]
        public bool ShowCompletedProcesses { get; set; } = false;

        // Indicates whether per-process consoles are available in addition to unified module consoles.
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
        // First port used when allocating module and internal service listeners.
        [JsonPropertyName("modulesStart")]
        public int ModulesStart { get; set; } = 20000;

        // Captures legacy port fields during JSON load for one-time migration.
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; set; }

        /// <summary>
        /// Migrates legacy official/third-party port settings and clamps the start port.
        /// </summary>
        public void Normalize()
        {
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
        [JsonPropertyName("checkEnabled")]
        public bool CheckEnabled { get; set; } = true;

        [JsonPropertyName("autoUpdateEnabled")]
        public bool AutoUpdateEnabled { get; set; } = true;

        [JsonPropertyName("autoCheckPeriodHours")]
        public int AutoCheckPeriodHours { get; set; } = 12;

        [JsonPropertyName("lastAutoCheckUtc")]
        public string? LastAutoCheckUtc { get; set; }

        [JsonPropertyName("appChannel")]
        public string AppChannel { get; set; } = "release";

        [JsonPropertyName("installedReleaseTag")]
        public string? InstalledReleaseTag { get; set; }

        [JsonPropertyName("availableAppUpdate")]
        public PersistedUpdateCandidate? AvailableAppUpdate { get; set; }

        [JsonPropertyName("availableOllamaUpdate")]
        public PersistedUpdateCandidate? AvailableOllamaUpdate { get; set; }

        [JsonPropertyName("moduleDefaultMode")]
        public string ModuleDefaultMode { get; set; } = "release";

        [JsonPropertyName("moduleDefaultChannel")]
        public string ModuleDefaultChannel { get; set; } = "release";

        /// <summary>
        /// Restores safe defaults and clamps numeric values.
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
        // One of: Dark, Light, System, Custom.
        [JsonPropertyName("appearance")]
        public string Appearance { get; set; } = "Dark";

        // BCP-47-style language code (e.g. en). Managed in personalization; modules receive a snapshot via locale settings.
        [JsonPropertyName("language")]
        public string Language { get; set; } = "en";

        // Identifier of the selected custom theme when Appearance is "Custom".
        [JsonPropertyName("customThemeId")]
        public string? CustomThemeId { get; set; }

        /// <summary>
        /// Restores safe defaults after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
            Appearance = NormalizeAppearance(Appearance);
            Language = NormalizeLanguage(Language);
            if (!string.Equals(Appearance, "Custom", StringComparison.OrdinalIgnoreCase))
            {
                CustomThemeId = null;
            }
        }

        private static readonly HashSet<string> SupportedLanguageCodes = new(StringComparer.OrdinalIgnoreCase)
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
            return SupportedLanguageCodes.Contains(trimmed) ? trimmed : "en";
        }

        /// <summary>
        /// Returns the canonical appearance string, falling back to Dark for unknown values.
        /// </summary>
        public static string NormalizeAppearance(string? value)
        {
            if (string.Equals(value, "Light", StringComparison.OrdinalIgnoreCase)) return "Light";
            if (string.Equals(value, "System", StringComparison.OrdinalIgnoreCase)) return "System";
            if (string.Equals(value, "Custom", StringComparison.OrdinalIgnoreCase)) return "Custom";
            return "Dark";
        }
    }
}
