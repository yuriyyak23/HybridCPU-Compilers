# HB-INT-02 / HB-INT-03: captured Csc bytes to Core v2 (opt-in)

**Status: bounded, unqualified byte transport; not compiler or source qualification.**
**Ownership:** HybridCPU-Compilers / HardwareBridge S2. Based on
HybridCPU-Compilers master 77947f82cd61ed5248c0ae48d722d7fbfdf29a9a and
CircuitWeave master 26724fc70a1cb7c403aa50d8e0f41383202a22ac.
Implements part of [the six-package plan](CircuitWeave_Pipeline_Integration_Plan.md).

## Functional addition

HB-INT-02 already produces hybridcpu.hardwarebridge.csc-closure-index/v2:
an externally *reported* Csc byte closure with 1 MiB binary chunks. Core's
independent v2 reader expects a different
circuitweave.bridge-source-closure-index/v2: compact JSON index plus JSONL
chunks and actual files. These formats had no bounded bridge.

HWIR/EvaluatedProjectGraph/csc_to_core_v2.py adds an **opt-in converter**:

1. Verify every HB captured chunk/file plus an independently supplied HB
   closureRootSha256; refuse stale, unknown or malformed records.
2. Require explicit non-hardware method root selectors in a separate JSON file;
   do not infer C# symbol bodies or synthesize hardware intent.
3. Copy only captured bytes into a new direct OS TEMP child, with safe generated
   names, per-input SHA-256 and bounds. Do not touch Golden, Compilers,
   MSBuild output, existing capture, or external sibling checkouts.
4. Emit the already supported Core v2 index wire shape (Schema, Roots, Chunks,
   ClosureSha256) and sorted JSONL rows (Path, Role, Sha256, Bytes) using exact
   UTF-8 compact serialization and uppercase SHA-256; never change a wire DTO.
5. Emit bridge-export-receipt.json (schema
   hybridcpu.hardwarebridge.core-transport-receipt/v1) that binds the original
   Csc node/kind/logical path to each staged Core path. This is
   **unqualified diagnostics**, not a binding lock, signature or evidence.
6. Revalidate staged files, index/chunks and original capture post-write.
   Cancel/timeout/failed verification removes the partial output directory.

No new Csc executor, general parser, Core-owned importer, ToolBridge, RTL
candidate emitter or verification authority was created.

## Fail-closed ownership map

| HB-INT-02 kind/role | Core v2 role | Contract |
|---|---|---|
| csc-source / generator-source, primary | source | candidate source bytes only |
| csc-source / generator-source, owned-body | owned-body | asserted, not independently verified body |
| csc-source / generator-source, metadata-only | metadata-only | never promotes ownership |
| reference, analyzer, config, additional-file, app-config, source-link, csc-binary, project, assets | metadata-only | never executable body or emitted PE/PDB |

Unknown kind, role, duplicated root, missing primary source, non-UTF-8 source,
traversal/symlink or malformed SHA all refuse. Restricted ASCII explicit
selector names avoid Python versus System.Text.Json escaping disagreements.
File paths are synthetic deterministic names; the original logical path
remains in the separate unqualified receipt, **not** a Core symbol identity.

Core v2 limits: 1..32 exact roots, at most 8,192 inputs and 512 MiB total;
source <=2 MiB, metadata <=32 MiB, 256 JSONL chunks, 4 MiB each, 64 MiB
manifest and 1 MiB index. Oversized HB snapshots are Blocked, not truncated.

## Commands (PowerShell, Python 3.13+ stdlib)

Explicit roots JSON array (canonical key order):
~~~json
[{"TypeName":"Fixture","MethodName":"AddWord","MetadataToken":null,"TypeArguments":[],"MethodArguments":[]}]
~~~

~~~powershell
$tool = 'Compilers/HybridCPU_Compiler/HardwareBridge/HWIR/EvaluatedProjectGraph/csc_to_core_v2.py'
$temp = python -c 'import tempfile; print(tempfile.gettempdir())'
$out = Join-Path $temp ('hb-core-v2-' + [Guid]::NewGuid().ToString('N'))
python $tool export --capture '<existing-HB-int02-capture>' --expected-capture-sha '<64-lowercase-hex>' --roots '<roots.json>' --output $out
python $tool verify --capture '<existing-HB-int02-capture>' --expected-capture-sha '<64-lowercase-hex>' --roots '<roots.json>' --output $out
python -m unittest discover -s Compilers/HybridCPU_Compiler/HardwareBridge/HWIR/EvaluatedProjectGraph -p test_csc_to_core_v2.py -v
~~~

The Windows workflow additionally compiles **the existing production**
BridgeSegmentIndexV2 codec in an isolated .NET 11 project and compares every
JSONL/index byte from the Python converter against real writer output.
This is synthetic fixture wire parity, not actual Golden execution or Core
independent import. Actual commands, CI logs, exit and hashes must be checked
before calling even these bounded tests Passed.

## Compatibility and unresolved gates

Core CW-INT-03 importer may consume the emitted bridge-index.json, staged
workspace and *independently supplied* CoreClosureSha256. Even if that
importer returns Candidate, this cannot qualify Csc semantic ownership.

Still **Blocked**: actual approved Csc observation/PE/PDB for full Golden
HybridCPU-v2; independent Roslyn closure and Core importer over emitted
real bytes; dirty worktree qualification; Golden/RTL equivalence; FPGA/ASIC
targets and Processor top. Native EDA remains **NotRun** without separate
approval. Existing v1 sidecar, source snapshot v1, HB csc index v2, Core
index schema and Golden C# remain unchanged.
