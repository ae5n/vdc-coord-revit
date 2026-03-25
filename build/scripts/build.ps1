param(
    [ValidateSet("2024", "2025", "2026")]
    [string]$RevitYear = "2026",
    [string]$ApiDir,
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
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

function Get-TargetFramework {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Year
    )

    switch ($Year) {
        "2024" { return "net48" }
        "2025" { return "net8.0-windows10.0.19041" }
        "2026" { return "net8.0-windows10.0.19041" }
        default { throw "Unsupported Revit year '$Year'." }
    }
}

function Sync-SchemasToBuildOutput {
    param(
        [string]$RepoRoot,
        [string]$BuildConfiguration,
        [string]$TargetFramework
    )

    $schemaSourceDir = Join-Path $RepoRoot "schemas"
    if (-not (Test-Path $schemaSourceDir)) {
        throw "Schema source directory not found: $schemaSourceDir"
    }

    $outputDir = Join-Path $RepoRoot ("src/RevitSuite.Host/bin/{0}/{1}" -f $BuildConfiguration, $TargetFramework)
    if (-not (Test-Path $outputDir)) {
        throw "Build output directory not found: $outputDir"
    }

    $schemaTargetDir = Join-Path $outputDir "schemas"
    if (Test-Path $schemaTargetDir) {
        Remove-Item -Recurse -Force $schemaTargetDir
    }

    New-Item -ItemType Directory -Force -Path $schemaTargetDir | Out-Null
    Copy-Item -Force (Join-Path $schemaSourceDir "*.json") $schemaTargetDir
    Write-Host "Synced schema files to $schemaTargetDir" -ForegroundColor DarkCyan
}

$targetFramework = Get-TargetFramework -Year $RevitYear
$requiresLocalApi = $targetFramework -eq "net48"
$resolvedApiDir = if ($requiresLocalApi) { Resolve-RevitApiDir -Year $RevitYear -ExplicitPath $ApiDir } else { $null }
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
    elseif ($requiresLocalApi -and -not $env:REVIT_API_DIR) {
        Write-Warning "REVIT_API_DIR not set. Set it explicitly or pass -RevitYear/-ApiDir so the build can locate RevitAPI.dll."
    }
    elseif ($requiresLocalApi) {
        $apiDirMessage = "Using existing REVIT_API_DIR='$($env:REVIT_API_DIR)'"
    }
    elseif ($ApiDir) {
        Write-Warning "-ApiDir is ignored for Revit $RevitYear because this build uses NuGet-based Revit API references on $targetFramework."
    }

    if ($apiDirMessage) {
        Write-Host $apiDirMessage -ForegroundColor DarkCyan
    }

    $repoRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path))
    $projectPath = Join-Path $repoRoot "src/RevitSuite.Host/RevitSuite.Host.csproj"

    Write-Host "Building RevitSuite.Host for Revit $RevitYear ($Configuration, $targetFramework)..." -ForegroundColor Cyan
    dotnet build $projectPath -c $Configuration -p:RevitVersion=$RevitYear -p:RevitTargetFramework=$targetFramework
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed for RevitSuite.Host."
    }

    Sync-SchemasToBuildOutput -RepoRoot $repoRoot -BuildConfiguration $Configuration -TargetFramework $targetFramework

    $cmdSetProject = Join-Path $repoRoot "src/RevitMCPCommandSet/RevitMCPCommandSet.csproj"
    Write-Host "Building RevitMCPCommandSet for Revit $RevitYear ($Configuration, $targetFramework)..." -ForegroundColor Cyan
    dotnet build $cmdSetProject -c $Configuration -p:RevitVersion=$RevitYear -p:RevitTargetFramework=$targetFramework
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed for RevitMCPCommandSet."
    }

    $serverDir = Join-Path $repoRoot "mcp-server"
    if (Test-Path (Join-Path $serverDir "package.json")) {
        Write-Host "Building TypeScript MCP server..." -ForegroundColor Cyan
        Push-Location $serverDir
        try {
            npm run build
            if ($LASTEXITCODE -ne 0) {
                throw "npm run build failed for the MCP server."
            }
        }
        finally { Pop-Location }
    }
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
