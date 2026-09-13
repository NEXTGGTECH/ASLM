// Copyright NEXTGGTECH. Apache License 2.0.

using System.Globalization;
using System.Text.Json;

namespace ASLM.Controls.Downloads;

/// <summary>Reads document geometry through WebView2's DevTools protocol without injecting web code.</summary>
internal sealed class DownloadInfoPreview(Func<string, string, Task<string>> call)
{
    private int _documentVersion;

    public async Task InitializeAsync()
    {
        await call("Emulation.setScrollbarsHidden", "{\"hidden\":true}");
        await call("DOM.enable", "{}");
        await call("LayerTree.enable", "{}");
    }

    public void ResetDocument() => _documentVersion++;

    public async Task<DownloadPreviewSize> MeasureAsync()
    {
        var version = _documentVersion;
        using var snapshot = await ReadAsync("DOMSnapshot.captureSnapshot",
            "{\"computedStyles\":[\"margin-bottom\"]}", version);
        var document = snapshot.RootElement.GetProperty("documents")[0];
        var size = ReadSize(snapshot.RootElement);
        if (document.TryGetProperty("scrollOffsetY", out var offset) && offset.GetDouble() != 0)
        {
            var nodes = document.GetProperty("nodes");
            var rootIndex = nodes.GetProperty("nodeType").EnumerateArray()
                .Select((node, index) => (Type: node.GetInt32(), Index: index)).First(node => node.Type == 1).Index;
            // Content can grow before the host gets the layout event. Reset any temporary
            // browser scroll so the expanded document still starts at the top.
            using var response = await ReadAsync("DOM.scrollIntoViewIfNeeded", JsonSerializer.Serialize(new
            {
                backendNodeId = nodes.GetProperty("backendNodeId")[rootIndex].GetInt32(),
                rect = new { x = 0, y = 0, width = 1, height = 1 }
            }), version);
        }
        return size;
    }

    internal static DownloadPreviewSize ReadSize(JsonElement snapshot)
    {
        var document = snapshot.GetProperty("documents")[0];
        var nodeTypes = document.GetProperty("nodes").GetProperty("nodeType");
        var layout = document.GetProperty("layout");
        var nodeIndices = layout.GetProperty("nodeIndex");
        var bounds = layout.GetProperty("bounds");
        var styles = layout.GetProperty("styles");
        var strings = snapshot.GetProperty("strings");
        var height = 1d;
        var width = 0d;

        // The document's own box is viewport-sized. Real layout boxes allow shrinking and
        // also include overflowing/absolutely positioned content that body bounds can miss.
        for (var index = 0; index < nodeIndices.GetArrayLength(); index++)
        {
            var nodeType = nodeTypes[nodeIndices[index].GetInt32()].GetInt32();
            var box = bounds[index];
            if (nodeType == 9)
            {
                // Snapshot coordinates already include browser zoom. Use its viewport
                // width as well, rather than mixing them with unzoomed CSS pixels.
                width = box[2].GetDouble();
                continue;
            }
            var bottom = box[1].GetDouble() + box[3].GetDouble();
            if (nodeType == 1 && styles[index].GetArrayLength() > 0)
            {
                var margin = strings[styles[index][0].GetInt32()].GetString() ?? "";
                if (margin.EndsWith("px", StringComparison.Ordinal) &&
                    double.TryParse(margin.AsSpan(0, margin.Length - 2), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var pixels))
                    bottom += Math.Max(0, pixels);
            }
            height = Math.Max(height, bottom);
        }

        height = Math.Ceiling(height);
        if (!IsValidHeight(height) || !IsValidHeight(width))
            throw new InvalidOperationException("WebView2 returned invalid preview dimensions.");
        return new DownloadPreviewSize(height, width);
    }

    private async Task<JsonDocument> ReadAsync(string method, string parameters, int version)
    {
        var response = await call(method, parameters);
        if (version != _documentVersion) throw new OperationCanceledException();
        return JsonDocument.Parse(response);
    }

    public static bool IsValidHeight(double height) =>
        double.IsFinite(height) && height > 0 && height <= int.MaxValue;
}

internal readonly record struct DownloadPreviewSize(double Height, double ViewportWidth);
