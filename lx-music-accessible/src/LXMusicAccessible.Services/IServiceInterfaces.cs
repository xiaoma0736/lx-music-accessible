using LXMusicAccessible.Models;
using LXMusicAccessible.Core.MusicApi;
using LXMusicAccessible.Audio;
using System.Windows.Input;

namespace LXMusicAccessible.Services;

/// <summary>
/// 鏃犻殰纰嶆湇鍔℃帴鍙?
/// </summary>
public interface IAccessibilityService
{
    /// <summary>
    /// 鍙戦€佽闊虫彁绀?
    /// </summary>
    void Announce(string message);

    /// <summary>
    /// 璁剧疆鐒︾偣鍒版寚瀹氬厓绱?
    /// </summary>
    void SetFocus(System.Windows.FrameworkElement element);

    /// <summary>
    /// 瀵艰埅鍒版寚瀹氭柟鍚?
    /// </summary>
    void Navigate(NavigationDirection direction);

    /// <summary>
    /// 鐒︾偣鍙樺寲浜嬩欢
    /// </summary>
    event EventHandler<FocusChangedEventArgs>? FocusChanged;

    /// <summary>
    /// 鑾峰彇褰撳墠鐒︾偣鍏冪礌
    /// </summary>
    System.Windows.FrameworkElement? GetCurrentFocus();

    /// <summary>
    /// 楠岃瘉鐒︾偣椤哄簭
    /// </summary>
    bool ValidateFocusOrder();
}

/// <summary>
/// 闊充箰鎾斁鍣ㄦ湇鍔℃帴鍙?
/// </summary>
public interface IMusicPlayerService
{
    /// <summary>
    /// 鎾斁鎸囧畾闊充箰
    /// </summary>
    Task PlayAsync(string url, MusicInfo musicInfo);

    /// <summary>
    /// 鏆傚仠鎾斁
    /// </summary>
    void Pause();

    /// <summary>
    /// 鍋滄鎾斁
    /// </summary>
    void Stop();

    /// <summary>
    /// 涓嬩竴棣?
    /// </summary>
    void Next();

    /// <summary>
    /// 涓婁竴棣?
    /// </summary>
    void Previous();

    /// <summary>
    /// 璁剧疆闊抽噺
    /// </summary>
    void SetVolume(double volume);
    
    /// <summary>
    /// 鑾峰彇褰撳墠闊抽噺
    /// </summary>
    double Volume { get; }

    /// <summary>
    /// 鎶婅澶囷紙闊抽噺鍚堟垚鍣級褰撳墠闊抽噺鍚屾鍒板唴閮ㄧ姸鎬侊紙渚涘畾鏃惰疆璇㈣皟鐢紝瀹炵幇鐑敭涓庡悎鎴愬櫒鍙屽悜鍚屾锛?
    /// </summary>
    void SyncVolumeFromDevice();

    /// <summary>
    /// 璁剧疆鎾斁浣嶇疆
    /// </summary>
    void SetPosition(TimeSpan position);

    /// <summary>
    /// 瀹氫綅鍒版寚瀹氭挱鏀句綅缃紙娓呯┖杈撳嚭缂撳啿锛岀‘淇濋煶棰戠珛鍗宠烦鍒版柊浣嶇疆锛涗緵蹇繘/蹇€€浣跨敤锛?
    /// </summary>
    void SeekTo(TimeSpan position);

    /// <summary>
    /// 褰撳墠鎾斁浣嶇疆
    /// </summary>
    TimeSpan Position { get; }

    /// <summary>
    /// 闊充箰鎬绘椂闀?
    /// </summary>
    TimeSpan Duration { get; }

    /// <summary>
    /// 鏄惁姝ｅ湪鎾斁
    /// </summary>
    bool IsPlaying { get; }
    /// <summary>
    /// 鏄惁澶勪簬鏆傚仠鐘舵€?
    /// </summary>
    bool IsPaused { get; }

    /// <summary>
    /// 鎭㈠鎾斁锛堜笉閲嶆柊鍔犺浇锛屼粠鏆傚仠浣嶇疆缁х画锛?
    /// </summary>
    Task ResumeAsync();

    /// <summary>
    /// 璁剧疆鎾斁闃熷垪锛堟暣鍒楄〃椤哄簭鎾斁锛夛細浠庢潵婧愬垪琛ㄦ瀯寤洪槦鍒楀苟瀹氫綅褰撳墠姝屾洸锛屼娇涓婁竴棣?涓嬩竴棣?鑷姩杩炴挱浣滅敤浜庢暣涓垪琛?
    /// </summary>
    void SetQueue(List<MusicInfo> queue, MusicInfo currentMusic);

