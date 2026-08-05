// Copyright NEXTGGTECH. Apache License 2.0.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace ASLM
{
    /// <summary>
    /// Hands restart and self-update work to a shadow copy of the bundled patcher helper.
    /// </summary>
    /// <remarks>
    /// The helper (Contents/Helpers/aslm-patcher) waits for this process to exit, applies a
    /// pending update from the data root when one is staged, and relaunches the .app bundle.
    /// It runs from a temp copy because the bundle itself may be replaced by the update.
    /// </remarks>
    public static class MacAppRelauncher
    {
        // Single-file .NET binaries cannot be lipo-merged, so the bundle ships one helper per arch.
        private const string HelperFileName = "aslm-patcher";

        /// <summary>
        /// Starts the detached helper that waits for this process, patches, and relaunches.
        /// </summary>
        public static void StartDetachedRelaunch()
        {
            var bundlePath = MacBundleLocator.FindBundleRoot()
                ?? throw new InvalidOperationException("The ASLM .app bundle was not found.");

            var helpersDir = Path.Combine(bundlePath, "Contents", "Helpers");
            var archSuffix = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
            var helperSource = Path.Combine(helpersDir, $"{HelperFileName}-{archSuffix}");
            if (!File.Exists(helperSource))
            {
                helperSource = Path.Combine(helpersDir, HelperFileName);
            }

            if (!File.Exists(helperSource))
            {
                throw new FileNotFoundException("The ASLM update helper was not found.", helperSource);
            }

            var shadowDir = Path.Combine(Path.GetTempPath(), "ASLM-Patcher-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(shadowDir);

            var shadowHelper = Path.Combine(shadowDir, HelperFileName);
            File.Copy(helperSource, shadowHelper);
            File.SetUnixFileMode(
                shadowHelper,
                File.GetUnixFileMode(shadowHelper) |
                UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);

            var startInfo = new ProcessStartInfo
            {
                FileName = shadowHelper,
                WorkingDirectory = shadowDir,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add("--root");
            startInfo.ArgumentList.Add(AppRoot.Directory);
            startInfo.ArgumentList.Add("--launcher");
            startInfo.ArgumentList.Add(bundlePath);
            startInfo.ArgumentList.Add("--wait-process");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

            if (Process.Start(startInfo) == null)
            {
                throw new InvalidOperationException("The ASLM update helper did not start.");
            }
        }
    }
}
