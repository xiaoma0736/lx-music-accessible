param(
    [Parameter(Mandatory = $true)][string]$InstallDirectory,
    [Parameter(Mandatory = $true)][Guid]$OwnershipId,
    [int]$WaitForProcessId = 0
)

$ErrorActionPreference = "Stop"

function Get-NormalizedPath([string]$Path) {
    $root = [System.IO.Path]::GetPathRoot($Path)
    if (-not [System.IO.Path]::IsPathRooted($Path) -or
        [string]::IsNullOrWhiteSpace($root) -or
        -not $root.EndsWith([System.IO.Path]::DirectorySeparatorChar)) {
        throw "Install path is not absolute."
    }
    return [System.IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
}

function Assert-NoReparsePoint([string]$Path) {
    $current = Get-Item -LiteralPath $Path -Force
    while ($null -ne $current) {
        if ($current.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
            throw "Cleanup path contains a reparse point."
        }
        if ($current -is [System.IO.FileInfo]) {
            $current = $current.Directory
        }
        else {
            $current = $current.Parent
        }
    }
}

function Get-OwnedFilePath([string]$Root, [string]$RelativePath) {
    if ([string]::IsNullOrWhiteSpace($RelativePath) -or [System.IO.Path]::IsPathRooted($RelativePath)) {
        throw "Manifest contains an invalid file path."
    }
    $path = [System.IO.Path]::GetFullPath((Join-Path $Root $RelativePath))
    $prefix = $Root + [System.IO.Path]::DirectorySeparatorChar
    if (-not $path.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Manifest file escapes the install directory."
    }
    return $path
}

try {
    if ($WaitForProcessId -gt 0) {
        Wait-Process -Id $WaitForProcessId -ErrorAction SilentlyContinue
    }

    $target = Get-NormalizedPath $InstallDirectory
    $root = [System.IO.Path]::GetPathRoot($target).TrimEnd('\', '/')
    if ([string]::Equals($target, $root, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Cleanup refuses a drive root."
    }
    if (-not (Test-Path -LiteralPath $target -PathType Container)) { exit 0 }
    Assert-NoReparsePoint $target

    $protected = @(
        [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile),
        [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory),
        [Environment]::GetFolderPath([Environment+SpecialFolder]::MyDocuments),
        [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData),
        [Environment]::GetFolderPath([Environment+SpecialFolder]::Windows),
        [Environment]::GetFolderPath([Environment+SpecialFolder]::System),
        [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles),
        [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86),
        [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { Get-NormalizedPath $_ }
    if ($protected -contains $target) { throw "Cleanup refuses a protected directory." }

    $windows = Get-NormalizedPath ([Environment]::GetFolderPath([Environment+SpecialFolder]::Windows))
    $system = Get-NormalizedPath ([Environment]::GetFolderPath([Environment+SpecialFolder]::System))
    $targetPrefix = $target + [System.IO.Path]::DirectorySeparatorChar
    if ($targetPrefix.StartsWith($windows + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase) -or
        $targetPrefix.StartsWith($system + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Cleanup refuses a system directory."
    }

    $markerPath = Join-Path $target ".lxmusic-install-owner.json"
    $manifestPath = Join-Path $target ".lxmusic-install-manifest.json"
    if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "Installation ownership metadata is missing."
    }

    $marker = [System.IO.File]::ReadAllText($markerPath) | ConvertFrom-Json
    $manifest = [System.IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
    if ($marker.ApplicationId -ne "LXMusicAccessible" -or $manifest.ApplicationId -ne "LXMusicAccessible" -or
        [int]$marker.SchemaVersion -ne 1 -or [int]$manifest.SchemaVersion -ne 1 -or
        [Guid]$marker.OwnershipId -ne $OwnershipId -or [Guid]$manifest.OwnershipId -ne $OwnershipId -or
        -not [string]::Equals((Get-NormalizedPath $marker.DirectoryPath), $target, [System.StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals((Get-NormalizedPath $manifest.DirectoryPath), $target, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Installation ownership metadata does not match."
    }

    $directories = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($file in @($manifest.Files)) {
        $path = Get-OwnedFilePath $target ([string]$file.RelativePath)
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
        Assert-NoReparsePoint $path
        $info = Get-Item -LiteralPath $path -Force
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        if ($info.Length -ne [long]$file.Length -or
            -not [string]::Equals($hash, [string]$file.Sha256, [System.StringComparison]::OrdinalIgnoreCase)) {
            continue
        }
        Remove-Item -LiteralPath $path -Force
        $directory = $info.Directory
        while ($null -ne $directory -and
               -not [string]::Equals($directory.FullName, $target, [System.StringComparison]::OrdinalIgnoreCase)) {
            [void]$directories.Add($directory.FullName)
            $directory = $directory.Parent
        }
    }

    foreach ($directory in @($directories) | Sort-Object Length -Descending) {
        if ((Test-Path -LiteralPath $directory -PathType Container) -and
            -not (Get-ChildItem -LiteralPath $directory -Force | Select-Object -First 1)) {
            Remove-Item -LiteralPath $directory -Force
        }
    }

    $remaining = @(Get-ChildItem -LiteralPath $target -Force)
    $unknown = @($remaining | Where-Object { $_.FullName -notin @($markerPath, $manifestPath) })
    if ($unknown.Count -eq 0) {
        Remove-Item -LiteralPath $manifestPath -Force
        Remove-Item -LiteralPath $markerPath -Force
        Remove-Item -LiteralPath $target -Force
    }
}
finally {
    Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
}
