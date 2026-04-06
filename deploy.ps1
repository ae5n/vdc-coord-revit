param(
    [ValidateSet("2024", "2025", "2026")]
    [string]$RevitYear = "2026",
    [string]$ApiDir,
    [string]$Version
)

$ErrorActionPreference = "Stop"

$scriptPath = Join-Path $PSScriptRoot "build/scripts/deploy.ps1"
if ([string]::IsNullOrWhiteSpace($Version)) {
    & $scriptPath -RevitYear $RevitYear -ApiDir $ApiDir
}
else {
    & $scriptPath -RevitYear $RevitYear -ApiDir $ApiDir -Version $Version
}
