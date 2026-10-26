[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$VisualStudioDirectory,
    [string]$WindowsSdkDirectory = (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10'),
    [string]$ToolsetVersion = '14.41.34120',
    [string]$WindowsSdkVersion = '10.0.22621.0'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($env:OS -ne 'Windows_NT') { throw 'The synthetic native probe requires Windows.' }
$probeOutput = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $probeOutput) { throw 'OutputDirectory must be a fresh directory.' }
$probeAncestor = Split-Path $probeOutput -Parent
while ($probeAncestor) {
    if ((Test-Path -LiteralPath $probeAncestor) -and ((Get-Item -LiteralPath $probeAncestor).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'OutputDirectory cannot traverse a directory link.' }
    $probeNext = Split-Path $probeAncestor -Parent
    if ($probeNext -eq $probeAncestor) { break }
    $probeAncestor = $probeNext
}
$probeRepo = (& git -C $PSScriptRoot rev-parse --show-toplevel 2>$null)
if ($LASTEXITCODE -ne 0) { throw 'Run this tool from a source checkout with Git available.' }
$probeRepo = [IO.Path]::GetFullPath([string]$probeRepo)
if ($probeOutput.Equals($probeRepo, [StringComparison]::OrdinalIgnoreCase)) { throw 'OutputDirectory cannot be the repository root.' }
if ($probeOutput.StartsWith($probeRepo.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    & git -C $PSScriptRoot check-ignore -- (Join-Path $probeOutput 'probe-output-check') | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Build output inside the repository must be gitignored.' }
}
if ([string]::IsNullOrWhiteSpace($VisualStudioDirectory)) {
    $probeVswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $probeVswhere -PathType Leaf)) { throw 'Supply VisualStudioDirectory or install Visual Studio C++ build tools.' }
    $VisualStudioDirectory = (& $probeVswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($VisualStudioDirectory)) { throw 'Visual Studio x86 C++ tools were not found.' }
}
$probeVc = Join-Path $VisualStudioDirectory ('VC\Tools\MSVC\' + $ToolsetVersion)
$probeBin = Join-Path $probeVc 'bin\Hostx64\x86'
$probeCompiler = Join-Path $probeBin 'cl.exe'
$probeLinker = Join-Path $probeBin 'link.exe'
$probeSdkInclude = Join-Path $WindowsSdkDirectory ('Include\' + $WindowsSdkVersion)
$probeSdkLib = Join-Path $WindowsSdkDirectory ('Lib\' + $WindowsSdkVersion)
$probeSources = @((Join-Path $PSScriptRoot 'DisplayProbe.cpp'), (Join-Path $PSScriptRoot 'ComponentIdentity.cpp'))
$probeRequired = @($probeCompiler, $probeLinker, (Join-Path $probeBin 'c1xx.dll'), (Join-Path $probeSdkInclude 'um\ddraw.h')) + $probeSources
foreach ($probeRequiredPath in $probeRequired) { if (-not (Test-Path -LiteralPath $probeRequiredPath -PathType Leaf)) { throw ('Required build input missing: ' + $probeRequiredPath) } }
New-Item -ItemType Directory -Path $probeOutput | Out-Null
$probeLog = Join-Path $probeOutput 'build.log'

function Invoke-ProbeTool([string]$FilePath, [string[]]$ToolArguments) {
    # Each argument is passed to a native process, never evaluated as shell code.
    # Windows file names cannot contain a quote; reject quotes/newlines in all inputs.
    foreach ($probeArgument in $ToolArguments) { if ($probeArgument.IndexOfAny([char[]]@('"', "`r", "`n")) -ge 0) { throw 'Unsupported quote or newline in a build argument.' } }
    $probeInfo = [Diagnostics.ProcessStartInfo]::new()
    $probeInfo.FileName = $FilePath
    $probeInfo.Arguments = (($ToolArguments | ForEach-Object { '"' + $_ + '"' }) -join ' ')
    $probeInfo.WorkingDirectory = $probeOutput
    $probeInfo.UseShellExecute = $false
    $probeInfo.CreateNoWindow = $true
    $probeInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $probeInfo.RedirectStandardOutput = $true
    $probeInfo.RedirectStandardError = $true
    foreach ($probeKey in @('CL', '_CL_', 'LINK', '_LINK_', 'INCLUDE', 'LIB', 'LIBPATH')) { $probeInfo.EnvironmentVariables.Remove($probeKey) }
    $probeInfo.EnvironmentVariables['PATH'] = $probeBin + ';' + $probeInfo.EnvironmentVariables['PATH']
    $probeProcess = [Diagnostics.Process]::Start($probeInfo)
    $probeStdout = $probeProcess.StandardOutput.ReadToEndAsync()
    $probeStderr = $probeProcess.StandardError.ReadToEndAsync()
    if (-not $probeProcess.WaitForExit(60000)) { $probeProcess.Kill(); throw 'Synthetic probe build tool timed out.' }
    [IO.File]::AppendAllText($probeLog, $FilePath + ' ' + $probeInfo.Arguments + [Environment]::NewLine + $probeStdout.Result + $probeStderr.Result)
    if ($probeProcess.ExitCode -ne 0) { throw ('Build failed with exit ' + $probeProcess.ExitCode + '; inspect ' + $probeLog) }
}
$probeIncludes = @(('/I' + (Join-Path $probeVc 'include')), ('/I' + (Join-Path $probeSdkInclude 'ucrt')), ('/I' + (Join-Path $probeSdkInclude 'shared')), ('/I' + (Join-Path $probeSdkInclude 'um')))
$probeCompile = @('/nologo', '/c', '/std:c++17', '/W4', '/WX', '/EHsc', '/O2', '/MT', '/Brepro', '/DUNICODE', '/D_UNICODE') + $probeIncludes
$probeHostObject = Join-Path $probeOutput 'DisplayProbe.obj'
$probeObserverObject = Join-Path $probeOutput 'DisplayObserver.obj'
$probeIdentityObject = Join-Path $probeOutput 'ComponentIdentity.obj'
Invoke-ProbeTool $probeCompiler ($probeCompile + @(('/Fo' + $probeIdentityObject), $probeSources[1]))
Invoke-ProbeTool $probeCompiler ($probeCompile + @(('/Fo' + $probeHostObject), $probeSources[0]))
Invoke-ProbeTool $probeCompiler ($probeCompile + @('/DASPECT_PROBE_OBSERVER', ('/Fo' + $probeObserverObject), $probeSources[0]))
$probeLink = @('/nologo', '/MACHINE:X86', '/SUBSYSTEM:CONSOLE', '/INCREMENTAL:NO', '/OPT:REF', '/OPT:ICF', '/DYNAMICBASE', '/NXCOMPAT', '/Brepro', ('/LIBPATH:' + (Join-Path $probeVc 'lib\x86')), ('/LIBPATH:' + (Join-Path $probeSdkLib 'um\x86')), ('/LIBPATH:' + (Join-Path $probeSdkLib 'ucrt\x86')), 'user32.lib', 'gdi32.lib', 'ole32.lib', 'dxguid.lib')
$probeHost = Join-Path $probeOutput 'DisplayProbe.exe'
$probeObserver = Join-Path $probeOutput 'DisplayObserver.exe'
Invoke-ProbeTool $probeLinker ($probeLink + @(('/OUT:' + $probeHost), $probeHostObject, $probeIdentityObject, 'winmm.lib'))
Invoke-ProbeTool $probeLinker ($probeLink + @(('/OUT:' + $probeObserver), $probeObserverObject, $probeIdentityObject))
$probeFiles = @($probeSources) + @($PSCommandPath, (Join-Path $PSScriptRoot 'Run-DisplayProbe.ps1'), (Join-Path $PSScriptRoot 'README.md'), $probeHost, $probeObserver, $probeCompiler, $probeLinker, (Join-Path $probeBin 'c1xx.dll'), (Join-Path $probeSdkInclude 'um\ddraw.h'))
$probeManifest = foreach ($probeFile in $probeFiles) { $probeItem = Get-Item -LiteralPath $probeFile; [ordered]@{name=$probeItem.Name; path=$probeItem.FullName; length=$probeItem.Length; sha256=(Get-FileHash -LiteralPath $probeItem.FullName -Algorithm SHA256).Hash.ToLowerInvariant()} }
[ordered]@{kind='OriginalSyntheticDisplayProbe'; architecture='x86'; toolset=$ToolsetVersion; sdk=$WindowsSdkVersion; files=@($probeManifest)} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $probeOutput 'build-receipt.json') -Encoding UTF8
Get-Content -LiteralPath (Join-Path $probeOutput 'build-receipt.json')
