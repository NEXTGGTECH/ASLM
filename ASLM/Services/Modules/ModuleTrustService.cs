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
    /// </summary>
    public sealed class ModuleTrustService
    {
        private const string TrustSourceFileName = "ASLM_ModuleTrustSource.json";
        private const string ReviewedCacheFileName = "ASLM_ReviewedModules.cache.json";

        private static readonly OfficialModuleTrustEntry[] OfficialModules =
        [
            new("aslm-chat", "NGGTLightKeeper/ASLM-Chat"),
        ];

        private readonly HttpClient _httpClient;
        private readonly ILogger<ModuleTrustService> _logger;

        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true
        };

        private readonly JsonSerializerOptions _canonicalJsonOptions = new()
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private readonly object _sync = new();
        private ModuleTrustSourceConfig _sourceConfig = new();
        private List<ReviewedModuleTrustEntry> _reviewedModules = [];
        private DateTime _lastRemoteRefresh = DateTime.MinValue;


        // Initialization

        /// <summary>
        /// Creates the module trust service.
        /// </summary>
        public ModuleTrustService(ILogger<ModuleTrustService> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ASLM-ModuleTrust");
        }


        // Lifecycle

        /// <summary>
        /// Loads shipped configuration, restores the verified cache, and refreshes when configured.
        /// </summary>
        public async Task InitializeAsync(CancellationToken ct = default)
        {
            _sourceConfig = await LoadTrustSourceConfigAsync(ct) ?? new ModuleTrustSourceConfig();
            _sourceConfig.Normalize();

            if (!TryLoadVerifiedCache(out var cachedModules))
            {
                lock (_sync)
                {
                    _reviewedModules = [];
                }
            }
            else
            {
                lock (_sync)
                {
                    _reviewedModules = cachedModules;
                }
            }

            await RefreshReviewedListAsync(ct);
        }


        // Trust resolution

        /// <summary>
        /// Resolves the trust level for one installed module manifest.
        /// </summary>
        public ModuleTrustLevel Resolve(ModuleConfig config)
        {
            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            config.Normalize();

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
        /// </summary>
        public async Task RefreshReviewedListAsync(CancellationToken ct = default)
        {
            var source = _sourceConfig;
            if (string.IsNullOrWhiteSpace(source.ReviewedListUrl))
            {
                return;
            }

            if (!ShouldRefreshRemoteList(source))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(source.PublicKeyBase64))
            {
                _logger.LogWarning("Community-reviewed module list URL is configured but publicKeyBase64 is missing.");
                return;
            }

            try
            {
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

                var modules = payload.Modules
                    .Where(module => !string.IsNullOrWhiteSpace(module.Id) && !string.IsNullOrWhiteSpace(module.Repo))
                    .ToList();

                await SaveVerifiedCacheAsync(payload, payload.Signature, ct);

                lock (_sync)
                {
                    _reviewedModules = modules;
                    _lastRemoteRefresh = DateTime.UtcNow;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to refresh the community-reviewed module list.");
            }
        }


        // Matching

        /// <summary>
        /// Returns whether the manifest matches a built-in official module entry.
        /// </summary>
        private static bool TryMatchOfficial(ModuleConfig config) =>
            OfficialModules.Any(entry => ModuleTrustIdentity.Matches(config, entry.Id, entry.Repo));

        /// <summary>
        /// Returns whether the manifest matches a cached community-reviewed entry.
        /// </summary>
        private bool TryMatchReviewed(ModuleConfig config)
        {
            List<ReviewedModuleTrustEntry> snapshot;
            lock (_sync)
            {
                snapshot = _reviewedModules;
            }

            return snapshot.Any(entry => ModuleTrustIdentity.Matches(config, entry.Id, entry.Repo));
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
        /// Restores the in-memory reviewed list from disk after signature verification.
        /// </summary>
        private bool TryLoadVerifiedCache(out List<ReviewedModuleTrustEntry> modules)
        {
            modules = [];

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

                cache.Payload.Normalize();
                var signature = string.IsNullOrWhiteSpace(cache.Signature)
                    ? cache.Payload.Signature
                    : cache.Signature;

                if (string.IsNullOrWhiteSpace(signature))
                {
                    return false;
                }

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

                modules = cache.Payload.Modules
                    .Where(module => !string.IsNullOrWhiteSpace(module.Id) && !string.IsNullOrWhiteSpace(module.Repo))
                    .ToList();

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load the community-reviewed module cache.");
                return false;
            }
        }

        /// <summary>
        /// Persists a verified signed payload and its signature for offline startup.
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
        /// </summary>
        private bool TryParseSignedPayload(string json, out SignedReviewedModulesPayload payload)
        {
            payload = null!;

            try
            {
                var parsed = JsonSerializer.Deserialize<SignedReviewedModulesPayload>(json, _jsonOptions);
                if (parsed == null)
                {
                    return false;
                }

                parsed.Normalize();
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
                _logger.LogWarning(ex, "Community-reviewed module list signature verification failed.");
                return false;
            }
        }


        // Refresh policy

        /// <summary>
        /// Returns whether the configured refresh interval has elapsed since the last remote fetch.
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
