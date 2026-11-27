# HardwareBridge bounded AddWord HWIR path

This is the legacy migration profile. The portable HWIR project leaves `EnableBridgeHwirLegacyFixtures` off. Explicit opt-in uses four pinned CircuitWeave references, separate from the production software Companion. Where a fixture calls the same Core lowering as the comparison, it cannot establish independent implementations. See [migration](Migration/README.md) and [current gates](../Docs/current/STATUS.md).

`EvaluatedProjectGraph` captures MSBuild's own evaluated `ProjectGraph` nodes,
global properties, Compile paths, evaluated reference/analyzer items, assets-file
hashes and edges for Debug/EnableInternalTestHooks. These items are only the
evaluation-time set; target-added references and generators may be missing.
It executes no targets and does not resolve references or generators. Output
must be a new file outside the model and compiler trees. Its read-only fixture
checks `GlobalPropertiesToRemove` and conditional source selection:

```powershell
pwsh -File HWIR/EvaluatedProjectGraph/Test-EvaluatedProjectGraph.ps1 -SdkDirectory '<InstalledNet11SdkRoot>'
```

`SemanticClosureAudit` parses captured root files using the target-time defines,
byte-checked reference images from an isolated build, and emitted generated
sources. It reports source type/member identities and compilation diagnostics;
project references remain isolated binaries and the result is not evidence of
the original build or hardware elaboration. Generated files are read only from
the supplied TEMP directory. Source captures, references and global-usings
bytes are rechecked before use.

`BridgeHwirModelInventory` also checks the complete supplied source-index
projection for duplicate semantic type/member identities, declaration coordinates
and member ownership. CircuitWeave checks the projection against the cited Roslyn
report and captured bytes before publishing the resulting bridge summary. This
remains source-only metadata: it does not re-resolve symbols, prove all MSBuild
inputs, or produce Processor HWIR modules.

## Root MSBuild import inventory

`ImportInventory/ImportInventory.csproj` evaluates one project with the selected
SDK and captures its active imports, `Compile` paths and selected properties;
it executes no targets. The CLI can check this evidence using `--msbuild-imports`
alongside a matching source capture, semantic report and project graph. The list
is source provenance, not an elaborated design, and does not cover conditional imports absent in the selected configuration,
generator closure or concurrent changes during evaluation.

Supplying an optional fourth argument containing the captured project graph also
records each referenced project with a separate Debug evaluation. The producer
checks graph identity, source paths, selected properties and observed import bytes;
this does not prove complete ProjectReference global-property propagation.

```powershell
dotnet run --project ImportInventory/ImportInventory.csproj -- `
  '<GoldenRoot>/HybridCPU_ISE\HybridCPU_ISE.csproj' `
  '<InstalledNet11SdkRoot>' `
  '<ExternalOutputRoot>/new-root-imports.json'
```

The output must be a new file outside the model tree. The SDK path must match
the SDK used for the source capture. Run `ImportInventory/ImportInventory.Tests.ps1`
with `-SdkDirectory` for the isolated evaluation fixture.

`HardwareBridge.Hwir.csproj` is an opt-in bridge project under HardwareBridge.
It freezes original source and runtime CoreLib bytes, invokes the existing pinned
`GoldenAddWordCandidateCompiler`, captures the existing BridgeSourceSnapshotV1
source/root identity, and revalidates the returned scalar HWIR. CircuitWeave CLI
uses this path for both `candidate addword` and `studio publish-addword`.

The source snapshot v1 bounds each reference image at 32 MiB and aggregate capture
at 64 MiB. The current CoreLib also has an independent SHA-256 recorded by the
bounded AddWord adapter; the source snapshot itself does not contain that reference,
while the exact frozen image is passed to the frontend. The compiler
checks the pinned Golden Git blob, binding, selected methods, owned closure and
metadata against the loaded CoreLib. The bridge snapshot and metadata hashes are
published in a `bridgeProvenance` artifact as producer-reported candidate data.
This is not a binding authorization, full-model import, compiler-independent
attestation, RTL emission or equivalence proof. The existing compiler/producer
coordinator is not used by this bounded CLI path.

The project defaults to a portable, source-snapshot-only DTO assembly without
any Core ProjectReference. To enable the old source-linked BridgeHwir regression
compilers, set EnableBridgeHwirLegacyFixtures=true and provide the reviewed Core
root, frontend project, exact HEAD SHA and .NET SDK version as described in
[HB-INT-01 pinned opt-in migration](../Docs/HB_INT_01_Pinned_OptIn.md).
No sibling directory name is inferred. Golden/ISE source is never an output tree.
For the current Core launcher and compatibility evidence, see the [status register](../Docs/current/STATUS.md).
Run the **original bounded P14 fixture suite** only through the explicit
revision-checked compatibility launcher in the Core checkout:

```bash
bash eng/cw-int-02-bridge-compat.sh /absolute/path/to/bridge-checkout <reviewed-bridge-40-hex> <reviewed-core-40-hex>
```

The default Core-only P14 command tests the typed `MissingBridge` refusal,
not source/RTL vectors. Other older smoke projects that still embed a sibling
BridgeHwir reference require migration and are **NotRun** in the portable
build; they are not production dependencies. No source file is modified.

## SubWord candidate

The same pinned blob contains `ScalarAluOps.SubWord` and the shared
`SignExtendWord` helper. `CompileSubWord` admits a distinct proposed binding and
lowers their unchanged Roslyn operations into `scalar_subw`. It checks the same
closure and scalar HWIR rules, including the exact subtraction wiring. It does
not imply active working-tree Processor integration.

From CircuitWeave-main, with an existing empty workspace:

```powershell
$workspace = Join-Path $env:TEMP 'cw-subword-hwir-bridge'
New-Item -ItemType Directory -Force $workspace | Out-Null
dotnet run --project src/CircuitWeave.Cli -- studio publish-subword `
  --golden-source (Resolve-Path tests/CircuitWeave.P14.Cli/fixtures/ScalarAluOps.golden.txt).Path `
  --manifest (Resolve-Path samples/HybridCPU/subword.bindings.v0.2.json).Path `
  --output $workspace
```

