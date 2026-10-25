$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
Add-Type -AssemblyName System.IO.Compression
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
$application = Join-Path $staging "app-framework-dependent"
$installerScript = Join-Path $root "packaging\inno\LXMusicAccessible.iss"
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
        $entries = @($archive.Entries | ForEach-Object { $_.FullName })
        $invalidEntries = @($entries | Where-Object { $_.Contains('\') })
        if ($invalidEntries.Count -gt 0) {
            throw "Archive $ArchivePath contains non-portable backslash entry names: $($invalidEntries[0])"
        }

        foreach ($relativePath in $RequiredFiles) {
            $portablePath = $relativePath.Replace('\', '/')
            if ($entries -notcontains $portablePath) {
                throw "Archive $ArchivePath is missing: $relativePath"
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}

function New-ReleaseZip([string]$SourceDirectory, [string]$ArchivePath) {
    $sourceFullPath = [System.IO.Path]::GetFullPath($SourceDirectory)
    $archiveFullPath = [System.IO.Path]::GetFullPath($ArchivePath)
    $sourcePrefix = $sourceFullPath.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    $releasePrefix = [System.IO.Path]::GetFullPath($releaseRoot).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar

    if (-not ($archiveFullPath.StartsWith($releasePrefix, [System.StringComparison]::OrdinalIgnoreCase))) {
        throw "Release ZIP path is outside the release directory: $archiveFullPath"
    }
    if (Test-Path -LiteralPath $archiveFullPath) {
        Remove-Item -LiteralPath $archiveFullPath -Force
    }

    $archive = [System.IO.Compression.ZipFile]::Open($archiveFullPath, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem -LiteralPath $sourceFullPath -File -Recurse | Sort-Object FullName) {
            $fileFullPath = [System.IO.Path]::GetFullPath($file.FullName)
            if (-not $fileFullPath.StartsWith($sourcePrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "Payload file is outside the source directory: $fileFullPath"
            }

            $entryName = $fileFullPath.Substring($sourcePrefix.Length).Replace('\', '/')
            if ($entryName.Contains('\') -or [System.IO.Path]::IsPathRooted($entryName) -or $entryName -match '(^|/)\.\.(/|$)') {
                throw "Unsafe ZIP entry name: $entryName"
            }
            $null = [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $fileFullPath, $entryName, [System.IO.Compression.CompressionLevel]::Optimal)
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Get-PortableExecutableMachine([string]$Path) {
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $reader = New-Object System.IO.BinaryReader($stream)
        if ($stream.Length -lt 64 -or $reader.ReadUInt16() -ne 0x5A4D) {
            throw "Invalid executable image: $Path"
        }
        $null = $reader.ReadBytes(58)
        $stream.Position = $reader.ReadInt32()
        if ($reader.ReadUInt32() -ne 0x00004550) {
            throw "Portable executable signature is missing: $Path"
        }
        return $reader.ReadUInt16()
    }
    finally {
        $stream.Dispose()
    }
}

function Assert-NativeArchitecture([string]$Directory, [string[]]$NativeFiles) {
    foreach ($relativePath in $NativeFiles) {
        $path = Join-Path $Directory $relativePath
        $machine = Get-PortableExecutableMachine $path
        if ($machine -ne 0x8664) {
            $formatted = "0x{0:X4}" -f $machine
            throw "Native library is not x64: $relativePath ($formatted)"
        }
    }
    Write-Host "Verified x64 native images: $($NativeFiles.Count)"
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

function Assert-SystemTextJsonRuntime([string]$Application) {
    $assemblyPath = Join-Path $Application "System.Text.Json.dll"
    if (-not (Test-Path -LiteralPath $assemblyPath -PathType Leaf)) {
        throw "System.Text.Json runtime is missing: $assemblyPath"
    }
    $assemblyVersion = [System.Reflection.AssemblyName]::GetAssemblyName((Resolve-Path -LiteralPath $assemblyPath)).Version
    if ($assemblyVersion -ne [version]"8.0.0.6") {
        throw "Expected System.Text.Json 8.0.0.6, found $assemblyVersion"
    }

    $configPath = Get-ChildItem -LiteralPath $Application -Filter "*.exe.config" | Select-Object -First 1
    if ($null -eq $configPath) {
        throw "Application binding configuration is missing in $Application"
    }
    $config = [System.IO.File]::ReadAllText($configPath.FullName)
    if ($config -notmatch '<bindingRedirect oldVersion="0\.0\.0\.0-[^"]+" newVersion="8\.0\.0\.6" />') {
        throw "System.Text.Json binding redirect from 8.0.0.5 to 8.0.0.6 is missing."
    }
}
function Invoke-Installer([string]$Installer, [string[]]$Arguments, [string]$FailureMessage) {
    $process = Start-Process -FilePath $Installer -ArgumentList $Arguments -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "$FailureMessage ExitCode=$($process.ExitCode)" }
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

New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
$releaseRoot = (Resolve-Path -LiteralPath $releaseRoot).Path
if ((Get-Item -LiteralPath $releaseRoot).Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
    throw "Release output directory cannot be a reparse point: $releaseRoot"
}

# Clean output makes the release list exact and cannot leave stale x86/self-contained artifacts.
Get-ChildItem -LiteralPath $releaseRoot -Force | Remove-Item -Recurse -Force
if (Test-Path -LiteralPath $staging) {
    if ((Get-Item -LiteralPath $staging).Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
        throw "Release staging directory cannot be a reparse point: $staging"
    }
    Remove-Item -LiteralPath $staging -Recurse -Force
}
New-Item -ItemType Directory -Path $staging -Force | Out-Null

# Build-time script checks keep the Inno Setup runtime gate pinned to .NET Framework 4.8.
$installerSource = [System.IO.File]::ReadAllText($installerScript)
if (-not $installerSource.Contains("Release >= 528040")) {
    throw "Installer .NET Framework 4.8 runtime gate is missing."
}
if (-not $installerSource.Contains('MessagesFile: "Languages\ChineseSimplified.isl"')) {
    throw "Installer Simplified Chinese language gate is missing."
}

$innoCandidates = @(
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
    (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
    (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
)
if ($env:INNO_SETUP6) {
    $innoCandidates = @($env:INNO_SETUP6) + $innoCandidates
}
$innoCompiler = @($innoCandidates | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } | Select-Object -First 1)
if (-not $innoCompiler) {
    throw "Inno Setup 6 compiler (ISCC.exe) was not found. Install JRSoftware.InnoSetup or set INNO_SETUP6."
}

[xml]$appProjectXml = [System.IO.File]::ReadAllText($appProject)
$version = [string]$appProjectXml.Project.PropertyGroup.Version
if ($version -ne "3.0.0") { throw "Expected application/release version 3.0.0, found $version" }
$assemblyName = [string]$appProjectXml.Project.PropertyGroup.AssemblyName
if ([string]::IsNullOrWhiteSpace($assemblyName)) { throw "Application assembly name is missing from $appProject" }
$userDocuments = @(
    $appProjectXml.Project.ItemGroup.Content |
        ForEach-Object { [string]$_.Link } |
        Where-Object { $_ -like "Docs\*.md" }
)
if ($userDocuments.Count -ne 1) { throw "Expected exactly one packaged user document." }

$applicationExecutable = "$assemblyName.exe"
$nativeFiles = @(
    "bass.dll",
    "bassape.dll",
    "bassflac.dll",
    "bassopus.dll",
    "byctrl-x64.dll",
    "ClearScriptV8.win-x64.dll",
    "e_sqlite3.dll",
    "prism.dll",
    "tolk.dll",
    "WebView2Loader.dll"
)
$requiredPayload = @(
    $applicationExecutable,
    "ClearScript.V8.ICUData.dll",
    "PRISM_LICENSE.txt",
    "UninstallCleanup.ps1",
    $userDocuments[0]
) + $nativeFiles

$minimumSqliteVersion = [Version]"3.50.2"
$frameworkZip = Join-Path $releaseRoot "LXMusicAccessible-$version-win-x64-framework-dependent.zip"
$setupArtifact = Join-Path $releaseRoot "LXMusicAccessible-Setup-$version-win-x64.exe"
$manifestPath = Join-Path $releaseRoot "release-manifest.json"

Write-Host "== 1/5 Build framework-dependent payload and Inno Setup installer =="
Invoke-DotNet @(
    "publish", $appProject,
    "--configuration", "Release",
    "--runtime", "win-x64",
    "--self-contained", "false",
    "-p:PublishSingleFile=false",
    "--output", $application
) "Application publish failed."

Assert-Payload $application $requiredPayload
Assert-SystemTextJsonRuntime $application
Assert-NativeArchitecture $application $nativeFiles
$sqliteVersion = Assert-SqliteVersion $application $minimumSqliteVersion

$appVersion = (Get-Item -LiteralPath (Join-Path $application $applicationExecutable)).VersionInfo.ProductVersion
if (-not $appVersion.StartsWith($version, [System.StringComparison]::Ordinal)) {
    throw "Application artifact version mismatch. Expected $version; app=$appVersion"
}
Write-Host "== 2/5 Create release artifacts =="
& $innoCompiler "/DAppSource=$application" "/DAppVersion=$version" "/DOutputDir=$releaseRoot" $installerScript
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed. ExitCode=$LASTEXITCODE" }
if (-not (Test-Path -LiteralPath $setupArtifact -PathType Leaf)) {
    throw "Inno Setup installer was not produced: $setupArtifact"
}

New-ReleaseZip -SourceDirectory $application -ArchivePath $frameworkZip

Write-Host "== 3/5 Validate artifact contents and versions =="
Assert-ZipPayload $frameworkZip $requiredPayload
$appMachine = Get-PortableExecutableMachine (Join-Path $application $applicationExecutable)
if ($appMachine -ne 0x8664) { throw "Application executable is not x64." }

$applicationPrefix = $application.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
$payloadFiles = foreach ($payloadFile in Get-ChildItem -LiteralPath $application -File -Recurse | Sort-Object FullName) {
    $payloadFullPath = [System.IO.Path]::GetFullPath($payloadFile.FullName)
    if (-not $payloadFullPath.StartsWith($applicationPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Payload file is outside the application directory: $payloadFullPath"
    }
    [ordered]@{
        path = $payloadFullPath.Substring($applicationPrefix.Length).Replace('\', '/')
        sha256 = (Get-FileHash -LiteralPath $payloadFullPath -Algorithm SHA256).Hash.ToLowerInvariant()
        bytes = $payloadFile.Length
    }
}

$releaseArtifacts = foreach ($artifactPath in @($frameworkZip, $setupArtifact)) {
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
    framework = "net48"
    sqliteVersion = $sqliteVersion
    payloadScope = "complete-framework-dependent-application-payload"
    generatedAtUtc = [DateTime]::UtcNow.ToString("O")
    payload = @($payloadFiles)
    artifacts = @($releaseArtifacts)
}
$manifestJson = $manifest | ConvertTo-Json -Depth 5
[System.IO.File]::WriteAllText($manifestPath, $manifestJson + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))

Write-Host "== 4/5 Verify recorded SHA-256 values =="
$savedManifest = [System.IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
if ($savedManifest.version -ne $version -or $savedManifest.framework -ne "net48") {
    throw "Release manifest runtime metadata mismatch."
}
if ($savedManifest.sqliteVersion -ne $sqliteVersion) { throw "Release manifest SQLite version mismatch." }
if (@($savedManifest.artifacts).Count -ne 2) { throw "Release manifest must record exactly the ZIP and installer." }
foreach ($payload in $savedManifest.payload) {
    if ($payload.path -match '[\\/]\.\.[\\/]' -or [System.IO.Path]::IsPathRooted($payload.path)) {
        throw "Unsafe payload path in release manifest: $($payload.path)"
    }
    $payloadPath = Join-Path $application ($payload.path.Replace('/', '\'))
    $actualHash = (Get-FileHash -LiteralPath $payloadPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $payload.sha256) {
        throw "Release payload hash mismatch: $($payload.path)"
    }
}

$releaseZip = [System.IO.Compression.ZipFile]::OpenRead($frameworkZip)
try {
    $zipEntries = @{}
    foreach ($entry in $releaseZip.Entries) {
        if ($entry.FullName.Contains('\')) {
            throw "Release ZIP contains non-portable entry name: $($entry.FullName)"
        }
        $zipEntries[$entry.FullName] = $entry
    }

    foreach ($payload in $savedManifest.payload) {
        if (-not $zipEntries.ContainsKey($payload.path)) {
            throw "Release ZIP is missing manifest payload: $($payload.path)"
        }
        $entry = $zipEntries[$payload.path]
        if ($entry.Length -ne $payload.bytes) {
            throw "Release ZIP payload size mismatch: $($payload.path)"
        }

        $entryStream = $entry.Open()
        try {
            $sha = [System.Security.Cryptography.SHA256]::Create()
            try {
                $actualHash = [System.BitConverter]::ToString($sha.ComputeHash($entryStream)).Replace('-', '').ToLowerInvariant()
            }
            finally {
                $sha.Dispose()
            }
        }
        finally {
            $entryStream.Dispose()
        }
        if ($actualHash -ne $payload.sha256) {
            throw "Release ZIP payload hash mismatch: $($payload.path)"
        }
    }
}
finally {
    $releaseZip.Dispose()
}
foreach ($artifact in $savedManifest.artifacts) {
    $artifactPath = Join-Path $releaseRoot $artifact.file
    $actualHash = (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $artifact.sha256) {
        throw "Release artifact hash mismatch: $($artifact.file)"
    }
}

Write-Host "== 5/5 Smoke-test the generated installer lifecycle =="
$smokeParent = Join-Path $staging "installer-smoke-parent"
$smokeTarget = Join-Path $smokeParent "LXMusicAccessible"
$parentSentinel = Join-Path $smokeParent "parent-sentinel.txt"
$dataSentinel = Join-Path $smokeParent "retained-user-data.txt"
$unknownInstallFile = Join-Path $smokeTarget "unknown-user-file.txt"
$installLog = Join-Path $smokeParent "install.log"
$uninstallLog = Join-Path $smokeParent "uninstall.log"
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

    $installArgs = @(
        "/VERYSILENT",
        "/SUPPRESSMSGBOXES",
        "/NORESTART",
        "/LOG=$installLog",
        "/DIR=$smokeTarget"
    )
    Invoke-Installer $setupArtifact $installArgs "First install smoke test failed."
    $installedExecutable = Join-Path $smokeTarget $applicationExecutable
    if (-not (Test-Path -LiteralPath $installedExecutable -PathType Leaf)) { throw "Installed application executable is missing." }
    if (-not (Test-Path -LiteralPath (Join-Path $smokeTarget $userDocuments[0]) -PathType Leaf)) { throw "Installed user document is missing." }

    Invoke-Installer $setupArtifact $installArgs "Upgrade smoke test failed."

    [System.IO.File]::WriteAllText($unknownInstallFile, "keep")
    $uninstallArgs = @(
        "/VERYSILENT",
        "/SUPPRESSMSGBOXES",
        "/NORESTART",
        "/LOG=$uninstallLog"
    )
    Invoke-Installer (Join-Path $smokeTarget "unins000.exe") $uninstallArgs "Preserving uninstall smoke test failed."
    if (-not (Test-Path -LiteralPath $unknownInstallFile -PathType Leaf) -or
        -not (Test-Path -LiteralPath $parentSentinel -PathType Leaf) -or
        -not (Test-Path -LiteralPath $dataSentinel -PathType Leaf)) {
        throw "Uninstall removed unknown, parent, or retained user data."
    }

    $completeParent = Join-Path $smokeParent "complete-install"
    $completeTarget = Join-Path $completeParent "LXMusicAccessible"
    $completeLog = Join-Path $smokeParent "complete-uninstall.log"
    $completeArgs = @(
        "/VERYSILENT",
        "/SUPPRESSMSGBOXES",
        "/NORESTART",
        "/LOG=$installLog",
        "/DIR=$completeTarget"
    )
    Invoke-Installer $setupArtifact $completeArgs "Complete install smoke test failed."
    Invoke-Installer (Join-Path $completeTarget "unins000.exe") $uninstallArgs "Complete uninstall smoke test failed."
    if (Test-Path -LiteralPath $completeTarget) { throw "Complete uninstall left the application directory behind." }
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

