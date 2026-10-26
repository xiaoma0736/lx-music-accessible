[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$root = $PSScriptRoot
$solution = Join-Path $root "src\LXMusicAccessible.sln"
$artifacts = Join-Path $root "artifacts"
$results = Join-Path $artifacts "local-validation"
$textCheck = Join-Path $root "scripts\check-text.ps1"
$releaseBuild = Join-Path $root "build_release.ps1"

Push-Location $root
try {
    Write-Host "== 1/7 Restore =="
    # A reusable MSBuild node can outlive a confined runner and make --force-evaluate return 1
    # without an error line. Keep the gate deterministic in clean CI and sandboxed agents.
    & dotnet restore $solution --force-evaluate -nodeReuse:false
    if ($LASTEXITCODE -ne 0) { throw "Restore failed." }

    Write-Host "== 2/7 Audit High/Critical dependencies =="
    $auditOutput = & dotnet list $solution package --vulnerable --include-transitive --format json
    if ($LASTEXITCODE -ne 0) { throw "Dependency audit failed." }

    $audit = ($auditOutput -join [Environment]::NewLine) | ConvertFrom-Json
    $blockedVulnerabilities = @(
        foreach ($project in @($audit.projects)) {
            foreach ($framework in @($project.frameworks)) {
                foreach ($package in @($framework.topLevelPackages) + @($framework.transitivePackages)) {
                    foreach ($vulnerability in @($package.vulnerabilities)) {
                        if ($vulnerability.severity -in @("High", "Critical")) {
                            [pscustomobject]@{
                                Project = $project.path
                                Package = $package.id
                                Version = $package.resolvedVersion
                                Severity = $vulnerability.severity
                                Advisory = $vulnerability.advisoryurl
                            }
                        }
                    }
                }
            }
        }
    )
    if ($blockedVulnerabilities.Count -gt 0) {
        $blockedVulnerabilities | Format-Table -AutoSize | Out-Host
        throw "Dependency audit found High/Critical vulnerabilities."
    }
    Write-Host "High/Critical vulnerabilities: 0"

    Write-Host "== 3/7 Build Release =="
    & dotnet build $solution --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Release build failed." }

    Write-Host "== 4/7 Run offline tests =="
    New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
    $resolvedArtifacts = (Resolve-Path -LiteralPath $artifacts).Path
    $resolvedResults = [System.IO.Path]::GetFullPath($results)
    if (-not $resolvedResults.StartsWith($resolvedArtifacts + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Test result directory is outside the repository artifacts directory."
    }
    if (Test-Path -LiteralPath $resolvedResults) {
        Remove-Item -LiteralPath $resolvedResults -Recurse -Force
    }
    New-Item -ItemType Directory -Path $resolvedResults -Force | Out-Null

    $proxyNames = @("HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY")
    $savedProxyValues = @{}
    foreach ($name in $proxyNames) {
        $savedProxyValues[$name] = [Environment]::GetEnvironmentVariable($name, "Process")
    }
    try {
        $env:HTTP_PROXY = "http://127.0.0.1:9"
        $env:HTTPS_PROXY = "http://127.0.0.1:9"
        $env:ALL_PROXY = "http://127.0.0.1:9"
        $env:NO_PROXY = ""
        # Redirect the native test host output to a file. Plain piping can deadlock an STA
        # testhost when the gate itself is launched by another capture-only PowerShell host.
        $testConsoleLog = Join-Path $resolvedResults "test-console.log"
        & dotnet test $solution --configuration Release --no-build --no-restore --logger "trx;LogFileName=tests.trx" --results-directory $resolvedResults *> $testConsoleLog
        $testExitCode = $LASTEXITCODE
        if (Test-Path -LiteralPath $testConsoleLog) {
            Get-Content -LiteralPath $testConsoleLog | Write-Host
        }
        if ($testExitCode -ne 0) { throw "Tests failed." }
    }
    finally {
        foreach ($name in $proxyNames) {
            [Environment]::SetEnvironmentVariable($name, $savedProxyValues[$name], "Process")
        }
    }

    Write-Host "== 5/7 Require discovered tests =="
    $resultFiles = @(Get-ChildItem -LiteralPath $resolvedResults -Filter *.trx -Recurse)
    if ($resultFiles.Count -eq 0) { throw "No test result files were produced." }

    $executed = 0
    foreach ($resultFile in $resultFiles) {
        [xml]$result = Get-Content -Raw -LiteralPath $resultFile.FullName
        $counters = Select-Xml -Xml $result -XPath "//*[local-name()='Counters']" | Select-Object -First 1
        if ($null -ne $counters) {
            $executed += [int]$counters.Node.executed
        }
    }
    # Guard against silently losing compiled suites as well as a zero-test discovery.
    $minimumExecutedTests = 780
    if ($executed -lt $minimumExecutedTests) {
        throw "Only $executed tests executed; the release gate requires at least $minimumExecutedTests."
    }
    Write-Host "Executed tests: $executed"

    Write-Host "== 6/7 Text checks =="
    & git diff --check
    if ($LASTEXITCODE -ne 0) { throw "Git text checks failed." }
    & $textCheck
    if ($LASTEXITCODE -ne 0) { throw "Repository text checks failed." }

    $sourceFiles = @(Get-ChildItem -LiteralPath (Join-Path $root "src") -Filter *.cs -File -Recurse |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' })
    $locatorMatches = @($sourceFiles | Select-String -Pattern @(
        '\bBuildServiceProvider\s*\(',
        '\bstatic\s+(?:readonly\s+)?IServiceProvider\b',
        '\bServiceLocator\b'))
    if ($locatorMatches.Count -gt 0) {
        $locatorMatches | ForEach-Object { Write-Host "$($_.Path):$($_.LineNumber): $($_.Line.Trim())" }
        throw "Static service locator or second container construction found."
    }

    $emptyCatchFiles = @(
        foreach ($sourceFile in $sourceFiles) {
            $source = [System.IO.File]::ReadAllText($sourceFile.FullName)
            if ($source -match '\bcatch\s*(?:\([^{}]*\))?\s*(?:when\s*\([^{}]*\))?\s*\{\s*\}') {
                $sourceFile.FullName
            }
        }
    )
    if ($emptyCatchFiles.Count -gt 0) {
        $emptyCatchFiles | ForEach-Object { Write-Host $_ }
        throw "Empty catch block found. Record an expected fallback or a redacted failure."
    }

    $shellSource = Get-Content -Raw -LiteralPath (Join-Path $root "src\LXMusicAccessible.App\WinFormsShell.Workflows.cs")
    if ($shellSource -match '\bIServiceProvider\b') {
        throw "Shell must not hold or accept a general IServiceProvider."
    }
    Write-Host "Host composition and exception diagnostics checks passed ($($sourceFiles.Count) C# source files)."

    Write-Host "== 7/7 Clean release and installer verification =="
    & $releaseBuild
    if ($LASTEXITCODE -ne 0) { throw "Release verification failed." }

    Write-Host "Local quality gate passed."
}
finally {
    Pop-Location
}

# Native child exit codes (notably Inno's uninstaller) can otherwise leak into the wrapper.
exit 0
