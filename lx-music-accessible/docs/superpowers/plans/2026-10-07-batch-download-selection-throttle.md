# Batch Download Selection And Throttle Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development and superpowers:test-driven-development. Complete the tasks in order. Do not parallelize implementation.

**Goal:** Let users explicitly multi-select songs in the four song lists, batch download only that explicit set through the existing download manager, and throttle serial downloads by configurable inter-song delay and transfer speed without losing accessibility or shutdown safety.

**Approved specification:** `docs/superpowers/specs/2026-10-07-batch-download-selection-throttle-design.md`

**Architecture:** Keep native WinForms `ListBox` / `ContextMenu` / `MenuItem`; extend `DownloadPreferences` and `UserSettings`; retain one serial `DownloadListAsync` loop; add cancellation-aware delay between terminal song states and token-aware transfer pacing only on the audio response body. UI commands pass explicit selections only. All settings continue through `SettingsWorkflow.Edit`, normalization, persistence, and `SettingsChanged`.

**Tech stack:** .NET Framework 4.8, C#, WinForms, xUnit, existing `BackgroundWorkHost`, `SettingsWorkflow`, `DownloadWorkflow`, and `verify.ps1`.

## Repository contract

- Work on the current dedicated branch: `feature/batch-download-selection-throttle`.
- One ticket-owning sub-agent owns all implementation, review fixes, validation, commit creation, issue comment, and issue closure.
- Use red/green/refactor for every behavior task: write the failing test first, run it to prove the specified failure, implement narrowly, rerun, and only then commit.
- Do not replace `ListBox`, `ContextMenu`, or `MenuItem`; do not use `ContextMenuStrip`.
- Do not auto-expand pagination or convert an album/playlist/ranking into a whole-list download.
- Do not use `Thread.Sleep`; every wait accepts and honors a `CancellationToken`.
- Keep the source list focused and selected after a batch command; do not move focus as a background-task side effect.
- Run targeted tests from `D:\000\codex\lx-music-accessible` before each implementation commit.
- Final gate is `.\verify.ps1`; record its exact output line `Executed tests: <count>` and result in the GitHub issue before closure.

## Shared data and invariants

- Song list control names are exactly:
  - `SearchResults`
  - `FavoriteSongs`
  - `CategorySongs`
  - `RankingSongs`
- `WinFormsUi.Songs(ListBox)` keeps its existing fallback semantics for existing single-item commands. Batch download must use a new explicit-selection reader, never that fallback.
- Explicit selections are read as `SelectedItems.OfType<ObjectBox<MusicInfo>>().Select(box => box.Value).ToArray()`.
- A command with fewer than two explicit selections must not call `DownloadListAsync` for batch mode.
- Preferences are read atomically under `_gate` at the start of each batch and passed through the loop.
- `BatchDownloadDelaySeconds` starts after the current task reaches a terminal state and runs only when another song remains and shutdown cancellation is not already requested.
- `DownloadSpeedLimitKbps == 0` means unlimited; otherwise pacing uses `1024` bytes per KB and starts with the first audio byte. Metadata/API calls and lyric calls are not byte-rate limited.
- Every batch announcement must update both TTS and `LiveRegion` through the existing `Announce` path.

## Task 1: Persist and normalize throttling settings

### Red

Create `src/LXMusicAccessible.Tests/SettingsBatchDownloadTests.cs` (not an excluded legacy test file). Define the test-local `RecordingSettingsStore`, `RecordingHotkeyAdapter`, and `CreateWorkflow` helpers used below; the identically named helpers in other test classes are private and cannot be reused. Cover all four cases:

