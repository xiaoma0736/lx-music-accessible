using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using LXMusicAccessible.App;
using LXMusicAccessible.Audio;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LXMusicAccessible.Tests;

/// <summary>
/// 语音提示范围是主开关下面的细粒度开关：默认保持原有可朗读类别；用户关闭某一项后，
/// 对应状态仍进入可见播报行，只是不再打断音乐和读屏器。
/// </summary>
public sealed class VoiceAnnouncementScopeTests
{
    [Fact]
    public void Scope_preferences_default_to_the_current_speech_behavior()
    {
        var settings = new AccessibilitySettings();

        Assert.True(settings.AnnouncePlaybackState);
        Assert.True(settings.AnnounceTrackChange);
        Assert.True(settings.AnnounceVolume);
        Assert.True(settings.AnnounceSeek);
        Assert.True(settings.AnnounceWindowVisibility);
        Assert.True(settings.AnnouncePlaybackMode);
        Assert.True(settings.AnnounceAudioDevice);
    }

    [Theory]
    [InlineData("playback-state", nameof(AccessibilitySettings.AnnouncePlaybackState))]
    [InlineData("playback-song", nameof(AccessibilitySettings.AnnounceTrackChange))]
    [InlineData("volume", nameof(AccessibilitySettings.AnnounceVolume))]
    [InlineData("seek", nameof(AccessibilitySettings.AnnounceSeek))]
    [InlineData("desktop-lifecycle", nameof(AccessibilitySettings.AnnounceWindowVisibility))]
    [InlineData("play-mode", nameof(AccessibilitySettings.AnnouncePlaybackMode))]
    [InlineData("audio-device", nameof(AccessibilitySettings.AnnounceAudioDevice))]
    public void Each_scope_switch_controls_its_matching_activity(string activityId, string propertyName)
    {
        var settings = new AccessibilitySettings();
        var announcement = new AccessibleAnnouncement("提示", ActivityId: activityId);
        Assert.True(AccessibleSpeechPolicy.IsScopeAllowed(announcement, settings));

        typeof(AccessibilitySettings).GetProperty(propertyName)!.SetValue(settings, false);

        Assert.False(AccessibleSpeechPolicy.IsScopeAllowed(announcement, settings));
    }

    [Fact]
    public void Scoped_activities_are_silent_when_their_switch_is_off()
    {
        var settings = new AccessibilitySettings
        {
            AnnouncePlaybackState = false,
            AnnounceTrackChange = false,
            AnnounceVolume = false,
            AnnounceSeek = false,
            AnnounceWindowVisibility = false
        };

        Assert.False(AccessibleSpeechPolicy.ShouldSpeak(new AccessibleAnnouncement("已暂停", ActivityId: "playback-state"), settings));
        Assert.False(AccessibleSpeechPolicy.ShouldSpeak(new AccessibleAnnouncement("正在播放", ActivityId: "playback-song"), settings));
        Assert.False(AccessibleSpeechPolicy.ShouldSpeak(new AccessibleAnnouncement("音量 60%", ActivityId: "volume"), settings));
        Assert.False(AccessibleSpeechPolicy.ShouldSpeak(new AccessibleAnnouncement("已跳转到 01:10", ActivityId: "seek"), settings));
        Assert.False(AccessibleSpeechPolicy.ShouldSpeak(new AccessibleAnnouncement("已隐藏", ActivityId: "desktop-lifecycle"), settings));
    }

    [Fact]
    public void Errors_are_not_lost_because_of_a_routine_scope_switch()
    {
        var settings = new AccessibilitySettings
        {
            AnnouncePlaybackState = false,
            AnnounceVolume = false,
            AnnounceSeek = false
        };

        Assert.True(AccessibleSpeechPolicy.ShouldSpeak(new AccessibleAnnouncement("音量调整失败", AnnouncementKind.Error, "volume"), settings));
        Assert.True(AccessibleSpeechPolicy.ShouldSpeak(new AccessibleAnnouncement("跳转失败", AnnouncementKind.Error, "seek"), settings));
    }

    [Fact]
    public void Announcer_keeps_the_visible_mirror_but_skips_scoped_speech()
    {
        WinFormsTestRunner.Run(() =>
        {
            var speech = new ScopeRecordingSpeech();
            var settings = new AccessibilitySettings { AnnounceVolume = false };
            using var live = new Label();
            var announcer = new WinFormsAccessibleAnnouncer(new ITtsService[] { speech }, NullLogger<WinFormsAccessibleAnnouncer>.Instance);
            announcer.Attach(live);
            announcer.SpeechScope = settings;

            announcer.Announce(new AccessibleAnnouncement("音量 60%", AnnouncementKind.Status, "volume"));
            announcer.Announce(new AccessibleAnnouncement("播放设备：系统默认", AnnouncementKind.Status, "audio-device"));

            Assert.Equal("播放设备：系统默认", live.Text);
            Assert.Equal(new[] { "播放设备：系统默认" }, speech.Spoken);
        });
    }

    [Fact]
    public void Seeking_announces_the_target_time()
    {
        WinFormsTestRunner.Run(() =>
        {
            var announcer = new RecordingAnnouncer();
            var audio = new ScopeAudio { Position = TimeSpan.FromMinutes(1), Duration = TimeSpan.FromMinutes(5) };
            using var shell = new WinFormsShell(new WinFormsShellServices
            {
                Announcer = announcer,
                BrowseSource = new FakeBrowseSource(),
                Playback = new RecordingPlayback(),
                Audio = audio,
                TemporaryDirectory = Path.Combine(Path.GetTempPath(), "lx-seek-speech-" + Guid.NewGuid().ToString("N"))
            });

            typeof(WinFormsShell).GetMethod("Seek", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(shell, new object[] { 10d });

            var announcement = Assert.Single(announcer.Announcements, item => item.ActivityId == "seek");
            Assert.Equal("已跳转到 01:10", announcement.Text);
            Assert.Equal(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(10), audio.Position);
        });
    }

    private sealed class ScopeRecordingSpeech : ITtsService
    {
        public List<string> Spoken { get; } = new();
        public bool IsAvailable => true;
        public string ActiveBackendName => "Scope recorder";
        public Task SpeakAsync(string text) { Spoken.Add(text); return Task.CompletedTask; }
        public Task SpeakQueuedAsync(string text) => SpeakAsync(text);
        public void Stop() { }
        public void SetRate(double rate) { }
        public void SetVoice(string voiceId) { }
        public List<VoiceInfo> GetAvailableVoices() => new();
    }

    private sealed class ScopeAudio : IAudioPlaybackControl
    {
        public double Volume { get; set; } = 0.5;
        public TimeSpan Position { get; set; }
        public TimeSpan Duration { get; set; } = TimeSpan.FromMinutes(5);

        public void SetVolume(double volume) => Volume = volume;
        public void SyncVolumeFromDevice() { }
        public void SetPosition(TimeSpan position) => Position = position;
        public void SeekTo(TimeSpan position) => Position = position;
        public string? ActualQuality => null;
        public IReadOnlyList<OutputDeviceInfo> GetOutputDevices() => Array.Empty<OutputDeviceInfo>();
        public int CurrentOutputDeviceIndex => -1;
        public string CurrentOutputDeviceName => "系统默认";
        public string? SwitchToNextOutputDevice() => null;
        public string? SwitchToPreviousOutputDevice() => null;
        public string? SwitchOutputDevice(int index) => null;
    }
}
