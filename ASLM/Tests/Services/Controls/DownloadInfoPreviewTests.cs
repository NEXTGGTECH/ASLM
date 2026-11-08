// Copyright NEXTGGTECH. Apache License 2.0.

using System.Text.Json;
using ASLM.Controls.Downloads;

namespace ASLM.Tests.Services.Controls
{
    /// <summary>
    /// Verifies <see cref="DownloadInfoPreview"/>, which measures a rendered download description through the
    /// browser's DevTools protocol: the commands it sends, how it derives the content size from a DOM snapshot, and
    /// how it handles stale documents and scroll offsets. A fake protocol replaces the real browser.
    /// </summary>
    public sealed class DownloadInfoPreviewTests
    {
        // Packaging and initialization

        /// <summary>
        /// Verifies that the application assembly embeds no resource of the former script-and-styles based preview.
        /// </summary>
        [Fact]
        public void Does_not_package_the_old_script_or_styles() =>
            typeof(DownloadInfoPreview).Assembly.GetManifestResourceNames().Should()
                .NotContain(name => name.StartsWith("ASLM.Resources.Web.DownloadInfoPreview.", StringComparison.Ordinal));

        /// <summary>
        /// Verifies that initialization sends exactly the scrollbar-hiding, <c>DOM.enable</c>, and
        /// <c>LayerTree.enable</c> commands, in that order, and that scrollbars are requested hidden.
        /// </summary>
        [Fact]
        public async Task Initialization_uses_only_native_browser_commands()
        {
            var protocol = new Protocol();
            await new DownloadInfoPreview(protocol.Call).InitializeAsync();
            protocol.Commands.Select(command => command.Method).Should().Equal(
                "Emulation.setScrollbarsHidden", "DOM.enable", "LayerTree.enable");
            using var parameters = JsonDocument.Parse(protocol.Commands[0].Parameters);
            parameters.RootElement.GetProperty("hidden").GetBoolean().Should().BeTrue();
        }


        // Size calculation

        /// <summary>
        /// Verifies that the measured height comes from the lowest element box (plus a pixel bottom margin, ignoring
        /// negative margins), rounded up and at least one pixel, instead of from the viewport-sized document box, and
        /// that the width is the document box width.
        /// </summary>
        [Theory]
        [InlineData(720, 1500, 1500, 0, 0, 1500)]
        [InlineData(1800, 700, 700, 0, 0, 700)]
        [InlineData(720, 0, 88, 0, 0, 88)]
        [InlineData(720, 0, 0, 0, 0, 1)]
        [InlineData(720, 250.5, 240, 0, 0, 251)]
        [InlineData(720, 100, 120, 0, 20, 140)]
        [InlineData(720, 100, 120, 0, -20, 120)]
        [InlineData(2000, 28, 28, 2000, 0, 2000)]
        [InlineData(2000, 28, 28, 0, 0, 28)]
        public void Measures_real_layout_not_the_old_viewport(
            double viewport, double rootHeight, double bodyHeight, double overflowBottom, double margin, double expected)
        {
            var size = DownloadInfoPreview.ReadSize(Snapshot(viewport, rootHeight, bodyHeight, overflowBottom, margin));
            size.Height.Should().Be(expected);
            size.ViewportWidth.Should().Be(900);
        }

        /// <summary>
        /// Verifies that a snapshot scrolled by 300 px yields the same height as an unscrolled one, because the
        /// layout bounds are document coordinates.
        /// </summary>
        [Fact]
        public void Bounds_are_document_coordinates_and_do_not_depend_on_scroll_offset()
        {
            var snapshot = Snapshot(720, 1500, 1500, scrollY: 300);
            DownloadInfoPreview.ReadSize(snapshot).Height.Should().Be(1500);
        }


        // Live measurement

        /// <summary>
        /// Verifies that every measurement requests a new snapshot, so a changed document height is reported
        /// instead of an earlier result.
        /// </summary>
        [Fact]
        public async Task Refreshes_geometry_without_caching_old_document_nodes()
        {
            var protocol = new Protocol();
            var preview = new DownloadInfoPreview(protocol.Call);
            (await preview.MeasureAsync()).Height.Should().Be(1200);
            protocol.Height = 88;
            (await preview.MeasureAsync()).Height.Should().Be(88);
            protocol.Commands.Count(command => command.Method == "DOMSnapshot.captureSnapshot").Should().Be(2);
        }

        /// <summary>
        /// Verifies that a measurement still waiting for its snapshot fails with
        /// <see cref="OperationCanceledException"/> when the document is reset meanwhile, and that no follow-up
        /// command is sent for the stale result.
        /// </summary>
        [Fact]
        public async Task Discards_results_received_after_navigation()
        {
            // Arrange: a protocol whose snapshot request stays pending until the test completes it.
            var pending = new TaskCompletionSource<string>();
            var protocol = new Protocol();
            var preview = new DownloadInfoPreview((method, parameters) => method == "DOMSnapshot.captureSnapshot"
                ? pending.Task : protocol.Call(method, parameters));

            // Act: start measuring, replace the document, then deliver the old snapshot.
            var measurement = preview.MeasureAsync();
            preview.ResetDocument();
            pending.SetResult(Snapshot(720, 1200, 1200).GetRawText());

            // Assert: the stale measurement is cancelled without any other protocol command.
            await Assert.ThrowsAsync<OperationCanceledException>(() => measurement);
            protocol.Commands.Should().BeEmpty();
        }

