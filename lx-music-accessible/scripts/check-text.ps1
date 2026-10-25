[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$textExtensions = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($extension in @(
    ".cs", ".csproj", ".js", ".json", ".manifest", ".md", ".props", ".ps1",
    ".sln", ".targets", ".txt", ".xaml", ".xml", ".yaml", ".yml"
)) {
    [void]$textExtensions.Add($extension)
}
$textNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($name in @(".editorconfig", ".gitattributes", ".gitignore")) {
    [void]$textNames.Add($name)
}

# Private-use/replacement characters, long question-mark runs, and known fragments from UTF-8 text decoded as a legacy code page.
$damagePattern = [regex]::new(
    '[\uE000-\uF8FF\uFFFD]|\?{4,}|\u951B|\u9286|\u9225|\u9428|\u934F\u3129\u5134|\u95CA\u5145\u7BB0|\u59DD\u5C83\u761D|\u7481\u5267\u7586|\u947E\u5CF0\u5F47',
    [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
$strictUtf8 = [System.Text.UTF8Encoding]::new($false, $true)
$failures = [System.Collections.Generic.List[string]]::new()
$checked = 0

Push-Location $root
try {
    $trackedFiles = @(& git -c core.quotepath=false ls-files)
    if ($LASTEXITCODE -ne 0) { throw "Could not enumerate tracked files." }

    foreach ($relativePath in $trackedFiles) {
        $fileName = [System.IO.Path]::GetFileName($relativePath)
        $extension = [System.IO.Path]::GetExtension($relativePath)
        if (-not $textExtensions.Contains($extension) -and -not $textNames.Contains($fileName)) {
            continue
        }

        $path = Join-Path $root ($relativePath -replace '/', [System.IO.Path]::DirectorySeparatorChar)
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            continue
        }

        $checked++
        try {
            $text = $strictUtf8.GetString([System.IO.File]::ReadAllBytes($path))
        }
        catch {
            $failures.Add("${relativePath}: invalid UTF-8")
            continue
        }

        $lineNumber = 0
        foreach ($line in $text -split "\r?\n") {
            $lineNumber++
            if ($damagePattern.IsMatch($line)) {
                $failures.Add("${relativePath}:${lineNumber}: replacement character or known encoding damage")
            }
        }

        if ($text.EndsWith([Environment]::NewLine + [Environment]::NewLine) -or $text.EndsWith([char]10 + [char]10)) {
            $failures.Add("${relativePath}: blank line at EOF")
        }
    }
}
finally {
    Pop-Location
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ -ErrorAction Continue }
    exit 1
}

Write-Host "Text encoding check passed ($checked tracked text files)."
