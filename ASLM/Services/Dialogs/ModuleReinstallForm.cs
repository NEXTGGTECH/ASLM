// Copyright NEXTGGTECH. Apache License 2.0.

using System.Text.Json;
using ASLM.Localization;
using ASLM.Models;

namespace ASLM.Services.Dialogs
{
    /// <summary>
    /// Edits a detached reinstall target through the shared dialog, without saving canceled choices.
    /// </summary>
    internal sealed class ModuleReinstallForm : IDisposable
    {
        // Fields

        /// <summary>
        /// Private copy of the module being reinstalled, so edits in the dialog never reach the installed manifest.
        /// </summary>
        private readonly ModuleConfig _module;

        /// <summary>
        /// Test seam that loads target choices for a draft module instead of querying the update manager.
        /// </summary>
        private readonly Func<ModuleConfig, CancellationToken, Task<IReadOnlyList<DialogChoice>>>? _load;

        /// <summary>
        /// Update manager that supplies the repository channel, release and branch catalog, or null when a test loader is used.
        /// </summary>
        private readonly UpdateManager? _updates;

        /// <summary>
        /// True while a loaded catalog is applied to the fields, so their change handlers do not start another load.
        /// </summary>
        private bool _synchronizing;

        /// <summary>
        /// Last explicit target per source mode, restored when the user switches back to that mode.
        /// </summary>
        private readonly Dictionary<string, string> _selections = new(StringComparer.Ordinal);

        /// <summary>
        /// Cancellation source of the option load in progress, or null when none is running.
        /// </summary>
        private CancellationTokenSource? _loading;

        /// <summary>
        /// Whether the form was disposed; late catalog responses are then ignored.
        /// </summary>
        private bool _disposed;


        // Dialog contents

        /// <summary>
        /// Selector for the source mode (release, pre-release or branch).
        /// </summary>
        public DialogField Channel { get; }

        /// <summary>
        /// Selector for the release or branch to install; its options depend on the selected channel.
        /// </summary>
        public DialogField Target { get; }

        /// <summary>
        /// Dialog definition that hosts both selectors and the cancel and reinstall actions.
        /// </summary>
        public DialogRequest Request { get; }


        // Initialization

        /// <summary>
        /// Uses the same release and branch catalog as the module update configuration page.
        /// </summary>
        /// <param name="module">Module to reinstall; the form edits a copy.</param>
        /// <param name="updates">Update manager that queries the repository for channels, releases and branches.</param>
        public ModuleReinstallForm(ModuleConfig module, UpdateManager updates) : this(module)
        {
            _updates = updates;
        }

        /// <summary>
        /// Initializes selectors from saved preferences and accepts a testable asynchronous option loader.
        /// </summary>
        /// <param name="module">Module to reinstall; the form edits a copy.</param>
        /// <param name="load">Returns the target choices for a draft module with the selected channel.</param>
        internal ModuleReinstallForm(ModuleConfig module,
            Func<ModuleConfig, CancellationToken, Task<IReadOnlyList<DialogChoice>>> load) : this(module)
        {
            _load = load;
        }

