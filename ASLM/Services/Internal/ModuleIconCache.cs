// Copyright NEXTGGTECH. Apache License 2.0.

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace ASLM.Services.Internal;

/// <summary>Bounded disk cache for catalog artwork, independent of installed module files.</summary>
public sealed class ModuleIconCache
{
    internal const int Capacity = 256;
    internal static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);
    private const int MaximumIconBytes = 8 * 1024 * 1024;
    private const string IndexFileName = "index.json";
    private readonly string _directory;
    private readonly TimeProvider _clock;
    private readonly ILogger<ModuleIconCache>? _logger;
    private readonly SemaphoreSlim _filesGate = new(1, 1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _requests = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public ModuleIconCache(ILogger<ModuleIconCache> logger)
        : this(Path.Combine(AppRoot.Directory, "Data", "Downloads", "IconModules"), TimeProvider.System, logger) { }

    internal ModuleIconCache(string directory, TimeProvider? clock = null, ILogger<ModuleIconCache>? logger = null)
    {
        _directory = Path.GetFullPath(directory);
        _clock = clock ?? TimeProvider.System;
        _logger = logger;
    }

    internal sealed record DownloadedIcon(byte[] Data, string Extension);

    internal sealed record Entry(
        [property: JsonPropertyName("moduleId")] string ModuleId,
        [property: JsonPropertyName("downloadedAt")] DateTimeOffset DownloadedAt,
        [property: JsonPropertyName("fileName")] string FileName);

    internal async Task<byte[]?> GetOrDownloadAsync(string moduleId,
        Func<CancellationToken, Task<DownloadedIcon?>> download, CancellationToken ct = default)
    {
        var requestGate = _requests.GetOrAdd(moduleId, _ => new SemaphoreSlim(1, 1));
        await requestGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Read the index and file on every request, including after an external cache deletion.
            var cached = await Task.Run(() => ReadAsync(moduleId, ct), ct).ConfigureAwait(false);
            if (cached != null) return cached;
            var icon = await download(ct).ConfigureAwait(false);
            if (icon == null) return null;
            if (icon.Data.Length is > 0 and <= MaximumIconBytes)
                await Task.Run(() => StoreAsync(moduleId, icon, ct), ct).ConfigureAwait(false);
            return icon.Data;
        }
        finally { requestGate.Release(); }
    }

    /// <summary>Called by startup on a worker thread; requests also enforce expiry without restarting ASLM.</summary>
    internal async Task PruneAsync(CancellationToken ct = default)
    {
        await _filesGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureDirectory();
            var entries = ReadIndex();
            Prune(entries, Capacity);
            WriteIndex(entries);
        }
        catch (Exception ex) when (IsCacheError(ex)) { LogFailure(ex); }
        finally { _filesGate.Release(); }
    }

    private async Task<byte[]?> ReadAsync(string moduleId, CancellationToken ct)
    {
        await _filesGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureDirectory();
            var entries = ReadIndex();
            var now = _clock.GetUtcNow();
            // A hit only checks the requested icon. Full cleanup belongs to startup and cache writes,
            // not every card render (which otherwise scans all cached files once per module).
            var entry = entries.Where(item => string.Equals(item.ModuleId, moduleId, StringComparison.OrdinalIgnoreCase) &&
                    item.DownloadedAt <= now && now - item.DownloadedAt < Lifetime)
                .OrderByDescending(item => item.DownloadedAt).FirstOrDefault(item => IsUsableIcon(item.FileName));
            if (entry == null) return null;
            // Files can disappear between checking and reading; that is a cache miss, not a catalog failure.
            return await File.ReadAllBytesAsync(Path.Combine(_directory, entry.FileName), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsCacheError(ex)) { LogFailure(ex); return null; }
        finally { _filesGate.Release(); }
    }

    private async Task StoreAsync(string moduleId, DownloadedIcon icon, CancellationToken ct)
    {
        await _filesGate.WaitAsync(ct).ConfigureAwait(false);
        string? createdPath = null;
        try
        {
            EnsureDirectory();
            var entries = ReadIndex();
            foreach (var previous in entries.Where(item => string.Equals(item.ModuleId, moduleId, StringComparison.OrdinalIgnoreCase)).ToList())
                entries.Remove(previous);
            Prune(entries, Capacity - 1);
            // A locked orphan must not let the physical cache grow beyond the limit.
            if (Directory.EnumerateFiles(_directory).Count(path => IsIconFileName(Path.GetFileName(path))) >= Capacity)
            {
                WriteIndex(entries);
                return;
            }
            var extension = NormalizeExtension(icon.Extension);
            while (true)
            {
                var name = Guid.NewGuid().ToString("N") + extension;
                if (entries.Any(entry => string.Equals(entry.FileName, name, StringComparison.OrdinalIgnoreCase))) continue;
                var path = Path.Combine(_directory, name);
                FileStream stream;
                try { stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, useAsync: true); }
                catch (IOException) when (File.Exists(path)) { continue; }
                createdPath = path;
                await using (stream)
                {
                    await stream.WriteAsync(icon.Data, ct).ConfigureAwait(false);
                }
                entries.Add(new(moduleId, _clock.GetUtcNow(), name));
                WriteIndex(entries);
                createdPath = null;
                break;
            }
        }
        catch (Exception ex) when (IsCacheError(ex)) { LogFailure(ex); }
        finally
        {
            if (createdPath != null) DeleteCacheFile(Path.GetFileName(createdPath));
            _filesGate.Release();
        }
    }

    private void EnsureDirectory()
    {
        Directory.CreateDirectory(_directory);
        if ((File.GetAttributes(_directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The module icon cache must not be a symbolic link.");
    }

    private List<Entry> ReadIndex()
    {
        var path = Path.Combine(_directory, IndexFileName);
        if (!File.Exists(path)) return [];
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The module icon index must not be a symbolic link.");
        try { return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path))?.Where(entry => entry != null).ToList() ?? []; }
        catch (JsonException ex) { LogFailure(ex); return []; }
    }

    private void Prune(List<Entry> entries, int capacity)
    {
        var now = _clock.GetUtcNow();
        var retained = entries.Where(entry => !string.IsNullOrWhiteSpace(entry.ModuleId) &&
                entry.DownloadedAt <= now && now - entry.DownloadedAt < Lifetime && IsUsableIcon(entry.FileName))
            .OrderByDescending(entry => entry.DownloadedAt)
            .DistinctBy(entry => entry.ModuleId, StringComparer.OrdinalIgnoreCase)
            .DistinctBy(entry => entry.FileName, StringComparer.OrdinalIgnoreCase)
            .Take(capacity).ToList();
        entries.Clear();
        entries.AddRange(retained);
        var names = retained.Select(entry => entry.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Delete only files with our generated names; never follow paths supplied by the index.
        foreach (var path in Directory.EnumerateFiles(_directory))
        {
            var name = Path.GetFileName(path);
            if (IsIconFileName(name) && !names.Contains(name)) DeleteCacheFile(name);
        }
    }

    private bool IsUsableIcon(string? name)
    {
        if (!IsIconFileName(name)) return false;
        var info = new FileInfo(Path.Combine(_directory, name!));
        return info.Exists && (info.Attributes & FileAttributes.ReparsePoint) == 0 && info.Length is > 0 and <= MaximumIconBytes;
    }

    private static string NormalizeExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".ico" or ".svg" => extension.ToLowerInvariant(),
        _ => ".img"
    };

    private static bool IsIconFileName(string? name) => name is { Length: >= 36 and <= 37 } &&
        Guid.TryParseExact(name.AsSpan(0, 32), "N", out _) && name[32..] == NormalizeExtension(name[32..]);

    private void DeleteCacheFile(string name)
    {
        if (!IsIconFileName(name)) return;
        try { File.Delete(Path.Combine(_directory, name)); }
        catch (Exception ex) when (IsCacheError(ex)) { LogFailure(ex); }
    }

    private void WriteIndex(List<Entry> entries)
    {
        string temporary;
        FileStream stream;
        while (true)
        {
            temporary = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".tmp");
            try { stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None); break; }
            catch (IOException) when (File.Exists(temporary)) { }
        }
        try
        {
            using (stream)
            {
                JsonSerializer.Serialize(stream, entries, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, Path.Combine(_directory, IndexFileName), overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static bool IsCacheError(Exception error) => error is IOException or UnauthorizedAccessException or JsonException;
    private void LogFailure(Exception error) => _logger?.LogWarning(error, "Module icon cache operation failed; artwork can still be fetched from its repository.");
}
