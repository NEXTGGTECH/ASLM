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
        private readonly ModuleInstaller _moduleInstaller;
        private readonly ModuleDownloadBridge _bridge;
        private readonly EngineInstaller _engineInstaller;
        private readonly ModuleEnvironmentResolver _environmentResolver;
        private readonly DownloadStateStore _stateStore;
        private readonly NotificationCenter _notifications;
        private readonly ILogger<DownloadInstaller> _logger;
        private readonly HttpClient _httpClient = new();

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
            ILogger<DownloadInstaller> logger)
        {
            _moduleInstaller = moduleInstaller;
            _bridge = bridge;
            _engineInstaller = engineInstaller;
            _environmentResolver = environmentResolver;
            _stateStore = stateStore;
            _notifications = notifications;
            _logger = logger;
        }


        // Catalog install

        /// <summary>
        /// Installs one shared catalog item using the first module source that resolves a valid install manifest.
        /// </summary>
        public async Task<DownloadInstallResult> InstallAsync(
            DownloadCatalogItem item,
            DownloadCatalogVariant? selectedVariant,
            IProgress<string>? log = null,
            CancellationToken ct = default)
        {
            if (item.Sources.Count == 0)
            {
                return new DownloadInstallResult(false, "No module source is available for this download item.");
            }

            var selectedResourceKey = !string.IsNullOrWhiteSpace(selectedVariant?.ResourceKey)
                ? selectedVariant!.ResourceKey
                : !string.IsNullOrWhiteSpace(item.DefaultVariantResourceKey)
                    ? item.DefaultVariantResourceKey
                    : item.ResourceKey;
            var selectedTitle = !string.IsNullOrWhiteSpace(selectedVariant?.Title)
                ? selectedVariant!.Title
                : item.Title;
            var selectedVersion = !string.IsNullOrWhiteSpace(selectedVariant?.Version)
                ? selectedVariant!.Version
                : item.Version;
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

            var lastError = "No module source produced an install manifest.";

            foreach (var source in item.Sources)
            {
                ct.ThrowIfCancellationRequested();

                var module = await _moduleInstaller.LoadModuleConfig(source.ModuleSourcePath);
                if (module == null)
                {
                    lastError = $"Module manifest could not be loaded: {source.ModuleSourcePath}";
                    continue;
                }

                ModuleDownloadInstallManifest? manifest;
                try
                {
                    manifest = await _bridge.ResolveInstallAsync(module, source.CategoryId, selectedResourceKey, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to resolve install manifest for resource {ResourceKey} via module {ModuleId}.", selectedResourceKey, module.Id);
                    lastError = ex.Message;
                    continue;
                }

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
                    var effectiveTitle = !string.IsNullOrWhiteSpace(manifest.Title) ? manifest.Title : selectedTitle;
                    log?.Report($"Installing {effectiveTitle} via {module.Name}...");

                    await ExecuteManifestAsync(module, manifest, operationKey, log, ct);

                    var version = !string.IsNullOrWhiteSpace(manifest.Version)
                        ? manifest.Version
                        : selectedVersion;

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
                    _logger.LogError(ex, "Install manifest execution failed for resource {ResourceKey} via module {ModuleId}.", selectedResourceKey, module.Id);
                    lastError = ex.Message;
                    log?.Report($"Install failed via {module.Name}: {ex.Message}");
                    _notifications.FailDownload(operationKey, $"Install failed: {ex.Message}");
                }
            }

            _notifications.FailDownload(operationKey, lastError);
            return new DownloadInstallResult(false, lastError);
        }

        /// <summary>
        /// Removes one shared catalog variant using the first module source that resolves a valid uninstall manifest.
        /// </summary>
        public async Task<DownloadInstallResult> UninstallAsync(
            DownloadCatalogItem item,
            DownloadCatalogVariant? selectedVariant,
            IProgress<string>? log = null,
            CancellationToken ct = default)
        {
            if (item.Sources.Count == 0)
            {
                return new DownloadInstallResult(false, "No module source is available for this download item.");
            }

            var selectedResourceKey = !string.IsNullOrWhiteSpace(selectedVariant?.ResourceKey)
                ? selectedVariant!.ResourceKey
                : !string.IsNullOrWhiteSpace(item.DefaultVariantResourceKey)
                    ? item.DefaultVariantResourceKey
                    : item.ResourceKey;
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

            var lastError = "No module source produced an uninstall manifest.";

            foreach (var source in item.Sources)
            {
                ct.ThrowIfCancellationRequested();

                var module = await _moduleInstaller.LoadModuleConfig(source.ModuleSourcePath);
                if (module == null)
                {
                    lastError = $"Module manifest could not be loaded: {source.ModuleSourcePath}";
                    continue;
                }

                ModuleDownloadInstallManifest? manifest;
                try
                {
                    manifest = await _bridge.ResolveUninstallAsync(module, source.CategoryId, selectedResourceKey, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to resolve uninstall manifest for resource {ResourceKey} via module {ModuleId}.", selectedResourceKey, module.Id);
                    lastError = ex.Message;
                    continue;
                }

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
                    _logger.LogError(ex, "Uninstall manifest execution failed for resource {ResourceKey} via module {ModuleId}.", selectedResourceKey, module.Id);
                    lastError = ex.Message;
                    log?.Report($"Remove failed via {module.Name}: {ex.Message}");
                    _notifications.FailDownload(operationKey, $"Removal failed: {ex.Message}");
                }
            }

            _notifications.FailDownload(operationKey, lastError);
            return new DownloadInstallResult(false, lastError);
        }


        // Manifest execution

        /// <summary>
        /// Executes one install manifest action-by-action.
        /// </summary>
        private async Task ExecuteManifestAsync(
            ModuleConfig module,
            ModuleDownloadInstallManifest manifest,
            string operationKey,
            IProgress<string>? log,
            CancellationToken ct)
        {
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
        /// </summary>
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

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            log?.Report($"Downloading {action.Url}");
            var downloadTitle = !string.IsNullOrWhiteSpace(action.Title)
                ? action.Title
                : Path.GetFileName(new Uri(action.Url).LocalPath);
            _notifications.ReportDownloadStatus(operationKey, $"Downloading {downloadTitle}");

            using var response = await _httpClient.GetAsync(action.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? 0;
            await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
            await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true);
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

            _notifications.ReportDownloadProgress(
                operationKey,
                new DownloadProgress(
                    1.0,
                    downloadedBytes,
                    totalBytes > 0 ? totalBytes : downloadedBytes,
                    downloadTitle));

            if (!string.IsNullOrWhiteSpace(action.Sha256))
            {
                var actualHash = await ComputeSha256Async(destinationPath, ct);
                if (!string.Equals(actualHash, action.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Checksum mismatch for {destinationPath}.");
                }
            }

            if (!string.IsNullOrWhiteSpace(action.ArtifactId))
            {
                context.Artifacts[action.ArtifactId] = destinationPath;
            }

            log?.Report($"Saved to {destinationPath}");
        }

        /// <summary>
        /// Extracts one previously downloaded ZIP artifact into a managed target directory.
        /// </summary>
        private void ExecuteExtractZip(
            ModuleConfig module,
            ModuleDownloadInstallManifest manifest,
            ModuleDownloadInstallAction action,
            InstallExecutionContext context,
            string operationKey,
            IProgress<string>? log)
        {
            _notifications.ReportDownloadStatus(operationKey, "Extracting archive…");

            if (string.IsNullOrWhiteSpace(action.SourceArtifactId))
            {
                throw new InvalidOperationException("extract_zip action is missing 'sourceArtifactId'.");
            }

            if (!context.Artifacts.TryGetValue(action.SourceArtifactId, out var sourcePath) ||
                !File.Exists(sourcePath))
            {
                throw new InvalidOperationException($"Artifact '{action.SourceArtifactId}' was not found for extract_zip.");
            }

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

            var destinationPrefix = EnsureTrailingSeparator(Path.GetFullPath(destination));
            using var archive = ZipFile.OpenRead(sourcePath);
            foreach (var entry in archive.Entries)
            {
                var targetPath = Path.GetFullPath(Path.Combine(destination, entry.FullName));
                if (!targetPath.StartsWith(destinationPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"ZIP entry '{entry.FullName}' escapes the destination directory.");
                }

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
        /// </summary>
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

            var engineId = !string.IsNullOrWhiteSpace(action.EngineId)
                ? action.EngineId
                : "python-runtime";

            if (action.Packages.Count == 0)
            {
                throw new InvalidOperationException("python_package action does not declare any packages.");
            }

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
        private async Task ExecuteOllamaPullAsync(
            ModuleConfig module,
            ModuleDownloadInstallManifest manifest,
            ModuleDownloadInstallAction action,
            string operationKey,
            IProgress<string>? log,
            CancellationToken ct)
        {
            var modelName = !string.IsNullOrWhiteSpace(action.Model)
                ? action.Model
                : manifest.ResourceKey;
            if (string.IsNullOrWhiteSpace(modelName))
            {
                throw new InvalidOperationException("ollama_pull action is missing 'model'.");
            }

            _notifications.ReportDownloadStatus(operationKey, $"Preparing Ollama: {modelName}");

            var targetRef = GetEffectiveTargetRef(manifest, action);
            if (string.IsNullOrWhiteSpace(targetRef))
            {
                throw new InvalidOperationException("ollama_pull action is missing targetRef.");
            }

            var modelsDirectory = ResolveTargetDirectory(module, targetRef);
            Directory.CreateDirectory(modelsDirectory);

            var engineId = !string.IsNullOrWhiteSpace(action.EngineId)
                ? action.EngineId
                : "ollama-service";

            var executablePath = _engineInstaller.GetEngineExecutablePath(engineId)
                ?? throw new InvalidOperationException($"Engine '{engineId}' is not installed.");

            var engineWorkingDirectory = Path.GetDirectoryName(executablePath) ?? GetRootDirectory();
            var port = AllocateLocalPort();

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
        /// Removes one Ollama model from the managed models directory via the engine CLI.
        /// </summary>
        private async Task ExecuteOllamaRemoveAsync(
            ModuleConfig module,
            ModuleDownloadInstallManifest manifest,
            ModuleDownloadInstallAction action,
            string operationKey,
            IProgress<string>? log,
            CancellationToken ct)
        {
            var modelName = !string.IsNullOrWhiteSpace(action.Model)
                ? action.Model
                : manifest.ResourceKey;
            if (string.IsNullOrWhiteSpace(modelName))
            {
                throw new InvalidOperationException("ollama_remove action is missing 'model'.");
            }

            _notifications.ReportDownloadStatus(operationKey, $"Removing Ollama model {modelName}…");

            var targetRef = GetEffectiveTargetRef(manifest, action);
            if (string.IsNullOrWhiteSpace(targetRef))
            {
                throw new InvalidOperationException("ollama_remove action is missing targetRef.");
            }

            var modelsDirectory = ResolveTargetDirectory(module, targetRef);
            Directory.CreateDirectory(modelsDirectory);

            var engineId = !string.IsNullOrWhiteSpace(action.EngineId)
                ? action.EngineId
                : "ollama-service";

            var executablePath = _engineInstaller.GetEngineExecutablePath(engineId)
                ?? throw new InvalidOperationException($"Engine '{engineId}' is not installed.");

            var engineWorkingDirectory = Path.GetDirectoryName(executablePath) ?? GetRootDirectory();
            var port = AllocateLocalPort();

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
        /// </summary>
        private async Task WaitForOllamaAsync(int port, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow.AddSeconds(25);
            var url = $"http://127.0.0.1:{port}/api/tags";

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
        /// </summary>
        private async Task StreamOllamaPullAsync(
            int port,
            string modelName,
            string operationKey,
            IProgress<string>? log,
            CancellationToken ct)
        {
            var requestPayload = JsonSerializer.Serialize(new { model = modelName, stream = true }, _jsonOptions);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/api/pull")
            {
                Content = new StringContent(requestPayload, Encoding.UTF8, new MediaTypeHeaderValue("application/json"))
            };

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);

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
        /// </summary>
        private static async Task RunProcessAsync(
            string fileName,
            string arguments,
            string workingDirectory,
            IProgress<string>? log,
            CancellationToken ct,
            IDictionary<string, string?>? environment = null)
        {
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

            await Task.WhenAll(stdoutTask, stderrTask);
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"Process exited with code {process.ExitCode}: {Path.GetFileName(fileName)} {arguments}");
            }
        }

        /// <summary>
        /// Returns one environment value from a process start info using Windows-friendly key matching.
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
        /// Resolves the effective target reference for one action.
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
        private string ResolveTargetDirectory(ModuleConfig module, string targetRef)
        {
            var bridge = module.DownloadsBridge
                ?? throw new InvalidOperationException("Module does not declare a downloads bridge.");

            if (!bridge.Targets.TryGetValue(targetRef, out var target) || target == null)
            {
                throw new InvalidOperationException($"Target '{targetRef}' is not declared by module '{module.Name}'.");
            }

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
        private static string ResolveChildPath(string rootPath, string relativePath)
        {
            var normalizedRoot = EnsureTrailingSeparator(Path.GetFullPath(rootPath));
            var safeRelative = relativePath ?? string.Empty;
            if (Path.IsPathRooted(safeRelative))
            {
                throw new InvalidOperationException("Absolute install paths are not allowed in download manifests.");
            }

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
        /// Computes the SHA-256 hash for one file.
        /// </summary>
        private static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct)
        {
            await using var stream = File.OpenRead(filePath);
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

            var isPython = executableName.StartsWith("python", StringComparison.OrdinalIgnoreCase) ||
                           executableName.StartsWith("py", StringComparison.OrdinalIgnoreCase);
            if (!isPython)
            {
                return;
            }

            if (!psi.Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Any(argument => string.Equals(argument, "-u", StringComparison.OrdinalIgnoreCase)))
            {
                psi.Arguments = string.IsNullOrWhiteSpace(psi.Arguments)
                    ? "-u"
                    : $"-u {psi.Arguments}";
            }

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
            public InstallExecutionContext(string rootDirectory)
            {
                TempDir = Path.Combine(Path.GetTempPath(), "ASLM_Downloads", Guid.NewGuid().ToString("N"));
                Artifacts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            public string TempDir { get; }
            public Dictionary<string, string> Artifacts { get; }

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
