// Copyright NEXTGGTECH. Apache License 2.0.

using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace ASLM.Tests.TestSupport;

/// <summary>
/// A real loopback HTTP server for download and local-runtime scenarios
/// (range-resumable file payloads, JSON endpoints, NDJSON streaming).
/// Register handlers before the code under test hits the server.
/// </summary>
public sealed class LoopbackHttpServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _loop;
    private readonly ConcurrentDictionary<string, Func<HttpListenerContext, Task>> _routes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<ServedRequest> _requests = new();

    private LoopbackHttpServer(Uri baseUri, Action<HttpListener> initialize)
    {
        BaseUri = baseUri;
        initialize(_listener);
        _loop = Task.Run(() => AcceptLoopAsync(_shutdown.Token));
    }

    /// <summary>
    /// Gets the loopback base address every registered path hangs off.
    /// </summary>
    public Uri BaseUri { get; }

    /// <summary>
    /// Gets every request the server has served, oldest first.
    /// </summary>
    public IReadOnlyList<ServedRequest> Requests => _requests.ToArray();

    /// <summary>
    /// Starts the server on an OS-assigned loopback port.
    /// </summary>
    public static LoopbackHttpServer Start()
    {
        var port = GetFreeLoopbackPort();
        var uri = new Uri($"http://127.0.0.1:{port}/");
        return new LoopbackHttpServer(uri, listener =>
        {
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
        });
    }

    /// <summary>
    /// Registers a handler for one absolute path (with or without leading slash).
    /// </summary>
    public LoopbackHttpServer Map(string path, Func<HttpListenerContext, Task> handle)
    {
        _routes[path.StartsWith('/') ? path : "/" + path] = handle;
        return this;
    }

    /// <summary>
    /// Serves a byte payload with optional single-part HTTP Range support.
    /// </summary>
    public LoopbackHttpServer ServeBytes(string path, byte[] content, string contentType = "application/octet-stream") =>
        Map(path, async context =>
        {
            context.Response.ContentType = contentType;
            var rangeHeader = context.Request.Headers["Range"];
            if (rangeHeader != null && rangeHeader.StartsWith("bytes=")
                && int.TryParse(rangeHeader["bytes=".Length..].Split('-')[0], out var from))
            {
                context.Response.StatusCode = (int)HttpStatusCode.PartialContent;
                context.Response.Headers.Add("Content-Range", $"bytes {from}-{content.Length - 1}/{content.Length}");
                await context.Response.OutputStream.WriteAsync(content.AsMemory(from));
            }
            else
            {
                await context.Response.OutputStream.WriteAsync(content);
            }
        });

    /// <summary>
    /// Serves a serialized JSON payload.
    /// </summary>
    public LoopbackHttpServer ServeJson(string path, string json) =>
        Map(path, async context =>
        {
            context.Response.ContentType = "application/json";
            var buffer = Encoding.UTF8.GetBytes(json);
            await context.Response.OutputStream.WriteAsync(buffer);
        });

    /// <summary>
    /// Serves newline-delimited JSON chunks with small delays to exercise streaming readers.
    /// </summary>
    public LoopbackHttpServer ServeNdjson(string path, params string[] jsonLines) =>
        Map(path, async context =>
        {
            context.Response.ContentType = "application/x-ndjson";
            foreach (var line in jsonLines)
            {
                var buffer = Encoding.UTF8.GetBytes(line + "\n");
                await context.Response.OutputStream.WriteAsync(buffer);
                await context.Response.OutputStream.FlushAsync();
                await Task.Delay(10);
            }
        });

    /// <inheritdoc />
    public void Dispose()
    {
        _shutdown.Cancel();
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch
        {
            // Listener teardown races with in-flight requests.
        }

        try
        {
            _loop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(ct);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or HttpListenerException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(context), CancellationToken.None);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        _requests.Enqueue(new ServedRequest(context.Request.HttpMethod, context.Request.Url!,
            context.Request.Headers["Range"]));
        try
        {
            if (_routes.TryGetValue(context.Request.Url!.AbsolutePath, out var handler))
            {
                await handler(context);
            }
            else
            {
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
            }
        }
        catch (Exception)
        {
            try
            {
                context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
            }
            catch
            {
            }
        }
        finally
        {
            try
            {
                context.Response.Close();
            }
            catch
            {
            }
        }
    }

    private static int GetFreeLoopbackPort()
    {
        using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>
    /// One request served by the fake server.
    /// </summary>
    public sealed record ServedRequest(string Method, Uri Url, string? RangeHeader);
}
