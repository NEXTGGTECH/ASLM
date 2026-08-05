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
        // Version of the manifest schema used for compatibility checks.
        [JsonPropertyName("fileVersion")]
        public int FileVersion { get; set; } = 1;

        // Stable model identifier used throughout the application.
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        // Human-readable model name shown in the UI.
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        // Short description of the model's purpose or capabilities.
        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        // Version string of the packaged model.
        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;

        // Resource type stored in the manifest. Defaults to "model".
        [JsonPropertyName("type")]
        public string Type { get; set; } = "model";

        // Functional category used for dependency matching.
        [JsonPropertyName("category")]
        public string Category { get; set; } = string.Empty;

        // Location of the remote model repository.
        [JsonPropertyName("source")]
        public ModelSource Source { get; set; } = new();

        // Optional explicit file list. When empty, the installer discovers files remotely.
        [JsonPropertyName("files")]
        public List<string> Files { get; set; } = [];

        // Persisted installation state of the model.
        [JsonPropertyName("status")]
        public ModelStatus Status { get; set; } = new();

        // Absolute path to the source manifest file resolved at runtime.
        [JsonIgnore]
        public string SourcePath { get; set; } = string.Empty;

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
        // Source provider identifier. The current installer expects "huggingface".
        [JsonPropertyName("type")]
        public string Type { get; set; } = "huggingface";

        // Remote repository identifier, for example "openai/whisper-large-v3".
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
        // Indicates whether the model is currently installed.
        [JsonPropertyName("installed")]
        public bool Installed { get; set; }

        // Version string of the installed model, if known.
        [JsonPropertyName("installedVersion")]
        public string? InstalledVersion { get; set; }

        // Timestamp of the latest installation check or update.
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
