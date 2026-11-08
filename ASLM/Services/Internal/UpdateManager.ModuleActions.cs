// Copyright NEXTGGTECH. Apache License 2.0.

using System.Collections.Concurrent;
using ASLM.Localization;
using ASLM.Models;
using Microsoft.Extensions.Logging;

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Manual module action part of the update manager: user-triggered update checks and updates of one module.
    /// </summary>
    public sealed partial class UpdateManager
    {
        // Operation state

        /// <summary>
        /// Stage of a user-triggered module action.
        /// </summary>
        private enum ManualModuleUpdatePhase
        {
            /// <summary>
            /// Only repository metadata is being checked; no files are replaced.
            /// </summary>
            Checking,

            /// <summary>
            /// An explicitly requested update is replacing module files.
            /// </summary>
            Applying
        }

        /// <summary>
        /// Manual module actions in flight by module manifest path; an entry also blocks a second concurrent action.
        /// </summary>
        private readonly ConcurrentDictionary<string, ManualModuleUpdatePhase> _manualModuleUpdates =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Raised when a manual check starts, finishes, or proceeds to an explicitly requested update.
        /// </summary>
        internal event EventHandler? ModuleUpdateStateChanged;

        /// <summary>
        /// Reports manual checks and updates independently of the view that initiated them.
        /// Returns true while any manual action, checking or applying, is running for the module manifest path.
        /// </summary>
        internal bool IsModuleUpdatePending(string sourcePath) => _manualModuleUpdates.ContainsKey(sourcePath);

        /// <summary>
        /// Identifies metadata checks without presenting them as installation or update activity.
        /// </summary>
        internal bool IsCheckingModuleUpdate(string sourcePath) =>
            _manualModuleUpdates.TryGetValue(sourcePath, out var phase) && phase == ManualModuleUpdatePhase.Checking;


        // Explicit user actions

        /// <summary>
        /// Refreshes the saved candidate without downloading anything, even when an update is already known.
        /// </summary>
        public Task<ModuleConfig?> CheckModuleUpdatesAsync(ModuleConfig module, CancellationToken ct = default) =>
            RunModuleUpdateAsync(module, apply: false, ct: ct);

        /// <summary>
        /// Revalidates the saved source and applies an available update without opening a view.
        /// </summary>
        public Task<ModuleConfig?> UpdateModuleAsync(ModuleConfig module,
            IProgress<DownloadProgress>? progress = null, CancellationToken ct = default) =>
            RunModuleUpdateAsync(module, apply: true, progress, ct);


        // Shared execution

        /// <summary>
        /// Runs one reserved module action and returns its checked preferences to the caller.
        /// Returns null when the action was rejected as a duplicate, failed, or was canceled.
        /// </summary>
        private async Task<ModuleConfig?> RunModuleUpdateAsync(ModuleConfig module, bool apply,
            IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
        {
            // A card and an open ModuleInfo must not start the same operation twice.
            if (!_manualModuleUpdates.TryAdd(module.SourcePath, ManualModuleUpdatePhase.Checking))
            {
                return null;
            }

            // Announce the check and open a fresh maintenance console for this action.
            IProgress<string>? log = null;
            try
            {
                RaiseModuleUpdateStateChanged();
                log = _moduleRunner.ConsoleStore.CreateMaintenanceLog(module, null, reset: true);

                // Checking metadata is not maintenance. ApplyModuleUpdateAsync owns the Updating activity
                // and starts it only after a real candidate has been found and accepted for installation.
                using var operation = await ModuleInstaller.BeginContentOperationAsync(ct, module).ConfigureAwait(false);

                // Cards can outlive configuration edits; always use the persisted channel and target.
                var selected = await _moduleInstaller.LoadModuleConfig(module.SourcePath).ConfigureAwait(false);
                if (selected == null || !string.Equals(selected.Id, module.Id, StringComparison.OrdinalIgnoreCase) ||
                    !_moduleInstaller.Registry.ReadIds().Contains(selected.Id))
                {
                    throw new InvalidOperationException(L.Get(LocalizationKeys.ModuleInfo_NotAvailable, module.Name));
                }

                // Check the repository and persist the result as the module's pending update (or clear it).
                ct.ThrowIfCancellationRequested();
                log.Report(L.Get(LocalizationKeys.ModuleUpdate_Log_CheckingFormat, selected.Name));
                var candidate = await CheckModuleUpdateAsync(selected, ct,
                    publishUpdateNotification: false, isManualRequest: true).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                selected.Update.PendingUpdate = candidate == null ? null : ModulePendingUpdate.FromCandidate(selected, candidate);
                SaveModuleUpdatePreferences(selected);
                if (candidate == null)
                {
                    log.Report(L.Get(LocalizationKeys.ModuleUpdate_Status_UpToDate));
                    return selected;
                }

                // A check only records the candidate. Applying it uses the existing shared maintenance pipeline.
                log.Report(L.Get(LocalizationKeys.ModuleUpdate_Status_UpdateAvailableFormat, candidate.RemoteVersion));
                if (!apply)
                {
                    return selected;
                }

                // Only an explicit update action can cross from metadata checking into file replacement.
                _manualModuleUpdates[module.SourcePath] = ManualModuleUpdatePhase.Applying;
                RaiseModuleUpdateStateChanged();
                var success = await ApplyModuleUpdateAsync(candidate, log, progress,
                    isManualRequest: true, ct: ct).ConfigureAwait(false);
                log.Report(L.Get(success ? LocalizationKeys.ModuleUpdate_Status_Updated : LocalizationKeys.ModuleUpdate_Status_UpdateFailed));
                return selected;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The caller canceled: record it in the console; the cancellation is not an error.
                log?.Report(L.Get(LocalizationKeys.ModuleInfo_Canceled));
            }
            catch (Exception ex)
            {
                // Background card actions have no dialog owner; keep failures in the same module console.
                _logger.LogError(ex, "Manual module update failed for {ModuleId}.", module.Id);
                log?.Report(L.Get(LocalizationKeys.ModuleInfo_Failed, ex.Message));
            }
            finally
            {
                // Always release the reservation so the module can be checked again, and tell the views.
                _manualModuleUpdates.TryRemove(module.SourcePath, out _);
                RaiseModuleUpdateStateChanged();
            }

            return null;
        }

        /// <summary>
        /// Notifies active views without letting a detached UI subscriber interrupt operation cleanup.
        /// </summary>
        private void RaiseModuleUpdateStateChanged()
        {
            // Subscribers dispatch their own UI work; one closing view must not suppress the remaining listeners.
            foreach (EventHandler handler in ModuleUpdateStateChanged?.GetInvocationList() ?? [])
            {
                try
                {
                    handler(this, EventArgs.Empty);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not notify a module update state subscriber.");
                }
            }
        }
    }
}
