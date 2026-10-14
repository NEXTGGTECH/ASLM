// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Models;

/// <summary>
/// A repository-backed selection shared by setup, installation and update configuration.
/// </summary>
public sealed record ModuleDownloadOptions(
    IReadOnlyList<string> Channels, string Mode, string Branch,
    IReadOnlyList<UpdateCandidate> Releases, IReadOnlyList<string> Branches)
{
    /// <summary>
    /// Shows local preferences before repository metadata narrows the available channels.
    /// </summary>
    internal static IReadOnlyList<string> InitialChannels { get; } =
        Array.AsReadOnly<string>(["release", "pre-release", "branch"]);

    /// <summary>
    /// Applies an unavailable-channel fallback without changing a valid pinned target.
    /// </summary>
    public bool ApplyTo(ModuleConfig module)
    {
        var changed = module.Update.Mode != Mode || (Mode == "branch" && module.Update.Branch != Branch);
        if (!changed)
        {
            return false;
        }

        // A target from a missing stream cannot be carried into a different stream.
        if (module.Update.Mode != Mode)
        {
            module.Update.SelectedReleaseTag = ModuleUpdateConfig.LatestReleaseTag;
        }
        module.Update.Mode = Mode;
        module.Update.Channel = Mode == "pre-release" ? "pre-release" : "release";
        if (Mode == "branch")
        {
            module.Update.Branch = Branch;
        }
        module.Update.PendingUpdate = null;
        module.HasDeclaredUpdateConfig = true;
        return true;
    }
}
