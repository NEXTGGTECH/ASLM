// Copyright NEXTGGTECH. Apache License 2.0.

using System.Runtime.InteropServices;
using WinRT.Interop;

namespace ASLM.Installer;

/// <summary>
/// Opens a native folder selection dialog without relying on WinRT pickers.
/// </summary>
internal static class WindowsFolderPicker
{
    private const int OperationCanceled = unchecked((int)0x800704C7);
    private static readonly Guid FileOpenDialogId = new("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");

    /// <summary>
    /// Shows the folder picker and returns the selected file system path.
    /// </summary>
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


    // Owner window binding.

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
            return IntPtr.Zero;
        }
    }


    // Shell dialog interop.

    [ComImport]
    [Guid("D57C7288-D4AD-4768-BE02-9D969532D960")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        [PreserveSig]
        int Show(IntPtr owner);

        void SetFileTypes(uint fileTypesCount, IntPtr filterSpec);

        void SetFileTypeIndex(uint fileType);

        void GetFileTypeIndex(out uint fileType);

        void Advise(IntPtr events, out uint cookie);

        void Unadvise(uint cookie);

        void SetOptions(FileOpenOptions options);

        void GetOptions(out FileOpenOptions options);

        void SetDefaultFolder(IntPtr folder);

        void SetFolder(IntPtr folder);

        void GetFolder(out IntPtr folder);

        void GetCurrentSelection(out IntPtr selectedItem);

        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string fileName);

        void GetFileName(out IntPtr fileName);

        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);

        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);

        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);

        void GetResult(out IShellItem item);

        void AddPlace(IntPtr folder, int placement);

        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);

        void Close(int result);

        void SetClientGuid(ref Guid guid);

        void ClearClientData();

        void SetFilter(IntPtr filter);

        void GetResults(out IntPtr items);

        void GetSelectedItems(out IntPtr items);
    }

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr bindContext, ref Guid handlerId, ref Guid interfaceId, out IntPtr ppv);

        void GetParent(out IShellItem parent);

        void GetDisplayName(ShellItemDisplayName displayName, out IntPtr name);

        void GetAttributes(uint attributeMask, out uint attributes);

        void Compare(IShellItem item, uint hint, out int order);
    }

    [Flags]
    private enum FileOpenOptions : uint
    {
        NoChangeDir = 0x00000008,
        PickFolders = 0x00000020,
        ForceFileSystem = 0x00000040,
        PathMustExist = 0x00000800,
    }

    private enum ShellItemDisplayName : uint
    {
        FileSystemPath = 0x80058000,
    }
}
