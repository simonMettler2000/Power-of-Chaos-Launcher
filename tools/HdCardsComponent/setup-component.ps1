# Explicit offline setup. Builds and copies local output; never loads a DLL or starts a game.
param([Parameter(Mandatory=$true)][string]$WorkDirectory,
      [Parameter(Mandatory=$true)][string]$Destination,
      [Parameter(Mandatory=$true)][string]$InputCache)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 or later is required' }
. (Join-Path $PSScriptRoot 'local-paths.ps1')
$work=[IO.Path]::GetFullPath($WorkDirectory)
$destinationPath=[IO.Path]::GetFullPath($Destination)
Assert-Private $work -Fresh
Assert-Private $destinationPath -Fresh
Assert-NoLinks $InputCache
if ((Split-Path $destinationPath -Leaf) -cne 'hd-card-artwork-v1' -or (Split-Path (Split-Path $destinationPath -Parent) -Leaf) -cne 'Components') { throw 'Destination must end in Components/hd-card-artwork-v1' }
if (-not (Test-Path -LiteralPath (Split-Path $destinationPath -Parent) -PathType Container)) { throw 'Destination parent must exist' }
if ($destinationPath.StartsWith($work.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase) -or $work.StartsWith($destinationPath.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Work and destination must be disjoint' }
$pins=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'component-pins.json') -Raw | ConvertFrom-Json
$base=Join-Path $PSScriptRoot '..\DisplayComponent'
foreach ($row in $pins.baseRecipe.PSObject.Properties) {
    Assert-NoLinks (Join-Path $base $row.Name)
    if ((Get-FileHash -LiteralPath (Join-Path $base $row.Name) -Algorithm SHA256).Hash -ine $row.Value) { throw ('Base recipe pin mismatch: '+$row.Name) }
}
foreach ($row in $pins.nativeSources.PSObject.Properties) {
    Assert-NoLinks (Join-Path $PSScriptRoot $row.Name)
    if ((Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $row.Name) -Algorithm SHA256).Hash -ine $row.Value) { throw ('Native source pin mismatch: '+$row.Name) }
}
# Verify all offline inputs before creating work or starting a compiler.
foreach ($inputPin in $pins.inputs) {
    $path=Join-Path $InputCache $inputPin.name
    Assert-NoLinks $path
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw ('Missing offline input '+$inputPin.name) }
    if ((Get-Item -LiteralPath $path).Length -ne $inputPin.length -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $inputPin.sha256) { throw ('Pinned input mismatch: '+$inputPin.name) }
}
& python -B (Join-Path $PSScriptRoot 'verify-toolchain.py')
if ($LASTEXITCODE -ne 0) { throw 'Toolchain verification failed' }
New-Item -ItemType Directory -Path $work | Out-Null
# CL and link command injection variables are not part of this build contract.
$oldEnvironment=@{}
foreach ($key in @('CL','_CL_','LINK','_LINK_','INCLUDE','LIB','LIBPATH')) {
    $oldEnvironment[$key]=[Environment]::GetEnvironmentVariable($key)
    [Environment]::SetEnvironmentVariable($key,$null)
}
try {
    & (Join-Path $base 'setup-component.ps1') -WorkDirectory (Join-Path $work 'aspect-work') -Destination (Join-Path $work 'aspect-bundle') -InputCache $InputCache
    $aspectSource=Join-Path $work 'aspect-work\build\source\cnc-ddraw-7.1.0.0'
    $hdSource=Join-Path $work 'hd-source'
    & python -B (Join-Path $PSScriptRoot 'patch_provider.py') $aspectSource $hdSource
    if ($LASTEXITCODE -ne 0) { throw 'Authored HD source patch failed' }
    & (Join-Path $PSScriptRoot 'Build-Provider.ps1') -SourceDirectory $hdSource -OutputDirectory (Join-Path $work 'hd-build')
} finally {
    foreach ($key in $oldEnvironment.Keys) { [Environment]::SetEnvironmentVariable($key,$oldEnvironment[$key]) }
}
$bundle=Join-Path $work 'bundle'
New-Item -ItemType Directory -Path $bundle | Out-Null
foreach ($pin in @($pins.components)+@($pins.notices)) {
    $source=if ($pin.name -eq 'ddraw.dll') { Join-Path $work 'hd-build\out\ddraw.dll' } else { Join-Path $work ('aspect-bundle\'+$pin.name) }
    Assert-NoLinks $source
    if ((Get-Item -LiteralPath $source).Length -ne $pin.length -or (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ine $pin.sha256) { throw ('Bundle pin mismatch: '+$pin.name) }
    Copy-Item -LiteralPath $source -Destination (Join-Path $bundle $pin.name)
}
$receipt=[ordered]@{profile=$pins.profile;files=@($pins.components)+@($pins.notices);componentExecuted=$false;gameExecuted=$false;
    pinsSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'component-pins.json') -Algorithm SHA256).Hash.ToLowerInvariant();
    providerBuildReceiptSha256=(Get-FileHash -LiteralPath (Join-Path $work 'hd-build\build-receipt.json') -Algorithm SHA256).Hash.ToLowerInvariant()}
$receipt | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $work 'setup-receipt.json') -Encoding utf8
Assert-Private $destinationPath -Fresh
[IO.Directory]::Move($bundle,$destinationPath)
Write-Output ('Verified local HD component: '+$destinationPath)
