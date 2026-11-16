// Copyright NEXTGGTECH. Apache License 2.0.

using System.Globalization;
using System.Runtime.InteropServices;

namespace ASLM
{
    /// <summary>
    /// Checks native dependencies before GTK bindings initialize or a user profile is created.
    /// </summary>
    internal static class LinuxRuntimeCheck
    {
        // Native signatures

        /// <summary>
        /// Reads one component of the version exported by a native library.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint ReadVersionComponent();

        /// <summary>
        /// Stops the native error dialog's event loop when its close button is pressed.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void DialogResponse(IntPtr dialog, int response, IntPtr loop);


        // Runtime inspection

        /// <summary>
        /// Returns a dependency error, or null when GTK, WebKit, fonts, graphics and globalization are available.
        /// </summary>
        internal static string? Check()
        {
            var errors = new List<string>();
            foreach (var library in new[] { "libgtk-4.so.1", "libwebkitgtk-6.0.so.4", "libfontconfig.so.1",
                         "libfreetype.so.6", "libgssapi_krb5.so.2", "libssl.so.3", "libSkiaSharp.so" })
            {
                IntPtr handle = IntPtr.Zero;
                try
                {
                    // The OS must resolve system libraries; AssemblyDirectory alone excludes that search in .NET 10.
                    handle = NativeLibrary.Load(library, typeof(LinuxRuntimeCheck).Assembly, searchPath: null);
                    if (library == "libgtk-4.so.1")
                    {
                        var version = ReadGtkVersion(handle);
                        if (version < new Version(4, 12, 0))
                            errors.Add($"GTK {version} is installed; GTK 4.12.0 or later is required.");
                    }
                }
                catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
                {
                    errors.Add($"Cannot load {library}: {ex.Message}");
                }
                finally
                {
                    if (handle != IntPtr.Zero) NativeLibrary.Free(handle);
                }
            }

            // ICU is a .NET runtime dependency even when GTK's own dependencies did not pull it in.
            try { _ = CultureInfo.GetCultureInfo("en-US").TextInfo.ToUpper("aslm"); }
            catch (CultureNotFoundException) { errors.Add("ICU globalization data is unavailable."); }

            return errors.Count == 0 ? null : "ASLM cannot start on this system.\n\n" +
                string.Join("\n", errors) + "\n\nInstall the native dependencies using the ASLM installer. " +
                "GTK 4.12 or later and WebKitGTK API 6.0 are required.";
        }

        /// <summary>
        /// Reads the loaded GTK version without initializing a display connection.
        /// </summary>
        private static Version ReadGtkVersion(IntPtr library)
        {
            // Reads a version component through the GTK C ABI rather than a generated binding.
            uint Read(string name) => Marshal.GetDelegateForFunctionPointer<ReadVersionComponent>(
                NativeLibrary.GetExport(library, name))();
            return new Version((int)Read("gtk_get_major_version"), (int)Read("gtk_get_minor_version"),
                (int)Read("gtk_get_micro_version"));
        }


        // Startup diagnostics

        /// <summary>
        /// Shows a native closeable error message when GTK can open a display, keeping stderr as a fallback.
        /// </summary>
        internal static void ShowError(string message)
        {
            Console.Error.WriteLine(message);
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) &&
                string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))) return;

            IntPtr dialog = IntPtr.Zero;
            IntPtr loop = IntPtr.Zero;
            try
            {
                if (!GtkInitCheck()) return;
                loop = MainLoopNew(IntPtr.Zero, false);
                dialog = MessageDialogNew(IntPtr.Zero, Gtk.DialogFlags.Modal, Gtk.MessageType.Error,
                    Gtk.ButtonsType.Close, "%s", message);
                if (dialog == IntPtr.Zero) return;

                // Keep the callback alive until the native loop has stopped dispatching responses.
                DialogResponse response = (_, _, state) => MainLoopQuit(state);
                SignalConnect(dialog, "response", response, loop, IntPtr.Zero, 0);
                WindowPresent(dialog);
                MainLoopRun(loop);
                GC.KeepAlive(response);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                // A machine missing GTK must still receive the original message through stderr.
            }
            finally
            {
                if (dialog != IntPtr.Zero) WindowDestroy(dialog);
                if (loop != IntPtr.Zero) MainLoopUnref(loop);
            }
        }


        // Native error dialog

        /// <summary>
        /// Opens a GTK display when one is available.
        /// </summary>
        [DllImport("libgtk-4.so.1", EntryPoint = "gtk_init_check")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GtkInitCheck();

        /// <summary>
        /// Creates the GTK 4 error dialog using a fixed format string and one UTF-8 message argument.
        /// </summary>
        [DllImport("libgtk-4.so.1", EntryPoint = "gtk_message_dialog_new")]
        private static extern IntPtr MessageDialogNew(IntPtr parent, Gtk.DialogFlags flags, Gtk.MessageType type, Gtk.ButtonsType buttons,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string format, [MarshalAs(UnmanagedType.LPUTF8Str)] string message);

        /// <summary>
        /// Presents the dependency error window.
        /// </summary>
        [DllImport("libgtk-4.so.1", EntryPoint = "gtk_window_present")]
        private static extern void WindowPresent(IntPtr window);

        /// <summary>
        /// Releases the dependency error window.
        /// </summary>
        [DllImport("libgtk-4.so.1", EntryPoint = "gtk_window_destroy")]
        private static extern void WindowDestroy(IntPtr window);

        /// <summary>
        /// Connects the dialog's response signal to the managed callback.
        /// </summary>
        [DllImport("libgobject-2.0.so.0", EntryPoint = "g_signal_connect_data")]
        private static extern ulong SignalConnect(IntPtr instance, [MarshalAs(UnmanagedType.LPUTF8Str)] string signal,
            DialogResponse callback, IntPtr data, IntPtr destroyData, int flags);

        /// <summary>
        /// Creates the event loop owned by the dependency error dialog.
        /// </summary>
        [DllImport("libglib-2.0.so.0", EntryPoint = "g_main_loop_new")]
        private static extern IntPtr MainLoopNew(IntPtr context, [MarshalAs(UnmanagedType.Bool)] bool running);

        /// <summary>
        /// Dispatches GTK events while the dependency error dialog is open.
        /// </summary>
        [DllImport("libglib-2.0.so.0", EntryPoint = "g_main_loop_run")]
        private static extern void MainLoopRun(IntPtr loop);

        /// <summary>
        /// Stops dispatching after the user closes the dependency error dialog.
        /// </summary>
        [DllImport("libglib-2.0.so.0", EntryPoint = "g_main_loop_quit")]
        private static extern void MainLoopQuit(IntPtr loop);

        /// <summary>
        /// Releases the dependency error dialog's event loop.
        /// </summary>
        [DllImport("libglib-2.0.so.0", EntryPoint = "g_main_loop_unref")]
        private static extern void MainLoopUnref(IntPtr loop);
    }
}
