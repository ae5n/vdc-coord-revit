param(
    [string]$RevitYear = "2025"
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$buildScript = Join-Path $scriptRoot "build.ps1"
$installScript = Join-Path $scriptRoot "install.ps1"

Write-Host "Running build script..." -ForegroundColor Cyan
& $buildScript

Write-Host "Running install script..." -ForegroundColor Cyan
& $installScript -RevitYear $RevitYear

Write-Host "Deployment complete." -ForegroundColor Green
