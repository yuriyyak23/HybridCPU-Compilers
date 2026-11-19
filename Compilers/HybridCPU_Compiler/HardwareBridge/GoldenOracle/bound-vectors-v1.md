# HB-INT-04 continuation: source-bound scalar vector replay v1

**Status: optional data-only producer contract; Golden oracle attestation and RTL equivalence Blocked.**

This feature completes the *scalar/vector* interchange gap beside `GoldenRawTraceV1` (step transition DTO) without modifying `TrustedAddWordOracleV1`, `BridgeSidecarCodecV1` or Golden HybridCPU-v2. Contract: `hybridcpu.hardwarebridge.golden-bound-vectors/v1`. It does **not** run arbitrary C#, load assemblies, choose a delegate from JSON, execute targets or create an independent proof gate.

## What is actually bound

`GoldenBoundVectorsCodecV1.Capture` accepts the previously captured, self-consistent `BridgeSourceSnapshotV1`, an ordered array of original source **bytes** matching its original Document digest rows, a caller-provided compiled assembly byte buffer, an explicit source RootIndex, an ordered independently requested `GoldenVectorV1` list, and an existing `GoldenObservationV1` returned by the existing explicitly reviewed scalar adapter.

The codec refuses missing, changed or size-inconsistent original bytes; computes exact uppercase SHA-256 of the provided compiled assembly; binds source snapshot digest, ordered original document rows, an aggregate document-row hash, source root index, fixed `explicit-trusted-addword32/v1` adapter ID and reviewed source label, and verifies every observed A/B vector matches the request **in the same order**. Every observed case must have one canonical 32-bit output with correct signedness and no diagnostic, while blocked cases have no output and a bounded diagnostic. The summary observation must be Observed iff all cases are Observed; otherwise it is Blocked.

**All fields are unqualified assertions**: the Bridge does not prove the supplied bytes were built into the supplied assembly, that the registered delegate came from Golden, that the delegate is pure, that a blocked diagnostic is true, or that its outputs are correct. Source and assembly hashes are integrity data, not signatures. A deliberately forged, re-signed output can pass internal self-consistency and **must never** be treated as an independent oracle. Core CW-INT-10 owns trusted Golden execution, assembly/source/method attestation and replay acceptance; CW-INT-11 owns independent RTL simulation/formal equivalence. No hardware acceptance or physical signoff status exists in the new DTO.

## Wire contract and strict reader

`GoldenBoundVectorsV1` JSON properties, in exact order:

1. `Schema`
2. `SourceSnapshotDigest`
3. `OriginalDocuments` — ordered `BridgeDocumentDigestV1` rows
4. `OriginalSourceSetSha256` — SHA-256 of compact `System.Text.Json` serialization of those rows
5. `CompiledAssemblySha256` — SHA-256 of supplied compiled assembly **bytes**
6. `RootIndex` — existing exact root ordinal, never a requested method to invoke
7. `AdapterId` — fixed `explicit-trusted-addword32/v1`
8. `ReviewedSourceLabel`
9. `RequestedVectors` — 1..512 `GoldenVectorV1` records in original request order
10. `Observation` — existing `hybridcpu.hardwarebridge.golden-observation/v1`, 1..512 exact corresponding cases
11. `ReplayIdentity` — SHA-256 of compact UTF-8 JSON with fields 1..10.

`GoldenWordV1` preserves `Width=32`, `Signed` and full unsigned decimal `Bits` (including two's-complement patterns), and its inherited computed `IsCanonical` getter in the JSON. The byte-preserving read requires canonical compact `System.Text.Json` with exact case/order/names, no extra members, no duplicate JSON keys at any depth, no invalid word high bits, no unknown versions/statuses, and a correct replay hash. No text normalization, inferred signed conversion, event/clock semantics or silent reordering.

`GoldenBoundVectorsCodecV1.ReadAndValidate` requires **independently supplied** current `BridgeSourceSnapshotV1`, expected assembly SHA-256, expected reviewed source label and expected ordered vector list; it refuses a document with stale/reordered inputs even if somebody recomputed ReplayIdentity. The consumer is responsible for obtaining expectations from a separately authorized immutable session, not from the payload itself. Output is still an unqualified software observation.

Budgets: max 2 MiB JSON, 32 MiB compiled assembly byte input, 1..512 source-bound 32-bit vectors; source snapshot retains its own document/reference/bytes limits. Invalid identities, unmatched request/response, forbidden adapter fields, noncanonical JSON, malformed code/data or over-budget inputs produce a fail-closed `ArgumentException`; cancellation uses `OperationCanceledException`. These APIs have no process/timeouts and cannot preempt a malicious reviewed delegate invoked by an external host. No choice of clocks, reset, memory arbiter, rank4 or CPU-cycle contract is encoded.

## Compatibility and migration

The existing `TrustedAddWordOracleV1.RegisterExplicitlyTrusted(Func<uint,uint,uint>, label, signed)` and its ID remain unchanged; `GoldenObservationV1` is still available to legacy sidecar/v1 consumers without any extra fields. The new `GoldenBoundVectorsV1` is separately serialized and opt-in. Existing Core/Studio readers are unchanged. No automatic migration or default producer switch. Core CW-INT-10 needs a paired reader/attestation PR to qualify **any** of these outputs as Golden evidence, including PE/PDB/method closure and independent expected vectors; the Bridge cannot certify itself.

A host already holding a reviewed `TrustedAddWordOracleV1` calls its original `Observe`, then explicitly binds the returned observation with `GoldenBoundVectorsCodecV1.Capture`, passing original source and assembly bytes. An invalid original v1 adapter request may yield a singleton Blocked sentinel not matching the ordered requested vectors; this new codec intentionally refuses to misrepresent it as a fully bound batch. A per-vector blocked runtime exception with matching request is preserved as Blocked, never Observed.

## QA and constraints

Run `dotnet run --project Compilers/HybridCPU_Compiler/HardwareBridge/Tests/GoldenBoundVectors.ContractTests.csproj -c Release` with .NET 11 (independent of transitive compiler Core). Cross-platform workflow: `.github/workflows/hb-int-04-vectors.yml`. Tests use explicitly reviewed fixture AddWord and synthetic opaque assembly bytes — **not a real Golden compiled PE or hardware verification receipt**. They cover 32-bit overflow/two's-complement, deterministic replay, independently supplied vector order, source/assembly identity, unknown field/delegate injection, duplicate property, changed width/signedness/high bits, case order/truncation, output mutation, blocked cases, cardinality, cancellation, source immutability and input list mutation. Companion build, full Golden source capture, OS sandbox and native RTL/formal tests remain separate required blockers where not independently completed.
