// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Localization;
using Microsoft.Extensions.DependencyInjection;

namespace ASLM.Pages
{
    /// <summary>
    /// Shows the blocking legal acceptance overlay on pages that host <see cref="ContentView"/> overlays.
    /// </summary>
    internal static class LegalAcceptanceOverlay
    {
        /// <summary>
        /// Presents the legal acceptance overlay when required and waits for the user to accept.
        /// </summary>
        /// <param name="overlayContainer">Overlay slot of the hosting page that receives the acceptance view.</param>
        /// <param name="legalAcceptance">Service that reports whether manual acceptance is required.</param>
        /// <param name="services">Service provider that creates the acceptance view.</param>
        public static async Task PresentIfRequiredAsync(
            ContentView overlayContainer,
            LegalAcceptanceService legalAcceptance,
            IServiceProvider services)
        {
            // Nothing to show when the documents are already accepted.
            if (!legalAcceptance.ManualAcceptanceRequired)
            {
                return;
            }

            // Create the view and wait for its completion event through a task.
            var view = services.GetRequiredService<LegalAcceptanceView>();
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            view.AcceptanceCompleted += OnAcceptanceCompleted;

            if (view is ILocalizable localizable)
            {
                localizable.ApplyLocalization();
            }

            // Show the view in the overlay slot and block until the user has accepted; always remove it afterwards.
            overlayContainer.Content = view;
            overlayContainer.IsVisible = true;
            try
            {
                await view.OpenAsync();
                await completion.Task;
                legalAcceptance.ClearManualAcceptanceRequired();
            }
            finally
            {
                view.AcceptanceCompleted -= OnAcceptanceCompleted;
                overlayContainer.IsVisible = false;
                overlayContainer.Content = null;
            }

            // Local handler: completes the wait task when the view reports that the user accepted.
            void OnAcceptanceCompleted(object? sender, EventArgs e) => completion.TrySetResult();
        }
    }
}
