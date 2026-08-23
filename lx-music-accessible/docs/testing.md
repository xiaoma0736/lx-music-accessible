# Automated testing

Run the deterministic Windows gate from the repository root:

```powershell
dotnet restore src/LXMusicAccessible.sln
dotnet build src/LXMusicAccessible.sln --configuration Release --no-restore
dotnet test src/LXMusicAccessible.sln --configuration Release --no-build
```

`LXMusicAccessible.Tests` starts the same `ApplicationHost` registrations as the desktop app. Its fixture uses a unique temporary data directory and real file-backed SQLite, then removes that directory when the fixture is disposed. HTTP requests must be registered with the rejecting handler; unknown requests fail immediately. Browser/process launches, announcements, speech, and native audio are replaced by recording adapters.

Automated tests must not depend on public network access, the real user profile, an installed browser, speech software, or an audio device. The Windows workflow additionally points standard proxy variables at a closed loopback port while tests run and fails when no test is discovered.

`LXMusicAccessible.TestConsole` remains a manual diagnostic program. It accesses real services and native libraries, so it is not part of the automated quality gate.
