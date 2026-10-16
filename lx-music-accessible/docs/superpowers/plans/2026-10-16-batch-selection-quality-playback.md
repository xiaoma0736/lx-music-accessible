# Batch Selection And Quality Playback Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move explicit multi-selection into an accessible batch-download window, restore predictable Space playback in song lists, guarantee append focus, and make source/quality detection match real playback.

**Architecture:** Keep the WinForms shell as the browsing and playback host, but add a small owned `BatchSelectionForm` for explicit checkbox selection. Replace ad-hoc append-focus code with one shell helper. Keep `ManualSourceSwitchForm` as the source-switch surface while expanding its workflow result to carry every declared source/quality candidate with a playability state. Keep quality probing behind the existing `IQualityCandidateSource` / `QualityProbeWorkflow` boundary.

**Tech Stack:** .NET Framework 4.8 WinForms, C# compatible with the repository's net48 settings, xUnit, existing DI and workflow seams.

## Global Constraints

- Do not add NuGet packages or a general service locator.
- All focusable controls need concise `AccessibleName` values.
- Selection changes, focus movement, and menu expansion must not start network requests, playback, or downloads.
- Ordinary song lists use `SelectionMode.One`; only `BatchSelectionForm` represents explicit multi-selection.
- In ordinary song lists, unmodified `Space` performs the playback toggle described in the specification.
- In `BatchSelectionForm`, unmodified `Space` only changes the focused checkbox.
- Album, playlist, ranking, and saved-playlist batch downloads continue to use `DefaultDownloadQuality`.
- Do not claim a built-in quality that has no implemented URL mapping.
- Final validation is `powershell -ExecutionPolicy Bypass -File verify.ps1`.

## File Structure

- Modify `src/LXMusicAccessible.App/WinFormsShell.Controls.cs`: make the four browsing song lists single-select.
- Modify `src/LXMusicAccessible.App/WinFormsShell.Workflows.cs`: centralize Space handling, append focus, and batch-window opening.
- Create `src/LXMusicAccessible.App/BatchSelectionForm.cs`: owned accessible multi-select/download window.
- Modify `src/LXMusicAccessible.Services/ManualSourceSwitchWorkflow.cs`: expand candidate model and parallelize declared-quality resolution.
- Modify `src/LXMusicAccessible.App/WinFormsToolForms.cs`: render playability state and prevent unavailable exact playback activation.
- Extend `src/LXMusicAccessible.Tests/BatchDownloadSelectionTests.cs`, `ManualSourceSwitchWorkflowTests.cs`, paging tests, and quality tests.
- Keep quality and playback seams intact; change `MusicSourceService`, `QualityProbeWorkflow`, or `MusicPlayerService` only when an audit proves a concrete mismatch.

---

### Task 1: Single-select song lists and Space playback

**Files:**
- Modify: `src/LXMusicAccessible.App/WinFormsShell.Controls.cs`
- Modify: `src/LXMusicAccessible.App/WinFormsShell.Workflows.cs`
- Modify: `src/LXMusicAccessible.Tests/BatchDownloadSelectionTests.cs`

**Interfaces:**
- Produces: `private bool HandleSongListSpace(ListBox list, KeyEventArgs e)`; returns true only for unmodified Space.
- Consumes existing `TogglePlayback()` and `ActivateSong(ListBox, PlaybackQueueOrigin)`.

- [ ] **Step 1: Write failing tests.** Replace the old multi-selection assertions with: all four song lists equal `SelectionMode.One`; `Space` with a playing `PlaybackSpy` sets `Handled`, increments `PauseCount`, and does not call `PlayAsync`; no-current Space starts the focused song exactly once and does not call ReplayAsync. Use existing `CreateShell`, `RequiredListBox`, `FillSongs`, and `RaiseKeyDown` helpers. Extend `PlaybackSpy` with `PauseCount` and `ResumeCount` where needed.
- [ ] **Step 2: Run RED.** `dotnet test src\LXMusicAccessible.Tests\LXMusicAccessible.Tests.csproj --configuration Release --filter "FullyQualifiedName~BatchDownloadSelectionTests"`; expected failure is `MultiSimple`/swallowed Space, not compilation noise.
- [ ] **Step 3: Implement.** Change `SearchResults`, `FavoriteSongs`, `CategorySongs`, and `RankingSongs` to `SelectionMode.One`. Replace `HandleSongListSelection` with:

```csharp
private bool HandleSongListSpace(ListBox list, KeyEventArgs e)
{
    if (e.Control || e.Shift || e.Alt || e.KeyCode != Keys.Space) return false;
    e.Handled = true;
    if (_services.Playback?.CurrentMusic is not null)
    {
        TogglePlayback();
        return true;
    }

    var song = FocusedItem<MusicInfo>(list);
    if (song is not null) _ = ActivateSong(list);
    return true;
}
```

