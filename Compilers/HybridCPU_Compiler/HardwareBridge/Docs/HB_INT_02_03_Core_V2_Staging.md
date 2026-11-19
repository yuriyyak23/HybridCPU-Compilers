# HB-INT-02/03 — opt-in pinned Csc bytes → Core v2 staging

**Scope:** read-only software observation transport, not a general C#→HWIR compiler, Golden execution, hardware root inference or independent verification. This closes one **producer-side representation gap** between HB-INT-02 `hybridcpu.hardwarebridge.csc-closure-index/v2` and HB-INT-03 / CW-INT-03 `circuitweave.bridge-source-closure-index/v2`. Both original versions and sidecar/v1 remain unchanged.

**Baseline:** HybridCPU-Compilers `master@77947f82cd61ed5248c0ae48d722d7fbfdf29a9a`; CircuitWeave `master@26724fc70a1cb7c403aa50d8e0f41383202a22ac` (2026-11-19). See [HB plan](CircuitWeave_Pipeline_Integration_Plan.md), [HB-INT-02](HB_INT_02_Segmented_Csc_Closure.md), [HB-INT-03](HB_INT_03_V2_Interchange.md), and [Core importer tests](https://github.com/yuriyyak23/CircuitWeave/tree/master/tests/CircuitWeave.CWINT03.BridgeImport).

## What the adapter actually does

`HWIR/EvaluatedProjectGraph/stage_core_v2.py` accepts **an already completed, independently reviewed** HB-INT-02 capture. It calls the actual existing `segmented_csc_capture_v2.verify` before and after byte staging and requires an independently approved lowercase SHA-256 of the original HB-INT-02 closure. The source capture may still be synthetic: pinning it does not prove Csc execution.

The caller also provides a separate, byte-pinned root-selector manifest. The manifest is **external hardware intent**, not inferred from source, assembly names or PDB; the Bridge validates syntax/identity only, not whether the root method exists. Its JSON schema is `hybridcpu.hardwarebridge.root-selectors/v1`, with `Schema` and `Roots`. A root has `TypeName`, `MethodName`, nullable `MetadataToken`, `TypeArguments` and `MethodArguments`, matching the existing `BridgeRootSelectorV1` property names. This first bridge subset permits only simple ASCII C# identity components; unrepresentable roots are **Blocked**, never simplified silently.

The adapter copies exact chunk bytes into a **new unused direct TEMP child** and refuses symlinks/reparse ancestors, file mutation, incorrect role/byte size/count, missing or duplicate roots, wrong SHA, timeout and overwrite. It never opens or writes a Golden source path, runs Csc, invokes a generator, or creates verified facts. It reuses HB-INT-02's already captured binary chunks, **not** the unverified Csc observation's declared original file paths.

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