        /// <summary>
        /// Builds the shared fields and actions before attaching a repository option provider.
        /// </summary>
        private ModuleReinstallForm(ModuleConfig module)
        {
            // Work on a deep copy; SourcePath is not serialized, so it is carried over explicitly.
            _module = JsonSerializer.Deserialize<ModuleConfig>(JsonSerializer.Serialize(module))!;
            _module.SourcePath = module.SourcePath;
            _module.Normalize();

            // Both selectors start from the saved update preferences; the target stays disabled until choices load.
            Channel = new() { Id = "channel", Label = DialogText.Localized(LocalizationKeys.ModuleInfo_Channel),
                Kind = DialogFieldKind.Choice, Required = true, Value = _module.Update.Mode,
                Choices = ModuleDownloadOptions.InitialChannels.Select(mode => new DialogChoice(mode, mode)).ToArray() };
            Target = new() { Id = "target", Label = TargetLabel(), Kind = DialogFieldKind.Choice, Required = true,
                Value = _module.Update.Mode == "branch" ? _module.Update.Branch : _module.Update.SelectedReleaseTag ?? ModuleUpdateConfig.LatestReleaseTag };
            _selections[_module.Update.Mode] = Target.Value!.ToString()!;
            Target.UpdateChoices(TargetLabel(), BuildChoices([]), isEnabled: false);
            Channel.ValueChanged += OnChannelChanged;
            Target.ValueChanged += OnTargetChanged;

            // The reinstall action is destructive and stays disabled until the target choices are loaded without error.
            Request = new()
            {
                Title = DialogText.Localized(LocalizationKeys.Modules_ReinstallConfirmTitle, module.Name),
                Message = DialogText.Localized(LocalizationKeys.Modules_RemoveConfirmFormat, module.Name),
                Fields = [Channel, Target],
                Actions = [new() { Id = "cancel", Text = DialogText.Localized(LocalizationKeys.Common_Cancel), IsCancel = true },
                    new() { Id = "reinstall", Text = DialogText.Localized(LocalizationKeys.Modules_Reinstall),
                        Style = DialogActionStyle.Danger, IsEnabled = () => Target.IsEnabled && Target.PresentationError == null }]
            };
        }


        // Selector options

        /// <summary>
        /// Resolves the second selector's caption from the selected source mode.
        /// </summary>
        private DialogText TargetLabel() => DialogText.Localized(Channel.Value?.ToString() == "branch"
            ? LocalizationKeys.ModuleUpdate_RepositoryBranch : LocalizationKeys.ModuleUpdate_ReleaseVersion);

        /// <summary>
        /// Retains saved targets even when they are absent from the current remote catalog.
        /// </summary>
        /// <param name="remote">Choices loaded from the repository; empty while nothing is loaded yet.</param>
        /// <returns>Choices without duplicates: the latest release (release channels), the remote entries and the current target.</returns>
        private IReadOnlyList<DialogChoice> BuildChoices(IReadOnlyList<DialogChoice> remote)
        {
            // Release channels always offer "latest"; the current target is kept so it never disappears from the list.
            var selected = Target.Value?.ToString() ?? string.Empty;
            var choices = new List<DialogChoice>();
            if (Channel.Value?.ToString() != "branch")
                choices.Add(remote.FirstOrDefault(choice => choice.Id == "latest") ?? new("latest", "latest"));
            choices.AddRange(remote);
            if (!string.IsNullOrEmpty(selected)) choices.Add(new(selected, selected));
            return choices.DistinctBy(choice => choice.Id, StringComparer.Ordinal).ToArray();
        }


        // Event handlers

        /// <summary>
        /// Switches selectors in place and discards stale network responses after rapid channel changes.
        /// </summary>
        private async void OnChannelChanged(object? sender, EventArgs e)
        {
            // Changes made while applying a loaded catalog must not trigger another load.
            if (_synchronizing) return;

            // Restore the target remembered for this mode (or its default) and reload the choices.
            var mode = Channel.Value!.ToString()!;
            Target.Value = _selections.GetValueOrDefault(mode, mode == "branch" ? _module.Update.Branch : "latest");
            await LoadOptionsAsync();
        }

        /// <summary>
        /// Remembers the explicit selection separately for each source mode during this dialog session.
        /// </summary>
        private void OnTargetChanged(object? sender, EventArgs e) =>
            _selections[Channel.Value!.ToString()!] = Target.Value?.ToString() ?? string.Empty;


        // Option loading

