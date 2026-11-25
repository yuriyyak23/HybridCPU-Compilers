# HB-INT-02/03 follow-up: large Csc capture → existing Core v2 source transport

> **Baseline 2026-11-25.** The projection writes the CW source index, not the HB provenance envelope. Keep roots and index pins independently bound and preserve the original compiler order through separate capture evidence. Source-role promotion requires explicit producer/reader review. See [current status](current/STATUS.md).

**Status: implemented producer-only adapter; unqualified, no executed-compiler receipt.** This is an additive extension of the [six-package HardwareBridge plan](CircuitWeave_Pipeline_Integration_Plan.md), specifically HB-INT-02 exact capture + HB-INT-03 strict interchange, for seam S2 / Core CW-INT-03. It does **not** revise BridgeSourceSnapshot v1, sidecar v1, HB-INT-03 provenance envelope v2, or CircuitWeave's independent source verifier.

## Why this exists

HB-INT-02 has a segmented binary source-capture format, hybridcpu.hardwarebridge.csc-closure-index/v2, capable of >1000 Processor inputs. Separately, Core CW-INT-03 merged a strict reader of **circuitweave.bridge-source-closure-index/v2**, using compact JSON root/index, canonical sorted UTF-8 JSONL rows and staged files. HB-INT-03's in-memory BridgeSegmentIndexV2 already emits that Core format, but the provenance envelope remains bounded by SourceSnapshot v1 (128 documents/references). An actual large HB-INT-02 capture therefore needs a bounded **producer-side** mapping to the existing Core v2 input contract without treating the v1 companion as full Processor coverage.

New opt-in script: HWIR/EvaluatedProjectGraph/project_csc_to_core_v2.py.

It revalidates a previously captured HB-INT-02 workspace, reads separately supplied explicit root selectors, stages the existing observed raw source/metadata bytes in a **NEW direct TEMP child**, emits exactly the existing Core v2 index and 64-row canonical JSONL manifest chunks, and adds a separate **unqualified** mapping file capture-crosswalk.json. This mapping records capture root digest, Core index and closure digests, observed project/source roles and original logical identities. Hashes are checksums, **not self-attestation, signatures, or a newly authorized source lock**.

Core reader expects caller to independently supply ExpectedLiveClosureSha256, in addition to independently re-hashing actual staged files. Never use this Bridge-generated value as the independent expected live closure root. Software source vs executable body ownership remains a separate CW-INT-04/Roslyn decision; observed primary/owned-body source claims are recorded in the crosswalk but are **not** promoted into trusted Core input roles. Actual source contents are sent with Core Role=source, and compiler, assets, PE, analyzer, config and other support files always with Role=metadata-only.

## Bounded migration contract

- Source input: a valid HB-INT-02 index.json and raw binary chunks. The producer re-hashes all original chunks and staged bytes; production changes in Golden source remain a separate pinned-capture responsibility, not verified by projecting already frozen bytes.
- Root selectors: separately supplied JSON array, 1–32 exact fields per selector in order: TypeName, MethodName, MetadataToken, TypeArguments, MethodArguments. This first adapter admits a conservative ASCII identifier subset; unknown/ambiguous/generic-without-token selectors are refused.
- Existing Core v2 limits: 8192 indexed inputs, 2 MiB per UTF-8 source, 32 MiB per metadata file, 512 MiB aggregate, 1 MiB index, 4 MiB/manifest chunk, 256 chunks, 64 MiB aggregate manifest. Oversize captures fail closed, never truncate or silently skip sources.
- Core index: exact Schema,Roots,Chunks,ClosureSha256 field order; uppercase SHA256; sorted paths; binary manifest chunk paths bridge-index/chunk-0000.jsonl etc; JSONL rows Path,Role,Sha256,Bytes in ascending ordinal path order; SHA256 of **ordered concatenated raw manifest chunks** is ClosureSha256.
- Staged file paths src/NNNNN.cs or metadata/NNNNN.bin are synthetic and stable for an ordered capture, not Roslyn symbol identities. The separate crosswalk preserves project/observed logical identity for independent Core qualification. No user-supplied path is reinterpreted as an output path.
- Every output is exclusive, only under a fresh TEMP-owned directory, never Golden. Missing/corrupt source or chunk, changed staged bytes, symlink/reparse ancestors, unknown formats and metadata-to-source promotion are refused. Python producer executes no MSBuild, Csc, generator or native EDA target.
- Core importer may return **Candidate/unqualified diagnostic**, not accepted HWIR or independently Verified. No Bridge proof gate is added.

## Invocation

Use **absolute** paths. Run after a separately approved isolated Csc observation and HB-INT-02 capture (see HB_INT_02_Segmented_Csc_Closure.md).

~~~powershell
python Compilers/HybridCPU_Compiler/HardwareBridge/HWIR/EvaluatedProjectGraph/project_csc_to_core_v2.py project --capture "<existing absolute HB02 capture TEMP dir>" --roots "<absolute roots.json>" --output "<new direct child of OS TEMP>"
python Compilers/HybridCPU_Compiler/HardwareBridge/HWIR/EvaluatedProjectGraph/project_csc_to_core_v2.py verify --workspace "<projected TEMP dir>" --capture "<original HB02 capture TEMP dir>"
python3 -m unittest -v Compilers/HybridCPU_Compiler/HardwareBridge/HWIR/EvaluatedProjectGraph/test_project_csc_to_core_v2.py
~~~

Exit 0 and printed uppercase closure hash mean producer-only byte transport integrity. Exit 2 on malformed, stale, or blocked input. The Core reader must be invoked by its **own** agreed entry point with the exact staged workspace, raw index bytes and externally established expected current closure hash. A current Git commit SHA is never a replacement for current worktree byte hashes.

## Next gates — not passed by these tests

1. Full *actual* isolated Csc task invocation capture (target-time args, all source generator emissions, compiler image and SDK/target properties) with approved execution environment and independent trace/process identity.
2. Reproducible clean pinned-commit and dirty Golden Processor worktree captures with same-input before/after SHA evidence, then real >1000-source compilation closure. Synthetic tests do not qualify that.
3. Paired runtime Core CW-INT-03 importer regression against generated workspace, independent expected digest/current byte rehash, and CW-INT-04 qualified primary/owned body symbols. The imported **source** role is not executable body ownership.
4. Independent RTL proof, native FPGA/ASIC target observation and physical signoff remain Blocked/NotRun.
5. True cross-repo .NET 11/native tests remain NotRun where prereqs/tool pins are absent. CI here tests Python producer only and never substitutes fixture bytes for actual executable compilations.

The legacy v1 SourceSnapshot budgets and wire, Companion facts, Golden oracle and v1 sidecar are **unchanged**. This mapping is additive and opt-in; future implementation requiring a changed Core wire format must use a separately agreed paired Core PR.