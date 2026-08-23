using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using Microsoft.Extensions.Logging;

namespace LXMusicAccessible.App;

public interface IApplicationShutdown
{
    Task<ShutdownResult> RequestAsync();
}

public enum ShutdownStatus
{
    Completed,
    Failed,
    TimedOut
}

public readonly record struct ShutdownResult(ShutdownStatus Status, string Phase)
{
    public bool Succeeded => Status == ShutdownStatus.Completed;
}

internal sealed record ApplicationShutdownSnapshot(
    UserSettings Settings,
    IReadOnlyList<MusicSource> Sources);

internal interface IApplicationShutdownParticipant
{
    ApplicationShutdownSnapshot CaptureAndBlock();
    Task PersistAsync(ApplicationShutdownSnapshot snapshot, CancellationToken cancellationToken);
}

internal interface IShutdownFeedback
{
    void Report(ShutdownResult result);
}

internal sealed class ApplicationShutdownRequest : IApplicationShutdown
{
    private Func<Task<ShutdownResult>>? _request;

    internal void Bind(Func<Task<ShutdownResult>> request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Interlocked.CompareExchange(ref _request, request, null) is not null)
            throw new InvalidOperationException("The shutdown request is already bound.");
    }

    public Task<ShutdownResult> RequestAsync() =>
        Volatile.Read(ref _request)?.Invoke()
        ?? Task.FromException<ShutdownResult>(
            new InvalidOperationException("Application shutdown is not ready."));
}

/// <summary>
/// Coordinates the complete shutdown workflow behind one idempotent Task.
/// Callers do not need to know its ordering, timeout, or error policy.
/// </summary>
internal sealed class ApplicationShutdownCoordinator
{
    private readonly object _gate = new();
    private readonly Func<IApplicationShutdownParticipant> _participant;
    private readonly Func<CancellationToken, Task> _quiesceBackground;
    private readonly Func<CancellationToken, Task> _releaseHost;
    private readonly IShutdownFeedback _feedback;
    private readonly ILogger<ApplicationShutdownCoordinator> _logger;
    private readonly TimeSpan _timeout;
    private Task<ShutdownResult>? _shutdown;

    internal ApplicationShutdownCoordinator(
        Func<IApplicationShutdownParticipant> participant,
        Func<CancellationToken, Task> quiesceBackground,
        Func<CancellationToken, Task> releaseHost,
        IShutdownFeedback feedback,
        ILogger<ApplicationShutdownCoordinator> logger,
        TimeSpan timeout)
    {
        _participant = participant;
        _quiesceBackground = quiesceBackground;
        _releaseHost = releaseHost;
        _feedback = feedback;
        _logger = logger;
        _timeout = timeout > TimeSpan.Zero
            ? timeout
            : throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    internal Task<ShutdownResult> RequestAsync()
    {
        TaskCompletionSource<ShutdownResult>? start = null;
        lock (_gate)
        {
            if (_shutdown is null)
            {
                start = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _shutdown = start.Task;
            }
        }

        if (start is not null)
            _ = CompleteAsync(start);
        return _shutdown;
    }

    private async Task CompleteAsync(TaskCompletionSource<ShutdownResult> completion)
    {
        try { completion.SetResult(await RunAsync()); }
        catch (Exception exception) { completion.SetException(exception); }
    }

    private async Task<ShutdownResult> RunAsync()
    {
        using var timeout = new CancellationTokenSource(_timeout);
        var result = new ShutdownResult(ShutdownStatus.Completed, "completed");
        ApplicationShutdownSnapshot? snapshot = null;
        IApplicationShutdownParticipant? participant = null;

        try
        {
            participant = _participant();
            snapshot = participant.CaptureAndBlock();
        }
        catch (Exception exception)
        {
            result = Failure("capture", exception);
        }

        if (snapshot is not null)
        {
            try
            {
                await _quiesceBackground(timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                result = TimedOut("background");
            }
            catch (Exception exception)
            {
                result = Failure("background", exception);
            }

            if (result.Succeeded)
            {
                try
                {
                    await participant!.PersistAsync(snapshot, timeout.Token);
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                    result = TimedOut("persistence");
                }
                catch (Exception exception)
                {
                    result = Failure("persistence", exception);
                }
            }
        }

        if (!result.Succeeded)
            Report(result);

        try
        {
            await _releaseHost(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            if (result.Succeeded)
                result = TimedOut("host-release");
        }
        catch (Exception exception)
        {
            if (result.Succeeded)
                result = Failure("host-release", exception);
            else
                LogFailure("host-release", exception);
        }

        if (!result.Succeeded && result.Phase == "host-release")
            Report(result);

        return result;
    }

    private ShutdownResult Failure(string phase, Exception exception)
    {
        LogFailure(phase, exception);
        return new ShutdownResult(ShutdownStatus.Failed, phase);
    }

    private ShutdownResult TimedOut(string phase)
    {
        _logger.LogError("Application shutdown timed out during {Phase}", phase);
        return new ShutdownResult(ShutdownStatus.TimedOut, phase);
    }

    private void LogFailure(string phase, Exception exception) =>
        _logger.LogError(
            "Application shutdown failed during {Phase}: {ErrorType}",
            phase,
            exception.GetType().Name);

    private void Report(ShutdownResult result)
    {
        try { _feedback.Report(result); }
        catch (Exception exception) { LogFailure("feedback", exception); }
    }
}

internal sealed class WpfShutdownFeedback(IAccessibleAnnouncer announcer) : IShutdownFeedback
{
    public void Report(ShutdownResult result)
    {
        var message = result.Status == ShutdownStatus.TimedOut
            ? "退出等待超时，应用将继续安全关闭；最近的部分更改可能未保存。"
            : "退出保存失败，应用将继续安全关闭；最近的部分更改可能未保存。";
        announcer.Announce(new AccessibleAnnouncement(
            message,
            AnnouncementKind.Error,
            "application-shutdown",
            AnnouncementSpeech.FollowPreference));
        System.Windows.MessageBox.Show(
            message,
            "落雪音乐无障碍版",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Warning);
    }
}

internal sealed class MainWindowShutdownParticipant(MainWindow window)
    : IApplicationShutdownParticipant
{
    public ApplicationShutdownSnapshot CaptureAndBlock() =>
        window.CaptureAndBlockShutdown();

    public Task PersistAsync(
        ApplicationShutdownSnapshot snapshot,
        CancellationToken cancellationToken) =>
        window.PersistShutdownAsync(snapshot, cancellationToken);
}
