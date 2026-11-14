# Semantic source inventory (diagnostic, not HWIR)

`HardwareBridge.SemanticInventory.csproj` reads a byte-pinned MSBuild evaluation,
checks every source hash and groups active class/struct declarations by Roslyn
symbol. It records declaration coordinates, attributes, explicit members,
resolved type-syntax references and separate unqualified stage statuses. The
MSBuild `Using` items are reconstructed as a synthetic global-usings tree;
the compiler assemblies come from the SDK used to build this tool. The input
and output are never allowed to overlap, output under the model repository or
through a linked directory is refused, and existing output is never replaced.

This is **not the HardwareBridge HWIR path or a complete semantic closure**:
The full set of source generators, the project's resolved exact metadata references, complete
call/dataflow dependencies, and architectural classification are not yet implemented.
Optional ProjectReference compilations use independently evaluated source snapshots;
MSBuild global-property propagation and imported build inputs are not yet qualified. Runtime framework assemblies are
recorded with hashes but are not claimed to match MSBuild's resolved graph.
Compilation errors are grouped in the output, and source attributes/dependencies
can remain unresolved. `incomplete-unqualified` is unconditional. The result
must not be published as elaborated modules or verification evidence.

To reproduce on this machine, first run the Studio checkout's
`eng/Capture-HybridCpuSourceBytes.ps1 -Output <new-path-under-Studio-eng>`; it refuses to overwrite a capture. From this HardwareBridge directory run:

```powershell
dotnet run --project SemanticInventory/HardwareBridge.SemanticInventory.csproj -- `
  'C:\Users\Yuriy\Desktop\CircuitWeave-Studio-main\eng\hybridcpu-debug-source-bytes.json' `
  'C:\Users\Yuriy\Desktop\CircuitWeave-Studio-main\eng\new-inventory.json'
powershell -NoProfile -ExecutionPolicy Bypass -File Tests/SemanticInventory.Tests.ps1
```

Select a new output path for each run. A changed source file fails closed; capture
a new byte inventory before rerunning. The test uses isolated temporary sources.

To include the transitive project sources, first capture a new graph in an allowed
output directory, then pass it to the inventory command:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File SemanticInventory/CaptureProjectGraph.ps1 `
  -RootInventory 'C:\Users\Yuriy\Desktop\CircuitWeave-Studio-main\eng\hybridcpu-debug-source-bytes.json' `
  -Output 'C:\Users\Yuriy\Desktop\CircuitWeave-Studio-main\eng\new-project-graph.json'
dotnet run --project SemanticInventory/HardwareBridge.SemanticInventory.csproj -- `
  'C:\Users\Yuriy\Desktop\CircuitWeave-Studio-main\eng\hybridcpu-debug-source-bytes.json' `
  'C:\Users\Yuriy\Desktop\CircuitWeave-Studio-main\eng\new-project-inventory.json' `
  --project-graph 'C:\Users\Yuriy\Desktop\CircuitWeave-Studio-main\eng\new-project-graph.json'
```

Project files and all evaluated Compile sources are SHA-256 pinned. Reference
compilations remain distinct, and their diagnostics are included separately.
The graph is a set of independent Debug evaluations, not a proven design-time
MSBuild build; conditional property propagation, imported files, analyzers,
the full generator set, and exact framework/package references still need qualification.

An optional `--sdk-json-generator <manifest.json>` after `--project-graph` runs
only `System.Text.Json.SourceGeneration.dll` under the installed .NET reference
pack. Its manifest schema is `hardwarebridge.sdk-json-generator/v1` with absolute
`path` and `sha256`. The tool checks both before loading, captures generated
source text, UTF-8 hashes, generator identity and diagnostics, and includes the
generated syntax trees in symbol merging. This explicit selection does not prove
that the full MSBuild generator set or generator options were reproduced. The
HybridCPU example manifest is `eng/hybridcpu-sdk-json-generator.json` in Studio;
run with a new output path and append:

```powershell
--sdk-json-generator 'C:\Users\Yuriy\Desktop\CircuitWeave-Studio-main\eng\hybridcpu-sdk-json-generator.json'
```

Each explicit member now has a documentation-comment identity (including overload
signature), and each partial declaration identifies its own direct members. The
member dependency projection records resolved calls, constructions and field,
property or event references, with the first observed source location per edge.
It intentionally does not claim complete dynamic dispatch, implicit operations,
all occurrences or dataflow. Nested types have their own entries. Root assembly
identity comes from evaluated `AssemblyName` when present; older snapshots without
that property use an explicitly synthetic `HardwareBridge.Inventory` identity.

