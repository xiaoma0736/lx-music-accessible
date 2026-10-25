using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace LXMusicAccessible;

internal static class FrameworkCompatibility
{
    public static bool IsWindows => Environment.OSVersion.Platform == PlatformID.Win32NT;

    public static T Clamp<T>(T value, T min, T max) where T : IComparable<T>
    {
        if (value.CompareTo(min) < 0) return min;
        return value.CompareTo(max) > 0 ? max : value;
    }

    public static string GetRelativePath(string relativeTo, string path)
    {
        var baseUri = new Uri(Path.GetFullPath(relativeTo).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
        var fullUri = new Uri(Path.GetFullPath(path));
        if (baseUri.IsBaseOf(fullUri))
        {
            var relative = baseUri.MakeRelativeUri(fullUri).ToString();
            return Uri.UnescapeDataString(relative).Replace('/', Path.DirectorySeparatorChar);
        }

        return Path.GetFullPath(path);
    }

    public static void ThrowIfNull(object? value, string parameterName = "")
    {
        if (value is null) throw new ArgumentNullException(parameterName);
    }

    public static void ThrowIfNullOrWhiteSpace(string? value, string parameterName = "")
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("The value cannot be null or whitespace.", parameterName);
    }

    public static void Move(string source, string destination, bool overwrite)
    {
        if (overwrite && File.Exists(destination)) File.Delete(destination);
        File.Move(source, destination);
    }

    public static string ToHexString(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", string.Empty);

    public static byte[] HashData(byte[] bytes)
    {
        using var hash = SHA256.Create();
        return hash.ComputeHash(bytes);
    }

    public static byte[] HashData(Stream stream)
    {
        using var hash = SHA256.Create();
        return hash.ComputeHash(stream);
    }

    public static byte[] GetRandomBytes(int count)
    {
        var bytes = new byte[count];
        using var random = RandomNumberGenerator.Create();
        random.GetBytes(bytes);
        return bytes;
    }
    public static bool IsPathFullyQualified(string path) =>
        Path.IsPathRooted(path) && path.Length > 2 && path[1] == Path.VolumeSeparatorChar;

    public static byte[] Md5HashData(byte[] bytes)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        return md5.ComputeHash(bytes);
    }

    public static KeyValuePair<TKey, TValue> CreatePair<TKey, TValue>(TKey key, TValue value) => new(key, value);

    public static Random SharedRandom => new();

    public static string ReplaceLineEndings(string value, string replacement = "\r\n") =>
        value.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", replacement);

    public static long CurrentTickMs => (long)(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);

    public static string TrimEndingDirectorySeparator(string path) =>
        path.Length > 1 && (path.EndsWith("\\") || path.EndsWith("/")) ? path.TrimEnd('\\', '/') : path;

    public static bool IsAsciiLetterOrDigit(char value) =>
        (value >= 'a' && value <= 'z') || (value >= 'A' && value <= 'Z') || (value >= '0' && value <= '9');

}

internal static class TaskCompatibilityExtensions
{
    public static async Task WaitAsync(this Task task, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken)))
        {
            var finished = await Task.WhenAny(task, completion.Task).ConfigureAwait(false);
            await finished.ConfigureAwait(false);
        }
    }

    public static async Task<T> WaitAsync<T>(this Task<T> task, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken)))
        {
            var finished = await Task.WhenAny(task, completion.Task).ConfigureAwait(false);
            if (!ReferenceEquals(finished, task)) await finished.ConfigureAwait(false);
            return await task.ConfigureAwait(false);
        }
    }

    public static async Task WaitAsync(this Task task, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        await task.WaitAsync(cancellation.Token).ConfigureAwait(false);
    }

    public static async Task<T> WaitAsync<T>(this Task<T> task, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        return await task.WaitAsync(cancellation.Token).ConfigureAwait(false);
    }
}


internal static class StringCompatibilityExtensions
{
    public static bool Contains(this string value, string text, StringComparison comparison) =>
        value.IndexOf(text, comparison) >= 0;

    public static TValue? GetValueOrDefault<TKey, TValue>(
        this IReadOnlyDictionary<TKey, TValue> values,
        TKey key,
        TValue? fallback = default)
        where TKey : notnull
        => values.TryGetValue(key, out var result) ? result : fallback;

    public static bool IsAsciiLetterOrDigit(this char value) =>
        (value >= 'a' && value <= 'z') || (value >= 'A' && value <= 'Z') || (value >= '0' && value <= '9');

    public static bool IsPathFullyQualified(string path) =>
        Path.IsPathRooted(path) && path.Length > 2 && path[1] == Path.VolumeSeparatorChar;

    public static byte[] Md5HashData(byte[] bytes)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        return md5.ComputeHash(bytes);
    }

    public static KeyValuePair<TKey, TValue> CreatePair<TKey, TValue>(TKey key, TValue value) => new(key, value);

    public static Random SharedRandom => new();

    public static void ThrowIfNull(object? value, string parameterName = "")
    {
        if (value is null) throw new ArgumentNullException(parameterName);
    }
    public static string Replace(this string value, string oldValue, string newValue, StringComparison comparison)
        => value.IndexOf(oldValue, comparison) < 0
            ? value
            : string.Join(newValue, value.Split(new[] { oldValue }, StringSplitOptions.None));

    public static string Remove(this string value, int startIndex, int count, StringComparison comparison)
    {
        var index = value.IndexOf(value.Substring(startIndex, count), comparison);
        return index < 0 ? value : value.Remove(index, count);
    }
}

internal static class CompatibilityCollectionExtensions
{
    public static void Deconstruct<TKey, TValue>(this KeyValuePair<TKey, TValue> pair, out TKey key, out TValue value)
    {
        key = pair.Key;
        value = pair.Value;
    }

    public static IEnumerable<string> SplitTrimmed(this string value, params char[] separators)
        => value.Split(separators, StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Trim())
            .Where(item => item.Length > 0);

    public static string ReplaceLineEndings(this string value, string replacement = "\r\n") =>
        FrameworkCompatibility.ReplaceLineEndings(value, replacement);
}