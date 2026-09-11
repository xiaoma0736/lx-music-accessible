# JSON 音源接口实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 支持导入和执行声明式 JSON 音源，覆盖搜索、播放 URL、歌词和封面，同时保留现有 LX JS 音源能力与安全边界。

**Architecture:** 在 `ScriptMusicSource` 的导入解析层识别 JSON 清单并解析为强类型定义；新增 `JsonMusicSource` 用现有 `ScriptNetworkClient` 发起受限 HTTP 请求；`MusicSourceService` 把 JSON 清单纳入现有搜索、播放、歌词、启用和持久化生命周期。JSON 只作为数据，不执行任何代码。

**Tech Stack:** .NET 8 / WPF / xUnit / System.Text.Json / 现有 `ScriptNetworkClient`。

## Global Constraints

- JSON 永不执行 JavaScript；不使用 Jint/ClearScript 处理 JSON。
- JSON 与 JS 音源都继续使用 `ScriptNetworkClient` 的公网目标、端口、重定向、请求/响应大小和超时约束。
- 只支持 HTTP/HTTPS 接口；播放 URL 也必须为 HTTP/HTTPS。
- 平台和质量不匹配时必须跳过 JSON 音源，禁止跨平台复用歌曲 ID。
- 不添加第三方 JSONPath、HTTP 或脚本依赖。
- 不改变现有 LX JS 接口语义。
- 每个任务遵守测试优先；最终必须通过 `./verify.ps1`。

## 文件结构

- Create: `src/LXMusicAccessible.Core/MusicApi/JsonSourceDefinition.cs` — JSON 清单类型、解析器和失败异常。
- Create: `src/LXMusicAccessible.Core/MusicApi/JsonMusicSource.cs` — JSON 音源运行时。
- Modify: `src/LXMusicAccessible.Core/MusicApi/ScriptMusicSource.cs` — 识别/暂存/注册 JSON 清单，并分发播放请求。
- Modify: `src/LXMusicAccessible.Services/MusicSourceService.cs` — JSON 清单纳入激活、测试、搜索、播放和歌词流程。
- Modify: `src/LXMusicAccessible.App/SourceManagerWindow.xaml.cs` — 本地导入筛选器支持 JSON。
- Test: `src/LXMusicAccessible.Tests/JsonSourceDefinitionTests.cs`
- Test: `src/LXMusicAccessible.Tests/JsonMusicSourceTests.cs`
- Test: `src/LXMusicAccessible.Tests/JsonSourceLifecycleTests.cs`
- Test: `src/LXMusicAccessible.Tests/SourceManagerWindowTests.cs`

---

### Task 1: JSON 清单解析

**Files:**
- Create: `src/LXMusicAccessible.Core/MusicApi/JsonSourceDefinition.cs`
- Modify: `src/LXMusicAccessible.Core/MusicApi/ScriptMusicSource.cs`
- Test: `src/LXMusicAccessible.Tests/JsonSourceDefinitionTests.cs`

**Interfaces:**
- Consumes: `ScriptMusicSource.StageFromContent`, `ScriptMusicSource.LastFailureCode`, `ScriptNetworkClient.MaxScriptBytes`。
- Produces:
  - `LxSourceType Kind`
  - `JsonSourceDefinition? JsonSource`
  - `JsonSourceDefinitionParser.Parse(string content)`
  - `JsonSourceManifestException(string code)`

- [ ] **Step 1: 写失败测试**

新建测试至少包含以下断言，测试样例使用私有常量 `ValidJson`：

