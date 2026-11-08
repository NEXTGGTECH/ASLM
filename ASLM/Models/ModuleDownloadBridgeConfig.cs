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
        // Manifest properties

        /// <summary>
        /// Version of the bridge protocol spoken over JSON stdio.
        /// </summary>
        [JsonPropertyName("protocolVersion")]
        public int ProtocolVersion { get; set; } = 1;

        /// <summary>
        /// Engine identifier used to launch the bridge command, if required.
        /// </summary>
        [JsonPropertyName("engine")]
        public string Engine { get; set; } = string.Empty;

        /// <summary>
        /// Command or script entrypoint executed inside the module directory.
        /// </summary>
        [JsonPropertyName("entryPoint")]
        public string EntryPoint { get; set; } = string.Empty;

        /// <summary>
        /// Operations supported by the bridge, for example <c>list_items</c> or <c>resolve_install</c>.
        /// </summary>
        [JsonPropertyName("operations")]
        public List<string> Operations { get; set; } = [];

        /// <summary>
        /// Categories that the module may expose to the shared download catalog.
        /// </summary>
        [JsonPropertyName("categories")]
        public List<ModuleDownloadBridgeCategory> Categories { get; set; } = [];

        /// <summary>
        /// Named install targets that map bridge manifests to safe directories inside ASLM.
        /// </summary>
        [JsonPropertyName("targets")]
        public Dictionary<string, ModuleDownloadBridgeTarget> Targets { get; set; } = new(StringComparer.OrdinalIgnoreCase);


        // Computed properties

        /// <summary>
        /// Gets whether the bridge has enough data for ASLM to invoke it.
        /// </summary>
        [JsonIgnore]
        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(EntryPoint);


        // Normalization

        /// <summary>
        /// Restores nested objects and collections after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
            // Keep scalar string fields non-null.
            Engine ??= string.Empty;
            EntryPoint ??= string.Empty;

            // Drop blank operation names and collapse duplicates ignoring case.
            Operations ??= [];
            Operations = Operations
                .Where(static operation => !string.IsNullOrWhiteSpace(operation))
                .Select(static operation => operation.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Normalize every declared category.
            Categories ??= [];
            foreach (var category in Categories)
            {
                category?.Normalize();
            }

            // Rebuild the target map without blank keys or null entries, using trimmed keys.
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
        // Manifest properties

        /// <summary>
        /// Stable category identifier used by bridge requests.
        /// </summary>
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        /// <summary>
        /// Display title shown in ASLM.
        /// </summary>
        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        /// <summary>
        /// Optional description shown under the category title.
        /// </summary>
        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Shared grouping key used to merge equivalent categories across modules.
        /// </summary>
        [JsonPropertyName("groupKey")]
        public string GroupKey { get; set; } = string.Empty;

        /// <summary>
        /// Optional target reference used when the category usually installs into one managed location.
        /// </summary>
        [JsonPropertyName("targetRef")]
        public string TargetRef { get; set; } = string.Empty;

        /// <summary>
        /// Optional sort order applied after categories from multiple modules are merged.
        /// </summary>
        [JsonPropertyName("sortOrder")]
        public int SortOrder { get; set; }


        // Normalization

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
        /// <summary>
        /// Root bucket inside ASLM, for example <c>Models</c> or <c>Data</c>.
        /// </summary>
        [JsonPropertyName("root")]
        public string Root { get; set; } = string.Empty;

        /// <summary>
        /// Relative path inside the selected root bucket.
        /// </summary>
        [JsonPropertyName("relative")]
        public string Relative { get; set; } = string.Empty;

        /// <summary>
        /// Optional help text shown in diagnostics or future UI.
        /// </summary>
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
