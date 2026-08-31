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
    [InlineData("download:42")]
    [InlineData("download-list:abc")]
    public void Allowed_activity_categories_enable_speech(string activityId)
    {
        Assert.True(AccessibleSpeechPolicy.ShouldSpeak(
            new AccessibleAnnouncement("??", ActivityId: activityId)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("welcome")]
    [InlineData("search")]
    [InlineData("lyrics")]
    [InlineData("download")] // ?????????????? download ??/???
    public void Other_activity_categories_do_not_enable_speech(string? activityId)
    {
        Assert.False(AccessibleSpeechPolicy.ShouldSpeak(
            new AccessibleAnnouncement("??", ActivityId: activityId)));
    }

    [Fact]
    public void Explicit_none_still_disables_speech_for_allowed_categories()
    {
        Assert.False(AccessibleSpeechPolicy.ShouldSpeak(
            new AccessibleAnnouncement(
                "????",
                ActivityId: "download:42",
                Speech: AnnouncementSpeech.None)));
    }
}
