# HardwareBridge v1: read-only, unqualified export

This existing directory owns the compact JSON sidecar spec: [sidecar-v1.md](sidecar-v1.md) and [example-candidate.json](example-candidate.json). See sibling SourceSnapshot/, CompilerFacts/, GoldenOracle/ and Tests/ READMEs.

Architecture: a caller supplies complete source/reference buffers and exact roots; the opt-in Companion adapter deep-copies them and uses the existing source/CIL importer; the bridge projects source digest, output PE/PDB identities, closed software call graph and primary-only source map. A separately **reviewed and explicitly registered** scalar Golden delegate may produce observations. All output is `unqualified`/diagnostic; even `Candidate` and `Observed` have zero hardware authority.

Build boundary: `HybridCPU_Compiler.csproj` does not directly reference Roslyn/CIL, so its unchanged default recursive Compile cannot compile the typed adapter. That file is gated behind `HARDWAREBRIDGE_COMPANION` and built by `HardwareBridge.Companion.csproj` only, inside the allowlist. Both project files and generated-artifact exclusions are contained under HardwareBridge. No existing compiler, ISE, CircuitWeave or Studio file is modified.

Compiler-facts projection becomes available to hosts referencing the opt-in project, **not** automatically via the parent HybridCPU_Compiler assembly. The parent project itself also references an external sibling `HybridCPU ISE` project; a standalone checkout may not build it.

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
