// Copyright NEXTGGTECH. Apache License 2.0.

using Microsoft.Extensions.Logging;

namespace ASLM.Patcher;

/// <summary>
/// Creates the MAUI host used by the standalone patcher.
/// </summary>
public static class MauiProgram
{
    /// <summary>
    /// Configures the patcher MAUI application.
    /// </summary>
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
