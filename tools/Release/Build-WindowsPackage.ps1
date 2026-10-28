param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][ValidateSet('win-x64', 'win-x86', 'win-arm64')][string]$RuntimeIdentifier,
    [Parameter(Mandatory = $true)][string]$SourceCommit,
    [Parameter(Mandatory = $true)][string]$DisplayComponentsDirectory,
    [Parameter(Mandatory = $true)][string]$NoticesDirectory,
    [Parameter(Mandatory = $true)][string]$PackageOutputDirectory,
    [string]$SettingsPath = (Join-Path $PSScriptRoot 'release-settings.json'),
    [string]$DotNetPath = 'dotnet',
    [string]$NuGetPackagesDirectory,
    [string]$SourceManifestPath,
    [switch]$NoRestore
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 or later is required.' }
. (Join-Path $PSScriptRoot 'Package.Common.ps1')
Assert-PackageVersion $Version
if ($SourceCommit -cnotmatch '^[a-f0-9]{40}$') { throw 'An exact source commit is required.' }
$repository = Get-PackageFullPath (Join-Path $PSScriptRoot '../..')
$output = Get-PackageFullPath $PackageOutputDirectory
$componentInput = Get-PackageFullPath $DisplayComponentsDirectory
$noticeInput = Get-PackageFullPath $NoticesDirectory
foreach ($sourceInput in @($componentInput, $noticeInput)) { Assert-PackageDisjoint $sourceInput $output; Assert-PackageOrdinaryPath $sourceInput }
Assert-PackageOrdinaryPath $output
if (Test-Path -LiteralPath $output) { throw 'Package output directory must be new.' }
if ($output.Equals($repository, [StringComparison]::OrdinalIgnoreCase)) { throw 'The source checkout cannot be the package output.' }
$settings = Read-PackageSettings $SettingsPath
if ((Get-PackageFullPath $SettingsPath) -ne (Get-PackageFullPath (Join-Path $PSScriptRoot 'release-settings.json'))) {
    throw 'Release settings must be the reviewed source settings beside this tool.'
}
if ($RuntimeIdentifier -cnotin $settings.supportedRuntimeIdentifiers) { throw 'The runtime identifier is not admitted by the release settings.' }
$pinsPath = Join-Path $repository 'tools/DisplayComponent/component-pins.json'
$pins = Read-PackageJson $pinsPath 131072
Assert-PackagePins $componentInput @(@($pins.components) + @($pins.notices))
Assert-PackagePins $noticeInput @($settings.notices)
Assert-PackageCorrespondingSources $noticeInput @($settings.notices)
if ([string]::IsNullOrWhiteSpace($SourceManifestPath)) {
    $actualRoot = (& git -C $repository rev-parse --show-toplevel)
    if ($LASTEXITCODE -ne 0 -or (Get-PackageFullPath $actualRoot) -ne $repository) { throw 'A clean launcher Git checkout is required.' }
    $actualHead = (& git -C $repository rev-parse HEAD)
    if ($LASTEXITCODE -ne 0 -or $actualHead.Trim() -cne $SourceCommit) { throw 'Source commit does not match the checkout.' }
    $dirty = @(& git -C $repository status --porcelain --untracked-files=all)
    if ($LASTEXITCODE -ne 0 -or $dirty.Count -ne 0) { throw 'Release source must be clean and committed.' }
} else {
    $sourceManifest = Read-PackageJson $SourceManifestPath 4194304
    if ($sourceManifest.sourceCommit -cne $SourceCommit -or $sourceManifest.files.Count -lt 15 -or $sourceManifest.files.Count -gt 1000) {
        throw 'The committed source-export manifest is invalid.'
    }
    $sourceNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $sourceRepository = Get-PackageFullPath $sourceManifest.sourceRepositoryDirectory
    Assert-PackageOrdinaryPath $sourceRepository
    $knownCommit = (& git -C $sourceRepository rev-parse --verify ($SourceCommit + '^{commit}'))
    if ($LASTEXITCODE -ne 0 -or $knownCommit.Trim() -cne $SourceCommit) { throw 'The export source commit cannot be verified in its Git object store.' }
    $gitPaths = @(& git -C $sourceRepository -c core.quotepath=false ls-tree -r --full-tree --name-only $SourceCommit)
    if ($LASTEXITCODE -ne 0 -or $gitPaths.Count -ne $sourceManifest.files.Count) { throw 'Export file count differs from the source commit.' }
    foreach ($entry in $sourceManifest.files) {
        Assert-PackageRelativeName $entry.path
        if (-not $sourceNames.Add($entry.path) -or $entry.sha256 -cnotmatch '^[a-f0-9]{64}$') { throw 'Invalid source-export entry.' }
        $path = Join-Path $repository $entry.path
        if ((Get-PackageFileHash $path) -cne $entry.sha256 -or (Get-Item -LiteralPath $path).Length -ne $entry.length) { throw 'Committed source-export identity differs.' }
        if ($entry.path -cnotin $gitPaths) { throw 'Exported path is absent from the exact source commit.' }
        $gitBlob = (& git -C $sourceRepository rev-parse ($SourceCommit + ':' + $entry.path))
        if ($LASTEXITCODE -ne 0 -or $gitBlob -cnotmatch '^[a-f0-9]{40}$') { throw 'Source commit entry is not a known Git blob.' }
        $exportBlob = (& git -C $sourceRepository hash-object --no-filters -- $path)
        if ($LASTEXITCODE -ne 0 -or $exportBlob.Trim() -cne $gitBlob.Trim()) { throw 'Exported bytes differ from the exact committed Git blob.' }
    }
    # A supplied manifest must describe this exact export, not omit injected files.
    foreach ($entry in Get-PackageFiles $repository) {
        if (-not $sourceNames.Contains($entry.path)) { throw 'Unexpected file in the committed source export.' }
    }
}

