#Requires -Version 5.1
param(
    [Parameter(Mandatory=$true)][string]$CurrentAssembly,
    [Parameter(Mandatory=$true)][string]$PreviousAssembly
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
. (Join-Path $repo 'scripts\ReleaseVersion.ps1')
$version = Get-ReleaseVersion $repo
if ((Get-PortableVersionLabel $version) -cne "v$version") { throw 'Wrong default ZIP label.' }
if ((Get-PortableVersionLabel $version "v$version-rc.1") -cne "v$version-rc.1") { throw 'Candidate suffix lost.' }

foreach ($label in @('v0.1.19', 'v999.0.0', "v${version}0", "v$version-../escape", "v$version`n")) {
    $rejected = $false
    try { Get-PortableVersionLabel $version $label | Out-Null }
    catch { $rejected = $_.Exception.Message -like 'Portable label must begin*' }
    if (-not $rejected) { throw "Invalid portable label accepted: $label" }
}

Assert-ReleaseAssemblyVersion $CurrentAssembly ([version]"$version.0")
$rejected = $false
try { Assert-ReleaseAssemblyVersion $PreviousAssembly ([version]"$version.0") }
catch { $rejected = $_.Exception.Message -like 'Assembly version gate failed*' }
if (-not $rejected) { throw 'Prior assembly accepted as current.' }
Write-Host 'Task390 PowerShell checks passed: 9'
