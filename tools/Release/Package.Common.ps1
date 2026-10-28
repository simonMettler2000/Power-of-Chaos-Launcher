Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-PackageFullPath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { throw 'A nonempty path is required.' }
    return [IO.Path]::GetFullPath($Path)
}

function Assert-PackageOrdinaryPath([string]$Path) {
    for ($item = (Get-PackageFullPath $Path); $item; $item = [IO.Path]::GetDirectoryName($item)) {
        if (Test-Path -LiteralPath $item) {
            $attributes = [IO.File]::GetAttributes($item)
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Linked package paths are not supported.' }
        }
    }
}

function Assert-PackageDisjoint([string]$First, [string]$Second) {
    $a = [IO.Path]::TrimEndingDirectorySeparator((Get-PackageFullPath $First))
    $b = [IO.Path]::TrimEndingDirectorySeparator((Get-PackageFullPath $Second))
    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    if ($a.Equals($b, $comparison) -or $a.StartsWith($b + [IO.Path]::DirectorySeparatorChar, $comparison) -or
        $b.StartsWith($a + [IO.Path]::DirectorySeparatorChar, $comparison)) { throw 'Package input and output paths must not overlap.' }
}

function Assert-PackageRelativeName([string]$Name) {
    if ([string]::IsNullOrEmpty($Name) -or $Name.Length -gt 240 -or $Name.Contains('\') -or $Name.Contains(':') -or
        $Name.StartsWith('/') -or $Name -match '[\x00-\x1f\x7f]' -or [IO.Path]::IsPathRooted($Name)) {
        throw 'Unsafe package entry name.'
    }
    foreach ($part in $Name.Split('/')) {
        if ($part -in @('', '.', '..') -or $part.EndsWith('.') -or $part.EndsWith(' ') -or
            $part -match '[<>"|?*]' -or $part -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)') {
            throw 'Unsafe Windows package entry name.'
        }
    }
}

function Get-PackageFileHash([string]$Path) {
    Assert-PackageOrdinaryPath $Path
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-PackageOrdinalNames($Names) {
    $ordered = [Collections.Generic.List[string]]::new()
    foreach ($name in $Names) { $ordered.Add([string]$name) }
    $ordered.Sort([StringComparer]::Ordinal)
    return @($ordered)
}

function Copy-PackageBoundedStream([IO.Stream]$Source, [IO.Stream]$Destination, [long]$ExpectedLength) {
    if ($ExpectedLength -lt 0 -or $ExpectedLength -gt 134217728) { throw 'Invalid stream length bound.' }
    $buffer = [byte[]]::new(65536)
    $total = 0L
    while ($true) {
        $count = [int][Math]::Min($buffer.Length, $ExpectedLength - $total + 1)
        $read = $Source.Read($buffer, 0, $count)
        if ($read -eq 0) { break }
        $total += $read
        if ($total -gt $ExpectedLength) { throw 'Expanded stream exceeds its declared length.' }
        $Destination.Write($buffer, 0, $read)
    }
    if ($total -ne $ExpectedLength) { throw 'Expanded stream is shorter than its declared length.' }
}

function Read-PackageJson([string]$Path, [long]$MaximumBytes = 4194304) {
    Assert-PackageOrdinaryPath $Path
    $file = Get-Item -LiteralPath $Path
    if ($file.PSIsContainer -or $file.Length -le 0 -or $file.Length -gt $MaximumBytes) { throw 'Invalid package metadata size.' }
    $text = [IO.File]::ReadAllText($file.FullName, [Text.UTF8Encoding]::new($false, $true))
    return ConvertFrom-Json -InputObject $text -AsHashtable -Depth 32
}

function Write-PackageJson([string]$Path, $Value) {
    if (Test-Path -LiteralPath $Path) { throw 'Package metadata must be a new file.' }
    Assert-PackageOrdinaryPath $Path
    $text = (ConvertTo-Json -InputObject $Value -Depth 32) + "`n"
    [IO.File]::WriteAllText((Get-PackageFullPath $Path), $text.Replace("`r`n", "`n"), [Text.UTF8Encoding]::new($false))
}

function Assert-PackageVersion([string]$Version) {
    if ($Version.Length -gt 64 -or $Version -cnotmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z][0-9A-Za-z.-]*)?$') {
        throw 'A safe semantic package version is required.'
    }
}

