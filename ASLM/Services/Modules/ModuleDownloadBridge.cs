// Copyright NEXTGGTECH. Apache License 2.0.

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ASLM.Models;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace ASLM.Services.Modules
{
    /// <summary>
    /// Executes module-declared downloads bridge commands and exchanges JSON requests over stdio.
    /// <para>
    /// A bridge is a command declared in the module manifest. For each request ASLM starts it as a child process,
    /// writes one JSON request to its stdin and reads one JSON response from its stdout. The bridge describes
    /// download categories, items and install or uninstall manifests; this class also validates and caches the
    /// icons and colors that such a response references.
    /// </para>
    /// </summary>
    public class ModuleDownloadBridge
    {
        // Fields and constants

        /// <summary>
        /// Maximum number of characters kept from each of the bridge's stdout and stderr; the rest is discarded.
        /// </summary>
        private const int MaxBridgeOutputCharacters = 2_000_000;

        /// <summary>
        /// Time allowed for one bridge request, including process start and output reading.
        /// </summary>
        private static readonly TimeSpan BridgeRequestTimeout = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Engine service that resolves the engine executable of engine-backed bridges.
        /// </summary>
        private readonly EngineInstaller _engineInstaller;

        /// <summary>
        /// Prepares and resolves the module-specific environment of an engine, such as a virtual environment.
        /// </summary>
        private readonly ModuleEnvironmentResolver _environmentResolver;

        /// <summary>
        /// Module runner that supplies the effective (resolved) setting values for placeholders and environment variables.
        /// </summary>
        private readonly ModuleRunner _moduleRunner;

        /// <summary>
        /// Logger for bridge failures, timeouts and warnings reported by bridges.
        /// </summary>
        private readonly ILogger<ModuleDownloadBridge> _logger;

        /// <summary>
        /// Maximum size in bytes of one icon file or decoded embedded icon.
        /// </summary>
        private const int MaxIconBytes = 256 * 1024;

        /// <summary>
        /// Maximum width and height in pixels of one icon.
        /// </summary>
        private const int MaxIconDimension = 512;

        /// <summary>
        /// Maximum number of distinct icons resolved for one response and kept in the shared icon cache.
        /// </summary>
        private const int MaxCachedIcons = 64;

        /// <summary>
        /// Cache of validated icons by content hash, shared by all bridge responses; guarded by <see cref="_cacheLock"/>.
        /// </summary>
        private readonly Dictionary<string, DownloadCatalogIcon> _images = new(StringComparer.Ordinal);

        /// <summary>
        /// Insertion order of the keys in <see cref="_images"/>, used to evict the oldest icon first.
        /// </summary>
        private readonly Queue<string> _imageKeys = new();

        /// <summary>
        /// Guards the icon cache and its eviction queue.
        /// </summary>
        private readonly object _cacheLock = new();

        /// <summary>
        /// JSON settings for bridge requests and responses: case-insensitive reading, compact output.
        /// </summary>
        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = false
        };


        // Initialization

        /// <summary>
        /// Creates the downloads bridge over the services needed to start and configure bridge processes.
        /// </summary>
        public ModuleDownloadBridge(
            EngineInstaller engineInstaller,
            ModuleEnvironmentResolver environmentResolver,
            ModuleRunner moduleRunner,
            ILogger<ModuleDownloadBridge> logger)
        {
            _engineInstaller = engineInstaller;
            _environmentResolver = environmentResolver;
            _moduleRunner = moduleRunner;
            _logger = logger;
        }


        // Bridge operations

        /// <summary>
        /// Returns the categories exposed by one module bridge.
        /// A module without a configured bridge has no categories. When the bridge does not support
        /// <c>list_categories</c>, the categories declared statically in the manifest are returned without
        /// starting the bridge.
        /// </summary>
        /// <param name="module">Module that declares the bridge.</param>
        /// <param name="preferCached">Asks the bridge to answer from its own cache when it can.</param>
        /// <param name="forceRefresh">Asks the bridge to ignore its own cache.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <exception cref="InvalidOperationException">Thrown when the bridge reports a failure.</exception>
        public async Task<List<ModuleDownloadCategoryPayload>> GetCategoriesAsync(
            ModuleConfig module,
            bool preferCached = false,
            bool forceRefresh = false,
            CancellationToken ct = default)
        {
            var bridge = module.DownloadsBridge;
            if (bridge == null || !bridge.IsConfigured)
            {
                return [];
            }

            // Fall back to the manifest's static category list when the bridge cannot enumerate categories.
            if (!SupportsOperation(bridge, "list_categories"))
            {
                return bridge.Categories
                    .Select(category => new ModuleDownloadCategoryPayload
                    {
                        Id = category.Id,
                        Title = category.Title,
                        Description = category.Description,
                        GroupKey = category.GroupKey,
                        TargetRef = category.TargetRef,
                        SortOrder = category.SortOrder
                    })
                    .ToList();
            }

            var response = await InvokeAsync(module, new ModuleDownloadBridgeRequest
            {
                ProtocolVersion = bridge.ProtocolVersion,
                Operation = "list_categories",
                PreferCached = preferCached,
                ForceRefresh = forceRefresh
            }, ct);

            if (!response.Success)
            {
                throw new InvalidOperationException(response.Error ?? "Downloads bridge list_categories request failed.");
            }

            return response.Categories;
        }

        /// <summary>
        /// Returns the items exposed by one module bridge category.
        /// An empty response is returned when the module has no configured bridge or the bridge does not
        /// support <c>list_items</c>.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown when the bridge reports a failure.</exception>
        public async Task<ModuleDownloadBridgeResponse> GetItemsAsync(
            ModuleConfig module,
            string categoryId,
            string? queryText = null,
            IReadOnlyCollection<string>? filters = null,
            bool preferCached = false,
            bool forceRefresh = false,
            CancellationToken ct = default)
        {
            var bridge = module.DownloadsBridge;
            if (bridge == null || !bridge.IsConfigured || !SupportsOperation(bridge, "list_items"))
            {
                return new ModuleDownloadBridgeResponse();
            }

            var response = await InvokeAsync(module, new ModuleDownloadBridgeRequest
            {
                ProtocolVersion = bridge.ProtocolVersion,
                Operation = "list_items",
                CategoryId = categoryId,
                QueryText = queryText ?? string.Empty,
                Filters = filters?.ToList() ?? [],
                PreferCached = preferCached,
                ForceRefresh = forceRefresh
            }, ct);

            if (!response.Success)
            {
                throw new InvalidOperationException(response.Error ?? $"Downloads bridge list_items request failed for category '{categoryId}'.");
            }

            return response;
        }

        /// <summary>
        /// Returns the detailed item payload exposed by one module bridge.
        /// Returns null when the module has no configured bridge or the bridge does not support <c>describe_item</c>.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown when the bridge reports a failure.</exception>
        public async Task<ModuleDownloadItemDetailPayload?> GetItemDetailAsync(
            ModuleConfig module,
            string categoryId,
            string resourceKey,
            bool preferCached = false,
            bool forceRefresh = false,
            CancellationToken ct = default)
        {
            var bridge = module.DownloadsBridge;
            if (bridge == null || !bridge.IsConfigured || !SupportsOperation(bridge, "describe_item"))
            {
                return null;
            }

            var response = await InvokeAsync(module, new ModuleDownloadBridgeRequest
            {
                ProtocolVersion = bridge.ProtocolVersion,
                Operation = "describe_item",
                CategoryId = categoryId,
                ResourceKey = resourceKey,
                PreferCached = preferCached,
                ForceRefresh = forceRefresh
            }, ct);

            if (!response.Success)
            {
                throw new InvalidOperationException(response.Error ?? $"Downloads bridge describe_item request failed for resource '{resourceKey}'.");
            }

            return response.ItemDetail;
        }

        /// <summary>
        /// Returns one install manifest resolved by the module bridge.
        /// Always asks the bridge for fresh data. Returns null when the module has no configured bridge or the
        /// bridge does not support <c>resolve_install</c>.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown when the bridge reports a failure.</exception>
        public async Task<ModuleDownloadInstallManifest?> ResolveInstallAsync(
            ModuleConfig module,
            string categoryId,
            string resourceKey,
            CancellationToken ct = default)
        {
            var bridge = module.DownloadsBridge;
            if (bridge == null || !bridge.IsConfigured || !SupportsOperation(bridge, "resolve_install"))
            {
                return null;
            }

            var response = await InvokeAsync(module, new ModuleDownloadBridgeRequest
            {
                ProtocolVersion = bridge.ProtocolVersion,
                Operation = "resolve_install",
                CategoryId = categoryId,
                ResourceKey = resourceKey,
                ForceRefresh = true
            }, ct);

            if (!response.Success)
            {
                throw new InvalidOperationException(response.Error ?? $"Downloads bridge resolve_install request failed for resource '{resourceKey}'.");
            }

            return response.InstallManifest;
        }

        /// <summary>
        /// Returns one uninstall manifest resolved by the module bridge.
        /// Always asks the bridge for fresh data. Returns null when the module has no configured bridge or the
        /// bridge does not support <c>resolve_uninstall</c>.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown when the bridge reports a failure.</exception>
        public async Task<ModuleDownloadInstallManifest?> ResolveUninstallAsync(
            ModuleConfig module,
            string categoryId,
            string resourceKey,
            CancellationToken ct = default)
        {
            var bridge = module.DownloadsBridge;
            if (bridge == null || !bridge.IsConfigured || !SupportsOperation(bridge, "resolve_uninstall"))
            {
                return null;
            }

            var response = await InvokeAsync(module, new ModuleDownloadBridgeRequest
            {
                ProtocolVersion = bridge.ProtocolVersion,
                Operation = "resolve_uninstall",
                CategoryId = categoryId,
                ResourceKey = resourceKey,
                ForceRefresh = true
            }, ct);

            if (!response.Success)
            {
                throw new InvalidOperationException(response.Error ?? $"Downloads bridge resolve_uninstall request failed for resource '{resourceKey}'.");
            }

            return response.UninstallManifest;
        }

        /// <summary>
        /// Executes one bridge request and returns the parsed JSON response.
        /// Failures of the bridge itself (missing declaration, start failure, non-zero exit, bad output, timeout)
        /// are returned as an unsuccessful response instead of an exception; only cancellation by the caller
        /// is thrown.
        /// </summary>
        public async Task<ModuleDownloadBridgeResponse> InvokeAsync(
            ModuleConfig module,
            ModuleDownloadBridgeRequest request,
            CancellationToken ct = default)
        {
            // Register the module as in use for the whole request; this waits while an overlapping module removal
            // is running and keeps a removal from starting until the bridge process has finished.
            using var operation = await ModuleInstaller.BeginContentOperationAsync(ct, module).ConfigureAwait(false);

            // Normalize the outgoing request before any bridge-specific checks.
            request.Normalize();

            // The module must declare a configured bridge and have a resolvable directory to run in.
            var bridge = module.DownloadsBridge;
            if (bridge == null || !bridge.IsConfigured)
            {
                return CreateErrorResponse("Module does not declare a configured downloads bridge.");
            }

            var moduleDir = Path.GetDirectoryName(module.SourcePath);
            if (string.IsNullOrWhiteSpace(moduleDir))
            {
                return CreateErrorResponse("Module directory could not be resolved.");
            }

            // Combine the caller's token with the request timeout; the catch blocks tell the two apart.
            using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            requestCts.CancelAfter(BridgeRequestTimeout);
            var requestToken = requestCts.Token;
            Process? process = null;
            Task<string>? stdoutTask = null;
            Task<string>? stderrTask = null;

            try
            {
                // Engine-backed bridges need the installed engine and the module's prepared environment first.
                if (!string.IsNullOrWhiteSpace(bridge.Engine))
                {
                    var engineConfig = _engineInstaller.GetEngineConfig(bridge.Engine)
                        ?? throw new InvalidOperationException($"Engine '{bridge.Engine}' is not installed.");
                    await _environmentResolver.EnsureEnvironmentAsync(module, engineConfig, null, requestToken)
                        .ConfigureAwait(false);
                }

                // Start the bridge process with the same module context used by regular commands.
                var psi = CreateProcessStartInfo(module, bridge, moduleDir);
                process = new Process { StartInfo = psi };

                if (!process.Start())
                {
                    return CreateErrorResponse("Downloads bridge process could not be started.");
                }

                // Drain both streams immediately so a verbose bridge cannot block while stdin is written.
                stdoutTask = ReadBoundedToEndAsync(process.StandardOutput, requestToken);
                stderrTask = ReadBoundedToEndAsync(process.StandardError, requestToken);

                // Send the request as one JSON document and close stdin so the bridge sees the end of its input.
                // StandardInputEncoding is explicitly BOM-less so every JSON parser sees '{' as the first byte.
                var requestJson = JsonSerializer.Serialize(request, _jsonOptions);
                await process.StandardInput.WriteAsync(requestJson.AsMemory(), requestToken).ConfigureAwait(false);
                await process.StandardInput.FlushAsync().ConfigureAwait(false);
                process.StandardInput.Close();

                // Wait for the bridge to finish, then collect everything it wrote.
                await process.WaitForExitAsync(requestToken).ConfigureAwait(false);

                var stdout = await stdoutTask.ConfigureAwait(false);
                var stderr = await stderrTask.ConfigureAwait(false);

                // A non-zero exit code is a failure; its message is whatever the bridge printed.
                if (process.ExitCode != 0)
                {
                    // Prefer stderr, but fall back to stdout because some bridges only print there.
                    var error = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                    _logger.LogWarning(
                        "Downloads bridge for module {ModuleId} exited with code {ExitCode}: {Error}",
                        module.Id,
                        process.ExitCode,
                        error);

                    return CreateErrorResponse(string.IsNullOrWhiteSpace(error)
                        ? $"Downloads bridge exited with code {process.ExitCode}."
                        : error.Trim());
                }

                // Extract the JSON object from stdout in case the bridge logs extra lines.
                var jsonPayload = ExtractJsonPayload(stdout);
                if (string.IsNullOrWhiteSpace(jsonPayload))
                {
                    return CreateErrorResponse("Downloads bridge returned no JSON payload.");
                }

                var response = JsonSerializer.Deserialize<ModuleDownloadBridgeResponse>(jsonPayload, _jsonOptions);
                if (response == null)
                {
                    return CreateErrorResponse("Downloads bridge returned an unreadable JSON payload.");
                }

                // Resolve the icons and colors referenced by items on a worker thread (file reads and image decoding),
                // and log the warnings the bridge and the resolution produced.
                response.Normalize();
                if (response.Items.Count > 0 || response.ItemDetail != null)
                    await Task.Run(() => ResolveResourcesAsync(response, moduleDir, requestToken), requestToken)
                        .ConfigureAwait(false);
                foreach (var warning in response.Warnings)
                    _logger.LogWarning("Downloads bridge warning for {ModuleId}: {Warning}", module.Id, warning);
                return response;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The caller canceled: stop the bridge and let the cancellation propagate.
                await TerminateProcessTreeAsync(process, stdoutTask, stderrTask).ConfigureAwait(false);
                throw;
            }
            catch (OperationCanceledException) when (requestCts.IsCancellationRequested)
            {
                // Only the request timeout fired: stop the bridge and report a timeout error.
                await TerminateProcessTreeAsync(process, stdoutTask, stderrTask).ConfigureAwait(false);
                _logger.LogWarning(
                    "Downloads bridge request for module {ModuleId} timed out after {TimeoutSeconds} seconds.",
                    module.Id,
                    BridgeRequestTimeout.TotalSeconds);
                return CreateErrorResponse(
                    $"Downloads bridge request timed out after {BridgeRequestTimeout.TotalSeconds:0} seconds.");
            }
            catch (Exception ex)
            {
                // Any other failure (missing engine, start error, unreadable JSON) becomes an error response.
                await TerminateProcessTreeAsync(process, stdoutTask, stderrTask).ConfigureAwait(false);
                _logger.LogError(ex, "Downloads bridge invocation failed for module {ModuleId}.", module.Id);
                return CreateErrorResponse(ex.Message);
            }
            finally
            {
                process?.Dispose();
            }
        }


        // Process cleanup

        /// <summary>
        /// Stops a failed or canceled bridge process and observes its redirected output tasks.
        /// The whole process tree is killed so child processes of the bridge do not outlive it.
        /// </summary>
        private static async Task TerminateProcessTreeAsync(
            Process? process,
            Task<string>? stdoutTask,
            Task<string>? stderrTask)
        {
            // Kill the process tree if it is still running, then give the exit up to five seconds.
            if (process != null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // The process may have exited between the state check and termination request.
                }

                try
                {
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
                catch
                {
                    // Cleanup remains best-effort when the operating system no longer exposes the process handle.
                }
            }

            // Wait for the stream readers too, so no faulted task stays unobserved.
            await ObserveOutputTaskAsync(stdoutTask).ConfigureAwait(false);
            await ObserveOutputTaskAsync(stderrTask).ConfigureAwait(false);
        }

        /// <summary>
        /// Observes one redirected output task without allowing cleanup failures to replace the primary error.
        /// </summary>
        private static async Task ObserveOutputTaskAsync(Task<string>? outputTask)
        {
            if (outputTask == null)
            {
                return;
            }

            try
            {
                await outputTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch
            {
                // Output may be interrupted while a canceled process tree is being terminated.
            }
        }


        // Response-local colors and icons

        /// <summary>
        /// Replaces the icon names and color names in the details and tags of a response with validated icon images and
        /// normalized hex colors taken from the response's own resource tables.
        /// Entries that cannot be resolved lose the icon or color and add a warning to the response.
        /// </summary>
        /// <param name="response">Response whose items and item detail are updated in place.</param>
        /// <param name="moduleDirectory">Module directory that icon file paths are relative to.</param>
        /// <param name="ct">Cancellation token.</param>
        private async Task ResolveResourcesAsync(ModuleDownloadBridgeResponse response, string moduleDirectory, CancellationToken ct)
        {
            // Per-response caches keep every icon and color name resolved (and warned about) only once.
            var icons = new Dictionary<string, DownloadCatalogIcon?>(StringComparer.Ordinal);
            var colors = new Dictionary<string, string?>(StringComparer.Ordinal);
            var warnings = new HashSet<string>(StringComparer.Ordinal);

            // Resolves the icon and colors of every field in one details or tags list.
            async Task<List<ModuleDownloadField>?> ResolveList(List<ModuleDownloadField>? values)
            {
                // Preserve absent vs explicitly empty arrays for describe_item overrides.
                if (values == null) return null;
                var result = new List<ModuleDownloadField>();
                foreach (var value in values)
                {
                    ct.ThrowIfCancellationRequested();
                    if (value == null) continue;

                    // Look the icon up in this response's cache first; load it only once per name and
                    // only for the first MaxCachedIcons distinct names.
                    var iconName = value.Icon?.Trim();
                    DownloadCatalogIcon? icon = null;
                    if (!string.IsNullOrEmpty(iconName))
                    {
                        if (!icons.TryGetValue(iconName, out icon))
                        {
                            if (icons.Count < MaxCachedIcons &&
                                response.Resources?.Icons?.TryGetValue(iconName, out var definition) == true &&
                                definition != null)
                            {
                                icon = await LoadIconAsync(definition, moduleDirectory, ct).ConfigureAwait(false);
                            }

                            icons[iconName] = icon;
                            if (icon == null) warnings.Add($"Downloads metadata icon '{iconName}' is missing or invalid.");
                        }
                    }

                    // A field needs text or a resolved icon to be shown; otherwise it is dropped.
                    var text = value.Text?.Trim() ?? string.Empty;
                    if (text.Length == 0 && icon == null) continue;
                    result.Add(new ModuleDownloadField
                    {
                        Text = text,
                        BackgroundColor = ResolveColor(value.BackgroundColor),
                        TextColor = ResolveColor(value.TextColor),
                        ShowInCatalog = value.ShowInCatalog,
                        Icon = iconName,
                        Image = icon
                    });
                }

                return result;
            }

            // Resolves a literal '#' hex value or a named color from the response's color table to #AARRGGBB.
            string? ResolveColor(string? nameOrHex)
            {
                if (string.IsNullOrWhiteSpace(nameOrHex)) return null;
                var key = nameOrHex.Trim();
                if (colors.TryGetValue(key, out var cached)) return cached;
                var value = key;
                if (!value.StartsWith('#'))
                    value = response.Resources?.Colors?.GetValueOrDefault(value) ?? string.Empty;
                var color = NormalizeHex(value);
                colors[key] = color;
                if (color == null) warnings.Add($"Downloads metadata color '{nameOrHex}' is missing or invalid.");
                return color;
            }

            // Resolve every item of a list response and the item detail of a describe_item response,
            // then report the collected problems through the response warnings.
            foreach (var item in response.Items)
            {
                item.Details = await ResolveList(item.Details).ConfigureAwait(false);
                item.Tags = await ResolveList(item.Tags).ConfigureAwait(false);
            }

            if (response.ItemDetail is { } detail)
            {
                detail.Details = await ResolveList(detail.Details).ConfigureAwait(false);
                detail.Tags = await ResolveList(detail.Tags).ConfigureAwait(false);
            }

            response.Warnings.AddRange(warnings);
        }

        /// <summary>
        /// Normalizes a <c>#RRGGBB</c> or <c>#AARRGGBB</c> color to uppercase <c>#AARRGGBB</c>
        /// (opaque when no alpha is given). Returns null for any other text.
        /// </summary>
        private static string? NormalizeHex(string? value)
        {
            var hex = value?.Trim();
            if (hex == null || (hex.Length != 7 && hex.Length != 9) || hex[0] != '#' ||
                hex.AsSpan(1).ContainsAnyExcept("0123456789abcdefABCDEF")) return null;
            return hex.Length == 7 ? "#FF" + hex[1..].ToUpperInvariant() : hex.ToUpperInvariant();
        }

        /// <summary>
        /// Loads and validates one bridge icon, from a file inside the module directory or from inline Base64 PNG data.
        /// The icon must be a PNG within the size and dimension limits and must decode. Validated icons are shared
        /// through the icon cache by content hash. Returns null for any invalid, oversized, unreadable or
        /// ambiguous (both file and inline data) definition.
        /// </summary>
        private async Task<DownloadCatalogIcon?> LoadIconAsync(
            ModuleDownloadIconPayload definition, string moduleDirectory, CancellationToken ct)
        {
            try
            {
                // Read the raw bytes. A file icon must be read completely within the size limit;
                // an inline icon must declare image/png and fit the limit even before decoding the Base64 text.
                byte[] bytes;
                if (!string.IsNullOrWhiteSpace(definition.Path))
                {
                    if (!string.IsNullOrWhiteSpace(definition.Base64)) return null;
                    var path = ResolveIconPath(moduleDirectory, definition.Path);
                    if (path == null) return null;
                    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                        4096, FileOptions.Asynchronous);
                    if (stream.Length is <= 0 or > MaxIconBytes) return null;
                    bytes = new byte[(int)stream.Length];
                    await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
                    if (stream.ReadByte() != -1) return null;
                }
                else
                {
                    if (!string.Equals(definition.MimeType, "image/png", StringComparison.OrdinalIgnoreCase) ||
                        string.IsNullOrWhiteSpace(definition.Base64) ||
                        definition.Base64.Length > ((MaxIconBytes + 2) / 3) * 4) return null;
                    bytes = Convert.FromBase64String(definition.Base64);
                }

                // Identical icons from any module share one cache entry, keyed by the hash of their bytes.
                ct.ThrowIfCancellationRequested();
                if (bytes.Length is <= 0 or > MaxIconBytes) return null;
                var key = Convert.ToHexString(SHA256.HashData(bytes));
                lock (_cacheLock)
                    if (_images.TryGetValue(key, out var cached)) return cached;

                // Validate the image: it must be a PNG within the dimension limit and fully decodable.
                using var data = SKData.CreateCopy(bytes);
                using var codec = SKCodec.Create(data);
                if (codec == null || codec.EncodedFormat != SKEncodedImageFormat.Png ||
                    codec.Info.Width is <= 0 or > MaxIconDimension ||
                    codec.Info.Height is <= 0 or > MaxIconDimension) return null;
                using var bitmap = SKBitmap.Decode(bytes);
                if (bitmap == null) return null;

                // Store the validated icon; another request may have added it meanwhile, and the oldest icons are
                // evicted first once the cache is full.
                var image = new DownloadCatalogIcon(key, bytes);
                lock (_cacheLock)
                {
                    if (_images.TryGetValue(key, out var cached)) return cached;
                    while (_images.Count >= MaxCachedIcons) _images.Remove(_imageKeys.Dequeue());
                    _images.Add(key, image);
                    _imageKeys.Enqueue(key);
                }

                return image;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       FormatException or ArgumentException or NotSupportedException)
            {
                // A missing or unreadable file or malformed Base64 only means this icon is unavailable.
                return null;
            }
        }

        /// <summary>
        /// Resolves a relative icon path to an absolute path that stays inside the module directory.
        /// Returns null for rooted paths, paths that escape the directory, and paths that cross a file link or
        /// directory junction.
        /// </summary>
        private static string? ResolveIconPath(string moduleDirectory, string relativePath)
        {
            // Only relative paths are accepted, and the resolved path must stay below the module directory.
            if (Path.IsPathRooted(relativePath)) return null;
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(moduleDirectory));
            var path = Path.GetFullPath(relativePath, root);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, comparison)) return null;

            // Do not follow file links or directory junctions out of the module.
            for (var current = path; !string.Equals(current, root, comparison); current = Path.GetDirectoryName(current)!)
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return null;
            }

            return path;
        }


        // Bridge command resolution

        /// <summary>
        /// Returns whether the module manifest declares support for one operation.
        /// A bridge that lists no operations is treated as supporting all of them.
        /// </summary>
        private static bool SupportsOperation(ModuleDownloadsBridge bridge, string operation)
        {
            return bridge.Operations.Count == 0 ||
                   bridge.Operations.Any(candidate => string.Equals(candidate, operation, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Drains a bridge output stream while keeping only a bounded amount of text in memory.
        /// Text beyond <see cref="MaxBridgeOutputCharacters"/> is read and discarded, and a truncation marker is appended.
        /// </summary>
        private static async Task<string> ReadBoundedToEndAsync(StreamReader reader, CancellationToken ct)
        {
            var builder = new StringBuilder();
            var buffer = new char[8192];
            var truncated = false;

            // Keep reading until the stream ends so the bridge never blocks on a full pipe.
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                if (read == 0)
                {
                    break;
                }

                var remainingCapacity = MaxBridgeOutputCharacters - builder.Length;
                if (remainingCapacity > 0)
                {
                    builder.Append(buffer, 0, Math.Min(read, remainingCapacity));
                }

                if (read > remainingCapacity)
                {
                    truncated = true;
                }
            }

            if (truncated)
            {
                builder.AppendLine();
                builder.Append("[output truncated]");
            }

            return builder.ToString();
        }

        /// <summary>
        /// Creates the process startup info for one bridge invocation.
        /// Engine-backed bridges run through the installed engine executable (inside the module environment when
        /// the engine has one) with the entry point as arguments; other bridges run their entry point directly.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown when the engine is not installed or the entry point is empty.</exception>
        internal ProcessStartInfo CreateProcessStartInfo(
            ModuleConfig module,
            ModuleDownloadsBridge bridge,
            string moduleDir)
        {
            string fileName;
            string arguments;

            // Engine-backed bridges use the installed engine executable and pass the entry point as arguments.
            if (!string.IsNullOrWhiteSpace(bridge.Engine))
            {
                var engineConfig = _engineInstaller.GetEngineConfig(bridge.Engine)
                    ?? throw new InvalidOperationException($"Engine '{bridge.Engine}' is not installed.");
                var environment = ModuleEnvironmentResolver.HasModuleEnvironment(engineConfig)
                    ? _environmentResolver.ResolveEnvironment(module, engineConfig)
                    : null;
                fileName = _environmentResolver.ResolveCommandExecutable(environment, engineConfig);

                arguments = ResolveCommandPlaceholders(module, bridge.EntryPoint);
            }
            else
            {
                // Raw entry points must be split into the executable and argument list manually.
                var parts = SplitCommand(ResolveCommandPlaceholders(module, bridge.EntryPoint));
                if (parts.Count == 0)
                {
                    throw new InvalidOperationException("Downloads bridge entryPoint is empty.");
                }

                fileName = parts[0];
                arguments = parts.Count > 1 ? JoinArguments(parts.Skip(1)) : string.Empty;
            }

            // Redirect all three standard streams; stdin is BOM-free UTF-8 and the bridge runs in the module folder.
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = moduleDir,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            // Keep bridge execution aligned with regular module process setup.
            InjectModuleEnvironment(module, moduleDir, psi);
            if (!string.IsNullOrWhiteSpace(bridge.Engine))
            {
                var engineConfig = _engineInstaller.GetEngineConfig(bridge.Engine)
                    ?? throw new InvalidOperationException($"Engine '{bridge.Engine}' is not installed.");
                _environmentResolver.ApplyEnvironmentVariables(module, engineConfig, psi);
            }

            ConfigurePythonProcess(psi, fileName, bridge.Engine);
            return psi;
        }

        /// <summary>
        /// Resolves module setting placeholders inside one bridge command.
        /// Each <c>{key}</c> is replaced with the display form of the setting's current effective value.
        /// </summary>
        private string ResolveCommandPlaceholders(ModuleConfig module, string command)
        {
            var resolvedCommand = command ?? string.Empty;
            if (module.Settings == null)
            {
                return resolvedCommand;
            }

            // Resolve placeholders from the current effective setting values rather than raw defaults.
            foreach (var setting in module.Settings)
            {
                var resolvedValue = _moduleRunner.GetResolvedSettingValue(module, setting);
                var displayValue = setting.FormatValueForDisplay(resolvedValue);
                resolvedCommand = resolvedCommand.Replace($"{{{setting.Key}}}", displayValue, StringComparison.Ordinal);
            }

            return resolvedCommand;
        }

        /// <summary>
        /// Injects the same useful module environment values that regular module commands receive:
        /// one <c>ASLM_{KEY}</c> variable per setting plus <c>ASLM_MODULE_ID</c> and <c>ASLM_MODULE_DIR</c>.
        /// </summary>
        private void InjectModuleEnvironment(ModuleConfig module, string moduleDir, ProcessStartInfo psi)
        {
            // Export each resolved module setting so the bridge can stay declarative.
            foreach (var setting in module.Settings)
            {
                var resolvedValue = _moduleRunner.GetResolvedSettingValue(module, setting);
                var displayValue = setting.FormatValueForDisplay(resolvedValue);
                psi.Environment[$"ASLM_{setting.Key.ToUpperInvariant()}"] = displayValue;
            }

            psi.Environment["ASLM_MODULE_ID"] = module.Id;
            psi.Environment["ASLM_MODULE_DIR"] = moduleDir;
        }


        // Python process handling

        /// <summary>
        /// Ensures Python-based bridges run in unbuffered UTF-8 mode.
        /// </summary>
        private static void ConfigurePythonProcess(ProcessStartInfo psi, string fileName, string? engineId)
        {
            if (!IsPythonProcess(fileName, engineId))
            {
                return;
            }

            if (!HasPythonUnbufferedFlag(psi.Arguments))
            {
                psi.Arguments = string.IsNullOrWhiteSpace(psi.Arguments)
                    ? "-u"
                    : $"-u {psi.Arguments}";
            }

            psi.Environment["PYTHONUNBUFFERED"] = "1";
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            psi.Environment["PYTHONUTF8"] = "1";
        }


        // Command parsing

        /// <summary>
        /// Returns whether one process launch targets Python: the engine id mentions Python or the executable
        /// name starts with <c>python</c> or <c>py</c>.
        /// </summary>
        private static bool IsPythonProcess(string fileName, string? engineId)
        {
            if (!string.IsNullOrWhiteSpace(engineId) &&
                engineId.Contains("python", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var executableName = Path.GetFileName(fileName);
            if (string.IsNullOrWhiteSpace(executableName))
            {
                return false;
            }

            return executableName.StartsWith("python", StringComparison.OrdinalIgnoreCase) ||
                   executableName.StartsWith("py", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Returns whether the command line already enables Python unbuffered mode.
        /// </summary>
        private static bool HasPythonUnbufferedFlag(string arguments)
        {
            if (string.IsNullOrWhiteSpace(arguments))
            {
                return false;
            }

            var parts = SplitCommand(arguments);
            return parts.Any(part => string.Equals(part, "-u", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Extracts the most likely JSON object from bridge stdout: the text from the first <c>{</c> to the last
        /// <c>}</c>, so log lines before or after the payload are ignored. Returns an empty string when there is none.
        /// </summary>
        private static string ExtractJsonPayload(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
            {
                return string.Empty;
            }

            var startIndex = output.IndexOf('{');
            var endIndex = output.LastIndexOf('}');
            if (startIndex < 0 || endIndex < startIndex)
            {
                return string.Empty;
            }

            return output[startIndex..(endIndex + 1)];
        }

        /// <summary>
        /// Splits a command string into tokens while respecting quotes.
        /// </summary>
        private static List<string> SplitCommand(string command)
        {
            var args = new List<string>();
            var currentArg = new StringBuilder();
            var inQuotes = false;
            var quoteChar = '\0';

            // Keep quoted segments intact so bridge entry points can contain spaces safely.
            foreach (var character in command)
            {
                // Inside quotes everything up to the matching quote belongs to the current token.
                if (inQuotes)
                {
                    if (character == quoteChar)
                    {
                        inQuotes = false;
                        quoteChar = '\0';
                    }
                    else
                    {
                        currentArg.Append(character);
                    }
                }
                else
                {
                    // Outside quotes whitespace ends a token and a single or double quote opens a quoted part.
                    if (char.IsWhiteSpace(character))
                    {
                        if (currentArg.Length > 0)
                        {
                            args.Add(currentArg.ToString());
                            currentArg.Clear();
                        }
                    }
                    else if (character == '"' || character == '\'')
                    {
                        inQuotes = true;
                        quoteChar = character;
                    }
                    else
                    {
                        currentArg.Append(character);
                    }
                }
            }

            // Flush the last token, which has no trailing whitespace after it.
            if (currentArg.Length > 0)
            {
                args.Add(currentArg.ToString());
            }

            return args;
        }


        // Response helpers

        /// <summary>
        /// Rebuilds a command-line argument string from parsed tokens.
        /// </summary>
        private static string JoinArguments(IEnumerable<string> arguments)
        {
            return string.Join(" ", arguments.Select(argument => argument.Contains(' ') ? $"\"{argument}\"" : argument));
        }

        /// <summary>
        /// Creates a standardized failed bridge response.
        /// </summary>
        private static ModuleDownloadBridgeResponse CreateErrorResponse(string error)
        {
            return new ModuleDownloadBridgeResponse
            {
                Success = false,
                Error = string.IsNullOrWhiteSpace(error) ? "Unknown downloads bridge error." : error.Trim()
            };
        }
    }
}
