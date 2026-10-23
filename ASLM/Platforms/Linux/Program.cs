// Copyright NEXTGGTECH. Apache License 2.0.

using Microsoft.Maui.Platforms.Linux.Gtk4.Platform;

namespace ASLM;

/// <summary>
/// Owns the GTK application loop while reusing ASLM's pages and service registrations.
/// </summary>
public sealed class Program : GtkMauiApplication
{
    // A portable preview must not create a desktop shortcut pointing into a temporary build directory.
    protected override bool CreateDesktopEntry => false;

    /// <summary>
    /// Creates the shared application on the GTK thread.
    /// </summary>
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    /// <summary>
    /// Starts the desktop application without changing its working directory.
    /// </summary>
    public static void Main(string[] args)
    {
        // Do not open a second UI while the old instance still owns its modules or API port.
        if (!LinuxAppRelauncher.WaitForPreviousInstance(ref args))
        {
            Console.Error.WriteLine("ASLM restart aborted: the previous instance did not exit.");
            Environment.ExitCode = 1;
            return;
        }

        // Opening App/ASLM directly must use the same pending-update gate as opening the packaged launcher.
        if (LinuxAppRelauncher.TryHandOffPendingUpdate()) return;

        // The GTK font backend uses LocalApplicationData. Ensure it exists so .NET does not return an empty path.
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);
        if (!string.IsNullOrEmpty(localData)) Directory.CreateDirectory(localData);

        // Select the display backend before GTK opens its connection; changing it after startup has no effect.
        ConfigureDisplayBackend();

        // Seed before constructing services, which resolve data paths in their constructors.
        LinuxAppDataSeeder.EnsureSeeded(Path.Combine(AppContext.BaseDirectory, "seed"), AppRoot.Directory);
        var application = new Program();
        application.Run(args);
    }

    // Display compatibility

    /// <summary>
    /// Avoids WSLg's unresponsive Wayland popup surfaces without changing a normal Linux desktop or explicit overrides.
    /// </summary>
    internal static void ConfigureDisplayBackend()
    {
        var isWslg = File.Exists("/mnt/wslg/versions.txt") &&
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WSL_DISTRO_NAME"));
        if (!ShouldPreferWslgX11(isWslg, Environment.GetEnvironmentVariable("GDK_BACKEND"),
            Environment.GetEnvironmentVariable("DISPLAY"))) return;

        // This setting belongs only to ASLM's process; no shell profiles, WSL settings or desktop configuration are edited.
        Environment.SetEnvironmentVariable("GDK_BACKEND", "x11");
        // .NET's managed environment cache does not update libc getenv. Initialize the bindings and choose
        // the native backend through GDK itself, before GtkMauiApplication opens a display connection.
        Gdk.Module.Initialize();
        Gdk.Functions.SetAllowedBackends("x11");
        Console.Error.WriteLine("[ASLM] Using X11 for WSLg native popup compatibility");
    }

    /// <summary>
    /// Selects WSLg's XWayland connection only when it is available and the user did not choose a backend.
    /// </summary>
    internal static bool ShouldPreferWslgX11(bool isWslg, string? configuredBackend, string? display) =>
        isWslg && configuredBackend == null && !string.IsNullOrWhiteSpace(display);
}
