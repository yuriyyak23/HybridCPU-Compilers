# HB-INT-03: strict Candidate software-method graph coherence (v2)

**Plan:** [six HB-INT packages](CircuitWeave_Pipeline_Integration_Plan.md) and [HB-INT-03 v2 interchange](HB_INT_03_V2_Interchange.md). Companion owner: HardwareBridge source/facts codec only. Cross-repo consumer, independent symbol ownership, C#→HWIR, verified RTL and target signoff belong to CircuitWeave.

## Changed behavior — no schema change

`BridgeProvenanceCodecV2.Write` now refuses a `Candidate` envelope whose claimed **software** compiler graph is internally inconsistent. It first preserves the existing v1 SourceSnapshot digest / v2 index/Csc/PE/PDB byte pin checks, then applies bounded checks:

- 1–256 unique successfully imported method identities; exact nonempty software method symbol/type/signature, token, SHA-like identity digest, and method-body provenance.
- Exact root count, unique root identities, each resolved to one imported method with matching declaring type, method name and optional metadata token. A single imported method cannot stand in for two declared roots.
- Direct callees must exist as imported method facts, be unique, and appear in graph edges; each graph edge must have a known caller/callee and agree with the caller's declared direct-callee set. Edge stable identifiers must be unique, total edges ≤16,384.
- Each source-map row must identify an imported **primary** PE method, the declared primary PE/PDB hashes and method metadata token. Nonprimary/metadata-only bodies cannot claim primary PDB ownership.
- Generic-type/method-root instantiations are currently **refused** at this opt-in v2 Candidate boundary: the existing `BridgeMethodFactV1` DTO lacks generic arguments to prove exact instantiation. Existing v1 unchanged. Introduce generic source-owned facts only with a separately versioned producer and paired Core reader, never silently accept a name match.

`Rejected` software facts still serialize as before; they are not promoted by this gate. `Candidate` remains software-import-only, never `Verified`. All source/chunk/provenance JSON property order, versions and digest preimages are unchanged. New failures emit existing `ArgumentException` from the writer; `ReadAndValidate` maps incompatible live input to typed `BridgeV2Refusal.InvalidPin`. Valid prior non-generic software Candidate facts produced by `CompanionBridgeProjectionV1.Project` should remain structurally compatible; actual Companion Csc integration is a **separate failing gate** and is not silently waived.

**This is an internal-consistency guard, not independent truth.** A malicious producer can forge both method rows and checksums. CircuitWeave must independently resolve source bytes, selected Roslyn symbol/body ownership, target semantics, native evidence and trust.

## Tests

```sh
dotnet restore Compilers/HybridCPU_Compiler/HardwareBridge/Tests/HardwareBridge.V2.ContractTests.csproj
dotnet run --project Compilers/HybridCPU_Compiler/HardwareBridge/Tests/HardwareBridge.V2.ContractTests.csproj -c Release
```

The real v2 contract host already checks v1 unchanged bytes, source/PE/PDB/SDK pins, chunk bounds, 1200-source synthetic transport, cancel/negative cases and synthetic Candidate success. New mutation tests refuse unknown/duplicated root, wrong method name/token, unsuccessful method import, duplicated identities, missing/undeclared direct callees, inconsistent edges, wrong primary PDB and unowned source map, plus a changed live Candidate graph after a valid envelope.

Only synthetic software-fact payloads are used; no fake compiler run or RTL proof. CI `hb-int-03-candidate-graph.yml` runs this actual managed project on Linux and Windows with SDK **11.0.100-rc.2.26504.105**, capturing process exit and raw log SHA-256. Locally if `dotnet` is absent the test is **NotRun**; do not claim actual PASS until hosted runner provides it.

## Remaining gates

HB-INT-02 current exact Csc invocation/generator closure and full Golden clean/dirty worktree: **Blocked**. HB-INT-06 Companion selftest compilation currently fails on missing parent compiler Core type `CompilerEmissionPackage` / `HybridCPU.Compiler.Core.IR.Artifacts`: **Failed** until independently fixed; this PR does not edit or mask the parent compiler. Paired private Core import needs explicit repository read entitlement: **Blocked/NotRun**. CW-INT-06/13 generic scalar and sequential exact-method parity, rank4, lane6, Processor top, ToolBridge native simulation/formal, FPGA/ASIC and Physical signoff remain independently gated and are **Blocked/NotRun**.
