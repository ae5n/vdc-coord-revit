param(
    [string]$RemoteName = "upstream-mcp",
    [string]$RemoteUrl = "https://github.com/mcp-servers-for-revit/mcp-servers-for-revit.git",
    [string]$Branch = "main",
    [string]$Commit,
    [switch]$Record,
    [switch]$ShowPatch
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$repoRoot = Split-Path -Parent $repoRoot
$statePath = Join-Path $repoRoot "docs\upstream-mcp-sync.json"

function Get-RemoteUrl {
    param([string]$Name)

    $result = git remote get-url $Name 2>$null
    if ($LASTEXITCODE -ne 0) {
        return $null
    }

    return $result.Trim()
}

function Ensure-Remote {
    param(
        [string]$Name,
        [string]$Url
    )

    $existingUrl = Get-RemoteUrl -Name $Name
    if ($null -eq $existingUrl) {
        git remote add $Name $Url | Out-Null
        return
    }

    if ($existingUrl -ne $Url) {
        throw "Remote '$Name' already exists with URL '$existingUrl'. Expected '$Url'."
    }
}

function Read-State {
    param([string]$Path)

    if (-not (Test-Path $Path)) {
        return [ordered]@{
            remoteName = $RemoteName
            remoteUrl = $RemoteUrl
            branch = $Branch
            lastAdaptedCommit = $null
            lastAdaptedAt = $null
        }
    }

    $raw = Get-Content $Path -Raw | ConvertFrom-Json
    return [ordered]@{
        remoteName = $raw.remoteName
        remoteUrl = $raw.remoteUrl
        branch = $raw.branch
        lastAdaptedCommit = $raw.lastAdaptedCommit
        lastAdaptedAt = $raw.lastAdaptedAt
    }
}

function Write-State {
    param(
        [string]$Path,
        [object]$State
    )

    $directory = Split-Path -Parent $Path
    if (-not (Test-Path $directory)) {
        New-Item -ItemType Directory -Path $directory | Out-Null
    }

    $json = $State | ConvertTo-Json -Depth 4
    Set-Content -Path $Path -Value $json -Encoding UTF8
}

function Resolve-Commit {
    param([string]$Ref)

    $resolved = git rev-parse $Ref 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw "Could not resolve commit '$Ref'."
    }

    return $resolved.Trim()
}

Ensure-Remote -Name $RemoteName -Url $RemoteUrl
git fetch $RemoteName $Branch --tags

$state = Read-State -Path $statePath
$state["remoteName"] = $RemoteName
$state["remoteUrl"] = $RemoteUrl
$state["branch"] = $Branch

$targetRef = if ([string]::IsNullOrWhiteSpace($Commit)) { "$RemoteName/$Branch" } else { $Commit.Trim() }
$targetCommit = Resolve-Commit -Ref $targetRef
$lastAdaptedCommit = $state["lastAdaptedCommit"]

if ($Record) {
    $recordedAt = [DateTime]::UtcNow.ToString("o")
    $state["lastAdaptedCommit"] = $targetCommit
    $state["lastAdaptedAt"] = $recordedAt
    Write-State -Path $statePath -State $state

    Write-Host "Recorded upstream MCP adapted baseline."
    Write-Host "  Remote: $RemoteName"
    Write-Host "  Branch: $Branch"
    Write-Host "  Commit: $targetCommit"
    Write-Host "  State:  $statePath"
    exit 0
}

Write-Host "Upstream MCP review"
Write-Host "  Remote: $RemoteName"
Write-Host "  URL:    $RemoteUrl"
Write-Host "  Branch: $Branch"
Write-Host "  Target: $targetCommit"
Write-Host "  State:  $statePath"

if (-not [string]::IsNullOrWhiteSpace($lastAdaptedCommit)) {
    Write-Host "  Last adapted: $lastAdaptedCommit"
    Write-Host ""
    Write-Host "Commits since last adapted baseline:"
    git log --oneline --decorate "$lastAdaptedCommit..$targetCommit"
}
else {
    Write-Host "  Last adapted: <not recorded yet>"
    Write-Host ""
    Write-Host "Recent upstream commits:"
    git log --oneline --decorate -n 15 $targetCommit
}

Write-Host ""
Write-Host "Target commit summary:"
git show --stat --summary --no-patch $targetCommit

if ($ShowPatch) {
    Write-Host ""
    Write-Host "Patch:"
    git show $targetCommit
}

Write-Host ""
Write-Host "Next step:"
Write-Host "  Review upstream, port only what you adopt, then record the adapted baseline with:"
Write-Host "  .\build\scripts\review-upstream-mcp.ps1 -Commit $targetCommit -Record"
