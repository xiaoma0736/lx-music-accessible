using System.Collections.Concurrent;
using System.IO;
using LXMusicAccessible.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class BackgroundWorkHostTests
{
    [Fact]
    public async Task Latest_generation_is_the_only_generation_allowed_to_publish()
    {
        await using var host = new BackgroundWorkHost(new RecordingLogger<BackgroundWorkHost>());
        var firstStarted = NewSignal();
        var releaseFirst = NewSignal();
        var published = new List<string>();

        host.StartLatest("player", async context =>
        {
            firstStarted.SetResult();
            await releaseFirst.Task;
            context.TryCommit(() => published.Add("old"));
        });
        await firstStarted.Task;

        host.StartLatest("player", context =>
        {
            context.TryCommit(() => published.Add("new"));
            return Task.CompletedTask;
        });
        releaseFirst.SetResult();

        await host.WaitAsync("player");

        Assert.Equal(new[] { "new" }, published);
        Assert.Equal(0, host.ActiveCount);
    }

    [Fact]
    public async Task Stop_cancels_all_work_and_releases_each_resource_and_file_once()
    {
        await using var host = new BackgroundWorkHost(new RecordingLogger<BackgroundWorkHost>());
        var root = Directory.CreateTempSubdirectory("LXMusicAccessible.T20-").FullName;
        var probes = Enumerable.Range(0, 4).Select(_ => new ReleaseProbe()).ToArray();
        var started = Enumerable.Range(0, 4).Select(_ => NewSignal()).ToArray();
        var slots = new[] { "precache", "download", "script-execution", "script-source" };

        try
        {
            for (var index = 0; index < slots.Length; index++)
            {
                var capture = index;
                host.StartLatest(slots[index], async context =>
                {
                    await using var probe = probes[capture];
                    var path = Path.Combine(root, $"{slots[capture]}.tmp");
                    await File.WriteAllTextAsync(path, "owned", context.CancellationToken);
                    started[capture].SetResult();
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
                    }
                    finally
                    {
                        File.Delete(path);
                    }
                });
            }

            await Task.WhenAll(started.Select(signal => signal.Task));
            await Task.WhenAll(host.StopAsync(), host.StopAsync());

            Assert.Equal(0, host.ActiveCount);
            Assert.All(probes, probe => Assert.Equal(1, probe.ReleaseCount));
            Assert.Empty(Directory.EnumerateFiles(root));

            foreach (var slot in slots)
            {
                host.Cancel(slot);
                host.Cancel(slot);
                await host.WaitAsync(slot);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Background_exception_is_observed_without_logging_exception_message()
    {
        var logger = new RecordingLogger<BackgroundWorkHost>();
        await using var host = new BackgroundWorkHost(logger);
        const string secret = "SECRET-cookie-token";

        host.StartLatest("script-execution", _ =>
            throw new InvalidOperationException(secret));
        await host.WaitAsync("script-execution");

        var output = string.Join(Environment.NewLine, logger.Messages);
        Assert.Contains("InvalidOperationException", output, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, output, StringComparison.Ordinal);
        Assert.Equal(0, host.ActiveCount);
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class ReleaseProbe : IAsyncDisposable
    {
        public int ReleaseCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            ReleaseCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Enqueue(formatter(state, exception));
    }
}
