using System.Security.Cryptography;
using System.Text;

namespace LXMusicAccessible.Core.MusicApi;

/// <summary>
/// 网易云音乐 API 加密工具 — 移植自原版 lx-music-desktop weapi/eapi/linuxapi
/// 参考: https://github.com/Binaryify/NeteaseCloudMusicApi/blob/master/util/crypto.js
/// </summary>
public static class NeteaseCrypto
{
    private static readonly byte[] Iv = Encoding.UTF8.GetBytes("0102030405060708");
    private static readonly byte[] PresetKey = Encoding.UTF8.GetBytes("0CoJUm6Qyw8W8jud");
    private static readonly byte[] LinuxApiKey = Encoding.UTF8.GetBytes("rFgB&h#%2?^eDg:Q");
    private static readonly byte[] EapiKey = Encoding.UTF8.GetBytes("e82ckenh8dichen8");
    private const string Base62 = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    private const string PublicKeyPem = @"-----BEGIN PUBLIC KEY-----
MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQDgtQn2JZ34ZC28NWYpAUd98iZ37BUrX/aKzmFbt7clFSs6sXqHauqKWqdtLkF2KexO40H1YTX8z2lSgBBOAxLsvaklV8k4cBFK9snQXE9/DDaFt6Rr7iVZMldczhC0JNgTz+SHXT6CBHuX3e9SdB1Ua44oncaTWz7OBGLbCiK45wIDAQAB
-----END PUBLIC KEY-----";

    /// <summary>weapi 加密: 返回 { params, encSecKey }</summary>
    public static Dictionary<string, string> Weapi(object data)
    {
        var text = System.Text.Json.JsonSerializer.Serialize(data);
        var secretKey = GenerateRandomKey();

        var firstEncrypt = AesEncrypt(Encoding.UTF8.GetBytes(text), CipherMode.CBC, PresetKey, Iv);
        var firstBase64 = Convert.ToBase64String(firstEncrypt);

        var secondEncrypt = AesEncrypt(Encoding.UTF8.GetBytes(firstBase64), CipherMode.CBC, secretKey, Iv);
        var params_ = Convert.ToBase64String(secondEncrypt);

        var reversedKey = secretKey.Reverse().ToArray();
        var encSecKey = RsaEncryptRaw(reversedKey);

        return new Dictionary<string, string>
        {
            ["params"] = params_,
            ["encSecKey"] = encSecKey
        };
    }

    /// <summary>eapi 加密: 返回 { params }</summary>
    public static Dictionary<string, string> Eapi(string url, object data)
    {
        var text = System.Text.Json.JsonSerializer.Serialize(data);
        var message = $"nobody{url}use{text}md5forencrypt";
        var digest = Md5Hash(message);
        var combined = $"{url}-36cd479b6b5-{text}-36cd479b6b5-{digest}";

        var encrypted = AesEncrypt(Encoding.UTF8.GetBytes(combined), CipherMode.ECB, EapiKey, null!);
        var params_ = FrameworkCompatibility.ToHexString(encrypted).ToUpper();

        return new Dictionary<string, string>
        {
            ["params"] = params_
        };
    }

    /// <summary>linuxapi 加密: 返回 { eparams }</summary>
    public static Dictionary<string, string> Linuxapi(object data)
    {
        var text = System.Text.Json.JsonSerializer.Serialize(data);
        var encrypted = AesEncrypt(Encoding.UTF8.GetBytes(text), CipherMode.ECB, LinuxApiKey, null!);
        var eparams = FrameworkCompatibility.ToHexString(encrypted).ToUpper();

        return new Dictionary<string, string>
        {
            ["eparams"] = eparams
        };
    }

    // --- 内部方法 ---

    private static byte[] GenerateRandomKey()
    {
        var bytes = FrameworkCompatibility.GetRandomBytes(16);
        var result = new byte[16];
        for (var i = 0; i < 16; i++)
            result[i] = (byte)Base62[bytes[i] % 62];
        return result;
    }

    private static byte[] AesEncrypt(byte[] data, CipherMode mode, byte[] key, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.Mode = mode;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = key;
        if (mode == CipherMode.CBC)
            aes.IV = iv;

        using var encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(data, 0, data.Length);
    }

    /// <summary>Raw RSA encryption (RSA_NO_PADDING); input must be exactly 128 bytes.</summary>
    private static string RsaEncryptRaw(byte[] data)
    {
        var padded = new byte[128];
        Array.Copy(data, 0, padded, 128 - data.Length, data.Length);
        var pemBody = string.Concat(PublicKeyPem
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => !line.StartsWith("-----", StringComparison.Ordinal)));
        var keyBytes = Convert.FromBase64String(pemBody);
        using var rsa = RSA.Create();
        rsa.ImportParameters(new RSAParameters
        {
            Modulus = GetFirst128(keyBytes),
            Exponent = GetLast3(keyBytes)
        });
        var parameters = rsa.ExportParameters(false);
        var modulus = new System.Numerics.BigInteger(parameters.Modulus!.Reverse().ToArray());
        var exponent = new System.Numerics.BigInteger(parameters.Exponent!.Reverse().ToArray());
        var plain = new System.Numerics.BigInteger(padded.Reverse().ToArray());
        var cipher = System.Numerics.BigInteger.ModPow(plain, exponent, modulus);
        var cipherBytes = cipher.ToByteArray();
        if (cipherBytes.Length > 0 && cipherBytes[cipherBytes.Length - 1] == 0)
        {
            var trimmed = new byte[cipherBytes.Length - 1];
            Array.Copy(cipherBytes, trimmed, trimmed.Length);
            cipherBytes = trimmed;
        }
        var encrypted = cipherBytes.Reverse().ToArray();
        var result = new byte[128];
        var sourceOffset = Math.Max(0, encrypted.Length - 128);
        var count = Math.Min(128, encrypted.Length);
        Array.Copy(encrypted, sourceOffset, result, 128 - count, count);
        return FrameworkCompatibility.ToHexString(result).ToLower();
    }

    private static byte[] GetFirst128(byte[] value)
    {
        var result = new byte[128];
        Array.Copy(value, result, Math.Min(128, value.Length));
        return result;
    }

    private static byte[] GetLast3(byte[] value)
    {
        var result = new byte[3];
        Array.Copy(value, value.Length - 3, result, 0, 3);
        return result;
    }

    private static string Md5Hash(string input)
    {
        var hash = FrameworkCompatibility.Md5HashData(Encoding.UTF8.GetBytes(input));
        return FrameworkCompatibility.ToHexString(hash).ToLower();
    }
}
