// Copyright NEXTGGTECH. Apache License 2.0.

using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ASLM.Localization;
using ASLM.Models;
using Microsoft.Maui.ApplicationModel;

namespace ASLM.Services.Internal
{
    // Event arguments

    /// <summary>
    /// Carries one user action on an update-available notification row or toast.
    /// </summary>
    public sealed class UpdateNotificationActionEventArgs : EventArgs
    {
        /// <summary>
        /// Creates the event payload.
        /// </summary>
        /// <param name="notification">Notification that was acted on.</param>
        /// <param name="updateNow">True for Update now, false for Update later.</param>
        public UpdateNotificationActionEventArgs(AppNotification notification, bool updateNow)
        {
            Notification = notification;
            UpdateNow = updateNow;
        }

        /// <summary>
        /// Gets the notification that was acted on.
        /// </summary>
        public AppNotification Notification { get; }

        /// <summary>
        /// Gets whether the user chose Update now (true) or Update later from the list (false).
        /// </summary>
        public bool UpdateNow { get; }
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
        /// Computes the transfer speed label shown on download notifications.
        /// </summary>
        private readonly DownloadTransferSpeedEstimator _downloadTransferSpeedEstimator = new();


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
        /// Loads persisted notifications once during application startup.
        /// </summary>
        public async Task InitializeAsync()
        {
            if (_isInitialized)
            {
                return;
            }

            await LoadAsync();
            _isInitialized = true;
        }


        // Events

        /// <summary>
        /// Raised when notification counts or filter projections may have changed.
        /// </summary>
        public event EventHandler? NotificationsChanged;

        /// <summary>
        /// Raised when a new notification should be shown as an in-app toast.
        /// </summary>
        public event EventHandler<AppNotification>? NotificationPublished;

        /// <summary>
        /// Raised when the user requests Update now or Update later on an update-available notification.
        /// </summary>
        public event EventHandler<UpdateNotificationActionEventArgs>? UpdateNotificationActionRequested;


        // Exposed collection

        /// <summary>
        /// Gets the live notification collection sorted by newest update first.
        /// </summary>
        public ReadOnlyObservableCollection<AppNotification> Notifications => _readOnlyNotifications;


        // Update notifications

        /// <summary>
        /// Publishes a notification for one newly discovered update candidate.
        /// The notification id contains the remote version, so each new version gets its own row.
        /// </summary>
        /// <param name="candidate">Update that was found; candidates without target or remote version are ignored.</param>
        public void PublishUpdateCandidate(UpdateCandidate candidate)
        {
            // Without target and version there is nothing meaningful to show or to deduplicate on.
            if (string.IsNullOrWhiteSpace(candidate.TargetKind) ||
                string.IsNullOrWhiteSpace(candidate.TargetId) ||
                string.IsNullOrWhiteSpace(candidate.RemoteVersion))
            {
                return;
            }

            // Build the localized texts: the title depends on whether the target is the app, an engine or a module.
            var isApp = string.Equals(candidate.TargetKind, "app", StringComparison.OrdinalIgnoreCase);
            var isEngine = string.Equals(candidate.TargetKind, "engine", StringComparison.OrdinalIgnoreCase);
            var title = isApp
                ? L.Get(LocalizationKeys.Notifications_AslmUpdateAvailable)
                : isEngine
                    ? L.Get(LocalizationKeys.Notifications_EngineUpdateAvailable)
                    : L.Get(LocalizationKeys.Notifications_ModuleUpdateAvailable);
            var currentVersion = string.IsNullOrWhiteSpace(candidate.CurrentVersion)
                ? L.Get(LocalizationKeys.Notifications_InstalledVersionFallback)
                : candidate.CurrentVersion;
            var message = $"{candidate.Name}: {currentVersion} -> {candidate.RemoteVersion}";
            var detail = string.IsNullOrWhiteSpace(candidate.Channel)
                ? string.Empty
                : L.Get(LocalizationKeys.Notifications_ChannelFormat, candidate.Channel);

            // Create or refresh the row and show a toast for a new one; the toast stays until the user acts on it.
            UpsertNotification(
                id: BuildUpdateNotificationId(candidate),
                category: AppNotificationCategory.Updates,
                severity: AppNotificationSeverity.Info,
                title: title,
                message: message,
                statusText: L.Get(LocalizationKeys.Notifications_NewVersionFound),
                detailText: detail,
                sourceKind: candidate.TargetKind,
                sourceId: candidate.TargetId,
                showToastForNew: true,
                afterUpdate: notification =>
                    notification.SetUpdateAvailabilityPresentation(
                        offersUpdateActions: true,
                        suppressToastAutoDismiss: true));
        }