    /// <summary>
    /// 播放队列条目替换（同曲换源用）：以实际播放的同曲替换原条目，保持上一首/下一首位置正确
    /// </summary>
    void ReplaceQueueItem(MusicInfo oldItem, MusicInfo newItem);

    /// <summary>
    /// 褰撳墠鎾斁闊充箰淇℃伅
    /// </summary>
    MusicInfo? CurrentMusic { get; }

    /// <summary>
    /// 褰撳墠瀹為檯鎾斁闊宠川锛堢敱鎾斁鍣ㄦ祦璇诲彇锛氱紪鐮佹牸寮?+ 骞冲潎鐮佺巼锛夛紝鏃犳硶纭畾鏃朵负 null
    /// </summary>
    string? ActualQuality { get; }
    
    /// <summary>
    /// 寰幆鎾斁妯″紡
    /// </summary>
    bool IsLoopMode { get; set; }
    
    /// <summary>
    /// 闅忔満鎾斁妯″紡
    /// </summary>
    bool IsShuffleMode { get; set; }

    /// <summary>
    /// 鑷姩杩炴挱/涓婁竴棣?涓嬩竴棣栬В鏋愭挱鏀鹃摼鎺ユ椂浣跨敤鐨勯粯璁ら煶璐?
    /// </summary>
    string DefaultQuality { get; set; }

    /// <summary>
    /// 鎾斁鍣ㄧ姸鎬佸彉鍖栦簨浠?
    /// </summary>
    event EventHandler<PlayerStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// 鎾斁闃熷垪宸叉挱瀹岋紙椤哄簭鎾斁鍒版湯灏句笖闈炲惊鐜ā寮忔椂瑙﹀彂锛屼緵鑷姩缈婚〉缁挱锛?
    /// </summary>
    event EventHandler? QueueCompleted;

    /// <summary>
    /// 鑾峰彇鎵€鏈夊彲鐢ㄨ緭鍑鸿澶囷紙棣栭」涓衡€滅郴缁熼粯璁も€濓級
    /// </summary>
    IReadOnlyList<OutputDeviceInfo> GetOutputDevices();

    /// <summary>
    /// 褰撳墠杈撳嚭璁惧绱㈠紩锛?1 = 绯荤粺榛樿锛?
    /// </summary>
    int CurrentOutputDeviceIndex { get; }

    /// <summary>
    /// 褰撳墠杈撳嚭璁惧鍚嶇О锛堜緵鎾姤锛?
    /// </summary>
    string CurrentOutputDeviceName { get; }

    /// <summary>
    /// 鍒囨崲鍒颁笅涓€涓挱鏀捐澶囷紝杩斿洖鏂拌澶囧悕绉帮紙澶辫触杩斿洖 null锛?
    /// </summary>
    string? SwitchToNextOutputDevice();

    /// <summary>
    /// 鍒囨崲鍒颁笂涓€涓挱鏀捐澶囷紝杩斿洖鏂拌澶囧悕绉帮紙澶辫触杩斿洖 null锛?
    /// </summary>
    string? SwitchToPreviousOutputDevice();

    /// <summary>
    /// 鍒囨崲鍒版寚瀹氳緭鍑鸿澶囷紙鎾斁涓儹鍒囨崲淇濈暀杩涘害锛夛紝杩斿洖鏂拌澶囧悕绉帮紙澶辫触杩斿洖 null锛?
    /// </summary>
    string? SwitchOutputDevice(int index);
}

/// <summary>
/// 姝岃瘝缁撴灉锛堝惈鏉ユ簮涓庢槸鍚﹁法婧愭悳绱級
/// </summary>
public class LyricResult
{
    /// <summary>瑙ｆ瀽鍚庣殑姝岃瘝</summary>
    public Lyric Lyric { get; set; } = new();

    /// <summary>姝岃瘝瀹為檯鏉ユ簮闊虫簮 id锛堝悓骞冲彴鎴栬法骞冲彴鎼滅储鍛戒腑鑰咃級</summary>
    public string SourceId { get; set; } = "";

    /// <summary>鏄惁閫氳繃璺ㄥ钩鍙版悳绱㈣幏寰?/summary>
    public bool FromSearch { get; set; }
}

/// <summary>
/// 姝岃瘝鏈嶅姟鎺ュ彛
/// </summary>
public interface ILyricService
{
    /// <summary>
    /// 鑾峰彇姝岃瘝锛氬悓骞冲彴浼樺厛锛岀己澶辨椂鑷姩璺ㄥ钩鍙版悳绱紙netease鈫抭q鈫択g鈫択w鈫抦igu锛?
    /// </summary>
    Task<LyricResult?> GetLyricAsync(MusicInfo music);

