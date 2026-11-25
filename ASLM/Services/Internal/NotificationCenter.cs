// Copyright NEXTGGTECH. Apache License 2.0.

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using ASLM.Localization;
using ASLM.Models;
using Microsoft.Maui.ApplicationModel;

namespace ASLM.Services.Internal
{
    // Event arguments

    /// <summary>
    /// Carries the current offer selected for immediate update.
    /// </summary>
    public sealed class UpdateNotificationActionEventArgs : EventArgs
    {
        /// <summary>
        /// Creates the event payload.
        /// </summary>
        /// <param name="notification">Notification that was acted on.</param>
        public UpdateNotificationActionEventArgs(AppNotification notification)
        {
            Notification = notification;
        }

        /// <summary>
        /// Gets the notification that was acted on.
        /// </summary>
        public AppNotification Notification { get; }
    }


    // Notification service

    /// <summary>
    /// Stores internal ASLM notifications, persists them, and exposes helpers for update and download events.
    /// All mutations of the notification list run on the UI thread; saves are debounced and written in the background.
    /// </summary>
    public sealed class NotificationCenter
    {
        // Fields

        /// <summary>
        /// Name of the notifications file inside <c>Data/App</c>.
        /// </summary>
        private const string NotificationsFileName = "ASLM_Notifications.json";

        /// <summary>
        /// Upper bound of notifications kept in memory and written to disk; the oldest ones are dropped first.
        /// </summary>
        private const int MaxPersistedNotifications = 200;

        /// <summary>
        /// First journal version that preserves a module update offer's deferred state.
        /// </summary>
        private const int PersistentDeferralVersion = 3;

        /// <summary>
        /// Backing collection of notifications, newest first; changed only on the UI thread.
        /// </summary>
        private readonly ObservableCollection<AppNotification> _notifications = [];

        /// <summary>
        /// Read-only view of <see cref="_notifications"/> that is exposed to the UI.
        /// </summary>
        private readonly ReadOnlyObservableCollection<AppNotification> _readOnlyNotifications;

        /// <summary>
        /// Absolute path of the persisted notifications file.
        /// </summary>
        private readonly string _filePath;

        /// <summary>
        /// Runs notification mutations on the UI thread.
        /// </summary>
        private readonly Dispatcher _dispatcher;

