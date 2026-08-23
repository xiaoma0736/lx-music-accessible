[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$root = $PSScriptRoot
$solution = Join-Path $root "src\LXMusicAccessible.sln"
$artifacts = Join-Path $root "artifacts"
$results = Join-Path $artifacts "local-validation"
$textCheck = Join-Path $root "scripts\check-text.ps1"

Push-Location $root
try {
    Write-Host "== 1/6 Restore =="
    & dotnet restore $solution
    if ($LASTEXITCODE -ne 0) { throw "Restore failed." }

    Write-Host "== 2/6 Audit High/Critical dependencies =="
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

    Write-Host "== 3/6 Build Release =="
    & dotnet build $solution --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Release build failed." }

    Write-Host "== 4/6 Run offline tests =="
    New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
    $resolvedArtifacts = (Resolve-Path -LiteralPath $artifacts).Path
    $resolvedResults = [System.IO.Path]::GetFullPath($results)
    if (-not $resolvedResults.StartsWith($resolvedArtifacts + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Test result directory is outside the repository artifacts directory."
    }
    if (Test-Path -LiteralPath $resolvedResults) {
        Remove-Item -LiteralPath $resolvedResults -Recurse -Force
    }

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
        & dotnet test $solution --configuration Release --no-build --no-restore --logger "trx;LogFileName=tests.trx" --results-directory $resolvedResults
        if ($LASTEXITCODE -ne 0) { throw "Tests failed." }
    }
    finally {
        foreach ($name in $proxyNames) {
            [Environment]::SetEnvironmentVariable($name, $savedProxyValues[$name], "Process")
        }
    }

    Write-Host "== 5/6 Require discovered tests =="
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
    if ($executed -lt 1) { throw "The test run discovered zero tests." }
    Write-Host "Executed tests: $executed"

    Write-Host "== 6/6 Text checks =="
    & git diff --check
    if ($LASTEXITCODE -ne 0) { throw "Git text checks failed." }
    & $textCheck
    if ($LASTEXITCODE -ne 0) { throw "Repository text checks failed." }

    Write-Host "Local quality gate passed."
}
finally {
    Pop-Location
}