[void][IO.Directory]::CreateDirectory($output)
$work = Join-Path $output 'work'
[void][IO.Directory]::CreateDirectory($work)
$publish = Join-Path $work 'publish'
$dotnet = if ([IO.Path]::IsPathRooted($DotNetPath)) { (Get-Item -LiteralPath $DotNetPath).FullName } else { (Get-Command $DotNetPath -CommandType Application).Source }
Assert-PackageOrdinaryPath $dotnet
$originalEnvironment = @{}
$environmentValues = @{
    DOTNET_CLI_HOME = (Join-Path $work 'dotnet-home'); DOTNET_SKIP_FIRST_TIME_EXPERIENCE = 'true';
    DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'; DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false';
    DOTNET_CLI_TELEMETRY_OPTOUT = 'true'; DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'; DOTNET_NOLOGO = 'true';
    MSBuildEnableWorkloadResolver = 'false'; PATH = ([IO.Path]::GetDirectoryName($dotnet) + [IO.Path]::PathSeparator + $env:PATH)
}
if (-not [string]::IsNullOrWhiteSpace($NuGetPackagesDirectory)) { $environmentValues.NUGET_PACKAGES = Get-PackageFullPath $NuGetPackagesDirectory }
foreach ($key in $environmentValues.Keys) { $originalEnvironment[$key] = [Environment]::GetEnvironmentVariable($key); [Environment]::SetEnvironmentVariable($key, $environmentValues[$key]) }
Push-Location -LiteralPath $repository
try {
    $sdk = (& $dotnet --version)
    if ($LASTEXITCODE -ne 0 -or $sdk.Trim() -cne $settings.sdkVersion) { throw 'The package SDK differs from its pin.' }
    $arguments = @('publish', 'src/PowerOfChaos.Launcher/PowerOfChaos.Launcher.csproj', '-c', 'Release', '-r', $RuntimeIdentifier,
        '--self-contained', 'true', '-m:1', '-nodeReuse:false', '-p:UseSharedCompilation=false', '-p:PublishSingleFile=false',
        '-p:PublishTrimmed=false', '-p:DebugSymbols=false', '-p:DebugType=None', ('-p:Version=' + $Version),
        ('-p:PackageVersion=' + $Version), ('-p:InformationalVersion=' + $Version + '+' + $SourceCommit),
        '-p:IncludeSourceRevisionInInformationalVersion=false', ('-p:RepositoryCommit=' + $SourceCommit),
        ('-p:SourceRevisionId=' + $SourceCommit), ('-p:RuntimeFrameworkVersion=' + $settings.runtimeVersion),
        ('-p:TargetFramework=' + $settings.targetFramework), '-o', $publish)
    if ($NoRestore) { $arguments += '--no-restore' }
    else { $arguments += '-p:RestoreLockedMode=true' }
    # A normal source prefix can be normalized without introducing MSBuild or Roslyn map delimiters.
    # Unusual developer paths remain usable; the package's no-debug-symbol policy still applies there.
    if ($repository -notmatch '[,;=%"$]') { $arguments += ('-p:PathMap=' + $repository + '=/_/src') }
    & $dotnet @arguments *> (Join-Path $output 'publish.log')
    if ($LASTEXITCODE -ne 0) { throw ('Self-contained publish failed; inspect ' + (Join-Path $output 'publish.log')) }
} finally {
    Pop-Location
    foreach ($key in $originalEnvironment.Keys) { [Environment]::SetEnvironmentVariable($key, $originalEnvironment[$key]) }
}

