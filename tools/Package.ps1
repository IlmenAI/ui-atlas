param([string]$Version = '2.0.0', [string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$dist = Join-Path $root 'dist'
$staging = Join-Path $dist ('.staging-' + [Guid]::NewGuid().ToString('N'))
$payload = Join-Path $staging 'ui-atlas-core'
$buildRoot = Join-Path $staging 'build'
$isolatedBuild = "-p:UiAtlasIsolatedBuildRoot=$buildRoot"
$stableRevision = @('-p:IncludeSourceRevisionInInformationalVersion=false', '-p:UiAtlasBuildNumber=0')
& (Join-Path $PSScriptRoot 'Test-UIMapsFoundation.ps1') -RepositoryRoot $root
New-Item -ItemType Directory -Force -Path $payload | Out-Null
try {
  dotnet restore (Join-Path $root 'UiAtlas.Core.slnx') --locked-mode $isolatedBuild @stableRevision
  if ($LASTEXITCODE) { throw 'Locked restore failed.' }
  dotnet build (Join-Path $root 'UiAtlas.Core.slnx') -c $Configuration --no-restore --no-incremental -p:DebugType=None -p:DebugSymbols=false -p:UseAppHost=false $isolatedBuild @stableRevision
  if ($LASTEXITCODE) { throw 'Deterministic package build failed.' }
  dotnet publish (Join-Path $root 'src\UiAtlas.Core.Cli\UiAtlas.Core.Cli.csproj') -c $Configuration --no-restore --no-build -p:PublishDir=$payload -p:DebugType=None -p:DebugSymbols=false -p:UseAppHost=false $isolatedBuild @stableRevision
  if ($LASTEXITCODE) { throw 'CLI publish failed.' }
  Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'ui-atlas.cmd') -Destination $payload
  Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-UiAtlas.ps1') -Destination (Join-Path $payload 'install.ps1')
  $desktop = Join-Path $staging 'desktop'
  dotnet publish (Join-Path $root 'src\UiAtlas.Core.Desktop\UiAtlas.Core.Desktop.csproj') -c $Configuration --no-restore --no-build -p:PublishDir=$desktop -p:DebugType=None -p:DebugSymbols=false -p:UseAppHost=false $isolatedBuild @stableRevision
  if ($LASTEXITCODE) { throw 'Desktop publish failed.' }
  Get-ChildItem -LiteralPath $desktop -Force | Copy-Item -Destination $payload -Recurse -Force
  $launcher = Join-Path $payload 'launcher'
  dotnet publish (Join-Path $root 'src\Launcher\Launcher.csproj') -c $Configuration --no-restore --no-build -p:PublishDir=$launcher -p:DebugType=None -p:DebugSymbols=false -p:UseAppHost=false $isolatedBuild @stableRevision
  if ($LASTEXITCODE) { throw 'Launcher publish failed.' }
  Copy-Item -LiteralPath (Join-Path $root 'README.md'),(Join-Path $root 'LICENSE'),(Join-Path $root 'THIRD-PARTY-NOTICES.md') -Destination $payload
  Copy-Item -LiteralPath (Join-Path $root 'schemas') -Destination $payload -Recurse
  Copy-Item -LiteralPath (Join-Path $root 'licenses') -Destination $payload -Recurse
  $foundationNotices = Join-Path $payload 'licenses\uimaps'
  New-Item -ItemType Directory -Force -Path $foundationNotices | Out-Null
  foreach ($component in @('uimaps-protocol','uimaps-perception','uimaps-map','uimaps-recorder')) {
    Copy-Item -LiteralPath (Join-Path $root "vendor\uimaps\$component\LICENSE.txt") -Destination (Join-Path $foundationNotices "$component-MIT.txt")
  }
  Copy-Item -LiteralPath (Join-Path $root 'sbom') -Destination $payload -Recurse
  Copy-Item -LiteralPath (Join-Path $root 'provenance') -Destination $payload -Recurse
  # Ship end-user mapping and interchange documentation. Investigation notes and
  # developer-only product handoffs are not part of the three-surface product.
  $packageDocs = Join-Path $payload 'docs'
  New-Item -ItemType Directory -Force -Path $packageDocs | Out-Null
  foreach ($document in @('2026-10-19-shared-atlas-library.md','2026-10-19-shared-launchers.md','2026-10-13-uimaps-foundation-rollout.md','2026-10-15-native-recorder-parity.md','2026-10-15-appmap-five-mode-parity.md','2026-10-15-office-popup-presentation.md','2026-10-15-raw-world-reconstruction.md','architecture.md','privacy.md','known-limitations.md',
      'recording-bundle-v1.md','interaction-trace.md','ui-atlas-compatibility.md','uikg-v1.md','uikg-v2.md','uikg-v4.md','uikg-v5.md','release.md','threat-model.md','qualification.md')) {
    Copy-Item -LiteralPath (Join-Path $root "docs\$document") -Destination $packageDocs
  }
  Copy-Item -LiteralPath (Join-Path $root 'SECURITY.md') -Destination $payload
  New-Item -ItemType Directory -Force -Path (Join-Path $packageDocs 'media') | Out-Null
  foreach ($thumbnail in @('ui-atlas-preview.jpg','ui-atlas-demo-preview.jpg')) {
    Copy-Item -LiteralPath (Join-Path $root "docs\media\$thumbnail") -Destination (Join-Path $packageDocs 'media')
  }
  $utf8NoBom = [Text.UTF8Encoding]::new($false)
  Get-ChildItem -LiteralPath $payload -Recurse -File | Where-Object {
    $_.Extension -in '.json','.md','.txt','.ps1' -or $_.Name -in 'LICENSE','ui-atlas.cmd'
  } | ForEach-Object {
    $content = [IO.File]::ReadAllText($_.FullName).Replace("`r`n", "`n").Replace("`r", "`n")
    if ($_.Name -eq 'ui-atlas.cmd') { $content = $content.Replace("`n", "`r`n") }
    [IO.File]::WriteAllText($_.FullName, $content, $utf8NoBom)
  }
  Get-ChildItem -LiteralPath $payload -Filter *.pdb -Recurse -File | Remove-Item -Force
  Get-ChildItem -LiteralPath $payload -Directory -Recurse | Where-Object { $_.Parent.Name -eq 'runtimes' -and $_.Name -notlike 'win-*' } | ForEach-Object {
    $candidate = [IO.Path]::GetFullPath($_.FullName)
    if (-not $candidate.StartsWith($payload + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Runtime filter escaped package root.' }
    Remove-Item -LiteralPath $candidate -Recurse -Force
  }
  $packagedSbomPath = Join-Path $payload 'sbom\ui-atlas-core.cdx.json'
  $packagedSbom = Get-Content -LiteralPath $packagedSbomPath -Raw | ConvertFrom-Json
  $oldRootRef = $packagedSbom.metadata.component.'bom-ref'
  $newRootRef = "pkg:generic/ui-atlas.core@$Version"
  $packagedSbom.metadata.component.version = $Version
  $packagedSbom.metadata.component.'bom-ref' = $newRootRef
  foreach ($dependency in $packagedSbom.dependencies) { if ($dependency.ref -eq $oldRootRef) { $dependency.ref = $newRootRef } }
  $packagedSbom | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $packagedSbomPath -Encoding utf8
  $deterministicTimestamp = [DateTime]::SpecifyKind([DateTime]::new(1980,1,1,0,0,0), [DateTimeKind]::Utc)
  Get-ChildItem -LiteralPath $payload -Recurse -File | ForEach-Object { $_.LastWriteTimeUtc = $deterministicTimestamp }

  $zipPath = Join-Path $dist "ui-atlas-core-$Version-windows.zip"
  if (Test-Path -LiteralPath $zipPath) { throw "Release archive already exists: $zipPath" }
  Add-Type -AssemblyName System.IO.Compression
  $stream = [IO.File]::Open($zipPath, [IO.FileMode]::CreateNew)
  try {
    $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $false)
    try {
      Get-ChildItem -LiteralPath $payload -Recurse -File | Sort-Object FullName | ForEach-Object {
        $entryPath = [IO.Path]::GetFullPath($_.FullName)
        $stagingPrefix = $staging.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if (-not $entryPath.StartsWith($stagingPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Package entry escaped staging root.' }
        $entryName = $entryPath.Substring($stagingPrefix.Length).Replace('\','/')
        $entry = $archive.CreateEntry($entryName, [IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime = [DateTimeOffset]::new(1980,1,1,0,0,0,[TimeSpan]::Zero)
        $input = [IO.File]::OpenRead($_.FullName)
        $output = $entry.Open()
        try { $input.CopyTo($output) } finally { $output.Dispose(); $input.Dispose() }
      }
    } finally { $archive.Dispose() }
  } finally { $stream.Dispose() }
  & (Join-Path $PSScriptRoot 'Test-Package.ps1') -Archive $zipPath
  if ($LASTEXITCODE) { throw 'Package compliance checks failed.' }
  $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
  Set-Content -LiteralPath (Join-Path $dist "ui-atlas-core-$Version-windows.sha256") -Value "$hash  $(Split-Path -Leaf $zipPath)" -Encoding ascii -NoNewline
  Write-Output $zipPath
}
finally {
  if (Test-Path -LiteralPath $staging) {
    $resolved = [IO.Path]::GetFullPath($staging)
    if ($resolved.StartsWith($dist + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
      Remove-Item -LiteralPath $resolved -Recurse -Force
    }
  }
}