`eng/Create-HybridCpuCoverageIndex.py` in the Studio checkout produces a compact
source-only index from the v2 report and records its SHA-256. The index labels all
roles and hardware stages as unqualified; it is not yet integrated into the Studio
manifest or loader. Its present input and output names are fixed and output is
create-new.

## Intel 8080 adapter

`Intel8080/Invoke-Intel8080Analysis.ps1` is the source-only entry point for the emulator. It selects the seven expected `.cs` files, rejects reparse-point paths and existing outputs, and writes only to `CircuitWeave-main/artifacts/8080/bridge` by default. `HardwareBridge.Intel8080.csproj` parses with the SDK Roslyn assemblies without compiling or executing emu8080.Core. It emits a JSON syntax inventory and Markdown report, including literal dictionary dispatch keys and source locations.

Example (choose another new output directory for each run):

```powershell
& .\Intel8080\Invoke-Intel8080Analysis.ps1 `
  -SourceRoot 'C:\Users\Yuriy\Desktop\8080-emulator-net-master\emu8080.Core' `
  -OutputDirectory 'C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\8080\bridge-next'
```

This adapter does not invoke SemanticInventory's HybridCPU byte-inventory contract. It parses selected files and creates an in-memory Roslyn compilation against SDK trusted platform assemblies to bind method and member symbols; unresolved-reference/compiler diagnostics are retained. It does not emit an assembly, build or execute the input project, perform HWIR/MLIR lowering, emit SystemVerilog, or functionally verify instructions. Its `memberDependencies` list records direct method calls, object creations and field/property/event references with source/target locations where Roslyn resolves them. It is an occurrence projection, not complete dataflow or dynamic dispatch. The transferred project graph and full semantic closure tools remain diagnostics for explicitly supplied MSBuild/Csc captures; `HardwareBridge.Hwir` adapters are not copied because their admitted operations and bindings are HybridCPU-specific.

## Intel 8080 project/import inventory

`ProjectInventory/HardwareBridge.Intel8080.ProjectInventory.csproj` ports the generic evaluation-only MSBuild import inventory for this source project. The opt-in wrapper `Intel8080/Invoke-Intel8080ProjectInventory.ps1` records the selected Debug evaluation's project properties, package/project references, Compile items, and hashes of observed imported/project files. The wrapper only writes a new file under `CircuitWeave-main/artifacts/8080`; existing outputs and linked output paths are refused.

This evaluates the project using the installed SDK but does not restore, build, or execute targets from `emu8080.Core`. Evaluation output is configuration-specific and unqualified: it does not establish inactive conditional imports, the resolved compiler reference graph, analyzer/generator closure, runtime behavior, or input freshness after capture. Keep this as a separate explicit action; the source-only seven-file analysis does not evaluate the project automatically.

```powershell
& .\Intel8080\Invoke-Intel8080ProjectInventory.ps1 `
  -ProjectPath 'C:\Users\Yuriy\Desktop\8080-emulator-net-master\emu8080.Core\emu8080.Core.csproj' `
  -OutputFile 'C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\8080\project-inventory-next.json'
```

## Intel 8080 editable CircuitWeave template export

`Intel8080/Export-Intel8080Template.ps1` ports the source-evidence-to-template publishing step for the 8080 adapter. It consumes an inventory from `Invoke-Intel8080Analysis.ps1`, optionally includes the evaluation-only project inventory, and records selected types, source-mapped dependency occurrences, dispatch observations, confirmed clock/memory/I/O/IRQ contracts, and the current partial RTL status. Before export it checks that the bridge RTL files match the `artifacts/8080/rtl` link targets by SHA-256. It writes only a new `.cwtemplate.json` inside the allowed artifacts root and refuses overwrite or linked paths.

The export is an editable partial model; it does not mean the RTL is complete or verified. No MLIR lowering is claimed. Example using fresh input and output paths:

```powershell
& .\Intel8080\Invoke-Intel8080Analysis.ps1 -OutputDirectory `
  'C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\8080\new-analysis'
& .\Intel8080\Export-Intel8080Template.ps1 `
  -InventoryPath 'C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\8080\new-analysis\source-inventory.json' `
  -ProjectInventoryPath 'C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\8080\project-inventory.json' `
  -OutputFile 'C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\8080\new-hardware-model.cwtemplate.json'
```
