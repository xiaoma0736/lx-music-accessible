using System.Net;
using System.Net.Http;

namespace LXMusicAccessible.Tests.Infrastructure;

internal sealed class RejectingHttpMessageHandler : HttpMessageHandler
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Method, string Uri), Func<CancellationToken, Task<HttpResponseMessage>>> _responses = new();
    private readonly List<(string Method, string Uri)> _requests = new();

    public IReadOnlyList<(string Method, string Uri)> Requests
    {
        get
        {
            lock (_gate) return _requests.ToArray();
        }
    }

    public void Register(HttpMethod method, Uri uri, Func<HttpResponseMessage> responseFactory)
    {
        if (method is null) throw new ArgumentNullException(nameof(method));
        if (uri is null) throw new ArgumentNullException(nameof(uri));
        if (responseFactory is null) throw new ArgumentNullException(nameof(responseFactory));
        lock (_gate)
        {
            _responses[(method.Method, uri.AbsoluteUri)] = _ => Task.FromResult(responseFactory());
        }
    }

    public void RegisterAsync(
        HttpMethod method,
        Uri uri,
        Func<CancellationToken, Task<HttpResponseMessage>> responseFactory)
    {
        if (method is null) throw new ArgumentNullException(nameof(method));
        if (uri is null) throw new ArgumentNullException(nameof(uri));
        if (responseFactory is null) throw new ArgumentNullException(nameof(responseFactory));
        lock (_gate) _responses[(method.Method, uri.AbsoluteUri)] = responseFactory;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var uri = request.RequestUri?.AbsoluteUri
            ?? throw new InvalidOperationException("HTTP requests must have an absolute URI.");
        var key = (request.Method.Method, uri);
        Func<CancellationToken, Task<HttpResponseMessage>> responseFactory;
        lock (_gate)
        {
            _requests.Add(key);
            if (!_responses.TryGetValue(key, out responseFactory!))
            {
                throw new InvalidOperationException(
                    $"Unregistered HTTP request was blocked: {request.Method} {uri}");
            }
        }

        var response = await responseFactory(cancellationToken);
        response.RequestMessage = request;
        return response;
    }

    public void RegisterText(HttpMethod method, Uri uri, string content)
    {
        Register(method, uri, () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(content)
        });
    }
}
