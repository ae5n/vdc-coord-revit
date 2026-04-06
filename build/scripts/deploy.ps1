param(
    [ValidateSet("2024", "2025", "2026")]
    [string]$RevitYear = "2026",
    [string]$ApiDir,
    [string]$Version = "0.1.0-beta.1"
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$buildScript = Join-Path $scriptRoot "build.ps1"
$installScript = Join-Path $scriptRoot "install.ps1"

Write-Host "Running build script..." -ForegroundColor Cyan
& $buildScript -RevitYear $RevitYear -ApiDir $ApiDir -Version $Version

Write-Host "Running install script..." -ForegroundColor Cyan
& $installScript -RevitYear $RevitYear

Write-Host "Deployment complete." -ForegroundColor Green
