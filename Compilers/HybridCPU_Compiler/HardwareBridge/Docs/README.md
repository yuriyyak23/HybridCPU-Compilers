# HardwareBridge: read-only, unqualified software export

This existing directory owns the compact JSON sidecar spec: [sidecar-v1.md](sidecar-v1.md) and [example-candidate.json](example-candidate.json). See sibling SourceSnapshot/, CompilerFacts/, GoldenOracle/ and Tests/ READMEs.

Architecture: a caller supplies complete source/reference buffers and exact roots; the opt-in Companion adapter deep-copies them and uses the existing source/CIL importer; the bridge projects source digest, output PE/PDB identities, closed software call graph and primary-only source map. A separately **reviewed and explicitly registered** scalar Golden delegate may produce observations. All output is `unqualified`/diagnostic; even `Candidate` and `Observed` have zero hardware authority.

Build boundary: portable DTO/codec checks, default-off legacy HWIR, explicit pinned CircuitWeave migration fixtures and production Companion each have distinct dependencies and evidence. Production Companion uses the software Core → Cil → Roslyn → Companion dependency-first build order. The adapter is compiled only in the project defining `HARDWAREBRIDGE_COMPANION`. The top-level compiler also has a sibling ISE dependency for its own build path.

See [current status](current/STATUS.md), [wire compatibility](current/COMPATIBILITY.md), [blockers](current/blockers.md), [evidence](current/evidence-index.json), and the [document inventory](current/document-inventory.md). The current production build failure and the unrelated private preflight refusal are separate from passing portable codec checks.

The external CircuitWeave consumer must validate the source closure, qualify Bindings/target semantics, independently verify source-to-HWIR-to-RTL evidence and reject stale/missing artifacts. Studio consumes only separately published read-only artifacts, not this bridge directly.


The separate opt-in [bounded AddWord HWIR adapter](../HWIR/README.md) now
connects the existing source snapshot to CircuitWeave's pinned Roslyn scalar
compiler. The `CompilerFacts` sidecar remains a software-only path and does not
provide general HWIR or full Processor elaboration.

## 2026-11-19 proposed pipeline integration refactor

- [Detailed HardwareBridge ↔ CircuitWeave integration plan](CircuitWeave_Pipeline_Integration_Plan.md): six HB-INT packages mapped to 16 CW-INT packages; portable project boundaries, source/Csc capture, strict sidecar versioning, independent Golden oracle protocol, legacy candidate parity and no-write tests.

Plan only: v1 remains read-only/unqualified; nothing in this document grants hardware or Physical Design authority.

## HB-INT-02 partial implementation: segmented Csc observation transport

See [HB-INT-02 segmented v2 capture and migration](HB_INT_02_Segmented_Csc_Closure.md). The opt-in Python capture tool reads caller-supplied isolated Csc argv/bytes, hashes bounded binary chunks, and checks Golden source freshness without executing targets. This is a **separate unqualified outer v2 transport**, NOT an update to SourceSnapshot v1, sidecar v1, or evidence authority. Actual Processor capture, independent Core CW-INT-03 reader and downstream hardware/physical gates remain Blocked. Run transport-only regression with Python unittest; it is not a real Csc test.

## HB-INT-03 Candidate graph strictness

[Source/facts Candidate method graph coherence](HB_INT_03_Candidate_Graph_Coherence.md) rejects internally inconsistent roots, method import statuses, direct call edges and primary PDB ownership while preserving v1/v2 JSON schema and candidate-only trust.

## HB-INT-02/03: existing Core v2 large-capture projection (producer only)

[HB-INT-02/03 bounded Core v2 transport adapter](HB_INT_02_Core_V2_Projection.md) maps an already frozen HB-INT-02 segmented Csc capture to the existing Core CW-INT-03 source index v2 contract, with explicit roots, staged bytes, manifest chunks and an unqualified source-identity crosswalk. This is opt-in read-only software-facts interchange, **not** Csc execution, Core source verification, or hardware admission. SourceSnapshot v1 and the HB-INT-03 provenance envelope v2 remain unchanged.


**HB-INT-02 v2 cross-runtime QA:** [HB_INT_02_Core_V2_Parity.md](HB_INT_02_Core_V2_Parity.md) tests the existing Python capture→Core projection (PR #14) byte-for-byte against the production .NET 11 Bridge serializer; synthetic-only, not Core independent import or RTL proof.
