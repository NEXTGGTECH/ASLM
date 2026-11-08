// Copyright NEXTGGTECH. Apache License 2.0.

using System.Net;
using System.Text;

namespace ASLM.Tests.TestSupport
{
    /// <summary>
    /// A routing <see cref="HttpMessageHandler"/> fake: register responders for absolute URLs
    /// (with or without a trailing <c>*</c> wildcard) and inspect every request the code under
    /// test has sent. Unmatched requests fail with a synthetic 599 status so missing
    /// registrations surface as visible assertion failures.
    /// </summary>
    public sealed class StubHttpHandler : HttpMessageHandler
    {
        // Fields and constants

        /// <summary>
        /// Non-standard status returned for requests that match no registration.
        /// </summary>
        private const HttpStatusCode UnregisteredStatusCode = (HttpStatusCode)599;

        /// <summary>
        /// Registered URL matchers with their responders, in registration order.
        /// </summary>
        private readonly List<(Func<Uri, bool> Matches, Func<RequestContext, HttpResponseMessage> Respond)> _routes = [];

        /// <summary>
        /// Requests seen so far, in arrival order.
        /// </summary>
        private readonly List<RecordedRequest> _requests = [];

        /// <summary>
        /// Guards the route and request lists, because the code under test may send requests concurrently.
        /// </summary>
        private readonly object _lock = new();


        // Inspection

        /// <summary>
        /// Gets a snapshot of every request that passed through the handler, oldest first,
        /// including requests that matched no registration.
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


        // Route registration

        /// <summary>
        /// Registers a responder for one absolute URL. A URL ending in <c>*</c> matches by prefix.
        /// Matching is case-insensitive and the last matching registration wins.
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
        /// Registers a transport failure for one URL: the exception from the factory is thrown from the send call.
        /// </summary>
        public StubHttpHandler OnNetworkError(string url, Func<Exception> createException) =>
            On(url, _ => throw createException());

        /// <summary>
        /// Builds a UTF-8 JSON response with the <c>application/json</c> content type.
        /// </summary>
        public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };


        // Request handling

        /// <summary>
        /// Records the request with its body, then answers with the most recently registered responder whose
        /// URL matches, or with a 599 response naming the URL when none matches.
        /// </summary>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            // Capture the body as text, so tests can assert on it even after the request content is disposed.
            var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (_lock)
            {
                _requests.Add(new RecordedRequest(request.Method, request.RequestUri!, request.Headers, body));
            }

            // Search from the newest registration backwards so later registrations override earlier ones.
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

            // Unmatched requests fail visibly instead of returning an empty success.
            if (responder == null)
            {
                return new HttpResponseMessage(UnregisteredStatusCode)
                {
                    Content = new StringContent($"No stub registered for {request.RequestUri}")
                };
            }

            return responder(new RequestContext(request, body));
        }


        // Nested types

        /// <summary>
        /// The request handed to a responder: the live message and its body, which was read as text beforehand
        /// (null when the request has no content).
        /// </summary>
        public sealed record RequestContext(HttpRequestMessage Request, string? Body);

        /// <summary>
        /// One outgoing request as recorded when it was sent, with its body read as text (null when absent).
        /// </summary>
        public sealed record RecordedRequest(HttpMethod Method, Uri Url,
            System.Net.Http.Headers.HttpRequestHeaders Headers, string? Body);
    }
}