    /// <summary>
    /// 鑾峰彇褰撳墠姝岃瘝琛?
    /// </summary>
    LyricLine? GetCurrentLine(TimeSpan position);

    /// <summary>
    /// 鑾峰彇鎵€鏈夋瓕璇嶈
    /// </summary>
    List<LyricLine> GetAllLines();

    /// <summary>
    /// 姝岃瘝琛屽彉鍖栦簨浠?
    /// </summary>
    event EventHandler<LyricLineChangedEventArgs>? LineChanged;

    /// <summary>
    /// 璁剧疆姝岃瘝
    /// </summary>
    void SetLyric(Lyric lyric);
}

/// <summary>
/// TTS鏈嶅姟鎺ュ彛
/// </summary>
public interface ITtsService
{
    /// <summary>
    /// 鏄惁鏈夊彲鐢ㄨ闊宠緭鍑猴紙Prism 鍚庣鎴?SAPI 鍏滃簳锛?
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// 褰撳墠娲诲姩鏈楄鍚庣鏄剧ず鍚嶏紙浜夋浮/淇濈泭/璇诲睆/SAPI锛?
    /// </summary>
    string ActiveBackendName { get; }

    /// <summary>
    /// 鏈楄鏂囨湰
    /// </summary>
    Task SpeakAsync(string text);

    /// <summary>
    /// 鏈楄鏂囨湰锛堟帓闃熸ā寮忥級锛氫笉鎵撴柇褰撳墠鏈楄锛岃拷鍔犲埌璇煶闃熷垪鏈熬
    /// </summary>
    Task SpeakQueuedAsync(string text);

    /// <summary>
    /// 鍋滄鏈楄
    /// </summary>
    void Stop();

    /// <summary>
    /// 璁剧疆璇€?
    /// </summary>
    void SetRate(double rate);

    /// <summary>
    /// 璁剧疆璇煶
    /// </summary>
    void SetVoice(string voiceId);

    /// <summary>
    /// 鑾峰彇鍙敤璇煶鍒楄〃
    /// </summary>
    List<VoiceInfo> GetAvailableVoices();
}

/// <summary>
/// 璇煶淇℃伅
/// </summary>
public class VoiceInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Culture { get; set; } = string.Empty;
    public string Gender { get; set; } = string.Empty;
    public int Age { get; set; }
}

/// <summary>
/// 蹇嵎閿鐞嗗櫒鎺ュ彛
/// </summary>
public interface IHotkeyManager
{
    /// <summary>
    /// 娉ㄥ唽蹇嵎閿?
    /// </summary>
    void RegisterHotkey(Hotkey hotkey);

    /// <summary>
    /// 娉ㄩ攢蹇嵎閿?
    /// </summary>
    void UnregisterHotkey(Hotkey hotkey);

    /// <summary>
    /// 娉ㄥ唽鍏ㄥ眬蹇嵎閿?
    /// </summary>
    void RegisterGlobalHotkey(Hotkey hotkey);

    /// <summary>
    /// 娉ㄩ攢鍏ㄥ眬蹇嵎閿?
    /// </summary>
    void UnregisterGlobalHotkey(Hotkey hotkey);

    /// <summary>
    /// 蹇嵎閿寜涓嬩簨浠?
    /// </summary>
    event EventHandler<HotkeyPressedEventArgs>? HotkeyPressed;

    /// <summary>
    /// 妫€鏌ュ揩鎹烽敭鍐茬獊
    /// </summary>
    bool CheckConflict(Hotkey hotkey);

    /// <summary>
    /// 杞藉叆蹇嵎閿厤缃紙缂哄け鍔ㄤ綔鑷姩琛ラ粯璁ゅ€硷級
    /// </summary>
    void LoadHotkeys(IEnumerable<Hotkey> hotkeys);

    /// <summary>
    /// 鑾峰彇鍏ㄩ儴蹇嵎閿?
    /// </summary>
    List<Hotkey> GetAllHotkeys();

    /// <summary>
    /// 鍒濆鍖栧叏灞€蹇嵎閿敞鍐岋紙澶辫触瑙﹀彂 HotkeysRegistrationFailed锛?
    /// </summary>
    void InitializeGlobalHotkeys(IntPtr windowHandle);

    /// <summary>
    /// 閲嶆柊娉ㄥ唽鍏ㄩ儴鍏ㄥ眬蹇嵎閿?
    /// </summary>
    void RefreshGlobalHotkeys();

