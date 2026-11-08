// Copyright NEXTGGTECH. Apache License 2.0.

using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ASLM.Models;
using Microsoft.Extensions.Logging;

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Resolves bridge install manifests and executes whitelisted install actions inside safe ASLM-managed directories.
    /// </summary>
    public class DownloadInstaller
    {
        // Fields

        /// <summary>
        /// Loads module manifests and guards module content during install operations.
        /// </summary>
        private readonly ModuleInstaller _moduleInstaller;

        /// <summary>
        /// Client that asks module bridges for install and uninstall manifests.
        /// </summary>
        private readonly ModuleDownloadBridge _bridge;

        /// <summary>
        /// Locates installed engines whose executables run package managers and Ollama.
        /// </summary>
        private readonly EngineInstaller _engineInstaller;

        /// <summary>
        /// Prepares the per-module environment used for package installation.
        /// </summary>
        private readonly ModuleEnvironmentResolver _environmentResolver;

        /// <summary>
        /// Persisted installation state updated after a successful install or removal.
        /// </summary>
        private readonly DownloadStateStore _stateStore;

        /// <summary>
        /// Shows progress, completion and failure of each operation to the user.
        /// </summary>
        private readonly NotificationCenter _notifications;

        /// <summary>
        /// Logger for manifest resolution and execution failures.
        /// </summary>
        private readonly ILogger<DownloadInstaller> _logger;

        /// <summary>
        /// Queue that serializes operations per lane and exposes their state.
        /// </summary>
        private readonly DownloadQueue _downloadQueue;

        /// <summary>
        /// HTTP client used for file downloads and the temporary Ollama endpoint.
        /// </summary>
        private readonly HttpClient _httpClient;

        /// <summary>
        /// JSON settings used for the request bodies sent to the temporary Ollama service.
        /// </summary>
        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = false
        };


        // Initialization

        /// <summary>
        /// Creates the download install service.
        /// </summary>
        public DownloadInstaller(
            ModuleInstaller moduleInstaller,
            ModuleDownloadBridge bridge,
            EngineInstaller engineInstaller,
            ModuleEnvironmentResolver environmentResolver,
            DownloadStateStore stateStore,
            NotificationCenter notifications,
            ILogger<DownloadInstaller> logger,
            DownloadQueue downloadQueue)
            : this(moduleInstaller, bridge, engineInstaller, environmentResolver, stateStore, notifications,
                logger, downloadQueue, new HttpClient())
        {
        }

        /// <summary>
        /// Creates the download install service with an explicit HTTP client (test isolation).
        /// </summary>
        internal DownloadInstaller(
            ModuleInstaller moduleInstaller,
            ModuleDownloadBridge bridge,
            EngineInstaller engineInstaller,
            ModuleEnvironmentResolver environmentResolver,
            DownloadStateStore stateStore,
            NotificationCenter notifications,
            ILogger<DownloadInstaller> logger,
            DownloadQueue downloadQueue,
            HttpClient httpClient)
        {
            _moduleInstaller = moduleInstaller;
            _bridge = bridge;
            _engineInstaller = engineInstaller;
            _environmentResolver = environmentResolver;
            _stateStore = stateStore;
            _notifications = notifications;
            _logger = logger;
            _downloadQueue = downloadQueue;
            _httpClient = httpClient;
        }


        // Operation queue access

        /// <summary>
        /// Raised when an install or removal operation is queued, starts or finishes.
        /// </summary>
        public event EventHandler? OperationsChanged
        {
            add => _downloadQueue.StateChanged += value;
            remove => _downloadQueue.StateChanged -= value;
        }

        /// <summary>
        /// Resolves the resource key that identifies an operation: the selected variant, else the item's default
        /// variant, else the item itself.
        /// </summary>
        internal static string GetResourceKey(DownloadCatalogItem item, DownloadCatalogVariant? variant) =>
            !string.IsNullOrWhiteSpace(variant?.ResourceKey) ? variant.ResourceKey
                : !string.IsNullOrWhiteSpace(item.DefaultVariantResourceKey) ? item.DefaultVariantResourceKey : item.ResourceKey;

        /// <summary>
        /// Returns whether an install or removal for the selected variant is queued or running.
        /// </summary>
        public bool IsPending(DownloadCatalogItem item, DownloadCatalogVariant? variant) =>
            _downloadQueue.Contains("resource:" + GetResourceKey(item, variant));

        /// <summary>
        /// Returns the queue phase of the operation for one resource key, or null when none is pending.
        /// </summary>
        public DownloadOperationState? GetState(string resourceKey) => _downloadQueue.GetState("resource:" + resourceKey);

        /// <summary>
        /// Requests cancellation of the pending operation for one resource key.
        /// </summary>
        public void Cancel(string resourceKey) => _downloadQueue.Cancel("resource:" + resourceKey);


        // Install

        /// <summary>
        /// Installs one shared catalog item using the first module source that resolves a valid install manifest.
        /// The operation is queued, and an operation already pending for the same resource is reused.
        /// </summary>
        /// <param name="item">Catalog item to install.</param>
        /// <param name="selectedVariant">Variant chosen by the user, or null for the item's default variant.</param>
        /// <param name="log">Optional receiver of human-readable log lines.</param>
        /// <param name="ct">Cancellation token that also cancels the queued operation.</param>
        public Task<DownloadInstallResult> InstallAsync(
            DownloadCatalogItem item,
            DownloadCatalogVariant? selectedVariant,
            IProgress<string>? log = null,
            CancellationToken ct = default)
            => _downloadQueue.EnqueueAsync("resource:" + GetResourceKey(item, selectedVariant),
                token => InstallCoreAsync(item, selectedVariant, log, token), ct);

        /// <summary>
        /// Runs the queued install: announces it to the user, then tries each module source in order until one
        /// resolves an install manifest that executes successfully.
        /// </summary>
        /// <param name="item">Catalog item to install.</param>
        /// <param name="selectedVariant">Variant chosen by the user, or null for the item's default variant.</param>
        /// <param name="log">Optional receiver of human-readable log lines.</param>
        /// <param name="ct">Cancellation token; cancellation is reported to the user and rethrown.</param>
        private async Task<DownloadInstallResult> InstallCoreAsync(DownloadCatalogItem item,
            DownloadCatalogVariant? selectedVariant, IProgress<string>? log, CancellationToken ct)
        {
            if (item.Sources.Count == 0)
            {
                return new DownloadInstallResult(false, "No module source is available for this download item.");
            }

            // Work out the resource and display names, then show the operation in the notification center.
            var selectedResourceKey = GetResourceKey(item, selectedVariant);
            var selectedTitle = !string.IsNullOrWhiteSpace(selectedVariant?.Title)
                ? selectedVariant!.Title
                : item.Title;
            var operationKey = NotificationCenter.BuildOperationKey("download-install", selectedResourceKey);
            var catalogLabel = item.Title;
            var notificationMessage = string.Equals(selectedTitle, catalogLabel, StringComparison.OrdinalIgnoreCase)
                ? catalogLabel
                : $"{catalogLabel} · {selectedTitle}";
            await _notifications.StartDownloadAsync(
                operationKey,
                "Installing download",
                notificationMessage,
                "download",
                selectedResourceKey);

            // The last failure is reported if no source succeeds.
            var lastError = "No module source produced an install manifest.";

            foreach (var source in item.Sources)
            {
                ct.ThrowIfCancellationRequested();

                // Load the manifest of the module behind this source; try the next source when it is gone.
                var module = await _moduleInstaller.LoadModuleConfig(source.ModuleSourcePath);
                if (module == null)
                {
                    lastError = $"Module manifest could not be loaded: {source.ModuleSourcePath}";
                    continue;
                }

                // Register a content operation for the module, which blocks overlapping module removals until this
                // iteration ends, then ask the module bridge how the resource is installed.
                using var operation = await ModuleInstaller.BeginContentOperationAsync(ct, module).ConfigureAwait(false);
                ModuleDownloadInstallManifest? manifest;
                try
                {
                    manifest = await _bridge.ResolveInstallAsync(module, source.CategoryId, selectedResourceKey, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    _notifications.FailDownload(operationKey, $"Installation canceled for {selectedTitle}.");
                    throw;
                }
                catch (Exception ex)
                {
                    // A failing source is not fatal: remember the reason and try the next source.
                    _logger.LogWarning(ex, "Failed to resolve install manifest for resource {ResourceKey} via module {ModuleId}.", selectedResourceKey, module.Id);
                    lastError = ex.Message;
                    continue;
                }

                // A usable manifest has at least one action.
                if (manifest == null)
                {
                    lastError = $"Module '{module.Name}' did not return an install manifest.";
                    continue;
                }

                manifest.Normalize();
                if (manifest.Actions.Count == 0)
                {
                    lastError = $"Module '{module.Name}' returned an empty install manifest.";
                    continue;
                }

                try
                {
                    // Execute the manifest, record the installation and finish the notification.
                    var effectiveTitle = !string.IsNullOrWhiteSpace(manifest.Title) ? manifest.Title : selectedTitle;
                    log?.Report($"Installing {effectiveTitle} via {module.Name}...");

                    await ExecuteManifestAsync(module, manifest, operationKey, log, ct);

                    var version = !string.IsNullOrWhiteSpace(manifest.Version)
                        ? manifest.Version
                        : item.Version;

                    await _stateStore.MarkInstalledAsync(selectedResourceKey, version, module.Id);

                    var message = string.IsNullOrWhiteSpace(version)
                        ? $"{effectiveTitle} installed successfully."
                        : $"{effectiveTitle} {version} installed successfully.";

                    log?.Report(message);
                    _notifications.CompleteDownload(operationKey, message);
                    return new DownloadInstallResult(true, message);
                }
                catch (OperationCanceledException)
                {
                    _notifications.FailDownload(operationKey, $"Installation canceled for {selectedTitle}.");
                    throw;
                }
                catch (Exception ex)
                {
                    // Execution failed: report it and let the next source try.
                    _logger.LogError(ex, "Install manifest execution failed for resource {ResourceKey} via module {ModuleId}.", selectedResourceKey, module.Id);
                    lastError = ex.Message;
                    log?.Report($"Install failed via {module.Name}: {ex.Message}");
                    _notifications.FailDownload(operationKey, $"Install failed: {ex.Message}");
                }
            }

            // No source succeeded.
            _notifications.FailDownload(operationKey, lastError);
            return new DownloadInstallResult(false, lastError);
        }


        // Uninstall

        /// <summary>
        /// Removes one shared catalog variant using the first module source that resolves a valid uninstall manifest.
        /// The operation is queued in the removal state, and an operation already pending for the same resource is reused.
        /// </summary>
        /// <param name="item">Catalog item whose variant is removed.</param>
        /// <param name="selectedVariant">Variant chosen by the user, or null for the item's default variant.</param>
        /// <param name="log">Optional receiver of human-readable log lines.</param>
        /// <param name="ct">Cancellation token that also cancels the queued operation.</param>
        public Task<DownloadInstallResult> UninstallAsync(
            DownloadCatalogItem item,
            DownloadCatalogVariant? selectedVariant,
            IProgress<string>? log = null,
            CancellationToken ct = default)
            => _downloadQueue.EnqueueAsync("resource:" + GetResourceKey(item, selectedVariant),
                token => UninstallCoreAsync(item, selectedVariant, log, token), ct, removal: true);

        /// <summary>
        /// Runs the queued removal: announces it to the user, then tries each module source in order until one
        /// resolves an uninstall manifest that executes successfully.
        /// </summary>
        /// <param name="item">Catalog item whose variant is removed.</param>
        /// <param name="selectedVariant">Variant chosen by the user, or null for the item's default variant.</param>
        /// <param name="log">Optional receiver of human-readable log lines.</param>
        /// <param name="ct">Cancellation token; cancellation is reported to the user and rethrown.</param>
        private async Task<DownloadInstallResult> UninstallCoreAsync(DownloadCatalogItem item,
            DownloadCatalogVariant? selectedVariant, IProgress<string>? log, CancellationToken ct)
        {
            if (item.Sources.Count == 0)
            {
                return new DownloadInstallResult(false, "No module source is available for this download item.");
            }

            // Work out the resource and display names, then show the operation in the notification center.
            var selectedResourceKey = GetResourceKey(item, selectedVariant);
            var selectedTitle = !string.IsNullOrWhiteSpace(selectedVariant?.Title)
                ? selectedVariant!.Title
                : item.Title;
            var operationKey = NotificationCenter.BuildOperationKey("download-remove", selectedResourceKey);
            var catalogLabel = item.Title;
            var notificationMessage = string.Equals(selectedTitle, catalogLabel, StringComparison.OrdinalIgnoreCase)
                ? catalogLabel
                : $"{catalogLabel} · {selectedTitle}";
            await _notifications.StartDownloadAsync(
                operationKey,
                "Removing download",
                notificationMessage,
                "download",
                selectedResourceKey);

            // The last failure is reported if no source succeeds.
            var lastError = "No module source produced an uninstall manifest.";

            foreach (var source in item.Sources)
            {
                ct.ThrowIfCancellationRequested();

                // Load the manifest of the module behind this source; try the next source when it is gone.
                var module = await _moduleInstaller.LoadModuleConfig(source.ModuleSourcePath);
                if (module == null)
                {
                    lastError = $"Module manifest could not be loaded: {source.ModuleSourcePath}";
                    continue;
                }

                // Register a content operation for the module, which blocks overlapping module removals until this
                // iteration ends, then ask the module bridge how the resource is removed.
                using var operation = await ModuleInstaller.BeginContentOperationAsync(ct, module).ConfigureAwait(false);
                ModuleDownloadInstallManifest? manifest;
                try
                {
                    manifest = await _bridge.ResolveUninstallAsync(module, source.CategoryId, selectedResourceKey, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    _notifications.FailDownload(operationKey, $"Removal canceled for {selectedTitle}.");
                    throw;
                }
                catch (Exception ex)
                {
                    // A failing source is not fatal: remember the reason and try the next source.
                    _logger.LogWarning(ex, "Failed to resolve uninstall manifest for resource {ResourceKey} via module {ModuleId}.", selectedResourceKey, module.Id);
                    lastError = ex.Message;
                    continue;
                }

                // A usable manifest has at least one action.
                if (manifest == null)
                {
                    lastError = $"Module '{module.Name}' did not return an uninstall manifest.";
                    continue;
                }

                manifest.Normalize();
                if (manifest.Actions.Count == 0)
                {
                    lastError = $"Module '{module.Name}' returned an empty uninstall manifest.";
                    continue;
                }

                try
                {
                    // Execute the manifest, clear the persisted installation state and finish the notification.
                    var effectiveTitle = !string.IsNullOrWhiteSpace(manifest.Title) ? manifest.Title : selectedTitle;
                    log?.Report($"Removing {effectiveTitle} via {module.Name}...");

                    await ExecuteManifestAsync(module, manifest, operationKey, log, ct);
                    await _stateStore.MarkUninstalledAsync(selectedResourceKey);

                    var message = $"{effectiveTitle} removed successfully.";
                    log?.Report(message);
                    _notifications.CompleteDownload(operationKey, message);
                    return new DownloadInstallResult(true, message);
                }
                catch (OperationCanceledException)
                {
                    _notifications.FailDownload(operationKey, $"Removal canceled for {selectedTitle}.");
                    throw;
                }
                catch (Exception ex)
                {
                    // Execution failed: report it and let the next source try.
                    _logger.LogError(ex, "Uninstall manifest execution failed for resource {ResourceKey} via module {ModuleId}.", selectedResourceKey, module.Id);
                    lastError = ex.Message;
                    log?.Report($"Remove failed via {module.Name}: {ex.Message}");
                    _notifications.FailDownload(operationKey, $"Removal failed: {ex.Message}");
                }
            }

            // No source succeeded.
            _notifications.FailDownload(operationKey, lastError);
            return new DownloadInstallResult(false, lastError);
        }


        // Manifest execution

        /// <summary>
        /// Executes one install or uninstall manifest action-by-action.
        /// All actions share one temporary workspace, which is deleted afterwards even when an action fails.
        /// </summary>
        /// <param name="module">Module whose bridge declared the manifest; owns the named targets.</param>
        /// <param name="manifest">Normalized manifest with the ordered actions.</param>
        /// <param name="operationKey">Notification operation that receives progress.</param>
        /// <param name="log">Optional receiver of human-readable log lines.</param>
        /// <param name="ct">Cancellation token checked before every action.</param>
        internal async Task ExecuteManifestAsync(
            ModuleConfig module,
            ModuleDownloadInstallManifest manifest,
            string operationKey,
            IProgress<string>? log,
            CancellationToken ct)
        {
            // The temporary workspace holds artifacts downloaded by earlier actions for later ones.
            var context = new InstallExecutionContext(GetRootDirectory());
            Directory.CreateDirectory(context.TempDir);

            try
            {
                // Execute each manifest action in order inside one shared temp workspace.
                foreach (var action in manifest.Actions)
                {
                    ct.ThrowIfCancellationRequested();

                    var title = !string.IsNullOrWhiteSpace(action.Title)
                        ? action.Title
                        : action.Type;
                    log?.Report($"[{action.Type}] {title}");

                    // Only whitelisted action types run; any other type aborts the manifest.
                    switch (action.Type.Trim().ToLowerInvariant())
                    {
                        case "download_file":
                            await ExecuteDownloadFileAsync(module, manifest, action, context, operationKey, log, ct);
                            break;

                        case "extract_zip":
                            ExecuteExtractZip(module, manifest, action, context, operationKey, log);
                            break;

                        case "python_package":
                            await ExecutePythonPackageAsync(module, operationKey, action, log, ct);
                            break;

                        case "ollama_pull":
                            await ExecuteOllamaPullAsync(module, manifest, action, operationKey, log, ct);
                            break;

                        case "ollama_remove":
                            await ExecuteOllamaRemoveAsync(module, manifest, action, operationKey, log, ct);
                            break;

                        default:
                            throw new InvalidOperationException($"Unsupported download install action: {action.Type}");
                    }
                }
            }
            finally
            {
                context.TryCleanup();
            }
        }


        // Action execution

        /// <summary>
        /// Downloads one remote file either into a managed target or into the temporary artifact store.
        /// A file with an <c>artifactId</c> is registered in the workspace so later actions (such as extraction) can use it.
        /// </summary>
        /// <param name="module">Module that owns the named targets.</param>
        /// <param name="manifest">Manifest that supplies the default target reference.</param>
        /// <param name="action">The <c>download_file</c> action with the URL, destination and optional checksum.</param>
        /// <param name="context">Temporary workspace shared by the manifest actions.</param>
        /// <param name="operationKey">Notification operation that receives progress.</param>
        /// <param name="log">Optional receiver of human-readable log lines.</param>
        /// <param name="ct">Cancellation token for the transfer.</param>
        private async Task ExecuteDownloadFileAsync(
            ModuleConfig module,
            ModuleDownloadInstallManifest manifest,
            ModuleDownloadInstallAction action,
            InstallExecutionContext context,
            string operationKey,
            IProgress<string>? log,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(action.Url))
            {
                throw new InvalidOperationException("download_file action is missing 'url'.");
            }

            // Choose the destination: a path below a declared target (validated against traversal) or, for
            // intermediate files, a sanitized name in the temporary workspace.
            string destinationPath;
            var targetRef = GetEffectiveTargetRef(manifest, action);
            if (!string.IsNullOrWhiteSpace(targetRef))
            {
                var targetRoot = ResolveTargetDirectory(module, targetRef);
                var relativePath = !string.IsNullOrWhiteSpace(action.RelativePath)
                    ? action.RelativePath
                    : Path.GetFileName(new Uri(action.Url).LocalPath);
                destinationPath = ResolveChildPath(targetRoot, relativePath);
            }
            else if (!string.IsNullOrWhiteSpace(action.ArtifactId))
            {
                destinationPath = Path.Combine(context.TempDir, SanitizeFileName(action.ArtifactId));
            }
            else
            {
                throw new InvalidOperationException("download_file action must declare either targetRef or artifactId.");
            }

            // Start the transfer and tell the user what is being downloaded.
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            log?.Report($"Downloading {action.Url}");
            var downloadTitle = !string.IsNullOrWhiteSpace(action.Title)
                ? action.Title
                : Path.GetFileName(new Uri(action.Url).LocalPath);
            _notifications.ReportDownloadStatus(operationKey, $"Downloading {downloadTitle}");

            using var response = await _httpClient.GetAsync(action.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            // Stream the body straight to disk; the file is recreated, so an earlier partial file is discarded.
            var totalBytes = response.Content.Headers.ContentLength ?? 0;
            await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
            await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.Read, 65536, true);
            var buffer = new byte[65536];
            long downloadedBytes = 0;
            int bytesRead;
            var throttle = Stopwatch.StartNew();

            _notifications.ReportDownloadProgress(
                operationKey,
                new DownloadProgress(0, 0, totalBytes, downloadTitle));

            while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                downloadedBytes += bytesRead;

                // Throttle notification updates to one every 75 ms.
                if (throttle.ElapsedMilliseconds < 75)
                {
                    continue;
                }

                throttle.Restart();
                var fraction = totalBytes > 0 ? (double)downloadedBytes / totalBytes : 0;
                _notifications.ReportDownloadProgress(
                    operationKey,
                    new DownloadProgress(fraction, downloadedBytes, totalBytes, downloadTitle));
            }

            // Report a final 100% so the progress does not stay at the last throttled value.
            _notifications.ReportDownloadProgress(
                operationKey,
                new DownloadProgress(
                    1.0,
                    downloadedBytes,
                    totalBytes > 0 ? totalBytes : downloadedBytes,
                    downloadTitle));

            // Verify the checksum before the file is registered or used by any later action.
            if (!string.IsNullOrWhiteSpace(action.Sha256))
            {
                // Flush the write buffers so the concurrent reader below observes the full payload.
                await fileStream.FlushAsync(ct);
                var actualHash = await ComputeSha256Async(destinationPath, ct);
                if (!string.Equals(actualHash, action.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Checksum mismatch for {destinationPath}.");
                }
            }

            // Register the file under its artifact id so extract actions can find it.
            if (!string.IsNullOrWhiteSpace(action.ArtifactId))
            {
                context.Artifacts[action.ArtifactId] = destinationPath;
            }

            log?.Report($"Saved to {destinationPath}");
        }

        /// <summary>
        /// Extracts one previously downloaded ZIP artifact into a managed target directory.
        /// Entries that would land outside the destination are rejected.
        /// </summary>
        /// <param name="module">Module that owns the named targets.</param>
        /// <param name="manifest">Manifest that supplies the default target reference.</param>
        /// <param name="action">The <c>extract_zip</c> action naming the source artifact, target and optional subfolder.</param>
        /// <param name="context">Temporary workspace that holds the downloaded artifacts.</param>
        /// <param name="operationKey">Notification operation that receives status updates.</param>
        /// <param name="log">Optional receiver of human-readable log lines.</param>
        private void ExecuteExtractZip(
            ModuleConfig module,
            ModuleDownloadInstallManifest manifest,
            ModuleDownloadInstallAction action,
            InstallExecutionContext context,
            string operationKey,
            IProgress<string>? log)
        {
            _notifications.ReportDownloadStatus(operationKey, "Extracting archive…");

            // The archive must be an artifact that an earlier download_file action stored in the workspace.
            if (string.IsNullOrWhiteSpace(action.SourceArtifactId))
            {
                throw new InvalidOperationException("extract_zip action is missing 'sourceArtifactId'.");
            }

            if (!context.Artifacts.TryGetValue(action.SourceArtifactId, out var sourcePath) ||
                !File.Exists(sourcePath))
            {
                throw new InvalidOperationException($"Artifact '{action.SourceArtifactId}' was not found for extract_zip.");
            }

            // Resolve the extraction directory below a declared target, rejecting traversal out of it.
            var targetRef = GetEffectiveTargetRef(manifest, action);
            if (string.IsNullOrWhiteSpace(targetRef))
            {
                throw new InvalidOperationException("extract_zip action is missing targetRef.");
            }

            var targetRoot = ResolveTargetDirectory(module, targetRef);
            var destination = string.IsNullOrWhiteSpace(action.RelativePath)
                ? targetRoot
                : ResolveChildPath(targetRoot, action.RelativePath);

            Directory.CreateDirectory(destination);
            log?.Report($"Extracting {sourcePath} to {destination}");

            // Extract entry by entry instead of the whole archive, so each path can be checked against the
            // destination first (zip-slip protection). Existing files are overwritten.
            var destinationPrefix = EnsureTrailingSeparator(Path.GetFullPath(destination));
            using var archive = ZipFile.OpenRead(sourcePath);
            foreach (var entry in archive.Entries)
            {
                var targetPath = Path.GetFullPath(Path.Combine(destination, entry.FullName));
                if (!targetPath.StartsWith(destinationPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"ZIP entry '{entry.FullName}' escapes the destination directory.");
                }

                // Entries without a file name are directories.
                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(targetPath);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                entry.ExtractToFile(targetPath, overwrite: true);
            }
        }

        /// <summary>
        /// Installs one set of Python packages through the declared engine package manager.
        /// When the engine declares a module environment, the packages go into the module's own environment,
        /// which is created on demand.
        /// </summary>
        /// <param name="module">Module whose environment receives the packages.</param>
        /// <param name="operationKey">Notification operation that receives status updates.</param>
        /// <param name="action">The <c>python_package</c> action listing the packages and optionally the engine.</param>
        /// <param name="log">Optional receiver of human-readable log lines.</param>
        /// <param name="ct">Cancellation token for the environment setup and the package manager run.</param>
        private async Task ExecutePythonPackageAsync(
            ModuleConfig module,
            string operationKey,
            ModuleDownloadInstallAction action,
            IProgress<string>? log,
            CancellationToken ct)
        {
            _notifications.ReportDownloadStatus(
                operationKey,
                $"Installing Python packages ({action.Packages.Count})…");

            // Default to the shared Python runtime engine when the action names no engine.
            var engineId = !string.IsNullOrWhiteSpace(action.EngineId)
                ? action.EngineId
                : "python-runtime";

            if (action.Packages.Count == 0)
            {
                throw new InvalidOperationException("python_package action does not declare any packages.");
            }

            // The engine must be installed and must know how to install packages.
            var engineConfig = _engineInstaller.GetEngineConfig(engineId);
            if (engineConfig == null)
            {
                throw new InvalidOperationException($"Engine '{engineId}' is not installed.");
            }

            if (engineConfig.PackageManager == null &&
                string.IsNullOrWhiteSpace(engineConfig.ModuleEnvironment?.PackageManagerCommand))
            {
                throw new InvalidOperationException($"Engine '{engineId}' does not declare a package manager.");
            }

            // Make sure the module environment exists (if the engine uses one), then run the package manager.
            var environment = await _environmentResolver.EnsureEnvironmentAsync(module, engineConfig, log, ct);
            var executablePath = _environmentResolver.ResolvePackageManagerExecutable(environment, engineConfig);
            var arguments = _environmentResolver.BuildPackageInstallArguments(environment, engineConfig, action.Packages);
            var environmentVariables = environment?.EnvironmentVariables;

            await RunProcessAsync(
                executablePath,
                arguments,
                Path.GetDirectoryName(module.SourcePath) ?? GetRootDirectory(),
                log,
                ct,
                environmentVariables?.ToDictionary(pair => pair.Key, pair => (string?)pair.Value, StringComparer.OrdinalIgnoreCase));
        }


        // Ollama actions

        /// <summary>
        /// Pulls one Ollama model into a managed models directory by running a temporary local Ollama service.
        /// </summary>
        /// <param name="module">Module that owns the named targets.</param>
        /// <param name="manifest">Manifest that supplies the default model name and target reference.</param>
        /// <param name="action">The <c>ollama_pull</c> action naming the model and optionally the engine.</param>
        /// <param name="operationKey">Notification operation that receives progress.</param>
        /// <param name="log">Optional receiver of human-readable log lines.</param>
        /// <param name="ct">Cancellation token for startup and the pull.</param>
        private async Task ExecuteOllamaPullAsync(
            ModuleConfig module,
            ModuleDownloadInstallManifest manifest,
            ModuleDownloadInstallAction action,
            string operationKey,
            IProgress<string>? log,
            CancellationToken ct)
        {
            // Resolve the model name; the manifest resource key is the fallback.
            var modelName = !string.IsNullOrWhiteSpace(action.Model)
                ? action.Model
                : manifest.ResourceKey;
            if (string.IsNullOrWhiteSpace(modelName))
            {
                throw new InvalidOperationException("ollama_pull action is missing 'model'.");
            }

            _notifications.ReportDownloadStatus(operationKey, $"Preparing Ollama: {modelName}");

            // The models directory comes from a declared target, so the pull stays inside ASLM-managed storage.
            var targetRef = GetEffectiveTargetRef(manifest, action);
            if (string.IsNullOrWhiteSpace(targetRef))
            {
                throw new InvalidOperationException("ollama_pull action is missing targetRef.");
            }

            var modelsDirectory = ResolveTargetDirectory(module, targetRef);
            Directory.CreateDirectory(modelsDirectory);

            // Locate the Ollama engine executable and pick a free loopback port for the temporary service.
            var engineId = !string.IsNullOrWhiteSpace(action.EngineId)
                ? action.EngineId
                : "ollama-service";

            var executablePath = _engineInstaller.GetEngineExecutablePath(engineId)
                ?? throw new InvalidOperationException($"Engine '{engineId}' is not installed.");

            var engineWorkingDirectory = Path.GetDirectoryName(executablePath) ?? GetRootDirectory();
            var port = AllocateLocalPort();

            // Configure `ollama serve` to listen on that port and store models in the managed directory.
            var psi = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = "serve",
                WorkingDirectory = engineWorkingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            psi.Environment["OLLAMA_HOST"] = $"127.0.0.1:{port}";
            psi.Environment["OLLAMA_MODELS"] = modelsDirectory;

            // Start the service and forward its console output to the log.
            using var process = new Process { StartInfo = psi };
            process.OutputDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                {
                    log?.Report($"[ollama] {args.Data}");
                }
            };
            process.ErrorDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                {
                    log?.Report($"[ollama] {args.Data}");
                }
            };

            if (!process.Start())
            {
                throw new InvalidOperationException("Temporary Ollama runtime could not be started.");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Pull through the HTTP API once the service answers; the process is always stopped afterwards.
            try
            {
                await WaitForOllamaAsync(port, ct);
                _notifications.ReportDownloadProgress(
                    operationKey,
                    new DownloadProgress(0, 0, 0, modelName),
                    $"Pulling {modelName}");
                await StreamOllamaPullAsync(port, modelName, operationKey, log, ct);
            }
            finally
            {
                TryStopProcess(process);
            }
        }

        /// <summary>
        /// Removes one Ollama model from the managed models directory by running a temporary local Ollama service
        /// and calling its HTTP delete API.
        /// </summary>
        /// <param name="module">Module that owns the named targets.</param>
        /// <param name="manifest">Manifest that supplies the default model name and target reference.</param>
        /// <param name="action">The <c>ollama_remove</c> action naming the model and optionally the engine.</param>
        /// <param name="operationKey">Notification operation that receives status updates.</param>
        /// <param name="log">Optional receiver of human-readable log lines.</param>
        /// <param name="ct">Cancellation token for startup and the deletion.</param>
        private async Task ExecuteOllamaRemoveAsync(
            ModuleConfig module,
            ModuleDownloadInstallManifest manifest,
            ModuleDownloadInstallAction action,
            string operationKey,
            IProgress<string>? log,
            CancellationToken ct)
        {
            // Resolve the model name; the manifest resource key is the fallback.
            var modelName = !string.IsNullOrWhiteSpace(action.Model)
                ? action.Model
                : manifest.ResourceKey;
            if (string.IsNullOrWhiteSpace(modelName))
            {
                throw new InvalidOperationException("ollama_remove action is missing 'model'.");
            }

            _notifications.ReportDownloadStatus(operationKey, $"Removing Ollama model {modelName}…");

            // The models directory comes from a declared target, so the removal stays inside ASLM-managed storage.
            var targetRef = GetEffectiveTargetRef(manifest, action);
            if (string.IsNullOrWhiteSpace(targetRef))
            {
                throw new InvalidOperationException("ollama_remove action is missing targetRef.");
            }

            var modelsDirectory = ResolveTargetDirectory(module, targetRef);
            Directory.CreateDirectory(modelsDirectory);

            // Locate the Ollama engine executable and pick a free loopback port for the temporary service.
            var engineId = !string.IsNullOrWhiteSpace(action.EngineId)
                ? action.EngineId
                : "ollama-service";

            var executablePath = _engineInstaller.GetEngineExecutablePath(engineId)
                ?? throw new InvalidOperationException($"Engine '{engineId}' is not installed.");

            var engineWorkingDirectory = Path.GetDirectoryName(executablePath) ?? GetRootDirectory();
            var port = AllocateLocalPort();

            // Configure `ollama serve` to listen on that port and use the managed models directory.
            var psi = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = "serve",
                WorkingDirectory = engineWorkingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            psi.Environment["OLLAMA_HOST"] = $"127.0.0.1:{port}";
            psi.Environment["OLLAMA_MODELS"] = modelsDirectory;

            // Start the service and forward its console output to the log.
            using var process = new Process { StartInfo = psi };
            process.OutputDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                {
                    log?.Report($"[ollama] {args.Data}");
                }
            };
            process.ErrorDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                {
                    log?.Report($"[ollama] {args.Data}");
                }
            };

            if (!process.Start())
            {
                throw new InvalidOperationException("Temporary Ollama runtime could not be started.");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Delete through the HTTP API once the service answers; the process is always stopped afterwards.
            try
            {
                await WaitForOllamaAsync(port, ct);
                await DeleteOllamaModelAsync(port, modelName, ct);
            }
            finally
            {
                TryStopProcess(process);
            }
        }

        /// <summary>
        /// Waits until the temporary Ollama HTTP endpoint becomes available.
        /// Polls the model list endpoint every 500 ms and gives up after 25 seconds.
        /// </summary>
        /// <param name="port">Loopback port the temporary service listens on.</param>
        /// <param name="ct">Cancellation token for the wait.</param>
        private async Task WaitForOllamaAsync(int port, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow.AddSeconds(25);
            var url = $"http://127.0.0.1:{port}/api/tags";

            // An HTTP 200 answer means the service accepts requests; connection errors are retried.
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    using var response = await _httpClient.GetAsync(url, ct);
                    if (response.StatusCode is HttpStatusCode.OK)
                    {
                        return;
                    }
                }
                catch
                {
                    // Ignore transient bootstrap failures while the runtime starts.
                }

                await Task.Delay(500, ct);
            }

            throw new InvalidOperationException("Temporary Ollama runtime did not become ready in time.");
        }

        /// <summary>
        /// Streams one Ollama pull operation over the HTTP API and forwards progress into the UI log.
        /// The API answers with one JSON object per line; an <c>error</c> object fails the pull.
        /// </summary>
        /// <param name="port">Loopback port the temporary service listens on.</param>
        /// <param name="modelName">Model to pull.</param>
        /// <param name="operationKey">Notification operation that receives progress.</param>
        /// <param name="log">Optional receiver of human-readable log lines.</param>
        /// <param name="ct">Cancellation token for the stream.</param>
        private async Task StreamOllamaPullAsync(
            int port,
            string modelName,
            string operationKey,
            IProgress<string>? log,
            CancellationToken ct)
        {
            // Start a streaming pull request.
            var requestPayload = JsonSerializer.Serialize(new { model = modelName, stream = true }, _jsonOptions);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/api/pull")
            {
                Content = new StringContent(requestPayload, Encoding.UTF8, new MediaTypeHeaderValue("application/json"))
            };

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            // Notification updates are throttled: byte progress more often than status-only lines.
            var uiThrottle = Stopwatch.StartNew();
            const int progressIntervalMs = 100;
            const int statusOnlyIntervalMs = 400;

            string? line;
            while ((line = await reader.ReadLineAsync(ct)) != null)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                // Each line is a status object, or an error object that aborts the pull.
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;

                if (root.TryGetProperty("error", out var errorElement))
                {
                    throw new InvalidOperationException(errorElement.GetString() ?? "Ollama pull failed.");
                }

                var status = root.TryGetProperty("status", out var statusElement)
                    ? statusElement.GetString() ?? string.Empty
                    : string.Empty;

                var completed = root.TryGetProperty("completed", out var completedElement) && completedElement.TryGetInt64(out var completedValue)
                    ? completedValue
                    : 0;
                var total = root.TryGetProperty("total", out var totalElement) && totalElement.TryGetInt64(out var totalValue)
                    ? totalValue
                    : 0;

                // Layer downloads report completed/total bytes; other steps report only a status text.
                if (total > 0 && completed >= 0)
                {
                    var percent = Math.Clamp((double)completed / total * 100.0, 0, 100);
                    log?.Report($"[ollama] {status} ({percent:F1}%)");

                    if (uiThrottle.ElapsedMilliseconds >= progressIntervalMs)
                    {
                        uiThrottle.Restart();
                        var fraction = Math.Clamp(completed / (double)total, 0, 1);
                        var statusLine = string.IsNullOrWhiteSpace(status)
                            ? "Pulling layers"
                            : TruncateForNotificationStatus(status);
                        _notifications.ReportDownloadProgress(
                            operationKey,
                            new DownloadProgress(fraction, completed, total, modelName),
                            statusLine);
                    }
                }
                else if (!string.IsNullOrWhiteSpace(status))
                {
                    log?.Report($"[ollama] {status}");
                    if (uiThrottle.ElapsedMilliseconds >= statusOnlyIntervalMs)
                    {
                        uiThrottle.Restart();
                        _notifications.ReportDownloadProgress(
                            operationKey,
                            new DownloadProgress(0, 0, 0, modelName),
                            TruncateForNotificationStatus(status));
                    }
                }
            }
        }

        /// <summary>
        /// Shortens long Ollama status strings for the notification status line.
        /// Strings above the limit are cut and end with an ellipsis.
        /// </summary>
        private static string TruncateForNotificationStatus(string status)
        {
            const int maxLen = 140;
            if (status.Length <= maxLen)
            {
                return status;
            }

            return string.Concat(status.AsSpan(0, maxLen - 1), "…");
        }

        /// <summary>
        /// Deletes one Ollama model via the local HTTP API.
        /// </summary>
        /// <param name="port">Loopback port the temporary service listens on.</param>
        /// <param name="modelName">Model to delete.</param>
        /// <param name="ct">Cancellation token for the request.</param>
        private async Task DeleteOllamaModelAsync(int port, string modelName, CancellationToken ct)
        {
            var requestPayload = JsonSerializer.Serialize(new { model = modelName }, _jsonOptions);
            using var request = new HttpRequestMessage(HttpMethod.Delete, $"http://127.0.0.1:{port}/api/delete")
            {
                Content = new StringContent(requestPayload, Encoding.UTF8, new MediaTypeHeaderValue("application/json"))
            };

            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
        }


        // Process execution

        /// <summary>
        /// Runs one external process and forwards its output to the caller log.
        /// Throws when the process cannot be started or exits with a non-zero code.
        /// </summary>
        /// <param name="fileName">Executable to start.</param>
        /// <param name="arguments">Command-line argument string.</param>
        /// <param name="workingDirectory">Working directory of the process.</param>
        /// <param name="log">Optional receiver of the process output lines.</param>
        /// <param name="ct">Cancellation token for reading output and waiting for exit.</param>
        /// <param name="environment">Extra environment variables; <c>{path}</c> in a value expands to the current <c>PATH</c>.</param>
        private static async Task RunProcessAsync(
            string fileName,
            string arguments,
            string workingDirectory,
            IProgress<string>? log,
            CancellationToken ct,
            IDictionary<string, string?>? environment = null)
        {
            // Redirect all output so it can be forwarded to the log.
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            // Python gets unbuffered UTF-8 output; then the caller's variables are applied on top.
            ConfigurePythonProcess(psi, fileName);

            if (environment != null)
            {
                foreach (var pair in environment)
                {
                    var value = (pair.Value ?? string.Empty)
                        .Replace("{path}", GetProcessEnvironmentValue(psi, "PATH"), StringComparison.OrdinalIgnoreCase);
                    psi.Environment[pair.Key] = value;
                }
            }

            // Start the process and read stdout and stderr concurrently so neither pipe blocks the child.
            using var process = new Process { StartInfo = psi };
            if (!process.Start())
            {
                throw new InvalidOperationException($"Process could not be started: {fileName}");
            }

            var stdoutTask = Task.Run(async () =>
            {
                while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        log?.Report(line);
                    }
                }
            }, ct);

            var stderrTask = Task.Run(async () =>
            {
                while (await process.StandardError.ReadLineAsync(ct) is { } line)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        log?.Report(line);
                    }
                }
            }, ct);

            // Wait for both streams to end and the process to exit, then fail on a non-zero exit code.
            await Task.WhenAll(stdoutTask, stderrTask);
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"Process exited with code {process.ExitCode}: {Path.GetFileName(fileName)} {arguments}");
            }
        }

        /// <summary>
        /// Returns one environment value from a process start info using Windows-friendly key matching.
        /// Falls back to the current process environment when the start info does not define the key.
        /// </summary>
        private static string GetProcessEnvironmentValue(ProcessStartInfo psi, string key)
        {
            foreach (var pair in psi.Environment)
            {
                if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    return pair.Value ?? string.Empty;
                }
            }

            return Environment.GetEnvironmentVariable(key) ?? string.Empty;
        }


        // Target resolution

        /// <summary>
        /// Resolves the effective target reference for one action: the action's own reference wins over the
        /// manifest default.
        /// </summary>
        private static string GetEffectiveTargetRef(ModuleDownloadInstallManifest manifest, ModuleDownloadInstallAction action)
        {
            return !string.IsNullOrWhiteSpace(action.TargetRef)
                ? action.TargetRef
                : manifest.TargetRef;
        }

        /// <summary>
        /// Resolves one named target reference into a safe absolute directory inside ASLM.
        /// </summary>
        /// <param name="module">Module whose downloads bridge declares the target.</param>
        /// <param name="targetRef">Name of a target declared by the module.</param>
        internal string ResolveTargetDirectory(ModuleConfig module, string targetRef)
        {
            // A module can only use targets it declared itself.
            var bridge = module.DownloadsBridge
                ?? throw new InvalidOperationException("Module does not declare a downloads bridge.");

            if (!bridge.Targets.TryGetValue(targetRef, out var target) || target == null)
            {
                throw new InvalidOperationException($"Target '{targetRef}' is not declared by module '{module.Name}'.");
            }

            // Map the target's root bucket to a fixed ASLM directory, then resolve the relative part below it
            // (traversal out of the bucket is rejected).
            var rootDir = GetRootDirectory();
            var rootBucket = target.Root.Trim().ToLowerInvariant();

            var bucketRoot = rootBucket switch
            {
                "root" => rootDir,
                "data" => Path.Combine(rootDir, "Data"),
                "models" => Path.Combine(rootDir, "Models"),
                "tools" => Path.Combine(rootDir, "Tools"),
                "modules" => Path.Combine(rootDir, "Modules"),
                "engines" => Path.Combine(rootDir, "Engines"),
                _ => throw new InvalidOperationException($"Unsupported target root '{target.Root}'.")
            };

            return ResolveChildPath(bucketRoot, target.Relative);
        }

        /// <summary>
        /// Resolves one relative child path and rejects directory traversal outside the requested root.
        /// </summary>
        /// <param name="rootPath">Directory the result must stay inside.</param>
        /// <param name="relativePath">Path below the root; absolute paths and <c>..</c> escapes are rejected.</param>
        internal static string ResolveChildPath(string rootPath, string relativePath)
        {
            // Absolute paths would ignore the root, so they are never allowed.
            var normalizedRoot = EnsureTrailingSeparator(Path.GetFullPath(rootPath));
            var safeRelative = relativePath ?? string.Empty;
            if (Path.IsPathRooted(safeRelative))
            {
                throw new InvalidOperationException("Absolute install paths are not allowed in download manifests.");
            }

            // Compare full paths with a trailing separator so a sibling like "root2" cannot pass as inside "root".
            var combined = Path.GetFullPath(Path.Combine(normalizedRoot, safeRelative));
            var comparePath = EnsureTrailingSeparator(combined);
            if (!comparePath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Path '{relativePath}' escapes the managed target root.");
            }

            return combined;
        }

        /// <summary>
        /// Ensures directory paths end with a trailing separator for secure prefix checks.
        /// </summary>
        private static string EnsureTrailingSeparator(string path)
        {
            if (!path.EndsWith(Path.DirectorySeparatorChar) && !path.EndsWith(Path.AltDirectorySeparatorChar))
            {
                return path + Path.DirectorySeparatorChar;
            }

            return path;
        }

        /// <summary>
        /// Returns a filesystem-safe filename for temporary artifact entries.
        /// </summary>
        private static string SanitizeFileName(string name)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            var sanitized = new string(name.Select(character => invalidChars.Contains(character) ? '_' : character).ToArray());
            return string.IsNullOrWhiteSpace(sanitized) ? Guid.NewGuid().ToString("N") : sanitized;
        }

        /// <summary>
        /// Computes the SHA-256 hash for one file, tolerating a writer that still holds it open.
        /// </summary>
        private static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct)
        {
            await using var stream = new FileStream(
                filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var hashBytes = await SHA256.HashDataAsync(stream, ct);
            return Convert.ToHexStringLower(hashBytes);
        }

        /// <summary>
        /// Returns a currently free localhost TCP port.
        /// </summary>
        private static int AllocateLocalPort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        /// <summary>
        /// Stops one process tree on a best-effort basis.
        /// </summary>
        private static void TryStopProcess(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                    process.WaitForExit(5000);
                }
            }
            catch
            {
                // Best effort cleanup only.
            }
        }

        /// <summary>
        /// Tunes Python-based process launches so redirected output stays responsive and readable.
        /// </summary>
        private static void ConfigurePythonProcess(ProcessStartInfo psi, string fileName)
        {
            var executableName = Path.GetFileName(fileName);
            if (string.IsNullOrWhiteSpace(executableName))
            {
                return;
            }

            // Only python and the py launcher are tuned; other executables are left unchanged.
            var isPython = executableName.StartsWith("python", StringComparison.OrdinalIgnoreCase) ||
                           executableName.StartsWith("py", StringComparison.OrdinalIgnoreCase);
            if (!isPython)
            {
                return;
            }

            // Add the unbuffered flag (-u) unless present, so output reaches the log line by line.
            if (!psi.Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Any(argument => string.Equals(argument, "-u", StringComparison.OrdinalIgnoreCase)))
            {
                psi.Arguments = string.IsNullOrWhiteSpace(psi.Arguments)
                    ? "-u"
                    : $"-u {psi.Arguments}";
            }

            // Force UTF-8 text I/O regardless of the Windows locale.
            psi.Environment["PYTHONUNBUFFERED"] = "1";
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            psi.Environment["PYTHONUTF8"] = "1";
        }


        // Path helpers

        /// <summary>
        /// Returns the application root directory above the deployed App folder.
        /// </summary>
        private static string GetRootDirectory()
        {
            return AppRoot.Directory;
        }


        // Install execution context

        /// <summary>
        /// Stores temporary state used during one install manifest execution.
        /// </summary>
        private sealed class InstallExecutionContext
        {
            // Initialization

            /// <summary>
            /// Creates a context with a fresh, uniquely named temporary directory path.
            /// </summary>
            /// <param name="rootDirectory">ASLM root directory.</param>
            public InstallExecutionContext(string rootDirectory)
            {
                TempDir = Path.Combine(Path.GetTempPath(), "ASLM_Downloads", Guid.NewGuid().ToString("N"));
                Artifacts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }


            // State

            /// <summary>
            /// Temporary workspace for the artifacts of one manifest run; deleted by <see cref="TryCleanup"/>.
            /// </summary>
            public string TempDir { get; }

            /// <summary>
            /// Downloaded files that later actions can refer to, by artifact id.
            /// </summary>
            public Dictionary<string, string> Artifacts { get; }


            // Cleanup

            /// <summary>
            /// Deletes the temporary workspace; failures are ignored.
            /// </summary>
            public void TryCleanup()
            {
                try
                {
                    if (Directory.Exists(TempDir))
                    {
                        Directory.Delete(TempDir, true);
                    }
                }
                catch
                {
                    // Best effort cleanup only.
                }
            }
        }
    }
}
