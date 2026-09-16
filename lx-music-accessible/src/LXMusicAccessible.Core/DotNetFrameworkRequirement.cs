using System;

namespace LXMusicAccessible.Core;

public sealed record DotNetFrameworkCheck(
    bool IsInstalled,
    int RequiredRelease,
    int? Release,
    string Diagnostic,
    string DownloadUrl);

/// <summary>
/// Evaluates the Windows registry release key used by .NET Framework 4.8 and later.
/// </summary>
public static class DotNetFrameworkRequirement
{
    public const int RequiredRelease = 528040;
    public const string DownloadUrl = "https://dotnet.microsoft.com/download/dotnet-framework/net48";

    public static DotNetFrameworkCheck Evaluate() => Evaluate(ReadRegistryRelease);

    public static DotNetFrameworkCheck Evaluate(Func<int?> releaseReader)
    {
        FrameworkCompatibility.ThrowIfNull(releaseReader);
        int? release;
        try
        {
            release = releaseReader();
        }
        catch (Exception ex)
        {
            return new DotNetFrameworkCheck(
                false,
                RequiredRelease,
                null,
                $".NET Framework 4.8 detection failed ({ex.GetType().Name}).",
                DownloadUrl);
        }

        return release >= RequiredRelease
            ? new DotNetFrameworkCheck(
                true,
                RequiredRelease,
                release,
                $".NET Framework 4.8 or later detected (registry Release: {release}).",
                string.Empty)
            : new DotNetFrameworkCheck(
                false,
                RequiredRelease,
                release,
                $".NET Framework 4.8 was not detected (registry Release: {release?.ToString() ?? "missing"}).",
                DownloadUrl);
    }

    private static int? ReadRegistryRelease()
    {
        using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(
            Microsoft.Win32.RegistryHive.LocalMachine,
            Microsoft.Win32.RegistryView.Registry32);
        using var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full");
        if (key is null) return null;
        var value = key.GetValue("Release", null);
        return value is null ? null : Convert.ToInt32(value);
    }
}
