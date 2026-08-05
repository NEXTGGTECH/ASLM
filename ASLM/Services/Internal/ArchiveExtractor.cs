// Copyright NEXTGGTECH. Apache License 2.0.

using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Extracts release archives: zip everywhere, plus tar.gz/tgz used by macOS engine and app assets.
    /// </summary>
    /// <remarks>
    /// Tar extraction preserves Unix file modes and symlinks, which zip extraction cannot guarantee -
    /// that is why macOS runtimes (Python, Node.js, Ollama) ship as tarballs.
    /// </remarks>
    public static class ArchiveExtractor
    {
        /// <summary>
        /// Returns whether the archive is a (gzipped) tarball based on its file name.
        /// </summary>
        public static bool IsTarArchive(string archivePath)
        {
            return archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
                || archivePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase)
                || archivePath.EndsWith(".tar", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Extracts one archive into the destination directory, guarding against path traversal.
        /// </summary>
        public static void ExtractToDirectory(string archivePath, string destination, IProgress<string>? log = null)
        {
            Directory.CreateDirectory(destination);

            if (IsTarArchive(archivePath))
            {
                ExtractTar(archivePath, destination);
                return;
            }

            ExtractZip(archivePath, destination, log);
        }

        /// <summary>
        /// Extracts a tarball, decompressing gzip when the file name calls for it.
        /// </summary>
        private static void ExtractTar(string archivePath, string destination)
        {
            // Apple bsdtar puts LIBARCHIVE.xattr.* PAX records into its tarballs, which
            // System.Formats.Tar cannot parse - macOS extraction goes through the system tar.
            if (OperatingSystem.IsMacCatalyst() || OperatingSystem.IsMacOS())
            {
                ExtractTarWithSystemTar(archivePath, destination);
                return;
            }

            using var archiveStream = File.OpenRead(archivePath);

            if (archivePath.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
            {
                TarFile.ExtractToDirectory(archiveStream, destination, overwriteFiles: true);
                return;
            }

            using var gzipStream = new GZipStream(archiveStream, CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gzipStream, destination, overwriteFiles: true);
        }

        /// <summary>
        /// Extracts a tarball with the system bsdtar, which auto-detects compression and
        /// preserves modes and symlinks. Extended attributes stay out of the extracted
        /// files so archives cannot reintroduce quarantine flags.
        /// </summary>
        private static void ExtractTarWithSystemTar(string archivePath, string destination)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "/usr/bin/tar",
                UseShellExecute = false,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("--no-xattrs");
            startInfo.ArgumentList.Add("-xf");
            startInfo.ArgumentList.Add(archivePath);
            startInfo.ArgumentList.Add("-C");
            startInfo.ArgumentList.Add(destination);

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("System tar failed to start.");
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"System tar exited with code {process.ExitCode}: {stderr.Trim()}");
            }
        }

        /// <summary>
        /// Extracts a zip archive entry by entry with Zip Slip prevention.
        /// </summary>
        private static void ExtractZip(string archivePath, string destination, IProgress<string>? log)
        {
            var destPrefix = destination;
            if (!destPrefix.EndsWith(Path.DirectorySeparatorChar) && !destPrefix.EndsWith(Path.AltDirectorySeparatorChar))
            {
                destPrefix += Path.DirectorySeparatorChar;
            }

            using var archive = ZipFile.OpenRead(archivePath);
            foreach (var entry in archive.Entries)
            {
                var targetPath = Path.GetFullPath(Path.Combine(destination, entry.FullName));

                if (!targetPath.StartsWith(destPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Security violation: Zip entry '{entry.FullName}' attempts to extract to '{targetPath}' which is outside the destination directory.");
                }

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(targetPath);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

                try
                {
                    entry.ExtractToFile(targetPath, overwrite: true);
                }
                catch (IOException)
                {
                    // File is locked (e.g. by a running process) - skip it.
                    log?.Report($"  ⚠ Skipped (locked): {entry.FullName}");
                }
            }
        }
    }
}
