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
