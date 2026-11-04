// Copyright NEXTGGTECH. Apache License 2.0.

using System.Runtime.InteropServices;
using WinRT.Interop;

namespace ASLM.Installer
{
    /// <summary>
    /// Opens a native folder selection dialog without relying on WinRT pickers.
    /// </summary>
    internal static class WindowsFolderPicker
    {
        // Constants

        /// <summary>
        /// HRESULT that <c>IFileDialog.Show</c> returns when the user cancels the dialog (ERROR_CANCELLED).
        /// </summary>
        private const int OperationCanceled = unchecked((int)0x800704C7);

        /// <summary>
        /// CLSID of the shell's <c>FileOpenDialog</c> COM class.
        /// </summary>
        private static readonly Guid FileOpenDialogId = new("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");


        // Folder selection

        /// <summary>
        /// Shows the folder picker and returns the selected file system path.
        /// </summary>
        /// <param name="ownerWindow">WinUI window that owns the dialog; null shows the dialog without an owner.</param>
        /// <param name="title">Title of the dialog.</param>
        /// <returns>The selected folder path, or null when the user cancels the dialog.</returns>
        public static string? PickFolder(object? ownerWindow, string title)
        {
            var dialogType = Type.GetTypeFromCLSID(FileOpenDialogId, throwOnError: true)!;
            var dialog = (IFileOpenDialog)Activator.CreateInstance(dialogType)!;

            // Configure the dialog for folder selection on an existing file system path.
            dialog.GetOptions(out var options);
            dialog.SetOptions(options
                | FileOpenOptions.PickFolders
                | FileOpenOptions.ForceFileSystem
                | FileOpenOptions.PathMustExist
                | FileOpenOptions.NoChangeDir);
            dialog.SetTitle(title);

            // Show the dialog modally; a cancelled dialog is a normal outcome, any other failure throws.
            var ownerHandle = GetOwnerHandle(ownerWindow);
            var result = dialog.Show(ownerHandle);
            if (result == OperationCanceled)
            {
                return null;
            }

            Marshal.ThrowExceptionForHR(result);

            // Read the selected shell item path and release native memory.
            dialog.GetResult(out var item);
            item.GetDisplayName(ShellItemDisplayName.FileSystemPath, out var pathPointer);

            try
            {
                return Marshal.PtrToStringUni(pathPointer);
            }
            finally
            {
                Marshal.FreeCoTaskMem(pathPointer);
            }
        }


        // Owner window binding

        /// <summary>
        /// Resolves the WinUI owner HWND used to parent the native folder dialog.
        /// </summary>
        private static IntPtr GetOwnerHandle(object? ownerWindow)
        {
            if (ownerWindow is null)
            {
                return IntPtr.Zero;
            }

            try
            {
                return WindowNative.GetWindowHandle(ownerWindow);
            }
            catch
            {
                // An unparented dialog is still usable, so a window without a handle is not an error.
                return IntPtr.Zero;
            }
        }


        // Shell dialog interop

        /// <summary>
        /// COM interface of the shell's open dialog (<c>IFileOpenDialog</c> including its base <c>IFileDialog</c>).
        /// Only some members are called; all of them are declared because the method order must match the native
        /// vtable layout.
        /// </summary>
        [ComImport]
        [Guid("D57C7288-D4AD-4768-BE02-9D969532D960")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOpenDialog
        {
            /// <summary>
            /// Shows the dialog modally and returns the HRESULT, which signals cancellation instead of throwing.
            /// </summary>
            [PreserveSig]
            int Show(IntPtr owner);

            /// <summary>
            /// Sets the file type filters; unused by the folder picker.
            /// </summary>
            void SetFileTypes(uint fileTypesCount, IntPtr filterSpec);

            /// <summary>
            /// Selects the active file type filter by its one-based index.
            /// </summary>
            void SetFileTypeIndex(uint fileType);

            /// <summary>
            /// Returns the index of the active file type filter.
            /// </summary>
            void GetFileTypeIndex(out uint fileType);

            /// <summary>
            /// Registers an event sink that receives dialog notifications.
            /// </summary>
            void Advise(IntPtr events, out uint cookie);

            /// <summary>
            /// Unregisters an event sink that was registered with <c>Advise</c>.
            /// </summary>
            void Unadvise(uint cookie);

            /// <summary>
            /// Applies the dialog behavior flags.
            /// </summary>
            void SetOptions(FileOpenOptions options);

            /// <summary>
            /// Returns the current dialog behavior flags.
            /// </summary>
            void GetOptions(out FileOpenOptions options);

            /// <summary>
            /// Sets the folder the dialog shows when it has no recently used folder.
            /// </summary>
            void SetDefaultFolder(IntPtr folder);

            /// <summary>
            /// Sets the folder the dialog opens in.
            /// </summary>
            void SetFolder(IntPtr folder);

            /// <summary>
            /// Returns the folder that is currently shown.
            /// </summary>
            void GetFolder(out IntPtr folder);

            /// <summary>
            /// Returns the item that is currently selected in the dialog.
            /// </summary>
            void GetCurrentSelection(out IntPtr selectedItem);

            /// <summary>
            /// Sets the text of the file name box.
            /// </summary>
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string fileName);

            /// <summary>
            /// Returns the text of the file name box.
            /// </summary>
            void GetFileName(out IntPtr fileName);

            /// <summary>
            /// Sets the dialog title.
            /// </summary>
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);

