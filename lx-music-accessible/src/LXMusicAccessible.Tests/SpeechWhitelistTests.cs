using LXMusicAccessible.App;
using LXMusicAccessible.Services;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class SpeechWhitelistTests
{
    [Theory]
    [InlineData("play-mode")]
    [InlineData("audio-device")]
    [InlineData("playback-song")]
    [InlineData("lyrics")]
    [InlineData("lyrics-offset")]
    [InlineData("settings")]
    [InlineData("hotkey-recording")]
    [InlineData("download:42")]
    [InlineData("manual-source-query:netease:target-1")]
    [InlineData("manual-source-playback:qq:song-1:2")]
    [InlineData("download-list:abc")]
    public void Allowed_activity_categories_enable_speech(string activityId)
    {
        Assert.True(AccessibleSpeechPolicy.ShouldSpeak(
            new AccessibleAnnouncement("提示", ActivityId: activityId)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("welcome")]
    [InlineData("search")]
    [InlineData("download")] // 裸 download 不匹配，仅 download:/download-list: 前缀生效
    public void Other_activity_categories_do_not_enable_speech(string? activityId)
    {
        Assert.False(AccessibleSpeechPolicy.ShouldSpeak(
            new AccessibleAnnouncement("提示", ActivityId: activityId)));
    }

    [Fact]
    public void Explicit_none_still_disables_speech_for_allowed_categories()
    {
        Assert.False(AccessibleSpeechPolicy.ShouldSpeak(
            new AccessibleAnnouncement(
                "测试提示",
                ActivityId: "download:42",
                Speech: AnnouncementSpeech.None)));
    }
}
