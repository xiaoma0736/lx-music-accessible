$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
Add-Type -AssemblyName System.IO.Compression.FileSystem

if (-not ("NativeSqliteVersion" -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

public static class NativeSqliteVersion
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr SqliteVersion();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryW(string path);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);

    [DllImport("kernel32.dll")]
    private static extern bool FreeLibrary(IntPtr module);

    public static string Read(string path)
    {
        var module = LoadLibraryW(path);
        if (module == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to load SQLite runtime: " + path);

        try
        {
            var export = GetProcAddress(module, "sqlite3_libversion");
            if (export == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SQLite version export is missing: " + path);

            var readVersion = (SqliteVersion)Marshal.GetDelegateForFunctionPointer(export, typeof(SqliteVersion));
            var version = Marshal.PtrToStringAnsi(readVersion());
            if (version == null)
                throw new InvalidOperationException("SQLite returned no version.");

            return version;
        }
        finally
        {
            FreeLibrary(module);
        }
    }
}
"@
}

$root = [System.IO.Path]::GetFullPath($PSScriptRoot)
$rootPrefix = $root.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
$src = Join-Path $root "src"
$artifacts = Join-Path $root "artifacts"
$releaseRoot = Join-Path $artifacts "release"
$staging = Join-Path $artifacts "release-staging"
$selfContained = Join-Path $staging "app-self-contained"
$frameworkDependent = Join-Path $staging "app-framework-dependent"
$setupOutput = Join-Path $staging "setup"
$payloadArchive = Join-Path $staging "payload.zip"
$setupProject = Join-Path $src "LXMusicAccessible.Setup\LXMusicAccessible.Setup.csproj"
$appProject = Join-Path $src "LXMusicAccessible.App\LXMusicAccessible.App.csproj"

function Invoke-DotNet([string[]]$Arguments, [string]$FailureMessage) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw $FailureMessage }
}

function Assert-Payload([string]$Directory, [string[]]$RequiredFiles) {
    foreach ($relativePath in $RequiredFiles) {
        $fullPath = Join-Path $Directory $relativePath
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            throw "Required payload file is missing: $relativePath"
        }
    }
}

