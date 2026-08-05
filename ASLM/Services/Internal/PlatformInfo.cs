// Copyright NEXTGGTECH. Apache License 2.0.

using System.Runtime.InteropServices;

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Resolves the current operating system and processor architecture into the
    /// stable identifiers used by engine manifests, update sources, and build artifacts.
    /// </summary>
    /// <remarks>
    /// Canonical vocabulary (single source of truth across the app):
    /// - OS key:        windows | linux | macos
    /// - Arch key:      amd64 | arm64 | x86 | arm
    /// - Platform key:  "{os}-{arch}", e.g. windows-amd64, windows-arm64
    /// - RID:           win-x64 | win-arm64 | osx-arm64 | linux-x64 ...
    /// The macOS branch is intentionally present so manifests and callers written now
    /// resolve correctly once a macOS target is added; it does not change existing
    /// Windows behavior.
    /// </remarks>
    public static class PlatformInfo
    {
        /// <summary>
        /// Returns the canonical OS key for the current process.
        /// </summary>
        public static string OsKey
        {
            get
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "windows";
                if (OperatingSystem.IsMacCatalyst() || RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "macos";
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "linux";
                throw new PlatformNotSupportedException("The current OS is not supported.");
            }
        }

        /// <summary>
        /// Returns the canonical architecture key for the current process.
        /// </summary>
        public static string ArchKey => RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "amd64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            _ => throw new PlatformNotSupportedException(
                $"The current architecture '{RuntimeInformation.ProcessArchitecture}' is not supported.")
        };

        /// <summary>
        /// Returns the canonical platform key "{os}-{arch}" for the current process,
        /// for example <c>windows-amd64</c> or <c>windows-arm64</c>.
        /// </summary>
        public static string PlatformKey => $"{OsKey}-{ArchKey}";

        /// <summary>
        /// Returns the .NET runtime identifier (RID) for the current process.
        /// </summary>
        public static string RuntimeIdentifier => $"{RidOs(OsKey)}-{RidArch(ArchKey)}";

        /// <summary>
        /// Returns the lookup keys to try when reading a platform-keyed map, newest
        /// vocabulary first. Includes the legacy <c>x64</c> alias so update sources
        /// written before the amd64 unification keep resolving.
        /// </summary>
        public static IReadOnlyList<string> PlatformKeyCandidates() => PlatformKeyCandidates(OsKey, ArchKey);

        /// <summary>
        /// Returns the platform-key candidates for an explicit OS and architecture.
        /// </summary>
        public static IReadOnlyList<string> PlatformKeyCandidates(string osKey, string archKey) =>
            string.Equals(archKey, "amd64", StringComparison.OrdinalIgnoreCase)
                ? [$"{osKey}-amd64", $"{osKey}-x64"]
                : [$"{osKey}-{archKey}"];

        /// <summary>
        /// Returns the value for the current platform from a platform-keyed map,
        /// trying every candidate key. Returns null when no candidate is present.
        /// </summary>
        public static string? ResolveFromMap(IReadOnlyDictionary<string, string>? map)
        {
            if (map == null || map.Count == 0)
            {
                return null;
            }

            foreach (var key in PlatformKeyCandidates())
            {
                if (map.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return null;
        }

        private static string RidOs(string osKey) => osKey switch
        {
            "windows" => "win",
            "macos" => "osx",
            _ => osKey
        };

        private static string RidArch(string archKey) => archKey switch
        {
            "amd64" => "x64",
            _ => archKey
        };
    }
}
