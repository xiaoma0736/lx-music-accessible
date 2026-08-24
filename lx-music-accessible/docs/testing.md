# Automated testing

Run the deterministic Windows gate locally from the repository root:

```powershell
.\verify.ps1
```

The script restores packages, rejects High/Critical dependency advisories, builds the complete Release graph with warnings treated as errors, runs tests with public-network proxy variables pointed at a closed loopback port, requires at least one executed test, runs `scripts/check-text.ps1`, rejects static service locators and empty C# catch blocks, and creates a clean local release under `artifacts/release`. The release step builds the installer's payload through the Setup project dependency, validates the required application/native DLL/license/user-document files, checks application and installer versions, loads the SQLite native library from both published payloads and enforces the security baseline, verifies every SHA-256 recorded in `release-manifest.json`, and runs the generated Setup executable through isolated first-install, upgrade, preserving-uninstall, complete-uninstall, and repeated-uninstall smoke checks. The text check strictly decodes tracked first-party text as UTF-8 and rejects replacement characters, Unicode private-use characters, long question-mark runs, and known mojibake fragments. Binary files are excluded by the explicit text-extension allowlist; production source files are not excluded.

`LXMusicAccessible.Tests` starts the same `ApplicationHost` registrations as the desktop app. Its fixture uses a unique temporary data directory and real file-backed SQLite, then removes that directory when the fixture is disposed. HTTP requests must be registered with the rejecting handler; unknown requests fail immediately. Browser/process launches, announcements, speech, and native audio are replaced by recording adapters.

Automated tests must not depend on public network access, the real user profile, an installed browser, speech software, or an audio device. This repository intentionally has no remote CI/CD workflow. Every ticket owner runs `verify.ps1` locally, records the exact stage and test results on its GitHub issue, and does not wait for a remote check.

`LXMusicAccessible.TestConsole` remains a manual diagnostic program. It accesses real services and native libraries, so it is not part of the automated quality gate.

## WPF accessibility semantics

WPF controls use native `Content`, `Header`, `Text`, `Title`, and `Label.Target` as the accessible name whenever those properties already express the task. Do not repeat a platform role in an explicit name: use “搜索”, not “搜索按钮”; use a tab Header, not “搜索标签页”. “播放列表” remains a valid business term and must not be rejected merely because it contains “列表”.

An explicit `AutomationProperties.Name` is reserved for controls with no native name source, such as a composed list, an unlabeled edit, or a meaningful image. It must be short and role-free. Code-created controls follow the same rule and use `Content` or `Header` instead of a duplicate `SetName`.

Ordinary controls do not expose HelpText that repeats a label, visible instruction, current state, role, or standard keyboard behavior. HelpText is allowed only for a documented nonstandard task action that is not otherwise discoverable. Dynamic errors, progress, completion, and conflict state continue through live-region announcements and ItemStatus, not through verbose action names or duplicate HelpText. The audited exceptions and reasons are recorded in `docs/audits/accessibility-semantics-baseline-2026-08-24.md`.

`AccessibilitySemanticsTests` scans every application XAML file and code-created WPF control path. It includes positive and negative probes, validates a fixed HelpText exception set, and rejects unnamed interactive controls. These deterministic tests do not synthesize keyboard input or launch Narrator, NVDA, or JAWS; those flows remain manual pre-release checks.
