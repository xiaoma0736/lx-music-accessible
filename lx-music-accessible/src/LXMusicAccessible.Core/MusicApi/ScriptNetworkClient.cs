using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LXMusicAccessible.Core.MusicApi;

/// <summary>HTTP boundary used only by third-party source scripts.</summary>
public sealed class ScriptNetworkClient : IDisposable
{
    public const int MaxScriptBytes = 1024 * 1024;
    public const int MaxResponseBytes = 2 * 1024 * 1024;
    public const int MaxRequestBodyBytes = 1024 * 1024;
    private const int MaxRedirects = 5;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly HashSet<int> AllowedPorts = [80, 443];
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly HttpClient _client;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolver;

    public ScriptNetworkClient()
        : this(ResolveAsync)
    {
    }

    public ScriptNetworkClient(Func<string, CancellationToken, Task<IPAddress[]>> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _resolver = resolver;
        _client = new HttpClient(CreateProductionHandler(_resolver), disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public ScriptNetworkClient(
        HttpMessageHandler handler,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolver = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _resolver = resolver ?? ResolveAsync;
        _client = new HttpClient(handler, disposeHandler: false)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public async Task<string> GetScriptAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("lx-music-request/1.0");
        var response = await SendAsync(request, MaxScriptBytes, cancellationToken);
        if (response.StatusCode is < 200 or >= 300)
            throw new ScriptNetworkException("http_status_rejected");
        try
        {
            return StrictUtf8.GetString(response.Body);
        }
        catch (DecoderFallbackException)
        {
            throw new ScriptNetworkException("script_encoding_invalid");
        }
    }

    public async Task<ScriptHttpResponse> SendAsync(
        HttpRequestMessage request,
        int maxResponseBytes = MaxResponseBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RequestUri is not { IsAbsoluteUri: true } initialUri)
            throw new ScriptNetworkException("url_invalid");
        if (maxResponseBytes <= 0 || maxResponseBytes > MaxResponseBytes)
            throw new ArgumentOutOfRangeException(nameof(maxResponseBytes));

        var template = await RequestTemplate.CreateAsync(request, cancellationToken);
        var currentUri = initialUri;
        var currentMethod = request.Method;
        var includeBody = true;
        var includeSensitiveHeaders = true;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        try
        {
            for (var redirectCount = 0; ; redirectCount++)
            {
                await ValidateTargetAsync(currentUri, timeout.Token);
                using var currentRequest = template.Create(
                    currentUri,
                    currentMethod,
                    includeBody,
                    includeSensitiveHeaders);
                HttpResponseMessage response;
                try
                {
                    response = await _client.SendAsync(
                    currentRequest,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token);
                }
                catch (HttpRequestException ex) when (FindSecurityException(ex) is { } securityException)
                {
                    throw securityException;
                }
                using (response)
                {

                    if (!IsRedirect(response.StatusCode))
                    {
                        var body = await ReadResponseBodyAsync(response, maxResponseBytes, timeout.Token);
                        return new ScriptHttpResponse(
                            (int)response.StatusCode,
                            GetResponseHeaders(response),
                            body);
                    }

                    if (redirectCount >= MaxRedirects)
                        throw new ScriptNetworkException("redirect_limit_exceeded");
                    var location = response.Headers.Location
                        ?? throw new ScriptNetworkException("redirect_location_invalid");
                    var nextUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
                    includeSensitiveHeaders &= SameOrigin(currentUri, nextUri);
                    ApplyRedirectMethod(response.StatusCode, ref currentMethod, ref includeBody);
                    currentUri = nextUri;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ScriptNetworkException("request_timeout");
        }
    }

    private static ScriptNetworkException? FindSecurityException(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
            if (current is ScriptNetworkException securityException) return securityException;
        return null;
    }

    private async Task ValidateTargetAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (uri.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !AllowedPorts.Contains(uri.Port))
        {
            throw new ScriptNetworkException("network_target_blocked");
        }

        var host = uri.IdnHost.TrimEnd('.');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".home.arpa", StringComparison.OrdinalIgnoreCase))
        {
            throw new ScriptNetworkException("network_target_blocked");
        }

        await ResolvePublicAddressesAsync(host, _resolver, cancellationToken);
    }

