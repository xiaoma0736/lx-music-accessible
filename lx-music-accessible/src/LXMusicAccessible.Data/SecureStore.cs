using System;
using System.Security.Cryptography;
using System.Text;

namespace LXMusicAccessible.Data;

/// <summary>
/// 本机凭据安全存储：使用 Windows DPAPI（当前用户作用域）加密/解密，
/// 密钥由 Windows 按当前 Windows 用户派生，仅本机本账户可解。
/// 登录 Cookie/令牌绝不以明文落盘，也绝不上传任何第三方。
/// </summary>
public static class SecureStore
{
    /// <summary>用 DPAPI 加密 UTF-8 明文，返回 Base64 字符串；输入为空时返回 null</summary>
    public static string? Protect(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return null;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(plainText);
            var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(encrypted);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>用 DPAPI 解密 Base64 密文，返回 UTF-8 明文；失败时返回 null</summary>
    public static string? Unprotect(string? base64)
    {
        if (string.IsNullOrEmpty(base64)) return null;
        try
        {
            var bytes = Convert.FromBase64String(base64);
            var decrypted = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch
        {
            return null;
        }
    }
}