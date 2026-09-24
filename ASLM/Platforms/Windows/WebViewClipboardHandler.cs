// Copyright NEXTGGTECH. Apache License 2.0.

using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Maui.Handlers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using NativeClipboard = Windows.ApplicationModel.DataTransfer.Clipboard;

namespace ASLM.Services.Internal;

/// <summary>
/// Makes ordinary WebView copies eligible for Windows clipboard history without changing web content.
/// </summary>
public sealed class WebViewClipboardHandler : WebViewHandler
{
    // One subscription for all live WebViews; native controls and other applications are never republished.
    private static readonly HashSet<WebViewClipboardHandler> ConnectedHandlers = [];
    private static bool _publishing;
    private nint _window;

    protected override void ConnectHandler(WebView2 platformView)
    {
        base.ConnectHandler(platformView);
        platformView.CoreWebView2Initialized += OnCoreInitialized;
        platformView.Loaded += OnLoaded;
        platformView.Unloaded += OnUnloaded;
        AttachClipboard();
    }

    protected override void DisconnectHandler(WebView2 platformView)
    {
        DetachClipboard();
        platformView.CoreWebView2Initialized -= OnCoreInitialized;
        platformView.Loaded -= OnLoaded;
        platformView.Unloaded -= OnUnloaded;
        base.DisconnectHandler(platformView);
    }

    private void OnCoreInitialized(WebView2 sender, CoreWebView2InitializedEventArgs args) => AttachClipboard();
    private void OnLoaded(object sender, RoutedEventArgs args) => AttachClipboard();
    private void OnUnloaded(object sender, RoutedEventArgs args) => DetachClipboard();

    private void AttachClipboard()
    {
        if (!PlatformView.IsLoaded || PlatformView.CoreWebView2 == null ||
            (VirtualView as Microsoft.Maui.Controls.WebView)?.Window?.Handler?.PlatformView
                is not Microsoft.UI.Xaml.Window window)
            return;

        _window = WinRT.Interop.WindowNative.GetWindowHandle(window);
        if (_window != 0 && ConnectedHandlers.Add(this) && ConnectedHandlers.Count == 1)
            NativeClipboard.ContentChanged += OnClipboardChanged;
    }

    private void DetachClipboard()
    {
        _window = 0;
        if (ConnectedHandlers.Remove(this) && ConnectedHandlers.Count == 0)
            NativeClipboard.ContentChanged -= OnClipboardChanged;
    }

    private static void OnClipboardChanged(object? sender, object args)
    {
        var owner = WebViewClipboardHistory.GetClipboardOwner();
        var sequence = WebViewClipboardHistory.GetClipboardSequenceNumber();
        if (owner == 0 || sequence == 0) return;

        // ContentChanged is not assumed to arrive on the WebView's UI thread.
        MainThread.BeginInvokeOnMainThread(() => PublishClipboard(owner, sequence, 0));
    }

    private static void PublishClipboard(nint owner, uint sequence, int attempt)
    {
        if (_publishing || !WebViewClipboardHistory.IsCurrent(owner, sequence)) return;
        WebViewClipboardHistory.GetWindowThreadProcessId(owner, out var processId);
        if (processId == 0 || processId == Environment.ProcessId) return;

        try
        {
            var handler = ConnectedHandlers.FirstOrDefault(candidate => candidate.OwnsBrowserProcess(processId));
            if (handler == null) return;

            _publishing = true;
            var result = WebViewClipboardHistory.TryRepublish(owner, sequence, handler._window);
            if (result == WebViewClipboardHistory.PublishResult.Busy && attempt < 3)
            {
                // Retry contention only while this exact copy is current; never overwrite a newer copy.
                (handler.VirtualView as Microsoft.Maui.Controls.WebView)?.Dispatcher.DispatchDelayed(
                    TimeSpan.FromMilliseconds(20 * (attempt + 1)),
                    () => PublishClipboard(owner, sequence, attempt + 1));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WebViewClipboard] Clipboard processing failed: {ex.GetType().Name}.");
        }
        finally
        {
            _publishing = false;
        }
    }

    private bool OwnsBrowserProcess(uint processId)
    {
        try
        {
            var core = PlatformView.CoreWebView2;
            return _window != 0 && core != null && !core.Profile.IsInPrivateModeEnabled &&
                (core.BrowserProcessId == processId ||
                 core.Environment.GetProcessInfos().Any(process => process.ProcessId == processId));
        }
        catch
        {
            // A closed/crashed browser is not a trustworthy clipboard source.
            return false;
        }
    }
}

/// <summary>
/// Republishes an owned text copy under the host HWND, retaining native formats and privacy flags.
/// All reads, sequence checks, and writes occur under the same clipboard lock.
/// </summary>
internal static class WebViewClipboardHistory
{
    internal enum PublishResult { Skipped, Busy, Published }

    private const int MaxSnapshotBytes = 16 * 1024 * 1024;
    private const int MaxFormats = 128;
    private static readonly uint HistoryFormat = RegisterClipboardFormat("CanIncludeInClipboardHistory");
    private static readonly uint ExcludedFormat = RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
    private static readonly uint SmartPasteFormat = RegisterClipboardFormat("WebKit Smart Paste Format");

    internal static bool IsCurrent(nint owner, uint sequence) =>
        owner != 0 && sequence != 0 && GetClipboardOwner() == owner && GetClipboardSequenceNumber() == sequence;