    private static async Task<IPAddress[]> ResolvePublicAddressesAsync(
        string host,
        Func<string, CancellationToken, Task<IPAddress[]>> resolver,
        CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        try
        {
            addresses = await resolver(host, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            throw new ScriptNetworkException("dns_resolution_failed");
        }

        if (addresses.Length == 0 || addresses.Any(address => !IsPublicAddress(address)))
            throw new ScriptNetworkException("network_target_blocked");
        return addresses;
    }

    private static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var a = bytes[0];
            var b = bytes[1];
            return a != 0
                && a != 10
                && a != 127
                && !(a == 100 && b is >= 64 and <= 127)
                && !(a == 169 && b == 254)
                && !(a == 172 && b is >= 16 and <= 31)
                && !(a == 192 && b == 0 && bytes[2] is 0 or 2)
                && !(a == 192 && b == 88 && bytes[2] == 99)
                && !(a == 192 && b == 168)
                && !(a == 198 && b is 18 or 19)
                && !(a == 198 && b == 51 && bytes[2] == 100)
                && !(a == 203 && b == 0 && bytes[2] == 113)
                && a < 224;
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6) return false;
        var globalUnicast = (bytes[0] & 0xe0) == 0x20;
        var documentation = bytes[0] == 0x20 && bytes[1] == 0x01
            && bytes[2] == 0x0d && bytes[3] == 0xb8;
        return globalUnicast && !documentation;
    }

    private static SocketsHttpHandler CreateProductionHandler(
        Func<string, CancellationToken, Task<IPAddress[]>> resolver)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            UseCookies = false,
            UseProxy = false
        };
        handler.ConnectCallback = async (context, cancellationToken) =>
        {
            if (!AllowedPorts.Contains(context.DnsEndPoint.Port))
                throw new ScriptNetworkException("network_target_blocked");
            var addresses = await ResolvePublicAddressesAsync(
                context.DnsEndPoint.Host,
                resolver,
                cancellationToken);
            Exception? lastError = null;
            foreach (var address in addresses)
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(
                        new IPEndPoint(address, context.DnsEndPoint.Port),
                        cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lastError = ex;
                    socket.Dispose();
                }
            }
            throw new HttpRequestException("Unable to connect to the validated script host.", lastError);
        };
        return handler;
    }

    private static async Task<byte[]> ReadResponseBodyAsync(
        HttpResponseMessage response,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > maxBytes)
            throw new ScriptNetworkException("response_limit_exceeded");

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        var raw = await ReadBoundedAsync(input, maxBytes, "response_limit_exceeded", cancellationToken);
        if (response.Content.Headers.ContentEncoding.Count == 0) return raw;

        Stream decoded = new MemoryStream(raw, writable: false);
        try
        {
            foreach (var encoding in response.Content.Headers.ContentEncoding.Reverse())
            {
                decoded = encoding.ToLowerInvariant() switch
                {
                    "gzip" => new GZipStream(decoded, CompressionMode.Decompress),
                    "deflate" => new ZLibStream(decoded, CompressionMode.Decompress),
                    "br" => new BrotliStream(decoded, CompressionMode.Decompress),
                    "identity" => decoded,
                    _ => throw new ScriptNetworkException("content_encoding_unsupported")
                };
            }
            return await ReadBoundedAsync(
                decoded,
                maxBytes,
                "decompression_limit_exceeded",
                cancellationToken);
        }
        catch (InvalidDataException)
        {
            throw new ScriptNetworkException("content_encoding_invalid");
        }
        finally
        {
            await decoded.DisposeAsync();
        }
    }

    internal static async Task<byte[]> ReadBoundedAsync(
        Stream input,
        int maxBytes,
        string limitCode,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream(Math.Min(maxBytes, 16 * 1024));
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) return output.ToArray();
            if (output.Length + read > maxBytes)
                throw new ScriptNetworkException(limitCode);
            output.Write(buffer, 0, read);
        }
    }

    private static Dictionary<string, string> GetResponseHeaders(HttpResponseMessage response)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.Headers.Concat(response.Content.Headers))
            headers[header.Key] = string.Join(", ", header.Value);
        return headers;
    }

    private static bool IsRedirect(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static void ApplyRedirectMethod(
        HttpStatusCode statusCode,
        ref HttpMethod method,
        ref bool includeBody)
    {
        if (statusCode == HttpStatusCode.SeeOther
            || (statusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found
                && method == HttpMethod.Post))
        {
            method = HttpMethod.Get;
            includeBody = false;
        }
    }

    private static bool SameOrigin(Uri left, Uri right)
        => left.Scheme == right.Scheme
            && left.IdnHost.Equals(right.IdnHost, StringComparison.OrdinalIgnoreCase)
            && left.Port == right.Port;

    private static Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
        => Dns.GetHostAddressesAsync(host, cancellationToken);

    public void Dispose() => _client.Dispose();

    private sealed record RequestTemplate(
        IReadOnlyList<KeyValuePair<string, IEnumerable<string>>> Headers,
        byte[]? Body,
        IReadOnlyList<KeyValuePair<string, IEnumerable<string>>> ContentHeaders)
    {
        public static async Task<RequestTemplate> CreateAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            byte[]? body = null;
            IReadOnlyList<KeyValuePair<string, IEnumerable<string>>> contentHeaders = [];
            if (request.Content is not null)
            {
                await using var stream = await request.Content.ReadAsStreamAsync(cancellationToken);
                body = await ReadBoundedAsync(
                    stream,
                    MaxRequestBodyBytes,
                    "request_limit_exceeded",
                    cancellationToken);
                contentHeaders = request.Content.Headers
                    .Select(header => KeyValuePair.Create(
                        header.Key,
                        header.Value.ToArray().AsEnumerable()))
                    .ToArray();
            }
            return new(
                request.Headers.Select(header => KeyValuePair.Create(
                    header.Key,
                    header.Value.ToArray().AsEnumerable())).ToArray(),
                body,
                contentHeaders);
        }

        public HttpRequestMessage Create(
            Uri uri,
            HttpMethod method,
            bool includeBody,
            bool includeSensitiveHeaders)
        {
            var message = new HttpRequestMessage(method, uri);
            foreach (var header in Headers)
            {
                if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)
                    || (!includeSensitiveHeaders && IsSensitive(header.Key))) continue;
                message.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            if (includeBody && Body is not null && method != HttpMethod.Get && method != HttpMethod.Head)
            {
                message.Content = new ByteArrayContent(Body);
                foreach (var header in ContentHeaders)
                    message.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            return message;
        }

        private static bool IsSensitive(string header)
            => header.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                || header.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
                || header.Equals("Cookie", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record ScriptHttpResponse(
    int StatusCode,
    IReadOnlyDictionary<string, string> Headers,
    byte[] Body);

public sealed class ScriptNetworkException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
