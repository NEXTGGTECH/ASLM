// Copyright NEXTGGTECH. Apache License 2.0.

using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using Microsoft.Maui.Handlers;

namespace ASLM.Services.Controls
{
    /// <summary>
    /// Bridges GTK input to MAUI gesture events, including gestures added after a view is connected.
    /// </summary>
    internal sealed class LinuxGestureManager
    {
        // Fields

        /// <summary>
        /// Manager of each MAUI view; the weak table lets managers of discarded views be collected.
        /// </summary>
        private static readonly ConditionalWeakTable<View, LinuxGestureManager> Views = new();

        /// <summary>
        /// Manager of each native widget, used to find the nearest gesture owner below the pointer.
        /// </summary>
        private static readonly ConditionalWeakTable<Gtk.Widget, LinuxGestureManager> Widgets = new();

        /// <summary>
        /// Whether <see cref="Configure"/> already installed the mapper.
        /// </summary>
        private static bool _configured;

        /// <summary>
        /// Last pan identifier handed out; each drag gets a new one so MAUI can tell gestures apart.
        /// </summary>
        private static int _nextPanId;

        /// <summary>
        /// MAUI view whose gesture recognizers are bridged.
        /// </summary>
        private readonly View _view;

        /// <summary>
        /// Handler that was current when this manager was created; the manager ends when it is replaced.
        /// </summary>
        private readonly IViewHandler _handler;

        /// <summary>
        /// Native widget that receives the GTK controllers.
        /// </summary>
        private readonly Gtk.Widget _widget;

        /// <summary>
        /// Controllers this manager attached to <see cref="_widget"/>, kept so they can be removed again.
        /// </summary>
        private readonly List<Gtk.EventController> _controllers = [];

        /// <summary>
        /// Tap recognizers of the view at the last rebuild.
        /// </summary>
        private TapGestureRecognizer[] _taps = [];

        /// <summary>
        /// Pan recognizers of the view at the last rebuild.
        /// </summary>
        private PanGestureRecognizer[] _pans = [];

        /// <summary>
        /// Pointer recognizers of the view at the last rebuild.
        /// </summary>
        private PointerGestureRecognizer[] _pointers = [];

        /// <summary>
        /// Identifier of the pan in progress.
        /// </summary>
        private int _panId;

        /// <summary>
        /// Whether a pan was started and not yet completed or canceled.
        /// </summary>
        private bool _panning;


        // Registration and lifetime

        /// <summary>
        /// Installs one mapper for every shared view without changing Windows or Mac gesture handling.
        /// </summary>
        internal static void Configure()
        {
            // Install the mapper only once per process.
            if (_configured) return;
            _configured = true;

            // When a view is mapped again, its previous manager is disconnected before a new one takes over.
            ViewHandler.ViewMapper.AppendToMapping("ASLMLinuxGestures", static (handler, view) =>
            {
                if (view is not View control || handler.PlatformView is not Gtk.Widget widget) return;
                if (Views.TryGetValue(control, out var previous)) previous.Disconnect();
                var manager = new LinuxGestureManager(control, handler, widget);
                Views.Add(control, manager);
                Widgets.Add(widget, manager);
            });
        }

        /// <summary>
        /// Observes the recognizer collection and native lifetime, allocating controllers only when needed.
        /// </summary>
        private LinuxGestureManager(View view, IViewHandler handler, Gtk.Widget widget)
        {
            _view = view;
            _handler = handler;
            _widget = widget;

            // Follow recognizer changes, handler replacement and unmapping, then create the initial controllers.
            ((INotifyCollectionChanged)view.GestureRecognizers).CollectionChanged += OnRecognizersChanged;
            view.HandlerChanging += OnHandlerChanging;
            widget.OnUnmap += OnUnmapped;
            Rebuild();
        }

        /// <summary>
        /// Releases native callbacks before a handler is replaced, including a drag interrupted by navigation.
        /// </summary>
        private void OnHandlerChanging(object? sender, HandlerChangingEventArgs args)
        {
            if (args.OldHandler == _handler) Disconnect();
        }

