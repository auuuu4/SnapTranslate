<#
.SYNOPSIS
    编译并部署 SnapTranslate 到本机运行目录（默认 %LOCALAPPDATA%\Programs\SnapTranslate）。

.DESCRIPTION
    1. 调用 scripts/publish-windows.ps1 生成单文件自包含 exe
    2. 停掉正在运行的 SnapTranslate（占用的 exe 无法覆盖）
    3. 在部署目录建 backup-时间戳 备份，并只保留最近 KeepBackups 份（默认 3）
    4. 覆盖部署目录中的程序文件
    5. 重新启动

.EXAMPLE
    .\scripts\deploy-local.ps1
    .\scripts\deploy-local.ps1 -SkipBuild -NoRestart
#>
param(
    [ValidateSet("win-x64", "win-arm64")]
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release",
    [string]$DeployDir = (Join-Path $env:LOCALAPPDATA "Programs\SnapTranslate"),
    [int]$KeepBackups = 1,
    [switch]$SkipBuild,
    [switch]$NoRestart
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$exeName = "SnapTranslate.exe"

if (-not (Test-Path -LiteralPath $DeployDir)) {
    New-Item -ItemType Directory -Force -Path $DeployDir | Out-Null
}

$publishDir = Join-Path $root "artifacts\windows\$Runtime"
if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot "publish-windows.ps1") -Runtime $Runtime -Configuration $Configuration | Out-Host
}
$sourceExe = Join-Path $publishDir $exeName
if (-not (Test-Path -LiteralPath $sourceExe)) {
    throw "找不到发布产物：$sourceExe（先不带 -SkipBuild 跑一次）"
}

# 1. 停掉正在运行的实例，否则 exe 被占用无法覆盖
$running = Get-Process -Name "SnapTranslate" -ErrorAction SilentlyContinue
foreach ($process in $running) {
    Write-Host "停止运行中的 SnapTranslate (PID $($process.Id))"
    $null = $process.CloseMainWindow()
    if (-not $process.WaitForExit(5000)) {
        Write-Host "  未在 5 秒内退出，强制结束"
        Stop-Process -Id $process.Id -Force
        $process.WaitForExit(5000) | Out-Null
    }
}

# 2. 备份当前部署，保留原有的 backup-yyyyMMdd-HHmmss 习惯
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$backupDir = Join-Path $DeployDir "backup-$stamp"
$targetExe = Join-Path $DeployDir $exeName
if (Test-Path -LiteralPath $targetExe) {
    New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
    Get-ChildItem -LiteralPath $DeployDir -File | Copy-Item -Destination $backupDir -Force
    Write-Host "已备份当前部署 → $backupDir"
}

# 2.1 只保留最近 N 份备份（默认 3），避免每份 190 MB 的 exe 无限堆积
$backups = @(Get-ChildItem -LiteralPath $DeployDir -Directory -Filter "backup-*" | Sort-Object LastWriteTime)
if ($backups.Count -gt $KeepBackups) {
    $backups | Select-Object -First ($backups.Count - $KeepBackups) | ForEach-Object {
        Write-Host "清理旧备份 $($_.Name)"
        Remove-Item -LiteralPath $_.FullName -Recurse -Force
    }
}

# 3. 覆盖部署（exe / pdb / xml 一起同步，保持调试符号一致）
Get-ChildItem -LiteralPath $publishDir -File | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $DeployDir $_.Name) -Force
    Write-Host "部署 $($_.Name)"
}

# 4. 重启
if (-not $NoRestart) {
    Start-Process -FilePath $targetExe -WorkingDirectory $DeployDir
    Write-Host "已启动：$targetExe"
} else {
    Write-Host "已部署，未启动（-NoRestart）"
}
