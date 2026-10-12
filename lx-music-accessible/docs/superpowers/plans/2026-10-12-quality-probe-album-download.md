# Quality Probe, Playback Detail, And Album Download Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Probe real playable qualities for the selected song, expose them in playback/download menus, correct playback detail source/quality reporting, and download whole albums into album-name folders.

**Architecture:** Add a shared quality catalog and exact candidate enumerator, then a cached on-demand probe workflow. The shell consumes probe snapshots to build accessible WinForms submenus. Music detail and album download use the same source/quality and album-name contracts without changing default settings or persistence.

**Tech Stack:** .NET Framework 4.8, C#, WinForms `ContextMenu`, xUnit, existing `BackgroundWorkHost`, HttpClient Range requests, FLAC parsing.

## Global Constraints

- Do not probe while browsing, selection changes, or focus changes; probe only when a quality menu opens or an explicit action runs.
- Never report a quality playable unless URL resolution and media validation both succeed.
- Keep existing default-quality playback/download behavior working.
- Keep source candidate resolution exact to enabled interfaces that support the song platform; no accidental cross-source fallback.
- Menu updates must marshal back to the UI thread and must not move focus.
- Preserve keyboard/screen-reader access, state text, and deterministic error handling.
- Every production behavior change has a failing test before implementation.
- Final validation must use local `verify.ps1`.

---

### Task 1: Shared Quality Catalog And Ordering

**Files:**
- Modify: `src/LXMusicAccessible.Services/MusicQuality.cs`
- Create: `src/LXMusicAccessible.Services/QualityCatalog.cs`
- Test: `src/LXMusicAccessible.Tests/QualityCatalogTests.cs`

**Interfaces:**
- Produces: `internal static class QualityCatalog`
- Produces: `internal static string Label(string? quality)`
- Produces: `internal static int Rank(string? quality)`
- Produces: `internal static IReadOnlyList<string> Order(IEnumerable<string?> qualities)`
- Produces: `internal static string Normalize(string? quality)`

- [ ] **Step 1: Write failing catalog tests**

Cover canonical IDs (`128k`, `192k`, `320k`, `flac`, `flac24bit`, `hires`, `atmos`, `atmos_plus`, `master`, `jyeffect`, `sky`, `dolby`, `jymaster`, `viper`), Chinese labels, unknown-ID preservation, duplicate normalization, and descending-fidelity ordering.

- [ ] **Step 2: Run catalog tests**

Run: `dotnet test src/LXMusicAccessible.Tests/LXMusicAccessible.Tests.csproj -c Release --filter FullyQualifiedName~QualityCatalogTests`

Expected: compilation fails because `QualityCatalog` does not exist.

- [ ] **Step 3: Implement catalog and unify rank**

Implement labels and rank in `QualityCatalog`. Update `MusicQuality.Rank` to delegate to it. Preserve existing lossless playback/download compatibility. Give extended qualities a deterministic order above standard quality rather than collapsing all to rank 4.

- [ ] **Step 4: Run focused tests**

Run: `dotnet test src/LXMusicAccessible.Tests/LXMusicAccessible.Tests.csproj -c Release --filter "FullyQualifiedName~QualityCatalogTests|FullyQualifiedName~QualitySelectionRegressionTests"`

Expected: all pass.

### Task 2: Exact Source Interface Candidates

**Files:**
- Create: `src/LXMusicAccessible.Services/QualityCandidateSource.cs`
- Modify: `src/LXMusicAccessible.Services/MusicSourceService.cs`
- Modify: `src/LXMusicAccessible.App/WinFormsAppHost.cs`
- Test: `src/LXMusicAccessible.Tests/QualityCandidateSourceTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  internal sealed record SourceQualityCandidate(
      string InterfaceId,
      string InterfaceName,
      int Priority,
      string Quality);
  internal interface IQualityCandidateSource
  {
      Task<IReadOnlyList<SourceQualityCandidate>> GetCandidatesAsync(
          MusicInfo music,
          CancellationToken cancellationToken = default);
      Task<string?> ResolveAsync(
          MusicInfo music,
          SourceQualityCandidate candidate,
          CancellationToken cancellationToken = default);
  }
  ```

- [ ] **Step 1: Write failing candidate tests**

Use fake built-in/script-backed fixtures or a test subclass to prove:
- Only enabled interfaces supporting `music.Source` are returned.
- Hidden/bundled/local validation-only sources are not returned as script candidates.
- Built-in candidates use their declared qualities.
- Script candidates use their exact platform `QualityMap`.
- Exact resolution targets the selected interface ID and quality, not global fallback.

- [ ] **Step 2: Run candidate tests**

Expected: compilation fails because candidate abstractions do not exist.

- [ ] **Step 3: Implement exact enumeration and resolver**

Implement `IQualityCandidateSource` on `MusicSourceService` or through a narrow adapter. For built-ins, call `IMusicApi.GetPlayUrlAsync`. For scripts, call `TryResolveScriptUrlAsync` with the exact script ID, platform, and quality. Register the abstraction in DI.

- [ ] **Step 4: Run focused tests**

Expected: all candidate and existing playback source-safety tests pass.

### Task 3: Cached Real Quality Probe

