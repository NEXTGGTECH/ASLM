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
        private const uint CreateBreakawayFromJob = 0x01000000;

        /// <summary>
        /// Tries to start one process with <c>CREATE_BREAKAWAY_FROM_JOB</c> so it is not terminated with the current job group.
        /// </summary>
        public static bool TryStartBreakawayProcess(
            string fileName,
            string workingDirectory,
            IReadOnlyList<string> arguments)
        {
            if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(fileName) || !File.Exists(fileName))
            {
                return false;
            }

            var startupInfo = new STARTUPINFO
            {
                cb = (uint)Marshal.SizeOf<STARTUPINFO>()
            };

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

            CloseHandle(processInformation.hThread);
            CloseHandle(processInformation.hProcess);
            return true;
        }


        // Command line

        /// <summary>
        /// Builds a CreateProcess-compatible command line that safely preserves argument boundaries.
        /// </summary>
        private static string BuildCommandLine(string fileName, IReadOnlyList<string> arguments)
        {
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
        private static string QuoteArgument(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "\"\"";
            }

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

                if (backslashCount > 0)
                {
                    builder.Append('\\', backslashCount);
                    backslashCount = 0;
                }

                builder.Append(character);
            }

            if (backslashCount > 0)
            {
                builder.Append('\\', backslashCount * 2);
            }

            builder.Append('"');
            return builder.ToString();
        }

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

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public uint cb;
            public string? lpReserved;
            public string? lpDesktop;
            public string? lpTitle;
            public uint dwX;
            public uint dwY;
            public uint dwXSize;
            public uint dwYSize;
            public uint dwXCountChars;
            public uint dwYCountChars;
            public uint dwFillAttribute;
            public uint dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }
    }
}
