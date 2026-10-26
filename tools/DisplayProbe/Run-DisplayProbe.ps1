[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$BuildDirectory,
    [Parameter(Mandatory = $true)][string]$ComponentDirectory,
    [Parameter(Mandatory = $true)][string]$RuntimeDirectory,
    [ValidateSet('normal', 'virtual')][string]$Mode = 'normal',
    [ValidateSet('auto', 'gdi')][string]$Renderer = 'auto',
    [switch]$Interactive,
    [ValidateRange(1, 180)][int]$InteractiveSeconds = 180
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:OS -ne 'Windows_NT') { throw 'The synthetic native probe requires Windows.' }
function Get-ProbePath([string]$Path) {
    if ($Path.IndexOfAny([char[]]@('"', "`r", "`n")) -ge 0) { throw 'Unsupported quote or newline in a path.' }
    $probeFull = [IO.Path]::GetFullPath($Path)
    $probeAncestor = $probeFull
    while ($probeAncestor) {
        if ((Test-Path -LiteralPath $probeAncestor) -and ((Get-Item -LiteralPath $probeAncestor).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Probe paths cannot traverse directory links.' }
        $probeNext = Split-Path $probeAncestor -Parent
        if ($probeNext -eq $probeAncestor) { break }
        $probeAncestor = $probeNext
    }
    return $probeFull
}
$probeBuild = Get-ProbePath $BuildDirectory
$probeBundle = Get-ProbePath $ComponentDirectory
$probeRuntime = Get-ProbePath $RuntimeDirectory
if (Test-Path -LiteralPath $probeRuntime) { throw 'RuntimeDirectory must be fresh; previous evidence is never overwritten.' }
if ($probeRuntime.Length -gt 205) { throw 'Use a shorter RuntimeDirectory so all component paths remain within MAX_PATH.' }
if ($probeRuntime.ToCharArray() | Where-Object { [int]$_ -gt 127 }) { throw 'Use an ASCII RuntimeDirectory because the pinned wrapper reads its module-relative INI through ANSI APIs.' }
$probeRepo = (& git -C $PSScriptRoot rev-parse --show-toplevel 2>$null)
if ($LASTEXITCODE -ne 0) { throw 'Run this tool from a source checkout with Git available.' }
$probeRepo = [IO.Path]::GetFullPath([string]$probeRepo)
if ($probeRuntime.Equals($probeRepo, [StringComparison]::OrdinalIgnoreCase)) { throw 'RuntimeDirectory cannot be the repository root.' }
if ($probeRuntime.StartsWith($probeRepo.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    & git -C $PSScriptRoot check-ignore -- (Join-Path $probeRuntime 'probe-output-check') | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Runtime output inside the repository must be gitignored.' }
}
# An observer beside any app-local loader would no longer be an independent witness.
foreach ($probeForbidden in @('winmm.dll', 'ddraw.dll')) { if (Test-Path -LiteralPath (Join-Path $probeBuild $probeForbidden)) { throw 'BuildDirectory must contain no component or proxy DLL.' } }
$probeBuildReceipt = Get-Content -LiteralPath (Join-Path $probeBuild 'build-receipt.json') -Raw | ConvertFrom-Json
if ($probeBuildReceipt.kind -ne 'OriginalSyntheticDisplayProbe' -or $probeBuildReceipt.architecture -ne 'x86') { throw 'Build receipt is not for the authored synthetic x86 probe.' }
foreach ($probeName in @('DisplayProbe.cpp', 'ComponentIdentity.cpp', 'Build-DisplayProbe.ps1', 'Run-DisplayProbe.ps1', 'DisplayProbe.exe', 'DisplayObserver.exe')) {
    $probeEntries = @($probeBuildReceipt.files | Where-Object { $_.name -ceq $probeName })
    if ($probeEntries.Count -ne 1) { throw ('Build receipt needs exactly one ' + $probeName) }
    $probePath = if ($probeName.EndsWith('.exe')) { Join-Path $probeBuild $probeName } else { Join-Path $PSScriptRoot $probeName }
    $probeItem = Get-Item -LiteralPath $probePath
    if ($probeItem.Length -ne $probeEntries[0].length -or (Get-FileHash -LiteralPath $probePath -Algorithm SHA256).Hash -ine $probeEntries[0].sha256) { throw ('Build input/output changed: ' + $probeName) }
}
$probePins = @(
    @{name='ddraw.dll'; length=424448; sha256='0f5f094dd1ab1bc23cfdc1fe4c1ed51d91c1c9a5fafd063c9897fa018606be91'},
    @{name='winmm.dll'; length=98304; sha256='1de1ed07c548e262767ba06891284ae0ecc39f6cc0217dd715594e5bc30e0e8a'}
)
foreach ($probePin in $probePins) {
    $probePath = Join-Path $probeBundle $probePin.name
    $probeItem = Get-Item -LiteralPath $probePath
    if (($probeItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $probeItem.Length -ne $probePin.length -or (Get-FileHash -LiteralPath $probePath -Algorithm SHA256).Hash -ine $probePin.sha256) { throw ('Component pin mismatch: ' + $probePin.name) }
}
$probeApp = Join-Path $probeRuntime 'component'
$probeOutput = Join-Path $probeRuntime 'evidence'
$probeFixture = Join-Path $probeRuntime 'fixture-cwd'
New-Item -ItemType Directory -Path $probeApp, $probeOutput, $probeFixture | Out-Null
Copy-Item -LiteralPath (Join-Path $probeBuild 'DisplayProbe.exe') -Destination (Join-Path $probeApp 'DisplayProbe.exe')
foreach ($probePin in $probePins) { Copy-Item -LiteralPath (Join-Path $probeBundle $probePin.name) -Destination (Join-Path $probeApp $probePin.name) }
$probeIni = @"
[ddraw]
width=800
height=600
windowed=true
fullscreen=false
maintas=true
aspect_ratio=4:3
boxing=false
maxgameticks=-1
limiter_type=0
minfps=0
savesettings=0
resizable=true
border=true
toggle_borderless=true
toggle_upscaled=false
renderer=$Renderer
d3d9_filter=1
shader=Nearest neighbor
maxfps=-1
vsync=false
adjmouse=true
keyscreenshot=0x00
screenshotdir=$probeOutput\
singlecpu=false
fixchilds=0
vhack=false
hook_peekmessage=false
posX=-32000
posY=-32000
"@
[IO.File]::WriteAllText((Join-Path $probeApp 'ddraw.ini'), $probeIni, [Text.Encoding]::ASCII)
function Get-ProbeManifest {
    Get-ChildItem -LiteralPath $probeApp -File | ForEach-Object { [ordered]@{name=$_.Name; length=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()} }
}
Get-ProbeManifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $probeOutput 'component-before.json') -Encoding UTF8
$probeInfo = [Diagnostics.ProcessStartInfo]::new()
$probeInfo.FileName = Join-Path $probeApp 'DisplayProbe.exe'
$probeInfo.Arguments = '--dll "' + (Join-Path $probeApp 'ddraw.dll') + '" --observer-path "' + (Join-Path $probeBuild 'DisplayObserver.exe') + '" --output "' + $probeOutput + '"'
if ($Mode -eq 'virtual') { $probeInfo.Arguments += ' --wrapper-virtual --verified-windowed-profile' }
if ($Interactive) { $probeInfo.Arguments += ' --interactive --interactive-seconds ' + $InteractiveSeconds }
$probeInfo.WorkingDirectory = $probeFixture
$probeInfo.UseShellExecute = $false
$probeInfo.CreateNoWindow = $true
$probeInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
foreach ($probeKey in @($probeInfo.EnvironmentVariables.Keys)) { if ($probeKey -ieq 'CNC_DDRAW_CONFIG_FILE' -or $probeKey -ieq 'CNC_DDRAW_CONFIG_INIT') { $probeInfo.EnvironmentVariables.Remove($probeKey) } }
$probeWatch = [Diagnostics.Stopwatch]::StartNew()
$probeProcess = [Diagnostics.Process]::Start($probeInfo)
$probeReceipt = [ordered]@{kind='OriginalSyntheticDisplayProbeRun'; pid=$probeProcess.Id; startedUtc=[DateTime]::UtcNow.ToString('O'); mode=$Mode; renderer=$Renderer; interactive=[bool]$Interactive; interactiveSeconds=$InteractiveSeconds; output=$probeOutput; workingDirectory=$probeFixture; arguments=$probeInfo.Arguments; title=('Synthetic Display Probe (PID ' + $probeProcess.Id + ') - N next stage, Esc close')}
if ($Interactive) {
    # The independent observer also enforces the bound if component initialization/presentation stalls.
    $probeWatchInfo = [Diagnostics.ProcessStartInfo]::new()
    $probeWatchInfo.FileName = Join-Path $probeBuild 'DisplayObserver.exe'
    $probeWatchInfo.Arguments = '--output "' + $probeOutput + '" --watch-owned-process ' + $probeProcess.Id + ' --expected-executable "' + $probeInfo.FileName + '" --watch-seconds ' + $InteractiveSeconds
    $probeWatchInfo.WorkingDirectory = $probeBuild
    $probeWatchInfo.UseShellExecute = $false
    $probeWatchInfo.CreateNoWindow = $true
    $probeWatchInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    try { $probeDeadlineProcess = [Diagnostics.Process]::Start($probeWatchInfo) } catch { $probeProcess.Kill(); throw }
    $probeReceipt['watchdogPid'] = $probeDeadlineProcess.Id
    $probeReceipt | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $probeOutput 'execution.json') -Encoding UTF8
    # Only CUA or the human supplies input. No global input automation is used.
    $probeReceipt | ConvertTo-Json -Depth 4
    return
}
$probeReceipt | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $probeOutput 'execution.json') -Encoding UTF8
$probeFinished = $probeProcess.WaitForExit(25000)
if (-not $probeFinished) { $probeProcess.Kill(); $probeProcess.WaitForExit() }
$probeWatch.Stop()
$probeReceipt['finished'] = $probeFinished
$probeReceipt['exit'] = $probeProcess.ExitCode
$probeReceipt['elapsedSeconds'] = $probeWatch.Elapsed.TotalSeconds
$probeReceipt | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $probeOutput 'execution.json') -Encoding UTF8
$probeReceipt | ConvertTo-Json -Depth 4
Get-ProbeManifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $probeOutput 'component-after.json') -Encoding UTF8
Get-Content -LiteralPath (Join-Path $probeOutput 'probe.log')
if (-not $probeFinished -or $probeProcess.ExitCode -ne 0) { throw 'Synthetic probe did not pass. Inspect retained evidence; auto-renderer capture failure alone is not proof of a visible rendering failure.' }
