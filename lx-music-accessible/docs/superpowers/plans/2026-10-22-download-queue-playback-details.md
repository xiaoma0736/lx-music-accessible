# Download Queue And Playback Details Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Serialize all download tasks, refresh title and speech on automatic track changes, and show playback/source-interface details in song details.

**Architecture:** Add one process-wide download gate so singles cannot overlap batch/album requests; cancel queued tasks explicitly. Propagate playback trigger, quality, and resolved interface through player state events. Subscribe the shell to playing-state changes and use source declarations to build song details.

**Tech Stack:** .NET 48, C# 12, WinForms, XUnit.

## Global Constraints

- One download operation may transfer at a time.
- Queued and canceled downloads must never remain “等待中”.
- Preserve anti-ban inter-song delay and speed limits.
- Automatic track changes must update the title and announce artist/title.
- Manual playback announcements must not duplicate.
- Song-detail source information comes from declarations/current playback state; no media真实性 probing.
- Keyboard/screen-reader behavior must remain explicit and focus-stable.

---

### Task 1: Global Download Queue

**Files:**
- Modify: `lx-music-accessible/src/LXMusicAccessible.Services/DownloadWorkflow.cs`
- Test: `lx-music-accessible/src/LXMusicAccessible.Tests/DeclaredQualityDownloadTests.cs`

**Interfaces:**
- Produces: global serialization for `DownloadAsync`, `DownloadListAsync`, and retry operations.
- Produces: cancellation while queued sets `DownloadTaskStatus.Cancelled`.

- [ ] Step 1: Add failing tests for queued cancellation and serial execution.
- [ ] Step 2: Run focused tests and verify RED.
- [ ] Step 3: Add a download gate around task execution and queued-cancellation handling.
- [ ] Step 4: Run focused tests and verify GREEN.
- [ ] Step 5: Commit.

### Task 2: Automatic Track Title And Speech

**Files:**
- Modify: `lx-music-accessible/src/LXMusicAccessible.Models/AppModels.cs`
- Modify: `lx-music-accessible/src/LXMusicAccessible.Services/MusicPlayerService.cs`
- Modify: `lx-music-accessible/src/LXMusicAccessible.App/WinFormsShell.Controls.cs`
- Modify: `lx-music-accessible/src/LXMusicAccessible.App/WinFormsShell.Workflows.cs`
- Test: `lx-music-accessible/src/LXMusicAccessible.Tests/CurrentSongQualityMenuTests.cs`

**Interfaces:**
- Consumes: `IPlaybackCoordinator.StateChanged`.
- Produces: `PlayerStateChangedEventArgs.Trigger`.
- Produces: shell playing-state handler that refreshes current song/title and announces auto-continuation once.

- [ ] Step 1: Add failing shell tests for automatic state changes refreshing title and announcing artist/title.
- [ ] Step 2: Run focused tests and verify RED.
- [ ] Step 3: Add trigger metadata and shell playing-state handler.
- [ ] Step 4: Run focused tests and verify GREEN.
- [ ] Step 5: Commit.

### Task 3: Playback Interface Metadata

**Files:**
- Modify: `lx-music-accessible/src/LXMusicAccessible.Services/PlaybackSourceAdapter.cs`
- Modify: `lx-music-accessible/src/LXMusicAccessible.Services/MusicSourceService.cs`
- Modify: `lx-music-accessible/src/LXMusicAccessible.Services/MusicPlayerService.cs`
- Modify: `lx-music-accessible/src/LXMusicAccessible.Models/AppModels.cs`
- Test: `lx-music-accessible/src/LXMusicAccessible.Tests/CurrentSongQualityMenuTests.cs`

**Interfaces:**
- Produces: `PlaybackSourceResolution(Url, InterfaceId, InterfaceName, Quality)`.
- Produces: state event fields `RequestedQuality`, `ActualQuality`, `InterfaceId`, `InterfaceName`.
- Produces: shell fields for current playback quality and interface.

- [ ] Step 1: Add failing tests that state metadata reaches the shell.
- [ ] Step 2: Run focused tests and verify RED.
- [ ] Step 3: Return source metadata from playback resolution and publish it with playing state.
- [ ] Step 4: Run focused tests and verify GREEN.
- [ ] Step 5: Commit.

### Task 4: Song Details Source Information

**Files:**
- Modify: `lx-music-accessible/src/LXMusicAccessible.App/WinFormsShellServices` definitions in `WinFormsShell.Controls.cs`
- Modify: `lx-music-accessible/src/LXMusicAccessible.App/WinFormsAppHost.cs`
- Modify: `lx-music-accessible/src/LXMusicAccessible.App/WinFormsShell.Workflows.cs`
- Test: `lx-music-accessible/src/LXMusicAccessible.Tests/CurrentSongQualityMenuTests.cs`

**Interfaces:**
- Consumes: `IQualityCandidateSource.GetCandidatesAsync`.
- Consumes: current playing state metadata.
- Produces: asynchronous song-detail lines including current interface, requested/actual quality, and per-interface declared qualities.

- [ ] Step 1: Add failing song-detail tests for current and non-current songs.
- [ ] Step 2: Run focused tests and verify RED.
- [ ] Step 3: Inject candidate source and build asynchronous detail lines.
- [ ] Step 4: Run focused tests and verify GREEN.
- [ ] Step 5: Commit.

### Task 5: Regression Verification

- [ ] Run download, playback, shell, and accessibility focused suites.
- [ ] Run full test suite.
- [ ] Run release verification.
- [ ] Commit documentation/plan status updates.