        /// <summary>
        /// Removes subscriptions without retaining detached pages in a process-wide dictionary.
        /// </summary>
        private void Disconnect()
        {
            // End a drag that is still running, then detach every subscription and controller.
            CancelPan();
            ((INotifyCollectionChanged)_view.GestureRecognizers).CollectionChanged -= OnRecognizersChanged;
            _view.HandlerChanging -= OnHandlerChanging;
            _widget.OnUnmap -= OnUnmapped;
            RemoveControllers();

            // Drop the registrations so the lookup tables do not keep this manager reachable.
            Views.Remove(_view);
            Widgets.Remove(_widget);
        }

        /// <summary>
        /// Reconnects dynamically added or removed recognizers, such as color-picker pointer tracking.
        /// </summary>
        private void OnRecognizersChanged(object? sender, NotifyCollectionChangedEventArgs args) => Rebuild();

        /// <summary>
        /// Cancels an in-progress gesture when an overlay or page is hidden.
        /// </summary>
        private void OnUnmapped(Gtk.Widget sender, EventArgs args) => CancelPan();


        // Controller management

        /// <summary>
        /// Builds a small shared set of native controllers for the recognizer types used by the application.
        /// </summary>
        private void Rebuild()
        {
            // Start from a clean state and take a snapshot of the recognizers by type.
            CancelPan();
            RemoveControllers();
            _taps = _view.GestureRecognizers.OfType<TapGestureRecognizer>().ToArray();
            _pans = _view.GestureRecognizers.OfType<PanGestureRecognizer>().ToArray();
            _pointers = _view.GestureRecognizers.OfType<PointerGestureRecognizer>().ToArray();

            // Taps and pointer press/release share one click gesture; button 0 listens to every mouse button.
            if (_taps.Length > 0 || _pointers.Length > 0)
            {
                var click = Gtk.GestureClick.New();
                click.SetButton(0);
                click.OnPressed += OnPressed;
                click.OnReleased += OnReleased;
                AddController(click);
            }

            // Pans use a drag gesture on the primary button; a native cancellation ends the MAUI pan too.
            if (_pans.Length > 0)
            {
                var drag = Gtk.GestureDrag.New();
                drag.SetButton(1);
                drag.OnDragBegin += OnDragBegin;
                drag.OnDragUpdate += OnDragUpdate;
                drag.OnDragEnd += OnDragEnd;
                drag.OnCancel += (_, _) => CancelPan();
                AddController(drag);
            }

            // Pointer enter, move and exit come from a motion controller.
            if (_pointers.Length > 0)
            {
                var motion = Gtk.EventControllerMotion.New();
                motion.OnEnter += (_, args) => SendPointer(SendPointerEntered, args.X, args.Y);
                motion.OnMotion += (_, args) => SendPointer(SendPointerMoved, args.X, args.Y);
                motion.OnLeave += (_, _) => SendPointer(SendPointerExited, 0, 0);
                AddController(motion);
            }
        }

        /// <summary>
        /// Registers owned controllers in the bubble phase so native child inputs keep their own clicks.
        /// </summary>
        private void AddController(Gtk.EventController controller)
        {
            controller.SetPropagationPhase(Gtk.PropagationPhase.Bubble);
            _controllers.Add(controller);
            _widget.AddController(controller);
        }

        /// <summary>
        /// Detaches controllers on collection changes instead of accumulating duplicate callbacks.
        /// </summary>
        private void RemoveControllers()
        {
            foreach (var controller in _controllers)
            {
                _widget.RemoveController(controller);
                controller.Dispose();
            }

            _controllers.Clear();
        }


        // Click routing

        /// <summary>
        /// Forwards pointer presses without claiming the sequence before scrolling can recognize a drag.
        /// </summary>
        private void OnPressed(Gtk.GestureClick sender, Gtk.GestureClick.PressedSignalArgs args) =>
            SendPointer(SendPointerPressed, args.X, args.Y, ButtonMask(sender.GetCurrentButton()));

