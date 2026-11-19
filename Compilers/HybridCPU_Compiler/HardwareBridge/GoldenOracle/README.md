# GoldenOracle v1 — deliberately tiny trust boundary

Only `TrustedAddWordOracleV1.RegisterExplicitlyTrusted(Func<uint,uint,uint>, sourceLabel, signed)` can construct an executable adapter. The **host** must provide and review a stateless/pure AddWord-style delegate; bridge cannot attest purity, prevent deliberate delegate side effects or impose a timeout on it. Do not accept delegates, assembly identities or invocation names from sidecar JSON. No `Assembly.Load`, `Process.Start`, shell, search, dynamic code or general interpreter.

`Observe` executes only this explicitly registered adapter with exact canonical 32-bit signedness and bounded vector count, returning raw modulo bit patterns with `Observed` or `Blocked`. Wrong host-provided adapter ID, width, sign, noncanonical high-half bits, exception or unsupported input is blocked. Cancellation is checked before and after each call but a malicious/nonterminating delegate cannot be preempted here. No heap, state, native, EH or side-effect semantics are supported; there is no implicit latency, reset, timing or RTL comparison. The status **never** means formal proof, equivalence or hardware acceptance.

## HB-INT-04: source-bound Golden raw trace v1

`GoldenRawTraceCodecV1` is a **separate** read-only, non-executing protocol; `TrustedAddWordOracleV1` and the v1 sidecar remain unchanged. See [raw-trace-v1.md](raw-trace-v1.md) for exact byte/clock/step/memory/replay contracts and required Core-owned gates. These structs only serialize caller-recorded observations; no assembly loading, host execution, sandbox, clock inference or trust promotion is performed here.
