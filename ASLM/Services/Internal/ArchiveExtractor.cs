// Copyright NEXTGGTECH. Apache License 2.0.

using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using ZstdSharp;

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Extracts ZIP and TAR release archives, including GZIP and Zstandard engine packages.
    /// </summary>
    /// <remarks>
    /// Tar extraction preserves Unix file modes and symlinks, which zip extraction cannot guarantee -
    /// that is why macOS runtimes (Python, Node.js, Ollama) ship as tarballs.
    /// </remarks>
    public static class ArchiveExtractor
    {
        // Entry points

        /// <summary>
        /// Identifies supported uncompressed, GZIP and Zstandard TAR file names by their extension.
        /// </summary>
        /// <param name="archivePath">Archive file path or name; only the extension is inspected.</param>
        /// <returns>True for <c>.tar</c>, <c>.tar.gz</c>, <c>.tgz</c> and <c>.tar.zst</c> names; otherwise false.</returns>
        public static bool IsTarArchive(string archivePath)
        {
            return archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
                || archivePath.EndsWith(".tar.zst", StringComparison.OrdinalIgnoreCase)
                || archivePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase)
                || archivePath.EndsWith(".tar", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Extracts one archive into the destination directory, guarding against path traversal.
        /// </summary>
        /// <param name="archivePath">Path of the ZIP or TAR archive; the extension selects the format.</param>
        /// <param name="destination">Directory that receives the contents; it is created when missing.</param>
        /// <param name="log">Optional receiver of warnings; only ZIP extraction reports (skipped locked files).</param>
        public static void ExtractToDirectory(string archivePath, string destination, IProgress<string>? log = null)
        {
            Directory.CreateDirectory(destination);

            // TAR packages keep Unix modes and symlinks; everything else is treated as a ZIP archive.
            if (IsTarArchive(archivePath))
            {
                ExtractTar(archivePath, destination);
                return;
            }

            ExtractZip(archivePath, destination, log);
        }


        // TAR extraction

        /// <summary>
        /// Decompresses TAR packages while retaining the existing macOS extended-attribute handling.
        /// </summary>
        private static void ExtractTar(string archivePath, string destination)
        {
            // Linux Ollama packages use Zstandard. Stream decompression avoids another full archive on disk
            // and retains TarFile's path/link validation, Unix modes and symbolic-link handling.
            if (archivePath.EndsWith(".tar.zst", StringComparison.OrdinalIgnoreCase))
            {
                using var compressed = File.OpenRead(archivePath);
                using var decompressed = new DecompressionStream(compressed);
                TarFile.ExtractToDirectory(decompressed, destination, overwriteFiles: true);
                return;
            }

            // Apple bsdtar puts LIBARCHIVE.xattr.* PAX records into its tarballs, which
            // System.Formats.Tar cannot parse - macOS extraction goes through the system tar.
            if (OperatingSystem.IsMacCatalyst() || OperatingSystem.IsMacOS())
            {
                ExtractTarWithSystemTar(archivePath, destination);
                return;
            }

            // Plain tarballs are read as they are; the remaining names (.tar.gz, .tgz) are GZIP-compressed.
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
            // Paths go through ArgumentList so no shell quoting is needed: --no-xattrs drops extended attributes,
            // -xf extracts the archive and -C selects the destination directory.
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

            // Read stderr to the end before waiting for exit, then report it if tar failed.
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


        // ZIP extraction

        /// <summary>
        /// Extracts a zip archive entry by entry with Zip Slip prevention.
        /// </summary>
        private static void ExtractZip(string archivePath, string destination, IProgress<string>? log)
        {
            // Compare against the destination with a trailing separator so a sibling folder
            // that merely shares the name prefix is not accepted as inside it.
            var destPrefix = destination;
            if (!destPrefix.EndsWith(Path.DirectorySeparatorChar) && !destPrefix.EndsWith(Path.AltDirectorySeparatorChar))
            {
                destPrefix += Path.DirectorySeparatorChar;
            }

            using var archive = ZipFile.OpenRead(archivePath);
            foreach (var entry in archive.Entries)
            {
                // Reject entries whose resolved path escapes the destination (Zip Slip).
                var targetPath = Path.GetFullPath(Path.Combine(destination, entry.FullName));

                if (!targetPath.StartsWith(destPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Security violation: Zip entry '{entry.FullName}' attempts to extract to '{targetPath}' which is outside the destination directory.");
                }

                // An entry without a file name is a directory entry.
                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(targetPath);
                    continue;
                }

                // Archives may omit directory entries, so make sure the parent folder exists.
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
