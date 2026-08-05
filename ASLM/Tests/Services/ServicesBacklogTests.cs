// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Tests.Services;

/// <summary>
/// Documents public service APIs that require OS processes, live HTTP, or MAUI UI hosts.
/// </summary>
public sealed class ServicesBacklogTests
{
    [Fact(Skip = "Requires live HttpListener and proxy loop (AslmMirrorServer.StartAsync/StopAsync).")]
    public void AslmMirrorServer_lifecycle() { }

    [Fact(Skip = "Requires live HttpListener (AslmModuleInteropServer.StartAsync).")]
    public void AslmModuleInteropServer_lifecycle() { }

    [Fact(Skip = "Requires module bridge subprocess stdio (ModuleDownloadBridge.InvokeAsync).")]
    public void ModuleDownloadBridge_invoke() { }

    [Fact(Skip = "Requires full module runner/installer graph (ModuleLaunchCoordinator).")]
    public void ModuleLaunchCoordinator_launch() { }

    [Fact(Skip = "Requires real module processes and ports (ModuleRunner.ExecuteRunAsync).")]
    public void ModuleRunner_execute_run() { }

    [Fact(Skip = "Requires install pipeline with archives and processes (DownloadInstaller.InstallAsync).")]
    public void DownloadInstaller_install() { }

    [Fact(Skip = "Requires Win32 Job Object child tracking (ProcessTracker.AddProcess).")]
    public void ProcessTracker_job_object() { }

    [Fact(Skip = "Requires Toolhelp process snapshot (ProcessSnapshotReader.GetSnapshot).")]
    public void ProcessSnapshotReader_snapshot() { }

    [Fact(Skip = "Requires breakaway CreateProcess (DetachedProcessStarter.TryStartBreakawayProcess).")]
    public void DetachedProcessStarter_breakaway() { }

    [Fact(Skip = "Requires MAUI Application.Current resources (ThemeService.ApplyFromSettings).")]
    public void ThemeService_apply() { }

    [Fact(Skip = "Requires WinUI handler host (ConsoleOutputViewHandler).")]
    public void ConsoleOutputViewHandler_mapper() { }

    [Fact(Skip = "Requires registry or MAUI theme (OsAppThemeReader.IsWindowsAppDarkMode).")]
    public void OsAppThemeReader_dark_mode() { }

    [Fact(Skip = "Requires SkiaSharp and packaged assets (PackagedIconTintCache.Get).")]
    public void PackagedIconTintCache_tint() { }

    [Fact(Skip = "Private nested ProxyRoute factories; covered indirectly via HTTP integration backlog.")]
    public void AslmMirrorServer_proxy_route_resolution() { }
}
