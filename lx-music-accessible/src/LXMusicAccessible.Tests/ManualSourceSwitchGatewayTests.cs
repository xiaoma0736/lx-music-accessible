using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using LXMusicAccessible.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class ManualSourceSwitchGatewayTests
{
    [Fact]
    public async Task Builtin_discovery_includes_every_enabled_platform_for_cross_source_switching()
    {
        await using var host = await TestApplicationHost.StartAsync();
        var service = host.Services.GetRequiredService<MusicSourceService>();
        var target = new MusicInfo
        {
            Id = "netease-1",
            Source = "netease",
            Title = "????",
            Artist = "?????"
        };

        var interfaces = service.GetPlaybackInterfaces(target);

        Assert.Equal(
            ["netease", "qq", "kg", "kw", "migu"],
            interfaces.Where(item => !item.IsScript)
                .Select(item => item.Platform)
                .ToArray());
    }
}