Call it first in every main and imported song-list `KeyDown` handler. Preserve Enter, Shift+Enter, Backspace, load-more, Home/End, and arrows as native single-select behavior. Remove `_selectionAnchors`, `Ctrl+A`, `Ctrl+Space`, and Shift-arrow multi-select code after all references/tests are updated.
- [ ] **Step 4: Run GREEN.** Run the Task 1 filter plus `"FullyQualifiedName~Hotkey|FullyQualifiedName~WindowShortcut"`; both pass.
- [ ] **Step 5: Commit.** `git commit -m "feat(ui): restore song-list space playback"` with the three changed paths.

---

### Task 2: Accessible batch selection window

**Files:**
- Create: `src/LXMusicAccessible.App/BatchSelectionForm.cs`
- Modify: `src/LXMusicAccessible.App/WinFormsShell.Workflows.cs`
- Modify: `src/LXMusicAccessible.Tests/BatchDownloadSelectionTests.cs`

**Interfaces:**
- Produces `internal sealed class BatchSelectionForm : Form` with constructor `(IAccessibleAnnouncer announcer, IReadOnlyList<MusicInfo> songs, string sourceTitle, Func<IReadOnlyList<MusicInfo>, Task> download)`.
- Produces shell methods `OpenBatchSelection(ListBox list)`, `BatchSelectionTitle(ListBox list)`, and `DownloadExplicitSelectionAsync(IReadOnlyList<MusicInfo> songs, string label, string quality)`.

- [ ] **Step 1: Write failing tests.** Add tests for: checked songs 2 and 4 produce one `DownloadListRequest` containing exactly `song-1` and `song-3`; request quality equals `DefaultDownloadQuality`; status text is `已选择 2 首`; Space in the batch list checks the focused item and does not call playback; context menu contains `批量选择下载` and no `批量下载选中歌曲(` item; closing restores focus to the source list. Add internal-only adapters (`ItemsForTests`, `StatusText`, `SetItemChecked`, `DownloadCheckedForTests`, `OpenBatchSelectionForTests`, `OwnedBatchForm`) under the project's existing visible-internals convention.
- [ ] **Step 2: Run RED.** Use the Task 2 filtered test command; expected failure is missing `BatchSelectionForm`/`OpenBatchSelection`.
- [ ] **Step 3: Implement the form.** Use a native `CheckedListBox` named `BatchSelectionSongs`, `AccessibleName="批量下载歌曲"`, `CheckOnClick=true`; labels/buttons named “已选择歌曲数量”, “下载选中歌曲”, “全选”, “清空”, “关闭”. Display `Artist，Title，Album`. Do not set `AcceptButton`; Enter must not download. Space uses native `CheckedListBox` checking; `Ctrl+A` checks all; Escape closes; status updates through `ItemCheckEventArgs` and batch update suppression. Download method returns immediately after collecting checked `MusicInfo` values and invoking the injected callback; empty selection reports “没有选中歌曲” and performs no request.
- [ ] **Step 4: Wire the shell.** Remove the old direct batch menu item. Add `批量选择下载`, enabled only when `WinFormsUi.Songs(list).Count > 0`, and call `OpenBatchSelection(list)`. Track one `_batchSelection`; close/reuse rather than opening duplicates. Create the request with `DefaultDownloadQuality`, `BatchDownloadLabel`, and existing `DownloadListAsync`; announce only one result and show errors through `WinFormsUi.Error`. On `FormClosed`, restore focus to the requester when it owned focus.
- [ ] **Step 5: Run GREEN.** Filter `"FullyQualifiedName~BatchDownloadSelectionTests|FullyQualifiedName~WinFormsAccessibilityAudit"`; all pass.
- [ ] **Step 6: Commit.** `git commit -m "feat(downloads): add accessible batch selection window"`.

---

### Task 3: Unified first-appended-song focus

**Files:**
- Modify: `src/LXMusicAccessible.App/WinFormsShell.Workflows.cs`
- Modify: `src/LXMusicAccessible.Tests/WinFormsPlaylistPagingTests.cs`
- Modify existing album/search append tests: `WinFormsPlaylistPagingTests.cs`, `Issue76SearchLoadMoreCaretTests.cs`, and `BatchDownloadSelectionTests.cs`.

**Interfaces:**
- Produces: `private static void FocusFirstAppendedSong(ListBox list, int previousSongCount, IReadOnlyList<string>? preservedSongIds = null)`.

