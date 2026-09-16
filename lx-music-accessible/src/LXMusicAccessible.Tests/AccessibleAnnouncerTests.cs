using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using LXMusicAccessible.App;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LXMusicAccessible.Tests;

[Collection("WPF accessibility semantics")]
public sealed class AccessibleAnnouncerTests
{
    [Fact]
    public void Live_region_events_are_marshaled_coalesced_and_do_not_move_focus_when_tts_fails()
    {
        var uiReady = new ManualResetEventSlim();
        var clientReady = new ManualResetEventSlim();
        var received = new ConcurrentQueue<string>();
        var eventLock = new object();
        string? lastEventText = null;
        var rawEventCount = 0;
        var textChangeCount = 0;
        var speech = new ThrowingSpeechAdapter();
        WpfAccessibleAnnouncer? announcer = null;
        Dispatcher? uiDispatcher = null;
        Dispatcher? clientDispatcher = null;
        Window? window = null;
        Button? focusTarget = null;
        AnnouncementTextBlock? liveRegion = null;
        IntPtr windowHandle = IntPtr.Zero;
        Exception? uiFailure = null;
        Exception? clientFailure = null;
        var changedOnUiThread = true;

        var uiThread = new Thread(() =>
        {
            try
            {
                uiDispatcher = Dispatcher.CurrentDispatcher;
                announcer = new WpfAccessibleAnnouncer(
                    new[] { speech },
                    NullLogger<WpfAccessibleAnnouncer>.Instance);
                focusTarget = new Button { Content = "保持焦点", Name = "FocusTarget" };
                liveRegion = new AnnouncementTextBlock();
                AutomationProperties.SetAutomationId(liveRegion, "AnnouncementLiveRegion");
                window = new Window
                {
                    Width = 300,
                    Height = 160,
                    Content = new StackPanel { Children = { focusTarget, liveRegion } },
                    ShowInTaskbar = false
                };
                window.Show();
                window.Activate();
                focusTarget.Focus();
                Keyboard.Focus(focusTarget);
                announcer.Attach(liveRegion);
                DependencyPropertyDescriptor
                    .FromProperty(TextBlock.TextProperty, typeof(TextBlock))!
                    .AddValueChanged(liveRegion, (_, _) =>
                    {
                        textChangeCount++;
                        changedOnUiThread &= liveRegion.Dispatcher.CheckAccess();
                    });
                windowHandle = new WindowInteropHelper(window).Handle;
            }
            catch (Exception exception)
            {
                uiFailure = exception;
            }
            finally { uiReady.Set(); }
            if (uiFailure is null) Dispatcher.Run();
        });
        uiThread.SetApartmentState(ApartmentState.STA);
        uiThread.Start();
        Assert.True(uiReady.Wait(TimeSpan.FromSeconds(5)));
        Assert.Null(uiFailure);

        AutomationElement? liveElement = null;
        AutomationEventHandler? handler = null;
        var clientThread = new Thread(() =>
        {
            try
            {
                clientDispatcher = Dispatcher.CurrentDispatcher;
                var root = AutomationElement.FromHandle(windowHandle);
                liveElement = root.FindFirst(
                    TreeScope.Descendants,
                    new PropertyCondition(
                        AutomationElement.AutomationIdProperty,
                        "AnnouncementLiveRegion"));
                if (liveElement is null) throw new InvalidOperationException("Live region was not exposed to UIA.");
                handler = (sender, _) =>
                {
                    Interlocked.Increment(ref rawEventCount);
                    var text = ((AutomationElement)sender).Current.Name;
                    lock (eventLock)
                    {
                        if (text == lastEventText) return;
                        lastEventText = text;
                        received.Enqueue(text);
                    }
                };
                Automation.AddAutomationEventHandler(
                    AutomationElementIdentifiers.LiveRegionChangedEvent,
                    liveElement,
                    TreeScope.Element,
                    handler);
            }
            catch (Exception exception)
            {
                clientFailure = exception;
            }
            finally { clientReady.Set(); }
            if (clientFailure is null) Dispatcher.Run();
        });
        clientThread.SetApartmentState(ApartmentState.STA);
        clientThread.Start();
        Assert.True(clientReady.Wait(TimeSpan.FromSeconds(5)));
        Assert.Null(clientFailure);

        try
        {
            announcer!.Announce(new("进度 1", AnnouncementKind.Progress, "download:import"));
            announcer.Announce(new("进度 2", AnnouncementKind.Progress, "download:import"));
            announcer.Announce(new("进度 3", AnnouncementKind.Progress, "download:import"));
            WaitUntil(() => received.Count >= 1);

            announcer.Announce(new("导入完成", AnnouncementKind.Success, "download:import"));
            announcer.Announce(new("导入完成", AnnouncementKind.Success, "download:import"));
            WaitUntil(() => received.Count >= 2);
            announcer.Announce(new("导入失败", AnnouncementKind.Error, "download:retry"));
            announcer.Announce(new("导入失败", AnnouncementKind.Error, "download:retry"));
            WaitUntil(() => received.Count >= 3);

            uiDispatcher!.Invoke(() =>
            {
                var noSpeechAnnouncer = new WpfAccessibleAnnouncer(
                    Array.Empty<ITtsService>(),
                    NullLogger<WpfAccessibleAnnouncer>.Instance);
                noSpeechAnnouncer.Attach(liveRegion!);
                noSpeechAnnouncer.Announce("无 TTS 仍可播报");
            });
            WaitUntil(() => received.Count >= 4);

            announcer.SpeechEnabled = false;
            announcer.Announce(new(
                "语音关闭仍可播报",
                AnnouncementKind.Status,
                "speech-preference",
                AnnouncementSpeech.Queued));
            WaitUntil(() => received.Count >= 5);

            Assert.Equal(
                new[] { "进度 3", "导入完成", "导入失败", "无 TTS 仍可播报", "语音关闭仍可播报" },
                received.ToArray());
            Assert.True(changedOnUiThread);
            Assert.Equal(5, textChangeCount);
            Assert.True(rawEventCount >= 5);
            Assert.True(uiDispatcher!.Invoke(() => ReferenceEquals(focusTarget, Keyboard.FocusedElement)));
            Assert.Equal(
                AutomationLiveSetting.Polite,
                uiDispatcher.Invoke(() => AutomationProperties.GetLiveSetting(liveRegion!)));
            Assert.Equal(3, speech.AttemptCount);
        }
        finally
        {
            clientDispatcher!.Invoke(() =>
            {
                Automation.RemoveAutomationEventHandler(
                    AutomationElementIdentifiers.LiveRegionChangedEvent,
                    liveElement!,
                    handler!);
                clientDispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            });
            uiDispatcher!.Invoke(() =>
            {
                window!.Close();
                uiDispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            });
            Assert.True(clientThread.Join(TimeSpan.FromSeconds(5)));
            Assert.True(uiThread.Join(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public async Task Recording_adapter_observes_public_guide_workflow_through_the_host_seam()
    {
        using var app = await Infrastructure.TestApplicationHost.StartAsync();

        await RunOnStaThreadAsync(() =>
        {
            var guide = app.Services.GetRequiredService<ApplicationWindowFactory>()
                .CreateUserGuide(owner: null);
            try
            {
                guide.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, guide));
                guide.ChapterCombo.SelectedIndex = 0;
            }
            finally
            {
                guide.Close();
            }
        });

        var announcement = Assert.Single(
            app.Announcer.Announcements,
            item => item.ActivityId?.StartsWith("guide:", StringComparison.Ordinal) == true);
        Assert.Equal(AnnouncementKind.Status, announcement.Kind);
        Assert.StartsWith("已跳转到章节：", announcement.Text, StringComparison.Ordinal);
    }

    private static void WaitUntil(Func<bool> completed)
    {
        var timeout = Stopwatch.StartNew();
        while (!completed() && timeout.Elapsed < TimeSpan.FromSeconds(5)) Thread.Sleep(20);
        Assert.True(completed(), "Timed out waiting for UI Automation events.");
    }

    private static Task RunOnStaThreadAsync(Action action)
    {
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); completion.SetResult(null); }
            catch (Exception exception) { completion.SetException(exception); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private sealed class ThrowingSpeechAdapter : ITtsService
    {
        public int AttemptCount { get; private set; }
        public bool IsAvailable => true;
        public string ActiveBackendName => "Throwing test adapter";

        public Task SpeakAsync(string text)
        {
            AttemptCount++;
            throw new InvalidOperationException("simulated TTS failure");
        }

        public Task SpeakQueuedAsync(string text) => SpeakAsync(text);
        public void Stop() { }
        public void SetRate(double rate) { }
        public void SetVoice(string voiceId) { }
        public List<VoiceInfo> GetAvailableVoices() => new();
    }
}
