param([Parameter(Mandatory=$true)][string]$Destination,[string]$LocalHdComponentsArchive)
# -LocalHdComponentsArchive: the maintainer's local copy of the pinned HD component archive, for a candidate
# built before that archive is public. It must be the pinned file itself; only its download is skipped.
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if($PSVersionTable.PSVersion.Major -lt 7){throw 'PowerShell 7 or later is required.'}
. (Join-Path $PSScriptRoot 'Package.Common.ps1')
$target=Get-PackageFullPath $Destination
Assert-PackageOrdinaryPath $target
if(Test-Path -LiteralPath $target){throw 'Release input destination must be new.'}
$repository=Get-PackageFullPath (Join-Path $PSScriptRoot '../..')
Assert-PackageDisjoint $target $repository
$settings=Read-PackageSettings (Join-Path $PSScriptRoot 'release-settings.json')
$pins=Read-PackageJson (Join-Path $repository 'tools/DisplayComponent/component-pins.json')
$hdPins=Read-PackageJson (Join-Path $repository 'tools/HdCards/component-pins.json')
$hdRequired=@(Get-PackageHdComponentPins $hdPins $pins)
if($settings.hdComponentsProfile -cne $hdPins.profile){throw 'Release settings name another HD component profile.'}
$hdBundlePin=$settings.hdComponentBundle
if($hdBundlePin.name -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,127}\.zip$' -or -not ([Uri]$hdBundlePin.url).AbsolutePath.EndsWith('/'+$hdBundlePin.name,[StringComparison]::Ordinal)){throw 'Invalid HD component archive pin.'}
$localHdBundle=$null
if(-not [string]::IsNullOrWhiteSpace($LocalHdComponentsArchive)){
    $localHdBundle=Get-PackageFullPath $LocalHdComponentsArchive
    Assert-PackageOrdinaryPath $localHdBundle
    Assert-PackageDisjoint $localHdBundle $target
    if([IO.Path]::GetFileName($localHdBundle) -cne $hdBundlePin.name -or (Get-Item -LiteralPath $localHdBundle).Length -ne $hdBundlePin.length -or (Get-PackageFileHash $localHdBundle) -cne $hdBundlePin.sha256){throw 'The local HD component archive is not the pinned archive.'}
}
[void](New-Item -ItemType Directory -Path $target)
$cache=Join-Path $target 'download-cache'
$notices=Join-Path $target 'notices'
$components=Join-Path $target 'original-aspect-ratio-v1'
$hdComponents=Join-Path $target 'hd-card-artwork-v2'
foreach($directory in @($cache,$notices,$components,$hdComponents)){[void](New-Item -ItemType Directory -Path $directory)}
function Get-PinnedDownload([string]$Url,[string]$ExpectedHash,[long]$ExpectedLength=0){
    $uri=[Uri]$Url
    if($uri.Scheme -cne 'https' -or $uri.UserInfo -or $uri.Host -notin @('github.com','raw.githubusercontent.com','api.nuget.org','builds.dotnet.microsoft.com','api.github.com')){throw 'Unapproved release input origin.'}
    if($ExpectedHash -cnotmatch '^[0-9a-f]{64}$'){throw 'Invalid release download identity.'}
    $file=Join-Path $cache $ExpectedHash
    if(-not(Test-Path -LiteralPath $file)){
        $client=[Net.Http.HttpClient]::new()
        $client.Timeout=[TimeSpan]::FromMinutes(3)
        try{
            $response=$client.GetAsync($uri,[Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
            [void]$response.EnsureSuccessStatusCode()
            $source=$response.Content.ReadAsStream()
            $output=[IO.FileStream]::new($file,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
            try{
                $buffer=[byte[]]::new(65536);$total=0L
                while(($read=$source.Read($buffer,0,$buffer.Length)) -gt 0){
                    $total+=$read
                    if($total -gt 134217728 -or ($ExpectedLength -gt 0 -and $total -gt $ExpectedLength)){throw 'Release download exceeds its bound.'}
                    $output.Write($buffer,0,$read)
                }
                $output.Flush($true)
            }finally{$output.Dispose();$source.Dispose();$response.Dispose()}
        }finally{$client.Dispose()}
    }
    if((Get-PackageFileHash $file) -cne $ExpectedHash -or ($ExpectedLength -gt 0 -and (Get-Item -LiteralPath $file).Length -ne $ExpectedLength)){throw 'Release download identity mismatch.'}
    return $file
}
foreach($pin in $settings.notices){
    Assert-PackageRelativeName $pin.name
    if($pin.name.Contains('/')){throw 'Notice names must be flat.'}
    $destinationFile=Join-Path $notices $pin.name
    if($pin.ContainsKey('repositoryPath')){
        Assert-PackageRelativeName $pin.repositoryPath
        $source=Join-Path $repository $pin.repositoryPath
        Assert-PackageOrdinaryPath $source
        Copy-Item -LiteralPath $source -Destination $destinationFile
    }elseif($pin.ContainsKey('archiveEntry')){
        $archive=Get-PinnedDownload $pin.url $pin.archiveSha256
        $zip=[IO.Compression.ZipFile]::OpenRead($archive)
        try{
            $entries=@($zip.Entries | Where-Object {$_.FullName -ceq $pin.archiveEntry})
            if($entries.Count -ne 1 -or $entries[0].Length -ne $pin.length){throw 'Pinned notice archive entry is missing or ambiguous.'}
            $input=$entries[0].Open();$output=[IO.File]::Open($destinationFile,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
            try{
                $buffer=[byte[]]::new(65536);$total=0L
                while(($read=$input.Read($buffer,0,$buffer.Length)) -gt 0){$total+=$read;if($total -gt $pin.length){throw 'Notice expansion exceeds its pin.'};$output.Write($buffer,0,$read)}
            }finally{$output.Dispose();$input.Dispose()}
        }finally{$zip.Dispose()}
    }else{
        $source=Get-PinnedDownload $pin.url $pin.sha256 $pin.length
        Copy-Item -LiteralPath $source -Destination $destinationFile
    }
}
Assert-PackagePins $notices @($settings.notices)
Assert-PackageCorrespondingSources $notices @($settings.notices)
$bundle=Get-PinnedDownload $settings.componentBundle.url $settings.componentBundle.sha256 $settings.componentBundle.length
Expand-PackagePinnedBundle $bundle @(@($pins.components)+@($pins.notices)) $components
$hdBundle=if($null -eq $localHdBundle){Get-PinnedDownload $hdBundlePin.url $hdBundlePin.sha256 $hdBundlePin.length}else{$localHdBundle}
Expand-PackagePinnedBundle $hdBundle $hdRequired $hdComponents
[ordered]@{schemaVersion=1;displayComponentsDirectory=$components;hdComponentsDirectory=$hdComponents;noticesDirectory=$notices;downloadedInputsVerified=$true;hdComponentArchiveDownloaded=($null -eq $localHdBundle)} | ConvertTo-Json
