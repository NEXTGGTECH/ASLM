// Copyright NEXTGGTECH. Apache License 2.0.

using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using ASLM.Models;

namespace ASLM.Services.Modules
{
    /// <summary>
    /// Discovers module manifests and installs or refreshes module source files.
    /// </summary>
    public class ModuleInstaller
    {
        private const int ManifestWriteAttemptCount = 5;
        private static readonly TimeSpan ManifestWriteRetryDelay = TimeSpan.FromMilliseconds(50);

        private readonly HttpClient _httpClient = new();
        private readonly ModuleRunner _moduleRunner;
        private readonly ModuleTrustService _moduleTrustService;
        private readonly ModuleEngineReconciler _moduleEngineReconciler;

        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        /// <summary>
        /// Raised after an installed module manifest is saved.
        /// </summary>
        public event EventHandler? ModulesChanged;

        // Initialization

        /// <summary>
        /// Creates the module installer.
        /// </summary>
        public ModuleInstaller(
            ModuleRunner moduleRunner,
            ModuleTrustService moduleTrustService,
            ModuleEngineReconciler moduleEngineReconciler)
        {
            _moduleRunner = moduleRunner;
            _moduleTrustService = moduleTrustService;
            _moduleEngineReconciler = moduleEngineReconciler;
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ASLM-ModuleInstaller");
        }


        // Discovery

