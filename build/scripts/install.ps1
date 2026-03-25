param(
    [ValidateSet("2024", "2025", "2026")]
    [string]$RevitYear = "2026",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$TargetRoot
)

$ErrorActionPreference = "Stop"

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

function Reset-Directory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (Test-Path $Path) {
        Remove-Item -Recurse -Force $Path
    }

    New-Item -ItemType Directory -Force -Path $Path | Out-Null
}

$repoRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path))
$targetFramework = Get-TargetFramework -Year $RevitYear
$buildOutput = [System.IO.Path]::Combine(
    $repoRoot,
    "src",
    "RevitSuite.Host",
    "bin",
    $Configuration,
    $targetFramework,
    "RevitSuite.Host.dll"
)

if (-not (Test-Path $buildOutput)) {
    throw "Build output not found. Run build.ps1 before install.";
}

$resolvedTargetRoot = if ([string]::IsNullOrWhiteSpace($TargetRoot)) {
    Join-Path $env:APPDATA "Autodesk/Revit/Addins"
}
else {
    [System.IO.Path]::GetFullPath($TargetRoot)
}

$addinRoot = Join-Path $resolvedTargetRoot $RevitYear
$payloadDir = Join-Path $addinRoot "RevitSuite"

New-Item -ItemType Directory -Force -Path $payloadDir | Out-Null

$hostBinDir = Split-Path $buildOutput
Copy-Item -Path (Join-Path $hostBinDir "*") -Destination $payloadDir -Recurse -Force

$schemaSourceDir = Join-Path $repoRoot "schemas"
$schemaTargetDir = Join-Path $payloadDir "schemas"
Reset-Directory -Path $schemaTargetDir
Copy-Item -Force (Join-Path $schemaSourceDir "*.json") $schemaTargetDir

$templatePath = Join-Path $repoRoot "src/RevitSuite.Host/AddinManifest/RevitSuite.addin.tpl"
$addinTemplate = Get-Content $templatePath -Raw

$dllPath = Join-Path $payloadDir "RevitSuite.Host.dll"
$addinContent = $addinTemplate.Replace('$ADDIN_DLL$', $dllPath)

$addinFile = Join-Path $addinRoot "RevitSuite.addin"
$addinContent | Out-File -Encoding utf8 -FilePath $addinFile

# --- MCP Commands directory ---
$commandsDir = Join-Path $payloadDir "Commands"
Reset-Directory -Path $commandsDir

$cmdSetOutput = Join-Path $repoRoot ("src/RevitMCPCommandSet/bin/{0}/{1}" -f $Configuration, $targetFramework)
if (Test-Path $cmdSetOutput) {
    Copy-Item -Path (Join-Path $cmdSetOutput "*") -Destination $commandsDir -Recurse -Force
}


$registrySource = Join-Path $repoRoot "src/commandRegistry.json"
Copy-Item -Force $registrySource (Join-Path $commandsDir "commandRegistry.json")

# --- MCP Node.js server ---
$serverBuildDir = Join-Path $repoRoot "mcp-server/build"
if (Test-Path $serverBuildDir) {
    $serverTarget = Join-Path $payloadDir "mcp-server"
    New-Item -ItemType Directory -Force -Path $serverTarget | Out-Null
    $deployedBuildDir = Join-Path $serverTarget "build"
    Reset-Directory -Path $deployedBuildDir
    Copy-Item -Recurse -Force (Join-Path $serverBuildDir "*") $deployedBuildDir
    $nodeModulesSource = Join-Path $repoRoot "mcp-server/node_modules"
    $nodeModulesDest = Join-Path $serverTarget "node_modules"
    $requiredSdkDest = Join-Path $nodeModulesDest "@modelcontextprotocol\sdk"
    if ((Test-Path $nodeModulesSource) -and ((-not (Test-Path $nodeModulesDest)) -or (-not (Test-Path $requiredSdkDest)))) {
        if (Test-Path $nodeModulesDest) {
            try {
                Remove-Item -Recurse -Force $nodeModulesDest
            }
            catch {
                Write-Warning "Existing node_modules is incomplete but locked. Stop any MCP node.exe processes, then rerun deploy to refresh dependencies."
            }
        }
    }

    if ((Test-Path $nodeModulesSource) -and (-not (Test-Path $nodeModulesDest))) {
        Copy-Item -Recurse -Force $nodeModulesSource $serverTarget
        Write-Host "Copied node_modules to $serverTarget" -ForegroundColor DarkCyan
    } elseif (Test-Path $nodeModulesDest) {
        Write-Host "Skipping node_modules (already present - stop MCP server to force update)" -ForegroundColor DarkYellow
    }
}

Write-Host "Installed RevitSuite add-in to $addinRoot" -ForegroundColor Green
