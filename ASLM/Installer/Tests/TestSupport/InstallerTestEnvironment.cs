// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Installer.Tests.TestSupport;

/// <summary>
/// Creates an isolated temp directory and redirects TMP/TEMP so staging paths and bootstrapper
/// extraction directories land inside it, where tests can assert cleanup.
/// </summary>
internal sealed class TempScope : IDisposable
{
    private readonly string? _originalTmp;
    private readonly string? _originalTemp;

    public string Dir { get; }

    public TempScope()
    {
        Dir = Path.Combine(Path.GetTempPath(), "aslm-installer-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Dir);
        _originalTmp = Environment.GetEnvironmentVariable("TMP");
        _originalTemp = Environment.GetEnvironmentVariable("TEMP");
        Environment.SetEnvironmentVariable("TMP", Dir);
        Environment.SetEnvironmentVariable("TEMP", Dir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TMP", _originalTmp);
        Environment.SetEnvironmentVariable("TEMP", _originalTemp);
        TryDelete(Dir);
    }

    /// <summary>
    /// Returns the redirected staging root that <c>InstallerService.CreateStagingPath</c> uses.
    /// </summary>
    public string StagingRoot => Path.Combine(Dir, "ASLM", "InstallStaging");

    /// <summary>
    /// Returns the redirected bootstrapper extraction root under the redirected temp path.
    /// </summary>
    public string BootstrapperRoot => Path.Combine(Dir, "ASLM", "Installer");

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }
}

/// <summary>
/// Temporarily replaces one process environment variable and restores its original value.
/// </summary>
internal sealed class EnvironmentScope : IDisposable
{
    private readonly string _name;
    private readonly string? _original;

    public EnvironmentScope(string name, string? value)
    {
        _name = name;
        _original = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(_name, _original);
    }
}
