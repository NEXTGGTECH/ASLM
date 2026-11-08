// Copyright NEXTGGTECH. Apache License 2.0.

using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace ASLM.Tests.TestSupport
{
    /// <summary>
    /// A real loopback HTTP server for download and local-runtime scenarios
    /// (range-resumable file payloads, JSON endpoints, NDJSON streaming).
    /// Register handlers before the code under test hits the server.
    /// The server listens on 127.0.0.1 only, serves each request on its own task, and records every request
    /// (including those for unregistered paths, which receive 404). Disposing stops it and waits briefly for
    /// the accept loop to end.
    /// </summary>
    public sealed class LoopbackHttpServer : IDisposable
    {
        // Fields

        /// <summary>
        /// Listener that accepts the loopback connections.
        /// </summary>
        private readonly HttpListener _listener = new();

        /// <summary>
        /// Signals the accept loop to stop when the server is disposed.
        /// </summary>
        private readonly CancellationTokenSource _shutdown = new();

        /// <summary>
        /// Background task that accepts requests until shutdown.
        /// </summary>
        private readonly Task _loop;

        /// <summary>
        /// Registered handlers keyed by absolute request path (case-insensitive, leading slash included).
        /// </summary>
        private readonly ConcurrentDictionary<string, Func<HttpListenerContext, Task>> _routes = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Requests received so far, in arrival order.
        /// </summary>
        private readonly ConcurrentQueue<ServedRequest> _requests = new();


        // Initialization

        /// <summary>
        /// Lets the caller prepare the listener and then starts the accept loop.
        /// </summary>
        /// <param name="baseUri">Address that is reported through <see cref="BaseUri"/>.</param>
        /// <param name="initialize">Configures and starts the listener before accepting begins.</param>
        private LoopbackHttpServer(Uri baseUri, Action<HttpListener> initialize)
        {
            BaseUri = baseUri;
            initialize(_listener);
            _loop = Task.Run(() => AcceptLoopAsync(_shutdown.Token));
        }


        // Properties

        /// <summary>
        /// Gets the loopback base address every registered path hangs off.
        /// </summary>
        public Uri BaseUri { get; }

        /// <summary>
        /// Gets every request the server has received, oldest first, as a snapshot.
        /// </summary>
        public IReadOnlyList<ServedRequest> Requests => _requests.ToArray();


        // Startup

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


        // Route registration

        /// <summary>
        /// Registers a handler for one absolute path (with or without leading slash).
        /// A later registration for the same path replaces the earlier one.
        /// </summary>
        public LoopbackHttpServer Map(string path, Func<HttpListenerContext, Task> handle)
        {
            _routes[path.StartsWith('/') ? path : "/" + path] = handle;
            return this;
        }

        /// <summary>
        /// Serves a byte payload with optional single-part HTTP Range support.
        /// Only the start offset of a <c>bytes=from-</c> range is honored; the response then runs to the end
        /// of the payload with status 206 and a <c>Content-Range</c> header. Without a Range header the full
        /// payload is sent.
        /// </summary>
        public LoopbackHttpServer ServeBytes(string path, byte[] content, string contentType = "application/octet-stream") =>
            Map(path, async context =>
            {
                context.Response.ContentType = contentType;

                // A resumed download asks for the remaining bytes with a Range header.
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
        /// Serves an already serialized JSON string as <c>application/json</c>.
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
        /// Each line is written and flushed on its own, followed by a 10 ms pause.
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


        // Lifecycle

        /// <summary>
        /// Stops the accept loop and the listener and waits up to two seconds for the loop to finish.
        /// </summary>
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
                // A faulted accept loop must not fail test cleanup.
            }
        }


        // Request handling

        /// <summary>
        /// Accepts connections until shutdown and hands each one to its own task, so slow handlers never block
        /// the next request. Ends quietly when the token is cancelled or the listener is closed.
        /// </summary>
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

        /// <summary>
        /// Records the request, runs the handler registered for its path (404 when there is none), answers
        /// 500 when the handler throws, and always closes the response.
        /// </summary>
        private async Task HandleAsync(HttpListenerContext context)
        {
            // Record the request first, so tests can assert on it even when the handler fails.
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
                    // The handler already started the response, so the status line can no longer change.
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
                    // The client may have disconnected already; there is nothing left to clean up.
                }
            }
        }


        // Helpers

        /// <summary>
        /// Finds a free loopback port by binding a temporary listener to port 0 and releasing it again.
        /// Another process could take the port before the server binds it.
        /// </summary>
        private static int GetFreeLoopbackPort()
        {
            using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }


        // Nested types

        /// <summary>
        /// One request received by the server: HTTP method, full URL, and the raw <c>Range</c> header (null when absent).
        /// </summary>
        public sealed record ServedRequest(string Method, Uri Url, string? RangeHeader);
    }
}
