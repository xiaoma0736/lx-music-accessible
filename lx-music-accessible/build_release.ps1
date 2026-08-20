# 落雪音乐无障碍版 V2.0 发布构建脚本
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $root "src"
$pubRoot = Join-Path $root "发布"
$selfContained = Join-Path $root "_staging_selfcontained"
$fxDep = Join-Path $root "_staging_fxdep"
$setupOut = Join-Path $root "_staging_setup"
$fxDepTarget = Join-Path $pubRoot "落雪音乐无障碍版-便携版(不含.NET)"
$scTarget = Join-Path $pubRoot "落雪音乐无障碍版-便携版(含.NET8)"
$setupTarget = Join-Path $pubRoot "落雪音乐无障碍版-完整安装版"
$payload = Join-Path $src "LXMusicAccessible.Setup\payload.zip"
$zipNames = @("落雪音乐无障碍版-便携版(不含.NET).zip", "落雪音乐无障碍版-便携版(含.NET8).zip", "落雪音乐无障碍版-完整安装版.zip")

$resolvedRoot = (Resolve-Path $pubRoot).Path
foreach ($t in @($fxDepTarget, $scTarget, $setupTarget)) {
    if (-not $t.StartsWith($resolvedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Target outside release root: $t"
    }
}

Write-Host "== 1/5 发布自包含(含.NET8) =="
if (Test-Path $selfContained) { Remove-Item -LiteralPath $selfContained -Recurse -Force }
& dotnet publish (Join-Path $src "LXMusicAccessible.App") -c Release -r win-x64 --self-contained true -o $selfContained | Out-Null
if ($LASTEXITCODE -ne 0) { throw "self-contained publish failed" }

Write-Host "== 2/5 发布框架依赖(不含.NET) =="
if (Test-Path $fxDep) { Remove-Item -LiteralPath $fxDep -Recurse -Force }
& dotnet publish (Join-Path $src "LXMusicAccessible.App") -c Release --self-contained false -o $fxDep | Out-Null
if ($LASTEXITCODE -ne 0) { throw "framework-dependent publish failed" }

Write-Host "== 3/5 组装便携版目录 =="
foreach ($d in @($fxDepTarget, $scTarget)) {
    if (Test-Path $d) { Remove-Item -LiteralPath $d -Recurse -Force }
    New-Item -ItemType Directory -Path $d -Force | Out-Null
}
Copy-Item -Path (Join-Path $fxDep "*") -Destination $fxDepTarget -Recurse -Force
Copy-Item -Path (Join-Path $selfContained "*") -Destination $scTarget -Recurse -Force

Write-Host "== 4/5 生成payload.zip并构建安装程序(自包含单文件) =="
if (Test-Path $payload) { Remove-Item -LiteralPath $payload -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($selfContained, $payload, [System.IO.Compression.CompressionLevel]::Optimal, $false)
if (Test-Path $setupOut) { Remove-Item -LiteralPath $setupOut -Recurse -Force }
& dotnet publish (Join-Path $src "LXMusicAccessible.Setup") -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $setupOut | Out-Null
if ($LASTEXITCODE -ne 0) { throw "setup publish failed" }
$setupExe = Join-Path $setupOut "LXMusicAccessible.Setup.exe"
if (-not (Test-Path $setupExe)) { throw "setup exe not found" }
if (-not (Test-Path $setupTarget)) { New-Item -ItemType Directory -Path $setupTarget -Force | Out-Null }
Get-ChildItem -LiteralPath $setupTarget -File | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
Copy-Item -LiteralPath $setupExe -Destination (Join-Path $setupTarget "落雪音乐无障碍版Setup.exe") -Force

Write-Host "== 5/5 生成压缩包 =="
foreach ($zn in $zipNames) {
    $zp = Join-Path $pubRoot $zn
    if (Test-Path $zp) { Remove-Item -LiteralPath $zp -Force }
}
[System.IO.Compression.ZipFile]::CreateFromDirectory($fxDepTarget, (Join-Path $pubRoot $zipNames[0]), [System.IO.Compression.CompressionLevel]::Optimal, $true)
[System.IO.Compression.ZipFile]::CreateFromDirectory($scTarget, (Join-Path $pubRoot $zipNames[1]), [System.IO.Compression.CompressionLevel]::Optimal, $true)
[System.IO.Compression.ZipFile]::CreateFromDirectory($setupTarget, (Join-Path $pubRoot $zipNames[2]), [System.IO.Compression.CompressionLevel]::Optimal, $true)

Write-Host "== 完成 =="
Get-ChildItem -LiteralPath $pubRoot | Select-Object Name, Length