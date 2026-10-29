param(
    [Parameter(Mandatory = $true)][string]$PackagePath,
    [Parameter(Mandatory = $true)][string]$ValidationDirectory,
    [Parameter(Mandatory = $true)][string]$ExpectedVersion,
    [Parameter(Mandatory = $true)][string]$ExpectedSourceCommit,
    [Parameter(Mandatory = $true)][ValidateSet('win-x64', 'win-x86', 'win-arm64')][string]$ExpectedRuntimeIdentifier,
    [switch]$StructuralOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 or later is required.' }
. (Join-Path $PSScriptRoot 'Package.Common.ps1')
Assert-PackageVersion $ExpectedVersion
if ($ExpectedSourceCommit -cnotmatch '^[a-f0-9]{40}$') { throw 'An exact expected source commit is required.' }
$archivePath = Get-PackageFullPath $PackagePath
$validation = Get-PackageFullPath $ValidationDirectory
Assert-PackageDisjoint $archivePath $validation
Assert-PackageOrdinaryPath $validation
if (Test-Path -LiteralPath $validation) { throw 'Validation directory must be new.' }
[void][IO.Directory]::CreateDirectory($validation)
$extracted = Join-Path $validation 'launcher'
Expand-SafeWindowsPackage $archivePath $extracted
$manifest = Read-PackageJson (Join-Path $extracted 'package-manifest.json')
if ($manifest.schemaVersion -ne 1 -or $manifest.name -cne 'Power of Chaos Launcher' -or
    $manifest.version -cne $ExpectedVersion -or $manifest.sourceCommit -cne $ExpectedSourceCommit -or
    $manifest.runtimeIdentifier -cne $ExpectedRuntimeIdentifier -or $manifest.selfContained -ne $true -or
    $manifest.files.Count -lt 15 -or $manifest.files.Count -gt 4999) { throw 'Package identity or manifest shape differs.' }
$settings = Read-PackageSettings (Join-Path $extracted 'release-settings.json')
if ((Get-PackageFileHash (Join-Path $extracted 'release-settings.json')) -cne (Get-PackageFileHash (Join-Path $PSScriptRoot 'release-settings.json'))) {
    throw 'Packaged release settings differ from the reviewed source settings.'
}
if ($manifest.targetFramework -cne $settings.targetFramework -or $manifest.runtimeVersion -cne $settings.runtimeVersion -or
    $manifest.sourceRepository -cne $settings.sourceRepository -or $manifest.runtimeIdentifier -cnotin $settings.supportedRuntimeIdentifiers) {
    throw 'Package settings and manifest disagree.'
}
$pins = Read-PackageJson (Join-Path $extracted 'component-pins.json') 131072
$sourcePinsPath = Join-Path $PSScriptRoot '../DisplayComponent/component-pins.json'
if ((Get-PackageFileHash (Join-Path $extracted 'component-pins.json')) -cne (Get-PackageFileHash $sourcePinsPath)) {
    throw 'Packaged component pins differ from the reviewed source pins.'
}
if ($manifest.componentsProfile -cne $pins.profile) { throw 'Package component profile differs.' }
Assert-PackagePins (Join-Path $extracted 'Components/original-aspect-ratio-v1') @(@($pins.components) + @($pins.notices))
Assert-PackagePins (Join-Path $extracted 'Notices') @($settings.notices)
Assert-PackageCorrespondingSources (Join-Path $extracted 'Notices') @($settings.notices)
$actual = @(Get-PackageFiles $extracted)
if ($actual.Count -ne $manifest.files.Count + 1) { throw 'Package has missing or unlisted files.' }
$expected = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($file in $manifest.files) {
    Assert-PackagePayloadName $file.path
    if ($file.path -ceq 'package-manifest.json' -or $file.sha256 -cnotmatch '^[a-f0-9]{64}$' -or
        [long]$file.length -lt 0 -or [long]$file.length -gt 134217728 -or -not $expected.TryAdd($file.path, $file)) {
        throw 'Invalid or duplicate package file record.'
    }
}
foreach ($file in $actual) {
    Assert-PackagePayloadName $file.path
    if ($file.path -ceq 'package-manifest.json') { continue }
    if (-not $expected.ContainsKey($file.path)) { throw 'Unlisted package file.' }
    $record = $expected[$file.path]
    if ($record.path -cne $file.path -or $record.sha256 -cne $file.sha256 -or $record.length -ne $file.length) { throw 'Packaged file content or case differs from the manifest.' }
}
foreach ($required in @('PowerOfChaos.Launcher.exe', 'PowerOfChaos.Launcher.dll', 'PowerOfChaos.Launcher.Core.dll',
    'PowerOfChaos.Launcher.deps.json', 'PowerOfChaos.Launcher.runtimeconfig.json', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll',
    'System.Private.CoreLib.dll', 'README.txt', 'release-settings.json', 'component-pins.json')) {
    if (-not $expected.ContainsKey($required)) { throw ('Required self-contained file is missing: ' + $required) }
}
$runtimeConfig = Read-PackageJson (Join-Path $extracted 'PowerOfChaos.Launcher.runtimeconfig.json')
if ($runtimeConfig.runtimeOptions.tfm -cne $manifest.targetFramework -or $runtimeConfig.runtimeOptions.ContainsKey('framework') -or
    $runtimeConfig.runtimeOptions.ContainsKey('frameworks') -or -not $runtimeConfig.runtimeOptions.ContainsKey('includedFrameworks')) {
    throw 'The package can resolve a framework outside its own folder.'
}
$framework = @($runtimeConfig.runtimeOptions.includedFrameworks | Where-Object { $_.name -ceq 'Microsoft.NETCore.App' })
if ($framework.Count -ne 1 -or $framework[0].version -cne $manifest.runtimeVersion) { throw 'Packaged runtime version is not the declared self-contained pin.' }
$machine = switch ($ExpectedRuntimeIdentifier) { 'win-x86' { 0x014c } 'win-x64' { 0x8664 } 'win-arm64' { 0xaa64 } }
foreach ($native in @('PowerOfChaos.Launcher.exe', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll')) {
    if ((Get-PackagePeMachine (Join-Path $extracted $native)) -ne $machine) { throw 'Native package architecture differs.' }
}
foreach ($native in @('ddraw.dll', 'winmm.dll')) {
    if ((Get-PackagePeMachine (Join-Path $extracted ('Components/original-aspect-ratio-v1/' + $native))) -ne 0x014c) {
        throw 'The game display component must remain x86 independently of launcher architecture.'
    }
}
$deps = Read-PackageJson (Join-Path $extracted 'PowerOfChaos.Launcher.deps.json')
Assert-PackageDependencyFiles $actual $deps
$actualDependencyNames = @(Get-PackageOrdinalNames $deps.libraries.Keys)
$expectedDependencyNames = @(Get-PackageOrdinalNames @($manifest.dependencies | ForEach-Object { $_.id + '/' + $_.version }))
if ($actualDependencyNames.Count -ne $expectedDependencyNames.Count -or
    @($actualDependencyNames | Where-Object { $_ -cnotin $expectedDependencyNames }).Count -ne 0) { throw 'Packaged dependency metadata differs.' }
foreach ($dependency in $manifest.dependencies) {
    if ($dependency.type -cne $deps.libraries[$dependency.id + '/' + $dependency.version].type) { throw 'Packaged dependency classification differs.' }
}

$smokeExecuted = $false
if (-not $StructuralOnly) {
    if (-not $IsWindows) { throw 'Executable package validation requires Windows; structural-only validation is not a smoke-test pass.' }
    $architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
    if (($ExpectedRuntimeIdentifier -eq 'win-arm64' -and $architecture -ne 'Arm64') -or
        ($ExpectedRuntimeIdentifier -eq 'win-x64' -and $architecture -notin @('X64', 'Arm64'))) { throw 'This host cannot run the package architecture.' }
    $reportPath = Join-Path $validation 'package-check.json'
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = Join-Path $extracted 'PowerOfChaos.Launcher.exe'
    $start.WorkingDirectory = $extracted
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.ArgumentList.Add('--package-check')
    $start.ArgumentList.Add($reportPath)
    foreach ($key in @('DOTNET_ADDITIONAL_DEPS', 'DOTNET_SHARED_STORE', 'DOTNET_STARTUP_HOOKS', 'DOTNET_HOST_PATH')) { [void]$start.Environment.Remove($key) }
    $absent = Join-Path $validation 'absent-global-dotnet'
    foreach ($key in @('DOTNET_ROOT', 'DOTNET_ROOT_X64', 'DOTNET_ROOT_X86', 'DOTNET_ROOT_ARM64', 'DOTNET_ROOT(x86)')) { $start.Environment[$key] = $absent }
    $start.Environment['DOTNET_MULTILEVEL_LOOKUP'] = '0'
    $start.Environment['DOTNET_ROLL_FORWARD'] = 'Disable'
    $start.Environment['DOTNET_CLI_HOME'] = Join-Path $validation 'private-dotnet-home'
    $start.Environment['DOTNET_GENERATE_ASPNET_CERTIFICATE'] = 'false'
    $start.Environment['DOTNET_ADD_GLOBAL_TOOLS_TO_PATH'] = 'false'
    $start.Environment['DOTNET_CLI_TELEMETRY_OPTOUT'] = 'true'
    $start.Environment['POC_LAUNCHER_DATA_DIR'] = Join-Path $validation 'private-launcher-data'
    $start.Environment['PATH'] = Join-Path ([Environment]::GetFolderPath('Windows')) 'System32'
    $process = [Diagnostics.Process]::Start($start)
    if ($null -eq $process) { throw 'The authored package check did not start.' }
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    try {
        if (-not $process.WaitForExit(60000)) {
            # Only this newly created, owned authored launcher probe is stopped. No game is ever launched by this mode.
            $process.Kill($true)
            [void]$process.WaitForExit(5000)
            throw 'The authored package check timed out.'
        }
        [IO.File]::WriteAllText((Join-Path $validation 'package-check.stdout.log'), $stdout.GetAwaiter().GetResult(), [Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText((Join-Path $validation 'package-check.stderr.log'), $stderr.GetAwaiter().GetResult(), [Text.UTF8Encoding]::new($false))
        if ($process.ExitCode -ne 0) { throw 'The authored package check failed.' }
    } finally { $process.Dispose() }
    $smoke = Read-PackageJson $reportPath 131072
    Assert-PackageSmokeReport $smoke $manifest
    $smokeExecuted = $true
}
$result = [ordered]@{
    schemaVersion = 1; status = $(if ($smokeExecuted) { 'PASS' } else { 'STRUCTURE_ONLY' });
    sourceCommit = $manifest.sourceCommit; version = $manifest.version; runtimeIdentifier = $manifest.runtimeIdentifier;
    files = $actual.Count; packageSha256 = Get-PackageFileHash $archivePath; selfContained = $true;
    componentsValid = $true; smokeExecuted = $smokeExecuted; proprietaryGameExecution = $false
}
Write-PackageJson (Join-Path $validation 'validation.json') $result
$result | ConvertTo-Json
