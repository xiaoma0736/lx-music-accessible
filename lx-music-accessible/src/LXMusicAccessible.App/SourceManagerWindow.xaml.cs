using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Threading;
using LXMusicAccessible.Models;
using LXMusicAccessible.Services;

namespace LXMusicAccessible.App;

public partial class SourceManagerWindow : Window
{
    private readonly IAccessibleAnnouncer _announcer;
    private readonly IMusicSourceService _sources;
    private readonly Func<KeyEventArgs, bool> _handleHotkey;
    private bool _closed;

    public SourceManagerWindow(
        IAccessibleAnnouncer announcer,
        IMusicSourceService sources,
        Func<KeyEventArgs, bool> handleHotkey)
    {
        ArgumentNullException.ThrowIfNull(announcer);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(handleHotkey);
        InitializeComponent();
        _announcer = announcer;
        _sources = sources;
        _handleHotkey = handleHotkey;
        _sources.SourcesChanged += Sources_SourcesChanged;
        RefreshSources();
        Loaded += Window_Loaded;
    }

    internal bool IsSourceSubscriptionActive { get; private set; } = true;

    private void Window_Loaded(object sender, RoutedEventArgs e) =>
        Dispatcher.BeginInvoke(SourceList.Focus, DispatcherPriority.Input);

