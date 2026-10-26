# Automated testing

Run the deterministic Windows gate locally from the repository root:

```powershell
.\verify.ps1
```

The script restores packages, rejects High/Critical dependency advisories, builds the complete Release graph with warnings treated as errors, runs tests with public-network proxy variables pointed at a closed loopback port, requires at least 780 executed tests, runs `scripts/check-text.ps1`, rejects static service locators and empty C# catch blocks, and creates a clean local release under `artifacts/release`. The release step builds the installer's payload through the Setup project dependency, validates the required application/native DLL/license/user-document files, checks application and installer versions, loads the SQLite native library from both published payloads and enforces the security baseline, verifies every SHA-256 recorded in `release-manifest.json`, and runs the generated Setup executable through isolated first-install, upgrade, preserving-uninstall, and complete-uninstall smoke checks. The text check strictly decodes tracked first-party text as UTF-8 and rejects replacement characters, Unicode private-use characters, long question-mark runs, and known mojibake fragments. Binary files are excluded by the explicit text-extension allowlist; production source files are not excluded.

`LXMusicAccessible.Tests` starts the same `ApplicationHost` registrations as the desktop app. Its fixture uses a unique temporary data directory and real file-backed SQLite, then removes that directory when the fixture is disposed. HTTP requests must be registered with the rejecting handler; unknown requests fail immediately. Browser/process launches, announcements, speech, and native audio are replaced by recording adapters.

The media-quality regression suite drives every production token (`128k`, `192k`, `320k`, `flac`, `flac24bit`, `hires`, `atmos`, `atmos_plus`, `master`, `jyeffect`, `sky`, `dolby`, and `jymaster`) through both download and playback. Resolver URLs, content types, file extensions, distinguishable fixture bytes, MP3 frame bitrate, FLAC STREAMINFO, saved files, fallback status, and the audio-player seam are asserted independently so an extension-only success cannot hide a lowest-quality payload.

Automated tests must not depend on public network access, the real user profile, an installed browser, speech software, or an audio device. This repository intentionally has no remote CI/CD workflow. Every ticket owner runs `verify.ps1` locally, records the exact stage and test results on its GitHub issue, and does not wait for a remote check.

`LXMusicAccessible.TestConsole` remains a manual diagnostic program. It accesses real services and native libraries, so it is not part of the automated quality gate.

The test project still excludes 18 WPF-era or pre-net48 files that do not compile against the current WinForms/net48 interfaces (for example `DesktopLifecycleWorkflowTests`, `GlobalHotkeyMessageWindowTests`, `ScriptSecurityTests`, and `InstallDirectoryOwnershipTests`). They are not counted in the 783 executed tests. The gate requires at least 780 executed tests so a future accidental exclusion cannot silently reduce coverage; porting those suites remains follow-up work. New coverage for the final audit is in `FinalReleaseAuditTests`; the excluded files must not be presented as passing coverage.

## WinForms accessibility semantics

WinForms controls use native `Text`, headers, label association, and list-item rendering as the accessible name whenever those properties already express the task. Controls with no usable native name source receive a concise task-oriented `AccessibleName`. Names remain role-free: use “搜索”, not “搜索按钮”; a tab named “搜索” must not repeat “标签页”. “播放列表” remains a valid business term and must not be rejected merely because it contains “列表”.

Ordinary application controls do not expose operation-teaching HelpText, AccessibleDescription, or ToolTip-derived UIA descriptions. The platform and screen reader already describe basic navigation and activation, so instructions such as arrow-key browsing, Enter/Space activation, Delete, or application-key menus must not be attached to controls. Workflow guidance belongs in visible content or the user guide. Dynamic errors, progress, and completion use the WinForms UIA notification channel through `WinFormsAccessibleAnnouncer`; optional speech follows the accessibility settings and announcement whitelist. Short state needed to understand a list item belongs once in that item's accessible name. Background updates preserve or restore predictable focus and do not turn selection changes into network, playback, file, or destructive actions.

The STA audit tests instantiate every production form and inspect focusable controls for concise names and explicit tab order. Focused suites exercise native menus/tabs/lists, empty-list shell focus, focus preservation, Escape behavior, explicit activation, disabled actions, hotkey recording, source/download/playlist updates, and announcements. They do not synthesize full keyboard sessions or launch Narrator, NVDA, or JAWS; those flows remain human pre-release checks recorded in release documentation.
