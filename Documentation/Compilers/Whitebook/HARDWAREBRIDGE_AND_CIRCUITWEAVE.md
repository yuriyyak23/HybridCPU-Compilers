# HardwareBridge v1 and the independent CircuitWeave boundary

**Whitebook appendix · source review 2026-11-11.** Purpose: record the **implemented** software-compiler bridge without inventing a hardware compiler inside HybridCPU-Compilers. [Whitebook](README.md) · [Source owners](SOURCE_OWNERS_AND_BOUNDARIES.md).

## 1. Role of the bridge

[HardwareBridge](../../../Compilers/HybridCPU_Compiler/HardwareBridge/Docs/README.md) is a **compact opt-in read-only adapter** inside the existing HybridCPU software compiler. It exports reproducible diagnostic inputs to an independent producer of hardware bindings/proofs. It is not an alternative VLIW compiler, not C# → SystemVerilog, not a Hardware Bindings resolver and not a verification authority.

```text
HybridCPU-Compilers (software)
  Caller-owned C# / PE / references
       ↓
  SourceSnapshot  -- ordered exact input identities and role boundaries
       ↓
  Roslyn Companion / Restricted CIL importer (existing code)
       ↓
  CompilerFacts   -- projected source/graph/PE/PDB facts or Rejected
       ↘
  Trusted Golden adapter -- separately explicit finite scalar observations
       ↓
  hybridcpu.hardwarebridge.sidecar/v1  (unqualified)

                ───── trust / repository boundary ─────

CircuitWeave (independent)
  exact Binding Manifest + source closure verifier
  → owned hardware admission / HwComponent / HWIR / CIRCT
  → external simulator + independent property/equivalence verification
  → qualified, scope-specific evidence

CircuitWeave-Studio (read-only consumer)
  → published artifact inspection / proof-aware navigation
```

**Hardware Attributes** are hardware intent; **Hardware Bindings Manifest** establishes independently checkable source-to-hardware correspondence; **HwComponent** expresses hardware structure. These are **CircuitWeave-owned concepts**. Do not add attributes to or rewrite the Golden ISE to make HardwareBridge function.

## 2. Real project layout and opt-in boundary

| Area | Existing file | Semantic owner |
|---|---|---|
| Standalone opt-in project | [HardwareBridge.Companion.csproj](../../../Compilers/HybridCPU_Compiler/HardwareBridge/HardwareBridge.Companion.csproj) | Explicit project reference to Roslyn; `HARDWAREBRIDGE_COMPANION` source gate |
| Source snapshot | [BridgeSourceSnapshotV1.cs](../../../Compilers/HybridCPU_Compiler/HardwareBridge/SourceSnapshot/BridgeSourceSnapshotV1.cs) | Caller-supplied virtual UTF-8 sources/PE references, count/size/duplicate/traversal controls |
| Sidecar codec | [BridgeSidecarCodecV1.cs](../../../Compilers/HybridCPU_Compiler/HardwareBridge/SourceSnapshot/BridgeSidecarCodecV1.cs) | Deterministic JSON payload and integrity check |
| Compiler-facts DTO | [BridgeCompilerFactsV1.cs](../../../Compilers/HybridCPU_Compiler/HardwareBridge/CompilerFacts/BridgeCompilerFactsV1.cs) | Source/graph/diagnostic records, no new importer |
| Companion adapter | [CompanionBridgeProjectionV1.cs](../../../Compilers/HybridCPU_Compiler/HardwareBridge/CompilerFacts/CompanionBridgeProjectionV1.cs) | Calls **existing** Roslyn + Restricted CIL; gated to opt-in project |
| Golden sample | [TrustedAddWordOracleV1.cs](../../../Compilers/HybridCPU_Compiler/HardwareBridge/GoldenOracle/TrustedAddWordOracleV1.cs) | Host-allowlisted synchronous 32-bit scalar delegate; no reflection loading |
| Test harness | [HardwareBridge.SelfTests.csproj](../../../Compilers/HybridCPU_Compiler/HardwareBridge/Tests/HardwareBridge.SelfTests.csproj) | Standalone positive/negative tests without external xUnit dependency |
| Exact serialization spec | [sidecar-v1.md](../../../Compilers/HybridCPU_Compiler/HardwareBridge/Docs/sidecar-v1.md) | Wire-level source of truth; this Whitebook summarizes, does not redefine it |
| Example | [example-candidate.json](../../../Compilers/HybridCPU_Compiler/HardwareBridge/Docs/example-candidate.json) | Illustrative **rejected compiler** facts + test-only Golden observation; not an accepted program |

The opt-in project sets `EnableDefaultCompileItems=false` and explicitly selects source files from its three feature directories. The typed projection depends on Roslyn and CIL and is not compiled into the default upper compiler as an automatically available API. Its own `Directory.Build.props` keeps generated metadata outside its source subtree. No direct CircuitWeave or Studio assembly references are necessary.

## 3. SourceSnapshot v1

A caller passes an explicit `BridgeSourceRequestV1`: assembly name, language host mode, exact ordered source texts/virtual paths, ordered reference images, root selectors with optional token and exact generic type/method arguments, and **disjoint** body-reference versus metadata-only indices.

Properties enforced by the bridge include:
- limited document, reference and root count, source/image bytes and aggregate size;
- virtual path safety, case-insensitive duplicate detection and duplicate-reference rejection;
- immutable/detached views and copying of caller-controlled reference buffers;
- exact source role: `owned-body` / `metadata-only` / `unselected`;
- digest consistency and a deterministic representation of declared inputs.