- [ ] **Step 1: Write failing tests.** For saved-playlist, category-song, ranking-song, and album append, capture the prior song count, load one more page, and assert both `SelectedIndex` and caret/focused index equal the prior count. Assert explicitly selected old songs remain selected. Assert reset, failure, and superseded paths do not move focus.
- [ ] **Step 2: Run RED.** `dotnet test src\LXMusicAccessible.Tests\LXMusicAccessible.Tests.csproj --configuration Release --filter "FullyQualifiedName~WinFormsPlaylistPaging|FullyQualifiedName~Issue76|FullyQualifiedName~BatchDownloadSelectionTests"`; expected failures are stale carets/focus.
- [ ] **Step 3: Implement one helper.** Add exactly this helper next to the restore helpers:

```csharp
private static void FocusFirstAppendedSong(
    ListBox list,
    int previousSongCount,
    IReadOnlyList<string>? preservedSongIds = null)
{
    if (previousSongCount < 0 || previousSongCount >= list.Items.Count) return;
    var index = previousSongCount;
    if (preservedSongIds is not null && preservedSongIds.Count == 0)
        WinFormsUi.SelectOnly(list, index);
    else
        list.SetSelected(index, true);
    SetListCaretIndex(list, index);
    if (!list.IsDisposed && list.CanFocus) list.Focus();
}
```

Apply after successful append only: `SearchAsync(false)` (replacing its inline block), `LoadMoreAlbumSongsAsync`, `LoadMoreSavedSongsAsync`, `LoadCategorySongsAsync(..., append: true)`, and `LoadRankingSongsAsync(..., append: true)`. Capture the old count before awaiting the page. Do not call the helper on reset, failed, superseded, or no-new-item paths.
- [ ] **Step 4: Run GREEN.** Repeat the Task 3 filter; all pass.
- [ ] **Step 5: Commit.** `git commit -m "fix(ui): focus first appended song consistently"`.

---

### Task 4: Enumerate every declared source/quality for source switching

**Files:**
- Modify: `src/LXMusicAccessible.Services/ManualSourceSwitchWorkflow.cs`
- Modify: `src/LXMusicAccessible.App/WinFormsToolForms.cs`
- Modify: `src/LXMusicAccessible.Tests/ManualSourceSwitchWorkflowTests.cs`
- Compile fixes: `src/LXMusicAccessible.Tests/ManualSourceExactPlaybackTests.cs` if the record changes require them.

**Interfaces:**
- Extend `ManualSourceOption` with `public bool IsPlayable { get; init; } = true;` and `public string? StateMessage { get; init; }`.
- Add `ManualSourceOption.Unavailable(string sourceId, string sourceName, string platform, MusicInfo candidate, string quality, string stateMessage)`.
- `ManualSourceDiscoveryResult.Options` contains playable and unavailable candidates.
- `IManualSourceDiscoveryWorkflow.PlayAsync` rejects unavailable options before contacting playback.

- [ ] **Step 1: Write failing tests.** Use the existing `RecordingGateway`: source A declares 320k/128k and source B declares 128k/flac; only source A 320k resolves. Assert all four candidates are present in source order and descending quality rank, with exact `IsPlayable` values. Use a `CountdownEvent(3)` plus one resolve to prove all qualities of a three-quality source start before any finishes. Assert unavailable option `PlayAsync` returns `PlaybackResultStatus.Failed` and records zero exact playback requests.
- [ ] **Step 2: Run RED.** `dotnet test src\LXMusicAccessible.Tests\LXMusicAccessible.Tests.csproj --configuration Release --filter "FullyQualifiedName~ManualSourceSwitchWorkflowTests|FullyQualifiedName~ManualSourceExactPlaybackTests"`; expected failures are missing candidates, bad ordering, and missing unavailable guard.
- [ ] **Step 3: Implement parallel all-candidate discovery.** Distinct the interface's declared qualities, `Task.WhenAll` their `ResolveExactAsync` probes, preserve each exception type as `StateMessage`, and map non-empty URLs to `ManualSourceOption.Create`; map empty URLs/errors to `ManualSourceOption.Unavailable`. Preserve OperationCanceledException. Remove sequential quality awaiting. Order by source ID, then `QualityCatalog.Rank(option.Quality)` descending, then quality ID. Raise `DefaultBudget` from 11 seconds to 30 seconds while retaining cancellation propagation.
- [ ] **Step 4: Guard exact playback.** In `ManualSourceDiscoveryWorkflow.PlayAsync`, before playback, return `PlaybackResultStatus.Failed` with `StateMessage` or “所选音源音质不可用” when `request.Option.IsPlayable == false`. In `ManualSourceSwitchForm`, render unavailable text as `音源，音质，不可用，状态，歌曲，歌手，平台`; on activation, show the state and return before `PlayAsync`. Selectable focus may remain for review, but activation of an unavailable option performs no playback.
- [ ] **Step 5: Run GREEN.** `dotnet test src\LXMusicAccessible.Tests\LXMusicAccessible.Tests.csproj --configuration Release --filter "FullyQualifiedName~ManualSource|FullyQualifiedName~Quality"`; all pass.
- [ ] **Step 6: Commit.** `git commit -m "feat(playback): expose all source quality candidates"`.

