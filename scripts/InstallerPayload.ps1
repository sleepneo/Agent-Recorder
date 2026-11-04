# Same file inventory drives Inno [Files] and synthetic uninstall selection tests.
function Write-InstallerPayloadIncludes {
    param([Parameter(Mandatory=$true)][string]$payload, [Parameter(Mandatory=$true)][string]$work)
    function IssQuote([string]$value) { return $value.Replace('"','""') }
    $files=[Collections.Generic.List[string]]::new()
    $hashes=[Collections.Generic.List[string]]::new()
    $hashes.Add('function VerifyGuard(Base: String): Boolean;')
    $hashes.Add('begin')
    $hashes.Add('  Result := False;')
    $inventory=@()
    foreach ($f in (Get-ChildItem -LiteralPath $payload -Recurse -File | Sort-Object FullName)) {
        $rel=$f.FullName.Substring($payload.Length+1)
        $sha=(Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $inventory+=@{Path=$rel;Length=$f.Length;Sha256=$sha}
        if ($rel.StartsWith('AgentRecorder.Cli\',[StringComparison]::Ordinal)) {
            $guardRel=$rel.Substring('AgentRecorder.Cli\'.Length)
            $dest=Split-Path $guardRel -Parent
            $files.Add('Source: "'+(IssQuote $f.FullName)+'"; DestDir: "{tmp}\guard\'+(IssQuote $dest)+'"; Flags: dontcopy noencryption')
            $pascal=$guardRel.Replace("'","''")
            $hashes.Add("  if not OrdinaryPath(Base + '\$pascal') then Exit;")
            $hashes.Add("  if CompareText(GetSHA256OfFile(Base + '\$pascal'), '$sha') <> 0 then Exit;")
        }
    }
    $hashes.Add('  Result := True;'); $hashes.Add('end;')
    foreach ($f in (Get-ChildItem -LiteralPath $payload -Recurse -File | Sort-Object FullName)) {
        $rel=$f.FullName.Substring($payload.Length+1)
        $dest=Split-Path $rel -Parent
        $files.Add('Source: "'+(IssQuote $f.FullName)+'"; DestDir: "{app}\'+(IssQuote $dest)+'"; Flags: ignoreversion')
    }
    $fileList=Join-Path $work 'payload-files.iss'
    $guardHashes=Join-Path $work 'guard-hashes.iss'
    [IO.File]::WriteAllLines($fileList,$files,[Text.UTF8Encoding]::new($true))
    [IO.File]::WriteAllLines($guardHashes,$hashes,[Text.UTF8Encoding]::new($true))
    $inventory | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $work 'payload-manifest.json') -Encoding UTF8
    return [pscustomobject]@{FileList=$fileList;GuardHashes=$guardHashes}
}
