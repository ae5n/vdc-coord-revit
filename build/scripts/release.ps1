param(
    [switch]$Beta,
    [switch]$Patch,
    [switch]$Stable,
    [string]$SetVersion
)

$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "versioning.ps1")

function Get-VersionFilePath {
    $repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    return Join-Path $repoRoot "Directory.Build.props"
}

function Get-CurrentVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$VersionFile
    )

    [xml]$xml = Get-Content $VersionFile
    $versionNode = $xml.Project.PropertyGroup.Version | Select-Object -First 1
    if (-not $versionNode) {
        throw "Could not find <Version> in $VersionFile"
    }

    return $versionNode.InnerText
}

function Get-NextBetaVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Version
    )

    if ($Version -match '^(\d+)\.(\d+)\.(\d+)-beta\.(\d+)$') {
        return "{0}.{1}.{2}-beta.{3}" -f $matches[1], $matches[2], $matches[3], ([int]$matches[4] + 1)
    }

    $core = Get-VersionCore -Version $Version
    return "{0}.{1}.{2}-beta.1" -f $core.Major, $core.Minor, ($core.Patch + 1)
}

function Get-NextPatchVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Version
    )

    $core = Get-VersionCore -Version $Version
    return "{0}.{1}.{2}" -f $core.Major, $core.Minor, ($core.Patch + 1)
}

function Get-StableVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Version
    )

    $core = Get-VersionCore -Version $Version
    return "{0}.{1}.{2}" -f $core.Major, $core.Minor, $core.Patch
}

function Set-VersionInFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$VersionFile,
        [Parameter(Mandatory = $true)]
        [string]$Version
    )

    $assemblyVersion = Get-AssemblyVersionFromSemVer -Version $Version

    [xml]$xml = Get-Content $VersionFile
    $propertyGroup = $xml.Project.PropertyGroup | Select-Object -First 1
    if (-not $propertyGroup) {
        throw "Could not find <PropertyGroup> in $VersionFile"
    }

    $propertyGroup.Version.InnerText = $Version
    $propertyGroup.AssemblyVersion.InnerText = $assemblyVersion
    $propertyGroup.FileVersion.InnerText = $assemblyVersion
    $propertyGroup.InformationalVersion.InnerText = '$(Version)'
    $xml.Save($VersionFile)
}

$actions = @($Beta, $Patch, $Stable, -not [string]::IsNullOrWhiteSpace($SetVersion)) | Where-Object { $_ }
if ($actions.Count -ne 1) {
    throw "Choose exactly one action: -Beta, -Patch, -Stable, or -SetVersion <semver>."
}

$versionFile = Get-VersionFilePath
$currentVersion = Get-CurrentVersion -VersionFile $versionFile

if ($Beta) {
    $nextVersion = Get-NextBetaVersion -Version $currentVersion
}
elseif ($Patch) {
    $nextVersion = Get-NextPatchVersion -Version $currentVersion
}
elseif ($Stable) {
    $nextVersion = Get-StableVersion -Version $currentVersion
}
else {
    Get-VersionCore -Version $SetVersion | Out-Null
    $nextVersion = $SetVersion
}

Set-VersionInFile -VersionFile $versionFile -Version $nextVersion

Write-Host "Updated version: $currentVersion -> $nextVersion" -ForegroundColor Green
