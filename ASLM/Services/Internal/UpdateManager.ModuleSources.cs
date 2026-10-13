// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Models;

namespace ASLM.Services.Internal;

public sealed partial class UpdateManager
{
    // Repository option resolution

    /// <summary>
    /// Resolves real repository channels; failed requests never masquerade as empty catalogs.
    /// </summary>
    public async Task<ModuleDownloadOptions> GetModuleDownloadOptionsAsync(ModuleConfig module,
        CancellationToken ct = default, bool isManualRequest = false)
    {
        module.Normalize();
        if (!CanDownloadModule(module))
        {
            return new([], module.Update.Mode, module.Update.Branch, [], []);
        }

        // A selected branch does not depend on release metadata; load both catalogs concurrently.
        // Await both tasks together so a failure or cancellation never leaves an unobserved request.
        var releasesTask = _github.GetReleasesAsync(module.Source.Repo, true,
            ResolveRequestSource(isManualRequest), ct);
        var branchesTask = module.Update.Mode == "branch" ? GetModuleBranchesAsync(module, ct, isManualRequest) : null;
        if (branchesTask != null)
        {
            await Task.WhenAll(releasesTask, branchesTask);
        }
        var releases = await releasesTask;
        var all = releases.Select(release => (Release: release, Url: ResolveModuleDownloadUrl(module, release)))
            .Where(item => !string.IsNullOrWhiteSpace(item.Url)).ToList();
        var channels = new List<string>();
        if (all.Any(item => !item.Release.Prerelease))
        {
            channels.Add("release");
        }
        if (all.Any(item => item.Release.Prerelease))
        {
            channels.Add("pre-release");
        }
        channels.Add("branch");
        var mode = channels.Contains(module.Update.Mode) ? module.Update.Mode : channels[0];

        // Only fetch branch metadata when needed; keep an explicitly selected branch if it disappeared.
        var branch = module.Update.Branch;
        IReadOnlyList<string> branches = [];
        if (mode == "branch")
        {
            branches = (await (branchesTask ?? GetModuleBranchesAsync(module, ct, isManualRequest)))
                .Select(item => item.Name).ToArray();
            if (module.Update.Mode != "branch" && !branches.Contains(branch, StringComparer.OrdinalIgnoreCase))
            {
                branch = branches.FirstOrDefault(item => item == "main")
                    ?? branches.FirstOrDefault(item => item == "master") ?? branches.FirstOrDefault() ?? branch;
            }
        }

        var candidates = mode == "branch" ? [] : all
            .Where(item => mode == "pre-release" || !item.Release.Prerelease)
            .Select(item => BuildModuleReleaseCandidate(module, item.Release, item.Url)).ToList();
        // Preserve the effective source mode on every selectable release candidate.
        foreach (var candidate in candidates)
        {
            candidate.Mode = mode;
        }
        return new(channels, mode, branch, candidates, branches);
    }

    // Release presentation

    /// <summary>
    /// Formats the moving release alias without changing the stored literal latest selection.
    /// </summary>
    internal static string FormatLatestRelease(UpdateCandidate? latest)
    {
        if (string.IsNullOrWhiteSpace(latest?.ReleaseTag))
        {
            return ModuleUpdateConfig.LatestReleaseTag;
        }
        var date = latest.PublishedAt?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        return date == null ? $"latest ({latest.ReleaseTag})" : $"latest ({latest.ReleaseTag} - {date})";
    }
}
