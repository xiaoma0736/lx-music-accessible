using LXMusicAccessible.Services;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class QualityCatalogTests
{
    [Theory]
    [InlineData("128k", "标准")]
    [InlineData("192k", "较高")]
    [InlineData("320k", "极高")]
    [InlineData("flac", "无损 FLAC")]
    [InlineData("flac24bit", "Hi-Res 无损")]
    [InlineData("hires", "Hi-Res")]
    [InlineData("atmos", "杜比全景声")]
    [InlineData("atmos_plus", "杜比全景声+")]
    [InlineData("master", "母带")]
    [InlineData("jyeffect", "高清环绕声")]
    [InlineData("sky", "沉浸环绕声")]
    [InlineData("dolby", "杜比")]
    [InlineData("jymaster", "珍品/超清母带")]
    [InlineData("viper", "蝰蛇音质")]
    [InlineData("spatial", "空间音频")]
    [InlineData("hifi", "Hi-Fi 无损")]
    [InlineData("ape", "无损 APE")]
    [InlineData("alac", "无损 ALAC")]
    [InlineData("wav", "无损 WAV")]
    [InlineData("aac", "AAC")]
    [InlineData("m4a", "AAC/M4A")]
    [InlineData("  FLAC ", "无损 FLAC")]
    public void Label_maps_known_quality_ids(string quality, string expected)
    {
        Assert.Equal(expected, QualityCatalog.Label(quality));
    }

    [Fact]
    public void Label_preserves_unknown_quality_id()
    {
        Assert.Equal("provider_gold", QualityCatalog.Label("provider_gold"));
        Assert.Equal("未知", QualityCatalog.Label(" "));
    }

    [Fact]
    public void Order_removes_duplicates_and_sorts_from_highest_fidelity()
    {
        var actual = QualityCatalog.Order([
            "320k", "master", "128k", "VIPER", "flac", "master", null, ""
        ]);

        Assert.Equal(["viper", "master", "flac", "320k", "128k"], actual);
    }

    [Theory]
    [InlineData("128k", 1)]
    [InlineData("320k", 3)]
    [InlineData("flac", 4)]
    [InlineData("flac24bit", 5)]
    [InlineData("hires", 6)]
    [InlineData("atmos", 10)]
    [InlineData("atmos_plus", 11)]
    [InlineData("master", 12)]
    [InlineData("jymaster", 13)]
    [InlineData("viper", 14)]
    [InlineData("spatial", 10)]
    [InlineData("hifi", 5)]
    [InlineData("ape", 4)]
    public void Rank_orders_extended_qualities(string quality, int expected)
    {
        Assert.Equal(expected, QualityCatalog.Rank(quality));
    }
}
