// Copyright NEXTGGTECH. Apache License 2.0.

using System.Net;
using System.Text;
using System.Text.Json;
using ASLM.Models;
using ASLM.Services.API;
using ASLM.Tests.TestSupport;

namespace ASLM.Tests.Services.API;

/// <summary>
/// Drives the real loopback interop JSON API over HTTP: registry snapshots, route
/// errors, and the caller-must-be-running rule for coordinated module starts.
/// </summary>
public sealed class AslmModuleInteropServerTests : IDisposable
{
    private readonly TestServiceGraph _graph = new();
    private readonly AslmMirrorServer _mirror;
    private readonly AslmModuleInteropServer _server;
    private readonly HttpClient _client = new();

    public AslmModuleInteropServerTests()
    {
        _mirror = new AslmMirrorServer(_graph.AppData, _graph.Ports, _graph.Installer,
            TestLoggerFactory.Create<AslmMirrorServer>());
        _server = new AslmModuleInteropServer(_graph.AppData, _graph.Ports, _mirror, _graph.Installer,
            _graph.Runner, _graph.Coordinator, _graph.InteropHost,
            TestLoggerFactory.Create<AslmModuleInteropServer>());
    }

    public void Dispose()
    {
        _client.Dispose();
        _server.Dispose();
        _mirror.Dispose();
        _graph.Dispose();
    }

    [Fact]
    public async Task Started_server_serves_the_registry_and_the_interop_host_state()
    {
        await _server.EnsureStartedAsync();

        _server.IsRunning.Should().BeTrue();
        _server.LastError.Should().BeEmpty();
        _graph.InteropHost.TryGetListening(out var listeningUrl, out var port).Should().BeTrue();
        port.Should().Be(new Uri(_server.BaseUrl).Port);
        listeningUrl.Should().NotBeNullOrWhiteSpace();

        using var response = await _client.GetAsync(_server.BaseUrl + "v1/registry");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var root = document.RootElement;
        root.GetProperty("interopBaseUrl").GetString().Should().NotBeNullOrWhiteSpace();
        root.TryGetProperty("installedModules", out _).Should().BeTrue();
        root.TryGetProperty("runningModules", out _).Should().BeTrue();
        root.TryGetProperty("aslmApi", out _).Should().BeTrue();
    }

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

    [Fact]
    public async Task A_running_caller_can_request_itself_and_receives_mapped_statuses()
    {
        await _server.EnsureStartedAsync();
        var module = CreateRunnableModule();
        (await _graph.Runner.ExecuteRunAsync(module, new SilentProgress(), CancellationToken.None))
            .Should().BeTrue();
        try
        {
            using var alreadyRunning = await PostStartAsync(module.Id, module.Id);
            alreadyRunning.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await alreadyRunning.Content.ReadAsStringAsync();
            body.Should().Contain("alreadyRunning").And.Contain(module.Id);

            using var notFound = await PostStartAsync(module.Id, "does-not-exist");
            notFound.StatusCode.Should().Be(HttpStatusCode.OK);
            (await notFound.Content.ReadAsStringAsync()).Should().Contain("notFound");
        }
        finally
        {
            await _graph.Runner.StopModuleAsync(module.SourcePath);
        }
    }

    private async Task<HttpResponseMessage> PostStartAsync(string caller, string target)
    {
        var payload = JsonSerializer.Serialize(new { callerModuleId = caller, moduleIds = new[] { target } });
        return await _client.PostAsync(_server.BaseUrl + "v1/modules/start",
            new StringContent(payload, Encoding.UTF8, "application/json"));
    }

    private ModuleConfig CreateRunnableModule()
    {
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

    private sealed class SilentProgress : IProgress<string>
    {
        public void Report(string value) { }
    }
}