```csharp
[Theory]
[InlineData(-1, 0, 0, 0)]
[InlineData(61, 0, 60, 0)]
[InlineData(2, -1, 2, 0)]
[InlineData(2, 31, 2, 32)]
public async Task Load_clamps_out_of_range_batch_download_values(int storedDelay, int storedSpeed, int expectedDelay, int expectedSpeed)
{
    var store = new RecordingSettingsStore
    {
        Loaded = new UserSettings
        {
            BatchDownloadDelaySeconds = storedDelay,
            DownloadSpeedLimitKbps = storedSpeed
        }
    };
    var workflow = CreateWorkflow(store, new RecordingHotkeyAdapter());

    var loaded = await workflow.LoadAsync(IntPtr.Zero);

    Assert.Equal(expectedDelay, loaded.Settings.BatchDownloadDelaySeconds);
    Assert.Equal(expectedSpeed, loaded.Settings.DownloadSpeedLimitKbps);
}

[Fact]
public async Task Batch_download_preferences_round_trip()
{
    var store = new RecordingSettingsStore();
    var workflow = CreateWorkflow(store, new RecordingHotkeyAdapter());
    await workflow.LoadAsync(IntPtr.Zero);

    var edited = workflow.Edit(settings =>
    {
        settings.BatchDownloadDelaySeconds = 5;
        settings.DownloadSpeedLimitKbps = 128;
    });
    await workflow.SaveAsync(edited.State);
    var restarted = CreateWorkflow(
        new RecordingSettingsStore { Loaded = store.Saved.Single() },
        new RecordingHotkeyAdapter());

    var settings = (await restarted.LoadAsync(IntPtr.Zero)).Settings;
    Assert.Equal(5, settings.BatchDownloadDelaySeconds);
    Assert.Equal(128, settings.DownloadSpeedLimitKbps);
}
```

Run:

```powershell
dotnet test src/LXMusicAccessible.Tests --filter "FullyQualifiedName~SettingsBatchDownloadTests"
```

Expected: new tests fail because `UserSettings.BatchDownloadDelaySeconds` and `UserSettings.DownloadSpeedLimitKbps` do not exist.

### Green

1. In `src/LXMusicAccessible.Models/AppModels.cs`, extend `UserSettings`:

```csharp
/// <summary>批量下载相邻歌曲之间的等待秒数。</summary>
public int BatchDownloadDelaySeconds { get; set; } = 2;

/// <summary>音频下载速度限制；0 表示不限速，非零有效范围为 32 到 100000。</summary>
public int DownloadSpeedLimitKbps { get; set; } = 0;
```

2. In `SettingsWorkflow.Normalize`, clamp both values explicitly:

```csharp
settings.BatchDownloadDelaySeconds = settings.BatchDownloadDelaySeconds switch
{
    < 0 => 0,
    > 60 => 60,
    _ => settings.BatchDownloadDelaySeconds
};
settings.DownloadSpeedLimitKbps = settings.DownloadSpeedLimitKbps switch
{
    <= 0 => 0,
    < 32 => 32,
    > 100000 => 100000,
    _ => settings.DownloadSpeedLimitKbps
};
```

3. In `WinFormsShell.Controls.cs`, locate the existing `ApplyPreferences` call and pass all five preferences:

```csharp
_services.Downloads?.ApplyPreferences(new(
    settings.DownloadLyric,
    settings.DownloadNameOrder,
    settings.DownloadNameIncludeQuality,
    settings.BatchDownloadDelaySeconds,
    settings.DownloadSpeedLimitKbps));
```

### DownloadPreferences shape

In `src/LXMusicAccessible.Services/DownloadWorkflow.cs`, use optional positional fields so existing source/test constructors compile:

```csharp
public sealed record DownloadPreferences(
    bool DownloadLyric,
    DownloadNameOrder NameOrder,
    bool IncludeQualityTag,
    int BatchDownloadDelaySeconds = 2,
    int DownloadSpeedLimitKbps = 0);
```

Normalize values defensively in `ApplyPreferences` or before assignment so a direct API caller cannot install an out-of-range policy.

### Commit

Commit only after the targeted command is green:

```text
feat(settings): normalize batch download throttles
```

## Task 2: Throttle serial batches and audio transfer

### Red

Create `src/LXMusicAccessible.Tests/DownloadThrottleTests.cs` as a newly discovered test file. Do not rely on `DownloadWorkflowTests.cs`, because the test project currently excludes that legacy file. First define and use this internal recording subclass:

