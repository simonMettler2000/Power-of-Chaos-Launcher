param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$nativeOutput=[IO.Path]::GetFullPath($OutputDirectory)
. (Join-Path $PSScriptRoot 'local-paths.ps1')
Assert-Private $nativeOutput -Fresh
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
$compile=@('/nologo','/c','/std:c++17','/W4','/EHsc','/O2','/MT','/Brepro','/DUNICODE','/D_UNICODE')+$includes
$names=@('hd_visible','hd_visible_d3d9','hd_visible_test','SyntheticDisplayProbe','SyntheticProviderProbe','ConfigAdversarialProbe')
foreach ($name in $names) { Invoke-Native $compiler ($compile+@(('/Fo'+(Join-Path $nativeOutput ($name+'.obj'))),(Join-Path $PSScriptRoot ($name+'.cpp')))) }
$link=@('/nologo','/MACHINE:X86','/INCREMENTAL:NO','/OPT:REF','/OPT:ICF','/DYNAMICBASE','/NXCOMPAT','/Brepro',
    ('/LIBPATH:'+(Join-Path $vc 'lib\x86')),('/LIBPATH:'+(Join-Path $sdk "Lib\$sdkVersion\um\x86")),('/LIBPATH:'+(Join-Path $sdk "Lib\$sdkVersion\ucrt\x86")))
Invoke-Native $linker ($link+@(('/OUT:'+(Join-Path $nativeOutput 'hd_visible_test.exe')),(Join-Path $nativeOutput 'hd_visible.obj'),(Join-Path $nativeOutput 'hd_visible_test.obj')))
Invoke-Native $linker ($link+@('/DLL',('/DEF:'+(Join-Path $PSScriptRoot 'hd_visible.def')),('/OUT:'+(Join-Path $nativeOutput 'hd_visible_native.dll')),(Join-Path $nativeOutput 'hd_visible.obj'),(Join-Path $nativeOutput 'hd_visible_d3d9.obj'),'ole32.lib','windowscodecs.lib','uuid.lib'))
Invoke-Native $linker ($link+@(('/OUT:'+(Join-Path $nativeOutput 'SyntheticDisplayProbe.exe')),(Join-Path $nativeOutput 'SyntheticDisplayProbe.obj'),'user32.lib','d3d9.lib','ole32.lib','windowscodecs.lib','uuid.lib'))
Invoke-Native $linker ($link+@(('/OUT:'+(Join-Path $nativeOutput 'SyntheticProviderProbe.exe')),(Join-Path $nativeOutput 'SyntheticProviderProbe.obj'),'user32.lib','ole32.lib','windowscodecs.lib','uuid.lib'))
Invoke-Native $linker ($link+@(('/OUT:'+(Join-Path $nativeOutput 'ConfigAdversarialProbe.exe')),(Join-Path $nativeOutput 'ConfigAdversarialProbe.obj')))
$files=@(Get-ChildItem -LiteralPath $PSScriptRoot -File | Where-Object Extension -in @('.cpp','.h','.def','.ps1','.py'))+@(Get-ChildItem -LiteralPath $nativeOutput -File | Where-Object Extension -in @('.exe','.dll'))
$receipt=@($files | ForEach-Object { [ordered]@{path=$_.FullName;length=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()} })
$receipt | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $nativeOutput 'manifest.json') -Encoding utf8
Write-Output ('Built authored native artifacts at '+$nativeOutput)
