# Download Retry And Redownload Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add safe retry/redownload behavior and a focused-task download context menu.

**Architecture:** Extend the download plan with rollback targets and force-lyric behavior, add `RedownloadAsync`, promote replacement files only after success, and change the task context menu to target the focused row only. Toolbar selection behavior remains unchanged.

**Tech Stack:** .NET 48, C# 12, WinForms, XUnit.

## Global Constraints

- Manual redownload and retry always attempt lyrics.
- Old audio/lyrics remain until replacement succeeds.
- Successful replacement removes stale lyrics and backups.
- Failed replacement preserves old audio/lyrics.
- Context menu affects only the focused task; toolbar remains selection-based.
- No parallel downloads or new authenticity probing.
- Existing queue, throttle, timeout, cancellation, and accessibility behavior must not regress.

---

### Task 1: Service Retry And Replacement

**Files:**
- Modify: `lx-music-accessible/src/LXMusicAccessible.Services/DownloadWorkflow.cs`
- Test: `lx-music-accessible/src/LXMusicAccessible.Tests/DeclaredQualityDownloadTests.cs`

**Interfaces:**
- Produces: `Task<bool> RedownloadAsync(DownloadTask task, string? targetDirectory = null, CancellationToken cancellationToken = default)`.
- Produces: replacement plan targets and forced lyric retry.
- Produces: successful replacement without duplicate `(1)` final files.

- [ ] Step 1: Add failing tests for retry/redownload replacement and failed rollback.
- [ ] Step 2: Run focused tests and verify RED.
- [ ] Step 3: Extend download plan and replacement commit logic.
- [ ] Step 4: Run focused tests and verify GREEN.
- [ ] Step 5: Commit.

### Task 2: Focused Context Menu

**Files:**
- Modify: `lx-music-accessible/src/LXMusicAccessible.App/WinFormsToolForms.cs`
- Test: `lx-music-accessible/src/LXMusicAccessible.Tests/DownloadManagerLifecycleTests.cs`

**Interfaces:**
- Produces: `_tasks.ContextMenu`.
- Produces: focused-task helpers for redownload/retry/pause/resume/stop/remove.
- Consumes: `IDownloadWorkflow.RedownloadAsync`.

- [ ] Step 1: Add failing tests that context actions target only the focused task.
- [ ] Step 2: Run focused tests and verify RED.
- [ ] Step 3: Assign the context menu, set focus on right-click, and use focused-task helpers.
- [ ] Step 4: Run focused tests and verify GREEN.
- [ ] Step 5: Commit.

### Task 3: Module Audit

**Files:**
- Modify: production/tests only where audit exposes a defect.
- Test: download workflow, lifecycle, manager, and accessibility suites.

- [ ] Step 1: Audit single, batch, album, retry, redownload, pause/resume, cancel, delete, timeouts, and event cleanup.
- [ ] Step 2: Fix any verified defects with regression tests.
- [ ] Step 3: Run all focused download suites and verify GREEN.
- [ ] Step 4: Commit fixes.

### Task 4: Final Verification

- [ ] Step 1: Run full test suite.
- [ ] Step 2: Run release verification (`verify.ps1`).
- [ ] Step 3: Commit documentation/status updates.
