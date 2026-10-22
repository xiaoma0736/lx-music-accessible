# Declared Quality Download Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make quality menus source-declaration-only and make single, batch, and album downloads predictable and accessible.

**Architecture:** Replace media-probing quality detection with a direct transform of `IQualityCandidateSource` declarations. Change the download source seam to resolve declared qualities per song, reject undeclared qualities, and never sniff downloaded audio for quality verification. Remove duplicate current-song download entries and preserve serial batch/album behavior.

**Tech Stack:** .NET 48, C# 12, WinForms, XUnit, Microsoft.Extensions.DependencyInjection.

## Global Constraints

- Do not add or restore the removed local third-party music source integration.
- Quality menus must derive from source-declared qualities only.
- Do not issue HTTP media probes for quality availability.
- Do not verify downloaded media authenticity or actual quality.
- The only current-song download entry is “下载单曲音质”; selecting a quality immediately downloads.
- No automatic quality fallback for remote downloads.
- Keep every menu action keyboard operable and screen-reader announced.
- Follow the existing project patterns and do not restructure unrelated code.
- Run focused tests after each task and the full suite before final verification.

---

### Task 1: Declarative Quality Probe

**Files:**
- Modify: `lx-music-accessible/src/LXMusicAccessible.Services/QualityProbeWorkflow.cs`
- Modify: `lx-music-accessible/src/LXMusicAccessible.App/WinFormsAppHost.cs`
- Modify: `lx-music-accessible/src/LXMusicAccessible.Tests/Infrastructure/TestApplicationHost.cs`
- Modify: `lx-music-accessible/src/LXMusicAccessible.Tests/QualityProbeWorkflowTests.cs`
- Modify: `lx-music-accessible/src/LXMusicAccessible.Tests/QualityCandidateSourceTests.cs`

**Interfaces:**
- Consumes: `IQualityCandidateSource.GetCandidatesAsync(MusicInfo, CancellationToken)`.
- Produces: `QualityProbeWorkflow(IQualityCandidateSource candidateSource)`.
- Produces: `QualityOption.Status = QualityAvailability.Playable` for every declared quality; `ActualQuality` and `DownloadQuality` remain null.

- [ ] **Step 1: Replace probe tests with declaration-only tests**

Rewrite the obsolete HTTP probe tests so a fake candidate source declares `320k`, `flac24bit`, and `flac`. Assert:
- output is ordered by descending `QualityCatalog.Rank`;
- each option is `Playable`;
- interface name is the first candidate by priority;
- `ActualQuality` and `DownloadQuality` are null;
- empty declarations return zero options with a no-declared-quality message;
- a second probe may reuse the cached result;
- an unreachable or throwing HTTP handler can be constructed but must receive zero requests.

- [ ] **Step 2: Run focused tests and verify RED**

Run: `dotnet test lx-music-accessible/src/LXMusicAccessible.Tests --filter "FullyQualifiedName~QualityProbeWorkflowTests|FullyQualifiedName~QualityCandidateSourceTests"`

Expected: compile/test failures because `QualityProbeWorkflow` still requires `HttpClient` and still probes media.

- [ ] **Step 3: Implement declaration-only probe**

Remove HTTP fields, probe budget, media reader, and candidate resolution from `QualityProbeWorkflow`. Implement synchronous semantic probe:
- get candidates for the song;
- normalize and group by quality;
- order groups by descending rank then quality id;
- choose the lowest-priority-number candidate as display interface;
- return each group as `Playable`;
- retain a simple completed-result cache, `GetCached`, `Invalidate`, and `InvalidateAll`.
Update DI registrations to construct `QualityProbeWorkflow` without `HttpClient`.

- [ ] **Step 4: Run focused tests and verify GREEN**

Run: `dotnet test lx-music-accessible/src/LXMusicAccessible.Tests --filter "FullyQualifiedName~QualityProbeWorkflowTests|FullyQualifiedName~QualityCandidateSourceTests"`

Expected: all focused tests pass.

- [ ] **Step 5: Commit**

```powershell
git add -- lx-music-accessible/src/LXMusicAccessible.Services/QualityProbeWorkflow.cs lx-music-accessible/src/LXMusicAccessible.App/WinFormsAppHost.cs lx-music-accessible/src/LXMusicAccessible.Tests/Infrastructure/TestApplicationHost.cs lx-music-accessible/src/LXMusicAccessible.Tests/QualityProbeWorkflowTests.cs lx-music-accessible/src/LXMusicAccessible.Tests/QualityCandidateSourceTests.cs
git commit -m "fix(quality): trust source-declared qualities"
```

### Task 2: Exact-Quality Download Seam

**Files:**
- Modify: `lx-music-accessible/src/LXMusicAccessible.Services/DownloadWorkflow.cs`
- Modify: `lx-music-accessible/src/LXMusicAccessible.Tests/DownloadWorkflowTests.cs`
- Modify: affected download test fakes implementing `IDownloadSource`

