// Copyright NEXTGGTECH. Apache License 2.0.

using Microsoft.Maui.Dispatching;

namespace ASLM.Services.Internal;

/// <summary>
/// Dispatches application work through the active window dispatcher on portable platform heads.
/// </summary>
internal static class LinuxMainThread
{
    private static IDispatcher? _dispatcher;

    /// <summary>
    /// Captures the application dispatcher before resources or background services can request UI work.
    /// </summary>
    internal static void Initialize(IDispatcher dispatcher)
    {
        Volatile.Write(ref _dispatcher, dispatcher);
    }

    /// <summary>
    /// Reports whether the caller already owns the application UI thread.
    /// </summary>
    internal static bool IsMainThread =>
        !GetDispatcher().IsDispatchRequired;

    /// <summary>
    /// Executes immediately on the UI thread or queues work without blocking its caller.
    /// </summary>
    internal static void BeginInvokeOnMainThread(Action action)
    {
        var dispatcher = GetDispatcher();
        if (!dispatcher.IsDispatchRequired) action();
        else if (!dispatcher.Dispatch(action)) throw new InvalidOperationException("The ASLM UI dispatcher is unavailable.");
    }

    /// <summary>
    /// Dispatches synchronous work and propagates completion or exceptions to its caller.
    /// </summary>
    internal static Task InvokeOnMainThreadAsync(Action action) =>
        GetDispatcher().DispatchAsync(action);

    /// <summary>
    /// Dispatches a synchronous value-producing operation.
    /// </summary>
    internal static Task<T> InvokeOnMainThreadAsync<T>(Func<T> action) =>
        GetDispatcher().DispatchAsync(action);

    /// <summary>
    /// Dispatches asynchronous work and waits for the operation, not only its first continuation.
    /// </summary>
    internal static Task InvokeOnMainThreadAsync(Func<Task> action) =>
        GetDispatcher().DispatchAsync(action);

    /// <summary>
    /// Dispatches an asynchronous value-producing operation without introducing async-void callbacks.
    /// </summary>
    internal static Task<T> InvokeOnMainThreadAsync<T>(Func<Task<T>> action) =>
        GetDispatcher().DispatchAsync(action);

    /// <summary>
    /// Rejects premature UI access instead of silently running GTK operations on a worker thread.
    /// </summary>
    private static IDispatcher GetDispatcher() => Volatile.Read(ref _dispatcher)
        ?? throw new InvalidOperationException("The ASLM application dispatcher has not been initialized.");
}