```csharp
public sealed class JsonSourceDefinitionTests
{
    private const string ValidJson = """
    {
      "name": "残像音源",
      "version": "1.0.1",
      "author": "残像API",
      "description": "declarative source",
      "platforms": {
        "qq": {
          "search": {
            "method": "GET",
            "url": "https://api.example.test/qq",
            "params": { "msg": "{keyword}" },
            "result": {
              "list": "$.data.songs",
              "title": "$.name",
              "artist": "$.artist",
              "coverUrl": "$.cover",
              "sourceId": "$.song_id"
            }
          },
          "url": {
            "url": "https://api.example.test/qq",
            "params": { "id": "{sourceId}", "br": "{quality}" },
            "qualities": { "standard": "128", "high": "320", "lossless": "flac" },
            "result": { "url": "$.data.play_url" }
          },
          "lyric": {
            "url": "https://api.example.test/qq",
            "params": { "id": "{sourceId}" },
            "result": { "lyric": "$.data.lyrics" }
          }
        },
        "wy": {
          "url": {
            "url": "https://api.example.test/wy",
            "params": { "id": "{sourceId}", "br": "{quality}" },
            "qualities": { "standard": "standard", "high": "exhigh", "lossless": "lossless" },
            "result": { "url": "$.data.url" }
          }
        }
      }
    }
    """;

    [Fact]
    public void Parse_extracts_metadata_platforms_qualities_and_templates()
    {
        var manifest = JsonSourceDefinitionParser.Parse(ValidJson);

        Assert.Equal("残像音源", manifest.Name);
        Assert.Equal("1.0.1", manifest.Version);
        Assert.Equal("残像API", manifest.Author);
        Assert.Equal("declarative source", manifest.Description);
        Assert.Equal(["qq", "netease"], manifest.Platforms.Keys.Select(MapPlatform).ToArray());
        Assert.Equal("https://api.example.test/qq", manifest.Platforms["qq"].Search!.Url);
        Assert.Equal("{keyword}", manifest.Platforms["qq"].Search!.Params["msg"]);
        Assert.Equal("flac", manifest.Platforms["qq"].Url!.Qualities["lossless"]);
    }

    [Fact]
    public void StageFromContent_detects_json_manifest()
    {
        using var source = new ScriptMusicSource(NullLogger<ScriptMusicSource>.Instance);
        var info = source.StageFromContent(ValidJson, "json-source", "fixture", true);

        Assert.NotNull(info);
        Assert.Equal(LxSourceType.JsonManifest, info!.Kind);
        Assert.Equal("残像音源", info.Name);
        Assert.Equal("1.0.1", info.Version);
        Assert.NotNull(info.JsonSource);
        Assert.Equal(["qq", "netease"], info.SupportedSources);
        Assert.Contains("128k", info.QualityMap["qq"]);
        Assert.Contains("flac24bit", info.QualityMap["qq"]);
    }

    [Theory]
    [InlineData("[]", "json_manifest_invalid")]
    [InlineData("""{"name":"x","version":"1.0"}""", "json_manifest_invalid")]
    [InlineData("""{"name":"x","version":"","platforms":{"qq":{"url":{"url":"https://api.example.test/"}}}}""", "json_manifest_invalid")]
    [InlineData("""{"name":"x","version":"1.0","platforms":{"bad":{"url":{"url":"https://api.example.test/"}}}}""", "json_manifest_invalid")]
    [InlineData("""{"name":"x","version":"1.0","platforms":{"qq":{"url":{"url":"ftp://api.example.test/"}}}}""", "json_manifest_url_invalid")]
    [InlineData("""{"name":"x","version":"1.0","platforms":{"qq":{"url":{"method":"TRACE","url":"https://api.example.test/"}}}}""", "json_manifest_method_unsupported")]
    public void Invalid_manifest_is_rejected(string json, string expectedCode)
    {
        var exception = Assert.Throws<JsonSourceManifestException>(
            () => JsonSourceDefinitionParser.Parse(json));
        Assert.Equal(expectedCode, exception.Code);

        using var source = new ScriptMusicSource(NullLogger<ScriptMusicSource>.Instance);
        Assert.Null(source.StageFromContent(json, "bad-json", "fixture", true));
        Assert.Equal(expectedCode, source.LastFailureCode);
    }
}
```

