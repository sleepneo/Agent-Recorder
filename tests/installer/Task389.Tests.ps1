#Requires -Version 5.1
param([Parameter(Mandatory=$true)][string]$WorkDirectory)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$areas=@('task389','task389r','task389s') | ForEach-Object { Join-Path $repo ".local-data\$_" }
$work=[IO.Path]::GetFullPath($WorkDirectory)
$area=$areas | Where-Object { $work.StartsWith($_+'\',[StringComparison]::OrdinalIgnoreCase) }
if (-not $area -or (Test-Path -LiteralPath $work)) { throw 'Use a new installer task directory.' }
New-Item -ItemType Directory -Path $work | Out-Null
. (Join-Path $repo 'scripts\PortablePackageValidation.ps1')
. (Join-Path $repo 'scripts\InstallerPayload.ps1')
Add-Type -AssemblyName System.IO.Compression.FileSystem
$required=@('README.md','README.zh-CN.md','QUICKSTART.md','QUICKSTART.zh-CN.md','AGENT-INSTRUCTIONS.zh-CN.md','AGENT-API-REFERENCE.zh-CN.md','LICENSE','LICENSE-NOTICE.md','docs/api.md','docs/safety.md',
 'AgentRecorder.App/AgentRecorder.App.exe','AgentRecorder.App/ffmpeg.exe','AgentRecorder.Headless/AgentRecorder.Headless.exe','AgentRecorder.Cli/AgentRecorder.Cli.exe',
 'AgentRecorder.AudioHelper/AgentRecorder.AudioHelper.exe','AgentRecorder.AudioHelper/NAudio.Core.dll','AgentRecorder.AudioHelper/NAudio.Wasapi.dll','AgentRecorder.WgcHelper/wgc-native-helper.exe')
$results=[Collections.Generic.List[object]]::new()
function Check([string]$Name,[scriptblock]$Body) {
    & $Body
    $results.Add([pscustomobject]@{Name=$Name;Passed=$true})
}
function New-Zip([string]$Name,[string[]]$Extra=@(),[string]$Omit='',[switch]$Link) {
 $path=Join-Path $work "$Name.zip"
 $zip=[IO.Compression.ZipFile]::Open($path,[IO.Compression.ZipArchiveMode]::Create)
 try {
  foreach($n in ($required+$Extra)) {
   if($n -ceq $Omit){continue}
   $entry=$zip.CreateEntry($n)
   if($Link -and $n -ceq 'AgentRecorder.App/link.dll'){$entry.ExternalAttributes=-1577123840}
   $writer=[IO.StreamWriter]::new($entry.Open())
   try{$writer.Write('synthetic')}finally{$writer.Dispose()}
  }
 }finally{$zip.Dispose()}
 return $path
}
$valid=New-Zip 'valid'
Check 'synthetic-valid-complete-layout' {if((Assert-PortableZipContents $valid) -ne $required.Count){throw 'count mismatch'}}
$invalid=@('../escape.dll','/absolute.dll','C:/absolute.dll','AgentRecorder.App/../escape.dll','AgentRecorder.App\escape.dll',
 'AgentRecorder.App//escape.dll','AgentRecorder.App/NUL.dll','AgentRecorder.App/trailing./x.dll','AgentRecorder.App/x.dll:stream',
 'AgentRecorder.App/foo?.dll','README.md','readme.MD','prompt/private.md','doc/private.md','report/private.md',
 'AgentRecorder.App/api-key.txt','AgentRecorder.App/secrets.json','AgentRecorder.App/bin/a.dll','AgentRecorder.App/source.cs',
 'AgentRecorder.App/private.md','AgentRecorder.WgcHelper/extra.dll','AgentRecorder.App/block.dll','AgentRecorder.App/block.dll/child.dll')
$i=0
foreach($n in $invalid){
 $i++;$zip=New-Zip "bad-$i" -Extra @($n)
 if($n -eq 'AgentRecorder.App/block.dll/child.dll'){$zip=New-Zip 'bad-file-parent' -Extra @('AgentRecorder.App/block.dll',$n)}
 # A normal extra DLL is allowed; exercise the conflicting parent only in the final case.
 if($n -eq 'AgentRecorder.App/block.dll'){continue}
 $rejected=$false;try{Assert-PortableZipContents $zip | Out-Null}catch{$rejected=$true}
 if(-not $rejected){throw "Unsafe archive accepted: $n"}
 $results.Add([pscustomobject]@{Name="reject:$n";Passed=$true})
}
Check 'required-helper-missing-rejected' {
 $zip=New-Zip 'missing' -Omit 'AgentRecorder.WgcHelper/wgc-native-helper.exe'
 $rejected=$false;try{Assert-PortableZipContents $zip | Out-Null}catch{$rejected=$true};if(-not $rejected){throw 'Missing helper accepted'}
}
Check 'symbolic-entry-rejected' {
 $zip=New-Zip 'link' -Extra @('AgentRecorder.App/link.dll') -Link
 $rejected=$false;try{Assert-PortableZipContents $zip | Out-Null}catch{$rejected=$true};if(-not $rejected){throw 'Link accepted'}
}
Check 'wrong-sha-rejected-before-extraction-or-compiler' {
 $buildWorkspace=Join-Path $area 'installer-build'
 $buildWorkspaceExisted=Test-Path -LiteralPath $buildWorkspace
 $rejected=$false
 try { & (Join-Path $repo 'scripts\build-installer.ps1') -PortableZip $valid -PortableSha256 ('0'*64) -BuildDirectory $buildWorkspace -ValidateOnly | Out-Null }
 catch { $rejected=$_.Exception.Message -match 'SHA256 mismatch' }
 if(-not $rejected){throw 'Mismatched portable SHA accepted'}
 if(-not $buildWorkspaceExisted -and (Test-Path -LiteralPath $buildWorkspace)){throw 'Invalid input created build workspace'}
}
Check 'owned-uninstall-manifest-preserves-data-and-program-extras' {
 $payload=Join-Path $work 'payload';[IO.Compression.ZipFile]::ExtractToDirectory($valid,$payload)
 $includes=Write-InstallerPayloadIncludes -payload $payload -work $work
 $manifest=Get-Content -LiteralPath (Join-Path $work 'payload-manifest.json') -Raw | ConvertFrom-Json
 $program=Join-Path $work 'local\Programs\AgentRecorder';$data=Join-Path $work 'local\AgentRecorder'
 foreach($entry in $manifest){
   $dest=Join-Path $program $entry.Path
   New-Item -ItemType Directory -Path (Split-Path $dest -Parent) -Force | Out-Null
   Copy-Item -LiteralPath (Join-Path $payload $entry.Path) -Destination $dest
 }
 $keep=@((Join-Path $program 'user-extra.txt'),(Join-Path $program 'extra-dir\keep.json'),(Join-Path $data 'Videos\test.mp4'),
  (Join-Path $data 'config\api-key.txt'),(Join-Path $data 'config\settings.json'),(Join-Path $data 'state\history.db'),(Join-Path $data 'logs\audit.jsonl'))
 foreach($p in $keep){New-Item -ItemType Directory -Path (Split-Path $p -Parent) -Force | Out-Null;[IO.File]::WriteAllText($p,'synthetic-preserve')}
 $before=@{};foreach($p in $keep){$before[$p]=(Get-FileHash -LiteralPath $p).Hash}
 foreach($entry in $manifest){
   $owned=[IO.Path]::GetFullPath((Join-Path $program $entry.Path))
   if(-not $owned.StartsWith($program+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Uninstall escape'}
   if((Get-Item -LiteralPath $owned).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Reparse owned fixture'}
   Remove-Item -LiteralPath $owned
 }
 foreach($entry in $manifest){if(Test-Path -LiteralPath (Join-Path $program $entry.Path)){throw 'Owned file remains'}}
 foreach($p in $keep){if((Get-FileHash -LiteralPath $p).Hash -cne $before[$p]){throw 'User fixture changed'}}
 $lines=Get-Content -LiteralPath $includes.FileList
 if(@($lines | Where-Object {$_ -match 'Flags: ignoreversion$'}).Count -ne $manifest.Count){throw 'Inno file selection differs from simulated inventory'}
}
Check 'uninstall-registration-removes-only-owned-values-and-empty-product-key' {
 $iss=Get-Content -LiteralPath (Join-Path $repo 'installer\AgentRecorder.iss') -Raw
 $block=[regex]::Match($iss,'(?ms)^\[Registry\]\s*$(.*?)^\[').Groups[1].Value
 $entries=@($block -split '\r?\n' | Where-Object {$_ -match '^Root:'})
 if($entries.Count -ne 6 -or $entries[0] -cne 'Root: HKCU; Subkey: "{#ProductKey}"; Flags: uninsdeletekeyifempty'){
  throw 'Empty-key deletion must be logged first and scoped to the dedicated product key'
 }
 $owned=@('Product','InstallLocation','CliPath','AgentInstructions','DataDirectory')
 for($i=0;$i -lt $owned.Count;$i++){
  $prefix='Root: HKCU; Subkey: "{#ProductKey}"; ValueType: string; ValueName: "'+$owned[$i]+'"; '
  if(-not $entries[$i+1].StartsWith($prefix,[StringComparison]::Ordinal) -or -not $entries[$i+1].EndsWith('; Flags: uninsdeletevalue')){
   throw 'Unexpected registry value ownership'
  }
 }
 # Model the actual entries in reverse uninstall order, without running Setup.
 foreach($scenario in @('empty','extra-value','default-value','child-key')){
  $values=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
  foreach($name in $owned){[void]$values.Add($name)}
  if($scenario -eq 'extra-value'){[void]$values.Add('Unowned')}
  if($scenario -eq 'default-value'){[void]$values.Add('')}
  $subkeys=if($scenario -eq 'child-key'){1}else{0}
  for($i=$entries.Count-1;$i -ge 1;$i--){
   $name=[regex]::Match($entries[$i],'ValueName: "([^"]+)"').Groups[1].Value
   [void]$values.Remove($name)
  }
  $removeKey=$values.Count -eq 0 -and $subkeys -eq 0
  if($removeKey -ne ($scenario -eq 'empty')){throw "Unsafe cleanup: $scenario"}
  if($scenario -eq 'extra-value' -and -not $values.Contains('Unowned')){throw 'Unknown value lost'}
  if($scenario -eq 'default-value' -and -not $values.Contains('')){throw 'Default value lost'}
 }
}
$results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $work 'results.json') -Encoding UTF8
Write-Output "PASS $($results.Count) installer archive/inventory scenarios. Inno was not executed."
