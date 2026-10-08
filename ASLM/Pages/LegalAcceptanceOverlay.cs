// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Localization;
using Microsoft.Extensions.DependencyInjection;

namespace ASLM.Pages;

/// <summary>
/// Shows the blocking legal acceptance overlay on pages that host <see cref="ContentView"/> overlays.
/// </summary>
internal static class LegalAcceptanceOverlay
{
    /// <summary>
    /// Presents the legal acceptance overlay when required and waits for the user to accept.
    /// </summary>
    public static async Task PresentIfRequiredAsync(
        ContentView overlayContainer,
        LegalAcceptanceService legalAcceptance,
        IServiceProvider services)
    {
        if (!legalAcceptance.ManualAcceptanceRequired)
        {
            return;
        }

        var view = services.GetRequiredService<LegalAcceptanceView>();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        view.AcceptanceCompleted += OnAcceptanceCompleted;

        if (view is ILocalizable localizable)
        {
            localizable.ApplyLocalization();
        }

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

        void OnAcceptanceCompleted(object? sender, EventArgs e) => completion.TrySetResult();
    }
}
