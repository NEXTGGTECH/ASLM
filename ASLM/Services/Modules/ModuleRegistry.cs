// Copyright NEXTGGTECH. Apache License 2.0.

using System.Text.Json;

namespace ASLM.Services.Modules
{
    /// <summary>
    /// Keeps installed module identities separately from the local manifest catalog.
    /// The file is simply a JSON array of module ids; an existing empty array is authoritative.
    /// </summary>
    public sealed class ModuleRegistry
    {
        // Required modules

        /// <summary>
        /// Ids of modules that must stay installed. This is host policy, not a module-controlled manifest flag
        /// or a user preference.
        /// </summary>
        public static IReadOnlyList<string> RequiredModuleIds { get; } = Array.AsReadOnly<string>(["aslm-chat"]);

        /// <summary>
        /// Returns whether the module is part of the required set (case-insensitive id comparison).
        /// </summary>
        public static bool IsRequired(string moduleId) =>
            RequiredModuleIds.Contains(moduleId, StringComparer.OrdinalIgnoreCase);


        // Fields

        /// <summary>
        /// Process-wide lock that serializes every registry read-modify-write, across all instances.
        /// </summary>
        private static readonly object FileLock = new();

        /// <summary>
        /// Absolute ASLM root directory that contains <c>Data/</c> and <c>Modules/</c>.
        /// </summary>
        private readonly string _root;


        // Initialization

        /// <summary>
        /// Creates a registry over the application root directory.
        /// </summary>
        public ModuleRegistry() : this(AppRoot.Directory) { }

        /// <summary>
        /// Creates a registry over an explicit root directory (test isolation).
        /// </summary>
        internal ModuleRegistry(string root) => _root = Path.GetFullPath(root);


        // Registry access

        /// <summary>
        /// Absolute path of the registry file <c>Data/App/ASLM_Modules.json</c>.
        /// </summary>
        private string FilePath => Path.Combine(_root, "Data", "App", "ASLM_Modules.json");

        /// <summary>
        /// Returns the ids of all installed modules. When the registry file does not exist yet, it is created from
        /// the installed manifests, counting a module as installed once it is installed or its first run completed.
        /// </summary>
        /// <exception cref="InvalidDataException">Thrown when the registry file exists but is not a JSON array.</exception>
        public HashSet<string> ReadIds()
        {
            lock (FileLock)
            {
                // An existing file is authoritative, even when it holds an empty array.
                if (File.Exists(FilePath))
                {
                    // Do not silently overwrite a damaged registry or turn it into an empty installation.
                    var ids = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath))
                        ?? throw new InvalidDataException("Invalid module registry.");
                    return ids.Where(id => !string.IsNullOrWhiteSpace(id))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                }

                // First read without a registry file: derive the installed set from the local manifests and persist it.
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

        /// <summary>
        /// Registers a module id as installed; does nothing when it is already registered.
        /// </summary>
        internal void Add(string id) => Change(id, add: true);

        /// <summary>
        /// Removes a module id from the registry; does nothing when it is not registered.
        /// </summary>
        internal void Remove(string id) => Change(id, add: false);

        /// <summary>
        /// Adds or removes one id and rewrites the file only when the set actually changed.
        /// </summary>
        private void Change(string id, bool add)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A module id is required.", nameof(id));
            lock (FileLock)
            {
                var ids = ReadIds();
                if (add ? ids.Add(id) : ids.Remove(id)) Write(ids);
            }
        }

        /// <summary>
        /// Persists the id set as a sorted JSON array. The data goes to a temporary file that then replaces the
        /// registry, so a crash never leaves a half-written file.
        /// </summary>
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
                // The temporary file only remains when writing or moving failed.
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }
}
