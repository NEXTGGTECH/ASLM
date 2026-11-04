// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Localization;
using ASLM.Models;
using Microsoft.Maui.Layouts;

namespace ASLM.Controls.Dialogs
{
    /// <summary>
    /// Presents queued requests in one bounded, scrollable modal surface per application window.
    /// </summary>
    public partial class DialogHostView : ContentView, ILocalizable
    {
        // Fields

        /// <summary>
        /// Field views of the dialog that is currently shown, in display order.
        /// </summary>
        private readonly List<DialogFieldView> _fields = [];

        /// <summary>
        /// Buttons of the current dialog keyed by the action they trigger.
        /// </summary>
        private readonly Dictionary<DialogAction, Button> _buttons = [];

        /// <summary>
        /// Session being presented, or null while the host is idle.
        /// </summary>
        private DialogSession? _active;

        /// <summary>
        /// Dialog service that this host's page is registered with, or null before <see cref="Initialize"/>.
        /// </summary>
        private DialogService? _service;

        /// <summary>
        /// Page that owns this host and is re-attached to the service whenever it loads.
        /// </summary>
        private Page? _page;

        /// <summary>
        /// Page content behind the dialog; its native input is blocked while a dialog is open.
        /// </summary>
        private View? _background;

        /// <summary>
        /// Element that receives keyboard focus first once it is loaded, or null when there is none.
        /// </summary>
        private View? _initialFocusTarget;

        /// <summary>
        /// Whether a resize is already scheduled on the dispatcher, so geometry bursts cause only one measurement.
        /// </summary>
        private bool _resizeQueued;


        // State and events

        /// <summary>
        /// Whether a dialog is currently being presented.
        /// </summary>
        internal bool IsOpen => _active != null;

        /// <summary>
        /// Raised after a dialog was closed and its controls were released, so waiting notices can be shown again.
        /// </summary>
        internal event EventHandler? Closed;


        // Initialization

