// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Services.Internal;
using ASLM.Tests.TestSupport;

namespace ASLM.Tests.Services.Internal;

/// <summary>
/// Verifies the best-effort local Ollama version probe used to sync engine release tags.
/// </summary>
public sealed class UpdateManagerOllamaProbeTests
{
    [Fact]
    public async Task Probe_returns_the_version_from_a_live_endpoint()
    {
        using var server = LoopbackHttpServer.Start();
        server.ServeJson("api/version", """{"version":"0.12.6"}""");

        var version = await UpdateManager.TryProbeOllamaApiVersionAsync(
            CancellationToken.None, new Uri(server.BaseUri, "api/version"));

        version.Should().Be("0.12.6");
    }

    [Fact]
    public async Task Probe_tolerates_errors_and_unexpected_payloads()
    {
        using var server = LoopbackHttpServer.Start();
        server.ServeJson("api/version", """{"status":"running"}""");

        var noVersionField = await UpdateManager.TryProbeOllamaApiVersionAsync(
            CancellationToken.None, new Uri(server.BaseUri, "api/version"));
        noVersionField.Should().BeNull();

        using var dead = LoopbackHttpServer.Start();
        var unreachable = await UpdateManager.TryProbeOllamaApiVersionAsync(
            CancellationToken.None,
            new Uri($"http://127.0.0.1:{FindClosedPort()}/api/version"));
        unreachable.Should().BeNull();
    }

    private static int FindClosedPort()
    {
        using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