其中 `MapPlatform` 是测试内辅助方法，映射 `wy` 到 `netease`。

- [ ] **Step 2: 运行测试确认失败**

```powershell
dotnet test src/LXMusicAccessible.Tests --filter JsonSourceDefinitionTests
```

预期编译失败，因为 `JsonSourceDefinitionParser`、`LxSourceType` 和 `Kind` 尚不存在。

- [ ] **Step 3: 实现最小解析器**

在 `JsonSourceDefinition.cs` 创建设计文档中的类型、`LxSourceType`、`JsonSourceManifestException` 和 `JsonSourceDefinitionParser.Parse`。解析器使用 `JsonDocument` 读取原始对象，必须验证平台枚举、绝对 HTTP/HTTPS URL、GET/POST、result/path 非空。平台结果返回原始清单键；`ScriptMusicSource` 负责映射为应用平台。失败抛出 `JsonSourceManifestException`，错误码使用设计文档中的固定值。

在 `ScriptMusicSource.ParseLxScript` 开头检测裁剪后的 `RawScript` 是否以 `{` 开始；是则调用解析器，填充 `Kind = JsonManifest`、`Name`、`Version`、`Description`、`JsonSource`、映射后的 `SupportedSources`，并根据每个平台 `Url.Qualities` 填充 `QualityMap`。捕获 `JsonSourceManifestException` 时设置 `LastFailureCode` 并返回 `null`。

- [ ] **Step 4: 运行测试确认通过**

```powershell
dotnet test src/LXMusicAccessible.Tests --filter JsonSourceDefinitionTests
```

预期全部通过。

- [ ] **Step 5: 提交**

```powershell
git add src/LXMusicAccessible.Core/MusicApi/JsonSourceDefinition.cs src/LXMusicAccessible.Core/MusicApi/ScriptMusicSource.cs src/LXMusicAccessible.Tests/JsonSourceDefinitionTests.cs
git commit -m "feat(sources): parse declarative JSON manifests"
```

---

### Task 2: JSON 请求运行时

**Files:**
- Create: `src/LXMusicAccessible.Core/MusicApi/JsonMusicSource.cs`
- Test: `src/LXMusicAccessible.Tests/JsonMusicSourceTests.cs`

**Interfaces:**
- Consumes: `JsonSourceDefinition`, `ScriptNetworkClient`, `MusicInfo`。
- Produces:
  - `JsonMusicSource(JsonSourceDefinition manifest, ScriptNetworkClient networkClient, ILogger? logger = null)`
  - `SearchAsync(string platform, string query, int page, int pageSize, string searchType, CancellationToken)`
  - `GetPlayUrlAsync(string platform, string sourceId, string quality, CancellationToken)`
  - `GetLyricAsync(string platform, MusicInfo music, CancellationToken)`
  - `GetPicAsync(string platform, MusicInfo music, CancellationToken)`
  - `JsonSourceLyric` record

- [ ] **Step 1: 写失败测试**

创建 `JsonMusicSourceTests`，复用 `RejectingHttpMessageHandler` 和公网 DNS stub：

```csharp
private static ScriptNetworkClient CreateNetwork(RejectingHttpMessageHandler handler) =>
    new(handler, (_, _) => Task.FromResult([IPAddress.Parse("93.184.216.34")]));
```

至少覆盖：

1. GET 播放请求：
   - manifest 使用 `https://api.example.test/qq?id={sourceId}&br={quality}`。
   - qualities 为 `standard=128,high=320,lossless=flac`。
   - 调用 `GetPlayUrlAsync("netease", "123", "320k")` 实际请求 query 为 `id=123&br=exhigh`。
   - 响应 `{"data":{"url":"https://cdn.example.test/audio.m4a"}}` 返回该 URL。
2. POST JSON：
   - method 为 POST，body type 为 `json`，参数 `{ "keyword": "{keyword}", "page": "{page}" }`。
   - 请求 content-type 为 `application/json`，body 为包含 keyword/page 的 JSON。
