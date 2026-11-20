# HB-INT-02 source transport / release-v2 (data-only)

**Contract:** `circuitweave.bridge-source-closure-index/v2`. This document specifies
the opt-in *software source byte transport* to the existing
`CircuitWeave.Frontend.Roslyn/Bridge/BridgeSegmentIndexImporterV2.cs`.
No Golden compiler run, method attestation, HWIR/RTL admission or independent
verification is implied by a successful transport. Do not label this Verified.

## Supported production route (no automatic root selection)

1. A separately observed, isolated task-time Csc invocation produces
   `hybridcpu.hardwarebridge.csc-observation/v2` and exact source bytes.
   `segmented_csc_capture_v2.py capture` produces the HB-INT-02 byte
   inventory and `closureRootSha256` (lowercase). Capture does **not**
   run Csc; a synthetic capture cannot stand in for a real software build.
2. Review and freeze **two independent input pins**: the HB02 closure root and
   a `hybridcpu.hardwarebridge.root-selectors/v1` manifest SHA-256
   (lowercase hex). This exact manifest has ordered `Roots` selectors;
   no inferred/executable owner is granted by the manifest itself.
3. `stage_core_v2.py stage` consumes those explicit pins, re-verifies all
   HB02 chunk bytes and roles, writes a **new direct child of external
   OS TEMP**, rehashes HB02 capture after staging, then validates the
   staged workspace. No overwrite, symlink/reparse/traversal or Golden
   writes; cancellation/timeout refuses and removes only its new workspace.
4. Supply **explicit** `bridge-index.json` bytes, the staged workspace
   path, an independently reviewed index SHA and an independently
   rederived live closure SHA to the **Core-owned** v2 importer.
   The importer returns Candidate/unqualified source inventory, not method
   qualification. Independent Core consumer/Golden integration E2E is
   **Blocked until R2-02**; passing producer parity is not that gate.

Example (all placeholders are mandatory caller-reviewed values; SHA is never
taken as trusted merely because it appears in the bridge output):

```sh
python3 Compilers/HybridCPU_Compiler/HardwareBridge/HWIR/EvaluatedProjectGraph/stage_core_v2.py stage \
  --bundle /external/temp/hb02-capture-UNIQUE \
  --reviewed-capture <64-lowercase-HB02-closure-sha> \
  --roots /reviewed/root-selectors-v1.json \
  --reviewed-roots <64-lowercase-root-manifest-sha> \
  --output /external/temp/hb02-core-stage-NEW
python3 Compilers/HybridCPU_Compiler/HardwareBridge/HWIR/EvaluatedProjectGraph/stage_core_v2.py verify \
  --output /external/temp/hb02-core-stage-NEW \
  --reviewed-capture <64-lowercase-HB02-closure-sha> \
  --reviewed-roots <64-lowercase-root-manifest-sha>
```

**Byte-format reference writer:** actual managed
`SourceSnapshot/BridgeSegmentIndexV2.Write` (System.Text.Json compact UTF-8),
with `BridgeSegmentIndexV2.ValidateAgainstActualBytes`. Python release
writer is `HWIR/EvaluatedProjectGraph/stage_core_v2.py`, whose **entire**
`bridge-index.json`, **each** `bridge-index/chunk-NNNN.jsonl` and **each**
staged raw input must be byte-identical to that managed reference writer.
The cross-language test invokes those production implementations; it does
not compare two hand-written fixture serializers.

## R2-01 canonical stage publish and independent byte fixture

**Sole reviewed HB02 production release entrypoint:** `stage_core_v2.py stage`.
The deprecated `project_csc_to_core_v2.py` is compatibility-only for
primary-only synthetic captures. Its legacy unpinned root *list* is never
authoritative for release, independently approved roots, or a Core qualified
source inventory. Passing that writer is not a replacement for the pinned
`hybridcpu.hardwarebridge.root-selectors/v1` manifest.

