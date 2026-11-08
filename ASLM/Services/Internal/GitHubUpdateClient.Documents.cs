// Copyright NEXTGGTECH. Apache License 2.0.

using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ASLM.Models;

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Repository documentation part of the GitHub client: lists and renders the README, license and similar
    /// files from a module repository under a strict root-directory and size policy.
    /// </summary>
    public sealed partial class GitHubUpdateClient
    {
        // Documentation policy and caches

        /// <summary>
        /// Largest accepted size in bytes of a documentation file and of a directory listing response.
        /// </summary>
        private const int MaxDocumentBytes = 1024 * 1024;

        /// <summary>
        /// Largest accepted size in bytes of a GitHub-rendered HTML document, kept below the WebView2 HTML limit.
        /// </summary>
        private const int MaxRenderedBytes = 768 * 1024;

        /// <summary>
        /// How long a document list or rendered document is reused before GitHub is asked again.
        /// </summary>
        private static readonly TimeSpan DocumentCacheLifetime = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Conventional documentation base names, in display order; a document whose name (without extension)
        /// is not listed here is not shown.
        /// </summary>
        private static readonly string[] DocumentNames =
        [
            "README", "LICENSE", "LICENCE", "NOTICE", "COPYING", "COPYRIGHT", "AUTHORS", "CONTRIBUTORS",
            "CHANGELOG", "HISTORY", "CONTRIBUTING", "CODE_OF_CONDUCT", "SECURITY", "SUPPORT"
        ];

        /// <summary>
        /// Cached document lists by repository.
        /// </summary>
        private readonly ConcurrentDictionary<string, CachedRequest<IReadOnlyList<ModuleRepositoryDocument>>> _documentLists =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Cached rendered document HTML by repository, document name and revision SHA.
        /// </summary>
        private readonly ConcurrentDictionary<string, CachedRequest<string>> _documentContents = new(StringComparer.Ordinal);


        // Document listing and rendering

        /// <summary>
        /// Lists only conventional documentation in the repository root on GitHub's actual default branch.
        /// </summary>
        /// <param name="repo">Repository in <c>owner/name</c> form.</param>
        /// <param name="ct">Cancellation token for the request.</param>
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
        /// Throws <see cref="ArgumentException"/> for any other document name.
        /// </summary>
        /// <param name="repo">Repository in <c>owner/name</c> form.</param>
        /// <param name="document">Document previously returned by the listing; its name and SHA identify the cached revision.</param>
        /// <param name="ct">Cancellation token for the request.</param>
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
        /// <param name="name">File name from the repository root; names with path separators are rejected.</param>
        internal static bool IsModuleDocumentName(string? name)
        {
            // Names that contain a path or drive separator are never plain root files.
            if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(['/', '\\', ':']) >= 0) return false;

            // Only Markdown and plain-text extensions (or none) are documentation.
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
        /// Returns the accepted documents with the README first.
        /// </summary>
        /// <param name="root">JSON array returned by the GitHub contents API for the repository root.</param>
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

                // Accept only recognized names located directly in the root with a plausible, bounded size.
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
        /// <param name="repo">Repository in <c>owner/name</c> form.</param>
        /// <param name="path">Path below the repository's <c>contents</c> API resource.</param>
        /// <param name="mediaType">GitHub media type to request: directory metadata or rendered HTML.</param>
        /// <param name="ct">Cancellation token for the request.</param>
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
}
