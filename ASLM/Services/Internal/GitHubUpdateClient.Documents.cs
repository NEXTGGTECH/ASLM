// Copyright NEXTGGTECH. Apache License 2.0.

using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ASLM.Models;

namespace ASLM.Services.Internal;

public sealed partial class GitHubUpdateClient
{
    // Repository documentation policy and short-lived request caches

    private const int MaxDocumentBytes = 1024 * 1024;
    private const int MaxRenderedBytes = 768 * 1024;
    private static readonly TimeSpan DocumentCacheLifetime = TimeSpan.FromMinutes(5);
    private static readonly string[] DocumentNames =
    [
        "README", "LICENSE", "LICENCE", "NOTICE", "COPYING", "COPYRIGHT", "AUTHORS", "CONTRIBUTORS",
        "CHANGELOG", "HISTORY", "CONTRIBUTING", "CODE_OF_CONDUCT", "SECURITY", "SUPPORT"
    ];
    private readonly ConcurrentDictionary<string, CachedRequest<IReadOnlyList<ModuleRepositoryDocument>>> _documentLists =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CachedRequest<string>> _documentContents = new(StringComparer.Ordinal);

    /// <summary>
    /// Lists only conventional documentation in the repository root on GitHub's actual default branch.
    /// </summary>
    internal Task<IReadOnlyList<ModuleRepositoryDocument>> GetModuleDocumentsAsync(string repo, CancellationToken ct = default) =>
        GetOrFetchCachedValueAsync(_documentLists, repo.Trim(), DocumentCacheLifetime, async token =>
        {
            // A single directory listing handles casing/extensions without probing guessed filenames.
            var bytes = await ReadDocumentResponseAsync(repo, "contents/", "application/vnd.github+json", token).ConfigureAwait(false);
            using var root = JsonDocument.Parse(bytes);
            return SelectModuleDocuments(root.RootElement);
        }, ct);

    /// <summary>
    /// Retrieves GitHub-rendered HTML only for a document accepted by the root-directory policy.
    /// </summary>
    internal Task<string> GetModuleDocumentHtmlAsync(string repo, ModuleRepositoryDocument document,
        CancellationToken ct = default)
    {
        if (!IsModuleDocumentName(document.Name))
            throw new ArgumentException("Unsupported module document name.", nameof(document));

        // Case is significant for Git paths; SHA separates cached revisions of the same document.
        return GetOrFetchCachedValueAsync(_documentContents, $"{repo.Trim()}/{document.Name}/{document.Sha}",
            DocumentCacheLifetime, async token => Encoding.UTF8.GetString(await ReadDocumentResponseAsync(repo,
                "contents/" + Uri.EscapeDataString(document.Name), "application/vnd.github.html+json", token).ConfigureAwait(false)), ct);
    }

    /// <summary>
    /// Accepts exact conventional basenames, regardless of case, with a small set of text extensions.
    /// </summary>
    internal static bool IsModuleDocumentName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(['/', '\\', ':']) >= 0) return false;
        var extension = Path.GetExtension(name);
        if (extension.Length > 0 && !extension.Equals(".md", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".markdown", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
            return false;
        // Do not normalize away a trailing dot or other extra characters outside the accepted extension.
        var basename = extension.Length == 0 ? name : name[..^extension.Length];
        return DocumentNames.Contains(basename, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Rejects directories, links, nested paths, oversized files and non-document names before fetching content.
    /// </summary>
    internal static IReadOnlyList<ModuleRepositoryDocument> SelectModuleDocuments(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array) return [];
        var documents = new List<ModuleRepositoryDocument>();
        foreach (var entry in root.EnumerateArray())
        {
            // Do not follow links or trust a friendly filename whose actual path points elsewhere.
            if (!entry.TryGetProperty("type", out var type) || type.GetString() != "file" ||
                entry.TryGetProperty("target", out _) || entry.TryGetProperty("submodule_git_url", out _) ||
                !entry.TryGetProperty("name", out var nameValue) || nameValue.ValueKind != JsonValueKind.String)
                continue;
            var name = nameValue.GetString()!;
            if (!IsModuleDocumentName(name) || !entry.TryGetProperty("path", out var path) || path.GetString() != name ||
                !entry.TryGetProperty("size", out var size) || !size.TryGetInt64(out var length) || length is <= 0 or > MaxDocumentBytes)
                continue;
            var sha = entry.TryGetProperty("sha", out var value) ? value.GetString() ?? string.Empty : string.Empty;
            documents.Add(new(name, sha));
        }

        // README leads; retain distinct accepted files (including separate license variants) deterministically.
        return documents.DistinctBy(document => document.Name)
            .OrderBy(document => Array.FindIndex(DocumentNames, name =>
                name.Equals(Path.GetFileNameWithoutExtension(document.Name), StringComparison.OrdinalIgnoreCase)))
            .ThenBy(document => document.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// Downloads a bounded metadata/HTML response through the shared account and quota tracking.
    /// </summary>
    private async Task<byte[]> ReadDocumentResponseAsync(string repo, string path, string mediaType, CancellationToken ct)
    {
        using var request = CreateAuthorizedGetRequest(BuildApiUrl(repo, path));
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(mediaType));
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        TrackApiResponse(request, response);
        response.EnsureSuccessStatusCode();

        // Enforce the same limit for chunked responses, not just Content-Length. This also bounds WebView memory.
        // Leave headroom for the themed wrapper and UTF-16 conversion below WebView2's 2 MB HTML limit.
        var limit = mediaType == "application/vnd.github.html+json" ? MaxRenderedBytes : MaxDocumentBytes;
        if (response.Content.Headers.ContentLength > limit)
            throw new InvalidDataException("Repository document is too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (output.Length + count > limit)
                throw new InvalidDataException("Repository document is too large.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
}
