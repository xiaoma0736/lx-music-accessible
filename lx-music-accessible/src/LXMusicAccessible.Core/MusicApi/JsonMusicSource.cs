using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LXMusicAccessible.Models;
using Microsoft.Extensions.Logging;

namespace LXMusicAccessible.Core.MusicApi;

public sealed record JsonSourceLyric(
    string Lyric,
    string? Translation,
    string? Romanization,
    string? LxLyric);

public sealed class JsonMusicSource(
    JsonSourceDefinition manifest,
    ScriptNetworkClient networkClient,
    ILogger? logger = null)
{
    public async Task<IReadOnlyList<MusicInfo>> SearchAsync(
        string platform,
        string query,
        int page,
        int pageSize,
        string searchType,
        CancellationToken cancellationToken = default)
    {
        var operation = GetOperation(platform, "search");
        if (operation is null || page < 1 || pageSize < 1) return [];

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["keyword"] = query,
            ["page"] = page.ToString(CultureInfo.InvariantCulture),
            ["pageSize"] = pageSize.ToString(CultureInfo.InvariantCulture),
            ["searchType"] = searchType
        };
        var response = await SendJsonAsync(operation, values, cancellationToken);
        if (response is null) return [];

        var list = SelectList(response.Value, operation.Result.GetValueOrDefault("list"));
        if (list is null) return [];

        var results = new List<MusicInfo>();
        foreach (var item in list)
        {
            if (item.ValueKind is not JsonValueKind.Object) continue;
            var id = SelectString(item, operation.Result.GetValueOrDefault("sourceId"));
            var title = SelectString(item, operation.Result.GetValueOrDefault("title"));
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title)) continue;

            var durationSeconds = SelectString(item, operation.Result.GetValueOrDefault("duration"));
            results.Add(new MusicInfo
            {
                Id = id,
                Title = title,
                Artist = SelectString(item, operation.Result.GetValueOrDefault("artist")) ?? "",
                Album = SelectString(item, operation.Result.GetValueOrDefault("album")) ?? "",
                CoverUrl = SelectString(item, operation.Result.GetValueOrDefault("coverUrl")) ?? "",
                Duration = FormatDuration(durationSeconds),
                Source = NormalizePlatform(platform)
            });
        }
        return results;
    }

    public async Task<string?> GetPlayUrlAsync(
        string platform,
        string sourceId,
        string quality,
        CancellationToken cancellationToken = default)
    {
        var operation = GetOperation(platform, "url");
        if (operation is null) return null;
        var supplierQuality = MapQuality(platform, quality);
        if (supplierQuality is null) return null;

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["sourceId"] = sourceId,
            ["quality"] = supplierQuality,
            ["qualityKey"] = NormalizeQuality(quality),
            ["page"] = "1",
            ["pageSize"] = "1"
        };
        var response = await SendJsonAsync(operation, values, cancellationToken);
        var url = response is null ? null : SelectString(response.Value, operation.Result.GetValueOrDefault("url"));
        return IsHttpUrl(url) ? url : null;
    }

    public async Task<JsonSourceLyric?> GetLyricAsync(
        string platform,
        MusicInfo music,
        CancellationToken cancellationToken = default)
    {
        var operation = GetOperation(platform, "lyric");
        if (operation is null) return null;
        var response = await SendJsonAsync(operation, MusicValues(music), cancellationToken);
        if (response is null) return null;

        var lyric = SelectString(response.Value, operation.Result.GetValueOrDefault("lyric"));
        if (string.IsNullOrWhiteSpace(lyric)) return null;
        return new JsonSourceLyric(
            lyric,
            SelectString(response.Value, operation.Result.GetValueOrDefault("tlyric")),
            SelectString(response.Value, operation.Result.GetValueOrDefault("rlyric")),
            SelectString(response.Value, operation.Result.GetValueOrDefault("lxlyric")));
    }

    public async Task<string?> GetPicAsync(
        string platform,
        MusicInfo music,
        CancellationToken cancellationToken = default)
    {
        var operation = GetOperation(platform, "pic");
        if (operation is null) return null;
        var response = await SendJsonAsync(operation, MusicValues(music), cancellationToken);
        var url = response is null ? null : SelectString(response.Value, operation.Result.GetValueOrDefault("url"));
        return IsHttpUrl(url) ? url : null;
    }

    private static Dictionary<string, string> MusicValues(MusicInfo music) => new(StringComparer.Ordinal)
    {
        ["sourceId"] = music.Id,
        ["title"] = music.Title,
        ["artist"] = music.Artist,
        ["album"] = music.Album,
        ["hash"] = music.Id,
        ["page"] = "1",
        ["pageSize"] = "1"
    };

    private JsonSourceOperation? GetOperation(string platform, string name)
    {
        if (!manifest.Platforms.TryGetValue(ReversePlatform(platform), out var definition))
            return null;
        return name switch
        {
            "search" => definition.Search,
            "url" => definition.Url,
            "lyric" => definition.Lyric,
            "pic" => definition.Pic,
            _ => null
        };
    }

    private async Task<JsonElement?> SendJsonAsync(
        JsonSourceOperation operation,
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = BuildRequest(operation, values);
            var response = await networkClient.SendAsync(
                request,
                ScriptNetworkClient.MaxResponseBytes,
                cancellationToken);
            if (response.StatusCode is < 200 or >= 300) return null;

            var content = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(response.Body);
            using var document = JsonDocument.Parse(content, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
            return document.RootElement.Clone();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ScriptNetworkException
                or HttpRequestException
                or JsonException
                or DecoderFallbackException
                or UriFormatException)
        {
            logger?.LogDebug("JSON source request failed: {ErrorType}", exception.GetType().Name);
            return null;
        }
    }

    private HttpRequestMessage BuildRequest(
        JsonSourceOperation operation,
        IReadOnlyDictionary<string, string> values)
    {
        var method = operation.Method.Equals("POST", StringComparison.OrdinalIgnoreCase)
            ? HttpMethod.Post
            : HttpMethod.Get;
        var request = new HttpRequestMessage(method, BuildUri(operation, values));
        foreach (var (name, value) in operation.Headers)
            request.Headers.TryAddWithoutValidation(name, ReplaceTokens(value, values, encode: false));

        if (method != HttpMethod.Post || operation.Body is null)
            return request;

        var bodyValues = operation.Body.Values.ToDictionary(
            pair => pair.Key,
            pair => ReplaceTokens(pair.Value, values, encode: false),
            StringComparer.Ordinal);
        if (operation.Body.Type.Equals("form", StringComparison.OrdinalIgnoreCase))
        {
            request.Content = new FormUrlEncodedContent(bodyValues);
        }
        else
        {
            var json = JsonSerializer.Serialize(bodyValues);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }
        return request;
    }

    private static Uri BuildUri(JsonSourceOperation operation, IReadOnlyDictionary<string, string> values)
    {
        var url = ReplaceTokens(operation.Url, values, encode: true);
        if (operation.Params.Count > 0)
        {
            var query = operation.Params.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(ReplaceTokens(pair.Value, values, encode: false))}");
            url += (url.Contains("?", StringComparison.Ordinal) ? "&" : "?") + string.Join("&", query);
        }
        return new Uri(url, UriKind.Absolute);
    }

    private string? MapQuality(string platform, string quality)
    {
        if (!manifest.Platforms.TryGetValue(ReversePlatform(platform), out var definition)
            || definition.Url is null)
            return null;
        if (definition.Url.Qualities.TryGetValue(NormalizeQuality(quality), out var supplierQuality)
            && !string.IsNullOrWhiteSpace(supplierQuality))
            return supplierQuality;
        var rawQuality = quality.ToLowerInvariant();
        if (definition.Url.Qualities.TryGetValue(rawQuality, out supplierQuality)
            && !string.IsNullOrWhiteSpace(supplierQuality))
            return supplierQuality;
        var canonicalQuality = NormalizeQuality(quality);
        var identityQuality = canonicalQuality switch
        {
            "standard" => "128k",
            "high" => "320k",
            "lossless" => "flac24bit",
            _ => canonicalQuality
        };
        return definition.Url.Qualities.TryGetValue(identityQuality, out supplierQuality)
            && !string.IsNullOrWhiteSpace(supplierQuality)
            ? supplierQuality
            : null;
    }

    private static string NormalizeQuality(string quality) => quality.ToLowerInvariant() switch
    {
        "128k" or "192k" or "standard" => "standard",
        "320k" or "high" => "high",
        "flac" => "flac",
        "flac24bit" or "lossless" => "lossless",
        _ => quality.ToLowerInvariant()
    };

    private static string NormalizePlatform(string platform) => platform.ToLowerInvariant() switch
    {
        "wy" => "netease",
        "mg" => "migu",
        _ => platform.ToLowerInvariant()
    };

    private static string ReversePlatform(string platform) => platform.ToLowerInvariant() switch
    {
        "netease" => "wy",
        "migu" => "mg",
        _ => platform.ToLowerInvariant()
    };

    private static string ReplaceTokens(
        string template,
        IReadOnlyDictionary<string, string> values,
        bool encode)
        => Regex.Replace(template, @"\{([^{}]+)\}", match =>
        {
            if (!values.TryGetValue(match.Groups[1].Value, out var value)) return match.Value;
            return encode ? Uri.EscapeDataString(value) : value;
        });

    private static JsonElement[]? SelectList(JsonElement root, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var segments = ParsePath(path);
        if (segments is null) return null;
        var values = Evaluate(root, segments);
        if (values is null) return null;
        if (values.Count == 1 && values[0].ValueKind is JsonValueKind.Array)
            return values[0].EnumerateArray().ToArray();
        return values.ToArray();
    }

    private static string? SelectString(JsonElement root, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var segments = ParsePath(path);
        if (segments is null) return null;
        var values = Evaluate(root, segments);
        var value = values?.FirstOrDefault(item => item.ValueKind is JsonValueKind.String or JsonValueKind.Number);
        return value?.ValueKind switch
        {
            JsonValueKind.String => value.Value.GetString(),
            JsonValueKind.Number => value.Value.GetRawText(),
            _ => null
        };
    }

    private static List<JsonPathSegment>? ParsePath(string path)
    {
        if (!path.StartsWith("$", StringComparison.Ordinal)) return null;
        var segments = new List<JsonPathSegment>();
        var index = 1;
        while (index < path.Length)
        {
            if (path[index] == '.')
            {
                index++;
                var start = index;
                while (index < path.Length && path[index] is not '.' and not '[') index++;
                if (start == index) return null;
                segments.Add(new JsonPathSegment(PathKind.Property, path[start..index]));
                continue;
            }
            if (path[index] != '[') return null;
            index++;
            var close = path.IndexOf(']', index);
            if (close < 0) return null;
            var token = path[index..close];
            if (token == "*") segments.Add(new JsonPathSegment(PathKind.Wildcard, ""));
            else if (int.TryParse(token, out var arrayIndex)) segments.Add(new JsonPathSegment(PathKind.Index, "", arrayIndex));
            else return null;
            index = close + 1;
        }
        return segments;
    }

    private static List<JsonElement>? Evaluate(JsonElement root, List<JsonPathSegment> segments)
    {
        var current = new List<JsonElement> { root };
        foreach (var segment in segments)
        {
            var next = new List<JsonElement>();
            foreach (var element in current)
            {
                switch (segment.Kind)
                {
                    case PathKind.Property when element.ValueKind is JsonValueKind.Object
                        && element.TryGetProperty(segment.Name, out var property):
                        next.Add(property);
                        break;
                    case PathKind.Index when element.ValueKind is JsonValueKind.Array
                        && segment.Index >= 0
                        && segment.Index < element.GetArrayLength():
                        next.Add(element[segment.Index]);
                        break;
                    case PathKind.Wildcard when element.ValueKind is JsonValueKind.Array:
                        next.AddRange(element.EnumerateArray());
                        break;
                }
            }
            current = next;
            if (current.Count == 0) return current;
        }
        return current;
    }

    private static string FormatDuration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !double.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var seconds)
            || seconds < 0)
            return "";
        var time = TimeSpan.FromSeconds(Math.Round(seconds));
        return $"{(int)time.TotalMinutes}:{time.Seconds:00}";
    }

    private static bool IsHttpUrl(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Port is 80 or 443);

    private enum PathKind
    {
        Property,
        Index,
        Wildcard
    }

    private readonly record struct JsonPathSegment(PathKind Kind, string Name, int Index = 0);
}







