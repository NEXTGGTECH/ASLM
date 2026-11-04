// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Controls.Downloads
{
    /// <summary>
    /// Renders the catalog supplied by the active module downloads bridge category.
    /// </summary>
    public partial class BridgeDownloadsView : ContentView
    {
        // Initialization

        /// <summary>
        /// Creates the dynamic module download surface defined by XAML.
        /// </summary>
        public BridgeDownloadsView()
        {
            InitializeComponent();
        }


        // Events

        /// <summary>
        /// Raised when the text of the search box changes.
        /// </summary>
        public event EventHandler<TextChangedEventArgs>? SearchTextChanged;

        /// <summary>
        /// Raised when the native handler of the information web view is created or replaced.
        /// </summary>
        public event EventHandler? InfoBlockWebViewHandlerChanged;

        /// <summary>
        /// Raised when the information web view starts navigating, so the coordinator can allow or cancel it.
        /// </summary>
        public event EventHandler<WebNavigatingEventArgs>? InfoBlockWebViewNavigating;

        /// <summary>
        /// Raised when the information web view finished navigating.
        /// </summary>
        public event EventHandler<WebNavigatedEventArgs>? InfoBlockWebViewNavigated;

        /// <summary>
        /// Raised when the user presses the download (install) button.
        /// </summary>
        public event EventHandler? InstallRequested;

        /// <summary>
        /// Raised when the user presses the link button that opens the selected variant's homepage.
        /// </summary>
        public event EventHandler? OpenRequested;

        /// <summary>
        /// Raised when the user presses the remove button.
        /// </summary>
        public event EventHandler? RemoveRequested;

        /// <summary>
        /// Raised when the user presses the cancel button of a running download.
        /// </summary>
        public event EventHandler? CancelRequested;

        /// <summary>
        /// Raised when the variant popup must be opened or closed.
        /// </summary>
        public event EventHandler? VariantSelectorToggleRequested;


        // Exposed controls

        /// <summary>
        /// Search box above the item list.
        /// </summary>
        public Entry SearchInput => SearchEntry;

        /// <summary>
        /// Placeholder label shown when the item list is empty.
        /// </summary>
        public Label ItemListEmptyTitle => ItemListEmptyTitleLabel;

        /// <summary>
        /// Placeholder label shown when no item is selected for the detail column.
        /// </summary>
        public Label DetailEmptyTitle => DetailEmptyTitleLabel;

        /// <summary>
        /// Heading of the variant section in the detail column.
        /// </summary>
        public Label VariantTitle => VariantSectionLabel;

        /// <summary>
        /// Heading of the item description section.
        /// </summary>
        public Label ItemDetailsTitle => ItemDetailsLabel;

        /// <summary>
        /// Heading of the item feature list section.
        /// </summary>
        public Label ItemFeaturesTitle => ItemFeaturesLabel;

        /// <summary>
        /// Button that downloads and installs the selected variant.
        /// </summary>
        public Button InstallAction => InstallButton;

        /// <summary>
        /// Link button next to the detail title that opens the selected variant's homepage.
        /// </summary>
        public ImageButton OpenAction => OpenButton;

        /// <summary>
        /// Button that removes the installed variant.
        /// </summary>
        public Button RemoveAction => RemoveButton;

        /// <summary>
        /// Button that cancels a running download or installation.
        /// </summary>
        public Button CancelAction => CancelButton;

        /// <summary>
        /// Web view that renders the item's information block.
        /// </summary>
        public WebView InfoBlockBrowser => InfoBlockWebView;


        // Download list

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


        // Variant popup

        /// <summary>
        /// Requests opening or closing the variant popup and positions it after bindings update.
        /// </summary>
        private void OnVariantSelectorTapped(object? sender, TappedEventArgs e)
        {
            // The coordinator flips the popup visibility; position it once that binding update has run.
            VariantSelectorToggleRequested?.Invoke(this, EventArgs.Empty);
            Dispatcher.Dispatch(UpdateVariantMenuPosition);
        }

        /// <summary>
        /// Closes the variant popup when the user clicks outside it.
        /// </summary>
        private void OnVariantSelectorBackdropTapped(object? sender, TappedEventArgs e) =>
            VariantSelectorToggleRequested?.Invoke(this, EventArgs.Empty);

        /// <summary>
        /// Repositions the popup when the selector that anchors it changes size.
        /// </summary>
        private void OnVariantSelectorAnchorSizeChanged(object? sender, EventArgs e) => UpdateVariantMenuPosition();

        /// <summary>
        /// Repositions the popup when the overlay that hosts it changes size.
        /// </summary>
        private void OnVariantOverlayLayerSizeChanged(object? sender, EventArgs e) => UpdateVariantMenuPosition();

        /// <summary>
        /// Places the popup over the detail column without adding it to the card's measured height.
        /// </summary>
        private void UpdateVariantMenuPosition()
        {
            // Nothing to position until the popup is shown and both the overlay and its anchor have been laid out.
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

            // The popup is as wide as its anchor and stays horizontally inside the overlay.
            var anchorPosition = GetPositionRelativeTo(VariantSelectorAnchor, VariantOverlayLayer);
            var popupWidth = Math.Max(0, Math.Min(VariantSelectorAnchor.Width, VariantOverlayLayer.Width - edge * 2));
            var popupX = Math.Clamp(anchorPosition.X, edge, Math.Max(edge, VariantOverlayLayer.Width - popupWidth - edge));

            // Measure the free space around the anchor and the height the content wants.
            var belowY = anchorPosition.Y + VariantSelectorAnchor.Height + gap;
            var availableBelow = Math.Max(0, VariantOverlayLayer.Height - belowY - edge);
            var availableAbove = Math.Max(0, anchorPosition.Y - gap - edge);
            var measuredHeight = VariantMenuPopup.Measure(popupWidth, maximumHeight).Height;
            var desiredHeight = Math.Clamp(measuredHeight, 48, maximumHeight);

            // Open below the anchor, or above it when the content does not fit below and there is more room above.
            var openAbove = availableBelow < desiredHeight && availableAbove > availableBelow;
            var availableHeight = openAbove ? availableAbove : availableBelow;
            var popupHeight = Math.Min(desiredHeight, Math.Max(48, availableHeight));
            var popupY = openAbove
                ? Math.Max(edge, anchorPosition.Y - gap - popupHeight)
                : belowY;

            // Absolute bounds keep the popup out of the card's measured height.
            AbsoluteLayout.SetLayoutBounds(
                VariantMenuPopup,
                new Rect(popupX, popupY, popupWidth, popupHeight));
        }

        /// <summary>
        /// Returns the position of <paramref name="element"/> inside <paramref name="relativeTo"/>.
        /// </summary>
        private static Point GetPositionRelativeTo(VisualElement element, VisualElement relativeTo)
        {
            var elementPosition = GetPositionInVisualTree(element);
            var relativePosition = GetPositionInVisualTree(relativeTo);
            return new Point(
                elementPosition.X - relativePosition.X,
                elementPosition.Y - relativePosition.Y);
        }

        /// <summary>
        /// Sums the offsets and translations of an element and its ancestors, minus the scroll offsets they sit in.
        /// </summary>
        private static Point GetPositionInVisualTree(VisualElement element)
        {
            var x = 0d;
            var y = 0d;
            Element? current = element;

            // Walk up to the root; the scrolled-out distance of each scroll view is not part of the visible position.
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
        // Native list transitions

        /// <summary>
        /// Clears the item container transitions of every list in the native visual subtree.
        /// </summary>
        private static void DisableItemTransitions(Microsoft.UI.Xaml.DependencyObject element)
        {
            if (element is Microsoft.UI.Xaml.Controls.ListViewBase list)
                list.ItemContainerTransitions = new Microsoft.UI.Xaml.Media.Animation.TransitionCollection();

            var childCount = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(element);
            for (var index = 0; index < childCount; index++)
                DisableItemTransitions(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(element, index));
        }
#endif


        // Event forwarding

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

        /// <summary>
        /// Forwards a cancel request to the page coordinator.
        /// </summary>
        private void OnCancelClicked(object? sender, EventArgs e) => CancelRequested?.Invoke(sender, e);
    }
}
