function Get-ReleaseVersion([string]$ProjectRoot) {
    $props = [xml](Get-Content -LiteralPath (Join-Path $ProjectRoot 'Directory.Build.props') -Raw)
    $version = [string]$props.Project.PropertyGroup.Version
    if ($version -cnotmatch '\A(?:0|[1-9][0-9]*)(?:\.(?:0|[1-9][0-9]*)){2}\z') {
        throw 'Directory.Build.props must define a numeric three-component product Version.'
    }
    return $version
}

function Get-PortableVersionLabel([string]$ProductVersion, [string]$Label) {
    $prefix = "v$ProductVersion"
    if (-not $Label) { return $prefix }
    $pattern = '\A' + [regex]::Escape($prefix) + '(?:-[a-zA-Z0-9]+(?:[.-][a-zA-Z0-9]+)*)?\z'
    if ($Label -cnotmatch $pattern) {
        throw "Portable label must begin with $prefix and may append a candidate suffix."
    }
    return $Label
}

function Assert-ReleaseAssemblyVersion([string]$Assembly, [version]$ExpectedVersion) {
    $actual = [Reflection.AssemblyName]::GetAssemblyName($Assembly).Version
    if ($actual -ne $ExpectedVersion) {
        throw "Assembly version gate failed for ${Assembly}: expected $ExpectedVersion, got $actual."
    }
    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($Assembly)
    $fileVersion = [version]::new($info.FileMajorPart, $info.FileMinorPart, $info.FileBuildPart, $info.FilePrivatePart)
    $productVersion = ($info.ProductVersion -split '\+', 2)[0]
    if ($fileVersion -ne $ExpectedVersion -or $productVersion -cne $ExpectedVersion.ToString(3)) {
        throw "File/product version gate failed for ${Assembly}: got $fileVersion / $($info.ProductVersion)."
    }
}
