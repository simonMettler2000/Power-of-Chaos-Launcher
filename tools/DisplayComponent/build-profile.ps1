param(
    [Parameter(Mandatory=$true)][string]$SourceArchive,
    [Parameter(Mandatory=$true)][string]$Destination,
    [string]$MaximizeSource=(Join-Path $PSScriptRoot 'launcher-maximize.c'),
    [string]$VisualStudioRoot='C:\Program Files\Microsoft Visual Studio\2022\Community'
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 or later is required' }
function Assert-NoLinks([string]$Path) {
    for ($item=[IO.Path]::GetFullPath($Path); $item; $item=[IO.Path]::GetDirectoryName($item)) {
        if ((Test-Path -LiteralPath $item) -and ((Get-Item -LiteralPath $item -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw ('Linked paths are not supported: '+$item)
        }
    }
}
Assert-NoLinks $SourceArchive
Assert-NoLinks $MaximizeSource
Assert-NoLinks $Destination
$expectedArchive='A4F638383364E3BC1E4682B1D549A1F5400FA0CC312EE216D1B42555B1C814EB'
if ((Get-FileHash -LiteralPath $MaximizeSource -Algorithm SHA256).Hash -ne '2AF741CC89340AC6CB5D889B2805CFF5B50EBAF51BD03E7A4F54E594D5518984') { throw 'Wrong authored maximize source' }
if ((Get-FileHash -LiteralPath $SourceArchive -Algorithm SHA256).Hash -ne $expectedArchive) { throw 'Wrong source archive' }
if (Test-Path -LiteralPath $Destination) { throw 'Destination must be new' }
$destinationPath=[System.IO.Path]::GetFullPath($Destination)
New-Item -ItemType Directory -Path $destinationPath | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip=[System.IO.Compression.ZipFile]::OpenRead($SourceArchive)
try {
    if ($zip.Entries.Count -gt 500) { throw 'Too many entries' }
    $total=0L
    $names=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $zip.Entries) {
        if ([System.IO.Path]::IsPathRooted($entry.FullName) -or $entry.FullName -match '(^|[\\/])\.\.([\\/]|$)|:' -or
            -not $entry.FullName.StartsWith('cnc-ddraw-7.1.0.0/',[StringComparison]::Ordinal) -or
            -not $names.Add($entry.FullName.Replace('\','/')) -or
            (($entry.ExternalAttributes -shr 16) -band 0xf000) -eq 0xa000) { throw 'Unsafe archive path' }
        $total += $entry.Length
        if ($total -gt 50000000) { throw 'Archive exceeds limit' }
    }
} finally { $zip.Dispose() }
Expand-Archive -LiteralPath $SourceArchive -DestinationPath (Join-Path $destinationPath 'source')
$sourceRoot=Join-Path $destinationPath 'source\cnc-ddraw-7.1.0.0'
$expectedSources=@{
    'src\dllmain.c'='47D7C0AD8FA032EAF73B6C8A3FE7D2CB4FBFACF42E11461A0BC266407C6D18D0'
    'src\utils.c'='9E6FDF08956964692A0E1E232B495EE1DAAE3A543AFE786D24FE51FD3655F157'
    'src\dd.c'='FCAAB9E132B574F094B7948F43ADAABB9EF4D1F0E7B69FF9881801CFB24C12EF'
    'cnc-ddraw.vcxproj'='C6DFBDE83432117B2E1570CA17BDFAFC80D67C00A47F95E7EF3E8B9C6AEEFCCB'
}
foreach ($item in $expectedSources.GetEnumerator()) {
    if ((Get-FileHash -LiteralPath (Join-Path $sourceRoot $item.Key) -Algorithm SHA256).Hash -ne $item.Value) { throw ('Wrong source: '+$item.Key) }
}
$originalManifest=@(Get-ChildItem -LiteralPath $sourceRoot -Recurse -File | Sort-Object FullName | ForEach-Object {
    [ordered]@{path=$_.FullName.Substring($sourceRoot.Length+1);sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
})
$dllPath=Join-Path $sourceRoot 'src\dllmain.c'
$dllText=[System.IO.File]::ReadAllText($dllPath)
if (($dllText.Split('        indeo_enable();').Count-1) -ne 1 -or ($dllText.Split('        indeo_disable();').Count-1) -ne 1) { throw 'Indeo patch precondition mismatch' }
$dllText=$dllText.Replace('        indeo_enable();','        /* Launcher profile: do not register legacy codecs. */').Replace('        indeo_disable();','        /* Launcher profile: never delete user codec registrations. */')
$start=$dllText.IndexOf('        /* WINE does not support SetThreadExecutionState')
$end=$dllText.IndexOf('        /* Launcher profile: do not register legacy codecs. */',$start)
if ($start -lt 0 -or $end -le $start) { throw 'Screensaver patch precondition mismatch' }
$dllText=$dllText.Substring(0,$start)+$dllText.Substring($end)
$start=$dllText.IndexOf('        if (g_screensaver_disabled)')
$end=$dllText.IndexOf('        ULONG(WINAPI* remove_handler)',$start)
if ($start -lt 0 -or $end -le $start) { throw 'Screensaver restore patch precondition mismatch' }
$dllText=$dllText.Substring(0,$start)+$dllText.Substring($end)
[System.IO.File]::WriteAllText($dllPath,$dllText)
$utilsPath=Join-Path $sourceRoot 'src\utils.c'
$utilsText=[System.IO.File]::ReadAllText($utilsPath)
$start=$utilsText.IndexOf('void util_toggle_maximize()')
$end=$utilsText.IndexOf('void util_toggle_fullscreen()',$start)
if ($start -lt 0 -or $end -le $start) { throw 'Maximize patch precondition mismatch' }
$maxCode=[System.IO.File]::ReadAllText($MaximizeSource).TrimEnd()
$utilsText=$utilsText.Substring(0,$start)+$maxCode+"`r`n`r`n"+$utilsText.Substring($end)
[System.IO.File]::WriteAllText($utilsPath,$utilsText)
$ddPath=Join-Path $sourceRoot 'src\dd.c'
$ddText=[System.IO.File]::ReadAllText($ddPath)
$start=$ddText.IndexOf('        if (g_config.singlecpu)')
$end=$ddText.IndexOf('        if (GetProcessAffinityMask(proc, &proc_affinity, &system_affinity))',$start)
if ($start -lt 0 -or $end -le $start) { throw 'Affinity patch precondition mismatch' }
$ddText=$ddText.Substring(0,$start)+'        /* Launcher profile: preserve the inherited process affinity. */'+"`r`n`r`n"+$ddText.Substring($end)
[System.IO.File]::WriteAllText($ddPath,$ddText)
$projectPath=Join-Path $sourceRoot 'cnc-ddraw.vcxproj'
[xml]$project=[System.IO.File]::ReadAllText($projectPath)
$ns=[System.Xml.XmlNamespaceManager]::new($project.NameTable)
$ns.AddNamespace('m','http://schemas.microsoft.com/developer/msbuild/2003')
foreach ($event in @($project.SelectNodes('//m:PreBuildEvent | //m:PostBuildEvent',$ns))) { $event.ParentNode.RemoveChild($event) | Out-Null }
foreach ($node in $project.SelectNodes('//m:WholeProgramOptimization',$ns)) { $node.InnerText='false' }
foreach ($node in $project.SelectNodes('//m:ClCompile[m:Optimization]',$ns)) {
    $item=$project.CreateElement('DebugInformationFormat',$ns.LookupNamespace('m')); $item.InnerText='None'; $node.AppendChild($item) | Out-Null
}
foreach ($node in $project.SelectNodes('//m:Link',$ns)) {
    $item=$project.CreateElement('GenerateDebugInformation',$ns.LookupNamespace('m')); $item.InnerText='false'; $node.AppendChild($item) | Out-Null
    $item=$project.CreateElement('AdditionalOptions',$ns.LookupNamespace('m')); $item.InnerText='/Brepro %(AdditionalOptions)'; $node.AppendChild($item) | Out-Null
}
$project.Save($projectPath)
Set-Content -LiteralPath (Join-Path $sourceRoot 'inc\git.h') -Value '#define GIT_COMMIT "541b5de-launcher-test"', '#define GIT_BRANCH "launcher-profile"' -Encoding ascii
$toolsRoot=Join-Path $VisualStudioRoot 'VC\Tools\MSVC\14.41.34120\bin\Hostx64\x86'
$msbuild=Join-Path $VisualStudioRoot 'MSBuild\Current\Bin\MSBuild.exe'
$tools=@($msbuild,(Join-Path $toolsRoot 'cl.exe'),(Join-Path $toolsRoot 'c1.dll'),(Join-Path $toolsRoot 'c2.dll'),(Join-Path $toolsRoot 'link.exe'),'C:\Program Files (x86)\Windows Kits\10\bin\10.0.22621.0\x64\rc.exe',(Join-Path $toolsRoot 'c1xx.dll'),'C:\Program Files (x86)\Windows Kits\10\bin\10.0.22621.0\x64\rcdll.dll')
$toolManifest=@($tools | ForEach-Object { [ordered]@{path=$_;sha256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash} })
$expectedTools=@(
    '17904D23183D310D63DAE141DC93EC0BD7598125C65EC7F3C3C437479E3F1A02'
    '102C5505E7BB850E1BB21E1A622D578E11B99D75B101219C26DA6C17F0016E1A'
    '4BA40737B98FDA88C36D047596B84D577C979B16E198DC9DE2959A0555CDB8A8'
    'A94ADDA38DA6F711707E18B58BA6906574D8074E0BB751597375A990325B260E'
    '882F4A258DF99CA02A382796623E3050999D0EAEF623FEE59F3A6B775B3DCAF6'
    '9274B05389A0A99C9D7C7ABA9ECB6341023B2ADDC3435DD814FBF04AF641C5E5'
    'CD81C961BC5103D80A678F478108BA0D9031BBAC107DDCE257854AF0FE7F374C'
    '3B316FA399A3378A7B3D7AC34A425F0834EBFEA34374D7A993C268D32C97377D'
)
for ($i=0; $i -lt $tools.Count; $i++) { if ($toolManifest[$i].sha256 -ne $expectedTools[$i]) { throw ('Wrong pinned build tool: '+$tools[$i]) } }
$outDir=Join-Path $destinationPath 'out'
$intDir=Join-Path $destinationPath 'int'
& $msbuild $projectPath '/t:Build' '/p:Configuration=Release' '/p:Platform=Win32' '/p:PlatformToolset=v143' '/p:VCToolsVersion=14.41.34120' '/p:WindowsTargetPlatformVersion=10.0.22621.0' '/p:PreferredToolArchitecture=x64' '/p:ImportDirectoryBuildProps=false' '/p:ImportDirectoryBuildTargets=false' "/p:UserRootDir=$destinationPath\unused-user-props\" "/p:OutDir=$outDir\\" "/p:IntDir=$intDir\\" '/p:LanguageStandard_C=stdc11' '/m:2' '/v:minimal' '/nologo' *> (Join-Path $destinationPath 'build.log')
$buildExit=$LASTEXITCODE
if ($buildExit -ne 0) { throw ('Build failed; see build.log. Exit '+$buildExit) }
$dllOutput=Join-Path $outDir 'ddraw.dll'
if ((Get-Item -LiteralPath $dllOutput).Length -ne 424448 -or
    (Get-FileHash -LiteralPath $dllOutput -Algorithm SHA256).Hash -ne '0F5F094DD1AB1BC23CFDC1FE4C1ED51D91C1C9A5FAFD063C9897FA018606BE91') {
    throw 'Build output does not match the pinned component; no usable bundle was produced'
}
$manifest=[ordered]@{
    upstreamCommit='541b5de218ec3fbd6ea91e606ebfadc07c1786b0'
    sourceArchiveSha256=$expectedArchive
    originalSources=$originalManifest
    tools=$toolManifest
    authoredMaximizeSha256=(Get-FileHash -LiteralPath $MaximizeSource -Algorithm SHA256).Hash
    buildScriptSha256=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
    patchedSources=@('src\dllmain.c','src\dd.c','src\utils.c','cnc-ddraw.vcxproj','inc\git.h' | ForEach-Object { [ordered]@{path=$_;sha256=(Get-FileHash -LiteralPath (Join-Path $sourceRoot $_) -Algorithm SHA256).Hash} })
    dllSha256=(Get-FileHash -LiteralPath $dllOutput -Algorithm SHA256).Hash
    dllLength=(Get-Item -LiteralPath $dllOutput).Length
    buildExitCode=$buildExit
    executedDll=$false
}
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $destinationPath 'build-receipt.json') -Encoding utf8
$manifest.dllSha256
