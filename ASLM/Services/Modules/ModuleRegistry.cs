// Copyright NEXTGGTECH. Apache License 2.0.

using System.Text.Json;

namespace ASLM.Services.Modules;

/// <summary>
/// Keeps installed module identities separately from the local manifest catalog.
/// The file is simply a JSON array of module ids; an existing empty array is authoritative.
/// </summary>
public sealed class ModuleRegistry
{
    // Host policy, not a module-controlled manifest flag or a user preference.
    public static IReadOnlyList<string> RequiredModuleIds { get; } = Array.AsReadOnly<string>(["aslm-chat"]);

    public static bool IsRequired(string moduleId) =>
        RequiredModuleIds.Contains(moduleId, StringComparer.OrdinalIgnoreCase);

    private static readonly object FileLock = new();
    private readonly string _root;

    public ModuleRegistry() : this(AppRoot.Directory) { }

    internal ModuleRegistry(string root) => _root = Path.GetFullPath(root);

    private string FilePath => Path.Combine(_root, "Data", "App", "ASLM_Modules.json");

    public HashSet<string> ReadIds()
    {
        lock (FileLock)
        {
            if (File.Exists(FilePath))
            {
                // Do not silently overwrite a damaged registry or turn it into an empty installation.
                var ids = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath))
                    ?? throw new InvalidDataException("Invalid module registry.");
                return ids.Where(id => !string.IsNullOrWhiteSpace(id))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }

            var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var manifest in ModuleManifestDiscovery.EnumerateInstalledManifests(Path.Combine(_root, "Modules")))
            {
                var module = ModuleManifestParser.Parse(File.ReadAllText(manifest), manifest);
                if (module.Status.Installed || module.Status.FirstRunCompleted)
                    installed.Add(module.Id);
            }
            Write(installed);
            return installed;
        }
    }

    internal void Add(string id) => Change(id, add: true);
    internal void Remove(string id) => Change(id, add: false);

    private void Change(string id, bool add)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A module id is required.", nameof(id));
        lock (FileLock)
        {
            var ids = ReadIds();
            if (add ? ids.Add(id) : ids.Remove(id)) Write(ids);
        }
    }

    private void Write(HashSet<string> ids)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(
                ids.Order(StringComparer.OrdinalIgnoreCase), new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
