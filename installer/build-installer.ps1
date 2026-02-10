param(
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",
    [Alias("RevitYear")]
    [ValidateSet("2024", "2025")]
    [string[]]$RevitYears = @("2024", "2025"),
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
$issPath = Join-Path $repoRoot "installer\RevitSuite.iss"
$stagingRoot = Join-Path $repoRoot "installer\staging"

if (-not (Test-Path $buildScript)) {
    throw "Build script not found: $buildScript"
}

if (-not (Test-Path $issPath)) {
    throw "Installer script not found: $issPath"
}

Write-Host "Building RevitSuite.Host ($Configuration)..." -ForegroundColor Cyan
if ($ApiDir -and $RevitYears.Count -ne 1) {
    throw "-ApiDir can only be used when building a single year. Use -RevitYears 2024 (or 2025) with -ApiDir."
}

if (Test-Path $stagingRoot) {
    Remove-Item -Recurse -Force $stagingRoot
}
New-Item -ItemType Directory -Path $stagingRoot | Out-Null

foreach ($year in ($RevitYears | Select-Object -Unique)) {
    Write-Host "Building RevitSuite.Host for Revit $year ($Configuration)..." -ForegroundColor Cyan
    if ($ApiDir) {
        & $buildScript -RevitYear $year -Configuration $Configuration -ApiDir $ApiDir
    }
    else {
        & $buildScript -RevitYear $year -Configuration $Configuration
    }

    $buildOutputDir = Join-Path $repoRoot ("src\RevitSuite.Host\bin\{0}\net48" -f $Configuration)
    if (-not (Test-Path (Join-Path $buildOutputDir "RevitSuite.Host.dll"))) {
        throw "Build output not found at $buildOutputDir"
    }

    $yearStagingDir = Join-Path $stagingRoot $year
    New-Item -ItemType Directory -Path $yearStagingDir -Force | Out-Null
    Copy-Item -Path (Join-Path $buildOutputDir "*") -Destination $yearStagingDir -Recurse -Force
}

$isccPath = Resolve-Iscc
Write-Host "Compiling installer with $isccPath..." -ForegroundColor Cyan
Push-Location (Join-Path $repoRoot "installer")
try {
    & $isccPath `
        ("/DSource2024=" + (Join-Path $stagingRoot "2024")) `
        ("/DSource2025=" + (Join-Path $stagingRoot "2025")) `
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
