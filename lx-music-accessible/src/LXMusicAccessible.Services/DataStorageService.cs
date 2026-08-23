using LXMusicAccessible.Models;
using System.Collections.ObjectModel;
using System.Text.Json;

namespace LXMusicAccessible.Services;

/// <summary>
/// 数据存储服务接口
/// </summary>
public interface IDataStorageService
{
    /// <summary>
    /// 保存搜索历史
    /// </summary>
    Task SaveSearchHistoryAsync(ObservableCollection<SearchHistory> history);

    /// <summary>
    /// 加载搜索历史
    /// </summary>
    Task<ObservableCollection<SearchHistory>> LoadSearchHistoryAsync();

    /// <summary>
    /// 清除搜索历史
    /// </summary>
    Task ClearSearchHistoryAsync();

    /// <summary>
    /// 保存用户设置
    /// </summary>
    Task SaveUserSettingsAsync<T>(string key, T settings);

    /// <summary>
    /// 加载用户设置
    /// </summary>
    Task<T?> LoadUserSettingsAsync<T>(string key);
}

/// <summary>
/// 本地文件数据存储服务实现
/// </summary>
public class LocalFileDataStorageService : IDataStorageService
{
    private readonly string _appDataPath;
    private readonly string _historyFilePath;
    private readonly string _settingsPath;
    private readonly JsonSerializerOptions _jsonOptions;

    public LocalFileDataStorageService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LXMusicAccessible"))
    {
    }

    public LocalFileDataStorageService(string appDataPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataPath);
        _appDataPath = Path.GetFullPath(appDataPath);
        Directory.CreateDirectory(_appDataPath);

        _historyFilePath = Path.Combine(_appDataPath, "search_history.json");
        _settingsPath = Path.Combine(_appDataPath, "user_settings.json");

        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }

    public async Task SaveSearchHistoryAsync(ObservableCollection<SearchHistory> history)
    {
        try
        {
            var historyList = history.Take(50).ToList(); // 只保存最近50条
            var json = JsonSerializer.Serialize(historyList, _jsonOptions);
            await File.WriteAllTextAsync(_historyFilePath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"保存搜索历史失败: {ex.Message}");
        }
    }

    public async Task<ObservableCollection<SearchHistory>> LoadSearchHistoryAsync()
    {
        try
        {
            if (!File.Exists(_historyFilePath))
            {
                return new ObservableCollection<SearchHistory>();
            }

            var json = await File.ReadAllTextAsync(_historyFilePath);
            var historyList = JsonSerializer.Deserialize<List<SearchHistory>>(json, _jsonOptions);
            
            return new ObservableCollection<SearchHistory>(historyList ?? new List<SearchHistory>());
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"加载搜索历史失败: {ex.Message}");
            return new ObservableCollection<SearchHistory>();
        }
    }

    public async Task ClearSearchHistoryAsync()
    {
        try
        {
            if (File.Exists(_historyFilePath))
            {
                File.Delete(_historyFilePath);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"清除搜索历史失败: {ex.Message}");
        }
    }

    public async Task SaveUserSettingsAsync<T>(string key, T settings)
    {
        try
        {
            var settingsData = new Dictionary<string, object>();
            
            if (File.Exists(_settingsPath))
            {
                var json = await File.ReadAllTextAsync(_settingsPath);
                settingsData = JsonSerializer.Deserialize<Dictionary<string, object>>(json, _jsonOptions) ?? new Dictionary<string, object>();
            }

            settingsData[key] = settings!;
            var serializedJson = JsonSerializer.Serialize(settingsData, _jsonOptions);
            await File.WriteAllTextAsync(_settingsPath, serializedJson);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"保存用户设置失败: {ex.Message}");
        }
    }

    public async Task<T?> LoadUserSettingsAsync<T>(string key)
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return default;
            }

            var json = await File.ReadAllTextAsync(_settingsPath);
            var settingsData = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, _jsonOptions);
            
            if (settingsData != null && settingsData.TryGetValue(key, out var element))
            {
                return JsonSerializer.Deserialize<T>(element.GetRawText(), _jsonOptions);
            }

            return default;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"加载用户设置失败: {ex.Message}");
            return default;
        }
    }
}
