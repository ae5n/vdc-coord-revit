param(
    [string]$RevitYear = "2025",
    [string]$ApiDir
)

$ErrorActionPreference = "Stop"

$scriptPath = Join-Path $PSScriptRoot "build/scripts/deploy.ps1"
& $scriptPath -RevitYear $RevitYear -ApiDir $ApiDir
