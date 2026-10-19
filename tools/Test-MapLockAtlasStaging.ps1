param([string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('atlas-maplock-stage-' + [Guid]::NewGuid().ToString('N'))
$buildRoot = Join-Path $scratch 'build'
$staged = Join-Path $scratch 'staged'
$harness = Join-Path $scratch 'UIMaps.Application.proj'
New-Item -ItemType Directory -Path $scratch | Out-Null
function Xml([string]$value) { [Security.SecurityElement]::Escape($value) }
# Import the real staging target, without depending on the private MapLock repo.
@"
<Project>
  <PropertyGroup>
    <UIAtlasFoundationRoot>$(Xml $repository)</UIAtlasFoundationRoot>
    <AtlasMappingToolsBuildRoot>$(Xml $buildRoot)</AtlasMappingToolsBuildRoot>
    <TargetDir>$(Xml $staged)\</TargetDir>
  </PropertyGroup>
  <Import Project="$(Xml (Join-Path $repository 'integration/UIMaps.SharedFoundation.targets'))" />
</Project>
"@ | Set-Content -LiteralPath $harness -Encoding utf8
$contractProject = Join-Path $repository 'src/UiAtlas.Catalog.Contracts/UiAtlas.Catalog.Contracts.csproj'
# A solution-only reference decision must not suppress the isolated dependency.
$solutionConfiguration = '<SolutionConfiguration><ProjectConfiguration Project="{12C12054-ACBF-4D32-98AC-CFE96AFA0362}" AbsolutePath="' + (Xml $contractProject) + '" BuildProjectInSolution="False">' + $Configuration + '|AnyCPU</ProjectConfiguration></SolutionConfiguration>'
$properties = @("-p:Configuration=$Configuration", '-p:BuildingInsideVisualStudio=true', '-p:BuildProjectReferences=false',
    '-p:BuildingSolutionFile=true', "-p:CurrentSolutionConfigurationContents=$solutionConfiguration", '-p:UiAtlasBuildNumber=0')
function Stage-Atlas {
    & dotnet msbuild $harness -t:StageAtlasMappingTools -nologo -v:minimal @properties
    if ($LASTEXITCODE) { throw 'Atlas staging failed under Visual Studio build flags.' }
}
function Assert-Deployment {
    foreach ($file in @('ui-atlas.dll', 'ui-atlas.runtimeconfig.json', 'ui-atlas.deps.json', 'UiAtlas.Catalog.Contracts.dll',
        'viewer/UiAtlas.Core.Desktop.dll', 'viewer/UiAtlas.Catalog.Contracts.dll', 'native-recorder/UIMaps.Recorder.ProviderProbe.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $staged "atlas/$file"))) { throw "Missing staged dependency: $file" }
    }
    $built = Join-Path $buildRoot "bin/UiAtlas.Catalog.Contracts/$Configuration/net10.0/UiAtlas.Catalog.Contracts.dll"
    if ((Get-FileHash -LiteralPath $built).Hash -ne (Get-FileHash -LiteralPath (Join-Path $staged 'atlas/UiAtlas.Catalog.Contracts.dll')).Hash) {
        throw 'Staged catalog contract differs from the isolated build.'
    }
}
Stage-Atlas
Assert-Deployment
# Reproduce the missing-reference state without deleting any checkout outputs.
$reference = Join-Path $buildRoot "obj/UiAtlas.Catalog.Contracts/$Configuration/net10.0/ref/UiAtlas.Catalog.Contracts.dll"
if (-not (Test-Path -LiteralPath $reference)) { throw 'Isolated reference assembly was not built.' }
Remove-Item -LiteralPath $reference
Stage-Atlas
Assert-Deployment
if (-not (Test-Path -LiteralPath $reference)) { throw 'An incremental stage did not repair the missing reference assembly.' }
Write-Output 'Passed: fresh VS-context staging and missing-reference repair include the full Atlas dependency graph.'
Write-Output "Isolated outputs: $scratch"
