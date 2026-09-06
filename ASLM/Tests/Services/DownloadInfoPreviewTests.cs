// Copyright NEXTGGTECH. Apache License 2.0.

using System.Text.Json;
using ASLM.Controls.Downloads;

namespace ASLM.Tests.Services;

public sealed class DownloadInfoPreviewTests
{
    [Fact]
    public void Does_not_package_the_old_script_or_styles() =>
        typeof(DownloadInfoPreview).Assembly.GetManifestResourceNames().Should()
            .NotContain(name => name.StartsWith("ASLM.Resources.Web.DownloadInfoPreview.", StringComparison.Ordinal));

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

    [Fact]
    public void Bounds_are_document_coordinates_and_do_not_depend_on_scroll_offset()
    {
        var snapshot = Snapshot(720, 1500, 1500, scrollY: 300);
        DownloadInfoPreview.ReadSize(snapshot).Height.Should().Be(1500);
    }

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

    [Fact]
    public async Task Discards_results_received_after_navigation()
    {
        var pending = new TaskCompletionSource<string>();
        var protocol = new Protocol();
        var preview = new DownloadInfoPreview((method, parameters) => method == "DOMSnapshot.captureSnapshot"
            ? pending.Task : protocol.Call(method, parameters));
        var measurement = preview.MeasureAsync();
        preview.ResetDocument();
        pending.SetResult(Snapshot(720, 1200, 1200).GetRawText());
        await Assert.ThrowsAsync<OperationCanceledException>(() => measurement);
        protocol.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task Resets_only_a_nonzero_browser_scroll_offset_without_injecting_code()
    {
        var protocol = new Protocol { ScrollY = 100 };
        var preview = new DownloadInfoPreview(protocol.Call);
        await preview.MeasureAsync();
        var reset = protocol.Commands.Single(command => command.Method == "DOM.scrollIntoViewIfNeeded");
        using var parameters = JsonDocument.Parse(reset.Parameters);
        parameters.RootElement.GetProperty("backendNodeId").GetInt32().Should().Be(2);
        parameters.RootElement.GetProperty("rect").GetProperty("y").GetInt32().Should().Be(0);

        protocol.Commands.Clear();
        protocol.ScrollY = 0;
        await preview.MeasureAsync();
        protocol.Commands.Should().NotContain(command => command.Method == "DOM.scrollIntoViewIfNeeded");
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.MaxValue)]
    public void Rejects_invalid_layout_dimensions(double height) =>
        DownloadInfoPreview.IsValidHeight(height).Should().BeFalse();

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

    private sealed class Protocol
    {
        public List<(string Method, string Parameters)> Commands { get; } = [];
        public double ScrollY { get; set; }
        public double Height { get; set; } = 1200;

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
