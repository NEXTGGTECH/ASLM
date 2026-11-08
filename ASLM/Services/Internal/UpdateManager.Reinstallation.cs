// Copyright NEXTGGTECH. Apache License 2.0.

using System.Text.Json;
using ASLM.Localization;
using ASLM.Models;

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Reinstallation part of the update manager: removes an installed module and installs a selected version again.
    /// </summary>
    public sealed partial class UpdateManager
    {
        // Reinstall request

        /// <summary>
        /// Queues a clean reinstall while reserving the module and its installation dependencies.
        /// </summary>
        public Task<ModuleLaunchResult> ReinstallModuleAsync(ModuleConfig selected, ModuleUpdateConfig selection,
            IProgress<string> log, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
        {
            if (ct.IsCancellationRequested) return Task.FromCanceled<ModuleLaunchResult>(ct);

            // Snapshot the accepted preferences before background preparation can yield to other UI work.
            var preferences = JsonSerializer.Deserialize<ModuleUpdateConfig>(JsonSerializer.Serialize(selection))!;

            // The prepare step creates the request; the queued work and the finish step consume it.
            ModuleInstaller.CatalogInstallation? request = null;
            _moduleInstaller.RecordManualRequest(selected.Id);
            return _downloadQueue.EnqueueAsync("module:" + selected.Id,
                token => ReinstallModuleCoreAsync(request!, log, progress, token), ct, DownloadQueueKind.Modules,
                prepare: async token =>
                {
                    // Plan under the shared gate so removals and other plans see a consistent reservation.
                    await _moduleInstaller.CatalogPlanGate.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        // Refuse while the module is being removed or another pending installation depends on it.
                        if (_moduleInstaller.GetRemovalState(selected.Id) != null ||
                            !string.IsNullOrEmpty(_moduleInstaller.GetPendingConsumers(selected.Id)))
                            throw new InvalidOperationException(L.Get(LocalizationKeys.Modules_RemoveBusy));

                        // Only a module that is registered as installed can be reinstalled.
                        var catalog = await _moduleInstaller.DiscoverModulesAsync().ConfigureAwait(false);
                        var original = catalog.SingleOrDefault(module => string.Equals(module.Id, selected.Id, StringComparison.OrdinalIgnoreCase))
                            ?? throw new InvalidOperationException(L.Get(LocalizationKeys.ModuleInfo_NotAvailable, selected.Name));
                        var registered = _moduleInstaller.Registry.ReadIds();
                        if (!registered.Contains(original.Id))
                            throw new InvalidOperationException(L.Get(LocalizationKeys.Notifications_ModuleNotInstalledMessage));

                        // Build the clean snapshot and expand its dependency order; every module that will be
                        // (re)downloaded needs a supported platform and a downloadable source.
                        var module = CreateReinstallConfig(original, preferences);
                        _moduleInstaller.ResolvePlatformSupport(module);
                        var order = ModuleDependencyResolver.ExpandInstallOrder([module], catalog);
                        foreach (var item in order)
                        {
                            ApplyCatalogDefaults(item, registered.Contains(item.Id));
                            if (!item.IsSupportedOnCurrentPlatform)
                                throw new InvalidOperationException(L.Get(LocalizationKeys.SetupWizard_ModuleUnsupported, PlatformInfo.PlatformKey));
                            if ((item == module || !registered.Contains(item.Id)) && !CanDownloadModule(item))
                                throw new InvalidOperationException(L.Get(LocalizationKeys.ModuleInfo_SourceUnavailable, item.Name));
                        }

                        // Reserve the plan; the reservation lives until the finish step disposes it.
                        token.ThrowIfCancellationRequested();
                        request = _moduleInstaller.BeginCatalogInstallation(module, order, reinstall: true);
                    }
                    finally { _moduleInstaller.CatalogPlanGate.Release(); }
                },
                finish: async canceled =>
                {
                    // Undo partial work after a cancellation, and always release the reservation.
                    if (request == null) return;
                    try
                    {
                        if (canceled) await _moduleInstaller.RollbackCatalogInstallationAsync(request, log).ConfigureAwait(false);
                    }
                    finally { request.Dispose(); }
                });
        }


        // Reinstall execution

        /// <summary>
        /// Resolves the selected version before removal, then installs it through the normal catalog pipeline.
        /// </summary>
        private async Task<ModuleLaunchResult> ReinstallModuleCoreAsync(ModuleInstaller.CatalogInstallation request,
            IProgress<string> log, IProgress<DownloadProgress>? progress, CancellationToken ct)
        {
            var module = request.Root;

            // Resolve the exact target first, so an unavailable source fails before anything is removed.
            // The snapshot has cleared installed-version baselines, allowing even the same release or commit.
            var candidate = await ResolveModuleInstallCandidateAsync(module, ct, isManualRequest: true).ConfigureAwait(false)
                ?? throw new InvalidOperationException(L.Get(LocalizationKeys.ModuleInfo_SourceUnavailable, module.Name));
            ct.ThrowIfCancellationRequested();

            // Remove the installed files through the dedicated removal lane and report any content that had to stay.
            var retained = await _downloadQueue.EnqueueAsync("module-remove:" + module.Id, async token =>
            {
                request.SetPhase(module.Id, DownloadOperationState.Removing);
                _moduleInstaller.SetRemovalState(module.Id, DownloadOperationState.Removing);
                return await _moduleInstaller.RemoveForReinstallAsync(module, token).ConfigureAwait(false);
            }, ct, DownloadQueueKind.ModuleRemoval, removal: true,
                finish: _ =>
                {
                    _moduleInstaller.SetRemovalState(module.Id, null);
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
            if (retained != null) log.Report(L.Get(LocalizationKeys.Modules_RemoveCleanupFormat, retained));

            // Install the resolved version through the shared catalog pipeline.
            // No content lease is held while waiting for the removal lane, so independent queues cannot deadlock.
            return await InstallCatalogModuleCoreAsync(request, log, progress, ct, candidate).ConfigureAwait(false);
        }


        // Reinstall configuration

        /// <summary>
        /// Creates a clean installation snapshot without altering the installed module before confirmation.
        /// </summary>
        internal static ModuleConfig CreateReinstallConfig(ModuleConfig original, ModuleUpdateConfig selection)
        {
            // Work on deep copies, so the installed module keeps its state until the reinstall actually runs.
            var module = JsonSerializer.Deserialize<ModuleConfig>(JsonSerializer.Serialize(original))!;
            module.SourcePath = original.SourcePath;
            module.Status = new();
            module.Update = JsonSerializer.Deserialize<ModuleUpdateConfig>(JsonSerializer.Serialize(selection))!;

            // Forget what is installed now, so the selected release or commit installs even when it is the same one.
            module.Update.InstalledReleaseTag = null;
            module.Update.InstalledCommitSha = null;
            module.Update.PendingUpdate = null;
            module.Update.UseDefaultChannel = false;

            // Reinstallation never carries old setting values or preserved directories into the fresh archive.
            module.Settings.Clear();
            module.Update.Preserve.Clear();
            module.HasDeclaredUpdateConfig = true;
            module.Normalize();
            return module;
        }
    }
}
