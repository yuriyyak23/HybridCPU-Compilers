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
