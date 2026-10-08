using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LXMusicAccessible.Services;
using Microsoft.Extensions.Logging;

namespace LXMusicAccessible.App;

/// <summary>Outcome of one application shutdown request.</summary>
public enum ShutdownStatus
{
    Completed,
    Failed,
    TimedOut
}

/// <summary>Shutdown outcome plus the phase that produced it.</summary>
public readonly record struct ShutdownResult(ShutdownStatus Status, string Phase)
{
    public bool Succeeded => Status == ShutdownStatus.Completed;
}

/// <summary>The exit seam used by the desktop lifecycle workflow.</summary>
public interface IApplicationShutdown
{
    Task<ShutdownResult> RequestAsync();
}

/// <summary>
/// Connects the exit path to the shutdown coordinator. The composition root binds the coordinator
/// before the main window is shown; an unbound request fails loudly instead of silently skipping
/// the save.
/// </summary>
public sealed class ApplicationShutdownRequest : IApplicationShutdown
{
    private Func<Task<ShutdownResult>>? _request;

    /// <summary>True once the composition root connected the shutdown coordinator.</summary>
    internal bool IsBound => Volatile.Read(ref _request) is not null;

    /// <summary>Binds the single production shutdown pipeline. Binding twice is a composition defect.</summary>
    internal void Bind(Func<Task<ShutdownResult>> request)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        if (Interlocked.CompareExchange(ref _request, request, null) is not null)
            throw new InvalidOperationException("Application shutdown is already bound.");
    }

    public Task<ShutdownResult> RequestAsync()
    {
        var request = Volatile.Read(ref _request);
        return request is not null
            ? request()
            : Task.FromException<ShutdownResult>(
                new InvalidOperationException("Application shutdown is not ready."));
    }
}

/// <summary>State captured on the UI thread before background work stops.</summary>
public sealed record ApplicationShutdownSnapshot(SettingsEditState Settings);

/// <summary>A module that can persist its own state during shutdown.</summary>
public interface IApplicationShutdownParticipant
{
    /// <summary>Captures the mutable state. Runs on the UI thread before any background work stops.</summary>
    ApplicationShutdownSnapshot CaptureAndBlock();

    /// <summary>Persists the captured state. A failure must be an exception so it can be announced.</summary>
    Task PersistAsync(ApplicationShutdownSnapshot snapshot, CancellationToken cancellationToken);
}

/// <summary>Announces a failed or timed-out shutdown to the user.</summary>
internal interface IShutdownFeedback
{
    void Report(ShutdownResult result);
}

/// <summary>
/// Runs the shutdown pipeline exactly once: capture and block, quiesce background work, persist,
/// release the host, then announce a failure. Concurrent callers share one request and no exception
/// escapes, so a failed save still closes the application with the previous data intact.
/// </summary>
internal sealed class ApplicationShutdownCoordinator
{
    internal const string PersistencePhase = "persistence";
    internal const string ReleasePhase = "release";

    private readonly Func<IApplicationShutdownParticipant> _participant;
    private readonly Func<CancellationToken, Task> _quiesceBackground;
    private readonly Func<CancellationToken, Task> _releaseHost;
    private readonly IShutdownFeedback _feedback;
    private readonly ILogger<ApplicationShutdownCoordinator> _logger;
    private readonly TimeSpan _timeout;
    private readonly object _gate = new();
    private Task<ShutdownResult>? _request;

    internal ApplicationShutdownCoordinator(
        Func<IApplicationShutdownParticipant> participant,
        Func<CancellationToken, Task> quiesceBackground,
        Func<CancellationToken, Task> releaseHost,
        IShutdownFeedback feedback,
        ILogger<ApplicationShutdownCoordinator> logger,
        TimeSpan timeout)
    {
        _participant = participant ?? throw new ArgumentNullException(nameof(participant));
        _quiesceBackground = quiesceBackground ?? throw new ArgumentNullException(nameof(quiesceBackground));
        _releaseHost = releaseHost ?? throw new ArgumentNullException(nameof(releaseHost));
        _feedback = feedback ?? throw new ArgumentNullException(nameof(feedback));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeout = timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(15) : timeout;
    }

    internal Task<ShutdownResult> RequestAsync()
    {
        lock (_gate) return _request ??= RunAsync();
    }

