# HB-INT-03 — opt-in strict source/facts v2 interchange

> **Baseline 2026-11-25.** Canonical HB description of the two v2 formats. At the reviewed CW HEAD there is an implemented source-index reader. The HB provenance envelope still has no confirmed matching CW reader. Historical pins below remain scoped to their original fixtures. The reviewed CW root SDK is stable `11.0.100`; historical paired and RC2 capture pins remain distinct. See [current status](current/STATUS.md).

Status: **Candidate producer, unqualified; Core v2 facts acceptance Blocked**. This is not a hardware or Processor qualification.

## HEAD, pins, and dependency

- HybridCPU-Compilers master before branch: `94f59223b36659c9162620224f91349ac79fb896`.
- CircuitWeave master before branch: `555672232e46cb4e8b157e2d1b1db1e873c4194e`.
- Earlier audit SHA: HB `c66d2ec2bb8a06e29ced1d884c98a055054b4a26`; Core `58a7c44c3353a9c95108192224b5ea190f96464b`.
- Historical Core `global.json` SDK pin: `11.0.100-rc.2.26504.105` (prerelease/latestPatch). A version string is not a real Csc binary pin.
- Related: [CW-INT-03 draft Core reader PR #3](https://github.com/yuriyyak23/CircuitWeave/pull/3), [CW-INT-04 backlog](https://github.com/yuriyyak23/CircuitWeave/blob/master/docs/roadmap/circuitweave/12_CW_INT_Execution_Packages.md), [HB plan PR #4](https://github.com/yuriyyak23/HybridCPU-Compilers/pull/4), [master plan PR #1](https://github.com/yuriyyak23/CircuitWeave/pull/1).

## Three deliberately separate formats

1. **Unchanged default v1**: `hybridcpu.hardwarebridge.sidecar/v1` keeps `Schema,PayloadSha256,Payload` with payload `Schema,Source,Facts,GoldenOracle`. Hash exactly the old compact UTF-8 serialized Payload bytes, never a normalized tree. Preserve v1 writer/reader, nullable Facts/GoldenOracle, and all existing fixtures.
2. **Core-compatible v2 source transport**: `circuitweave.bridge-source-closure-index/v2`, from `BridgeSegmentIndexV2.Write`. Compact JSON object, exactly `Schema,Roots,Chunks,ClosureSha256`. Each chunk contains LF-terminated compact canonical JSONL rows `Path,Role,Sha256,Bytes`, sorted ordinal by path; descriptor is `Path,Sha256,Bytes,Rows`. SHA-256 of concatenated ordered raw chunk bytes = ClosureSha256. Roles: source / owned-body / metadata-only / unselected / compiler-pe / compiler-pdb. Limits from Core PR #3: 1 MiB index, 4 MiB per chunk, 256 chunks, 8,192 rows, 64 MiB aggregate manifest, 512 MiB input bytes. The Core draft reader accepts transport as **unqualified** source inventory, not accepted binding.
3. **Bridge-only opt-in provenance envelope**: `hybridcpu.hardwarebridge.provenance-index/v2`, from `BridgeProvenanceCodecV2`. Compact `Schema,PayloadSha256,Payload`; payload ordered fields `Schema,SourceSnapshotDigest,CompanionSourceInputDigest,SdkVersion,CscSha256,CscOptionsSha256,PeSha256,PdbSha256,SoftwareStatus,CoreIndexSha256,ClosureSha256,SourceRoles,Inputs`. The snapshot digest is distinct from the Companion input digest. SourceRoles require original / working / generated for each source. Compiler/Csc/PE/PDB/reference bytes are explicit pins; Candidate requires actual matching PE/PDB inputs; Rejected cannot claim those pins. Allowed SoftwareStatus = Candidate or Rejected. No Verified, accepted RTL, signature or Physical fields. Max 4 MiB, JSON max depth 16, reject duplicate properties anywhere, unexpected/reordered keys, wrong hash, changed byte evidence, wrong live source/SDK, unknown schema or noncanonical serialization.

**Contract seam:** The reviewed `BridgeSegmentIndexImporterV2` consumes format (2). No reader for (3) was confirmed in CircuitWeave. Paired Core reader review/PR and independent Roslyn body/source byte validation are required before any default producer switch; a checksum is not a signature. `BridgeProvenanceCodecV2.Write` explicitly cross-checks v2 source/reference bytes against the bounded existing v1 snapshot (max 128 docs/refs). The pure index can emit 1,200+ sources, but a separate scalable, qualified snapshot and software compilation path is required before claiming large Processor facts. Do not fabricate that root.

## Opt-in migration

1. Capture exact live v1 SnapshotDigest and separate Companion SourceInputDigest; keep default v1 bytes and current Golden source unchanged.
2. Obtain actual source/ref/PE/PDB/Csc executable and UTF-8 Csc arguments bytes, and an independent expected SDK pin. Csc image/arguments are supplied, never guessed from a version string.
3. Construct `BridgeV2Input` with actual bytes, strict relative paths and explicit role; create an in-memory transport. Stage index, chunks and referenced files in a **separately owned** immutable workspace, never inside Golden. Staging must reject symlinks, path traversal, replacement, and mutation; these codecs deliberately do no filesystem IO.
4. Create the separately checksummed provenance envelope and call `ReadAndValidate` with externally expected live snapshot/SDK and actual bytes. Core must rehash every staged byte, verify Csc/PE/PDB/ref/source closure independently, and own hardware body/manifest acceptance.
5. Enable default v2 only after paired Core contract review, exact cross-repo fixture round-trip, and CI confirmation. Until then all v1 consumers remain unchanged.

## Repro and honest QA

    dotnet --info
    dotnet restore Compilers/HybridCPU_Compiler/HardwareBridge/Tests/HardwareBridge.SelfTests.csproj
    dotnet run --project Compilers/HybridCPU_Compiler/HardwareBridge/Tests/HardwareBridge.SelfTests.csproj -c Release
    dotnet build Compilers/HybridCPU_Compiler/HardwareBridge/HardwareBridge.Companion.csproj -c Release

New automated tests cover unchanged v1 bytes, v1→v2 migration, deterministic 1,200-row synthetic segmentation, duplicate JSON property, unknown Verified field, bad canonicalization/checksum, stale live source, corrupted/missing source/reference/chunk input, role promotion, wrong SDK/Csc/PE/PDB, oversized/malicious JSON, cancellation and invalid paths. **Synthetic fixture Csc/PE/PDB bytes are not production compilation receipts.** Existing Companion compile-only tests remain separate and are not replaced.

At PR creation this runner has **no dotnet or csc**: local build/tests, actual compiled binary SHA, native tool pins, isolated timeout, full Golden checkout byte immutability and Core E2E cross-repo tests are **NotRun**. No pretend exit code 0 or fixture-only replacement for real compilation. Symlink/writer-fs tests are inapplicable to this memory-only codec and remain a Core/HB-INT-06 workspace staging gate. Full CPU HWIR/RTL/physical proof remains Blocked/NotRun.
