param(
    [string]$RevitYear,
    [string]$ApiDir
)

$ErrorActionPreference = "Stop"

function Resolve-RevitApiDir {
    param(
        [string]$Year,
        [string]$ExplicitPath
    )

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        return $ExplicitPath
    }

    if ([string]::IsNullOrWhiteSpace($Year)) {
        return $null
    }

    $programFiles = ${env:ProgramFiles}
    if ([string]::IsNullOrWhiteSpace($programFiles)) {
        return $null
    }

    $candidate = Join-Path $programFiles ("Autodesk\Revit $Year")
    if (Test-Path (Join-Path $candidate "RevitAPI.dll")) {
        return $candidate
    }

    return $null
}

$resolvedApiDir = Resolve-RevitApiDir -Year $RevitYear -ExplicitPath $ApiDir
$previousApiDir = $env:REVIT_API_DIR
$apiDirMessage = $null

try {
    if ($resolvedApiDir) {
        if (-not (Test-Path (Join-Path $resolvedApiDir "RevitAPI.dll")) -or
            -not (Test-Path (Join-Path $resolvedApiDir "RevitAPIUI.dll"))) {
            throw "Resolved Revit API directory '$resolvedApiDir' does not contain RevitAPI.dll/RevitAPIUI.dll."
        }

        $env:REVIT_API_DIR = $resolvedApiDir
        $apiDirMessage = "Using REVIT_API_DIR='$resolvedApiDir'"
    }
    elseif (-not $env:REVIT_API_DIR) {
        Write-Warning "REVIT_API_DIR not set. Set it explicitly or pass -RevitYear/-ApiDir so the build can locate RevitAPI.dll."
    }
    else {
        $apiDirMessage = "Using existing REVIT_API_DIR='$($env:REVIT_API_DIR)'"
    }

    if ($apiDirMessage) {
        Write-Host $apiDirMessage -ForegroundColor DarkCyan
    }

    $repoRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path))
    $projectPath = Join-Path $repoRoot "src/RevitSuite.Host/RevitSuite.Host.csproj"

    Write-Host "Building RevitSuite.Host..." -ForegroundColor Cyan
    dotnet build $projectPath -c Release
}
finally {
    if ($resolvedApiDir) {
        if ($previousApiDir) {
            $env:REVIT_API_DIR = $previousApiDir
        }
        else {
            Remove-Item Env:\REVIT_API_DIR -ErrorAction SilentlyContinue
        }
    }
}