        /// <summary>
        /// Verifies that a browser scroll offset other than zero is reset by scrolling the root element to the top
        /// through a protocol command, that nothing is sent once the offset is zero, and that no code is injected.
        /// </summary>
        [Fact]
        public async Task Resets_only_a_nonzero_browser_scroll_offset_without_injecting_code()
        {
            // Act and assert, scrolled document: the root element (backend node 2) is scrolled to y = 0.
            var protocol = new Protocol { ScrollY = 100 };
            var preview = new DownloadInfoPreview(protocol.Call);
            await preview.MeasureAsync();
            var reset = protocol.Commands.Single(command => command.Method == "DOM.scrollIntoViewIfNeeded");
            using var parameters = JsonDocument.Parse(reset.Parameters);
            parameters.RootElement.GetProperty("backendNodeId").GetInt32().Should().Be(2);
            parameters.RootElement.GetProperty("rect").GetProperty("y").GetInt32().Should().Be(0);

            // Act and assert, unscrolled document: no scroll command is sent.
            protocol.Commands.Clear();
            protocol.ScrollY = 0;
            await preview.MeasureAsync();
            protocol.Commands.Should().NotContain(command => command.Method == "DOM.scrollIntoViewIfNeeded");
        }


        // Dimension validation

        /// <summary>
        /// Verifies that non-finite, non-positive, and too large values are rejected as layout dimensions.
        /// </summary>
        [Theory]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(double.MaxValue)]
        public void Rejects_invalid_layout_dimensions(double height) =>
            DownloadInfoPreview.IsValidHeight(height).Should().BeFalse();


        // Helpers

        /// <summary>
        /// Builds a minimal <c>DOMSnapshot.captureSnapshot</c> result with a document node, a root element, a body
        /// element carrying the given bottom margin, and one overflowing element. The document box is
        /// <paramref name="viewport"/> high and 900 wide.
        /// </summary>
        /// <param name="viewport">Height of the document box, which equals the viewport.</param>
        /// <param name="rootHeight">Height of the root element box.</param>
        /// <param name="bodyHeight">Height of the body element box.</param>
        /// <param name="overflowBottom">Height of an extra 20 px wide box that represents overflowing content.</param>
        /// <param name="margin">Bottom margin of the body in pixels.</param>
        /// <param name="scrollY">Vertical scroll offset reported for the document.</param>
        private static JsonElement Snapshot(double viewport, double rootHeight, double bodyHeight,
            double overflowBottom = 0, double margin = 0, double scrollY = 0) => JsonSerializer.SerializeToElement(new
        {
            strings = new[] { margin.ToString(System.Globalization.CultureInfo.InvariantCulture) + "px" },
            documents = new[]
            {
                new
                {
                    scrollOffsetY = scrollY,
                    nodes = new { nodeType = new[] { 9, 1, 1, 1 }, backendNodeId = new[] { 1, 2, 3, 4 } },
                    layout = new
                    {
                        nodeIndex = new[] { 0, 1, 2, 3 },
                        bounds = new[]
                        {
                            new[] { 0d, 0, 900, viewport }, new[] { 0d, 0, 900, rootHeight },
                            new[] { 0d, 0, 900, bodyHeight }, new[] { 0d, 0, 20, overflowBottom }
                        },
                        styles = new[] { Array.Empty<int>(), Array.Empty<int>(), new[] { 0 }, Array.Empty<int>() }
                    }
                }
            }
        });


        // Test doubles

        /// <summary>
        /// Fake DevTools protocol endpoint: records every command and answers the ones the preview is expected to
        /// send; any other command throws, so unplanned protocol use fails the test.
        /// </summary>
        private sealed class Protocol
        {
            /// <summary>
            /// Commands received so far, in order, with their JSON parameters.
            /// </summary>
            public List<(string Method, string Parameters)> Commands { get; } = [];

            /// <summary>
            /// Vertical scroll offset reported in the next snapshots.
            /// </summary>
            public double ScrollY { get; set; }

            /// <summary>
            /// Content height reported in the next snapshots.
            /// </summary>
            public double Height { get; set; } = 1200;

            /// <summary>
            /// Records the command and returns an empty JSON object for setup commands or a snapshot for
            /// <c>DOMSnapshot.captureSnapshot</c>.
            /// </summary>
            public Task<string> Call(string method, string parameters)
            {
                Commands.Add((method, parameters));

                // Any script/style injection or unplanned protocol command makes this test fail.
                return Task.FromResult(method switch
                {
                    "Emulation.setScrollbarsHidden" or "DOM.enable" or "LayerTree.enable" or "DOM.scrollIntoViewIfNeeded" => "{}",
                    "DOMSnapshot.captureSnapshot" => Snapshot(720, Height, Height, scrollY: ScrollY).GetRawText(),
                    _ => throw new InvalidOperationException(method)
                });
            }
        }
    }
}