R2-01 constructs a private 0700 staging directory beneath the **same OS TEMP
parent** and publishes it under the caller's fresh direct-child name only
after all raw files, ordered canonical JSONL chunks, bridge index, pinned
source map, capture and root inputs reverify. Publication uses Linux
`renameat2(RENAME_NOREPLACE)` or Windows no-overwrite directory rename;
an occupied destination, unsupported OS primitive, mutation, cancellation,
or timeout fails closed and cleans only the private staging directory.
Before publication, there is no partially populated public destination.

For stronger **producer-only** crosswalk replay, supply the same separately
reviewed HB02 bundle when verifying:

```sh
python3 Compilers/HybridCPU_Compiler/HardwareBridge/HWIR/EvaluatedProjectGraph/stage_core_v2.py verify \
  --output /external/temp/hb02-core-stage-NEW \
  --bundle /external/temp/hb02-capture-UNIQUE \
  --reviewed-capture <64-lowercase-HB02-closure-sha> \
  --reviewed-roots <64-lowercase-root-manifest-sha>
```

The full `capture-map.json` bytes are canonical and bound to every original
reviewed HB02 entry when `--bundle` is supplied. This does **not** turn a
capture map or SHA into independent source qualification; only a Core-owned
consumer and independently pinned live closure can do that.

The immutable, **synthetic** interop fixture
`HWIR/EvaluatedProjectGraph/r2_01_wire_contract.json` contains the exact
raw bytes (hex), one source, one owned-body and one metadata-only row, ordered
explicit roots, and expected index/JSONL SHA-256 values. R2-03 may independently
recreate the fixture using the C# serializer; R2-02 may use it as an
unqualified transport fixture. The current Python tests verify the fixture
and 1001+ separately captured synthetic source inputs; they do not run Csc,
Core or Golden. A divergence must be reported as first differing offset
with both byte digests and the fixture, without modifying another owner.

## Exact preimages and layout

- `RootManifestSha256` = lowercase SHA256 of raw reviewed
  `roots-manifest.json` bytes. `Hb02ClosureRootSha256` = lowercase
  HB02 canonical index payload root checked against captured raw chunks.
- Sort staged input paths ordinally: `inputs/00000.cs` or `.bin`,
  `inputs/00001...`, derived from the HB02 entry order. Each JSONL record is
  compact UTF-8, **exactly** `{"Path":"...","Role":"...","Sha256":"UPPER","Bytes":N}\n`;
  file SHA is uppercase SHA256 of actual raw input bytes.
- Group up to **64 rows per chunk**, flush before exceeding 4 MiB, and name
  `bridge-index/chunk-0000.jsonl` onward. Descriptor properties:
  `Path,Sha256,Bytes,Rows`. Chunk SHA is **uppercase** SHA256(raw JSONL).
- `ClosureSha256` = **uppercase**
  SHA256(`raw_chunk_0000 || raw_chunk_0001 || ...`), in declared order,
  **not** SHA256(JSON objects), source bytes or roots.
- Index properties in strict order `Schema,Roots,Chunks,ClosureSha256`;
  `CoreIndexSha256` is **uppercase** SHA256 of *the whole* raw compact
  `bridge-index.json`. Unlike the closure root, this digest binds root
  selectors and chunk descriptors. Caller independently pins these values.
- `capture-map.json` is an **unqualified, producer-only crosswalk**:
  original HB02 node/kind/project role/logical path and raw SHA/length to
  staged input. It cannot promote a role or satisfy Core qualification.

| HB02 kind | HB02 role | Core role | Staged suffix |
|---|---|---|---|
| `csc-source`, `generator-source` | `primary` | `source` | `.cs` |
| `csc-source`, `generator-source` | `owned-body` | `owned-body` | `.cs` |
| `csc-source`, `generator-source` | `metadata-only` | `metadata-only` | `.bin` |
| `reference`, `analyzer`, `analyzer-config`, `additional-file`, `source-link`, `app-config`, `csc-binary`, `project`, `assets` | `metadata-only` only | `metadata-only` | `.bin` |

