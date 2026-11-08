// Copyright NEXTGGTECH. Apache License 2.0.

using System.Text.Json.Serialization;

namespace ASLM.Models
{
    // Model manifest

    /// <summary>
    /// Describes a model package loaded from <c>ASLM_Model.json</c>.
    /// </summary>
    public class ModelConfig
    {
        // Manifest properties

        /// <summary>
        /// Version of the manifest schema used for compatibility checks.
        /// </summary>
        [JsonPropertyName("fileVersion")]
        public int FileVersion { get; set; } = 1;

        /// <summary>
        /// Stable model identifier used throughout the application.
        /// </summary>
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        /// <summary>
        /// Human-readable model name shown in the UI.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Short description of the model's purpose or capabilities.
        /// </summary>
        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Version string of the packaged model.
        /// </summary>
        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;

        /// <summary>
        /// Resource type stored in the manifest. Defaults to <c>model</c>.
        /// </summary>
        [JsonPropertyName("type")]
        public string Type { get; set; } = "model";

        /// <summary>
        /// Functional category used for dependency matching.
        /// </summary>
        [JsonPropertyName("category")]
        public string Category { get; set; } = string.Empty;

        /// <summary>
        /// Location of the remote model repository.
        /// </summary>
        [JsonPropertyName("source")]
        public ModelSource Source { get; set; } = new();

        /// <summary>
        /// Optional explicit file list. When empty, the installer discovers files remotely.
        /// </summary>
        [JsonPropertyName("files")]
        public List<string> Files { get; set; } = [];

        /// <summary>
        /// Persisted installation state of the model.
        /// </summary>
        [JsonPropertyName("status")]
        public ModelStatus Status { get; set; } = new();


        // Runtime state

        /// <summary>
        /// Absolute path to the source manifest file resolved at runtime.
        /// </summary>
        [JsonIgnore]
        public string SourcePath { get; set; } = string.Empty;


        // Normalization

        /// <summary>
        /// Restores nested objects, collections, and strings after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
            // Keep scalar string fields safe for downstream code and UI bindings.
            Id ??= string.Empty;
            Name ??= string.Empty;
            Description ??= string.Empty;
            Version ??= string.Empty;
            Type = string.IsNullOrWhiteSpace(Type) ? "model" : Type;
            Category ??= string.Empty;
            SourcePath ??= string.Empty;

            // Recreate and normalize the remote source definition.
            Source ??= new();
            Source.Normalize();

            // Remove empty file names while preserving explicit manifest entries.
            Files ??= [];
            Files = Files
                .Where(static file => !string.IsNullOrWhiteSpace(file))
                .ToList();

            // Ensure persisted runtime status is always available.
            Status ??= new();
            Status.Normalize();
        }
    }


    // Model source

    /// <summary>
    /// Describes where the model package is hosted.
    /// </summary>
    public class ModelSource
    {
        /// <summary>
        /// Source provider identifier. The current installer expects <c>huggingface</c>.
        /// </summary>
        [JsonPropertyName("type")]
        public string Type { get; set; } = "huggingface";

        /// <summary>
        /// Remote repository identifier, for example <c>openai/whisper-large-v3</c>.
        /// </summary>
        [JsonPropertyName("repoId")]
        public string RepoId { get; set; } = string.Empty;

        /// <summary>
        /// Restores required string values after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
            // Keep source values non-null and preserve the expected default provider.
            Type = string.IsNullOrWhiteSpace(Type) ? "huggingface" : Type;
            RepoId ??= string.Empty;
        }
    }


    // Model status

    /// <summary>
    /// Tracks the persisted installation state of a model package.
    /// </summary>
    public class ModelStatus
    {
        /// <summary>
        /// Indicates whether the model is currently installed.
        /// </summary>
        [JsonPropertyName("installed")]
        public bool Installed { get; set; }

        /// <summary>
        /// Version string of the installed model, if known.
        /// </summary>
        [JsonPropertyName("installedVersion")]
        public string? InstalledVersion { get; set; }

        /// <summary>
        /// Timestamp of the latest installation check or update.
        /// </summary>
        [JsonPropertyName("lastChecked")]
        public string? LastChecked { get; set; }

        /// <summary>
        /// Normalizes optional persisted values after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
            // Empty text values are treated as missing persisted state.
            InstalledVersion = string.IsNullOrWhiteSpace(InstalledVersion) ? null : InstalledVersion;
            LastChecked = string.IsNullOrWhiteSpace(LastChecked) ? null : LastChecked;
        }
    }
}
