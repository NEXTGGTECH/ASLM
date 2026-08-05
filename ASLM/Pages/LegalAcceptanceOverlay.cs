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
    /// Presents the legal acceptance overlay when manual review is still required at startup.
    /// </summary>
    public static void PresentIfRequired(
        ContentView overlayContainer,
        LegalAcceptanceService legalAcceptance,
        IServiceProvider services)
    {
        if (!legalAcceptance.ManualAcceptanceRequired)
        {
            return;
        }

        var view = services.GetRequiredService<LegalAcceptanceView>();
        view.AcceptanceCompleted -= OnAcceptanceCompleted;
        view.AcceptanceCompleted += OnAcceptanceCompleted;

        if (view is ILocalizable localizable)
        {
            localizable.ApplyLocalization();
        }

        overlayContainer.Content = view;
        overlayContainer.IsVisible = true;
        _ = view.OpenAsync();
        return;

        void OnAcceptanceCompleted(object? sender, EventArgs e)
        {
            view.AcceptanceCompleted -= OnAcceptanceCompleted;
            overlayContainer.IsVisible = false;
            overlayContainer.Content = null;
            legalAcceptance.ClearManualAcceptanceRequired();
        }
    }
}
