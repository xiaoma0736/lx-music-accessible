using System.Text;

namespace LXMusicAccessible.Core.MusicApi;

public enum LoginDiagnosticOutcome
{
    Success,
    Failure,
    Canceled,
    Exception
}

public enum LoginDiagnosticError
{
    None,
    LoginRejected,
    LogoutFailed,
    BrowserUnavailable,
    CookieCollectionFailed,
    Unexpected
}

/// <summary>
/// Builds login diagnostics from an allowlist. Credential values and arbitrary
/// exception text are never accepted as output fields.
/// </summary>
public static class LoginDiagnostics
{
    public const string ReportFileName = "login-diagnostic.txt";

    public static string CreateCorrelationId() => Guid.NewGuid().ToString("N");

    public static string CreateSafeSummary(
        string platform,
        string credentials,
        LoginDiagnosticOutcome outcome,
        LoginDiagnosticError error,
        string correlationId)
    {
        var entries = ParseCredentials(credentials);
        return $"platform={NormalizePlatform(platform)}; domain={GetDomain(platform)}; " +
               $"outcome={outcome}; errorCode={error}; correlationId={NormalizeCorrelationId(correlationId)}; " +
               $"credentials=[{string.Join(", ", entries.Select(FormatEntry))}]";
    }

    public static string CreateReport(
        string platform,
        string credentials,
        LoginDiagnosticOutcome outcome,
        LoginDiagnosticError error,
        string correlationId)
    {
        var entries = ParseCredentials(credentials);
        var sb = new StringBuilder();
        sb.AppendLine("=== 落雪音乐登录诊断报告 ===");
        sb.AppendLine($"平台：{NormalizePlatform(platform)}");
        sb.AppendLine($"域：{GetDomain(platform)}");
        sb.AppendLine($"结果：{outcome}");
        sb.AppendLine($"错误码：{error}");
        sb.AppendLine($"关联标识：{NormalizeCorrelationId(correlationId)}");
        sb.AppendLine("凭据元数据：");
        foreach (var entry in entries)
            sb.AppendLine($"  {FormatEntry(entry)}");

        var missing = GetMissingRequiredCookies(platform, entries);
        sb.AppendLine($"缺少的关键 Cookie：{(missing.Length == 0 ? "无" : string.Join("、", missing))}");
        sb.AppendLine("安全说明：本报告不应包含 Cookie、令牌、授权头或其他凭据值。");
        return sb.ToString();
    }

    public static string CreateUserMessage(
        LoginDiagnosticOutcome outcome,
        LoginDiagnosticError error,
        string correlationId)
    {
        var message = outcome switch
        {
            LoginDiagnosticOutcome.Success => "登录成功。",
            LoginDiagnosticOutcome.Canceled => "已取消登录。",
            _ when error == LoginDiagnosticError.BrowserUnavailable => "网页登录组件初始化失败，请改用粘贴 Cookie 登录。",
            _ when error == LoginDiagnosticError.CookieCollectionFailed => "尚未获取到登录 Cookie，请确认已完成网页登录后再试。",
            _ when error == LoginDiagnosticError.LoginRejected => "Cookie 校验失败，Cookie 不完整或已失效，请重新登录后再试。",
            _ when error == LoginDiagnosticError.LogoutFailed => "退出登录失败，请重试。",
            _ => "登录失败，请重试。"
        };
        return $"{message} 错误码：{error}；关联标识：{NormalizeCorrelationId(correlationId)}。";
    }

    private static string[] GetMissingRequiredCookies(string platform, IReadOnlyList<CredentialMetadata> entries)
    {
        var values = entries.ToDictionary(e => e.Name, e => e.Present, StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        bool Has(string name) => values.TryGetValue(name, out var present) && present;
        void Require(string name)
        {
            if (!Has(name)) missing.Add(name);
        }

        switch (platform?.ToLowerInvariant())
        {
            case "netease":
                Require("MUSIC_U");
                break;
            case "qq":
                Require("uin");
                if (!Has("qm_keyst") && !Has("qqmusic_key")) missing.Add("qm_keyst/qqmusic_key");
                break;
            case "kg":
                if (!Has("KugooID") && !Has("userid")) missing.Add("KugooID/userid");
                if (!Has("t") && !Has("token")) missing.Add("t/token");
                break;
            case "kw":
                Require("userid");
                Require("websid");
                break;
        }
        return missing.ToArray();
    }

    private static List<CredentialMetadata> ParseCredentials(string credentials)
    {
        var entries = new List<CredentialMetadata>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in (credentials ?? string.Empty).Split(
                     new[] { ';', '\r', '\n' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0 && part.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))
                separator = part.IndexOf(':');
            if (separator <= 0) continue;

            var name = NormalizeCredentialName(part[..separator]);
            if (name.Length == 0 || !seen.Add(name)) continue;
            var valueLength = part[(separator + 1)..].Trim().Length;
            entries.Add(new CredentialMetadata(name, valueLength > 0, valueLength));
        }
        return entries;
    }

    private static string FormatEntry(CredentialMetadata entry) =>
        $"name={entry.Name}; present={entry.Present.ToString().ToLowerInvariant()}; " +
        $"length={entry.Length}; expired=unknown";

    private static string NormalizeCredentialName(string name)
    {
        var safe = new string(name.Trim().Take(64)
            .Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.').ToArray());
        return safe;
    }

    private static string NormalizeCorrelationId(string correlationId) =>
        Guid.TryParseExact(correlationId, "N", out var id) ? id.ToString("N") : CreateCorrelationId();

    private static string NormalizePlatform(string platform) => platform?.ToLowerInvariant() switch
    {
        "netease" => "netease",
        "qq" => "qq",
        "kg" => "kg",
        "kw" => "kw",
        _ => "unknown"
    };

    private static string GetDomain(string platform) => platform?.ToLowerInvariant() switch
    {
        "netease" => "music.163.com",
        "qq" => "qq.com",
        "kg" => "kugou.com",
        "kw" => "kuwo.cn",
        _ => "unknown"
    };

    private sealed record CredentialMetadata(string Name, bool Present, int Length);
}
