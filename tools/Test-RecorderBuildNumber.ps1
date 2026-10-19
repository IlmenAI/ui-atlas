$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$scratch = Join-Path $env:TEMP ('ui-atlas-build-number-test-' + [Guid]::NewGuid().ToString('N'))
$buildRoot = Join-Path $scratch 'build'
$counter = Join-Path $scratch 'counter.txt'
$project = Join-Path $repository 'src\UiAtlas.Core.Cli\UiAtlas.Core.Cli.csproj'
$properties = @("-p:UiAtlasIsolatedBuildRoot=$buildRoot", "-p:UiAtlasBuildCounterPath=$counter", '-p:StopRunningUiAtlasProcesses=false')

function Invoke-NumberedBuild([string]$Target, [string]$Configuration, [string[]]$Extra = @()) {
  & dotnet msbuild $project -nologo -v:quiet "-t:$Target" "-p:Configuration=$Configuration" @properties @Extra
  if ($LASTEXITCODE) { throw "$Target failed." }
}
function Assert-Banner([string]$Configuration, [long]$Expected) {
  $executable = Join-Path $buildRoot "bin\UiAtlas.Core.Cli\$Configuration\net10.0-windows10.0.19041.0\ui-atlas.exe"
  $banner = & $executable help | Select-Object -First 1
  if ($banner -ne "UI Atlas Core 2.0.$Expected") { throw "Incorrect embedded build number: $banner" }
}

& dotnet restore $project --locked-mode @properties -v:quiet
if ($LASTEXITCODE) { throw 'Isolated restore failed.' }
Invoke-NumberedBuild Build Debug
Assert-Banner Debug 1
Invoke-NumberedBuild Build Debug
Assert-Banner Debug 2
Invoke-NumberedBuild Rebuild Debug
Assert-Banner Debug 3
Invoke-NumberedBuild Build Release
Assert-Banner Release 4
Invoke-NumberedBuild GenerateRecorderBuildNumber Debug @('-p:DesignTimeBuild=true')
if ((Get-Content -LiteralPath $counter -Raw).Trim() -ne '4') { throw 'Design-time build incremented the counter.' }

# A fixed number supports reproducible release packaging without changing
# the development counter. Two compilations must produce the same binary.
Invoke-NumberedBuild Build Debug @('-p:UiAtlasBuildNumber=99')
Assert-Banner Debug 99
$assembly = Join-Path $buildRoot 'bin\UiAtlas.Core.Cli\Debug\net10.0-windows10.0.19041.0\ui-atlas.dll'
$firstHash = (Get-FileHash -LiteralPath $assembly).Hash
Invoke-NumberedBuild Build Debug @('-p:UiAtlasBuildNumber=99')
if ((Get-FileHash -LiteralPath $assembly).Hash -ne $firstHash) { throw 'Fixed-number builds are not deterministic.' }
if ((Get-Content -LiteralPath $counter -Raw).Trim() -ne '4') { throw 'Fixed-number build changed the development counter.' }
Write-Output 'Passed: repeated Build increments, Rebuild persistence, shared Debug/Release counter, design-time skip, and deterministic fixed-number builds.'
Write-Output "Isolated test outputs: $scratch"
