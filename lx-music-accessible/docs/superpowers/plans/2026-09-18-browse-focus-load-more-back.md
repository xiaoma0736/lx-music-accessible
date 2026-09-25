# Browse Focus Load More Back Navigation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans or subagent-driven-development. Steps use checkbox syntax.

**Goal:** Make tab/list keyboard navigation stable, put explicit load-more entries inside paged lists, support Backspace return navigation, and make announcements use TTS without UIA notification duplication.

**Architecture:** Keep state ownership in `WinFormsShell`; add reusable ListBox load-more markers and explicit browse-back handlers. Route shared announcements exclusively through the existing TTS service while retaining live-region text for visual state.

**Tech Stack:** .NET Framework 4.8 WinForms, xUnit, STA UI tests.

## Global Constraints
- Selection or focus movement must not trigger network work; only Enter activates load-more.
- Backspace must not interfere with editing in TextBox controls.
- Every load-more item has a concise accessible label and explicit activation.
- Background list updates must preserve focused list and nearby selection.
- TTS is the announcement output; UIA notification must not be raised.

## Tasks

### Task 1: Announcement channel
- Modify `src/LXMusicAccessible.App/AccessibleSpeechPolicy.cs`: allow all non-empty announcements except `AnnouncementSpeech.None`.
- Modify `src/LXMusicAccessible.App/WinFormsCommon.cs`: always use TTS when available and enabled; never call `IUiaNotifier`/`RaiseAutomationNotification`.
- Update `src/LXMusicAccessible.Tests/Issue71AccessibleWindowAndSpeechTests.cs` to assert zero UIA notifications and TTS calls for both status and lyric messages.

### Task 2: Stable list focus and load-more markers
- Add reusable load-more marker helpers in `WinFormsShell.Workflows.cs`.
- Add Enter handling for load-more markers on search/album, saved playlist songs, category playlists, category songs, and ranking songs.
- Update `UpdateLoadMore` calls to append/remove the in-list marker while retaining the existing button state.
- Test that selecting the marker does not call browse APIs until Enter; Enter calls the matching load-more action once.

### Task 3: Backspace parent navigation
- Add Backspace handling in `ProcessCmdKey` only when a browse ListBox has focus.
- Implement album-to-search, category song-to-playlist, category playlist-to-category, ranking song-to-ranking, and saved song-to-playlist return paths.
- Restore search data from `MusicBrowseWorkflow.CurrentSearch`, focus the parent list, preserve selection, and announce the returned context.
- Test each back path and verify Backspace in a TextBox still edits normally.

### Task 4: Documentation and quality gate
- Update `src/LXMusicAccessible.App/Docs/使用说明.md` for in-list load-more, Backspace, and TTS-only announcement behavior.
- Run `dotnet test`, then `./verify.ps1`.
