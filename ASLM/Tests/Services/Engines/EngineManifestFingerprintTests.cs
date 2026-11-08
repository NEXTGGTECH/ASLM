// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Models;

namespace ASLM.Tests.Services.Engines
{
    /// <summary>
    /// Verifies that <see cref="EngineManifestFingerprint"/> hashes the declarative engine definition and not the
    /// local installation state.
    /// </summary>
    public sealed class EngineManifestFingerprintTests
    {
        // Fingerprint comparison

        /// <summary>
        /// Verifies that two engines with the same definition have the same fingerprint even when one of them
        /// carries installed status, version, and manifest hash.
        /// </summary>
        [Fact]
        public void Fingerprint_ignores_installation_status()
        {
            var first = CreateEngine("https://example.test/runtime.zip");
            var second = CreateEngine("https://example.test/runtime.zip");
            second.Status.Installed = true;
            second.Status.InstalledVersion = "9.0";
            second.Status.InstalledManifestHash = "local-state";

            EngineManifestFingerprint.Compute(first)
                .Should().Be(EngineManifestFingerprint.Compute(second));
        }

        /// <summary>
        /// Verifies that engines whose resolved platform install steps differ (different download URL) have
        /// different fingerprints.
        /// </summary>
        [Fact]
        public void Fingerprint_changes_with_effective_definition()
        {
            var first = CreateEngine("https://example.test/one.zip");
            var second = CreateEngine("https://example.test/two.zip");

            EngineManifestFingerprint.Compute(first)
                .Should().NotBe(EngineManifestFingerprint.Compute(second));
        }


        // Helpers

        /// <summary>
        /// Builds a normalized Windows amd64 engine with one download install step, resolved for that platform.
        /// </summary>
        /// <param name="url">Download URL of the install step.</param>
        private static EngineConfig CreateEngine(string url)
        {
            var config = new EngineConfig
            {
                Id = "vendor-runtime",
                SupportedPlatforms =
                [
                    new SupportedPlatform { Os = "windows", Arch = "amd64", Key = "windows-amd64" }
                ],
                Platforms = new Dictionary<string, EnginePlatform>(StringComparer.OrdinalIgnoreCase)
                {
                    ["windows-amd64"] = new EnginePlatform
                    {
                        ExecutablePath = "runtime/vendor.exe",
                        Install = [new InstallStep { Action = "download", Url = url }]
                    }
                }
            };
            config.Normalize();
            config.ResolveForPlatform("windows", "amd64");
            return config;
        }
    }
}
