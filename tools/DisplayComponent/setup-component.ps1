# Explicit local setup only. The launcher never calls this script during Play.
# Downloaded sources, notices, DLLs and receipts are local outputs, not repository source.
param(
    [Parameter(Mandatory=$true)][string]$WorkDirectory,
    [Parameter(Mandatory=$true)][string]$Destination,
    [string]$InputCache,
    [switch]$DownloadMissing,
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
function Assert-Pin([string]$Path, $Pin) {
    Assert-NoLinks $Path
    $item=Get-Item -LiteralPath $Path -Force
    if ($item.PSIsContainer -or $item.Length -ne $Pin.length -or
        (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ine $Pin.sha256) {
        throw ('Pinned input mismatch: '+$Pin.name)
    }
}
function Read-FirstComment([string]$Path) {
    $text=[IO.File]::ReadAllText($Path).Replace("`r`n","`n")
    $match=[regex]::Match($text,'(?s)/\*(.*?)\*/')
    if (-not $match.Success) { throw 'Expected source notice is absent' }
    return $match.Groups[1].Value.Trim()+"`r`n"
}
function Save-HttpsInput($Pin, [string]$Output) {
    $uri=[Uri]$Pin.url
    if ($uri.Scheme -ne 'https') { throw 'Only HTTPS inputs are allowed' }
    $handler=[Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect=$false
    $client=[Net.Http.HttpClient]::new($handler)
    $client.Timeout=[TimeSpan]::FromSeconds(120)
    $client.DefaultRequestHeaders.UserAgent.ParseAdd('Power-of-Chaos-Launcher-component-setup/1')
    $response=$null
    try {
        for ($redirect=0; $redirect -le 5; $redirect++) {
            $response=$client.GetAsync($uri,[Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
            if ([int]$response.StatusCode -notin @(301,302,303,307,308)) { break }
            if ($redirect -eq 5 -or -not $response.Headers.Location) { throw 'Too many or invalid input redirects' }
            $uri=[Uri]::new($uri,$response.Headers.Location)
            if ($uri.Scheme -ne 'https') { throw 'Refusing non-HTTPS redirect' }
            $response.Dispose(); $response=$null
        }
        $response.EnsureSuccessStatusCode() | Out-Null
        $limit=if ($Pin.kind -eq 'git-blob') { 16384L } else { [long]$Pin.length }
        if ($response.Content.Headers.ContentLength -gt $limit) { throw 'Download exceeds pinned size bound' }
        $stream=$response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        $memory=[IO.MemoryStream]::new()
        try {
            $buffer=[byte[]]::new(8192)
            $total=0L
            while (($count=$stream.Read($buffer,0,$buffer.Length)) -gt 0) {
                $total+=$count
                if ($total -gt $limit) { throw 'Download exceeds pinned size bound' }
                $memory.Write($buffer,0,$count)
            }
            $bytes=$memory.ToArray()
        } finally { $memory.Dispose(); $stream.Dispose() }
        if ($Pin.kind -eq 'git-blob') {
            $blob=[Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json
            if ($blob.encoding -ne 'base64' -or $blob.size -ne $Pin.length) { throw 'Unexpected license blob' }
            $bytes=[Convert]::FromBase64String($blob.content)
        }
        [IO.File]::WriteAllBytes($Output,$bytes)
        Assert-Pin $Output $Pin
    } finally {
        if ($response) { $response.Dispose() }
        $client.Dispose(); $handler.Dispose()
    }
}

$work=[IO.Path]::GetFullPath($WorkDirectory)
$destinationPath=[IO.Path]::GetFullPath($Destination)
Assert-NoLinks $work
Assert-NoLinks $destinationPath
if ($InputCache) { Assert-NoLinks $InputCache; $InputCache=[IO.Path]::GetFullPath($InputCache) }
if (Test-Path -LiteralPath $work) { throw 'WorkDirectory must be new; preserve previous receipts' }
if (Test-Path -LiteralPath $destinationPath) { throw 'Destination must be new; no component is overwritten' }
if (-not (Test-Path -LiteralPath ([IO.Path]::GetDirectoryName($destinationPath)) -PathType Container)) { throw 'Destination parent must exist' }
$pins=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'component-pins.json') -Raw | ConvertFrom-Json
foreach ($pin in @($pins.inputs)+@($pins.components)+@($pins.notices)) {
    if ($pin.name -notmatch '^[A-Za-z0-9][A-Za-z0-9_.-]*$' -or $pin.length -lt 1 -or $pin.length -gt 50000000 -or $pin.sha256 -notmatch '^[a-f0-9]{64}$') {
        throw 'Invalid component pin metadata'
    }
}
New-Item -ItemType Directory -Path $work | Out-Null
$inputDirectory=Join-Path $work 'inputs'
$bundle=Join-Path $work 'bundle'
New-Item -ItemType Directory -Path $inputDirectory,$bundle | Out-Null
foreach ($inputPin in $pins.inputs) {
    $output=Join-Path $inputDirectory $inputPin.name
    $cached=if ($InputCache) { Join-Path $InputCache $inputPin.name } else { $null }
    if ($cached -and (Test-Path -LiteralPath $cached)) {
        Assert-Pin $cached $inputPin
        Copy-Item -LiteralPath $cached -Destination $output
    } elseif ($DownloadMissing) {
        Save-HttpsInput $inputPin $output
    } else {
        throw ('Missing offline input '+$inputPin.name+'. Supply InputCache or explicitly use -DownloadMissing.')
    }
    Assert-Pin $output $inputPin
}
$buildDirectory=Join-Path $work 'build'
& (Join-Path $PSScriptRoot 'build-profile.ps1') -SourceArchive (Join-Path $inputDirectory 'source-v7.1.0.0.zip') -Destination $buildDirectory -VisualStudioRoot $VisualStudioRoot
if ($LASTEXITCODE -ne 0) { throw 'Component build failed' }
$source=Join-Path $buildDirectory 'source\cnc-ddraw-7.1.0.0'
Copy-Item -LiteralPath (Join-Path $buildDirectory 'out\ddraw.dll') -Destination (Join-Path $bundle 'ddraw.dll')

# Read only the known proxy member; no proxy build/configuration program is executed.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive=[IO.Compression.ZipFile]::OpenRead((Join-Path $inputDirectory 'proxy-dlls.zip'))
try {
    if ($archive.Entries.Count -gt 500) { throw 'Proxy archive has too many entries' }
    $members=@($archive.Entries | Where-Object { $_.FullName -ceq 'Universal proxy for any game/winmm.dll' })
    if ($members.Count -ne 1 -or $members[0].Length -ne 98304) { throw 'Unexpected proxy member' }
    $inputStream=$members[0].Open()
    $outputStream=[IO.File]::Open((Join-Path $bundle 'winmm.dll'),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try { $inputStream.CopyTo($outputStream) } finally { $outputStream.Dispose(); $inputStream.Dispose() }
} finally { $archive.Dispose() }

foreach ($notice in $pins.notices) {
    $parts=$notice.source.Split(':',2)
    $base=if ($parts[0] -eq 'source') { $source } elseif ($parts[0] -eq 'input') { $inputDirectory } else { throw 'Unknown notice origin' }
    $path=Join-Path $base $parts[1]
    $output=Join-Path $bundle $notice.name
    switch ($notice.transform) {
        'copy' { Copy-Item -LiteralPath $path -Destination $output }
        'first-comment' { [IO.File]::WriteAllText($output,(Read-FirstComment $path),[Text.UTF8Encoding]::new($false)) }
        'lf-final-newline' { [IO.File]::WriteAllText($output,([IO.File]::ReadAllText($path).Replace("`r`n","`n").TrimEnd()+"`n"),[Text.UTF8Encoding]::new($false)) }
        'khronos-comments' {
            $blocks=@('inc/glcorearb.h','inc/KHR/khrplatform.h','inc/wglext.h') | ForEach-Object { (Read-FirstComment (Join-Path $source $_)).TrimEnd()+"`n" }
            [IO.File]::WriteAllText($output,($blocks -join "`n"),[Text.UTF8Encoding]::new($false))
        }
        default { throw 'Unknown notice transform' }
    }
    Assert-Pin $output $notice
}
foreach ($component in $pins.components) { Assert-Pin (Join-Path $bundle $component.name) $component }
$receipt=[ordered]@{
    profile=$pins.profile
    upstreamCommit=$pins.upstreamCommit
    pinsSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'component-pins.json') -Algorithm SHA256).Hash.ToLowerInvariant()
    scriptSha256=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant()
    inputs=$pins.inputs
    files=@($pins.components)+@($pins.notices)
    buildReceiptSha256=(Get-FileHash -LiteralPath (Join-Path $buildDirectory 'build-receipt.json') -Algorithm SHA256).Hash.ToLowerInvariant()
    downloadedMissing=[bool]$DownloadMissing
    executedComponent=$false
}
[IO.File]::WriteAllText((Join-Path $work 'setup-receipt.json'),($receipt | ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
# Same-volume rename publishes only a completely verified bundle. Failure preserves evidence.
[IO.Directory]::Move($bundle,$destinationPath)
Write-Output ('Verified local component: '+$destinationPath)
