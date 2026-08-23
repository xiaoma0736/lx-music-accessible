# Automated testing

Run the deterministic Windows gate locally from the repository root:

```powershell
.\verify.ps1
```

The script restores packages, rejects High/Critical dependency advisories, builds Release, runs tests with public-network proxy variables pointed at a closed loopback port, requires at least one executed test, and performs applicable text checks. T08 may add `scripts/check-text.ps1`; the gate runs it automatically when present.

`LXMusicAccessible.Tests` starts the same `ApplicationHost` registrations as the desktop app. Its fixture uses a unique temporary data directory and real file-backed SQLite, then removes that directory when the fixture is disposed. HTTP requests must be registered with the rejecting handler; unknown requests fail immediately. Browser/process launches, announcements, speech, and native audio are replaced by recording adapters.

Automated tests must not depend on public network access, the real user profile, an installed browser, speech software, or an audio device. This repository intentionally has no remote CI/CD workflow. Every ticket owner runs `verify.ps1` locally, records the exact stage and test results on its GitHub issue, and does not wait for a remote check.

`LXMusicAccessible.TestConsole` remains a manual diagnostic program. It accesses real services and native libraries, so it is not part of the automated quality gate.
