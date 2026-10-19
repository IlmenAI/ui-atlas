# MapLock Launcher Debug build: missing catalog reference

## Root cause

Visual Studio built `UiAtlas.Catalog.Contracts` as a solution project into its
normal `src/UiAtlas.Catalog.Contracts/obj/Debug` directory. MapLock then invoked
`StageAtlasMappingTools`, which builds the Atlas CLI in a separate output tree:
`.codex-out/maplock-deployment`.

That nested MSBuild invocation inherited `BuildingInsideVisualStudio=true` and
`BuildProjectReferences=false`. MSBuild consequently resolved dependency output
paths without building those dependencies. The CLI compiler expected the new
contract's reference assembly in the isolated Debug directory, where it did not
exist, and reported CS0006. Restoring NuGet packages could not create the missing
compiled reference assembly.

The failure was reproduced before the fix by invoking the production staging
target with those Visual Studio flags. The earlier command-line Release checks
built project references normally and did not exercise this IDE-specific case.

## Fix

`integration/UIMaps.SharedFoundation.targets` now treats the nested Atlas build
as a separate build graph: `BuildingInsideVisualStudio=false`,
`BuildProjectReferences=true`, and `BuildingSolutionFile=false`. It also removes
the parent's `CurrentSolutionConfigurationContents`, so solution-only dependency
selection cannot suppress an isolated Atlas dependency.

The outer MapLock solution retains Visual Studio's normal dependency scheduling.
The fix does not copy a reference DLL from another configuration or require an
initial command-line build. `AtlasMappingToolsBuildRoot` allows the staging
regression check to start with a genuinely empty output tree.

## Verification

`tools/Test-MapLockAtlasStaging.ps1` imports the production target and supplies the
Visual Studio build flags plus a solution configuration that disables the
contract project. Fresh Debug staging succeeds and contains the CLI, viewer,
native recorder and catalog contract. Removing the isolated contract reference
assembly and repeating staging recreates it successfully. Staged and built
contract DLL hashes match.

In the open Visual Studio 2026 `UIMaps.Core` solution, Debug Build succeeded
with 31 projects built, one up-to-date and zero failures. Rebuild All then
succeeded with all 32 projects built and zero failures. F5 started `MapLock.exe`
from the Launcher project's Debug output under the debugger.
The running launcher then loaded all 23 recordings from the existing Atlas
catalog. Visual Studio displayed its optimized-library Just My Code notice for
`UIMaps.Recorder.Windows.dll`; Continue Debugging proceeded successfully without
changing debugger settings.

The pre-fix reproduction log is in the UIMaps checkout at
`artifacts/atlas-debug-reference-repro.log`; the regression log is
`.codex-out/maplock-vs-staging-regression.log` in the Atlas checkout.
