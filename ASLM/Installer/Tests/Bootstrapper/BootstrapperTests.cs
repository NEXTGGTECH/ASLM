// Copyright NEXTGGTECH. Apache License 2.0.

using System.IO.Compression;
using System.Text;
using ASLM.Installer.Bootstrapper;
using ASLM.Installer.Tests.TestSupport;

namespace ASLM.Installer.Tests.Bootstrapper;

/// <summary>
/// Verifies the CLI bootstrapper flow: extraction, payload handoff, argument forwarding, and cleanup.
/// </summary>
public sealed class BootstrapperTests
{
    private const string InstallerZipResourceName = "installer-ui.zip";
    private const string PayloadResourceName = "aslm-payload.zip";
    private const string InstallerExecutableName = "ASLM-Installer.exe";

    /// <summary>
    /// Builds the embedded installer-UI zip around a native, dependency-free stub executable.
    /// </summary>
    private static byte[] CreateInstallerUiZip(params (string EntryName, byte[] Content)[] entries)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (entryName, content) in entries)
            {
                var entry = archive.CreateEntry(entryName);
                using var stream = entry.Open();
                stream.Write(content, 0, content.Length);
            }
            // Directory-only entries must be skipped by the extractor.
            archive.CreateEntry("nested/");
        }
        return output.ToArray();
    }

    /// <summary>
    /// Copies cmd.exe, which is native and standalone, as the installer UI stub.
    /// </summary>
    private static byte[] ReadCmdStub() =>
        File.ReadAllBytes(Path.Combine(Environment.SystemDirectory, "cmd.exe"));

    private static MemoryStream Resource(byte[] bytes) => new(bytes);

    [Fact]
    public void Run_starts_the_installer_with_payload_path_and_forwards_arguments_and_exit_code()
    {
        using var scope = new TempScope();
        var payload = Encoding.UTF8.GetBytes("payload-bytes");
        var marker = Path.Combine(scope.Dir, "handoff.marker");
        var resources = new Dictionary<string, byte[]>
        {
            [InstallerZipResourceName] = CreateInstallerUiZip((InstallerExecutableName, ReadCmdStub())),
            [PayloadResourceName] = payload,
        };
        Stream? OpenResource(string name) =>
            resources.TryGetValue(name, out var bytes) ? Resource(bytes) : null;

        // cmd /d /c writes the handed-off payload path into the marker file and exits with code 7.
        // The marker path stays unquoted: the fixture temp directory contains no spaces, and
        // cmd /c would otherwise keep the inner quotes and redirect into a quoted file name.
        var forwardedCommand = $"echo %ASLM_INSTALLER_PAYLOAD_PATH%>{marker}&exit /b 7";
        var exitCode = Program.Run(new[] { "/d", "/c", forwardedCommand }, OpenResource);

        exitCode.Should().Be(7, "the bootstrapper must return the installer UI exit code");
        File.ReadAllText(marker).Trim().Trim('"').Should().NotBeNullOrEmpty();
        Directory.EnumerateFileSystemEntries(scope.BootstrapperRoot).Should().BeEmpty(
            "the temporary extraction directory must be removed after the UI exits");
    }

    [Fact]
    public void Run_reports_missing_installer_executable_and_cleans_up()
    {
        using var scope = new TempScope();
        var errors = new List<Exception>();
        var resources = new Dictionary<string, byte[]>
        {
            [InstallerZipResourceName] = CreateInstallerUiZip(("readme.txt", Encoding.UTF8.GetBytes("no exe"))),
            [PayloadResourceName] = Encoding.UTF8.GetBytes("payload"),
        };
        Stream? OpenResource(string name) =>
            resources.TryGetValue(name, out var bytes) ? Resource(bytes) : null;

        var exitCode = Program.Run([], OpenResource, errors.Add);

        exitCode.Should().Be(1);
        errors.Should().ContainSingle(error => error is FileNotFoundException);
        Directory.EnumerateFileSystemEntries(scope.BootstrapperRoot).Should().BeEmpty();
    }

    [Fact]
    public void Run_rejects_installer_resources_that_escape_the_extraction_directory()
    {
        using var scope = new TempScope();
        var errors = new List<Exception>();
        var resources = new Dictionary<string, byte[]>
        {
            [InstallerZipResourceName] = CreateInstallerUiZip(("../escaped.exe", ReadCmdStub())),
            [PayloadResourceName] = Encoding.UTF8.GetBytes("payload"),
        };
        Stream? OpenResource(string name) =>
            resources.TryGetValue(name, out var bytes) ? Resource(bytes) : null;

        var exitCode = Program.Run([], OpenResource, errors.Add);

        exitCode.Should().Be(1);
        errors.Should().ContainSingle(error => error is InvalidOperationException);
        File.Exists(Path.Combine(scope.Dir, "escaped.exe")).Should().BeFalse();
    }

    [Fact]
    public void Run_reports_missing_embedded_resources()
    {
        var errors = new List<Exception>();
        Stream? OpenNone(string name) => null;

        var exitCode = Program.Run([], OpenNone, errors.Add);

        exitCode.Should().Be(1);
        errors.Should().ContainSingle(error => error is InvalidOperationException
            && error.Message.Contains(InstallerZipResourceName));
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("with space", "\"with space\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    public void QuoteArgument_quotes_only_arguments_that_need_it(string argument, string expected)
    {
        Program.QuoteArgument(argument).Should().Be(expected);
    }

    [Theory]
    [InlineData(@"C:\base", @"C:\base\child\file.txt", true)]
    [InlineData(@"C:\base", @"C:\base", true)]
    [InlineData(@"C:\base", @"C:\base2\file.txt", false)]
    [InlineData(@"C:\base", @"D:\base\file.txt", false)]
    public void IsChildPath_compares_complete_path_segments(string parent, string child, bool expected)
    {
        Program.IsChildPath(parent, child).Should().Be(expected);
    }

    [Fact]
    public void ExtractInstallerUi_skips_directory_entries_and_overwrites_existing_files()
    {
        using var scope = new TempScope();
        var destination = Path.Combine(scope.Dir, "ui");
        Directory.CreateDirectory(Path.Combine(destination, "nested"));
        File.WriteAllText(Path.Combine(destination, "existing.txt"), "old content");
        var zip = CreateInstallerUiZip(
            ("existing.txt", Encoding.UTF8.GetBytes("new content")),
            ("nested/deeper/file.txt", Encoding.UTF8.GetBytes("deep")));

        using var zipStream = Resource(zip);
        Program.ExtractInstallerUi(zipStream, destination);

        File.ReadAllText(Path.Combine(destination, "existing.txt")).Should().Be("new content");
        File.ReadAllText(Path.Combine(destination, "nested", "deeper", "file.txt")).Should().Be("deep");
    }

    [Fact]
    public void ExtractEmbeddedPayload_writes_the_resource_bytes_next_to_the_ui()
    {
        using var scope = new TempScope();
        var payload = Encoding.UTF8.GetBytes("payload-bytes");
        Stream? OpenPayload(string name) => name == PayloadResourceName ? Resource(payload) : null;

        var payloadPath = Program.ExtractEmbeddedPayload(scope.Dir, OpenPayload!);

        payloadPath.Should().Be(Path.Combine(scope.Dir, PayloadResourceName));
        File.ReadAllBytes(payloadPath).Should().Equal(payload);
    }
}
