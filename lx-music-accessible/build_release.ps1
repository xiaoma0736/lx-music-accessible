$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
Add-Type -AssemblyName System.IO.Compression.FileSystem

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
    $userDocuments[0]
)

$selfContainedZip = Join-Path $releaseRoot "LXMusicAccessible-$version-win-x64-self-contained.zip"
$frameworkDependentZip = Join-Path $releaseRoot "LXMusicAccessible-$version-win-x64-framework-dependent.zip"
$setupArtifact = Join-Path $releaseRoot "LXMusicAccessible-Setup-$version-win-x64.exe"
$manifestPath = Join-Path $releaseRoot "release-manifest.json"

foreach ($knownOutput in @($selfContainedZip, $frameworkDependentZip, $setupArtifact, $manifestPath)) {
    if (Test-Path -LiteralPath $knownOutput) { Remove-Item -LiteralPath $knownOutput -Force }
}

Write-Host "== 1/5 Build installer and its explicit self-contained payload =="
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

$appVersion = (Get-Item -LiteralPath (Join-Path $selfContained $applicationExecutable)).VersionInfo.ProductVersion
$setupVersion = (Get-Item -LiteralPath $setupExecutable).VersionInfo.ProductVersion
if (-not $appVersion.StartsWith($version, [System.StringComparison]::Ordinal) -or
    -not $setupVersion.StartsWith($version, [System.StringComparison]::Ordinal)) {
    throw "Artifact version mismatch. Expected $version; app=$appVersion; setup=$setupVersion"
}

Write-Host "== 2/5 Build framework-dependent application =="
Invoke-DotNet @(
    "publish", $appProject,
    "--configuration", "Release",
    "--runtime", "win-x64",
    "--self-contained", "false",
    "--output", $frameworkDependent
) "Framework-dependent publish failed."
Assert-Payload $frameworkDependent $requiredPayload

Write-Host "== 3/5 Create release artifacts =="
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

Write-Host "== 4/5 Validate artifact contents and versions =="
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
    generatedAtUtc = [DateTime]::UtcNow.ToString("O")
    payload = @($payloadFiles)
    artifacts = @($releaseArtifacts)
}
$manifestJson = $manifest | ConvertTo-Json -Depth 5
[System.IO.File]::WriteAllText($manifestPath, $manifestJson + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))

Write-Host "== 5/5 Verify recorded SHA-256 values =="
$savedManifest = [System.IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
if ($savedManifest.version -ne $version) { throw "Release manifest version mismatch." }
foreach ($artifact in $savedManifest.artifacts) {
    $artifactPath = Join-Path $releaseRoot $artifact.file
    $actualHash = (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $artifact.sha256) {
        throw "Release artifact hash mismatch: $($artifact.file)"
    }
}

Write-Host "Release build passed: $releaseRoot"
Get-ChildItem -LiteralPath $releaseRoot -File | Select-Object Name, Length
