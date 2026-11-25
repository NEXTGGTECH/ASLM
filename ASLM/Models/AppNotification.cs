// Copyright NEXTGGTECH. Apache License 2.0.

using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using ASLM.Localization;
using ASLM.Resources.Strings;
using Microsoft.Maui.Graphics;

namespace ASLM.Models
{
    // Notification classification

    /// <summary>
    /// Distinguishes the top-level notification groups shown in the notifications page.
    /// </summary>
    public enum AppNotificationCategory
    {
        /// <summary>
        /// Available ASLM, module, and engine updates.
        /// </summary>
        Updates,

        /// <summary>
        /// Long-running downloads that report transfer progress.
        /// </summary>
        Downloads,

        /// <summary>
        /// General application events, such as short-lived system toasts.
        /// </summary>
        System
    }

    /// <summary>
    /// Distinguishes the visual importance of one application notification.
    /// </summary>
    public enum AppNotificationSeverity
    {
        /// <summary>
        /// Neutral information; shown with the blue accent color.
        /// </summary>
        Info,

        /// <summary>
        /// Completed operation; shown with the green accent color.
        /// </summary>
        Success,

        /// <summary>
        /// Recoverable problem that needs attention; shown with the yellow accent color.
        /// </summary>
        Warning,

        /// <summary>
        /// Failed operation; shown with the red accent color.
        /// </summary>
        Error
    }


    // Notification item

    /// <summary>
    /// Represents one internal ASLM notification with optional progress state.
    /// </summary>
    public sealed class AppNotification : INotifyPropertyChanged
    {
        // Fields

        /// <summary>
        /// Marks optional hyphen breaks inside long alphanumeric parts of the title.
        /// </summary>
        private static readonly Regex HeadingBreaks = new(@"(?<=[A-Za-z0-9]{6})(?=[A-Za-z0-9]{6})");

        /// <summary>
        /// Recognizes saved fallback labels after the UI language has changed.
        /// </summary>
        private static readonly Lazy<string[]> LegacyUnknownVersions =
            new(() => GetHistoricalLabels(LocalizationKeys.Notifications_InstalledVersionFallback));

        /// <summary>
        /// Recognizes first installations saved by versions that used the module update event for both operations.
        /// </summary>
        private static readonly Lazy<string[]> LegacyInstallationStatuses =
            new(() => GetHistoricalLabels(LocalizationKeys.ModuleInfo_Installed));

        /// <summary>
        /// Backing field of <see cref="Title"/>.
        /// </summary>
        private string _title;

        /// <summary>
        /// Backing field of <see cref="Message"/>.
        /// </summary>
        private string _message;

        /// <summary>
        /// Backing field of <see cref="StatusText"/>.
        /// </summary>
        private string _statusText;

        /// <summary>
        /// Backing field of <see cref="DetailText"/>.
        /// </summary>
        private string _detailText;

        /// <summary>
        /// Backing field of <see cref="Severity"/>.
        /// </summary>
        private AppNotificationSeverity _severity;

        /// <summary>
        /// Backing field of <see cref="UpdatedAt"/>.
        /// </summary>
        private DateTimeOffset _updatedAt;

        /// <summary>
        /// Backing field of <see cref="IsInProgress"/>.
        /// </summary>
        private bool _isInProgress;

        /// <summary>
        /// Backing field of <see cref="ProgressFraction"/>, always within 0 and 1.
        /// </summary>
        private double _progressFraction;

        /// <summary>
        /// Backing field of <see cref="HasProgress"/>.
        /// </summary>
        private bool _hasProgress;

        /// <summary>
        /// Name of the file or stream being transferred right now; empty when unknown.
        /// </summary>
        private string _activeTransferLabel = string.Empty;

        /// <summary>
        /// Smoothed transfer speed text, or a dash placeholder while no speed is known.
        /// </summary>
        private string _transferSpeedDisplay = "—";

        /// <summary>
        /// Percent text of the download metrics row, or a dash placeholder while the total size is unknown.
        /// </summary>
        private string _transferPercentDisplay = "0%";

        /// <summary>
        /// Whether the running download reports a total size, so a percentage is meaningful.
        /// </summary>
        private bool _downloadHasKnownTotal;

        /// <summary>
        /// Backing field of <see cref="ShowDownloadMetricsRow"/>.
        /// </summary>
        private bool _showDownloadMetricsRow;

        /// <summary>
        /// Backing field of <see cref="OffersUpdateActions"/>.
        /// </summary>
        private bool _offersUpdateActions;

        /// <summary>
        /// Backing field of <see cref="SuppressToastAutoDismiss"/>.
        /// </summary>
        private bool _suppressToastAutoDismiss;


        // Initialization

