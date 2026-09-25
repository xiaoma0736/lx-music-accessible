# Accessibility Focus Lyrics Child Hotkeys Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans or subagent-driven-development. Steps use checkbox syntax.

**Goal:** Deliver quiet TTS, non-stealing tab focus, an accessible lyrics viewer, and consistent local hotkeys in child windows.

**Architecture:** Keep navigation ownership in `WinFormsShell`; classify routine announcements in `AccessibleSpeechPolicy`; expose a shared shell hotkey callback to child forms; replace the hidden lyrics TextBox with a visible native TextBox.

**Tech Stack:** .NET Framework 4.8 WinForms, xUnit STA UI tests, existing TTS routing and hotkey settings workflow.

## Global Constraints
- Errors always remain spoken; welcome and routine browse completion/busy messages do not.
- Selection and focus changes do not trigger playback, network work, or file writes.
- Writable text boxes suppress hotkeys; read-only text and lists allow them.
- All actionable controls keep concise semantic accessible names.

## Tasks

### Task 1: Tab focus
- Modify `WinFormsShell.Workflows.cs`: `SelectTab` focuses `MainTabs`, not a page control.
- Remove completion-time focus grabs for first category/ranking loads.
- Tests assert `MainTabs` is `ActiveControl` after selecting playlist/ranking tabs and remains there after loading.

### Task 2: Quiet routine speech
- Modify `AccessibleSpeechPolicy`: deny `welcome`; deny non-error browse categories (`music-browse:*`, `categories`, `rankings`, `search-results`, `browse-status`, `saved-playlists`).
- Keep all errors spoken.
- Update speech tests to expect quiet welcome/browse success and spoken browse errors.

### Task 3: Lyrics viewer and Shift+Enter
- In `WinFormsToolForms.cs`, replace `SilentTextBox` with a visible read-only TextBox named `歌词`; preserve highlight, auto-scroll, copy button and Ctrl+Shift+C.
- Add `OpenLyrics(MusicInfo? music)`; song-list Shift+Enter opens selected song lyrics, falling back to current playback.
- Tests cover accessible text box, copy, Shift+Enter on search list, and fallback/current behavior.

### Task 4: Child local hotkeys
- Add the shell hotkey callback to download manager and playlist import forms; invoke the callback from their `OnKeyDown`.
- Ensure source manager, settings, lyrics and manual source forms already route through the callback and writable editors suppress it.
- Tests assert F5/F6 dispatch through each child form and a writable TextBox retains editing priority.

### Task 5: Docs and quality
- Update `src/LXMusicAccessible.App/Docs/使用说明.md` and README speech wording.
- Run targeted tests, full `dotnet test`, then `./verify.ps1`.