The bridge-generated `SnapshotDigest` hashes its **own** serialized source preimage. It **does not equal** Roslyn Companion's `SourceInputDigest` and must not be substituted for it. The bridge cannot assert that a metadata declaration has an owned method body, nor find arbitrary dependencies via filesystem discovery.

## 4. CompilerFacts v1

The optional [typed projection](../../../Compilers/HybridCPU_Compiler/HardwareBridge/CompilerFacts/CompanionBridgeProjectionV1.cs) copies request inputs, checks ownership and invokes the existing `ManagedSourceCompanionV1.CompileBodyWorld`. Successful facts require:
- actual Roslyn PE and PDB with matching exact `CompanionArtifactIdentityV1`;
- real admitted `StandaloneRestrictedModule` graph, closed over its method identities;
- exact root selection (declaring type, method, token when supplied, closed type/method generic context);
- imported-body provenance belonging to **primary PE** or explicitly owned body reference images;
- no invalid borrowing of primary PDB source-map rows for dependency methods;
- bounded methods/edges, consistent diagnostics, no fabricated hashes on refusal.

The semantic statuses are `Candidate` or `Rejected`, **not** `Verified`. A candidate graph is still software compiler facts; downstream hardware admission, timing, clocks, memories and implementation equivalence remain undecided.

## 5. GoldenOracle v1

`TrustedAddWordOracleV1` only executes an explicitly registered, **host-reviewed** `Func<uint,uint,uint>` under the fixed adapter ID `explicit-trusted-addword32/v1`. The input is bounded in case count and 32-bit canonical words; output is an explicit bit-pattern. Signedness modifies interpretation, not storage.

- `Observed` means **the registered delegate returned** the recorded value for an admitted input. It does **not** mean the delegate is pure, correct or equal to ISE/hardware.
- `Blocked` is returned on unsupported input width/sign/shape or throwing delegate; cancellation is independent.
- The bridge does not synthesize a combinational circuit and does not compare Golden values with RTL.
- Stateful, async, heap/native and exception-oriented operations, memory and clock semantics are not covered by this v1 adapter.
- A caller-controlled delegate executes **in-process** and cannot be sandboxed or preempted by the bridge. A host must not accept untrusted delegates from sidecar metadata.

**Do not extend this simple adapter into a general arbitrary-code runner.** A broader Golden system requires separate threat, state, replay, time/clock and evidence contracts.

## 6. JSON sidecar and integrity

The wire format is `hybridcpu.hardwarebridge.sidecar/v1`: a top-level `Schema`, `PayloadSha256` and `Payload` object with `Source`, optional `Facts` and optional `GoldenOracle`. The expected SHA-256 is over exact compact UTF-8 bytes of the `Payload` object using the codec's defined serialization order. These are **checksums**, not signatures.

Consumers must verify the format and size budget, requested snapshot digest, role/identity consistency and exact source/reference closure independently. Unknown schema, malformed JSON, duplicate fields (per parser policy), stale digest, wrong graph or missing ownership must **refuse**. A trustworthy attestation needs a separate actor, policy and evidence, not a property named `Verified`.

The checked-in [example](../../../Compilers/HybridCPU_Compiler/HardwareBridge/Docs/example-candidate.json) deliberately contains **Rejected** compiler-facts with a test-only Golden `Observed` record. Do not use it as a known-good RTL fixture.

## 7. Future consumer path and division of responsibilities

1. A tool with explicit permission invokes HardwareBridge on caller-owned inputs and records current source/assembly identity and compiler-facts export.
2. A separate CircuitWeave ingestion adapter validates exact matching **Hardware Binding Manifest + source bytes/assembly closure**. No name-only fallback or ambiguous generic selector.
3. CircuitWeave admits a bounded C# hardware subset or independent `HwComponent`, builds its **own** canonical portable HWIR and generates candidate RTL via CIRCT/IP providers.
4. Independent verification processes Golden values and RTL simulation/formal/equivalence evidence under exact assumptions, tool/version/hash, target and temporal scope. Syntax success and simulator exit zero do not establish equivalence.
5. CircuitWeave publishes immutable, evidence-labelled outputs; Studio only visualizes these outputs and their limits. Viewer labels do not grant hardware authority.

More on the independent projects: [CircuitWeave](https://github.com/yuriyyak23/CircuitWeave), [External EDA adoption](https://github.com/yuriyyak23/CircuitWeave/blob/main/docs/roadmap/09_External_Tool_Adoption_2026-11-10.md), [CircuitWeave-Studio](https://github.com/yuriyyak23/CircuitWeave-Studio).

## 8. Practical checks

```bash
dotnet build Compilers/HybridCPU_Compiler/HardwareBridge/HardwareBridge.Companion.csproj -c Release
dotnet run --project Compilers/HybridCPU_Compiler/HardwareBridge/Tests/HardwareBridge.SelfTests.csproj -c Release
```

The self-test suite covers deterministic serialization, wrong/stale/duplicate inputs, ownership and source-map refusals and scalar Golden edge cases; it is **not** an EDA test. Check actual command exit, SDK, inputs and fresh outputs. A missing .NET 11/required reference/tool remains `NOT RUN`, not `PASS`.

**Non-goals:** hardware DSL, C#→RTL, Hardware Bindings resolver, CIRCT, native EDA execution, waveform visualization, memory/clock modelling, formal proof or ABI inference. See [build/evidence rules](BUILD_TEST_AND_RELEASE.md).
