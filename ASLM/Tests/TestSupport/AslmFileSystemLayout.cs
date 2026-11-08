// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Tests.TestSupport
{
    /// <summary>
    /// Prepares the on-disk layout expected by services that call <c>GetRootDirectory()</c>
    /// (parent of <see cref="AppDomain.CurrentDomain.BaseDirectory"/>).
    /// The layout is the shared test output tree, not a private copy: it is not deleted afterwards and
    /// every instance sees the files of the previous one unless the data folder is reset.
    /// </summary>
    public sealed class AslmFileSystemLayout : IDisposable
    {
        // State

        /// <summary>
        /// Whether disposing the layout deletes the root directory; false because the root is the shared test
        /// output tree.
        /// </summary>
        private readonly bool _ownsRoot;


        // Locations

        /// <summary>
        /// ASLM data root: the parent of the test output directory.
        /// </summary>
        public string Root { get; }

        /// <summary>
        /// Test output directory that plays the role of the deployed <c>App/</c> folder.
        /// </summary>
        public string AppDir { get; }

        /// <summary>
        /// Folder below the root that holds the application's JSON data files.
        /// </summary>
        public string DataAppDir => Path.Combine(Root, "Data", "App");

        /// <summary>
        /// Folder below the root in which installed modules live.
        /// </summary>
        public string ModulesDir => Path.Combine(Root, "Modules");

        /// <summary>
        /// Path of the application data file (settings and user state).
        /// </summary>
        public string AppDataFilePath => Path.Combine(DataAppDir, "ASLM_Data.json");

        /// <summary>
        /// Path of the file that stores user-defined themes.
        /// </summary>
        public string CustomThemesFilePath => Path.Combine(DataAppDir, "ASLM_CustomThemes.json");

        /// <summary>
        /// Path of the file that stores the persisted download state.
        /// </summary>
        public string DownloadsFilePath => Path.Combine(DataAppDir, "ASLM_Downloads.json");

        /// <summary>
        /// Path of the file that stores the module port assignments and the hosts of running modules.
        /// </summary>
        public string PortsFilePath => Path.Combine(DataAppDir, "ASLM_Ports.json");

        /// <summary>
        /// Path of the file that stores persisted notifications.
        /// </summary>
        public string NotificationsFilePath => Path.Combine(DataAppDir, "ASLM_Notifications.json");


        // Initialization

        /// <summary>
        /// Uses the test assembly output directory when it already lives under <c>{root}/App/</c>, so its
        /// parent becomes the root; creates the standard folders and optionally clears the data folder.
        /// </summary>
        /// <param name="resetData">When true, deletes the files in the data folder so the test starts clean.</param>
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


        // Directory preparation

        /// <summary>
        /// Creates the data and modules folders when they do not exist yet.
        /// </summary>
        public void EnsureStandardDirectories()
        {
            Directory.CreateDirectory(DataAppDir);
            Directory.CreateDirectory(ModulesDir);
        }

        /// <summary>
        /// Deletes the files directly inside the data folder, or creates the folder when it is missing.
        /// Subfolders are left untouched.
        /// </summary>
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


        // Seed files

        /// <summary>
        /// Overwrites the application data file with the given raw JSON.
        /// </summary>
        public void WriteAppDataJson(string json) => File.WriteAllText(AppDataFilePath, json);

        /// <summary>
        /// Overwrites the port registry file with the given raw JSON.
        /// </summary>
        public void WritePortsJson(string json) => File.WriteAllText(PortsFilePath, json);


        // Cleanup

        /// <summary>
        /// Deletes the root directory when the layout owns it; otherwise leaves the shared test tree in place.
        /// </summary>
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
}
