param(
    [string]$PythonExe
)

$ErrorActionPreference = "Stop"

function Resolve-Python([string]$candidate) {
    if ([string]::IsNullOrWhiteSpace($candidate)) {
        return $null
    }

    if ([System.IO.Path]::IsPathRooted($candidate)) {
        if (Test-Path $candidate) {
            return $candidate
        }

        return $null
    }

    $cmd = Get-Command $candidate -ErrorAction SilentlyContinue
    if ($cmd) {
        return $cmd.Path
    }

    return $null
}

$candidates = @()
if ($PSBoundParameters.ContainsKey("PythonExe")) {
    $candidates += $PythonExe
}

if (-not [string]::IsNullOrWhiteSpace($env:REVITSUITE_PYTHON)) {
    $candidates += $env:REVITSUITE_PYTHON
}

$candidates += "py", "python"

$resolvedPython = $null
foreach ($candidate in $candidates) {
    $resolvedPython = Resolve-Python $candidate
    if ($resolvedPython) {
        break
    }
}

if (-not $resolvedPython) {
    $message = @(
        "Unable to locate a Python executable.",
        "Specify -PythonExe <path-to-python.exe> or set the REVITSUITE_PYTHON environment variable."
    ) -join " "
    throw $message
}

$enginePath = Join-Path $PSScriptRoot "engine-py/server.py"
& $resolvedPython $enginePath
