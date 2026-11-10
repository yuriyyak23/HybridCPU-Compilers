# Phase 25B runtime stack walking and moving GC

`hybridcpu.managed-gc-runtime/v1` is a runtime-owned, default-disabled consumer of
Phase 25A HCMG/HCMM metadata. The runtime implementation is physically outside the
compiler and independently pins the managed ABI, target-platform, native ABI and
runtime-pack identities. It rejects schema or digest skew before reading roots.

The qualified subset walks exact object-reference locations in 64 architectural
registers and fixed frame slots. It validates every root and transitive object edge
before mutation, computes reachability, deterministically assigns forwarding
addresses, rewrites object fields and frame roots, and reclaims unreachable objects.
Collection is transactional: invalid metadata, a missing frame slot, an out-of-heap
root or a broken object edge returns the original thread and heap without a partial
forwarding result.

Compiler metadata remains descriptive input. The runtime alone chooses and performs
root walking, reachability, movement and reclamation. This isolated qualification API
does not alter CPU legality, SafetyVerifier, execution, publication, commit or retire
authority, and production moving collection remains disabled.

Managed byrefs/interior references, concurrent or generational collection, barriers,
allocation helpers, suspension coordination, TLS transitions, EH/unwind and interop
remain separate Phase 25 gates. Passing 25B does not enable an umbrella managed-runtime
capability.
