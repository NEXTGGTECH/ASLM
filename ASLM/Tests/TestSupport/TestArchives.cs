// Copyright NEXTGGTECH. Apache License 2.0.

using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using ZstdSharp;

namespace ASLM.Tests.TestSupport
{
    /// <summary>
    /// Builds the archive flavors ASLM ships (zip, tar, tar.gz, tar.zst) from in-memory entries
    /// so extraction tests never depend on checked-in binaries.
    /// </summary>
    public static class TestArchives
    {
        // Entry model

        /// <summary>
        /// One archive entry: relative path, UTF-8 text, and optional Unix mode/symlink target.
        /// In tar archives an entry with a link target becomes a symbolic link and its content is ignored;
        /// zip archives ignore the mode and the link target.
        /// </summary>
        public sealed record Entry(string Path, string Content, int? UnixMode = null, string? LinkTarget = null);


        // Archive writers

        /// <summary>
        /// Writes a zip archive to <paramref name="archivePath"/>; entry names are taken from
        /// <see cref="Entry.Path"/> as given, so callers pass forward-slash names. Returns the archive path.
        /// </summary>
        public static string WriteZip(string archivePath, params Entry[] entries)
        {
            using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
            foreach (var entry in entries)
            {
                var zipEntry = archive.CreateEntry(entry.Path, CompressionLevel.Optimal);
                using var writer = new StreamWriter(zipEntry.Open(), Encoding.UTF8);
                writer.Write(entry.Content);
            }

            return archivePath;
        }

        /// <summary>
        /// Writes an uncompressed tar archive.
        /// </summary>
        public static string WriteTar(string archivePath, params Entry[] entries) =>
            WriteTarCore(archivePath, entries, stream => WriteTarEntries(stream, entries));

        /// <summary>
        /// Writes a gzip-compressed tar archive.
        /// </summary>
        public static string WriteTarGz(string archivePath, params Entry[] entries) =>
            WriteTarCore(archivePath, entries, stream =>
            {
                using var gzip = new GZipStream(stream, CompressionLevel.Optimal, leaveOpen: true);
                WriteTarEntries(gzip, entries);
            });

        /// <summary>
        /// Writes a zstandard-compressed tar archive (the Linux Ollama package format).
        /// </summary>
        public static string WriteTarZst(string archivePath, params Entry[] entries) =>
            WriteTarCore(archivePath, entries, stream =>
            {
                using var zstd = new CompressionStream(stream);
                WriteTarEntries(zstd, entries);
            });


        // Tar helpers

        /// <summary>
        /// Creates the archive file, lets the callback write the (optionally compressed) tar stream into it,
        /// and returns the archive path.
        /// </summary>
        /// <param name="archivePath">Path of the archive file to create or overwrite.</param>
        /// <param name="entries">Not used by this method; the callback already captures the entries.</param>
        /// <param name="write">Writes the archive content into the opened file stream.</param>
        private static string WriteTarCore(string archivePath, Entry[] entries, Action<Stream> write)
        {
            using var stream = File.Create(archivePath);
            write(stream);
            return archivePath;
        }

        /// <summary>
        /// Writes the entries as PAX tar entries to the stream, leaving the stream open.
        /// Disposing the tar writer at the end writes the archive terminator.
        /// </summary>
        private static void WriteTarEntries(Stream stream, Entry[] entries)
        {
            using var writer = new TarWriter(stream, leaveOpen: true);
            foreach (var entry in entries)
            {
                var mode = entry.UnixMode.HasValue ? (UnixFileMode)entry.UnixMode.Value : (UnixFileMode?)null;

                // Entries with a link target become symbolic links; their content is not stored.
                if (entry.LinkTarget != null)
                {
                    var link = new PaxTarEntry(TarEntryType.SymbolicLink, entry.Path)
                    {
                        LinkName = entry.LinkTarget
                    };
                    if (mode.HasValue)
                    {
                        link.Mode = mode.Value;
                    }

                    writer.WriteEntry(link);
                    continue;
                }

                // Everything else is a regular file holding the UTF-8 content.
                var tarEntry = new PaxTarEntry(TarEntryType.RegularFile, entry.Path)
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes(entry.Content))
                };
                if (mode.HasValue)
                {
                    tarEntry.Mode = mode.Value;
                }

                writer.WriteEntry(tarEntry);
            }
        }
    }
}