        /// <summary>
        /// Creates the shared surface and connects its layout, keyboard, and page-lifetime handlers.
        /// </summary>
        public DialogHostView()
        {
            InitializeComponent();
            SizeChanged += OnGeometryChanged;
            Body.SizeChanged += OnGeometryChanged;
            HandlerChanged += (_, _) => ConnectKeyboard();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        /// <summary>
        /// Registers this page's presenter and connects localization to the view lifetime.
        /// </summary>
        /// <param name="page">Page that owns the host; dialogs queued for it are shown here.</param>
        /// <param name="background">Page content behind the dialog whose input is blocked while it is open.</param>
        /// <param name="services">Provider used to resolve the dialog and localization services.</param>
        internal void Initialize(Page page, View background, IServiceProvider services)
        {
            _page = page;
            _background = background;
            _service = services.GetRequiredService<DialogService>();
            _service.Attach(page, this);
            LocalizableAttach.Hook(this, services.GetRequiredService<AppLocalizationService>(), this);
        }


        // Page lifetime

        /// <summary>
        /// Attaches the presenter when its owning page becomes available again.
        /// </summary>
        private void OnLoaded(object? sender, EventArgs e)
        {
            if (_page != null) _service?.Attach(_page, this);
            ConnectKeyboard();
        }

        /// <summary>
        /// Cancels the page's dialogs and releases native input hooks when it leaves the window.
        /// </summary>
        private void OnUnloaded(object? sender, EventArgs e)
        {
            if (_page != null) _service?.Detach(_page, this);
            _active?.Cancel();
            UpdateModalPlatform(false);
            DisconnectKeyboard();
        }


        // Presentation

        /// <summary>
        /// Presents one session and releases its controls and modal input scope on every exit path.
        /// </summary>
        internal async Task<DialogResult> PresentAsync(DialogSession session)
        {
            _active = session;
            try
            {
                // Block input at the native surface, not through inherited MAUI IsEnabled changes.
                // The latter revisits the entire settings tree and restyles every palette editor.
                Render(session);
                return await session.Completion;
            }
            finally
            {
                // Detach value notifications before dropping controls that can contain secret input.
                session.Changed -= OnSessionChanged;
                if (_initialFocusTarget != null) _initialFocusTarget.Loaded -= OnInitialInputLoaded;
                _initialFocusTarget = null;
                foreach (var field in _fields) field.Dispose();
                _fields.Clear();
                Fields.Clear();
                Buttons.Clear();
                _buttons.Clear();

                // Hide the surface, let input pass through again, and leave the modal input scope.
                DialogLayer.IsVisible = false;
                InputTransparent = true;
                _active = null;
                UpdateModalPlatform(false);
                Closed?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Builds only the current request's fields and actions, then activates its input scope.
        /// </summary>
        private void Render(DialogSession session)
        {
            // One view per field, created in request order.
            session.Changed += OnSessionChanged;
            foreach (var field in session.Request.Fields)
            {
                var view = new DialogFieldView(field, InvokeDefaultAsync);
                _fields.Add(view);
                Fields.Add(view);
            }

            // Action identity stays in the session; labels and styles remain presentation details.
            foreach (var action in session.Request.Actions)
            {
                var button = new Button { Margin = new Thickness(4, 4, 0, 0) };
                button.SetDynamicResource(StyleProperty, action.Style switch
                {
                    DialogActionStyle.Primary => "DialogPrimaryButtonStyle",
                    DialogActionStyle.Danger => "DialogDangerButtonStyle",
                    _ => "DialogButtonStyle"
                });
                button.Clicked += async (_, _) => await session.InvokeAsync(action);
                if (session.Request.ActionsAsList)
                {
                    FlexLayout.SetBasis(button, new FlexBasis(1, true));
                    button.MaximumWidthRequest = double.PositiveInfinity;
                }

                _buttons.Add(action, button);
                Buttons.Add(button);
            }

            // Fill in the localized texts, then choose where keyboard focus starts.
            ApplyLocalization();
            _initialFocusTarget = _fields.FirstOrDefault() as View ?? GetInitialButton();
            if (_initialFocusTarget != null) _initialFocusTarget.Loaded += OnInitialInputLoaded;

            // Show the surface, block the background, size the card and reset scrolling to the top.
            InputTransparent = false;
            DialogLayer.IsVisible = true;
            UpdateModalPlatform(true);
            ResizeDialog();
            _ = Scroller.ScrollToAsync(0, 0, false);
            FocusInitialInput();

            // Already loaded handlers can accept focus now; newly attached inputs also notify Loaded.
            Dispatcher.Dispatch(() => { if (_active == session) FocusInitialInput(); });
        }


        // Initial focus

        /// <summary>
        /// Resolves the default visible action, falling back to the first available dialog button.
        /// </summary>
        private Button? GetInitialButton() =>
            _buttons.FirstOrDefault(b => b.Key.IsDefault && b.Value.IsVisible).Value ??
            _buttons.Values.FirstOrDefault(b => b.IsVisible);

        /// <summary>
        /// Retries initial focus after newly created input handlers have joined the native visual tree.
        /// </summary>
        private void OnInitialInputLoaded(object? sender, EventArgs e)
        {
            if (_initialFocusTarget != null) _initialFocusTarget.Loaded -= OnInitialInputLoaded;
            var session = _active;
            Dispatcher.Dispatch(() => { if (session != null && _active == session) FocusInitialInput(); });
        }

        /// <summary>
        /// Places keyboard focus in the first field or the primary visible action.
        /// </summary>
        private void FocusInitialInput()
        {
            if (_active is not { IsBusy: false }) return;
            if (_fields.Count > 0) _fields[0].FocusInput();
            else GetInitialButton()?.Focus();
        }


        // Actions and dismissal

        /// <summary>
        /// Invokes the visible default action when an input or the platform handles Enter.
        /// </summary>
        internal Task InvokeDefaultAsync()
        {
            var session = _active;
            var action = session?.Request.Actions.FirstOrDefault(a => a.IsDefault && a.IsVisible?.Invoke() != false);
            return action != null ? session!.InvokeAsync(action) : Task.CompletedTask;
        }

        /// <summary>
        /// Requests dismissal; the session rejects it for button-only or currently busy dialogs.
        /// </summary>
        internal void Dismiss() => _active?.Dismiss();

        /// <summary>
        /// Handles both the close button and the backdrop without bypassing dismissal policy.
        /// </summary>
        private void OnDismissClicked(object? sender, EventArgs e) => Dismiss();


        // Session state

        /// <summary>
        /// Refreshes action state and validation without rebuilding or relocalizing form inputs.
        /// </summary>
        private void OnSessionChanged(object? sender, EventArgs e)
        {
            // Ignore notifications from sessions that are no longer the one on screen.
            if (_active == null || !ReferenceEquals(sender, _active) || _active.Completion.IsCompleted) return;
            RefreshSessionState();
            if (_active is { IsBusy: false } session && session.Errors.Count > 0)
            {
                var field = _fields.FirstOrDefault(f => session.Errors.ContainsKey(f.Field.Id));

                // Reveal the first invalid field even when the form is taller than its viewport.
                if (field != null)
                    Dispatcher.Dispatch(async () =>
                    {
                        if (_active != session) return;
                        await Scroller.ScrollToAsync(field, ScrollToPosition.MakeVisible, false);
                        field.FocusInput();
                    });
            }
        }

        /// <summary>
        /// Resolves localized text when the dialog opens or the application language changes.
        /// </summary>
        public void ApplyLocalization()
        {
            // Nothing to translate while the host is idle; the next dialog resolves its own texts.
            if (_active is not { } session) return;

            // Header and message texts, close button accessibility text, then every field and action caption.
            TitleLabel.Text = session.Request.Title.Resolve();
            MessageLabel.Text = session.Request.Message?.Resolve();
            MessageLabel.IsVisible = !string.IsNullOrEmpty(MessageLabel.Text);
            Fields.IsVisible = _fields.Count > 0;
            CloseButton.IsVisible = session.Request.IsDismissible;
            SemanticProperties.SetDescription(CloseButton, L.Get(LocalizationKeys.Common_Close));
            foreach (var field in _fields) field.ApplyLocalization();
            foreach (var (action, button) in _buttons) button.Text = action.Text.Resolve();
            RefreshSessionState();
        }

        /// <summary>
        /// Updates validation and busy state only within the small active dialog tree.
        /// </summary>
        private void RefreshSessionState()
        {
            if (_active is not { } session) return;

            // Lock the inputs while an action runs and show the action's own error, if any.
            Fields.IsEnabled = !session.IsBusy;
            CloseButton.IsEnabled = !session.IsBusy;
            ActionErrorLabel.Text = session.ActionError;
            ActionErrorLabel.IsVisible = !string.IsNullOrEmpty(session.ActionError);

            // Per-field messages: a validation error wins over the field's own presentation error.
            foreach (var field in _fields)
                field.ShowError(session.Errors.GetValueOrDefault(field.Field.Id) ?? field.Field.PresentationError?.Resolve());

            // Action buttons follow their visibility and enabled callbacks, and stay disabled while busy.
            foreach (var (action, button) in _buttons)
            {
                button.IsVisible = action.IsVisible?.Invoke() != false;
                button.IsEnabled = !session.IsBusy && action.IsEnabled?.Invoke() != false;
            }

            QueueResize();
        }


        // Layout

        /// <summary>
        /// Coalesces host and content geometry changes into one pending measurement.
        /// </summary>
        private void OnGeometryChanged(object? sender, EventArgs e) => QueueResize();

        /// <summary>
        /// Schedules measurement only while a dialog is open, avoiding work from the hidden host.
        /// </summary>
        private void QueueResize()
        {
            if (!IsOpen || _resizeQueued) return;
            _resizeQueued = true;
            Dispatcher.Dispatch(() => { _resizeQueued = false; ResizeDialog(); });
        }

        /// <summary>
        /// Limits the card to the configured width and sixty percent of the available window height.
        /// </summary>
        internal static (double Width, double Height) CalculateBounds(double width, double height, double contentHeight) =>
            (Math.Max(0, Math.Min(480, width - 48)), Math.Max(0, Math.Min(height * .6, contentHeight)));

        /// <summary>
        /// Measures wrapped content and grows the card until the scrollable height limit is reached.
        /// </summary>
        private void ResizeDialog()
        {
            if (!IsOpen || Width <= 0 || Height <= 0) return;
            var width = CalculateBounds(Width, Height, 0).Width;

            // Reserve room for the custom scrollbar while measuring, preventing scrollbar/wrapping oscillation.
            var desired = ((IView)Body).Measure(Math.Max(0, width - 38), double.PositiveInfinity).Height + 26;
            var bounds = CalculateBounds(Width, Height, desired);
            Card.WidthRequest = bounds.Width;
            Card.HeightRequest = bounds.Height;
            Card.MaximumHeightRequest = Height * .6;
            ScrollBar.Refresh();
        }


        // Platform hooks

        /// <summary>
        /// Connects platform keyboard handling when the native host becomes available.
        /// </summary>
        partial void ConnectKeyboard();

        /// <summary>
        /// Removes keyboard subscriptions before handler replacement or unloading.
        /// </summary>
        partial void DisconnectKeyboard();

        /// <summary>
        /// Blocks background input and restores the previous native focus when the dialog closes.
        /// </summary>
        partial void UpdateModalPlatform(bool visible);
    }
}
