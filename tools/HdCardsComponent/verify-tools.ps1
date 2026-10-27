# Bounded fail-closed checks. No compiler, component DLL or original game is run.
param([Parameter(Mandatory=$true)][string]$ScratchDirectory)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'local-paths.ps1')
$scratch=[IO.Path]::GetFullPath($ScratchDirectory)
Assert-Private $scratch -Fresh
New-Item -ItemType Directory -Path $scratch | Out-Null
New-Item -ItemType Directory -Path (Join-Path $scratch 'Components'),(Join-Path $scratch 'cache') | Out-Null
$setup=Join-Path $PSScriptRoot 'setup-component.ps1'
$results=[Collections.Generic.List[object]]::new()
function Expect-Failure([string]$Name,[scriptblock]$Action,[string]$Message) {
    $caught=$null
    try { & $Action | Out-Null } catch { $caught=$_.Exception.Message }
    if (-not $caught -or -not $caught.Contains($Message,[StringComparison]::Ordinal)) { throw ('Expected failure absent: '+$Name+'; '+$caught) }
    $results.Add([ordered]@{name=$Name;passed=$true;observed=$caught})
}
Expect-Failure 'outside-scratch' { Assert-Private (Join-Path $PSScriptRoot 'unexpected-output') -Fresh } 'inside repository .local'
$existing=Join-Path $scratch 'existing'
New-Item -ItemType Directory -Path $existing | Out-Null
[IO.File]::WriteAllText((Join-Path $existing 'sentinel'),'keep')
Expect-Failure 'existing-output' { Assert-Private $existing -Fresh } 'Destination must be new'
if ([IO.File]::ReadAllText((Join-Path $existing 'sentinel')) -cne 'keep') { throw 'Existing output changed' }
$work=Join-Path $scratch 'missing-work'
$destination=Join-Path $scratch 'Components\hd-card-artwork-v1'
Expect-Failure 'offline-missing-input' { & $setup -WorkDirectory $work -Destination $destination -InputCache (Join-Path $scratch 'cache') } 'Missing offline input source-v7.1.0.0.zip'
if ((Test-Path -LiteralPath $work) -or (Test-Path -LiteralPath $destination)) { throw 'Missing-input check wrote output' }
[IO.File]::WriteAllText((Join-Path $scratch 'cache\source-v7.1.0.0.zip'),'synthetic-corrupt-archive')
Expect-Failure 'corrupt-archive' { & $setup -WorkDirectory $work -Destination $destination -InputCache (Join-Path $scratch 'cache') } 'Pinned input mismatch: source-v7.1.0.0.zip'
if ((Test-Path -LiteralPath $work) -or (Test-Path -LiteralPath $destination)) { throw 'Corrupt-input check wrote output' }
Expect-Failure 'bundle-directory' { & $setup -WorkDirectory $work -Destination (Join-Path $scratch 'Components\wrong') -InputCache (Join-Path $scratch 'cache') } 'Destination must end in Components/hd-card-artwork-v1'
foreach ($file in Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1') {
    $parseTokens=$null;$parseErrors=$null
    [Management.Automation.Language.Parser]::ParseFile($file.FullName,[ref]$parseTokens,[ref]$parseErrors) | Out-Null
    if ($parseErrors.Count) { throw ('PowerShell syntax failed: '+$file.Name) }
}
$results.Add([ordered]@{name='powershell-syntax';passed=$true})
$results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $scratch 'verification.json') -Encoding utf8
Write-Output ('PASS: '+$results.Count+' fail-closed tooling checks; no compiler or game executed')