    /// <summary>
    /// 娉ㄩ攢鍏ㄩ儴鍏ㄥ眬蹇嵎閿?
    /// </summary>
    void UnregisterAllGlobal();

    /// <summary>
    /// 鎸夊叏灞€娉ㄥ唽 id 瑙﹀彂蹇嵎閿紙鐢?WM_HOTKEY 璋冪敤锛?
    /// </summary>
    void RaiseGlobalHotkey(int id);

    /// <summary>
    /// 绋嬪簭鍐呭厹搴曞尮閰嶏紙浠呰繑鍥炲叏灞€娉ㄥ唽澶辫触鐨勭粍鍚堬級
    /// </summary>
    Hotkey? MatchFallback(Key key, ModifierKeys modifiers);

    /// <summary>
    /// 绋嬪簭鍐呭眬閮ㄥ揩鎹烽敭鍖归厤锛堜粎鍖归厤 IsGlobal=false 鐨勫眬閮ㄥ揩鎹烽敭锛屼富绐楀彛鑱氱劍鏃剁敱 PreviewKeyDown 璋冪敤锛?
    /// </summary>
    Hotkey? MatchLocalHotkey(Key key, ModifierKeys modifiers);

    /// <summary>
    /// 鍏ㄥ眬蹇嵎閿敞鍐屽け璐ヤ簨浠?
    /// </summary>
    event EventHandler<HotkeysRegistrationFailedEventArgs>? HotkeysRegistrationFailed;

    /// <summary>
    /// 妫€娴嬬粍鍚堥敭鏄惁宸茶鍏朵粬绋嬪簭鍗犵敤锛堜复鏃舵敞鍐屾帰娴嬶紝鎴愬姛绔嬪嵆娉ㄩ攢锛?
    /// </summary>
    bool IsComboOccupied(Key key, ModifierKeys modifiers);

    /// <summary>
    /// 鍒ゆ柇鎸囧畾鍏ㄥ眬蹇嵎閿綋鍓嶆槸鍚﹀凡鍦ㄧ郴缁熸敞鍐屾垚鍔燂紙鐢ㄤ簬璁剧疆鍒楄〃鈥滃啿绐佲€濇爣璁帮級
    /// </summary>
    bool IsGlobalHotkeyRegistered(Hotkey hotkey);
}

/// <summary>
/// 鍏ㄥ眬蹇嵎閿敞鍐屽け璐ヤ簨浠跺弬鏁?
/// </summary>
public class HotkeysRegistrationFailedEventArgs : EventArgs
{
    /// <summary>
    /// 鏈鍒锋柊涓敞鍐屽け璐ョ殑蹇嵎閿垪琛?
    /// </summary>
    public IReadOnlyList<Hotkey> FailedHotkeys { get; set; } = Array.Empty<Hotkey>();
}

/// <summary>
/// 闊虫簮绠＄悊鏈嶅姟鎺ュ彛
/// </summary>
public interface IMusicSourceService
{
    /// <summary>
    /// 音源列表变化事件（默认/启用/禁用/增删等），供界面刷新音源列表
    /// </summary>
    event EventHandler? SourcesChanged;

    /// <summary>
    /// 脚本调用 lx.send(updateAlert) 时上抛（供界面提示音源更新）
    /// </summary>
    event EventHandler<LxScriptUpdateAlertEventArgs>? ScriptUpdateAlert;

    /// <summary>
    /// 通过脚本源的 lyric 动作解析歌词（失败/无歌词返回 null）
    /// </summary>
    Task<LxLyricRaw?> GetLyricFromScriptsAsync(MusicInfo music);

    /// <summary>
    /// 通过脚本源的 pic 动作解析封面 URL（失败返回 null）
    /// </summary>
    Task<string?> GetPicFromScriptAsync(MusicInfo music);
    /// <summary>
    /// 鎼滅储闊充箰
    /// </summary>
    Task<PagedResult<MusicInfo>> SearchAsync(string query, int page = 1, int pageSize = 20, string searchType = "song", string sourceId = "all");

    /// <summary>
    /// 鑾峰彇闊充箰淇℃伅
    /// </summary>
    Task<MusicInfo?> GetMusicInfoAsync(string musicId, string source);

    /// <summary>
    /// 鑾峰彇鎾斁URL
    /// </summary>
    Task<string?> GetPlayUrlAsync(string musicId, string source, string quality = "128k");

    /// <summary>
    /// 鑾峰彇鎾斁URL锛堝畬鏁存瓕鏇蹭俊鎭紝渚涜剼鏈簮 Jint 鎵ц浣跨敤锛?
    /// </summary>
    Task<string?> GetPlayUrlAsync(MusicInfo music, string source, string quality = "128k");

