// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Services.Internal;
using ASLM.Tests.TestSupport;

namespace ASLM.Tests.Services.Internal;

/// <summary>
/// Verifies extraction of every archive format ASLM ships and the security guards around it.
/// </summary>
public sealed class ArchiveExtractorTests : IDisposable
{
    private readonly UniqueAslmRoot _root = new("archive-extractor");

    public void Dispose() => _root.Dispose();

    [Theory]
    [InlineData("payload.zip")]
    [InlineData("payload.tar")]
    [InlineData("payload.tar.gz")]
    [InlineData("payload.tgz")]
    [InlineData("payload.tar.zst")]
    public void ExtractToDirectory_restores_nested_files_in_every_supported_format(string fileName)
    {
        var archivePath = Path.Combine(_root.Root, fileName);
        WriteArchive(archivePath);
        var destination = Path.Combine(_root.Root, "out");

        ArchiveExtractor.ExtractToDirectory(archivePath, destination);

        File.ReadAllText(Path.Combine(destination, "wrapper", "app.bin")).Should().Be("binary");
        File.ReadAllText(Path.Combine(destination, "wrapper", "sub", "config.json")).Should().Be("{}");
    }

    [Fact]
    public void ExtractToDirectory_throws_on_zip_slip_entry()
    {
        var archivePath = Path.Combine(_root.Root, "evil.zip");
        TestArchives.WriteZip(archivePath, new TestArchives.Entry("../outside.txt", "stolen"));

        var act = () => ArchiveExtractor.ExtractToDirectory(archivePath, Path.Combine(_root.Root, "out"));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*outside the destination directory*");
        File.Exists(Path.Combine(_root.Root, "outside.txt")).Should().BeFalse();
    }

    [Fact]
    public void ExtractToDirectory_skips_locked_files_and_continues_remaining_entries()
    {
        var archivePath = Path.Combine(_root.Root, "locked.zip");
        TestArchives.WriteZip(archivePath,
            new TestArchives.Entry("held.txt", "locked"),
            new TestArchives.Entry("free.txt", "fine"));
        var destination = Path.Combine(_root.Root, "out");
        Directory.CreateDirectory(destination);

        // Hold the target file with no sharing so extraction of that entry fails with IOException.
        var heldPath = Path.Combine(destination, "held.txt");
        using (var held = File.Open(heldPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            held.Write("old"u8);
            var skipped = new List<string>();
            ArchiveExtractor.ExtractToDirectory(archivePath, destination, new Progress<string>(skipped.Add));

            skipped.Should().Contain(s => s.Contains("held.txt"));
            File.ReadAllText(Path.Combine(destination, "free.txt")).Should().Be("fine");
        }
    }

    [Theory]
    [InlineData("payload.tar.gz", true)]
    [InlineData("payload.tar.zst", true)]
    [InlineData("payload.tgz", true)]
    [InlineData("payload.tar", true)]
    [InlineData("payload.zip", false)]
    [InlineData("payload.exe", false)]
    public void IsTarArchive_matches_supported_extensions(string fileName, bool expected)
    {
        ArchiveExtractor.IsTarArchive(Path.Combine(_root.Root, fileName)).Should().Be(expected);
    }

    private static void WriteArchive(string archivePath)
    {
        var entries = new[]
        {
            new TestArchives.Entry("wrapper/app.bin", "binary"),
            new TestArchives.Entry("wrapper/sub/config.json", "{}")
        };

        if (archivePath.EndsWith(".zip"))
        {
            TestArchives.WriteZip(archivePath, entries);
        }
        else if (archivePath.EndsWith(".tar"))
        {
            TestArchives.WriteTar(archivePath, entries);
        }
        else if (archivePath.EndsWith(".tar.zst"))
        {
            TestArchives.WriteTarZst(archivePath, entries);
        }
        else
        {
            TestArchives.WriteTarGz(archivePath, entries);
        }
    }
}
