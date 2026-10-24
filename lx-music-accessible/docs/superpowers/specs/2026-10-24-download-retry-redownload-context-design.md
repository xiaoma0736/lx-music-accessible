# Download Retry, Redownload, And Context Menu Design

## Background

The download manager already has toolbar actions for selected tasks, and failed/cancelled tasks can retry. The task context menu exists but does not clearly target the focused item, completed songs cannot be manually redownloaded, and retry behavior is not explicit about replacing old audio and lyrics.

Users need predictable recovery after failed downloads and a safe way to redownload completed songs. Old files must not be lost while the replacement download is still running.

## Goals

1. Failed or cancelled download tasks can retry both audio and lyrics.
2. Completed or terminal tasks can be manually redownloaded.
3. Manual redownload replaces the previous audio and lyric files only after the new download succeeds.
4. The task list context menu always targets the focused task where the cursor/keyboard focus stays.
5. The context menu exposes common actions: manual redownload, retry, pause, resume, stop, and remove.
6. Every download workflow action remains keyboard and screen-reader usable.

## Non-Goals

- No parallel downloads.
- No media authenticity probing.
- No partial lyric-only replacement command in this iteration.
- No changes to anti-ban timing beyond the existing global queue and throttle.

## Behavior

### Retry

- Failed and cancelled tasks remain retryable.
- Retry reuses the original song, requested quality, target directory, and preferences.
- Retry always attempts audio and lyric download again, regardless of whether a previous attempt timed out on lyrics.
- Existing final files are treated as rollback targets until the new download completes successfully.
- On success, new audio replaces the previous audio; a newly downloaded lyric replaces the previous lyric.
- If no previous file exists, the task uses the normal conflict-safe naming behavior.

### Manual Redownload

- Completed, failed, and cancelled tasks with a saved plan are manually redownloadable.
- Manual redownload is explicit and separate from automatic retry in the context menu.
- It uses the original song, requested quality, target directory, and preferences.
- It always attempts both audio and lyrics.
- The task enters the normal global download queue; it cannot bypass anti-ban controls.
- During download, the old final file remains visible and unchanged.
- After successful completion:
  - the new audio replaces the old audio;
  - the new lyric replaces the old lyric;
  - if no new lyric was obtained, the old lyric is removed so stale lyrics do not remain;
  - backups are deleted.
- If download or replacement fails:
  - the task reports failure;
  - old audio and lyric files remain available;
  - temporary files are deleted.

### Context Menu

The context menu targets exactly the focused `DownloadTask`; selected-but-unfocused tasks are not affected by context-menu commands. Keyboard focus and the focused item determine the target.

Menu entries:

- `手动重新下载`
  - available for terminal tasks with a saved plan;
  - invokes manual redownload.
- `重试下载`
  - available for failed/cancelled tasks;
  - invokes retry.
- `暂停下载`
  - available while downloading.
- `继续下载`
  - available while paused.
- `停止下载`
  - available while pending/downloading/paused.
- `移除下载`
  - available for the focused task;
  - removes only that task and, after confirmation, its saved source files.

Toolbar actions continue to operate on selected tasks for users who deliberately select multiple entries. Context-menu actions intentionally do not expand to all selected tasks.

## Safety And Accessibility

- Context menu uses WinForms `ContextMenu` and `MenuItem`.
- Every menu command is explicit; focus or selection changes never start network work.
- Menu state is updated when the menu opens.
- Disabled state is perceivable through standard WinForms menu behavior.
- After actions, the task list refreshes without moving focus unexpectedly.
- Error and completion outcomes remain announced through the existing accessible announcer.

## Replacement Rollback Model

Manual redownload/retry keeps the following rollback rules:

1. New media and lyrics are written as temporary files first.
2. Old files are renamed to backup paths only after new content is ready.
3. New audio is promoted, then new lyric is promoted.
4. Backups are removed only after promotion succeeds.
5. If promotion fails, backups are restored and the task reports failure.
6. If there was no old file, normal conflict-safe naming applies.

The old file remains playable until the new download reaches the replacement stage.

## Testing Requirements

- Retry failed audio downloads both audio and lyrics.
- Manual redownload replaces old audio and lyric without creating duplicate numbered files.
- Failed replacement preserves old files.
- Lyric-less previous files are removed after a successful replacement that downloads lyrics.
- Context menu targets only the focused task.
- Stop, pause, resume, retry, manual redownload, and focused removal invoke the correct workflow methods.
- Existing batch, album, queue, timeout, cancellation, and accessibility behavior remains covered.
- Full test suite and release verification pass.

## Acceptance Criteria

- A failed task can retry and recover both files.
- A completed task can be manually redownloaded.
- A successful redownload replaces old content and leaves no temporary or backup files.
- A failed redownload leaves the old content usable.
- Context-menu commands cannot accidentally affect another row.
- All download commands are reachable by keyboard and announced accessibly.
