// Copyright NEXTGGTECH. Apache License 2.0.

using System.Text.Json;
using System.Text.Json.Serialization;
using ASLM.Models;
using Microsoft.Extensions.Logging;

namespace ASLM.Services.Modules
{
    /// <summary>
    /// Builds a JSON snapshot of the host ASLM UI language for modules that declare a <c>locale</c> setting.
    /// The snapshot is delivered through the standard <c>setExec</c> integration (typically via a temp file path).
    /// </summary>
    public sealed class ModuleLocalePayloadBuilder
    {
        // Fields

        /// <summary>
        /// Application data store that supplies the user's selected UI language.
        /// </summary>
        private readonly AppDataStore _appData;

        /// <summary>
        /// Logger for payload build failures.
        /// </summary>
        private readonly ILogger<ModuleLocalePayloadBuilder> _logger;

        /// <summary>
        /// Compact camelCase JSON settings; null values are omitted.
        /// </summary>
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };


        // Initialization

        /// <summary>
        /// Creates the payload builder.
        /// </summary>
        public ModuleLocalePayloadBuilder(AppDataStore appData, ILogger<ModuleLocalePayloadBuilder> logger)
        {
            _appData = appData;
            _logger = logger;
        }


        // Payload build

        /// <summary>
        /// Serializes the active host language to a single-line JSON string.
        /// Falls back to an English payload when the language cannot be read or serialized.
        /// </summary>
        public string BuildJson()
        {
            try
            {
                // Normalize the stored language code to a supported one and attach its native display name.
                var language = AppPersonalizationConfig.NormalizeLanguage(_appData.Data.Personalization.Language);
                var dto = new ModuleHostLocalePayloadDto
                {
                    Language = language,
                    DisplayName = AppLocalizationService.GetDisplayName(language)
                };

                return JsonSerializer.Serialize(dto, JsonOptions);
            }
            catch (Exception ex)
            {
                // A module must always receive a valid locale payload, so a failure degrades to English.
                _logger.LogError(ex, "Failed to build module host locale payload.");
                return "{\"language\":\"en\",\"displayName\":\"English\"}";
            }
        }


        // Serialization DTO

        /// <summary>
        /// JSON shape delivered to modules through the locale setting integration.
        /// </summary>
        private sealed class ModuleHostLocalePayloadDto
        {
            /// <summary>
            /// Normalized ASLM UI language code, for example <c>en</c>.
            /// </summary>
            public string Language { get; set; } = "en";

            /// <summary>
            /// Language name written in that language itself.
            /// </summary>
            public string DisplayName { get; set; } = "English";
        }
    }
}
