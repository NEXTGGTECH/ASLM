// Copyright NEXTGGTECH. Apache License 2.0.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ASLM.Models;
using Microsoft.Extensions.Logging;

namespace ASLM.Services.Modules
{
    /// <summary>
    /// Resolves module trust levels from the official catalog and a signed community-reviewed list.
    /// A module is official when it matches a built-in rule, community reviewed when it matches an entry of the
    /// reviewed list, and unreviewed otherwise. The reviewed list is downloaded from a configured URL, accepted only
    /// with a valid RSA signature, and cached on disk so it also works offline.
    /// </summary>
    public sealed class ModuleTrustService
    {
        // Fields and constants

        /// <summary>
        /// File name of the shipped trust-source configuration in <c>Data/App</c>.
        /// </summary>
        private const string TrustSourceFileName = "ASLM_ModuleTrustSource.json";

        /// <summary>
        /// File name of the cached, signature-verified reviewed list in <c>Data/App</c>.
        /// </summary>
        private const string ReviewedCacheFileName = "ASLM_ReviewedModules.cache.json";

        /// <summary>
        /// Built-in rules for official modules: every GitHub repository of the NEXTGGTECH author.
        /// </summary>
        private static readonly OfficialModuleTrustEntry[] OfficialRules =
        [
            new("github", null, "NEXTGGTECH/*"),
            // new("github", "aslm-chat", "NEXTGGTECH/ASLM-Chat"),
        ];

        /// <summary>
        /// HTTP client used to download the reviewed list.
        /// </summary>
        private readonly HttpClient _httpClient;

        /// <summary>
        /// Logger for refresh, cache and verification problems.
        /// </summary>
        private readonly ILogger<ModuleTrustService> _logger;

        /// <summary>
        /// JSON settings used to read and write the trust source, the reviewed list and its cache.
        /// </summary>
        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true
        };

        /// <summary>
        /// Compact JSON settings that produce the exact byte form of the signed payload for signature checks.
        /// </summary>
        private readonly JsonSerializerOptions _canonicalJsonOptions = new()
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        /// <summary>
        /// Guards the reviewed rules against concurrent trust lookups and refreshes.
        /// </summary>
        private readonly object _sync = new();

        /// <summary>
        /// Shipped trust-source configuration: reviewed list URL, public key and refresh interval.
        /// </summary>
        private ModuleTrustSourceConfig _sourceConfig = new();

        /// <summary>
        /// Community-reviewed rules from the last verified list or cache; empty until one is loaded.
        /// </summary>
        private List<ReviewedModuleTrustEntry> _reviewedRules = [];

        /// <summary>
        /// UTC time of the last successful remote refresh, or <see cref="DateTime.MinValue"/> when none happened yet.
        /// </summary>
        private DateTime _lastRemoteRefresh = DateTime.MinValue;


        // Initialization

        /// <summary>
        /// Creates the module trust service.
        /// </summary>
        public ModuleTrustService(ILogger<ModuleTrustService> logger)
            : this(logger, new HttpClient())
        {
        }

