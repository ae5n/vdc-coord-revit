param(
    [string]$RevitYear = "2025"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path))
$buildOutput = [System.IO.Path]::Combine(
    $repoRoot,
    "src",
    "RevitSuite.Host",
    "bin",
    "Release",
    "net48",
    "RevitSuite.Host.dll"
)

if (-not (Test-Path $buildOutput)) {
    throw "Build output not found. Run build.ps1 before install.";
}

$addinRoot = Join-Path $env:APPDATA ("Autodesk/Revit/Addins/{0}" -f $RevitYear)
$payloadDir = Join-Path $addinRoot "RevitSuite"

New-Item -ItemType Directory -Force -Path $payloadDir | Out-Null

$destinationDll = Join-Path $payloadDir "RevitSuite.Host.dll"
Copy-Item -Force $buildOutput $destinationDll

# Copy RevitMCPSDK.dll - required by the Mcp infrastructure compiled into RevitSuite.Host
$hostBinDir = Split-Path $buildOutput
$sdkDll = Join-Path $hostBinDir "RevitMCPSDK.dll"
if (Test-Path $sdkDll) {
    Copy-Item -Force $sdkDll $payloadDir
} else {
    Write-Warning "RevitMCPSDK.dll not found in build output - MCP toggle will fail at runtime."
}

$schemaSourceDir = Join-Path $repoRoot "schemas"
$schemaTargetDir = Join-Path $payloadDir "schemas"

if (Test-Path $schemaTargetDir) {
    Remove-Item -Recurse -Force $schemaTargetDir
}

New-Item -ItemType Directory -Force -Path $schemaTargetDir | Out-Null
Copy-Item -Force (Join-Path $schemaSourceDir "*.json") $schemaTargetDir

$templatePath = Join-Path $repoRoot "src/RevitSuite.Host/AddinManifest/RevitSuite.addin.tpl"
$addinTemplate = Get-Content $templatePath -Raw

$dllPath = Join-Path $payloadDir "RevitSuite.Host.dll"
$addinContent = $addinTemplate.Replace('$ADDIN_DLL$', $dllPath)

$addinFile = Join-Path $addinRoot "RevitSuite.addin"
$addinContent | Out-File -Encoding utf8 -FilePath $addinFile

# --- MCP Commands directory ---
$commandsDir = Join-Path $payloadDir "Commands"
New-Item -ItemType Directory -Force -Path $commandsDir | Out-Null

$cmdSetOutput = Join-Path $repoRoot "src/RevitMCPCommandSet/bin/Release/net48"
if (Test-Path $cmdSetOutput) {
    Copy-Item -Force (Join-Path $cmdSetOutput "*.dll") $commandsDir
}


$registrySource = Join-Path $repoRoot "src/commandRegistry.json"
Copy-Item -Force $registrySource (Join-Path $commandsDir "commandRegistry.json")

# --- MCP Node.js server ---
$serverBuildDir = Join-Path $repoRoot "mcp-server/build"
if (Test-Path $serverBuildDir) {
    $serverTarget = Join-Path $payloadDir "mcp-server"
    New-Item -ItemType Directory -Force -Path $serverTarget | Out-Null
    Copy-Item -Recurse -Force $serverBuildDir $serverTarget
    $nodeModulesSource = Join-Path $repoRoot "mcp-server/node_modules"
    $nodeModulesDest = Join-Path $serverTarget "node_modules"
    if ((Test-Path $nodeModulesSource) -and (-not (Test-Path $nodeModulesDest))) {
        Copy-Item -Recurse -Force $nodeModulesSource $serverTarget
        Write-Host "Copied node_modules to $serverTarget" -ForegroundColor DarkCyan
    } elseif (Test-Path $nodeModulesDest) {
        Write-Host "Skipping node_modules (already present - stop MCP server to force update)" -ForegroundColor DarkYellow
    }
}

Write-Host "Installed RevitSuite add-in to $addinRoot" -ForegroundColor Green
