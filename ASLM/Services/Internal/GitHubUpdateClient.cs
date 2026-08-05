// Copyright NEXTGGTECH. Apache License 2.0.

using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using ASLM.Models;

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Provides the GitHub API calls needed by ASLM update checks.
    /// </summary>
    public sealed class GitHubUpdateClient
    {
        private static readonly TimeSpan ReleaseCacheLifetime = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan BranchCacheLifetime = TimeSpan.FromMinutes(5);

        private readonly HttpClient _httpClient = new();
        private readonly GitHubRateLimitStore _rateLimitStore;
        private readonly GitHubAccountStore _githubAccountStore;
        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };
        private readonly SemaphoreSlim _cacheGate = new(1, 1);
        private readonly Dictionary<string, CacheEntry<List<GitHubReleaseInfo>>> _releaseCache =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CacheEntry<List<GitHubBranchInfo>>> _branchCache =
            new(StringComparer.OrdinalIgnoreCase);


        // Initialization

        /// <summary>
        /// Creates the GitHub client with the headers required by the REST API.
        /// </summary>
        public GitHubUpdateClient(GitHubRateLimitStore rateLimitStore, GitHubAccountStore githubAccountStore)
        {
            _rateLimitStore = rateLimitStore ?? throw new ArgumentNullException(nameof(rateLimitStore));
            _githubAccountStore = githubAccountStore ?? throw new ArgumentNullException(nameof(githubAccountStore));
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ASLM-Updater");
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            _httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        }


        // Rate limit metadata

        /// <summary>
        /// Refreshes the persisted GitHub rate-limit window from the dedicated API endpoint.
        /// </summary>
        public async Task RefreshRateLimitAsync(
            string requestSource = GitHubRequestSources.Auto,
            CancellationToken ct = default)
        {
            const string url = "https://api.github.com/rate_limit";
            using var request = CreateAuthorizedGetRequest(url);
            using var response = await _httpClient.SendAsync(request, ct);
            ApplyRateLimitHeaders(response);
            _rateLimitStore.RecordRequest(url, GitHubRequestTypes.RateLimit, requestSource, (int)response.StatusCode);
            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var payload = await JsonSerializer.DeserializeAsync<GitHubRateLimitPayload>(stream, _jsonOptions, ct);
            var core = payload?.Resources?.Core;
            if (core != null)
            {
                _rateLimitStore.UpdateFromHeaders(core.Limit, core.Remaining, core.Reset);
            }
        }


        // Release metadata

        /// <summary>
        /// Returns the most recent release allowed by the requested channel.
        /// </summary>
        public async Task<GitHubReleaseInfo?> GetLatestReleaseAsync(
            string repo,
            bool includePrerelease,
            string requestSource = GitHubRequestSources.Auto,
            CancellationToken ct = default)
        {
            var releases = await GetReleasesAsync(repo, includePrerelease, requestSource, ct);
            return releases.FirstOrDefault();
        }

        /// <summary>
        /// Returns the release list allowed by the requested channel.
        /// </summary>
        public async Task<List<GitHubReleaseInfo>> GetReleasesAsync(
            string repo,
            bool includePrerelease,
            string requestSource = GitHubRequestSources.Auto,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(repo))
            {
                return [];
            }

            var releases = await GetOrFetchCachedValueAsync(
                cache: _releaseCache,
                cacheKey: repo.Trim(),
                lifetime: ReleaseCacheLifetime,
                fetch: async cancellationToken => await FetchReleasesAsync(repo, requestSource, cancellationToken),
                ct);

            var filtered = releases
                .Where(release => !release.Draft)
                .Where(release => includePrerelease || !release.Prerelease)
                .ToList();
            filtered.Sort(ReleaseTagOrdering.CompareGitHubReleasesNewestFirst);
            return filtered;
        }


        // Branch metadata

        /// <summary>
        /// Returns all repository branch names with their current commit SHA.
        /// </summary>
        public Task<List<GitHubBranchInfo>> GetBranchesAsync(
            string repo,
            string requestSource = GitHubRequestSources.Auto,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(repo))
            {
                return Task.FromResult(new List<GitHubBranchInfo>());
            }

            return GetOrFetchCachedValueAsync(
                cache: _branchCache,
                cacheKey: repo.Trim(),
                lifetime: BranchCacheLifetime,
                fetch: async cancellationToken => await FetchBranchesAsync(repo, requestSource, cancellationToken),
                ct);
        }


        // Downloads

        /// <summary>
        /// Downloads one URL to the requested file path with throttled progress.
        /// </summary>
        public async Task DownloadFileAsync(
            string url,
            string destinationPath,
            IProgress<string>? log = null,
            IProgress<DownloadProgress>? downloadProgress = null,
            string requestSource = GitHubRequestSources.Auto,
            CancellationToken ct = default)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            log?.Report($"Downloading {url}");

            using var request = CreateAuthorizedGetRequest(url);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            ApplyRateLimitHeaders(response);
            _rateLimitStore.RecordRequest(
                url,
                ResolveDownloadRequestType(url),
                requestSource,
                (int)response.StatusCode);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? 0;
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

                if (totalBytes > 0 && throttle.ElapsedMilliseconds >= 75)
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
            log?.Report("Download complete.");
        }

        /// <summary>
        /// Downloads a repository ZIP archive for the requested ref.
        /// </summary>
        public Task DownloadRepositoryZipAsync(
            string repo,
            string reference,
            string destinationPath,
            IProgress<string>? log = null,
            IProgress<DownloadProgress>? downloadProgress = null,
            string requestSource = GitHubRequestSources.Auto,
            CancellationToken ct = default)
        {
            var url = BuildApiUrl(repo, $"zipball/{Uri.EscapeDataString(reference)}");
            return DownloadFileAsync(url, destinationPath, log, downloadProgress, requestSource, ct);
        }


        // Cache helpers

        /// <summary>
        /// Returns a cached value when still fresh, otherwise fetches and stores a new one.
        /// </summary>
        private async Task<T> GetOrFetchCachedValueAsync<T>(
            Dictionary<string, CacheEntry<T>> cache,
            string cacheKey,
            TimeSpan lifetime,
            Func<CancellationToken, Task<T>> fetch,
            CancellationToken ct)
        {
            if (TryGetCachedValue(cache, cacheKey, out var cached))
            {
                return cached;
            }

            await _cacheGate.WaitAsync(ct);
            try
            {
                if (TryGetCachedValue(cache, cacheKey, out cached))
                {
                    return cached;
                }

                try
                {
                    var fetched = await fetch(ct);
                    cache[cacheKey] = new CacheEntry<T>(fetched, DateTimeOffset.UtcNow.Add(lifetime));
                    return fetched;
                }
                catch when (TryGetAnyCachedValue(cache, cacheKey, out var stale))
                {
                    // When GitHub throttles the client, stale metadata is still more useful than a hard failure.
                    return stale;
                }
            }
            finally
            {
                _cacheGate.Release();
            }
        }

        /// <summary>
        /// Returns whether a cached value is still valid.
        /// </summary>
        private static bool TryGetCachedValue<T>(
            Dictionary<string, CacheEntry<T>> cache,
            string cacheKey,
            out T value)
        {
            if (cache.TryGetValue(cacheKey, out var entry) &&
                entry.ExpiresAt > DateTimeOffset.UtcNow)
            {
                value = entry.Value;
                return true;
            }

            value = default!;
            return false;
        }

        /// <summary>
        /// Returns any cached value even when it has expired.
        /// </summary>
        private static bool TryGetAnyCachedValue<T>(
            Dictionary<string, CacheEntry<T>> cache,
            string cacheKey,
            out T value)
        {
            if (cache.TryGetValue(cacheKey, out var entry))
            {
                value = entry.Value;
                return true;
            }

            value = default!;
            return false;
        }


        // API fetchers

        /// <summary>
        /// Loads the full release list from GitHub for the requested repository.
        /// </summary>
        private async Task<List<GitHubReleaseInfo>> FetchReleasesAsync(
            string repo,
            string requestSource,
            CancellationToken ct)
        {
            var url = BuildApiUrl(repo, "releases?per_page=100");
            return await GetGitHubJsonAsync<List<GitHubReleaseInfo>>(
                url,
                GitHubRequestTypes.Releases,
                requestSource,
                ct) ?? [];
        }

        /// <summary>
        /// Loads every branch name and commit SHA from GitHub for the requested repository.
        /// </summary>
        private async Task<List<GitHubBranchInfo>> FetchBranchesAsync(
            string repo,
            string requestSource,
            CancellationToken ct)
        {
            var branches = new List<GitHubBranchInfo>();
            var page = 1;

            while (true)
            {
                var url = BuildApiUrl(repo, $"branches?per_page=100&page={page}");
                var payload = await GetGitHubJsonAsync<List<GitHubBranchPayload>>(
                    url,
                    GitHubRequestTypes.Branches,
                    requestSource,
                    ct) ?? [];
                if (payload.Count == 0)
                {
                    break;
                }

                branches.AddRange(payload
                    .Where(branch => !string.IsNullOrWhiteSpace(branch.Name))
                    .Select(branch => new GitHubBranchInfo(branch.Name, branch.Commit?.Sha ?? string.Empty)));

                if (payload.Count < 100)
                {
                    break;
                }

                page++;
            }

            return branches
                .OrderBy(branch => branch.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Sends one GitHub API request, records usage, and deserializes the JSON payload.
        /// </summary>
        private async Task<T?> GetGitHubJsonAsync<T>(
            string url,
            string requestType,
            string requestSource,
            CancellationToken ct)
        {
            using var request = CreateAuthorizedGetRequest(url);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            ApplyRateLimitHeaders(response);
            _rateLimitStore.RecordRequest(url, requestType, requestSource, (int)response.StatusCode);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            return await JsonSerializer.DeserializeAsync<T>(stream, _jsonOptions, ct);
        }

        /// <summary>
        /// Creates one authorized GET request for the GitHub REST API.
        /// </summary>
        private HttpRequestMessage CreateAuthorizedGetRequest(string url)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyAuthorizationHeader(request);
            return request;
        }

        /// <summary>
        /// Applies the persisted GitHub bearer token when one is configured.
        /// </summary>
        private void ApplyAuthorizationHeader(HttpRequestMessage request)
        {
            var token = _githubAccountStore.GetPersonalAccessToken();
            if (!string.IsNullOrWhiteSpace(token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
        }

        /// <summary>
        /// Applies GitHub rate-limit headers when they are present on a response.
        /// </summary>
        private void ApplyRateLimitHeaders(HttpResponseMessage response)
        {
            if (!TryReadRateLimitHeaders(response, out var limit, out var remaining, out var resetEpoch))
            {
                return;
            }

            _rateLimitStore.UpdateFromHeaders(limit, remaining, resetEpoch);
        }

        /// <summary>
        /// Reads GitHub rate-limit headers from one HTTP response.
        /// </summary>
        private static bool TryReadRateLimitHeaders(
            HttpResponseMessage response,
            out int limit,
            out int remaining,
            out long resetEpoch)
        {
            limit = 0;
            remaining = 0;
            resetEpoch = 0;

            if (!response.Headers.TryGetValues("X-RateLimit-Limit", out var limitValues) ||
                !int.TryParse(limitValues.FirstOrDefault(), out limit))
            {
                return false;
            }

            if (!response.Headers.TryGetValues("X-RateLimit-Remaining", out var remainingValues) ||
                !int.TryParse(remainingValues.FirstOrDefault(), out remaining))
            {
                return false;
            }

            if (!response.Headers.TryGetValues("X-RateLimit-Reset", out var resetValues) ||
                !long.TryParse(resetValues.FirstOrDefault(), out resetEpoch))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Resolves the persisted request type for one download URL.
        /// </summary>
        private static string ResolveDownloadRequestType(string url)
        {
            if (url.Contains("/zipball/", StringComparison.OrdinalIgnoreCase))
            {
                return GitHubRequestTypes.Download;
            }

            return GitHubRequestTypes.Download;
        }

        /// <summary>
        /// Builds a GitHub REST API URL for the requested repository path.
        /// </summary>
        private static string BuildApiUrl(string repo, string path) =>
            $"https://api.github.com/repos/{repo.Trim().Trim('/')}/{path}";

        /// <summary>
        /// Stores one cached GitHub payload with its expiration timestamp.
        /// </summary>
        private sealed record CacheEntry<T>(T Value, DateTimeOffset ExpiresAt);
    }

    /// <summary>
    /// Minimal GitHub release payload used by update services.
    /// </summary>
    public sealed class GitHubReleaseInfo
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }

        [JsonPropertyName("created_at")]
        public DateTimeOffset? CreatedAt { get; set; }

        [JsonPropertyName("published_at")]
        public DateTimeOffset? PublishedAt { get; set; }

        [JsonPropertyName("zipball_url")]
        public string ZipballUrl { get; set; } = string.Empty;

        [JsonPropertyName("assets")]
        public List<GitHubReleaseAssetInfo> Assets { get; set; } = [];
    }

    /// <summary>
    /// Minimal GitHub release asset payload used by update services.
    /// </summary>
    public sealed class GitHubReleaseAssetInfo
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = string.Empty;

        [JsonPropertyName("size")]
        public long Size { get; set; }
    }

    /// <summary>
    /// Minimal GitHub branch payload used while paging the branches API.
    /// </summary>
    internal sealed class GitHubBranchPayload
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("commit")]
        public GitHubBranchCommitPayload? Commit { get; set; }
    }

    /// <summary>
    /// Commit reference embedded in one GitHub branch payload.
    /// </summary>
    internal sealed class GitHubBranchCommitPayload
    {
        [JsonPropertyName("sha")]
        public string Sha { get; set; } = string.Empty;
    }

    /// <summary>
    /// Minimal GitHub rate-limit payload returned by the dedicated API endpoint.
    /// </summary>
    internal sealed class GitHubRateLimitPayload
    {
        [JsonPropertyName("resources")]
        public GitHubRateLimitResourcesPayload? Resources { get; set; }
    }

    /// <summary>
    /// Resource buckets returned by the GitHub rate-limit API.
    /// </summary>
    internal sealed class GitHubRateLimitResourcesPayload
    {
        [JsonPropertyName("core")]
        public GitHubRateLimitCorePayload? Core { get; set; }
    }

    /// <summary>
    /// Core REST API rate-limit bucket returned by GitHub.
    /// </summary>
    internal sealed class GitHubRateLimitCorePayload
    {
        [JsonPropertyName("limit")]
        public int Limit { get; set; }

        [JsonPropertyName("remaining")]
        public int Remaining { get; set; }

        [JsonPropertyName("reset")]
        public long Reset { get; set; }
    }
}
