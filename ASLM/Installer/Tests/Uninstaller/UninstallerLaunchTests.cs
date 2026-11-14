// Copyright NEXTGGTECH. Apache License 2.0.

using System.Diagnostics;
using System.Runtime.InteropServices;
using ASLM.Installer.Tests.TestSupport;
using ASLM.Installer.Uninstaller;

namespace ASLM.Installer.Tests.Uninstaller
{
    /// <summary>
    /// Verifies the uninstaller command line, the temporary copy for the removal for every user, and the handover
    /// between the original uninstaller and that copy.
    /// </summary>
    public sealed class UninstallerLaunchTests
    {
        // Command-line parsing

        /// <summary>
        /// Verifies that without arguments the uninstaller removes the installation of its own folder for the user
        /// to choose, without waiting for another process.
        /// </summary>
        [Fact]
        public void Parse_without_arguments_uses_the_own_folder()
        {
            var arguments = UninstallerLaunch.Parse([], @"C:\Program Files\ASLM\");

            arguments.Should().Be(new UninstallerArguments(@"C:\Program Files\ASLM\", false, null, null));
        }

        /// <summary>
        /// Verifies that every argument is read regardless of case.
        /// </summary>
        [Fact]
        public void Parse_reads_every_argument_regardless_of_case()
        {
            var arguments = UninstallerLaunch.Parse(
                ["--ALL-USERS", "--install-dir", @"D:\ASLM", "--Wait-Process", "42", "--wait-start", "123456"],
                @"C:\ignored");

            arguments.Should().Be(new UninstallerArguments(@"D:\ASLM", true, 42, 123456));
        }

        /// <summary>
        /// Verifies that unknown arguments, missing values and invalid numbers are rejected.
        /// </summary>
        [Theory]
        [InlineData("--unknown")]
        [InlineData("--install-dir")]
        [InlineData("--wait-process", "0")]
        [InlineData("--wait-process", "abc")]
        [InlineData("--wait-start", "-1")]
        public void Parse_rejects_invalid_command_lines(params string[] args)
        {
            var parse = () => UninstallerLaunch.Parse(args, @"C:\ASLM");

            parse.Should().Throw<ArgumentException>();
        }


        // Command-line quoting

        /// <summary>
        /// Verifies the quoting of single arguments: plain text stays, empty text becomes a pair of quotes, and
        /// whitespace, quotes and trailing backslashes are escaped.
        /// </summary>
        [Theory]
        [InlineData("plain", "plain")]
        [InlineData("", "\"\"")]
        [InlineData(@"C:\Program Files\ASLM", "\"C:\\Program Files\\ASLM\"")]
        [InlineData(@"C:\Program Files\ASLM\", "\"C:\\Program Files\\ASLM\\\\\"")]
        [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
        public void QuoteArgument_escapes_by_the_windows_rules(string value, string expected)
        {
            UninstallerLaunch.QuoteArgument(value).Should().Be(expected);
        }

        /// <summary>
        /// Verifies that the command line of the copy, split the way Windows splits it for the started process,
        /// parses back into the same request.
        /// </summary>
        [Fact]
        public void Copy_command_line_parses_back_into_the_same_request()
        {
            var installDir = @"C:\Program Files\My ASLM\";

            var commandLine = UninstallerLaunch.BuildAllUsersArguments(installDir, 4242, 638_000_000_000_000_000);
            var arguments = UninstallerLaunch.Parse(SplitCommandLine(commandLine), @"C:\ignored");

            arguments.Should().Be(new UninstallerArguments(installDir, true, 4242, 638_000_000_000_000_000));
        }


        // Temporary copy

        /// <summary>
        /// Verifies that the copy goes into a GUID-named folder below <c>ASLM\Uninstaller</c> of the temporary folder,
        /// takes every file named like the executable, and leaves other files of its folder behind.
        /// </summary>
        [Fact]
        public void CreateTemporaryCopy_copies_the_uninstaller_files_into_a_copy_folder()
        {
            // Arrange: a framework-dependent uninstaller next to an unrelated file.
            using var scope = new TempScope();
            var source = Path.Combine(scope.Dir, "Program Files", "ASLM");
            Directory.CreateDirectory(source);
            foreach (var name in new[] { "uninstall.exe", "uninstall.dll", "uninstall.runtimeconfig.json", "ASLM.exe" })
            {
                File.WriteAllText(Path.Combine(source, name), name);
            }

            // Act.
            var copyPath = UninstallerLaunch.CreateTemporaryCopy(Path.Combine(source, "uninstall.exe"), scope.Dir);

            // Assert.
            var copyFolder = Path.GetDirectoryName(copyPath)!;
            Path.GetDirectoryName(copyFolder).Should().Be(Path.Combine(scope.Dir, "ASLM", "Uninstaller"));
            UninstallerLaunch.IsTemporaryCopyFolder(copyFolder).Should().BeTrue();
            Directory.EnumerateFiles(copyFolder).Select(Path.GetFileName).Should().BeEquivalentTo(
                new[] { "uninstall.exe", "uninstall.dll", "uninstall.runtimeconfig.json" });
        }

        /// <summary>
        /// Verifies that only GUID-named folders below <c>ASLM\Uninstaller</c> count as copy folders.
        /// </summary>
        [Theory]
        [InlineData(@"C:\Temp\ASLM\Uninstaller\030f5475ac324420890a612efd3e3154", true)]
        [InlineData(@"C:\Temp\ASLM\Uninstaller\030f5475ac324420890a612efd3e3154\", true)]
        [InlineData(@"C:\Program Files\ASLM", false)]
        [InlineData(@"C:\Temp\ASLM\Uninstaller\copy", false)]
        [InlineData(@"C:\Temp\Other\Uninstaller\030f5475ac324420890a612efd3e3154", false)]
        public void IsTemporaryCopyFolder_accepts_only_copy_folders(string folder, bool expected)
        {
            UninstallerLaunch.IsTemporaryCopyFolder(folder).Should().Be(expected);
        }

        /// <summary>
        /// Verifies that the copy folder is scheduled for removal at the next restart with its files first and the
        /// work folders after it, and that a folder that is not a copy folder is never scheduled.
        /// </summary>
        [Fact]
        public void ScheduleCopyRemoval_schedules_files_before_their_folders_and_only_for_copies()
        {
            // Arrange: a copy folder with one file, and the shared folder the uninstaller may also run from.
            using var scope = new TempScope();
            var copyPath = Path.Combine(scope.Dir, "ASLM", "Uninstaller", Guid.NewGuid().ToString("N"), "uninstall.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(copyPath)!);
            File.WriteAllText(copyPath, "copy");
            var scheduled = new List<string>();

            // Act.
            UninstallerLaunch.ScheduleCopyRemoval(Path.GetDirectoryName(copyPath)!, path => { scheduled.Add(path); return true; });
            UninstallerLaunch.ScheduleCopyRemoval(Path.Combine(scope.Dir, "Program Files", "ASLM"), path => { scheduled.Add(path); return true; });

            // Assert.
            scheduled.Should().Equal(
                copyPath,
                Path.GetDirectoryName(copyPath),
                Path.Combine(scope.Dir, "ASLM", "Uninstaller"),
                Path.Combine(scope.Dir, "ASLM"));
        }


        // Handover

        /// <summary>
        /// Verifies that the copy waits until the original uninstaller has exited.
        /// </summary>
        [Fact]
        public void WaitForParentExit_returns_after_the_parent_exited()
        {
            using var parent = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 2 127.0.0.1 >nul")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            })!;
            var arguments = new UninstallerArguments(
                @"C:\ASLM", true, parent.Id, parent.StartTime.ToUniversalTime().Ticks);

            UninstallerLaunch.WaitForParentExit(arguments);

            parent.HasExited.Should().BeTrue();
        }

        /// <summary>
        /// Verifies that a process whose start time differs from the recorded one, which means the process id was
        /// reused, is not waited for.
        /// </summary>
        [Fact]
        public void WaitForParentExit_ignores_a_reused_process_id()
        {
            using var stranger = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 >nul")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            })!;

            try
            {
                var arguments = new UninstallerArguments(@"C:\ASLM", true, stranger.Id, 1);
                var watch = Stopwatch.StartNew();

                UninstallerLaunch.WaitForParentExit(arguments);

                watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
                stranger.HasExited.Should().BeFalse();
            }
            finally
            {
                stranger.Kill(entireProcessTree: true);
            }
        }


        // Command-line splitting

        /// <summary>
        /// Splits a command line the way Windows splits it for a started process.
        /// </summary>
        private static string[] SplitCommandLine(string arguments)
        {
            // The program name comes first, as in a real command line; it is not part of the arguments.
            var argv = CommandLineToArgv("uninstall.exe " + arguments, out var count);
            try
            {
                return Enumerable.Range(1, count - 1)
                    .Select(index => Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, index * IntPtr.Size))!)
                    .ToArray();
            }
            finally
            {
                LocalFree(argv);
            }
        }

        /// <summary>
        /// Splits a command line into arguments (shell32 <c>CommandLineToArgvW</c>).
        /// </summary>
        [DllImport("shell32.dll", EntryPoint = "CommandLineToArgvW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CommandLineToArgv(string commandLine, out int count);

        /// <summary>
        /// Frees the argument array returned by <c>CommandLineToArgvW</c>.
        /// </summary>
        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);
    }
}