        /// <summary>
        /// Delivers both MAUI commands and Tapped events, preventing an inner action from dismissing its overlay.
        /// </summary>
        private void OnReleased(Gtk.GestureClick sender, Gtk.GestureClick.ReleasedSignalArgs args)
        {
            // Pointer recognizers always see the release.
            var button = ButtonMask(sender.GetCurrentButton());
            SendPointer(SendPointerReleased, args.X, args.Y, button);

            // A tap needs an input-ready view that is the tap target at this point (not a nested owner or native
            // input) and a recognizer matching the button and the number of presses in the click sequence.
            if (!CanReceiveInput() || !IsTapTarget(args.X, args.Y, button)) return;
            var taps = _taps.Where(tap => (tap.Buttons & button) != 0 && args.NPress % Math.Max(1, tap.NumberOfTapsRequired) == 0).ToArray();
            if (taps.Length == 0) return;

            // Claim only a recognized click. The nearest recognizer, including an intentionally empty card
            // handler, absorbs it; backdrop handlers must never run for clicks inside that card.
            sender.SetState(Gtk.EventSequenceState.Claimed);
            var x = args.X;
            var y = args.Y;
            foreach (var tap in taps) SendTapped(tap, _view, target => GetPosition(target, x, y));
        }

        /// <summary>
        /// Finds the closest gesture owner or interactive native child under the pointer.
        /// </summary>
        private bool IsTapTarget(double x, double y, ButtonsMask button)
        {
            // Walk up from the widget under the pointer: reaching this view's widget first means it owns the tap,
            // while a nested gesture owner or an interactive native control claims it for itself.
            for (var picked = _widget.Pick(x, y, Gtk.PickFlags.Default); picked != null; picked = picked.GetParent())
            {
                if (picked == _widget) return true;
                if (Widgets.TryGetValue(picked, out var child) && child.CanReceiveInput() &&
                    child._taps.Any(tap => (tap.Buttons & button) != 0)) return false;
                if (picked is Gtk.Button or Gtk.Entry or Gtk.DropDown or Gtk.TextView or Gtk.Range or Gtk.Switch) return false;
            }

            return false;
        }

        /// <summary>
        /// Rejects interaction with hidden, disabled or input-transparent views.
        /// </summary>
        private bool CanReceiveInput() => _view.IsVisible && _view.IsEnabled && !_view.InputTransparent && _widget.IsSensitive();

        /// <summary>
        /// Converts GTK button numbers to the primary and secondary buttons supported by MAUI.
        /// </summary>
        private static ButtonsMask ButtonMask(uint button) => button == 3 ? ButtonsMask.Secondary
            : button == 1 ? ButtonsMask.Primary : (ButtonsMask)0;

        /// <summary>
        /// Converts gesture coordinates through GTK's actual layout, including scrolled and translated ancestors.
        /// </summary>
        private Point? GetPosition(IElement? relativeTo, double x, double y)
        {
            var target = relativeTo?.Handler?.PlatformView as Gtk.Widget ?? _widget.GetRoot() as Gtk.Widget;
            return target != null && _widget.TranslateCoordinates(target, x, y, out var targetX, out var targetY)
                ? new Point(targetX, targetY) : null;
        }


        // Drag and pointer events

        /// <summary>
        /// Starts a MAUI pan through its public controller contract instead of reflection on nonexistent methods.
        /// </summary>
        private void OnDragBegin(Gtk.GestureDrag sender, Gtk.GestureDrag.DragBeginSignalArgs args)
        {
            if (!CanReceiveInput()) return;

            // Each drag gets a process-unique id, which MAUI uses to match updates to their pan.
            _panning = true;
            _panId = Interlocked.Increment(ref _nextPanId);
            foreach (IPanGestureController pan in _pans) pan.SendPanStarted(_view, _panId);
        }

