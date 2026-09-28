using LXMusicAccessible.App;
using LXMusicAccessible.Services;
using Xunit;

namespace LXMusicAccessible.Tests;

/// <summary>
/// Speech is opt-in. A screen reader already says focus, selection and list content, and playback itself is
/// audible, so anything that only echoes what the user can already perceive stays out of this list. Keeping
/// the list explicit is what prevents 已暂停, 音量 60% or a browse page from interrupting the reader again.
/// </summary>
public sealed class SpeechWhitelistTests
{
    [Theory]
    [InlineData("album")]
    [InlineData("application-shutdown")]
    [InlineData("audio-device")]
    [InlineData("desktop-lifecycle")]
    [InlineData("seek")]
    [InlineData("download-manager")]
    [InlineData("external-playback")]
    [InlineData("favorite")]
    [InlineData("hotkey-recording")]
    [InlineData("hotkey-registration")]
    [InlineData("lyrics")]
    [InlineData("lyrics-offset")]
    [InlineData("play-mode")]
    [InlineData("playback-queue")]
    [InlineData("playback-state")]
    [InlineData("playback-song")]
    [InlineData("playlist-import")]
    [InlineData("volume")]
    [InlineData("playlist-login")]
    [InlineData("playlist-preview")]
    [InlineData("qr-login")]
    [InlineData("remote-favorite")]
    [InlineData("search-history-clear")]
    [InlineData("search-history-delete")]
    [InlineData("settings")]
    [InlineData("source-default")]
    [InlineData("source-delete")]
    [InlineData("source-disable")]
    [InlineData("source-enable")]
    [InlineData("source-import")]
    [InlineData("source-list")]
    [InlineData("source-test")]
    [InlineData("source-update")]
    [InlineData("sponsor")]
    [InlineData("web-login")]
    [InlineData("download:42")]
    [InlineData("download-list:abc")]
    [InlineData("download-external:abc")]
    [InlineData("manual-source-query:netease:target-1")]
    [InlineData("manual-source-playback:qq:song-1:2")]
    [InlineData("playback-track:7")]
    public void Allowed_activity_categories_enable_speech(string activityId)
    {
        Assert.True(AccessibleSpeechPolicy.ShouldSpeak(
            new AccessibleAnnouncement("提示", ActivityId: activityId)));
    }

    [Theory]
    [InlineData("welcome")]
    [InlineData("search-clear")]
    [InlineData("search-results")]
    [InlineData("guide")] // 章节名：组合框换选项时读屏器已经朗读了新选项
    [InlineData("search-history")]
    [InlineData("search")]
    [InlineData("browse-back")]
    [InlineData("categories")]
    [InlineData("rankings")]
    [InlineData("saved-playlists")]
    [InlineData("music-browse:1")]
    [InlineData("playback")]
    [InlineData("lyric")] // 前缀不等于 lyrics
    [InlineData("downloaddirectory")]
    public void Everything_that_is_not_listed_stays_silent(string activityId)
    {
        Assert.False(AccessibleSpeechPolicy.ShouldSpeak(
            new AccessibleAnnouncement("提示", ActivityId: activityId)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void An_announcement_without_an_activity_never_reaches_speech(string? activityId)
    {
        Assert.False(AccessibleSpeechPolicy.ShouldSpeak(
            new AccessibleAnnouncement("提示", ActivityId: activityId)));
    }

    [Fact]
    public void A_failure_is_worth_interrupting_for_even_outside_the_list()
    {
        Assert.True(AccessibleSpeechPolicy.ShouldSpeak(
            new AccessibleAnnouncement("加载失败，请检查网络", AnnouncementKind.Error, "music-browse:1")));
        Assert.True(AccessibleSpeechPolicy.ShouldSpeak(
            new AccessibleAnnouncement("已停止播放失败", AnnouncementKind.Error, "playback-state")));
    }

    [Fact]
    public void Busy_and_progress_echoes_of_a_silenced_activity_stay_silent()
    {
        Assert.False(AccessibleSpeechPolicy.ShouldSpeak(
            new AccessibleAnnouncement("正在加载歌单分类", AnnouncementKind.Busy, "music-browse:2")));
        Assert.False(AccessibleSpeechPolicy.ShouldSpeak(
            new AccessibleAnnouncement("已加载 30 首", AnnouncementKind.Success, "music-browse:2")));
    }

    [Fact]
    public void Explicit_none_still_disables_speech_for_allowed_categories()
    {
        Assert.False(AccessibleSpeechPolicy.ShouldSpeak(
            new AccessibleAnnouncement(
                "测试提示",
                ActivityId: "download:42",
                Speech: AnnouncementSpeech.None)));
        // A call site can even outrank the failure rule, because the change is audible on its own.
        Assert.False(AccessibleSpeechPolicy.ShouldSpeak(
            new AccessibleAnnouncement(
                "音量 60%",
                AnnouncementKind.Error,
                "volume",
                AnnouncementSpeech.None)));
    }

}
