// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Tests.TestSupport;

/// <summary>
/// Prepares the on-disk layout expected by services that call <c>GetRootDirectory()</c>
/// (parent of <see cref="AppDomain.CurrentDomain.BaseDirectory"/>).
/// </summary>
public sealed class AslmFileSystemLayout : IDisposable
{
    private readonly bool _ownsRoot;

    public string Root { get; }

    public string AppDir { get; }

    public string DataAppDir => Path.Combine(Root, "Data", "App");

    public string ModulesDir => Path.Combine(Root, "Modules");

    public string AppDataFilePath => Path.Combine(DataAppDir, "ASLM_Data.json");

    public string CustomThemesFilePath => Path.Combine(DataAppDir, "ASLM_CustomThemes.json");

    public string DownloadsFilePath => Path.Combine(DataAppDir, "ASLM_Downloads.json");

    public string PortsFilePath => Path.Combine(DataAppDir, "ASLM_Ports.json");

    public string NotificationsFilePath => Path.Combine(DataAppDir, "ASLM_Notifications.json");

    /// <summary>
    /// Uses the test assembly output directory when it already lives under <c>{root}/App/</c>.
    /// </summary>
    public AslmFileSystemLayout(bool resetData = true)
    {
        AppDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        Root = Directory.GetParent(AppDir)?.FullName
            ?? throw new InvalidOperationException(
                $"Test output must be under {{root}}/App; actual BaseDirectory: {AppDir}");

        _ownsRoot = false;
        EnsureStandardDirectories();

        if (resetData)
        {
            ResetDataAppDirectory();
        }
    }

    public void EnsureStandardDirectories()
    {
        Directory.CreateDirectory(DataAppDir);
        Directory.CreateDirectory(ModulesDir);
    }

    public void ResetDataAppDirectory()
    {
        if (Directory.Exists(DataAppDir))
        {
            foreach (var file in Directory.EnumerateFiles(DataAppDir))
            {
                File.Delete(file);
            }
        }
        else
        {
            Directory.CreateDirectory(DataAppDir);
        }
    }

    public void WriteAppDataJson(string json) => File.WriteAllText(AppDataFilePath, json);

    public void WritePortsJson(string json) => File.WriteAllText(PortsFilePath, json);

    public void Dispose()
    {
        if (_ownsRoot && Directory.Exists(Root))
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // Best effort cleanup for transient roots only.
            }
        }
    }
}
