param([Parameter(Mandatory=$true)][string]$OutputDirectory,
      [Parameter(Mandatory=$true)][string]$ExternalSourceDirectory)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$nativeOutput=[IO.Path]::GetFullPath($OutputDirectory)
. (Join-Path $PSScriptRoot 'local-paths.ps1')
Assert-Private $nativeOutput -Fresh
$nativePins=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'component-pins.json') -Raw | ConvertFrom-Json
$nativeFiles=@(Get-ChildItem -LiteralPath $PSScriptRoot -File | Where-Object Extension -in @('.cpp','.h','.def'))
if ($nativeFiles.Count -ne @($nativePins.nativeSources.PSObject.Properties).Count) { throw 'Exact native source inventory mismatch' }
foreach ($file in $nativeFiles) {
    Assert-NoLinks $file.FullName
    $pin=$nativePins.nativeSources.PSObject.Properties[$file.Name]
    if (-not $pin -or (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ine $pin.Value) { throw ('Native source pin mismatch: '+$file.Name) }
}
& python -B (Join-Path $PSScriptRoot 'verify-toolchain.py')
if ($LASTEXITCODE -ne 0) { throw 'Pinned toolchain mismatch' }
$vc='C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Tools\MSVC\14.41.34120'
$sdk='C:\Program Files (x86)\Windows Kits\10'
$sdkVersion='10.0.22621.0'
$toolBin=Join-Path $vc 'bin\Hostx64\x86'
$compiler=Join-Path $toolBin 'cl.exe'
$linker=Join-Path $toolBin 'link.exe'
if ((Get-FileHash -LiteralPath $compiler -Algorithm SHA256).Hash -ne '102C5505E7BB850E1BB21E1A622D578E11B99D75B101219C26DA6C17F0016E1A' -or
    (Get-FileHash -LiteralPath $linker -Algorithm SHA256).Hash -ne '882F4A258DF99CA02A382796623E3050999D0EAEF623FEE59F3A6B775B3DCAF6') { throw 'Pinned tool hash mismatch' }
$externalSource=[IO.Path]::GetFullPath($ExternalSourceDirectory)
Assert-Private $externalSource
Assert-NoTreeLinks $externalSource
& python -B (Join-Path $PSScriptRoot 'verify_source.py') $externalSource aspect
if ($LASTEXITCODE -ne 0) { throw 'Pinned external source tree mismatch' }
$externalPins=@{
    'src/lodepng.c'='a7f82599ae6a4dbf0f3db3aa45a6125a1e88ba4d84c1f4be670bf3b4b5a27c86'
    'inc/lodepng.h'='52a3e979fdc3048c804eec72a7927c3095ac254b03580f35539d46b72ff75f52'
}
foreach ($name in $externalPins.Keys) {
    if ((Get-FileHash -LiteralPath (Join-Path $externalSource $name) -Algorithm SHA256).Hash -ine $externalPins[$name]) { throw 'Pinned external LodePNG mismatch' }
}
New-Item -ItemType Directory -Path $nativeOutput | Out-Null
$log=Join-Path $nativeOutput 'build.log'
function Invoke-Native([string]$Tool,[string[]]$Arguments) {
    $start=[Diagnostics.ProcessStartInfo]::new()
    $start.FileName=$Tool; $start.WorkingDirectory=$nativeOutput
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
    $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    foreach ($key in @('CL','_CL_','LINK','_LINK_','INCLUDE','LIB','LIBPATH')) { $start.Environment.Remove($key) | Out-Null }
    $start.Environment['PATH']=$toolBin+';'+$start.Environment['PATH']
    $process=[Diagnostics.Process]::Start($start)
    $stdout=$process.StandardOutput.ReadToEndAsync(); $stderr=$process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(60000)) { $process.Kill(); throw 'Native build exceeded60 seconds' }
    [IO.File]::AppendAllText($log,$Tool+' '+($Arguments -join ' ')+[Environment]::NewLine+$stdout.Result+$stderr.Result)
    if ($process.ExitCode -ne 0) { throw ('Native build failed '+$process.ExitCode+' see '+$log) }
}
$includes=@(('/I'+(Join-Path $vc 'include')),('/I'+(Join-Path $sdk "Include\$sdkVersion\ucrt")),('/I'+(Join-Path $sdk "Include\$sdkVersion\shared")),('/I'+(Join-Path $sdk "Include\$sdkVersion\um")))
$includes+=('/I'+(Join-Path $sdk "Include\$sdkVersion\winrt"))
$includes+=('/I'+(Join-Path $externalSource 'src')),('/I'+(Join-Path $externalSource 'inc'))
$compile=@('/nologo','/c','/std:c++17','/W4','/WX','/EHsc','/O2','/MT','/Brepro','/DUNICODE','/D_UNICODE')+$includes
$names=@('hd_visible','hd_visible_d3d9','hd_visible_test','SyntheticDisplayProbe','SyntheticProviderProbe','ConfigAdversarialProbe','hd_visible_gdi','hd_visible_png','hd_visible_gdi_test','hd_visible_gdi_fault_test','SyntheticGdiProviderProbe','GdiWindowOracle','allocator_test','png_regression_test','master_profile_test','profile_gdi_test','png_decode_test','cursor_test')
foreach ($name in $names) { Invoke-Native $compiler ($compile+@(('/Fo'+(Join-Path $nativeOutput ($name+'.obj'))),(Join-Path $PSScriptRoot ($name+'.cpp')))) }
Invoke-Native $compiler ($compile+@(('/FI'+(Join-Path $PSScriptRoot 'gdi_fault_shim.h')),('/Fo'+(Join-Path $nativeOutput 'hd_visible_gdi_injected.obj')),(Join-Path $PSScriptRoot 'hd_visible_gdi.cpp')))
$link=@('/nologo','/MACHINE:X86','/INCREMENTAL:NO','/OPT:REF','/OPT:ICF','/DYNAMICBASE','/NXCOMPAT','/Brepro',
    ('/LIBPATH:'+(Join-Path $vc 'lib\x86')),('/LIBPATH:'+(Join-Path $sdk "Lib\$sdkVersion\um\x86")),('/LIBPATH:'+(Join-Path $sdk "Lib\$sdkVersion\ucrt\x86")))