        /// <summary>
        /// JSON settings used to read and write the notifications file; enums are stored by name.
        /// </summary>
        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() }
        };

        /// <summary>
        /// Cancellation source of the pending debounced save; replaced on every new save request.
        /// </summary>
        private CancellationTokenSource? _saveCts;

        /// <summary>
        /// Whether persisted notifications were loaded; saves are skipped before that so the file is not overwritten early.
        /// </summary>
        private bool _isInitialized;

        /// <summary>
        /// Serializes writes so an older snapshot cannot replace a newer one.
        /// </summary>
        private readonly SemaphoreSlim _saveGate = new(1);

        /// <summary>
        /// Tracks live download attempts even when their progress rows are cleared from the journal.
        /// </summary>
        private readonly Dictionary<string, AppNotification> _activeDownloads = new(StringComparer.OrdinalIgnoreCase);


        // UI dispatcher

        /// <summary>
        /// Marshals notification mutations onto the MAUI UI thread; replaceable in tests.
        /// </summary>
        internal abstract class Dispatcher
        {
            /// <summary>
            /// Dispatcher that uses the MAUI main thread.
            /// </summary>
            public static Dispatcher Default { get; } = new MainThreadDispatcher();

            /// <summary>
            /// Gets whether the current thread is the UI thread.
            /// </summary>
            public abstract bool IsMainThread { get; }

            /// <summary>
            /// Schedules an action on the UI thread without waiting for it.
            /// </summary>
            public abstract void BeginInvoke(Action action);

            /// <summary>
            /// Runs an action on the UI thread; the task completes when the action has finished.
            /// </summary>
            public abstract Task InvokeAsync(Action action);

            /// <summary>
            /// Runs a function on the UI thread and returns its result through the task.
            /// </summary>
            public abstract Task<T> InvokeAsync<T>(Func<T> work);

            /// <summary>
            /// Production dispatcher that forwards to the MAUI main thread.
            /// </summary>
            private sealed class MainThreadDispatcher : Dispatcher
            {
                /// <summary>
                /// Gets whether the current thread is the MAUI main thread.
                /// </summary>
                public override bool IsMainThread => MainThread.IsMainThread;

                /// <summary>
                /// Schedules an action on the MAUI main thread without waiting for it.
                /// </summary>
                public override void BeginInvoke(Action action) => MainThread.BeginInvokeOnMainThread(action);

                /// <summary>
                /// Runs an action on the MAUI main thread and completes when it has finished.
                /// </summary>
                public override Task InvokeAsync(Action action) => MainThread.InvokeOnMainThreadAsync(action);

                /// <summary>
                /// Runs a function on the MAUI main thread and returns its result through the task.
                /// </summary>
                public override Task<T> InvokeAsync<T>(Func<T> work) => MainThread.InvokeOnMainThreadAsync(work);
            }
        }


        // Initialization

        /// <summary>
        /// Creates the notification service and resolves its persistence file path.
        /// </summary>
        public NotificationCenter()
            : this(Dispatcher.Default)
        {
        }

        /// <summary>
        /// Creates the notification service with an explicit UI dispatcher (test isolation).
        /// </summary>
        /// <param name="dispatcher">Dispatcher that runs notification mutations on the UI thread.</param>
        internal NotificationCenter(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher;
            _readOnlyNotifications = new ReadOnlyObservableCollection<AppNotification>(_notifications);
            _filePath = Path.Combine(GetRootDirectory(), "Data", "App", NotificationsFileName);
        }

        /// <summary>
        /// Restores the journal before producers or views begin using it.
        /// </summary>
        public async Task InitializeAsync(string? installedReleaseTag = null, string? pendingReleaseTag = null)
        {
            if (_isInitialized) return;

            var changed = await LoadAsync(installedReleaseTag, pendingReleaseTag);
            _isInitialized = true;
            if (changed) QueueSave(0);
        }


        // Events and journal

        /// <summary>
        /// Raised when the journal contents or a notification's presentation change.
        /// </summary>
        public event EventHandler? NotificationsChanged;

        /// <summary>
        /// Raised when a new notification should be shown as a toast.
        /// </summary>
        public event EventHandler<AppNotification>? NotificationPublished;

        /// <summary>
        /// Raised when a withdrawn, replaced, or deferred notification needs to leave the toast stack.
        /// </summary>
        public event EventHandler<AppNotification>? NotificationWithdrawn;

        /// <summary>
        /// Raised when the user chooses to apply the current update offer.
        /// </summary>
        public event EventHandler<UpdateNotificationActionEventArgs>? UpdateNotificationActionRequested;

        /// <summary>
        /// Gets the live notification collection sorted by newest update first.
        /// </summary>
        public ReadOnlyObservableCollection<AppNotification> Notifications => _readOnlyNotifications;


        // Update offers

        /// <summary>
        /// Publishes the latest offer unless the check was superseded or that version is already being applied.
        /// </summary>
        internal void PublishUpdateOffer(string sourceKind, string sourceId, string versionKey, string componentName,
            string targetVersion, string? currentVersion = null, Func<bool>? isCurrent = null)
        {
            RunOnMainThread(() =>
            {
                if (isCurrent?.Invoke() == false) return;

                var source = BuildOperationKey(sourceKind, sourceId);
                var id = $"update:{source}:{versionKey}".ToLowerInvariant();
                if (_activeDownloads.Values.Any(item => MatchesSource(item, source) &&
                        string.Equals(item.VersionKey, versionKey, StringComparison.OrdinalIgnoreCase)) ||
                    _notifications.Any(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }

                var notification = new AppNotification(id, AppNotificationCategory.Updates,
                    AppNotificationSeverity.Info, componentName, string.Empty, sourceKind, sourceId)
                {
                    EventType = "update-available",
                    VersionKey = versionKey,
                    TargetVersion = targetVersion,
                    CurrentVersion = currentVersion
                };
                notification.SetUpdateAvailabilityPresentation(offersUpdateActions: true, suppressToastAutoDismiss: true);
                Publish(notification);
            });
        }

        /// <summary>
        /// Withdraws offers for an entire source kind or for one source, preserving operation results.
        /// </summary>
        internal void WithdrawUpdateOffers(string sourceKind, string? sourceId = null, Func<bool>? isCurrent = null)
        {
            RunOnMainThread(() =>
            {
                if (isCurrent?.Invoke() == false) return;

                if (Withdraw(item => item.Category == AppNotificationCategory.Updates &&
                        string.Equals(item.SourceKind, sourceKind, StringComparison.OrdinalIgnoreCase) &&
                        (sourceId == null || string.Equals(item.SourceId, sourceId, StringComparison.OrdinalIgnoreCase))))
                {
                    SaveChanges(0);
                }
            });
        }

        /// <summary>
        /// Removes a stale offer identified by the instance that initiated the update check.
        /// </summary>
        internal void WithdrawUpdateOffer(AppNotification notification) => RunOnMainThread(() =>
        {
            if (Withdraw(item => ReferenceEquals(item, notification) &&
                    item.Category == AppNotificationCategory.Updates))
            {
                SaveChanges(0);
            }
        });

        /// <summary>
        /// Applies the requested action to the current offer, persisting any deferral.
        /// </summary>
        public void RequestUpdateNotificationAction(AppNotification notification, bool updateNow)
        {
            RunOnMainThread(() =>
            {
                if (!notification.OffersUpdateActions || !_notifications.Contains(notification)) return;

                if (updateNow)
                {
                    UpdateNotificationActionRequested?.Invoke(this, new UpdateNotificationActionEventArgs(notification));
                }
                else if (notification.CanDeferUpdate)
                {
                    notification.SetUpdateAvailabilityPresentation(
                        offersUpdateActions: false, suppressToastAutoDismiss: false);
                    NotificationWithdrawn?.Invoke(this, notification);
                    SaveChanges(0);
                }
            });
        }


        // System toasts

        /// <summary>
        /// Publishes a short-lived system toast with a stable event identity and optional component identity.
        /// </summary>
        public void PublishSystemToast(string title, string message, string statusText, string eventType,
            string sourceId = "", AppNotificationSeverity severity = AppNotificationSeverity.Success) =>
            PublishMessage("system", sourceId, eventType, severity, title, message, statusText, persist: false);

        /// <summary>
        /// Publishes a source-owned message through the shared journal and toast replacement rules.
        /// </summary>
        internal void PublishMessage(string sourceKind, string sourceId, string eventType, AppNotificationSeverity severity,
            string title, string message, string statusText, bool persist)
        {
            RunOnMainThread(() =>
            {
                var id = $"message:{BuildOperationKey(sourceKind, sourceId)}:{eventType}";
                var notification = new AppNotification(id, AppNotificationCategory.System, severity,
                    title, message, sourceKind, sourceId) { EventType = eventType };
                notification.UpdateContent(severity, title, message, statusText, string.Empty);
                Publish(notification, persist);
            });
        }


        // Download notifications

        /// <summary>
        /// Opens a download attempt and returns the identity used for its later reports.
        /// </summary>
        public Task<string> StartDownloadAsync(string operationType, string title, string message,
            string sourceKind, string sourceId, string? versionKey = null, bool replacesUpdateOffer = false,
            string? targetVersion = null)
        {
            return _dispatcher.InvokeAsync(() =>
            {
                var id = $"download:{Guid.NewGuid():N}";
                var notification = new AppNotification(id, AppNotificationCategory.Downloads,
                    AppNotificationSeverity.Info, title, message, sourceKind, sourceId)
                {
                    EventType = operationType,
                    VersionKey = versionKey,
                    TargetVersion = targetVersion
                };
                notification.UpdateContent(notification.Severity, title, message,
                    L.Get(LocalizationKeys.Notifications_StartingDownload), "0 B");
                notification.UpdateProgress(hasProgress: true, progressFraction: 0, isInProgress: true);
                _activeDownloads.Add(id, notification);

                Publish(notification, replacesUpdateOffer: replacesUpdateOffer);
                return id;
            });
        }

        /// <summary>
        /// Updates a download notification with byte progress.
        /// </summary>
        /// <param name="operationKey">Operation key of the download.</param>
        /// <param name="progress">Current byte counts and completed fraction.</param>
        /// <param name="statusText">Status line to show, or null for the localized download status.</param>
        public void ReportDownloadProgress(string operationKey, DownloadProgress progress, string? statusText = null)
        {
            RunOnMainThread(() =>
            {
                // Each attempt has its own identity, so reports posted after completion are ignored.
                if (!_activeDownloads.TryGetValue(operationKey, out var notification))
                {
                    return;
                }

                // Display the same measurement sent to the other progress receivers.
                notification.ApplyDownloadTransferSample(progress);

                var detail = progress.TotalBytes > 0
                    ? $"{FormatBytes(progress.DownloadedBytes)} / {FormatBytes(progress.TotalBytes)}"
                    : FormatBytes(progress.DownloadedBytes);

                notification.UpdateContent(
                    AppNotificationSeverity.Info,
                    notification.Title,
                    notification.Message,
                    statusText ?? L.Get(LocalizationKeys.Settings_Downloading),
                    detail);
                notification.UpdateProgress(hasProgress: true, progress.Fraction, isInProgress: true);

                // Progress arrives often, so the save is debounced longer than for other changes.
                Resort(notification);
                SaveChanges(1000);
            });
        }

        /// <summary>
        /// Updates a download notification with a textual status line.
        /// </summary>
        /// <param name="operationKey">Operation key of the download.</param>
        /// <param name="statusText">Status line to show.</param>
        public void ReportDownloadStatus(string operationKey, string statusText)
        {
            RunOnMainThread(() =>
            {
                if (!_activeDownloads.TryGetValue(operationKey, out var notification))
                {
                    return;
                }

                // A status phase (for example installing) is not a byte transfer, so the speed display is cleared.
                notification.ResetDownloadTransferRow();

                notification.UpdateContent(
                    AppNotificationSeverity.Info,
                    notification.Title,
                    notification.Message,
                    statusText,
                    string.Empty);
                notification.UpdateProgress(hasProgress: false, progressFraction: 0, isInProgress: true);
                Resort(notification);
                SaveChanges(500);
            });
        }

        /// <summary>
        /// Marks a download notification as completed successfully.
        /// </summary>
        /// <param name="operationKey">Operation key of the download.</param>
        /// <param name="statusText">Result text to show.</param>
        public void CompleteDownload(string operationKey, string statusText)
        {
            FinishDownload(operationKey, AppNotificationSeverity.Success, statusText);
        }

        /// <summary>
        /// Marks a download notification as failed.
        /// </summary>
        /// <param name="operationKey">Operation key of the download.</param>
        /// <param name="statusText">Failure text to show.</param>
        public void FailDownload(string operationKey, string statusText)
        {
            FinishDownload(operationKey, AppNotificationSeverity.Error, statusText);
        }

        /// <summary>
        /// Creates a progress bridge that updates notifications and forwards progress to an existing sink.
        /// </summary>
        /// <param name="operationKey">Operation key of the download whose notification receives the progress.</param>
        /// <param name="innerProgress">Optional receiver that gets every report after the notification was updated.</param>
        public IProgress<DownloadProgress> CreateDownloadProgressBridge(
            string operationKey,
            IProgress<DownloadProgress>? innerProgress = null)
        {
            return new InlineProgress<DownloadProgress>(progress =>
            {
                ReportDownloadProgress(operationKey, progress);
                innerProgress?.Report(progress);
            });
        }

        /// <summary>
        /// Records a canceled download attempt.
        /// </summary>
        internal void CancelDownload(string operationKey, string statusText) =>
            FinishDownload(operationKey, AppNotificationSeverity.Warning, statusText);

        /// <summary>
        /// Records the staged release before the external launcher takes over the ASLM update.
        /// </summary>
        internal async Task PrepareDownloadForRestartAsync(string operationKey, string statusText)
        {
            await _dispatcher.InvokeAsync(() =>
                FinishDownload(operationKey, AppNotificationSeverity.Info, statusText, awaitingRestart: true));
            if (!_isInitialized) return;

            try
            {
                await SaveSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The launcher can still apply the staged update if the journal could not be saved.
                Debug.WriteLine(ex);
            }
        }

        /// <summary>
        /// Finishes the attempt once and publishes its result even when its progress row is gone.
        /// </summary>
        private void FinishDownload(string operationKey, AppNotificationSeverity severity, string statusText,
            bool awaitingRestart = false)
        {
            RunOnMainThread(() =>
            {
                if (!_activeDownloads.Remove(operationKey, out var notification)) return;

                notification.AwaitingRestart = awaitingRestart;
                notification.UpdateContent(severity, notification.Title, notification.Message, statusText, string.Empty);
                notification.UpdateProgress(hasProgress: false, 0, isInProgress: false);
                Publish(notification, saveDelay: 0);
            });
        }


        // Publication and ordering

        /// <summary>
        /// Removes one journal entry when the user can dismiss it.
        /// </summary>
        public void Dismiss(AppNotification notification) => RunOnMainThread(() =>
        {
            if (Withdraw(item => ReferenceEquals(item, notification) && item.CanDismiss)) SaveChanges(0);
        });

        /// <summary>
        /// Removes every entry that the user can dismiss.
        /// </summary>
        public void DismissAll() => RunOnMainThread(() =>
        {
            if (Withdraw(item => item.CanDismiss)) SaveChanges(0);
        });

        /// <summary>
        /// Builds a stable, case-insensitive source key.
        /// </summary>
        public static string BuildOperationKey(string sourceKind, string sourceId) =>
            $"{sourceKind.Trim().ToLowerInvariant()}:{sourceId.Trim().ToLowerInvariant()}";

        /// <summary>
        /// Replaces the journal entry for the same event and announces the new toast.
        /// </summary>
        private void Publish(AppNotification notification, bool persist = true, int saveDelay = 200,
            bool replacesUpdateOffer = false)
        {
            if (persist)
            {
                // Add the new row first so replacing the only item does not briefly empty the native list.
                if (_notifications.Contains(notification)) Resort(notification);
                else _notifications.Insert(0, notification);

                var key = GetDeduplicationKey(notification);
                Withdraw(item => !ReferenceEquals(item, notification) &&
                    (GetDeduplicationKey(item) == key || replacesUpdateOffer &&
                        item.Category == AppNotificationCategory.Updates && MatchesSource(item, key.Source)));
                TrimOldNotifications();
                SaveChanges(saveDelay);
            }

            NotificationPublished?.Invoke(this, notification);
        }

        /// <summary>
        /// Removes matching journal entries and withdraws any visible toasts for them.
        /// </summary>
        private bool Withdraw(Predicate<AppNotification> matches)
        {
            var removed = false;
            for (var i = _notifications.Count - 1; i >= 0; i--)
            {
                var item = _notifications[i];
                if (!matches(item)) continue;
                _notifications.RemoveAt(i);
                NotificationWithdrawn?.Invoke(this, item);
                removed = true;
            }

            return removed;
        }

        /// <summary>
        /// Groups offers and active work by event and source, and results by version and outcome as well.
        /// </summary>
        private static (AppNotificationCategory Category, string Source, string Type, string? Version,
            AppNotificationSeverity? Result) GetDeduplicationKey(AppNotification notification)
        {
            var result = notification.Category == AppNotificationCategory.Downloads && !notification.IsInProgress;
            var eventType = notification.Category == AppNotificationCategory.Updates && notification.EventType.Length == 0
                ? "update-available"
                : notification.EventType;
            return (notification.Category, BuildOperationKey(notification.SourceKind, notification.SourceId),
                eventType, result ? notification.VersionKey?.ToLowerInvariant() : null,
                result ? notification.Severity : null);
        }

        /// <summary>
        /// Checks whether a journal entry belongs to the normalized source.
        /// </summary>
        private static bool MatchesSource(AppNotification notification, string source) =>
            BuildOperationKey(notification.SourceKind, notification.SourceId) == source;

        /// <summary>
        /// Moves an updated notification back into newest-first order using Move so listeners
        /// receive a Move notification rather than Remove+Insert, which avoids item animations.
        /// </summary>
        private void Resort(AppNotification notification)
        {
            var currentIndex = _notifications.IndexOf(notification);
            if (currentIndex < 0)
            {
                return;
            }

            // Compute target index while the item is still in the collection.
            var targetIndex = 0;
            for (var i = 0; i < _notifications.Count; i++)
            {
                if (i == currentIndex)
                {
                    continue;
                }

                if (_notifications[i].UpdatedAt <= notification.UpdatedAt)
                {
                    break;
                }

                targetIndex++;
            }

            // Already in newest-first position for this UpdatedAt.
            if (targetIndex == currentIndex)
            {
                return;
            }

            _notifications.Move(currentIndex, targetIndex);
        }

        /// <summary>
        /// Keeps the persisted file bounded so startup remains quick by dropping the oldest entries
        /// (the list is ordered newest first).
        /// </summary>
        private void TrimOldNotifications()
        {
            while (_notifications.Count > MaxPersistedNotifications)
            {
                var item = _notifications[^1];
                _notifications.RemoveAt(_notifications.Count - 1);
                NotificationWithdrawn?.Invoke(this, item);
            }
        }


        // Persistence

        /// <summary>
        /// Restores journal entries, discards superseded offers, and reconciles operations that crossed a restart.
        /// </summary>
        private async Task<bool> LoadAsync(string? installedReleaseTag, string? pendingReleaseTag)
        {
            try
            {
                if (!File.Exists(_filePath)) return false;

                var json = await File.ReadAllTextAsync(_filePath);
                var store = JsonSerializer.Deserialize<NotificationStore>(json, _jsonOptions) ?? new NotificationStore();
                var changed = false;
                var keys = new HashSet<(AppNotificationCategory, string, string, string?, AppNotificationSeverity?)>();
                foreach (var item in store.Notifications.OrderByDescending(item => item.UpdatedAt))
                {
                    if (_notifications.Count >= MaxPersistedNotifications) break;
                    if (string.IsNullOrWhiteSpace(item.Id)) continue;

                    // Restore unfinished work only when the staged ASLM release is still waiting for the launcher.
                    if (item.AwaitingRestart &&
                        !string.Equals(item.VersionKey, pendingReleaseTag, StringComparison.OrdinalIgnoreCase))
                    {
                        var applied = string.Equals(item.VersionKey, installedReleaseTag, StringComparison.OrdinalIgnoreCase);
                        item.Severity = applied ? AppNotificationSeverity.Success : AppNotificationSeverity.Error;
                        item.StatusText = applied
                            ? L.Get(LocalizationKeys.ModuleUpdate_Status_Updated)
                            : L.Get(LocalizationKeys.ModuleUpdate_Status_UpdateFailed);
                        item.AwaitingRestart = false;
                        changed = true;
                    }

                    if (item.IsInProgress)
                    {
                        item.Severity = AppNotificationSeverity.Warning;
                        item.StatusText = LocalizationKeys.Notifications_OperationInterrupted;
                        item.DetailText = string.Empty;
                        item.IsInProgress = false;
                        item.HasProgress = false;
                        item.ProgressFraction = 0;
                        changed = true;
                    }

                    // Earlier journals restored disabled offers as actionable. Keep newer deferrals as saved.
                    var isOffer = item.Category == AppNotificationCategory.Updates;
                    var restoreActions = isOffer && store.FileVersion < PersistentDeferralVersion && !item.OffersUpdateActions;
                    changed |= restoreActions;

                    var notification = new AppNotification(
                        item.Id, item.Category, item.Severity, item.Title, item.Message, item.SourceKind, item.SourceId,
                        item.CreatedAt, item.UpdatedAt, item.IsInProgress, item.ProgressFraction, item.HasProgress,
                        item.StatusText, item.DetailText, restoreActions || item.OffersUpdateActions, item.SuppressToastAutoDismiss)
                    {
                        EventType = item.EventType ?? string.Empty,
                        VersionKey = item.VersionKey,
                        TargetVersion = item.TargetVersion,
                        CurrentVersion = item.CurrentVersion,
                        AwaitingRestart = item.AwaitingRestart
                    };
                    if (isOffer || notification.EventType.Length > 0)
                    {
                        if (!keys.Add(GetDeduplicationKey(notification)))
                        {
                            changed = true;
                            continue;
                        }
                    }

                    _notifications.Add(notification);
                }

                RaiseNotificationsChanged();
                return changed;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // Keep the original file for diagnosis and start with an empty journal.
                Debug.WriteLine(ex);
                _notifications.Clear();
                return false;
            }
        }

        /// <summary>
        /// Queues a debounced save of the current notification snapshot.
        /// </summary>
        /// <param name="delayMs">Milliseconds to wait before saving; 0 saves right away. A newer request replaces a pending one.</param>
        private void QueueSave(int delayMs)
        {
            // Nothing is written before the persisted notifications were loaded, otherwise the file would be overwritten.
            if (!_isInitialized)
            {
                return;
            }

            // Replace any pending save: cancelling it restarts the debounce delay.
            _saveCts?.Cancel();
            _saveCts?.Dispose();
            _saveCts = new CancellationTokenSource();
            var ct = _saveCts.Token;

            // Wait out the delay and save in the background, without blocking the caller.
            _ = Task.Run(async () =>
            {
                try
                {
                    if (delayMs > 0)
                    {
                        await Task.Delay(delayMs, ct);
                    }

                    await SaveSnapshotAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    // Superseded by a newer save request, which writes the latest state.
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The journal remains available in memory and the next mutation can retry the write.
                    Debug.WriteLine(ex);
                }
            }, ct);
        }

        /// <summary>
        /// Saves a point-in-time copy of the notification collection to disk.
        /// </summary>
        private async Task SaveSnapshotAsync(CancellationToken ct)
        {
            await _saveGate.WaitAsync(ct).ConfigureAwait(false);
            var temporaryPath = _filePath + ".tmp";
            try
            {
                var snapshot = await _dispatcher.InvokeAsync(() => new NotificationStore
                {
                    Notifications = _notifications.Take(MaxPersistedNotifications).Select(CreateStoreItem).ToList()
                }).ConfigureAwait(false);

                Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
                var json = JsonSerializer.Serialize(snapshot, _jsonOptions);
                await File.WriteAllTextAsync(temporaryPath, json, ct);
                ct.ThrowIfCancellationRequested();
                File.Move(temporaryPath, _filePath, overwrite: true);
            }
            finally
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                finally
                {
                    _saveGate.Release();
                }
            }
        }

        /// <summary>
        /// Converts a live notification into its persisted DTO.
        /// </summary>
        private static NotificationStoreItem CreateStoreItem(AppNotification notification)
        {
            return new NotificationStoreItem
            {
                Id = notification.Id,
                Category = notification.Category,
                Severity = notification.Severity,
                Title = notification.Title,
                Message = notification.Message,
                StatusText = notification.StatusText,
                DetailText = notification.DetailText,
                SourceKind = notification.SourceKind,
                SourceId = notification.SourceId,
                EventType = notification.EventType,
                VersionKey = notification.VersionKey,
                TargetVersion = notification.TargetVersion,
                CurrentVersion = notification.CurrentVersion,
                AwaitingRestart = notification.AwaitingRestart,
                CreatedAt = notification.CreatedAt,
                UpdatedAt = notification.UpdatedAt,
                IsInProgress = notification.IsInProgress,
                ProgressFraction = notification.ProgressFraction,
                HasProgress = notification.HasProgress,
                OffersUpdateActions = notification.OffersUpdateActions,
                SuppressToastAutoDismiss = notification.SuppressToastAutoDismiss
            };
        }


        // Threading and change notification

        /// <summary>
        /// Runs one notification mutation on the MAUI UI thread: immediately when already there, otherwise queued.
        /// </summary>
        private void RunOnMainThread(Action action)
        {
            if (_dispatcher.IsMainThread)
            {
                action();
                return;
            }

            _dispatcher.BeginInvoke(action);
        }

        /// <summary>
        /// Raises the aggregate notification change event.
        /// </summary>
        private void RaiseNotificationsChanged()
        {
            NotificationsChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Notifies the journal and schedules persistence of the changed collection.
        /// </summary>
        private void SaveChanges(int delayMs)
        {
            RaiseNotificationsChanged();
            QueueSave(delayMs);
        }


        // Identifiers and formatting

        /// <summary>
        /// Formats byte counts into compact transfer labels (B, KB, MB or GB with one decimal for larger units).
        /// </summary>
        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1_073_741_824)
            {
                return $"{bytes / 1_073_741_824.0:F1} GB";
            }

            if (bytes >= 1_048_576)
            {
                return $"{bytes / 1_048_576.0:F1} MB";
            }

            if (bytes >= 1024)
            {
                return $"{bytes / 1024.0:F1} KB";
            }

            return $"{bytes} B";
        }

        /// <summary>
        /// Returns the ASLM data root directory that contains the <c>Data</c> folder.
        /// </summary>
        private static string GetRootDirectory()
        {
            return AppRoot.Directory;
        }


        // Persistence models

        /// <summary>
        /// Represents the persisted notifications file.
        /// </summary>
        private sealed class NotificationStore
        {
            /// <summary>
            /// Version of the file format.
            /// </summary>
            public int FileVersion { get; set; } = PersistentDeferralVersion;

            /// <summary>
            /// Persisted notifications, newest first.
            /// </summary>
            public List<NotificationStoreItem> Notifications { get; set; } = [];
        }

        /// <summary>
        /// Represents one persisted notification.
        /// </summary>
        private sealed class NotificationStoreItem
        {
            /// <summary>
            /// Stable identity of the notification.
            /// </summary>
            public string Id { get; set; } = string.Empty;

            /// <summary>
            /// Category of the notification: updates, downloads or system.
            /// </summary>
            public AppNotificationCategory Category { get; set; }

            /// <summary>
            /// Severity (info, success, warning or error) that decides the accent color of the notification.
            /// </summary>
            public AppNotificationSeverity Severity { get; set; }

            /// <summary>
            /// Notification title.
            /// </summary>
            public string Title { get; set; } = string.Empty;

            /// <summary>
            /// Notification message.
            /// </summary>
            public string Message { get; set; } = string.Empty;

            /// <summary>
            /// Status line shown below the message.
            /// </summary>
            public string StatusText { get; set; } = string.Empty;

            /// <summary>
            /// Detail line, for example the transferred byte counts of a download.
            /// </summary>
            public string DetailText { get; set; } = string.Empty;

            /// <summary>
            /// Kind of operation or item that produced the notification.
            /// </summary>
            public string SourceKind { get; set; } = string.Empty;

            /// <summary>
            /// Identifier of the item that produced the notification.
            /// </summary>
            public string SourceId { get; set; } = string.Empty;

            /// <summary>
            /// Identifies the event within the source for deduplication.
            /// </summary>
            public string EventType { get; set; } = string.Empty;

            /// <summary>
            /// Identifies the release or commit associated with the event, if known.
            /// </summary>
            public string? VersionKey { get; set; }

            /// <summary>
            /// Display version offered, installed, or updated by this event.
            /// </summary>
            public string? TargetVersion { get; set; }

            /// <summary>
            /// Installed version at the time of the offer, when known.
            /// </summary>
            public string? CurrentVersion { get; set; }

            /// <summary>
            /// Indicates that this update has been staged for the launcher.
            /// </summary>
            public bool AwaitingRestart { get; set; }

            /// <summary>
            /// When the notification was created.
            /// </summary>
            public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

            /// <summary>
            /// When the notification was last changed; defines the list order.
            /// </summary>
            public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

            /// <summary>
            /// Whether the operation behind the notification was still running when it was saved.
            /// </summary>
            public bool IsInProgress { get; set; }

            /// <summary>
            /// Completed fraction of the operation, from 0 to 1.
            /// </summary>
            public double ProgressFraction { get; set; }

            /// <summary>
            /// Whether the notification shows a progress bar.
            /// </summary>
            public bool HasProgress { get; set; }

            /// <summary>
            /// Whether the row offers Update now / Update later actions.
            /// </summary>
            public bool OffersUpdateActions { get; set; }

            /// <summary>
            /// Whether the toast stays until the user dismisses it or acts on it.
            /// </summary>
            public bool SuppressToastAutoDismiss { get; set; }
        }
    }
}
