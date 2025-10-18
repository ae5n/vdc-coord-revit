param(
    [string]$RevitYear = "2025"
)

$ErrorActionPreference = "Stop"

$scriptPath = Join-Path $PSScriptRoot "build/scripts/deploy.ps1"
& $scriptPath -RevitYear $RevitYear
