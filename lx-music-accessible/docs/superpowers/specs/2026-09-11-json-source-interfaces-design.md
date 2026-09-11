# JSON 音源接口设计

## 目标

在不弱化现有安全边界的前提下，让应用导入并执行声明式 JSON 音源，例如
`残像音源v1.0.1.json`。JSON 音源与现有 LX JS 音源共同参与搜索、播放 URL、
歌词和封面解析；原项目的完整 LX JS 音源接口继续由 `LxScriptEngine` 承担。

## 非目标

- 不在 JSON 中执行 JavaScript、表达式或脚本。
- 不用 JSON 生成动态代码。
- 不改变 LX JS 脚本的执行语义。
- 不引入第三方 JSONPath 依赖；首版支持本仓库所需的 JSON Path 子集。

## 数据模型

`LxScriptInfo` 增加来源类型：

```csharp
public enum LxSourceType
{
    Script,
    JsonManifest
}
```

JSON 清单解析为独立的强类型定义，`RawScript` 仍保存原始 JSON 文本，
以便沿用现有 SHA-256、启用状态和持久化流程：

```csharp
public sealed class JsonSourceDefinition
{
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public string Author { get; init; } = "";
    public string Homepage { get; init; } = "";
    public string Description { get; init; } = "";
    public IReadOnlyDictionary<string, JsonSourcePlatform> Platforms { get; init; } =
        new Dictionary<string, JsonSourcePlatform>(StringComparer.OrdinalIgnoreCase);
}

public sealed class JsonSourcePlatform
{
    public JsonSourceOperation? Search { get; init; }
    public JsonSourceOperation? Url { get; init; }
    public JsonSourceOperation? Lyric { get; init; }
    public JsonSourceOperation? Pic { get; init; }
}

public sealed class JsonSourceOperation
{
    public string Method { get; init; } = "GET";
    public string Url { get; init; } = "";
    public IReadOnlyDictionary<string, string> Headers { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, string> Params { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
    public JsonSourceBody? Body { get; init; }
    public IReadOnlyDictionary<string, string> Qualities { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, string> Result { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public sealed class JsonSourceBody
{
    public string Type { get; init; } = "json";
    public IReadOnlyDictionary<string, string> Values { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
```

## 平台和质量映射

清单平台标识映射：

- `qq` -> `qq`
- `wy` -> `netease`
- `kg` -> `kg`
- `kw` -> `kw`
- `mg` -> `migu`

应用质量映射：

- `128k`、`192k` -> `standard`
- `320k` -> `high`
- `flac`、`flac24bit` -> `lossless`

请求 API 时使用 `qualities[standard|high|lossless]` 里的供应商值。
平台没有声明 URL 质量，或缺少当前应用质量映射时，不得尝试该音源。

## 解析和验证

`ScriptMusicSource.Stage*` 在解析 LX JS 前识别 UTF-8 JSON 根对象。
JSON 必须满足：

- 根对象为 object。
- `name` 是非空字符串。
- `version` 是非空字符串；缺失时拒绝而不是默认为 1.0。
- 至少有一个受支持平台。
- 每个平台至少配置 `search`、`url`、`lyric` 或 `pic` 之一。
- 每个操作有绝对 HTTP/HTTPS URL 和 GET/POST 方法。
- 首版支持 GET 和 POST；POST body 支持 `json` 和 `form`。
- 参数和 header 值大小、清单大小沿用现有 `ScriptNetworkClient` 限制。

解析失败返回 `null` 并设置可诊断失败码：

- `json_manifest_invalid`
- `json_manifest_url_invalid`
- `json_manifest_method_unsupported`
- `script_limit_exceeded`
- `script_encoding_invalid`

## 请求模板

占位符支持：

- `{keyword}`：搜索词
- `{page}`：1 起始页码
- `{pageSize}`
- `{sourceId}`
- `{title}`
- `{artist}`
- `{album}`
- `{hash}`
- `{songmid}`
- `{quality}`：供应商质量值
- `{qualityKey}`：standard/high/lossless

GET 的 `params` 使用 URL 编码后生成 query。POST JSON body 输出 JSON 对象；
POST form body 输出 `application/x-www-form-urlencoded`。URL 本身也做必要的
URI 编码，但不得重复编码已合法的 URL 分隔符。

## 响应映射

支持 JSON Path 子集：

- `$.data.songs`
- `$.data.play_url`
- `$.items[*].name`
- 根数组：`$`

路径段支持 object property、数组索引和 `[*]`。首版只需要返回字符串和对象数组。
播放 URL 仍要求 HTTP/HTTPS。

搜索结果映射字段：

- `list`：结果数组
- `title`
- `artist`
- `album`
- `coverUrl`
- `duration`
- `sourceId`

播放 URL 使用 `url`；歌词使用 `lyric`、`tlyric`、`rlyric`、`lxlyric`；
封面使用 `url`。

## 执行组件

新增 `JsonMusicSource`，放在 Core 层：

```csharp
public Task<IReadOnlyList<MusicInfo>> SearchAsync(
    string platform, string query, int page, int pageSize,
    string searchType, CancellationToken cancellationToken);

public Task<string?> GetPlayUrlAsync(
    string platform, string sourceId, string quality,
    CancellationToken cancellationToken);

public Task<JsonSourceLyric?> GetLyricAsync(
    string platform, MusicInfo music,
    CancellationToken cancellationToken);

public Task<string?> GetPicAsync(
    string platform, MusicInfo music,
    CancellationToken cancellationToken);
```

所有请求使用 `ScriptNetworkClient`，继续获得公网目标校验、安全代理、
重定向检查、15 秒网络超时和 2 MB 响应上限。

## 应用集成

- `TestSourceAsync` 对 `JsonManifest` 校验强类型定义，不调用 JS 引擎。
- 启用、禁用、刷新、删除、默认源和持久化沿用现有 `MusicSource` 生命周期。
- 搜索源列表包含启用的 JSON 清单；`all` 时按清单内平台顺序搜索，
  指定平台时只搜索匹配平台。
- 播放 URL 解析按默认源和其他启用源优先级尝试 JSON 音源。
- 歌词按启用 JSON 源优先级尝试。
- 播放 URL 返回后继续执行现有可播放性探测和降级统计。

## 用户界面和辅助功能

- 本地导入筛选器改为 `音源脚本与清单|*.js;*.json|所有文件|*.*`。
- 网络导入不改 URL 输入行为。
- 失败和成功继续通过现有 `IAccessibleAnnouncer` 读屏播报。
- 类型显示继续使用现有“自定义/网络/本地”标签；不新增依赖颜色的状态。

## 测试策略

以 TDD 覆盖：

1. JSON 清单解析元数据、平台、质量和请求模板。
2. 无效根、缺平台、非法 URL 和不支持方法被拒绝。
3. GET 播放请求编码、header、质量和响应路径提取。
4. POST JSON/form 请求。
5. 搜索数组映射和必要字段校验。
6. 歌词和封面路径提取。
7. JSON 与 JS 混合时的优先级和不串平台。
8. 本地导入对话框筛选器和读屏播报保持可用。

最终运行 `./verify.ps1`。
