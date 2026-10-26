using System.Text.Json;

namespace LXMusicAccessible.Core.MusicApi;

public enum LxSourceType
{
    Script,
    JsonManifest
}

public sealed class JsonSourceDefinition
{
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public string Author { get; init; } = "";
    public string Homepage { get; init; } = "";
    public string Description { get; init; } = "";
    public IReadOnlyDictionary<string, JsonSourcePlatform> Platforms { get; init; } =
        new Dictionary<string, JsonSourcePlatform>(StringComparer.OrdinalIgnoreCase);
}

public sealed class JsonSourcePlatform
{
    public JsonSourceOperation? Search { get; init; }
    public JsonSourceOperation? Url { get; init; }
    public JsonSourceOperation? Lyric { get; init; }
    public JsonSourceOperation? Pic { get; init; }
}

public sealed class JsonSourceOperation
{
    public string Method { get; init; } = "GET";
    public string Url { get; init; } = "";
    public IReadOnlyDictionary<string, string> Headers { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, string> Params { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
    public JsonSourceBody? Body { get; init; }
    public IReadOnlyDictionary<string, string> Qualities { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, string> Result { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public sealed class JsonSourceBody
{
    public string Type { get; init; } = "json";
    public IReadOnlyDictionary<string, string> Values { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

public sealed class JsonSourceManifestException : Exception
{
    public string Code { get; }

    public JsonSourceManifestException(string code, Exception? innerException = null)
        : base($"JSON source manifest rejected: {code}", innerException)
    {
        Code = code;
    }
}

public static class JsonSourceDefinitionParser
{
    private static readonly HashSet<string> SupportedPlatforms = new(StringComparer.OrdinalIgnoreCase)
    { "qq", "wy", "kg", "kw", "mg" };

    public static JsonSourceDefinition Parse(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
            var root = document.RootElement;
            if (root.ValueKind is not JsonValueKind.Object)
                throw new JsonSourceManifestException("json_manifest_invalid");

            var name = RequiredString(root, "name");
            var version = RequiredString(root, "version");
            if (!root.TryGetProperty("platforms", out var platformsElement)
                || platformsElement.ValueKind is not JsonValueKind.Object
                || !platformsElement.EnumerateObject().Any())
                throw new JsonSourceManifestException("json_manifest_invalid");

            var platforms = new Dictionary<string, JsonSourcePlatform>(StringComparer.OrdinalIgnoreCase);
            foreach (var platformProperty in platformsElement.EnumerateObject())
            {
                if (!SupportedPlatforms.Contains(platformProperty.Name)
                    || platformProperty.Value.ValueKind is not JsonValueKind.Object)
                    throw new JsonSourceManifestException("json_manifest_invalid");

                var operation = ParseOperation(platformProperty.Value, "search")
                    ?? ParseOperation(platformProperty.Value, "url")
                    ?? ParseOperation(platformProperty.Value, "lyric")
                    ?? ParseOperation(platformProperty.Value, "pic");
                if (operation is null)
                    throw new JsonSourceManifestException("json_manifest_invalid");

                platforms[platformProperty.Name] = new JsonSourcePlatform
                {
                    Search = ParseOperation(platformProperty.Value, "search"),
                    Url = ParseOperation(platformProperty.Value, "url"),
                    Lyric = ParseOperation(platformProperty.Value, "lyric"),
                    Pic = ParseOperation(platformProperty.Value, "pic")
                };
            }

            return new JsonSourceDefinition
            {
                Name = name,
                Version = version,
                Author = OptionalString(root, "author"),
                Homepage = OptionalString(root, "homepage"),
                Description = OptionalString(root, "description"),
                Platforms = platforms
            };
        }
        catch (JsonSourceManifestException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            throw new JsonSourceManifestException("json_manifest_invalid", exception);
        }
    }

    private static JsonSourceOperation? ParseOperation(JsonElement platform, string name)
    {
        if (!platform.TryGetProperty(name, out var element)) return null;
        if (element.ValueKind is not JsonValueKind.Object) throw Invalid();

        if (!TryAbsoluteHttpUrl(RequiredString(element, "url"), out _))
            throw new JsonSourceManifestException("json_manifest_url_invalid");

        var method = OptionalString(element, "method");
        if (string.IsNullOrWhiteSpace(method)) method = "GET";
        if (!method.Equals("GET", StringComparison.OrdinalIgnoreCase)
            && !method.Equals("POST", StringComparison.OrdinalIgnoreCase))
            throw new JsonSourceManifestException("json_manifest_method_unsupported");

        JsonSourceBody? body = null;
        if (element.TryGetProperty("body", out var bodyElement))
        {
            if (bodyElement.ValueKind is not JsonValueKind.Object) throw Invalid();
            var bodyType = OptionalString(bodyElement, "type");
            if (string.IsNullOrWhiteSpace(bodyType)) bodyType = "json";
            if (bodyType is not ("json" or "form")) throw Invalid();
            body = new JsonSourceBody
            {
                Type = bodyType,
                Values = ReadStrings(bodyElement, "values")
            };
        }

        return new JsonSourceOperation
        {
            Method = method.ToUpperInvariant(),
            Url = RequiredString(element, "url"),
            Headers = ReadStrings(element, "headers"),
            Params = ReadStrings(element, "params"),
            Body = body,
            Qualities = ReadStrings(element, "qualities"),
            Result = ReadStrings(element, "result")
        };
    }

    private static IReadOnlyDictionary<string, string> ReadStrings(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var element))
            return new Dictionary<string, string>(StringComparer.Ordinal);
        if (element.ValueKind is not JsonValueKind.Object) throw Invalid();

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(property.Name)
                || property.Value.ValueKind is not JsonValueKind.String
                || property.Value.GetString() is not { } value)
                throw Invalid();
            values[property.Name] = value;
        }
        return values;
    }

    private static string RequiredString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var element)
            || element.ValueKind is not JsonValueKind.String
            || element.GetString() is not { } value
            || string.IsNullOrWhiteSpace(value))
            throw Invalid();
        return value;
    }

    private static string OptionalString(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var element)
            && element.ValueKind is JsonValueKind.String
            && element.GetString() is { } value
            ? value
            : "";

    private static bool TryAbsoluteHttpUrl(string value, out Uri uri)
        => Uri.TryCreate(value, UriKind.Absolute, out uri!)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && !string.IsNullOrWhiteSpace(uri.Host);

    private static JsonSourceManifestException Invalid() => new("json_manifest_invalid");
}
