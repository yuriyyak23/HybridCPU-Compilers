# GoldenOracle v1 — deliberately tiny trust boundary

Only the explicitly registered AddWord32 adapter executes a delegate. It accepts 1–512 ordered vectors with width 32 and the registered signedness, reports unsigned decimal bit patterns and returns `Observed` only when every case succeeds. `HB3001` count, `HB3002` adapter ID, `HB3003` width/sign/bits and `HB3004` delegate exception are blocked cases or batches. The reviewed source label is caller-supplied. Runtime Golden revision, exact source bytes, compiled assembly hash/MVID and method signature/token must be independently established for a live qualification. For the workflow’s source pin and execution status see [current status](../Docs/current/STATUS.md).

Only `TrustedAddWordOracleV1.RegisterExplicitlyTrusted(Func<uint,uint,uint>, sourceLabel, signed)` can construct an executable adapter. The **host** must provide and review a stateless/pure AddWord-style delegate; bridge cannot attest purity, prevent deliberate delegate side effects or impose a timeout on it. Do not accept delegates, assembly identities or invocation names from sidecar JSON. No `Assembly.Load`, `Process.Start`, shell, search, dynamic code or general interpreter.

`Observe` executes only this explicitly registered adapter with exact canonical 32-bit signedness and bounded vector count, returning raw modulo bit patterns with `Observed` or `Blocked`. Wrong host-provided adapter ID, width, sign, noncanonical high-half bits, exception or unsupported input is blocked. Cancellation is checked before and after each call but a malicious/nonterminating delegate cannot be preempted here. No heap, state, native, EH or side-effect semantics are supported; there is no implicit latency, reset, timing or RTL comparison. The status **never** means formal proof, equivalence or hardware acceptance.

## HB-INT-04: source-bound Golden raw trace v1

`GoldenRawTraceCodecV1` is a **separate** read-only, non-executing protocol; `TrustedAddWordOracleV1` and the v1 sidecar remain unchanged. See [raw-trace-v1.md](raw-trace-v1.md) for exact byte/clock/step/memory/replay contracts and required Core-owned gates. These structs only serialize caller-recorded observations; no assembly loading, host execution, sandbox, clock inference or trust promotion is performed here.

## HB-INT-04 follow-up: source-bound AddWord vector replay

The optional [bound-vectors-v1.md](bound-vectors-v1.md) describes `GoldenBoundVectorsCodecV1`: it binds the unchanged reviewed AddWord observation to original source bytes/digest, supplied compiled assembly bytes/digest, exact root, reviewed label, and **independently requested ordered vectors**. The codec does not invoke any delegate or grant verification authority. The previously merged step trace DTO and sidecar/v1 remain unchanged. Independent Golden attestation and Golden↔RTL equivalence require Core CW-INT-10/11.

See also [R3-01 scalar session v2](r301-scalar-session-v2.md) for stricter pinned host replay identity. `Observed` is finite evidence for the selected call or host-recorded step. Clock, reset, temporal behavior, full Processor and formal equivalence have their own external evidence obligations.

## Golden identity reviewed for migration

The reviewed Golden pin `6946ccab9d888f7dae0a4d8e18480f436138acd9` contains `HybridCPU_ISE/CloseToHSL/Core/Execution/Scalar/ALU/ScalarAluOps.cs`, Git blob `c375dd8f9388fb09bf0cb8c4c09a06f3a25403bc`, raw SHA-256 `f1726c298722496f1218aeee41de7c8f75c79c26a36c0e606cae7e8b5123c5fe`. This identifies the source file for the selected `ScalarAluOps.AddWord` migration scope. It does not pin the token for every compiled assembly. Historical Debug and Release attempts used different PE/PDB and MethodDef identities. Reestablish PE SHA/MVID, MethodDef/signature, selected body closure and independent expected vectors per run. Historical 8,292-vector claims belong to the specific receipts listed in the [archived integration history](../Docs/CircuitWeave_Pipeline_Integration_Plan.md) and are outside the 1–512-vector AddWord adapter invocation contract.
