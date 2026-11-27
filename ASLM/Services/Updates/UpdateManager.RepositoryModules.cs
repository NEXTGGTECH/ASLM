// Copyright NEXTGGTECH. Apache License 2.0.

using System.Net;
using System.Text;
using System.Text.Json;
using ASLM.Localization;
using ASLM.Models;

namespace ASLM.Services.Updates
{
    /// <summary>
    /// Previews repository modules without creating local catalog entries before installation.
    /// </summary>
    public sealed partial class UpdateManager
    {
        // Repository discovery

        /// <summary>
        /// Resolves an exact repository, validates its root manifest and detects local identity conflicts.
        /// </summary>
        internal async Task<ModuleConfig> FindRepositoryModuleAsync(string query, CancellationToken ct = default)
        {
            if (!GitHubRepository.TryParse(query, out var repo))
                throw new ArgumentException(L.Get(LocalizationKeys.ModulesDownloads_InvalidRepository));

            // Resolve the real repository identity before reading a manifest from its default branch.
            GitHubRepositoryInfo repository;
            try { repository = await _github.GetRepositoryInfoAsync(repo, ct); }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                throw new InvalidOperationException(L.Get(LocalizationKeys.ModulesDownloads_RepositoryUnavailable), ex);
            }

            string json;
            try { json = await _github.GetModuleManifestJsonAsync(repository.FullName, repository.DefaultBranch, ct); }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                throw new InvalidOperationException(L.Get(LocalizationKeys.ModulesDownloads_ManifestMissing), ex);
            }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or FormatException or DecoderFallbackException)
            {
                throw new InvalidDataException(L.Get(LocalizationKeys.ModulesDownloads_InvalidManifest, ex.Message), ex);
            }

            // Parse the common schema before deriving any filesystem paths or trusting source metadata.
            ModuleConfig module;
            try { module = ModuleManifestParser.Parse(json); }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or NotSupportedException)
            {
                throw new InvalidDataException(L.Get(LocalizationKeys.ModulesDownloads_InvalidManifest, ex.Message), ex);
            }

            ValidateRepositorySource(module, repository.FullName);
            module.SourcePath = GetRepositoryModuleManifestPath(module.Id);
            module.RepositoryPreview = new(repository.FullName, repository.DefaultBranch, json);
            module.Status = new();
            module.Update.InstalledCommitSha = null;
            module.Update.InstalledReleaseTag = null;
            module.Update.PendingUpdate = null;
            module.HasDeclaredUpdateConfig = true;

            // An undeclared branch follows GitHub's default, including repositories that do not use main.
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("update", out var update) ||
                update.ValueKind != JsonValueKind.Object || !update.TryGetProperty("branch", out var branch) ||
                branch.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(branch.GetString()))
                module.Update.Branch = repository.DefaultBranch;
            _moduleInstaller.ResolvePlatformSupport(module);
            ApplyCatalogDefaults(module, installed: false);

            // Existing modules remain authoritative; an identical id from another source is never substituted.
            var catalog = await _moduleInstaller.DiscoverModulesAsync().ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return ResolveRepositorySelection(module, catalog, snapshot: false);
        }


        // Installation identity

        /// <summary>
        /// Rechecks repository collisions and snapshots a new selection before its installation enters the queue.
        /// </summary>
        internal ModuleConfig ResolveRepositorySelection(ModuleConfig selected, IReadOnlyList<ModuleConfig> catalog,
            bool snapshot)
        {
            var matches = catalog.Where(item => string.Equals(item.Id, selected.Id,
                StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length > 1 || matches.Any(item =>
                    !string.Equals(item.Source.Type, "github", StringComparison.OrdinalIgnoreCase) ||
                    !GitHubRepository.Equals(item.Source.Repo, selected.Source.Repo)))
                throw new InvalidOperationException(L.Get(LocalizationKeys.ModulesDownloads_IdentityConflict, selected.Id));
            if (matches.Length == 1) return matches[0];

            var path = GetRepositoryModuleManifestPath(selected.Id);
            var directory = Path.GetDirectoryName(path)!;
            if (Directory.Exists(directory) || File.Exists(directory))
                throw new InvalidOperationException(L.Get(LocalizationKeys.ModulesDownloads_IdentityConflict, selected.Id));
            if (!snapshot) return selected;

            // Keep picker edits made after queueing from changing the reserved installation target.
            var result = ModuleManifestParser.Parse(JsonSerializer.Serialize(selected), path);
            result.RepositoryPreview = selected.RepositoryPreview;
            result.HasDeclaredUpdateConfig = true;
            _moduleInstaller.ResolvePlatformSupport(result);
            return result;
        }

        /// <summary>
        /// Validates or supplies the GitHub source without allowing a manifest to redirect installation elsewhere.
        /// </summary>
        internal static void ValidateRepositorySource(ModuleConfig module, string repository)
        {
            if (!string.Equals(module.Source.Type, "github", StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrWhiteSpace(module.Source.Repo) && !GitHubRepository.Equals(module.Source.Repo, repository))
                throw new InvalidDataException(L.Get(LocalizationKeys.ModulesDownloads_SourceMismatch));
            module.Source.Type = "github";
            module.Source.Repo = repository;
        }

        /// <summary>
        /// Maps a portable folder id to one manifest directly below Modules without creating the folder.
        /// </summary>
        internal static string GetRepositoryModuleManifestPath(string id)
        {
            var stem = id.Split('.')[0];
            var reserved = stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                    stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '1' and <= '9';
            if (string.IsNullOrWhiteSpace(id) || id != id.Trim() || id is "." or ".." || reserved ||
                id.EndsWith('.') || Encoding.UTF8.GetByteCount(id) > 255 ||
                id.Any(character => char.IsControl(character) || "<>:\"/\\|?*".Contains(character)))
                throw new InvalidDataException(L.Get(LocalizationKeys.ModulesDownloads_InvalidModuleId));
            return Path.Combine(GetRootDirectory(), "Modules", id, ModuleManifestDiscovery.ManifestFileName);
        }
    }
}