if ([string]::IsNullOrWhiteSpace($SourceManifestPath)) {
    $actualHead = (& git -C $repository rev-parse HEAD)
    $dirty = @(& git -C $repository status --porcelain --untracked-files=all)
    if ($LASTEXITCODE -ne 0 -or $actualHead.Trim() -cne $SourceCommit -or $dirty.Count -ne 0) { throw 'Committed source changed during publish.' }
} else {
    foreach ($entry in $sourceManifest.files) {
        $path = Join-Path $repository $entry.path
        if ((Get-PackageFileHash $path) -cne $entry.sha256 -or (Get-Item -LiteralPath $path).Length -ne $entry.length) { throw 'Source export changed during publish.' }
    }
    foreach ($entry in Get-PackageFiles $repository) {
        if (-not $sourceNames.Contains($entry.path) -and @($entry.path.Split('/') | Where-Object { $_ -in @('bin', 'obj') }).Count -eq 0) {
            throw 'Unexpected source file appeared during publish.'
        }
    }
}

$runtimeConfig = Read-PackageJson (Join-Path $publish 'PowerOfChaos.Launcher.runtimeconfig.json')
if ($runtimeConfig.runtimeOptions.tfm -cne $settings.targetFramework -or $runtimeConfig.runtimeOptions.ContainsKey('framework') -or
    $runtimeConfig.runtimeOptions.ContainsKey('frameworks') -or -not $runtimeConfig.runtimeOptions.ContainsKey('includedFrameworks')) {
    throw 'Publish output is not a self-contained runtime.'
}
$framework = @($runtimeConfig.runtimeOptions.includedFrameworks | Where-Object { $_.name -ceq 'Microsoft.NETCore.App' })
if ($framework.Count -ne 1 -or $framework[0].version -cne $settings.runtimeVersion) { throw 'Published runtime version differs from its pin.' }
$componentTarget = Join-Path $publish 'Components/original-aspect-ratio-v1'
[void][IO.Directory]::CreateDirectory($componentTarget)
foreach ($pin in @(@($pins.components) + @($pins.notices))) { Copy-Item -LiteralPath (Join-Path $componentInput $pin.name) -Destination (Join-Path $componentTarget $pin.name) }
$noticeTarget = Join-Path $publish 'Notices'
[void][IO.Directory]::CreateDirectory($noticeTarget)
foreach ($pin in $settings.notices) { Copy-Item -LiteralPath (Join-Path $noticeInput $pin.name) -Destination (Join-Path $noticeTarget $pin.name) }
Copy-Item -LiteralPath $pinsPath -Destination (Join-Path $publish 'component-pins.json')
Copy-Item -LiteralPath $SettingsPath -Destination (Join-Path $publish 'release-settings.json')
$readme = @"
Power of Chaos Launcher $Version ($RuntimeIdentifier)

