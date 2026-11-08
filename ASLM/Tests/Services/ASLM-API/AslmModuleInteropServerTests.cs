// Copyright NEXTGGTECH. Apache License 2.0.

using System.Net;
using System.Text;
using System.Text.Json;
using ASLM.Models;
using ASLM.Services.API;
using ASLM.Tests.TestSupport;

namespace ASLM.Tests.Services.API
{
    /// <summary>
    /// Drives the real loopback interop JSON API over HTTP: registry snapshots, route
    /// errors, and the caller-must-be-running rule for coordinated module starts.
    /// </summary>
    public sealed class AslmModuleInteropServerTests : IDisposable
    {
        // Fields

        /// <summary>
        /// Production service graph over an isolated root, created for each test.
        /// </summary>
        private readonly TestServiceGraph _graph = new();

        /// <summary>
        /// Mirror server the interop server reports in its registry; it is never started by these tests.
        /// </summary>
        private readonly AslmMirrorServer _mirror;

        /// <summary>
        /// Interop server under test, listening on a loopback port once a test starts it.
        /// </summary>
        private readonly AslmModuleInteropServer _server;

        /// <summary>
        /// HTTP client used to call the interop server.
        /// </summary>
        private readonly HttpClient _client = new();


        // Lifecycle

        /// <summary>
        /// Builds the mirror and interop servers on top of the test service graph without starting them.
        /// </summary>
        public AslmModuleInteropServerTests()
        {
            _mirror = new AslmMirrorServer(_graph.AppData, _graph.Ports, _graph.Installer,
                TestLoggerFactory.Create<AslmMirrorServer>());
            _server = new AslmModuleInteropServer(_graph.AppData, _graph.Ports, _mirror, _graph.Installer,
                _graph.Runner, _graph.Coordinator, _graph.InteropHost,
                TestLoggerFactory.Create<AslmModuleInteropServer>());
        }

        /// <summary>
        /// Disposes the client, both servers and the service graph, which stops modules a test left running and
        /// removes the isolated root.
        /// </summary>
        public void Dispose()
        {
            _client.Dispose();
            _server.Dispose();
            _mirror.Dispose();
            _graph.Dispose();
        }


        // Registry and routing

        /// <summary>
        /// Verifies that starting the interop server publishes its listening address in the shared host state and
        /// that <c>GET v1/registry</c> answers 200 with the interop base URL and the installed, running, and
        /// ASLM API sections.
        /// </summary>
        [Fact]
        public async Task Started_server_serves_the_registry_and_the_interop_host_state()
        {
            // Act: start the server on demand.
            await _server.EnsureStartedAsync();

            // Assert, host state: the server reports running and publishes the port it listens on.
            _server.IsRunning.Should().BeTrue();
            _server.LastError.Should().BeEmpty();
            _graph.InteropHost.TryGetListening(out var listeningUrl, out var port).Should().BeTrue();
            port.Should().Be(new Uri(_server.BaseUrl).Port);
            listeningUrl.Should().NotBeNullOrWhiteSpace();

            // Assert, registry snapshot: the JSON document carries all expected top-level sections.
            using var response = await _client.GetAsync(_server.BaseUrl + "v1/registry");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            var root = document.RootElement;
            root.GetProperty("interopBaseUrl").GetString().Should().NotBeNullOrWhiteSpace();
            root.TryGetProperty("installedModules", out _).Should().BeTrue();
            root.TryGetProperty("runningModules", out _).Should().BeTrue();
            root.TryGetProperty("aslmApi", out _).Should().BeTrue();
        }

        /// <summary>
        /// Verifies that an unknown path answers 404 with a JSON error envelope coded <c>not_found</c>, and that a
        /// known path called with the wrong HTTP method also answers 404.
        /// </summary>
        [Fact]
        public async Task Unknown_routes_and_methods_return_a_json_404_envelope()
        {
            await _server.EnsureStartedAsync();

            using var unknown = await _client.GetAsync(_server.BaseUrl + "v1/does-not-exist");
            unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await unknown.Content.ReadAsStringAsync()).Should().Contain("not_found");

            using var wrongMethod = await _client.GetAsync(_server.BaseUrl + "v1/modules/start");
            wrongMethod.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }


