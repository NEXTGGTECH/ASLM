// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Models;
using ASLM.Services.Engines;
using ASLM.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace ASLM.Tests.Services.Engines
{
    /// <summary>
    /// Verifies the managed Ollama account state against a real loopback runtime API,
    /// including the unavailable-engine and signed-out paths.
    /// </summary>
    public sealed class OllamaSettingsStoreTests : IDisposable
    {
        // Fields and helpers

        /// <summary>
        /// Isolated ASLM root in which the fake <c>ollama-service</c> engine is installed.
        /// </summary>
        private readonly UniqueAslmRoot _root = new("ollama");

        /// <summary>
        /// Deletes the isolated root and restores the previous root setting.
        /// </summary>
        public void Dispose() => _root.Dispose();

        /// <summary>
        /// Creates a store that finds engines under the isolated root and talks to the runtime on the given
        /// loopback port.
        /// </summary>
        /// <param name="port">Port of the fake runtime API; null when no runtime is expected.</param>
        private OllamaSettingsStore CreateStore(int? port) =>
            new(new EngineInstaller(), NullLogger<OllamaSettingsStore>.Instance,
                new HttpClient(), port);


        // Missing engine

        /// <summary>
        /// Verifies that without an installed <c>ollama-service</c> engine the refreshed settings report the
        /// command line as unavailable and the account as signed out.
        /// </summary>
        [Fact]
        public async Task Without_an_installed_engine_the_runtime_reports_unavailable()
        {
            var store = CreateStore(null);

            var settings = await store.RefreshSettingsAsync();

            settings.IsCliAvailable.Should().BeFalse("no ollama-service engine is installed");
            settings.IsSignedIn.Should().BeFalse();
        }


        // Account state

        /// <summary>
        /// Verifies that with an installed engine and a runtime that answers the version and account endpoints, the
        /// refreshed settings are signed in with the profile name and email, and that the stored snapshot returns
        /// the same account later.
        /// </summary>
        [Fact]
        public async Task Reachable_runtime_with_a_signed_in_account_reports_the_profile()
        {
            using var server = LoopbackHttpServer.Start();
            server.ServeJson("api/version", """{"version":"0.12.6"}""");
            server.ServeJson("api/me", """{"name":"octocat","email":"octo@example.test"}""");
            InstallFakeEngine("ollama-runtime.cmd");
            var store = CreateStore(server.BaseUri.Port);

            var settings = await store.RefreshSettingsAsync();

            settings.IsCliAvailable.Should().BeTrue();
            settings.IsSignedIn.Should().BeTrue();
            settings.UserName.Should().Be("octocat");
            settings.Email.Should().Be("octo@example.test");

            // The cached snapshot answers without another probe.
            server.Requests.Should().Contain(r => r.Url.AbsolutePath == "/api/me");
            var cached = store.LoadSettings();
            cached.IsSignedIn.Should().BeTrue();
            cached.UserName.Should().Be("octocat");
        }

        /// <summary>
        /// Verifies that a runtime whose account endpoint answers 401 is still reported as available, but with the
        /// account signed out.
        /// </summary>
        [Fact]
        public async Task Unauthorized_account_endpoint_reports_signed_out()
        {
            using var server = LoopbackHttpServer.Start();
            server.ServeJson("api/version", """{"version":"0.12.6"}""");
            server.Map("api/me", context =>
            {
                context.Response.StatusCode = 401;
                return Task.CompletedTask;
            });
            InstallFakeEngine("ollama-runtime.cmd");
            var store = CreateStore(server.BaseUri.Port);

            var settings = await store.RefreshSettingsAsync();

            settings.IsCliAvailable.Should().BeTrue();
            settings.IsSignedIn.Should().BeFalse("the runtime answered with 401");
        }


        // Broken engine installation

        /// <summary>
        /// Verifies that an installed engine manifest whose declared executable file does not exist is reported as
        /// unavailable even though the runtime API answers.
        /// </summary>
        [Fact]
        public async Task Engine_manifest_with_a_missing_executable_reports_unavailable()
        {
            using var server = LoopbackHttpServer.Start();
            server.ServeJson("api/version", """{"version":"0.12.6"}""");
            InstallFakeEngine("vanished.cmd", createFile: false);
            var store = CreateStore(server.BaseUri.Port);

            var settings = await store.RefreshSettingsAsync();

            settings.IsCliAvailable.Should().BeFalse("the declared executable does not exist");
        }


        // Test setup

        /// <summary>
        /// Installs an <c>ollama-service</c> engine manifest whose executable exists but never
        /// needs to run while the fake HTTP runtime answers.
        /// </summary>
        /// <param name="executableName">File name of the executable declared in the manifest.</param>
        /// <param name="createFile">When false, the manifest declares the executable but the file is not created.</param>
        private void InstallFakeEngine(string executableName, bool createFile = true)
        {
            var engineDir = Path.Combine(_root.EnginesDir, "ollama-service");
            Directory.CreateDirectory(engineDir);
            var executablePath = Path.Combine(engineDir, executableName);
            if (createFile)
            {
                File.WriteAllText(executablePath, "@echo off\r\nexit /b 1\r\n");
            }

            // Discovery resets the installed flag when the runtime directory is empty.
            var runtimeDir = Path.Combine(engineDir, "runtime");
            Directory.CreateDirectory(runtimeDir);
            File.WriteAllText(Path.Combine(runtimeDir, ".keep"), "");

            var platformKey = PlatformInfo.PlatformKey;
            var config = new EngineConfig
            {
                FileVersion = 2,
                Id = "ollama-service",
                Name = "Ollama",
                Version = "0.12.6",
                Type = "runtime",
                SourcePath = Path.Combine(engineDir, "ASLM_Engine.json"),
                Status = new EngineStatus { Installed = true, InstalledVersion = "0.12.6" },
                SupportedPlatforms =
                [
                    new SupportedPlatform { Os = PlatformInfo.OsKey, Arch = PlatformInfo.ArchKey, Key = platformKey }
                ],
                Platforms = new Dictionary<string, EnginePlatform>(StringComparer.OrdinalIgnoreCase)
                {
                    [platformKey] = new EnginePlatform { ExecutablePath = executableName }
                }
            };
            File.WriteAllText(config.SourcePath, System.Text.Json.JsonSerializer.Serialize(config));
        }
    }
}
