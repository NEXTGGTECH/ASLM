// Copyright NEXTGGTECH. Apache License 2.0.

using System.IO.Compression;

namespace ASLM.Installer.Tests.TestSupport
{
    /// <summary>
    /// Builds payload archives laid out like the real one: the root launcher and uninstaller, the App and Patcher
    /// program folders, and the data folders.
    /// </summary>
    internal static class TestPayloadArchive
    {
        /// <summary>
        /// Writes <c>aslm-payload.zip</c> into a directory.
        /// </summary>
        /// <param name="directory">Directory in which <c>aslm-payload.zip</c> is created.</param>
        /// <param name="launcher">Content of the root launcher, such as a real executable with a version
        /// resource; placeholder text when null.</param>
        /// <param name="includeUninstaller">Whether the payload carries <c>uninstall.exe</c> at its root.</param>
        /// <returns>The full path of the created archive.</returns>
        public static string Write(string directory, byte[]? launcher = null, bool includeUninstaller = true)
        {
            var payloadPath = Path.Combine(directory, "aslm-payload.zip");
            using var stream = File.Create(payloadPath);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

            // Adds one entry with binary content to the archive.
            void AddBytes(string entryName, byte[] content)
            {
                using var entryStream = archive.CreateEntry(entryName).Open();
                entryStream.Write(content, 0, content.Length);
            }

            // Adds one text entry to the archive.
            void Add(string entryName, string content)
            {
                var entry = archive.CreateEntry(entryName);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(content);
            }

            // Root shared files, program folders and data folders as the real payload lays them out.
            if (launcher is null)
            {
                Add("ASLM.exe", "launcher binary");
            }
            else
            {
                AddBytes("ASLM.exe", launcher);
            }

            if (includeUninstaller)
            {
                Add("uninstall.exe", "uninstaller binary");
            }

            Add("App/ASLM.dll", "app assembly");
            Add("App/ASLM.runtimeconfig.json", "{}");
            Add("Patcher/ASLM Patcher.exe", "patcher binary");
            Add("Patcher/ASLM Patcher.dll", "patcher assembly");
            Add("Data/App/ASLM_Data.json", """{"user":{"name":"kept"}}""");
            Add("Engines/ollama/engine.bin", "engine");

            return payloadPath;
        }
    }
}
