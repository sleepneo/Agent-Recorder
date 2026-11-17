#Requires -Version 7.2
<## Build a source-identified GitHub preview archive; never installs or records. ##>
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (@(& git -C $root status --porcelain).Count) { throw 'Commit source changes before building release assets.' }
$commit = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') { throw 'Source commit unavailable.' }
$manifest = Get-Content -LiteralPath (Join-Path $root 'integrations/codex/plugin/plugin.json') -Raw | ConvertFrom-Json
$version = $manifest.version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid plugin version.' }
$output = Join-Path $root ('.local-data/plugin-builds/release-' + [Guid]::NewGuid().ToString('N'))
$stage = Join-Path $output 'marketplace'
& pwsh -NoProfile -File (Join-Path $PSScriptRoot 'build-codex-plugin.ps1') -OutputDirectory $stage
if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed.' }
$plugin = Join-Path $stage 'agent-recorder-bootstrap'
$assembly = Join-Path $plugin 'server/AgentRecorder.Mcp.dll'
$product = [Diagnostics.FileVersionInfo]::GetVersionInfo($assembly).ProductVersion
if ($product -cne "$version+$commit") { throw 'Published assembly does not identify the selected source commit.' }
Copy-Item -LiteralPath (Join-Path $root 'docs/integrations/codex.md') -Destination (Join-Path $stage 'README.md')
$receipt = [ordered]@{
    plugin_version = $version
    source_commit = $commit
    recorder_version = '0.1.25'
    distribution = 'github-prerelease-local-marketplace-not-directory-submission'
    mcp_sdk_version = '2.4.0'
    mcp_dll_sha256 = (Get-FileHash -LiteralPath $assembly -Algorithm SHA256).Hash.ToLowerInvariant()
}
[IO.File]::WriteAllText((Join-Path $stage 'RELEASE.json'), ($receipt | ConvertTo-Json))
$name = "AgentRecorder-Codex-Plugin-v$version-win-x64.zip"
$zip = Join-Path $output $name
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
$archive = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $archive.Entries) {
        $path = $entry.FullName
        if ($path.Contains('\') -or $path.StartsWith('/') -or $path.Contains(':') -or
            @($path.Split('/') | Where-Object { $_ -in @('.', '..') }).Count -or -not $seen.Add($path)) { throw 'Unsafe or duplicate ZIP path.' }
        if ($path -match '(^|/)(config|state|prompt|report|doc|obj|bin)/|\.(mp4|db|key|pem|pdb|trx)$|api-key') { throw "Private artifact in ZIP: $path" }
    }
    foreach ($required in @('.agents/plugins/marketplace.json','agent-recorder-bootstrap/plugin.json','agent-recorder-bootstrap/mcp.json','agent-recorder-bootstrap/server/AgentRecorder.Mcp.exe','README.md','RELEASE.json')) {
        if (-not $seen.Contains($required)) { throw "Missing archive entry: $required" }
    }
    if ($archive.Entries.Count -gt 300 -or ($archive.Entries | Measure-Object Length -Sum).Sum -gt 100MB) { throw 'Unexpected archive inventory size.' }
} finally { $archive.Dispose() }
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
$checksum = $zip + '.sha256'
[IO.File]::WriteAllText($checksum, "$hash  $name`n", [Text.Encoding]::ASCII)
[ordered]@{version=$version;source_commit=$commit;zip=$zip;sha256=$hash;checksum=$checksum;marketplace=$stage} | ConvertTo-Json