        /// <summary>
        /// Creates a notification with stable identity and initial display fields.
        /// </summary>
        /// <param name="id">Stable identity used for deduplication.</param>
        /// <param name="category">Group of the notification on the notifications page.</param>
        /// <param name="severity">Initial visual importance.</param>
        /// <param name="title">Title shown in the notification list.</param>
        /// <param name="message">Main notification message.</param>
        /// <param name="sourceKind">
        /// Kind of the source that produced the notification, for example <c>app</c> or <c>module</c>.
        /// </param>
        /// <param name="sourceId">Identifier of the source within its kind.</param>
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
            // Copy the persisted identity and display state without raising change events.
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

            // A running download whose detail reads "done / total" is known to have a total size.
            if (category == AppNotificationCategory.Downloads && hasProgress && isInProgress &&
                detailText.Contains(" / ", StringComparison.Ordinal))
            {
                _downloadHasKnownTotal = true;
            }

            // Derive the transfer row state and tell bindings about values computed above.
            RefreshTransferPercentDisplay();
            SyncShowDownloadMetricsRow();
            OnPropertyChanged(nameof(ShowActiveTransferInCard));
            OnPropertyChanged(nameof(OffersUpdateActions));
            OnPropertyChanged(nameof(SuppressToastAutoDismiss));
        }


        // Events

        /// <summary>
        /// Raised when a bindable property changes so bound notification cards refresh.
        /// </summary>
        public event PropertyChangedEventHandler? PropertyChanged;


        // Identity and timestamps

        /// <summary>
        /// Gets the stable notification identity used for deduplication.
        /// </summary>
        public string Id { get; }

        /// <summary>
        /// Identifies the event within its category and source for replacement.
        /// </summary>
        internal string EventType { get; init; } = string.Empty;

        /// <summary>
        /// Identifies the release or commit associated with the event, when known.
        /// </summary>
        internal string? VersionKey { get; init; }

        /// <summary>
        /// Gets the display version of the component offered, installed, or updated by this notification.
        /// </summary>
        internal string? TargetVersion { get; init; }

        /// <summary>
        /// Gets the previously installed version shown in an available update, when known.
        /// </summary>
        internal string? CurrentVersion { get; init; }

        /// <summary>
        /// Indicates that the application update has been staged and still needs to be applied.
        /// </summary>
        internal bool AwaitingRestart { get; set; }

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


        // Display content

        /// <summary>
        /// Gets the title shown in the notification list.
        /// </summary>
        public string Title
        {
            get => _title;
            private set
            {
                if (!SetField(ref _title, value)) return;

                OnPropertyChanged(nameof(CardTitle));
                OnPropertyChanged(nameof(ShowActiveTransferInCard));
            }
        }

        /// <summary>
        /// Gets the main notification message.
        /// </summary>
        public string Message
        {
            get => _message;
            private set
            {
                if (!SetField(ref _message, value)) return;

                OnPropertyChanged(nameof(CardTitle));
                RefreshCardStatus();
                OnPropertyChanged(nameof(ShowActiveTransferInCard));
            }
        }

        /// <summary>
        /// Gets the stored status text or the resource key for a built-in operation result.
        /// </summary>
        public string StatusText
        {
            get => _statusText;
            private set
            {
                if (SetField(ref _statusText, value))
                {
                    RefreshCardStatus();
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
                    OnPropertyChanged(nameof(CardTitle));
                    RefreshCardStatus();
                }
            }
        }


        // Progress state

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
                    OnPropertyChanged(nameof(CardTitle));
                    RefreshCardStatus();
                    OnPropertyChanged(nameof(ShowActiveTransferInCard));
                    OnPropertyChanged(nameof(CanDismiss));
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
                // Ignore changes below the display precision to avoid a flood of binding updates.
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


        // Derived display values

        /// <summary>
        /// Gets the title shared by the journal and toasts, with optional hyphens inside long names.
        /// </summary>
        public string CardTitle
        {
            get
            {
                var heading = Category == AppNotificationCategory.Updates || IsComponentUpdate || IsModuleInstallation
                    ? FormatComponentHeading()
                    : Title;
                return HeadingBreaks.Replace(heading, "\u00AD");
            }
        }

