// Copyright NEXTGGTECH. Apache License 2.0.

using System.Text.Json;
using ASLM.Localization;
using ASLM.Models;
using ASLM.Services.Dialogs;

namespace ASLM.Tests.Services.Dialogs;

/// <summary>
/// Verifies live reinstall selectors and confirmation without creating native UI or contacting GitHub.
/// </summary>
public sealed class ModuleReinstallFormTests
{
    /// <summary>
    /// Creates a module with explicit saved update preferences for an isolated dialog draft.
    /// </summary>
    private static ModuleConfig Module(string mode = "release", string? tag = null) => new()
    {
        Id = "aslm-chat", Name = "ASLM Chat", SourcePath = "module/ASLM_Module.json",
        Update = new() { Mode = mode, Channel = mode, SelectedReleaseTag = tag, Branch = "preview", UseDefaultChannel = false }
    };

    /// <summary>
    /// Saved targets, including the implicit latest marker, are selected before any network response.
    /// </summary>
    [Theory]
    [InlineData("release", null, "latest")]
    [InlineData("release", "v1.2", "v1.2")]
    [InlineData("pre-release", "v2-beta", "v2-beta")]
    [InlineData("branch", null, "preview")]
    public async Task Saved_channel_and_target_are_selected_without_mutating_manifest(string mode, string? tag, string target)
    {
        var module = Module(mode, tag);
        var original = JsonSerializer.Serialize(module);
        using var form = new ModuleReinstallForm(module, (_, _) => Task.FromResult<IReadOnlyList<DialogChoice>>([]));
        form.Channel.Value.Should().Be(mode);
        form.Target.Value.Should().Be(target);
        form.Target.Choices.Should().Contain(choice => choice.Id == target);
        form.Target.IsEnabled.Should().BeFalse();
        form.Request.Actions.Single(action => action.Id == "reinstall").Style.Should().Be(DialogActionStyle.Danger);
        await form.LoadOptionsAsync();
        form.Target.IsEnabled.Should().BeTrue();
        using var session = new DialogSession(form.Request);
        await session.InvokeAsync(form.Request.Actions.Single(action => action.IsCancel));
        (await session.Completion).IsCancelled.Should().BeTrue();
        JsonSerializer.Serialize(module).Should().Be(original);
    }

    /// <summary>
    /// Switching source types updates the second caption and remembers separate target selections.
    /// </summary>
    [Fact]
    public async Task Channel_switch_refreshes_caption_choices_and_confirmed_values_in_place()
    {
        var module = Module(tag: "v1.2");
        using var form = new ModuleReinstallForm(module, (draft, _) =>
            Task.FromResult<IReadOnlyList<DialogChoice>>(draft.Update.Mode == "branch"
                ? [new("main", "main"), new("preview", "preview")]
                : [new("v3", "Version 3"), new("v3", "Version 3")]));
        await form.LoadOptionsAsync();
        form.Target.Choices.Select(choice => choice.Id).Should().Equal("latest", "v3", "v1.2");
        form.Channel.Value = "branch";
        form.Target.Label.Resolve().Should().Be(L.Get(LocalizationKeys.ModuleUpdate_RepositoryBranch));
        form.Target.Choices.Select(choice => choice.Id).Should().Equal("main", "preview");
        form.Target.Value = "main";
        form.Channel.Value = "pre-release";
        form.Target.Value.Should().Be("latest");
        form.Target.Label.Resolve().Should().Be(L.Get(LocalizationKeys.ModuleUpdate_ReleaseVersion));
        form.Channel.Value = "release";
        form.Target.Value.Should().Be("v1.2");
        form.Channel.Value = "branch";
        form.Target.Value.Should().Be("main");

        using var session = new DialogSession(form.Request);
        await session.InvokeAsync(form.Request.Actions.Single(action => action.Id == "reinstall"));
        var selected = form.BuildSelection((await session.Completion).Values);
        selected.Mode.Should().Be("branch");
        selected.Branch.Should().Be("main");
        selected.UseDefaultChannel.Should().BeFalse();
        module.Update.Mode.Should().Be("release");
        module.Update.Branch.Should().Be("preview");
    }

