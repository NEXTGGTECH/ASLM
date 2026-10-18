// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Services.Internal;

/// <summary>
/// Exports themes through GTK's desktop file dialog without assuming a writable application directory.
/// </summary>
internal static class LinuxThemeFileExporter
{
    /// <summary>
    /// Lets the user choose a JSON destination and writes only after the native save dialog confirms it.
    /// </summary>
    internal static async Task ExportAsync(string suggestedFileName, string json)
    {
        var window = Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView as Gtk.Window
            ?? throw new InvalidOperationException("The ASLM window is not available.");
        // GirCore 0.7 hides GError codes in FileDialog.SaveAsync. Native response IDs distinguish dismissal
        // from write failures reliably, without matching translated exception messages or using reflection.
        using var dialog = Gtk.FileChooserNative.New(null, window, Gtk.FileChooserAction.Save, null, null);
        dialog.SetModal(true);
        dialog.SetCurrentName(suggestedFileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? suggestedFileName : suggestedFileName + ".json");
        using var filter = Gtk.FileFilter.New();
        filter.AddPattern("*.json");
        dialog.AddFilter(filter);
        dialog.SetFilter(filter);
        var response = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        dialog.OnResponse += (_, args) => response.TrySetResult(args.ResponseId == (int)Gtk.ResponseType.Accept);
        dialog.Show();
        if (!await response.Task) return;

        // Cancellation never writes anything; actual filesystem errors still reach the caller's error handling.
        using var file = dialog.GetFile();
        if (file?.GetPath() is not { } path) throw new IOException("The selected theme destination is not a local file.");
        await File.WriteAllTextAsync(path, json);
    }
}
