// Copyright NEXTGGTECH. Apache License 2.0.

using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Maui.Handlers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using NativeClipboard = Windows.ApplicationModel.DataTransfer.Clipboard;

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Makes ordinary WebView copies eligible for Windows clipboard history without changing web content.
    /// </summary>
    /// <remarks>
    /// While any WebView2 control is live, this handler watches clipboard changes and recognizes text copies made
    /// by the browser processes of those controls. Such a copy is republished under the host window, keeping all
    /// native formats and privacy flags and adding the <c>CanIncludeInClipboardHistory</c> format when it is missing.
    /// Copies from other applications and from native controls are never touched.
    /// </remarks>
    public sealed class WebViewClipboardHandler : WebViewHandler
    {
        // Fields

        /// <summary>
        /// Live handlers that share one clipboard subscription. Native controls and other applications are never
        /// republished: the subscription exists once, for all live WebViews.
        /// </summary>
        private static readonly HashSet<WebViewClipboardHandler> ConnectedHandlers = [];

        /// <summary>
        /// Whether a republish is running, which prevents the clipboard change it causes from being processed again.
        /// </summary>
        private static bool _publishing;

        /// <summary>
        /// Handle (HWND) of the application window that hosts the WebView, used as the clipboard owner when
        /// republishing; 0 while the handler is not attached.
        /// </summary>
        private nint _window;


        // Handler lifecycle

        /// <summary>
        /// Subscribes to the WebView2 load events and attaches to the clipboard when the control is ready.
        /// </summary>
        protected override void ConnectHandler(WebView2 platformView)
        {
            base.ConnectHandler(platformView);
            platformView.CoreWebView2Initialized += OnCoreInitialized;
            platformView.Loaded += OnLoaded;
            platformView.Unloaded += OnUnloaded;
            AttachClipboard();
        }

        /// <summary>
        /// Detaches from the clipboard and unsubscribes from the WebView2 load events.
        /// </summary>
        protected override void DisconnectHandler(WebView2 platformView)
        {
            DetachClipboard();
            platformView.CoreWebView2Initialized -= OnCoreInitialized;
            platformView.Loaded -= OnLoaded;
            platformView.Unloaded -= OnUnloaded;
            base.DisconnectHandler(platformView);
        }


        // Event handlers

        /// <summary>
        /// Attaches to the clipboard once the core browser exists, because browser processes can be queried only then.
        /// </summary>
        private void OnCoreInitialized(WebView2 sender, CoreWebView2InitializedEventArgs args) => AttachClipboard();

        /// <summary>
        /// Attaches to the clipboard when the control enters the visual tree and its window is known.
        /// </summary>
        private void OnLoaded(object sender, RoutedEventArgs args) => AttachClipboard();

        /// <summary>
        /// Detaches from the clipboard when the control leaves the visual tree.
        /// </summary>
        private void OnUnloaded(object sender, RoutedEventArgs args) => DetachClipboard();


        // Clipboard subscription

        /// <summary>
        /// Registers this handler as a republish candidate once the control is loaded, has a core browser and a
        /// window; the first registered handler subscribes to clipboard changes.
        /// </summary>
        private void AttachClipboard()
        {
            // The window handle is only available after the control is loaded into a MAUI window.
            if (!PlatformView.IsLoaded || PlatformView.CoreWebView2 == null ||
                (VirtualView as Microsoft.Maui.Controls.WebView)?.Window?.Handler?.PlatformView
                    is not Microsoft.UI.Xaml.Window window)
                return;

            _window = WinRT.Interop.WindowNative.GetWindowHandle(window);
            if (_window != 0 && ConnectedHandlers.Add(this) && ConnectedHandlers.Count == 1)
                NativeClipboard.ContentChanged += OnClipboardChanged;
        }

        /// <summary>
        /// Unregisters this handler; the last one to leave removes the clipboard subscription.
        /// </summary>
        private void DetachClipboard()
        {
            _window = 0;
            if (ConnectedHandlers.Remove(this) && ConnectedHandlers.Count == 0)
                NativeClipboard.ContentChanged -= OnClipboardChanged;
        }


        // Clipboard republishing

        /// <summary>
        /// Captures the owner and sequence number of the new clipboard content and schedules a republish.
        /// </summary>
        private static void OnClipboardChanged(object? sender, object args)
        {
            var owner = WebViewClipboardHistory.GetClipboardOwner();
            var sequence = WebViewClipboardHistory.GetClipboardSequenceNumber();
            if (owner == 0 || sequence == 0) return;

            // ContentChanged is not assumed to arrive on the WebView's UI thread.
            MainThread.BeginInvokeOnMainThread(() => PublishClipboard(owner, sequence, 0));
        }

        /// <summary>
        /// Republishes the current clipboard content when it was copied by the browser process of a live WebView.
        /// </summary>
        /// <param name="owner">Window that owned the clipboard when the change was observed.</param>
        /// <param name="sequence">Clipboard sequence number of the observed copy; a newer copy cancels the republish.</param>
        /// <param name="attempt">Zero-based retry counter used when another process holds the clipboard open.</param>
        private static void PublishClipboard(nint owner, uint sequence, int attempt)
        {
            // Ignore stale notifications, our own writes and copies made inside this process.
            if (_publishing || !WebViewClipboardHistory.IsCurrent(owner, sequence)) return;
            WebViewClipboardHistory.GetWindowThreadProcessId(owner, out var processId);
            if (processId == 0 || processId == Environment.ProcessId) return;

            try
            {
                // Only copies made by a WebView's own browser process are republished.
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

        /// <summary>
        /// Returns whether the process belongs to this control's browser, so its clipboard writes can be trusted
        /// as web content. Controls in private browsing mode never qualify.
        /// </summary>
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
        // Publish result

        /// <summary>
        /// Outcome of one republish attempt.
        /// </summary>
        internal enum PublishResult
        {
            /// <summary>
            /// Nothing was changed: the copy is outdated, excluded from history, not text, or could not be read safely.
            /// </summary>
            Skipped,

            /// <summary>
            /// The clipboard is open in another process; the caller may retry shortly.
            /// </summary>
            Busy,

            /// <summary>
            /// The clipboard content was replaced by a copy that is eligible for clipboard history.
            /// </summary>
            Published
        }


        // Limits and clipboard formats

        /// <summary>
        /// Maximum total size, in bytes, of all clipboard formats that are copied; larger copies are left untouched.
        /// </summary>
        private const int MaxSnapshotBytes = 16 * 1024 * 1024;

        /// <summary>
        /// Maximum number of clipboard formats that are copied; copies with more formats are left untouched.
        /// </summary>
        private const int MaxFormats = 128;

        /// <summary>
        /// Registered format <c>CanIncludeInClipboardHistory</c>: a DWORD that is 1 when the content may be stored in
        /// Windows clipboard history and 0 when it must not.
        /// </summary>
        private static readonly uint HistoryFormat = RegisterClipboardFormat("CanIncludeInClipboardHistory");

        /// <summary>
        /// Registered format <c>ExcludeClipboardContentFromMonitorProcessing</c>: its presence asks clipboard
        /// monitors, including clipboard history, to ignore the content.
        /// </summary>
        private static readonly uint ExcludedFormat = RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");

        /// <summary>
        /// Registered format <c>WebKit Smart Paste Format</c>, a presence-only marker written by Chromium that has
        /// no readable data of its own.
        /// </summary>
        private static readonly uint SmartPasteFormat = RegisterClipboardFormat("WebKit Smart Paste Format");


        // Republishing

        /// <summary>
        /// Returns whether the clipboard still holds the observed copy, which is identified by its owner window
        /// and its sequence number.
        /// </summary>
        internal static bool IsCurrent(nint owner, uint sequence) =>
            owner != 0 && sequence != 0 && GetClipboardOwner() == owner && GetClipboardSequenceNumber() == sequence;

        /// <summary>
        /// Rewrites the current clipboard content under the host window so it becomes eligible for clipboard
        /// history. Every format is read and prepared before the clipboard is emptied, and the clipboard stays
        /// open for the whole operation so it cannot change in between.
        /// </summary>
        /// <param name="owner">Window that owned the clipboard when the copy was observed.</param>
        /// <param name="sequence">Clipboard sequence number of the observed copy.</param>
        /// <param name="hostWindow">Application window that becomes the clipboard owner.</param>
        /// <returns>How the attempt ended.</returns>
        internal static PublishResult TryRepublish(nint owner, uint sequence, nint hostWindow)
        {
            // Skip when the copy is outdated or already owned by the host window. Opening the clipboard fails while
            // another process holds it, which the caller may retry.
            if (hostWindow == 0 || hostWindow == owner || !IsCurrent(owner, sequence))
                return PublishResult.Skipped;
            if (!OpenClipboard(hostWindow)) return PublishResult.Busy;

            var snapshot = new List<FormatCopy>();
            try
            {
                // Only copies that contain Unicode text (CF_UNICODETEXT, 13) and carry no exclusion marker qualify.
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

                // Snapshot every format with its payload; any unsupported or oversized format cancels the republish.
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

                // A failed enumeration or a copy that changed while reading must not be republished.
                if (Marshal.GetLastPInvokeError() != 0 || !IsCurrent(owner, sequence))
                    return PublishResult.Skipped;

                // Prepare every allocation BEFORE clearing anything. Preserve format order and native payloads,
                // including HTML headers, RTF, URLs and CanUploadToCloudClipboard (no change to cloud policy).
                if (snapshot.All(copy => copy.Format != HistoryFormat))
                    snapshot.Add(new FormatCopy(HistoryFormat, BitConverter.GetBytes(1u)));
                foreach (var copy in snapshot) copy.Prepare();

                // Take ownership and write the formats back in their original order.
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
                // Free allocations Windows did not take over, wipe the copied payloads and release the clipboard.
                foreach (var copy in snapshot) copy.Dispose();
                CloseClipboard();
            }
        }


        // Snapshot reading

        /// <summary>
        /// Returns whether the format stores its data in a global memory block that can be read as raw bytes.
        /// These are CF_TEXT (1), CF_OEMTEXT (7), CF_DIB (8), CF_UNICODETEXT (13), CF_HDROP (15), CF_LOCALE (16),
        /// CF_DIBV5 (17) and registered formats (0xC000 and above); bitmap, metafile and palette formats use other
        /// handle kinds.
        /// </summary>
        private static bool IsMemoryFormat(uint format) =>
            format is 1 or 7 or 8 or 13 or 15 or 16 or 17 || format >= 0xC000;

        /// <summary>
        /// Copies the raw payload of one clipboard format, or returns null when it is empty, larger than the limit
        /// or cannot be locked.
        /// </summary>
        /// <param name="format">Clipboard format to read.</param>
        /// <param name="limit">Remaining byte budget of the whole snapshot.</param>
        private static byte[]? ReadBytes(uint format, int limit)
        {
            var handle = GetClipboardData(format);

            // Chromium's smart-paste format is a presence-only marker, not delayed text to extract.
            if (handle == 0) return format == SmartPasteFormat ? new byte[1] : null;

            // Reject empty or over-budget payloads, then copy the bytes while the block is locked.
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


        // Snapshot entry

        /// <summary>
        /// One clipboard format with its payload and the global memory block that carries it into the clipboard.
        /// The payload is wiped when the entry is disposed.
        /// </summary>
        private sealed class FormatCopy(uint format, byte[] bytes) : IDisposable
        {
            /// <summary>
            /// Clipboard format identifier of this entry.
            /// </summary>
            internal uint Format { get; } = format;

            /// <summary>
            /// Global memory handle prepared for <see cref="Publish"/>; 0 before <see cref="Prepare"/> and after
            /// Windows took ownership of the allocation.
            /// </summary>
            internal nint Handle { get; private set; }

            /// <summary>
            /// Allocates a zero-initialized moveable global block and fills it with the payload.
            /// </summary>
            internal void Prepare()
            {
                // GMEM_MOVEABLE | GMEM_ZEROINIT
                Handle = GlobalAlloc(0x0042, (nuint)bytes.Length);
                if (Handle == 0) throw new OutOfMemoryException();
                var memory = GlobalLock(Handle);
                if (memory == 0) throw new InvalidOperationException("Cannot lock clipboard allocation.");
                try { Marshal.Copy(bytes, 0, memory, bytes.Length); }
                finally { GlobalUnlock(Handle); }
            }

            /// <summary>
            /// Hands the prepared block to the clipboard. Returns false when Windows rejects it; the caller keeps
            /// the allocation in that case.
            /// </summary>
            internal bool Publish()
            {
                if (SetClipboardData(Format, Handle) == 0) return false;

                // Windows now owns the allocation.
                Handle = 0;
                return true;
            }

            /// <summary>
            /// Frees the allocation if the clipboard did not take it and wipes the copied payload.
            /// </summary>
            public void Dispose()
            {
                if (Handle != 0) GlobalFree(Handle);
                Array.Clear(bytes);
            }
        }


        // Win32 interop

        /// <summary>
        /// Win32 <c>GetClipboardOwner</c>: returns the handle of the window that last emptied the clipboard,
        /// or 0 when there is none.
        /// </summary>
        [DllImport("user32.dll")]
        internal static extern nint GetClipboardOwner();

        /// <summary>
        /// Win32 <c>GetClipboardSequenceNumber</c>: returns a number that increases with every clipboard change
        /// in the window station, or 0 when access is denied.
        /// </summary>
        [DllImport("user32.dll")]
        internal static extern uint GetClipboardSequenceNumber();

        /// <summary>
        /// Win32 <c>GetWindowThreadProcessId</c>: returns the id of the thread that created the window and
        /// writes the id of its process to the output parameter.
        /// </summary>
        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);

        /// <summary>
        /// Win32 <c>OpenClipboard</c>: opens the clipboard for examination and associates it with the given window.
        /// Returns false while another window holds the clipboard open.
        /// </summary>
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool OpenClipboard(nint window);

        /// <summary>
        /// Win32 <c>CloseClipboard</c>: closes the clipboard that this thread opened.
        /// </summary>
        [DllImport("user32.dll")]
        private static extern bool CloseClipboard();

        /// <summary>
        /// Win32 <c>EmptyClipboard</c>: removes all clipboard content and makes the window that opened the
        /// clipboard its owner.
        /// </summary>
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EmptyClipboard();

        /// <summary>
        /// Win32 <c>EnumClipboardFormats</c>: returns the next available format after the given one in clipboard
        /// order (start with 0), or 0 after the last format or on failure.
        /// </summary>
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint EnumClipboardFormats(uint format);

        /// <summary>
        /// Win32 <c>IsClipboardFormatAvailable</c>: returns whether the clipboard holds data in the given format.
        /// </summary>
        [DllImport("user32.dll")]
        private static extern bool IsClipboardFormatAvailable(uint format);

        /// <summary>
        /// Win32 <c>GetClipboardData</c>: returns the handle of the clipboard data in the given format,
        /// or 0 on failure. The clipboard owns the handle, so it must not be freed.
        /// </summary>
        [DllImport("user32.dll", SetLastError = true)]
        private static extern nint GetClipboardData(uint format);

        /// <summary>
        /// Win32 <c>SetClipboardData</c>: places a global memory block on the clipboard in the given format.
        /// Returns the handle of the data, or 0 on failure. On success the system owns the memory.
        /// </summary>
        [DllImport("user32.dll", SetLastError = true)]
        private static extern nint SetClipboardData(uint format, nint memory);

        /// <summary>
        /// Win32 <c>RegisterClipboardFormat</c>: registers a named clipboard format and returns its identifier
        /// (0xC000 to 0xFFFF), or 0 on failure. Registering an existing name returns the same identifier.
        /// </summary>
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint RegisterClipboardFormat(string name);

        /// <summary>
        /// Win32 <c>GlobalAlloc</c>: allocates the given number of bytes from the global heap with the given
        /// flags and returns its handle, or 0 on failure.
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern nint GlobalAlloc(uint flags, nuint bytes);

        /// <summary>
        /// Win32 <c>GlobalFree</c>: frees a global memory block. Returns 0 on success, otherwise the handle.
        /// </summary>
        [DllImport("kernel32.dll")]
        private static extern nint GlobalFree(nint memory);

        /// <summary>
        /// Win32 <c>GlobalSize</c>: returns the current size of a global memory block in bytes, or 0 on failure.
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern nuint GlobalSize(nint memory);

        /// <summary>
        /// Win32 <c>GlobalLock</c>: locks a global memory block and returns a pointer to its first byte,
        /// or 0 on failure. Each lock must be balanced by <c>GlobalUnlock</c>.
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern nint GlobalLock(nint memory);

        /// <summary>
        /// Win32 <c>GlobalUnlock</c>: decrements the lock count of a global memory block. Returns whether the
        /// block is still locked.
        /// </summary>
        [DllImport("kernel32.dll")]
        private static extern bool GlobalUnlock(nint memory);
    }
}
