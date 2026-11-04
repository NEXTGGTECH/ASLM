// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Models;
using Microsoft.Extensions.Logging;

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Builds the shared download catalog by querying every installed module bridge and merging equivalent items.
    /// </summary>
    public class DownloadCatalog
    {
        // Fields and constants

        /// <summary>
        /// Upper bound of module bridges that are queried at the same time.
        /// </summary>
        private const int MaxConcurrentBridgeRequests = 4;

        /// <summary>
        /// Discovers installed modules and loads their manifests.
        /// </summary>
        private readonly ModuleInstaller _moduleInstaller;

        /// <summary>
        /// Client that calls the download bridge of a module.
        /// </summary>
        private readonly ModuleDownloadBridge _bridge;

        /// <summary>
        /// Persisted installation state that marks items and variants as installed.
        /// </summary>
        private readonly DownloadStateStore _stateStore;

        /// <summary>
        /// Logger for bridge failures.
        /// </summary>
        private readonly ILogger<DownloadCatalog> _logger;


        // Initialization

        /// <summary>
        /// Creates the shared download catalog service.
        /// </summary>
        public DownloadCatalog(
            ModuleInstaller moduleInstaller,
            ModuleDownloadBridge bridge,
            DownloadStateStore stateStore,
            ILogger<DownloadCatalog> logger)
        {
            _moduleInstaller = moduleInstaller;
            _bridge = bridge;
            _stateStore = stateStore;
            _logger = logger;
        }


        // Catalog loading

        /// <summary>
        /// Publishes categories as their items arrive, then returns the complete merged snapshot.
        /// Every module with a configured downloads bridge is queried (a few at a time); a failing module only
        /// adds a warning, so the remaining providers still contribute.
        /// </summary>
        /// <param name="categoryQueries">Search text and filters per category group key; null loads without a query.</param>
        /// <param name="categoryGroupKey">When set, only the category with this group key is loaded.</param>
        /// <param name="preferCached">Allows the bridges to answer from their local cache.</param>
        /// <param name="forceRefresh">Asks the bridges to refresh their upstream cache first.</param>
        /// <param name="categoryLoaded">Called with the merged category each time new items were merged into it.</param>
        /// <param name="ct">Cancellation token for the whole load.</param>
        public async Task<DownloadCatalogSnapshot> LoadCatalogAsync(
            IReadOnlyDictionary<string, DownloadCatalogQuery>? categoryQueries = null,
            string? categoryGroupKey = null,
            bool preferCached = false,
            bool forceRefresh = false,
            Func<DownloadCatalogCategory, Task>? categoryLoaded = null,
            CancellationToken ct = default)
        {
            // Discover only modules that actually expose the downloads bridge contract.
            var modules = await _moduleInstaller.DiscoverInstalledModulesAsync();
            var bridgeModules = modules
                .Where(module => module.DownloadsBridge?.IsConfigured == true)
                .ToList();

            // Shared merge state: providers contribute in parallel, but the number of simultaneous bridge
            // calls is throttled and delivery of merged categories is serialized.
            var warnings = new List<string>();
            var categoryBuilders = new Dictionary<string, CategoryBuilder>(StringComparer.OrdinalIgnoreCase);
            var mergeLock = new object();
            using var bridgeThrottle = new SemaphoreSlim(MaxConcurrentBridgeRequests);
            using var publishLock = new SemaphoreSlim(1);

            // Query all bridge modules concurrently, each task waiting for a free throttle slot.
            var loadTasks = bridgeModules.Select(async module =>
            {
                await bridgeThrottle.WaitAsync(ct);
                try
                {
                    return await LoadModuleCatalogAsync(
                        module,
                        categoryQueries,
                        categoryGroupKey,
                        preferCached,
                        forceRefresh,
                        warnings,
                        categoryBuilders,
                        mergeLock,
                        publishLock,
                        categoryLoaded,
                        ct);
                }
                finally
                {
                    bridgeThrottle.Release();
                }
            });

            var providerResults = await Task.WhenAll(loadTasks);

            // Convert builders only after every module has contributed its portion of the catalog.
            return new DownloadCatalogSnapshot
            {
                ConfiguredProviderCount = bridgeModules.Count,
                SuccessfulProviderCount = providerResults.Count(static succeeded => succeeded),
                Warnings = warnings
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(static warning => warning, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                Categories = categoryBuilders.Values
                    .Select(builder => builder.ToCategory(_stateStore))
                    .OrderBy(category => category.SortOrder)
                    .ThenBy(category => category.Title, StringComparer.OrdinalIgnoreCase)
                    .ToList()
            };
        }

        /// <summary>
        /// Loads and merges the bridge catalog exposed by one module.
        /// Returns whether the module's categories could be loaded; item failures only add warnings.
        /// </summary>
        /// <param name="module">Module whose bridge is queried.</param>
        /// <param name="categoryQueries">Search text and filters per category group key.</param>
        /// <param name="categoryGroupKey">When set, only the category with this group key is loaded.</param>
        /// <param name="preferCached">Allows the bridge to answer from its local cache.</param>
        /// <param name="forceRefresh">Asks the bridge to refresh its upstream cache first.</param>
        /// <param name="warnings">Shared warning list; guarded by <paramref name="mergeLock"/>.</param>
        /// <param name="categoryBuilders">Shared merged categories by group key; guarded by <paramref name="publishLock"/>.</param>
        /// <param name="mergeLock">Guards <paramref name="warnings"/>.</param>
        /// <param name="publishLock">Serializes merging into <paramref name="categoryBuilders"/> and delivery to the callback.</param>
        /// <param name="categoryLoaded">Called with each merged category after new items arrived.</param>
        /// <param name="ct">Cancellation token for the load.</param>
        private async Task<bool> LoadModuleCatalogAsync(
            ModuleConfig module,
            IReadOnlyDictionary<string, DownloadCatalogQuery>? categoryQueries,
            string? categoryGroupKey,
            bool preferCached,
            bool forceRefresh,
            List<string> warnings,
            Dictionary<string, CategoryBuilder> categoryBuilders,
            object mergeLock,
            SemaphoreSlim publishLock,
            Func<DownloadCatalogCategory, Task>? categoryLoaded,
            CancellationToken ct)
        {
            // Ask the module for its categories; cancellation propagates, any other failure degrades to
            // the categories declared in the manifest plus a warning.
            List<ModuleDownloadCategoryPayload> categories;
            var succeeded = true;
            try
            {
                categories = await _bridge.GetCategoriesAsync(module, preferCached, forceRefresh, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The provider is unavailable: keep its declared categories so the UI can still show the sections.
                _logger.LogWarning(ex, "Failed to load download categories for module {ModuleId}.", module.Id);
                AddWarning(warnings, mergeLock, $"{module.Name}: download categories could not be loaded.");
                categories = CreateDeclaredCategoryPayloads(module);
                succeeded = false;
            }

            // A module that reports no categories falls back to the manifest declaration as well.
            if (categories.Count == 0)
            {
                categories = CreateDeclaredCategoryPayloads(module);
            }

            // Load and merge the items of each category in turn.
            foreach (var category in categories)
            {
                ct.ThrowIfCancellationRequested();

                // Group by the shared category key so equivalent provider catalogs merge into one UI surface.
                if (string.IsNullOrWhiteSpace(category.Id))
                {
                    continue;
                }

                var groupKey = !string.IsNullOrWhiteSpace(category.GroupKey)
                    ? category.GroupKey
                    : $"{module.Id}:{category.Id}";

                // The caller may restrict loading to a single merged category.
                if (categoryGroupKey != null && !string.Equals(groupKey, categoryGroupKey, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Fetch the category items; a failed category adds a warning and contributes nothing.
                ModuleDownloadBridgeResponse itemResponse;
                try
                {
                    var query = categoryQueries?.GetValueOrDefault(groupKey);
                    itemResponse = succeeded
                        ? await _bridge.GetItemsAsync(module, category.Id, query?.QueryText, query?.Filters, preferCached, forceRefresh, ct)
                        : new ModuleDownloadBridgeResponse();
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load download items for module {ModuleId} category {CategoryId}.", module.Id, category.Id);
                    AddWarning(warnings, mergeLock, $"{module.Name}: {category.Title} could not be loaded.");
                    itemResponse = new ModuleDownloadBridgeResponse();
                }

                // Forward warnings reported by the provider itself, prefixed with the module name.
                foreach (var warning in itemResponse.Warnings)
                {
                    AddWarning(warnings, mergeLock, $"{module.Name}: {warning}");
                }

                // Serialize merging and delivery so an older provider result cannot overwrite a newer merge.
                await publishLock.WaitAsync(ct);
                try
                {
                    // Merge the provider's filters into the shared category first.
                    var categoryBuilder = GetOrCreateCategoryBuilder(categoryBuilders, groupKey, category);
                    var updatedCategories = new HashSet<CategoryBuilder> { categoryBuilder };
                    foreach (var filterPayload in itemResponse.Filters)
                    {
                        ct.ThrowIfCancellationRequested();
                        filterPayload.Normalize();
                        if (string.IsNullOrWhiteSpace(filterPayload.Key))
                        {
                            continue;
                        }

                        categoryBuilder.MergeFilter(filterPayload);
                    }

                    foreach (var item in itemResponse.Items)
                    {
                        ct.ThrowIfCancellationRequested();

                        // Resource group keys may intentionally redirect items into a different merged category.
                        item.Normalize();
                        if (string.IsNullOrWhiteSpace(item.ResourceKey))
                        {
                            continue;
                        }

                        var resourceGroupKey = !string.IsNullOrWhiteSpace(item.GroupKey)
                            ? item.GroupKey
                            : groupKey;

                        var itemCategoryBuilder = string.Equals(resourceGroupKey, categoryBuilder.GroupKey, StringComparison.OrdinalIgnoreCase)
                            ? categoryBuilder
                            : GetOrCreateCategoryBuilder(categoryBuilders, resourceGroupKey, category);

                        MergeItem(itemCategoryBuilder, module, category, item);
                        updatedCategories.Add(itemCategoryBuilder);
                    }

                    // Deliver every category that received filters or items, still under the publish lock.
                    if (categoryLoaded != null)
                        foreach (var updated in updatedCategories)
                            await categoryLoaded(updated.ToCategory(_stateStore));
                }
                finally
                {
                    publishLock.Release();
                }
            }

            return succeeded;
        }

        /// <summary>
        /// Adds one warning while bridge catalog loading runs in parallel.
        /// </summary>
        /// <param name="warnings">Shared warning list.</param>
        /// <param name="mergeLock">Lock that guards the list.</param>
        /// <param name="warning">Warning text to add.</param>
        private static void AddWarning(List<string> warnings, object mergeLock, string warning)
        {
            lock (mergeLock)
            {
                warnings.Add(warning);
            }
        }

        /// <summary>
        /// Materializes the categories declared directly in one module manifest.
        /// </summary>
        private static List<ModuleDownloadCategoryPayload> CreateDeclaredCategoryPayloads(ModuleConfig module)
        {
            return module.DownloadsBridge?.Categories
                .Where(static category => category != null && !string.IsNullOrWhiteSpace(category.Id))
                .Select(static category => new ModuleDownloadCategoryPayload
                {
                    Id = category.Id,
                    Title = category.Title,
                    Description = category.Description,
                    GroupKey = category.GroupKey,
                    TargetRef = category.TargetRef,
                    SortOrder = category.SortOrder
                })
                .ToList() ?? [];
        }


        // Item details

        /// <summary>
        /// Loads the detailed payload for one selected catalog item.
        /// Queries every module source of the item in a stable order and merges their details; when no source
        /// supplies details, a minimal fallback detail is built from the list item.
        /// </summary>
        /// <param name="item">Catalog item whose details are requested.</param>
        /// <param name="preferCached">Allows the bridges to answer from their local cache.</param>
        /// <param name="forceRefresh">Asks the bridges to refresh their upstream cache first.</param>
        /// <param name="ct">Cancellation token for the load.</param>
        public async Task<DownloadCatalogItemDetail> GetItemDetailAsync(
            DownloadCatalogItem item,
            bool preferCached = false,
            bool forceRefresh = false,
            CancellationToken ct = default)
        {
            if (item.Sources.Count == 0)
            {
                return BuildFallbackDetail(item);
            }

            var builder = new ItemDetailBuilder(item);
            var hadAnyDetail = false;

            // Ordering the sources makes the merge result independent of the order modules answered in.
            foreach (var source in item.Sources.OrderBy(source => source.ModuleSourcePath, StringComparer.Ordinal)
                .ThenBy(source => source.CategoryId, StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();

                // Skip sources whose module manifest can no longer be loaded.
                var module = await _moduleInstaller.LoadModuleConfig(source.ModuleSourcePath);
                if (module == null)
                {
                    continue;
                }

                // A failing source is logged and skipped so the remaining sources can still contribute.
                try
                {
                    var detail = await _bridge.GetItemDetailAsync(
                        module,
                        source.CategoryId,
                        item.ResourceKey,
                        preferCached,
                        forceRefresh,
                        ct);

                    if (detail == null)
                    {
                        continue;
                    }

                    detail.Normalize();
                    builder.Merge(detail, source);
                    hadAnyDetail = true;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load item details for resource {ResourceKey} via module {ModuleId}.", item.ResourceKey, module.Id);
                }
            }

            // Fall back to list data when no module could describe the item.
            return hadAnyDetail
                ? builder.ToDetail(_stateStore)
                : BuildFallbackDetail(item);
        }


        // Builder helpers

        /// <summary>
        /// Returns the existing category builder or creates one from the current bridge category.
        /// An existing builder is enriched with metadata the new payload provides.
        /// </summary>
        /// <param name="categoryBuilders">Merged categories by group key.</param>
        /// <param name="groupKey">Group key that identifies the merged category.</param>
        /// <param name="category">Bridge category that is being merged.</param>
        private static CategoryBuilder GetOrCreateCategoryBuilder(
            IDictionary<string, CategoryBuilder> categoryBuilders,
            string groupKey,
            ModuleDownloadCategoryPayload category)
        {
            if (categoryBuilders.TryGetValue(groupKey, out var existing))
            {
                existing.Merge(category);
                return existing;
            }

            var builder = new CategoryBuilder(groupKey, category);
            categoryBuilders[groupKey] = builder;
            return builder;
        }

        /// <summary>
        /// Merges one bridge item into the current aggregated category.
        /// Items with the same resource key from different modules become one item with several sources.
        /// </summary>
        /// <param name="categoryBuilder">Aggregated category that receives the item.</param>
        /// <param name="module">Module that provided the item.</param>
        /// <param name="category">Bridge category the item was reported under.</param>
        /// <param name="item">Item payload to merge.</param>
        private static void MergeItem(
            CategoryBuilder categoryBuilder,
            ModuleConfig module,
            ModuleDownloadCategoryPayload category,
            ModuleDownloadItemPayload item)
        {
            // The first provider creates the item; later providers only fill in missing fields.
            if (!categoryBuilder.Items.TryGetValue(item.ResourceKey, out var itemBuilder))
            {
                itemBuilder = new ItemBuilder(item.ResourceKey, category.Id, categoryBuilder.GroupKey, item);
                categoryBuilder.Items[item.ResourceKey] = itemBuilder;
            }
            else
            {
                itemBuilder.Merge(item);
            }

            // Remember which module and category supplied the item so details can be requested from it later.
            itemBuilder.Sources.Add(new DownloadCatalogItemSource
            {
                ModuleId = module.Id,
                ModuleName = module.Name,
                ModuleSourcePath = module.SourcePath,
                CategoryId = string.IsNullOrWhiteSpace(item.CategoryId) ? category.Id : item.CategoryId,
                Details = item.Details ?? [],
                Tags = item.Tags ?? []
            });
        }

        /// <summary>
        /// Builds a minimal fallback detail payload when the module does not implement describe_item.
        /// </summary>
        private DownloadCatalogItemDetail BuildFallbackDetail(DownloadCatalogItem item)
        {
            // The fallback path still exposes one installable variant when describe_item is unavailable.
            var variantKey = !string.IsNullOrWhiteSpace(item.DefaultVariantResourceKey)
                ? item.DefaultVariantResourceKey
                : item.ResourceKey;
            var persistedState = _stateStore.GetResourceState(variantKey);

            return new DownloadCatalogItemDetail
            {
                ResourceKey = item.ResourceKey,
                CategoryId = item.CategoryId,
                GroupKey = item.GroupKey,
                Title = item.Title,
                Summary = item.Summary,
                Provider = item.Provider,
                Version = item.Version,
                HomepageUrl = item.HomepageUrl,
                Details = [.. item.Details],
                Tags = [.. item.Tags],
                DefaultVariantResourceKey = variantKey,
                Variants =
                [
                    new DownloadCatalogVariant
                    {
                        ResourceKey = variantKey,
                        Title = item.Title,
                        Summary = item.Summary,
                        HomepageUrl = item.HomepageUrl,
                        SortOrder = 0,
                        Installed = persistedState?.Installed == true
                    }
                ]
            };
        }

        /// <summary>
        /// Returns the state key that best represents installation for one grouped item.
        /// An item with at most one variant and a known default variant is installed through that variant, so its
        /// key is used; otherwise the item's own resource key is the state key.
        /// </summary>
        private static string GetPrimaryStateKey(ItemBuilder itemBuilder)
        {
            if (itemBuilder.VariantCount <= 1 && !string.IsNullOrWhiteSpace(itemBuilder.DefaultVariantResourceKey))
            {
                return itemBuilder.DefaultVariantResourceKey;
            }

            return itemBuilder.ResourceKey;
        }


        // Field merging

        /// <summary>
        /// Merges display fields collected from several sources. Keeps module order and collapses only
        /// identical fields after their resources are resolved.
        /// </summary>
        /// <param name="fields">Detail or tag fields collected from all module sources, in module order.</param>
        private static List<ModuleDownloadField> MergeFields(IEnumerable<ModuleDownloadField> fields) =>
            fields.DistinctBy(field => (field.Text, field.BackgroundColor, field.TextColor,
                field.ShowInCatalog, field.Image?.Key)).ToList();


        // Category builder

        /// <summary>
        /// Builds one aggregated category while bridge payloads are being merged.
        /// </summary>
        private sealed class CategoryBuilder
        {
            // Initialization

            /// <summary>
            /// Seeds one aggregate category from the first payload.
            /// </summary>
            public CategoryBuilder(string groupKey, ModuleDownloadCategoryPayload category)
            {
                GroupKey = groupKey;
                Title = category.Title;
                Description = category.Description;
                SortOrder = category.SortOrder;
            }


            // Properties

            /// <summary>
            /// Key shared by equivalent provider categories; identifies the merged category.
            /// </summary>
            public string GroupKey { get; }

            /// <summary>
            /// Display title; the first non-empty title of any provider wins.
            /// </summary>
            public string Title { get; private set; }

            /// <summary>
            /// Display description; the first non-empty description of any provider wins.
            /// </summary>
            public string Description { get; private set; }

            /// <summary>
            /// Position of the category in the catalog; the first non-zero value of any provider wins.
            /// </summary>
            public int SortOrder { get; private set; }

            /// <summary>
            /// Merged filters of the category by filter key.
            /// </summary>
            public Dictionary<string, FilterBuilder> Filters { get; } = new(StringComparer.OrdinalIgnoreCase);

            /// <summary>
            /// Merged items of the category by resource key.
            /// </summary>
            public Dictionary<string, ItemBuilder> Items { get; } = new(StringComparer.OrdinalIgnoreCase);


            // Merging

            /// <summary>
            /// Merges missing category metadata from a later payload.
            /// </summary>
            public void Merge(ModuleDownloadCategoryPayload category)
            {
                if (string.IsNullOrWhiteSpace(Title) && !string.IsNullOrWhiteSpace(category.Title))
                {
                    Title = category.Title;
                }

                if (string.IsNullOrWhiteSpace(Description) && !string.IsNullOrWhiteSpace(category.Description))
                {
                    Description = category.Description;
                }

                if (SortOrder == 0 && category.SortOrder != 0)
                {
                    SortOrder = category.SortOrder;
                }
            }

            /// <summary>
            /// Merges or creates one filter inside the category.
            /// </summary>
            public void MergeFilter(ModuleDownloadFilterPayload filter)
            {
                if (!Filters.TryGetValue(filter.Key, out var existing))
                {
                    Filters[filter.Key] = new FilterBuilder(filter);
                    return;
                }

                existing.Merge(filter);
            }


            // Materialization

            /// <summary>
            /// Materializes the final category model with filters and items sorted by order, then title.
            /// </summary>
            /// <param name="stateStore">Persisted state used to mark items as installed.</param>
            public DownloadCatalogCategory ToCategory(DownloadStateStore stateStore)
            {
                return new DownloadCatalogCategory
                {
                    GroupKey = GroupKey,
                    Title = Title,
                    Description = Description,
                    SortOrder = SortOrder,
                    Filters = Filters.Values
                        .Select(filter => filter.ToFilter())
                        .OrderBy(filter => filter.SortOrder)
                        .ThenBy(filter => filter.Title, StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                    Items = Items.Values
                        .Select(item => item.ToItem(stateStore))
                        .OrderBy(item => item.SortOrder)
                        .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
                        .ToList()
                };
            }
        }


        // Filter builder

        /// <summary>
        /// Builds one merged category filter option while bridge payloads are being merged.
        /// </summary>
        private sealed class FilterBuilder
        {
            // Initialization

            /// <summary>
            /// Seeds one aggregate filter from the first payload.
            /// </summary>
            public FilterBuilder(ModuleDownloadFilterPayload filter)
            {
                Key = filter.Key;
                Title = filter.Title;
                Kind = filter.Kind;
                Selected = filter.Selected;
                SortOrder = filter.SortOrder;
            }


            // Properties

            /// <summary>
            /// Filter key that identifies the option across providers.
            /// </summary>
            public string Key { get; }

            /// <summary>
            /// Display title; the first non-empty title of any provider wins.
            /// </summary>
            public string Title { get; private set; }

            /// <summary>
            /// Filter kind (for example <c>sort</c>, which is single-select); the first non-empty kind of any provider wins.
            /// </summary>
            public string Kind { get; private set; }

            /// <summary>
            /// Whether any provider reports the filter as selected.
            /// </summary>
            public bool Selected { get; private set; }

            /// <summary>
            /// Position of the filter; the first non-zero value of any provider wins.
            /// </summary>
            public int SortOrder { get; private set; }


            // Merging

            /// <summary>
            /// Merges missing metadata and preserves any selected state.
            /// </summary>
            public void Merge(ModuleDownloadFilterPayload filter)
            {
                if (string.IsNullOrWhiteSpace(Title) && !string.IsNullOrWhiteSpace(filter.Title))
                {
                    Title = filter.Title;
                }

                if (string.IsNullOrWhiteSpace(Kind) && !string.IsNullOrWhiteSpace(filter.Kind))
                {
                    Kind = filter.Kind;
                }

                Selected |= filter.Selected;

                if (SortOrder == 0 && filter.SortOrder != 0)
                {
                    SortOrder = filter.SortOrder;
                }
            }


            // Materialization

            /// <summary>
            /// Materializes the final filter model.
            /// </summary>
            public DownloadCatalogFilter ToFilter()
            {
                return new DownloadCatalogFilter
                {
                    Key = Key,
                    Title = Title,
                    Kind = Kind,
                    Selected = Selected,
                    SortOrder = SortOrder
                };
            }
        }


        // Item builder

        /// <summary>
        /// Builds one aggregated grouped item while bridge payloads are being merged.
        /// </summary>
        private sealed class ItemBuilder
        {
            // Initialization

            /// <summary>
            /// Seeds one aggregate item from the first payload.
            /// </summary>
            /// <param name="resourceKey">Resource key that identifies the item across providers.</param>
            /// <param name="categoryId">Bridge category id used when the payload names none.</param>
            /// <param name="groupKey">Group key of the merged category the item belongs to.</param>
            /// <param name="item">First payload that reported the item.</param>
            public ItemBuilder(
                string resourceKey,
                string categoryId,
                string groupKey,
                ModuleDownloadItemPayload item)
            {
                ResourceKey = resourceKey;
                CategoryId = string.IsNullOrWhiteSpace(item.CategoryId) ? categoryId : item.CategoryId;
                GroupKey = groupKey;
                Title = item.Title;
                Summary = item.Summary;
                Provider = item.Provider;
                Version = item.Version;
                HomepageUrl = item.HomepageUrl;
                VariantCount = item.VariantCount;
                DefaultVariantResourceKey = item.DefaultVariantResourceKey;
                SortOrder = item.SortOrder;
            }


            // Properties

            /// <summary>
            /// Resource key that identifies the item across providers.
            /// </summary>
            public string ResourceKey { get; }

            /// <summary>
            /// Bridge category id the first provider reported the item under.
            /// </summary>
            public string CategoryId { get; }

            /// <summary>
            /// Group key of the merged category the item is listed in.
            /// </summary>
            public string GroupKey { get; }

            /// <summary>
            /// Display title; the first non-empty title of any provider wins.
            /// </summary>
            public string Title { get; private set; }

            /// <summary>
            /// Short description; the first non-empty summary of any provider wins.
            /// </summary>
            public string Summary { get; private set; }

            /// <summary>
            /// Name of the publisher or provider; the first non-empty value of any provider wins.
            /// </summary>
            public string Provider { get; private set; }

            /// <summary>
            /// Version label; the first non-empty value of any provider wins.
            /// </summary>
            public string Version { get; private set; }

            /// <summary>
            /// Project page URL; the first non-empty value of any provider wins.
            /// </summary>
            public string HomepageUrl { get; private set; }

            /// <summary>
            /// Number of installable variants; the first positive count of any provider wins.
            /// </summary>
            public int VariantCount { get; private set; }

            /// <summary>
            /// Resource key of the variant installed by default; the first non-empty value of any provider wins.
            /// </summary>
            public string DefaultVariantResourceKey { get; private set; }

            /// <summary>
            /// Position of the item in the category; the first non-zero value of any provider wins.
            /// </summary>
            public int SortOrder { get; private set; }

            /// <summary>
            /// Every module and category that reported this item.
            /// </summary>
            public List<DownloadCatalogItemSource> Sources { get; } = [];


            // Merging

            /// <summary>
            /// Merges non-empty item fields while preserving the earliest stable identity.
            /// </summary>
            public void Merge(ModuleDownloadItemPayload item)
            {
                // The first provider's values win; a later payload only fills fields that are still empty.
                if (string.IsNullOrWhiteSpace(Title) && !string.IsNullOrWhiteSpace(item.Title))
                {
                    Title = item.Title;
                }

                if (string.IsNullOrWhiteSpace(Summary) && !string.IsNullOrWhiteSpace(item.Summary))
                {
                    Summary = item.Summary;
                }

                if (string.IsNullOrWhiteSpace(Provider) && !string.IsNullOrWhiteSpace(item.Provider))
                {
                    Provider = item.Provider;
                }

                if (string.IsNullOrWhiteSpace(Version) && !string.IsNullOrWhiteSpace(item.Version))
                {
                    Version = item.Version;
                }

                if (string.IsNullOrWhiteSpace(HomepageUrl) && !string.IsNullOrWhiteSpace(item.HomepageUrl))
                {
                    HomepageUrl = item.HomepageUrl;
                }

                if (VariantCount == 0 && item.VariantCount > 0)
                {
                    VariantCount = item.VariantCount;
                }

                if (string.IsNullOrWhiteSpace(DefaultVariantResourceKey) && !string.IsNullOrWhiteSpace(item.DefaultVariantResourceKey))
                {
                    DefaultVariantResourceKey = item.DefaultVariantResourceKey;
                }

                if (SortOrder == 0 && item.SortOrder != 0)
                {
                    SortOrder = item.SortOrder;
                }
            }


            // Materialization

            /// <summary>
            /// Materializes the final grouped item model.
            /// </summary>
            /// <param name="stateStore">Persisted state used to mark the item as installed.</param>
            public DownloadCatalogItem ToItem(DownloadStateStore stateStore)
            {
                // Order the sources deterministically, then build the item from the merged fields and installation state.
                var persistedState = stateStore.GetResourceState(GetPrimaryStateKey(this));
                var sources = Sources.OrderBy(source => source.ModuleSourcePath, StringComparer.Ordinal)
                    .ThenBy(source => source.CategoryId, StringComparer.Ordinal).ToList();

                return new DownloadCatalogItem
                {
                    ResourceKey = ResourceKey,
                    CategoryId = CategoryId,
                    GroupKey = GroupKey,
                    Title = Title,
                    Summary = Summary,
                    Provider = Provider,
                    Version = Version,
                    HomepageUrl = HomepageUrl,
                    Details = MergeFields(sources.SelectMany(source => source.Details)),
                    Tags = MergeFields(sources.SelectMany(source => source.Tags)),
                    VariantCount = VariantCount,
                    DefaultVariantResourceKey = DefaultVariantResourceKey,
                    SortOrder = SortOrder,
                    Installed = persistedState?.Installed == true,
                    InstalledVersion = persistedState?.InstalledVersion ?? string.Empty,
                    // Collapse equivalent sources so the same module-category pair is listed once.
                    Sources = sources
                        .GroupBy(source => $"{source.ModuleSourcePath}|{source.CategoryId}", StringComparer.OrdinalIgnoreCase)
                        .Select(group => group.First())
                        .ToList()
                };
            }
        }


        // Item detail builder

        /// <summary>
        /// Merges detailed item payloads returned by one or more module bridges.
        /// </summary>
        private sealed class ItemDetailBuilder
        {
            // Initialization

            /// <summary>
            /// Seeds one aggregate detail model from the selected list item.
            /// </summary>
            /// <param name="item">List item whose list data is the starting point; detail payloads fill in what is missing.</param>
            public ItemDetailBuilder(DownloadCatalogItem item)
            {
                ResourceKey = item.ResourceKey;
                CategoryId = item.CategoryId;
                GroupKey = item.GroupKey;
                Title = item.Title;
                Summary = item.Summary;
                Provider = item.Provider;
                Version = item.Version;
                HomepageUrl = item.HomepageUrl;
                _sources = item.Sources;
                DefaultVariantResourceKey = item.DefaultVariantResourceKey;
            }


            // Properties and state

            /// <summary>
            /// Resource key that identifies the item across providers.
            /// </summary>
            public string ResourceKey { get; }

            /// <summary>
            /// Bridge category id; starts from the list item and is completed from detail payloads when empty.
            /// </summary>
            public string CategoryId { get; private set; }

            /// <summary>
            /// Group key of the merged category; starts from the list item and is completed from detail payloads when empty.
            /// </summary>
            public string GroupKey { get; private set; }

            /// <summary>
            /// Display title; starts from the list item and is completed from detail payloads when empty.
            /// </summary>
            public string Title { get; private set; }

            /// <summary>
            /// Short description; starts from the list item and is completed from detail payloads when empty.
            /// </summary>
            public string Summary { get; private set; }

            /// <summary>
            /// Name of the publisher or provider; starts from the list item and is completed from detail payloads when empty.
            /// </summary>
            public string Provider { get; private set; }

            /// <summary>
            /// Version label; starts from the list item and is completed from detail payloads when empty.
            /// </summary>
            public string Version { get; private set; }

            /// <summary>
            /// Project page URL; starts from the list item and is completed from detail payloads when empty.
            /// </summary>
            public string HomepageUrl { get; private set; }

            /// <summary>
            /// Sources of the list item; their list-level details and tags are used when a source sends no detail payload.
            /// </summary>
            private readonly List<DownloadCatalogItemSource> _sources;

            /// <summary>
            /// Detail payload received from each source that answered.
            /// </summary>
            private readonly Dictionary<DownloadCatalogItemSource, ModuleDownloadItemDetailPayload> _sourceDetails = new();

            /// <summary>
            /// Resource key of the variant installed by default; starts from the list item and is completed from detail payloads when empty.
            /// </summary>
            public string DefaultVariantResourceKey { get; private set; }

            /// <summary>
            /// Merged installable variants by resource key.
            /// </summary>
            public Dictionary<string, VariantBuilder> Variants { get; } = new(StringComparer.OrdinalIgnoreCase);

            /// <summary>
            /// Merged information blocks by block key.
            /// </summary>
            public Dictionary<string, BlockBuilder> Blocks { get; } = new(StringComparer.OrdinalIgnoreCase);


            // Merging

            /// <summary>
            /// Merges detail payloads from every contributing module source.
            /// </summary>
            /// <param name="detail">Normalized detail payload returned by one module.</param>
            /// <param name="source">Module source that returned the payload.</param>
            public void Merge(ModuleDownloadItemDetailPayload detail, DownloadCatalogItemSource source)
            {
                // Fill identity and display fields that the list item left empty.
                if (string.IsNullOrWhiteSpace(CategoryId) && !string.IsNullOrWhiteSpace(detail.CategoryId))
                {
                    CategoryId = detail.CategoryId;
                }

                if (string.IsNullOrWhiteSpace(GroupKey) && !string.IsNullOrWhiteSpace(detail.GroupKey))
                {
                    GroupKey = detail.GroupKey;
                }

                if (string.IsNullOrWhiteSpace(Title) && !string.IsNullOrWhiteSpace(detail.Title))
                {
                    Title = detail.Title;
                }

                if (string.IsNullOrWhiteSpace(Summary) && !string.IsNullOrWhiteSpace(detail.Summary))
                {
                    Summary = detail.Summary;
                }

                if (string.IsNullOrWhiteSpace(Provider) && !string.IsNullOrWhiteSpace(detail.Provider))
                {
                    Provider = detail.Provider;
                }

                if (string.IsNullOrWhiteSpace(Version) && !string.IsNullOrWhiteSpace(detail.Version))
                {
                    Version = detail.Version;
                }

                if (string.IsNullOrWhiteSpace(HomepageUrl) && !string.IsNullOrWhiteSpace(detail.HomepageUrl))
                {
                    HomepageUrl = detail.HomepageUrl;
                }

                if (string.IsNullOrWhiteSpace(DefaultVariantResourceKey) && !string.IsNullOrWhiteSpace(detail.DefaultVariantResourceKey))
                {
                    DefaultVariantResourceKey = detail.DefaultVariantResourceKey;
                }

                // Keep the payload per source so its details and tags can be resolved at the end.
                _sourceDetails[source] = detail;

                // Variants merge by resource key so multiple modules can enrich the same option.
                foreach (var variant in detail.Variants)
                {
                    if (string.IsNullOrWhiteSpace(variant.ResourceKey))
                    {
                        continue;
                    }

                    if (!Variants.TryGetValue(variant.ResourceKey, out var existingVariant))
                    {
                        existingVariant = new VariantBuilder(variant);
                        Variants[variant.ResourceKey] = existingVariant;
                    }
                    else
                    {
                        existingVariant.Merge(variant);
                    }
                }

                // Blocks merge by stable id first, then by title when the source omits an id.
                foreach (var block in detail.Blocks)
                {
                    var blockKey = !string.IsNullOrWhiteSpace(block.Id) ? block.Id : block.Title;
                    if (string.IsNullOrWhiteSpace(blockKey))
                    {
                        continue;
                    }

                    if (!Blocks.TryGetValue(blockKey, out var existingBlock))
                    {
                        existingBlock = new BlockBuilder(blockKey, block);
                        Blocks[blockKey] = existingBlock;
                    }
                    else
                    {
                        existingBlock.Merge(block);
                    }
                }
            }


            // Materialization

            /// <summary>
            /// Materializes the final detail model.
            /// </summary>
            /// <param name="stateStore">Persisted state used to mark variants as installed.</param>
            public DownloadCatalogItemDetail ToDetail(DownloadStateStore stateStore)
            {
                // Missing fields inherit this source's list data; an explicit [] clears it.
                var details = MergeFields(_sources.SelectMany(source => _sourceDetails.GetValueOrDefault(source)?.Details ?? source.Details));
                var tags = MergeFields(_sources.SelectMany(source => _sourceDetails.GetValueOrDefault(source)?.Tags ?? source.Tags));
                var variants = Variants.Values
                    .Select(variant => variant.ToVariant(stateStore))
                    .OrderBy(variant => variant.SortOrder)
                    .ThenBy(variant => variant.Title, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // Keep one synthetic variant so install flows still work when no explicit variants were returned.
                if (variants.Count == 0)
                {
                    var fallbackState = stateStore.GetResourceState(!string.IsNullOrWhiteSpace(DefaultVariantResourceKey) ? DefaultVariantResourceKey : ResourceKey);
                    variants.Add(new DownloadCatalogVariant
                    {
                        ResourceKey = !string.IsNullOrWhiteSpace(DefaultVariantResourceKey) ? DefaultVariantResourceKey : ResourceKey,
                        Title = Title,
                        Summary = Summary,
                        HomepageUrl = HomepageUrl,
                        SortOrder = 0,
                        Installed = fallbackState?.Installed == true
                    });
                }

                // Without an explicit default, the first variant in display order becomes the default.
                if (string.IsNullOrWhiteSpace(DefaultVariantResourceKey))
                {
                    DefaultVariantResourceKey = variants[0].ResourceKey;
                }

                return new DownloadCatalogItemDetail
                {
                    ResourceKey = ResourceKey,
                    CategoryId = CategoryId,
                    GroupKey = GroupKey,
                    Title = Title,
                    Summary = Summary,
                    Provider = Provider,
                    Version = Version,
                    HomepageUrl = HomepageUrl,
                    Details = details,
                    Tags = tags,
                    DefaultVariantResourceKey = DefaultVariantResourceKey,
                    Variants = variants,
                    Blocks = Blocks.Values
                        .Select(block => block.ToBlock())
                        .OrderBy(block => block.SortOrder)
                        .ThenBy(block => block.Title, StringComparer.OrdinalIgnoreCase)
                        .ToList()
                };
            }
        }


        // Variant builder

        /// <summary>
        /// Builds one merged variant payload.
        /// </summary>
        private sealed class VariantBuilder
        {
            // Initialization

            /// <summary>
            /// Seeds one aggregate variant from the first payload.
            /// </summary>
            public VariantBuilder(ModuleDownloadVariantPayload variant)
            {
                ResourceKey = variant.ResourceKey;
                Title = variant.Title;
                Summary = variant.Summary;
                HasSize = variant.HasSize && variant.Size >= 0;
                Size = HasSize ? variant.Size : 0;
                HomepageUrl = variant.HomepageUrl;
                SortOrder = variant.SortOrder;
            }


            // Properties

            /// <summary>
            /// Resource key that identifies the variant and its installation state.
            /// </summary>
            public string ResourceKey { get; }

            /// <summary>
            /// Display title; the first non-empty title of any provider wins.
            /// </summary>
            public string Title { get; private set; }

            /// <summary>
            /// Short description; the first non-empty summary of any provider wins.
            /// </summary>
            public string Summary { get; private set; }

            /// <summary>
            /// Download size in bytes; meaningful only when <see cref="HasSize"/> is true.
            /// </summary>
            public long Size { get; private set; }

            /// <summary>
            /// Whether a provider reported a valid, non-negative size.
            /// </summary>
            public bool HasSize { get; private set; }

            /// <summary>
            /// Project page URL; the first non-empty value of any provider wins.
            /// </summary>
            public string HomepageUrl { get; private set; }

            /// <summary>
            /// Position of the variant in the list; the first non-zero value of any provider wins.
            /// </summary>
            public int SortOrder { get; private set; }


            // Merging

            /// <summary>
            /// Merges non-empty fields from later payloads.
            /// </summary>
            public void Merge(ModuleDownloadVariantPayload variant)
            {
                // The first provider's values win; a later payload only fills fields that are still empty.
                if (string.IsNullOrWhiteSpace(Title) && !string.IsNullOrWhiteSpace(variant.Title))
                {
                    Title = variant.Title;
                }

                if (string.IsNullOrWhiteSpace(Summary) && !string.IsNullOrWhiteSpace(variant.Summary))
                {
                    Summary = variant.Summary;
                }

                if (!HasSize && variant.HasSize && variant.Size >= 0)
                {
                    Size = variant.Size;
                    HasSize = true;
                }

                if (string.IsNullOrWhiteSpace(HomepageUrl) && !string.IsNullOrWhiteSpace(variant.HomepageUrl))
                {
                    HomepageUrl = variant.HomepageUrl;
                }

                if (SortOrder == 0 && variant.SortOrder != 0)
                {
                    SortOrder = variant.SortOrder;
                }
            }


            // Materialization

            /// <summary>
            /// Materializes the final variant model.
            /// </summary>
            /// <param name="stateStore">Persisted state used to mark the variant as installed.</param>
            public DownloadCatalogVariant ToVariant(DownloadStateStore stateStore)
            {
                var persistedState = stateStore.GetResourceState(ResourceKey);
                return new DownloadCatalogVariant
                {
                    ResourceKey = ResourceKey,
                    Title = Title,
                    Summary = Summary,
                    Size = Size,
                    HasSize = HasSize,
                    HomepageUrl = HomepageUrl,
                    SortOrder = SortOrder,
                    Installed = persistedState?.Installed == true
                };
            }
        }


        // Block builder

        /// <summary>
        /// Builds one merged details block payload.
        /// </summary>
        private sealed class BlockBuilder
        {
            // Initialization

            /// <summary>
            /// Seeds one aggregate info block from the first payload.
            /// </summary>
            /// <param name="key">Block id, or the block title when the payload has no id.</param>
            /// <param name="block">First payload that described the block.</param>
            public BlockBuilder(string key, ModuleDownloadInfoBlockPayload block)
            {
                Key = key;
                Title = block.Title;
                Format = block.Format;
                Content = block.Content;
                ContentUrl = block.ContentUrl;
                SourceUrl = block.SourceUrl;
                SortOrder = block.SortOrder;
            }


            // Properties

            /// <summary>
            /// Key that identifies the block across providers.
            /// </summary>
            public string Key { get; }

            /// <summary>
            /// Heading shown above the block; the first non-empty title of any provider wins.
            /// </summary>
            public string Title { get; private set; }

            /// <summary>
            /// Content format such as <c>text</c> or <c>markdown</c>; the first non-empty value of any provider wins.
            /// </summary>
            public string Format { get; private set; }

            /// <summary>
            /// Inline content; the first non-empty value of any provider wins.
            /// </summary>
            public string Content { get; private set; }

            /// <summary>
            /// URL the content is loaded from instead of inline content; the first non-empty value of any provider wins.
            /// </summary>
            public string ContentUrl { get; private set; }

            /// <summary>
            /// Base URL for relative links inside the content; the first non-empty value of any provider wins.
            /// </summary>
            public string SourceUrl { get; private set; }

            /// <summary>
            /// Position of the block; the first non-zero value of any provider wins.
            /// </summary>
            public int SortOrder { get; private set; }


            // Merging

            /// <summary>
            /// Merges missing block metadata from later payloads.
            /// </summary>
            public void Merge(ModuleDownloadInfoBlockPayload block)
            {
                // The first provider's values win; a later payload only fills fields that are still empty.
                if (string.IsNullOrWhiteSpace(Title) && !string.IsNullOrWhiteSpace(block.Title))
                {
                    Title = block.Title;
                }

                if (string.IsNullOrWhiteSpace(Format) && !string.IsNullOrWhiteSpace(block.Format))
                {
                    Format = block.Format;
                }

                if (string.IsNullOrWhiteSpace(Content) && !string.IsNullOrWhiteSpace(block.Content))
                {
                    Content = block.Content;
                }

                if (string.IsNullOrWhiteSpace(ContentUrl) && !string.IsNullOrWhiteSpace(block.ContentUrl))
                {
                    ContentUrl = block.ContentUrl;
                }

                if (string.IsNullOrWhiteSpace(SourceUrl) && !string.IsNullOrWhiteSpace(block.SourceUrl))
                {
                    SourceUrl = block.SourceUrl;
                }

                if (SortOrder == 0 && block.SortOrder != 0)
                {
                    SortOrder = block.SortOrder;
                }
            }


            // Materialization

            /// <summary>
            /// Materializes the final info block model.
            /// </summary>
            public DownloadCatalogInfoBlock ToBlock()
            {
                return new DownloadCatalogInfoBlock
                {
                    Id = Key,
                    Title = Title,
                    Format = Format,
                    Content = Content,
                    ContentUrl = ContentUrl,
                    SourceUrl = SourceUrl,
                    SortOrder = SortOrder
                };
            }
        }
    }
}
