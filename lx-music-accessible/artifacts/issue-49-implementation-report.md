# Issue #49 implementation report

Branch: `feature/accessible-settings-hotkeys-playback`
Baseline: `c25b14f` (`fix: route global hotkeys through a dedicated hidden window`)

## Result

Implemented and verified all approved Issue #49 criteria. The fresh post-commit local quality gate passed with 311/311 tests.

## Acceptance criteria review

- [x] Settings category `ListBoxItem`s expose explicit `AutomationProperties.Name` values. Reverse keyboard traversal from settings content refocuses the selected category and announces its accessible name through UIA with activity `settings` and `AnnouncementSpeech.None`, so application TTS is not duplicated.
- [x] Settings announcements use the policy-approved `settings` activity; hotkey recording, cancellation, and results use `hotkey-recording`. The voice-disabled confirmation uses `AnnouncementSpeech.Always` so it remains audible when voice prompts are disabled.
- [x] Added local-only `toggle_voice_announcement` (`Shift+F5`) and `toggle_lyric_announcement` (`Shift+F6`). Defaults merge safely, persisted user bindings retain precedence, conflicts leave a new default unbound/disabled, changed bindings persist, and MainWindow dispatch toggles `EnableVoiceAnnouncement` / `LyricAutoAnnounce`.
- [x] Script source resolution requires explicit support for the requested platform. An empty `SupportedSources` set is no longer accepted.
- [x] Cross-source playback alternatives use strict same-song validation: exact normalized title, complete artist-set equality, valid durations on both sides within 5 seconds, and consistency when both albums are known. There is no approximate fallback match.
- [x] Added focused regression coverage for accessibility, speech policy, hotkeys, source platform support, and same-song safety.
- [x] Fresh `verify.ps1` local quality gate passed.

## Commits

- `38a9fe21e060e852f7c73ee35edd1d04c839ef10` — `fix(settings): restore accessible category and speech behavior`
- `55eb046b8a7259a52de3ce1e21ef541e5905bec5` — `feat(hotkeys): add voice and lyric announcement toggles`
- `d2864c3f5954c9f337437f7280dfff005fd7f42a` — `fix(playback): enforce source and song matching safety`

The evidence-only report commit is created after this report; its hash is recorded in the Issue #49 closing comment.

## Changed files

Production:

- `src/LXMusicAccessible.App/AccessibleSpeechPolicy.cs`
- `src/LXMusicAccessible.App/MainWindow.xaml.cs`
- `src/LXMusicAccessible.App/SettingsWindow.xaml`
- `src/LXMusicAccessible.App/SettingsWindow.xaml.cs`
- `src/LXMusicAccessible.Models/AppModels.cs`
- `src/LXMusicAccessible.Services/HotkeyManagerService.cs`
- `src/LXMusicAccessible.Services/MusicSearchMatcher.cs`
- `src/LXMusicAccessible.Services/MusicSourceService.cs`

Tests:

- `src/LXMusicAccessible.Tests/AccessibilitySemanticsTests.cs`
- `src/LXMusicAccessible.Tests/AccessibleAnnouncerTests.cs`
- `src/LXMusicAccessible.Tests/Issue49HotkeyTests.cs`
- `src/LXMusicAccessible.Tests/Issue49PlaybackSourceSafetyTests.cs`
- `src/LXMusicAccessible.Tests/Issue49SameSongTests.cs`
- `src/LXMusicAccessible.Tests/Issue49SettingsAccessibilityTests.cs`
- `src/LXMusicAccessible.Tests/PlaybackFakeIpRegressionTests.cs`
- `src/LXMusicAccessible.Tests/ScriptSourceLifecycleTests.cs`
- `src/LXMusicAccessible.Tests/SettingsWorkflowTests.cs`
- `src/LXMusicAccessible.Tests/SpeechWhitelistTests.cs`

Evidence/report file:

- `artifacts/issue-49-implementation-report.md`

## TDD evidence

All work was run from the repository root. WPF test runs require the machine-scoped `windir` value in the current process:

```powershell
$env:windir = [Environment]::GetEnvironmentVariable('windir','Machine')
```

### Red evidence

The red runs below were re-verified after the implementation commits by temporarily restoring only the relevant production files from baseline `c25b14f`, running current focused tests, and restoring those files from `HEAD`.

1. Settings accessibility / speech policy

```powershell
git restore --source=c25b14f -- src/LXMusicAccessible.App/AccessibleSpeechPolicy.cs src/LXMusicAccessible.App/SettingsWindow.xaml src/LXMusicAccessible.App/SettingsWindow.xaml.cs
dotnet test src/LXMusicAccessible.Tests/LXMusicAccessible.Tests.csproj --logger "console;verbosity=normal" --filter "FullyQualifiedName~Issue49SettingsAccessibilityTests|FullyQualifiedName~SpeechWhitelistTests"
git restore --source=HEAD -- src/LXMusicAccessible.App/AccessibleSpeechPolicy.cs src/LXMusicAccessible.App/SettingsWindow.xaml src/LXMusicAccessible.App/SettingsWindow.xaml.cs
```

