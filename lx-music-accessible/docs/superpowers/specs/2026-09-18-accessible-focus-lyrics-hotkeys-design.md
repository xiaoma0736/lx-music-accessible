# Accessibility Focus, Quiet Speech, Lyrics Viewer and Child Hotkeys

## Goal
Make tab navigation predictable, remove routine spoken noise, provide an accessible lyrics text viewer from any song list, and route configured local playback hotkeys through child windows.

## Behavior
- Tab commands change the selected page and focus the tab header; data loading never steals focus.
- Welcome and routine browse/loading completion messages remain visible in the status bar but are not spoken. Errors remain spoken.
- Every song list supports Shift+Enter to open the selected song's lyrics, falling back to current playback when nothing is selected.
- Lyrics use a visible read-only multiline TextBox named “歌词”, with full text, current-line highlight/scroll, copy button, Ctrl+Shift+C copy, and Escape close.
- Lyrics, downloads, playlist import, source manager, settings and manual source windows route configured local hotkeys to the shell; writable text editing retains priority.
- Existing global hotkey behavior and explicit global/local conflict rules remain unchanged.

## Constraints
- Native WinForms controls remain the semantic accessibility layer.
- UIA notifications are not used for speech; TTS is the single spoken output channel.
- Selection, focus movement and page changes must not start network work or playback.
