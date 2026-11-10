# Phase 25 managed feature set

`hybridcpu.managed-feature-set/v1` is the closed, per-workstream capability descriptor.
It deliberately contains no umbrella “managed runtime supported” bit.

The restricted Phase 25 qualified set is:

- `gc-maps-safepoints`: default-off exact object-reference maps after final allocation;
- `metadata-runtime-lookup`: default-off single-method HCMM registration consumed by
  the independently pinned runtime root walker and moving collector.

The following workstreams remain explicit `Unsupported`: allocation/read/write
barrier helpers, EH/unwind, managed/unmanaged interop, TLS/thread transitions and
debug/source mapping. An image requiring any such row is rejected. Generic/context
lookup, byrefs, interior references, pinning, marshaling, dynamic libraries and
unwind-looking sections are likewise outside the qualified set.

The managed-safety level is `BasicBlockObjectReferences`. FSP and VDSA are disabled
for managed code, so compiler evidence cannot shorten root liveness, suppress a
safepoint or turn a managed address into a fresh runtime address. Both qualified
workstreams remain default-off and require the runtime consumer; the compiler feature
descriptor has no GC, execution, publication, commit or retire authority.
