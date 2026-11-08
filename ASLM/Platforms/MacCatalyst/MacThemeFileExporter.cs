// Copyright NEXTGGTECH. Apache License 2.0.

using Foundation;
using UIKit;

namespace ASLM
{
    /// <summary>
    /// Presents the system export sheet so the user can save a theme JSON file anywhere.
    /// </summary>
    public static class MacThemeFileExporter
    {
        /// <summary>
        /// Writes the JSON to a temp file and opens the document export picker for it.
        /// </summary>
        /// <param name="suggestedFileName">File name without extension; <c>.json</c> is appended.</param>
        /// <param name="json">Theme JSON content to export.</param>
        public static async Task ExportAsync(string suggestedFileName, string json)
        {
            // The picker exports a file, so the JSON is staged in a temp folder first.
            var exportDir = Path.Combine(Path.GetTempPath(), "ASLM", "theme-export");
            Directory.CreateDirectory(exportDir);

            var exportFile = Path.Combine(exportDir, $"{suggestedFileName}.json");
            await File.WriteAllTextAsync(exportFile, json);

            // UIKit must be used on the main thread; asCopy lets the user pick a destination for a copy of the file.
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                var presenter = ResolvePresenter();
                if (presenter == null)
                {
                    return;
                }

                var picker = new UIDocumentPickerViewController(
                    [NSUrl.FromFilename(exportFile)],
                    asCopy: true);
                presenter.PresentViewController(picker, animated: true, completionHandler: null);
            });
        }

        /// <summary>
        /// Returns the top-most view controller of the key window.
        /// </summary>
        private static UIViewController? ResolvePresenter()
        {
            // Prefer the key window of any connected scene; fall back to the first window.
            var windows = UIApplication.SharedApplication.ConnectedScenes
                .OfType<UIWindowScene>()
                .SelectMany(scene => scene.Windows)
                .ToList();

            var window = windows.FirstOrDefault(candidate => candidate.IsKeyWindow) ?? windows.FirstOrDefault();
            var controller = window?.RootViewController;

            // Walk down the presented chain so the picker appears above any open modal.
            while (controller?.PresentedViewController != null)
            {
                controller = controller.PresentedViewController;
            }

            return controller;
        }
    }
}
