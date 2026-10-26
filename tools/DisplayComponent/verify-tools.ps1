# Canonical tooling check: two fresh offline builds plus fail-closed input checks.
# Requires the six exact local inputs named in component-pins.json. Never runs a DLL or game.
param(
    [Parameter(Mandatory=$true)][string]$InputCache,
    [Parameter(Mandatory=$true)][string]$ScratchDirectory,
    [string]$VisualStudioRoot='C:\Program Files\Microsoft Visual Studio\2022\Community'
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 or later is required' }
$scratch=[IO.Path]::GetFullPath($ScratchDirectory)
if (Test-Path -LiteralPath $scratch) { throw 'ScratchDirectory must be new' }
for ($item=$scratch; $item; $item=[IO.Path]::GetDirectoryName($item)) {
    if ((Test-Path -LiteralPath $item) -and ((Get-Item -LiteralPath $item -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'ScratchDirectory must not traverse links'
    }
}
New-Item -ItemType Directory -Path $scratch | Out-Null
$setup=Join-Path $PSScriptRoot 'setup-component.ps1'
$build=Join-Path $PSScriptRoot 'build-profile.ps1'
$pins=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'component-pins.json') -Raw | ConvertFrom-Json
$results=[Collections.Generic.List[object]]::new()
function Expect-Failure([string]$Name, [scriptblock]$Action, [string]$Message) {
    $caught=$null
    try { & $Action | Out-Null } catch { $caught=$_.Exception.Message }
    if (-not $caught -or -not $caught.Contains($Message,[StringComparison]::Ordinal)) {
        throw ('Expected failure was not established: '+$Name+'; actual='+$caught)
    }
    $results.Add([ordered]@{name=$Name;passed=$true;observed=$caught})
}

# Failing inputs must not publish a bundle or start a compiler.
$emptyCache=Join-Path $scratch 'empty-cache'
New-Item -ItemType Directory -Path $emptyCache | Out-Null
Expect-Failure 'offline-missing-input' {
    & $setup -WorkDirectory (Join-Path $scratch 'missing-work') -Destination (Join-Path $scratch 'missing-bundle') -InputCache $emptyCache -VisualStudioRoot $VisualStudioRoot
} 'Missing offline input source-v7.1.0.0.zip'
if (Test-Path -LiteralPath (Join-Path $scratch 'missing-bundle')) { throw 'Missing-input test published output' }

$corruptCache=Join-Path $scratch 'corrupt-cache'
New-Item -ItemType Directory -Path $corruptCache | Out-Null
$archive=[IO.File]::ReadAllBytes((Join-Path $InputCache 'source-v7.1.0.0.zip'))
$archive[0]=$archive[0] -bxor 1
[IO.File]::WriteAllBytes((Join-Path $corruptCache 'source-v7.1.0.0.zip'),$archive)
Expect-Failure 'corrupt-input-refused' {
    & $setup -WorkDirectory (Join-Path $scratch 'corrupt-work') -Destination (Join-Path $scratch 'corrupt-bundle') -InputCache $corruptCache -VisualStudioRoot $VisualStudioRoot
} 'Pinned input mismatch: source-v7.1.0.0.zip'
if (Test-Path -LiteralPath (Join-Path $scratch 'corrupt-bundle')) { throw 'Corrupt-input test published output' }
Expect-Failure 'build-archive-pin' {
    & $build -SourceArchive (Join-Path $corruptCache 'source-v7.1.0.0.zip') -Destination (Join-Path $scratch 'bad-build') -VisualStudioRoot $VisualStudioRoot
} 'Wrong source archive'
if (Test-Path -LiteralPath (Join-Path $scratch 'bad-build')) { throw 'Bad-archive build created output' }

$existing=Join-Path $scratch 'existing'
New-Item -ItemType Directory -Path $existing | Out-Null
[IO.File]::WriteAllText((Join-Path $existing 'sentinel.txt'),'keep',[Text.UTF8Encoding]::new($false))
Expect-Failure 'existing-bundle-preserved' {
    & $setup -WorkDirectory (Join-Path $scratch 'existing-work') -Destination $existing -InputCache $InputCache -VisualStudioRoot $VisualStudioRoot
} 'Destination must be new'
if ([IO.File]::ReadAllText((Join-Path $existing 'sentinel.txt')) -cne 'keep') { throw 'Existing component was altered' }

foreach ($number in 1,2) {
    & $setup -WorkDirectory (Join-Path $scratch ('build-'+$number)) -Destination (Join-Path $scratch ('bundle-'+$number)) -InputCache $InputCache -VisualStudioRoot $VisualStudioRoot *> (Join-Path $scratch ('build-'+$number+'.log'))
    if ($LASTEXITCODE -ne 0) { throw ('Fresh setup failed: '+$number) }
}
$fileRows=@()
foreach ($pin in @($pins.components)+@($pins.notices)) {
    $left=Join-Path $scratch ('bundle-1\'+$pin.name)
    $right=Join-Path $scratch ('bundle-2\'+$pin.name)
    $leftBytes=[IO.File]::ReadAllBytes($left)
    $rightBytes=[IO.File]::ReadAllBytes($right)
    if ($leftBytes.Length -ne $pin.length -or $rightBytes.Length -ne $pin.length -or
        -not [Linq.Enumerable]::SequenceEqual[byte]($leftBytes,$rightBytes) -or
        (Get-FileHash -LiteralPath $left -Algorithm SHA256).Hash -ine $pin.sha256) {
        throw ('Fresh builds disagree or have unexpected content: '+$pin.name)
    }
    if ($pin.name.EndsWith('.dll',[StringComparison]::Ordinal)) {
        $pe=[BitConverter]::ToInt32($leftBytes,0x3c)
        if ($leftBytes[0] -ne 0x4d -or $leftBytes[1] -ne 0x5a -or $pe -lt 64 -or $pe -gt $leftBytes.Length-256 -or
            [BitConverter]::ToUInt32($leftBytes,$pe) -ne 0x4550 -or
            [BitConverter]::ToUInt16($leftBytes,$pe+4) -ne 0x14c -or
            ([BitConverter]::ToUInt16($leftBytes,$pe+22) -band 0x2000) -eq 0 -or
            [BitConverter]::ToUInt16($leftBytes,$pe+24) -ne 0x10b) { throw 'Component is not a PE32 x86 DLL' }
    }
    $fileRows += [ordered]@{name=$pin.name;length=$leftBytes.Length;sha256=$pin.sha256}
}
$expectedCount=@($pins.components).Count+@($pins.notices).Count
foreach ($number in 1,2) {
    if (@(Get-ChildItem -LiteralPath (Join-Path $scratch ('bundle-'+$number)) -Force).Count -ne $expectedCount) { throw 'Unexpected bundle files' }
}
$results.Add([ordered]@{name='two-fresh-builds-byte-identical';passed=$true;files=$fileRows})
$receipt=[ordered]@{
    checks=$results
    scriptSha256=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant()
    pinsSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'component-pins.json') -Algorithm SHA256).Hash.ToLowerInvariant()
    executedComponent=$false
}
[IO.File]::WriteAllText((Join-Path $scratch 'verification.json'),($receipt | ConvertTo-Json -Depth 10),[Text.UTF8Encoding]::new($false))
Write-Output ('PASS: '+$results.Count+' component-tooling checks; DLLs never executed')
