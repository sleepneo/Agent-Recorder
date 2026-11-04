# Shared by the existing portable build and the installer consumer. Read-only: never extracts or executes entries.
function Assert-PortableZipContents {
    param([Parameter(Mandatory = $true)][string]$Path, [string[]]$ForbiddenText = @())
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $names = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
        $files = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
        $public = @('README.md','README.zh-CN.md','QUICKSTART.md','QUICKSTART.zh-CN.md','AGENT-INSTRUCTIONS.zh-CN.md','AGENT-API-REFERENCE.zh-CN.md','LICENSE','LICENSE-NOTICE.md','docs/api.md','docs/safety.md')
        $components = @('AgentRecorder.App','AgentRecorder.Headless','AgentRecorder.Cli','AgentRecorder.AudioHelper','AgentRecorder.WgcHelper')
        [long]$expanded = 0
        if ($archive.Entries.Count -gt 20000) { throw 'Too many archive entries.' }
        foreach ($entry in $archive.Entries) {
            $n = $entry.FullName
            $parts = $n.TrimEnd('/').Split('/')
            if ([string]::IsNullOrEmpty($n) -or $n.Length -gt 240 -or $n.Contains('\') -or $n.StartsWith('/') -or
                $n -match '[:*?"<>|\x00-\x1f]' -or -not $names.Add($n.TrimEnd('/'))) { throw "Unsafe/duplicate archive path: $n" }
            foreach ($part in $parts) {
                if ($part -in @('', '.', '..') -or $part.EndsWith('.') -or $part.EndsWith(' ') -or
                    $part -match '^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\.|$)') { throw "Unsafe Windows path: $n" }
            }
            if ((($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000 -or
                ($entry.ExternalAttributes -band 0x400) -ne 0) { throw "Link/reparse archive entry: $n" }
            if ($parts[0] -notin $components -and $n -notin $public -and $n -ne 'docs/') { throw "Unapproved payload path: $n" }
            if (-not $n.EndsWith('/') -and $n -notin $public -and $n -notmatch '\.(dll|exe)$' -and
                $n -notmatch '^AgentRecorder\.(App|Headless|Cli|AudioHelper)/AgentRecorder\.\1\.(deps|runtimeconfig)\.json$') { throw "Unapproved payload file: $n" }
            if ($n -match '(^|/)(doc|prompt|report|bin|obj|\.git|\.local-data)(/|$)' -or
                $n -match '(^|/)(api-key|appsettings\.Development)' -or $n -match '\.(pdb|obj|cpp|h|vcxproj|sln|cs|ps1|key|pem)$') { throw "Forbidden payload artifact: $n" }
            if ($n.StartsWith('AgentRecorder.WgcHelper/', [System.StringComparison]::OrdinalIgnoreCase) -and
                $n -cne 'AgentRecorder.WgcHelper/wgc-native-helper.exe') { throw "Unexpected WGC artifact: $n" }
            if (-not $n.EndsWith('/')) { [void]$files.Add($n) }
            $expanded += $entry.Length
            if ($expanded -gt 4GB) { throw 'Expanded package exceeds the 4 GiB build limit.' }
            if ($n -match '\.(md|txt|json|xml|config)$' -or $n -ceq 'LICENSE') {
                if ($entry.Length -gt 16MB) { throw "Text entry exceeds limit: $n" }
                $reader = [System.IO.StreamReader]::new($entry.Open())
                try { $content = $reader.ReadToEnd() } finally { $reader.Dispose() }
                foreach ($needle in $ForbiddenText) {
                    if ($needle -and $content.IndexOf($needle, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) { throw "Private path in $n" }
                }
            }
        }
        foreach ($n in $names) {
            $parent = $n
            while ($parent.Contains('/')) {
                $parent = $parent.Substring(0, $parent.LastIndexOf('/'))
                if ($files.Contains($parent)) { throw "File/directory archive conflict: $n" }
            }
        }
        foreach ($required in ($public + @('AgentRecorder.App/AgentRecorder.App.exe','AgentRecorder.App/ffmpeg.exe',
            'AgentRecorder.Headless/AgentRecorder.Headless.exe','AgentRecorder.Cli/AgentRecorder.Cli.exe',
            'AgentRecorder.AudioHelper/AgentRecorder.AudioHelper.exe','AgentRecorder.AudioHelper/NAudio.Core.dll',
            'AgentRecorder.AudioHelper/NAudio.Wasapi.dll','AgentRecorder.WgcHelper/wgc-native-helper.exe'))) {
            if (-not $files.Contains($required)) { throw "Required portable payload missing: $required" }
        }
        return $archive.Entries.Count
    } finally { $archive.Dispose() }
}
