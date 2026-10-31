param([Parameter(Mandatory=$true)][string]$UIMapsRoot)
$ErrorActionPreference = 'Stop'
$atlas = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$core = [IO.Path]::GetFullPath($UIMapsRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
if (-not (Test-Path -LiteralPath (Join-Path $core 'UIMaps.Core.slnx'))) { throw 'UIMaps integration root marker is missing.' }
if ($core -eq $atlas -or $core.StartsWith($atlas + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'The proprietary consumer must be outside the public Atlas repository.' }
& (Join-Path $PSScriptRoot 'Test-UIMapsFoundation.ps1') -RepositoryRoot $atlas
$integration = Join-Path $core 'Directory.Build.Integration.targets'
$content = [IO.File]::ReadAllText($integration)
$settings = Join-Path $core 'Directory.Build.Atlas.targets'
$relative = [IO.Path]::GetRelativePath($core, $atlas).Replace('/', '\')
$escaped = [Security.SecurityElement]::Escape($relative)
$template = @'
<Project>
  <PropertyGroup>
    <UIMapsIntegrationRoot>$(MSBuildThisFileDirectory)</UIMapsIntegrationRoot>
    <UIAtlasFoundationRoot Condition="'$(UIAtlasFoundationRoot)' == ''">$(MSBuildThisFileDirectory)__ATLAS__</UIAtlasFoundationRoot>
  </PropertyGroup>
  <Import Project="$(UIAtlasFoundationRoot)\integration\UIMaps.SharedFoundation.targets" />
</Project>
'@
[IO.File]::WriteAllText($settings, $template.Replace('__ATLAS__', $escaped))
$import = '  <Import Project="$(MSBuildThisFileDirectory)Directory.Build.Atlas.targets" />'
if (-not $content.Contains('Directory.Build.Atlas.targets')) {
  [IO.File]::WriteAllText($integration, $content.Replace('</Project>', $import + "`r`n</Project>"))
}
# Split only the three shared OCR contracts from private grid algorithms. The
# conditional keeps standalone component builds working before/without integration.
$gridPath = Join-Path $core 'uimaps-perception\src\UIMaps.Perception.Core\Grid\GridAnalysisEngine.cs'
$grid = [IO.File]::ReadAllText($gridPath)
if (-not $grid.Contains('UIMAPS_SHARED_FOUNDATION')) {
  $record = 'public sealed record GridOcrToken'
  $model = 'public interface IGridModelProvider'
  $exception = 'public sealed class GridAnalysisException'
  $engine = 'public sealed partial class GridAnalysisEngine'
  foreach ($marker in @($record, $model, $exception, $engine)) { if (-not $grid.Contains($marker)) { throw "Unrecognized grid contract boundary: $marker" } }
  $grid = $grid.Replace($record, "#if !UIMAPS_SHARED_FOUNDATION`r`n" + $record).Replace($model, "#endif`r`n`r`n" + $model)
  $grid = $grid.Replace($exception, "#if !UIMAPS_SHARED_FOUNDATION`r`n" + $exception).Replace($engine, "#endif`r`n`r`n" + $engine)
  [IO.File]::WriteAllText($gridPath, $grid)
}
$workflowSource = Join-Path $core 'uimaps-protocol\src\UIMaps.Protocol\Workflow\RecordingContracts.cs'
if (Test-Path -LiteralPath $workflowSource) {
  $contracts = Join-Path $core 'uimaps-task-contracts'
  New-Item -ItemType Directory -Force -Path $contracts | Out-Null
  $project = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><PackageId>UIMaps.Task.Contracts</PackageId><Version>1.0.0</Version><IsPackable>false</IsPackable></PropertyGroup>
  <ItemGroup><Compile Include="..\uimaps-protocol\src\UIMaps.Protocol\Workflow\RecordingContracts.cs" /></ItemGroup>
</Project>
'@
  [IO.File]::WriteAllText((Join-Path $contracts 'UIMaps.Task.Contracts.csproj'), $project)
  [IO.File]::WriteAllText((Join-Path $contracts 'LICENSE.txt'), "Copyright (c) AI Successors Corp.`r`nAll rights reserved. Proprietary workflow contracts.`r`n")
}
# Normal developer builds retain their existing process cleanup. Isolated
# qualification can opt out without terminating an attended customer session.
$targetFiles = @($integration) + @(Get-ChildItem -LiteralPath $core -Directory -Filter 'uimaps-*' | ForEach-Object {
  $candidate = Join-Path $_.FullName 'Directory.Build.targets'
  if (Test-Path -LiteralPath $candidate) { $candidate }
})
foreach ($targetFile in $targetFiles) {
  $original = [IO.File]::ReadAllText($targetFile)
  $updated = [regex]::Replace($original, '(?s)(<Target\s+Name="Stop[^"\r\n]*"[^>]*?Condition=")([^"]*)(")', {
    param($match)
    if ($match.Groups[2].Value.Contains('UIMapsStopRunningComponents')) { return $match.Value }
    return $match.Groups[1].Value + $match.Groups[2].Value + ' and ''$(UIMapsStopRunningComponents)'' != ''false''' + $match.Groups[3].Value
  })
  if ($updated -cne $original) { [IO.File]::WriteAllText($targetFile, $updated) }
}
Write-Output 'Private consumer connected to public native Recorder, Protocol, Perception and Map sources. App builds/publishes stage Atlas tools. Private app launcher and Map workspace-import dispatch must also be present; see the rollout document.'
