param(
    [Parameter(Mandatory = $false)]
    [string]$Icon = "WallFraming",

    [Parameter(Mandatory = $false)]
    [string]$AssemblyPath
)

Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName WindowsBase

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptRoot

if (-not $AssemblyPath) {
    $candidatePaths = @(
        (Join-Path $repoRoot "src\RevitSuite.Host\bin\Release\net48\RevitSuite.Host.dll"),
        (Join-Path $repoRoot "src\RevitSuite.Host\bin\Debug\net48\RevitSuite.Host.dll"),
        (Join-Path $repoRoot "installer\staging\Addins\2024\RevitSuite\RevitSuite.Host.dll")
    )

    $AssemblyPath = $candidatePaths | Where-Object { Test-Path $_ } | Select-Object -First 1
}

if (-not $AssemblyPath -or -not (Test-Path $AssemblyPath)) {
    throw "Could not find RevitSuite.Host.dll. Pass -AssemblyPath explicitly after building."
}

$assembly = [System.Reflection.Assembly]::LoadFrom($AssemblyPath)
$factoryType = $assembly.GetType("RevitSuite.Host.UI.RibbonIconFactory", $true)
$property = $factoryType.GetProperty($Icon, [System.Reflection.BindingFlags] "Public, Static")

if (-not $property) {
    $available = $factoryType.GetProperties([System.Reflection.BindingFlags] "Public, Static") |
        Select-Object -ExpandProperty Name |
        Sort-Object
    throw "Unknown icon '$Icon'. Available icons: $($available -join ', ')"
}

$iconSet = $property.GetValue($null, $null)
$largeImageProperty = $iconSet.GetType().GetProperty("LargeImage", [System.Reflection.BindingFlags] "Public, Instance")
$bitmap = $largeImageProperty.GetValue($iconSet, $null) -as [System.Windows.Media.Imaging.BitmapSource]

if (-not $bitmap) {
    throw "Failed to read bitmap for icon '$Icon' from $AssemblyPath"
}

$outPath = Join-Path $scriptRoot ("icon-preview-" + $Icon + ".png")
$encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
$encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
$stream = [System.IO.FileStream]::new($outPath, [System.IO.FileMode]::Create)
try {
    $encoder.Save($stream)
}
finally {
    $stream.Close()
}

Write-Host "Assembly: $AssemblyPath"
Write-Host "Saved: $outPath"

try {
    Start-Process $outPath | Out-Null
}
catch {
    Write-Host "Preview file saved but could not be opened automatically."
}