        /// <summary>
        /// Raises the update notification action event on the UI thread for shell routing.
        /// </summary>
        /// <param name="notification">Update notification the user acted on.</param>
        /// <param name="updateNow">True for Update now, false for Update later.</param>
        public void RequestUpdateNotificationAction(AppNotification notification, bool updateNow)
        {
            RunOnMainThread(() =>
                UpdateNotificationActionRequested?.Invoke(this, new UpdateNotificationActionEventArgs(notification, updateNow)));
        }

        /// <summary>
        /// Hides inline update actions after the user defers from the notifications list.
        /// </summary>
        /// <param name="notification">Update notification whose Update now / Update later actions are removed.</param>
        public void ClearUpdateNotificationDeferredActions(AppNotification notification)
        {
            RunOnMainThread(() =>
            {
                notification.ClearUpdateAvailabilityPresentation();
                RaiseNotificationsChanged();
                QueueSave(0);
            });
        }


        // System toasts

        /// <summary>
        /// Publishes a short-lived system toast without adding it to the persisted notification list.
        /// </summary>
        /// <param name="title">Toast title.</param>
        /// <param name="message">Toast message.</param>
        /// <param name="statusText">Status line shown below the message.</param>
        /// <param name="sourceId">Identifier of the component that raised the toast; also part of the toast id.</param>
        public void PublishSystemToast(string title, string message, string statusText, string sourceId)
        {
            RunOnMainThread(() =>
            {
                // The id contains a timestamp, so repeated toasts of one source never share a notification.
                var notification = new AppNotification(
                    $"toast:system:{sourceId}:{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                    AppNotificationCategory.System,
                    AppNotificationSeverity.Success,
                    title,
                    message,
                    "system",
                    sourceId);

                notification.UpdateContent(
                    AppNotificationSeverity.Success,
                    title,
                    message,
                    statusText,
                    string.Empty);

                // Only the toast is raised; the notification is never added to the list.
                NotificationPublished?.Invoke(this, notification);
            });
        }


        // Download notifications

        /// <summary>
        /// Starts or refreshes a download notification on the UI thread and waits until it is inserted
        /// so background download progress cannot race ahead of the notification row.
        /// </summary>
        /// <param name="operationKey">Operation key from <see cref="BuildOperationKey"/> that identifies the download.</param>
        /// <param name="title">Notification title.</param>
        /// <param name="message">Notification message.</param>
        /// <param name="sourceKind">Kind of operation or item that produced the download; stored with the notification.</param>
        /// <param name="sourceId">Identifier of the item the download belongs to; stored with the notification.</param>
        public Task StartDownloadAsync(string operationKey, string title, string message, string sourceKind, string sourceId)
        {
            // Start a fresh speed window for this operation.
            _downloadTransferSpeedEstimator.Reset(operationKey);

            return _dispatcher.InvokeAsync(() =>
            {
                UpsertNotificationImpl(
                    id: BuildDownloadNotificationId(operationKey),
                    category: AppNotificationCategory.Downloads,
                    severity: AppNotificationSeverity.Info,
                    title: title,
                    message: message,
                    statusText: "Starting download...",
                    detailText: "0 B",
                    sourceKind: sourceKind,
                    sourceId: sourceId,
                    showToastForNew: true,
                    afterUpdate: notification => notification.UpdateProgress(hasProgress: true, progressFraction: 0, isInProgress: true));
            });
        }

