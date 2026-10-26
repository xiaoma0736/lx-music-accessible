using System;
using System.IO;
using LXMusicAccessible.App;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class ProductionCompositionTests
{
    [Fact]
    public void Production_builder_registers_shutdown_request_required_by_desktop_lifecycle()
    {
        var directory = Path.Combine(Path.GetTempPath(), "LXMusicAccessible.Composition-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var host = ApplicationHost.CreateBuilder(directory).Build();
            var shutdown = host.Services.GetRequiredService<ApplicationShutdownRequest>();
            Assert.NotNull(shutdown);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