    private async Task<ShutdownResult> RunAsync()
    {
        var persistence = await PersistAsync().ConfigureAwait(false);
        var release = await ReleaseAsync().ConfigureAwait(false);
        var result = persistence.Succeeded ? release : persistence;
        if (!result.Succeeded) Report(result);
        return result;
    }

    private async Task<ShutdownResult> PersistAsync()
    {
        using var timeout = new CancellationTokenSource(_timeout);
        try
        {
            var participant = _participant();
            var snapshot = participant.CaptureAndBlock();
            await _quiesceBackground(timeout.Token).ConfigureAwait(false);
            await participant.PersistAsync(snapshot, timeout.Token).ConfigureAwait(false);
            return new ShutdownResult(ShutdownStatus.Completed, PersistencePhase);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            _logger.LogError("Application shutdown timed out while {Phase}.", PersistencePhase);
            return new ShutdownResult(ShutdownStatus.TimedOut, PersistencePhase);
        }
        catch (Exception exception)
        {
            _logger.LogError(
                "Application shutdown failed while {Phase}: {ErrorType}",
                PersistencePhase,
                exception.GetType().Name);
            return new ShutdownResult(ShutdownStatus.Failed, PersistencePhase);
        }
    }

    private async Task<ShutdownResult> ReleaseAsync()
    {
        // Persistence is protected by its own budget. Release gets the same hard bound because a
        // single hung native/synchronous Dispose must not prevent the shell or process from closing.
        using var timeout = new CancellationTokenSource(_timeout);
        try
        {
            // Host disposal is synchronous and may block its calling thread. Start that work off the
            // shutdown continuation first, or a native hang would prevent the timeout waiter from existing.
            var releaseTask = Task.Run(() => _releaseHost(timeout.Token), timeout.Token);
            ObserveAbandonedRelease(releaseTask);
            await WaitAsync(releaseTask, timeout.Token).ConfigureAwait(false);
            return new ShutdownResult(ShutdownStatus.Completed, ReleasePhase);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            _logger.LogError("Application shutdown timed out while {Phase}.", ReleasePhase);
            return new ShutdownResult(ShutdownStatus.TimedOut, ReleasePhase);
        }
        catch (Exception exception)
        {
            _logger.LogError(
                "Application shutdown failed while {Phase}: {ErrorType}",
                ReleasePhase,
                exception.GetType().Name);
            return new ShutdownResult(ShutdownStatus.Failed, ReleasePhase);
        }
    }

    private void ObserveAbandonedRelease(Task releaseTask)
    {
        _ = releaseTask.ContinueWith(
            task => _logger.LogError(
                "Application shutdown failed while {Phase}: {ErrorType}",
                ReleasePhase,
                task.Exception!.GetBaseException().GetType().Name),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    private static async Task WaitAsync(Task task, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken)))
        {
            var finished = await Task.WhenAny(task, completion.Task).ConfigureAwait(false);
            await finished.ConfigureAwait(false);
        }
    }

    private void Report(ShutdownResult result)
    {
        try
        {
            _feedback.Report(result);
        }
        catch (Exception exception)
        {
            _logger.LogError("Application shutdown feedback failed: {ErrorType}", exception.GetType().Name);
        }
    }
}

/// <summary>Announces a failed shutdown; the previous data stays intact.</summary>
internal sealed class ShutdownFeedback(IAccessibleAnnouncer announcer) : IShutdownFeedback
{
    internal const string FailureMessage = "退出保存失败，应用将继续安全关闭；最近的部分更改可能未保存。";
    internal const string TimeoutMessage = "退出保存超时，应用将继续安全关闭；最近的部分更改可能未保存。";

    public void Report(ShutdownResult result)
    {
        var announcement = AnnouncementFor(result);
        announcer.Announce(announcement);
        // The blocking dialog is the channel for this outcome: it takes the reader with it, so a second
        // spoken copy of the same sentence from the announcer would only read the warning twice while the
        // application is already closing. The text still reaches the live region to be re-read.
        MessageBox.Show(announcement.Text, "落雪音乐无障碍版", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    internal static AccessibleAnnouncement AnnouncementFor(ShutdownResult result) => new(
        result.Status == ShutdownStatus.TimedOut ? TimeoutMessage : FailureMessage,
        AnnouncementKind.Error,
        "application-shutdown",
        AnnouncementSpeech.None);
}