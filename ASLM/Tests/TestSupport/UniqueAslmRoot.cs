// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Services.Internal;

namespace ASLM.Tests.TestSupport;

/// <summary>
/// Gives one test a private throwaway ASLM root directory: installs it as the process
/// <c>ASLM_ROOT</c> and resets the cached <see cref="AppRoot"/> value, so every service
/// constructed while the fixture is alive reads and writes only under <see cref="Root"/>.
/// Create this fixture before constructing any service in the test.
/// </summary>
public sealed class UniqueAslmRoot : IDisposable
{
    private readonly string? _previousRootOverride;

    /// <summary>
    /// Gets the isolated application root directory.
    /// </summary>
    public string Root { get; }

    /// <summary>
    /// Gets <c>{root}/Data/App</c> where ASLM stores its JSON state files.
    /// </summary>
    public string DataAppDir => Path.Combine(Root, "Data", "App");

    /// <summary>
    /// Gets <c>{root}/Modules</c> where module manifests live.
    /// </summary>
    public string ModulesDir => Path.Combine(Root, "Modules");

    /// <summary>
    /// Gets <c>{root}/Engines</c> where engine manifests live.
    /// </summary>
    public string EnginesDir => Path.Combine(Root, "Engines");

    /// <summary>
    /// Gets <c>{root}/Models</c> where engine model directories live.
    /// </summary>
    public string ModelsDir => Path.Combine(Root, "Models");

    /// <summary>
    /// Gets the persisted application data file path.
    /// </summary>
    public string AppDataFilePath => Path.Combine(DataAppDir, "ASLM_Data.json");

    /// <summary>
    /// Gets the persisted port map file path.
    /// </summary>
    public string PortsFilePath => Path.Combine(DataAppDir, "ASLM_Ports.json");

    /// <summary>
    /// Creates the root directory, installs it as the process root, and prepares the
    /// standard subdirectories services expect to find or create.
    /// </summary>
    public UniqueAslmRoot(string? name = null)
    {
        var id = string.IsNullOrWhiteSpace(name) ? Guid.NewGuid().ToString("N") : $"{name}-{Guid.NewGuid():N}";
        Root = Path.Combine(Path.GetTempPath(), "ASLM-Tests", id);

        _previousRootOverride = Environment.GetEnvironmentVariable("ASLM_ROOT");
        Environment.SetEnvironmentVariable("ASLM_ROOT", Root);
        AppRoot.ResetForTests();

        Directory.CreateDirectory(DataAppDir);
        Directory.CreateDirectory(ModulesDir);
        Directory.CreateDirectory(EnginesDir);
    }

    /// <summary>
    /// Writes a raw JSON document into <c>Data/App</c>.
    /// </summary>
    public string WriteDataAppFile(string fileName, string json)
    {
        var path = Path.Combine(DataAppDir, fileName);
        File.WriteAllText(path, json);
        return path;
    }

    /// <summary>
    /// Restores the previous process root and deletes the throwaway directory best-effort.
    /// </summary>
    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ASLM_ROOT", _previousRootOverride);
        AppRoot.ResetForTests();

        TryDeleteDirectory(Root, attempts: 3);
    }

    private static void TryDeleteDirectory(string path, int attempts)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }

                return;
            }
            catch (IOException)
            {
                // Files can still be held by a just-exited module process; retry once drained.
            }
            catch (UnauthorizedAccessException)
            {
            }

            Thread.Sleep(200);
        }
    }
}
