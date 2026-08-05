// Copyright NEXTGGTECH. Apache License 2.0.


namespace ASLM.Tests.Services;

public sealed class ModuleInteropHostStateTests
{
    [Fact]
    public void TryGetListening_returns_false_when_cleared()
    {
        var state = new ModuleInteropHostState();

        state.TryGetListening(out _, out _).Should().BeFalse();
    }

    [Fact]
    public void SetListening_and_TryGetListening_round_trip()
    {
        var state = new ModuleInteropHostState();

        state.SetListening("http://127.0.0.1:12345/", 12345);

        state.TryGetListening(out var baseUrl, out var port).Should().BeTrue();
        baseUrl.Should().Be("http://127.0.0.1:12345/");
        port.Should().Be(12345);
    }

    [Theory]
    [InlineData("", 8080)]
    [InlineData("http://127.0.0.1/", 0)]
    [InlineData("http://127.0.0.1/", 70000)]
    public void TryGetListening_rejects_invalid_endpoints(string baseUrl, int port)
    {
        var state = new ModuleInteropHostState();
        state.SetListening(baseUrl, port);

        state.TryGetListening(out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Clear_removes_active_listener()
    {
        var state = new ModuleInteropHostState();
        state.SetListening("http://127.0.0.1:9000/", 9000);

        state.Clear();

        state.TryGetListening(out _, out _).Should().BeFalse();
    }
}