```csharp
internal sealed class RecordingThrottle : DownloadThrottle
{
    public List<TimeSpan> Delays { get; } = new();

    public override Task DelayBetweenSongsAsync(
        DownloadPreferences preferences,
        bool hasMoreSongs,
        CancellationToken cancellationToken)
    {
        if (!hasMoreSongs || cancellationToken.IsCancellationRequested)
            return Task.CompletedTask;
        Delays.Add(TimeSpan.FromSeconds(preferences.BatchDownloadDelaySeconds));
        return Task.CompletedTask;
    }

    public override Task PaceTransferAsync(
        DownloadPreferences preferences,
        TimeSpan elapsed,
        long totalBytes,
        CancellationToken cancellationToken)
    {
        Delays.Add(DownloadThrottle.TransferDelay(preferences.DownloadSpeedLimitKbps, elapsed, totalBytes));
        return Task.CompletedTask;
    }
}

[Fact]
public async Task Transfer_delay_uses_1024_bytes_per_kilobyte()
{
    var throttle = new RecordingThrottle();
    var preferences = new DownloadPreferences(
        true,
        DownloadNameOrder.ArtistTitle,
        true,
        2,
        32);

    await throttle.PaceTransferAsync(preferences, TimeSpan.Zero, 1024, CancellationToken.None);

    Assert.Equal(TimeSpan.FromSeconds(1024d / (32 * 1024d)), throttle.Delays.Single());
}

[Fact]
public async Task Transfer_delay_is_reduced_by_elapsed_transfer_time()
{
    var elapsed = TimeSpan.FromMilliseconds(20);
    var delay = DownloadThrottle.TransferDelay(32, elapsed, 2048);

    Assert.Equal(TimeSpan.FromMilliseconds(42.5), delay);
}
```

Add assertions that `TransferDelay` returns `TimeSpan.Zero` for speed 0 and negative bytes, and that a positive `elapsed` greater than the target never returns a negative delay. Run the following and prove it fails before adding production code:

```powershell
dotnet test src/LXMusicAccessible.Tests --filter "FullyQualifiedName~DownloadThrottleTests"
```

### Production design

1. Add this internal policy type to the Services project:

```csharp
internal class DownloadThrottle
{
    public virtual Task DelayBetweenSongsAsync(
        DownloadPreferences preferences,
        bool hasMoreSongs,
        CancellationToken cancellationToken)
    {
        if (!hasMoreSongs || cancellationToken.IsCancellationRequested)
            return Task.CompletedTask;
        var seconds = Math.Max(0, preferences.BatchDownloadDelaySeconds);
        return seconds == 0
            ? Task.CompletedTask
            : Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken);
    }

    public virtual async Task PaceTransferAsync(
        DownloadPreferences preferences,
        TimeSpan elapsed,
        long totalBytes,
        CancellationToken cancellationToken)
    {
        var delay = TransferDelay(preferences.DownloadSpeedLimitKbps, elapsed, totalBytes);
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, cancellationToken);
    }

    internal static TimeSpan TransferDelay(int limitKbps, TimeSpan elapsed, long totalBytes)
    {
        if (limitKbps <= 0 || totalBytes <= 0)
            return TimeSpan.Zero;
        var target = TimeSpan.FromSeconds(totalBytes / (limitKbps * 1024d));
        return target > elapsed ? target - elapsed : TimeSpan.Zero;
    }
}
```

2. Add a single private seam to `DownloadWorkflow`:

```csharp
private readonly DownloadThrottle _throttle;
```

Existing public constructors construct `new DownloadThrottle()`. Add one internal constructor that accepts a `DownloadThrottle`; do not add a second public constructor.

3. In `DownloadHttpToTemporaryFileAsync`, pass the currently active preferences into the method (or as a parameter from the calling path). Start a `Stopwatch` before reading the response body, accumulate `read` after each successful write, and pace cumulative bytes:

```csharp
var clock = Stopwatch.StartNew();
long read = 0;
while ((count = await input.ReadAsync(buffer, 0, buffer.Length, timeout.Token)) > 0)
{
    await output.WriteAsync(buffer, 0, count, timeout.Token);
    read += count;
    await _throttle.PaceTransferAsync(
        preferences,
        clock.Elapsed,
        read,
        timeout.Token);
    progress?.Invoke(read, total);
}
```

This paces the audio body only when actual transfer is ahead of the configured limit. It does not pace source resolution, URL lookup, lyric lookup, or file I/O independently.

4. In `DownloadListAsync`, read preferences under the existing lock before the loop. After each task reaches terminal status, use:

