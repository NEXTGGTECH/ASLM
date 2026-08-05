// Copyright NEXTGGTECH. Apache License 2.0.

using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ASLM.Models;

namespace ASLM.Services.Engines
{
    /// <summary>
    /// Discovers, validates, and installs engine runtimes from <c>ASLM_Engine.json</c> manifests.
    /// </summary>
    public class EngineInstaller
    {
        private readonly HttpClient _httpClient = new();

        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private List<EngineConfig>? _cachedEngines;
        private Dictionary<string, EngineConfig>? _cachedEnginesById;
        private Dictionary<string, EngineConfig>? _cachedInstalledEnginesById;
        private readonly object _cacheLock = new();

        // Discovery

        /// <summary>
        /// Scans <c>Engines/*/ASLM_Engine.json</c> files and returns the discovered configs.
        /// </summary>
        /// <returns>A list of discovered engine configurations.</returns>
        public List<EngineConfig> DiscoverEngines()
        {
            lock (_cacheLock)
            {
                if (_cachedEngines != null)
                {
                    EnsureEngineLookups(_cachedEngines);
                    return _cachedEngines.ToList();
                }

                var baseDir = GetRootDirectory();
                var enginesRoot = Path.Combine(baseDir, "Engines");
                var engines = new List<EngineConfig>();

                if (Directory.Exists(enginesRoot))
                {
                    // Probe each engine folder for its manifest directly. A recursive
                    // EnumerateFiles name filter returns nothing for the seeded Engines
                    // folder on Mac Catalyst, so engines go through the same per-folder
                    // scan the module discovery uses.
                    foreach (var engineDir in Directory.EnumerateDirectories(enginesRoot))
                    {
                        var jsonFile = Path.Combine(engineDir, "ASLM_Engine.json");
                        if (!File.Exists(jsonFile))
                        {
                            continue;
                        }

                        try
                        {
                            var json = File.ReadAllText(jsonFile);
                            var config = JsonSerializer.Deserialize<EngineConfig>(json, _jsonOptions);
                            if (config != null)
                            {
                                config.Normalize();

                                // Backward compatibility: files without fileVersion are treated as v1
                                if (config.FileVersion == 0)
                                    config.FileVersion = 1;

                                if (config.FileVersion != 1 && config.FileVersion != 2)
                                {
                                    Debug.WriteLine($"Unsupported fileVersion {config.FileVersion} in {jsonFile}, skipping.");
                                    continue;
                                }

                                config.SourcePath = jsonFile;
                                config.ResolveForPlatform(PlatformInfo.OsKey, PlatformInfo.ArchKey);
                                var manifestChanged = BackfillEngineManifestMetadata(config);

                                // Validate that "installed" engines actually have their runtime on disk.
                                // If a user manually deleted the runtime folder, reset the status.
                                if (config.Status.Installed)
                                {
                                    var runtimeDir = Path.Combine(engineDir, "runtime");

                                    if (!Directory.Exists(runtimeDir) ||
                                        !Directory.EnumerateFileSystemEntries(runtimeDir).Any())
                                    {
                                        Debug.WriteLine($"Runtime missing for {config.Name}, resetting installed status.");
                                        config.Status.Installed = false;
                                        config.Status.InstalledVersion = null;

                                        // Persist the reset status back to JSON.
                                        var updatedJson = JsonSerializer.Serialize(config, _jsonOptions);
                                        File.WriteAllText(jsonFile, updatedJson);
                                    }
                                }

                                if (manifestChanged)
                                {
                                    var updatedJson = JsonSerializer.Serialize(config, _jsonOptions);
                                    File.WriteAllText(jsonFile, updatedJson);
                                }

                                engines.Add(config);
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Failed to parse {jsonFile}: {ex.Message}");
                        }
                    }
                }

                _cachedEngines = engines;
                EnsureEngineLookups(_cachedEngines);
                return _cachedEngines.ToList();
            }
        }

        /// <summary>
        /// Builds id-based engine lookup caches when the discovered engine list changes.
        /// </summary>
        private void EnsureEngineLookups(IReadOnlyList<EngineConfig> engines)
        {
            _cachedEnginesById ??= engines
                .GroupBy(engine => engine.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            _cachedInstalledEnginesById ??= engines
                .Where(engine => engine.Status.Installed)
                .GroupBy(engine => engine.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Restores shipped update metadata on older engine manifests that predate the update block.
        /// </summary>
        public static bool EnsureManifestMetadata(EngineConfig config) => BackfillEngineManifestMetadata(config);

        private static bool BackfillEngineManifestMetadata(EngineConfig config)
        {
            if (!string.Equals(config.Id, "ollama-service", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Update metadata is platform specific; only backfill the resolved active platform.
            if (!config.IsSupportedOnCurrentPlatform)
            {
                return false;
            }

            var changed = false;
            config.Update ??= new EngineUpdateConfig();
            if (string.IsNullOrWhiteSpace(config.Update.Repo))
            {
                config.Update.Repo = "ollama/ollama";
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(config.Update.AssetName))
            {
                config.Update.AssetName = $"ollama-{config.ActivePlatformKey}.zip";
                changed = true;
            }

            config.Update.Normalize();
            return changed;
        }

        // Engine lookup

        /// <summary>
        /// Resolves the absolute path to an installed engine executable.
        /// </summary>
        /// <param name="engineId">The unique ID of the engine.</param>
        /// <returns>The full path to the executable, or null.</returns>
        public string? GetEngineExecutablePath(string engineId)
        {
            var engine = GetEngineConfig(engineId);

            if (engine == null || string.IsNullOrEmpty(engine.ExecutablePath))
                return null;

            var engineDir = Path.GetDirectoryName(engine.SourcePath);
            if (string.IsNullOrEmpty(engineDir)) return null;

            var fullPath = Path.Combine(engineDir, engine.ExecutablePath);
            return File.Exists(fullPath) ? fullPath : null;
        }

        /// <summary>
        /// Returns the installed engine config for one engine id.
        /// </summary>
        /// <param name="engineId">The unique ID of the engine.</param>
        /// <returns>The engine configuration, or null.</returns>
        public EngineConfig? GetEngineConfig(string engineId)
        {
            DiscoverEngines();

            lock (_cacheLock)
            {
                return _cachedInstalledEnginesById != null &&
                       _cachedInstalledEnginesById.TryGetValue(engineId, out var engine)
                    ? engine
                    : null;
            }
        }

        /// <summary>
        /// Returns whether a manifest exists for one engine id, regardless of install state.
        /// </summary>
        public bool HasEngine(string engineId)
        {
            DiscoverEngines();

            lock (_cacheLock)
            {
                return _cachedEnginesById?.ContainsKey(engineId) == true;
            }
        }

        // Installation

        /// <summary>
        /// Executes all declared install and post-install steps for one engine.
        /// </summary>
        /// <param name="config">Engine configuration with install steps.</param>
        /// <param name="log">Receives human-readable log messages for the UI console.</param>
        /// <param name="downloadProgress">Receives download fraction updates for the progress bar.</param>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        /// <returns>A task representing the installation process.</returns>
        public async Task InstallAsync(
            EngineConfig config,
            IProgress<string> log,
            IProgress<DownloadProgress>? downloadProgress = null,
            CancellationToken ct = default)
        {
            await Task.Run(async () =>
            {
                // Resolve the active platform block and refuse to install on an unsupported platform.
                config.ResolveForPlatform(PlatformInfo.OsKey, PlatformInfo.ArchKey);
                if (!config.IsSupportedOnCurrentPlatform)
                {
                    throw new PlatformNotSupportedException(
                        $"Engine '{config.Name}' does not support {PlatformInfo.PlatformKey}.");
                }

                // Scoped state; safe for the singleton because only one install runs at a time.
                var baseDir = GetRootDirectory();
                var tempDir = Path.Combine(Path.GetTempPath(), "ASLM", config.Id);

                Directory.CreateDirectory(tempDir);

                var versionLabel = config.Version.All(c => char.IsDigit(c) || c == '.')
                    ? $"v{config.Version}"
                    : config.Version;
                log.Report($"=== Installing {config.Name} {versionLabel} ===");
                log.Report($"Base directory: {baseDir}");

                var context = new StepContext(baseDir, tempDir);

                for (int i = 0; i < config.Install.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    var step = config.Install[i];
                    log.Report($"[{i + 1}/{config.Install.Count}] Action: {step.Action}");

                    switch (step.Action.ToLowerInvariant())
                    {
                        case "download":
                            await ExecuteDownloadAsync(step, context, log, downloadProgress, ct);
                            break;
                        case "extract":
                            ExecuteExtract(step, context, log);
                            break;
                        case "modify_file":
                            ExecuteModifyFile(step, context, log);
                            break;
                        case "execute":
                            await ExecuteCommandAsync(step, context, log, ct);
                            break;
                        case "move":
                            ExecuteMove(step, context, log);
                            break;
                        case "cleanup":
                            ExecuteCleanup(step, context, log);
                            break;
                        case "rename_file":
                            ExecuteRenameFile(step, context, log);
                            break;
                        case "delete_file":
                            ExecuteDeleteFile(step, context, log);
                            break;
                        default:
                            log.Report($"  ⚠ Unknown action: {step.Action}, skipping.");
                            break;
                    }
                }

                // Execute post-install steps (engine-specific fixes)
                if (config.PostInstall.Count > 0)
                {
                    log.Report($"Running {config.PostInstall.Count} post-install step(s)...");
                    for (int i = 0; i < config.PostInstall.Count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var step = config.PostInstall[i];
                        var label = step.Name ?? step.Action;
                        log.Report($"[PostInstall {i + 1}/{config.PostInstall.Count}] {label}");

                        switch (step.Action.ToLowerInvariant())
                        {
                            case "rename_file":
                                ExecuteRenameFile(step, context, log);
                                break;
                            case "delete_file":
                                ExecuteDeleteFile(step, context, log);
                                break;
                            case "modify_file":
                                ExecuteModifyFile(step, context, log);
                                break;
                            case "execute":
                                await ExecuteCommandAsync(step, context, log, ct);
                                break;
                            default:
                                log.Report($"  ⚠ Unknown post-install action: {step.Action}, skipping.");
                                break;
                        }
                    }
                }

                EnsureExecutablePermission(config);

                // Mark engine as installed and persist to disk.
                config.Status.Installed = true;
                config.Status.InstalledVersion = config.Version;
                config.Status.LastChecked = DateTime.UtcNow.ToString("o");

                await SaveEngineConfigAsync(config);
                log.Report($"=== {config.Name} installed successfully ===");

                InvalidateCache();
            }, ct);
        }

        /// <summary>
        /// Saves one engine manifest back to disk.
        /// </summary>
        /// <param name="config">The config to save.</param>
        public Task SaveEngineConfigAsync(EngineConfig config) => SaveConfigAsync(config);

        /// <summary>
        /// Clears cached engine discovery results so the next lookup reloads manifests from disk.
        /// </summary>
        public void InvalidateCache()
        {
            lock (_cacheLock)
            {
                _cachedEngines = null;
                _cachedEnginesById = null;
                _cachedInstalledEnginesById = null;
            }
        }

        // Step execution

        /// <summary>
        /// Downloads a file from <c>step.Url</c> to <c>step.Dest</c>,
        /// optionally verifying the SHA-256 checksum.
        /// Retries up to 3 times on transport errors, resuming from the last
        /// byte using an HTTP <c>Range</c> header so large downloads don't restart.
        /// </summary>
        /// <param name="step">The installation step configuration.</param>
        /// <param name="ctx">Context for resolving paths.</param>
        /// <param name="log">Progress logger.</param>
        /// <param name="downloadProgress">Download progress reporter.</param>
        /// <param name="ct">Cancellation token.</param>
        private async Task ExecuteDownloadAsync(
            InstallStep step,
            StepContext ctx,
            IProgress<string> log,
            IProgress<DownloadProgress>? downloadProgress,
            CancellationToken ct)
        {
            var url = step.Url ?? throw new InvalidOperationException("Download step missing 'url'.");
            var dest = ctx.ResolvePath(step.Dest ?? throw new InvalidOperationException("Download step missing 'dest'."));
            var transferLabel = Path.GetFileName(dest);

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            log.Report($"  Downloading: {url}");

            const int maxAttempts = 3;
            const int retryDelayMs = 3000;

            long totalBytes = 0;
            long downloaded = 0;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, url);

                    // Resume from where we left off (if previous attempt made progress).
                    if (downloaded > 0)
                    {
                        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(downloaded, null);
                        log.Report($"  Resuming from {downloaded / 1024.0 / 1024.0:F1} MB (attempt {attempt}/{maxAttempts})...");
                    }

                    using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                    response.EnsureSuccessStatusCode();

                    // On first request (or if server doesn't support Range) get total size.
                    if (totalBytes == 0)
                        totalBytes = response.Content.Headers.ContentLength ?? 0;

                    // If server returned 200 (ignored Range) restart the file from scratch.
                    bool fullResponse = response.StatusCode == System.Net.HttpStatusCode.OK && downloaded > 0;
                    var fileMode = (downloaded == 0 || fullResponse) ? FileMode.Create : FileMode.Append;
                    if (fullResponse)
                    {
                        downloaded = 0;
                        log.Report("  Server does not support resume, restarting download.");
                    }

                    await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
                    await using var fileStream = new FileStream(dest, fileMode, FileAccess.Write, FileShare.None, 65536, true);

                    var buffer = new byte[65536];
                    int bytesRead;
                    var throttle = Stopwatch.StartNew();

                    downloadProgress?.Report(new DownloadProgress(
                        totalBytes > 0 ? (double)downloaded / totalBytes : 0,
                        downloaded,
                        totalBytes,
                        transferLabel));

                    while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                        downloaded += bytesRead;

                        // Throttle UI updates to ~20 fps (every 50 ms).
                        if (totalBytes > 0 && throttle.ElapsedMilliseconds >= 50)
                        {
                            throttle.Restart();
                            downloadProgress?.Report(new DownloadProgress(
                                (double)downloaded / totalBytes,
                                downloaded,
                                totalBytes,
                                transferLabel));
                        }
                    }

                    // Success — exit retry loop.
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (attempt == maxAttempts)
                        throw;

                    log.Report($"  ⚠ Download error (attempt {attempt}/{maxAttempts}): {ex.Message}");
                    log.Report($"  Retrying in {retryDelayMs / 1000} s...");
                    await Task.Delay(retryDelayMs, ct);
                }
            }

            // Report final 100%.
            downloadProgress?.Report(new DownloadProgress(
                1.0,
                downloaded,
                totalBytes > 0 ? totalBytes : downloaded,
                transferLabel));

            // Verify SHA-256 if a hash was provided.
            if (!string.IsNullOrWhiteSpace(step.Sha256))
            {
                log.Report("  Verifying checksum...");
                var hash = await ComputeSha256Async(dest, ct);
                if (!string.Equals(hash, step.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Checksum mismatch. Expected: {step.Sha256}, Got: {hash}");

                log.Report("  ✓ Checksum verified.");
            }

            log.Report("  ✓ Download complete.");
        }

        /// <summary>
        /// Extracts a zip or tar.gz archive from <c>step.Source</c> to <c>step.Dest</c>.
        /// </summary>
        /// <param name="step">The installation step configuration.</param>
        /// <param name="ctx">Context for resolving paths.</param>
        /// <param name="log">Progress logger.</param>
        private static void ExecuteExtract(InstallStep step, StepContext ctx, IProgress<string> log)
        {
            var source = ctx.ResolvePath(step.Source ?? throw new InvalidOperationException("Extract step missing 'source'."));
            var dest = ctx.ResolvePath(step.Dest ?? throw new InvalidOperationException("Extract step missing 'dest'."));

            log.Report($"  Extracting: {source}");
            log.Report($"  To: {dest}");

            ArchiveExtractor.ExtractToDirectory(source, dest, log);

            log.Report("  ✓ Extraction complete.");
        }

        /// <summary>
        /// Performs a text replacement inside <c>step.Path</c>.
        /// </summary>
        /// <param name="step">The installation step configuration.</param>
        /// <param name="ctx">Context for resolving paths.</param>
        /// <param name="log">Progress logger.</param>
        private static void ExecuteModifyFile(InstallStep step, StepContext ctx, IProgress<string> log)
        {
            var path = ctx.ResolvePath(step.Path ?? throw new InvalidOperationException("ModifyFile step missing 'path'."));
            var find = step.Find ?? throw new InvalidOperationException("ModifyFile step missing 'find'.");
            var replace = step.Replace ?? throw new InvalidOperationException("ModifyFile step missing 'replace'.");

            log.Report($"  Modifying: {path}");
            log.Report($"  Find: \"{find}\" → Replace: \"{replace}\"");

            var content = File.ReadAllText(path);

            if (!content.Contains(find))
            {
                log.Report("  ⚠ Pattern not found, skipping modification.");
                return;
            }

            content = content.Replace(find, replace);
            File.WriteAllText(path, content);

            log.Report("  ✓ File modified.");
        }

        /// <summary>
        /// Runs an external process. The first token of <c>step.Command</c> is resolved
        /// as the executable path; remaining tokens are arguments. Paths containing
        /// slashes are automatically resolved to absolute paths.
        /// </summary>
        /// <param name="step">The installation step configuration.</param>
        /// <param name="ctx">Context for resolving paths.</param>
        /// <param name="log">Progress logger.</param>
        /// <param name="ct">Cancellation token.</param>
        private static async Task ExecuteCommandAsync(InstallStep step, StepContext ctx, IProgress<string> log, CancellationToken ct)
        {
            var command = step.Command ?? throw new InvalidOperationException("Execute step missing 'command'.");
            command = ctx.ResolveVariables(command);

            // Split into executable + argument string.
            var parts = SplitCommand(command);
            if (parts.Count == 0)
                throw new InvalidOperationException("Execute step contains an empty command.");

            var exePath = ctx.ResolvePath(parts[0]);
            var arguments = parts.Count > 1 ? ctx.ResolveArgPaths(parts.Skip(1)) : string.Empty;

            log.Report($"  Executing: {exePath} {arguments}");

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                WorkingDirectory = ctx.BaseDir,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };

            // Ensure Python writes UTF-8 regardless of Windows locale.
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            psi.Environment["PYTHONUTF8"] = "1";

            using var process = new Process { StartInfo = psi };
            process.Start();
            process.StandardInput.Close();

            // Stream stdout / stderr in parallel.
            var stdoutTask = Task.Run(async () =>
            {
                while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
                    log.Report($"    > {line}");
            }, ct);

            var stderrTask = Task.Run(async () =>
            {
                while (await process.StandardError.ReadLineAsync(ct) is { } line)
                    log.Report($"    ERR> {line}");
            }, ct);

            await Task.WhenAll(stdoutTask, stderrTask);
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
                log.Report($"  ⚠ Process exited with code {process.ExitCode}");
            else
                log.Report("  ✓ Command complete.");
        }

        /// <summary>
        /// Moves a directory from <c>step.Source</c> to <c>step.Dest</c>.
        /// </summary>
        /// <param name="step">The installation step configuration.</param>
        /// <param name="ctx">Context for resolving paths.</param>
        /// <param name="log">Progress logger.</param>
        private static void ExecuteMove(InstallStep step, StepContext ctx, IProgress<string> log)
        {
            var source = ctx.ResolvePath(step.Source ?? throw new InvalidOperationException("Move step missing 'source'."));
            var dest = ctx.ResolvePath(step.Dest ?? throw new InvalidOperationException("Move step missing 'dest'."));

            log.Report($"  Moving: {source}");
            log.Report($"  To: {dest}");

            if (Directory.Exists(dest))
                Directory.Delete(dest, true);

            Directory.Move(source, dest);

            log.Report("  ✓ Move complete.");
        }

        /// <summary>
        /// Deletes <c>step.Target</c> recursively, defaulting to the temp directory.
        /// </summary>
        /// <param name="step">The installation step configuration.</param>
        /// <param name="ctx">Context for resolving paths.</param>
        /// <param name="log">Progress logger.</param>
        private static void ExecuteCleanup(InstallStep step, StepContext ctx, IProgress<string> log)
        {
            var target = ctx.ResolvePath(step.Target ?? ctx.TempDir);

            log.Report($"  Cleaning up: {target}");

            if (Directory.Exists(target))
            {
                Directory.Delete(target, true);
                log.Report("  ✓ Cleanup complete.");
            }
            else
            {
                log.Report("  Directory not found, nothing to clean.");
            }
        }

        /// <summary>
        /// Renames a file from <c>step.Source</c> to <c>step.Dest</c>.
        /// </summary>
        /// <param name="step">The installation step configuration.</param>
        /// <param name="ctx">Context for resolving paths.</param>
        /// <param name="log">Progress logger.</param>
        private static void ExecuteRenameFile(InstallStep step, StepContext ctx, IProgress<string> log)
        {
            var source = ctx.ResolvePath(step.Source ?? throw new InvalidOperationException("rename_file step missing 'source'."));
            var dest = ctx.ResolvePath(step.Dest ?? throw new InvalidOperationException("rename_file step missing 'dest'."));

            if (!File.Exists(source))
            {
                log.Report($"  File not found: {source}, skipping.");
                return;
            }

            if (File.Exists(dest))
                File.Delete(dest);

            File.Move(source, dest);
            log.Report($"  ✓ Renamed: {Path.GetFileName(source)} → {Path.GetFileName(dest)}");
        }

        /// <summary>
        /// Deletes the file at <c>step.Target</c>.
        /// </summary>
        /// <param name="step">The installation step configuration.</param>
        /// <param name="ctx">Context for resolving paths.</param>
        /// <param name="log">Progress logger.</param>
        private static void ExecuteDeleteFile(InstallStep step, StepContext ctx, IProgress<string> log)
        {
            var target = ctx.ResolvePath(step.Target ?? throw new InvalidOperationException("delete_file step missing 'target'."));

            if (!File.Exists(target))
            {
                log.Report($"  File not found: {target}, skipping.");
                return;
            }

            File.Delete(target);
            log.Report($"  ✓ Deleted: {Path.GetFileName(target)}");
        }

        // Path helpers

        /// <summary>
        /// Returns the application root directory (parent of the <c>App/</c> folder).
        /// The MAUI executable runs from <c>ASLM/App/</c>, but data directories
        /// (<c>Engines/</c>, <c>Modules/</c>, etc.) live one level up at <c>ASLM/</c>.
        /// </summary>
        /// <returns>The root directory path.</returns>
        private static string GetRootDirectory()
        {
            return AppRoot.Directory;
        }

        /// <summary>
        /// Ensures the engine executable carries the Unix execute bit; a no-op on Windows.
        /// </summary>
        internal static void EnsureExecutablePermission(EngineConfig config)
        {
            if (OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(config.ExecutablePath))
                return;

            try
            {
                var engineDir = Path.GetDirectoryName(config.SourcePath);
                if (string.IsNullOrWhiteSpace(engineDir))
                    return;

                var executable = Path.Combine(engineDir, config.ExecutablePath);
                if (!File.Exists(executable))
                    return;

                File.SetUnixFileMode(
                    executable,
                    File.GetUnixFileMode(executable) |
                    UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }
            catch
            {
                // Best effort - tar extraction preserves the mode in normal cases.
            }
        }

        /// <summary>
        /// Computes a lowercase SHA-256 hash for one file.
        /// </summary>
        /// <param name="filePath">Path to the file to hash.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Lowercase hex string of the SHA256 hash.</returns>
        private static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct)
        {
            await using var stream = File.OpenRead(filePath);
            var hashBytes = await SHA256.HashDataAsync(stream, ct);
            return Convert.ToHexStringLower(hashBytes);
        }

        /// <summary>
        /// Saves one engine manifest back to disk.
        /// </summary>
        /// <param name="config">The config to save.</param>
        private async Task SaveConfigAsync(EngineConfig config)
        {
            if (string.IsNullOrEmpty(config.SourcePath))
                return;

            var json = JsonSerializer.Serialize(config, _jsonOptions);
            await File.WriteAllTextAsync(config.SourcePath, json);
        }

        // Command parsing

        /// <summary>
        /// Splits a command string into tokens while respecting quotes.
        /// </summary>
        private static List<string> SplitCommand(string command)
        {
            var args = new List<string>();
            var currentArg = new System.Text.StringBuilder();
            var inQuotes = false;
            var quoteChar = '\0';

            foreach (var c in command)
            {
                if (inQuotes)
                {
                    if (c == quoteChar)
                    {
                        inQuotes = false;
                        quoteChar = '\0';
                    }
                    else
                    {
                        currentArg.Append(c);
                    }
                }
                else
                {
                    if (char.IsWhiteSpace(c))
                    {
                        if (currentArg.Length > 0)
                        {
                            args.Add(currentArg.ToString());
                            currentArg.Clear();
                        }
                    }
                    else if (c == '"' || c == '\'')
                    {
                        inQuotes = true;
                        quoteChar = c;
                    }
                    else
                    {
                        currentArg.Append(c);
                    }
                }
            }

            if (currentArg.Length > 0)
            {
                args.Add(currentArg.ToString());
            }

            return args;
        }

        /// <summary>
        /// Rebuilds an argument string from parsed tokens.
        /// </summary>
        private static string JoinArguments(IEnumerable<string> args)
        {
            return string.Join(" ", args.Select(arg => arg.Contains(' ') ? $"\"{arg}\"" : arg));
        }

        // Install context

        /// <summary>
        /// Holds directory paths for a single installation run.
        /// Avoids storing mutable state as fields on the singleton service.
        /// </summary>
        /// <param name="baseDir">The application base directory.</param>
        /// <param name="tempDir">The temporary directory for this installation.</param>
        private sealed class StepContext
        {
            /// <summary>
            /// Normalized absolute path to the application base directory.
            /// </summary>
            public string BaseDir { get; }

            /// <summary>
            /// Normalized absolute path to the temporary installation directory.
            /// </summary>
            public string TempDir { get; }

            /// <summary>
            /// Initializes a new instance of the <see cref="StepContext"/> class.
            /// </summary>
            /// <param name="baseDir">The application base directory.</param>
            /// <param name="tempDir">The temporary directory for this installation.</param>
            public StepContext(string baseDir, string tempDir)
            {
                BaseDir = EnsureTrailingSeparator(Path.GetFullPath(baseDir));
                TempDir = EnsureTrailingSeparator(Path.GetFullPath(tempDir));
            }

            /// <summary>
            /// Ensures that a directory path ends with a directory separator.
            /// </summary>
            /// <param name="path">The path to normalize.</param>
            /// <returns>The path with a trailing separator.</returns>
            private static string EnsureTrailingSeparator(string path)
            {
                if (!path.EndsWith(Path.DirectorySeparatorChar) && !path.EndsWith(Path.AltDirectorySeparatorChar))
                    return path + Path.DirectorySeparatorChar;
                return path;
            }

            /// <summary>Replaces <c>{temp}</c> placeholder with the actual temp directory.</summary>
            /// <param name="input">The string containing variables to resolve.</param>
            /// <returns>The string with variables replaced.</returns>
            public string ResolveVariables(string input)
                => input.Replace("{temp}", TempDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            /// <summary>
            /// Resolves <c>{temp}</c> variable and converts relative paths to absolute
            /// (relative to <see cref="BaseDir"/>). Validates that the resulting path
            /// is within either <see cref="BaseDir"/> or <see cref="TempDir"/>.
            /// </summary>
            /// <param name="path">The path to resolve.</param>
            /// <returns>A validated absolute path.</returns>
            /// <exception cref="InvalidOperationException">Thrown if a path traversal attempt is detected.</exception>
            public string ResolvePath(string path)
            {
                path = ResolveVariables(path);

                string combined = Path.IsPathRooted(path) ? path : Path.Combine(BaseDir, path);
                var fullPath = Path.GetFullPath(combined);

                // For secure comparison, ensure we're checking against directory boundaries correctly.
                var comparePath = fullPath;
                if (!comparePath.EndsWith(Path.DirectorySeparatorChar) && !comparePath.EndsWith(Path.AltDirectorySeparatorChar))
                    comparePath += Path.DirectorySeparatorChar;

                if (!comparePath.StartsWith(BaseDir, StringComparison.OrdinalIgnoreCase) &&
                    !comparePath.StartsWith(TempDir, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Security violation: Path '{path}' resolves to '{fullPath}' which is outside allowed boundaries.");
                }

                return fullPath;
            }

            /// <summary>
            /// Resolves path-like tokens inside argument values and rebuilds the argument string.
            /// Any token containing <c>/</c> or <c>\</c> is treated as a path and resolved.
            /// </summary>
            /// <param name="args">The argument values to process.</param>
            /// <returns>The argument string with resolved paths.</returns>
            public string ResolveArgPaths(IEnumerable<string> args)
            {
                var tokens = args.ToArray();
                for (int i = 0; i < tokens.Length; i++)
                {
                    if (tokens[i].Contains('/') || tokens[i].Contains('\\'))
                        tokens[i] = ResolvePath(tokens[i]);
                }

                return EngineInstaller.JoinArguments(tokens);
            }
        }
    }
}