**Files:**
- Create: `src/LXMusicAccessible.Services/QualityProbeWorkflow.cs`
- Modify: `src/LXMusicAccessible.Services/MusicQuality.cs`
- Modify: `src/LXMusicAccessible.App/WinFormsAppHost.cs`
- Test: `src/LXMusicAccessible.Tests/QualityProbeWorkflowTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  internal enum QualityAvailability { Unknown, Probing, Playable, Unavailable, Failed }
  internal sealed record QualityOption(
      string Quality,
      string Label,
      QualityAvailability Status,
      string? InterfaceName,
      bool IsCurrentPlaybackQuality);
  internal sealed record QualityProbeResult(
      string CacheKey,
      IReadOnlyList<QualityOption> Options,
      string? Message);
  internal interface IQualityProbeWorkflow
  {
      Task<QualityProbeResult> ProbeAsync(
          MusicInfo music,
          CancellationToken cancellationToken = default);
      QualityProbeResult GetCached(MusicInfo music);
      void Invalidate(MusicInfo music);
  }
  ```

- [ ] **Step 1: Write failing probe tests**

Use fake candidates and a fake HTTP handler to cover:
- playable HTTP/audio media,
- HTTP failure,
- short payload rejection,
- FLAC bit-depth detection,
- cache reuse,
- cancellation,
- extended quality labels,
- successful interface name capture.

- [ ] **Step 2: Run probe tests**

Expected: compilation fails because probe workflow does not exist.

- [ ] **Step 3: Implement probe workflow**

Use `BackgroundWorkHost`, bounded Range GET, and a shared audio inspector. Group candidates by normalized quality, sort descending fidelity, probe interface candidates by priority, and cache successful results for 10 minutes. Cache failures only for retry semantics, not as successful results.

- [ ] **Step 4: Extract shared audio inspection**

Move FLAC/container bit-depth logic needed by probe into an internal audio inspector and make download workflow reuse it without changing output.

- [ ] **Step 5: Run probe and download tests**

Expected: all pass.

### Task 4: Quality Menus

**Files:**
- Modify: `src/LXMusicAccessible.App/WinFormsShell.Controls.cs`
- Modify: `src/LXMusicAccessible.App/WinFormsShell.Workflows.cs`
- Modify: `src/LXMusicAccessible.App/WinFormsShell.Services.cs` or the existing shell services seam
- Test: `src/LXMusicAccessible.Tests/QualityMenuTests.cs`

**Interfaces:**
- Consumes: `IQualityProbeWorkflow`
- Produces: dynamic `当前歌曲音质` playback submenu
- Produces: dynamic `下载音质` song context submenu

- [ ] **Step 1: Write failing menu tests**

Cover initial `探测中`, disabled unavailable options, enabled playable options, source/interface text, play click quality, download click quality, stale-song replacement, no-focus movement, and no probe on construction.

- [ ] **Step 2: Run menu tests**

Expected: new tests fail because menus do not exist.

- [ ] **Step 3: Implement UI probe and submenu rebuild**

Add quality workflow to shell services. Build menu state on popup, run probe through background work, marshal rebuilds to UI thread, and disable unavailable entries. Selecting playback quality calls `PlaybackRequest` with that quality; selecting download quality creates a `DownloadRequest` with that quality.

- [ ] **Step 4: Run focused UI tests**

Expected: all pass.

### Task 5: Playback Detail Source And Quality Fix

**Files:**
- Modify: `src/LXMusicAccessible.App/WinFormsShell.Workflows.cs`
- Modify: `src/LXMusicAccessible.App/WinFormsShell.Controls.cs`
- Test: `src/LXMusicAccessible.Tests/PlaybackDetailRegressionTests.cs`

**Interfaces:**
- Produces: `protected static string TranslateSource(string? sourceId)`

- [ ] **Step 1: Write failing detail tests**

Cover `netease/qq/kg/kw/migu` source names, unknown source preservation, stale actual-quality rejection when source differs, and correct actual-quality inclusion when source and ID match.

- [ ] **Step 2: Run detail tests**

Expected: source and stale-match tests fail.

- [ ] **Step 3: Implement localized source and strict active-song match**

Update detail construction to compare `Source` and `Id`, use localized source names, and retain declared quality plus actual quality lines.

- [ ] **Step 4: Run detail tests**

Expected: all pass.

### Task 6: Whole Album Download Folder

**Files:**
- Modify: `src/LXMusicAccessible.App/WinFormsShell.Workflows.cs`
- Test: `src/LXMusicAccessible.Tests/AlbumDownloadTests.cs`

**Interfaces:**
- Consumes: existing `DownloadListRequest(..., string? Subdirectory = null)`
- Produces: `下载整张专辑` context command in album track lists

- [ ] **Step 1: Write failing album tests**

Cover album subdirectory use, safe album-name sanitization, multiple-page loading before download, existing single-page download, and partial-page failure cancellation.

- [ ] **Step 2: Run album tests**

Expected: new tests fail.

- [ ] **Step 3: Implement album download workflow**

When `_openAlbum` is active, add `下载整张专辑`. If `CurrentAlbumPage` total items exceed loaded items, load remaining pages through browse workflow. Create one `DownloadListRequest` with all album songs and `Subdirectory = album.Title`. Use existing safe directory creation and announce the album name.

- [ ] **Step 4: Run focused tests**

Expected: all pass.

### Task 7: Integration Audit And Quality Gate

**Files:**
- Modify only if audit finds a defect.

- [ ] Review quality submenu keyboard order, labels, disabled states, announcements, stale cache, and focus behavior.
- [ ] Review exact source/interface resolution and ensure playback safety tests remain active.
- [ ] Review download default-quality behavior and album subdirectory safety.
- [ ] Run: `dotnet test src/LXMusicAccessible.Tests/LXMusicAccessible.Tests.csproj -c Release`
- [ ] Run: `.\verify.ps1`
- [ ] Expected: build, all discovered tests, packaging, hashes, and installer smoke test pass with zero failures.