```csharp
if (context.CancellationToken.IsCancellationRequested)
{
    skipped = songs.Length - index - 1;
    break;
}

if (index < songs.Length - 1)
{
    await _throttle.DelayBetweenSongsAsync(
        preferences,
        hasMoreSongs: true,
        context.CancellationToken);
}
```

Keep the existing pre-loop cancellation check so cancellation before a song remains `Skipped`, count a failing song as `Failed`, and continue the loop unless cancellation is requested.

### Red-to-green behavior checks

Add focused assertions for each of the following:

- two songs with delay two seconds produce one between-song delay;
- two songs with delay zero produce no between-song delay;
- cancellation before song two produces no between-song delay and reports song two as skipped;
- one failing song still allows song two to start and the batch result remains partial;
- speed zero does not calculate a transfer delay;
- speed 32 KB/s and 2048 bytes calculate exactly 62.5 ms before any elapsed credit;
- cancellation during a transfer or between-song delay propagates and cleans temporary files through the existing path.

Run:

```powershell
dotnet test src/LXMusicAccessible.Tests --filter "FullyQualifiedName~DownloadThrottleTests"
```

### Commit

```text
feat(download): throttle selected batch downloads
```
## Task 3: Multi-select the four native song lists

### Red

Create `src/LXMusicAccessible.Tests/BatchDownloadSelectionTests.cs` using `WinFormsTestRunner.RunStaAsync` and the existing shell helpers/fakes. Seed fake song rows through the existing test-loading methods or direct `ObjectBox<MusicInfo>` rows. Cover:

1. All four lists report `SelectionMode.MultiSimple`.
2. `Ctrl+Space` toggles only the focused item.
3. `Ctrl+A` selects every song in the focused list.
4. plain `Up`/`Down` moves `SelectedIndex` while `SelectedIndices` remains unchanged;
5. plain `Space` still invokes playback and does not alter selection;
6. `Enter` still activates/play the focused song and `Shift+Enter` still opens lyrics;
7. right-click/menu-key opening leaves `SelectedIndices` unchanged and issues no fake source/download call;
8. context menu text is `批量下载选中歌曲(N)` and is enabled only when `N >= 2`;
9. invoking the batch item passes exactly the explicit selected IDs in displayed order, not the focused fallback;
10. after command dispatch, the source list remains focused and its selected indices are unchanged.

Use a download workflow test double that records `DownloadListRequest`; do not perform HTTP.

### Native behavior implementation

1. In `CreateSearchPage`, `CreatePlaylistPage`, `CreateCategoryPage`, and `CreateRankingPage`, set:

```csharp
SearchResults.SelectionMode = SelectionMode.MultiSimple;
FavoriteSongs.SelectionMode = SelectionMode.MultiSimple;
CategorySongs.SelectionMode = SelectionMode.MultiSimple;
RankingSongs.SelectionMode = SelectionMode.MultiSimple;
```

2. Extend `AttachSongList` so all four lists share one handler. Check modifiers before any unmodified key:

```csharp
list.KeyDown += (_, e) =>
{
    if (HandleSongListSelection(list, e)) return;
    if (await TryActivateLoadMore(list, e)) return; // preserve only where this list already has it
    if (e.Control || e.Alt || e.Shift) return;
    if (e.KeyCode == Keys.Enter)
    {
        e.Handled = true;
        _ = ActivateSong(list);
        return;
    }
    if (e.KeyCode == Keys.Space)
    {
        e.Handled = true;
        TogglePlayback();
    }
};
```

Keep each list's existing `Backspace`, queue-origin, and load-more behavior. The shared helper:

```csharp
private bool HandleSongListSelection(ListBox list, KeyEventArgs e)
{
    if (e.Control && !e.Alt && e.KeyCode == Keys.Space)
    {
        var index = list.SelectedIndex;
        if (index >= 0)
            list.SetSelected(index, !list.GetSelected(index));
        e.Handled = e.SuppressKeyPress = true;
        return true;
    }

    if (e.Control && !e.Alt && !e.Shift && e.KeyCode == Keys.A)
    {
        for (var index = 0; index < list.Items.Count; index++)
            list.SetSelected(index, true);
        e.Handled = e.SuppressKeyPress = true;
        return true;
    }

    if (!e.Control && !e.Alt && !e.Shift && e.KeyCode is Keys.Up or Keys.Down)
    {
        var delta = e.KeyCode == Keys.Up ? -1 : 1;
        var next = Math.Clamp(list.SelectedIndex + delta, 0, list.Items.Count - 1);
        if (next != list.SelectedIndex)
        {
            var selected = list.SelectedIndices.Cast<int>().ToArray();
            list.SelectedIndex = next;
            list.ClearSelected();
            foreach (var index in selected)
                list.SetSelected(index, true);
        }
        e.Handled = e.SuppressKeyPress = true;
        return list.Items.Count > 0;
    }

    return false;
}
```

