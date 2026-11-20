param(
    [Parameter(Mandatory = $true)][string]$WorkDirectory,
    [string]$BundleDirectory = '',
    [string]$BundleArchive = '',
    [string]$VisualStudioRoot = ''
)
# Builds the gameplay component (an authored msacm32.dll) from the sources in src/, runs its checks
# against made-up data, and optionally stages the runtime bundle and the archive a release acquires.
# Nothing is downloaded and no game is started.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 or later is required' }
$here = (Get-Location).ProviderPath
$work = [IO.Path]::GetFullPath($WorkDirectory, $here)
if ($BundleDirectory) { $BundleDirectory = [IO.Path]::GetFullPath($BundleDirectory, $here) }
if ($BundleArchive) { $BundleArchive = [IO.Path]::GetFullPath($BundleArchive, $here) }
if (Test-Path -LiteralPath $work) { throw 'The work directory must be new' }
if ($BundleDirectory -and (Test-Path -LiteralPath $BundleDirectory)) { throw 'The bundle directory must be new' }
if ($BundleArchive -and -not $BundleDirectory) { throw 'A bundle archive needs -BundleDirectory' }
if ($BundleArchive -and (Test-Path -LiteralPath $BundleArchive)) { throw 'The bundle archive must be new' }
if (-not $VisualStudioRoot) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio 2022 (or its Build Tools) with the C++ x86 toolset is required' }
    $VisualStudioRoot = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
}
$developer = Join-Path $VisualStudioRoot 'Common7\Tools\VsDevCmd.bat'
if (-not (Test-Path -LiteralPath $developer)) { throw "VsDevCmd.bat not found under $VisualStudioRoot" }
foreach ($line in (& $env:ComSpec /d /s /c "`"$developer`" -arch=x86 -host_arch=x64 -no_logo >nul <nul && set")) {
    if ($line -match '^([^=]+)=(.*)$') { Set-Item -LiteralPath "Env:$($Matches[1])" -Value $Matches[2] }
}
$pins = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'component-pins.json') -Raw | ConvertFrom-Json
$toolset = "$env:VCToolsVersion".TrimEnd('\')
$sdk = "$env:WindowsSDKVersion".TrimEnd('\')
"toolchain: MSVC $toolset, Windows SDK $sdk"
if ($toolset -ne $pins.toolchain.msvc -or $sdk -ne $pins.toolchain.windowsSdk) {
    "The pinned component was built with MSVC $($pins.toolchain.msvc) and Windows SDK $($pins.toolchain.windowsSdk); this build will differ."
}

# Every stand-in function must carry the name its export has: entry n of the name list belongs to pocgp_forward_n.
$proxySource = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'src/pocgp_proxy.c') -Raw
$listed = @([regex]::Matches([regex]::Match($proxySource, 'g_names\[FORWARDS\]\s*=\s*\{([^}]*)\}').Groups[1].Value, '"(\w+)"') |
    ForEach-Object { $_.Groups[1].Value })
$defined = [regex]::Matches((Get-Content -LiteralPath (Join-Path $PSScriptRoot 'src/msacm32.def') -Raw), '(?m)^\s+(\w+)=pocgp_forward_(\d+) @(\d+)\s*$')
if ($listed.Count -ne 44 -or $defined.Count -ne $listed.Count) { throw 'The name list and the export definitions differ in length' }
foreach ($export in $defined) {
    $index = [int]$export.Groups[2].Value
    if ($listed[$index] -cne $export.Groups[1].Value -or [int]$export.Groups[3].Value -ne $index + 1) {
        throw "Export $($export.Groups[1].Value) is paired with the wrong function"
    }
}
foreach ($directory in $work,(Join-Path $work 'test'), (Join-Path $work 'dll'), (Join-Path $work 'proxy')) { New-Item -ItemType Directory -Path $directory | Out-Null }
$source = Join-Path $PSScriptRoot 'src'
$units = 'pocgp_patch.c', 'pocgp_rewards.c', 'pocgp_decks.c', 'pocgp_settings.c', 'pocgp_games.c'
$compile = '/nologo', '/c', '/O1', '/W4', '/WX', '/MT', '/GS', '/Gy', '/Brepro', '/D_CRT_SECURE_NO_WARNINGS', '/DWIN32_LEAN_AND_MEAN'
Push-Location -LiteralPath $source
try {
    # The checks first: the component's own logic against made-up data.
    & cl.exe @compile "/Fo$work/test/" @units 'pocgp_test.c'
    if ($LASTEXITCODE -ne 0) { throw 'Compiling the checks failed' }
    & link.exe /nologo /Brepro /INCREMENTAL:NO /SUBSYSTEM:CONSOLE "/OUT:$work\pocgp_test.exe" (Get-ChildItem -LiteralPath (Join-Path $work 'test') -Filter '*.obj' | ForEach-Object FullName)
    if ($LASTEXITCODE -ne 0) { throw 'Linking the checks failed' }
    & (Join-Path $work 'pocgp_test.exe')
    if ($LASTEXITCODE -ne 0) { throw 'The component checks failed' }

    & cl.exe @compile "/Fo$work/dll/" @units 'pocgp_main.c' 'pocgp_proxy.c'
    if ($LASTEXITCODE -ne 0) { throw 'Compiling the component failed' }
    & link.exe /nologo /DLL /Brepro /INCREMENTAL:NO /MACHINE:X86 /SUBSYSTEM:WINDOWS /DYNAMICBASE /NXCOMPAT /SAFESEH `
        /OPT:REF /OPT:ICF /MANIFEST:NO /DEF:msacm32.def "/OUT:$work\msacm32.dll" "/IMPLIB:$work\msacm32.lib" `
        (Get-ChildItem -LiteralPath (Join-Path $work 'dll') -Filter '*.obj' | ForEach-Object FullName) kernel32.lib
    if ($LASTEXITCODE -ne 0) { throw 'Linking the component failed' }

    # The built library as a stand-in: same names and ordinals as the system library, and calls pass through.
    & cl.exe @compile "/Fo$work/proxy/" 'pocgp_proxy_test.c'
    if ($LASTEXITCODE -ne 0) { throw 'Compiling the stand-in checks failed' }
    & link.exe /nologo /Brepro /INCREMENTAL:NO /SUBSYSTEM:CONSOLE "/OUT:$work\pocgp_proxy_test.exe" (Join-Path $work 'proxy\pocgp_proxy_test.obj')
    if ($LASTEXITCODE -ne 0) { throw 'Linking the stand-in checks failed' }
    & (Join-Path $work 'pocgp_proxy_test.exe') (Join-Path $work 'msacm32.dll')
    if ($LASTEXITCODE -ne 0) { throw 'The stand-in checks failed' }
} finally { Pop-Location }

$dll = Join-Path $work 'msacm32.dll'
$hash = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.ToLowerInvariant()
$length = (Get-Item -LiteralPath $dll).Length
'{0}  {1} bytes  {2}' -f $hash, $length, $dll
$pin = $pins.components | Where-Object name -eq 'msacm32.dll'
$pinned = $pin.sha256 -eq $hash -and $pin.length -eq $length
if (-not $pinned) {
    "The component differs from component-pins.json ($($pin.sha256), $($pin.length) bytes)."
    'Update that file and GameplayComponents.Library together if this build is the one to ship.'
}

if ($BundleDirectory) {
    New-Item -ItemType Directory -Path $BundleDirectory | Out-Null
    Copy-Item -LiteralPath $dll -Destination (Join-Path $BundleDirectory 'msacm32.dll')
    "bundle ready: $BundleDirectory"
    if ($BundleArchive) {
        # The archive a release acquires: the bundle's files, flat, with fixed timestamps. Only for the pinned build.
        if (-not $pinned) { throw 'The bundle archive is only written for the pinned component' }
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [IO.Compression.ZipFile]::Open($BundleArchive, [IO.Compression.ZipArchiveMode]::Create)
        try {
            $entry = $archive.CreateEntry('msacm32.dll', [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
            $entry.ExternalAttributes = 0
            $input = [IO.File]::OpenRead((Join-Path $BundleDirectory 'msacm32.dll'))
            $target = $entry.Open()
            try { $input.CopyTo($target) } finally { $target.Dispose(); $input.Dispose() }
        } finally { $archive.Dispose() }
        '{0}  {1} bytes  {2}' -f (Get-FileHash -LiteralPath $BundleArchive -Algorithm SHA256).Hash.ToLowerInvariant(),
            (Get-Item -LiteralPath $BundleArchive).Length, $BundleArchive
    }
}
