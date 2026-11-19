param(
    [Parameter(Mandatory = $true)][string]$SourceArchive,
    [Parameter(Mandatory = $true)][string]$WorkDirectory,
    [string]$AspectBundleDirectory = '',
    [string]$BundleDirectory = '',
    [string]$VisualStudioRoot = '',
    [string]$Configuration = 'Release',
    [string]$AuthoredDirectory = '',
    [switch]$ProfileOnly
)
# Builds the HD card display provider from the pinned upstream archive plus the authored sources here,
# and optionally stages the complete runtime bundle (provider, unchanged bootstrap, all notices).
# Nothing is downloaded and no game or component is started.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 or later is required' }
$expectedArchive = 'A4F638383364E3BC1E4682B1D549A1F5400FA0CC312EE216D1B42555B1C814EB'
if ((Get-FileHash -LiteralPath $SourceArchive -Algorithm SHA256).Hash -ne $expectedArchive) { throw 'Wrong source archive' }
if ($BundleDirectory -and -not $AspectBundleDirectory) { throw 'A bundle needs -AspectBundleDirectory for the bootstrap and notices' }
if ($BundleDirectory -and (Test-Path -LiteralPath $BundleDirectory)) { throw 'The bundle directory must be new' }
if (-not $VisualStudioRoot) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio 2022 (or its Build Tools) with the C++ x86 toolset is required' }
    $VisualStudioRoot = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
}
$msbuild = Join-Path $VisualStudioRoot 'MSBuild\Current\Bin\MSBuild.exe'
if (-not (Test-Path -LiteralPath $msbuild)) { throw "MSBuild not found under $VisualStudioRoot" }
$work = [IO.Path]::GetFullPath($WorkDirectory)
if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
New-Item -ItemType Directory -Path $work | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($SourceArchive)
try {
    foreach ($entry in $zip.Entries) {
        if ([IO.Path]::IsPathRooted($entry.FullName) -or $entry.FullName -match '(^|[\\/])\.\.([\\/]|$)|:' -or
            -not $entry.FullName.StartsWith('cnc-ddraw-7.1.0.0/', [StringComparison]::Ordinal)) { throw 'Unsafe archive path' }
    }
} finally { $zip.Dispose() }
[IO.Compression.ZipFile]::ExtractToDirectory($SourceArchive, (Join-Path $work 'source'))
$sourceRoot = Join-Path $work 'source\cnc-ddraw-7.1.0.0'
$patchArguments = @('-I', '-B', (Join-Path $PSScriptRoot 'patch_provider.py'), '--source-root', $sourceRoot)
if ($ProfileOnly) { $patchArguments += '--profile-only' }
if ($AuthoredDirectory) { $patchArguments += @('--authored', $AuthoredDirectory) }
& python @patchArguments
if ($LASTEXITCODE -ne 0) { throw 'Source patch failed' }
$outDir = Join-Path $work 'out'
$intDir = Join-Path $work 'int'
$log = Join-Path $work 'build.log'
& $msbuild (Join-Path $sourceRoot 'cnc-ddraw.vcxproj') '/t:Build' "/p:Configuration=$Configuration" '/p:Platform=Win32' `
    '/p:PlatformToolset=v143' '/p:PreferredToolArchitecture=x64' '/p:ImportDirectoryBuildProps=false' `
    '/p:ImportDirectoryBuildTargets=false' "/p:OutDir=$outDir\\" "/p:IntDir=$intDir\\" '/p:LanguageStandard_C=stdc11' `
    '/m' '/v:minimal' '/nologo' *> $log
if ($LASTEXITCODE -ne 0) {
    Get-Content -LiteralPath $log | Select-String -Pattern 'error' | Select-Object -First 40 | ForEach-Object { $_.Line }
    throw "Build failed; see $log"
}
$dll = Join-Path $outDir 'ddraw.dll'
$hash = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.ToLowerInvariant()
$length = (Get-Item -LiteralPath $dll).Length
'{0}  {1} bytes  {2}' -f $hash, $length, $dll

if ($BundleDirectory) {
    # The provider pin recorded for the launcher; a different toolchain or source gives a different file.
    $pins = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'component-pins.json') -Raw | ConvertFrom-Json
    $provider = $pins.components | Where-Object name -eq 'ddraw.dll'
    if ($provider.sha256 -ne $hash -or $provider.length -ne $length) {
        "The provider differs from component-pins.json ($($provider.sha256), $($provider.length) bytes)."
        'Update that file and HdCardArtworkComponents.Provider together if this build is the one to ship.'
    }
    $aspectPins = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\DisplayComponent\component-pins.json') -Raw | ConvertFrom-Json
    $bundle = [IO.Path]::GetFullPath($BundleDirectory)
    New-Item -ItemType Directory -Path $bundle | Out-Null
    Copy-Item -LiteralPath $dll -Destination (Join-Path $bundle 'ddraw.dll')
    foreach ($name in $pins.shared.files) {
        $pin = @($aspectPins.components) + @($aspectPins.notices) | Where-Object name -eq $name
        $source = Join-Path $AspectBundleDirectory $name
        if (-not $pin -or -not (Test-Path -LiteralPath $source)) { throw "Missing shared bundle file: $name" }
        if ((Get-Item -LiteralPath $source).Length -ne $pin.length -or
            (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $pin.sha256) { throw "Shared bundle file differs from its pin: $name" }
        Copy-Item -LiteralPath $source -Destination (Join-Path $bundle $name)
    }
    "bundle ready: $bundle"
}
