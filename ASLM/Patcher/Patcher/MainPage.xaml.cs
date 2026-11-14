// Copyright NEXTGGTECH. Apache License 2.0.

using System.Text;

namespace ASLM.Patcher
{
    /// <summary>
    /// Shows live patcher status while the file replacement runs in the background.
    /// </summary>
    public partial class MainPage : ContentPage
    {
        // Fields

        /// <summary>
        /// Whether the window closes itself shortly after the patch finished, so the user is not left with
        /// a stale dialog.
        /// </summary>
        private static readonly bool CloseAutomaticallyAfterPatch = true;

        /// <summary>
        /// Progress messages received so far, shown together in the log editor.
        /// </summary>
        private readonly StringBuilder _log = new();

        /// <summary>
        /// Whether the patch operation was already started, so repeated page loads cannot run it twice.
        /// </summary>
        private bool _started;


        // Page construction

        /// <summary>
        /// Creates the patcher status page.
        /// </summary>
        public MainPage()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }


        // Patch lifecycle

        /// <summary>
        /// Starts the patch operation after the native window is visible.
        /// </summary>
        private async void OnLoaded(object? sender, EventArgs e)
        {
            if (_started)
            {
                return;
            }

            // Run the shared patch operation with this process's command line, reporting progress to the page.
            _started = true;
            var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
            var progress = new Progress<PatcherProgress>(OnProgress);
            var exitCode = await PatcherRunner.RunAsync(args, progress);

            // The process exits with the patch result, which a patcher waiting for this elevated copy reads.
            Environment.ExitCode = exitCode;

            // Update the headline once the background patcher thread finishes.
            StatusLabel.Text = exitCode == 0
                ? "Update finished. ASLM has been started."
                : "Update failed. ASLM has been started.";
            SubtitleLabel.Text = exitCode == 0
                ? "Finished"
                : "Failed";

            // Leave the result readable for a moment, then close the window.
            if (CloseAutomaticallyAfterPatch)
            {
                await Task.Delay(900);
                Application.Current?.Quit();
            }
        }


        // Progress reporting

        /// <summary>
        /// Appends one patcher progress message to the visible console.
        /// </summary>
        private void OnProgress(PatcherProgress progress)
        {
            StatusLabel.Text = progress.Message;
            _log.AppendLine(progress.Message);
            LogEditor.Text = _log.ToString();

            // Scroll the log view after layout so the newest line stays visible.
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                await Task.Yield();
                await LogScroll.ScrollToAsync(0, LogScroll.ContentSize.Height, false);
            });
        }
    }
}
