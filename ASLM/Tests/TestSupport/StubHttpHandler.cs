// Copyright NEXTGGTECH. Apache License 2.0.

using System.Net;
using System.Text;

namespace ASLM.Tests.TestSupport;

/// <summary>
/// A routing <see cref="HttpMessageHandler"/> fake: register responders for absolute URLs
/// (with or without a trailing <c>*</c> wildcard) and inspect every request the code under
/// test has sent. Unmatched requests fail with a synthetic 599 status so missing
/// registrations surface as visible assertion failures.
/// </summary>
public sealed class StubHttpHandler : HttpMessageHandler
{
    private const HttpStatusCode UnregisteredStatusCode = (HttpStatusCode)599;

    private readonly List<(Func<Uri, bool> Matches, Func<RequestContext, HttpResponseMessage> Respond)> _routes = [];
    private readonly List<RecordedRequest> _requests = [];
    private readonly object _lock = new();

    /// <summary>
    /// Gets every request that passed through the handler, oldest first.
    /// </summary>
    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_lock)
            {
                return _requests.ToArray();
            }
        }
    }

    /// <summary>
    /// Registers a responder for one absolute URL. A URL ending in <c>*</c> matches by prefix.
    /// The last matching registration wins.
    /// </summary>
    public StubHttpHandler On(string url, Func<RequestContext, HttpResponseMessage> respond)
    {
        lock (_lock)
        {
            if (url.EndsWith('*'))
            {
                var prefix = url[..^1];
                _routes.Add((uri => uri.ToString().StartsWith(prefix, StringComparison.OrdinalIgnoreCase), respond));
            }
            else
            {
                _routes.Add((uri => string.Equals(uri.ToString(), url, StringComparison.OrdinalIgnoreCase), respond));
            }
        }

        return this;
    }

    /// <summary>
    /// Registers a fixed JSON response for one URL.
    /// </summary>
    public StubHttpHandler OnJson(string url, string json, HttpStatusCode status = HttpStatusCode.OK) =>
        On(url, _ => Json(json, status));

    /// <summary>
    /// Registers a fixed byte payload with a content type for one URL.
    /// </summary>
    public StubHttpHandler OnBytes(string url, byte[] content, string contentType = "application/octet-stream",
        HttpStatusCode status = HttpStatusCode.OK) =>
        On(url, _ => new HttpResponseMessage(status)
        {
            Content = new ByteArrayContent(content)
            {
                Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType) }
            }
        });

    /// <summary>
    /// Registers a transport failure for one URL.
    /// </summary>
    public StubHttpHandler OnNetworkError(string url, Func<Exception> createException) =>
        On(url, _ => throw createException());

    /// <summary>
    /// Builds a JSON response with the GitHub API content type.
    /// </summary>
    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (_lock)
        {
            _requests.Add(new RecordedRequest(request.Method, request.RequestUri!, request.Headers, body));
        }

        Func<RequestContext, HttpResponseMessage>? responder = null;
        lock (_lock)
        {
            for (var index = _routes.Count - 1; index >= 0; index--)
            {
                if (_routes[index].Matches(request.RequestUri!))
                {
                    responder = _routes[index].Respond;
                    break;
                }
            }
        }

        if (responder == null)
        {
            return new HttpResponseMessage(UnregisteredStatusCode)
            {
                Content = new StringContent($"No stub registered for {request.RequestUri}")
            };
        }

        return responder(new RequestContext(request, body));
    }

    /// <summary>
    /// The request handed to a responder.
    /// </summary>
    public sealed record RequestContext(HttpRequestMessage Request, string? Body);

    /// <summary>
    /// One recorded outgoing request.
    /// </summary>
    public sealed record RecordedRequest(HttpMethod Method, Uri Url,
        System.Net.Http.Headers.HttpRequestHeaders Headers, string? Body);
}
