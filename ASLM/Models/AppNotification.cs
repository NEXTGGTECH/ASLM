// Copyright NEXTGGTECH. Apache License 2.0.

using System.ComponentModel;
using System.Runtime.CompilerServices;
using ASLM.Localization;
using Microsoft.Maui.Graphics;

namespace ASLM.Models
{
    /// <summary>
    /// Distinguishes the top-level notification groups shown in the notifications page.
    /// </summary>
    public enum AppNotificationCategory
    {
        Updates,
        Downloads,
        System
    }

    /// <summary>
    /// Distinguishes the visual importance of one application notification.
    /// </summary>
    public enum AppNotificationSeverity
    {
        Info,
        Success,
        Warning,
        Error
    }

    /// <summary>
    /// Represents one internal ASLM notification with optional progress state.
    /// </summary>
    public sealed class AppNotification : INotifyPropertyChanged
    {
        private string _title;
        private string _message;
        private string _statusText;
        private string _detailText;
        private AppNotificationSeverity _severity;
        private DateTimeOffset _updatedAt;
        private bool _isInProgress;
        private double _progressFraction;
        private bool _hasProgress;
        private string _activeTransferLabel = string.Empty;
        private string _transferSpeedDisplay = "—";
        private string _transferPercentDisplay = "0%";
        private bool _downloadHasKnownTotal;
        private bool _showDownloadMetricsRow;
        private bool _offersUpdateActions;
        private bool _suppressToastAutoDismiss;

        /// <summary>
        /// Creates a notification with stable identity and initial display fields.
        /// </summary>
        public AppNotification(
            string id,
            AppNotificationCategory category,
            AppNotificationSeverity severity,
            string title,
            string message,
            string sourceKind,
            string sourceId)
        {
            Id = id;
            Category = category;
            SourceKind = sourceKind;
            SourceId = sourceId;
            CreatedAt = DateTimeOffset.Now;
            _updatedAt = CreatedAt;
            _title = title;
            _message = message;
            _statusText = string.Empty;
            _detailText = string.Empty;
            _severity = severity;
            _transferSpeedDisplay = "—";
            _transferPercentDisplay = "0%";
        }

        /// <summary>
        /// Restores a notification from persisted storage.
        /// </summary>
        internal AppNotification(
            string id,
            AppNotificationCategory category,
            AppNotificationSeverity severity,
            string title,
            string message,
            string sourceKind,
            string sourceId,
            DateTimeOffset createdAt,
            DateTimeOffset updatedAt,
            bool isInProgress,
            double progressFraction,
            bool hasProgress,
            string statusText,
            string detailText,
            bool offersUpdateActions = false,
            bool suppressToastAutoDismiss = false)
        {
            Id = id;
            Category = category;
            SourceKind = sourceKind;
            SourceId = sourceId;
            CreatedAt = createdAt;
            _updatedAt = updatedAt;
            _title = title;
            _message = message;
            _statusText = statusText;
            _detailText = detailText;
            _severity = severity;
            _isInProgress = isInProgress;
            _progressFraction = Math.Clamp(progressFraction, 0, 1);
            _hasProgress = hasProgress;
            _offersUpdateActions = offersUpdateActions;
            _suppressToastAutoDismiss = suppressToastAutoDismiss;
            if (category == AppNotificationCategory.Downloads && hasProgress && isInProgress &&
                detailText.Contains(" / ", StringComparison.Ordinal))
            {
                _downloadHasKnownTotal = true;
            }

            RefreshTransferPercentDisplay();
            SyncShowDownloadMetricsRow();
            OnPropertyChanged(nameof(ShowActiveTransferInCard));
            OnPropertyChanged(nameof(OffersUpdateActions));
            OnPropertyChanged(nameof(SuppressToastAutoDismiss));
        }

        /// <inheritdoc />
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// Gets the stable notification identity used for deduplication.
        /// </summary>
        public string Id { get; }

        /// <summary>
        /// Gets the notification group used by the notifications page filters.
        /// </summary>
        public AppNotificationCategory Category { get; }

        /// <summary>
        /// Gets the source type that produced this notification.
        /// </summary>
        public string SourceKind { get; }

        /// <summary>
        /// Gets the source identifier that produced this notification.
        /// </summary>
        public string SourceId { get; }