    private void Sources_SourcesChanged(object? sender, EventArgs e)
    {
        if (_closed) return;
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (!_closed) RefreshSources();
        }, DispatcherPriority.Background);
    }

    internal void RefreshSources()
    {
        if (_closed) return;
        var selectedId = (SourceList.SelectedItem as MusicSource)?.Id;
        var selectedIndex = SourceList.SelectedIndex;
        var hadFocus = SourceList.IsKeyboardFocusWithin;
        var sources = _sources.GetAllSources();

        SourceList.ItemsSource = sources;
        var selected = selectedId is null
            ? null
            : sources.FirstOrDefault(source => string.Equals(
                source.Id,
                selectedId,
                StringComparison.OrdinalIgnoreCase));
        if (selected is not null)
        {
            SourceList.SelectedItem = selected;
        }
        else if (sources.Count > 0)
        {
            var fallback = Math.Max(0, selectedIndex);
            SourceList.SelectedIndex = Math.Min(fallback, sources.Count - 1);
        }

        var empty = sources.Count == 0;
        EmptySourceText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetItemStatus(SourceList, empty ? "暂无音源" : $"共 {sources.Count} 个音源");
        if (hadFocus) SourceList.Focus();
    }

    private async void ImportLocalSourceBtn_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "音源脚本|*.js|所有文件|*.*",
            Title = "导入本地音源脚本",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;

        Announce("正在导入本地音源...");
        try
        {
            var source = await _sources.ImportFromFileAsync(dialog.FileName);
            if (source is null)
            {
                Announce("导入失败：无法读取或解析脚本", AnnouncementKind.Error);
                return;
            }
            RefreshIfOpen();
            Announce($"已导入并启用本地音源：{source.Name} v{source.Version}", AnnouncementKind.Success);
        }
        catch (Exception exception)
        {
            Announce($"导入失败：{exception.Message}", AnnouncementKind.Error);
        }
    }

    private async void ImportNetworkSourceBtn_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new TextInputDialog("从网络导入音源", "音源URL：", "", "音源名称（可留空）：", "")
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true) return;
        var url = dialog.Value?.Trim();
        if (string.IsNullOrEmpty(url))
        {
            Announce("导入已取消");
            return;
        }

        Announce("正在从网络导入音源...");
        try
        {
            var source = await _sources.ImportFromUrlAsync(url);
            if (source is null)
            {
                Announce("导入失败：无法下载或解析脚本", AnnouncementKind.Error);
                return;
            }
            if (!string.IsNullOrWhiteSpace(dialog.Value2))
            {
                source.Name = dialog.Value2.Trim();
                await _sources.UpdateSourceAsync(source);
            }
            RefreshIfOpen();
            Announce($"已导入并启用网络音源：{source.Name} v{source.Version}", AnnouncementKind.Success);
        }
        catch (Exception exception)
        {
            Announce($"导入失败：{exception.Message}", AnnouncementKind.Error);
        }
    }

    private void RefreshSourcesBtn_Click(object sender, RoutedEventArgs e)
    {
        RefreshSources();
        SourceList.Focus();
        Announce(SourceList.Items.Count == 0 ? "暂无音源" : "音源列表已刷新");
    }

    private void SourceList_KeyDown(object sender, KeyEventArgs e)
    {
        if (SourceList.SelectedItem is not MusicSource source) return;
        if (e.Key == Key.Enter)
        {
            Announce(source.AccessibleName);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            DeleteSelectedSource();
            e.Handled = true;
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (SourceList.ContextMenu?.IsOpen == true) return;
            Close();
            e.Handled = true;
            return;
        }
        if ((e.Key == Key.F4 || e.SystemKey == Key.F4)
            && (Keyboard.Modifiers & ModifierKeys.Alt) != 0)
        {
            Close();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Apps || (e.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift))
        {
            if (OpenSourceContextMenu()) e.Handled = true;
            return;
        }
        if (SourceList.IsKeyboardFocusWithin && e.Key is Key.Enter or Key.Delete) return;
        if (_handleHotkey(e)) e.Handled = true;
    }

    private bool OpenSourceContextMenu()
    {
        if (!SourceList.IsKeyboardFocusWithin || SourceList.SelectedItem is not MusicSource) return false;
        if (SourceList.ContextMenu is not { } menu) return false;
        menu.PlacementTarget = SourceList;
        menu.IsOpen = true;
        return true;
    }

    private void ContextMenu_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Apps) e.Handled = true;
    }

    private void SourceMenu_SetDefault_Click(object sender, RoutedEventArgs e)
    {
        if (SourceList.SelectedItem is not MusicSource source) return;
        if (source.TrustState == ScriptTrustState.Untrusted)
        {
            Announce("暂存音源必须先验证并启用，才能设为默认", AnnouncementKind.Error);
            return;
        }
        _sources.SetDefaultSource(source.Id);
        RefreshIfOpen();
        Announce($"已将 {source.Name} 设为默认音源", AnnouncementKind.Success);
    }

    private async void SourceMenu_Enable_Click(object sender, RoutedEventArgs e)
    {
        if (SourceList.SelectedItem is not MusicSource source) return;
        try
        {
            var result = await _sources.EnableSourceAsync(source);
            RefreshIfOpen();
            Announce(
                result.Succeeded ? $"已启用音源：{source.Name}" : result.Message,
                result.Succeeded ? AnnouncementKind.Success : AnnouncementKind.Error);
        }
        catch (Exception exception)
        {
            Announce($"启用失败：{exception.Message}", AnnouncementKind.Error);
        }
    }

    private void SourceMenu_Disable_Click(object sender, RoutedEventArgs e)
    {
        if (SourceList.SelectedItem is not MusicSource source) return;
        _sources.DisableSource(source);
        RefreshIfOpen();
        Announce($"已禁用音源：{source.Name}", AnnouncementKind.Success);
    }

    private async void SourceMenu_Test_Click(object sender, RoutedEventArgs e)
    {
        if (SourceList.SelectedItem is not MusicSource source) return;
        Announce($"正在检测 {source.Name} 有效性...");
        try
        {
            var valid = await _sources.TestSourceAsync(source);
            RefreshIfOpen();
            Announce(
                valid ? $"{source.Name} 有效" : $"{source.Name} 无效",
                valid ? AnnouncementKind.Success : AnnouncementKind.Error);
        }
        catch (Exception exception)
        {
            Announce($"检测失败：{exception.Message}", AnnouncementKind.Error);
        }
    }

    private async void SourceMenu_Edit_Click(object sender, RoutedEventArgs e)
    {
        if (SourceList.SelectedItem is not MusicSource source) return;
        var dialog = new TextInputDialog("编辑音源名称", "音源名称：", source.Name) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var name = dialog.Value?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            Announce("名称不能为空", AnnouncementKind.Error);
            return;
        }

        source.Name = name;
        try
        {
            await _sources.UpdateSourceAsync(source);
            RefreshIfOpen();
            Announce($"音源名称已改为 {source.Name}", AnnouncementKind.Success);
        }
        catch (Exception exception)
        {
            Announce($"编辑失败：{exception.Message}", AnnouncementKind.Error);
        }
    }

    private void SourceMenu_Delete_Click(object sender, RoutedEventArgs e) => DeleteSelectedSource();

    private void DeleteSelectedSource()
    {
        if (SourceList.SelectedItem is not MusicSource source) return;
        if (source.Type == SourceType.BuiltIn)
        {
            Announce("内置音源不可删除", AnnouncementKind.Error);
            return;
        }
        if (MessageBox.Show(
                this,
                $"确定删除音源 {source.Name} 吗？",
                "确认删除",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        _sources.RemoveSource(source);
        RefreshIfOpen();
        SourceList.Focus();
        Announce($"已删除音源：{source.Name}", AnnouncementKind.Success);
    }

    private void RefreshIfOpen()
    {
        if (!_closed) RefreshSources();
    }

    private void Announce(string text, AnnouncementKind kind = AnnouncementKind.Status) =>
        _announcer.Announce(new(text, kind, Speech: AnnouncementSpeech.FollowPreference));

    private void Window_Closed(object? sender, EventArgs e)
    {
        if (_closed) return;
        _closed = true;
        Loaded -= Window_Loaded;
        _sources.SourcesChanged -= Sources_SourcesChanged;
        IsSourceSubscriptionActive = false;
    }
}