        /// <summary>
        /// Gets the status line when it adds information to the title.
        /// </summary>
        public string CardStatusText
        {
            get
            {
                if (Category == AppNotificationCategory.Updates ||
                    IsSuccessfulComponentOperation && (IsComponentUpdate || IsModuleInstallation))
                {
                    return string.Empty;
                }

                // Older journals stored these built-in results as English sentences.
                var status = StatusText switch
                {
                    "Operation interrupted." or LocalizationKeys.Notifications_OperationInterrupted =>
                        L.Get(LocalizationKeys.Notifications_OperationInterrupted),
                    "Update prepared. Restart to apply." or LocalizationKeys.Notifications_RestartToFinishUpdate =>
                        L.Get(LocalizationKeys.Notifications_RestartToFinishUpdate),
                    _ => string.IsNullOrWhiteSpace(StatusText) ? SeverityLabel : StatusText
                };
                if (IsComponentUpdate || IsModuleInstallation || string.IsNullOrWhiteSpace(Message)) return status;

                return string.IsNullOrWhiteSpace(StatusText) ||
                    string.Equals(Message, StatusText, StringComparison.OrdinalIgnoreCase)
                    ? Message
                    : $"{Message} - {status}";
            }
        }

        /// <summary>
        /// Indicates whether the card needs a status row.
        /// </summary>
        public bool HasCardStatus => !string.IsNullOrWhiteSpace(CardStatusText);

        /// <summary>
        /// Gets the component or resource description for comparison with the active transfer label.
        /// </summary>
        private string Subject => TargetVersion != null ? $"{Title} {TargetVersion}" :
            string.IsNullOrWhiteSpace(Message) ? Title : Message;

        /// <summary>
        /// Identifies completed component operations that can state the result in the title.
        /// </summary>
        private bool IsSuccessfulComponentOperation => !IsInProgress && Severity == AppNotificationSeverity.Success;

        /// <summary>
        /// Recognizes app, engine, and module updates, including the old operation-key format.
        /// </summary>
        private bool IsComponentUpdate => Category == AppNotificationCategory.Downloads &&
            (EventType is "app-update" or "engine-update" or "module-update" ||
                IsLegacyDownload($"{SourceKind}-update"));

        /// <summary>
        /// Recognizes first installations, including old successful installations saved as module updates.
        /// </summary>
        private bool IsModuleInstallation => Category == AppNotificationCategory.Downloads &&
            (EventType == "module-install" || IsLegacyDownload("module-install") ||
                IsLegacyDownload("module-update") && LegacyInstallationStatuses.Value.Contains(StatusText));