        /// <summary>
        /// Updates a download notification with byte progress.
        /// </summary>
        /// <param name="operationKey">Operation key of the download.</param>
        /// <param name="progress">Current byte counts and completed fraction.</param>
        /// <param name="statusText">Status line to show, or null for the default "Downloading..." text.</param>
        public void ReportDownloadProgress(string operationKey, DownloadProgress progress, string? statusText = null)
        {
            RunOnMainThread(() =>
            {
                // Late reports for a download that was never started or already removed are ignored.
                var notification = FindNotification(BuildDownloadNotificationId(operationKey));
                if (notification == null)
                {
                    return;
                }

                // Sample the transfer speed and format the downloaded / total byte detail line.
                var speedLabel = _downloadTransferSpeedEstimator.Sample(operationKey, progress.DownloadedBytes);
                notification.ApplyDownloadTransferSample(progress, speedLabel);

                var detail = progress.TotalBytes > 0
                    ? $"{FormatBytes(progress.DownloadedBytes)} / {FormatBytes(progress.TotalBytes)}"
                    : FormatBytes(progress.DownloadedBytes);

                notification.UpdateContent(
                    AppNotificationSeverity.Info,
                    notification.Title,
                    notification.Message,
                    statusText ?? "Downloading...",
                    detail);
                notification.UpdateProgress(hasProgress: true, progress.Fraction, isInProgress: true);

                // Progress arrives often, so the save is debounced longer than for other changes.
                Resort(notification);
                RaiseNotificationsChanged();
                QueueSave(1000);
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
                var notification = FindNotification(BuildDownloadNotificationId(operationKey));
                if (notification == null)
                {
                    return;
                }

                // A status phase (for example installing) is not a byte transfer, so the speed display is cleared.
                _downloadTransferSpeedEstimator.Reset(operationKey);
                notification.ResetDownloadTransferRow();

                notification.UpdateContent(
                    AppNotificationSeverity.Info,
                    notification.Title,
                    notification.Message,
                    statusText,
                    notification.DetailText);
                notification.UpdateProgress(notification.HasProgress, notification.ProgressFraction, isInProgress: true);
                Resort(notification);
                RaiseNotificationsChanged();
                QueueSave(500);
            });
        }

        /// <summary>
        /// Marks a download notification as completed successfully.
        /// </summary>
        /// <param name="operationKey">Operation key of the download.</param>
        /// <param name="statusText">Result text to show.</param>
        public void CompleteDownload(string operationKey, string statusText)
        {
            FinishDownload(operationKey, AppNotificationSeverity.Success, statusText, 1.0);
        }

        /// <summary>
        /// Marks a download notification as failed.
        /// </summary>
        /// <param name="operationKey">Operation key of the download.</param>
        /// <param name="statusText">Failure text to show.</param>
        public void FailDownload(string operationKey, string statusText)
        {
            FinishDownload(operationKey, AppNotificationSeverity.Error, statusText, 0);
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
            return new Progress<DownloadProgress>(progress =>
            {
                ReportDownloadProgress(operationKey, progress);
                innerProgress?.Report(progress);
            });
        }

