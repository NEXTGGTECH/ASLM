// Copyright NEXTGGTECH. Apache License 2.0.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using ASLM.Models;

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Provides the GitHub API calls needed by ASLM update checks.
    /// This part covers rate-limit tracking, release and branch metadata, file and archive downloads and the
    /// shared request helpers; repository documentation lives in the Documents part.
    /// </summary>
    public sealed partial class GitHubUpdateClient
    {
        // Fields and constants

        /// <summary>
        /// How long a fetched release list is reused before GitHub is asked again.
        /// </summary>
        private static readonly TimeSpan ReleaseCacheLifetime = TimeSpan.FromMinutes(5);

        /// <summary>
        /// How long a fetched branch list is reused before GitHub is asked again.
        /// </summary>
        private static readonly TimeSpan BranchCacheLifetime = TimeSpan.FromMinutes(5);

        /// <summary>
        /// HTTP client used for all GitHub requests; carries the default REST API headers.
        /// </summary>
        private readonly HttpClient _httpClient;

        /// <summary>
        /// Quota tracker that records every request and learns the remaining budget from response headers.
        /// </summary>
        private readonly GitHubRateLimitStore _rateLimitStore;

        /// <summary>
        /// Source of the persisted personal access token that authorizes API requests.
        /// </summary>
        private readonly GitHubAccountStore _githubAccountStore;

        /// <summary>
        /// Cache that stores the module icons downloaded from repositories.
        /// </summary>
        private readonly ModuleIconCache _moduleIcons;

        /// <summary>
        /// JSON settings used to read GitHub API payloads.
        /// </summary>
        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        /// <summary>
        /// Cached release lists by repository.
        /// </summary>
        private readonly ConcurrentDictionary<string, CachedRequest<List<GitHubReleaseInfo>>> _releaseCache =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Cached branch lists by repository.
        /// </summary>
        private readonly ConcurrentDictionary<string, CachedRequest<List<GitHubBranchInfo>>> _branchCache =
            new(StringComparer.OrdinalIgnoreCase);


        // Initialization

        /// <summary>
        /// Creates the GitHub client with the headers required by the REST API.
        /// </summary>
        public GitHubUpdateClient(GitHubRateLimitStore rateLimitStore, GitHubAccountStore githubAccountStore, ModuleIconCache moduleIcons)
            : this(rateLimitStore, githubAccountStore, new HttpClient(), moduleIcons)
        {
        }

        /// <summary>
        /// Creates the GitHub client with an explicit HTTP client (test isolation).
        /// </summary>
        internal GitHubUpdateClient(GitHubRateLimitStore rateLimitStore, GitHubAccountStore githubAccountStore, HttpClient httpClient,
            ModuleIconCache moduleIcons)
        {
            _httpClient = httpClient;
            _moduleIcons = moduleIcons;
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
        /// <param name="requestSource">Whether the user or the application triggered the refresh.</param>
        /// <param name="ct">Cancellation token for the request.</param>
        public async Task RefreshRateLimitAsync(
            string requestSource = GitHubRequestSources.Auto,
            CancellationToken ct = default)
        {
            // Query the rate-limit endpoint and record the request in the usage history.
            const string url = "https://api.github.com/rate_limit";
            using var request = CreateAuthorizedGetRequest(url);
            using var response = await _httpClient.SendAsync(request, ct);
            ApplyRateLimitHeaders(response, request);
            _rateLimitStore.RecordRequest(url, GitHubRequestTypes.RateLimit, requestSource, (int)response.StatusCode);
            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            // Apply the core (REST) bucket from the payload to the persisted window.
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var payload = await JsonSerializer.DeserializeAsync<GitHubRateLimitPayload>(stream, _jsonOptions, ct);
            var core = payload?.Resources?.Core;
            if (core != null)
            {
                _rateLimitStore.UpdateFromHeaders(core.Limit, core.Remaining, core.Reset, request.Headers.Authorization != null);
            }
        }


        // Release metadata

        /// <summary>
        /// Returns the most recent release allowed by the requested channel, or null when there is none.
        /// </summary>
        /// <param name="repo">Repository in <c>owner/name</c> form.</param>
        /// <param name="includePrerelease">Whether prereleases may be returned.</param>
        /// <param name="requestSource">Whether the user or the application triggered the request.</param>
        /// <param name="ct">Cancellation token for the request.</param>
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
        /// Returns the release list allowed by the requested channel, sorted newest first.
        /// Drafts are always excluded; the list is cached per repository for a short time.
        /// </summary>
        /// <param name="repo">Repository in <c>owner/name</c> form; a blank value yields an empty list.</param>
        /// <param name="includePrerelease">Whether prereleases are included.</param>
        /// <param name="requestSource">Whether the user or the application triggered the request.</param>
        /// <param name="ct">Cancellation token for the request.</param>
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

            // The cache stores the unfiltered list, so both channels share one GitHub request.
            var releases = await GetOrFetchCachedValueAsync(
                cache: _releaseCache,
                cacheKey: repo.Trim(),
                lifetime: ReleaseCacheLifetime,
                fetch: async cancellationToken => await FetchReleasesAsync(repo, requestSource, cancellationToken),
                ct);

            // Apply the channel filter on a copy and order the result newest first.
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
        /// The list is cached per repository for a short time.
        /// </summary>
        /// <param name="repo">Repository in <c>owner/name</c> form; a blank value yields an empty list.</param>
        /// <param name="requestSource">Whether the user or the application triggered the request.</param>
        /// <param name="ct">Cancellation token for the request.</param>
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


        // Module artwork

        /// <summary>
        /// Reads the current manifest and icon from the repository's default branch, without downloading the module.
        /// The repository is contacted only when the icon cache has no icon for the module yet.
        /// </summary>
        /// <param name="repo">Repository in <c>owner/name</c> form.</param>
        /// <param name="moduleId">Module id that the repository manifest must declare.</param>
        /// <param name="ct">Cancellation token for the requests.</param>
        internal async Task<ModuleArtwork> GetModuleArtworkAsync(string repo, string moduleId, CancellationToken ct = default) =>
            new(await _moduleIcons.GetOrDownloadAsync(moduleId,
                async token =>
                {
                    var manifestBytes = await GetRepositoryFileAsync(repo, "ASLM_Module.json", 1024 * 1024, token);
                    using var manifest = JsonDocument.Parse(manifestBytes);
                    var iconPath = ReadModuleIconPath(manifest.RootElement, moduleId);
                    return iconPath == null ? null : new ModuleIconCache.DownloadedIcon(
                        await GetRepositoryFileAsync(repo, iconPath, 8 * 1024 * 1024, token), Path.GetExtension(iconPath));
                }, ct));

        /// <summary>
        /// Artwork read from a module repository.
        /// </summary>
        /// <param name="Icon">Icon file bytes, or null when the manifest declares no icon.</param>
        internal sealed record ModuleArtwork(byte[]? Icon);

        /// <summary>
        /// Extracts the icon path from a module manifest and validates it.
        /// Returns null when the manifest declares no icon and throws <see cref="JsonException"/> when the manifest
        /// belongs to another module or the icon path could escape the repository.
        /// </summary>
        /// <param name="manifest">Root element of the <c>ASLM_Module.json</c> manifest.</param>
        /// <param name="moduleId">Module id the manifest must declare.</param>
        internal static string? ReadModuleIconPath(JsonElement manifest, string moduleId)
        {
            // A repository that holds another module must not provide this module's icon.
            if (!manifest.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
                !string.Equals(id.GetString(), moduleId, StringComparison.OrdinalIgnoreCase))
                throw new JsonException("The repository manifest belongs to a different module.");
            if (!manifest.TryGetProperty("icon", out var icon) || icon.ValueKind == JsonValueKind.Null) return null;

            // Normalize separators and reject traversal, empty segments and drive-like segments.
            var path = icon.GetString()?.Trim().Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(path)) return null;
            var segments = path.Split('/');
            if (segments.Any(segment => segment is ".." or "" || segment.Contains(':')))
                throw new JsonException("The module icon must be a repository-relative path.");
            return string.Join('/', segments.Where(segment => segment != "."));
        }

        /// <summary>
        /// Downloads one file from the repository's default branch through the contents API, refusing files above a size limit.
        /// </summary>
        /// <param name="repo">Repository in <c>owner/name</c> form.</param>
        /// <param name="path">Repository-relative file path using forward slashes.</param>
        /// <param name="maxBytes">Largest accepted file size in bytes.</param>
        /// <param name="ct">Cancellation token for the request.</param>
        private async Task<byte[]> GetRepositoryFileAsync(string repo, string path, int maxBytes, CancellationToken ct)
        {
            // Omitting ref selects GitHub's actual default branch (which need not be named main).
            var url = BuildApiUrl(repo, "contents/" + string.Join('/', path.Split('/').Select(Uri.EscapeDataString)));
            using var request = CreateAuthorizedGetRequest(url);
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.raw+json"));
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            ApplyRateLimitHeaders(response, request);
            _rateLimitStore.RecordRequest(url, GitHubRequestTypes.Download, GitHubRequestSources.Manual, (int)response.StatusCode);
            response.EnsureSuccessStatusCode();

            // Reject by declared size first, then enforce the same limit while reading, because the length may be missing.
            if (response.Content.Headers.ContentLength > maxBytes) throw new InvalidDataException("Repository file is too large.");
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(buffer, ct)) > 0)
            {
                if (output.Length + count > maxBytes) throw new InvalidDataException("Repository file is too large.");
                output.Write(buffer, 0, count);
            }

            return output.ToArray();
        }


        // Downloads

        /// <summary>
        /// Downloads one URL to the requested file path with throttled progress.
        /// </summary>
        /// <param name="url">URL to download; GitHub API URLs are authorized and counted against the quota.</param>
        /// <param name="destinationPath">File that is created or overwritten.</param>
        /// <param name="log">Optional receiver of human-readable log lines.</param>
        /// <param name="downloadProgress">Optional receiver of progress updates.</param>
        /// <param name="requestSource">Whether the user or the application triggered the download.</param>
        /// <param name="ct">Cancellation token for the transfer.</param>
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

            // Send the request and account for it in the quota history before touching the body.
            using var request = CreateAuthorizedGetRequest(url);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            ApplyRateLimitHeaders(response, request);
            _rateLimitStore.RecordRequest(
                url,
                ResolveDownloadRequestType(url),
                requestSource,
                (int)response.StatusCode);
            response.EnsureSuccessStatusCode();

            // Stream the body to disk and report progress.
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

                // Throttle UI updates to one every 75 ms; the size must be known to compute a fraction.
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

            // Report a final 100% so consumers do not stay at the last throttled value.
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
        /// <param name="repo">Repository in <c>owner/name</c> form.</param>
        /// <param name="reference">Tag, branch or commit to archive.</param>
        /// <param name="destinationPath">File that is created or overwritten.</param>
        /// <param name="log">Optional receiver of human-readable log lines.</param>
        /// <param name="downloadProgress">Optional receiver of progress updates.</param>
        /// <param name="requestSource">Whether the user or the application triggered the download.</param>
        /// <param name="ct">Cancellation token for the transfer.</param>
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
        /// A failed fetch falls back to the stale value when one exists.
        /// </summary>
        /// <param name="cache">Cache that holds one entry per key.</param>
        /// <param name="cacheKey">Key of the requested value, for example a repository name.</param>
        /// <param name="lifetime">How long a fetched value stays fresh.</param>
        /// <param name="fetch">Loads the value from GitHub.</param>
        /// <param name="ct">Cancellation token for waiting and fetching.</param>
        private async Task<T> GetOrFetchCachedValueAsync<T>(
            ConcurrentDictionary<string, CachedRequest<T>> cache,
            string cacheKey,
            TimeSpan lifetime,
            Func<CancellationToken, Task<T>> fetch,
            CancellationToken ct)
        {
            // Coalesce the same request without making unrelated repositories or branches wait for it.
            var request = cache.GetOrAdd(cacheKey, _ => new CachedRequest<T>());
            await request.Gate.WaitAsync(ct);
            try
            {
                // A fresh entry is served without contacting GitHub.
                if (request.Entry is { } cached && cached.ExpiresAt > DateTimeOffset.UtcNow)
                {
                    return cached.Value;
                }

                // Fetch and cache a new value.
                try
                {
                    var fetched = await fetch(ct);
                    ct.ThrowIfCancellationRequested();
                    request.Entry = new CacheEntry<T>(fetched, DateTimeOffset.UtcNow.Add(lifetime));
                    return fetched;
                }
                catch when (!ct.IsCancellationRequested && request.Entry != null)
                {
                    // When GitHub throttles the client, stale metadata is still more useful than a hard failure.
                    return request.Entry.Value;
                }
            }
            finally
            {
                request.Gate.Release();
            }
        }


        // API fetchers

        /// <summary>
        /// Loads the full release list from GitHub for the requested repository.
        /// Only the first page (up to 100 releases) is requested.
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

            // Page through the branches API (100 per page) until an empty or short page signals the end.
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

            // Present the branches alphabetically regardless of the order GitHub returned them in.
            return branches
                .OrderBy(branch => branch.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Sends one GitHub API request, records usage, and deserializes the JSON payload.
        /// </summary>
        /// <param name="url">Absolute GitHub API URL.</param>
        /// <param name="requestType">Kind of request recorded in the usage history (see <see cref="GitHubRequestTypes"/>).</param>
        /// <param name="requestSource">Whether the user or the application triggered the request.</param>
        /// <param name="ct">Cancellation token for the request.</param>
        private async Task<T?> GetGitHubJsonAsync<T>(
            string url,
            string requestType,
            string requestSource,
            CancellationToken ct)
        {
            // Account for the request in the quota history before failing on an error status.
            using var request = CreateAuthorizedGetRequest(url);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            ApplyRateLimitHeaders(response, request);
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
            PrepareApiRequest(request);
            return request;
        }

        /// <summary>
        /// Applies the persisted GitHub bearer token when one is configured.
        /// Requests to hosts other than the GitHub API are left untouched, so the token is never sent elsewhere.
        /// </summary>
        internal void PrepareApiRequest(HttpRequestMessage request)
        {
            if (!IsGitHubApiRequest(request)) return;
            request.Headers.UserAgent.ParseAdd("ASLM-Updater");
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            var token = _githubAccountStore.GetPersonalAccessToken();
            if (!string.IsNullOrWhiteSpace(token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
        }

        /// <summary>
        /// Applies GitHub rate-limit headers when they are present on a response.
        /// </summary>
        private void ApplyRateLimitHeaders(HttpResponseMessage response, HttpRequestMessage request)
        {
            if (IsGitHubApiRequest(request))
                _rateLimitStore.UpdateFromResponse(response, request.Headers.Authorization != null);
        }

        /// <summary>
        /// Shares quota tracking with direct module/engine archive downloads.
        /// Responses that did not come from the GitHub API are ignored.
        /// </summary>
        /// <param name="request">Request that was sent.</param>
        /// <param name="response">Response received for that request.</param>
        internal void TrackApiResponse(HttpRequestMessage request, HttpResponseMessage response)
        {
            if (!IsGitHubApiRequest(request)) return;
            ApplyRateLimitHeaders(response, request);
            _rateLimitStore.RecordRequest(request.RequestUri!.AbsoluteUri, GitHubRequestTypes.Download,
                GitHubRequestSources.Manual, (int)response.StatusCode);
        }

        /// <summary>
        /// Returns whether the request targets the HTTPS GitHub REST API host, the only host that receives
        /// the token and counts against the quota.
        /// </summary>
        private static bool IsGitHubApiRequest(HttpRequestMessage request) =>
            request.RequestUri is { Scheme: "https" } uri &&
            string.Equals(uri.Host, "api.github.com", StringComparison.OrdinalIgnoreCase);

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

        /// <summary>
        /// Cache slot of one request key; the gate lets concurrent callers of the same key share one fetch.
        /// </summary>
        private sealed class CachedRequest<T>
        {
            /// <summary>
            /// Serializes fetches of this key.
            /// </summary>
            public SemaphoreSlim Gate { get; } = new(1, 1);

            /// <summary>
            /// Last successfully fetched value, or null before the first fetch.
            /// </summary>
            public CacheEntry<T>? Entry { get; set; }
        }
    }


    // GitHub API payloads

    /// <summary>
    /// Minimal GitHub release payload used by update services.
    /// </summary>
    public sealed class GitHubReleaseInfo
    {
        /// <summary>
        /// Git tag the release was created from, for example <c>v1.2.3.4</c>.
        /// </summary>
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = string.Empty;

        /// <summary>
        /// Human-readable release title; may be empty when the release has no title.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Whether the release is an unpublished draft.
        /// </summary>
        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        /// <summary>
        /// Whether the release is marked as a prerelease.
        /// </summary>
        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }

        /// <summary>
        /// Time of the commit the release is based on (not the publication time), or null when absent.
        /// </summary>
        [JsonPropertyName("created_at")]
        public DateTimeOffset? CreatedAt { get; set; }

        /// <summary>
        /// Time the release was published, or null for drafts.
        /// </summary>
        [JsonPropertyName("published_at")]
        public DateTimeOffset? PublishedAt { get; set; }

        /// <summary>
        /// API URL of the source ZIP archive of the release tag.
        /// </summary>
        [JsonPropertyName("zipball_url")]
        public string ZipballUrl { get; set; } = string.Empty;

        /// <summary>
        /// Files uploaded to the release.
        /// </summary>
        [JsonPropertyName("assets")]
        public List<GitHubReleaseAssetInfo> Assets { get; set; } = [];
    }

    /// <summary>
    /// Minimal GitHub release asset payload used by update services.
    /// </summary>
    public sealed class GitHubReleaseAssetInfo
    {
        /// <summary>
        /// File name of the asset as uploaded to the release.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Public URL from which the asset file can be downloaded.
        /// </summary>
        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = string.Empty;

        /// <summary>
        /// Asset size in bytes.
        /// </summary>
        [JsonPropertyName("size")]
        public long Size { get; set; }
    }

    /// <summary>
    /// Minimal GitHub branch payload used while paging the branches API.
    /// </summary>
    internal sealed class GitHubBranchPayload
    {
        /// <summary>
        /// Branch name.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Commit the branch currently points to, or null when GitHub omits it.
        /// </summary>
        [JsonPropertyName("commit")]
        public GitHubBranchCommitPayload? Commit { get; set; }
    }

    /// <summary>
    /// Commit reference embedded in one GitHub branch payload.
    /// </summary>
    internal sealed class GitHubBranchCommitPayload
    {
        /// <summary>
        /// Full SHA of the commit at the branch head.
        /// </summary>
        [JsonPropertyName("sha")]
        public string Sha { get; set; } = string.Empty;
    }

    /// <summary>
    /// Minimal GitHub rate-limit payload returned by the dedicated API endpoint.
    /// </summary>
    internal sealed class GitHubRateLimitPayload
    {
        /// <summary>
        /// Rate-limit buckets by API resource category.
        /// </summary>
        [JsonPropertyName("resources")]
        public GitHubRateLimitResourcesPayload? Resources { get; set; }
    }

    /// <summary>
    /// Resource buckets returned by the GitHub rate-limit API.
    /// </summary>
    internal sealed class GitHubRateLimitResourcesPayload
    {
        /// <summary>
        /// Bucket of the core REST API, which all repository, release and contents requests count against.
        /// </summary>
        [JsonPropertyName("core")]
        public GitHubRateLimitCorePayload? Core { get; set; }
    }

    /// <summary>
    /// Core REST API rate-limit bucket returned by GitHub.
    /// </summary>
    internal sealed class GitHubRateLimitCorePayload
    {
        /// <summary>
        /// Requests allowed per window.
        /// </summary>
        [JsonPropertyName("limit")]
        public int Limit { get; set; }

        /// <summary>
        /// Requests left in the current window.
        /// </summary>
        [JsonPropertyName("remaining")]
        public int Remaining { get; set; }

        /// <summary>
        /// Unix time in seconds at which the window resets.
        /// </summary>
        [JsonPropertyName("reset")]
        public long Reset { get; set; }
    }
}
