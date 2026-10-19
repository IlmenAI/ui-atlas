param([Parameter(Mandatory=$true)][string]$Archive)
$ErrorActionPreference = 'Stop'
$path = [IO.Path]::GetFullPath($Archive)
Add-Type -AssemblyName System.IO.Compression
$stream = [IO.File]::OpenRead($path)
try {
  $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Read, $false)
  try {
    $names = @($zip.Entries | ForEach-Object { $_.FullName.Replace('\','/') })
    if ($names | Where-Object { $_.EndsWith('.pdb', [StringComparison]::OrdinalIgnoreCase) }) { throw 'Package contains debug-symbol files.' }
    if ($names | Where-Object { $_.EndsWith('.exe', [StringComparison]::OrdinalIgnoreCase) }) { throw 'Framework-dependent package contains architecture-specific apphosts.' }
    foreach ($required in @('ui-atlas-core/ui-atlas.dll','ui-atlas-core/ui-atlas.cmd','ui-atlas-core/install.ps1')) {
      if ($required -notin $names) { throw "Package CLI entry is missing: $required" }
    }
    foreach ($required in @('UiAtlas.dll','UiAtlas.deps.json','UiAtlas.runtimeconfig.json','UiAtlas.Shell.dll','UiAtlas.Catalog.Contracts.dll','tools/ui-atlas.dll','tools/UiAtlas.Catalog.Contracts.dll','tools/native-recorder/UIMaps.Recorder.ProviderProbe.dll')) {
      if ("ui-atlas-core/launcher/$required" -notin $names) { throw "Launcher deployment is missing: $required" }
    }
    foreach ($required in @('UiAtlas.Core.Mapping.dll','UiAtlas.Core.Mapping.Atlas.dll','UIMaps.Protocol.dll','UIMaps.Perception.Core.dll','UIMaps.Map.Core.dll','Google.Protobuf.dll')) {
      if ("ui-atlas-core/$required" -notin $names) { throw "UI Maps foundation payload is missing: $required" }
    }
    if ($names | Where-Object { $_ -match '(?i)(UIMaps\.(Application|Workflow|Diagnostics|Localization)|grpc_csharp_plugin|protoc\.exe)' }) {
      throw 'Public Atlas package contains a product executable or build-only compiler.'
    }
    if ($names | Where-Object { $_ -match '(?i)(UiAtlas\.Core\.(Mcp|Migration|FirebirdMigration|Consumer)|FirebirdSql|ModelContextProtocol)' -and $_ -match '(?i)\.(dll|exe|deps\.json|runtimeconfig\.json)$' }) {
      throw 'Public mapping release contains a separate product or legacy integration executable.'
    }
    foreach ($required in @('UIMaps.Recorder.ProviderProbe.dll','UIMaps.Recorder.ProviderProbe.deps.json','UIMaps.Recorder.ProviderProbe.runtimeconfig.json','UIMaps.Recorder.Windows.dll','UIMaps.Protocol.dll')) {
      if ("ui-atlas-core/native-recorder/$required" -notin $names) { throw "Native provider deployment is incomplete: $required" }
    }
    # Framework-dependent publish resolves Drawing/SystemEvents from the declared
    # Windows Desktop runtime; ordinary build staging also carries NuGet copies.
    $nativeRuntimeReader = [IO.StreamReader]::new($zip.GetEntry('ui-atlas-core/native-recorder/UIMaps.Recorder.ProviderProbe.runtimeconfig.json').Open())
    try { $nativeRuntime = $nativeRuntimeReader.ReadToEnd() | ConvertFrom-Json } finally { $nativeRuntimeReader.Dispose() }
    if (-not ($nativeRuntime.runtimeOptions.frameworks | Where-Object { $_.name -eq 'Microsoft.WindowsDesktop.App' -and $_.version -eq '10.0.0' })) {
      throw 'Native recorder must declare its Windows Desktop runtime dependency.'
    }
    foreach ($component in @('uimaps-protocol','uimaps-perception','uimaps-map','uimaps-recorder')) {
      if ("ui-atlas-core/licenses/uimaps/$component-MIT.txt" -notin $names) { throw "UI Maps source notice is missing: $component" }
    }
    foreach ($required in @('ui-atlas-core/licenses/Google.Protobuf-BSD-3-Clause.txt','ui-atlas-core/provenance/uimaps-foundation.json')) {
      if ($required -notin $names) { throw "UI Maps provenance or runtime notice is missing: $required" }
    }
    $projectionEntries = @($names | Where-Object { [IO.Path]::GetFileName($_) -in @('Microsoft.Windows.SDK.NET.dll','WinRT.Runtime.dll') })
    $requiredProjectionEntries = @('ui-atlas-core/Microsoft.Windows.SDK.NET.dll','ui-atlas-core/WinRT.Runtime.dll',
      'ui-atlas-core/native-recorder/Microsoft.Windows.SDK.NET.dll','ui-atlas-core/native-recorder/WinRT.Runtime.dll')
    if (@($requiredProjectionEntries | Where-Object { $_ -notin $projectionEntries }).Count -ne 0) {
      throw 'Windows projection assemblies must include the complete root and native-worker runtime pairs.'
    }
    foreach ($entry in $projectionEntries) {
      $directory = $entry.Substring(0, $entry.LastIndexOf('/') + 1)
      if ($directory -notin @('ui-atlas-core/','ui-atlas-core/native-recorder/') -and -not $directory.StartsWith('ui-atlas-core/launcher/')) { throw "Unexpected projection location: $entry" }
      if (($directory + 'Microsoft.Windows.SDK.NET.dll') -notin $names -or ($directory + 'WinRT.Runtime.dll') -notin $names) { throw "Incomplete Windows projection pair: $entry" }
    }
    $depsEntry = $zip.GetEntry('ui-atlas-core/ui-atlas.deps.json')
    if ($null -eq $depsEntry) { throw 'Package runtime dependency manifest is missing.' }
    $depsReader = [IO.StreamReader]::new($depsEntry.Open())
    try { $depsText = $depsReader.ReadToEnd() } finally { $depsReader.Dispose() }
    foreach ($requiredDependency in @('runtimepack.Microsoft.Windows.SDK.NET.Ref/10.0.19041.57','Microsoft.Windows.SDK.NET.dll','WinRT.Runtime.dll')) {
      if ($depsText.IndexOf($requiredDependency, [StringComparison]::Ordinal) -lt 0) { throw "Windows OCR runtime dependency is not pinned in ui-atlas.deps.json: $requiredDependency" }
    }
    if ($names | Where-Object { $_ -match '/runtimes/([^/]+)/' -and $Matches[1] -notlike 'win-*' }) { throw 'Package contains a non-Windows native runtime.' }
    foreach ($required in @('ui-atlas-core/licenses/Apache-2.0.txt','ui-atlas-core/licenses/CsWinRT-MIT.txt','ui-atlas-core/licenses/Interop.UIAutomationClient-MIT.txt','ui-atlas-core/licenses/Microsoft-Windows-SDK-NET-NOTICE.txt','ui-atlas-core/licenses/Microsoft.Data.Sqlite-MIT.txt','ui-atlas-core/licenses/SQLite-public-domain.txt','ui-atlas-core/THIRD-PARTY-NOTICES.md','ui-atlas-core/sbom/ui-atlas-core.cdx.json','ui-atlas-core/provenance/files.csv')) {
      if ($required -notin $names) { throw "Package compliance entry is missing: $required" }
    }
    foreach ($assemblyEntry in @($zip.Entries | Where-Object { $_.FullName -eq 'ui-atlas-core/ui-atlas.dll' -or $_.FullName -match '^ui-atlas-core/(samples/[^/]+/)?UiAtlas\.Core\.[^/]+\.dll$' })) {
      $memory = New-Object IO.MemoryStream
      try {
        $input = $assemblyEntry.Open()
        try { $input.CopyTo($memory) } finally { $input.Dispose() }
        $bytes = $memory.ToArray()
        $ascii = [Text.Encoding]::ASCII.GetString($bytes)
        $utf16 = [Text.Encoding]::Unicode.GetString($bytes)
        # Require a printable path tail so arbitrary IL bytes such as `r:\0`
        # cannot be mistaken for a source path. Real PDB/source paths are much
        # longer and are encoded as either UTF-8/ASCII or UTF-16LE strings.
        $driveRootPath = '[A-Za-z]:\\[ -~]{6,}'
        if ($ascii -match $driveRootPath -or $utf16 -match $driveRootPath) { throw "Managed package payload contains a drive-root build path: $($assemblyEntry.FullName)" }
      } finally { $memory.Dispose() }
    }
    $sbomEntry = $zip.GetEntry('ui-atlas-core/sbom/ui-atlas-core.cdx.json')
    $reader = [IO.StreamReader]::new($sbomEntry.Open())
    try { $sbom = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    $refs = @($sbom.components | ForEach-Object { $_.'bom-ref' })
    foreach ($requiredRef in @('pkg:nuget/Microsoft.Windows.SDK.NET.Ref@10.0.19041.57','pkg:nuget/Microsoft.Windows.CsWinRT@2.2.0')) {
      if ($requiredRef -notin $refs) { throw "Windows OCR runtime is missing from the SBOM: $requiredRef" }
    }
    foreach ($dependency in $sbom.dependencies) {
      if ($dependency.ref -ne $sbom.metadata.component.'bom-ref' -and $dependency.ref -notin $refs) { throw 'SBOM dependency ref is dangling.' }
      foreach ($child in $dependency.dependsOn) { if ($child -notin $refs) { throw 'SBOM dependency child is dangling.' } }
    }
  } finally { $zip.Dispose() }
} finally { $stream.Dispose() }
Write-Output 'Package compliance checks passed.'
