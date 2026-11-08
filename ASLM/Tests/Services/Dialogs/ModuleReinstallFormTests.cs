// Copyright NEXTGGTECH. Apache License 2.0.

using System.Text.Json;
using ASLM.Localization;
using ASLM.Models;
using ASLM.Services.Dialogs;

namespace ASLM.Tests.Services.Dialogs
{
    /// <summary>
    /// Verifies live reinstall selectors and confirmation without creating native UI or contacting GitHub.
    /// </summary>
    public sealed class ModuleReinstallFormTests
    {
        // Helpers

        /// <summary>
        /// Creates a module with explicit saved update preferences for an isolated dialog draft.
        /// </summary>
        private static ModuleConfig Module(string mode = "release", string? tag = null) => new()
        {
            Id = "aslm-chat", Name = "ASLM Chat", SourcePath = "module/ASLM_Module.json",
            Update = new() { Mode = mode, Channel = mode, SelectedReleaseTag = tag, Branch = "preview", UseDefaultChannel = false }
        };


        // Initial selection

        /// <summary>
        /// Verifies that the saved channel and target (the implicit latest marker for releases without a tag, the
        /// saved branch for branches) are selected before any option response, that the target selector is disabled
        /// until options load, that the reinstall action is styled as dangerous, and that cancelling leaves the
        /// module manifest unchanged.
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


        // Channel switching

        /// <summary>
        /// Verifies that switching the channel updates the target caption and choices in place, remembers a separate
        /// target per channel, and that the confirmed selection reflects the final choice without altering the
        /// original module.
        /// </summary>
        [Fact]
        public async Task Channel_switch_refreshes_caption_choices_and_confirmed_values_in_place()
        {
            // Arrange: a release module with saved tag v1.2 and a loader that offers branches or one release.
            var module = Module(tag: "v1.2");
            using var form = new ModuleReinstallForm(module, (draft, _) =>
                Task.FromResult<IReadOnlyList<DialogChoice>>(draft.Update.Mode == "branch"
                    ? [new("main", "main"), new("preview", "preview")]
                    : [new("v3", "Version 3"), new("v3", "Version 3")]));

            // Act and assert, initial load: latest marker, the remote release, and the saved tag (duplicates removed).
            await form.LoadOptionsAsync();
            form.Target.Choices.Select(choice => choice.Id).Should().Equal("latest", "v3", "v1.2");

            // Act and assert, branch channel: the caption and choices switch and an explicit branch is picked.
            form.Channel.Value = "branch";
            form.Target.Label.Resolve().Should().Be(L.Get(LocalizationKeys.ModuleUpdate_RepositoryBranch));
            form.Target.Choices.Select(choice => choice.Id).Should().Equal("main", "preview");
            form.Target.Value = "main";

            // Act and assert, back to release channels: each channel restores its own remembered target.
            form.Channel.Value = "pre-release";
            form.Target.Value.Should().Be("latest");
            form.Target.Label.Resolve().Should().Be(L.Get(LocalizationKeys.ModuleUpdate_ReleaseVersion));
            form.Channel.Value = "release";
            form.Target.Value.Should().Be("v1.2");
            form.Channel.Value = "branch";
            form.Target.Value.Should().Be("main");

            // Assert, confirmation: the selection carries branch "main" and the original module stays unchanged.
            using var session = new DialogSession(form.Request);
            await session.InvokeAsync(form.Request.Actions.Single(action => action.Id == "reinstall"));
            var selected = form.BuildSelection((await session.Completion).Values);
            selected.Mode.Should().Be("branch");
            selected.Branch.Should().Be("main");
            selected.UseDefaultChannel.Should().BeFalse();
            module.Update.Mode.Should().Be("release");
            module.Update.Branch.Should().Be("preview");
        }


        // Option loading

