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
        private const int MaxConcurrentBridgeRequests = 4;

        private readonly ModuleInstaller _moduleInstaller;
        private readonly ModuleDownloadBridge _bridge;
        private readonly DownloadStateStore _stateStore;
        private readonly ILogger<DownloadCatalog> _logger;

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
        /// Queries all installed module bridges and returns one merged download catalog snapshot.
        /// </summary>
        public async Task<DownloadCatalogSnapshot> LoadCatalogAsync(
            string? queryText = null,
            IReadOnlyCollection<string>? filters = null,
            bool preferCached = false,
            bool forceRefresh = false,
            CancellationToken ct = default)
        {
            // Discover only modules that actually expose the downloads bridge contract.
            var modules = await _moduleInstaller.DiscoverModulesAsync();
            var bridgeModules = modules
                .Where(module => module.DownloadsBridge?.IsConfigured == true)
                .ToList();

            var warnings = new List<string>();
            var categoryBuilders = new Dictionary<string, CategoryBuilder>(StringComparer.OrdinalIgnoreCase);
            var mergeLock = new object();
            using var bridgeThrottle = new SemaphoreSlim(MaxConcurrentBridgeRequests);

            var loadTasks = bridgeModules.Select(async module =>
            {
                await bridgeThrottle.WaitAsync(ct);
                try
                {
                    await LoadModuleCatalogAsync(
                        module,
                        queryText,
                        filters,
                        preferCached,
                        forceRefresh,
                        warnings,
                        categoryBuilders,
                        mergeLock,
                        ct);
                }
                finally
                {
                    bridgeThrottle.Release();
                }
            });

            await Task.WhenAll(loadTasks);

            // Convert builders only after every module has contributed its portion of the catalog.
            return new DownloadCatalogSnapshot
            {
                Warnings = warnings,
                Categories = categoryBuilders.Values
                    .Select(builder => builder.ToCategory(_stateStore))
                    .OrderBy(category => category.SortOrder)
                    .ThenBy(category => category.Title, StringComparer.OrdinalIgnoreCase)
                    .ToList()
            };
        }

        /// <summary>
        /// Loads and merges the bridge catalog exposed by one module.
        /// </summary>
        private async Task LoadModuleCatalogAsync(
            ModuleConfig module,
            string? queryText,
            IReadOnlyCollection<string>? filters,
            bool preferCached,
            bool forceRefresh,
            List<string> warnings,
            Dictionary<string, CategoryBuilder> categoryBuilders,
            object mergeLock,
            CancellationToken ct)
        {
            List<ModuleDownloadCategoryPayload> categories;
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
                _logger.LogWarning(ex, "Failed to load download categories for module {ModuleId}.", module.Id);
                AddWarning(warnings, mergeLock, $"{module.Name}: download categories could not be loaded.");
                return;
            }

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

                ModuleDownloadBridgeResponse itemResponse;
                try
                {
                    itemResponse = await _bridge.GetItemsAsync(module, category.Id, queryText, filters, preferCached, forceRefresh, ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load download items for module {ModuleId} category {CategoryId}.", module.Id, category.Id);
                    AddWarning(warnings, mergeLock, $"{module.Name}: {category.Title} could not be loaded.");
                    continue;
                }

                lock (mergeLock)
                {
                    var categoryBuilder = GetOrCreateCategoryBuilder(categoryBuilders, groupKey, category);

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
                    }
                }
            }
        }

        /// <summary>
        /// Adds one warning while bridge catalog loading runs in parallel.
        /// </summary>
        private static void AddWarning(List<string> warnings, object mergeLock, string warning)
        {
            lock (mergeLock)
            {
                warnings.Add(warning);
            }
        }

        /// <summary>
        /// Loads the detailed payload for one selected catalog item.
        /// </summary>
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

            foreach (var source in item.Sources)
            {
                ct.ThrowIfCancellationRequested();

                var module = await _moduleInstaller.LoadModuleConfig(source.ModuleSourcePath);
                if (module == null)
                {
                    continue;
                }

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
                    builder.Merge(detail);
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

            return hadAnyDetail
                ? builder.ToDetail(_stateStore)
                : BuildFallbackDetail(item);
        }


        // Builder helpers

        /// <summary>
        /// Returns the existing category builder or creates one from the current bridge category.
        /// </summary>
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
        /// </summary>
        private static void MergeItem(
            CategoryBuilder categoryBuilder,
            ModuleConfig module,
            ModuleDownloadCategoryPayload category,
            ModuleDownloadItemPayload item)
        {
            if (!categoryBuilder.Items.TryGetValue(item.ResourceKey, out var itemBuilder))
            {
                itemBuilder = new ItemBuilder(item.ResourceKey, category.Id, categoryBuilder.GroupKey, item);
                categoryBuilder.Items[item.ResourceKey] = itemBuilder;
            }
            else
            {
                itemBuilder.Merge(item);
            }

            itemBuilder.Sources.Add(new DownloadCatalogItemSource
            {
                ModuleId = module.Id,
                ModuleName = module.Name,
                ModuleSourcePath = module.SourcePath,
                CategoryId = string.IsNullOrWhiteSpace(item.CategoryId) ? category.Id : item.CategoryId
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
                Detail = item.Detail,
                Tags = [.. item.Tags],
                DefaultVariantResourceKey = variantKey,
                Variants =
                [
                    new DownloadCatalogVariant
                    {
                        ResourceKey = variantKey,
                        Title = item.Title,
                        Summary = item.Summary,
                        Version = item.Version,
                        Detail = item.Detail,
                        HomepageUrl = item.HomepageUrl,
                        Tags = [.. item.Tags],
                        SortOrder = 0,
                        Installed = persistedState?.Installed == true,
                        InstalledVersion = persistedState?.InstalledVersion ?? string.Empty
                    }
                ]
            };
        }

        /// <summary>
        /// Returns the state key that best represents installation for one grouped item.
        /// </summary>
        private static string GetPrimaryStateKey(ItemBuilder itemBuilder)
        {
            if (itemBuilder.VariantCount <= 1 && !string.IsNullOrWhiteSpace(itemBuilder.DefaultVariantResourceKey))
            {
                return itemBuilder.DefaultVariantResourceKey;
            }

            return itemBuilder.ResourceKey;
        }


        // Merge builders


        /// <summary>
        /// Builds one aggregated category while bridge payloads are being merged.
        /// </summary>
        private sealed class CategoryBuilder
        {
            // Category builder initialization

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

            public string GroupKey { get; }
            public string Title { get; private set; }
            public string Description { get; private set; }
            public int SortOrder { get; private set; }
            public Dictionary<string, FilterBuilder> Filters { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, ItemBuilder> Items { get; } = new(StringComparer.OrdinalIgnoreCase);


            // Category builder merging

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


            /// <summary>
            /// Materializes the final category model.
            /// </summary>
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


        /// <summary>
        /// Builds one merged category filter option while bridge payloads are being merged.
        /// </summary>
        private sealed class FilterBuilder
        {
            // Filter builder initialization

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

            public string Key { get; }
            public string Title { get; private set; }
            public string Kind { get; private set; }
            public bool Selected { get; private set; }
            public int SortOrder { get; private set; }


            // Filter builder merging

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


        /// <summary>
        /// Builds one aggregated grouped item while bridge payloads are being merged.
        /// </summary>
        private sealed class ItemBuilder
        {
            // Item builder initialization

            /// <summary>
            /// Seeds one aggregate item from the first payload.
            /// </summary>
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
                Detail = item.Detail;
                VariantCount = item.VariantCount;
                DefaultVariantResourceKey = item.DefaultVariantResourceKey;
                SortOrder = item.SortOrder;
                Tags = item.Tags.ToHashSet(StringComparer.OrdinalIgnoreCase);
            }

            public string ResourceKey { get; }
            public string CategoryId { get; }
            public string GroupKey { get; }
            public string Title { get; private set; }
            public string Summary { get; private set; }
            public string Provider { get; private set; }
            public string Version { get; private set; }
            public string HomepageUrl { get; private set; }
            public string Detail { get; private set; }
            public int VariantCount { get; private set; }
            public string DefaultVariantResourceKey { get; private set; }
            public int SortOrder { get; private set; }
            public HashSet<string> Tags { get; }
            public List<DownloadCatalogItemSource> Sources { get; } = [];


            // Item builder merging

            /// <summary>
            /// Merges non-empty item fields while preserving the earliest stable identity.
            /// </summary>
            public void Merge(ModuleDownloadItemPayload item)
            {
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

                if (string.IsNullOrWhiteSpace(Detail) && !string.IsNullOrWhiteSpace(item.Detail))
                {
                    Detail = item.Detail;
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

                foreach (var tag in item.Tags)
                {
                    Tags.Add(tag);
                }
            }


            /// <summary>
            /// Materializes the final grouped item model.
            /// </summary>
            public DownloadCatalogItem ToItem(DownloadStateStore stateStore)
            {
                var persistedState = stateStore.GetResourceState(GetPrimaryStateKey(this));

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
                    Detail = Detail,
                    Tags = Tags.OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase).ToList(),
                    VariantCount = VariantCount,
                    DefaultVariantResourceKey = DefaultVariantResourceKey,
                    SortOrder = SortOrder,
                    Installed = persistedState?.Installed == true,
                    InstalledVersion = persistedState?.InstalledVersion ?? string.Empty,
                    // Collapse equivalent sources so the same module-category pair is listed once.
                    Sources = Sources
                        .GroupBy(source => $"{source.ModuleSourcePath}|{source.CategoryId}", StringComparer.OrdinalIgnoreCase)
                        .Select(group => group.First())
                        .ToList()
                };
            }
        }


        /// <summary>
        /// Merges detailed item payloads returned by one or more module bridges.
        /// </summary>
        private sealed class ItemDetailBuilder
        {
            // Detail builder initialization

            /// <summary>
            /// Seeds one aggregate detail model from the selected list item.
            /// </summary>
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
                Detail = item.Detail;
                Tags = item.Tags.ToHashSet(StringComparer.OrdinalIgnoreCase);
                DefaultVariantResourceKey = item.DefaultVariantResourceKey;
            }

            public string ResourceKey { get; }
            public string CategoryId { get; private set; }
            public string GroupKey { get; private set; }
            public string Title { get; private set; }
            public string Summary { get; private set; }
            public string Provider { get; private set; }
            public string Version { get; private set; }
            public string HomepageUrl { get; private set; }
            public string Detail { get; private set; }
            public HashSet<string> Tags { get; }
            public string DefaultVariantResourceKey { get; private set; }
            public Dictionary<string, VariantBuilder> Variants { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, BlockBuilder> Blocks { get; } = new(StringComparer.OrdinalIgnoreCase);


            // Detail builder merging

            /// <summary>
            /// Merges detail payloads from every contributing module source.
            /// </summary>
            public void Merge(ModuleDownloadItemDetailPayload detail)
            {
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

                if (string.IsNullOrWhiteSpace(Detail) && !string.IsNullOrWhiteSpace(detail.Detail))
                {
                    Detail = detail.Detail;
                }

                if (string.IsNullOrWhiteSpace(DefaultVariantResourceKey) && !string.IsNullOrWhiteSpace(detail.DefaultVariantResourceKey))
                {
                    DefaultVariantResourceKey = detail.DefaultVariantResourceKey;
                }

                foreach (var tag in detail.Tags)
                {
                    Tags.Add(tag);
                }

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


            /// <summary>
            /// Materializes the final detail model.
            /// </summary>
            public DownloadCatalogItemDetail ToDetail(DownloadStateStore stateStore)
            {
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
                        Version = Version,
                        Detail = Detail,
                        HomepageUrl = HomepageUrl,
                        Tags = Tags.OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase).ToList(),
                        SortOrder = 0,
                        Installed = fallbackState?.Installed == true,
                        InstalledVersion = fallbackState?.InstalledVersion ?? string.Empty
                    });
                }

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
                    Detail = Detail,
                    Tags = Tags.OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase).ToList(),
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


        /// <summary>
        /// Builds one merged variant payload.
        /// </summary>
        private sealed class VariantBuilder
        {
            // Variant builder initialization

            /// <summary>
            /// Seeds one aggregate variant from the first payload.
            /// </summary>
            public VariantBuilder(ModuleDownloadVariantPayload variant)
            {
                ResourceKey = variant.ResourceKey;
                Title = variant.Title;
                Summary = variant.Summary;
                Version = variant.Version;
                Detail = variant.Detail;
                HomepageUrl = variant.HomepageUrl;
                SortOrder = variant.SortOrder;
                Tags = variant.Tags.ToHashSet(StringComparer.OrdinalIgnoreCase);
            }

            public string ResourceKey { get; }
            public string Title { get; private set; }
            public string Summary { get; private set; }
            public string Version { get; private set; }
            public string Detail { get; private set; }
            public string HomepageUrl { get; private set; }
            public int SortOrder { get; private set; }
            public HashSet<string> Tags { get; }


            // Variant builder merging

            /// <summary>
            /// Merges non-empty fields from later payloads.
            /// </summary>
            public void Merge(ModuleDownloadVariantPayload variant)
            {
                if (string.IsNullOrWhiteSpace(Title) && !string.IsNullOrWhiteSpace(variant.Title))
                {
                    Title = variant.Title;
                }

                if (string.IsNullOrWhiteSpace(Summary) && !string.IsNullOrWhiteSpace(variant.Summary))
                {
                    Summary = variant.Summary;
                }

                if (string.IsNullOrWhiteSpace(Version) && !string.IsNullOrWhiteSpace(variant.Version))
                {
                    Version = variant.Version;
                }

                if (string.IsNullOrWhiteSpace(Detail) && !string.IsNullOrWhiteSpace(variant.Detail))
                {
                    Detail = variant.Detail;
                }

                if (string.IsNullOrWhiteSpace(HomepageUrl) && !string.IsNullOrWhiteSpace(variant.HomepageUrl))
                {
                    HomepageUrl = variant.HomepageUrl;
                }

                if (SortOrder == 0 && variant.SortOrder != 0)
                {
                    SortOrder = variant.SortOrder;
                }

                foreach (var tag in variant.Tags)
                {
                    Tags.Add(tag);
                }
            }


            /// <summary>
            /// Materializes the final variant model.
            /// </summary>
            public DownloadCatalogVariant ToVariant(DownloadStateStore stateStore)
            {
                var persistedState = stateStore.GetResourceState(ResourceKey);
                return new DownloadCatalogVariant
                {
                    ResourceKey = ResourceKey,
                    Title = Title,
                    Summary = Summary,
                    Version = Version,
                    Detail = Detail,
                    HomepageUrl = HomepageUrl,
                    Tags = Tags.OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase).ToList(),
                    SortOrder = SortOrder,
                    Installed = persistedState?.Installed == true,
                    InstalledVersion = persistedState?.InstalledVersion ?? string.Empty
                };
            }
        }


        /// <summary>
        /// Builds one merged details block payload.
        /// </summary>
        private sealed class BlockBuilder
        {
            // Block builder initialization

            /// <summary>
            /// Seeds one aggregate info block from the first payload.
            /// </summary>
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

            public string Key { get; }
            public string Title { get; private set; }
            public string Format { get; private set; }
            public string Content { get; private set; }
            public string ContentUrl { get; private set; }
            public string SourceUrl { get; private set; }
            public int SortOrder { get; private set; }


            // Block builder merging

            /// <summary>
            /// Merges missing block metadata from later payloads.
            /// </summary>
            public void Merge(ModuleDownloadInfoBlockPayload block)
            {
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
