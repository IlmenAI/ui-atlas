param(
  [string]$Configuration = 'Debug',
  [string]$MSBuildAssembly
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('ui-atlas-viewer-build-' + [Guid]::NewGuid().ToString('N'))
$buildRoot = Join-Path $scratch 'build'
$project = Join-Path $repository 'src/UiAtlas.Core.Cli/UiAtlas.Core.Cli.csproj'
[string[]]$buildCommand = if ($MSBuildAssembly) { @($MSBuildAssembly) } else { @('msbuild') }
$recorder = Join-Path $buildRoot "bin/UiAtlas.Core.Cli/$Configuration/net10.0-windows10.0.19041.0"
$desktop = Join-Path $buildRoot "bin/UiAtlas.Core.Desktop/$Configuration/net10.0-windows10.0.19041.0"

function Build-Recorder([string]$Target) {
  & dotnet @buildCommand $project -restore "-t:$Target" -nologo -v:quiet "-p:Configuration=$Configuration" "-p:UiAtlasIsolatedBuildRoot=$buildRoot" '-p:StopRunningUiAtlasProcesses=false' '-p:UiAtlasBuildNumber=0'
  if ($LASTEXITCODE) { throw "Recorder-only $Target failed." }
}

function Assert-Viewer {
  foreach ($required in @('UIMaps.Recorder.ProviderProbe.dll','UIMaps.Recorder.ProviderProbe.deps.json','UIMaps.Recorder.ProviderProbe.runtimeconfig.json','UIMaps.Recorder.Windows.dll','UIMaps.Protocol.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $recorder "native-recorder/$required"))) { throw "Native recorder dependency missing: $required" }
  }
  $runtime = Get-Content -LiteralPath (Join-Path $recorder 'native-recorder/UIMaps.Recorder.ProviderProbe.runtimeconfig.json') -Raw | ConvertFrom-Json
  if (-not ($runtime.runtimeOptions.frameworks | Where-Object name -eq 'Microsoft.WindowsDesktop.App')) { throw 'Native recorder requires Windows Desktop runtime.' }
  foreach ($required in @('UiAtlas.Core.Desktop.dll', 'UiAtlas.Core.Desktop.runtimeconfig.json', 'UiAtlas.Core.Desktop.deps.json', 'Assets/explorer-icon-export.png')) {
    if (-not (Test-Path -LiteralPath (Join-Path $recorder "viewer/$required"))) { throw "Viewer dependency missing: $required" }
  }
  foreach ($file in Get-ChildItem -LiteralPath $desktop -Recurse -File) {
    $relative = [IO.Path]::GetRelativePath($desktop, $file.FullName)
    $staged = Join-Path $recorder "viewer/$relative"
    if (-not (Test-Path -LiteralPath $staged) -or (Get-FileHash -LiteralPath $staged).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) {
      throw "Viewer dependency differs: $relative"
    }
  }
  foreach ($file in @('UiAtlas.Core.Contracts.dll', 'UiAtlas.Core.Build.dll', 'UiAtlas.Core.Storage.dll', 'UiAtlas.Core.Reader.dll',
      'UiAtlas.Core.Mapping.dll', 'UiAtlas.Core.Mapping.Atlas.dll', 'UIMaps.Protocol.dll', 'UIMaps.Perception.Core.dll', 'UIMaps.Map.Core.dll')) {
    if ((Get-FileHash -LiteralPath (Join-Path $recorder $file)).Hash -ne (Get-FileHash -LiteralPath (Join-Path $recorder "viewer/$file")).Hash) {
      throw "Recorder/viewer mismatch: $file"
    }
  }
}

Build-Recorder Build
Assert-Viewer
# Simulate an out-of-date staged reader, then verify ordinary Build refreshes it.
Set-Content -LiteralPath (Join-Path $recorder 'viewer/UiAtlas.Core.Contracts.dll') -Value 'stale'
Build-Recorder Build
Assert-Viewer
Build-Recorder Rebuild
Assert-Viewer
Write-Output 'Passed: recorder-only Build, repeated Build repair, and Rebuild stage a complete matching viewer and native recorder.'
Write-Output "Isolated outputs: $scratch"
