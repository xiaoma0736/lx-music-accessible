using LXMusicAccessible.Core;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class DotNetFrameworkRequirementTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(40605)]
    [InlineData(528039)]
    public void Missing_or_older_release_fails_with_net48_download_path(int? release)
    {
        var result = DotNetFrameworkRequirement.Evaluate(() => release);

        Assert.False(result.IsInstalled);
        Assert.Equal(528040, result.RequiredRelease);
        Assert.Equal(release, result.Release);
        Assert.Contains("https://dotnet.microsoft.com/download/dotnet-framework/net48", result.DownloadUrl);
        Assert.Contains(release?.ToString() ?? "missing", result.Diagnostic, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(528040)]
    [InlineData(533325)]
    public void Release_528040_or_later_is_net48_or_compatible(int release)
    {
        var result = DotNetFrameworkRequirement.Evaluate(() => release);

        Assert.True(result.IsInstalled);
        Assert.Equal(release, result.Release);
        Assert.Equal(528040, result.RequiredRelease);
        Assert.Empty(result.DownloadUrl);
    }
}
