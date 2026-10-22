// Copyright NEXTGGTECH. Apache License 2.0.

using Microsoft.Maui.Platforms.Linux.Gtk4.Handlers;

namespace ASLM.Services.Controls;

/// <summary>
/// Preserves WebKit sandboxing and forwards native navigation decisions to ASLM's navigation policy.
/// </summary>
public sealed class LinuxWebViewHandler : WebViewHandler
{
    private static bool _initialized;

    /// <summary>
    /// Initializes WebKit without the preview backend's automatic sandbox-disable workaround.
    /// </summary>
    protected override WebKit.WebView CreatePlatformView()
    {
        // GTK creates all handlers on one thread. Do not silently relax WebKit security on restricted hosts.
        if (!_initialized)
        {
            WebKit.Module.Initialize();
            _initialized = true;
        }
        return new WebKit.WebView();
    }

    /// <summary>
    /// Observes real navigation, including links and redirects not initiated by MAUI's Source property.
    /// </summary>
    protected override void ConnectHandler(WebKit.WebView platformView)
    {
        base.ConnectHandler(platformView);
        platformView.OnDecidePolicy += OnDecidePolicy;
    }

    /// <summary>
    /// Removes native callbacks before releasing a browser.
    /// </summary>
    protected override void DisconnectHandler(WebKit.WebView platformView)
    {
        platformView.OnDecidePolicy -= OnDecidePolicy;
        base.DisconnectHandler(platformView);
    }

    /// <summary>
    /// Cancels navigation rejected by the owning page, preserving its existing external-link handling.
    /// </summary>
    private bool OnDecidePolicy(WebKit.WebView sender, WebKit.WebView.DecidePolicySignalArgs args)
    {
        if (args.Decision is not WebKit.NavigationPolicyDecision navigation) return false;
        var uri = navigation.GetNavigationAction().GetRequest().GetUri();
        if (VirtualView?.Navigating(WebNavigationEvent.NewPage, uri) != true) return false;
        navigation.Ignore();
        return true;
    }
}
