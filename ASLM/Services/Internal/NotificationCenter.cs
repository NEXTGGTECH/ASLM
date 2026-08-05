// Copyright NEXTGGTECH. Apache License 2.0.

using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ASLM.Localization;
using ASLM.Models;
using Microsoft.Maui.ApplicationModel;

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Carries one user action on an update-available notification row or toast.
    /// </summary>
    public sealed class UpdateNotificationActionEventArgs : EventArgs
    {
        /// <summary>
        /// Creates the event payload.
        /// </summary>
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

    /// <summary>
    /// Stores internal ASLM notifications, persists them, and exposes helpers for update and download events.
    /// </summary>
    public sealed class NotificationCenter
    {
        // Fields

        private const string NotificationsFileName = "ASLM_Notifications.json";
        private const int MaxPersistedNotifications = 200;

        private readonly ObservableCollection<AppNotification> _notifications = [];
        private readonly ReadOnlyObservableCollection<AppNotification> _readOnlyNotifications;
        private readonly string _filePath;
        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() }
        };

        private CancellationTokenSource? _saveCts;
        private bool _isInitialized;
        private readonly DownloadTransferSpeedEstimator _downloadTransferSpeedEstimator = new();


        // Initialization

        /// <summary>
        /// Creates the notification service and resolves its persistence file path.
        /// </summary>
        public NotificationCenter()
        {
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


        // Public surface

        /// <summary>
        /// Gets the live notification collection sorted by newest update first.
        /// </summary>
        public ReadOnlyObservableCollection<AppNotification> Notifications => _readOnlyNotifications;


        // Update notifications

        /// <summary>
        /// Publishes a notification for one newly discovered update candidate.
        /// </summary>
        public void PublishUpdateCandidate(UpdateCandidate candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate.TargetKind) ||
                string.IsNullOrWhiteSpace(candidate.TargetId) ||
                string.IsNullOrWhiteSpace(candidate.RemoteVersion))
            {
                return;
            }

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
        public void RequestUpdateNotificationAction(AppNotification notification, bool updateNow)
        {
            RunOnMainThread(() =>
                UpdateNotificationActionRequested?.Invoke(this, new UpdateNotificationActionEventArgs(notification, updateNow)));
        }

        /// <summary>
        /// Hides inline update actions after the user defers from the notifications list.
        /// </summary>
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
        public void PublishSystemToast(string title, string message, string statusText, string sourceId)
        {
            RunOnMainThread(() =>
            {
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

                NotificationPublished?.Invoke(this, notification);
            });
        }


        // Download notifications

        /// <summary>
        /// Starts or refreshes a download notification on the UI thread and waits until it is inserted
        /// so background download progress cannot race ahead of the notification row.
        /// </summary>
        public Task StartDownloadAsync(string operationKey, string title, string message, string sourceKind, string sourceId)
        {
            _downloadTransferSpeedEstimator.Reset(operationKey);

            return MainThread.InvokeOnMainThreadAsync(() =>
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
        public void ReportDownloadProgress(string operationKey, DownloadProgress progress, string? statusText = null)
        {
            RunOnMainThread(() =>
            {
                var notification = FindNotification(BuildDownloadNotificationId(operationKey));
                if (notification == null)
                {
                    return;
                }

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
                Resort(notification);
                RaiseNotificationsChanged();
                QueueSave(1000);
            });
        }

        /// <summary>
        /// Updates a download notification with a textual status line.
        /// </summary>
        public void ReportDownloadStatus(string operationKey, string statusText)
        {
            RunOnMainThread(() =>
            {
                var notification = FindNotification(BuildDownloadNotificationId(operationKey));
                if (notification == null)
                {
                    return;
                }

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
        public void CompleteDownload(string operationKey, string statusText)
        {
            FinishDownload(operationKey, AppNotificationSeverity.Success, statusText, 1.0);
        }

        /// <summary>
        /// Marks a download notification as failed.
        /// </summary>
        public void FailDownload(string operationKey, string statusText)
        {
            FinishDownload(operationKey, AppNotificationSeverity.Error, statusText, 0);
        }

        /// <summary>
        /// Creates a progress bridge that updates notifications and forwards progress to an existing sink.
        /// </summary>
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


        // Public helpers

        /// <summary>
        /// Builds a stable operation key for download notifications.
        /// </summary>
        public static string BuildOperationKey(string sourceKind, string sourceId)
        {
            return $"{sourceKind.Trim().ToLowerInvariant()}:{sourceId.Trim().ToLowerInvariant()}";
        }


        // Upsert and ordering

        /// <summary>
        /// Inserts or updates one notification on the UI thread.
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
        /// </summary>
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
        /// Finds one notification by stable identity.
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
        /// Keeps the persisted file bounded so startup remains quick.
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
        private void QueueSave(int delayMs)
        {
            if (!_isInitialized)
            {
                return;
            }

            _saveCts?.Cancel();
            _saveCts?.Dispose();
            _saveCts = new CancellationTokenSource();
            var ct = _saveCts.Token;

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
                }
            }, ct);
        }

        /// <summary>
        /// Saves a point-in-time copy of the notification collection to disk.
        /// </summary>
        private async Task SaveSnapshotAsync(CancellationToken ct)
        {
            var snapshot = await MainThread.InvokeOnMainThreadAsync(() => new NotificationStore
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
        /// Runs one notification mutation on the MAUI UI thread.
        /// </summary>
        private static void RunOnMainThread(Action action)
        {
            if (MainThread.IsMainThread)
            {
                action();
                return;
            }

            MainThread.BeginInvokeOnMainThread(action);
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
        /// Formats byte counts into compact transfer labels.
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
        /// Returns the application root directory above the deployed App folder.
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
            public int FileVersion { get; set; } = 1;
            public List<NotificationStoreItem> Notifications { get; set; } = [];
        }

        /// <summary>
        /// Represents one persisted notification.
        /// </summary>
        private sealed class NotificationStoreItem
        {
            public string Id { get; set; } = string.Empty;
            public AppNotificationCategory Category { get; set; }
            public AppNotificationSeverity Severity { get; set; }
            public string Title { get; set; } = string.Empty;
            public string Message { get; set; } = string.Empty;
            public string StatusText { get; set; } = string.Empty;
            public string DetailText { get; set; } = string.Empty;
            public string SourceKind { get; set; } = string.Empty;
            public string SourceId { get; set; } = string.Empty;
            public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
            public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
            public bool IsInProgress { get; set; }
            public double ProgressFraction { get; set; }
            public bool HasProgress { get; set; }
            public bool OffersUpdateActions { get; set; }
            public bool SuppressToastAutoDismiss { get; set; }
        }

    }
}