        // Coordinated module starts

        /// <summary>
        /// Verifies that <c>POST v1/modules/start</c> from a caller that is not a running module is rejected with
        /// 403 and the error code <c>caller_not_running</c>.
        /// </summary>
        [Fact]
        public async Task Module_start_requires_a_running_caller_module()
        {
            await _server.EnsureStartedAsync();

            using var response = await _client.PostAsync(
                _server.BaseUrl + "v1/modules/start",
                new StringContent("""{"callerModuleId":"ghost","moduleIds":["any"]}""", Encoding.UTF8, "application/json"));

            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            var body = await response.Content.ReadAsStringAsync();
            body.Should().Contain("caller_not_running");
        }

        /// <summary>
        /// Verifies that a module that is really running can call the start endpoint: asking for itself reports
        /// <c>alreadyRunning</c> and asking for an unknown module reports <c>notFound</c>, both inside a 200 response.
        /// </summary>
        [Fact]
        public async Task A_running_caller_can_request_itself_and_receives_mapped_statuses()
        {
            // Arrange: start the server and a real module process that stays alive during the test.
            await _server.EnsureStartedAsync();
            var module = CreateRunnableModule();
            (await _graph.Runner.ExecuteRunAsync(module, new SilentProgress(), CancellationToken.None))
                .Should().BeTrue();
            try
            {
                // Act and assert, own id: the caller is already running.
                using var alreadyRunning = await PostStartAsync(module.Id, module.Id);
                alreadyRunning.StatusCode.Should().Be(HttpStatusCode.OK);
                var body = await alreadyRunning.Content.ReadAsStringAsync();
                body.Should().Contain("alreadyRunning").And.Contain(module.Id);

                // Act and assert, unknown id: the per-module result carries the not-found status.
                using var notFound = await PostStartAsync(module.Id, "does-not-exist");
                notFound.StatusCode.Should().Be(HttpStatusCode.OK);
                (await notFound.Content.ReadAsStringAsync()).Should().Contain("notFound");
            }
            finally
            {
                await _graph.Runner.StopModuleAsync(module.SourcePath);
            }
        }


        // Helpers

        /// <summary>
        /// Posts a start request for one target module on behalf of the given caller module.
        /// </summary>
        private async Task<HttpResponseMessage> PostStartAsync(string caller, string target)
        {
            var payload = JsonSerializer.Serialize(new { callerModuleId = caller, moduleIds = new[] { target } });
            return await _client.PostAsync(_server.BaseUrl + "v1/modules/start",
                new StringContent(payload, Encoding.UTF8, "application/json"));
        }

        /// <summary>
        /// Creates an installed module with a unique id whose run command is a PowerShell script that sleeps for a
        /// minute, so the module stays running while a test talks to the server. Its manifest and registry entry are
        /// written under the isolated root.
        /// </summary>
        private ModuleConfig CreateRunnableModule()
        {
            // Manifest folder with a long-running script as the module's run command.
            var id = $"interop-{Guid.NewGuid():N}";
            var path = Path.Combine(_graph.Root.ModulesDir, id, ModuleManifestDiscovery.ManifestFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var script = Path.Combine(Path.GetDirectoryName(path)!, "run.ps1");
            File.WriteAllText(script, "Start-Sleep -Seconds 60", new UTF8Encoding(true));
            var module = ModuleConfigBuilder.Create(id: id, configure: config =>
            {
                config.SourcePath = path;
                config.Commands.Run = [new ModuleCommand { Name = "Run", Exec = $"powershell.exe -NoProfile -NonInteractive -File \"{script}\"" }];
            });

            // The coordinator resolves starts by id: persist the manifest and registry entry.
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(module));
            File.WriteAllText(Path.Combine(_graph.Root.DataAppDir, "ASLM_Modules.json"),
                System.Text.Json.JsonSerializer.Serialize(new[] { id }));
            return module;
        }


        // Nested types

        /// <summary>
        /// Progress sink that drops every message, for runner calls whose output the test does not inspect.
        /// </summary>
        private sealed class SilentProgress : IProgress<string>
        {
            /// <summary>
            /// Ignores the reported message.
            /// </summary>
            public void Report(string value) { }
        }
    }
}
