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
        var (modulusBytes, exponentBytes) = ReadRsaPublicKey(Convert.FromBase64String(pemBody));
        var modulus = ToBigInteger(modulusBytes);
        var exponent = ToBigInteger(exponentBytes);
        var plain = ToBigInteger(padded);
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

    /// <summary>
    /// 从 SubjectPublicKeyInfo DER 中读出 RSA 模数和指数。
    /// 旧实现直接取 DER 头部的前 128 字节，取到的是结构头而非模数，
    /// 因此 encSecKey 无效，网易云对 weapi 请求返回 HTTP 200 空响应（歌单/排行榜全部加载失败）。
    /// </summary>
    private static (byte[] Modulus, byte[] Exponent) ReadRsaPublicKey(byte[] der)
    {
        var outerIndex = 0;
        var outer = ReadDerElement(der, ref outerIndex, 0x30);         // SubjectPublicKeyInfo
        var algorithmIndex = 0;
        ReadDerElement(outer, ref algorithmIndex, 0x30);               // AlgorithmIdentifier
        var subjectPublicKey = ReadDerElement(outer, ref algorithmIndex, 0x03); // BIT STRING
        var keyIndex = 1;                                              // 跳过 unused-bits 字节
        var rsaPublicKey = ReadDerElement(subjectPublicKey, ref keyIndex, 0x30); // RSAPublicKey
        var valueIndex = 0;
        var modulus = TrimLeadingZeroPadding(ReadDerElement(rsaPublicKey, ref valueIndex, 0x02));
        var exponent = TrimLeadingZeroPadding(ReadDerElement(rsaPublicKey, ref valueIndex, 0x02));
        if (modulus.Length != 128)
            throw new CryptographicException($"Unexpected Netease RSA modulus length: {modulus.Length}.");
        return (modulus, exponent);
    }

    private static byte[] ReadDerElement(byte[] data, ref int index, byte expectedTag)
    {
        if (index + 1 >= data.Length || data[index] != expectedTag)
            throw new CryptographicException("Malformed DER while reading the Netease RSA public key.");
        index++;
        var length = ReadDerLength(data, ref index);
        if (index + length > data.Length)
            throw new CryptographicException("Truncated DER while reading the Netease RSA public key.");
        var value = new byte[length];
        Array.Copy(data, index, value, 0, length);
        index += length;
        return value;
    }

    private static int ReadDerLength(byte[] data, ref int index)
    {
        if (index >= data.Length)
            throw new CryptographicException("Truncated DER while reading the Netease RSA public key.");
        var first = data[index++];
        if (first < 0x80) return first;
        var count = first & 0x7F;
        if (count == 0 || count > 4 || index + count > data.Length)
            throw new CryptographicException("Malformed DER length while reading the Netease RSA public key.");
        var length = 0;
        for (var i = 0; i < count; i++) length = (length << 8) | data[index++];
        return length;
    }

    private static byte[] TrimLeadingZeroPadding(byte[] value)
    {
        var offset = 0;
        while (offset < value.Length - 1 && value[offset] == 0) offset++;
        var trimmed = new byte[value.Length - offset];
        Array.Copy(value, offset, trimmed, 0, trimmed.Length);
        return trimmed;
    }

    /// <summary>Big-endian 字节转非负 BigInteger（net48 缺少 isUnsigned/isBigEndian 重载）。</summary>
    private static System.Numerics.BigInteger ToBigInteger(byte[] bigEndian)
    {
        var littleEndian = new byte[bigEndian.Length + 1];
        for (var i = 0; i < bigEndian.Length; i++) littleEndian[i] = bigEndian[bigEndian.Length - 1 - i];
        return new System.Numerics.BigInteger(littleEndian);
    }

    private static string Md5Hash(string input)
    {
        var hash = FrameworkCompatibility.Md5HashData(Encoding.UTF8.GetBytes(input));
        return FrameworkCompatibility.ToHexString(hash).ToLower();
    }
}