    /// <summary>
    /// A late response from an abandoned channel cannot overwrite the current selector or enable removal early.
    /// </summary>
    [Fact]
    public async Task Loading_blocks_confirmation_and_discards_stale_channel_responses()
    {
        var first = new TaskCompletionSource<IReadOnlyList<DialogChoice>>();
        var oldToken = CancellationToken.None;
        using var form = new ModuleReinstallForm(Module(), (draft, token) =>
        {
            if (draft.Update.Mode == "branch") return Task.FromResult<IReadOnlyList<DialogChoice>>([new("preview", "preview")]);
            oldToken = token;
            return first.Task;
        });
        using var session = new DialogSession(form.Request);
        var changed = 0;
        session.Changed += (_, _) => changed++;
        var load = form.LoadOptionsAsync();
        var accept = form.Request.Actions.Single(action => action.Id == "reinstall");
        await session.InvokeAsync(accept);
        session.Completion.IsCompleted.Should().BeFalse();
        form.Channel.Value = "branch";
        oldToken.IsCancellationRequested.Should().BeTrue();
        first.SetResult([new("stale-release", "stale-release")]);
        await load;
        form.Target.Choices.Should().ContainSingle(choice => choice.Id == "preview");
        changed.Should().BeGreaterThan(0);
        await session.InvokeAsync(accept);
        (await session.Completion).Values["target"].Should().Be("preview");
    }

    /// <summary>
    /// Catalog errors leave the installed module untouched and allow dismissal or a channel retry.
    /// </summary>
    [Fact]
    public async Task Catalog_failure_disables_confirmation_until_another_channel_loads_successfully()
    {
        using var form = new ModuleReinstallForm(Module(), (draft, _) => draft.Update.Mode == "release"
            ? Task.FromException<IReadOnlyList<DialogChoice>>(new IOException("offline"))
            : Task.FromResult<IReadOnlyList<DialogChoice>>([]));
        using var session = new DialogSession(form.Request);
        await form.LoadOptionsAsync();
        form.Target.PresentationError!.Resolve().Should().Contain("offline");
        var accept = form.Request.Actions.Single(action => action.Id == "reinstall");
        await session.InvokeAsync(accept);
        session.Completion.IsCompleted.Should().BeFalse();
        form.Channel.Value = "pre-release";
        form.Target.PresentationError.Should().BeNull();
        await session.InvokeAsync(accept);
        (await session.Completion).Values["channel"].Should().Be("pre-release");
    }

    /// <summary>
    /// Closing a dialog cancels its lookup and prevents any later response from refreshing discarded inputs.
    /// </summary>
    [Fact]
    public async Task Disposing_form_cancels_the_lookup_without_publishing_late_options()
    {
        var response = new TaskCompletionSource<IReadOnlyList<DialogChoice>>();
        var token = CancellationToken.None;
        using var form = new ModuleReinstallForm(Module(), (_, ct) => { token = ct; return response.Task; });
        var load = form.LoadOptionsAsync();
        form.Dispose();
        token.IsCancellationRequested.Should().BeTrue();
        response.SetResult([new("late", "late")]);
        await load;
        form.Target.Choices.Should().NotContain(choice => choice.Id == "late");
    }

    /// <summary>
    /// A reinstall resets installation baselines and user settings, but preserves the confirmed source choice.
    /// </summary>
    [Theory]
    [InlineData("release")]
    [InlineData("branch")]
    public void Clean_snapshot_allows_the_same_version_without_modifying_installed_data(string mode)
    {
        var module = Module(mode, "v1");
        module.Version = "1";
        module.Status.Installed = module.Status.FirstRunCompleted = module.Status.Enabled = true;
        module.Update.InstalledReleaseTag = "v1";
        module.Update.InstalledCommitSha = "abc123";
        module.Update.Preserve = ["Data"];
        module.Settings.Add(new() { Key = "secret", Value = "old data" });
        var original = JsonSerializer.Serialize(module);
        var clean = UpdateManager.CreateReinstallConfig(module, module.Update);
        clean.SourcePath.Should().Be(module.SourcePath);
        clean.Update.Mode.Should().Be(mode);
        clean.Update.SelectedReleaseTag.Should().Be("v1");
        clean.Status.Installed.Should().BeFalse();
        clean.Status.FirstRunCompleted.Should().BeFalse();
        clean.Status.Enabled.Should().BeFalse();
        clean.Settings.Should().BeEmpty();
        clean.Update.Preserve.Should().BeEmpty();
        clean.Update.InstalledReleaseTag.Should().BeNull();
        clean.Update.InstalledCommitSha.Should().BeNull();
        UpdateManager.ShouldOfferReleaseInstallCandidate(clean, "v1").Should().BeTrue();
        UpdateManager.IsModuleAlreadyAtInstallTarget(clean, new() { Mode = mode, ReleaseTag = "v1", CommitSha = "abc123" })
            .Should().BeFalse();
        JsonSerializer.Serialize(module).Should().Be(original);
    }
}