3. POST form：
   - body type 为 `form` 时 content-type 为 `application/x-www-form-urlencoded`，body 包含 URL 编码值。
4. 搜索映射：
   - 响应 `{"data":{"songs":[{"name":"Song","artist":"Artist","song_id":"1","cover":"https://cdn.example.test/a.jpg"}]}}`。
   - 返回一个 `MusicInfo`，`Id == "1"`、`Title == "Song"`、`Artist == "Artist"`、`Source == "qq"`、`CoverUrl` 正确。
5. 歌词和封面：
   - 分别从 `$.data.lyric`、`$.data.tlyric` 和 `$.data.cover` 提取值。
6. 拒绝错误：
   - HTTP 非 2xx 返回空/`null`。
   - 提取的播放 URL 为 `ftp://` 时返回 `null`。
   - 请求私有地址被 `ScriptNetworkClient` 阻断且 handler 无请求。

`JsonSourceLyric` 定义为：

```csharp
public sealed record JsonSourceLyric(
    string Lyric,
    string? Translation,
    string? Romanization,
    string? LxLyric);
```

- [ ] **Step 2: 运行测试确认失败**

```powershell
dotnet test src/LXMusicAccessible.Tests --filter JsonMusicSourceTests
```

预期类型不存在导致编译失败。

- [ ] **Step 3: 实现请求、模板和路径提取**

`JsonMusicSource` 使用 `System.Text.Json`。实现：

- 内部平台键转换为清单键：`netease -> wy`、`migu -> mg`，其余相同。
- `standard/high/lossless` 从应用质量生成；无法映射时返回空结果。
- `BuildRequest` 处理 query、headers、JSON/form body 和占位符；query 值使用 `Uri.EscapeDataString`。
- `SendJsonAsync` 调用 `ScriptNetworkClient.SendAsync`，2xx 时用 strict UTF-8 解析并限制为 `MaxResponseBytes`。
- `SelectPath` 支持 `$`、`.property`、`[index]`、`[*]`；路径无法解析返回 `null`。
- 搜索逐项要求 `sourceId` 和 `title` 非空，不完整项跳过；duration 统一格式化为 `m:ss`。
- 歌词 `lyric` 非空才返回结果。
- 捕获取消并重新抛出；捕获 `ScriptNetworkException` 和 JSON 解析失败后返回空结果。

不要让 `JsonMusicSource` 拥有传入的 `ScriptNetworkClient`。

- [ ] **Step 4: 运行测试确认通过**

```powershell
dotnet test src/LXMusicAccessible.Tests --filter JsonMusicSourceTests
```

预期全部通过。

- [ ] **Step 5: 提交**

```powershell
git add src/LXMusicAccessible.Core/MusicApi/JsonMusicSource.cs src/LXMusicAccessible.Tests/JsonMusicSourceTests.cs
git commit -m "feat(sources): execute declarative JSON APIs"
```

---

### Task 3: 生命周期和播放搜索集成

**Files:**
- Modify: `src/LXMusicAccessible.Core/MusicApi/ScriptMusicSource.cs`
- Modify: `src/LXMusicAccessible.Services/MusicSourceService.cs`
- Test: `src/LXMusicAccessible.Tests/JsonSourceLifecycleTests.cs`

**Interfaces:**
- Consumes: Task 1/2 的 JSON 类型和运行时。
- Produces:
  - `ScriptMusicSource.GetJsonSource(string id)`
  - JSON 音源可导入、启用、持久化、搜索、解析播放 URL 和歌词。

- [ ] **Step 1: 写失败测试**

创建 `JsonSourceLifecycleTests`，使用现有测试宿主与 `RejectingHttpMessageHandler`。至少添加：

