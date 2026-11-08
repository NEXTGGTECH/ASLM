// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Tests.TestSupport;
using ASLM.Models;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace ASLM.Tests.Services.Modules
{
    /// <summary>
    /// Verifies the shared module launch and installation pipeline: module card commands, manual update checks and
    /// updates, reinstall, catalog installation and uninstall queues, launch after installation, and the engine
    /// installation that launch performs.
    /// </summary>
    [Collection("ModuleManifestDiscovery")]
    public sealed class ModuleLaunchEngineReconciliationTests
    {
        // Module card commands

        /// <summary>
        /// Verifies that the card's check command never dispatches the update action, even when the update badge is
        /// visible, and that the update command dispatches it only when an update is pending.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Card_check_and_update_commands_remain_separate(bool hasPendingUpdate)
        {
            // Arrange: a module card over a catalog module, optionally with a pending update (which shows the badge).
            using var layout = new AslmFileSystemLayout();
            ResetDirectory(layout.ModulesDir);
            var module = await WriteCatalogModuleAsync(layout, "card-actions");
            var engines = new EngineInstaller();
            using var runner = CreateRunner(engines);
            var installer = new ModuleInstaller(runner, null!, new ModuleEngineReconciler(engines));
            using var http = new HttpClient(new ReinstallCatalogHandler(missing: false));
            var manager = CreateReinstallManager(layout, installer, engines, runner, http, new DownloadQueue());
            module.Update.InstalledReleaseTag = "v0.1.0";
            module.Update.PendingUpdate = hasPendingUpdate ? ModulePendingUpdate.FromCandidate(module,
                new() { Mode = "release", ReleaseTag = "v1.0.0", RemoteVersion = "v1.0.0" }) : null;
            var checks = 0;
            var updates = 0;
            var card = new ASLM.Pages.ModuleViewModel(module, installer, runner, null!, manager,
                new ModuleTrustService(NullLogger<ModuleTrustService>.Instance),
                () => { }, _ => { }, _ => { }, _ => updates++, _ => checks++, _ => { });

            // Act and assert: test command dispatch itself without a MAUI window, archive download, or module mutation.
            card.HasUpdate.Should().Be(hasPendingUpdate);
            card.CheckUpdateCommand.Execute(null);
            checks.Should().Be(1);
            updates.Should().Be(0);
            card.UpdateCommand.Execute(null);
            updates.Should().Be(hasPendingUpdate ? 1 : 0);
            checks.Should().Be(1);
        }


        // Manual update checks and updates

        /// <summary>
        /// Verifies that a manual check uses the update preferences saved on disk rather than the stale in-memory
        /// card, only records the available candidate as the pending update, and starts no installation or Updating
        /// activity, also when it is repeated.
        /// </summary>
        [Fact]
        public async Task Manual_check_uses_saved_preferences_and_only_records_the_candidate()
        {
            // Arrange: an installed module saved with pre-release update preferences.
            using var layout = new AslmFileSystemLayout();
            ResetDirectory(layout.ModulesDir);
            var module = await WriteCatalogModuleAsync(layout, "manual-check");
            var engines = new EngineInstaller();
            using var runner = CreateRunner(engines);
            var installer = new ModuleInstaller(runner, null!, new ModuleEngineReconciler(engines));
            module.Status.Installed = module.Status.FirstRunCompleted = true;
            module.Update.Mode = module.Update.Channel = "pre-release";
            module.Update.InstalledReleaseTag = "v1.0.0";
            await installer.SaveConfigAsync(module);

            // Simulate a card created before the user changed the saved configuration.
            module.Update.Mode = module.Update.Channel = "release";

            // The fake catalog also asserts that no maintenance activity is shown while the metadata is requested.
            using var http = new HttpClient(new ReinstallCatalogHandler(missing: false, beforeResponse: _ =>
            {
                runner.ConsoleStore.GetActivity(module.SourcePath).Should().BeNull("checking is not updating");
                return Task.CompletedTask;
            }));
            var manager = CreateReinstallManager(layout, installer, engines, runner, http, new DownloadQueue());

            // Act: check for updates.
            var checkedModule = await manager.CheckModuleUpdatesAsync(module);

            // Assert: the saved pre-release preference decided the candidate, and only the candidate was recorded.
            var saved = (await installer.LoadModuleConfig(module.SourcePath))!;
            saved.Update.PendingUpdate.Should().NotBeNull();
            saved.Update.PendingUpdate!.ReleaseTag.Should().Be("v2.0.0-beta");
            checkedModule.Should().NotBeNull("the view needs the checked preferences without reopening itself");
            checkedModule!.Update.PendingUpdate!.ReleaseTag.Should().Be(saved.Update.PendingUpdate.ReleaseTag);
            saved.Update.Mode.Should().Be("pre-release");
            saved.Update.InstalledReleaseTag.Should().Be("v1.0.0");
            saved.Version.Should().Be(module.Version);
            manager.IsModuleUpdatePending(module.SourcePath).Should().BeFalse();
            runner.ConsoleStore.GetActivity(module.SourcePath).Should().BeNull();

            // A second check with an already saved candidate must not turn into archive installation.
            var rechecked = await manager.CheckModuleUpdatesAsync(saved);
            rechecked!.Update.PendingUpdate!.ReleaseTag.Should().Be("v2.0.0-beta");
            rechecked.Update.InstalledReleaseTag.Should().Be("v1.0.0");
            runner.ConsoleStore.GetActivity(module.SourcePath).Should().BeNull();
            manager.IsCheckingModuleUpdate(module.SourcePath).Should().BeFalse();
        }

        /// <summary>
        /// Verifies that rechecking after the update channel changes replaces or clears the pending candidate, always
        /// persists it, and never installs the available version or shows maintenance activity.
        /// </summary>
        [Fact]
        public async Task Checks_after_channel_changes_refresh_the_candidate_without_starting_maintenance()
        {
            // Arrange: an installed module on release v1.0.0, and a fake catalog that also offers a newer pre-release.
            using var layout = new AslmFileSystemLayout();
            ResetDirectory(layout.ModulesDir);
            var module = await WriteCatalogModuleAsync(layout, "changed-channel");
            var engines = new EngineInstaller();
            using var runner = CreateRunner(engines);
            var installer = new ModuleInstaller(runner, null!, new ModuleEngineReconciler(engines));
            module.Status.Installed = module.Status.FirstRunCompleted = true;
            module.Update.InstalledReleaseTag = "v1.0.0";
            await installer.SaveConfigAsync(module);
            using var http = new HttpClient(new ReinstallCatalogHandler(missing: false));
            var manager = CreateReinstallManager(layout, installer, engines, runner, http, new DownloadQueue());

            // Act and assert: switch the saved mode back and forth; the candidate follows the mode, and the result
            // returned to the caller matches what was persisted.
            foreach (var mode in new[] { "pre-release", "release", "pre-release" })
            {
                module.Update.Mode = module.Update.Channel = mode;
                manager.SaveModuleUpdatePreferences(module);
                var result = await manager.CheckModuleUpdatesAsync(module);
                result.Should().NotBeNull();
                result!.Update.Mode.Should().Be(mode);
                (result.Update.PendingUpdate?.ReleaseTag).Should().Be(mode == "pre-release" ? "v2.0.0-beta" : null);
                var saved = (await installer.LoadModuleConfig(module.SourcePath))!;
                (saved.Update.PendingUpdate?.ReleaseTag).Should().Be(result.Update.PendingUpdate?.ReleaseTag);
                saved.Update.InstalledReleaseTag.Should().Be("v1.0.0");
                runner.ConsoleStore.GetActivity(module.SourcePath).Should().BeNull();
                manager.IsModuleUpdatePending(module.SourcePath).Should().BeFalse();
            }
        }

        /// <summary>
        /// Verifies that a direct update works without a view, does not show the Updating activity during its
        /// metadata check, ignores a repeated request while running, releases its reservation when canceled, and then
        /// completes as up to date when run again.
        /// </summary>
        [Fact]
        public async Task Direct_update_needs_no_view_and_can_run_again_after_cancellation()
        {
            // Arrange: an installed module whose catalog request blocks until it is released, so the update stays
            // in its metadata check.
            using var layout = new AslmFileSystemLayout();
            ResetDirectory(layout.ModulesDir);
            var module = await WriteCatalogModuleAsync(layout, "direct-update");
            var engines = new EngineInstaller();
            using var runner = CreateRunner(engines);
            var installer = new ModuleInstaller(runner, null!, new ModuleEngineReconciler(engines));
            module.Status.Installed = module.Status.FirstRunCompleted = true;
            module.Update.InstalledReleaseTag = "v1.0.0";
            await installer.SaveConfigAsync(module);
            var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var requestCount = 0;
            using var http = new HttpClient(new ReinstallCatalogHandler(missing: false, beforeResponse: async ct =>
            {
                Interlocked.Increment(ref requestCount);
                requested.TrySetResult();
                await release.Task.WaitAsync(ct);
            }));
            var manager = CreateReinstallManager(layout, installer, engines, runner, http, new DownloadQueue());
            using var cts = new CancellationTokenSource();
            var phases = new System.Collections.Concurrent.ConcurrentQueue<bool>();
            manager.ModuleUpdateStateChanged += (_, _) => phases.Enqueue(manager.IsCheckingModuleUpdate(module.SourcePath));

            // Act: start the update and wait until it is inside the blocked metadata request.
            var updating = manager.UpdateModuleAsync(module, ct: cts.Token);
            try
            {
                await requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
                manager.IsModuleUpdatePending(module.SourcePath).Should().BeTrue();
                manager.IsCheckingModuleUpdate(module.SourcePath).Should().BeTrue("metadata checking has its own animation state");
                runner.ConsoleStore.GetActivity(module.SourcePath).Should().BeNull("metadata checks must not show Updating");

                // A second request while the first one runs returns at once without another catalog request.
                await manager.UpdateModuleAsync(module).WaitAsync(TimeSpan.FromSeconds(5));
                requestCount.Should().Be(1, "a repeated click must not start or enqueue another update");

                // Canceling ends the update and clears every reservation and state change it made.
                cts.Cancel();
                await updating.WaitAsync(TimeSpan.FromSeconds(5));
                manager.IsModuleUpdatePending(module.SourcePath).Should().BeFalse();
                manager.IsCheckingModuleUpdate(module.SourcePath).Should().BeFalse();
                phases.Should().Equal(true, false);
                runner.ConsoleStore.GetActivity(module.SourcePath).Should().BeNull();
            }
            finally
            {
                cts.Cancel();
                release.TrySetResult();
                await updating;
            }

            // The installed release already matches the server, so retry completes without any download or native UI.
            await manager.UpdateModuleAsync(module);
            requestCount.Should().Be(2);
            phases.Should().Equal(true, false, true, false);
            runner.ConsoleStore.GetMaintenanceSnapshot(module.SourcePath).Text.Should()
                .Contain(ASLM.Localization.L.Get(ASLM.Localization.LocalizationKeys.ModuleUpdate_Status_UpToDate));
            manager.IsModuleUpdatePending(module.SourcePath).Should().BeFalse();
            runner.ConsoleStore.GetActivity(module.SourcePath).Should().BeNull();
        }

        /// <summary>
        /// Verifies that a failing catalog request during a manual update is written to the module's maintenance
        /// console, leaves no pending state or activity behind, and that a later check can succeed.
        /// </summary>
        [Fact]
        public async Task Manual_update_request_failure_is_logged_and_allows_retry()
        {
            // Arrange: an installed module whose catalog request fails until the flag is cleared.
            using var layout = new AslmFileSystemLayout();
            ResetDirectory(layout.ModulesDir);
            var module = await WriteCatalogModuleAsync(layout, "failed-check");
            var engines = new EngineInstaller();
            using var runner = CreateRunner(engines);
            var installer = new ModuleInstaller(runner, null!, new ModuleEngineReconciler(engines));
            module.Status.Installed = module.Status.FirstRunCompleted = true;
            module.Update.InstalledReleaseTag = "v0.1.0";
            await installer.SaveConfigAsync(module);
            var fail = true;
            using var http = new HttpClient(new ReinstallCatalogHandler(missing: false, beforeResponse: _ =>
                fail ? throw new HttpRequestException("test-offline") : Task.CompletedTask));
            var manager = CreateReinstallManager(layout, installer, engines, runner, http, new DownloadQueue());

            // The failed update is logged in the console and leaves the module idle.
            await manager.UpdateModuleAsync(module);
            runner.ConsoleStore.GetMaintenanceSnapshot(module.SourcePath).Text.Should().Contain("test-offline");
            manager.IsModuleUpdatePending(module.SourcePath).Should().BeFalse();
            runner.ConsoleStore.GetActivity(module.SourcePath).Should().BeNull();

            // Once the catalog answers, a retry records the pending update.
            fail = false;
            await manager.CheckModuleUpdatesAsync(module);
            (await installer.LoadModuleConfig(module.SourcePath))!.Update.PendingUpdate.Should().NotBeNull();
        }


        // Reinstall

        /// <summary>
        /// Verifies that a reinstall whose release or branch metadata is missing throws before the installed module
        /// is stopped or deleted: manifest, user data, registry entry and queue states stay unchanged.
        /// </summary>
        [Theory]
        [InlineData("release")]
        [InlineData("pre-release")]
        [InlineData("branch")]
        public async Task Reinstall_with_an_unavailable_target_preserves_the_existing_installation(string mode)
        {
            // Arrange: an installed module with a user data file, and a fake catalog without releases or branches.
            using var layout = new AslmFileSystemLayout();
            ResetDirectory(layout.ModulesDir);
            var module = await WriteCatalogModuleAsync(layout, "aslm-chat");
            var engines = new EngineInstaller();
            using var runner = CreateRunner(engines);
            var installer = new ModuleInstaller(runner, null!, new ModuleEngineReconciler(engines));
            module.Status.Installed = module.Status.FirstRunCompleted = true;
            await installer.SaveConfigAsync(module);
            var manifest = await File.ReadAllTextAsync(module.SourcePath);
            var payload = Path.Combine(Path.GetDirectoryName(module.SourcePath)!, "user-data.txt");
            await File.WriteAllTextAsync(payload, "untouched");
            using var http = new HttpClient(new ReinstallCatalogHandler(missing: true));
            var manager = CreateReinstallManager(layout, installer, engines, runner, http, new DownloadQueue());

            // Act: the reinstall fails because its target cannot be resolved.
            await FluentActions.Awaiting(() => manager.ReinstallModuleAsync(module,
                new() { Mode = mode, SelectedReleaseTag = "v1.0.0", Branch = "preview" }, new RecordingProgress()))
                .Should().ThrowAsync<InvalidOperationException>();

            // Assert: nothing was removed or reserved.
            File.ReadAllText(module.SourcePath).Should().Be(manifest);
            File.ReadAllText(payload).Should().Be("untouched");
            installer.Registry.ReadIds().Should().Contain(module.Id);
            installer.GetInstallationState(module.Id).Should().BeNull();
            installer.GetRemovalState(module.Id).Should().BeNull();
        }

        /// <summary>
        /// Verifies that a reinstall reserves its module while queued, waits for its place in both the module and the
        /// removal queue, and enters the ordinary installation (with the old files and registry entry already gone)
        /// only after the removal, keeping the chosen update selection on the catalog entry.
        /// </summary>
        [Theory]
        [InlineData("release")]
        [InlineData("pre-release")]
        [InlineData("branch")]
        public async Task Reinstall_uses_both_queues_and_enters_installation_only_after_removal(string mode)
        {
            // Arrange: an installed module with a data file, and one blocking job in each of the module and removal
            // queues that holds the reinstall back until the test releases it.
            using var layout = new AslmFileSystemLayout();
            ResetDirectory(layout.ModulesDir);
            var module = await WriteCatalogModuleAsync(layout, "aslm-chat");
            var engines = new EngineInstaller();
            using var runner = CreateRunner(engines);
            var installer = new ModuleInstaller(runner, null!, new ModuleEngineReconciler(engines));
            module.Status.Installed = module.Status.FirstRunCompleted = true;
            module.Update.InstalledReleaseTag = "v1.0.0";
            module.Update.InstalledCommitSha = "abcdef123456";
            await installer.SaveConfigAsync(module);
            var payload = Path.Combine(Path.GetDirectoryName(module.SourcePath)!, "user-data.txt");
            await File.WriteAllTextAsync(payload, "old data");
            var queue = new DownloadQueue();
            using var http = new HttpClient(new ReinstallCatalogHandler(missing: false));
            var manager = CreateReinstallManager(layout, installer, engines, runner, http, queue);
            var releaseInstall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseRemoval = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var removeQueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var installedLane = queue.EnqueueAsync("preceding-install", async _ => { await releaseInstall.Task; return true; },
                kind: DownloadQueueKind.Modules);
            var removalLane = queue.EnqueueAsync("preceding-removal", async _ => { await releaseRemoval.Task; return true; },
                kind: DownloadQueueKind.ModuleRemoval);
            using var cts = new CancellationTokenSource();
            var reachedInstall = false;

            // Observe the installer: signal when the reinstall is queued, and when it reaches the installation phase
            // check that the removal already happened, then cancel to avoid any download.
            installer.ModulesChanged += (_, _) =>
            {
                var state = installer.GetInstallationState(module.Id);
                if (state == null) return;
                if (state.Phase == DownloadOperationState.Queued) published.TrySetResult();
                if (state.Phase != DownloadOperationState.Running) return;
                installer.Registry.ReadIds().Should().NotContain(module.Id);
                File.Exists(payload).Should().BeFalse();
                reachedInstall = true;

                // Stop before archive downloading: this test has no native notification dispatcher.
                cts.Cancel();
            };
            queue.StateChanged += (_, _) =>
            {
                if (queue.Contains("module-remove:" + module.Id)) removeQueued.TrySetResult();
            };

            // Act: request the reinstall, then release the blocking jobs one queue at a time.
            var reinstall = manager.ReinstallModuleAsync(module,
                new() { Mode = mode, SelectedReleaseTag = "v1.0.0", Branch = "preview" }, new RecordingProgress(), ct: cts.Token);
            try
            {
                // While queued, the module is reserved as a root installation and nothing has been deleted yet.
                await published.Task.WaitAsync(TimeSpan.FromSeconds(5));
                installer.GetInstallationState(module.Id)!.IsDependency.Should().BeFalse();
                File.Exists(payload).Should().BeTrue();

                // Once the module queue is free, the reinstall queues its removal in the separate removal queue, which
                // is still blocked, so the files stay.
                releaseInstall.SetResult();
                await removeQueued.Task.WaitAsync(TimeSpan.FromSeconds(5));
                File.Exists(payload).Should().BeTrue("reinstall must respect the separate removal queue");

                // Releasing the removal lane lets the removal run and the installation phase start (and be canceled).
                releaseRemoval.SetResult();
                await FluentActions.Awaiting(() => reinstall.WaitAsync(TimeSpan.FromSeconds(5)))
                    .Should().ThrowAsync<OperationCanceledException>();
                reachedInstall.Should().BeTrue();
                var retainedCatalog = (await installer.LoadModuleConfig(module.SourcePath))!;
                retainedCatalog.Update.Mode.Should().Be(mode);
                retainedCatalog.Update.SelectedReleaseTag.Should().Be("v1.0.0");
                installer.GetRemovalState(module.Id).Should().BeNull();
                installer.GetInstallationState(module.Id).Should().BeNull();
            }
            finally
            {
                cts.Cancel();
                releaseInstall.TrySetResult();
                releaseRemoval.TrySetResult();
                await Task.WhenAll(installedLane, removalLane);
                try { await reinstall; } catch (OperationCanceledException) { }
            }
        }


        // Update and reinstall test support

        /// <summary>
        /// Creates a manager with a fake HTTP catalog; these tests stop before any archive or native UI work.
        /// </summary>
        private static UpdateManager CreateReinstallManager(AslmFileSystemLayout layout, ModuleInstaller installer,
            EngineInstaller engines, ModuleRunner runner, HttpClient http, DownloadQueue queue)
        {
            var appData = new AppDataStore(NullLogger<AppDataStore>.Instance);
            var limits = new GitHubRateLimitStore(NullLogger<GitHubRateLimitStore>.Instance);
            var account = new GitHubAccountStore(appData, limits, NullLogger<GitHubAccountStore>.Instance);
            var client = new GitHubUpdateClient(limits, account, http,
                new ModuleIconCache(Path.Combine(layout.Root, "Data", "Downloads", "IconModules")));
            return new UpdateManager(appData, installer, engines, runner, null!, null!, null!, null!, client,
                null!, NullLogger<UpdateManager>.Instance, queue);
        }

        /// <summary>
        /// Supplies deterministic metadata and rejects archive requests or accidental real-network access.
        /// The fake repository <c>test/catalog</c> offers release v1.0.0, pre-release v2.0.0-beta and branch
        /// <c>preview</c>, or nothing at all when <c>missing</c> is set.
        /// </summary>
        /// <param name="missing">True to answer with empty release and branch lists.</param>
        /// <param name="beforeResponse">Optional callback awaited before each response, for blocking or failing a request.</param>
        private sealed class ReinstallCatalogHandler(bool missing, Func<CancellationToken, Task>? beforeResponse = null) : HttpMessageHandler
        {
            /// <summary>
            /// Returns a fixed release or branch catalog without performing network IO.
            /// </summary>
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                // Let the test block or fail the request first, then answer by path; any other path is a test error.
                if (beforeResponse != null) await beforeResponse(cancellationToken);
                var json = request.RequestUri!.AbsolutePath switch
                {
                    "/repos/test/catalog/releases" => missing ? "[]" :
                        """[{"tag_name":"v1.0.0","zipball_url":"https://example.test/module.zip"},{"tag_name":"v2.0.0-beta","prerelease":true,"zipball_url":"https://example.test/preview.zip"}]""",
                    "/repos/test/catalog/branches" => missing ? "[]" :
                        """[{"name":"preview","commit":{"sha":"abcdef123456"}}]""",
                    _ => throw new InvalidOperationException("Unexpected test request: " + request.RequestUri)
                };
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json) };
            }
        }


        // Catalog installation queues

        /// <summary>
        /// Verifies that a catalog module installation runs in the module queue without waiting for jobs of the
        /// resource (bridge download) queue, that repeated requests return the already queued job, and that a
        /// queued resource download still completes normally afterwards.
        /// </summary>
        [Fact]
        public async Task Module_installs_do_not_wait_for_the_bridge_download_queue()
        {
            // Arrange: the real manager and download installer over a queue whose resource and module lanes are
            // each blocked by a preceding job.
            using var layout = new AslmFileSystemLayout();
            ResetDirectory(layout.ModulesDir);
            var module = await WriteCatalogModuleAsync(layout, "queued-module");
            var engines = new EngineInstaller();
            var reconciler = new ModuleEngineReconciler(engines);
            using var runner = CreateRunner(engines);
            var installer = new ModuleInstaller(runner, null!, reconciler);
            var coordinator = new ModuleLaunchCoordinator(installer, runner,
                new ModuleStartThrottle(), NullLogger<ModuleLaunchCoordinator>.Instance);
            var queue = new DownloadQueue();
            var manager = new UpdateManager(new AppDataStore(NullLogger<AppDataStore>.Instance),
                installer, engines, runner, coordinator, null!, reconciler, null!, null!, null!,
                NullLogger<UpdateManager>.Instance, queue);
            var downloads = new DownloadInstaller(installer, null!, engines, null!, null!, null!,
                NullLogger<DownloadInstaller>.Instance, queue);

            // Already prepared, no commands or network needed to verify scheduling through the real entry points.
            module.Status.Installed = module.Status.FirstRunCompleted = true;
            await installer.SaveConfigAsync(module);

            // One preceding job per lane keeps both the resource lane and the module lane busy.
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseModules = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var preceding = queue.EnqueueAsync("preceding", async _ => { await release.Task; return true; });
            var precedingModule = queue.EnqueueAsync("preceding-module", async _ => { await releaseModules.Task; return true; },
                kind: DownloadQueueKind.Modules);
            try
            {
                // Act: queue a module installation and a resource download; both wait, and repeated requests return
                // the job that is already queued.
                var moduleJob = manager.InstallCatalogModuleAsync(module, new RecordingProgress());
                var item = new DownloadCatalogItem { ResourceKey = "queued-resource" };
                var resourceJob = downloads.InstallAsync(item, null);
                manager.IsCatalogInstallPending(module.Id).Should().BeTrue();
                downloads.IsPending(item, null).Should().BeTrue();
                resourceJob.IsCompleted.Should().BeFalse();
                manager.InstallCatalogModuleAsync(module, new RecordingProgress()).Should().BeSameAs(moduleJob);
                downloads.InstallAsync(item, null).Should().BeSameAs(resourceJob);

                // Freeing only the module lane completes the module job while the resource lane stays blocked.
                releaseModules.SetResult();
                (await moduleJob.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(ModuleLaunchStatus.NoRunCommands);
                preceding.IsCompleted.Should().BeFalse();
                resourceJob.IsCompleted.Should().BeFalse();

                // Freeing the resource lane lets the remaining jobs finish.
                release.SetResult();
                await Task.WhenAll(preceding, precedingModule, moduleJob, resourceJob).WaitAsync(TimeSpan.FromSeconds(5));
                (await moduleJob).Status.Should().Be(ModuleLaunchStatus.NoRunCommands);

                // No source, handled normally without blocking the queue.
                (await resourceJob).Success.Should().BeFalse();
                manager.IsCatalogInstallPending(module.Id).Should().BeFalse();
                downloads.IsPending(item, null).Should().BeFalse();
            }
            finally { release.TrySetResult(); releaseModules.TrySetResult(); }
        }

        /// <summary>
        /// Verifies that a catalog installation announces the root and all its not yet installed dependencies as
        /// queued before anything is downloaded (an already installed dependency is not announced), and that a
        /// cancellation clears every announcement and leaves the available and installed lists as before.
        /// </summary>
        [Fact]
        public async Task Catalog_install_announces_all_pending_dependencies_before_downloading_and_clears_them_on_cancel()
        {
            // Arrange: a root that depends on a chain of two pending modules ending in one installed module.
            using var layout = new AslmFileSystemLayout();
            ResetDirectory(layout.ModulesDir);
            var ready = await WriteCatalogModuleAsync(layout, "already-installed");
            var leaf = await WriteCatalogModuleAsync(layout, "pending-leaf", ready.Id);
            var dependency = await WriteCatalogModuleAsync(layout, "pending-dependency", leaf.Id);
            var root = await WriteCatalogModuleAsync(layout, "pending-root", dependency.Id);
            var pending = new[] { root, dependency, leaf };
            var engines = new EngineInstaller();
            var reconciler = new ModuleEngineReconciler(engines);
            using var runner = CreateRunner(engines);
            var installer = new ModuleInstaller(runner, null!, reconciler);
            var coordinator = new ModuleLaunchCoordinator(installer, runner,
                new ModuleStartThrottle(), NullLogger<ModuleLaunchCoordinator>.Instance);
            var manager = new UpdateManager(new AppDataStore(NullLogger<AppDataStore>.Instance),
                installer, engines, runner, coordinator, null!, reconciler, null!, null!, null!,
                NullLogger<UpdateManager>.Instance, new DownloadQueue());
            ready.Status.Installed = ready.Status.FirstRunCompleted = true;
            await installer.SaveConfigAsync(ready);
            using var cts = new CancellationTokenSource();
            var announced = false;

            // When the installation is first announced, check the state of every module, then cancel.
            installer.ModulesChanged += (_, _) =>
            {
                if (!installer.IsInstalling(root.Id)) return;
                announced = true;
                foreach (var module in pending)
                {
                    installer.IsInstalling(module.Id).Should().BeTrue();
                    installer.GetInstallationState(module.Id)!.Phase.Should().Be(DownloadOperationState.Queued);
                    installer.GetInstallationState(module.Id)!.IsDependency.Should().Be(module.Id != root.Id);
                }

                installer.IsInstalling(ready.Id).Should().BeFalse();
                installer.Registry.ReadIds().Should().Equal(ready.Id);

                // Cancel before candidate resolution: this test must never request a real repository.
                cts.Cancel();
            };

            // Act: the installation is canceled as soon as it has been announced.
            var install = () => manager.InstallCatalogModuleAsync(root, new RecordingProgress(), ct: cts.Token);
            await install.Should().ThrowAsync<OperationCanceledException>();

            // Assert: the announcement happened, and everything it reserved has been released again.
            announced.Should().BeTrue();
            foreach (var module in pending)
            {
                installer.IsInstalling(module.Id).Should().BeFalse();
                runner.ConsoleStore.GetMaintenanceSnapshot(module.SourcePath).Activity.Should().BeNull();
            }

            (await installer.DiscoverAvailableModulesAsync()).Select(module => module.Id).Should()
                .BeEquivalentTo(pending.Select(module => module.Id));
            (await installer.DiscoverInstalledModulesAsync(includeInstalling: true)).Select(module => module.Id)
                .Should().Equal(ready.Id);
        }

        /// <summary>
        /// Verifies that while a catalog installation is reserved, both modules stay listed as available and
        /// installing even if the root manifest temporarily disappears or the dependency finishes, and that after the
        /// reservation ends only the modules that were actually downloaded remain installed.
        /// </summary>
        /// <param name="complete">True when the root module is also downloaded; false when only the dependency is.</param>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Pending_catalog_cards_survive_refresh_and_only_downloaded_modules_remain_after_the_operation(bool complete)
        {
            // Arrange: a root module and its dependency, both only in the local catalog.
            using var layout = new AslmFileSystemLayout();
            ResetDirectory(layout.ModulesDir);
            var dependency = await WriteCatalogModuleAsync(layout, "pending-dependency");
            var root = await WriteCatalogModuleAsync(layout, "pending-root", dependency.Id);
            var engines = new EngineInstaller();
            using var runner = CreateRunner(engines);
            var installer = new ModuleInstaller(runner, null!, new ModuleEngineReconciler(engines));
            var ids = new[] { root.Id, dependency.Id };
            var changes = 0;
            installer.ModulesChanged += (_, _) => changes++;

            // While the installation is reserved, nothing is registered yet, but both cards are available and installing.
            using (installer.BeginCatalogInstallation(root, [dependency, root]))
            {
                changes.Should().Be(1);
                installer.Registry.ReadIds().Should().BeEmpty();
                (await installer.DiscoverAvailableModulesAsync()).Select(module => module.Id).Should().BeEquivalentTo(ids);
                (await installer.DiscoverInstalledModulesAsync()).Should().BeEmpty();
                (await installer.DiscoverInstalledModulesAsync(includeInstalling: true)).Select(module => module.Id)
                    .Should().BeEquivalentTo(ids);

                // The updater can temporarily move the original manifest out of the live module directory.
                var manifest = await File.ReadAllTextAsync(root.SourcePath);
                File.Delete(root.SourcePath);
                try
                {
                    (await installer.DiscoverInstalledModulesAsync(includeInstalling: true)).Select(module => module.Id)
                        .Should().BeEquivalentTo(ids);
                }
                finally { await File.WriteAllTextAsync(root.SourcePath, manifest); }

                // Downloading the dependency (and, when completed, the root) must not change what the cards show
                // until the reservation ends.
                dependency.Status.Installed = dependency.Status.FirstRunCompleted = true;
                await installer.SaveConfigAsync(dependency);
                if (complete)
                {
                    root.Status.Installed = root.Status.FirstRunCompleted = true;
                    await installer.SaveConfigAsync(root);
                }

                (await installer.DiscoverAvailableModulesAsync()).Select(module => module.Id).Should().BeEquivalentTo(ids);
                (await installer.DiscoverInstalledModulesAsync(includeInstalling: true)).Select(module => module.Id)
                    .Should().BeEquivalentTo(ids);
                installer.IsInstalling(root.Id).Should().BeTrue();
                installer.IsInstalling(dependency.Id).Should().BeTrue();
            }

            // After the reservation ends, the state reflects only what was actually downloaded.
            changes.Should().Be(complete ? 4 : 3);
            installer.IsInstalling(root.Id).Should().BeFalse();
            installer.IsInstalling(dependency.Id).Should().BeFalse();
            runner.ConsoleStore.GetMaintenanceSnapshot(root.SourcePath).Activity.Should().BeNull();
            runner.ConsoleStore.GetMaintenanceSnapshot(dependency.SourcePath).Activity.Should().BeNull();
            var installed = complete ? ids : new[] { dependency.Id };
            installer.Registry.ReadIds().Should().BeEquivalentTo(installed);
            (await installer.DiscoverInstalledModulesAsync(includeInstalling: true)).Select(module => module.Id)
                .Should().BeEquivalentTo(installed);
            (await installer.DiscoverAvailableModulesAsync()).Select(module => module.Id)
                .Should().BeEquivalentTo(complete ? Array.Empty<string>() : new[] { root.Id });
        }

        /// <summary>
        /// Verifies that when a removal of an installed module is already queued and a new consumer of that module
        /// is then installed, the consumer's plan treats the module as a pending dependency and enters its
        /// installation only after the removal has finished (registry entry and files already gone).
        /// </summary>
        [Fact]
        public async Task Removal_queued_before_a_new_dependency_finishes_before_that_dependency_is_reinstalled()
        {
            // Arrange: an installed dependency with a payload file, a not yet installed consumer of it, and a
            // blocker job that holds the removal queue.
            using var layout = new AslmFileSystemLayout();
            ResetDirectory(layout.ModulesDir);
            var dependency = await WriteCatalogModuleAsync(layout, "remove-then-reinstall");
            var root = await WriteCatalogModuleAsync(layout, "new-consumer", dependency.Id);
            var engines = new EngineInstaller();
            var reconciler = new ModuleEngineReconciler(engines);
            using var runner = CreateRunner(engines);
            var installer = new ModuleInstaller(runner, null!, reconciler);
            var coordinator = new ModuleLaunchCoordinator(installer, runner,
                new ModuleStartThrottle(), NullLogger<ModuleLaunchCoordinator>.Instance);
            var queue = new DownloadQueue();
            var manager = new UpdateManager(new AppDataStore(NullLogger<AppDataStore>.Instance),
                installer, engines, runner, coordinator, null!, reconciler, null!, null!, null!,
                NullLogger<UpdateManager>.Instance, queue);
            dependency.Status.Installed = dependency.Status.FirstRunCompleted = true;
            await installer.SaveConfigAsync(dependency);
            var payload = Path.Combine(Path.GetDirectoryName(dependency.SourcePath)!, "payload.txt");
            await File.WriteAllTextAsync(payload, "installed dependency");
            var releaseRemoval = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var prepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var blocker = queue.EnqueueAsync("removal-blocker", async _ => { await releaseRemoval.Task; return true; },
                kind: DownloadQueueKind.ModuleRemoval);
            using var cts = new CancellationTokenSource();
            var reinstallReached = false;

            // Signal when the consumer's installation is prepared; when the dependency reaches its installation
            // phase, check that the removal already completed, then cancel.
            installer.ModulesChanged += (_, _) =>
            {
                if (!installer.IsInstalling(root.Id)) return;
                prepared.TrySetResult();
                if (installer.GetInstallationState(dependency.Id)?.Phase != DownloadOperationState.Running) return;
                reinstallReached = true;
                installer.Registry.ReadIds().Should().NotContain(dependency.Id);
                File.Exists(payload).Should().BeFalse();

                // Verify entry into reinstallation, but never download from a real repository in this test.
                cts.Cancel();
            };
            Task? install = null;
            Task<string?>? removal = null;
            try
            {
                // Act: queue the removal first, then the installation of the consumer.
                removal = manager.UninstallModuleAsync(dependency);
                installer.GetRemovalState(dependency.Id).Should().Be(DownloadOperationState.Queued);
                install = manager.InstallCatalogModuleAsync(root, new RecordingProgress(), ct: cts.Token);

                // While the removal waits, the dependency is listed as a pending dependency of the consumer.
                await prepared.Task.WaitAsync(TimeSpan.FromSeconds(5));
                installer.GetInstallationState(dependency.Id)!.IsDependency.Should().BeTrue();
                (await installer.DiscoverAvailableModulesAsync()).Select(module => module.Id)
                    .Should().BeEquivalentTo([root.Id, dependency.Id]);
                (await installer.DiscoverInstalledModulesAsync(includeInstalling: true)).Select(module => module.Id)
                    .Should().BeEquivalentTo([root.Id, dependency.Id]);
                install.IsCompleted.Should().BeFalse();

                // Releasing the removal lets it finish first; the installation then starts and is canceled.
                releaseRemoval.SetResult();
                await removal.WaitAsync(TimeSpan.FromSeconds(5));
                await FluentActions.Awaiting(() => install.WaitAsync(TimeSpan.FromSeconds(5)))
                    .Should().ThrowAsync<OperationCanceledException>();
                reinstallReached.Should().BeTrue();
                installer.GetRemovalState(dependency.Id).Should().BeNull();
            }
            finally
            {
                cts.Cancel();
                releaseRemoval.TrySetResult();
                await blocker;
                if (removal != null) await removal;
                if (install != null) try { await install; } catch (OperationCanceledException) { }
            }
        }

        /// <summary>
        /// Verifies that once a catalog installation is reserved, a new uninstall request for one of its installed
        /// dependencies is rejected and the dependency stays registered.
        /// </summary>
        [Fact]
        public async Task Already_queued_install_reserves_dependencies_against_new_uninstall_requests()
        {
            using var layout = new AslmFileSystemLayout();
            ResetDirectory(layout.ModulesDir);
            var dependency = await WriteCatalogModuleAsync(layout, "reserved-dependency");
            var root = await WriteCatalogModuleAsync(layout, "queued-consumer", dependency.Id);
            var engines = new EngineInstaller();
            using var runner = CreateRunner(engines);
            var installer = new ModuleInstaller(runner, null!, new ModuleEngineReconciler(engines));
            dependency.Status.Installed = dependency.Status.FirstRunCompleted = true;
            await installer.SaveConfigAsync(dependency);
            using var request = installer.BeginCatalogInstallation(root, [dependency, root]);
            var manager = new UpdateManager(new AppDataStore(NullLogger<AppDataStore>.Instance), installer,
                engines, runner, null!, null!, null!, null!, null!, null!, NullLogger<UpdateManager>.Instance, new DownloadQueue());

            await FluentActions.Awaiting(() => manager.UninstallModuleAsync(dependency))
                .Should().ThrowAsync<InvalidOperationException>();
            installer.Registry.ReadIds().Should().Contain(dependency.Id);
        }

        /// <summary>
        /// Verifies that several uninstall requests run and complete in the removal queue while the resource and
        /// module queues are blocked and an unrelated content operation is active.
        /// </summary>
        [Fact]
        public async Task Multiple_uninstalls_queue_and_complete_while_the_other_two_queues_are_busy()
        {
            // Arrange: two installed modules to remove.
            using var layout = new AslmFileSystemLayout();
            ResetDirectory(layout.ModulesDir);
            var first = await WriteCatalogModuleAsync(layout, "remove-first");
            var second = await WriteCatalogModuleAsync(layout, "remove-second");
            var engines = new EngineInstaller();
            using var runner = CreateRunner(engines);
            var installer = new ModuleInstaller(runner, null!, new ModuleEngineReconciler(engines));
            var queue = new DownloadQueue();
            var manager = new UpdateManager(new AppDataStore(NullLogger<AppDataStore>.Instance), installer,
                engines, runner, null!, null!, null!, null!, null!, null!, NullLogger<UpdateManager>.Instance, queue);
            foreach (var module in new[] { first, second })
            {
                module.Status.Installed = module.Status.FirstRunCompleted = true;
                await installer.SaveConfigAsync(module);
            }

            // Block the resource and module queues, and keep an unrelated content operation open.
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var jobs = new[] { DownloadQueueKind.Resources, DownloadQueueKind.Modules }
                .Select(kind => queue.EnqueueAsync(kind.ToString(), async _ => { await release.Task; return true; }, kind: kind)).ToArray();
            using var content = ModuleInstaller.BeginContentOperation(new ModuleConfig { Id = "unrelated-install" });
            try
            {
                // Act and assert: both removals finish although the other queues are still blocked.
                var firstRemoval = manager.UninstallModuleAsync(first);
                var secondRemoval = manager.UninstallModuleAsync(second);
                await Task.WhenAll(firstRemoval, secondRemoval).WaitAsync(TimeSpan.FromSeconds(5));
                installer.Registry.ReadIds().Should().NotContain(first.Id).And.NotContain(second.Id);
                installer.GetRemovalState(first.Id).Should().BeNull();
                installer.GetRemovalState(second.Id).Should().BeNull();
                jobs.Should().OnlyContain(job => !job.IsCompleted);
            }
            finally { release.TrySetResult(); await Task.WhenAll(jobs); }
        }


        // Catalog module test support

        /// <summary>
        /// Writes a version 2 catalog module for the current platform (source repository <c>test/catalog</c>, version
        /// 1.0.0, not installed) with the given module dependencies into the modules directory.
        /// </summary>
        /// <param name="layout">Layout whose modules directory receives the manifest.</param>
        /// <param name="id">Module id, also used as the module folder name.</param>
        /// <param name="dependencies">Ids of modules that the new module depends on.</param>
        /// <returns>The written module configuration.</returns>
        private static async Task<ModuleConfig> WriteCatalogModuleAsync(AslmFileSystemLayout layout, string id, params string[] dependencies)
        {
            var module = new ModuleConfig
            {
                FileVersion = 2, Id = id, Name = id, Version = "1.0.0",
                Source = new() { Type = "github", Repo = "test/catalog" },
                SupportedPlatforms = [SupportedPlatform.FromKey(PlatformInfo.PlatformKey)],
                SourcePath = Path.Combine(layout.ModulesDir, id, ModuleManifestDiscovery.ManifestFileName)
            };
            module.Dependencies.Modules.AddRange(dependencies.Select(dependency => new ModuleModuleDependency { Id = dependency }));
            Directory.CreateDirectory(Path.GetDirectoryName(module.SourcePath)!);
            await File.WriteAllTextAsync(module.SourcePath, JsonSerializer.Serialize(module));
            return module;
        }


        // Launch after installation

        /// <summary>
        /// Verifies the launch that follows the final installation stage of a catalog module: a module with a run
        /// command is started exactly once (a repeated request reports it as already running), one without run
        /// commands reports none, an unstartable command ends in an error, and a canceled request throws; in every
        /// case the module stays installed and registered.
        /// </summary>
        /// <param name="scenario">Kind of run command: <c>run</c>, <c>empty</c> (none), <c>invalid</c> or <c>canceled</c>.</param>
        /// <param name="expected">Launch status expected from the installation request.</param>
        [Theory]
        [InlineData("run", ModuleLaunchStatus.Started)]
        [InlineData("empty", ModuleLaunchStatus.NoRunCommands)]
        [InlineData("invalid", ModuleLaunchStatus.Error)]
        [InlineData("canceled", ModuleLaunchStatus.Error)]
        public async Task Catalog_install_completion_launches_once_and_preserves_installation_on_launch_failure(
            string scenario, ModuleLaunchStatus expected)
        {
            // Arrange: a manifest written to disk, loaded back and marked as installed with its first run completed.
            using var layout = new AslmFileSystemLayout();
            ResetDirectory(layout.ModulesDir);
            var directory = Path.Combine(layout.ModulesDir, "catalog-launch");
            Directory.CreateDirectory(directory);
            var manifestPath = Path.Combine(directory, ModuleManifestDiscovery.ManifestFileName);
            await File.WriteAllTextAsync(manifestPath, $$"""
            {
              "fileVersion": 2,
              "id": "catalog-launch",
              "name": "Catalog launch",
              "version": "1.0.0",
              "supportedPlatforms": [
                { "os": "{{PlatformInfo.OsKey}}", "arch": "{{PlatformInfo.ArchKey}}" }
              ]
            }
            """);

            var engines = new EngineInstaller();
            var reconciler = new ModuleEngineReconciler(engines);
            using var runner = CreateRunner(engines);
            var installer = new ModuleInstaller(runner, null!, reconciler);
            var coordinator = new ModuleLaunchCoordinator(installer, runner,
                new ModuleStartThrottle(), NullLogger<ModuleLaunchCoordinator>.Instance);
            var manager = new UpdateManager(new AppDataStore(NullLogger<AppDataStore>.Instance),
                installer, engines, runner, coordinator, null!, reconciler, null!, null!, null!,
                NullLogger<UpdateManager>.Instance, new DownloadQueue());
            var module = (await installer.LoadModuleConfig(manifestPath))!;
            module.Status.Installed = true;
            module.Status.FirstRunCompleted = true;

            // The scenario decides the run command: none, an unstartable one, or a long-running process.
            if (scenario != "empty")
                module.Commands.Run.Add(new ModuleCommand
                {
                    Name = "Test process",
                    Exec = scenario == "invalid" ? "aslm-nonexistent-test-command"
                        : "cmd.exe /d /c ping -n 30 127.0.0.1"
                });

            // Exercise the final installation stage without network requests or changing real user modules.
            await installer.SaveConfigAsync(module);
            var log = new RecordingProgress();
            using var cts = new CancellationTokenSource();
            if (scenario == "canceled") cts.Cancel();

            // Act and assert: request the installation completion and check the launch outcome per scenario.
            try
            {
                if (scenario == "canceled")
                {
                    var install = () => manager.InstallCatalogModuleAsync(module, log, ct: cts.Token);
                    await install.Should().ThrowAsync<OperationCanceledException>();
                }
                else
                {
                    var result = await manager.InstallCatalogModuleAsync(module, log, ct: cts.Token);
                    result.Status.Should().Be(expected);
                    if (expected == ModuleLaunchStatus.Started)
                    {
                        // A started module is launched exactly once: a second request finds it running.
                        runner.GetRunningModuleSourcePaths().Should().Contain(manifestPath);
                        var repeated = await manager.InstallCatalogModuleAsync(module, log);
                        repeated.Status.Should().Be(ModuleLaunchStatus.AlreadyRunning);
                        log.Messages.Count(message => message.StartsWith("[Run]", StringComparison.Ordinal)).Should().Be(1);
                    }
                    else
                        runner.GetRunningModuleSourcePaths().Should().NotContain(manifestPath);
                }

                // Whatever happened to the launch, the installation itself is kept.
                installer.Registry.ReadIds().Should().Contain(module.Id);
                (await installer.LoadModuleConfig(manifestPath))!.Status.FirstRunCompleted.Should().BeTrue();
                if (scenario == "canceled")
                    runner.GetRunningModuleSourcePaths().Should().NotContain(manifestPath);
            }
            finally
            {
                await runner.StopModuleAsync(manifestPath);
            }
        }


        // Engine installation during launch

        /// <summary>
        /// Verifies that launch installs a missing required engine before completing module setup.
        /// </summary>
        [Fact]
        public async Task Launch_installs_missing_required_module_engine_before_first_run()
        {
            // Arrange: a module that embeds a required engine whose runtime files exist on disk but which is not yet
            // recorded as installed.
            using var layout = new AslmFileSystemLayout();
            ResetDirectory(layout.ModulesDir);

            var moduleDir = Path.Combine(layout.ModulesDir, "launch-provider");
            var manifestPath = Path.Combine(moduleDir, ModuleManifestDiscovery.ManifestFileName);
            var engineStateDir = Path.Combine(
                layout.Root,
                "Engines",
                "Modules",
                "launch-provider",
                "launch-vendor-runtime");
            ResetDirectory(engineStateDir);

            Directory.CreateDirectory(moduleDir);
            var runtimeDir = Path.Combine(engineStateDir, "runtime");
            Directory.CreateDirectory(runtimeDir);
            await File.WriteAllTextAsync(Path.Combine(runtimeDir, "vendor"), "runtime");
            await File.WriteAllTextAsync(manifestPath, BuildManifest());

            var engineInstaller = new EngineInstaller();
            var reconciler = new ModuleEngineReconciler(engineInstaller);
            using var runner = CreateRunner(engineInstaller);
            var moduleInstaller = new ModuleInstaller(runner, null!, reconciler);
            var coordinator = new ModuleLaunchCoordinator(
                moduleInstaller,
                runner,
                new ModuleStartThrottle(),
                NullLogger<ModuleLaunchCoordinator>.Instance);
            var log = new RecordingProgress();

            // Act: launch the module by its manifest path.
            var result = await coordinator.LaunchOrEnsureRunningBySourcePathAsync(
                manifestPath,
                log,
                CancellationToken.None);

            // Assert: the module is installed, set up and running.
            result.Status.Should().Be(ModuleLaunchStatus.Started);
            result.EffectiveConfig.Should().NotBeNull();
            result.EffectiveConfig!.Status.Installed.Should().BeTrue();
            result.EffectiveConfig.Status.FirstRunCompleted.Should().BeTrue();
            result.EffectiveConfig.Status.Enabled.Should().BeTrue();

            // The engine is now recorded as installed, and the installation was logged.
            engineInstaller.InvalidateCache();
            var engine = engineInstaller.FindAvailableEngine("launch-vendor-runtime");
            engine.Should().NotBeNull();
            engine!.Status.Installed.Should().BeTrue();
            engine.Status.InstalledManifestHash.Should().NotBeNullOrWhiteSpace();
            log.Messages.Should().Contain(message =>
                message.Contains("Installing required engine", StringComparison.OrdinalIgnoreCase));
        }


        // Test helpers

        /// <summary>
        /// Creates the minimal runner graph needed for setup and a no-op run command.
        /// </summary>
        private static ModuleRunner CreateRunner(EngineInstaller engineInstaller)
        {
            var appData = new AppDataStore(NullLogger<AppDataStore>.Instance);
            return new ModuleRunner(
                engineInstaller,
                new ModuleEnvironmentResolver(engineInstaller),
                new PortRegistry(appData),
                null!,
                new ModuleConsoleStore(),
                null!,
                null!,
                null!,
                null!,
                null!,
                null!,
                new ModuleInteropHostState(),
                new EmptyServiceProvider(),
                NullLogger<ModuleRunner>.Instance);
        }

        /// <summary>
        /// Builds a portable v2 module manifest with one required embedded engine.
        /// </summary>
        private static string BuildManifest()
        {
            return $$"""
            {
              "fileVersion": 2,
              "id": "launch-provider",
              "name": "Launch Provider",
              "version": "1.0.0",
              "supportedPlatforms": [
                { "os": "{{PlatformInfo.OsKey}}", "arch": "{{PlatformInfo.ArchKey}}" }
              ],
              "dependencies": {
                "engines": [ { "id": "launch-vendor-runtime" } ]
              },
              "commands": {
                "run": [ { "name": "No-op run", "exec": "cmd.exe /d /c ping -n 2 127.0.0.1" } ]
              },
              "engines": [
                {
                  "fileVersion": 2,
                  "id": "launch-vendor-runtime",
                  "name": "Launch Vendor Runtime",
                  "version": "1.0.0",
                  "supportedPlatforms": [
                    {
                      "os": "{{PlatformInfo.OsKey}}",
                      "arch": "{{PlatformInfo.ArchKey}}",
                      "key": "{{PlatformInfo.PlatformKey}}"
                    }
                  ],
                  "{{PlatformInfo.PlatformKey}}": {
                    "executablePath": "runtime/vendor",
                    "install": []
                  }
                }
              ]
            }
            """;
        }

        /// <summary>
        /// Recreates a test directory so persisted engine state cannot affect the launch result.
        /// </summary>
        private static void ResetDirectory(string path)
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }

            Directory.CreateDirectory(path);
        }


        // Test doubles

        /// <summary>
        /// Collects launch messages safely across progress callbacks and worker threads.
        /// </summary>
        private sealed class RecordingProgress : IProgress<string>
        {
            // State

            /// <summary>
            /// Guards <see cref="_messages"/> against concurrent reports and reads.
            /// </summary>
            private readonly object _lock = new();

            /// <summary>
            /// Every message reported so far, in arrival order.
            /// </summary>
            private readonly List<string> _messages = [];


            // Reporting

            /// <summary>
            /// Returns a stable snapshot of recorded launch messages.
            /// </summary>
            public IReadOnlyList<string> Messages
            {
                get
                {
                    lock (_lock)
                    {
                        return _messages.ToList();
                    }
                }
            }

            /// <summary>
            /// Records one launch message for later assertions.
            /// </summary>
            public void Report(string value)
            {
                lock (_lock)
                {
                    _messages.Add(value);
                }
            }
        }

        /// <summary>
        /// Supplies no optional services for a module without module dependencies.
        /// </summary>
        private sealed class EmptyServiceProvider : IServiceProvider
        {
            /// <summary>
            /// Returns no service because this fixture does not resolve optional dependencies.
            /// </summary>
            public object? GetService(Type serviceType) => null;
        }
    }
}
