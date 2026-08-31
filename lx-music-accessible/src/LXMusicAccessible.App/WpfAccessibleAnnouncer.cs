using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Threading;
using LXMusicAccessible.Services;
using Microsoft.Extensions.Logging;

namespace LXMusicAccessible.App;

/// <summary>Owns UIA notification delivery, progress coalescing, and optional speech.</summary>
internal sealed class WpfAccessibleAnnouncer(
    IEnumerable<ITtsService> speechAdapters,
    ILogger<WpfAccessibleAnnouncer> logger) : IAccessibleAnnouncer
{
    private static readonly TimeSpan ProgressDelay = TimeSpan.FromMilliseconds(350);
    private readonly ITtsService? _speech = speechAdapters.SingleOrDefault();
    private readonly Dictionary<string, AccessibleAnnouncement> _pendingProgress = new(StringComparer.Ordinal);
    private readonly HashSet<string> _terminalAnnouncements = new(StringComparer.Ordinal);
    private readonly Queue<string> _terminalOrder = new();
    private AnnouncementTextBlock? _liveRegion;
    private DispatcherTimer? _progressTimer;

    public bool SpeechEnabled { get; set; } = true;

    internal void Attach(AnnouncementTextBlock liveRegion)
    {
        ArgumentNullException.ThrowIfNull(liveRegion);
        if (_liveRegion is not null && !ReferenceEquals(_liveRegion, liveRegion))
            throw new InvalidOperationException("The accessible announcer is already attached.");

        _liveRegion = liveRegion;
        AutomationProperties.SetLiveSetting(liveRegion, AutomationLiveSetting.Polite);
        _progressTimer = new DispatcherTimer(
            ProgressDelay,
            DispatcherPriority.Background,
            FlushProgress,
            liveRegion.Dispatcher);
        _progressTimer.Stop();
    }

    public void Announce(AccessibleAnnouncement announcement)
    {
        if (string.IsNullOrWhiteSpace(announcement.Text)) return;
        var target = _liveRegion;
        if (target is null)
        {
            logger.LogWarning("播报目标尚未连接，已忽略状态：{Kind}", announcement.Kind);
            return;
        }

        _ = target.Dispatcher.BeginInvoke(
            () => DeliverOrQueue(announcement with { Text = announcement.Text.Trim() }),
            DispatcherPriority.Input);
    }

    private void DeliverOrQueue(AccessibleAnnouncement announcement)
    {
        if (announcement.Kind == AnnouncementKind.Progress)
        {
            var key = announcement.ActivityId ?? announcement.Text;
            _pendingProgress[key] = announcement;
            _progressTimer!.Stop();
            _progressTimer.Start();
            return;
        }

        if (announcement.Kind is AnnouncementKind.Success or AnnouncementKind.Error
            && announcement.ActivityId is { Length: > 0 } activityId)
        {
            _pendingProgress.Remove(activityId);
            var terminalKey = activityId;
            if (!_terminalAnnouncements.Add(terminalKey)) return;
            _terminalOrder.Enqueue(terminalKey);
            if (_terminalOrder.Count > 128)
                _terminalAnnouncements.Remove(_terminalOrder.Dequeue());
        }

        Deliver(announcement);
    }

    private void FlushProgress(object? sender, EventArgs e)
    {
        _progressTimer!.Stop();
        var pending = _pendingProgress.Values.ToArray();
        _pendingProgress.Clear();
        foreach (var announcement in pending) Deliver(announcement);
    }

    private void Deliver(AccessibleAnnouncement announcement)
    {
        var target = _liveRegion!;
        target.Text = announcement.Text;
        target.RaiseLiveRegionChanged();

        logger.LogInformation("可访问状态：{Kind} {Message}", announcement.Kind, announcement.Text);
        if (ShouldSpeak(announcement)) _ = SpeakAsync(announcement);
    }

    private bool ShouldSpeak(AccessibleAnnouncement announcement) =>
        _speech?.IsAvailable == true
        && (SpeechEnabled || announcement.Speech == AnnouncementSpeech.Always)
        && AccessibleSpeechPolicy.ShouldSpeak(announcement);

    private async Task SpeakAsync(AccessibleAnnouncement announcement)
    {
        try
        {
            if (announcement.Speech == AnnouncementSpeech.Queued)
                await _speech!.SpeakQueuedAsync(announcement.Text);
            else
                await _speech!.SpeakAsync(announcement.Text);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "可选 TTS 播报失败，UI Automation 状态已发送");
        }
    }
}

internal sealed class AnnouncementTextBlock : TextBlock
{
    internal void RaiseLiveRegionChanged()
    {
        var peer = UIElementAutomationPeer.FromElement(this)
            ?? UIElementAutomationPeer.CreatePeerForElement(this);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    protected override AutomationPeer OnCreateAutomationPeer() =>
        new TextBlockAutomationPeer(this);
}