        /// <summary>
        /// Gets the creation timestamp.
        /// </summary>
        public DateTimeOffset CreatedAt { get; }

        /// <summary>
        /// Gets the last update timestamp.
        /// </summary>
        public DateTimeOffset UpdatedAt
        {
            get => _updatedAt;
            private set => SetField(ref _updatedAt, value);
        }

        /// <summary>
        /// Gets the title shown in the notification list.
        /// </summary>
        public string Title
        {
            get => _title;
            private set => SetField(ref _title, value);
        }

        /// <summary>
        /// Gets the main notification message.
        /// </summary>
        public string Message
        {
            get => _message;
            private set
            {
                if (SetField(ref _message, value))
                {
                    OnPropertyChanged(nameof(ShowActiveTransferInCard));
                }
            }
        }

        /// <summary>
        /// Gets the short status line shown above progress details.
        /// </summary>
        public string StatusText
        {
            get => _statusText;
            private set
            {
                if (SetField(ref _statusText, value))
                {
                    OnPropertyChanged(nameof(HasStatusText));
                    OnPropertyChanged(nameof(ShowStatusInCard));
                    OnPropertyChanged(nameof(DetailLine));
                }
            }
        }

        /// <summary>
        /// Gets the optional detailed text shown under the main message.
        /// </summary>
        public string DetailText
        {
            get => _detailText;
            private set
            {
                if (SetField(ref _detailText, value))
                {
                    OnPropertyChanged(nameof(HasDetailText));
                    OnPropertyChanged(nameof(DetailLine));
                }
            }
        }

        /// <summary>
        /// Gets the current visual severity.
        /// </summary>
        public AppNotificationSeverity Severity
        {
            get => _severity;
            private set
            {
                if (SetField(ref _severity, value))
                {
                    OnPropertyChanged(nameof(SeverityLabel));
                    OnPropertyChanged(nameof(AccentColor));
                }
            }
        }

        /// <summary>
        /// Gets whether a long-running operation is still active.
        /// </summary>
        public bool IsInProgress
        {
            get => _isInProgress;
            private set
            {
                if (SetField(ref _isInProgress, value))
                {
                    OnPropertyChanged(nameof(ShowStatusInCard));
                    OnPropertyChanged(nameof(ShowActiveTransferInCard));
                }
            }
        }