        /// <summary>
        /// Finishes a download notification with the requested severity and status text.
        /// </summary>
        /// <param name="operationKey">Operation key of the download.</param>
        /// <param name="severity">Success or error severity of the final state.</param>
        /// <param name="statusText">Result text to show.</param>
        /// <param name="finalProgress">Progress fraction the caller associates with the outcome; a finished row shows no progress bar.</param>
        private void FinishDownload(
            string operationKey,
            AppNotificationSeverity severity,
            string statusText,
            double finalProgress)
        {
            RunOnMainThread(() =>
            {
                var notification = FindNotification(BuildDownloadNotificationId(operationKey));
                if (notification == null)
                {
                    return;
                }

                _downloadTransferSpeedEstimator.Reset(operationKey);

                // Clear byte-transfer detail so completed cards show only the result message.
                notification.UpdateContent(
                    severity,
                    notification.Title,
                    notification.Message,
                    statusText,
                    string.Empty);
                notification.UpdateProgress(hasProgress: false, 0, isInProgress: false);

                // Save immediately and raise the toast so the user sees the final result.
                Resort(notification);
                RaiseNotificationsChanged();
                QueueSave(0);
                NotificationPublished?.Invoke(this, notification);
            });
        }


        // Notification list

        /// <summary>
        /// Removes one notification from the visible list.
        /// </summary>
        /// <param name="notification">Notification to remove.</param>
        public void Dismiss(AppNotification notification)
        {
            RunOnMainThread(() =>
            {
                _notifications.Remove(notification);
                RaiseNotificationsChanged();
                QueueSave(0);
            });
        }

        /// <summary>
        /// Clears all persisted notifications at once.
        /// </summary>
        public void DismissAll()
        {
            RunOnMainThread(() =>
            {
                _notifications.Clear();
                RaiseNotificationsChanged();
                QueueSave(0);
            });
        }


        // Operation keys

        /// <summary>
        /// Builds a stable operation key for download notifications: trimmed, lowercase <c>kind:id</c>.
        /// </summary>
        /// <param name="sourceKind">Kind of operation or item.</param>
        /// <param name="sourceId">Identifier of the item.</param>
        public static string BuildOperationKey(string sourceKind, string sourceId)
        {
            return $"{sourceKind.Trim().ToLowerInvariant()}:{sourceId.Trim().ToLowerInvariant()}";
        }


        // Upsert and ordering

        /// <summary>
        /// Inserts or updates one notification on the UI thread. The parameters are the same as for
        /// <see cref="UpsertNotificationImpl"/>, which performs the work.
        /// </summary>
        private void UpsertNotification(
            string id,
            AppNotificationCategory category,
            AppNotificationSeverity severity,
            string title,
            string message,
            string statusText,
            string detailText,
            string sourceKind,
            string sourceId,
            bool showToastForNew,
            Action<AppNotification>? afterUpdate = null)
        {
            RunOnMainThread(() => UpsertNotificationImpl(
                id,
                category,
                severity,
                title,
                message,
                statusText,
                detailText,
                sourceKind,
                sourceId,
                showToastForNew,
                afterUpdate));
        }

        /// <summary>
        /// Inserts or updates one notification while already executing on the UI thread.
        /// A toast is raised only when the notification is new and <paramref name="showToastForNew"/> is set.
        /// </summary>
        /// <param name="id">Stable identity; an existing notification with the same id is updated.</param>
        /// <param name="category">Category of a newly created notification.</param>
        /// <param name="severity">Severity shown after the update.</param>
        /// <param name="title">Title shown after the update.</param>
        /// <param name="message">Message shown after the update.</param>
        /// <param name="statusText">Status line shown after the update.</param>
        /// <param name="detailText">Detail line shown after the update.</param>
        /// <param name="sourceKind">Source kind of a newly created notification.</param>
        /// <param name="sourceId">Source id of a newly created notification.</param>
        /// <param name="showToastForNew">Whether a newly created notification is also announced as a toast.</param>
        /// <param name="afterUpdate">Optional callback that applies extra state to the notification after its content was refreshed.</param>
        private void UpsertNotificationImpl(
            string id,
            AppNotificationCategory category,
            AppNotificationSeverity severity,
            string title,
            string message,
            string statusText,
            string detailText,
            string sourceKind,
            string sourceId,
            bool showToastForNew,
            Action<AppNotification>? afterUpdate = null)
        {
            // Resolve an existing row or create one at the head of the list.
            var notification = FindNotification(id);
            var isNew = notification == null;
            if (notification == null)
            {
                notification = new AppNotification(id, category, severity, title, message, sourceKind, sourceId);
                _notifications.Insert(0, notification);
            }

            // Refresh visible text and let callers attach category-specific state.
            notification.UpdateContent(
                severity,
                title,
                message,
                statusText,
                detailText);
            afterUpdate?.Invoke(notification);

            // Re-sort, cap persisted size, notify listeners, and debounce disk write.
            Resort(notification);
            TrimOldNotifications();
            RaiseNotificationsChanged();
            QueueSave(200);

            if (isNew && showToastForNew)
            {
                NotificationPublished?.Invoke(this, notification);
            }
        }

