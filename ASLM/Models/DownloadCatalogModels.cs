// Copyright NEXTGGTECH. Apache License 2.0.

using System.Text.Json.Serialization;

namespace ASLM.Models
{
    // Bridge requests

    /// <summary>
    /// Represents one JSON request sent from ASLM to a module downloads bridge.
    /// </summary>
    public class ModuleDownloadBridgeRequest
    {
        // Version of the JSON protocol expected by both sides.
        [JsonPropertyName("protocolVersion")]
        public int ProtocolVersion { get; set; } = 1;

        // Requested bridge operation, for example list_items.
        [JsonPropertyName("operation")]
        public string Operation { get; set; } = string.Empty;

        // Optional category identifier targeted by the request.
        [JsonPropertyName("categoryId")]
        public string CategoryId { get; set; } = string.Empty;

        // Optional resource identifier targeted by the request.
        [JsonPropertyName("resourceKey")]
        public string ResourceKey { get; set; } = string.Empty;

        // Optional free-form query text, typically mapped to a provider search box.
        [JsonPropertyName("queryText")]
        public string QueryText { get; set; } = string.Empty;

        // Optional provider-specific filter keys selected by the user.
        [JsonPropertyName("filters")]
        public List<string> Filters { get; set; } = [];

        // Indicates whether the bridge may satisfy the request from a local module cache.
        [JsonPropertyName("preferCached")]
        public bool PreferCached { get; set; }

        // Indicates whether the bridge should refresh its upstream cache before returning.
        [JsonPropertyName("forceRefresh")]
        public bool ForceRefresh { get; set; }

        /// <summary>
        /// Restores string values after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
            Operation ??= string.Empty;
            CategoryId ??= string.Empty;
            ResourceKey ??= string.Empty;
            QueryText ??= string.Empty;

            Filters ??= [];
            Filters = Filters
                .Where(static filter => !string.IsNullOrWhiteSpace(filter))
                .Select(static filter => filter.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }


    // Bridge responses

    /// <summary>
    /// Represents one JSON response produced by a module downloads bridge.
    /// </summary>
    public class ModuleDownloadBridgeResponse
    {
        // Protocol version returned by the module.
        [JsonPropertyName("protocolVersion")]
        public int ProtocolVersion { get; set; } = 1;

        // Indicates whether the bridge operation completed successfully.
        [JsonPropertyName("success")]
        public bool Success { get; set; } = true;

        // Optional human-readable error message.
        [JsonPropertyName("error")]
        public string? Error { get; set; }

        // Optional human-readable warning messages.
        [JsonPropertyName("warnings")]
        public List<string> Warnings { get; set; } = [];

        // Optional category payload returned by list_categories.
        [JsonPropertyName("categories")]
        public List<ModuleDownloadCategoryPayload> Categories { get; set; } = [];

        // Optional item payload returned by list_items.
        [JsonPropertyName("items")]
        public List<ModuleDownloadItemPayload> Items { get; set; } = [];

        // Optional filter payload returned by list_items.
        [JsonPropertyName("filters")]
        public List<ModuleDownloadFilterPayload> Filters { get; set; } = [];

        // Optional detailed item payload returned by describe_item.
        [JsonPropertyName("itemDetail")]
        public ModuleDownloadItemDetailPayload? ItemDetail { get; set; }

        // Optional install manifest returned by resolve_install.
        [JsonPropertyName("installManifest")]
        public ModuleDownloadInstallManifest? InstallManifest { get; set; }

        // Optional uninstall manifest returned by resolve_uninstall.
        [JsonPropertyName("uninstallManifest")]
        public ModuleDownloadInstallManifest? UninstallManifest { get; set; }

        /// <summary>
        /// Restores nested payloads after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
            Error = string.IsNullOrWhiteSpace(Error) ? null : Error;

            Warnings ??= [];
            Warnings = Warnings
                .Where(static warning => !string.IsNullOrWhiteSpace(warning))
                .Select(static warning => warning.Trim())
                .ToList();

            Categories ??= [];
            foreach (var category in Categories)
            {
                category?.Normalize();
            }

            Items ??= [];
            foreach (var item in Items)
            {
                item?.Normalize();
            }

