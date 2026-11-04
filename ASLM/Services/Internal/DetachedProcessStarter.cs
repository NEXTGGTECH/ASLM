// Copyright NEXTGGTECH. Apache License 2.0.

using System.Runtime.InteropServices;
using System.Text;

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Starts one process outside the current Windows Job Object when self-update restart needs to survive app shutdown.
    /// </summary>
    internal static class DetachedProcessStarter
    {
        // Constants

        /// <summary>
        /// The <c>CREATE_BREAKAWAY_FROM_JOB</c> process creation flag: the new process is not placed in the job
        /// of its creator. CreateProcess fails with this flag unless that job allows breakaway
        /// (<c>JOB_OBJECT_LIMIT_BREAKAWAY_OK</c>, which <see cref="ProcessTracker"/> sets on its job).
        /// </summary>
        private const uint CreateBreakawayFromJob = 0x01000000;


        // Process start

        /// <summary>
        /// Tries to start one process with <c>CREATE_BREAKAWAY_FROM_JOB</c> so it is not terminated with the current job group.
        /// </summary>
        /// <param name="fileName">Full path of the executable; it must exist.</param>
        /// <param name="workingDirectory">Working directory of the new process.</param>
        /// <param name="arguments">Arguments of the new process; each one is quoted so its boundaries are preserved.</param>
        /// <returns>True when the process was created; false on non-Windows systems, a missing file or a failed start.</returns>
        public static bool TryStartBreakawayProcess(
            string fileName,
            string workingDirectory,
            IReadOnlyList<string> arguments)
        {
            // Job Objects and this creation flag exist only on Windows.
            if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(fileName) || !File.Exists(fileName))
            {
                return false;
            }

            // CreateProcess requires the size of the startup structure; every other field keeps its default.
            var startupInfo = new STARTUPINFO
            {
                cb = (uint)Marshal.SizeOf<STARTUPINFO>()
            };

            // The Unicode CreateProcess may modify its command line buffer, so it is passed as a StringBuilder.
            // The executable is taken from the first command-line token (no application name is passed) and
            // handles are not inherited, so the new process does not share this process's handles.
            var commandLine = new StringBuilder(BuildCommandLine(fileName, arguments));
            if (!CreateProcess(
                    lpApplicationName: null,
                    lpCommandLine: commandLine,
                    lpProcessAttributes: IntPtr.Zero,
                    lpThreadAttributes: IntPtr.Zero,
                    bInheritHandles: false,
                    dwCreationFlags: CreateBreakawayFromJob,
                    lpEnvironment: IntPtr.Zero,
                    lpCurrentDirectory: workingDirectory,
                    lpStartupInfo: ref startupInfo,
                    lpProcessInformation: out var processInformation))
            {
                return false;
            }

            // The caller does not wait for the process; closing these handles only releases this process's
            // references and does not stop the new process.
            CloseHandle(processInformation.hThread);
            CloseHandle(processInformation.hProcess);
            return true;
        }


        // Command line

        /// <summary>
        /// Builds a CreateProcess-compatible command line that safely preserves argument boundaries.
        /// </summary>
        /// <param name="fileName">Executable path; becomes the first token.</param>
        /// <param name="arguments">Arguments that follow the executable; null entries become empty arguments.</param>
        private static string BuildCommandLine(string fileName, IReadOnlyList<string> arguments)
        {
            // The executable comes first, then each argument quoted on its own.
            var parts = new List<string>(capacity: (arguments?.Count ?? 0) + 1)
            {
                QuoteArgument(fileName)
            };

            if (arguments != null)
            {
                foreach (var argument in arguments)
                {
                    parts.Add(QuoteArgument(argument ?? string.Empty));
                }
            }

            return string.Join(" ", parts);
        }

        /// <summary>
        /// Quotes one process argument using Windows command-line escaping rules.
        /// </summary>
        /// <param name="value">Raw argument text.</param>
        /// <returns>The argument unchanged when it needs no quoting; otherwise a quoted, escaped form.</returns>
        internal static string QuoteArgument(string value)
        {
            // An empty argument must still occupy a token, so it becomes a pair of quotes.
            if (string.IsNullOrEmpty(value))
            {
                return "\"\"";
            }

            // Only whitespace and quotes require quoting; other text is passed through as it is.
            var needsQuotes = false;
            foreach (var character in value)
            {
                if (char.IsWhiteSpace(character) || character == '"')
                {
                    needsQuotes = true;
                    break;
                }
            }

            if (!needsQuotes)
            {
                return value;
            }

            // Backslashes are literal unless they precede a quote, so they are counted and written only once the
            // next character is known: before a quote they are doubled and the quote is escaped.
            var builder = new StringBuilder(value.Length + 8);
            builder.Append('"');

            var backslashCount = 0;
            foreach (var character in value)
            {
                if (character == '\\')
                {
                    backslashCount++;
                    continue;
                }

                if (character == '"')
                {
                    builder.Append('\\', backslashCount * 2 + 1);
                    builder.Append('"');
                    backslashCount = 0;
                    continue;
                }

                // Backslashes not followed by a quote stay as they are.
                if (backslashCount > 0)
                {
                    builder.Append('\\', backslashCount);
                    backslashCount = 0;
                }

                builder.Append(character);
            }

            // Trailing backslashes would escape the closing quote, so they are doubled.
            if (backslashCount > 0)
            {
                builder.Append('\\', backslashCount * 2);
            }

            builder.Append('"');
            return builder.ToString();
        }


        // Win32 interop

        /// <summary>
        /// Creates a new process and its primary thread (Unicode variant of the kernel32 <c>CreateProcess</c>).
        /// </summary>
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcess(
            string? lpApplicationName,
            StringBuilder lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            bool bInheritHandles,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string? lpCurrentDirectory,
            ref STARTUPINFO lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        /// <summary>
        /// Closes an open kernel object handle.
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        /// <summary>
        /// Native <c>STARTUPINFO</c> structure that describes how the new process is started.
        /// Only <c>cb</c> is set here, so the new process gets the default window and standard handles.
        /// </summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            /// <summary>
            /// Size of the structure in bytes.
            /// </summary>
            public uint cb;

            /// <summary>
            /// Reserved by Windows; must be null.
            /// </summary>
            public string? lpReserved;

            /// <summary>
            /// Desktop or window station name; null uses the caller's.
            /// </summary>
            public string? lpDesktop;

            /// <summary>
            /// Console window title; null uses the executable name.
            /// </summary>
            public string? lpTitle;

            /// <summary>
            /// Horizontal position of a new window; read only with the use-position flag in <c>dwFlags</c>.
            /// </summary>
            public uint dwX;

            /// <summary>
            /// Vertical position of a new window; read only with the use-position flag in <c>dwFlags</c>.
            /// </summary>
            public uint dwY;

            /// <summary>
            /// Width of a new window; read only with the use-size flag in <c>dwFlags</c>.
            /// </summary>
            public uint dwXSize;

            /// <summary>
            /// Height of a new window; read only with the use-size flag in <c>dwFlags</c>.
            /// </summary>
            public uint dwYSize;

            /// <summary>
            /// Screen buffer width in characters for a new console window; read only with the use-count-chars flag.
            /// </summary>
            public uint dwXCountChars;

            /// <summary>
            /// Screen buffer height in characters for a new console window; read only with the use-count-chars flag.
            /// </summary>
            public uint dwYCountChars;

            /// <summary>
            /// Initial text and background colors of a new console window; read only with the use-fill-attribute flag.
            /// </summary>
            public uint dwFillAttribute;

            /// <summary>
            /// Flags that select which of the other fields are honored; zero means none.
            /// </summary>
            public uint dwFlags;

            /// <summary>
            /// Initial window show state; read only with the use-show-window flag in <c>dwFlags</c>.
            /// </summary>
            public short wShowWindow;

            /// <summary>
            /// Reserved by Windows; must be zero.
            /// </summary>
            public short cbReserved2;

            /// <summary>
            /// Reserved by Windows; must be null.
            /// </summary>
            public IntPtr lpReserved2;

            /// <summary>
            /// Standard input handle; read only with the use-standard-handles flag in <c>dwFlags</c>.
            /// </summary>
            public IntPtr hStdInput;

            /// <summary>
            /// Standard output handle; read only with the use-standard-handles flag in <c>dwFlags</c>.
            /// </summary>
            public IntPtr hStdOutput;

            /// <summary>
            /// Standard error handle; read only with the use-standard-handles flag in <c>dwFlags</c>.
            /// </summary>
            public IntPtr hStdError;
        }

        /// <summary>
        /// Native <c>PROCESS_INFORMATION</c> structure that receives the handles and ids of the created process.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            /// <summary>
            /// Handle to the new process; the caller must close it.
            /// </summary>
            public IntPtr hProcess;

            /// <summary>
            /// Handle to the primary thread of the new process; the caller must close it.
            /// </summary>
            public IntPtr hThread;

            /// <summary>
            /// Identifier of the new process.
            /// </summary>
            public int dwProcessId;

            /// <summary>
            /// Identifier of the primary thread of the new process.
            /// </summary>
            public int dwThreadId;
        }
    }
}
