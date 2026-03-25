param(
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",
    [Alias("RevitYear")]
    [ValidateSet("2024", "2025", "2026")]
    [string[]]$RevitYears = @("2024", "2025", "2026"),
    [string]$ApiDir
)

$ErrorActionPreference = "Stop"

function Resolve-Iscc {
    $iscc = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
    if ($iscc) {
        return $iscc.Source
    }

    $default = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    if (Test-Path $default) {
        return $default
    }

    throw "ISCC.exe not found. Install Inno Setup 6 and ensure ISCC.exe is in PATH."
}

$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$buildScript = Join-Path $repoRoot "build\scripts\build.ps1"
$installScript = Join-Path $repoRoot "build\scripts\install.ps1"
$issPath = Join-Path $repoRoot "installer\RevitSuite.iss"
$stagingRoot = Join-Path $repoRoot "installer\staging"
$stagingAddinsRoot = Join-Path $stagingRoot "Addins"

if (-not (Test-Path $buildScript)) {
    throw "Build script not found: $buildScript"
}

if (-not (Test-Path $installScript)) {
    throw "Install script not found: $installScript"
}

if (-not (Test-Path $issPath)) {
    throw "Installer script not found: $issPath"
}

if ($ApiDir -and $RevitYears.Count -ne 1) {
    throw "-ApiDir can only be used when building a single year. Use -RevitYears 2024 with -ApiDir if you need custom API DLLs."
}

if (Test-Path $stagingRoot) {
    Remove-Item -Recurse -Force $stagingRoot
}
New-Item -ItemType Directory -Path $stagingRoot | Out-Null
New-Item -ItemType Directory -Path $stagingAddinsRoot | Out-Null

foreach ($year in ($RevitYears | Select-Object -Unique)) {
    Write-Host "Building payload for Revit $year ($Configuration)..." -ForegroundColor Cyan
    if ($ApiDir) {
        & $buildScript -RevitYear $year -Configuration $Configuration -ApiDir $ApiDir
    }
    else {
        & $buildScript -RevitYear $year -Configuration $Configuration
    }

    & $installScript -RevitYear $year -Configuration $Configuration -TargetRoot $stagingAddinsRoot
}

$isccPath = Resolve-Iscc
Write-Host "Compiling installer with $isccPath..." -ForegroundColor Cyan
Push-Location (Join-Path $repoRoot "installer")
try {
    & $isccPath `
        ("/DSource2024=" + (Join-Path $stagingAddinsRoot "2024\RevitSuite")) `
        ("/DSource2025=" + (Join-Path $stagingAddinsRoot "2025\RevitSuite")) `
        ("/DSource2026=" + (Join-Path $stagingAddinsRoot "2026\RevitSuite")) `
        $issPath
}
finally {
    Pop-Location
}

$outPath = Join-Path $repoRoot "installer\out\RevitSuite-Setup.exe"
if (Test-Path $outPath) {
    Write-Host "Installer created: $outPath" -ForegroundColor Green
}
else {
    Write-Warning "Installer build completed but expected output not found at $outPath"
}
