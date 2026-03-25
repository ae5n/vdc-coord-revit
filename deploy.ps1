param(
    [ValidateSet("2024", "2025", "2026")]
    [string]$RevitYear = "2026",
    [string]$ApiDir
)

$ErrorActionPreference = "Stop"

$scriptPath = Join-Path $PSScriptRoot "build/scripts/deploy.ps1"
& $scriptPath -RevitYear $RevitYear -ApiDir $ApiDir
