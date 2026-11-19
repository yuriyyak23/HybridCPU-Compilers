# HB-INT-02/03/06 — opt-in v2 owned workspace publication

**Scope:** HardwareBridge read-only software facts -> portable Core CW-INT-03 transport. The new publisher is not a compiler, Core reader, independent source verifier, EDA runner or verification authority.

Baseline for this follow-up: HybridCPU-Compilers `master@77947f82cd61ed5248c0ae48d722d7fbfdf29a9a` after merged [HB-INT-03 PR #10](https://github.com/yuriyyak23/HybridCPU-Compilers/pull/10); CircuitWeave `master@26724fc70a1cb7c403aa50d8e0f41383202a22ac`. [Six-package HardwareBridge plan](CircuitWeave_Pipeline_Integration_Plan.md), [Core CW-INT-03 reader](https://github.com/yuriyyak23/CircuitWeave/pull/3).

## Production interface

`BridgeV2OwnedWorkspacePublisher.Publish(transport, inputs, roots, liveSources, goldenRoot, token)` requires:
- Already built `BridgeV2Transport` from `BridgeSegmentIndexV2.Write`, including a byte-exact canonical compact UTF-8 Core-compatible index and JSONL chunks.
- Caller-owned actual source/reference/compiler `BridgeV2Input` bytes, explicit roots and **one current absolute live file pin per `source` row**.
- Source-origin value `original` or `working` only for current files inside the explicit Golden root. `generated` files must be **outside** Golden. At least one source must be inside Golden.
- Fully qualified, existent, non-reparse Golden root; no source or root path auto-discovery, no implicit paths from git HEAD.

The publisher checks original SHA/size before and after staging and separately rehashes the published bytes. It returns an unqualified `BridgeV2PublishedWorkspace(Root, IndexPath, IndexSha256, ClosureSha256)`, not an accepted binding or signed receipt. Index/chunk/file bytes and hashes preserve the existing Core PR #3 schema. The publisher does **not** change the opt-in v2 or default v1 wire formats, or the distinction between SourceSnapshotDigest and CompanionSourceInputDigest.

Output is **only** in a newly allocated OS-temp child directory (random name `hb-int-v2-*`); the API does not allow callers to choose an output path or overwrite an existing file. It rejects OS temp nested under the Golden root, reserved `index.json` collisions, path traversal, case collisions, unknown source origins and observed symlink/reparse ancestors. On an exception it attempts to remove only the newly created output directory. On success, caller owns cleanup once independent Core ingestion ends; no automatic deletion before Core can read.

**Use with Core:** pass the actual bytes of `IndexPath`, the published `Root` and an **externally trusted** expected closure SHA-256 into the Core-owned `BridgeSegmentIndexImporterV2.Import`. Do not merely copy the producer's `ClosureSha256` as independent expected value. Core must rehash files and independently verify source/ownership/manifest/locks; this publisher only stages byte evidence.

## Negative checks / trust

Isolated `BridgeV2OwnedWorkspaceTests` runs actual disk operations against a **synthetic test Golden** and tests byte equality, deterministic output content, Golden source SHA before/after, stale source, altered candidate input, missing source, invalid original/generated role, reserved output collision, cancellation, symlink rejection where supported and cleanup. No synthetic test Csc or PE/PDB receipt is reported as a real build.

There are residual race limits for hostile processes running as the same user: path preflights cannot replace OS-level secure directory handles / no-follow flags on every platform. This opt-in transport must remain **unqualified** and runs only in a separately approved, isolated capture environment; a malicious concurrent rename or host compromise is a **Blocked** qualification gate, not a reason to relax checks.

## Commands / gates

    dotnet --info
    dotnet restore Compilers/HybridCPU_Compiler/HardwareBridge/Tests/HardwareBridge.V2.ContractTests.csproj
    dotnet run --project Compilers/HybridCPU_Compiler/HardwareBridge/Tests/HardwareBridge.V2.ContractTests.csproj -c Release

The existing [HB-INT-06 QA harness](HB_INT_06_QA_Matrix.md) and full Companion self-tests remain separate. A passing isolated publisher test does NOT unblock exact real Csc-task invocation capture, generated/analyzer/MSBuild closure provenance, Windows reparse/race assurance, source timebase or a full Processor closure. Those require HB-INT-02/06 and independent Core CW-INT-03/04 receipts on actual Golden source bytes. Hardware RTL/FPGA/ASIC/signoff are never Bridge-owned.
