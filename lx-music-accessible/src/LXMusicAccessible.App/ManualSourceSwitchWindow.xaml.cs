using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Threading;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;

namespace LXMusicAccessible.App;

/// <summary>Accessible manual playback source switching workflow host.</summary>
public partial class ManualSourceSwitchWindow : Window
{
    private readonly MusicInfo _target;
    private readonly IReadOnlyList<MusicInfo> _queue;
    private readonly IManualSourceDiscoveryWorkflow _workflow;
    private readonly IAccessibleAnnouncer _announcer;
    private readonly Func<ManualSourcePlaybackRequest, Task<PlaybackResult>> _playback;
    private readonly CancellationTokenSource _queryCancellation = new();
    private readonly PlaybackQueueOrigin _origin;
    private bool _completed;
    private bool _playbackInFlight;
    private bool _isClosed;
    private int _playbackAttempt;
    private int _emptyQueryAttempt;

    public ManualSourceSwitchWindow(
        MusicInfo target,
        IReadOnlyList<MusicInfo> queue,
        IManualSourceDiscoveryWorkflow workflow,
        IAccessibleAnnouncer announcer,
        Func<ManualSourcePlaybackRequest, Task<PlaybackResult>> playback,
        PlaybackQueueOrigin origin = PlaybackQueueOrigin.Static)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        _announcer = announcer ?? throw new ArgumentNullException(nameof(announcer));
        _playback = playback ?? throw new ArgumentNullException(nameof(playback));
        _origin = origin;

