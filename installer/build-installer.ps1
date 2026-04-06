param(
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",
    [Alias("RevitYear")]
    [ValidateSet("2024", "2025", "2026")]
    [string[]]$RevitYears = @("2024", "2025", "2026"),
    [string]$ApiDir,
    [string]$Version
)

$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "..\build\scripts\versioning.ps1")

function Get-DefaultVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot
    )

    $propsPath = Join-Path $RepoRoot "Directory.Build.props"
    if (-not (Test-Path $propsPath)) {
        throw "Version file not found: $propsPath"
    }

    [xml]$props = Get-Content $propsPath
    $versionNode = $props.Project.PropertyGroup.Version | Select-Object -First 1
    $resolvedVersion = $versionNode.'#text'

    if ([string]::IsNullOrWhiteSpace($resolvedVersion)) {
        throw "Could not resolve default version from $propsPath"
    }

    return $resolvedVersion.Trim()
}

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

function New-StagingRoot {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot
    )

    $preferred = Join-Path $RepoRoot "installer\staging"
    $tempFallback = Join-Path ([System.IO.Path]::GetTempPath()) ("RevitSuite-installer-staging-{0}" -f ([guid]::NewGuid().ToString("N")))

    if (Test-Path $preferred) {
        try {
            Remove-Item -Recurse -Force $preferred -ErrorAction Stop
        }
        catch {
            Write-Warning "Could not reset installer\\staging. Falling back to temp staging outside the repo."
            New-Item -ItemType Directory -Path $tempFallback | Out-Null
            return $tempFallback
        }
    }

    New-Item -ItemType Directory -Path $preferred | Out-Null
    return $preferred
}

$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$resolvedVersion = if ([string]::IsNullOrWhiteSpace($Version)) { Get-DefaultVersion -RepoRoot $repoRoot } else { $Version }
$buildScript = Join-Path $repoRoot "build\scripts\build.ps1"
$installScript = Join-Path $repoRoot "build\scripts\install.ps1"
$issPath = Join-Path $repoRoot "installer\RevitSuite.iss"
$stagingRoot = New-StagingRoot -RepoRoot $repoRoot
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

New-Item -ItemType Directory -Path $stagingAddinsRoot | Out-Null

try {
    foreach ($year in ($RevitYears | Select-Object -Unique)) {
        Write-Host "Building payload for Revit $year ($Configuration, version $resolvedVersion)..." -ForegroundColor Cyan
        if ($ApiDir) {
            & $buildScript -RevitYear $year -Configuration $Configuration -ApiDir $ApiDir -Version $resolvedVersion
        }
        else {
            & $buildScript -RevitYear $year -Configuration $Configuration -Version $resolvedVersion
        }

        & $installScript -RevitYear $year -Configuration $Configuration -TargetRoot $stagingAddinsRoot
    }
    
    $isccPath = Resolve-Iscc
    Write-Host "Compiling installer with $isccPath..." -ForegroundColor Cyan
    Push-Location (Join-Path $repoRoot "installer")
    try {
        & $isccPath `
            ("/DAppVersion=" + $resolvedVersion) `
            ("/DSetupBaseName=RevitSuite-Setup-" + $resolvedVersion) `
            ("/DSource2024=" + (Join-Path $stagingAddinsRoot "2024\RevitSuite")) `
            ("/DSource2025=" + (Join-Path $stagingAddinsRoot "2025\RevitSuite")) `
            ("/DSource2026=" + (Join-Path $stagingAddinsRoot "2026\RevitSuite")) `
            $issPath
    }
    finally {
        Pop-Location
    }

    $outPath = Join-Path $repoRoot ("installer\out\RevitSuite-Setup-{0}.exe" -f $resolvedVersion)
    if (Test-Path $outPath) {
        Write-Host "Installer created: $outPath" -ForegroundColor Green
    }
    else {
        Write-Warning "Installer build completed but expected output not found at $outPath"
    }
}
finally {
    if (Test-Path $stagingRoot) {
        try {
            Remove-Item -Recurse -Force $stagingRoot -ErrorAction Stop
        }
        catch {
            Write-Warning "Could not remove temporary staging folder: $stagingRoot"
        }
    }
}