Invoke-Native $linker ($link+@(('/OUT:'+(Join-Path $nativeOutput 'hd_visible_test.exe')),(Join-Path $nativeOutput 'hd_visible.obj'),(Join-Path $nativeOutput 'hd_visible_test.obj')))
Invoke-Native $linker ($link+@('/DLL',('/DEF:'+(Join-Path $PSScriptRoot 'hd_visible.def')),('/OUT:'+(Join-Path $nativeOutput 'hd_visible_native.dll')),(Join-Path $nativeOutput 'hd_visible.obj'),(Join-Path $nativeOutput 'hd_visible_d3d9.obj'),(Join-Path $nativeOutput 'hd_visible_png.obj'),'ole32.lib','windowscodecs.lib','uuid.lib'))
Invoke-Native $linker ($link+@(('/OUT:'+(Join-Path $nativeOutput 'SyntheticDisplayProbe.exe')),(Join-Path $nativeOutput 'SyntheticDisplayProbe.obj'),'user32.lib','d3d9.lib','ole32.lib','windowscodecs.lib','uuid.lib'))
Invoke-Native $linker ($link+@(('/OUT:'+(Join-Path $nativeOutput 'SyntheticProviderProbe.exe')),(Join-Path $nativeOutput 'SyntheticProviderProbe.obj'),'user32.lib','ole32.lib','windowscodecs.lib','uuid.lib'))
Invoke-Native $linker ($link+@(('/OUT:'+(Join-Path $nativeOutput 'ConfigAdversarialProbe.exe')),(Join-Path $nativeOutput 'ConfigAdversarialProbe.obj')))
foreach ($test in @('hd_visible_gdi_test','hd_visible_gdi_fault_test')) {
    $backend=if ($test -eq 'hd_visible_gdi_fault_test') { 'hd_visible_gdi_injected.obj' } else { 'hd_visible_gdi.obj' }
    Invoke-Native $linker ($link+@(('/OUT:'+(Join-Path $nativeOutput ($test+'.exe'))),(Join-Path $nativeOutput 'hd_visible.obj'),(Join-Path $nativeOutput $backend),(Join-Path $nativeOutput 'hd_visible_png.obj'),(Join-Path $nativeOutput ($test+'.obj')),'bcrypt.lib','gdi32.lib','ole32.lib','windowscodecs.lib','uuid.lib'))
}
Invoke-Native $linker ($link+@(('/OUT:'+(Join-Path $nativeOutput 'SyntheticGdiProviderProbe.exe')),(Join-Path $nativeOutput 'SyntheticGdiProviderProbe.obj'),'user32.lib','bcrypt.lib','gdi32.lib','ole32.lib','windowscodecs.lib','uuid.lib'))
Invoke-Native $linker ($link+@(('/OUT:'+(Join-Path $nativeOutput 'GdiWindowOracle.exe')),(Join-Path $nativeOutput 'GdiWindowOracle.obj'),'user32.lib','bcrypt.lib','gdi32.lib','ole32.lib','windowscodecs.lib','uuid.lib'))
# These three hosts include the exact authored PNG helper once, with test-local
# allocation hooks. Linking the helper object again would duplicate its API.
foreach ($test in @('allocator_test','png_regression_test','master_profile_test')) {
    Invoke-Native $linker ($link+@(('/OUT:'+(Join-Path $nativeOutput ($test+'.exe'))),(Join-Path $nativeOutput ($test+'.obj'))))
}
Invoke-Native $linker ($link+@(('/OUT:'+(Join-Path $nativeOutput 'profile_gdi_test.exe')),(Join-Path $nativeOutput 'profile_gdi_test.obj'),(Join-Path $nativeOutput 'hd_visible.obj'),(Join-Path $nativeOutput 'hd_visible_gdi.obj'),(Join-Path $nativeOutput 'hd_visible_png.obj'),'bcrypt.lib','gdi32.lib','ole32.lib','windowscodecs.lib','uuid.lib'))
Invoke-Native $linker ($link+@(('/OUT:'+(Join-Path $nativeOutput 'png_decode_test.exe')),(Join-Path $nativeOutput 'png_decode_test.obj'),'ole32.lib','windowscodecs.lib','uuid.lib'))
$files=@(Get-ChildItem -LiteralPath $PSScriptRoot -File | Where-Object Extension -in @('.cpp','.h','.def','.ps1','.py'))+@(Get-ChildItem -LiteralPath $nativeOutput -File | Where-Object Extension -in @('.exe','.dll'))
$receipt=@($files | ForEach-Object { [ordered]@{path=$_.FullName;length=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()} })
$receipt | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $nativeOutput 'manifest.json') -Encoding utf8
Write-Output ('Built authored native artifacts at '+$nativeOutput)

Invoke-Native $compiler ($compile+@(('/FI'+(Join-Path $PSScriptRoot 'cursor_fault_shim.h')),('/Fo'+(Join-Path $nativeOutput 'hd_visible_cursor_injected.obj')),(Join-Path $PSScriptRoot 'hd_visible_gdi.cpp')))
Invoke-Native $linker ($link+@(('/OUT:'+(Join-Path $nativeOutput 'cursor_test.exe')),(Join-Path $nativeOutput 'hd_visible.obj'),(Join-Path $nativeOutput 'hd_visible_cursor_injected.obj'),(Join-Path $nativeOutput 'hd_visible_png.obj'),(Join-Path $nativeOutput 'cursor_test.obj'),'bcrypt.lib','gdi32.lib'))
