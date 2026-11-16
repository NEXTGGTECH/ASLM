// Copyright NEXTGGTECH. Apache License 2.0.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ASLM.Models;

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Discovers a module's root manifest through authenticated, bounded repository requests.
    /// </summary>
    public sealed partial class GitHubUpdateClient
    {
        // Discovery caches

        /// <summary>
        /// Reuses repository identity and default branch metadata without retaining credentials in cache keys.
        /// </summary>
        private readonly ConcurrentDictionary<string, CachedRequest<GitHubRepositoryInfo>> _repositories =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Reuses root manifest JSON separately from mutable module previews.
        /// </summary>
        private readonly ConcurrentDictionary<string, CachedRequest<string>> _moduleManifests =
            new(StringComparer.Ordinal);


        // Repository discovery

        /// <summary>
        /// Resolves the canonical repository identity and its actual default branch.
        /// </summary>
        internal Task<GitHubRepositoryInfo> GetRepositoryInfoAsync(string repo, CancellationToken ct) =>
            GetOrFetchCachedValueAsync(_repositories, GetRepositoryCacheKey(repo), ReleaseCacheLifetime,
                async token =>
                {
                    var result = await GetGitHubJsonAsync<GitHubRepositoryInfo>(
                        $"https://api.github.com/repos/{repo}", GitHubRequestTypes.Download,
                        GitHubRequestSources.Manual, token);
                    if (result == null || !GitHubRepository.TryParse(result.FullName, out _) ||
                        string.IsNullOrWhiteSpace(result.DefaultBranch))
                        throw new InvalidDataException("GitHub returned incomplete repository metadata.");
                    return result;
                }, ct, allowStale: false);

        /// <summary>
        /// Reads only ASLM_Module.json in the selected root and refuses oversized or non-file responses.
        /// </summary>
        internal Task<string> GetModuleManifestJsonAsync(string repo, string branch, CancellationToken ct) =>
            GetOrFetchCachedValueAsync(_moduleManifests, GetRepositoryCacheKey(repo) + "/" + branch,
                ReleaseCacheLifetime, async token =>
                {
                    var path = "contents/ASLM_Module.json?ref=" + Uri.EscapeDataString(branch);
                    var bytes = await ReadDocumentResponseAsync(repo, path, "application/vnd.github+json", token,
                        maxBytes: 2 * 1024 * 1024);
                    using var document = JsonDocument.Parse(bytes);
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object ||
                        !root.TryGetProperty("type", out var type) || type.GetString() != "file" ||
                        !root.TryGetProperty("path", out var file) || file.GetString() != "ASLM_Module.json" ||
                        !root.TryGetProperty("size", out var size) || !size.TryGetInt32(out var length) ||
                        length is <= 0 or > 1024 * 1024 ||
                        !root.TryGetProperty("encoding", out var encoding) || encoding.GetString() != "base64" ||
                        !root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String)
                        throw new InvalidDataException("The repository root manifest is not a supported JSON file.");

                    var decoded = Convert.FromBase64String(content.GetString()!);
                    if (decoded.Length > 1024 * 1024)
                        throw new InvalidDataException("Module manifest is too large.");
                    var json = new UTF8Encoding(false, true).GetString(decoded);
                    return json.StartsWith((char)0xFEFF) ? json[1..] : json;
                }, ct, allowStale: false);

        /// <summary>
        /// Separates authenticated responses when the configured token changes, including disconnects.
        /// </summary>
        private string GetRepositoryCacheKey(string repo)
        {
            var credential = _githubAccountStore.GetPersonalAccessToken() ?? string.Empty;
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credential)));
            return fingerprint + "/" + repo.Trim().ToLowerInvariant();
        }
    }

    /// <summary>
    /// Contains only repository metadata needed to discover and install a module.
    /// </summary>
    internal sealed class GitHubRepositoryInfo
    {
        /// <summary>
        /// Gets or sets GitHub's canonical owner/name identity.
        /// </summary>
        [JsonPropertyName("full_name")]
        public string FullName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the default branch without assuming conventional branch names.
        /// </summary>
        [JsonPropertyName("default_branch")]
        public string DefaultBranch { get; set; } = string.Empty;
    }
}
