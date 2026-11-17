#Requires -Version 7.2
<#
.SYNOPSIS
Build a self-contained Windows x64 local development plugin without installing it.
#>
param([string]$OutputDirectory = '', [switch]$VerifyOnly)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'PluginStdioValidation.ps1')
$projectRoot = Split-Path $PSScriptRoot -Parent
$allowedRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot '.local-data/plugin-builds'))
$testRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot '.local-data/task430'))
$recordingCandidate = [IO.Path]::GetFullPath((Join-Path $projectRoot '.local-data/task431/development-recording'))
$nativeContractCandidate = [IO.Path]::GetFullPath((Join-Path $projectRoot '.local-data/task431/development-native-contract'))
$stopCandidate = [IO.Path]::GetFullPath((Join-Path $projectRoot '.local-data/task432/development-stop'))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $allowedRoot ('build-' + [Guid]::NewGuid().ToString('N')) }
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$testRelative = [IO.Path]::GetRelativePath($testRoot, $outputRoot).Replace('\', '/')
$privateTestOutput = $testRelative -match '^tests-[0-9a-f]{32}/.+'
$retainedCandidate = $VerifyOnly -and $testRelative -match '^task434-[0-9a-f]{32}$'
if (-not $outputRoot.StartsWith($allowedRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and
    -not $privateTestOutput -and -not $retainedCandidate -and
    -not ($VerifyOnly -and ([string]::Equals($outputRoot, $recordingCandidate, [StringComparison]::OrdinalIgnoreCase) -or
    [string]::Equals($outputRoot, $nativeContractCandidate, [StringComparison]::OrdinalIgnoreCase) -or
    [string]::Equals($outputRoot, $stopCandidate, [StringComparison]::OrdinalIgnoreCase)))) {
    throw 'OutputDirectory must be below .local-data/plugin-builds or an isolated test directory; legacy candidates are verify-only.'
}
if ((Test-Path -LiteralPath $outputRoot) -and -not $VerifyOnly) { throw 'OutputDirectory already exists; choose a new directory. Nothing was removed.' }
if ($VerifyOnly -and -not (Test-Path -LiteralPath $outputRoot)) { throw 'VerifyOnly requires an existing development directory.' }
for ($cursor = $outputRoot; $cursor; $cursor = Split-Path $cursor -Parent) {
    if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Output path contains a reparse point.'
    }
}
$sourceRoot = Join-Path $projectRoot 'integrations/codex'
$pluginRoot = Join-Path $outputRoot 'agent-recorder-bootstrap'

# Validate the canonical, checked-in official schemas before creating a package.
foreach ($name in @('plugin', 'mcp')) {
    $jsonPath = if ($VerifyOnly) { Join-Path $pluginRoot "$name.json" } else { Join-Path $sourceRoot "plugin/$name.json" }
    $json = Get-Content -LiteralPath $jsonPath -Raw
    if (-not (Test-Json -Json $json -SchemaFile (Join-Path $sourceRoot "schemas/$name.schema.json"))) {
        throw "Invalid $name manifest."
    }
}
$manifestPath = if ($VerifyOnly) { Join-Path $pluginRoot 'plugin.json' } else { Join-Path $sourceRoot 'plugin/plugin.json' }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.name -cne 'agent-recorder-bootstrap') { throw 'Unexpected plugin identity.' }
if ($manifest.version -cne '0.3.3') {
    # Verify retained historical artifacts only by their explicit immutable
    # contracts. Building always requires the current patch identity.
    $historical = @{
        '0.2.1' = @('0E88E8CFFA4CFFA1619C0A4452538F68AC148DD2DF58675EABE4DEBBE6F324C8', 'E5A2CE2855D9C89DFFA93EE59B9F096F89555E792E27AE1532FD635CCE72E6DA')
        '0.3.0' = @('EDB2B5FC29014D9E8CE4D2938B7DD92E2431CFAB6AA32784BD04FCC1D5AB0A09', '5DFBF982C22E4A0A01ADE9F5954EF4611E1FB0B21F19AA92ED38864B50958939')
        '0.3.1' = @('177994F104574C4365DD9307AFB21C2D0EF6AF4CAE27EE568F854695C4D4D868', '8FCE5C5BBC71205A620E12F8F747D254DB650B963785502D47CEB5EA5808445C')
        '0.3.2' = @('E65700A4F59C402A4F6F996FD73B971E8553B4705C410BF59E02999DC016962F', 'FC5E841B1DBBCE635694237F48E21D8107DF9321A9776D00ECCFAC9AA4369110')
    }
    if (-not $VerifyOnly -or -not $historical.ContainsKey($manifest.version)) { throw 'Unexpected plugin version.' }
    $expected = $historical[$manifest.version]
    if ((Get-FileHash -LiteralPath (Join-Path $pluginRoot 'server/AgentRecorder.Mcp.dll')).Hash -cne $expected[0] -or
        (Get-FileHash -LiteralPath $manifestPath).Hash -cne $expected[1]) { throw 'Historical plugin identity mismatch.' }
}
$configPath = if ($VerifyOnly) { Join-Path $pluginRoot 'mcp.json' } else { Join-Path $sourceRoot 'plugin/mcp.json' }
$sourceConfig = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
$sourceEntry = $sourceConfig.mcpServers.'agent-recorder-bootstrap'
$referenceRoot = if ($VerifyOnly) { $pluginRoot } else { Join-Path $sourceRoot 'plugin' }
# Before publish, check syntax/containment even though the server has not been built yet.
$null = Resolve-PluginStdioLaunch -PluginRoot $referenceRoot -Entry $sourceEntry -AllowMissing:(!$VerifyOnly)
if ($sourceEntry.command -cne './server/AgentRecorder.Mcp.exe' -or $sourceEntry.args.Count -ne 0) { throw 'Unexpected bundled stdio reference.' }
$serverRoot = Join-Path $pluginRoot 'server'
if (-not $VerifyOnly) {
    New-Item -ItemType Directory -Path $pluginRoot -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'plugin/plugin.json') -Destination $pluginRoot
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'plugin/mcp.json') -Destination $pluginRoot
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'plugin/skills') -Destination $pluginRoot -Recurse
    Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination $pluginRoot
    & dotnet publish (Join-Path $projectRoot 'src/AgentRecorder.Mcp/AgentRecorder.Mcp.csproj') -c Release -r win-x64 --self-contained true -o $serverRoot --nologo -p:DebugType=none -p:DebugSymbols=false -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) { throw 'MCP publish failed.' }
    $licensesRoot = Join-Path $pluginRoot 'licenses'
    New-Item -ItemType Directory -Path $licensesRoot -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'licenses/MCP-LICENSE.txt') -Destination $licensesRoot
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'licenses/MCP-THIRD-PARTY-NOTICES.txt') -Destination $licensesRoot
    $runtimeConfig = Get-Content -LiteralPath (Join-Path $serverRoot 'AgentRecorder.Mcp.runtimeconfig.json') -Raw | ConvertFrom-Json
    $runtimeVersion = ($runtimeConfig.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.NETCore.App').version
    $assets = Get-Content -LiteralPath (Join-Path $projectRoot 'src/AgentRecorder.Mcp/obj/project.assets.json') -Raw | ConvertFrom-Json
    $runtimePackage = $null
    foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
        $candidate = Join-Path $folder "microsoft.netcore.app.runtime.win-x64/$runtimeVersion"
        if (Test-Path -LiteralPath (Join-Path $candidate 'LICENSE.TXT')) { $runtimePackage = $candidate; break }
    }
    if (-not $runtimePackage) { throw 'Published runtime package license could not be resolved.' }
    Copy-Item -LiteralPath (Join-Path $runtimePackage 'LICENSE.TXT') -Destination (Join-Path $licensesRoot 'DOTNET-LICENSE.txt')
    Copy-Item -LiteralPath (Join-Path $runtimePackage 'THIRD-PARTY-NOTICES.TXT') -Destination (Join-Path $licensesRoot 'DOTNET-THIRD-PARTY-NOTICES.txt')
}
$mcp = Get-Content -LiteralPath (Join-Path $pluginRoot 'mcp.json') -Raw | ConvertFrom-Json
$entry = $mcp.mcpServers.'agent-recorder-bootstrap'
$launch = Resolve-PluginStdioLaunch -PluginRoot $pluginRoot -Entry $entry
foreach ($file in Get-ChildItem -LiteralPath $pluginRoot -File -Recurse) {
    if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse artifact.' }
    $relative = [IO.Path]::GetRelativePath($pluginRoot, $file.FullName).Replace('\', '/')
    if ($relative -match '(^|/)(prompt|report|doc|\.local-data|config|state|obj|bin)/|\.(pdb|db|key|pem|ps1|cs)$|api-key') { throw "Forbidden artifact: $relative" }
    if ($file.Extension -in @('.md', '.json', '.xml', '.config', '.txt')) {
        $contents = Get-Content -LiteralPath $file.FullName -Raw
        if ($contents -match '(?i)(?<![a-z])[a-z]:[\\/]|SECRET-fixture-key' -or $contents.Contains($projectRoot)) { throw "Machine path or private fixture in $relative" }
    }
}
foreach ($directory in Get-ChildItem -LiteralPath $pluginRoot -Directory -Recurse -Force) {
    if ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse directory in artifact.' }
}
if (-not (Test-Path -LiteralPath (Join-Path $pluginRoot 'skills/recorder-connection/SKILL.md') -PathType Leaf)) { throw 'Missing skill reference.' }
foreach ($notice in @('MCP-LICENSE.txt', 'MCP-THIRD-PARTY-NOTICES.txt', 'DOTNET-LICENSE.txt', 'DOTNET-THIRD-PARTY-NOTICES.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $pluginRoot "licenses/$notice") -PathType Leaf)) { throw "Missing license: $notice" }
}
foreach ($required in @('AgentRecorder.Mcp.exe', 'AgentRecorder.Mcp.dll', 'AgentRecorder.Mcp.runtimeconfig.json', 'AgentRecorder.Mcp.deps.json',
    'ModelContextProtocol.Core.dll', 'hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll', 'System.Private.CoreLib.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $serverRoot $required) -PathType Leaf)) { throw "Missing runtime artifact: $required" }
}
$assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $serverRoot 'AgentRecorder.Mcp.dll')).Version.ToString(3)
if ($assemblyVersion -cne $manifest.version) { throw 'Manifest and assembly versions differ.' }
$deps = Get-Content -LiteralPath (Join-Path $serverRoot 'AgentRecorder.Mcp.deps.json') -Raw | ConvertFrom-Json
if (-not $deps.libraries.PSObject.Properties['ModelContextProtocol.Core/2.4.0']) { throw 'Unexpected MCP SDK.' }
# Smoke the real published executable with EOF and a unique private override, never default state.
$smokeBinding = Join-Path $allowedRoot ('smoke-' + [Guid]::NewGuid().ToString('N') + '/binding.json')
$startInfo = [Diagnostics.ProcessStartInfo]::new($launch.Command)
$startInfo.WorkingDirectory = $launch.WorkingDirectory
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$startInfo.RedirectStandardInput = $true
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
$startInfo.ArgumentList.Add('--binding-file')
$startInfo.ArgumentList.Add($smokeBinding)
$process = [Diagnostics.Process]::Start($startInfo)
$stdoutTask = $process.StandardOutput.ReadToEndAsync()
$stderrTask = $process.StandardError.ReadToEndAsync()
$process.StandardInput.Close()
if (-not $process.WaitForExit(8000)) { throw 'EOF smoke did not exit; no process was killed.' }
if ($process.ExitCode -ne 0 -or $stdoutTask.GetAwaiter().GetResult() -ne '' -or $stderrTask.GetAwaiter().GetResult() -ne '') { throw 'Published executable EOF smoke failed.' }
$process.Dispose()
if (Test-Path -LiteralPath (Split-Path $smokeBinding -Parent)) { throw 'Read-only startup created binding state.' }
$catalog = Join-Path $outputRoot '.agents/plugins'
if (-not $VerifyOnly) {
    New-Item -ItemType Directory -Path $catalog -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'marketplace.example.json') -Destination (Join-Path $catalog 'marketplace.json')
}
if (-not (Test-Path -LiteralPath (Join-Path $catalog 'marketplace.json') -PathType Leaf)) { throw 'Missing marketplace sample.' }
Write-Host "$(if ($VerifyOnly) { 'Verified' } else { 'Built' }) local development plugin: $pluginRoot"
Write-Host 'Schema, references, artifact inventory and executable EOF smoke passed. No installation or global configuration changed.'