            /// <summary>
            /// Replaces the text of the confirmation button.
            /// </summary>
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);

            /// <summary>
            /// Replaces the label next to the file name box.
            /// </summary>
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);

            /// <summary>
            /// Returns the item the user chose after the dialog was confirmed.
            /// </summary>
            void GetResult(out IShellItem item);

            /// <summary>
            /// Adds a folder to the navigation pane of the dialog.
            /// </summary>
            void AddPlace(IntPtr folder, int placement);

            /// <summary>
            /// Sets the extension that is appended to file names typed without one.
            /// </summary>
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);

            /// <summary>
            /// Closes the dialog with the given result code.
            /// </summary>
            void Close(int result);

            /// <summary>
            /// Sets the identifier under which the dialog persists its per-application state.
            /// </summary>
            void SetClientGuid(ref Guid guid);

            /// <summary>
            /// Discards the persisted per-application state of the dialog.
            /// </summary>
            void ClearClientData();

            /// <summary>
            /// Sets a filter that restricts which shell items are shown.
            /// </summary>
            void SetFilter(IntPtr filter);

            /// <summary>
            /// Returns all chosen items when multiple selection is enabled.
            /// </summary>
            void GetResults(out IntPtr items);

            /// <summary>
            /// Returns the items that are currently selected in the dialog.
            /// </summary>
            void GetSelectedItems(out IntPtr items);
        }

        /// <summary>
        /// COM interface of a shell namespace item, used to read the path of the chosen folder.
        /// </summary>
        [ComImport]
        [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            /// <summary>
            /// Binds the item to a handler object; unused by the folder picker.
            /// </summary>
            void BindToHandler(IntPtr bindContext, ref Guid handlerId, ref Guid interfaceId, out IntPtr ppv);

            /// <summary>
            /// Returns the parent item.
            /// </summary>
            void GetParent(out IShellItem parent);

            /// <summary>
            /// Returns the item name in the requested form as a CoTaskMem string that the caller must free.
            /// </summary>
            void GetDisplayName(ShellItemDisplayName displayName, out IntPtr name);

            /// <summary>
            /// Returns the item attributes selected by the mask.
            /// </summary>
            void GetAttributes(uint attributeMask, out uint attributes);

            /// <summary>
            /// Compares this item with another item.
            /// </summary>
            void Compare(IShellItem item, uint hint, out int order);
        }

        /// <summary>
        /// Behavior flags of the shell open dialog (<c>FILEOPENDIALOGOPTIONS</c>).
        /// </summary>
        [Flags]
        private enum FileOpenOptions : uint
        {
            /// <summary>
            /// Keeps the process working directory unchanged when the user navigates.
            /// </summary>
            NoChangeDir = 0x00000008,

            /// <summary>
            /// Lets the user pick folders instead of files.
            /// </summary>
            PickFolders = 0x00000020,

            /// <summary>
            /// Returns only items that exist in the file system.
            /// </summary>
            ForceFileSystem = 0x00000040,

            /// <summary>
            /// Accepts only paths that already exist.
            /// </summary>
            PathMustExist = 0x00000800,
        }

        /// <summary>
        /// Form in which a shell item returns its name.
        /// </summary>
        private enum ShellItemDisplayName : uint
        {
            /// <summary>
            /// Full file system path of the item.
            /// </summary>
            FileSystemPath = 0x80058000,
        }
    }
}
