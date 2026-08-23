using Microsoft.Extensions.Logging;

namespace LXMusicAccessible.Services;

/// <summary>
/// Owns replaceable background work for the process. Callers only start,
/// cancel, wait, or stop work; task observation, generations, and CTS disposal
/// remain inside this module.
/// </summary>
public sealed class BackgroundWorkHost : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, WorkItem> _current = new(StringComparer.Ordinal);
    private readonly HashSet<WorkItem> _active = [];
    private readonly ILogger<BackgroundWorkHost> _logger;
    private Task? _stopTask;
    private bool _stopping;

    public BackgroundWorkHost(ILogger<BackgroundWorkHost> logger)
    {
        _logger = logger;
    }

    public int ActiveCount
    {
        get { lock (_gate) return _active.Count; }
    }

    public void StartLatest(string slot, Func<BackgroundWorkContext, Task> operation) =>
        StartCore(slot, operation);

    public Task RunLatestAsync(string slot, Func<BackgroundWorkContext, Task> operation) =>
        StartCore(slot, operation);

    public void Cancel(string slot)
    {
        WorkItem? item;
        lock (_gate) _current.TryGetValue(ValidateSlot(slot), out item);
        item?.Cancel();
    }

    public Task WaitAsync(string slot, CancellationToken cancellationToken = default)
    {
        Task completion;
        lock (_gate)
        {
            completion = _current.TryGetValue(ValidateSlot(slot), out var item)
                ? item.Completion.Task
                : Task.CompletedTask;
        }
        return completion.WaitAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task stopTask;
        lock (_gate)
        {
            if (_stopTask is null)
            {
                _stopping = true;
                var active = _active.ToArray();
                foreach (var item in active) item.Cancel();
                _stopTask = Task.WhenAll(active.Select(item => item.Completion.Task));
            }
            stopTask = _stopTask;
        }
        return stopTask.WaitAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        GC.SuppressFinalize(this);
    }

    internal bool TryCommit(WorkItem item, Action commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        lock (_gate)
        {
            if (_stopping
                || item.Token.IsCancellationRequested
                || !_current.TryGetValue(item.Slot, out var current)
                || !ReferenceEquals(current, item))
            {
                return false;
            }

            commit();
            return true;
        }
    }

    private Task StartCore(string slot, Func<BackgroundWorkContext, Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        slot = ValidateSlot(slot);

        WorkItem item;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            _current.TryGetValue(slot, out var previous);
            previous?.Cancel();
            item = new WorkItem(slot, previous?.Completion.Task ?? Task.CompletedTask);
            _current[slot] = item;
            _active.Add(item);
        }

        item.ObservedTask = ObserveAsync(item, operation);
        return item.Completion.Task;
    }

    private async Task ObserveAsync(
        WorkItem item,
        Func<BackgroundWorkContext, Task> operation)
    {
        try
        {
            await item.Predecessor;
            item.Token.ThrowIfCancellationRequested();
            await operation(new BackgroundWorkContext(this, item));
        }
        catch (OperationCanceledException) when (item.Token.IsCancellationRequested)
        {
            _logger.LogDebug("Background work {Slot} was cancelled", item.Slot);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Background work {Slot} failed: {ErrorType}",
                item.Slot,
                exception.GetType().Name);
        }
        finally
        {
            lock (_gate)
            {
                _active.Remove(item);
                if (_current.TryGetValue(item.Slot, out var current)
                    && ReferenceEquals(current, item))
                {
                    _current.Remove(item.Slot);
                }
            }
            item.DisposeOnce();
            item.Completion.TrySetResult();
        }
    }

    private static string ValidateSlot(string slot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slot);
        return slot;
    }

    internal sealed class WorkItem(string slot, Task predecessor)
    {
        private readonly CancellationTokenSource _cancellation = new();
        private int _disposed;

        internal string Slot { get; } = slot;
        internal CancellationToken Token => _cancellation.Token;
        internal Task Predecessor { get; } = predecessor;
        internal TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task? ObservedTask { get; set; }

        internal void Cancel()
        {
            lock (_cancellation)
            {
                if (_disposed == 0) _cancellation.Cancel();
            }
        }

        internal void DisposeOnce()
        {
            lock (_cancellation)
            {
                if (_disposed != 0) return;
                _disposed = 1;
                _cancellation.Dispose();
            }
        }
    }
}

public readonly struct BackgroundWorkContext
{
    private readonly BackgroundWorkHost _owner;
    private readonly BackgroundWorkHost.WorkItem _item;

    internal BackgroundWorkContext(
        BackgroundWorkHost owner,
        BackgroundWorkHost.WorkItem item)
    {
        _owner = owner;
        _item = item;
    }

    public CancellationToken CancellationToken => _item.Token;

    public bool TryCommit(Action commit) => _owner.TryCommit(_item, commit);
}