    internal static PublishResult TryRepublish(nint owner, uint sequence, nint hostWindow)
    {
        if (hostWindow == 0 || hostWindow == owner || !IsCurrent(owner, sequence))
            return PublishResult.Skipped;
        if (!OpenClipboard(hostWindow)) return PublishResult.Busy;

        var snapshot = new List<FormatCopy>();
        try
        {
            if (!IsCurrent(owner, sequence) || HistoryFormat == 0 || ExcludedFormat == 0 ||
                !IsClipboardFormatAvailable(13) || IsClipboardFormatAvailable(ExcludedFormat))
                return PublishResult.Skipped;

            // Respect explicit exclusions, including Chromium's password/private-copy protection.
            if (IsClipboardFormatAvailable(HistoryFormat))
            {
                var policy = GetClipboardData(HistoryFormat);
                if (policy == 0 || GlobalSize(policy) < sizeof(uint)) return PublishResult.Skipped;
                var memory = GlobalLock(policy);
                if (memory == 0) return PublishResult.Skipped;
                bool allowed;
                try { allowed = Marshal.ReadInt32(memory) == 1; }
                finally { GlobalUnlock(policy); }
                if (!allowed)
                    return PublishResult.Skipped;
            }

            var totalBytes = 0;
            uint format = 0;
            while ((format = EnumClipboardFormats(format)) != 0)
            {
                // Do not reinterpret GDI handles, private owner-managed data, or virtual-file objects as HGLOBALs.
                if (snapshot.Count >= MaxFormats || !IsMemoryFormat(format)) return PublishResult.Skipped;
                var bytes = ReadBytes(format, MaxSnapshotBytes - totalBytes);
                if (bytes == null) return PublishResult.Skipped;
                totalBytes += bytes.Length;
                snapshot.Add(new FormatCopy(format, bytes));
            }
            if (Marshal.GetLastPInvokeError() != 0 || !IsCurrent(owner, sequence))
                return PublishResult.Skipped;

            // Prepare every allocation BEFORE clearing anything. Preserve format order and native payloads,
            // including HTML headers, RTF, URLs and CanUploadToCloudClipboard (no change to cloud policy).
            if (snapshot.All(copy => copy.Format != HistoryFormat))
                snapshot.Add(new FormatCopy(HistoryFormat, BitConverter.GetBytes(1u)));
            foreach (var copy in snapshot) copy.Prepare();

            if (!IsCurrent(owner, sequence) || !EmptyClipboard()) return PublishResult.Skipped;
            foreach (var copy in snapshot)
            {
                if (copy.Publish()) continue;

                // The clipboard is still locked. Restore missing formats before releasing it if an OS write fails.
                foreach (var pending in snapshot.Where(entry => entry.Handle != 0))
                {
                    if (!pending.Publish())
                        Debug.WriteLine($"[WebViewClipboard] Failed to publish format {pending.Format}: {Marshal.GetLastPInvokeError()}.");
                }
                return PublishResult.Skipped;
            }
            return PublishResult.Published;
        }
        finally
        {
            foreach (var copy in snapshot) copy.Dispose();
            CloseClipboard();
        }
    }

    private static bool IsMemoryFormat(uint format) =>
        format is 1 or 7 or 8 or 13 or 15 or 16 or 17 || format >= 0xC000;

    private static byte[]? ReadBytes(uint format, int limit)
    {
        var handle = GetClipboardData(format);
        // Chromium's smart-paste format is a presence-only marker, not delayed text to extract.
        if (handle == 0) return format == SmartPasteFormat ? new byte[1] : null;
        var length = GlobalSize(handle).ToUInt64();
        if (length == 0 || length > (ulong)Math.Max(limit, 0)) return null;
        var memory = GlobalLock(handle);
        if (memory == 0) return null;
        try
        {
            var bytes = new byte[(int)length];
            Marshal.Copy(memory, bytes, 0, bytes.Length);
            return bytes;
        }
        finally { GlobalUnlock(handle); }
    }

    private sealed class FormatCopy(uint format, byte[] bytes) : IDisposable
    {
        internal uint Format { get; } = format;
        internal nint Handle { get; private set; }

        internal void Prepare()
        {
            Handle = GlobalAlloc(0x0042, (nuint)bytes.Length); // GMEM_MOVEABLE | GMEM_ZEROINIT
            if (Handle == 0) throw new OutOfMemoryException();
            var memory = GlobalLock(Handle);
            if (memory == 0) throw new InvalidOperationException("Cannot lock clipboard allocation.");
            try { Marshal.Copy(bytes, 0, memory, bytes.Length); }
            finally { GlobalUnlock(Handle); }
        }

        internal bool Publish()
        {
            if (SetClipboardData(Format, Handle) == 0) return false;
            Handle = 0; // Windows now owns the allocation.
            return true;
        }

        public void Dispose()
        {
            if (Handle != 0) GlobalFree(Handle);
            Array.Clear(bytes);
        }
    }

    [DllImport("user32.dll")]
    internal static extern nint GetClipboardOwner();
    [DllImport("user32.dll")]
    internal static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(nint window);
    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint EnumClipboardFormats(uint format);
    [DllImport("user32.dll")]
    private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetClipboardData(uint format);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetClipboardData(uint format, nint memory);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll")]
    private static extern nint GlobalFree(nint memory);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint GlobalSize(nint memory);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(nint memory);
}
