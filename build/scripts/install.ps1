param(
    [string]$RevitYear = "2025"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path))
$buildOutput = [System.IO.Path]::Combine(
    $repoRoot,
    "host-cs",
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
Copy-Item -Recurse -Force (Join-Path $repoRoot "engine-py") (Join-Path $payloadDir "python")
Copy-Item -Recurse -Force (Join-Path $repoRoot "schemas") (Join-Path $payloadDir "schemas")

$templatePath = Join-Path $repoRoot "host-cs/AddinManifest/RevitSuite.addin.tpl"
$addinTemplate = Get-Content $templatePath -Raw

$dllPath = Join-Path $payloadDir "RevitSuite.Host.dll"
$addinContent = $addinTemplate.Replace('$ADDIN_DLL$', $dllPath)

$addinFile = Join-Path $addinRoot "RevitSuite.addin"
$addinContent | Out-File -Encoding utf8 -FilePath $addinFile

Write-Host "Installed RevitSuite add-in to $addinRoot" -ForegroundColor Green
