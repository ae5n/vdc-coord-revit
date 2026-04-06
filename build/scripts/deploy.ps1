param(
    [ValidateSet("2024", "2025", "2026")]
    [string]$RevitYear = "2026",
    [string]$ApiDir,
    [string]$Version
)

$ErrorActionPreference = "Stop"

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

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent (Split-Path -Parent $scriptRoot)
$buildScript = Join-Path $scriptRoot "build.ps1"
$installScript = Join-Path $scriptRoot "install.ps1"
$resolvedVersion = if ([string]::IsNullOrWhiteSpace($Version)) { Get-DefaultVersion -RepoRoot $repoRoot } else { $Version }

Write-Host "Running build script..." -ForegroundColor Cyan
& $buildScript -RevitYear $RevitYear -ApiDir $ApiDir -Version $resolvedVersion

Write-Host "Running install script..." -ForegroundColor Cyan
& $installScript -RevitYear $RevitYear

Write-Host "Deployment complete." -ForegroundColor Green
