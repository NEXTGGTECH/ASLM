// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Models;

namespace ASLM.Controls.Modules;

/// <summary>
/// Shares the download catalog card between Downloads and first-run module selection.
/// </summary>
public partial class ModuleCatalogCardView : ContentView
{
    public event EventHandler<ModuleConfig>? ModuleSelected;

    /// <summary>
    /// Creates the card with an optional selection checkbox outside its navigation target.
    /// </summary>
    public ModuleCatalogCardView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Opens module information without changing the separate installation selection.
    /// </summary>
    private void OnModuleTapped(object? sender, TappedEventArgs e)
    {
        if (BindingContext is ModuleCatalogCardViewModel card)
        {
            ModuleSelected?.Invoke(this, card.Module);
        }
    }
}