        /// <summary>
        /// Gets the normalized progress fraction for determinate operations.
        /// </summary>
        public double ProgressFraction
        {
            get => _progressFraction;
            private set
            {
                var clamped = Math.Clamp(value, 0, 1);
                if (Math.Abs(_progressFraction - clamped) < 0.0001)
                {
                    return;
                }

                _progressFraction = clamped;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ProgressPercentLabel));
            }
        }

        /// <summary>
        /// Gets whether the progress bar should be shown.
        /// </summary>
        public bool HasProgress
        {
            get => _hasProgress;
            private set => SetField(ref _hasProgress, value);
        }

        /// <summary>
        /// Gets whether the status line contains visible text.
        /// </summary>
        public bool HasStatusText => !string.IsNullOrWhiteSpace(StatusText);

        /// <summary>
        /// Gets whether the status line should be shown in the notification card.
        /// Hidden during active download so the card only shows transfer data rows.
        /// Visible once the operation finishes so the result message is displayed.
        /// </summary>
        public bool ShowStatusInCard => HasStatusText && !IsInProgress;

        /// <summary>
        /// Gets whether the detail line contains visible text.
        /// </summary>
        public bool HasDetailText => !string.IsNullOrWhiteSpace(DetailText);

        /// <summary>
        /// Gets the category label shown in the notification card metadata.
        /// </summary>
        public string CategoryLabel => Category.ToString();

        /// <summary>
        /// Gets the severity label shown in the notification card metadata.
        /// </summary>
        public string SeverityLabel => Severity.ToString();

        /// <summary>
        /// Gets the compact progress label.
        /// </summary>
        public string ProgressPercentLabel => $"{ProgressFraction * 100.0:F0}%";

        /// <summary>
        /// Gets the optional label for the active download stream or file.
        /// </summary>
        public string ActiveTransferLabel => _activeTransferLabel;

        /// <summary>
        /// Gets whether a specific active transfer label is shown.
        /// </summary>
        public bool HasActiveTransferLabel => !string.IsNullOrWhiteSpace(_activeTransferLabel);

        /// <summary>
        /// Gets whether the active transfer line should appear in the card.
        /// Suppressed when it only repeats the same resource as <see cref="Message"/> with different punctuation
        /// (for example Ollama model name vs catalog subtitle).
        /// </summary>
        public bool ShowActiveTransferInCard =>
            IsInProgress &&
            HasActiveTransferLabel &&
            !AreResourceLabelsEquivalent(Message, ActiveTransferLabel);

        /// <summary>
        /// Gets the smoothed transfer speed for the download metrics row.
        /// </summary>
        public string TransferSpeedDisplay => _transferSpeedDisplay;

        /// <summary>
        /// Gets the percent or placeholder for the download metrics row.
        /// </summary>
        public string TransferPercentDisplay => _transferPercentDisplay;

        /// <summary>
        /// Gets whether the download metrics row (speed and percent) should be visible.
        /// </summary>
        public bool ShowDownloadMetricsRow => _showDownloadMetricsRow;

        /// <summary>
        /// Gets the timestamp label shown in the notification card.
        /// </summary>
        public string TimestampLabel => UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

        /// <summary>
        /// Gets the compact secondary line used by the notifications popover and toast cards.
        /// </summary>
        public string DetailLine
        {
            get
            {
                var status = !string.IsNullOrWhiteSpace(StatusText) ? StatusText : SeverityLabel;
                var detail = !string.IsNullOrWhiteSpace(DetailText) ? $" - {DetailText}" : string.Empty;
                return $"{status}{detail} - {TimestampLabel}";
            }
        }

        /// <summary>
        /// Gets whether this row should offer Update now / Update later actions (available update cards).
        /// </summary>
        public bool OffersUpdateActions => _offersUpdateActions;

        /// <summary>
        /// Gets the localized label for the inline Update now action.
        /// </summary>
        public string UpdateNowText => L.Get(LocalizationKeys.Notifications_UpdateNow);

        /// <summary>
        /// Gets the localized label for the inline Update later action.
        /// </summary>
        public string UpdateLaterText => L.Get(LocalizationKeys.Notifications_UpdateLater);

        /// <summary>
        /// Refreshes localized action button labels after the UI culture changes.
        /// </summary>
        internal void RefreshLocalizedPresentation()
        {
            OnPropertyChanged(nameof(UpdateNowText));
            OnPropertyChanged(nameof(UpdateLaterText));
        }

        /// <summary>
        /// Gets whether an in-app toast for this notification should stay until the user dismisses it or acts.
        /// </summary>
        public bool SuppressToastAutoDismiss => _suppressToastAutoDismiss;

        /// <summary>
        /// Marks one available-update notification as showing inline update actions and a persistent toast.
        /// </summary>
        internal void SetUpdateAvailabilityPresentation(bool offersUpdateActions, bool suppressToastAutoDismiss)
        {
            SetField(ref _offersUpdateActions, offersUpdateActions, nameof(OffersUpdateActions));
            SetField(ref _suppressToastAutoDismiss, suppressToastAutoDismiss, nameof(SuppressToastAutoDismiss));
        }

        /// <summary>
        /// Hides inline update actions after the user defers from the notifications list.
        /// </summary>
        internal void ClearUpdateAvailabilityPresentation()
        {
            SetUpdateAvailabilityPresentation(false, false);
        }

        /// <summary>
        /// Gets the accent color associated with the notification severity.
        /// </summary>
        public Color AccentColor => Severity switch
        {
            AppNotificationSeverity.Success => Color.FromArgb("#32D74B"),
            AppNotificationSeverity.Warning => Color.FromArgb("#FFD60A"),
            AppNotificationSeverity.Error => Color.FromArgb("#FF453A"),
            _ => Color.FromArgb("#0A84FF")
        };

        /// <summary>
        /// Refreshes the notification text and severity while preserving its identity.
        /// </summary>
        internal void UpdateContent(
            AppNotificationSeverity severity,
            string title,
            string message,
            string statusText,
            string detailText)
        {
            Severity = severity;
            Title = title;
            Message = message;
            StatusText = statusText;
            DetailText = detailText;
            UpdatedAt = DateTimeOffset.Now;

            OnPropertyChanged(nameof(TimestampLabel));
            OnPropertyChanged(nameof(DetailLine));
            RefreshTransferPercentDisplay();
        }

        /// <summary>
        /// Updates determinate or indeterminate progress for the notification.
        /// </summary>
        internal void UpdateProgress(bool hasProgress, double progressFraction, bool isInProgress)
        {
            HasProgress = hasProgress;
            ProgressFraction = progressFraction;
            IsInProgress = isInProgress;
            UpdatedAt = DateTimeOffset.Now;
            if (!isInProgress)
            {
                ResetDownloadTransferRow();
            }

            OnPropertyChanged(nameof(TimestampLabel));
            OnPropertyChanged(nameof(DetailLine));
            SyncShowDownloadMetricsRow();
            RefreshTransferPercentDisplay();
        }

        /// <summary>
        /// Clears streaming labels when a download phase changes or the transfer completes.
        /// </summary>
        internal void ResetDownloadTransferRow()
        {
            SetField(ref _activeTransferLabel, string.Empty, nameof(ActiveTransferLabel));
            OnPropertyChanged(nameof(HasActiveTransferLabel));
            OnPropertyChanged(nameof(ShowActiveTransferInCard));
            SetField(ref _transferSpeedDisplay, "—", nameof(TransferSpeedDisplay));
            _downloadHasKnownTotal = false;
            RefreshTransferPercentDisplay();
            SyncShowDownloadMetricsRow();
        }

        /// <summary>
        /// Applies one streamed download progress snapshot to the notification card.
        /// </summary>
        internal void ApplyDownloadTransferSample(DownloadProgress progress, string? measuredSpeedLabel)
        {
            if (!string.IsNullOrWhiteSpace(progress.ActiveTransferName))
            {
                if (SetField(ref _activeTransferLabel, progress.ActiveTransferName.Trim(), nameof(ActiveTransferLabel)))
                {
                    OnPropertyChanged(nameof(HasActiveTransferLabel));
                }
            }

            _downloadHasKnownTotal = progress.TotalBytes > 0;

            var speedText = string.IsNullOrWhiteSpace(measuredSpeedLabel) ? "—" : measuredSpeedLabel.Trim();
            SetField(ref _transferSpeedDisplay, speedText, nameof(TransferSpeedDisplay));

            RefreshTransferPercentDisplay();
            SyncShowDownloadMetricsRow();
            OnPropertyChanged(nameof(ShowActiveTransferInCard));
        }

        /// <summary>
        /// Recomputes the percent column from the current progress and size hint.
        /// </summary>
        internal void RefreshTransferPercentDisplay()
        {
            string text;
            if (!IsInProgress)
            {
                text = $"{ProgressFraction * 100.0:F0}%";
            }
            else if (!_downloadHasKnownTotal)
            {
                text = "—";
            }
            else
            {
                text = $"{ProgressFraction * 100.0:F0}%";
            }

            SetField(ref _transferPercentDisplay, text, nameof(TransferPercentDisplay));
        }

        /// <summary>
        /// Updates the download metrics row visibility flag for WinUI binding refresh.
        /// </summary>
        private void SyncShowDownloadMetricsRow()
        {
            var next = Category == AppNotificationCategory.Downloads && HasProgress && IsInProgress;
            SetField(ref _showDownloadMetricsRow, next, nameof(ShowDownloadMetricsRow));
        }

        /// <summary>
        /// Raises a property-changed event for one property.
        /// </summary>
        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>
        /// Replaces a field value and raises a property change when it actually changed.
        /// </summary>
        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        /// <summary>
        /// Returns true when the two labels refer to the same resource (ignoring separators and case).
        /// </summary>
        private static bool AreResourceLabelsEquivalent(string message, string transfer)
        {
            return string.Equals(
                NormalizeResourceKey(message),
                NormalizeResourceKey(transfer),
                StringComparison.Ordinal);
        }

        /// <summary>
        /// Keeps only letters and digits so catalog subtitles and engine names compare reliably.
        /// </summary>
        private static string NormalizeResourceKey(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var buffer = new char[value.Length];
            var length = 0;
            foreach (var ch in value)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    buffer[length++] = char.ToLowerInvariant(ch);
                }
            }

            return new string(buffer, 0, length);
        }
    }
}