Open `$workspace/studio.project.json` in current Studio. The candidate is
structural only: no generated RTL or independent equivalence is claimed.


## Captured working-tree AddWord

`studio publish-working-addword` reads the exact raw `ScalarAluOps.cs` bytes
listed in the selected MSBuild source capture. It validates the compact index
against its semantic report and checks all enumerated capture, graph, runtime
metadata and generator input bytes before publication. The original proposed
AddWord binding is used only as a scalar contract; the source commit is replaced
with the read-only Git HEAD as **base context**, not a clean-tree assertion. The
bridge records `sourceKind=capturedWorkingTree`, raw SHA-256, capture SHA-256,
base HEAD and projection identity. The source-map `commit` is this base HEAD and
must be interpreted with the accompanying `bridgeProvenance`; it does not by
itself identify the dirty working bytes. Studio checks the capture linkage and
displays the distinction. This remains a candidate, not a full Processor import.

```powershell
$workspace = Join-Path $env:TEMP 'cw-working-addword-v2'
New-Item -ItemType Directory -Force $workspace | Out-Null
dotnet run --project src/CircuitWeave.Cli -- studio publish-working-addword `
  --working-source '<GoldenRoot>/HybridCPU_ISE\CloseToHSL\Core\Execution\Scalar\ALU\ScalarAluOps.cs' `
  --git-root '<GoldenRoot>' `
  --manifest (Resolve-Path samples/HybridCPU/bindings.v0.2.json).Path --output $workspace `
  --source-inventory '<ExternalOutputRoot>/hybridcpu-debug-source-coverage-index-v2.json' `
  --inventory-report '<ExternalOutputRoot>/hybridcpu-debug-member-dependencies-v2.json' `
  --source-capture '<ExternalOutputRoot>/hybridcpu-debug-source-bytes-v2.json' `
  --project-graph '<ExternalOutputRoot>/hybridcpu-debug-project-graph-v2.json'
powershell -NoProfile -ExecutionPolicy Bypass -File tests/CircuitWeave.P14.Cli/CapturedWorkingTree.Tests.ps1
```

Any changed captured input is refused. Re-capture and regenerate the inventory
after intentional source changes. The capture does not prove the full MSBuild
property/generator/reference closure or subsequent source freshness.

For captured working-tree SubWord use the same command and input paths, with
`studio publish-working-subword`, a distinct output directory, and
`samples/HybridCPU/subword.bindings.v0.2.json` as the manifest. This creates a
separate `scalar_subw` candidate and retains the same source-capture linkage.
The command-line test above checks successful publication and stale-capture
refusal for both word operations; it does not claim RTL verification.

`CompareProjectReferenceProperties.ps1` repeats evaluation of the captured graph
with `Configuration` and `EnableInternalTestHooks` propagated across reference
edges, respecting evaluated `GlobalPropertiesToRemove`. It rechecks the saved
project and source bytes, then records differences in `Compile`, captured
properties and global usings. It does not execute project targets or establish
framework negotiation, all global properties, imports, generators or resolved
references. Example (choose a new output path):

```powershell
& '<BridgeRoot>/Compilers/HybridCPU_Compiler/HardwareBridge/HWIR\CompareProjectReferenceProperties.ps1' `
  -RootInventory '<ExternalOutputRoot>/hybridcpu-debug-source-bytes-v2.json' `
  -ProjectGraph '<ExternalOutputRoot>/hybridcpu-debug-project-graph-v2.json' `
  -Output '<ExternalOutputRoot>/new-reference-comparison.json'
```
