// Copyright NEXTGGTECH. Apache License 2.0.

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ASLM.Services.Internal;
using ASLM.Tests.TestSupport;

namespace ASLM.Tests.Services.Internal
{
    /// <summary>
    /// Verifies the real process infrastructure: snapshot capture of live processes and the
    /// breakaway starter used for launcher restarts.
    /// </summary>
    public sealed class ProcessInfrastructureTests
    {
        // Constants

        /// <summary>
        /// Selects the basic native job limits that describe child process inheritance.
        /// </summary>
        private const int JobObjectBasicLimitInformation = 2;

        /// <summary>
        /// Includes the Windows limits for explicit or automatic child process breakaway.
        /// </summary>
        private const uint BreakawayLimits = 0x00000800 | 0x00001000;

        /// <summary>
        /// Windows error returned when a process may not break away from its job.
        /// </summary>
        private const int ErrorAccessDenied = 5;


        // Tests

        /// <summary>
        /// Verifies that a process snapshot contains the running test host and also records parent process ids.
        /// </summary>
        [Fact]
        public void Snapshot_contains_the_running_test_host_process()
        {
            var reader = new ProcessSnapshotReader();
            var snapshot = reader.GetSnapshot();

            using var current = Process.GetCurrentProcess();
            snapshot.Should().Contain(entry => entry.ProcessId == current.Id);

            // A real snapshot also observes the parent chain: at least one entry with a parent.
            snapshot.Should().Contain(entry => entry.ParentProcessId > 0);
        }

        /// <summary>
        /// Verifies that a permitted breakaway runs to completion and that a restrictive host job causes a refusal.
        /// </summary>
        [Fact]
        public async Task Breakaway_starter_respects_the_test_hosts_job_limits()
        {
            var workingDirectory = Path.GetTempPath();
            var marker = Path.Combine(workingDirectory, $"aslm-breakaway-{Guid.NewGuid():N}.txt");
            var temporaryMarker = marker + ".tmp";
            var powerShell = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            File.Exists(powerShell).Should().BeTrue("the test needs the system PowerShell executable");
            var allowsBreakaway = CurrentJobAllowsBreakaway();
            var quotedMarker = marker.Replace("'", "''", StringComparison.Ordinal);
            var quotedTemporaryMarker = temporaryMarker.Replace("'", "''", StringComparison.Ordinal);
            var command = $"[IO.File]::WriteAllText('{quotedTemporaryMarker}', 'done'); " +
                $"[IO.File]::Move('{quotedTemporaryMarker}', '{quotedMarker}')";
            try
            {
                var started = DetachedProcessStarter.TryStartBreakawayProcess(powerShell, workingDirectory,
                    ["-NoProfile", "-NonInteractive", "-Command", command]);
                var error = started ? 0 : Marshal.GetLastPInvokeError();

                started.Should().Be(allowsBreakaway, "the immediate job controls breakaway (Win32 error {0})", error);
                if (!allowsBreakaway)
                {
                    error.Should().Be(ErrorAccessDenied);
                    return;
                }

                await TestWait.UntilAsync(() => File.Exists(marker), timeout: TimeSpan.FromSeconds(15));
                (await File.ReadAllTextAsync(marker)).Should().Be("done");
            }
            finally
            {
                File.Delete(marker);
                File.Delete(temporaryMarker);
            }
        }


        // Job inspection

        /// <summary>
        /// Reads the immediate Windows job's limits independently of the breakaway starter.
        /// </summary>
        private static bool CurrentJobAllowsBreakaway()
        {
            using var current = Process.GetCurrentProcess();
            if (!IsProcessInJob(current.Handle, IntPtr.Zero, out var inJob))
                throw new Win32Exception(Marshal.GetLastPInvokeError());

            if (!inJob) return true;

            if (!QueryInformationJobObject(IntPtr.Zero, JobObjectBasicLimitInformation, out var limits,
                    Marshal.SizeOf<JobBasicLimits>(), IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastPInvokeError());

            return (limits.LimitFlags & BreakawayLimits) != 0;
        }

        /// <summary>
        /// Gets whether a process is associated with any job when the job handle is null.
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsProcessInJob(
            IntPtr process, IntPtr job, [MarshalAs(UnmanagedType.Bool)] out bool inJob);

        /// <summary>
        /// Retrieves the requested information from the calling process's immediate job when the handle is null.
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryInformationJobObject(IntPtr job, int informationClass, out JobBasicLimits information,
            int informationLength, IntPtr returnLength);

        /// <summary>
        /// Covers the Win64 basic limit structure while exposing only the limit flags read by this test.
        /// </summary>
        [StructLayout(LayoutKind.Explicit, Size = 64)]
        private struct JobBasicLimits
        {
            /// <summary>
            /// Limit flags stored after the two 64-bit time limits.
            /// </summary>
            [FieldOffset(2 * sizeof(long))]
            public uint LimitFlags;
        }
    }
}