1. **导入即启用：** `ImportFromFileAsync` 没有本地文件依赖时可测试 `StageFromContent + EnableSourceAsync` 等价路径；断言 `Kind == JsonManifest`、`IsEnabled`、`TrustState == UserApproved`，且 `GetJsonSource` 非空。
2. **持久化恢复：** 保存后创建第二个 `MusicSourceService`，断言 JSON 源仍启用且可从 `ScriptMusicSource` 获取。
3. **JSON 优先播放：** 注册启用 JSON 源并注册响应 `https://cdn.example.test/audio.mp3`；调用 `GetPlayUrlAsync` 后 handler 收到 JSON API 请求，返回该 URL。使用 `IsPlayableAsync` 可接受的响应时可直接成功；否则注册可播放探测响应。
4. **平台与质量过滤：** JSON 只声明 `wy/url/high` 时，`qq` 请求、`wy/128k` 请求都不会发出 HTTP 请求。
5. **JSON 搜索：** `SearchAsync("Song", sourceId: "qq")` 调用 JSON 清单并把结果 `Source` 设为 `qq`；JSON 源与内置源共存时不会把 JSON 歌曲错误交给内置播放。
6. **歌词集成：** `GetLyricFromScriptsAsync` 返回 JSON 提取的歌词和翻译。

测试 fixture 使用 `https://api.example.test/...` 与公网 DNS stub，不访问真实网络。

- [ ] **Step 2: 运行测试确认失败**

```powershell
dotnet test src/LXMusicAccessible.Tests --filter JsonSourceLifecycleTests
```

预期 `GetJsonSource`、搜索集成或播放集成失败。

- [ ] **Step 3: 注册 JSON 运行时**

`ScriptMusicSource` 增加 `Dictionary<string, JsonMusicSource> _jsonSources`。`RegisterVerified` 对 `Kind == JsonManifest` 创建运行时；`Remove`/`Clear` 同步删除。新增：

```csharp
public JsonMusicSource? GetJsonSource(string id) =>
    _jsonSources.TryGetValue(id, out var source) ? source : null;
```

`GetPlayUrlAsync` 在 JSON 清单上直接调用运行时，不再返回 `null` 后继续 JS 执行。`TryResolveScriptUrlAsync` 中的 JS 引擎 fallback 增加 `script.Kind == LxSourceType.Script` 条件。

- [ ] **Step 4: 集成激活与刷新**

在 `MusicSourceService` 所有调用 `_scriptEngine.TestLoadAsync(candidate)` 的位置，先判断：

```csharp
var manifestValid = candidate.Kind == LxSourceType.JsonManifest;
if (candidate is null
    || (!manifestValid && !await _scriptEngine.TestLoadAsync(candidate, cancellationToken)))
```

实际编码时保留取消语义和现有错误回滚。`TestSourceAsync` 对 JSON 清单直接检查 `_scriptSource.GetScript(s.Id)?.JsonSource is not null`，不调用 JS 引擎。

- [ ] **Step 5: 集成搜索**

修改 `GetSearchSources`：先保留现有内置源行为；当 `sourceId` 为 `all` 或内置平台时，把支持该平台的启用 JSON 清单追加到返回值。`FetchNextSearchRoundAsync` 对 JSON 源调用新私有方法 `SearchJsonSourcePageAsync`：

```csharp
private async Task<PagedResult<MusicInfo>> SearchJsonSourcePageAsync(
    MusicSource source,
    string requestedSourceId,
    string query,
    int page,
    int pageSize,
    string searchType,
    CancellationToken cancellationToken)
```

内部从 `GetJsonSource(source.Id)` 获取运行时；`sourceId == "all"` 时把 `requestedSourceId` 传 `"all"`，否则传 `requestedSourceId`。结果包装为 `PagedResult<MusicInfo>`，失败返回空页。追加 JSON 结果到内置结果之后，继续使用现有去重键。

- [ ] **Step 6: 集成播放和歌词**

