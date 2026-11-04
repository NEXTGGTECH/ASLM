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
        // Request fields

        /// <summary>
        /// Version of the JSON protocol expected by both sides.
        /// </summary>
        [JsonPropertyName("protocolVersion")]
        public int ProtocolVersion { get; set; } = 1;

        /// <summary>
        /// Requested bridge operation, for example list_items.
        /// </summary>
        [JsonPropertyName("operation")]
        public string Operation { get; set; } = string.Empty;

        /// <summary>
        /// Optional category identifier targeted by the request.
        /// </summary>
        [JsonPropertyName("categoryId")]
        public string CategoryId { get; set; } = string.Empty;

        /// <summary>
        /// Optional resource identifier targeted by the request.
        /// </summary>
        [JsonPropertyName("resourceKey")]
        public string ResourceKey { get; set; } = string.Empty;

        /// <summary>
        /// Optional free-form query text, typically mapped to a provider search box.
        /// </summary>
        [JsonPropertyName("queryText")]
        public string QueryText { get; set; } = string.Empty;

        /// <summary>
        /// Optional provider-specific filter keys selected by the user.
        /// </summary>
        [JsonPropertyName("filters")]
        public List<string> Filters { get; set; } = [];

        /// <summary>
        /// Indicates whether the bridge may satisfy the request from a local module cache.
        /// </summary>
        [JsonPropertyName("preferCached")]
        public bool PreferCached { get; set; }

        /// <summary>
        /// Indicates whether the bridge should refresh its upstream cache before returning.
        /// </summary>
        [JsonPropertyName("forceRefresh")]
        public bool ForceRefresh { get; set; }


        // Normalization

        /// <summary>
        /// Restores string values after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
            Operation ??= string.Empty;
            CategoryId ??= string.Empty;
            ResourceKey ??= string.Empty;
            QueryText ??= string.Empty;

            // Drop blank filter keys and duplicates so the module sees a clean list.
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
        // Response fields

        /// <summary>
        /// Protocol version returned by the module.
        /// </summary>
        [JsonPropertyName("protocolVersion")]
        public int ProtocolVersion { get; set; } = 1;

        /// <summary>
        /// Indicates whether the bridge operation completed successfully.
        /// </summary>
        [JsonPropertyName("success")]
        public bool Success { get; set; } = true;

        /// <summary>
        /// Optional human-readable error message.
        /// </summary>
        [JsonPropertyName("error")]
        public string? Error { get; set; }

        /// <summary>
        /// Optional human-readable warning messages.
        /// </summary>
        [JsonPropertyName("warnings")]
        public List<string> Warnings { get; set; } = [];

        /// <summary>
        /// Resources are local to this response; metadata refers to them by name.
        /// </summary>
        [JsonPropertyName("resources")]
        public ModuleDownloadResources? Resources { get; set; }

        /// <summary>
        /// Optional category payload returned by list_categories.
        /// </summary>
        [JsonPropertyName("categories")]
        public List<ModuleDownloadCategoryPayload> Categories { get; set; } = [];

        /// <summary>
        /// Optional item payload returned by list_items.
        /// </summary>
        [JsonPropertyName("items")]
        public List<ModuleDownloadItemPayload> Items { get; set; } = [];

        /// <summary>
        /// Optional filter payload returned by list_items.
        /// </summary>
        [JsonPropertyName("filters")]
        public List<ModuleDownloadFilterPayload> Filters { get; set; } = [];

        /// <summary>
        /// Optional detailed item payload returned by describe_item.
        /// </summary>
        [JsonPropertyName("itemDetail")]
        public ModuleDownloadItemDetailPayload? ItemDetail { get; set; }

        /// <summary>
        /// Optional install manifest returned by resolve_install.
        /// </summary>
        [JsonPropertyName("installManifest")]
        public ModuleDownloadInstallManifest? InstallManifest { get; set; }

        /// <summary>
        /// Optional uninstall manifest returned by resolve_uninstall.
        /// </summary>
        [JsonPropertyName("uninstallManifest")]
        public ModuleDownloadInstallManifest? UninstallManifest { get; set; }


        // Normalization

        /// <summary>
        /// Restores nested payloads after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
            Error = string.IsNullOrWhiteSpace(Error) ? null : Error;

            // Drop blank warnings and trim the rest.
            Warnings ??= [];
            Warnings = Warnings
                .Where(static warning => !string.IsNullOrWhiteSpace(warning))
                .Select(static warning => warning.Trim())
                .ToList();

            // Normalize every list payload, skipping null entries that a module may have emitted.
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

            // Normalize the single-object payloads, which are present only for some operations.
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
        // Payload fields

        /// <summary>
        /// Stable category identifier used in later bridge calls.
        /// </summary>
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        /// <summary>
        /// Display title shown in ASLM.
        /// </summary>
        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        /// <summary>
        /// Optional description shown in the UI.
        /// </summary>
        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Shared grouping key used to merge equivalent categories across modules.
        /// </summary>
        [JsonPropertyName("groupKey")]
        public string GroupKey { get; set; } = string.Empty;

        /// <summary>
        /// Optional target reference commonly used by items in this category.
        /// </summary>
        [JsonPropertyName("targetRef")]
        public string TargetRef { get; set; } = string.Empty;

        /// <summary>
        /// Optional sort order used after merging categories.
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


    // Item fields and response-local resources

    /// <summary>
    /// One optional, independently styled detail or feature supplied by a module.
    /// </summary>
    public sealed class ModuleDownloadField
    {
        /// <summary>
        /// Text shown for the detail or feature.
        /// </summary>
        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;

        /// <summary>
        /// Background color as a <c>#RRGGBB</c>/<c>#AARRGGBB</c> value or the name of a color in the response
        /// resources. After the bridge resolves it, the value is a normalized <c>#AARRGGBB</c> string, or null
        /// when it is missing or invalid.
        /// </summary>
        [JsonPropertyName("backgroundColor")]
        public string? BackgroundColor { get; set; }

        /// <summary>
        /// Text color in the same format as <see cref="BackgroundColor"/>.
        /// </summary>
        [JsonPropertyName("textColor")]
        public string? TextColor { get; set; }

        /// <summary>
        /// Whether the value is also shown in the compact catalog list, not only in the details pane.
        /// </summary>
        [JsonPropertyName("showInCatalog")]
        public bool ShowInCatalog { get; set; }

        /// <summary>
        /// Name of an icon defined in the response resources; resolved into <see cref="Image"/>.
        /// </summary>
        [JsonPropertyName("icon")]
        public string? Icon { get; set; }

        /// <summary>
        /// Resolved before merging providers. Never read from or written to the wire.
        /// </summary>
        [JsonIgnore]
        public DownloadCatalogIcon? Image { get; set; }
    }

    /// <summary>
    /// Holds the named colors and icons that item metadata in one bridge response refers to.
    /// </summary>
    public sealed class ModuleDownloadResources
    {
        /// <summary>
        /// Named hex colors referenced by <see cref="ModuleDownloadField"/> color properties.
        /// </summary>
        [JsonPropertyName("colors")]
        public Dictionary<string, string?>? Colors { get; set; }

        /// <summary>
        /// Named icon definitions referenced by <see cref="ModuleDownloadField.Icon"/>.
        /// </summary>
        [JsonPropertyName("icons")]
        public Dictionary<string, ModuleDownloadIconPayload?>? Icons { get; set; }
    }

    /// <summary>
    /// Describes one PNG icon, supplied either as a file inside the module or as inline base64 data.
    /// </summary>
    public sealed class ModuleDownloadIconPayload
    {
        /// <summary>
        /// Path of a PNG file inside the module directory; cannot be combined with <see cref="Base64"/>.
        /// </summary>
        [JsonPropertyName("path")]
        public string? Path { get; set; }

        /// <summary>
        /// Media type of the inline data; only <c>image/png</c> is accepted.
        /// </summary>
        [JsonPropertyName("mimeType")]
        public string? MimeType { get; set; }

        /// <summary>
        /// Inline base64-encoded PNG data, used when no <see cref="Path"/> is given.
        /// </summary>
        [JsonPropertyName("base64")]
        public string? Base64 { get; set; }
    }

    /// <summary>
    /// Validated PNG data identified by its content, independent of module-local names.
    /// </summary>
    /// <param name="Key">Hex SHA-256 hash of the image bytes, shared by identical icons from different modules.</param>
    /// <param name="Data">Encoded PNG bytes.</param>
    public sealed record DownloadCatalogIcon(string Key, byte[] Data);


    // Item payloads

    /// <summary>
    /// Stores one installable family or grouped resource returned by a bridge category query.
    /// </summary>
    public class ModuleDownloadItemPayload
    {
        // Payload fields

        /// <summary>
        /// Stable resource key used to deduplicate equivalent item families across modules.
        /// </summary>
        [JsonPropertyName("resourceKey")]
        public string ResourceKey { get; set; } = string.Empty;

        /// <summary>
        /// Optional category identifier echoed back by the module.
        /// </summary>
        [JsonPropertyName("categoryId")]
        public string CategoryId { get; set; } = string.Empty;

        /// <summary>
        /// Optional grouping key used to merge equivalent items under one category.
        /// </summary>
        [JsonPropertyName("groupKey")]
        public string GroupKey { get; set; } = string.Empty;

        /// <summary>
        /// Human-readable title shown in the list.
        /// </summary>
        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        /// <summary>
        /// Short summary shown under the title.
        /// </summary>
        [JsonPropertyName("summary")]
        public string Summary { get; set; } = string.Empty;

        /// <summary>
        /// Optional provider label, for example Ollama.
        /// </summary>
        [JsonPropertyName("provider")]
        public string Provider { get; set; } = string.Empty;

        /// <summary>
        /// Optional version label exposed by the source.
        /// </summary>
        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;

        /// <summary>
        /// Optional homepage or details URL for the resource.
        /// </summary>
        [JsonPropertyName("homepageUrl")]
        public string HomepageUrl { get; set; } = string.Empty;

        /// <summary>
        /// Optional detail values shown for the item; null when the module supplies none.
        /// </summary>
        [JsonPropertyName("details")]
        public List<ModuleDownloadField>? Details { get; set; }

        /// <summary>
        /// Optional feature tags shown for the item; null when the module supplies none.
        /// </summary>
        [JsonPropertyName("tags")]
        public List<ModuleDownloadField>? Tags { get; set; }

        /// <summary>
        /// Optional number of variants available for this grouped item.
        /// </summary>
        [JsonPropertyName("variantCount")]
        public int VariantCount { get; set; }

        /// <summary>
        /// Optional default variant resource key that ASLM should preselect inside the details pane.
        /// </summary>
        [JsonPropertyName("defaultVariantResourceKey")]
        public string DefaultVariantResourceKey { get; set; } = string.Empty;

        /// <summary>
        /// Optional sort order used after merging duplicate items.
        /// </summary>
        [JsonPropertyName("sortOrder")]
        public int SortOrder { get; set; }


        // Normalization

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
            DefaultVariantResourceKey ??= string.Empty;
        }
    }

    /// <summary>
    /// Stores one filter option returned by a module bridge for a specific category query.
    /// </summary>
    public class ModuleDownloadFilterPayload
    {
        // Payload fields

        /// <summary>
        /// Stable filter key sent back to the module in later list requests.
        /// </summary>
        [JsonPropertyName("key")]
        public string Key { get; set; } = string.Empty;

        /// <summary>
        /// Display title of the filter button.
        /// </summary>
        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        /// <summary>
        /// Filter kind; <c>sort</c> filters are single-select, all other kinds can be combined.
        /// </summary>
        [JsonPropertyName("kind")]
        public string Kind { get; set; } = string.Empty;

        /// <summary>
        /// Whether the module reports the filter as selected.
        /// </summary>
        [JsonPropertyName("selected")]
        public bool Selected { get; set; }

        /// <summary>
        /// Position of the filter relative to the other filters of the category.
        /// </summary>
        [JsonPropertyName("sortOrder")]
        public int SortOrder { get; set; }


        // Normalization

        /// <summary>
        /// Restores string values after JSON deserialization.
        /// </summary>
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
        // Payload fields

        /// <summary>
        /// Resource key of the described item family.
        /// </summary>
        [JsonPropertyName("resourceKey")]
        public string ResourceKey { get; set; } = string.Empty;

        /// <summary>
        /// Optional category identifier of the item.
        /// </summary>
        [JsonPropertyName("categoryId")]
        public string CategoryId { get; set; } = string.Empty;

        /// <summary>
        /// Optional grouping key used to merge equivalent items.
        /// </summary>
        [JsonPropertyName("groupKey")]
        public string GroupKey { get; set; } = string.Empty;

        /// <summary>
        /// Title shown at the top of the details pane.
        /// </summary>
        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        /// <summary>
        /// Short summary shown under the title.
        /// </summary>
        [JsonPropertyName("summary")]
        public string Summary { get; set; } = string.Empty;

        /// <summary>
        /// Optional provider label, for example Ollama.
        /// </summary>
        [JsonPropertyName("provider")]
        public string Provider { get; set; } = string.Empty;

        /// <summary>
        /// Optional version label exposed by the source.
        /// </summary>
        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;

        /// <summary>
        /// Optional homepage or details URL for the resource.
        /// </summary>
        [JsonPropertyName("homepageUrl")]
        public string HomepageUrl { get; set; } = string.Empty;

        /// <summary>
        /// Optional detail values; null keeps the details from the list item, an empty list clears them.
        /// </summary>
        [JsonPropertyName("details")]
        public List<ModuleDownloadField>? Details { get; set; }

        /// <summary>
        /// Optional feature tags; null keeps the tags from the list item, an empty list clears them.
        /// </summary>
        [JsonPropertyName("tags")]
        public List<ModuleDownloadField>? Tags { get; set; }

        /// <summary>
        /// Resource key of the variant that ASLM should preselect.
        /// </summary>
        [JsonPropertyName("defaultVariantResourceKey")]
        public string DefaultVariantResourceKey { get; set; } = string.Empty;

        /// <summary>
        /// Selectable variants of the item family.
        /// </summary>
        [JsonPropertyName("variants")]
        public List<ModuleDownloadVariantPayload> Variants { get; set; } = [];

        /// <summary>
        /// Free-form information blocks rendered below the variant area.
        /// </summary>
        [JsonPropertyName("blocks")]
        public List<ModuleDownloadInfoBlockPayload> Blocks { get; set; } = [];


        // Normalization

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
            DefaultVariantResourceKey ??= string.Empty;

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
        // Payload fields

        /// <summary>
        /// Resource key that identifies the variant for installation and installed-state tracking.
        /// </summary>
        [JsonPropertyName("resourceKey")]
        public string ResourceKey { get; set; } = string.Empty;

        /// <summary>
        /// Display title of the variant.
        /// </summary>
        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        /// <summary>
        /// Short summary of the variant.
        /// </summary>
        [JsonPropertyName("summary")]
        public string Summary { get; set; } = string.Empty;

        /// <summary>
        /// Whole bytes. HasSize distinguishes a known empty download from an unknown size.
        /// </summary>
        [JsonPropertyName("size")]
        public long Size { get; set; }

        /// <summary>
        /// Whether <see cref="Size"/> is known.
        /// </summary>
        [JsonPropertyName("hasSize")]
        public bool HasSize { get; set; }

        /// <summary>
        /// Optional homepage or details URL of the variant.
        /// </summary>
        [JsonPropertyName("homepageUrl")]
        public string HomepageUrl { get; set; } = string.Empty;

        /// <summary>
        /// Position of the variant relative to the other variants of the item.
        /// </summary>
        [JsonPropertyName("sortOrder")]
        public int SortOrder { get; set; }


        // Normalization

        /// <summary>
        /// Restores variant values after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
            ResourceKey ??= string.Empty;
            Title ??= string.Empty;
            Summary ??= string.Empty;
            HomepageUrl ??= string.Empty;

            // A negative or unflagged size means unknown, so it is reset to zero.
            if (Size < 0) HasSize = false;
            if (!HasSize) Size = 0;
        }
    }

    /// <summary>
    /// Stores one arbitrary information block that ASLM renders under the item details pane.
    /// </summary>
    public class ModuleDownloadInfoBlockPayload
    {
        // Payload fields

        /// <summary>
        /// Identifier of the block within its item.
        /// </summary>
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        /// <summary>
        /// Heading shown above the block content.
        /// </summary>
        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        /// <summary>
        /// Format of the content, for example <c>text</c> or <c>markdown</c>.
        /// </summary>
        [JsonPropertyName("format")]
        public string Format { get; set; } = "text";

        /// <summary>
        /// Inline content of the block, formatted as described by <see cref="Format"/>.
        /// </summary>
        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;

        /// <summary>
        /// Optional URL from which ASLM loads the content instead of using <see cref="Content"/>.
        /// </summary>
        [JsonPropertyName("contentUrl")]
        public string ContentUrl { get; set; } = string.Empty;

        /// <summary>
        /// Optional base URL used to resolve relative links and images inside the content.
        /// </summary>
        [JsonPropertyName("sourceUrl")]
        public string SourceUrl { get; set; } = string.Empty;

        /// <summary>
        /// Position of the block relative to the other blocks of the item.
        /// </summary>
        [JsonPropertyName("sortOrder")]
        public int SortOrder { get; set; }


        // Normalization

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
        // Manifest fields

        /// <summary>
        /// Resource key that this manifest installs.
        /// </summary>
        [JsonPropertyName("resourceKey")]
        public string ResourceKey { get; set; } = string.Empty;

        /// <summary>
        /// Optional category identifier.
        /// </summary>
        [JsonPropertyName("categoryId")]
        public string CategoryId { get; set; } = string.Empty;

        /// <summary>
        /// Human-readable title used in progress logs.
        /// </summary>
        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        /// <summary>
        /// Optional version label persisted after successful installation.
        /// </summary>
        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;

        /// <summary>
        /// Default target reference used by actions that omit their own targetRef.
        /// </summary>
        [JsonPropertyName("targetRef")]
        public string TargetRef { get; set; } = string.Empty;

        /// <summary>
        /// Actions executed sequentially by ASLM.
        /// </summary>
        [JsonPropertyName("actions")]
        public List<ModuleDownloadInstallAction> Actions { get; set; } = [];


        // Normalization

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
        // Action fields

        /// <summary>
        /// Action type handled by ASLM.
        /// </summary>
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        /// <summary>
        /// Optional human-readable label used in the download log.
        /// </summary>
        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        /// <summary>
        /// Optional target reference overriding the manifest-level default target.
        /// </summary>
        [JsonPropertyName("targetRef")]
        public string TargetRef { get; set; } = string.Empty;

        /// <summary>
        /// Optional relative path inside the resolved target directory.
        /// </summary>
        [JsonPropertyName("relativePath")]
        public string RelativePath { get; set; } = string.Empty;

        /// <summary>
        /// Optional source URL used by download_file.
        /// </summary>
        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        /// <summary>
        /// Optional SHA-256 hash used to verify downloaded files.
        /// </summary>
        [JsonPropertyName("sha256")]
        public string Sha256 { get; set; } = string.Empty;

        /// <summary>
        /// Optional in-memory artifact identifier produced by previous actions.
        /// </summary>
        [JsonPropertyName("artifactId")]
        public string ArtifactId { get; set; } = string.Empty;

        /// <summary>
        /// Optional source artifact identifier consumed by later actions.
        /// </summary>
        [JsonPropertyName("sourceArtifactId")]
        public string SourceArtifactId { get; set; } = string.Empty;

        /// <summary>
        /// Optional engine identifier used by engine-specific actions.
        /// </summary>
        [JsonPropertyName("engineId")]
        public string EngineId { get; set; } = string.Empty;

        /// <summary>
        /// Optional Ollama model identifier used by ollama_pull.
        /// </summary>
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        /// <summary>
        /// Optional package list used by python_package.
        /// </summary>
        [JsonPropertyName("packages")]
        public List<string> Packages { get; set; } = [];


        // Normalization

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

            // Drop blank package names and duplicates before they reach the package manager.
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
    /// Captures one category's query for an in-flight catalog request.
    /// </summary>
    /// <param name="QueryText">Free-form search text typed by the user.</param>
    /// <param name="Filters">Keys of the filters selected by the user.</param>
    public sealed record DownloadCatalogQuery(string QueryText, IReadOnlyCollection<string> Filters);

    /// <summary>
    /// Represents the merged shared download catalog built from all installed modules.
    /// </summary>
    public class DownloadCatalogSnapshot
    {
        /// <summary>
        /// Number of installed modules that declare a configured downloads bridge.
        /// </summary>
        public int ConfiguredProviderCount { get; set; }

        /// <summary>
        /// Number of configured providers whose category request completed successfully.
        /// </summary>
        public int SuccessfulProviderCount { get; set; }

        /// <summary>
        /// Categories shown in the shared download page.
        /// </summary>
        public List<DownloadCatalogCategory> Categories { get; set; } = [];

        /// <summary>
        /// Non-fatal bridge warnings shown in the UI.
        /// </summary>
        public List<string> Warnings { get; set; } = [];
    }

    /// <summary>
    /// Represents one merged category shown in the shared download page.
    /// </summary>
    public class DownloadCatalogCategory
    {
        /// <summary>
        /// Shared grouping key by which equivalent categories of different modules were merged.
        /// </summary>
        public string GroupKey { get; set; } = string.Empty;

        /// <summary>
        /// Display title of the category.
        /// </summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>
        /// Optional description shown in the UI.
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Position of the category relative to the other categories.
        /// </summary>
        public int SortOrder { get; set; }

        /// <summary>
        /// Filter options offered for the category.
        /// </summary>
        public List<DownloadCatalogFilter> Filters { get; set; } = [];

        /// <summary>
        /// Merged items of the category.
        /// </summary>
        public List<DownloadCatalogItem> Items { get; set; } = [];
    }

    /// <summary>
    /// Represents one provider-defined filter option shown for a catalog category.
    /// </summary>
    public class DownloadCatalogFilter
    {
        /// <summary>
        /// Stable filter key sent back to the providers in list requests.
        /// </summary>
        public string Key { get; set; } = string.Empty;

        /// <summary>
        /// Display title of the filter button.
        /// </summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>
        /// Filter kind; <c>sort</c> filters are single-select, all other kinds can be combined.
        /// </summary>
        public string Kind { get; set; } = string.Empty;

        /// <summary>
        /// Whether a provider reports the filter as selected.
        /// </summary>
        public bool Selected { get; set; }

        /// <summary>
        /// Position of the filter relative to the other filters of the category.
        /// </summary>
        public int SortOrder { get; set; }
    }

    /// <summary>
    /// Represents one merged grouped resource shown in the shared download list.
    /// </summary>
    public class DownloadCatalogItem
    {
        // Display data

        /// <summary>
        /// Stable resource key by which equivalent items of different modules were merged.
        /// </summary>
        public string ResourceKey { get; set; } = string.Empty;

        /// <summary>
        /// Identifier of the category the item was listed under.
        /// </summary>
        public string CategoryId { get; set; } = string.Empty;

        /// <summary>
        /// Grouping key of the category the item belongs to.
        /// </summary>
        public string GroupKey { get; set; } = string.Empty;

        /// <summary>
        /// Human-readable title shown in the list.
        /// </summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>
        /// Short summary shown under the title.
        /// </summary>
        public string Summary { get; set; } = string.Empty;

        /// <summary>
        /// Provider label, for example Ollama.
        /// </summary>
        public string Provider { get; set; } = string.Empty;

        /// <summary>
        /// Version label exposed by the source.
        /// </summary>
        public string Version { get; set; } = string.Empty;

        /// <summary>
        /// Homepage or details URL for the resource.
        /// </summary>
        public string HomepageUrl { get; set; } = string.Empty;

        /// <summary>
        /// Detail values merged from all sources.
        /// </summary>
        public List<ModuleDownloadField> Details { get; set; } = [];

        /// <summary>
        /// Feature tags merged from all sources.
        /// </summary>
        public List<ModuleDownloadField> Tags { get; set; } = [];

        /// <summary>
        /// Number of variants available for this grouped item.
        /// </summary>
        public int VariantCount { get; set; }

        /// <summary>
        /// Resource key of the variant that the details pane preselects.
        /// </summary>
        public string DefaultVariantResourceKey { get; set; } = string.Empty;

        /// <summary>
        /// Position of the item relative to the other items of the category.
        /// </summary>
        public int SortOrder { get; set; }


        // Installation state and sources

        /// <summary>
        /// Whether the persisted download state marks the resource as installed.
        /// </summary>
        public bool Installed { get; set; }

        /// <summary>
        /// Version recorded when the resource was installed; empty when none is recorded.
        /// </summary>
        public string InstalledVersion { get; set; } = string.Empty;

        /// <summary>
        /// Module and category pairs that provide this item.
        /// </summary>
        public List<DownloadCatalogItemSource> Sources { get; set; } = [];
    }

    /// <summary>
    /// Represents one merged item detail payload shown after the user selects a grouped resource.
    /// </summary>
    public class DownloadCatalogItemDetail
    {
        // Display data

        /// <summary>
        /// Resource key of the described item.
        /// </summary>
        public string ResourceKey { get; set; } = string.Empty;

        /// <summary>
        /// Identifier of the category the item was listed under.
        /// </summary>
        public string CategoryId { get; set; } = string.Empty;

        /// <summary>
        /// Grouping key of the category the item belongs to.
        /// </summary>
        public string GroupKey { get; set; } = string.Empty;

        /// <summary>
        /// Title shown at the top of the details pane.
        /// </summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>
        /// Short summary shown under the title.
        /// </summary>
        public string Summary { get; set; } = string.Empty;

        /// <summary>
        /// Provider label, for example Ollama.
        /// </summary>
        public string Provider { get; set; } = string.Empty;

        /// <summary>
        /// Version label exposed by the source.
        /// </summary>
        public string Version { get; set; } = string.Empty;

        /// <summary>
        /// Homepage or details URL for the resource.
        /// </summary>
        public string HomepageUrl { get; set; } = string.Empty;

        /// <summary>
        /// Detail values merged from all sources.
        /// </summary>
        public List<ModuleDownloadField> Details { get; set; } = [];

        /// <summary>
        /// Feature tags merged from all sources.
        /// </summary>
        public List<ModuleDownloadField> Tags { get; set; } = [];


        // Variants and information blocks

        /// <summary>
        /// Resource key of the variant that the details pane preselects.
        /// </summary>
        public string DefaultVariantResourceKey { get; set; } = string.Empty;

        /// <summary>
        /// Selectable variants of the item.
        /// </summary>
        public List<DownloadCatalogVariant> Variants { get; set; } = [];

        /// <summary>
        /// Free-form information blocks shown below the variant area.
        /// </summary>
        public List<DownloadCatalogInfoBlock> Blocks { get; set; } = [];
    }

    /// <summary>
    /// Stores one selectable variant shown inside an item details pane.
    /// </summary>
    public class DownloadCatalogVariant
    {
        /// <summary>
        /// Resource key that identifies the variant for installation and installed-state tracking.
        /// </summary>
        public string ResourceKey { get; set; } = string.Empty;

        /// <summary>
        /// Display title of the variant.
        /// </summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>
        /// Short summary of the variant.
        /// </summary>
        public string Summary { get; set; } = string.Empty;

        /// <summary>
        /// Download size in whole bytes; meaningful only when <see cref="HasSize"/> is true.
        /// </summary>
        public long Size { get; set; }

        /// <summary>
        /// Whether <see cref="Size"/> is known.
        /// </summary>
        public bool HasSize { get; set; }

        /// <summary>
        /// Homepage or details URL of the variant.
        /// </summary>
        public string HomepageUrl { get; set; } = string.Empty;

        /// <summary>
        /// Position of the variant relative to the other variants of the item.
        /// </summary>
        public int SortOrder { get; set; }

        /// <summary>
        /// Whether the persisted download state marks this variant as installed.
        /// </summary>
        public bool Installed { get; set; }
    }

    /// <summary>
    /// Stores one arbitrary details block shown below the selected variant area.
    /// </summary>
    public class DownloadCatalogInfoBlock
    {
        /// <summary>
        /// Identifier of the block within its item.
        /// </summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>
        /// Heading shown above the block content.
        /// </summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>
        /// Format of the content, for example <c>text</c> or <c>markdown</c>.
        /// </summary>
        public string Format { get; set; } = "text";

        /// <summary>
        /// Inline content of the block, formatted as described by <see cref="Format"/>.
        /// </summary>
        public string Content { get; set; } = string.Empty;

        /// <summary>
        /// URL from which the content is loaded instead of using <see cref="Content"/>, if any.
        /// </summary>
        public string ContentUrl { get; set; } = string.Empty;

        /// <summary>
        /// Base URL used to resolve relative links and images inside the content.
        /// </summary>
        public string SourceUrl { get; set; } = string.Empty;

        /// <summary>
        /// Position of the block relative to the other blocks of the item.
        /// </summary>
        public int SortOrder { get; set; }
    }

    /// <summary>
    /// Stores one provider source that contributed a merged catalog item.
    /// </summary>
    public class DownloadCatalogItemSource
    {
        /// <summary>
        /// Detail values this provider reported for the item.
        /// </summary>
        public List<ModuleDownloadField> Details { get; set; } = [];

        /// <summary>
        /// Feature tags this provider reported for the item.
        /// </summary>
        public List<ModuleDownloadField> Tags { get; set; } = [];

        /// <summary>
        /// Identifier of the module that provides the item.
        /// </summary>
        public string ModuleId { get; set; } = string.Empty;

        /// <summary>
        /// Display name of the module that provides the item.
        /// </summary>
        public string ModuleName { get; set; } = string.Empty;

        /// <summary>
        /// Path of the module manifest, used to reload the module for detail and install requests.
        /// </summary>
        public string ModuleSourcePath { get; set; } = string.Empty;

        /// <summary>
        /// Identifier of the module category the item was listed under.
        /// </summary>
        public string CategoryId { get; set; } = string.Empty;
    }


    // Persistent state

    /// <summary>
    /// Stores the persisted shared download installation state.
    /// </summary>
    public class DownloadCatalogStateFile
    {
        /// <summary>
        /// Installation state of each shared resource, keyed by resource key and compared case-insensitively.
        /// </summary>
        [JsonPropertyName("resources")]
        public Dictionary<string, DownloadCatalogResourceState> Resources { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Restores nested state collections after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
            Resources ??= new(StringComparer.OrdinalIgnoreCase);

            // Rebuild the map without blank keys or null states, trimming the remaining keys.
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
        /// <summary>
        /// Whether the resource is currently installed.
        /// </summary>
        [JsonPropertyName("installed")]
        public bool Installed { get; set; }

        /// <summary>
        /// Version label recorded by the last successful installation, or null when none is recorded.
        /// </summary>
        [JsonPropertyName("installedVersion")]
        public string? InstalledVersion { get; set; }

        /// <summary>
        /// UTC time of the last successful installation, or null when the resource was never installed.
        /// </summary>
        [JsonPropertyName("lastInstalledUtc")]
        public string? LastInstalledUtc { get; set; }

        /// <summary>
        /// Identifier of the module that performed the installation, or null when it is unknown.
        /// </summary>
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
    /// <param name="Success">Whether the install request completed successfully.</param>
    /// <param name="Message">Human-readable outcome shown to the user.</param>
    public record DownloadInstallResult(bool Success, string Message);
}