        /// <summary>
        /// Matches an operation encoded in the notification id before event types were stored separately.
        /// </summary>
        private bool IsLegacyDownload(string operation) =>
            EventType.Length == 0 && Id.StartsWith($"download:{operation}:", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Formats component names and versions with the word order of the active locale.
        /// </summary>
        private string FormatComponentHeading()
        {
            var (name, target, current) = TargetVersion is null
                ? ReadHistoricalComponent()
                : (Title, TargetVersion, CurrentVersion);

            if (string.IsNullOrWhiteSpace(name)) return Title;

            if (Category == AppNotificationCategory.Updates)
            {
                if (string.IsNullOrWhiteSpace(target)) return name;
                return string.IsNullOrWhiteSpace(current)
                    ? L.Get(LocalizationKeys.Notifications_UpdateAvailableFormat, name, target)
                    : L.Get(LocalizationKeys.Notifications_UpdateAvailableFromFormat, name, current, target);
            }

            if (IsModuleInstallation)
            {
                if (IsInProgress)
                    return L.Get(LocalizationKeys.ModuleInfo_Downloading,
                        string.IsNullOrWhiteSpace(target) ? name : $"{name} {target}");
                if (IsSuccessfulComponentOperation)
                {
                    return string.IsNullOrWhiteSpace(target)
                        ? L.Get(LocalizationKeys.Notifications_InstalledFormat, name)
                        : L.Get(LocalizationKeys.Notifications_InstalledVersionFormat, name, target);
                }
            }
            else if (!string.IsNullOrWhiteSpace(target))
            {
                if (IsInProgress) return L.Get(LocalizationKeys.Notifications_UpdatingFormat, name, target);
                if (IsSuccessfulComponentOperation) return L.Get(LocalizationKeys.Notifications_UpdatedFormat, name, target);
            }

            return string.IsNullOrWhiteSpace(target) ? name : $"{name} {target}";
        }

        /// <summary>
        /// Recovers display arguments from history saved before version labels were separate fields.
        /// </summary>
        private (string Name, string? Target, string? Current) ReadHistoricalComponent()
        {
            var subject = Subject;
            if (Category == AppNotificationCategory.Updates)
            {
                var separator = subject.LastIndexOf(" -> ", StringComparison.Ordinal);
                if (separator >= 0)
                {
                    var from = subject[..separator];
                    var target = subject[(separator + 4)..];
                    var fallback = LegacyUnknownVersions.Value.FirstOrDefault(label =>
                        from.EndsWith($" {label}", StringComparison.OrdinalIgnoreCase));
                    if (fallback != null) return (from[..^(fallback.Length + 1)].TrimEnd(':'), target, null);

                    // Older offers separated the name and current version with a colon.
                    var nameEnd = from.LastIndexOf(' ');
                    if (nameEnd >= 0) return (from[..nameEnd].TrimEnd(':'), target, from[(nameEnd + 1)..]);
                }
            }

            // Installs without a release or commit kept only the name in the message.
            if (IsModuleInstallation && !IsLegacyDownload("module-update") && VersionKey is null)
                return (subject, null, null);

            var versionStart = subject.LastIndexOf(' ');
            return versionStart < 0 ? (subject, null, null) : (subject[..versionStart], subject[(versionStart + 1)..], null);
        }

        /// <summary>
        /// Reads past status labels in every shipped locale so a language change preserves their meaning.
        /// </summary>
        private static string[] GetHistoricalLabels(string key) =>
            AppPersonalizationConfig.SupportedLanguageCodes
                .Select(code => AppResources.ResourceManager.GetString(key, CultureInfo.GetCultureInfo(code)))
                .OfType<string>()
                .Distinct()
                .ToArray();

        /// <summary>
        /// Updates both the status label and its visibility together.
        /// </summary>
        private void RefreshCardStatus()
        {
            OnPropertyChanged(nameof(CardStatusText));
            OnPropertyChanged(nameof(HasCardStatus));
        }

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
            !AreResourceLabelsEquivalent(Subject, ActiveTransferLabel);

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


        // Notification actions

        /// <summary>
        /// Gets whether an available update still offers the Update now action.
        /// </summary>
        public bool OffersUpdateActions => _offersUpdateActions;

        /// <summary>
        /// Gets whether the user can hide the actions on a module update offer.
        /// </summary>
        public bool CanDeferUpdate => OffersUpdateActions &&
            string.Equals(SourceKind, "module", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Gets whether the user can remove the entry from the journal.
        /// </summary>
        public bool CanDismiss =>
            !(Category == AppNotificationCategory.Updates &&
                (string.Equals(SourceKind, "app", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(SourceKind, "engine", StringComparison.OrdinalIgnoreCase))) &&
            !((IsInProgress || AwaitingRestart) && IsComponentUpdate);

        /// <summary>
        /// Gets the localized label for the inline Update now action.
        /// </summary>
        public string UpdateNowText => L.Get(LocalizationKeys.Notifications_UpdateNow);

        /// <summary>
        /// Gets the localized label for the inline Update later action.
        /// </summary>
        public string UpdateLaterText => L.Get(LocalizationKeys.Notifications_UpdateLater);

        /// <summary>
        /// Refreshes localized card text and action captions after the UI culture changes.
        /// </summary>
        internal void RefreshLocalizedPresentation()
        {
            OnPropertyChanged(nameof(CardTitle));
            RefreshCardStatus();
            OnPropertyChanged(nameof(UpdateNowText));
            OnPropertyChanged(nameof(UpdateLaterText));
        }

        /// <summary>
        /// Gets whether an in-app toast for this notification should stay until the user dismisses it or acts.
        /// </summary>
        public bool SuppressToastAutoDismiss => _suppressToastAutoDismiss;

        /// <summary>
        /// Updates the action visibility and toast lifetime for an update offer.
        /// </summary>
        internal void SetUpdateAvailabilityPresentation(bool offersUpdateActions, bool suppressToastAutoDismiss)
        {
            if (SetField(ref _offersUpdateActions, offersUpdateActions, nameof(OffersUpdateActions)))
                OnPropertyChanged(nameof(CanDeferUpdate));
            SetField(ref _suppressToastAutoDismiss, suppressToastAutoDismiss, nameof(SuppressToastAutoDismiss));
        }


        // Severity styling

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


        // State updates

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

            // A finished transfer no longer has a current file, speed or known total.
            if (!isInProgress)
            {
                ResetDownloadTransferRow();
            }

            OnPropertyChanged(nameof(TimestampLabel));
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
        internal void ApplyDownloadTransferSample(DownloadProgress progress)
        {
            // Show the file currently being transferred when the sample names one.
            if (!string.IsNullOrWhiteSpace(progress.ActiveTransferName))
            {
                if (SetField(ref _activeTransferLabel, progress.ActiveTransferName.Trim(), nameof(ActiveTransferLabel)))
                {
                    OnPropertyChanged(nameof(HasActiveTransferLabel));
                }
            }

            // A positive total means the percentage is meaningful.
            _downloadHasKnownTotal = progress.TotalBytes > 0;

            var measuredSpeed = DownloadTransferSpeedEstimator.FormatSpeed(progress.BytesPerSecond);
            var speedText = measuredSpeed.Length > 0 ? measuredSpeed : "—";
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


        // Change notification

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


        // Resource label comparison

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
