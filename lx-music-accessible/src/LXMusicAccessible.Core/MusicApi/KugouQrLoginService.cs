using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace LXMusicAccessible.Core.MusicApi;

/// <summary>酷狗 APP 扫码登录：二维码信息</summary>
public class KugouQrKeyResult
{
    /// <summary>二维码 Key（用于轮询登录状态）</summary>
    public string QrKey { get; set; } = string.Empty;
    /// <summary>二维码图片（data:image/png;base64,...）</summary>
    public string QrImage { get; set; } = string.Empty;
}

/// <summary>酷狗 APP 扫码登录：状态轮询结果</summary>
public class KugouQrStatusResult
{
    /// <summary>0=过期/失败，1=等待扫码，2=已扫码待确认，4=扫码成功</summary>
    public int Status { get; set; }
    /// <summary>扫码成功后的客户端令牌</summary>
    public string Token { get; set; } = string.Empty;
    /// <summary>扫码成功后的用户ID</summary>
    public string UserId { get; set; } = string.Empty;
    /// <summary>错误描述（可选）</summary>
    public string Error { get; set; } = string.Empty;
}

/// <summary>
/// 酷狗 APP 扫码登录服务（官方二维码流程，web 签名）。
/// 流程：GetQrKeyAsync 获取二维码 → 用户用手机酷狗APP扫码 → 轮询 CheckStatusAsync 直到 status==4 拿到 token+userid。
/// 参考 MakcRe/KuGouMusicApi 的 login_qr_key / login_qr_check。
/// </summary>
public class KugouQrLoginService
{
    private const string WebSalt = "NVPh5oo715z5DIWAeQlhMDsWXXQV4hwt"; // 酷狗 Web 签名盐值
    private const int AppId = 1005;      // 标准版 appid（二维码落地页使用）
    private const int QrAppId = 1001;    // 二维码接口专用 appid
    private const int ClientVer = 20489;
    private const int SrcAppId = 2919;

    private readonly MusicHttpClient _http;
    private readonly ILogger<KugouQrLoginService> _logger;
    private readonly string _mid;
    private readonly string _dfid;

    private static readonly Dictionary<string, string> DefaultHeaders = new()
    {
        ["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
        ["Referer"] = "https://www.kugou.com/"
    };

    public KugouQrLoginService(MusicHttpClient http, ILogger<KugouQrLoginService> logger)
    {
        _http = http;
        _logger = logger;
        _mid = GenerateMid();
        _dfid = GenerateDfid();
    }

    /// <summary>获取登录二维码（含图片与 key）</summary>
    public async Task<KugouQrKeyResult?> GetQrKeyAsync()
    {
        try
        {
            var p = new Dictionary<string, object>
            {
                ["dfid"] = _dfid,
                ["mid"] = _mid,
                ["uuid"] = "-",
                ["appid"] = QrAppId,   // 二维码接口专用 appid=1001（覆盖默认 1005）
                ["clientver"] = ClientVer,
                ["clienttime"] = DateTimeOffset.Now.ToUnixTimeSeconds(),
                ["type"] = 1,
                ["plat"] = 4,
                ["qrcode_txt"] = $"https://h5.kugou.com/apps/loginQRCode/html/index.html?appid={AppId}&",
                ["srcappid"] = SrcAppId
            };
            p["signature"] = SignWeb(p);

            var resp = await _http.GetAsync("https://login-user.kugou.com/v2/qrcode", ToQuery(p), DefaultHeaders);
            using var doc = JsonDocument.Parse(resp);
            var root = doc.RootElement;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            {
                _logger.LogWarning("酷狗获取二维码失败：ErrorCode=InvalidResponse");
                return null;
            }
            var key = GetString(data, "qrcode");
            var img = GetString(data, "qrcode_img");
            if (string.IsNullOrEmpty(key)) return null;
            return new KugouQrKeyResult { QrKey = key, QrImage = img };
        }
        catch (Exception)
        {
            _logger.LogError("酷狗获取二维码异常：{Diagnostic}",
                LoginDiagnostics.CreateSafeSummary("kg", string.Empty, LoginDiagnosticOutcome.Exception,
                    LoginDiagnosticError.Unexpected, LoginDiagnostics.CreateCorrelationId()));
            return null;
        }
    }

    /// <summary>轮询扫码状态；status==4 时返回 token+userid</summary>
    public async Task<KugouQrStatusResult> CheckStatusAsync(string qrKey)
    {
        var result = new KugouQrStatusResult();
        try
        {
            var p = new Dictionary<string, object>
            {
                ["dfid"] = _dfid,
                ["mid"] = _mid,
                ["uuid"] = "-",
                ["appid"] = AppId,
                ["clientver"] = ClientVer,
                ["clienttime"] = DateTimeOffset.Now.ToUnixTimeSeconds(),
                ["plat"] = 4,
                ["srcappid"] = SrcAppId,
                ["qrcode"] = qrKey
            };
            p["signature"] = SignWeb(p);

            var resp = await _http.GetAsync("https://login-user.kugou.com/v2/get_userinfo_qrcode", ToQuery(p), DefaultHeaders);
            using var doc = JsonDocument.Parse(resp);
            var root = doc.RootElement;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return result;
            if (data.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.Number)
                result.Status = st.GetInt32();
            if (result.Status == 4)
            {
                result.Token = GetValue(data, "token");
                result.UserId = GetValue(data, "userid");
            }
            if (result.Status == 0)
                result.Error = "二维码已过期";
        }
        catch (Exception)
        {
            _logger.LogError("酷狗轮询扫码状态异常：{Diagnostic}",
                LoginDiagnostics.CreateSafeSummary("kg", string.Empty, LoginDiagnosticOutcome.Exception,
                    LoginDiagnosticError.Unexpected, LoginDiagnostics.CreateCorrelationId()));
            result.Error = "网络异常";
        }
        return result;
    }

    private static Dictionary<string, string> ToQuery(Dictionary<string, object> p) =>
        p.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? string.Empty);

    private static string SignWeb(Dictionary<string, object> p)
    {
        var ps = string.Concat(p.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key}={kv.Value}"));
        var input = WebSalt + ps + WebSalt;
        return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(input))).ToLower();
    }

    private static string GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    private static string GetValue(JsonElement el, string name)
    {
        if (el.TryGetProperty(name, out var v))
        {
            if (v.ValueKind == JsonValueKind.String) return v.GetString() ?? string.Empty;
            if (v.ValueKind == JsonValueKind.Number) return v.GetRawText();
        }
        return string.Empty;
    }

    private static string GenerateMid()
    {
        var guid = Guid.NewGuid().ToString();
        var md5Hex = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(guid))).ToLower();
        return BigInteger.Parse("0" + md5Hex, NumberStyles.HexNumber).ToString();
    }

    private static string GenerateDfid()
    {
        const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var rand = new Random();
        return new string(Enumerable.Repeat(chars, 24).Select(s => s[rand.Next(s.Length)]).ToArray());
    }
}
