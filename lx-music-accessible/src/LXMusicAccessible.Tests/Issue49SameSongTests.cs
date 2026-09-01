using LXMusicAccessible.Models;
using LXMusicAccessible.Services;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class Issue49SameSongTests
{
    [Theory]
    [InlineData("晴天", "周杰伦", "04:30", "晴天", "周杰伦", "04:31", true)]
    [InlineData("晴天 (Live)", "周杰伦/群星", "04:30", "晴天", "群星、周杰伦", "04:33", true)]
    [InlineData("晴天", "周杰伦", "04:30", "晴天", "周杰伦", "04:36", false)]
    [InlineData("晴天", "周杰伦", "04:30", "晴天", "周杰伦", "", false)]
    [InlineData("晴天", "周杰伦", "04:30", "晴天", "", "04:31", false)]
    [InlineData("晴天", "周杰伦", "04:30", "晴天", "另一名歌手", "04:31", false)]
    [InlineData("晴天", "周杰伦", "04:30", "晴天伴奏", "周杰伦", "04:31", false)]
    public void Playback_alternatives_require_strict_same_song_identity(
        string targetTitle,
        string targetArtist,
        string targetDuration,
        string candidateTitle,
        string candidateArtist,
        string candidateDuration,
        bool expected)
    {
        var target = new MusicInfo
        {
            Title = targetTitle,
            Artist = targetArtist,
            Duration = targetDuration,
            Album = "专辑"
        };
        var candidate = new MusicInfo
        {
            Title = candidateTitle,
            Artist = candidateArtist,
            Duration = candidateDuration,
            Album = "专辑"
        };

        Assert.Equal(expected, MusicSearchMatcher.IsSameSong(candidate, target));
    }

    [Fact]
    public void Album_mismatch_is_not_the_same_song_when_both_albums_are_known()
    {
        var target = new MusicInfo
        {
            Title = "晴天",
            Artist = "周杰伦",
            Duration = "04:30",
            Album = "叶惠美"
        };
        var candidate = new MusicInfo
        {
            Title = "晴天",
            Artist = "周杰伦",
            Duration = "04:31",
            Album = "演唱会"
        };

        Assert.False(MusicSearchMatcher.IsSameSong(candidate, target));
    }
}
