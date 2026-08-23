using System.Net;
using System.Net.Http;

namespace LXMusicAccessible.Tests.Infrastructure;

internal sealed class RejectingHttpMessageHandler : HttpMessageHandler
{
    private readonly Dictionary<(string Method, string Uri), Func<HttpResponseMessage>> _responses = new();
    private readonly List<(string Method, string Uri)> _requests = new();

    public IReadOnlyList<(string Method, string Uri)> Requests => _requests;

    public void Register(HttpMethod method, Uri uri, Func<HttpResponseMessage> responseFactory)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(responseFactory);
        _responses[(method.Method, uri.AbsoluteUri)] = responseFactory;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var uri = request.RequestUri?.AbsoluteUri
            ?? throw new InvalidOperationException("HTTP requests must have an absolute URI.");
        var key = (request.Method.Method, uri);
        _requests.Add(key);

        if (!_responses.TryGetValue(key, out var responseFactory))
        {
            throw new InvalidOperationException(
                $"Unregistered HTTP request was blocked: {request.Method} {uri}");
        }

        var response = responseFactory();
        response.RequestMessage = request;
        return Task.FromResult(response);
    }

    public void RegisterText(HttpMethod method, Uri uri, string content)
    {
        Register(method, uri, () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(content)
        });
    }
}
