# Inno Setup installer package

`LXMusicAccessible.iss` is the authoritative installer definition used by `build_release.ps1`.

Build requirements:

- Inno Setup 6.7 or later (`ISCC.exe`)
- .NET SDK used by the repository
- The Simplified Chinese messages file in `Languages/ChineseSimplified.isl`

`build_release.ps1` publishes the framework-dependent application payload, verifies its native and security-sensitive files, invokes ISCC, records artifact hashes, and runs silent install/upgrade/uninstall smoke tests.

`ChineseSimplified.isl` is the Simplified Chinese translation distributed with the Inno Setup 6 source tree and is included so the interactive installer is presented in Chinese.
