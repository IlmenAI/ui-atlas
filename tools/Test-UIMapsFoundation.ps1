param([string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
$vendorRoot = Join-Path $root 'vendor\uimaps'
$manifest = Get-Content -LiteralPath (Join-Path $root 'provenance\uimaps-foundation.json') -Raw | ConvertFrom-Json
if ($manifest.format -ne 'ui-atlas.uimaps-source-import/1') { throw 'Unknown UI Maps source manifest format.' }
$known = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $manifest.files) {
    $path = [IO.Path]::GetFullPath((Join-Path $root $entry.target))
    if (-not $path.StartsWith($vendorRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "UI Maps manifest path escaped the source import: $($entry.target)"
    }
    if (-not $known.Add($path)) { throw "Duplicate UI Maps source record: $($entry.target)" }
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing UI Maps source: $($entry.target)" }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $entry.sha256) {
        throw "UI Maps source changed without a reviewed provenance update: $($entry.target)"
    }
    if ($entry.commit -notmatch '^[0-9a-f]{40}$' -or $entry.repository -notmatch '^https://github.com/(AI-Successors/uimaps-(protocol|perception|map|recorder)|IlmenAI/UI-Atlas)$') {
        throw "UI Maps source revision is not pinned: $($entry.target)"
    }
}
Get-ChildItem -LiteralPath $vendorRoot -Recurse -File | Where-Object {
    $_.FullName -notmatch '[\\/](bin|obj)[\\/]'
} | ForEach-Object {
    if ($_.Name -eq 'packages.lock.json') { return }
    if (-not $known.Contains($_.FullName)) { throw "Unreviewed file in UI Maps source import: $($_.FullName)" }
    if ($_.Extension -eq '.cs') {
        $content = Get-Content -LiteralPath $_.FullName -Raw
        if ($content -match 'namespace\s+UIMaps\.(Application|Workflow|Diagnostics|Localization)\b') {
            throw "Product implementation crossed the mapping foundation boundary: $($_.Name)"
        }
    }
}
$foundationProjects = @(Get-ChildItem -LiteralPath $vendorRoot -Recurse -Filter *.csproj)
$foundationProjects += Get-Item -LiteralPath (Join-Path $root 'src\UiAtlas.Core.Mapping\UiAtlas.Core.Mapping.csproj')
foreach ($projectFile in $foundationProjects) {
    [xml]$project = Get-Content -LiteralPath $projectFile.FullName
    foreach ($reference in $project.SelectNodes('//ProjectReference')) {
        $path = [IO.Path]::GetFullPath((Join-Path $projectFile.DirectoryName $reference.Include))
        if (-not $path.StartsWith($vendorRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Mapping foundation depends on a product implementation: $($reference.Include)"
        }
    }
    foreach ($package in $project.SelectNodes('//PackageReference')) {
        if ($package.Include -notin @('Google.Protobuf', 'Grpc.Tools', 'Microsoft.Data.Sqlite', 'System.Drawing.Common')) {
            throw "Unreviewed mapping foundation dependency: $($package.Include)"
        }
    }
}
Write-Output "UI Maps foundation boundary passed: $($known.Count) pinned files; no external implementation dependency."
