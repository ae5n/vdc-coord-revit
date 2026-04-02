function Get-VersionCore {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Version
    )

    if ($Version -match '^(\d+)\.(\d+)\.(\d+)(?:[-+].*)?$') {
        return @{
            Major = [int]$matches[1]
            Minor = [int]$matches[2]
            Patch = [int]$matches[3]
        }
    }

    throw "Version '$Version' is not valid semver. Use values like 0.4.0, 0.4.0-beta.1, or 1.0.0-rc.2."
}

function Get-AssemblyVersionFromSemVer {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Version
    )

    $core = Get-VersionCore -Version $Version
    return "{0}.{1}.{2}.0" -f $core.Major, $core.Minor, $core.Patch
}