Core v2 already recognizes `source`, `owned-body`, `metadata-only`,
`unselected`, `compiler-pe`, `compiler-pdb`. This producer maps **only**
the explicit rows above; it does not fabricate compiler PE/PDB roles or
relabel `metadata-only` as executable. Both primary and owned-body remain
**unqualified claims** until Core independent source/operation admission.

## Compatibility and migration

- `project_csc_to_core_v2.py`: **deprecated primary-only compatibility**
  entrypoint, with its existing root-list input, `index.json`,
  `capture-crosswalk.json`, and `src/` + `metadata/` layout unchanged.
  It now **refuses any non-primary source kind** (including `owned-body`
  and `metadata-only`) rather than quietly promoting it to `source`. Existing primary-only fixtures remain replayable. Migration
  to release staging requires creating a **new reviewed root-selectors/v1**
  manifest, its independent raw SHA, and pinning the HB02 closure. No silent
  conversion or old-interface deletion.
- `BridgeV2OwnedWorkspacePublisher.Publish`: retained opt-in .NET publisher
  for caller-supplied, prevalidated transport with live source pins.
  It emits `index.json` in its own freshly allocated TEMP workspace,
  *not* the HB02 reviewed capture-map layout, and does not claim to implement
  the release HB02 capture crosswalk. Do not replace one entrypoint by
  renaming its root manifest, index file, or source-role assertions.
- If a future reader needs other roles or a different wire schema, require
  a separately reviewed **Core-owned dependency PR before changing this
  default**. This diff does not change Core, sidecar/v1 or Golden sources.

## Budgets, reproducibility, and fail-closed tests

Core ceilings: 1 MiB index, 4 MiB raw chunk, 256 chunk descriptors,
8192 input rows, 64 MiB cumulative JSONL manifests, 2 MiB per `source`,
32 MiB for every non-`source` input, and 512 MiB total source input
payload. The standard 64-row policy normally hits 128 chunks by 8192 rows;
a forged 257-descriptor manifest is refused, not truncated. Strict JSON
property order/duplicate refusal, external TEMP, case-safe paths,
traversal/reparse, root and role mutation, chunk hashes/order, raw file
rehash, timeout/cancel and rollback are covered in scoped tests.
Other Core consumer E2E security decisions stay Core-owned.

Local/Actions repro (install SDK 11.0.100-rc.2.26504.105 for the
managed test; isolate all `dotnet` artifacts and package caches in TEMP):

```sh
python3 -m unittest -v Compilers/HybridCPU_Compiler/HardwareBridge/HWIR/EvaluatedProjectGraph/test_stage_core_v2.py
python3 -m unittest -v Compilers/HybridCPU_Compiler/HardwareBridge/HWIR/EvaluatedProjectGraph/test_project_csc_to_core_v2.py
python3 Compilers/HybridCPU_Compiler/HardwareBridge/Tests/CscCoreV2Parity/emit_reviewed_stage_fixture.py /tmp/hb-stage-NEW 65
dotnet run --project Compilers/HybridCPU_Compiler/HardwareBridge/Tests/CscCoreV2Parity/HardwareBridge.CscCoreV2Parity.csproj -- /tmp/hb-stage-NEW
dotnet run --project Compilers/HybridCPU_Compiler/HardwareBridge/Tests/CscCoreV2Parity/HardwareBridge.CscCoreV2Parity.csproj -- --wire-limits
```

**Receipt fields to capture for each *actual* run:** Bridge source branch/head,
pinned SDK, OS/Python version, command, raw exit, raw stdout/stderr SHA256,
`HB02CapturedClosureSha256` (lower), `RootManifestSha256` (lower),
`CoreIndexSha256` (upper), `CoreClosureSha256` (upper), source count
and chunk count. CI logs print these; never supply invented SHA receipts.

**Release gates:** producer transport implementation only (Implemented);
managed/Python runners must actually execute before Tested in CI; cross-repo
Core importer E2E **Blocked (Core R2-02)**; genuine task-time Csc observer and
Golden checkout pre/post source/Git audit **NotRun here** until their
separate owner provides a real sealed capture. A fixture source no-write
check is **not** a Golden source no-write receipt.
