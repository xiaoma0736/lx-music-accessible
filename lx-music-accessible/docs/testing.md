# Automated testing

Run the deterministic Windows gate locally from the repository root:

```powershell
.\verify.ps1
```

The script restores packages, rejects High/Critical dependency advisories, builds Release, runs tests with public-network proxy variables pointed at a closed loopback port, requires at least one executed test, runs `scripts/check-text.ps1`, and creates a clean local release under `artifacts/release`. The release step builds the installer's payload through the Setup project dependency, validates the required application/native DLL/license/user-document files, checks application and installer versions, and verifies every SHA-256 recorded in `release-manifest.json`. The text check strictly decodes tracked first-party text as UTF-8 and rejects replacement characters, Unicode private-use characters, long question-mark runs, and known mojibake fragments. Binary files are excluded by the explicit text-extension allowlist; production source files are not excluded.

`LXMusicAccessible.Tests` starts the same `ApplicationHost` registrations as the desktop app. Its fixture uses a unique temporary data directory and real file-backed SQLite, then removes that directory when the fixture is disposed. HTTP requests must be registered with the rejecting handler; unknown requests fail immediately. Browser/process launches, announcements, speech, and native audio are replaced by recording adapters.

Automated tests must not depend on public network access, the real user profile, an installed browser, speech software, or an audio device. This repository intentionally has no remote CI/CD workflow. Every ticket owner runs `verify.ps1` locally, records the exact stage and test results on its GitHub issue, and does not wait for a remote check.

`LXMusicAccessible.TestConsole` remains a manual diagnostic program. It accesses real services and native libraries, so it is not part of the automated quality gate.