            Filters ??= [];
            foreach (var filter in Filters)
            {
                filter?.Normalize();
            }

            ItemDetail?.Normalize();
            InstallManifest?.Normalize();
            UninstallManifest?.Normalize();
        }
    }


    // Category payloads

    /// <summary>
    /// Stores one category returned by a bridge at runtime.
    /// </summary>
    public class ModuleDownloadCategoryPayload
    {
        // Stable category identifier used in later bridge calls.
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        // Display title shown in ASLM.
        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        // Optional description shown in the UI.
        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        // Shared grouping key used to merge equivalent categories across modules.
        [JsonPropertyName("groupKey")]
        public string GroupKey { get; set; } = string.Empty;

        // Optional target reference commonly used by items in this category.
        [JsonPropertyName("targetRef")]
        public string TargetRef { get; set; } = string.Empty;

        // Optional sort order used after merging categories.
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


    // Item payloads

    /// <summary>
    /// Stores one installable family or grouped resource returned by a bridge category query.
    /// </summary>
    public class ModuleDownloadItemPayload
    {
        // Stable resource key used to deduplicate equivalent item families across modules.
        [JsonPropertyName("resourceKey")]
        public string ResourceKey { get; set; } = string.Empty;

        // Optional category identifier echoed back by the module.
        [JsonPropertyName("categoryId")]
        public string CategoryId { get; set; } = string.Empty;

        // Optional grouping key used to merge equivalent items under one category.
        [JsonPropertyName("groupKey")]
        public string GroupKey { get; set; } = string.Empty;

        // Human-readable title shown in the list.
        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        // Short summary shown under the title.
        [JsonPropertyName("summary")]
        public string Summary { get; set; } = string.Empty;

        // Optional provider label, for example Ollama.
        [JsonPropertyName("provider")]
        public string Provider { get; set; } = string.Empty;

        // Optional version label exposed by the source.
        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;

        // Optional homepage or details URL for the resource.
        [JsonPropertyName("homepageUrl")]
        public string HomepageUrl { get; set; } = string.Empty;

        // Optional secondary label shown next to the provider.
        [JsonPropertyName("detail")]
        public string Detail { get; set; } = string.Empty;

        // Optional tags, capabilities, or size labels shown in compact form.
        [JsonPropertyName("tags")]
        public List<string> Tags { get; set; } = [];

        // Optional number of variants available for this grouped item.
        [JsonPropertyName("variantCount")]
        public int VariantCount { get; set; }

        // Optional default variant resource key that ASLM should preselect inside the details pane.
        [JsonPropertyName("defaultVariantResourceKey")]
        public string DefaultVariantResourceKey { get; set; } = string.Empty;

        // Optional sort order used after merging duplicate items.
        [JsonPropertyName("sortOrder")]
        public int SortOrder { get; set; }

        /// <summary>
        /// Restores string values after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
            ResourceKey ??= string.Empty;
            CategoryId ??= string.Empty;
            GroupKey ??= string.Empty;
            Title ??= string.Empty;
            Summary ??= string.Empty;
            Provider ??= string.Empty;
            Version ??= string.Empty;
            HomepageUrl ??= string.Empty;
            Detail ??= string.Empty;
            DefaultVariantResourceKey ??= string.Empty;

            Tags ??= [];
            Tags = Tags
                .Where(static tag => !string.IsNullOrWhiteSpace(tag))
                .Select(static tag => tag.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>
    /// Stores one filter option returned by a module bridge for a specific category query.
    /// </summary>
    public class ModuleDownloadFilterPayload
    {
        [JsonPropertyName("key")]
        public string Key { get; set; } = string.Empty;

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("kind")]
        public string Kind { get; set; } = string.Empty;

        [JsonPropertyName("selected")]
        public bool Selected { get; set; }

        [JsonPropertyName("sortOrder")]
        public int SortOrder { get; set; }

        public void Normalize()
        {
            Key ??= string.Empty;
            Title ??= string.Empty;
            Kind ??= string.Empty;
        }
    }

    /// <summary>
    /// Stores one detailed item payload returned by a bridge when ASLM opens a family details pane.
    /// </summary>
    public class ModuleDownloadItemDetailPayload
    {
        [JsonPropertyName("resourceKey")]
        public string ResourceKey { get; set; } = string.Empty;