Extract the entire ZIP into a new folder and open PowerOfChaos.Launcher.exe.
The .NET runtime and required display-mod components are included. No separate .NET installation is needed.
In Settings, choose Add Game path and select your installed game folder or a parent folder containing all three games.
The launcher reads card counts, original card artwork and names from your own games. No game files or saves are included.
Supported executable versions are listed in the project's mod documentation; discovery alone does not establish mod compatibility.
Temporary all cards requires a compatible existing shared save; close all games before switching that mod.
Keep every extracted file together. The Notices directory and component notices belong with the program.

Source: $($settings.sourceRepository)
Commit: $SourceCommit
"@
[IO.File]::WriteAllText((Join-Path $publish 'README.txt'), $readme.Replace("`r`n", "`n") + "`n", [Text.UTF8Encoding]::new($false))
$deps = Read-PackageJson (Join-Path $publish 'PowerOfChaos.Launcher.deps.json')
$dependencies = @(Get-PackageOrdinalNames $deps.libraries.Keys | ForEach-Object {
    $separator = $_.LastIndexOf('/')
    if ($separator -le 0) { throw 'Invalid published dependency identity.' }
    [ordered]@{ id = $_.Substring(0, $separator); version = $_.Substring($separator + 1); type = $deps.libraries[$_].type }
})
$files = @(Get-PackageFiles $publish)
foreach ($file in $files) { Assert-PackagePayloadName $file.path }
Assert-PackageDependencyFiles $files $deps
$manifest = [ordered]@{
    schemaVersion = 1; name = 'Power of Chaos Launcher'; version = $Version; sourceCommit = $SourceCommit;
    runtimeIdentifier = $RuntimeIdentifier; targetFramework = $settings.targetFramework; runtimeVersion = $settings.runtimeVersion;
    selfContained = $true; sourceRepository = $settings.sourceRepository; componentsProfile = $pins.profile;
    files = $files; dependencies = $dependencies
}
Write-PackageJson (Join-Path $publish 'package-manifest.json') $manifest
$name = 'PowerOfChaos-Launcher-' + $Version + '-' + $RuntimeIdentifier
$archivePath = Join-Path $output ($name + '.zip')
$zip = [IO.Compression.ZipFile]::Open($archivePath, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in Get-PackageFiles $publish) {
        $entry = $zip.CreateEntry($file.path, [IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
        $entry.ExternalAttributes = 0
        $sourceStream = [IO.File]::OpenRead((Join-Path $publish $file.path))
        $stream = $entry.Open()
        try { $sourceStream.CopyTo($stream) } finally { $stream.Dispose(); $sourceStream.Dispose() }
    }
} finally { $zip.Dispose() }
$manifestOutput = Join-Path $output ($name + '.manifest.json')
Copy-Item -LiteralPath (Join-Path $publish 'package-manifest.json') -Destination $manifestOutput
$checksums = (Get-PackageFileHash $archivePath) + '  ' + [IO.Path]::GetFileName($archivePath) + "`n" +
    (Get-PackageFileHash $manifestOutput) + '  ' + [IO.Path]::GetFileName($manifestOutput) + "`n"
[IO.File]::WriteAllText((Join-Path $output ($name + '.SHA256SUMS')), $checksums, [Text.UTF8Encoding]::new($false))
& (Join-Path $PSScriptRoot 'Test-WindowsPackage.ps1') -PackagePath $archivePath -ValidationDirectory (Join-Path $work 'structure-validation') `
    -ExpectedVersion $Version -ExpectedSourceCommit $SourceCommit -ExpectedRuntimeIdentifier $RuntimeIdentifier -StructuralOnly | Out-Null
[ordered]@{ packagePath = $archivePath; manifestPath = $manifestOutput; checksumsPath = (Join-Path $output ($name + '.SHA256SUMS'));
    publishedDirectory = $publish; sourceCommit = $SourceCommit; version = $Version; runtimeIdentifier = $RuntimeIdentifier; files = $files.Count + 1 } | ConvertTo-Json