        /// <summary>
        /// Scans <c>Modules/*/ASLM_Module.json</c> files asynchronously.
        /// </summary>
        public async Task<List<ModuleConfig>> DiscoverModulesAsync()
        {
            var baseDir = GetRootDirectory();
            var modulesRoot = Path.Combine(baseDir, "Modules");
            var modules = new List<ModuleConfig>();

            if (!Directory.Exists(modulesRoot))
            {
                return modules;
            }

            // Only root manifests (Modules/{folder}/ASLM_Module.json) are installed modules.
            var jsonFiles = await Task.Run(() => ModuleManifestDiscovery
                .EnumerateInstalledManifests(modulesRoot)
                .ToList());

            var tasks = jsonFiles.Select(LoadModuleConfig);
            var results = await Task.WhenAll(tasks);

            foreach (var result in results)
            {
                if (result != null)
                {
                    modules.Add(result);
                }
            }

            return modules
                .OrderBy(module => module.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // Single manifest

        /// <summary>
        /// Loads one module configuration from disk.
        /// </summary>
        public async Task<ModuleConfig?> LoadModuleConfig(string jsonFile)
        {
            if (!File.Exists(jsonFile))
            {
                return null;
            }

            var modulesRoot = Path.Combine(GetRootDirectory(), "Modules");
            if (ModuleManifestDiscovery.IsPathUnderDirectory(modulesRoot, jsonFile) &&
                !ModuleManifestDiscovery.IsInstalledModuleManifest(modulesRoot, jsonFile))
            {
                Debug.WriteLine($"Ignoring nested module manifest: {jsonFile}");
                return null;
            }

            try
            {
                var json = await File.ReadAllTextAsync(jsonFile);
                return ModuleManifestParser.Parse(json, jsonFile);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to parse {jsonFile}: {ex.Message}");
                return null;
            }
        }


        // Source download

        /// <summary>
        /// Downloads the module source archive from GitHub and merges it into the module folder.
        /// </summary>
        public async Task<bool> DownloadSourceAsync(
            ModuleConfig module,
            IProgress<string> log,
            IProgress<DownloadProgress>? downloadProgress = null,
            CancellationToken ct = default)
        {
            if (module.Source.Type != "github" || string.IsNullOrEmpty(module.Source.Repo))
            {
                log.Report("No GitHub source defined, skipping download.");
                return true;
            }

            var moduleDir = Path.GetDirectoryName(module.SourcePath);
            if (string.IsNullOrEmpty(moduleDir))
            {
                return false;
            }

            // Legacy setup installs should keep using main unless the manifest explicitly opted into update tracking.
            var branch = module.HasDeclaredUpdateConfig && !string.IsNullOrWhiteSpace(module.Update.Branch)
                ? module.Update.Branch
                : "main";
            var zipUrl = $"https://api.github.com/repos/{module.Source.Repo}/zipball/{Uri.EscapeDataString(branch)}";
            var tempZip = Path.GetTempFileName();
            var tempExtractDir = Path.Combine(Path.GetTempPath(), "ASLM_ModuleSrc_" + Guid.NewGuid());

            try
            {
                log.Report($"Downloading source from: {module.Source.Repo}");
                await DownloadFileAsync(zipUrl, tempZip, log, downloadProgress, ct);

                await Task.Run(() =>
                {
                    // Extract into a temporary folder first so the final module folder is only merged once.
                    Directory.CreateDirectory(tempExtractDir);
                    ZipFile.ExtractToDirectory(tempZip, tempExtractDir);

                    // GitHub archives wrap the repository inside one top-level folder.
                    var innerDir = Directory.GetDirectories(tempExtractDir).FirstOrDefault();
                    var sourceDir = innerDir ?? tempExtractDir;

                    // Validate a downloaded manifest before it can replace the installed one.
                    // Legacy source archives without a manifest continue to use the catalog manifest.
                    var downloadedManifestPath = Path.Combine(sourceDir, ModuleManifestDiscovery.ManifestFileName);
                    if (File.Exists(downloadedManifestPath))
                    {
                        var downloadedConfig = ModuleManifestParser.Parse(
                            File.ReadAllText(downloadedManifestPath),
                            downloadedManifestPath);
                        if (!string.Equals(downloadedConfig.Id, module.Id, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidDataException(
                                $"Downloaded module id '{downloadedConfig.Id}' does not match '{module.Id}'.");
                        }

                        if (!downloadedConfig.IsSupportedOnCurrentPlatform)
                        {
                            throw new PlatformNotSupportedException(
                                $"Module '{downloadedConfig.Name}' does not support {PlatformInfo.PlatformKey}.");
                        }
                    }

                    // Merge the extracted content into the existing module directory.
                    CopyDirectory(sourceDir, moduleDir);
                }, ct);

                log.Report("Source downloaded.");
                return true;
            }
            catch (Exception ex)
            {
                log.Report($"Source download failed: {ex.Message}");
                return false;
            }
            finally
            {
                TryDeleteFile(tempZip);
                TryDeleteDirectory(tempExtractDir);
            }
        }


        // Archive install

        /// <summary>
        /// Downloads a module archive, installs it into <c>Modules/{id}</c>, and runs first-run setup.
        /// </summary>
        public async Task<ModuleConfig> InstallFromUrlAsync(
            string zipUrl,
            IProgress<string> log,
            IProgress<DownloadProgress>? downloadProgress = null,
            CancellationToken ct = default)
        {
            var baseDir = GetRootDirectory();
            var modulesRoot = Path.Combine(baseDir, "Modules");
            var tempZip = Path.GetTempFileName();
            var tempExtractDir = Path.Combine(Path.GetTempPath(), "ASLM_Module_Install_" + Guid.NewGuid());

            try
            {
                log.Report($"Downloading module from: {zipUrl}");
                await DownloadFileAsync(zipUrl, tempZip, log, downloadProgress, ct);

                Directory.CreateDirectory(tempExtractDir);

                try
                {
                    log.Report("Extracting archive...");

                    var jsonFile = await Task.Run(() =>
                    {
                        ZipFile.ExtractToDirectory(tempZip, tempExtractDir);
                        return Directory
                            .EnumerateFiles(tempExtractDir, "ASLM_Module.json", SearchOption.AllDirectories)
                            .FirstOrDefault();
                    }, ct);

                    if (jsonFile == null)
                    {
                        throw new InvalidOperationException("Invalid module: ASLM_Module.json not found in archive.");
                    }

                    // Load the manifest first so the final install location can be derived from the module id.
                    var json = await File.ReadAllTextAsync(jsonFile, ct);
                    var config = ModuleManifestParser.Parse(json, jsonFile);
                    if (!config.IsSupportedOnCurrentPlatform)
                    {
                        throw new PlatformNotSupportedException(
                            $"Module '{config.Name}' does not support {PlatformInfo.PlatformKey}.");
                    }

                    var moduleSourceDir = Path.GetDirectoryName(jsonFile)!;
                    var finalDir = Path.Combine(modulesRoot, config.Id);

                    log.Report($"Installing to: {finalDir}");

                    await Task.Run(() =>
                    {
                        if (Directory.Exists(finalDir))
                        {
                            log.Report("Removing old version...");
                            Directory.Delete(finalDir, true);
                        }

                        Directory.CreateDirectory(finalDir);

                        // Copy the folder that owns the manifest so extra archive content stays outside the final install.
                        CopyDirectory(moduleSourceDir, finalDir);
                    }, ct);

                    config.SourcePath = Path.Combine(finalDir, "ASLM_Module.json");
                    await _moduleEngineReconciler.ReconcileRequiredEnginesAsync(
                        config,
                        log,
                        downloadProgress,
                        ct);

                    config.Status.Installed = true;
                    config.Status.InstalledVersion = config.Version;
                    config.Status.LastUpdated = DateTime.UtcNow.ToString("o");

                    await SaveConfigAsync(config);

                    // Run module first-run setup after the files are in their final location.
                    var success = await _moduleRunner.ExecuteFirstRunAsync(config, log, ct);
                    if (success)
                    {
                        await SaveConfigAsync(config);
                        log.Report($"Module '{config.Name}' installed successfully!");
                    }
                    else
                    {
                        log.Report($"Module '{config.Name}' installed, but setup failed.");
                    }

                    // Refresh the signed community-reviewed list when the remote trust API is enabled.
                    await _moduleTrustService.RefreshReviewedListAsync(ct);

                    return config;
                }
                finally
                {
                    TryDeleteDirectory(tempExtractDir);
                }
            }
            finally
            {
                TryDeleteFile(tempZip);
            }
        }

        /// <summary>
        /// Reconciles the engine definitions and dependencies declared by an installed module manifest.
        /// </summary>
        public Task ReconcileRequiredEnginesAsync(
            ModuleConfig module,
            IProgress<string> log,
            IProgress<DownloadProgress>? downloadProgress = null,
            CancellationToken ct = default) =>
            _moduleEngineReconciler.ReconcileRequiredEnginesAsync(module, log, downloadProgress, ct);


        // File copy

        /// <summary>
        /// Recursively copies one directory into another.
        /// </summary>
        private static void CopyDirectory(string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);

            foreach (var file in Directory.EnumerateFiles(sourceDir))
            {
                var destFile = Path.Combine(destDir, Path.GetFileName(file));
                File.Copy(file, destFile, true);
            }

            foreach (var subdir in Directory.EnumerateDirectories(sourceDir))
            {
                var destSubdir = Path.Combine(destDir, Path.GetFileName(subdir));
                CopyDirectory(subdir, destSubdir);
            }
        }


        // Download helper

        /// <summary>
        /// Downloads one file and reports throttled progress updates.
        /// </summary>
        private async Task DownloadFileAsync(
            string url,
            string destinationPath,
            IProgress<string> log,
            IProgress<DownloadProgress>? downloadProgress,
            CancellationToken ct)
        {
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? 0;
            log.Report(totalBytes > 0
                ? $"  Downloading: {totalBytes / 1024.0 / 1024.0:F1} MB..."
                : "  Downloading (size unknown)...");

            await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
            await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true);

            var buffer = new byte[65536];
            long downloaded = 0;
            int bytesRead;
            var throttle = Stopwatch.StartNew();

            var transferLabel = Path.GetFileName(destinationPath);
            downloadProgress?.Report(new DownloadProgress(0, 0, totalBytes, transferLabel));

            while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                downloaded += bytesRead;

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

            downloadProgress?.Report(new DownloadProgress(
                1.0,
                downloaded,
                totalBytes > 0 ? totalBytes : downloaded,
                transferLabel));

            log.Report("  Download complete.");
        }


        // Persistence helpers

        /// <summary>
        /// Returns the application root directory.
        /// </summary>
        private static string GetRootDirectory()
        {
            return AppRoot.Directory;
        }


        // Saving

        /// <summary>
        /// Saves a module manifest synchronously.
        /// </summary>
        /// <param name="config">Manifest to persist.</param>
        /// <param name="raiseModulesChanged">
        /// When true, raises <see cref="ModulesChanged"/> so hosts reload module lists and dashboards.
        /// Use false for saves that only mirror preferences already held by live view models (for example update-source pickers),
        /// so ephemeral state such as a completed update check is not discarded by rebuilding cards.
        /// </param>
        public void SaveModuleConfig(ModuleConfig config, bool raiseModulesChanged = true)
        {
            if (string.IsNullOrEmpty(config.SourcePath))
            {
                return;
            }

            var json = JsonSerializer.Serialize(config, _jsonOptions);
            WriteManifest(config.SourcePath, json);
            if (raiseModulesChanged)
            {
                RaiseModulesChanged();
            }
        }

        /// <summary>
        /// Saves a module manifest asynchronously.
        /// </summary>
        /// <param name="config">Manifest to persist.</param>
        /// <param name="raiseModulesChanged">
        /// When false, skips <see cref="ModulesChanged"/> so hosts do not rebuild module cards mid-flight
        /// (for example during a multi-step module update that would otherwise drop in-progress UI state).
        /// </param>
        public async Task SaveConfigAsync(ModuleConfig config, bool raiseModulesChanged = true)
        {
            if (string.IsNullOrEmpty(config.SourcePath))
            {
                return;
            }

            var json = JsonSerializer.Serialize(config, _jsonOptions);
            await WriteManifestAsync(config.SourcePath, json);
            if (raiseModulesChanged)
            {
                RaiseModulesChanged();
            }
        }

        /// <summary>
        /// Writes a manifest synchronously and retries brief reader/writer collisions.
        /// </summary>
        private static void WriteManifest(string path, string json)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.WriteAllText(path, json);
                    return;
                }
                catch (IOException ex) when (IsManifestSharingViolation(ex) && attempt < ManifestWriteAttemptCount)
                {
                    Thread.Sleep(ManifestWriteRetryDelay);
                }
            }
        }

        /// <summary>
        /// Writes a manifest asynchronously and retries brief reader/writer collisions.
        /// </summary>
        private static async Task WriteManifestAsync(string path, string json)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await File.WriteAllTextAsync(path, json);
                    return;
                }
                catch (IOException ex) when (IsManifestSharingViolation(ex) && attempt < ManifestWriteAttemptCount)
                {
                    await Task.Delay(ManifestWriteRetryDelay);
                }
            }
        }

        /// <summary>
        /// Identifies transient Windows sharing and file-lock violations that are safe to retry.
        /// </summary>
        private static bool IsManifestSharingViolation(IOException exception)
        {
            var errorCode = exception.HResult & 0xFFFF;
            return errorCode is 32 or 33;
        }


        // Temp cleanup

        /// <summary>
        /// Deletes a temporary file on a best-effort basis.
        /// </summary>
        private static void TryDeleteFile(string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
            catch
            {
                // Ignore cleanup failures for temporary files.
            }
        }

        /// <summary>
        /// Deletes a temporary directory on a best-effort basis.
        /// </summary>
        private static void TryDeleteDirectory(string directoryPath)
        {
            try
            {
                if (Directory.Exists(directoryPath))
                {
                    Directory.Delete(directoryPath, true);
                }
            }
            catch
            {
                // Ignore cleanup failures for temporary directories.
            }
        }

        /// <summary>
        /// Notifies listeners that installed module metadata changed.
        /// </summary>
        private void RaiseModulesChanged()
        {
            ModulesChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