        /// <summary>
        /// Verifies that confirming is ignored while options are loading, that switching channels cancels the
        /// earlier lookup, that its late response is discarded instead of overwriting the current selector, and that
        /// confirming afterwards uses the choices of the current channel.
        /// </summary>
        [Fact]
        public async Task Loading_blocks_confirmation_and_discards_stale_channel_responses()
        {
            // Arrange: the release lookup stays pending until the test completes it; the branch lookup answers at once.
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

            // Act and assert, while loading: confirming does nothing and the dialog stays open.
            var load = form.LoadOptionsAsync();
            var accept = form.Request.Actions.Single(action => action.Id == "reinstall");
            await session.InvokeAsync(accept);
            session.Completion.IsCompleted.Should().BeFalse();

            // Act and assert, channel change: the pending release lookup is cancelled and its late result dropped.
            form.Channel.Value = "branch";
            oldToken.IsCancellationRequested.Should().BeTrue();
            first.SetResult([new("stale-release", "stale-release")]);
            await load;
            form.Target.Choices.Should().ContainSingle(choice => choice.Id == "preview");
            changed.Should().BeGreaterThan(0);

            // Assert, confirmation: with the branch options loaded, confirming completes with the branch target.
            await session.InvokeAsync(accept);
            (await session.Completion).Values["target"].Should().Be("preview");
        }

        /// <summary>
        /// Verifies that a failed option lookup shows its message on the target selector and blocks confirming, and
        /// that switching to a channel whose lookup succeeds clears the error and allows confirming that channel.
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
        /// Verifies that disposing the form cancels the running lookup and that a response arriving afterwards does
        /// not add its choices to the target selector.
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


        // Reinstall configuration

        /// <summary>
        /// Verifies that the configuration created for a reinstall keeps the manifest path and the confirmed source
        /// choice but clears installation status, settings, preserved paths, and installed release and commit
        /// markers, so the same version is offered again as an install candidate, while the original module stays
        /// unchanged.
        /// </summary>
        [Theory]
        [InlineData("release")]
        [InlineData("branch")]
        public void Clean_snapshot_allows_the_same_version_without_modifying_installed_data(string mode)
        {
            // Arrange: an installed and configured module with data that a reinstall must reset.
            var module = Module(mode, "v1");
            module.Version = "1";
            module.Status.Installed = module.Status.FirstRunCompleted = module.Status.Enabled = true;
            module.Update.InstalledReleaseTag = "v1";
            module.Update.InstalledCommitSha = "abc123";
            module.Update.Preserve = ["Data"];
            module.Settings.Add(new() { Key = "secret", Value = "old data" });
            var original = JsonSerializer.Serialize(module);

            // Act: create the reinstall configuration from the module's own update preferences.
            var clean = UpdateManager.CreateReinstallConfig(module, module.Update);

            // Assert, kept values: manifest path and confirmed source choice.
            clean.SourcePath.Should().Be(module.SourcePath);
            clean.Update.Mode.Should().Be(mode);
            clean.Update.SelectedReleaseTag.Should().Be("v1");

            // Assert, reset values: installation status, settings, preserved paths, and installed markers.
            clean.Status.Installed.Should().BeFalse();
            clean.Status.FirstRunCompleted.Should().BeFalse();
            clean.Status.Enabled.Should().BeFalse();
            clean.Settings.Should().BeEmpty();
            clean.Update.Preserve.Should().BeEmpty();
            clean.Update.InstalledReleaseTag.Should().BeNull();
            clean.Update.InstalledCommitSha.Should().BeNull();

            // Assert, reinstall rules: the same version is still an install candidate, and the original is unchanged.
            UpdateManager.ShouldOfferReleaseInstallCandidate(clean, "v1").Should().BeTrue();
            UpdateManager.IsModuleAlreadyAtInstallTarget(clean, new() { Mode = mode, ReleaseTag = "v1", CommitSha = "abc123" })
                .Should().BeFalse();
            JsonSerializer.Serialize(module).Should().Be(original);
        }
    }
}
