$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path))
$projectPath = Join-Path $repoRoot "host-cs/RevitSuite.Host.csproj"

Write-Host "Building RevitSuite.Host..." -ForegroundColor Cyan
dotnet build $projectPath -c Release