        [JsonPropertyName("categoryId")]
        public string CategoryId { get; set; } = string.Empty;

        [JsonPropertyName("groupKey")]
        public string GroupKey { get; set; } = string.Empty;

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("summary")]
        public string Summary { get; set; } = string.Empty;

        [JsonPropertyName("provider")]
        public string Provider { get; set; } = string.Empty;

        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;

        [JsonPropertyName("homepageUrl")]
        public string HomepageUrl { get; set; } = string.Empty;

        [JsonPropertyName("detail")]
        public string Detail { get; set; } = string.Empty;

        [JsonPropertyName("tags")]
        public List<string> Tags { get; set; } = [];

        [JsonPropertyName("defaultVariantResourceKey")]
        public string DefaultVariantResourceKey { get; set; } = string.Empty;

        [JsonPropertyName("variants")]
        public List<ModuleDownloadVariantPayload> Variants { get; set; } = [];

        [JsonPropertyName("blocks")]
        public List<ModuleDownloadInfoBlockPayload> Blocks { get; set; } = [];

        /// <summary>
        /// Restores detailed payload values after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
            ResourceKey ??= string.Empty;
            CategoryId ??= string.Empty;
            GroupKey ??= string.Empty;
            Title ??= string.Empty;
            Summary ??= string.Empty;
            Provider ??= string.Empty;
            Version ??= string.Empty;
            HomepageUrl ??= string.Empty;
            Detail ??= string.Empty;
            DefaultVariantResourceKey ??= string.Empty;

            Tags ??= [];
            Tags = Tags
                .Where(static tag => !string.IsNullOrWhiteSpace(tag))
                .Select(static tag => tag.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            Variants ??= [];
            foreach (var variant in Variants)
            {
                variant?.Normalize();
            }

            Blocks ??= [];
            foreach (var block in Blocks)
            {
                block?.Normalize();
            }
        }
    }

    /// <summary>
    /// Stores one selectable variant inside a grouped download item.
    /// </summary>
    public class ModuleDownloadVariantPayload
    {
        [JsonPropertyName("resourceKey")]
        public string ResourceKey { get; set; } = string.Empty;

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("summary")]
        public string Summary { get; set; } = string.Empty;

        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;

        [JsonPropertyName("detail")]
        public string Detail { get; set; } = string.Empty;

        [JsonPropertyName("homepageUrl")]
        public string HomepageUrl { get; set; } = string.Empty;

        [JsonPropertyName("tags")]
        public List<string> Tags { get; set; } = [];

        [JsonPropertyName("sortOrder")]
        public int SortOrder { get; set; }

        /// <summary>
        /// Restores variant values after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
            ResourceKey ??= string.Empty;
            Title ??= string.Empty;
            Summary ??= string.Empty;
            Version ??= string.Empty;
            Detail ??= string.Empty;
            HomepageUrl ??= string.Empty;

            Tags ??= [];
            Tags = Tags
                .Where(static tag => !string.IsNullOrWhiteSpace(tag))
                .Select(static tag => tag.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>
    /// Stores one arbitrary information block that ASLM renders under the item details pane.
    /// </summary>
    public class ModuleDownloadInfoBlockPayload
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("format")]
        public string Format { get; set; } = "text";

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;

        [JsonPropertyName("contentUrl")]
        public string ContentUrl { get; set; } = string.Empty;

        [JsonPropertyName("sourceUrl")]
        public string SourceUrl { get; set; } = string.Empty;

        [JsonPropertyName("sortOrder")]
        public int SortOrder { get; set; }

