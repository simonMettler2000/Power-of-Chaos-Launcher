param([Parameter(Mandatory=$true)][string]$OutputDirectory,[Parameter(Mandatory=$true)][string]$SourceDirectory,[switch]$Synthetic)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$providerOutput=[IO.Path]::GetFullPath($OutputDirectory)
. (Join-Path $PSScriptRoot 'local-paths.ps1')
Assert-Private $providerOutput -Fresh
& python -B (Join-Path $PSScriptRoot 'verify-toolchain.py')
if ($LASTEXITCODE -ne 0) { throw 'Pinned toolchain mismatch' }
New-Item -ItemType Directory -Path $providerOutput | Out-Null
$msbuild='C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe'
if ((Get-FileHash -LiteralPath $msbuild -Algorithm SHA256).Hash -ne '17904D23183D310D63DAE141DC93EC0BD7598125C65EC7F3C3C437479E3F1A02') { throw 'MSBuild pin mismatch' }
$providerSource=[IO.Path]::GetFullPath($SourceDirectory)
Assert-Private $providerSource
Assert-NoTreeLinks $providerSource
$sourceKind=if ($Synthetic) { 'synthetic' } else { 'production' }
& python -B (Join-Path $PSScriptRoot 'verify_source.py') $providerSource $sourceKind
if ($LASTEXITCODE -ne 0) { throw 'Exact provider source tree pin mismatch' }
$project=Join-Path $providerSource 'cnc-ddraw.vcxproj'
$arguments=@($project,'/t:Build','/p:Configuration=Release','/p:Platform=Win32','/p:PlatformToolset=v143',
    '/p:VCToolsVersion=14.41.34120','/p:WindowsTargetPlatformVersion=10.0.22621.0','/p:PreferredToolArchitecture=x64',
    '/p:ImportDirectoryBuildProps=false','/p:ImportDirectoryBuildTargets=false',
    ('/p:UserRootDir='+$providerOutput+'\unused-user-props\'),('/p:OutDir='+$providerOutput+'\out\'),('/p:IntDir='+$providerOutput+'\int\'),
    '/p:LanguageStandard_C=stdc11','/m:1','/v:minimal','/nologo')
$arguments | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $providerOutput 'arguments.json') -Encoding utf8
$saved=@{}
foreach ($key in @('CL','_CL_','LINK','_LINK_','INCLUDE','LIB','LIBPATH')) { $saved[$key]=[Environment]::GetEnvironmentVariable($key); [Environment]::SetEnvironmentVariable($key,$null) }
try {
    & $msbuild @arguments *> (Join-Path $providerOutput 'build.log')
    $result=$LASTEXITCODE
} finally { foreach ($key in $saved.Keys) { [Environment]::SetEnvironmentVariable($key,$saved[$key]) } }
if ($result -ne 0) { throw ('Provider build failed '+$result+'; see '+$providerOutput+'\build.log') }
$dll=Join-Path $providerOutput 'out\ddraw.dll'
$expectedLength=if ($Synthetic) { 549888 } else { 546816 }
$expectedHash=if ($Synthetic) { 'c30016fe11669b2dba273b5be8b775a4f51380a99be971add29ec0d72993cbe5' } else { '70672798af371ce85c0264a52fd83f83d6e0977d32713299c94f9739245f5430' }
if ((Get-Item -LiteralPath $dll).Length -ne $expectedLength -or (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash -ine $expectedHash) { throw 'HD output pin mismatch' }
[ordered]@{exitCode=$result;dll=$dll;sha256=(Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.ToLowerInvariant();length=(Get-Item -LiteralPath $dll).Length;executed=$false} |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $providerOutput 'build-receipt.json') -Encoding utf8
Write-Output ('Built local patched provider without execution: '+$dll)