Result: 18 total, 12 passed, 6 failed. Log: `artifacts/issue-49/reverified-settings-red.log`.

2. Local announcement hotkeys

```powershell
git restore --source=c25b14f -- src/LXMusicAccessible.Models/AppModels.cs src/LXMusicAccessible.Services/HotkeyManagerService.cs src/LXMusicAccessible.App/MainWindow.xaml.cs
dotnet test src/LXMusicAccessible.Tests/LXMusicAccessible.Tests.csproj --logger "console;verbosity=normal" --filter "FullyQualifiedName~Issue49HotkeyTests"
git restore --source=HEAD -- src/LXMusicAccessible.Models/AppModels.cs src/LXMusicAccessible.Services/HotkeyManagerService.cs src/LXMusicAccessible.App/MainWindow.xaml.cs
```

Result: compilation failed as expected because `ToggleVoiceAnnouncement` and `ToggleLyricAnnouncement` were absent. Log: `artifacts/issue-49/reverified-hotkeys-red.log`.

3. Playback source and same-song safety

```powershell
git restore --source=c25b14f -- src/LXMusicAccessible.Services/MusicSearchMatcher.cs src/LXMusicAccessible.Services/MusicSourceService.cs
dotnet test src/LXMusicAccessible.Tests/LXMusicAccessible.Tests.csproj --logger "console;verbosity=normal" --filter "FullyQualifiedName~Issue49SameSongTests|FullyQualifiedName~Issue49PlaybackSourceSafetyTests"
git restore --source=HEAD -- src/LXMusicAccessible.Services/MusicSearchMatcher.cs src/LXMusicAccessible.Services/MusicSourceService.cs
```

Result: 10 total, 4 passed, 6 failed. Log: `artifacts/issue-49/reverified-playback-red.log`.

Original implementation-time red logs are retained as `tdd-red.log`, `speech-policy-red.log`, `hotkey-dispatch-red.log`, `playback-red.log`, and `playback-source-red.log`.

### Focused green evidence

Post-commit combined focused run:

```powershell
$env:windir = [Environment]::GetEnvironmentVariable('windir','Machine')
dotnet test src/LXMusicAccessible.Tests/LXMusicAccessible.Tests.csproj --no-build --logger "console;verbosity=normal" --filter "FullyQualifiedName~Issue49|FullyQualifiedName~ScriptSourceLifecycleTests|FullyQualifiedName~PlaybackFakeIpRegressionTests|FullyQualifiedName~SpeechWhitelistTests"
```

Result: 57 passed, 0 failed. Log: `artifacts/issue-49/post-commit-focused-green.log`.

Additional focused green logs:

- All Issue #49 focused tests: 17 passed — `artifacts/issue-49/all-issue49-green-2.log`
- Settings accessibility: 4 passed — `artifacts/issue-49/settings-focused.log`
- Same-song matching: 8 passed — `artifacts/issue-49/same-song-focused.log`
- Playback fake-IP/fallback regressions: 5 passed — `artifacts/issue-49/playback-regression-green.log`
- Script source lifecycle: 21 passed — `artifacts/issue-49/lifecycle-focused-green-3.log`
- UIA announcer marshaling: 1 passed — `artifacts/issue-49/announcer-green.log`

## Final local quality gate

Fresh post-commit command:

```powershell
$env:windir = [Environment]::GetEnvironmentVariable('windir','Machine')
powershell -ExecutionPolicy Bypass -File verify.ps1 2>&1 | Tee-Object -FilePath artifacts\issue-49\verify-post-commit.log
```

Observed result:

- Dependency audit: 0 High/Critical vulnerabilities
- Release build: 0 warnings, 0 errors
- Tests: 311 passed, 0 failed, 311 total
- Discovered-test guard: executed 311 tests
- Text checks: passed
- Release and installer verification: passed
- Final message: `Local quality gate passed.`
- Log: `artifacts/issue-49/verify-post-commit.log`

## Baseline and stale-test remediation

These focused updates were required to align pre-existing tests with current approved behavior; no Issue #49 regression was masked:

- Default hotkey counts changed from 29 total / 14 local to 31 total / 16 local after adding the two approved actions.
- Playback and lifecycle script fixtures now explicitly declare the platform/quality they exercise.
- The accessibility static-name policy narrowly permits exact `ListBoxItem` labels for the seven settings categories because Issue #49 explicitly requires those names.
- Existing announcer tests were updated to current download activity IDs (`download:import`, `download:retry`).
- Lifecycle tests cover persisted-only subscriptions: cold start does not fetch unconfigured fixed subscriptions, while refresh recognizes a persisted source by either source ID or URL. `RefreshSubscriptionsAsync` was updated accordingly so manually imported subscription updates can refresh without duplicate creation.

## Self-review notes and non-blocking limitations

- The hotkey dispatch test checks the MainWindow dispatch source for the two settings assignments because constructing the complete main WPF window is not practical in the offline focused test. Persistence, conflict handling, and defaults are behavior-tested directly.
- Raw TDD/gate logs remain local under the gitignored `artifacts/` directory; this report is force-added so the evidence summary is reviewable. No release artifacts or logs were committed.
- As instructed, no push or pull request was created.