        /// <summary>
        /// Loads only the active channel, keeping the dialog dismissible and destructive actions disabled.
        /// </summary>
        public async Task LoadOptionsAsync()
        {
            if (_disposed) return;

            // Supersede any load in flight and disable the target selector until the new choices arrive.
            _loading?.Cancel();
            using var cts = new CancellationTokenSource();
            _loading = cts;
            Target.UpdateChoices(TargetLabel(), BuildChoices([]), isEnabled: false);
            try
            {
                // Give each request its own snapshot so changing channels cannot mutate an in-flight query.
                var draft = JsonSerializer.Deserialize<ModuleConfig>(JsonSerializer.Serialize(_module))!;
                draft.Update.Mode = draft.Update.Channel = Channel.Value!.ToString()!;
                draft.Update.Normalize();

                // Query the repository (or the test loader) for the choices of the selected channel.
                IReadOnlyList<DialogChoice> choices;
                if (_updates == null) choices = await _load!(draft, cts.Token);
                else
                {
                    var options = await _updates.GetModuleDownloadOptionsAsync(draft, cts.Token, isManualRequest: true);
                    if (cts.IsCancellationRequested || _disposed) return;
                    _synchronizing = true;
                    try
                    {
                        // Keep the open form aligned with the same fallback used by setup and installation.
                        Channel.UpdateChoices(Channel.Label, options.Channels.Select(mode => new DialogChoice(mode, mode)).ToArray());
                        if (Channel.Value?.ToString() != options.Mode)
                        {
                            Channel.Value = options.Mode;
                            Target.Value = options.Mode == "branch" ? options.Branch : "latest";
                        }

                        choices = options.Mode == "branch"
                            ? options.Branches.Select(branch => new DialogChoice(branch, branch)).ToArray()
                            : new[] { new DialogChoice("latest", UpdateManager.FormatLatestRelease(options.Releases.FirstOrDefault())) }
                                .Concat(options.Releases.Select(release => new DialogChoice(release.ReleaseTag!, release.DisplayName))).ToArray();
                    }
                    finally { _synchronizing = false; }
                }

                // Publish the choices only if this load is still the current one.
                if (!cts.IsCancellationRequested && !_disposed)
                    Target.UpdateChoices(TargetLabel(), BuildChoices(choices));
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // A newer load or the disposal superseded this one, so its outcome is dropped.
            }
            catch (Exception ex)
            {
                // A failed lookup shows its error under the selector, which also keeps the reinstall action disabled.
                if (!cts.IsCancellationRequested && !_disposed)
                    Target.UpdateChoices(TargetLabel(), BuildChoices([]), error:
                        DialogText.Localized(LocalizationKeys.ModuleUpdate_LoadOptionsFailedFormat, ex.Message));
            }
            finally { if (ReferenceEquals(_loading, cts)) _loading = null; }
        }


        // Result and cleanup

        /// <summary>
        /// Returns confirmed preferences without touching the installed manifest or its settings.
        /// </summary>
        /// <param name="values">Dialog values keyed by field ID (<c>channel</c> and <c>target</c>).</param>
        /// <returns>Update preferences of the module copy with the chosen channel and target applied.</returns>
        public ModuleUpdateConfig BuildSelection(IReadOnlyDictionary<string, object?> values)
        {
            // The target is a branch name in branch mode and a release tag otherwise.
            var selection = JsonSerializer.Deserialize<ModuleUpdateConfig>(JsonSerializer.Serialize(_module.Update))!;
            selection.Mode = selection.Channel = values["channel"]!.ToString()!;
            if (selection.Mode == "branch") selection.Branch = values["target"]!.ToString()!;
            else selection.SelectedReleaseTag = values["target"]!.ToString()!;
            selection.UseDefaultChannel = false;
            selection.Normalize();
            return selection;
        }

        /// <summary>
        /// Cancels pending catalog lookups and detaches field events when the dialog is closed.
        /// </summary>
        public void Dispose()
        {
            _disposed = true;
            _loading?.Cancel();
            Channel.ValueChanged -= OnChannelChanged;
            Target.ValueChanged -= OnTargetChanged;
        }
    }
}