If native `MultiSimple` behavior or `ObjectBox` focus restoration differs under the new red test, adjust implementation while preserving the observable contract: focus moves, selection stays explicit, and no business action starts.

3. In `AssignSongContextMenu`, replace only the download command text and add a batch command bound to that exact `list` closure:

```csharp
var batch = new MenuItem("批量下载选中歌曲(0)", (_, _) => DownloadExplicitSelection(list));
menu.Popup += (_, _) =>
{
    var count = list.SelectedItems.Count;
    batch.Text = $"批量下载选中歌曲({count})";
    batch.Enabled = count >= 2;
};
menu.MenuItems.Add(batch);
```

Change the existing single-song command from `下载` to `下载当前歌曲`; do not change its shortcut or behavior.

4. Add an explicit selection helper and command:

```csharp
private static IReadOnlyList<MusicInfo> ExplicitSongs(ListBox list) =>
    list.SelectedItems.Cast<object>()
        .OfType<ObjectBox<MusicInfo>>()
        .Select(item => item.Value)
        .Where(song => song is not null)
        .ToArray();

private async void DownloadExplicitSelection(ListBox list)
{
    if (_services.Downloads is not IDownloadWorkflow downloads) return;
    var songs = ExplicitSongs(list);
    if (songs.Count < 2) return;

    var settings = _services.Settings?.Current.Settings;
    var label = list.AccessibleName ?? "选中歌曲";
    var result = await downloads.DownloadListAsync(new(
        songs,
        label,
        settings?.DefaultDownloadQuality ?? "128k"));
    Announce(
        result.Message,
        $"batch-download:{result.Requested}",
        result.Succeeded ? AnnouncementKind.Success : AnnouncementKind.Error);
}
```

If `AccessibleName` values are too short for a screen-reader context, use stable source labels (`搜索结果选中歌曲`, `收藏歌曲选中歌曲`, `分类歌曲选中歌曲`, `排行榜歌曲选中歌曲`) while retaining the approved exact menu text. Do not focus another control while awaiting the result.

### Commit

```text
feat(ui): multi-select and batch download explicit songs
```

## Task 4: Settings controls and accessibility

### Red

Extend `SettingsBatchDownloadTests` with STA UI assertions:

```csharp
var delay = Assert.IsType<NumericUpDown>(Required(form, "批量下载间隔"));
var speed = Assert.IsType<NumericUpDown>(Required(form, "下载限速"));
Assert.Equal(2, delay.Value);
Assert.Equal(0, speed.Value);
Assert.Equal("批量下载间隔（秒）", delay.AccessibleName);
Assert.Equal("下载限速（KB/s，0 表示不限速）", speed.AccessibleName);
Assert.True(delay.TabIndex < speed.TabIndex);
```

Raise `ValueChanged`, assert `_settings.Current.Settings` is updated immediately and the recording store receives persistence. Also assert values are clamped if code assigns an out-of-range value directly.

### Green

1. In `SettingsForm`, add:

```csharp
private readonly NumericUpDown _batchDelay = new()
{
    Minimum = 0,
    Maximum = 60,
    Value = 2,
    DecimalPlaces = 0
};
private readonly NumericUpDown _downloadSpeed = new()
{
    Minimum = 0,
    Maximum = 100000,
    Value = 0,
    DecimalPlaces = 0
};
```

2. Add them to the download page with captions and unique tab order after existing download controls:

```csharp
WinFormsUi.Name(_batchDelay, "批量下载间隔", nextTabIndex);
_batchDelay.AccessibleName = "批量下载间隔（秒）";
WinFormsUi.Name(_downloadSpeed, "下载限速", nextTabIndex + 2);
_downloadSpeed.AccessibleName = "下载限速（KB/s，0 表示不限速）";
```

