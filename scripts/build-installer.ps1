#Requires -Version 5.1
<#
.SYNOPSIS
Compile an unsigned per-user installer from a same-version portable release.
.DESCRIPTION
Supply a trusted ISCC.exe by absolute path and pinned SHA256. The engine must
have a valid Authenticode signature and match the exact expected PREPROCVER
version from a bounded, output-disabled probe. Build work stays in
.local-data/task389r/installer-build.
.PARAMETER CompilerEngineVersion
Expected Inno engine version, such as 6.7.4. Add a fourth component only for a
nonzero PREPROCVER build byte. PE resource versions are diagnostics.
.PARAMETER CompilerSha256
Expected SHA256 of the trusted ISCC.exe selected for this build.
.PARAMETER BuildDirectory
Fresh build directory within the repository's .local-data directory.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$PortableZip,
    [Parameter(Mandatory=$true)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$PortableSha256,
    [string]$CompilerPath,
    [string]$CompilerEngineVersion,
    [ValidatePattern('^[a-fA-F0-9]{64}$')][string]$CompilerSha256,
    [string]$BuildDirectory,
    [switch]$ValidateOnly
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'PortablePackageValidation.ps1')
. (Join-Path $PSScriptRoot 'ReleaseVersion.ps1')
$project = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$productVersion = Get-ReleaseVersion $project
$inputZip = [System.IO.Path]::GetFullPath($PortableZip)
if (-not (Test-Path -LiteralPath $inputZip -PathType Leaf)) { throw "Portable ZIP missing: $inputZip" }
if ((Get-FileHash -LiteralPath $inputZip -Algorithm SHA256).Hash -ine $PortableSha256) { throw 'Portable SHA256 mismatch.' }
$count = Assert-PortableZipContents -Path $inputZip -ForbiddenText @($project, 'D:\works\', 'E:\AI_agent\')
if ($ValidateOnly) {
    [pscustomobject]@{Status='ArchiveLayoutValidatedOnly'; Entries=$count; PortableSha256=$PortableSha256; CompilerRun=$false} | ConvertTo-Json
    return
}
if ($CompilerPath -notmatch '^[A-Za-z]:[\\/]' -or
    -not (Test-Path -LiteralPath $CompilerPath -PathType Leaf)) { throw 'ISCC_MISSING: supply an existing explicit ISCC.exe path; nothing was compiled.' }
if ([System.IO.Path]::GetFileName($CompilerPath) -ine 'ISCC.exe') { throw 'Expected explicit ISCC.exe.' }
if (-not $CompilerSha256 -or $CompilerEngineVersion -notmatch '^(0|[1-9][0-9]{0,2})(\.(0|[1-9][0-9]{0,2})){2}(\.[1-9][0-9]{0,2})?$') {
    throw 'Supply -CompilerEngineVersion and -CompilerSha256 for the trusted compiler.'
}
$CompilerPath = [IO.Path]::GetFullPath($CompilerPath)
$localData = Join-Path $project '.local-data'
$work = if ($BuildDirectory) { [IO.Path]::GetFullPath($BuildDirectory) } else { Join-Path $localData 'task389r\installer-build' }
if (-not $work.StartsWith($localData + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'BuildDirectory must be inside the repository .local-data directory.'
}
if (Test-Path -LiteralPath $work) { throw "Build work directory already exists; preserve/review it before another build: $work" }
foreach ($path in @($inputZip, $CompilerPath, (Split-Path $work -Parent))) {
    $part = $path
    while ($part) {
        if ((Test-Path -LiteralPath $part) -and ((Get-Item -LiteralPath $part -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Reparse input/output rejected: $part" }
        $part = Split-Path $part -Parent
    }
}
$compiler = Get-Item -LiteralPath $CompilerPath
$compilerHash = (Get-FileHash -LiteralPath $CompilerPath -Algorithm SHA256).Hash
if ($compilerHash -ine $CompilerSha256) { throw 'ISCC_SHA256_MISMATCH' }
$compilerSignature = Get-AuthenticodeSignature -LiteralPath $CompilerPath
if ($compilerSignature.Status -ne 'Valid') { throw "ISCC_SIGNATURE_INVALID: $($compilerSignature.Status)" }
. (Join-Path $PSScriptRoot 'InstallerCompiler.ps1')
$payload = Join-Path $work 'payload'
$out = Join-Path $work 'output'
$probeDir = Join-Path $work 'probe'
New-Item -ItemType Directory -Path $probeDir,(Join-Path $work 'temp') | Out-Null
$oldTemp=$env:TEMP; $oldTmp=$env:TMP
try {
    $env:TEMP=Join-Path $work 'temp'; $env:TMP=$env:TEMP
    $probeScript = Join-Path $probeDir 'CompilerVersionProbe.iss'
    Copy-Item -LiteralPath (Join-Path $project 'installer\CompilerVersionProbe.iss') -Destination $probeScript
    $probe = Invoke-InstallerCompiler -FilePath $CompilerPath -Arguments ('/O- "' + $probeScript + '"') `
        -WorkingDirectory $probeDir -LogPath (Join-Path $work 'compiler-probe.log') -TimeoutMs 15000
    $engine = Get-VerifiedInstallerEngine -Probe $probe -ExpectedVersion $CompilerEngineVersion
    $compilerEvidence = [ordered]@{
        Path=$CompilerPath; Sha256=$compilerHash; EngineVersion=$engine.Version; PreprocVersion=$engine.PreprocVersion
        FileVersion=$compiler.VersionInfo.FileVersion; ProductVersion=$compiler.VersionInfo.ProductVersion
        SignatureStatus=$compilerSignature.Status.ToString(); Signer=$compilerSignature.SignerCertificate.Subject
    }
    $compilerEvidence | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $work 'compiler-evidence.json') -Encoding UTF8
    New-Item -ItemType Directory -Path $payload,$out | Out-Null
    [System.IO.Compression.ZipFile]::ExtractToDirectory($inputZip, $payload)
    # AudioHelper ships in the same ZIP but has its own assembly version.
    $audioProject = [xml](Get-Content -LiteralPath (Join-Path $project 'tools\AgentRecorder.AudioHelper\AgentRecorder.AudioHelper.csproj') -Raw)
    $audioVersion = [version]$audioProject.SelectSingleNode('/Project/PropertyGroup/AssemblyVersion').InnerText
    foreach ($component in @('App','Headless','Cli','AudioHelper')) {
        $assembly=Join-Path $payload "AgentRecorder.$component\AgentRecorder.$component.dll"
        if ($component -eq 'AudioHelper') {
            if ([Reflection.AssemblyName]::GetAssemblyName($assembly).Version -ne $audioVersion) {
                throw "AudioHelper assembly version must be $audioVersion."
            }
        } else {
            Assert-ReleaseAssemblyVersion $assembly ([version]"$productVersion.0")
        }
        $executable=Join-Path $payload "AgentRecorder.$component\AgentRecorder.$component.exe"
        $bytes=[IO.File]::ReadAllBytes($executable)
        if($bytes.Length -lt 64 -or $bytes[0] -ne 0x4D -or $bytes[1] -ne 0x5A){throw "Invalid executable: $component"}
        $offset=[BitConverter]::ToInt32($bytes,0x3c)
        if($offset -lt 0 -or $offset -gt $bytes.Length-6 -or [BitConverter]::ToUInt32($bytes,$offset) -ne 0x4550 -or
            [BitConverter]::ToUInt16($bytes,$offset+4) -ne 0x8664){throw "Expected Windows x64 apphost: $component"}
        foreach($runtime in @('hostfxr.dll','coreclr.dll')) {
            if(-not(Test-Path -LiteralPath (Join-Path $payload "AgentRecorder.$component\$runtime") -PathType Leaf)){throw 'Installer requires the self-contained portable package.'}
        }
    }
    $infrastructure=@('App','Headless','Cli') | ForEach-Object {(Get-FileHash -LiteralPath (Join-Path $payload "AgentRecorder.$_\AgentRecorder.Infrastructure.dll")).Hash}
    if(@($infrastructure | Select-Object -Unique).Count -ne 1){throw 'App/Headless/CLI infrastructure differs.'}
    foreach($doc in @('README.md','README.zh-CN.md','QUICKSTART.md','QUICKSTART.zh-CN.md','AGENT-INSTRUCTIONS.zh-CN.md','AGENT-API-REFERENCE.zh-CN.md','LICENSE','LICENSE-NOTICE.md','docs\api.md','docs\safety.md')) {
        if((Get-FileHash -LiteralPath (Join-Path $payload $doc)).Hash -cne (Get-FileHash -LiteralPath (Join-Path $project $doc)).Hash){throw "Portable public document differs from this source: $doc"}
    }
    $cliDir=Join-Path $payload 'AgentRecorder.Cli'
    $cli=Join-Path $cliDir 'AgentRecorder.Cli.exe'
    $info=@(& $cli installation-package-info)
    $productId = 'AgentRecorder.PerUser.v1'
    if ($LASTEXITCODE -ne 0 -or $info.Count -ne 1 -or $info[0] -cne "$productId/$productVersion") {
        throw "Portable CLI installation identity must be $productId/$productVersion."
    }
    $marker = [ordered]@{format=1; product=$productId; version=$productVersion; mode='per-user'} | ConvertTo-Json -Compress
    [IO.File]::WriteAllText((Join-Path $payload 'agent-recorder.install.json'),$marker,[Text.UTF8Encoding]::new($false))
    . (Join-Path $PSScriptRoot 'InstallerPayload.ps1')
    $includes=Write-InstallerPayloadIncludes -payload $payload -work $work
    $fileList=$includes.FileList; $guardHashes=$includes.GuardHashes
    if ((Get-FileHash -LiteralPath $CompilerPath -Algorithm SHA256).Hash -cne $compilerHash) { throw 'ISCC changed after the engine probe.' }
    $iss = Join-Path $project 'installer\AgentRecorder.iss'
    $compileArgs = '"/DPayloadDir={0}" "/DPayloadFiles={1}" "/DGuardHashes={2}" "/DInstallerOutput={3}" /DReleaseVersion={4} "{5}"' -f $payload,$fileList,$guardHashes,$out,$productVersion,$iss
    $compileLog = Join-Path $work 'compile.log'
    $compiled = Invoke-InstallerCompiler -FilePath $CompilerPath -Arguments $compileArgs `
        -WorkingDirectory $work -LogPath $compileLog -TimeoutMs 900000
    if ($compiled.TimedOut -or $compiled.ExitCode -ne 0) { throw "ISCC compile failed. Exit: $($compiled.ExitCode). Timeout: $($compiled.TimedOut). Log: $compileLog" }
    $exe=Get-Item -LiteralPath (Join-Path $out "AgentRecorder-v$productVersion-win-x64-setup-unsigned.exe")
    $setupVersion = [version]"$productVersion.0"
    if ([version]$exe.VersionInfo.FileVersion -ne $setupVersion -or [version]$exe.VersionInfo.ProductVersion -ne $setupVersion) {
        throw "Compiled Setup PE version differs from $setupVersion."
    }
    $signature=Get-AuthenticodeSignature -LiteralPath $exe.FullName
    if ($signature.Status -ne 'NotSigned') { throw "Unexpected signature state: $($signature.Status)" }
    [pscustomobject]@{Status='CompiledUnsigned';ProductVersion=$productVersion;PortableZip=$inputZip;PortableSha256=$PortableSha256;Entries=$count;
        Compiler=$compilerEvidence; CompilerExitCode=$compiled.ExitCode; CompilerArguments=$compileArgs;
        Installer=$exe.FullName;InstallerBytes=$exe.Length;InstallerSha256=(Get-FileHash -LiteralPath $exe.FullName).Hash;SignatureStatus=$signature.Status.ToString();Installed=$false} |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $work 'build-evidence.json') -Encoding UTF8
} finally { $env:TEMP=$oldTemp; $env:TMP=$oldTmp }