**Interfaces:**
- Consumes: `IQualityCandidateSource` declarations through `DownloadSourceAdapter`.
- Produces: `IDownloadSource.GetQualitiesAsync(MusicInfo music, CancellationToken cancellationToken)`.
- Produces: exact-quality behavior with no `BuildQualityOrder` fallback and no downloaded-file quality rejection.

- [ ] **Step 1: Add failing exact-quality download tests**

Update `ControlledDownloadSource` to accept `MusicInfo`. Add tests proving:
- a declared requested quality downloads that exact quality;
- an undeclared requested quality does not call `GetUrlAsync` and fails with “音源未声明所选音质”;
- when the exact URL returns null or HTTP fails, the download fails without trying another quality;
- successful remote download does not reject based on bytes already delivered;
- external playback preparation follows the same exact-declared-quality rule.

- [ ] **Step 2: Run download tests and verify RED**

Run: `dotnet test lx-music-accessible/src/LXMusicAccessible.Tests --filter "FullyQualifiedName~DownloadWorkflowTests|FullyQualifiedName~DownloadWorkflowLifecycleTests|FullyQualifiedName~DownloadThrottleTests"`

Expected: compile/test failures because the download seam is source-string based and still applies fallback/verification.

- [ ] **Step 3: Implement declaration-checked exact download**

Change `IDownloadSource.GetQualitiesAsync` to take `MusicInfo`. Have `DownloadSourceAdapter` select declared qualities from `MusicSourceService` through its `IQualityCandidateSource` implementation, preserving default-source-first behavior and platform fallback. In `DownloadRemoteAsync`, normalize the requested quality, verify declaration, resolve only that quality, and return declaration-derived audio metadata without sniffing. Remove download quality fallback and `MediaQualityMismatchException` from remote download. Apply the same rule in `PrepareExternalPlaybackAsync`.

- [ ] **Step 4: Run download tests and verify GREEN**

Run: `dotnet test lx-music-accessible/src/LXMusicAccessible.Tests --filter "FullyQualifiedName~DownloadWorkflowTests|FullyQualifiedName~DownloadWorkflowLifecycleTests|FullyQualifiedName~DownloadThrottleTests"`

Expected: all focused tests pass.

- [ ] **Step 5: Commit**

```powershell
git add -- lx-music-accessible/src/LXMusicAccessible.Services/DownloadWorkflow.cs lx-music-accessible/src/LXMusicAccessible.Tests
git commit -m "fix(download): download only the declared selected quality"
```

### Task 3: Single-Quality Menu As Sole Current Download

**Files:**
- Modify: `lx-music-accessible/src/LXMusicAccessible.App/WinFormsShell.Controls.cs`
- Modify: `lx-music-accessible/src/LXMusicAccessible.App/WinFormsShell.Workflows.cs`
- Test: `lx-music-accessible/src/LXMusicAccessible.Tests/BatchDownloadSelectionTests.cs`
- Modify: `lx-music-accessible/KEYBOARD_NAVIGATION_GUIDE.md`
- Modify: `lx-music-accessible/README.md`
- Modify: `lx-music-accessible/src/LXMusicAccessible.App/Docs/使用说明.md`

**Interfaces:**
- Consumes: declaration-only `IQualityProbeWorkflow`.
- Produces: menu items for declared qualities that invoke `DownloadQualityAsync(song, quality)` on activation.
- Produces: no `DownloadSelected`, `Shortcut.F2`, or “下载当前歌曲” entry.

- [ ] **Step 1: Add failing menu tests**

In WinForms shell tests, locate the song context menu and assert:
- no item text contains “下载当前歌曲”;
- “下载单曲音质” is present;
- after its Popup completes, it contains only fake declared qualities;
- activating a quality item records exactly one `DownloadRequest` with the selected song and exact quality;
- the Alt play menu has no current-song direct download entry.

- [ ] **Step 2: Run shell menu tests and verify RED**

Run: `dotnet test lx-music-accessible/src/LXMusicAccessible.Tests --filter "FullyQualifiedName~BatchDownloadSelectionTests|FullyQualifiedName~WinFormsShellInteractionTests|FullyQualifiedName~WinFormsShellAdditionalTests"`

Expected: tests fail because direct download entries remain.

- [ ] **Step 3: Remove duplicate entries and simplify labels**

Remove the Alt play-menu direct item and context-menu direct item. Keep only “下载单曲音质”. Since probe results are declarative, render enabled items as `<label>, 可下载, <interface>` and remove actual-quality text. Ensure asynchronous Popup refresh does not move focus and activation remains explicit menu item activation.

- [ ] **Step 4: Update user-facing documentation**

Replace current-song direct download wording with the exact “下载单曲音质 → choose quality → Enter” workflow. Remove `F2` as a download accelerator.

- [ ] **Step 5: Run shell menu tests and verify GREEN**

Run: `dotnet test lx-music-accessible/src/LXMusicAccessible.Tests --filter "FullyQualifiedName~BatchDownloadSelectionTests|FullyQualifiedName~WinFormsShellInteractionTests|FullyQualifiedName~WinFormsShellAdditionalTests"`