        InitializeComponent();
        TargetSongText.Text = $"{_target.Title} - {_target.Artist}";
        Loaded += ManualSourceSwitchWindow_Loaded;
        Closed += (_, _) =>
        {
            _isClosed = true;
            _queryCancellation.Cancel();
            _queryCancellation.Dispose();
        };
    }

    private async void ManualSourceSwitchWindow_Loaded(object sender, RoutedEventArgs e)
    {
        SetStatus("\u6b63\u5728\u67e5\u8be2\u53ef\u64ad\u653e\u7684\u6362\u6e90\u9009\u9879", AnnouncementKind.Busy);
        Announce(StatusText.Text, AnnouncementKind.Busy, QueryActivityId);
        try
        {
            var result = await _workflow.DiscoverAsync(_target, _queryCancellation.Token);
            if (_completed || _queryCancellation.IsCancellationRequested || _isClosed) return;

            _completed = true;
            SourceOptionsList.ItemsSource = result.Options;
            var kind = result.Status switch
            {
                ManualSourceDiscoveryStatus.Success when result.Options.Count > 0 => AnnouncementKind.Success,
                ManualSourceDiscoveryStatus.Timeout => AnnouncementKind.Error,
                ManualSourceDiscoveryStatus.Failed => AnnouncementKind.Error,
                _ => AnnouncementKind.Error
            };
            SetStatus(result.Status switch
            {
                ManualSourceDiscoveryStatus.Success when result.Options.Count > 0 =>
                    $"\u627e\u5230 {result.Options.Count} \u4e2a\u53ef\u64ad\u653e\u7684\u6362\u6e90\u9009\u9879",
                ManualSourceDiscoveryStatus.Timeout => string.IsNullOrWhiteSpace(result.Message)
                    ? "\u6362\u6e90\u67e5\u8be2\u8d85\u65f6"
                    : result.Message,
                ManualSourceDiscoveryStatus.Failed => string.IsNullOrWhiteSpace(result.Message)
                    ? "\u6362\u6e90\u67e5\u8be2\u5931\u8d25"
                    : result.Message,
                _ => "\u672a\u627e\u5230\u53ef\u64ad\u653e\u7684\u6362\u6e90\u9009\u9879"
            }, kind);
            Announce(StatusText.Text, kind, QueryActivityId);
            _ = Dispatcher.BeginInvoke(SourceOptionsList.FocusItemOrList, DispatcherPriority.Input);
        }
        catch (ObjectDisposedException)
        {
            // Window closed during background query; cancellation is expected.
        }
        catch (OperationCanceledException)
        {
            // Query was cancelled by close, reopen, target switch, or app exit.
        }
        catch (Exception)
        {
            if (_completed || _queryCancellation.IsCancellationRequested || _isClosed) return;
            _completed = true;
            SourceOptionsList.ItemsSource = null;
            SetStatus("\u6362\u6e90\u67e5\u8be2\u5931\u8d25", AnnouncementKind.Error);
            Announce(StatusText.Text, AnnouncementKind.Error, QueryActivityId);
            _ = Dispatcher.BeginInvoke(SourceOptionsList.FocusItemOrList, DispatcherPriority.Input);
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        HandlePreviewKey(e);
    }

    private async void HandlePreviewKey(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Enter) return;

        if (SourceOptionsList.SelectedItem is not ManualSourceOption option)
        {
            if (SourceOptionsList.Items.Count == 0)
            {
                e.Handled = true;
                SetStatus("\u672a\u627e\u5230\u53ef\u64ad\u653e\u7684\u6362\u6e90\u9009\u9879", AnnouncementKind.Error);
                Announce(StatusText.Text, AnnouncementKind.Error, CreateEmptyQueryActivityId());
            }
            return;
        }
        if (_playbackInFlight) { e.Handled = true; return; }

        e.Handled = true;
        _playbackInFlight = true;
        var activityId = CreatePlaybackActivityId(option);
        SetStatus("\u6b63\u5728\u5c1d\u8bd5\u6362\u6e90\u64ad\u653e", AnnouncementKind.Busy);
        Announce(StatusText.Text, AnnouncementKind.Busy, activityId);
        PlaybackResult result;
        try
        {
            result = await _playback(
                new(_queue, _target, option, _origin));
        }
        catch (OperationCanceledException)
        {
            if (_isClosed) return;
            SetStatus("\u6362\u6e90\u64ad\u653e\u5df2\u53d6\u6d88", AnnouncementKind.Status);
            Announce(StatusText.Text, AnnouncementKind.Status, activityId);
            return;
        }
        catch (Exception)
        {
            result = new(
                PlaybackResultStatus.Failed,
                null,
                _origin,
                "\u6362\u6e90\u64ad\u653e\u5931\u8d25",
                PlaybackTrigger.UserInitiated);
        }
        finally
        {
            _playbackInFlight = false;
        }

        if (_isClosed) return;
        if (result.Status == PlaybackResultStatus.Playing)
        {
            Close();
            return;
        }

        SetStatus(GetPlaybackFailureText(result), AnnouncementKind.Error);
        Announce(StatusText.Text, AnnouncementKind.Error, activityId);
    }

    private string QueryActivityId =>
        $"manual-source-query:{_target.Source}:{_target.Id}";

    private string CreateEmptyQueryActivityId() =>
        $"manual-source-query:{_target.Source}:{_target.Id}:empty:{++_emptyQueryAttempt}";

    private string CreatePlaybackActivityId(ManualSourceOption option) =>
        $"manual-source-playback:{option.SourceId}:{option.Candidate.Id}:{++_playbackAttempt}";

    private void SetStatus(string text, AnnouncementKind kind)
    {
        StatusText.Text = text;
        AutomationProperties.SetLiveSetting(
            StatusText,
            kind == AnnouncementKind.Error
                ? AutomationLiveSetting.Assertive
                : AutomationLiveSetting.Polite);
    }

    private static string GetPlaybackFailureText(PlaybackResult result) =>
        string.IsNullOrWhiteSpace(result.Message)
            ? result.Status switch
            {
                PlaybackResultStatus.Superseded => "\u6362\u6e90\u64ad\u653e\u5df2\u8fc7\u671f",
                PlaybackResultStatus.NoChange => "\u6362\u6e90\u64ad\u653e\u672a\u751f\u6548",
                _ => "\u9009\u5b9a\u63a5\u53e3\u64ad\u653e\u5931\u8d25"
            }
            : result.Message;

    private void Announce(
        string text,
        AnnouncementKind kind,
        string? activityId = null) =>
        _announcer.Announce(new(text, kind, activityId, AnnouncementSpeech.FollowPreference));
}
