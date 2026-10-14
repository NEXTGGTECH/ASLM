// Copyright NEXTGGTECH. Apache License 2.0.

using System.Collections.Concurrent;
using ASLM.Localization;
using ASLM.Models;
using Microsoft.Extensions.Logging;

namespace ASLM.Services.Internal;

public sealed partial class UpdateManager
{
    // Operation state

    private enum ManualModuleUpdatePhase { Checking, Applying }

    private readonly ConcurrentDictionary<string, ManualModuleUpdatePhase> _manualModuleUpdates =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Raised when a manual check starts, finishes, or proceeds to an explicitly requested update.
    /// </summary>
    internal event EventHandler? ModuleUpdateStateChanged;

    /// <summary>
    /// Reports manual checks and updates independently of the view that initiated them.
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
    /// </summary>
    private async Task<ModuleConfig?> RunModuleUpdateAsync(ModuleConfig module, bool apply,
        IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        // A card and an open ModuleInfo must not start the same operation twice.
        if (!_manualModuleUpdates.TryAdd(module.SourcePath, ManualModuleUpdatePhase.Checking))
        {
            return null;
        }
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
