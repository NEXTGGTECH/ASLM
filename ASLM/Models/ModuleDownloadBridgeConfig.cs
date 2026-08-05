// Copyright NEXTGGTECH. Apache License 2.0.

using System.Text.Json.Serialization;

namespace ASLM.Models
{
    // Downloads bridge manifest

    /// <summary>
    /// Declares how one module exposes dynamic download catalogs and install manifests to ASLM.
    /// </summary>
    public class ModuleDownloadsBridge
    {
        // Version of the bridge protocol spoken over JSON stdio.
        [JsonPropertyName("protocolVersion")]
        public int ProtocolVersion { get; set; } = 1;

        // Engine identifier used to launch the bridge command, if required.
        [JsonPropertyName("engine")]
        public string Engine { get; set; } = string.Empty;

        // Command or script entrypoint executed inside the module directory.
        [JsonPropertyName("entryPoint")]
        public string EntryPoint { get; set; } = string.Empty;

        // Operations supported by the bridge, for example list_items or resolve_install.
        [JsonPropertyName("operations")]
        public List<string> Operations { get; set; } = [];

        // Categories that the module may expose to the shared download catalog.
        [JsonPropertyName("categories")]
        public List<ModuleDownloadBridgeCategory> Categories { get; set; } = [];

        // Named install targets that map bridge manifests to safe directories inside ASLM.
        [JsonPropertyName("targets")]
        public Dictionary<string, ModuleDownloadBridgeTarget> Targets { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Gets whether the bridge has enough data for ASLM to invoke it.
        /// </summary>
        [JsonIgnore]
        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(EntryPoint);

        /// <summary>
        /// Restores nested objects and collections after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
            Engine ??= string.Empty;
            EntryPoint ??= string.Empty;

            Operations ??= [];
            Operations = Operations
                .Where(static operation => !string.IsNullOrWhiteSpace(operation))
                .Select(static operation => operation.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            Categories ??= [];
            foreach (var category in Categories)
            {
                category?.Normalize();
            }

            Targets ??= new(StringComparer.OrdinalIgnoreCase);

            var normalizedTargets = new Dictionary<string, ModuleDownloadBridgeTarget>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, target) in Targets)
            {
                if (string.IsNullOrWhiteSpace(key) || target == null)
                {
                    continue;
                }

                target.Normalize();
                normalizedTargets[key.Trim()] = target;
            }

            Targets = normalizedTargets;
        }
    }


    // Declared categories

    /// <summary>
    /// Stores one category that a module may contribute to the shared download page.
    /// </summary>
    public class ModuleDownloadBridgeCategory
    {
        // Stable category identifier used by bridge requests.
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        // Display title shown in ASLM.
        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        // Optional description shown under the category title.
        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        // Shared grouping key used to merge equivalent categories across modules.
        [JsonPropertyName("groupKey")]
        public string GroupKey { get; set; } = string.Empty;

        // Optional target reference used when the category usually installs into one managed location.
        [JsonPropertyName("targetRef")]
        public string TargetRef { get; set; } = string.Empty;

        // Optional sort order applied after categories from multiple modules are merged.
        [JsonPropertyName("sortOrder")]
        public int SortOrder { get; set; }

        /// <summary>
        /// Restores string values after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
            Id ??= string.Empty;
            Title ??= string.Empty;
            Description ??= string.Empty;
            GroupKey ??= string.Empty;
            TargetRef ??= string.Empty;
        }
    }


    // Declared targets

    /// <summary>
    /// Maps one named target reference to a safe installation directory inside ASLM.
    /// </summary>
    public class ModuleDownloadBridgeTarget
    {
        // Root bucket inside ASLM, for example Models or Data.
        [JsonPropertyName("root")]
        public string Root { get; set; } = string.Empty;

        // Relative path inside the selected root bucket.
        [JsonPropertyName("relative")]
        public string Relative { get; set; } = string.Empty;

        // Optional help text shown in diagnostics or future UI.
        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Restores string values after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
            Root ??= string.Empty;
            Relative ??= string.Empty;
            Description ??= string.Empty;
        }
    }
}