function Read-PackageSettings([string]$Path) {
    $settings = Read-PackageJson $Path 131072
    if ($settings.schemaVersion -ne 1 -or $settings.sdkVersion -cnotmatch '^\d+\.\d+\.\d+$' -or
        $settings.runtimeVersion -cnotmatch '^\d+\.\d+\.\d+$' -or $settings.targetFramework -cnotmatch '^net\d+\.\d+$' -or
        $settings.sourceRepository -cnotmatch '^https://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$' -or
        $settings.supportedRuntimeIdentifiers.Count -le 0 -or $settings.supportedRuntimeIdentifiers.Count -gt 3 -or
        @($settings.supportedRuntimeIdentifiers | Where-Object { $_ -notin @('win-x64', 'win-x86', 'win-arm64') }).Count -ne 0) {
        throw 'Invalid pinned release settings.'
    }
    return $settings
}

function Get-PackageFiles([string]$Root) {
    Assert-PackageOrdinaryPath $Root
    $files = [Collections.Generic.List[object]]::new()
    $directories = [Collections.Generic.Queue[string]]::new()
    $directories.Enqueue((Get-PackageFullPath $Root))
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $total = 0L
    while ($directories.Count -gt 0) {
        $directory = $directories.Dequeue()
        foreach ($entry in Get-ChildItem -LiteralPath $directory -Force) {
            if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Linked package content is forbidden.' }
            $relative = [IO.Path]::GetRelativePath((Get-PackageFullPath $Root), $entry.FullName).Replace('\', '/')
            Assert-PackageRelativeName $relative
            if (-not $seen.Add($relative)) { throw 'Duplicate package path.' }
            if ($seen.Count -gt 5000) { throw 'Package entry count exceeds its bound.' }
            if ($entry.PSIsContainer) { $directories.Enqueue($entry.FullName); continue }
            if ($entry.Length -gt 134217728) { throw 'A package file exceeds its bound.' }
            $total += $entry.Length
            if ($total -gt 1073741824) { throw 'Package content exceeds its total bound.' }
            $files.Add([ordered]@{ path = $relative; length = $entry.Length; sha256 = Get-PackageFileHash $entry.FullName })
        }
    }
    $orderedNames = [Collections.Generic.List[string]]::new()
    $byName = @{}
    foreach ($file in $files) { $orderedNames.Add($file.path); $byName[$file.path] = $file }
    $orderedNames.Sort([StringComparer]::Ordinal)
    return @($orderedNames | ForEach-Object { $byName[$_] })
}

function Assert-PackagePayloadName([string]$Name) {
    Assert-PackageRelativeName $Name
    $parts = $Name.Split('/')
    if (@($parts | Where-Object { $_ -imatch '^(?:\.git|\.local|obj|bin|mod-cache|launcher-data|saves?|test-fixtures)$' }).Count -ne 0 -or
        $parts[-1] -imatch '^(?:yugi_pc\.exe|kaiba_pc\.exe|joey_pc\.exe|system\.dat|data\.dat|voice\.dat|launcher-settings\.json|playtime\.json|sessions\.json)$') {
        throw 'Game, personal or development content is forbidden in a package.'
    }
    if ($Name -like 'Components/original-aspect-ratio-v1/*') { return }
    if ($Name -like 'Notices/*') {
        # Corresponding source required by selected dependency licenses is admitted only through exact notice pins.
        if ($parts.Count -ne 2 -or ([IO.Path]::GetExtension($Name) -notin @('.txt', '.md', '.cpp', '.h', '.c') -and
            $parts[-1] -cne 'CorrespondingSource-libgifcodec-d06d2a6.zip')) { throw 'Unexpected notice path.' }
        return
    }
    if ($Name -in @('package-manifest.json', 'README.txt', 'component-pins.json', 'release-settings.json',
        'PowerOfChaos.Launcher.deps.json', 'PowerOfChaos.Launcher.runtimeconfig.json', 'LICENSE.txt', 'THIRD-PARTY-NOTICES.txt')) { return }
    if ([IO.Path]::GetExtension($Name) -ieq '.dll') { return }
    if ($Name -in @('PowerOfChaos.Launcher.exe', 'createdump.exe')) { return }
    throw 'Unexpected published package file type.'
}

function Assert-PackagePins([string]$Directory, $Pins) {
    Assert-PackageOrdinaryPath $Directory
    $records = @(Get-PackageFiles $Directory)
    $expected = @($Pins)
    if ($expected.Count -lt 1 -or $expected.Count -gt 128 -or $records.Count -ne $expected.Count) { throw 'Pinned file set does not match.' }
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($pin in $expected) {
        if ($pin.name -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$' -or -not $names.Add($pin.name) -or
            $pin.sha256 -cnotmatch '^[a-fA-F0-9]{64}$' -or [long]$pin.length -lt 1 -or [long]$pin.length -gt 134217728) { throw 'Invalid pinned file metadata.' }
        $record = @($records | Where-Object { $_.path -ceq $pin.name })
        if ($record.Count -ne 1 -or $record[0].length -ne $pin.length -or $record[0].sha256 -ine $pin.sha256) { throw ('Pinned file differs: ' + $pin.name) }
    }
}

function Assert-PackageCorrespondingSources([string]$Directory, $Pins) {
    foreach ($pin in $Pins) {
        if ([IO.Path]::GetExtension($pin.name) -ine '.zip') { continue }
        if ($pin.name -cne 'CorrespondingSource-libgifcodec-d06d2a6.zip') { throw 'Unknown corresponding-source archive.' }
        $zip = [IO.Compression.ZipFile]::OpenRead((Join-Path $Directory $pin.name))
        try {
            if ($zip.Entries.Count -lt 11 -or $zip.Entries.Count -gt 128) { throw 'Invalid corresponding-source inventory size.' }
            $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            $total = 0L
            foreach ($entry in $zip.Entries) {
                Assert-PackageRelativeName $entry.FullName
                if (-not $names.Add($entry.FullName) -or $entry.FullName.EndsWith('/') -or
                    (($entry.ExternalAttributes -shr 16) -band 0xf000) -notin @(0, 0x8000) -or $entry.Length -gt 1048576 -or
                    [IO.Path]::GetExtension($entry.FullName) -notin @('.cpp', '.h', '.c', '.md', '.txt', '.gni', '.gn', '.cake', '.chromium')) {
                    throw 'Unsafe corresponding-source entry.'
                }
                $total += $entry.Length
                if ($total -gt 2097152) { throw 'Corresponding-source archive exceeds its bound.' }
                $stream = $entry.Open()
                $memory = [IO.MemoryStream]::new()
                try {
                    Copy-PackageBoundedStream $stream $memory $entry.Length
                    if ($memory.Length -ne $entry.Length) { throw 'Corresponding-source entry length differs.' }
                    $text = [Text.UTF8Encoding]::new($false, $true).GetString($memory.ToArray())
                    if ($text.IndexOf([char]0) -ge 0) { throw 'Binary content in corresponding-source archive.' }
                } finally { $memory.Dispose(); $stream.Dispose() }
            }
            $prefix = 'libgifcodec-d06d2a6d42baf6c0c91cacc28df2542a911d05fe/'
            foreach ($required in @('LICENSE.md', 'README.chromium', 'README.md', 'SkGifCodec.h', 'SkGifImageReader.cpp',
                'SkGifImageReader.h', 'SkLibGifCodec.cpp', 'SkLibGifCodec.h', 'libgifcodec.gni')) {
                if (-not $names.Contains($prefix + $required)) { throw 'A corresponding-source file is missing.' }
            }
            if (-not $names.Contains('MPL-1.1.txt') -or -not $names.Contains('README.md')) { throw 'Corresponding-source license or provenance is missing.' }
        } finally { $zip.Dispose() }
    }
}

function Get-PackagePeMachine([string]$Path) {
    Assert-PackageOrdinaryPath $Path
    $stream = [IO.File]::OpenRead($Path)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        if ($stream.Length -lt 128 -or $reader.ReadUInt16() -ne 0x5A4D) { throw 'Invalid packaged PE header.' }
        $stream.Position = 0x3c
        $pe = $reader.ReadUInt32()
        if ($pe -lt 64 -or $pe -gt $stream.Length - 24) { throw 'Invalid packaged PE offset.' }
        $stream.Position = $pe
        if ($reader.ReadUInt32() -ne 0x4550) { throw 'Invalid packaged PE signature.' }
        return $reader.ReadUInt16()
    } finally { $reader.Dispose(); $stream.Dispose() }
}

