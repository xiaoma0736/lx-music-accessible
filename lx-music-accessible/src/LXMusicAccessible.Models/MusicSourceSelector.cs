using System.Collections.ObjectModel;
using System.Windows.Input;

namespace LXMusicAccessible.Models;

/// <summary>
/// 音乐站点选择器
/// </summary>
public class MusicSourceSelector
{
    public ObservableCollection<MusicSourceOption> AvailableSources { get; } = new();

    public MusicSourceSelector()
    {
        InitializeSources();
    }

    private void InitializeSources()
    {
        AvailableSources.Add(new MusicSourceOption
        {
            Source = MusicSourceEnum.Netease,
            Name = "网易云音乐",
            Id = "netease",
            IsEnabled = true
        });

        AvailableSources.Add(new MusicSourceOption
        {
            Source = MusicSourceEnum.QQ,
            Name = "QQ音乐",
            Id = "qq",
            IsEnabled = true
        });

        AvailableSources.Add(new MusicSourceOption
        {
            Source = MusicSourceEnum.Kugou,
            Name = "酷狗音乐",
            Id = "kg",
            IsEnabled = true
        });

        AvailableSources.Add(new MusicSourceOption
        {
            Source = MusicSourceEnum.Kuwo,
            Name = "酷我音乐",
            Id = "kw",
            IsEnabled = true
        });

        AvailableSources.Add(new MusicSourceOption
        {
            Source = MusicSourceEnum.Migu,
            Name = "咪咕音乐",
            Id = "migu",
            IsEnabled = true
        });
    }

    public MusicSourceOption? GetSourceById(string id)
    {
        return AvailableSources.FirstOrDefault(s => s.Id == id);
    }

    public MusicSourceOption? GetSourceByEnum(MusicSourceEnum source)
    {
        return AvailableSources.FirstOrDefault(s => s.Source == source);
    }
}

/// <summary>
/// 音乐站点选项
/// </summary>
public class MusicSourceOption
{
    public MusicSourceEnum Source { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public string? Description { get; set; }
}