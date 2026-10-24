// Copyright NEXTGGTECH. Apache License 2.0.

using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using ZstdSharp;

namespace ASLM.Tests.TestSupport;

/// <summary>
/// Builds the archive flavors ASLM ships (zip, tar, tar.gz, tar.zst) from in-memory entries
/// so extraction tests never depend on checked-in binaries.
/// </summary>
public static class TestArchives
{
    /// <summary>
    /// One archive entry: relative path, UTF-8 text, and optional Unix mode/symlink target.
    /// </summary>
    public sealed record Entry(string Path, string Content, int? UnixMode = null, string? LinkTarget = null);

    /// <summary>
    /// Writes a zip archive to <paramref name="archivePath"/> using forward-slash entry names.
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

    private static string WriteTarCore(string archivePath, Entry[] entries, Action<Stream> write)
    {
        using var stream = File.Create(archivePath);
        write(stream);
        return archivePath;
    }

    private static void WriteTarEntries(Stream stream, Entry[] entries)
    {
        using var writer = new TarWriter(stream, leaveOpen: true);
        foreach (var entry in entries)
        {
            var mode = entry.UnixMode.HasValue ? (UnixFileMode)entry.UnixMode.Value : (UnixFileMode?)null;
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
