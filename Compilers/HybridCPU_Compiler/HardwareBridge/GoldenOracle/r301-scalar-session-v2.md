# R3-01 — opt-in Golden oracle scalar session v2 (producer only)

**Status:** bounded explicitly invoked *managed fixture* observation, not trusted Golden CPU execution, PE/PDB/source proof, Core qualification, or RTL acceptance. The real Golden HybridCPU-v2 replay host is **NotRun** without explicit read-only approval; stateful Processor/clock/RAM/stall/retire and Core CW-INT-10/11 remain **Blocked**.

## Why a new shape

The existing `golden-observation/v1`, `golden-bound-vectors/v1`, and `golden-raw-trace/v1` wire schemas are unchanged. The v1 bound-vector codec accepts an externally supplied observation that *could* have been modified after host execution and rehashed. Its checksum validates interchange consistency, never origin. The new optional `hybridcpu.hardwarebridge.r301-scalar-session/v2` performs the reviewed delegate invocation **inside** capture, immediately pins the resulting ordered cases, and refuses duplicate requested vectors before executing. No path, method, delegate, binary or executable may be selected by JSON.

No new general runner or loader was added. A trusted caller must supply an already-loaded, reviewed **static** `Func<uint,uint,uint>` delegate, exact original source bytes, an externally captured SourceSnapshot, assembly bytes for the already-loaded method, a selected root index, independent ordered vectors, reviewed label and elapsed budget. The v2 method checks:

- Source snapshot consistency, original document bytes SHA/size/order, explicit selected source root type/name/optional metadata token.
- Already-loaded delegate method is static, non-generic, exact `uint(uint,uint)` with root-matching declaring type/method and no instance target.
- Runtime assembly name matches the source snapshot assembly label; its on-disk file has no reparse/symlink path component and matches the **caller-pinned assembly bytes** SHA. Host assembly is checked before and after observation.
- Root method metadata token, assembly hash, module MVID and method ID are copied into v2 receipt. Those are runtime *claims*, not PE/PDB independent provenance; the v2 code does not independently link the source graph to the loaded PE.
- 1..512 *distinct* ordered canonical 32-bit vector pairs with consistent signedness; a bounded diagnostic for each non-Observed case. Caller cannot supply precomputed cases.
- An explicit elapsed budget (greater than zero, up to 30 seconds) is checked between synchronous delegate calls, including after the last call, alongside cancellation. **A stalled/malicious synchronous delegate is not preempted**: the external approved host must enforce its own OS-level process timeout and kill policy. No independent execution sandbox is provided by this package.
- Canonical strict JSON with schema/version/status, duplicate-key rejection, no unknown fields, exact byte-for-byte compact serialization, and a replay SHA-256. `ReadAndValidate` requires an independently supplied expected source/host method/PE bytes/root/label/vectors and invokes the strict v1 nested reader.

The v2 JSON properties are: `Schema`, `Scope` (fixed `explicit-delegate-observation-only`), `Status` (`Observed` or `Blocked`), `HostAssemblySha256`, `HostAssemblyName`, `HostModuleVersionId`, `HostMethodId`, `HostMethodToken`, `BoundVectors` (v1 canonical object), `ReplayIdentity` (uppercase SHA-256 over preceding fields). Serialized JSON is limited to `GoldenBoundVectorsCodecV1.MaxJsonBytes + 65536` bytes. SHA is integrity, **not** an authenticating signature or proof that the delegate corresponds to a captured source file.

## Execution stages and provenance

The isolated project `Tests/GoldenBoundVectorsR301.ContractTests.csproj` compiles a deterministic fixture `R301ApprovedFixture.AddWord` into **its own** managed PE and explicitly registers that method. The test reads only its own already-built assembly bytes. It invokes the delegate with overflow/two's-complement vectors, prints its actual loaded assembly SHA-256, method ID, canonical receipt JSON SHA-256 and session replay SHA-256. That test is **Observed (fixture only)**; its synthetic SourceSnapshot is *not* an independent Csc/PE/PDB source closure. No Golden source checkout or Processor is opened, restored, built, written, or executed. The test's time-budget negative case deliberately uses a 30 ms slow *test-only* method and verifies a post-return timeout refusal.

The unchanged v1 raw trace codec now also has a **source-compatible optional reader overload** requiring an independently expected `RootIndex`. Existing 3-argument readers and wire bytes continue to work. HB-INT-04 raw trace tests cover mismatched independent root, step ordinal and logical clock, Before/After transition, illegal word width, effects digest/order, canceled capture and blocked physical CPU scope. No v1 clock-gap normalization is claimed: its historical contract permits strictly increasing host logical clock values (not necessarily contiguous hardware cycles). No host time, physical clock/reset, arbitration, or retire attestation is inferred from DTOs.

## Threat model, negative cases and dependency boundary

The tests reject duplicate vectors; wrong widths or high bits; source bytes/digest drift; changed PE; wrong method/root/token; swapped ordered vector replay; wrong host label; fake PASS/Verified; duplicate/unknown JSON fields; stale receipt hash; mutated post-hoc output with old digest; truncated or noncanonical JSON; cancellation; expired/oversized time budget; illegal step state/effect/clock. Malicious code that can rewrite **all** hashes and externally supplied expectations can still fabricate a self-consistent v2 JSON object. This requires **independent host/source execution attestation** in CW-INT-10; there is no bridge-owned hardware authority.

**Outstanding external contracts:**
1. R1-01/R1-02/R1-03 provide actual executed Csc inputs, immutable source closure, method/PE/PDB provenance. This package consumes pins only and does not create a linker or source verifier.
2. An explicitly approved Golden source checkout plus isolated, reviewed, fail-closed scalar host is needed before reporting actual Golden `Observed`; without this, actual Golden stays `NotRun`.
3. CW-INT-10 owns independent host and source/PE/method attestation plus real Golden replay; CW-INT-11 owns RTL differential comparison and evidence gates. Those cannot be marked Pass by synthetic fixture tests.
4. Processor stateful trace/clock/RAM/stall/retire requires independently specified and qualified semantics; no generalized CPU runner exists here.
5. Workflow `hb-int-04-vectors.yml` builds/runs both existing v1 and new v2 tests on Ubuntu/Windows with .NET 11; `hb-int-04-managed.yml` runs v1 raw trace tests and independent legacy baseline. Logs and SHA inventories are test-only, without upload of raw Golden artifacts.

## Reproduction

```sh
dotnet --info
dotnet run --project Compilers/HybridCPU_Compiler/HardwareBridge/Tests/GoldenBoundVectors.ContractTests.csproj -c Release
dotnet run --project Compilers/HybridCPU_Compiler/HardwareBridge/Tests/GoldenBoundVectorsR301.ContractTests.csproj -c Release
dotnet run --project Compilers/HybridCPU_Compiler/HardwareBridge/Tests/GoldenRawTrace.ContractTests.csproj -c Release
```

**Trust summary:** `Observed` = explicitly invoked reviewed fixture/host method response, not `Verified`, `AcceptedRTL`, source provenance, or Golden correctness. No Core/Golden repository changes or PR are part of R3-01.
