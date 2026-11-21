# HB-INT-02/03 — opt-in pinned Csc bytes → Core v2 staging

**Scope:** read-only software observation transport, not a general C#→HWIR compiler, Golden execution, hardware root inference or independent verification. This closes one **producer-side representation gap** between HB-INT-02 `hybridcpu.hardwarebridge.csc-closure-index/v2` and HB-INT-03 / CW-INT-03 `circuitweave.bridge-source-closure-index/v2`. Both original versions and sidecar/v1 remain unchanged.

**Baseline:** HybridCPU-Compilers `master@77947f82cd61ed5248c0ae48d722d7fbfdf29a9a`; CircuitWeave `master@26724fc70a1cb7c403aa50d8e0f41383202a22ac` (2026-11-19). See [HB plan](CircuitWeave_Pipeline_Integration_Plan.md), [HB-INT-02](HB_INT_02_Segmented_Csc_Closure.md), [HB-INT-03](HB_INT_03_V2_Interchange.md), and [Core importer tests](https://github.com/yuriyyak23/CircuitWeave/tree/master/tests/CircuitWeave.CWINT03.BridgeImport).

## What the adapter actually does

`HWIR/EvaluatedProjectGraph/stage_core_v2.py` accepts **an already completed, independently reviewed** HB-INT-02 capture. It calls the actual existing `segmented_csc_capture_v2.verify` before and after byte staging and requires an independently approved lowercase SHA-256 of the original HB-INT-02 closure. The source capture may still be synthetic: pinning it does not prove Csc execution.

The caller also provides a separate, byte-pinned root-selector manifest. The manifest is **external hardware intent**, not inferred from source, assembly names or PDB; the Bridge validates syntax/identity only, not whether the root method exists. Its JSON schema is `hybridcpu.hardwarebridge.root-selectors/v1`, with `Schema` and `Roots`. A root has `TypeName`, `MethodName`, nullable `MetadataToken`, `TypeArguments` and `MethodArguments`, matching the existing `BridgeRootSelectorV1` property names. This first bridge subset permits only simple ASCII C# identity components; unrepresentable roots are **Blocked**, never simplified silently.

The adapter copies exact chunk bytes into a **private direct TEMP child**, verifies the complete workspace, and atomically renames to a **new unused direct TEMP child** only on success (Linux no-replace renameat2 / Windows no-overwrite rename). It refuses symlinks/reparse ancestors, destination races, file mutation, incorrect role/byte size/count, missing or duplicate roots, wrong SHA, timeout and overwrite. It never opens or writes a Golden source path, runs Csc, invokes a generator, or creates verified facts. It reuses HB-INT-02's already captured binary chunks, **not** the unverified Csc observation's declared original file paths.

Output has `bridge-index.json` and `bridge-index/chunk-NNNN.jsonl` with the existing Core v2 exact field names and SHA-256 of concatenated ordered raw JSONL chunks; `inputs/NNNNN.cs` or `.bin` with their actual original byte pins; `roots-manifest.json` with exact reviewed bytes; and `capture-map.json` in separate `hybridcpu.hardwarebridge.csc-to-core-map/v1` for mapping original node/kind/role/logical identity to staging paths. This map is **not** a Core authority contract.

Role mapping: primary Csc/generated source → `source`; explicitly observed owned-body source → `owned-body`; all references, compilers, analyzer/config, project/assets, and other non-body inputs → `metadata-only`. The Core reader must **independently** evaluate actual executable body ownership: Bridge classifications are not trusted.

Budgets: ≤8192 files, ≤512 MiB total staged input, ≤2 MiB per primary source, ≤32 MiB per other input, ≤256 manifest chunks, ≤4 MiB per chunk, ≤1 MiB Core index. Oversized HB-INT-02 captures refuse conversion, rather than truncate or relax Core's limits.

## Reproduce from separately approved inputs

Create a separate roots JSON file **outside Golden** (example only; this is not a claim that `Processor.Tick` is admitted):

```json
{"Schema":"hybridcpu.hardwarebridge.root-selectors/v1","Roots":[{"TypeName":"Processor","MethodName":"Tick","MetadataToken":null,"TypeArguments":[],"MethodArguments":[]}]}
```

Review and record the actual bytes SHA-256 of this external file and the independently captured HB-INT-02 `closureRootSha256` (lowercase 64-hex). Do not automatically approve an observed SHA by reading it back from the same untrusted packet.

```sh
python3 Compilers/HybridCPU_Compiler/HardwareBridge/HWIR/EvaluatedProjectGraph/stage_core_v2.py stage \
  --bundle "<existing-absolute-HB02-TEMP-bundle>" \
  --reviewed-capture "<independently-reviewed-lowercase-HB02-closure-sha256>" \
  --roots "<external-absolute-root-manifest.json>" \
  --reviewed-roots "<independently-reviewed-lowercase-root-file-sha256>" \
  --output "<new-unused-direct-TEMP-child>"
python3 Compilers/HybridCPU_Compiler/HardwareBridge/HWIR/EvaluatedProjectGraph/stage_core_v2.py verify \
  --output "<same-staged-TEMP-directory>" \
  --reviewed-capture "<independently-reviewed-lowercase-HB02-closure-sha256>" \
  --reviewed-roots "<independently-reviewed-lowercase-root-file-sha256>"
python3 -m unittest -v Compilers/HybridCPU_Compiler/HardwareBridge/HWIR/EvaluatedProjectGraph/test_stage_core_v2.py
```

The staging CLI returns 0 only for **software byte transport consistency**, 2 for expected fail-closed validation failures. Log actual command/exit, Core index SHA-256, chunk SHA-256, captured input SHA-256, and before/after Golden inventory hashes from an independent workspace.

## Downstream gates

**Blocked until separately paired CW-INT-03/04 Core tests:** the Python encoder follows existing C# `BridgeSegmentIndexV2` and Core index field order/JSONL roles but is not independently accepted by Core merely because two serializers use similar JSON. Run the *actual Core importer against this staged bundle*, byte-by-byte, before declaring interoperability Passed. No Core source changed here and no v1 consumer switch is permitted.

**Still NotRun/Blocked:** real clean Golden Csc invocation and source byte closure, Companion CIL compiler build on a healthy Windows .NET 11 host, native tool pins, replayable Golden stateful host, full CPU top/Rank4, RTL acceptance, FPGA/ASIC timing and Physical signoff. Neither a staging SHA nor a green transport fixture changes those statuses.
## R2-01 regression and byte-exchange handoff

The sole reviewed release entrypoint is `stage_core_v2.py stage`; the legacy `project_csc_to_core_v2.py` root list is unpinned compatibility data and cannot be treated as reviewed selectors. Use `stage_core_v2.py verify --bundle <reviewed-HB02-bundle>` with both approved SHA pins for exact producer-side crosswalk replay. It checks canonical 64-row JSONL partitioning, index and metadata map bytes, raw chunk concatenation SHA and complete per-file hashes. The immutable synthetic three-role cross-language contract is `HWIR/EvaluatedProjectGraph/r2_01_wire_contract.json`; SHA-256 values and exact bytes are exercised by `test_stage_core_v2.py`. This fixture is not a real task-time Csc receipt and does not qualify the Core consumer. The `hb-r2-canonical-stage.yml` workflow runs its own Ubuntu/Windows Python tests without private Core checkout or Golden mutation. R2-02 and R2-03 own downstream import and C# parity gates, respectively.

## R2-01 residual closure: Core property order and execution receipts (21 November 2026)

The producer's `verify()` now enforces the same ordered property names as the
**Core-owned** `BridgeSegmentIndexImporterV2.Shape()`: index
(`Schema,Roots,Chunks,ClosureSha256`), exact root selectors, chunk
descriptors (`Path,Sha256,Bytes,Rows`), and JSONL input rows
(`Path,Role,Sha256,Bytes`). Rehashing reordered JSON is *not* sufficient:
the underlying Core reader would reject it. The local provenance map also
has an exact ordered schema. The externally reviewed root manifest is parsed
from the **identical raw bytes** that matched its pinned SHA-256; a second
path read is prohibited.

`stage_core_v2.py stage` remains **FixtureTransportOnly /
CandidateTransportOnly**. It publishes no `ExecutedCsc`, `Verified`,
`AcceptedRTL`, or live-Golden claims and does not alter
`circuitweave.bridge-source-closure-index/v2` bytes.

To express an *actual-execution requirement* rather than accidentally treat
transport as execution evidence, callers can supply
`--require-executed-csc --paired-receipt /TEMP/pair/receipt.json
--reviewed-receipt-sha <independently pinned lower-hex SHA256>`.
The gate validates a bounded, pinned
`hybridcpu.hardwarebridge.paired-csc-capture-receipt/v1` file and the exact
selected `clean`/`dirty` `closureRootSha256` against the pinned HB02
capture. **It deliberately returns `R2_01_EXECUTION_BLOCKED` even if a
caller edits `executionGate` to suggest qualification and recalculates
its hash.** R1-02/v1 cannot supply independently qualified task-time Csc
execution or immutable `@rsp` file-open/read proof. Missing pins return
`R2_01_EXECUTION_PIN_MISSING`; a changed receipt returns
`R2_01_EXECUTION_RECEIPT_SHA`, and mismatched closure returns
`R2_01_EXECUTION_CAPTURE_MISMATCH`. These are fail-closed
producer-side diagnostics, **not** Core verification verdicts.

Keep these identities distinct, never reconstruct them from the Core JSONL
closure: `SourceSnapshotDigest` is the source snapshot, `CscInvocationDigest`
binds the actual compiler invocation, `CompanionSourceInputDigest` binds
the Companion input, and the HB02 `closureRootSha256` and Core
`ClosureSha256` bind two different transport inventories. A matching
transport hash, R1-01 toy trace, or zero Csc exit cannot be silently
substituted for any of these independently captured receipts. Future
qualified evidence requires a separately reviewed versioned R1 contract,
R2-02 independent Core consumer gate, and Core-owned admission; do not
patch v1/v2 bytes or this producer to accept a forged status.

Mutation tests include reserialized/rehashed index, selector, chunk and
JSONL property reorder; a pinned-roots double-read trap; explicit paired
receipt pin, gate and closure mismatch cases. Positive tests still use only
synthetic isolated TEMP fixtures; actual Golden, paired Core and RTL remain
**Blocked/NotRun**.
