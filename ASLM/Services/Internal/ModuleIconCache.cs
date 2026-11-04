// Copyright NEXTGGTECH. Apache License 2.0.

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Bounded disk cache for catalog artwork, independent of installed module files.
    /// Icons are stored under generated file names next to an <c>index.json</c> that maps module ids to files;
    /// the cache is limited in entry count, entry age and file size, and every failure degrades to a cache miss.
    /// </summary>
    public sealed class ModuleIconCache
    {
        // Limits and file names

        /// <summary>
        /// Maximum number of cached icons, both in the index and as files on disk.
        /// </summary>
        internal const int Capacity = 256;

        /// <summary>
        /// How long a cached icon stays valid after it was downloaded.
        /// </summary>
        internal static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

        /// <summary>
        /// Largest icon, in bytes, that is stored or served from the cache.
        /// </summary>
        private const int MaximumIconBytes = 8 * 1024 * 1024;

        /// <summary>
        /// Name of the index file inside the cache directory.
        /// </summary>
        private const string IndexFileName = "index.json";


        // Dependencies and synchronization

        /// <summary>
        /// Absolute path of the cache directory.
        /// </summary>
        private readonly string _directory;

        /// <summary>
        /// Time source for entry ages; replaceable so tests can control expiry.
        /// </summary>
        private readonly TimeProvider _clock;

        /// <summary>
        /// Logger for cache failures, or null when none was supplied.
        /// </summary>
        private readonly ILogger<ModuleIconCache>? _logger;

        /// <summary>
        /// Serializes every access to the cache directory and its index file.
        /// </summary>
        private readonly SemaphoreSlim _filesGate = new(1, 1);

        /// <summary>
        /// Per-module gates so concurrent requests for one module download its icon only once.
        /// </summary>
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _requests = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// JSON settings used to write the index file.
        /// </summary>
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };


        // Initialization

        /// <summary>
        /// Creates the cache in <c>Data/Downloads/IconModules</c> below the ASLM data root.
        /// </summary>
        /// <param name="logger">Logger for cache failures.</param>
        public ModuleIconCache(ILogger<ModuleIconCache> logger)
            : this(Path.Combine(AppRoot.Directory, "Data", "Downloads", "IconModules"), TimeProvider.System, logger) { }

        /// <summary>
        /// Creates the cache in an explicit directory (test isolation).
        /// </summary>
        /// <param name="directory">Cache directory; created on first use.</param>
        /// <param name="clock">Time source, or null for the system clock.</param>
        /// <param name="logger">Logger for cache failures, or null to stay silent.</param>
        internal ModuleIconCache(string directory, TimeProvider? clock = null, ILogger<ModuleIconCache>? logger = null)
        {
            _directory = Path.GetFullPath(directory);
            _clock = clock ?? TimeProvider.System;
            _logger = logger;
        }


        // Nested types

        /// <summary>
        /// Icon bytes returned by a download callback together with their file extension.
        /// </summary>
        /// <param name="Data">Raw image data.</param>
        /// <param name="Extension">File extension of the image format, including the dot.</param>
        internal sealed record DownloadedIcon(byte[] Data, string Extension);

        /// <summary>
        /// One index record: the cached file of a module and when it was downloaded.
        /// </summary>
        /// <param name="ModuleId">Id of the module the icon belongs to.</param>
        /// <param name="DownloadedAt">Download time, used for expiry and for choosing the newest entry.</param>
        /// <param name="FileName">Generated name of the icon file inside the cache directory.</param>
        internal sealed record Entry(
            [property: JsonPropertyName("moduleId")] string ModuleId,
            [property: JsonPropertyName("downloadedAt")] DateTimeOffset DownloadedAt,
            [property: JsonPropertyName("fileName")] string FileName);


        // Cache operations

        /// <summary>
        /// Returns the cached icon of a module, or downloads, stores and returns it on a miss.
        /// </summary>
        /// <param name="moduleId">Id of the module whose icon is requested.</param>
        /// <param name="download">Callback that fetches the icon from its repository; may return null when there is none.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The icon bytes, or null when neither the cache nor the download produced an icon.</returns>
        internal async Task<byte[]?> GetOrDownloadAsync(string moduleId,
            Func<CancellationToken, Task<DownloadedIcon?>> download, CancellationToken ct = default)
        {
            // One request per module at a time, so parallel callers share a single download.
            var requestGate = _requests.GetOrAdd(moduleId, _ => new SemaphoreSlim(1, 1));
            await requestGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Read the index and file on every request, including after an external cache deletion.
                var cached = await Task.Run(() => ReadAsync(moduleId, ct), ct).ConfigureAwait(false);
                if (cached != null) return cached;

                // Cache miss: download, then store only icons of an acceptable size. The bytes are returned either way.
                var icon = await download(ct).ConfigureAwait(false);
                if (icon == null) return null;
                if (icon.Data.Length is > 0 and <= MaximumIconBytes)
                    await Task.Run(() => StoreAsync(moduleId, icon, ct), ct).ConfigureAwait(false);
                return icon.Data;
            }
            finally { requestGate.Release(); }
        }

        /// <summary>
        /// Removes expired, duplicate, unusable and surplus entries and files. Called by startup on a worker thread;
        /// requests also enforce expiry without restarting ASLM.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
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


        // Reading and storing icons

        /// <summary>
        /// Returns the bytes of the newest valid cached icon of a module, or null on a miss or a cache error.
        /// </summary>
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

        /// <summary>
        /// Writes a downloaded icon to a new uniquely named file and records it in the index,
        /// replacing any previous entry of the same module.
        /// </summary>
        private async Task StoreAsync(string moduleId, DownloadedIcon icon, CancellationToken ct)
        {
            await _filesGate.WaitAsync(ct).ConfigureAwait(false);
            string? createdPath = null;
            try
            {
                EnsureDirectory();

                // Drop the module's old entries and make room for the new one before writing anything.
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

                // Pick a generated file name that is not taken and create the file exclusively (CreateNew).
                // createdPath stays set until the index references the file, so a failure in between
                // removes the half-written file in the finally block below.
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

                    // Write the icon, then publish it in the index.
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


        // Directory, index and pruning

        /// <summary>
        /// Creates the cache directory when missing and refuses a directory that is a symbolic link.
        /// </summary>
        private void EnsureDirectory()
        {
            Directory.CreateDirectory(_directory);
            if ((File.GetAttributes(_directory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The module icon cache must not be a symbolic link.");
        }

        /// <summary>
        /// Reads the index file; a missing or corrupt file yields an empty list. A symbolic-link index is rejected.
        /// </summary>
        private List<Entry> ReadIndex()
        {
            var path = Path.Combine(_directory, IndexFileName);
            if (!File.Exists(path)) return [];
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The module icon index must not be a symbolic link.");
            try { return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path))?.Where(entry => entry != null).ToList() ?? []; }
            catch (JsonException ex) { LogFailure(ex); return []; }
        }

        /// <summary>
        /// Keeps only valid, unexpired entries (newest per module and per file, at most <paramref name="capacity"/>)
        /// and deletes cache files that no kept entry references.
        /// </summary>
        /// <param name="entries">Index entries; the list is replaced in place by the retained entries.</param>
        /// <param name="capacity">Maximum number of entries to keep.</param>
        private void Prune(List<Entry> entries, int capacity)
        {
            // Retain newest first; entries dated in the future, expired or without a usable file are dropped.
            var now = _clock.GetUtcNow();
            var retained = entries.Where(entry => !string.IsNullOrWhiteSpace(entry.ModuleId) &&
                    entry.DownloadedAt <= now && now - entry.DownloadedAt < Lifetime && IsUsableIcon(entry.FileName))
                .OrderByDescending(entry => entry.DownloadedAt)
                .DistinctBy(entry => entry.ModuleId, StringComparer.OrdinalIgnoreCase)
                .DistinctBy(entry => entry.FileName, StringComparer.OrdinalIgnoreCase)
                .Take(capacity).ToList();
            entries.Clear();
            entries.AddRange(retained);

            // Remove unreferenced icon files from disk. Only files with our generated names are deleted;
            // paths supplied by the index are never followed.
            var names = retained.Select(entry => entry.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.EnumerateFiles(_directory))
            {
                var name = Path.GetFileName(path);
                if (IsIconFileName(name) && !names.Contains(name)) DeleteCacheFile(name);
            }
        }


        // Icon file rules

        /// <summary>
        /// Checks that an index entry points to an existing regular icon file within the size limit.
        /// </summary>
        private bool IsUsableIcon(string? name)
        {
            if (!IsIconFileName(name)) return false;
            var info = new FileInfo(Path.Combine(_directory, name!));
            return info.Exists && (info.Attributes & FileAttributes.ReparsePoint) == 0 && info.Length is > 0 and <= MaximumIconBytes;
        }

        /// <summary>
        /// Lowercases a known image extension; unknown extensions are stored as <c>.img</c>.
        /// </summary>
        private static string NormalizeExtension(string extension) => extension.ToLowerInvariant() switch
        {
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".ico" or ".svg" => extension.ToLowerInvariant(),
            _ => ".img"
        };

        /// <summary>
        /// Recognizes the generated icon file names: a 32-digit GUID followed by a normalized extension.
        /// </summary>
        private static bool IsIconFileName(string? name) => name is { Length: >= 36 and <= 37 } &&
            Guid.TryParseExact(name.AsSpan(0, 32), "N", out _) && name[32..] == NormalizeExtension(name[32..]);

        /// <summary>
        /// Deletes one cache file by name; names that do not look like generated icon names are ignored.
        /// </summary>
        private void DeleteCacheFile(string name)
        {
            if (!IsIconFileName(name)) return;
            try { File.Delete(Path.Combine(_directory, name)); }
            catch (Exception ex) when (IsCacheError(ex)) { LogFailure(ex); }
        }


        // Index writing

        /// <summary>
        /// Replaces the index file atomically: writes a temporary file, flushes it to disk and moves it over the index.
        /// </summary>
        private void WriteIndex(List<Entry> entries)
        {
            // Create the temporary file exclusively under a fresh random name.
            string temporary;
            FileStream stream;
            while (true)
            {
                temporary = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".tmp");
                try { stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None); break; }
                catch (IOException) when (File.Exists(temporary))
                {
                    // The generated name is already taken: try another one.
                }
            }

            // Write and flush, then swap it in; a leftover temporary file is removed on any failure.
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


        // Error handling

        /// <summary>
        /// Identifies the errors treated as recoverable cache problems: file system access and JSON failures.
        /// </summary>
        private static bool IsCacheError(Exception error) => error is IOException or UnauthorizedAccessException or JsonException;

        /// <summary>
        /// Logs a cache failure as a warning; callers continue as if the icon were not cached.
        /// </summary>
        private void LogFailure(Exception error) => _logger?.LogWarning(error, "Module icon cache operation failed; artwork can still be fetched from its repository.");
    }
}
