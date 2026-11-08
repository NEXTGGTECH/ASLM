// Copyright NEXTGGTECH. Apache License 2.0.

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ASLM.Localization;
using ASLM.Models;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace ASLM.Pages
{
    /// <summary>
    /// Displays internal ASLM notifications in a compact downloads-style popover.
    /// </summary>
    public partial class NotificationsView : ContentView, INotifyPropertyChanged, ILocalizable
    {
        // Fields and constants

        /// <summary>
        /// Popover width used for edge clamping. Keep in sync with the <c>NotificationsPopup</c> WidthRequest in
        /// NotificationsView.xaml.
        /// </summary>
        private const double PopupWidth = 360;

        /// <summary>
        /// Popover height used for vertical centering and edge clamping. Keep in sync with the
        /// <c>NotificationsPopup</c> HeightRequest in NotificationsView.xaml.
        /// </summary>
        private const double PopupHeight = 480;

        /// <summary>
        /// Horizontal gap between the right edge of the sidebar button and the popover.
        /// </summary>
        private const double PopupGap = 21;

        /// <summary>
        /// Minimum distance kept between the popover and the edges of the host.
        /// </summary>
        private const double ScreenPadding = 14;

        /// <summary>
        /// Service that owns the notification list shown by this view.
        /// </summary>
        private readonly NotificationCenter _notifications;

        /// <summary>
        /// Localization service used to re-apply texts when the language changes.
        /// </summary>
        private readonly AppLocalizationService _localization;

        /// <summary>
        /// Bounds of the sidebar button the popover was last opened at, reused when the host is resized.
        /// </summary>
        private Rect _lastAnchorBounds;

        /// <summary>
        /// Whether the popover was positioned by <see cref="OpenAtAsync"/> and must follow host size changes.
        /// </summary>
        private bool _popupPositioningActive;

        /// <summary>
        /// Backing field of <see cref="DismissIconSource"/>.
        /// </summary>
        private ImageSource _dismissIconSource = null!;


        // Events

        /// <summary>
        /// Raised when the user asks to close the notifications popover.
        /// </summary>
        public event EventHandler? CloseRequested;

        /// <summary>
        /// Raised when a bindable property on this view changes.
        /// </summary>
        public new event PropertyChangedEventHandler? PropertyChanged;


        // Initialization

        /// <summary>
        /// Creates the notifications popover, binds it to itself and hooks localization updates.
        /// </summary>
        /// <param name="notifications">Service that owns the notification list.</param>
        /// <param name="localization">Localization service whose language changes re-apply the texts.</param>
        public NotificationsView(NotificationCenter notifications, AppLocalizationService localization)
        {
            _notifications = notifications;
            _localization = localization;

            InitializeComponent();
            BindingContext = this;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            LocalizableAttach.Hook(this, _localization, this);
            RefreshDismissIconChrome();
        }


        // Localization

        /// <summary>
        /// Applies localized strings to header, empty state, and visible notification rows.
        /// </summary>
        public void ApplyLocalization()
        {
            TitleLabel.Text = L.Get(LocalizationKeys.Notifications_Title);
            ClearAllButton.Text = L.Get(LocalizationKeys.Notifications_ClearAll);
            EmptyTitleLabel.Text = L.Get(LocalizationKeys.Notifications_EmptyTitle);
            EmptyMessageLabel.Text = L.Get(LocalizationKeys.Notifications_EmptyMessage);
            foreach (var notification in VisibleNotifications)
            {
                notification.RefreshLocalizedPresentation();
            }

            OnPropertyChanged(nameof(SummaryText));
            OnPropertyChanged(nameof(SectionTitle));
        }


        // Bound properties

        /// <summary>
        /// Gets the notifications currently visible in the popover.
        /// Bound directly to the service collection so CollectionView sees only granular
        /// add/remove/move events and does not play item-appear animations on every update.
        /// </summary>
        public ReadOnlyObservableCollection<AppNotification> VisibleNotifications => _notifications.Notifications;

        /// <summary>
        /// Gets the compact total count label shown in the footer.
        /// </summary>
        public string SummaryText => _notifications.Notifications.Count == 0
            ? L.Get(LocalizationKeys.Notifications_EmptyTitle)
            : L.Get(LocalizationKeys.Notifications_SummaryFormat, _notifications.Notifications.Count);

        /// <summary>
        /// Gets the small section label above the list.
        /// </summary>
        public string SectionTitle => L.Get(LocalizationKeys.Notifications_SectionRecent);

        /// <summary>
        /// Gets whether the list has any notifications to show; otherwise the empty state replaces it.
        /// </summary>
        public bool HasVisibleNotifications => _notifications.Notifications.Count > 0;

        /// <summary>
        /// Gets whether the empty-state placeholder should be shown instead of the list.
        /// </summary>
        public bool IsEmptyVisible => !HasVisibleNotifications;

        /// <summary>
        /// Gets whether there is anything to clear, which enables the Clear all button.
        /// </summary>
        public bool HasAnyNotifications => _notifications.Notifications.Count > 0;

        /// <summary>
        /// Gets the theme-tinted dismiss glyph for notification rows.
        /// </summary>
        public ImageSource DismissIconSource
        {
            get => _dismissIconSource;
            private set
            {
                if (_dismissIconSource == value)
                {
                    return;
                }

                _dismissIconSource = value;
                OnPropertyChanged();
            }
        }


        // Public API

        /// <summary>
        /// Positions the popover next to the notifications sidebar button and starts following host resizes.
        /// </summary>
        /// <param name="anchorBounds">Bounds of the sidebar button the popover is attached to.</param>
        /// <param name="hostWidth">Width of the overlay host, or 0 when unknown.</param>
        /// <param name="hostHeight">Height of the overlay host, or 0 when unknown.</param>
        public Task OpenAtAsync(Rect anchorBounds, double hostWidth, double hostHeight)
        {
            _lastAnchorBounds = anchorBounds;
            _popupPositioningActive = true;
            PositionPopup(anchorBounds, hostWidth, hostHeight);
            RefreshVisibleNotifications();
            return Task.CompletedTask;
        }

        /// <summary>
        /// Re-raises the view-state properties so the popover reflects the current notifications
        /// before the shell shows it.
        /// </summary>
        public Task RefreshAsync()
        {
            RefreshVisibleNotifications();
            return Task.CompletedTask;
        }


        // Lifecycle

        /// <summary>
        /// Subscribes to notification service changes once the view enters the visual tree.
        /// </summary>
        private void OnLoaded(object? sender, EventArgs e)
        {
            // Subscribe to theme changes; the unsubscribe-first pattern keeps repeated loads from stacking handlers.
            ThemeService.PaletteApplied -= OnPaletteAppliedForDismissIcon;
            ThemeService.PaletteApplied += OnPaletteAppliedForDismissIcon;
            if (Application.Current is { } app)
            {
                app.RequestedThemeChanged -= OnApplicationRequestedThemeChanged;
                app.RequestedThemeChanged += OnApplicationRequestedThemeChanged;
            }

            // Tint the icon for the current palette, then follow notification and host size changes.
            RefreshDismissIconChrome();
            _notifications.NotificationsChanged -= OnNotificationsChanged;
            _notifications.NotificationsChanged += OnNotificationsChanged;
            SizeChanged -= OnHostSizeChanged;
            SizeChanged += OnHostSizeChanged;
            RefreshVisibleNotifications();
        }

        /// <summary>
        /// Unsubscribes from service changes when the popover is removed.
        /// </summary>
        private void OnUnloaded(object? sender, EventArgs e)
        {
            ThemeService.PaletteApplied -= OnPaletteAppliedForDismissIcon;
            if (Application.Current is { } app)
            {
                app.RequestedThemeChanged -= OnApplicationRequestedThemeChanged;
            }

            _notifications.NotificationsChanged -= OnNotificationsChanged;
            SizeChanged -= OnHostSizeChanged;
            _popupPositioningActive = false;
        }

        /// <summary>
        /// Re-tints the dismiss icon on the main thread after a custom palette is applied.
        /// </summary>
        private void OnPaletteAppliedForDismissIcon()
        {
            MainThread.BeginInvokeOnMainThread(RefreshDismissIconChrome);
        }

        /// <summary>
        /// Re-tints the dismiss icon on the main thread when the application theme changes.
        /// </summary>
        private void OnApplicationRequestedThemeChanged(object? sender, AppThemeChangedEventArgs e)
        {
            MainThread.BeginInvokeOnMainThread(RefreshDismissIconChrome);
        }

        /// <summary>
        /// Recomputes the tinted dismiss icon for the current palette.
        /// </summary>
        private void RefreshDismissIconChrome()
        {
            var iconTint = IconTintHelper.ResolvePaletteColor("LabelPrimary");
            DismissIconSource = PackagedIconTintCache.Get("icon_delete.png", iconTint);
        }


        // Positioning

        /// <summary>
        /// Re-clamps the popover when the overlay host is resized so it stays on screen.
        /// </summary>
        private void OnHostSizeChanged(object? sender, EventArgs e)
        {
            if (!_popupPositioningActive)
            {
                return;
            }

            var hostW = Width > 0 ? Width : 0;
            var hostH = Height > 0 ? Height : 0;
            PositionPopup(_lastAnchorBounds, hostW, hostH);
        }

        /// <summary>
        /// Applies popover margins while keeping it inside the host bounds.
        /// </summary>
        /// <param name="anchorBounds">
        /// Bounds of the sidebar button; the popover opens to its right, vertically centered on it.
        /// </param>
        /// <param name="hostWidth">Width of the host; 0 skips the horizontal clamp.</param>
        /// <param name="hostHeight">Height of the host; 0 skips the vertical clamp.</param>
        private void PositionPopup(Rect anchorBounds, double hostWidth, double hostHeight)
        {
            // Preferred spot: right of the anchor, vertically centered on it.
            var x = anchorBounds.Right + PopupGap;
            var y = anchorBounds.Top - (PopupHeight / 2) + (anchorBounds.Height / 2);

            // Pull the popover back inside the host (when its size is known), then keep it off the top and left edges.
            if (hostWidth > 0)
            {
                x = Math.Min(x, Math.Max(ScreenPadding, hostWidth - PopupWidth - ScreenPadding));
            }

            if (hostHeight > 0)
            {
                y = Math.Min(y, Math.Max(ScreenPadding, hostHeight - PopupHeight - ScreenPadding));
            }

            x = Math.Max(ScreenPadding, x);
            y = Math.Max(ScreenPadding, y);

            NotificationsPopup.Margin = new Thickness(x, y, 0, 0);
        }


        // Close handling

        /// <summary>
        /// Closes the popover when the transparent background is tapped.
        /// </summary>
        private void OnBackgroundTapped(object? sender, EventArgs e)
        {
            RequestClose();
        }

        /// <summary>
        /// Swallows taps inside the popover so the backdrop does not close it.
        /// </summary>
        private void OnDialogTapped(object? sender, EventArgs e)
        {
            // Intentionally left blank so dialog taps stay inside the popover.
        }

        /// <summary>
        /// Closes the popover from the header close button.
        /// </summary>
        private void OnCloseClicked(object? sender, EventArgs e)
        {
            RequestClose();
        }

        /// <summary>
        /// Raises the close request event for the shell.
        /// </summary>
        private void RequestClose()
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }


        // Actions

        /// <summary>
        /// Clears all notifications when the Clear all button is pressed.
        /// </summary>
        private void OnClearAllClicked(object? sender, EventArgs e)
        {
            _notifications.DismissAll();
        }

        /// <summary>
        /// Dismisses one notification from its row action (Border + TapGestureRecognizer pattern from HomeView).
        /// </summary>
        private void OnDismissClicked(object? sender, EventArgs e)
        {
            for (var element = sender as Element; element != null; element = element.Parent)
            {
                if (element.BindingContext is AppNotification notification)
                {
                    _notifications.Dismiss(notification);
                    return;
                }
            }
        }

        /// <summary>
        /// Forwards Update now from an update-available row to the shell notification action pipeline.
        /// </summary>
        private void OnUpdateNotificationNowClicked(object? sender, EventArgs e)
        {
            if (TryFindNotificationBindingContext(sender, out var notification))
            {
                _notifications.RequestUpdateNotificationAction(notification, updateNow: true);
            }
        }

        /// <summary>
        /// Hides inline update actions after the user defers from the list.
        /// </summary>
        private void OnUpdateNotificationLaterClicked(object? sender, EventArgs e)
        {
            if (TryFindNotificationBindingContext(sender, out var notification))
            {
                _notifications.RequestUpdateNotificationAction(notification, updateNow: false);
            }
        }


        // Helpers

        /// <summary>
        /// Walks the visual tree from the event sender to find the bound notification.
        /// </summary>
        private static bool TryFindNotificationBindingContext(object? sender, out AppNotification notification)
        {
            for (var element = sender as Element; element != null; element = element.Parent)
            {
                if (element.BindingContext is AppNotification found)
                {
                    notification = found;
                    return true;
                }
            }

            notification = default!;
            return false;
        }


        // Change handling

        /// <summary>
        /// Refreshes the view-state properties when the notification service reports a change.
        /// </summary>
        private void OnNotificationsChanged(object? sender, EventArgs e)
        {
            RefreshVisibleNotifications();
        }

        /// <summary>
        /// Raises view-state properties when the service collection changes.
        /// </summary>
        private void RefreshVisibleNotifications()
        {
            RaiseViewStateProperties();
        }

        /// <summary>
        /// Raises all view-level properties affected by service changes.
        /// </summary>
        private void RaiseViewStateProperties()
        {
            OnPropertyChanged(nameof(SummaryText));
            OnPropertyChanged(nameof(SectionTitle));
            OnPropertyChanged(nameof(HasVisibleNotifications));
            OnPropertyChanged(nameof(IsEmptyVisible));
            OnPropertyChanged(nameof(HasAnyNotifications));
        }


        // Property notifications

        /// <summary>
        /// Raises the view property changed event.
        /// </summary>
        protected new void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