function Assert-ZipPayload([string]$ArchivePath, [string[]]$RequiredFiles) {
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $entries = @($archive.Entries | ForEach-Object { $_.FullName.Replace('/', '\') })
        foreach ($relativePath in $RequiredFiles) {
            if ($entries -notcontains $relativePath.Replace('/', '\')) {
                throw "Archive $ArchivePath is missing: $relativePath"
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Assert-SqliteVersion([string]$Directory, [Version]$MinimumVersion) {
    $libraryPath = Join-Path $Directory "e_sqlite3.dll"
    $versionText = [NativeSqliteVersion]::Read($libraryPath)
    $version = [Version]$versionText
    if ($version -lt $MinimumVersion) {
        throw "SQLite $version is older than required security baseline $MinimumVersion in $libraryPath"
    }

    Write-Host "Loaded SQLite runtime: $version ($libraryPath)"
    return $version.ToString()
}

function Invoke-Installer([string]$Installer, [string[]]$Arguments, [string]$FailureMessage) {
    & $Installer @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$FailureMessage ExitCode=$LASTEXITCODE" }
}

$releaseRoot = [System.IO.Path]::GetFullPath($releaseRoot)
$staging = [System.IO.Path]::GetFullPath($staging)
if (-not $releaseRoot.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase) -or
    -not $staging.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Release paths must remain inside the repository."
}

New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
if ((Get-Item -LiteralPath $artifacts).Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
    throw "Repository artifacts directory cannot be a reparse point: $artifacts"
}

# Create before Resolve-Path: a clean checkout has no release output directory.
New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
$releaseRoot = (Resolve-Path -LiteralPath $releaseRoot).Path
if ((Get-Item -LiteralPath $releaseRoot).Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
    throw "Release output directory cannot be a reparse point: $releaseRoot"
}
if (Test-Path -LiteralPath $staging) {
    if ((Get-Item -LiteralPath $staging).Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
        throw "Release staging directory cannot be a reparse point: $staging"
    }
    Remove-Item -LiteralPath $staging -Recurse -Force
}
New-Item -ItemType Directory -Path $staging -Force | Out-Null

[xml]$appProjectXml = [System.IO.File]::ReadAllText($appProject)
$version = [string]$appProjectXml.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($version)) { throw "Application version is missing from $appProject" }
$assemblyName = [string]$appProjectXml.Project.PropertyGroup.AssemblyName
if ([string]::IsNullOrWhiteSpace($assemblyName)) { throw "Application assembly name is missing from $appProject" }
$userDocuments = @(
    $appProjectXml.Project.ItemGroup.Content |
        ForEach-Object { [string]$_.Link } |
        Where-Object { $_ -like "Docs\*.md" }
)
if ($userDocuments.Count -ne 1) { throw "Expected exactly one packaged user document." }
$applicationExecutable = "$assemblyName.exe"

$requiredPayload = @(
    $applicationExecutable,
    "bass.dll",
    "bassape.dll",
    "bassflac.dll",
    "bassopus.dll",
    "byctrl-x64.dll",
    "e_sqlite3.dll",
    "prism.dll",
    "tolk.dll",
    "PRISM_LICENSE.txt",
    "UninstallCleanup.ps1",
    $userDocuments[0]
)
$minimumSqliteVersion = [Version]"3.50.2"

$selfContainedZip = Join-Path $releaseRoot "LXMusicAccessible-$version-win-x64-self-contained.zip"
$frameworkDependentZip = Join-Path $releaseRoot "LXMusicAccessible-$version-win-x64-framework-dependent.zip"
$setupArtifact = Join-Path $releaseRoot "LXMusicAccessible-Setup-$version-win-x64.exe"
$manifestPath = Join-Path $releaseRoot "release-manifest.json"

foreach ($knownOutput in @($selfContainedZip, $frameworkDependentZip, $setupArtifact, $manifestPath)) {
    if (Test-Path -LiteralPath $knownOutput) { Remove-Item -LiteralPath $knownOutput -Force }
}

Write-Host "== 1/6 Build installer and its explicit self-contained payload =="
Invoke-DotNet @(
    "publish", $setupProject,
    "--configuration", "Release",
    "--runtime", "win-x64",
    "--self-contained", "true",
    "-p:PublishSingleFile=true",
    "-p:InstallerPayloadDirectory=$selfContained",
    "-p:InstallerPayloadArchive=$payloadArchive",
    "--output", $setupOutput
) "Installer publish failed."

$setupExecutable = Join-Path $setupOutput "LXMusicAccessible.Setup.exe"
if (-not (Test-Path -LiteralPath $setupExecutable -PathType Leaf)) {
    throw "Installer executable was not produced: $setupExecutable"
}
Assert-Payload $selfContained $requiredPayload
Assert-ZipPayload $payloadArchive $requiredPayload
$selfContainedSqliteVersion = Assert-SqliteVersion $selfContained $minimumSqliteVersion

$appVersion = (Get-Item -LiteralPath (Join-Path $selfContained $applicationExecutable)).VersionInfo.ProductVersion
$setupVersion = (Get-Item -LiteralPath $setupExecutable).VersionInfo.ProductVersion
if (-not $appVersion.StartsWith($version, [System.StringComparison]::Ordinal) -or
    -not $setupVersion.StartsWith($version, [System.StringComparison]::Ordinal)) {
    throw "Artifact version mismatch. Expected $version; app=$appVersion; setup=$setupVersion"
}

Write-Host "== 2/6 Build framework-dependent application =="
Invoke-DotNet @(
    "publish", $appProject,
    "--configuration", "Release",
    "--runtime", "win-x64",
    "--self-contained", "false",
    "--output", $frameworkDependent
) "Framework-dependent publish failed."
Assert-Payload $frameworkDependent $requiredPayload
$frameworkDependentSqliteVersion = Assert-SqliteVersion $frameworkDependent $minimumSqliteVersion
if ($frameworkDependentSqliteVersion -ne $selfContainedSqliteVersion) {
    throw "Published SQLite runtime versions differ. self-contained=$selfContainedSqliteVersion; framework-dependent=$frameworkDependentSqliteVersion"
}

Write-Host "== 3/6 Create release artifacts =="
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $selfContained,
    $selfContainedZip,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $false)
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $frameworkDependent,
    $frameworkDependentZip,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $false)
Copy-Item -LiteralPath $setupExecutable -Destination $setupArtifact -Force

Write-Host "== 4/6 Validate artifact contents and versions =="
Assert-ZipPayload $selfContainedZip $requiredPayload
Assert-ZipPayload $frameworkDependentZip $requiredPayload

$payloadFiles = foreach ($relativePath in $requiredPayload) {
    $payloadFile = Join-Path $selfContained $relativePath
    [ordered]@{
        path = $relativePath.Replace('\', '/')
        sha256 = (Get-FileHash -LiteralPath $payloadFile -Algorithm SHA256).Hash.ToLowerInvariant()
        bytes = (Get-Item -LiteralPath $payloadFile).Length
    }
}

$releaseArtifacts = foreach ($artifactPath in @($selfContainedZip, $frameworkDependentZip, $setupArtifact)) {
    [ordered]@{
        file = [System.IO.Path]::GetFileName($artifactPath)
        sha256 = (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash.ToLowerInvariant()
        bytes = (Get-Item -LiteralPath $artifactPath).Length
    }
}

$manifest = [ordered]@{
    product = "LX Music Accessible"
    version = $version
    runtime = "win-x64"
    sqliteVersion = $selfContainedSqliteVersion
    generatedAtUtc = [DateTime]::UtcNow.ToString("O")
    payload = @($payloadFiles)
    artifacts = @($releaseArtifacts)
}
$manifestJson = $manifest | ConvertTo-Json -Depth 5
[System.IO.File]::WriteAllText($manifestPath, $manifestJson + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))

Write-Host "== 5/6 Verify recorded SHA-256 values =="
$savedManifest = [System.IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
if ($savedManifest.version -ne $version) { throw "Release manifest version mismatch." }
if ($savedManifest.sqliteVersion -ne $selfContainedSqliteVersion) { throw "Release manifest SQLite version mismatch." }
foreach ($payload in $savedManifest.payload) {
    $payloadPath = Join-Path $selfContained $payload.path
    $actualHash = (Get-FileHash -LiteralPath $payloadPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $payload.sha256) {
        throw "Release payload hash mismatch: $($payload.path)"
    }
}
foreach ($artifact in $savedManifest.artifacts) {
    $artifactPath = Join-Path $releaseRoot $artifact.file
    $actualHash = (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $artifact.sha256) {
        throw "Release artifact hash mismatch: $($artifact.file)"
    }
}

Write-Host "== 6/6 Smoke-test the generated installer lifecycle =="
$smokeParent = Join-Path $staging "installer-smoke-parent"
$smokeTarget = Join-Path $smokeParent "LXMusicAccessible"
$parentSentinel = Join-Path $smokeParent "parent-sentinel.txt"
$dataSentinel = Join-Path $smokeParent "retained-user-data.txt"
$unknownInstallFile = Join-Path $smokeTarget "unknown-user-file.txt"
$smokeParent = [System.IO.Path]::GetFullPath($smokeParent)
$stagingPrefix = $staging.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
if (-not $smokeParent.StartsWith($stagingPrefix, [System.StringComparison]::OrdinalIgnoreCase) -or
    [System.IO.Path]::GetFileName($smokeParent) -ne "installer-smoke-parent") {
    throw "Installer smoke path is unsafe: $smokeParent"
}

try {
    New-Item -ItemType Directory -Path $smokeParent -Force | Out-Null
    [System.IO.File]::WriteAllText($parentSentinel, "keep")
    [System.IO.File]::WriteAllText($dataSentinel, "keep")

    Invoke-Installer $setupArtifact @("--install-parent", $smokeParent) "First install smoke test failed."
    $markerPath = Join-Path $smokeTarget ".lxmusic-install-owner.json"
    if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) { throw "Install ownership marker is missing." }
    $firstMarker = [System.IO.File]::ReadAllText($markerPath) | ConvertFrom-Json

    Invoke-Installer $setupArtifact @("--install-parent", $smokeParent) "Upgrade smoke test failed."
    $upgradedMarker = [System.IO.File]::ReadAllText($markerPath) | ConvertFrom-Json
    if ($upgradedMarker.OwnershipId -ne $firstMarker.OwnershipId) {
        throw "Upgrade changed the install ownership identity."
    }

    [System.IO.File]::WriteAllText($unknownInstallFile, "keep")
    Invoke-Installer $setupArtifact @("--uninstall-target", $smokeTarget, $firstMarker.OwnershipId) "Preserving uninstall smoke test failed."
    if (-not (Test-Path -LiteralPath $unknownInstallFile -PathType Leaf) -or
        -not (Test-Path -LiteralPath $parentSentinel -PathType Leaf) -or
        -not (Test-Path -LiteralPath $dataSentinel -PathType Leaf)) {
        throw "Uninstall removed unknown, parent, or retained user data."
    }

    Remove-Item -LiteralPath $unknownInstallFile -Force
    Invoke-Installer $setupArtifact @("--uninstall-target", $smokeTarget, $firstMarker.OwnershipId) "Complete uninstall smoke test failed."
    Invoke-Installer $setupArtifact @("--uninstall-target", $smokeTarget, $firstMarker.OwnershipId) "Repeated uninstall smoke test failed."
    if (Test-Path -LiteralPath $smokeTarget) { throw "Complete uninstall left the application directory behind." }
    if (-not (Test-Path -LiteralPath $parentSentinel -PathType Leaf) -or
        -not (Test-Path -LiteralPath $dataSentinel -PathType Leaf)) {
        throw "Complete uninstall removed parent or retained user data."
    }
}
finally {
    if (Test-Path -LiteralPath $smokeParent) {
        $resolvedSmokeParent = (Resolve-Path -LiteralPath $smokeParent).Path
        if (-not $resolvedSmokeParent.StartsWith($stagingPrefix, [System.StringComparison]::OrdinalIgnoreCase) -or
            [System.IO.Path]::GetFileName($resolvedSmokeParent) -ne "installer-smoke-parent") {
            throw "Refusing unsafe installer smoke cleanup: $resolvedSmokeParent"
        }
        Remove-Item -LiteralPath $resolvedSmokeParent -Recurse -Force
    }
}

Write-Host "Release build passed: $releaseRoot"
Get-ChildItem -LiteralPath $releaseRoot -File | Select-Object Name, Length
