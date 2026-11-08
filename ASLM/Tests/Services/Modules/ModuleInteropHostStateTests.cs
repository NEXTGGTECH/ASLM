// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Tests.Services.Modules
{
    /// <summary>
    /// Verifies how the interop host state stores, validates and clears the listener endpoint.
    /// </summary>
    public sealed class ModuleInteropHostStateTests
    {
        /// <summary>
        /// Verifies that a fresh state with no recorded endpoint reports that nothing is listening.
        /// </summary>
        [Fact]
        public void TryGetListening_returns_false_when_cleared()
        {
            var state = new ModuleInteropHostState();

            state.TryGetListening(out _, out _).Should().BeFalse();
        }

        /// <summary>
        /// Verifies that a recorded endpoint is returned unchanged.
        /// </summary>
        [Fact]
        public void SetListening_and_TryGetListening_round_trip()
        {
            var state = new ModuleInteropHostState();

            state.SetListening("http://127.0.0.1:12345/", 12345);

            state.TryGetListening(out var baseUrl, out var port).Should().BeTrue();
            baseUrl.Should().Be("http://127.0.0.1:12345/");
            port.Should().Be(12345);
        }

        /// <summary>
        /// Verifies that an endpoint with an empty URL or a port outside 1-65535 is not reported as listening.
        /// </summary>
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

        /// <summary>
        /// Verifies that clearing the state after an active listener makes it report that nothing is listening.
        /// </summary>
        [Fact]
        public void Clear_removes_active_listener()
        {
            var state = new ModuleInteropHostState();
            state.SetListening("http://127.0.0.1:9000/", 9000);

            state.Clear();

            state.TryGetListening(out _, out _).Should().BeFalse();
        }
    }
}