        /// <summary>
        /// Finds one notification by stable identity (case-insensitive); returns null when it does not exist.
        /// </summary>
        private AppNotification? FindNotification(string id)
        {
            return _notifications.FirstOrDefault(notification =>
                string.Equals(notification.Id, id, StringComparison.OrdinalIgnoreCase));
        }

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
                _notifications.RemoveAt(_notifications.Count - 1);
            }
        }


        // Persistence

        /// <summary>
        /// Loads persisted notifications from disk and restores their progress state.
        /// </summary>
        private async Task LoadAsync()
        {
            try
            {
                // Nothing to restore on first run.
                if (!File.Exists(_filePath))
                {
                    return;
                }

                var json = await File.ReadAllTextAsync(_filePath);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return;
                }

                // Deserialize the snapshot and rebuild the in-memory collection.
                var store = JsonSerializer.Deserialize<NotificationStore>(json, _jsonOptions) ?? new NotificationStore();
                foreach (var item in store.Notifications
                             .Where(item => !string.IsNullOrWhiteSpace(item.Id))
                             .OrderByDescending(item => item.UpdatedAt)
                             .Take(MaxPersistedNotifications))
                {
                    _notifications.Add(new AppNotification(
                        item.Id,
                        item.Category,
                        item.Severity,
                        item.Title,
                        item.Message,
                        item.SourceKind,
                        item.SourceId,
                        item.CreatedAt,
                        item.UpdatedAt,
                        item.IsInProgress,
                        item.ProgressFraction,
                        item.HasProgress,
                        item.StatusText,
                        item.DetailText,
                        item.OffersUpdateActions,
                        item.SuppressToastAutoDismiss));
                }

                RaiseNotificationsChanged();
            }
            catch
            {
                // Corrupt or incompatible file: start with an empty list rather than partial state.
                _notifications.Clear();
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
            }, ct);
        }

        /// <summary>
        /// Saves a point-in-time copy of the notification collection to disk.
        /// </summary>
        private async Task SaveSnapshotAsync(CancellationToken ct)
        {
            // Copy the collection on the UI thread, where it is modified, then write the copy from the background.
            var snapshot = await _dispatcher.InvokeAsync(() => new NotificationStore
            {
                Notifications = _notifications
                    .OrderByDescending(notification => notification.UpdatedAt)
                    .Take(MaxPersistedNotifications)
                    .Select(CreateStoreItem)
                    .ToList()
            });

            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var json = JsonSerializer.Serialize(snapshot, _jsonOptions);
            await File.WriteAllTextAsync(_filePath, json, ct);
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


        // Identifiers and formatting

        /// <summary>
        /// Builds a stable notification id for one update candidate.
        /// </summary>
        private static string BuildUpdateNotificationId(UpdateCandidate candidate)
        {
            return $"update:{candidate.TargetKind}:{candidate.TargetId}:{candidate.RemoteVersion}".ToLowerInvariant();
        }

        /// <summary>
        /// Builds a stable notification id for one download operation.
        /// </summary>
        private static string BuildDownloadNotificationId(string operationKey)
        {
            return $"download:{operationKey}".ToLowerInvariant();
        }

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
            public int FileVersion { get; set; } = 1;

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
