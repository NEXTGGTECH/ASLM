// Copyright NEXTGGTECH. Apache License 2.0.

using System.Text.Json;
using ASLM.Localization;
using ASLM.Models;

namespace ASLM.Services.Dialogs;

/// <summary>
/// Edits a detached reinstall target through the shared dialog, without saving canceled choices.
/// </summary>
internal sealed class ModuleReinstallForm : IDisposable
{
    private readonly ModuleConfig _module;
    private readonly Func<ModuleConfig, CancellationToken, Task<IReadOnlyList<DialogChoice>>> _load;
    private readonly Dictionary<string, string> _selections = new(StringComparer.Ordinal);
    private CancellationTokenSource? _loading;
    private bool _disposed;
    public DialogField Channel { get; }
    public DialogField Target { get; }
    public DialogRequest Request { get; }

    /// <summary>
    /// Uses the same release and branch catalog as the module update configuration page.
    /// </summary>
    public ModuleReinstallForm(ModuleConfig module, UpdateManager updates)
        : this(module, async (draft, ct) => draft.Update.Mode == "branch"
            ? (await updates.GetModuleBranchesAsync(draft, ct, isManualRequest: true))
                .Select(branch => new DialogChoice(branch.Name, branch.Name)).ToArray()
            : (await updates.GetModuleReleaseCandidatesAsync(draft, ct, isManualRequest: true))
                .Where(release => !string.IsNullOrWhiteSpace(release.ReleaseTag))
                .Select(release => new DialogChoice(release.ReleaseTag!, release.DisplayName)).ToArray()) { }

    /// <summary>
    /// Initializes selectors from saved preferences and accepts a testable asynchronous option loader.
    /// </summary>
    internal ModuleReinstallForm(ModuleConfig module,
        Func<ModuleConfig, CancellationToken, Task<IReadOnlyList<DialogChoice>>> load)
    {
        _module = JsonSerializer.Deserialize<ModuleConfig>(JsonSerializer.Serialize(module))!;
        _module.SourcePath = module.SourcePath;
        _module.Normalize();
        _load = load;
        Channel = new() { Id = "channel", Label = DialogText.Localized(LocalizationKeys.ModuleInfo_Channel),
            Kind = DialogFieldKind.Choice, Required = true, Value = _module.Update.Mode,
            Choices = [new("release", "release"), new("pre-release", "pre-release"), new("branch", "branch")] };
        Target = new() { Id = "target", Label = TargetLabel(), Kind = DialogFieldKind.Choice, Required = true,
            Value = _module.Update.Mode == "branch" ? _module.Update.Branch : _module.Update.SelectedReleaseTag ?? ModuleUpdateConfig.LatestReleaseTag };
        _selections[_module.Update.Mode] = Target.Value!.ToString()!;
        Target.UpdateChoices(TargetLabel(), BuildChoices([]), isEnabled: false);
        Channel.ValueChanged += OnChannelChanged;
        Target.ValueChanged += OnTargetChanged;
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

    /// <summary>
    /// Resolves the second selector's caption from the selected source mode.
    /// </summary>
    private DialogText TargetLabel() => DialogText.Localized(Channel.Value?.ToString() == "branch"
        ? LocalizationKeys.ModuleUpdate_RepositoryBranch : LocalizationKeys.ModuleUpdate_ReleaseVersion);

    /// <summary>
    /// Retains saved targets even when they are absent from the current remote catalog.
    /// </summary>
    private IReadOnlyList<DialogChoice> BuildChoices(IReadOnlyList<DialogChoice> remote)
    {
        var selected = Target.Value?.ToString() ?? string.Empty;
        var choices = new List<DialogChoice>();
        if (Channel.Value?.ToString() != "branch") choices.Add(new("latest", "latest"));
        choices.AddRange(remote);
        if (!string.IsNullOrEmpty(selected)) choices.Add(new(selected, selected));
        return choices.DistinctBy(choice => choice.Id, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// Switches selectors in place and discards stale network responses after rapid channel changes.
    /// </summary>
    private async void OnChannelChanged(object? sender, EventArgs e)
    {
        var mode = Channel.Value!.ToString()!;
        Target.Value = _selections.GetValueOrDefault(mode, mode == "branch" ? _module.Update.Branch : "latest");
        await LoadOptionsAsync();
    }

    /// <summary>
    /// Remembers the explicit selection separately for each source mode during this dialog session.
    /// </summary>
    private void OnTargetChanged(object? sender, EventArgs e) =>
        _selections[Channel.Value!.ToString()!] = Target.Value?.ToString() ?? string.Empty;

    /// <summary>
    /// Loads only the active channel, keeping the dialog dismissible and destructive actions disabled.
    /// </summary>
    public async Task LoadOptionsAsync()
    {
        if (_disposed) return;
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
            var choices = await _load(draft, cts.Token);
            if (!cts.IsCancellationRequested && !_disposed)
                Target.UpdateChoices(TargetLabel(), BuildChoices(choices));
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested && !_disposed)
                Target.UpdateChoices(TargetLabel(), BuildChoices([]), error:
                    DialogText.Localized(LocalizationKeys.ModuleUpdate_LoadOptionsFailedFormat, ex.Message));
        }
        finally { if (ReferenceEquals(_loading, cts)) _loading = null; }
    }

    /// <summary>
    /// Returns confirmed preferences without touching the installed manifest or its settings.
    /// </summary>
    public ModuleUpdateConfig BuildSelection(IReadOnlyDictionary<string, object?> values)
    {
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