        /// <summary>
        /// Restores info-block values after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
            Id ??= string.Empty;
            Title ??= string.Empty;
            Format = string.IsNullOrWhiteSpace(Format) ? "text" : Format.Trim();
            Content ??= string.Empty;
            ContentUrl ??= string.Empty;
            SourceUrl ??= string.Empty;
        }
    }


    // Install manifests

    /// <summary>
    /// Represents one normalized install manifest returned by a bridge.
    /// </summary>
    public class ModuleDownloadInstallManifest
    {
        // Resource key that this manifest installs.
        [JsonPropertyName("resourceKey")]
        public string ResourceKey { get; set; } = string.Empty;

        // Optional category identifier.
        [JsonPropertyName("categoryId")]
        public string CategoryId { get; set; } = string.Empty;

        // Human-readable title used in progress logs.
        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        // Optional version label persisted after successful installation.
        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;

        // Default target reference used by actions that omit their own targetRef.
        [JsonPropertyName("targetRef")]
        public string TargetRef { get; set; } = string.Empty;

        // Actions executed sequentially by ASLM.
        [JsonPropertyName("actions")]
        public List<ModuleDownloadInstallAction> Actions { get; set; } = [];

        /// <summary>
        /// Restores string values and nested actions after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
            ResourceKey ??= string.Empty;
            CategoryId ??= string.Empty;
            Title ??= string.Empty;
            Version ??= string.Empty;
            TargetRef ??= string.Empty;

            Actions ??= [];
            foreach (var action in Actions)
            {
                action?.Normalize();
            }
        }
    }

    /// <summary>
    /// Describes one whitelisted action inside a downloads install manifest.
    /// </summary>
    public class ModuleDownloadInstallAction
    {
        // Action type handled by ASLM.
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        // Optional human-readable label used in the download log.
        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        // Optional target reference overriding the manifest-level default target.
        [JsonPropertyName("targetRef")]
        public string TargetRef { get; set; } = string.Empty;

        // Optional relative path inside the resolved target directory.
        [JsonPropertyName("relativePath")]
        public string RelativePath { get; set; } = string.Empty;

        // Optional source URL used by download_file.
        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        // Optional SHA-256 hash used to verify downloaded files.
        [JsonPropertyName("sha256")]
        public string Sha256 { get; set; } = string.Empty;

        // Optional in-memory artifact identifier produced by previous actions.
        [JsonPropertyName("artifactId")]
        public string ArtifactId { get; set; } = string.Empty;

        // Optional source artifact identifier consumed by later actions.
        [JsonPropertyName("sourceArtifactId")]
        public string SourceArtifactId { get; set; } = string.Empty;

        // Optional engine identifier used by engine-specific actions.
        [JsonPropertyName("engineId")]
        public string EngineId { get; set; } = string.Empty;

        // Optional Ollama model identifier used by ollama_pull.
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        // Optional package list used by python_package.
        [JsonPropertyName("packages")]
        public List<string> Packages { get; set; } = [];

        /// <summary>
        /// Restores scalar values and package collections after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
            Type ??= string.Empty;
            Title ??= string.Empty;
            TargetRef ??= string.Empty;
            RelativePath ??= string.Empty;
            Url ??= string.Empty;
            Sha256 ??= string.Empty;
            ArtifactId ??= string.Empty;
            SourceArtifactId ??= string.Empty;
            EngineId ??= string.Empty;
            Model ??= string.Empty;

            Packages ??= [];
            Packages = Packages
                .Where(static package => !string.IsNullOrWhiteSpace(package))
                .Select(static package => package.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }


    // Aggregated catalog

    /// <summary>
    /// Represents the merged shared download catalog built from all installed modules.
    /// </summary>
    public class DownloadCatalogSnapshot
    {
        // Categories shown in the shared download page.
        public List<DownloadCatalogCategory> Categories { get; set; } = [];

        // Non-fatal bridge warnings shown in the UI.
        public List<string> Warnings { get; set; } = [];
    }

    /// <summary>
    /// Represents one merged category shown in the shared download page.
    /// </summary>
    public class DownloadCatalogCategory
    {
        public string GroupKey { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public int SortOrder { get; set; }
        public List<DownloadCatalogFilter> Filters { get; set; } = [];
        public List<DownloadCatalogItem> Items { get; set; } = [];
    }

    /// <summary>
    /// Represents one provider-defined filter option shown for a catalog category.
    /// </summary>
    public class DownloadCatalogFilter
    {
        public string Key { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public bool Selected { get; set; }
        public int SortOrder { get; set; }
    }

    /// <summary>
    /// Represents one merged grouped resource shown in the shared download list.
    /// </summary>
    public class DownloadCatalogItem
    {
        public string ResourceKey { get; set; } = string.Empty;
        public string CategoryId { get; set; } = string.Empty;
        public string GroupKey { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public string Provider { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string HomepageUrl { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;
        public List<string> Tags { get; set; } = [];
        public int VariantCount { get; set; }
        public string DefaultVariantResourceKey { get; set; } = string.Empty;
        public int SortOrder { get; set; }
        public bool Installed { get; set; }
        public string InstalledVersion { get; set; } = string.Empty;
        public List<DownloadCatalogItemSource> Sources { get; set; } = [];
    }

    /// <summary>
    /// Represents one merged item detail payload shown after the user selects a grouped resource.
    /// </summary>
    public class DownloadCatalogItemDetail
    {
        public string ResourceKey { get; set; } = string.Empty;
        public string CategoryId { get; set; } = string.Empty;
        public string GroupKey { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public string Provider { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string HomepageUrl { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;
        public List<string> Tags { get; set; } = [];
        public string DefaultVariantResourceKey { get; set; } = string.Empty;
        public List<DownloadCatalogVariant> Variants { get; set; } = [];
        public List<DownloadCatalogInfoBlock> Blocks { get; set; } = [];
    }

    /// <summary>
    /// Stores one selectable variant shown inside an item details pane.
    /// </summary>
    public class DownloadCatalogVariant
    {
        public string ResourceKey { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;
        public string HomepageUrl { get; set; } = string.Empty;
        public List<string> Tags { get; set; } = [];
        public int SortOrder { get; set; }
        public bool Installed { get; set; }
        public string InstalledVersion { get; set; } = string.Empty;
    }

    /// <summary>
    /// Stores one arbitrary details block shown below the selected variant area.
    /// </summary>
    public class DownloadCatalogInfoBlock
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Format { get; set; } = "text";
        public string Content { get; set; } = string.Empty;
        public string ContentUrl { get; set; } = string.Empty;
        public string SourceUrl { get; set; } = string.Empty;
        public int SortOrder { get; set; }
    }

    /// <summary>
    /// Stores one provider source that contributed a merged catalog item.
    /// </summary>
    public class DownloadCatalogItemSource
    {
        public string ModuleId { get; set; } = string.Empty;
        public string ModuleName { get; set; } = string.Empty;
        public string ModuleSourcePath { get; set; } = string.Empty;
        public string CategoryId { get; set; } = string.Empty;
    }


    // Persistent state

    /// <summary>
    /// Stores the persisted shared download installation state.
    /// </summary>
    public class DownloadCatalogStateFile
    {
        [JsonPropertyName("resources")]
        public Dictionary<string, DownloadCatalogResourceState> Resources { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Restores nested state collections after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
            Resources ??= new(StringComparer.OrdinalIgnoreCase);

            var normalized = new Dictionary<string, DownloadCatalogResourceState>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, state) in Resources)
            {
                if (string.IsNullOrWhiteSpace(key) || state == null)
                {
                    continue;
                }

                state.Normalize();
                normalized[key.Trim()] = state;
            }

            Resources = normalized;
        }
    }

    /// <summary>
    /// Stores the persisted installation state for one shared resource.
    /// </summary>
    public class DownloadCatalogResourceState
    {
        [JsonPropertyName("installed")]
        public bool Installed { get; set; }

        [JsonPropertyName("installedVersion")]
        public string? InstalledVersion { get; set; }

        [JsonPropertyName("lastInstalledUtc")]
        public string? LastInstalledUtc { get; set; }

        [JsonPropertyName("providerModuleId")]
        public string? ProviderModuleId { get; set; }

        /// <summary>
        /// Normalizes optional text values after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
            InstalledVersion = string.IsNullOrWhiteSpace(InstalledVersion) ? null : InstalledVersion;
            LastInstalledUtc = string.IsNullOrWhiteSpace(LastInstalledUtc) ? null : LastInstalledUtc;
            ProviderModuleId = string.IsNullOrWhiteSpace(ProviderModuleId) ? null : ProviderModuleId;
        }
    }


    // Install results

    /// <summary>
    /// Represents the final outcome of one install request.
    /// </summary>
    public record DownloadInstallResult(bool Success, string Message);
}