function Assert-PackageDependencyFiles($Files, $Dependencies) {
    $admitted = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($target in $Dependencies.targets.Values) {
        foreach ($library in $target.Values) {
            foreach ($group in @('runtime', 'native', 'resources', 'runtimeTargets')) {
                if (-not $library.ContainsKey($group)) { continue }
                foreach ($path in $library[$group].Keys) { [void]$admitted.Add([IO.Path]::GetFileName($path.Replace('\', '/'))) }
            }
        }
    }
    foreach ($file in $Files) {
        if ([IO.Path]::GetExtension($file.path) -ine '.dll' -or $file.path.StartsWith('Components/', [StringComparison]::Ordinal)) { continue }
        if (-not $admitted.Contains([IO.Path]::GetFileName($file.path))) { throw ('Published DLL is absent from dependency provenance: ' + $file.path) }
    }
}

function Assert-PackageSmokeReport($Report, $Manifest) {
    if ($Report.schemaVersion -ne 1 -or $Report.success -ne $true -or $Report.runtimeLocal -ne $true -or
        $Report.actualVersion -cne $Manifest.runtimeVersion -or $Report.componentsValid -ne $true -or
        $Report.desktopDependenciesValid -ne $true -or $Report.version -cne $Manifest.version -or
        $Report.sourceCommit -cne $Manifest.sourceCommit -or $Report.runtimeIdentifier -cne $Manifest.runtimeIdentifier) {
        throw 'The authored self-contained desktop package report differs from the package identity.'
    }
}

function Expand-SafeWindowsPackage([string]$ArchivePath, [string]$Destination) {
    Assert-PackageOrdinaryPath $ArchivePath
    Assert-PackageOrdinaryPath $Destination
    if (Test-Path -LiteralPath $Destination) { throw 'Package extraction destination must be new.' }
    $zip = [IO.Compression.ZipFile]::OpenRead((Get-PackageFullPath $ArchivePath))
    try {
        if ($zip.Entries.Count -lt 10 -or $zip.Entries.Count -gt 5000) { throw 'Invalid archive entry count.' }
        $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $total = 0L
        foreach ($entry in $zip.Entries) {
            $name = $entry.FullName
            Assert-PackagePayloadName $name
            if ($name.EndsWith('/') -or -not $names.Add($name) -or (($entry.ExternalAttributes -shr 16) -band 0xf000) -notin @(0, 0x8000) -or
                $entry.Length -gt 134217728) { throw 'Unsafe archive entry.' }
            $total += $entry.Length
            if ($total -gt 1073741824) { throw 'Archive exceeds expanded size bound.' }
        }
        [void][IO.Directory]::CreateDirectory((Get-PackageFullPath $Destination))
        foreach ($entry in $zip.Entries) {
            $target = [IO.Path]::Combine((Get-PackageFullPath $Destination), $entry.FullName.Replace('/', [IO.Path]::DirectorySeparatorChar))
            Assert-PackageOrdinaryPath $target
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
            $sourceStream = $entry.Open()
            $output = [IO.File]::Open($target, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            try { Copy-PackageBoundedStream $sourceStream $output $entry.Length } finally { $output.Dispose(); $sourceStream.Dispose() }
            if ((Get-Item -LiteralPath $target).Length -ne $entry.Length) { throw 'Extracted archive entry length differs.' }
        }
    } finally { $zip.Dispose() }
}
