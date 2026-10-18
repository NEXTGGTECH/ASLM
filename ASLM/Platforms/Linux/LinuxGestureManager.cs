// Copyright NEXTGGTECH. Apache License 2.0.

using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using Microsoft.Maui.Handlers;

namespace ASLM.Services.Controls;

/// <summary>
/// Bridges GTK input to MAUI gesture events, including gestures added after a view is connected.
/// </summary>
internal sealed class LinuxGestureManager
{
    private static readonly ConditionalWeakTable<View, LinuxGestureManager> Views = new();
    private static readonly ConditionalWeakTable<Gtk.Widget, LinuxGestureManager> Widgets = new();
    private static bool _configured;
    private static int _nextPanId;
    private readonly View _view;
    private readonly IViewHandler _handler;
    private readonly Gtk.Widget _widget;
    private readonly List<Gtk.EventController> _controllers = [];
    private TapGestureRecognizer[] _taps = [];
    private PanGestureRecognizer[] _pans = [];
    private PointerGestureRecognizer[] _pointers = [];
    private int _panId;
    private bool _panning;

    // Registration and lifetime

    /// <summary>
    /// Installs one mapper for every shared view without changing Windows or Mac gesture handling.
    /// </summary>
    internal static void Configure()
    {
        if (_configured) return;
        _configured = true;
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
        CancelPan();
        ((INotifyCollectionChanged)_view.GestureRecognizers).CollectionChanged -= OnRecognizersChanged;
        _view.HandlerChanging -= OnHandlerChanging;
        _widget.OnUnmap -= OnUnmapped;
        RemoveControllers();
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

    /// <summary>
    /// Builds a small shared set of native controllers for the recognizer types used by the application.
    /// </summary>
    private void Rebuild()
    {
        CancelPan();
        RemoveControllers();
        _taps = _view.GestureRecognizers.OfType<TapGestureRecognizer>().ToArray();
        _pans = _view.GestureRecognizers.OfType<PanGestureRecognizer>().ToArray();
        _pointers = _view.GestureRecognizers.OfType<PointerGestureRecognizer>().ToArray();
        if (_taps.Length > 0 || _pointers.Length > 0)
        {
            var click = Gtk.GestureClick.New();
            click.SetButton(0);
            click.OnPressed += OnPressed;
            click.OnReleased += OnReleased;
            AddController(click);
        }
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
        var button = ButtonMask(sender.GetCurrentButton());
        SendPointer(SendPointerReleased, args.X, args.Y, button);
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

    // MAUI's tap/pointer dispatchers are internal in the pinned Controls build. UnsafeAccessor binds those
    // exact signatures once; it preserves event/command semantics without per-event reflection or private fields.

    /// <summary>Raises the framework's normal tap notification.</summary>
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "SendTapped")]
    private static extern void SendTapped(TapGestureRecognizer recognizer, View sender, Func<IElement?, Point?>? position);

    /// <summary>Raises the framework's normal pointer-enter notification.</summary>
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "SendPointerEntered")]
    private static extern void SendPointerEntered(PointerGestureRecognizer recognizer, View sender, Func<IElement?, Point?>? position,
        Microsoft.Maui.Controls.PlatformPointerEventArgs? platform, ButtonsMask button);

    /// <summary>Raises the framework's normal pointer-move notification.</summary>
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "SendPointerMoved")]
    private static extern void SendPointerMoved(PointerGestureRecognizer recognizer, View sender, Func<IElement?, Point?>? position,
        Microsoft.Maui.Controls.PlatformPointerEventArgs? platform, ButtonsMask button);

    /// <summary>Raises the framework's normal pointer-exit notification.</summary>
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "SendPointerExited")]
    private static extern void SendPointerExited(PointerGestureRecognizer recognizer, View sender, Func<IElement?, Point?>? position,
        Microsoft.Maui.Controls.PlatformPointerEventArgs? platform, ButtonsMask button);

    /// <summary>Raises the framework's normal pointer-press notification.</summary>
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "SendPointerPressed")]
    private static extern void SendPointerPressed(PointerGestureRecognizer recognizer, View sender, Func<IElement?, Point?>? position,
        Microsoft.Maui.Controls.PlatformPointerEventArgs? platform, ButtonsMask button);

    /// <summary>Raises the framework's normal pointer-release notification.</summary>
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "SendPointerReleased")]
    private static extern void SendPointerReleased(PointerGestureRecognizer recognizer, View sender, Func<IElement?, Point?>? position,
        Microsoft.Maui.Controls.PlatformPointerEventArgs? platform, ButtonsMask button);
}
