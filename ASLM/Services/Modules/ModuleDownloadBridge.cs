// Copyright NEXTGGTECH. Apache License 2.0.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ASLM.Models;
using Microsoft.Extensions.Logging;

namespace ASLM.Services.Modules
{
    /// <summary>
    /// Executes module-declared downloads bridge commands and exchanges JSON requests over stdio.
    /// </summary>
    public class ModuleDownloadBridge
    {
        private const int MaxBridgeOutputCharacters = 2_000_000;

        private readonly EngineInstaller _engineInstaller;
        private readonly ModuleEnvironmentResolver _environmentResolver;
        private readonly ModuleRunner _moduleRunner;
        private readonly ILogger<ModuleDownloadBridge> _logger;

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

            try
            {
                if (!string.IsNullOrWhiteSpace(bridge.Engine))
                {
                    var engineConfig = _engineInstaller.GetEngineConfig(bridge.Engine)
                        ?? throw new InvalidOperationException($"Engine '{bridge.Engine}' is not installed.");
                    await _environmentResolver.EnsureEnvironmentAsync(module, engineConfig, null, ct);
                }

                // Start the bridge process with the same module context used by regular commands.
                var psi = CreateProcessStartInfo(module, bridge, moduleDir);
                using var process = new Process { StartInfo = psi };

                if (!process.Start())
                {
                    return CreateErrorResponse("Downloads bridge process could not be started.");
                }

                // Write the JSON request first, then read both output streams to avoid blocking.
                var requestJson = JsonSerializer.Serialize(request, _jsonOptions);
                await process.StandardInput.WriteAsync(requestJson.AsMemory(), ct);
                await process.StandardInput.FlushAsync();
                process.StandardInput.Close();

                var stdoutTask = ReadBoundedToEndAsync(process.StandardOutput, ct);
                var stderrTask = ReadBoundedToEndAsync(process.StandardError, ct);

                await process.WaitForExitAsync(ct);

                var stdout = await stdoutTask;
                var stderr = await stderrTask;

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
                return response;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Downloads bridge invocation failed for module {ModuleId}.", module.Id);
                return CreateErrorResponse(ex.Message);
            }
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
        private ProcessStartInfo CreateProcessStartInfo(
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
