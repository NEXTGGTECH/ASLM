// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Models
{
    /// <summary>
    /// A repository-backed selection shared by setup, installation and update configuration.
    /// </summary>
    /// <param name="Channels">Update streams the repository offers: <c>release</c>, <c>pre-release</c> and <c>branch</c>.</param>
    /// <param name="Mode">Effective update stream; falls back to the first available channel when the saved one is missing.</param>
    /// <param name="Branch">Effective branch name, used only when <paramref name="Mode"/> is <c>branch</c>.</param>
    /// <param name="Releases">Selectable release candidates of the effective stream; empty in branch mode.</param>
    /// <param name="Branches">Branch names of the repository; loaded only in branch mode.</param>
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
        /// <param name="module">Module whose update settings are adjusted in place.</param>
        /// <returns>True when the module update settings changed and must be saved.</returns>
        public bool ApplyTo(ModuleConfig module)
        {
            // Nothing to do when the module already uses the effective stream and branch.
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

            // Switch to the effective stream and drop any update found for the previous one.
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
}
