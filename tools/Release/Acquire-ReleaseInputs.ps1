param([Parameter(Mandatory=$true)][string]$Destination)
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
[void](New-Item -ItemType Directory -Path $target)
$cache=Join-Path $target 'download-cache'
$notices=Join-Path $target 'notices'
$components=Join-Path $target 'original-aspect-ratio-v1'
foreach($directory in @($cache,$notices,$components)){[void](New-Item -ItemType Directory -Path $directory)}
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
$zip=[IO.Compression.ZipFile]::OpenRead($bundle)
try{
    $required=@(@($pins.components)+@($pins.notices))
    if($zip.Entries.Count -ne $required.Count){throw 'Unexpected component archive entries.'}
    $names=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($entry in $zip.Entries){
        Assert-PackageRelativeName $entry.FullName
        $pin=@($required | Where-Object {$_.name -ceq $entry.FullName})
        if($pin.Count -ne 1 -or -not $names.Add($entry.FullName) -or $entry.Length -ne $pin[0].length -or (($entry.ExternalAttributes -shr 16) -band 0xf000) -notin @(0,0x8000)){throw 'Unexpected component archive member.'}
        $input=$entry.Open();$output=[IO.File]::Open((Join-Path $components $entry.FullName),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try{$input.CopyTo($output)}finally{$output.Dispose();$input.Dispose()}
    }
}finally{$zip.Dispose()}
Assert-PackagePins $components @(@($pins.components)+@($pins.notices))
[ordered]@{schemaVersion=1;displayComponentsDirectory=$components;noticesDirectory=$notices;downloadedInputsVerified=$true} | ConvertTo-Json