    /// <summary>
    /// 鑾峰彇鎾斁鍒楄〃锛堝垎绫绘瓕鍗曪級
    /// </summary>
    Task<PagedResult<Playlist>> GetPlaylistsAsync(int page = 1, int pageSize = 20, string category = "鍏ㄩ儴");

    /// <summary>
    /// 鑾峰彇姝屽崟鍒嗙被
    /// </summary>
    Task<List<PlaylistCategory>> GetPlaylistCategoriesAsync(string sourceId = "netease");

    /// <summary>
    /// 鑾峰彇姝屽崟璇︽儏
    /// </summary>
    Task<PagedResult<MusicInfo>> GetPlaylistDetailAsync(string playlistId, int page = 1, int pageSize = 50);
    Task<PagedResult<Playlist>> GetPlaylistsByCategoryAsync(string category, int page = 1, int pageSize = 30, string sourceId = "netease");

    /// <summary>
    /// 鑾峰彇鎺掕姒?
    /// </summary>
    Task<List<Ranking>> GetRankingsAsync(string sourceId = "netease");

    /// <summary>
    /// 鑾峰彇鎺掕姒滆鎯?
    /// </summary>
    Task<PagedResult<MusicInfo>> GetRankingDetailAsync(string rankingId, int page = 1, int pageSize = 50);

    /// <summary>
    /// 鑾峰彇涓撹緫璇︽儏锛堟瓕鏇插垪琛級
    /// </summary>
    Task<PagedResult<MusicInfo>> GetAlbumDetailAsync(MusicInfo album, int page = 1, int pageSize = 50);

    /// <summary>
    /// 鑾峰彇鎸囧畾闊虫簮 API锛堢敤浜庢瓕璇嶇瓑锛?
    /// </summary>
    IMusicApi? GetApi(string id);

    /// <summary>
    /// 浠嶶RL瀵煎叆闊虫簮
    /// </summary>
    Task<MusicSource?> ImportFromUrlAsync(string url, CancellationToken cancellationToken = default);

    /// <summary>
    /// 浠庢枃浠跺鍏ラ煶婧?
    /// </summary>
    Task<MusicSource?> ImportFromFileAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 娴嬭瘯闊虫簮
    /// </summary>
    Task<bool> TestSourceAsync(MusicSource source);

    /// <summary>
    /// 鍚敤闊虫簮
    /// </summary>
    Task<ScriptActivationResult> EnableSourceAsync(MusicSource source, CancellationToken cancellationToken = default);

    /// <summary>
    /// 绂佺敤闊虫簮
    /// </summary>
    void DisableSource(MusicSource source);

    /// <summary>
    /// 鍒犻櫎闊虫簮
    /// </summary>
    void RemoveSource(MusicSource source);

    /// <summary>
    /// 鏇存柊闊虫簮
    /// </summary>
    Task<bool> UpdateSourceAsync(MusicSource source);

    /// <summary>
    /// 鑾峰彇鎵€鏈夐煶婧?
    /// </summary>
    List<MusicSource> GetAllSources();

    /// <summary>
    /// 鑾峰彇榛樿闊虫簮
    /// </summary>
    MusicSource? GetDefaultSource();

    /// <summary>
    /// 璁剧疆榛樿闊虫簮锛堜簰鏂ワ級锛屽苟鎸佷箙鍖?
    /// </summary>
    void SetDefaultSource(string id);

    /// <summary>
    /// 鑾峰彇鎸囧畾闊虫簮鏀寔鐨勯煶璐ㄥ垪琛紙鑴氭湰婧?鍐呯疆婧愬悎骞讹級
    /// </summary>
    Task<List<string>> GetQualitiesForSourceAsync(string sourceId);

    /// <summary>
    /// 閲嶆柊鍔犺浇鑴氭湰闊虫簮锛堢綉缁?鏈湴/鍐呭锛夛紝鍐呯疆婧愮洿鎺ヨ繑鍥?true
    /// </summary>
    Task<bool> ReloadSourceAsync(MusicSource source);

    /// <summary>
    /// 鎸佷箙鍖栧叏閮ㄩ煶婧?
    /// </summary>
    Task PersistSourcesAsync();

    /// <summary>
    /// 鑾峰彇鍚敤鐨勯煶婧?
    /// </summary>
    List<MusicSource> GetEnabledSources();

    /// <summary>
    /// 娓呴櫎缂撳瓨
    /// </summary>
    void ClearCache();
}
