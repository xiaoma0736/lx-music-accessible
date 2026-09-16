using LXMusicAccessible.Models;
using System.Text.Json;

namespace LXMusicAccessible.Services;

/// <summary>搜索历史的唯一文件存储实现；设置由 SettingsWorkflow/SQLite 独占。</summary>
public sealed class LocalSearchHistoryStore
{
    private readonly string _historyFilePath;
    private readonly JsonSerializerOptions _jsonOptions;

    public LocalSearchHistoryStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LXMusicAccessible"))
    {
    }

    public LocalSearchHistoryStore(string appDataPath)
    {
        FrameworkCompatibility.ThrowIfNullOrWhiteSpace(appDataPath);
        var directory = Path.GetFullPath(appDataPath);
        Directory.CreateDirectory(directory);

        _historyFilePath = Path.Combine(directory, "search_history.json");

        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }

    public async Task SaveAsync(IReadOnlyList<SearchHistory> history, CancellationToken cancellationToken = default)
    {
        FrameworkCompatibility.ThrowIfNull(history);
        var json = JsonSerializer.Serialize(history, _jsonOptions);
        await Task.Run(() => File.WriteAllText(_historyFilePath, json), cancellationToken);
    }

    public async Task<IReadOnlyList<SearchHistory>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_historyFilePath)) return [];
        var json = await Task.Run(() => File.ReadAllText(_historyFilePath), cancellationToken);
        return JsonSerializer.Deserialize<List<SearchHistory>>(json, _jsonOptions) ?? [];
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(_historyFilePath)) File.Delete(_historyFilePath);
        return Task.CompletedTask;
    }
}
