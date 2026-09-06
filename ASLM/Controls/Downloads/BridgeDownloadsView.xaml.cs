// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Controls.Downloads
{
    /// <summary>
    /// Renders the catalog supplied by the active module downloads bridge category.
    /// </summary>
    public partial class BridgeDownloadsView : ContentView
    {
        /// <summary>
        /// Creates the dynamic module download surface defined by XAML.
        /// </summary>
        public BridgeDownloadsView()
        {
            InitializeComponent();
        }

        public event EventHandler<TextChangedEventArgs>? SearchTextChanged;
        public event EventHandler? InfoBlockWebViewHandlerChanged;
        public event EventHandler<WebNavigatingEventArgs>? InfoBlockWebViewNavigating;
        public event EventHandler<WebNavigatedEventArgs>? InfoBlockWebViewNavigated;
        public event EventHandler? InstallRequested;
        public event EventHandler? OpenRequested;
        public event EventHandler? RemoveRequested;
        public event EventHandler? VariantSelectorToggleRequested;

        public Entry SearchInput => SearchEntry;
        public Label ItemListEmptyTitle => ItemListEmptyTitleLabel;
        public Label DetailEmptyTitle => DetailEmptyTitleLabel;
        public Label VariantTitle => VariantSectionLabel;
        public Label ItemDetailsTitle => ItemDetailsLabel;
        public Label ItemFeaturesTitle => ItemFeaturesLabel;
        public Button InstallAction => InstallButton;
        public ImageButton OpenAction => OpenButton;
        public Button RemoveAction => RemoveButton;
        public WebView InfoBlockBrowser => InfoBlockWebView;

        /// <summary>
        /// Removes native insert/remove/reorder transitions from both download lists.
        /// Runs again after loading because the platform template may not exist at handler creation.
        /// </summary>
        private void OnDownloadListReady(object? sender, EventArgs e)
        {
#if WINDOWS
            if (sender is CollectionView collection &&
                collection.Handler?.PlatformView is Microsoft.UI.Xaml.DependencyObject platformView)
            {
                DisableItemTransitions(platformView);
            }
#endif
        }

        /// <summary>
        /// Requests opening or closing the variant popup and positions it after bindings update.
        /// </summary>
        private void OnVariantSelectorTapped(object? sender, TappedEventArgs e)
        {
            VariantSelectorToggleRequested?.Invoke(this, EventArgs.Empty);
            Dispatcher.Dispatch(UpdateVariantMenuPosition);
        }

        /// <summary>
        /// Closes the variant popup when the user clicks outside it.
        /// </summary>
        private void OnVariantSelectorBackdropTapped(object? sender, TappedEventArgs e) =>
            VariantSelectorToggleRequested?.Invoke(this, EventArgs.Empty);

        private void OnVariantSelectorAnchorSizeChanged(object? sender, EventArgs e) => UpdateVariantMenuPosition();

        private void OnVariantOverlayLayerSizeChanged(object? sender, EventArgs e) => UpdateVariantMenuPosition();

        /// <summary>
        /// Places the popup over the detail column without adding it to the card's measured height.
        /// </summary>
        private void UpdateVariantMenuPosition()
        {
            if (!VariantOverlayLayer.IsVisible ||
                VariantOverlayLayer.Width <= 0 ||
                VariantOverlayLayer.Height <= 0 ||
                VariantSelectorAnchor.Width <= 0)
            {
                return;
            }

            const double edge = 4;
            const double gap = 4;
            const double maximumHeight = 280;

            var anchorPosition = GetPositionRelativeTo(VariantSelectorAnchor, VariantOverlayLayer);
            var popupWidth = Math.Max(0, Math.Min(VariantSelectorAnchor.Width, VariantOverlayLayer.Width - edge * 2));
            var popupX = Math.Clamp(anchorPosition.X, edge, Math.Max(edge, VariantOverlayLayer.Width - popupWidth - edge));
            var belowY = anchorPosition.Y + VariantSelectorAnchor.Height + gap;
            var availableBelow = Math.Max(0, VariantOverlayLayer.Height - belowY - edge);
            var availableAbove = Math.Max(0, anchorPosition.Y - gap - edge);
            var measuredHeight = VariantMenuPopup.Measure(popupWidth, maximumHeight).Height;
            var desiredHeight = Math.Clamp(measuredHeight, 48, maximumHeight);
            var openAbove = availableBelow < desiredHeight && availableAbove > availableBelow;
            var availableHeight = openAbove ? availableAbove : availableBelow;
            var popupHeight = Math.Min(desiredHeight, Math.Max(48, availableHeight));
            var popupY = openAbove
                ? Math.Max(edge, anchorPosition.Y - gap - popupHeight)
                : belowY;

            AbsoluteLayout.SetLayoutBounds(
                VariantMenuPopup,
                new Rect(popupX, popupY, popupWidth, popupHeight));
        }

        private static Point GetPositionRelativeTo(VisualElement element, VisualElement relativeTo)
        {
            var elementPosition = GetPositionInVisualTree(element);
            var relativePosition = GetPositionInVisualTree(relativeTo);
            return new Point(
                elementPosition.X - relativePosition.X,
                elementPosition.Y - relativePosition.Y);
        }

        private static Point GetPositionInVisualTree(VisualElement element)
        {
            var x = 0d;
            var y = 0d;
            Element? current = element;

            while (current is VisualElement visual)
            {
                x += visual.X + visual.TranslationX;
                y += visual.Y + visual.TranslationY;

                if (visual.Parent is ScrollView scrollView)
                {
                    x -= scrollView.ScrollX;
                    y -= scrollView.ScrollY;
                }

                current = visual.Parent;
            }

            return new Point(x, y);
        }

#if WINDOWS
        private static void DisableItemTransitions(Microsoft.UI.Xaml.DependencyObject element)
        {
            if (element is Microsoft.UI.Xaml.Controls.ListViewBase list)
                list.ItemContainerTransitions = new Microsoft.UI.Xaml.Media.Animation.TransitionCollection();

            var childCount = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(element);
            for (var index = 0; index < childCount; index++)
                DisableItemTransitions(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(element, index));
        }
#endif

        /// <summary>
        /// Forwards provider search text changes to the page coordinator.
        /// </summary>
        private void OnSearchTextChanged(object? sender, TextChangedEventArgs e) => SearchTextChanged?.Invoke(sender, e);

        /// <summary>
        /// Forwards rendered information handler initialization to the page coordinator.
        /// </summary>
        private void OnInfoBlockWebViewHandlerChanged(object? sender, EventArgs e) => InfoBlockWebViewHandlerChanged?.Invoke(sender, e);

        /// <summary>
        /// Forwards rendered information navigation requests to the page coordinator.
        /// </summary>
        private void OnInfoBlockWebViewNavigating(object? sender, WebNavigatingEventArgs e) => InfoBlockWebViewNavigating?.Invoke(sender, e);

        /// <summary>
        /// Forwards completed rendered information navigation to the page coordinator.
        /// </summary>
        private void OnInfoBlockWebViewNavigated(object? sender, WebNavigatedEventArgs e) => InfoBlockWebViewNavigated?.Invoke(sender, e);

        /// <summary>
        /// Forwards an install request to the page coordinator.
        /// </summary>
        private void OnInstallClicked(object? sender, EventArgs e) => InstallRequested?.Invoke(sender, e);

        /// <summary>
        /// Forwards an open request to the page coordinator.
        /// </summary>
        private void OnOpenClicked(object? sender, EventArgs e) => OpenRequested?.Invoke(sender, e);

        /// <summary>
        /// Forwards a remove request to the page coordinator.
        /// </summary>
        private void OnDeleteClicked(object? sender, EventArgs e) => RemoveRequested?.Invoke(sender, e);
    }
}
