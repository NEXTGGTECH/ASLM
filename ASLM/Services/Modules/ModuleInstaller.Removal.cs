// Copyright NEXTGGTECH. Apache License 2.0.

using System.Collections.Concurrent;
using System.Text.Json;
using ASLM.Localization;
using ASLM.Models;

namespace ASLM.Services.Modules;

public partial class ModuleInstaller
{
    private static readonly object OperationLock = new();
    private static int _contentOperations;
    private static bool _removingModule;
    private readonly ConcurrentDictionary<string, byte> _removedModuleIds = new(StringComparer.OrdinalIgnoreCase);

    // Removing shared runtimes must not race launches, updates, or bridge downloads, even for another module.
    internal static IDisposable BeginContentOperation()
    {
        lock (OperationLock)
        {
            if (_removingModule) throw new InvalidOperationException(L.Get(LocalizationKeys.Modules_RemoveBusy));
            _contentOperations++;
            return new ContentOperation();
        }
    }

    private sealed class ContentOperation : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            lock (OperationLock)
            {
                if (_disposed) return;
                _disposed = true;
                _contentOperations--;
            }
        }
    }

    private void EnsureNotRemoved(ModuleConfig module)
    {
        if (_removedModuleIds.ContainsKey(module.Id))
            throw new InvalidOperationException(L.Get(LocalizationKeys.Modules_RemoveStale));
    }

    /// <summary>
    /// Removes installed content while retaining the manifest and artwork as the local catalog entry.
    /// Returns a cleanup directory only if Windows could not delete staged files after committing removal.
    /// </summary>
    public async Task<string?> UninstallAsync(ModuleConfig selected, CancellationToken ct = default)
    {
        lock (OperationLock)
        {
            if (_removingModule || _contentOperations != 0)
                throw new InvalidOperationException(L.Get(LocalizationKeys.Modules_RemoveBusy));
            _removingModule = true;
        }

        var changed = false;
        try
        {
            var ids = Registry.ReadIds();
            if (!ids.Contains(selected.Id)) return null;
            var catalog = await DiscoverModulesAsync().ConfigureAwait(false);
            var installed = catalog.Where(module => ids.Contains(module.Id)).ToList();
            if (ids.Any(id => installed.Count(module => string.Equals(module.Id, id, StringComparison.OrdinalIgnoreCase)) != 1))
                throw new InvalidOperationException(L.Get(LocalizationKeys.Modules_RemoveUnknownDependencies));

            var target = installed.Single(module => string.Equals(module.Id, selected.Id, StringComparison.OrdinalIgnoreCase));
            var remaining = installed.Where(module => !ReferenceEquals(module, target)).ToList();
            var dependents = remaining.Where(module => module.Dependencies.Modules.Any(dependency =>
                string.Equals(dependency.Id, target.Id, StringComparison.OrdinalIgnoreCase))).ToList();
            if (dependents.Count > 0)
                throw new InvalidOperationException(L.Get(LocalizationKeys.Modules_RemoveRequiredFormat,
                    string.Join(", ", dependents.Select(module => module.Name))));

            var root = GetRootDirectory();
            var moduleDirectory = Path.GetDirectoryName(target.SourcePath)!;
            ValidateRemovalPath(moduleDirectory, Path.Combine(root, "Modules"));
            ValidateRemovalPath(target.SourcePath, moduleDirectory);
            if (!ModuleManifestDiscovery.IsInstalledModuleManifest(Path.Combine(root, "Modules"), target.SourcePath))
                throw new InvalidOperationException(L.Get(LocalizationKeys.Modules_RemoveUnsafePath));

            _engineInstaller.InvalidateCache();
            var engines = _engineInstaller.DiscoverEngines();
            var plan = BuildRemovalPlan(root, target, remaining, engines);
            foreach (var directory in plan.Directories.Append(moduleDirectory)) ValidateTree(directory);
            ct.ThrowIfCancellationRequested();

            if (_moduleRunner != null)
            {
                await _moduleRunner.StopModuleAsync(target.SourcePath).ConfigureAwait(false);
                if (target.Status.Enabled)
                {
                    // A failed removal must not leave the restored card claiming that its stopped process is running.
                    target.Status.Enabled = false;
                    await WriteManifestAsync(target.SourcePath, JsonSerializer.Serialize(target, _jsonOptions)).ConfigureAwait(false);
                    changed = true;
                }
            }

            var result = await Task.Run(() => RemoveInstalledFiles(target, moduleDirectory, plan), ct).ConfigureAwait(false);
            _removedModuleIds[target.Id] = 0;
            changed = true;
            return result;
        }
        finally
        {
            _engineInstaller.InvalidateCache();
            lock (OperationLock) _removingModule = false;
            if (changed) RaiseModulesChanged();
        }
    }

    internal sealed record RemovalPlan(List<string> Directories, List<EngineConfig> Engines);

    /// <summary>Checks all surviving consumers, including command/settings engines and custom paths.</summary>
    internal static RemovalPlan BuildRemovalPlan(string root, ModuleConfig target,
        IReadOnlyList<ModuleConfig> remaining, IReadOnlyList<EngineConfig> engines)
    {
        var directories = new List<string>();
        var unusedEngines = new List<EngineConfig>();
        var targetEngines = GetEngineIds(target);
        var retainedEngines = remaining.SelectMany(GetEngineIds).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var moduleDirectory = Path.GetDirectoryName(target.SourcePath)!;
        var customReferences = remaining.SelectMany(GetCustomPaths)
            .Concat(remaining.SelectMany(module => GetBridgeDirectories(root, module))).ToList();
        // A surviving alias might point into a candidate directory; do not guess its ownership.
        if (customReferences.Any(HasLinkedAncestor))
            throw new InvalidOperationException(L.Get(LocalizationKeys.Modules_RemoveUnknownDependencies));
        if (customReferences.Any(path => PathsOverlap(moduleDirectory, path)))
            throw new InvalidOperationException(L.Get(LocalizationKeys.Modules_RemoveSharedFiles));

        var modelReferences = remaining.SelectMany(module => GetModelDirectories(root, module)).ToList();
        foreach (var engine in engines.Where(engine => targetEngines.Contains(engine.Id)))
        {
            var engineDirectory = Path.GetDirectoryName(engine.SourcePath)!;
            ValidateRemovalPath(engineDirectory, Path.Combine(root, "Engines"));
            ValidateRemovalPath(engine.SourcePath, engineDirectory);
            var runtime = Path.Combine(engineDirectory, "runtime");
            if (!retainedEngines.Contains(engine.Id) && !customReferences.Any(path => PathsOverlap(engineDirectory, path)))
            {
                directories.Add(runtime);
                unusedEngines.Add(engine);
            }

            if (ModuleEnvironmentResolver.HasModuleEnvironment(engine))
            {
                var environment = ModuleEnvironmentResolver.GetEnvironmentDirectory(target, engine);
                ValidateRemovalPath(environment, engineDirectory);
                var sharedEnvironment = remaining.Where(module => GetEngineIds(module).Contains(engine.Id))
                    .Any(module => PathsOverlap(environment, ModuleEnvironmentResolver.GetEnvironmentDirectory(module, engine)));
                if (!sharedEnvironment && !customReferences.Any(path => PathsOverlap(environment, path)))
                    directories.Add(environment);
            }
        }

        foreach (var models in GetModelDirectories(root, target))
        {
            // A custom location outside ASLM belongs to the user, not to the module uninstaller.
            if (!IsStrictChild(models, Path.Combine(root, "Models"))) continue;
            if (modelReferences.Any(path => PathsOverlap(models, path)) ||
                customReferences.Any(path => PathsOverlap(models, path))) continue;
            // Model names alone do not identify storage or consumers; keep ambiguous shared stores intact.
            if (remaining.Any(module => module.Dependencies.Models.Count > 0)) continue;
            ValidateRemovalPath(models, Path.Combine(root, "Models"));
            directories.Add(models);
        }

        var distinct = directories.Distinct(PathComparer).ToList();
        return new RemovalPlan(distinct.Where(path => !distinct.Any(other =>
            !PathComparer.Equals(path, other) && IsStrictChild(path, other))).ToList(), unusedEngines);
    }

    private static HashSet<string> GetEngineIds(ModuleConfig module) =>
        module.Dependencies.Engines.Select(dependency => dependency.Id)
            .Concat(module.Engines.Select(engine => engine.Id))
            .Concat(module.Commands.FirstRun.Concat(module.Commands.Run).Select(command => command.Engine))
            .Concat(module.Settings.Select(setting => setting.Engine))
            .Concat(new[] { module.DownloadsBridge?.Engine ?? string.Empty })
            .Concat(module.Settings.Where(setting => setting.NormalizedType is "engine" or "path" or "data" or "models")
                .Select(setting => GetSettingEngineId(setting.Key)))
            .Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string GetSettingEngineId(string key) => key.EndsWith("_models", StringComparison.OrdinalIgnoreCase)
        ? key[..^7] : key.EndsWith("_path", StringComparison.OrdinalIgnoreCase) || key.EndsWith("_data", StringComparison.OrdinalIgnoreCase)
            ? key[..^5] : key;

    private static IEnumerable<string> GetCustomPaths(ModuleConfig module)
    {
        foreach (var setting in module.Settings.Where(setting => setting.UseCustomValue &&
                     setting.NormalizedType is "path" or "data" or "models"))
        {
            var value = (setting.Value ?? setting.Default)?.ToString();
            if (!string.IsNullOrWhiteSpace(value))
                yield return Path.GetFullPath(value, Path.GetDirectoryName(module.SourcePath)!);
        }
    }

    private static IEnumerable<string> GetModelDirectories(string root, ModuleConfig module)
    {
        // Engine consumers can use the common model store without declaring a models setting.
        foreach (var id in GetEngineIds(module))
        {
            if (id.IndexOfAny(['/', '\\']) >= 0 || id is "." or "..")
                throw new InvalidOperationException(L.Get(LocalizationKeys.Modules_RemoveUnsafePath));
            yield return Path.Combine(root, "Models", id);
        }
        foreach (var setting in module.Settings.Where(setting => setting.NormalizedType == "models" && setting.UseCustomValue))
        {
            var value = (setting.Value ?? setting.Default)?.ToString();
            if (!string.IsNullOrWhiteSpace(value))
                yield return Path.GetFullPath(value, Path.GetDirectoryName(module.SourcePath)!);
        }
        foreach (var path in GetBridgeDirectories(root, module).Where(path => IsStrictChild(path, Path.Combine(root, "Models"))))
            yield return path;
    }

    private static IEnumerable<string> GetBridgeDirectories(string root, ModuleConfig module)
    {
        if (module.DownloadsBridge == null) yield break;
        foreach (var target in module.DownloadsBridge.Targets.Values)
        {
            var bucket = target.Root.Trim().ToLowerInvariant() switch
            {
                "root" => root,
                "models" => Path.Combine(root, "Models"),
                "engines" => Path.Combine(root, "Engines"),
                "modules" => Path.Combine(root, "Modules"),
                "data" => Path.Combine(root, "Data"),
                "tools" => Path.Combine(root, "Tools"),
                _ => null
            };
            if (bucket == null) throw new InvalidOperationException(L.Get(LocalizationKeys.Modules_RemoveUnknownDependencies));
            var path = Path.GetFullPath(Path.Combine(bucket, target.Relative));
            if (!PathComparer.Equals(path, bucket) && !IsStrictChild(path, bucket))
                throw new InvalidOperationException(L.Get(LocalizationKeys.Modules_RemoveUnsafePath));
            yield return path;
        }
    }

    private string? RemoveInstalledFiles(ModuleConfig target, string moduleDirectory, RemovalPlan plan)
    {
        var stage = Path.Combine(GetRootDirectory(), "Data", "App", "ModuleRemoval-" + Guid.NewGuid().ToString("N"));
        ValidateRemovalPath(stage, Path.Combine(GetRootDirectory(), "Data", "App"));
        var moves = new List<(string Original, string Staged)>();
        var engineManifests = plan.Engines.Where(engine => File.Exists(engine.SourcePath))
            .ToDictionary(engine => engine.SourcePath, engine => File.ReadAllBytes(engine.SourcePath), PathComparer);
        var artwork = new[] { target.IconFullPath, target.SidebarIconFullPath }
            .Where(path => !string.IsNullOrWhiteSpace(path) && IsStrictChild(path!, moduleDirectory) && File.Exists(path))
            .Distinct(PathComparer).ToDictionary(path => Path.GetRelativePath(moduleDirectory, path!), path =>
            {
                ValidateRemovalPath(path!, moduleDirectory);
                return File.ReadAllBytes(path!);
            });

        Directory.CreateDirectory(stage);
        var catalogCreated = false;
        try
        {
            // Move first, commit metadata last. A locked file or failed registry write rolls back these moves.
            foreach (var directory in plan.Directories.Append(moduleDirectory).Where(Directory.Exists))
            {
                ValidateTree(directory);
                var staged = Path.Combine(stage, moves.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Directory.Move(directory, staged);
                moves.Add((directory, staged));
            }
            Directory.CreateDirectory(moduleDirectory);
            catalogCreated = true;
            foreach (var (relative, bytes) in artwork)
            {
                var destination = Path.Combine(moduleDirectory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.WriteAllBytes(destination, bytes);
            }
            target.Status = new ModuleStatus();
            target.Update.PendingUpdate = null;
            target.Update.InstalledCommitSha = null;
            target.Update.InstalledReleaseTag = null;
            foreach (var setting in target.Settings)
            {
                setting.Value = null;
                setting.UseCustomValue = false;
            }
            File.WriteAllText(target.SourcePath, JsonSerializer.Serialize(target, _jsonOptions));
            foreach (var engine in plan.Engines)
            {
                engine.Status = new EngineStatus();
                if (engineManifests.ContainsKey(engine.SourcePath))
                    File.WriteAllText(engine.SourcePath, JsonSerializer.Serialize(engine, _jsonOptions));
            }
            Registry.Remove(target.Id);
        }
        catch
        {
            // Never clear the staging area when restoration fails: it contains the recoverable original data.
            if (catalogCreated)
            {
                ValidateTree(moduleDirectory);
                Directory.Delete(moduleDirectory, recursive: true);
            }
            foreach (var move in moves.AsEnumerable().Reverse()) Directory.Move(move.Staged, move.Original);
            foreach (var (path, bytes) in engineManifests) File.WriteAllBytes(path, bytes);
            Directory.Delete(stage);
            throw;
        }

        try
        {
            ValidateTree(stage);
            ClearReadOnlyFiles(stage);
            Directory.Delete(stage, recursive: true);
            return null;
        }
        catch (Exception) { return stage; }
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows() || OperatingSystem.IsMacCatalyst() || OperatingSystem.IsMacOS()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static bool IsStrictChild(string path, string parent)
    {
        var parentPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(parentPath,
            PathComparer == StringComparer.OrdinalIgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static bool PathsOverlap(string first, string second) =>
        PathComparer.Equals(Path.GetFullPath(first), Path.GetFullPath(second)) ||
        IsStrictChild(first, second) || IsStrictChild(second, first);

    internal static void ValidateRemovalPath(string path, string parent)
    {
        if (!IsStrictChild(path, parent) || HasLinkedAncestor(path))
            throw new InvalidOperationException(L.Get(LocalizationKeys.Modules_RemoveUnsafePath));
    }

    private static bool HasLinkedAncestor(string path)
    {
        for (var current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
        {
            if ((Directory.Exists(current) || File.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                return true;
        }
        return false;
    }

    private static void ValidateTree(string directory)
    {
        if (!Directory.Exists(directory)) return;
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException(L.Get(LocalizationKeys.Modules_RemoveUnsafePath));
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            // A venv may contain symlinks. Directory.Delete removes links themselves, never their targets.
            if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
            if ((attributes & FileAttributes.Directory) != 0) ValidateTree(entry);
        }
    }

    private static void ClearReadOnlyFiles(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
            if ((attributes & FileAttributes.Directory) != 0) ClearReadOnlyFiles(entry);
            else File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
        }
    }
}
