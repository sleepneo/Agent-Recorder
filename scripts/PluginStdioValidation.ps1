# Read-only checks for this bundled Windows executable, not a complete Codex loader.
function Resolve-PluginContainedReference {
    param([string]$PluginRoot, [string]$Reference, [switch]$Directory, [switch]$AllowMissing)
    $code = if ($Directory) { 'PLUGIN_CWD_INVALID' } else { 'PLUGIN_COMMAND_INVALID' }
    if (-not $Reference.StartsWith('./', [StringComparison]::Ordinal)) { throw "${code}: expected contained ./ path." }
    $relative = $Reference.Substring(2)
    if ($Directory -and $relative -eq '') { $parts = @() }
    else {
        $parts = $relative.Split('/')
        foreach ($part in $parts) {
            if ($part -in @('', '.', '..') -or $part -match '[\\:<>"|?*\x00-\x1f$%{}]' -or
                $part.EndsWith('.') -or $part.EndsWith(' ') -or $part -match '^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])($|\.)') {
                throw "${code}: unsupported path segment."
            }
        }
    }
    $root = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($PluginRoot))
    $resolved = $root
    foreach ($part in $parts) { $resolved = Join-Path $resolved $part }
    $resolved = [IO.Path]::GetFullPath($resolved)
    $prefix = if ([IO.Path]::EndsInDirectorySeparator($root)) { $root } else { $root + [IO.Path]::DirectorySeparatorChar }
    if (-not [string]::Equals($root, $resolved, [StringComparison]::OrdinalIgnoreCase) -and
        -not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw "${code}: path escapes plugin root." }
    for ($cursor = $resolved; $cursor; $cursor = Split-Path $cursor -Parent) {
        if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "${code}: reparse path rejected."
        }
    }
    if (-not $AllowMissing) {
        $kind = if ($Directory) { 'Container' } else { 'Leaf' }
        if (-not (Test-Path -LiteralPath $resolved -PathType $kind)) { throw "${code}: referenced entry missing." }
    }
    return $resolved
}

function Resolve-PluginStdioLaunch {
    param([string]$PluginRoot, [object]$Entry, [switch]$AllowMissing)
    if ($Entry.type -cne 'stdio' -or $Entry.command -isnot [string] -or -not $Entry.command.EndsWith('.exe', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'PLUGIN_COMMAND_INVALID: expected a bundled Windows stdio executable.'
    }
    # Command is never placeholder-expanded. This package deliberately disallows PATH search.
    $command = Resolve-PluginContainedReference -PluginRoot $PluginRoot -Reference $Entry.command -AllowMissing:$AllowMissing
    $cwd = $Entry.cwd
    if ($null -eq $cwd -or $cwd -ceq '${PLUGIN_ROOT}') { $cwd = './' }
    elseif ($cwd -is [string] -and $cwd.StartsWith('${PLUGIN_ROOT}/', [StringComparison]::Ordinal)) {
        $cwd = './' + $cwd.Substring('${PLUGIN_ROOT}/'.Length)
    }
    if ($cwd -isnot [string]) { throw 'PLUGIN_CWD_INVALID: expected plugin-root working directory.' }
    $directory = Resolve-PluginContainedReference -PluginRoot $PluginRoot -Reference $cwd -Directory -AllowMissing:$AllowMissing
    [pscustomobject]@{ Command = $command; WorkingDirectory = $directory }
}