---

### Task 5: Current-source quality probe and playback audit

**Files:**
- Modify only for proven defects: `src/LXMusicAccessible.Services/MusicSourceService.cs`, `QualityProbeWorkflow.cs`, `MusicPlayerService.cs`, and the relevant `src/LXMusicAccessible.Core/MusicApi/*.cs`.
- Modify: `src/LXMusicAccessible.Tests/QualityCandidateSourceTests.cs`, `QualityProbeWorkflowTests.cs`, `QualitySelectionRegressionTests.cs`.

**Interfaces:**
- Consumes existing `IQualityCandidateSource.GetCandidatesAsync`, `QualityProbeWorkflow.ProbeAsync`, `PlaybackRequest.ForceResolve`, `MusicQuality.AcceptsDownload`, and `MusicQuality.AcceptsPlayback`.

- [ ] **Step 1: Add regression tests.** Assert builtin candidates all have `InterfaceId == music.Source`; scripts without the target platform/quality map produce none; a candidate resolving to lower actual media than requested is `QualityAvailability.Unavailable`; a playback load whose `GetActualQuality()` is below the requested quality fails and does not start audio.
- [ ] **Step 2: Run current quality suites.** `dotnet test src\LXMusicAccessible.Tests\LXMusicAccessible.Tests.csproj --configuration Release --filter "FullyQualifiedName~QualityCandidateSourceTests|FullyQualifiedName~QualityProbeWorkflowTests|FullyQualifiedName~QualitySelectionRegressionTests"`. If regression tests already pass, they are guards; do not invent a failure.
- [ ] **Step 3: Audit before changing production code.** Record in the implementation issue a table for netease, qq, kugou, kuwo, migu, and every loaded script family. For each source record: declared qualities from `GetSupportedQualities` or `QualityMap`, URL mapping actually implemented, probe behavior, `AcceptsDownload`/`AcceptsPlayback` behavior, and the concrete action taken. Never add a quality declaration unless its URL mapping is implemented and tested; if an interface intentionally supports only 128k, retain only 128k.
- [ ] **Step 4: Fix proven mismatches only.** One mismatch gets one failing test first. Fix cross-source candidates in `MusicSourceService`; lower-than-request probe acceptance in `QualityProbeWorkflow`/`MusicQuality`; lower-than-request playback in `MusicPlayerService.TryLoadAsync`; or a stale unsupported declaration. Then run `"FullyQualifiedName~Quality|FullyQualifiedName~Playback"`; all pass.
- [ ] **Step 5: Commit.** Production fixes use `fix(playback): align quality detection with real playback`; if only guards were added, use `test(playback): guard current-source quality behavior`.

---

### Task 6: Documentation and full gate

**Files:**
- Modify: `README.md`, `KEYBOARD_NAVIGATION_GUIDE.md`, `src/LXMusicAccessible.App/Docs/使用说明.md`.

- [ ] **Step 1: Update user behavior docs.** State that song lists are single-select; ordinary song-list `Space` toggles current playback, resumes paused current music, or starts the focused song when nothing is current; batch selection lives in the separate “批量选择下载” window and Space checks there; append moves the reading cursor to the first new song; source switching lists all enabled source/quality candidates with state; current-source quality probing only reports the current platform. Remove all stale `Ctrl+Space` batch-selection guidance.
- [ ] **Step 2: Run text checks.** `git diff --check` and `powershell -ExecutionPolicy Bypass -File scripts\check-text.ps1`; both exit 0.
- [ ] **Step 3: Full gate.** Run `powershell -ExecutionPolicy Bypass -File verify.ps1`. Restore, dependency audit, Release build, all tests, text checks, source checks, release/installer verification, and smoke test must pass. Record exact TRX executed count.
- [ ] **Step 4: Final self-review.** Review `git diff origin/fix/search-loadmore-first-new-item...HEAD` against every section of `docs/superpowers/specs/2026-10-16-batch-selection-quality-playback-design.md`; check no stale old batch menu, no stray multi-selection handler, and no stale keybinding. Commit documentation as `docs(ui): document batch selection and quality playback`.

## Acceptance Summary

- Four browsing lists and imported song lists retain one playing/reading cursor while batch selection is isolated.
- Ordinary song-list Space toggles playback; batch-window Space checks the focused song only.
- Every song append path moves the caret to the first new song.
- Source switching reports every enabled interface's declared quality with reliable playability state and exact playback.
- Current-source playback quality probing remains bounded to the selected song's platform.
- `verify.ps1` passes with the exact test count recorded in the implementation issue.