Place caption `Text` values before the controls and ensure the audit's label/control relationship rules pass. Choose the concrete integer tab indexes according to the current download page ordering; do not duplicate an existing `TabIndex`.

3. Wire immediate application:

```csharp
_batchDelay.ValueChanged += (_, _) =>
{
    if (Apply(settings => settings.BatchDownloadDelaySeconds = (int)_batchDelay.Value))
        Announce($"批量下载间隔已设置为 {_batchDelay.Value} 秒", "settings", AnnouncementKind.Status);
};
_downloadSpeed.ValueChanged += (_, _) =>
{
    var kbps = (int)_downloadSpeed.Value;
    if (Apply(settings => settings.DownloadSpeedLimitKbps = kbps))
        Announce(kbps == 0 ? "下载限速已关闭" : $"下载限速已设置为 {kbps} KB/s", "settings", AnnouncementKind.Status);
};
```

4. In `LoadControls`, guard against `NumericUpDown` bounds and set values only when `_loading` is true:

```csharp
_batchDelay.Value = Math.Clamp(settings.BatchDownloadDelaySeconds, 0, 60);
_downloadSpeed.Value = Math.Clamp(settings.DownloadSpeedLimitKbps, 0, 100000);
```

5. Apply settings changes propagated from another window through the existing `SettingsChanged` path, without recursive announcements when `_loading` is true.

### Accessibility checks

Run and keep green:

```powershell
dotnet test src/LXMusicAccessible.Tests --filter "FullyQualifiedName~WinFormsAccessibilityAuditTests"
dotnet test src/LXMusicAccessible.Tests --filter "FullyQualifiedName~SettingsBatchDownloadTests"
```

Confirm every new actionable control has one concise semantic accessible name, labels mirror runtime text, and status changes have a visual mirror through the existing announcer.

### Commit

```text
feat(settings): expose batch download throttle controls
```

## Task 5: Regression, self-review, and local gate

### Focused regression

Run from repository root:

```powershell
dotnet test src/LXMusicAccessible.Tests --filter "FullyQualifiedName~SettingsBatchDownloadTests|FullyQualifiedName~DownloadThrottleTests|FullyQualifiedName~BatchDownloadSelectionTests"
dotnet test src/LXMusicAccessible.Tests --filter "FullyQualifiedName~Issue72BrowseNavigationTests|FullyQualifiedName~Issue73FocusLyricsChildHotkeyTests|FullyQualifiedName~WinFormsAccessibilityAuditTests"
```

All must pass.

### Self-review checklist

Inspect the full diff and verify:

- [ ] No TODO/TBD/placeholder markers.
- [ ] Only four song-list controls changed to multi-select.
- [ ] Playlist/category/ranking name lists remain single-select.
- [ ] No pagination auto-load or whole-list implicit selection.
- [ ] Batch command cannot fall back to one focused item.
- [ ] Existing single download and external playback commands still work.
- [ ] Playback-mode runtime behavior is untouched by this feature.
- [ ] Shutdown cancellation propagates through delay and transfer waits.
- [ ] Failed/skipped/cancelled/completed counters remain mutually consistent.
- [ ] Announcements have both speech and accessible text mirror.
- [ ] Settings normalize old/invalid JSON at load and persist accepted changes.
- [ ] No `ContextMenuStrip`, custom list subclass, or `Thread.Sleep`.
- [ ] No generated binaries or temporary download files are committed.

### Full local quality gate

Run:

```powershell
.\verify.ps1
```

The run must end with `Local quality gate passed.` Capture the exact `Executed tests: <count>` line. Do not claim success from a prior run.

### Commit and issue closure

If the gate reveals changes, use red/green again and create a focused fix commit. The final implementation commit hash must exist before issue closure.

Comment on the implementation issue with:

- final commit hash;
- changed files;
- exact `.\verify.ps1` result and executed test count;
- selected-song scope, delay, and speed-limit acceptance notes;
- accessibility keyboard/checks performed by automation;
- explicit confirmation that no pagination auto-load or whole-list download was added.

Close the issue only after every acceptance criterion is met and the full gate passes.