        /// <summary>
        /// Creates the module trust service with an explicit HTTP client (test isolation).
        /// </summary>
        internal ModuleTrustService(ILogger<ModuleTrustService> logger, HttpClient httpClient)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _httpClient = httpClient;
            _httpClient.Timeout = TimeSpan.FromSeconds(30);
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ASLM-ModuleTrust");
        }


        // Lifecycle

        /// <summary>
        /// Loads shipped configuration, restores the verified cache, and refreshes when configured.
        /// </summary>
        public async Task InitializeAsync(CancellationToken ct = default)
        {
            // Without a shipped trust-source file the service runs with defaults: only built-in rules apply.
            _sourceConfig = await LoadTrustSourceConfigAsync(ct) ?? new ModuleTrustSourceConfig();
            _sourceConfig.Normalize();

            // Start from the signature-verified cache so trust works offline; an invalid cache leaves no reviewed rules.
            if (!TryLoadVerifiedCache(out var cachedRules))
            {
                lock (_sync)
                {
                    _reviewedRules = [];
                }
            }
            else
            {
                lock (_sync)
                {
                    _reviewedRules = cachedRules;
                }
            }

            // Then try to replace the cache with a fresh list when one is configured and due.
            await RefreshReviewedListAsync(ct);
        }


        // Trust resolution

        /// <summary>
        /// Resolves the trust level for one installed module manifest.
        /// Built-in official rules are checked before the community-reviewed list.
        /// </summary>
        public ModuleTrustLevel Resolve(ModuleConfig config)
        {
            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            config.Normalize();

            // Official rules take precedence over the reviewed list.
            if (TryMatchOfficial(config))
            {
                return ModuleTrustLevel.Official;
            }

            if (TryMatchReviewed(config))
            {
                return ModuleTrustLevel.CommunityReviewed;
            }

            return ModuleTrustLevel.Unreviewed;
        }


        // Remote refresh

        /// <summary>
        /// Downloads and verifies the signed community-reviewed list when a URL is configured.
        /// Nothing happens without a URL, before the refresh interval has elapsed, or without a public key.
        /// A failed download or verification keeps the rules that are currently loaded;
        /// only cancellation is rethrown.
        /// </summary>
        public async Task RefreshReviewedListAsync(CancellationToken ct = default)
        {
            // Skip the refresh when no list is configured or the last fetch is still fresh.
            var source = _sourceConfig;
            if (string.IsNullOrWhiteSpace(source.ReviewedListUrl))
            {
                return;
            }

            if (!ShouldRefreshRemoteList(source))
            {
                return;
            }

            // An unsigned download would be untrustworthy, so a list without a configured key is never fetched.
            if (string.IsNullOrWhiteSpace(source.PublicKeyBase64))
            {
                _logger.LogWarning("Community-reviewed module list URL is configured but publicKeyBase64 is missing.");
                return;
            }

            try
            {
                // Download the list and accept it only when it parses and carries a valid signature.
                var json = await _httpClient.GetStringAsync(source.ReviewedListUrl, ct);
                if (!TryParseSignedPayload(json, out var payload))
                {
                    _logger.LogWarning("Community-reviewed module list could not be parsed.");
                    return;
                }

                if (!TryVerifySignature(payload, source.PublicKeyBase64))
                {
                    _logger.LogWarning("Community-reviewed module list could not be verified.");
                    return;
                }

                var rules = payload.Modules
                    .ToList();

                // Cache the verified payload for offline starts, then switch the active rules to it.
                await SaveVerifiedCacheAsync(payload, payload.Signature, ct);

                lock (_sync)
                {
                    _reviewedRules = rules;
                    _lastRemoteRefresh = DateTime.UtcNow;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Network or disk problems must not break startup; the previously loaded rules stay active.
                _logger.LogWarning(ex, "Failed to refresh the community-reviewed module list.");
            }
        }


        // Matching

        /// <summary>
        /// Returns whether the manifest matches a built-in official module or author entry.
        /// </summary>
        private static bool TryMatchOfficial(ModuleConfig config) =>
            OfficialRules.Any(entry => ModuleTrustIdentity.Matches(config, entry.Source, entry.Id, entry.Repo));

        /// <summary>
        /// Returns whether the manifest matches a cached community-reviewed module or author entry.
        /// </summary>
        private bool TryMatchReviewed(ModuleConfig config)
        {
            // Copy the rules under the lock so a concurrent refresh cannot change the list while it is matched.
            List<ReviewedModuleTrustEntry> snapshot;
            lock (_sync)
            {
                snapshot = [.. _reviewedRules];
            }

            return snapshot.Any(entry => ModuleTrustIdentity.Matches(
                config,
                entry.Source,
                entry.Id,
                entry.Repo));
        }


        // Configuration

        /// <summary>
        /// Loads the shipped trust-source JSON from <c>Data/App</c> when present.
        /// </summary>
        private async Task<ModuleTrustSourceConfig?> LoadTrustSourceConfigAsync(CancellationToken ct)
        {
            var path = Path.Combine(GetRootDirectory(), "Data", "App", TrustSourceFileName);
            if (!File.Exists(path))
            {
                return null;
            }

            await using var stream = File.OpenRead(path);
            var config = await JsonSerializer.DeserializeAsync<ModuleTrustSourceConfig>(stream, _jsonOptions, ct);
            config?.Normalize();
            return config;
        }


        // Cache

        /// <summary>
        /// Returns the on-disk path for the verified community-reviewed module cache.
        /// </summary>
        private string GetReviewedCachePath() =>
            Path.Combine(GetRootDirectory(), "Data", "App", ReviewedCacheFileName);

        /// <summary>
        /// Restores the in-memory reviewed trust rules after signature verification.
        /// Returns false with no rules when the cache is missing, unsigned, unverifiable or unreadable.
        /// </summary>
        private bool TryLoadVerifiedCache(out List<ReviewedModuleTrustEntry> rules)
        {
            rules = [];

            // No cache file means nothing to restore yet.
            var path = GetReviewedCachePath();
            if (!File.Exists(path))
            {
                return false;
            }

            try
            {
                /*
                 * Deserialize the cache wrapper, normalize the payload, and re-verify the RSA
                 * signature before exposing any module entries to trust resolution.
                 */
                var json = File.ReadAllText(path);
                var cache = JsonSerializer.Deserialize<ReviewedModulesCacheDocument>(json, _jsonOptions);
                if (cache?.Payload == null)
                {
                    return false;
                }

                // The signature is stored beside the payload; fall back to the one embedded in the payload.
                cache.Payload.Normalize();
                var signature = string.IsNullOrWhiteSpace(cache.Signature)
                    ? cache.Payload.Signature
                    : cache.Signature;

                if (string.IsNullOrWhiteSpace(signature))
                {
                    return false;
                }

                // Without the configured public key a cached list cannot be trusted.
                var publicKey = _sourceConfig.PublicKeyBase64;
                if (string.IsNullOrWhiteSpace(publicKey))
                {
                    return false;
                }

                cache.Payload.Signature = signature;
                if (!TryVerifySignature(cache.Payload, publicKey))
                {
                    return false;
                }

                rules = cache.Payload.Modules.ToList();

                return true;
            }
            catch (Exception ex)
            {
                // A damaged cache is ignored; the next successful refresh replaces it.
                _logger.LogWarning(ex, "Failed to load the community-reviewed module cache.");
                return false;
            }
        }

        /// <summary>
        /// Persists a verified signed payload and its signature for offline startup.
        /// The cache is re-verified with the same public key whenever it is loaded.
        /// </summary>
        private async Task SaveVerifiedCacheAsync(SignedReviewedModulesPayload payload, string signature, CancellationToken ct)
        {
            var document = new ReviewedModulesCacheDocument
            {
                FetchedAt = DateTime.UtcNow.ToString("o"),
                Payload = payload,
                Signature = signature
            };

            var path = GetReviewedCachePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var stream = File.Create(path);
            await JsonSerializer.SerializeAsync(stream, document, _jsonOptions, ct);
        }


        // Signature verification

        /// <summary>
        /// Parses and normalizes a signed community-reviewed list JSON document.
        /// Only file version 1 documents that carry a signature are accepted; the signature itself is not checked here.
        /// </summary>
        private bool TryParseSignedPayload(string json, out SignedReviewedModulesPayload payload)
        {
            payload = null!;

            try
            {
                // Deserialize and normalize the document.
                var parsed = JsonSerializer.Deserialize<SignedReviewedModulesPayload>(json, _jsonOptions);
                if (parsed == null)
                {
                    return false;
                }

                parsed.Normalize();

                // Reject unknown schema versions and unsigned documents before any verification work.
                if (parsed.FileVersion != 1 || string.IsNullOrWhiteSpace(parsed.Signature))
                {
                    return false;
                }

                payload = parsed;
                return true;
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Community-reviewed module list payload is not valid JSON.");
                return false;
            }
        }

        /// <summary>
        /// Verifies an RSA PKCS#1 v1.5 + SHA-256 signature over canonical unsigned JSON.
        /// </summary>
        private bool TryVerifySignature(SignedReviewedModulesPayload payload, string publicKeyBase64)
        {
            try
            {
                /*
                 * Hash the compact JSON body (signature field excluded) and verify it against the
                 * configured subject public key and detached Base64 signature bytes.
                 */
                var publicKeyBytes = Convert.FromBase64String(publicKeyBase64);
                var signatureBytes = Convert.FromBase64String(payload.Signature);
                var canonicalBytes = Encoding.UTF8.GetBytes(
                    JsonSerializer.Serialize(payload.ToUnsignedBody(), _canonicalJsonOptions));

                using var rsa = RSA.Create();
                rsa.ImportSubjectPublicKeyInfo(publicKeyBytes, out _);
                return rsa.VerifyData(
                    canonicalBytes,
                    signatureBytes,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1);
            }
            catch (Exception ex)
            {
                // Malformed Base64 or key data counts as a failed verification, never as a trusted list.
                _logger.LogWarning(ex, "Community-reviewed module list signature verification failed.");
                return false;
            }
        }


        // Refresh policy

        /// <summary>
        /// Returns whether the configured refresh interval has elapsed since the last remote fetch.
        /// The interval is at least one hour, and the first refresh is always due.
        /// </summary>
        private bool ShouldRefreshRemoteList(ModuleTrustSourceConfig source)
        {
            if (_lastRemoteRefresh == DateTime.MinValue)
            {
                return true;
            }

            var interval = TimeSpan.FromHours(Math.Max(1, source.RefreshIntervalHours));
            return DateTime.UtcNow - _lastRemoteRefresh >= interval;
        }


        // Paths

        /// <summary>
        /// Returns the application root directory above the deployed app folder.
        /// </summary>
        private static string GetRootDirectory()
        {
            return AppRoot.Directory;
        }
    }
}
