function Assert-NoLinks([string]$Path) {
    for ($item=[IO.Path]::GetFullPath($Path); $item; $item=[IO.Path]::GetDirectoryName($item)) {
        if ((Test-Path -LiteralPath $item) -and ((Get-Item -LiteralPath $item -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw ('Linked paths are not supported: '+$item) }
    }
}
function Assert-NoTreeLinks([string]$Path) {
    Assert-NoLinks $Path
    foreach ($item in Get-ChildItem -LiteralPath $Path -Recurse -Force) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw ('Linked paths are not supported: '+$item.FullName) }
    }
}
function Assert-Private([string]$Path,[switch]$Fresh) {
    $full=[IO.Path]::GetFullPath($Path)
    Assert-NoLinks $full
    $checkout=$null
    for ($parent=[IO.Path]::GetDirectoryName($full); $parent; $parent=[IO.Path]::GetDirectoryName($parent)) {
        if (Test-Path -LiteralPath (Join-Path $parent '.git')) { $checkout=$parent; break }
    }
    if (-not $checkout -or -not $full.StartsWith((Join-Path $checkout '.local').TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Output/source must be inside repository .local scratch' }
    if ($Fresh -and (Test-Path -LiteralPath $full)) { throw 'Destination must be new' }
    & git -C $checkout check-ignore -- (Join-Path $full 'ignore-proof') | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Output must be ignored' }
}
