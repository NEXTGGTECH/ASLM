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
    /// </summary>
    public class ModuleDownloadBridge
    {
        private const int MaxBridgeOutputCharacters = 2_000_000;
        private static readonly TimeSpan BridgeRequestTimeout = TimeSpan.FromSeconds(30);

        private readonly EngineInstaller _engineInstaller;
        private readonly ModuleEnvironmentResolver _environmentResolver;
        private readonly ModuleRunner _moduleRunner;
        private readonly ILogger<ModuleDownloadBridge> _logger;
        private const int MaxIconBytes = 256 * 1024;
        private const int MaxIconDimension = 512;
        private const int MaxCachedIcons = 64;
        private readonly Dictionary<string, DownloadCatalogIcon> _images = new(StringComparer.Ordinal);
        private readonly Queue<string> _imageKeys = new();
        private readonly object _cacheLock = new();

        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = false
        };


        // Initialization

        /// <summary>
        /// Creates the downloads bridge.
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
        /// </summary>
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
        /// </summary>
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
        /// </summary>
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
        /// </summary>
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
        /// </summary>
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
        /// </summary>
        public async Task<ModuleDownloadBridgeResponse> InvokeAsync(
            ModuleConfig module,
            ModuleDownloadBridgeRequest request,
            CancellationToken ct = default)
        {
            // Normalize the outgoing request before any bridge-specific checks.
            request.Normalize();

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

            using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            requestCts.CancelAfter(BridgeRequestTimeout);
            var requestToken = requestCts.Token;
            Process? process = null;
            Task<string>? stdoutTask = null;
            Task<string>? stderrTask = null;

            try
            {
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

                // StandardInputEncoding is explicitly BOM-less so every JSON parser sees '{' as the first byte.
                var requestJson = JsonSerializer.Serialize(request, _jsonOptions);
                await process.StandardInput.WriteAsync(requestJson.AsMemory(), requestToken).ConfigureAwait(false);
                await process.StandardInput.FlushAsync().ConfigureAwait(false);
                process.StandardInput.Close();

                await process.WaitForExitAsync(requestToken).ConfigureAwait(false);

                var stdout = await stdoutTask.ConfigureAwait(false);
                var stderr = await stderrTask.ConfigureAwait(false);

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
                await TerminateProcessTreeAsync(process, stdoutTask, stderrTask).ConfigureAwait(false);
                throw;
            }
            catch (OperationCanceledException) when (requestCts.IsCancellationRequested)
            {
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
                await TerminateProcessTreeAsync(process, stdoutTask, stderrTask).ConfigureAwait(false);
                _logger.LogError(ex, "Downloads bridge invocation failed for module {ModuleId}.", module.Id);
                return CreateErrorResponse(ex.Message);
            }
            finally
            {
                process?.Dispose();
            }
        }

        /// <summary>
        /// Stops a failed or canceled bridge process and observes its redirected output tasks.
        /// </summary>
        private static async Task TerminateProcessTreeAsync(
            Process? process,
            Task<string>? stdoutTask,
            Task<string>? stderrTask)
        {
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

        private async Task ResolveResourcesAsync(ModuleDownloadBridgeResponse response, string moduleDirectory, CancellationToken ct)
        {
            var icons = new Dictionary<string, DownloadCatalogIcon?>(StringComparer.Ordinal);
            var colors = new Dictionary<string, string?>(StringComparer.Ordinal);
            var warnings = new HashSet<string>(StringComparer.Ordinal);

            async Task<List<ModuleDownloadField>?> ResolveList(List<ModuleDownloadField>? values)
            {
                // Preserve absent vs explicitly empty arrays for describe_item overrides.
                if (values == null) return null;
                var result = new List<ModuleDownloadField>();
                foreach (var value in values)
                {
                    ct.ThrowIfCancellationRequested();
                    if (value == null) continue;
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

        private static string? NormalizeHex(string? value)
        {
            var hex = value?.Trim();
            if (hex == null || (hex.Length != 7 && hex.Length != 9) || hex[0] != '#' ||
                hex.AsSpan(1).ContainsAnyExcept("0123456789abcdefABCDEF")) return null;
            return hex.Length == 7 ? "#FF" + hex[1..].ToUpperInvariant() : hex.ToUpperInvariant();
        }

        private async Task<DownloadCatalogIcon?> LoadIconAsync(
            ModuleDownloadIconPayload definition, string moduleDirectory, CancellationToken ct)
        {
            try
            {
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
                ct.ThrowIfCancellationRequested();
                if (bytes.Length is <= 0 or > MaxIconBytes) return null;
                var key = Convert.ToHexString(SHA256.HashData(bytes));
                lock (_cacheLock)
                    if (_images.TryGetValue(key, out var cached)) return cached;

                using var data = SKData.CreateCopy(bytes);
                using var codec = SKCodec.Create(data);
                if (codec == null || codec.EncodedFormat != SKEncodedImageFormat.Png ||
                    codec.Info.Width is <= 0 or > MaxIconDimension ||
                    codec.Info.Height is <= 0 or > MaxIconDimension) return null;
                using var bitmap = SKBitmap.Decode(bytes);
                if (bitmap == null) return null;
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
                return null;
            }
        }

        private static string? ResolveIconPath(string moduleDirectory, string relativePath)
        {
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
        /// </summary>
        private static bool SupportsOperation(ModuleDownloadsBridge bridge, string operation)
        {
            return bridge.Operations.Count == 0 ||
                   bridge.Operations.Any(candidate => string.Equals(candidate, operation, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Drains a bridge output stream while keeping only a bounded amount of text in memory.
        /// </summary>
        private static async Task<string> ReadBoundedToEndAsync(StreamReader reader, CancellationToken ct)
        {
            var builder = new StringBuilder();
            var buffer = new char[8192];
            var truncated = false;

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
        /// </summary>
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
        /// Injects the same useful module environment values that regular module commands receive.
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
        /// Returns whether one process launch targets Python.
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
        /// Extracts the most likely JSON object from bridge stdout.
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