Expected: all focused tests pass.

- [ ] **Step 6: Commit**

```powershell
git add -- lx-music-accessible/src/LXMusicAccessible.App/WinFormsShell.Controls.cs lx-music-accessible/src/LXMusicAccessible.App/WinFormsShell.Workflows.cs lx-music-accessible/src/LXMusicAccessible.Tests/BatchDownloadSelectionTests.cs lx-music-accessible/KEYBOARD_NAVIGATION_GUIDE.md lx-music-accessible/README.md "lx-music-accessible/src/LXMusicAccessible.App/Docs/使用说明.md"
git commit -m "fix(ui): make quality submenu the sole song download entry"
```

### Task 4: Audit Batch And Album Downloads

**Files:**
- Modify: `lx-music-accessible/src/LXMusicAccessible.Services/DownloadWorkflow.cs` only if audit exposes a defect
- Modify: `lx-music-accessible/src/LXMusicAccessible.App/WinFormsShell.Workflows.cs` only if audit exposes a defect
- Test: `lx-music-accessible/src/LXMusicAccessible.Tests/SettingsBatchDownloadTests.cs`
- Test: `lx-music-accessible/src/LXMusicAccessible.Tests/BatchDownloadSelectionTests.cs`
- Test: `lx-music-accessible/src/LXMusicAccessible.Tests/DownloadThrottleTests.cs`

**Interfaces:**
- Consumes: `DownloadListAsync(DownloadListRequest, CancellationToken)`.
- Produces: serial batch execution, latest-batch cancellation, complete album paging, and album subdirectory behavior.

- [ ] **Step 1: Write executable audit tests**

Add or strengthen tests for:
- two-song batch creates both tasks and downloads serially;
- song interval occurs after a terminal task before the next task;
- cancellation marks the current task cancelled, skips remaining songs, and reports skipped count;
- starting a second batch cancels the first through the `download.list` slot;
- album loading merges every paged result before one `DownloadListAsync` call;
- album request label and `Subdirectory` both equal the album title;
- repeated activation while album loading does not start duplicate downloads;
- an empty album does not call downloads and gives an accessible error.

- [ ] **Step 2: Run batch/album tests and verify current failures or PASS**

Run: `dotnet test lx-music-accessible/src/LXMusicAccessible.Tests --filter "FullyQualifiedName~SettingsBatchDownloadTests|FullyQualifiedName~BatchDownloadSelectionTests|FullyQualifiedName~DownloadThrottleTests"`

Expected: new assertions expose any existing race, paging, duplicate, or reporting defect. If all pass, record that no production change is needed.

- [ ] **Step 3: Fix only verified defects**

If a test fails, change the smallest production boundary that fixes its root cause. Preserve latest-batch cancellation, explicit user activation, focus stability, and accessible terminal summaries. Do not introduce concurrent song downloads.

- [ ] **Step 4: Run batch/album tests and verify GREEN**

Run: `dotnet test lx-music-accessible/src/LXMusicAccessible.Tests --filter "FullyQualifiedName~SettingsBatchDownloadTests|FullyQualifiedName~BatchDownloadSelectionTests|FullyQualifiedName~DownloadThrottleTests"`

Expected: all focused tests pass.

- [ ] **Step 5: Commit**

```powershell
git add -- lx-music-accessible/src lx-music-accessible/docs
git commit -m "fix(download): stabilize batch and album flows"
```

### Task 5: Cleanup And Full Verification

**Files:**
- Modify: `lx-music-accessible/docs/superpowers/specs/2026-10-22-declared-quality-download-design.md` if wording needs final cleanup
- Audit: all `lx-music-accessible` production, test, docs, and packaging files

**Interfaces:**
- Produces: final branch ready for review and release verification.

- [ ] **Step 1: Remove obsolete behavior references**

Search the project for `HttpClient` usage in `QualityProbeWorkflow`, `MediaQualityMismatchException`, download quality fallback, “下载当前歌曲”, and the removed local source integration. Remove stale code/docs references without deleting historical unrelated specs.

- [ ] **Step 2: Run formatter-safe build**

Run: `dotnet build lx-music-accessible/src/LXMusicAccessible.Tests --no-restore --nologo`

Expected: build succeeds with zero errors.

- [ ] **Step 3: Run full test suite**

Run: `dotnet test lx-music-accessible/src/LXMusicAccessible.Tests --no-build --nologo`

Expected: every test passes.

- [ ] **Step 4: Run release verification**

Run: `pwsh -NoProfile -File lx-music-accessible/build_release.ps1` if the project documents it as the supported release command; otherwise run the repository’s documented verification script.

Expected: build, packaging, and embedded verification complete successfully.

- [ ] **Step 5: Commit remaining changes**

```powershell
git add -- lx-music-accessible
git commit -m "fix(download): finalize declared quality flow"
```

- [ ] **Step 6: Report evidence**

Report branch, commit list, focused results, full test count, packaging artifact, and any remaining known limitations.