播放路径已通过 `ScriptMusicSource.GetPlayUrlAsync` 分发，仍沿用 `TryResolveScriptUrlAsync` 的占位 URL 检查、可播放性探测、失败降级和成功恢复。`GetLyricFromScriptsAsync` 按优先级遍历启用源；对 JSON 清单调用 `GetJsonSource` 并把 `JsonSourceLyric` 映射为 `LxLyricRaw`，否则保持现有 `_scriptEngine.GetLyricAsync`。

- [ ] **Step 7: 运行生命周期测试**

```powershell
dotnet test src/LXMusicAccessible.Tests --filter JsonSourceLifecycleTests
```

预期全部通过。

- [ ] **Step 8: 回归已有音源行为**

```powershell
dotnet test src/LXMusicAccessible.Tests --filter "FullyQualifiedName~ScriptSourceLifecycle|FullyQualifiedName~ScriptSecurity|FullyQualifiedName~Issue49PlaybackSourceSafety|FullyQualifiedName~QualitySelectionRegression|FullyQualifiedName~ManualSource"
```

预期全部通过。

- [ ] **Step 9: 提交**

```powershell
git add src/LXMusicAccessible.Core/MusicApi/ScriptMusicSource.cs src/LXMusicAccessible.Services/MusicSourceService.cs src/LXMusicAccessible.Tests/JsonSourceLifecycleTests.cs
git commit -m "feat(sources): integrate JSON manifests with playback and search"
```

---

### Task 4: 导入界面和最终验证

**Files:**
- Modify: `src/LXMusicAccessible.App/SourceManagerWindow.xaml.cs`
- Modify: `src/LXMusicAccessible.Tests/SourceManagerWindowTests.cs`

**Interfaces:**
- Consumes: `ImportFromFileAsync` 的 JSON 支持与现有 `IAccessibleAnnouncer`。
- Produces: 本地导入可选 `.js` 和 `.json`，成功/失败继续有读屏播报。

- [ ] **Step 1: 写失败 UI 测试**

在 `SourceManagerWindowTests` 增加断言：创建窗口后通过 `AutomationProperties.GetName`/按钮文本访问“导入本地音源”，点击并选择 `.json` fixture 后能调用导入路径并播报成功。若现有测试工具已有 file-dialog seam，使用现有 seam；不要在自动化测试中打开真实模态文件对话框。最少要先添加一个针对筛选器常量/构造参数的确定性测试，验证 filter 包含 `*.js` 和 `*.json`。

- [ ] **Step 2: 运行测试确认失败**

```powershell
dotnet test src/LXMusicAccessible.Tests --filter SourceManagerWindowTests
```

预期 JSON 筛选器断言失败。

- [ ] **Step 3: 更新导入筛选器**

将 `OpenFileDialog.Filter` 改为：

```csharp
Filter = "音源脚本与清单|*.js;*.json|所有文件|*.*"
Title = "导入本地音源脚本或清单"
```

不改变按钮焦点、快捷键、成功/失败播报或窗口关闭焦点恢复。

- [ ] **Step 4: 运行音源界面测试**

```powershell
dotnet test src/LXMusicAccessible.Tests --filter SourceManagerWindowTests
```

预期全部通过。

- [ ] **Step 5: 自审差异**

```powershell
git diff --check
git diff --stat
```

检查无占位符、空 catch、静态服务定位器、真实网络调用或私有地址目标。

- [ ] **Step 6: 最终质量门**

```powershell
./verify.ps1
```

预期 restore、dependency audit、release build、tests、text checks 和 release/installer verification 全部通过，并输出 `Local quality gate passed.`。

- [ ] **Step 7: 提交并记录**

```powershell
git add src/LXMusicAccessible.App/SourceManagerWindow.xaml.cs src/LXMusicAccessible.Tests/SourceManagerWindowTests.cs
git commit -m "feat(sources): allow importing JSON source manifests"
```

在实现 issue 评论中记录 `verify.ps1` 结果和 exact executed test count。