        /// <summary>
        /// Keeps native pointer capture during dragging and publishes cumulative offsets used by AppScrollBar.
        /// </summary>
        private void OnDragUpdate(Gtk.GestureDrag sender, Gtk.GestureDrag.DragUpdateSignalArgs args)
        {
            if (!_panning) return;

            // Claiming the sequence keeps the drag with this widget, so a parent scroller does not take it over.
            sender.SetState(Gtk.EventSequenceState.Claimed);
            foreach (IPanGestureController pan in _pans) pan.SendPan(_view, args.OffsetX, args.OffsetY, _panId);
        }

        /// <summary>
        /// Completes a drag once, leaving native scrolling and thumb refresh to the shared control.
        /// </summary>
        private void OnDragEnd(Gtk.GestureDrag sender, Gtk.GestureDrag.DragEndSignalArgs args)
        {
            if (!_panning) return;
            _panning = false;
            foreach (IPanGestureController pan in _pans) pan.SendPanCompleted(_view, _panId);
        }

        /// <summary>
        /// Releases shared drag state after native cancellation, hiding or handler replacement.
        /// </summary>
        private void CancelPan()
        {
            if (!_panning) return;
            _panning = false;
            foreach (IPanGestureController pan in _pans) pan.SendPanCanceled(_view, _panId);
        }

        /// <summary>
        /// Publishes pointer events and their commands through the same MAUI path with valid relative positions.
        /// </summary>
        private void SendPointer(Action<PointerGestureRecognizer, View, Func<IElement?, Point?>?, Microsoft.Maui.Controls.PlatformPointerEventArgs?, ButtonsMask> send,
            double x, double y, ButtonsMask button = ButtonsMask.Primary)
        {
            if (!CanReceiveInput()) return;
            foreach (var pointer in _pointers)
                if ((pointer.Buttons & button) != 0) send(pointer, _view, target => GetPosition(target, x, y), null, button);
        }


        // Framework dispatchers

        // MAUI's tap/pointer dispatchers are internal in the pinned Controls build. UnsafeAccessor binds those
        // exact signatures once; it preserves event/command semantics without per-event reflection or private fields.

        /// <summary>
        /// Raises the framework's normal tap notification.
        /// </summary>
        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "SendTapped")]
        private static extern void SendTapped(TapGestureRecognizer recognizer, View sender, Func<IElement?, Point?>? position);

        /// <summary>
        /// Raises the framework's normal pointer-enter notification.
        /// </summary>
        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "SendPointerEntered")]
        private static extern void SendPointerEntered(PointerGestureRecognizer recognizer, View sender, Func<IElement?, Point?>? position,
            Microsoft.Maui.Controls.PlatformPointerEventArgs? platform, ButtonsMask button);

        /// <summary>
        /// Raises the framework's normal pointer-move notification.
        /// </summary>
        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "SendPointerMoved")]
        private static extern void SendPointerMoved(PointerGestureRecognizer recognizer, View sender, Func<IElement?, Point?>? position,
            Microsoft.Maui.Controls.PlatformPointerEventArgs? platform, ButtonsMask button);

        /// <summary>
        /// Raises the framework's normal pointer-exit notification.
        /// </summary>
        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "SendPointerExited")]
        private static extern void SendPointerExited(PointerGestureRecognizer recognizer, View sender, Func<IElement?, Point?>? position,
            Microsoft.Maui.Controls.PlatformPointerEventArgs? platform, ButtonsMask button);

        /// <summary>
        /// Raises the framework's normal pointer-press notification.
        /// </summary>
        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "SendPointerPressed")]
        private static extern void SendPointerPressed(PointerGestureRecognizer recognizer, View sender, Func<IElement?, Point?>? position,
            Microsoft.Maui.Controls.PlatformPointerEventArgs? platform, ButtonsMask button);

        /// <summary>
        /// Raises the framework's normal pointer-release notification.
        /// </summary>
        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "SendPointerReleased")]
        private static extern void SendPointerReleased(PointerGestureRecognizer recognizer, View sender, Func<IElement?, Point?>? position,
            Microsoft.Maui.Controls.PlatformPointerEventArgs? platform, ButtonsMask button);
    }
}
