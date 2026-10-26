using System.Diagnostics;
using LXMusicAccessible.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class Issue75ShutdownResourceTests
{
    [Fact]
    public async Task Background_host_disposal_completes_when_work_ignores_cancellation()
    {
        var logger = new RecordingLogger<BackgroundWorkHost>();
        using var host = new BackgroundWorkHost(logger, TimeSpan.FromMilliseconds(50));
        var started = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

        host.StartLatest("issue75", async _ =>
        {
            started.TrySetResult(null);
            await release.Task;
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var stopwatch = Stopwatch.StartNew();
        await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        Assert.Contains(
            "timed out",
            string.Join(Environment.NewLine, logger.Messages),
            StringComparison.OrdinalIgnoreCase);
        release.TrySetResult(null);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
