# Unsupported and fail-closed scenarios

RefPlan7 closes 18 managed workstreams at the component boundary, all as
`QualifiedDefaultOff`. The SDK-pack manifest now promotes only the seven
workstreams that are wired through the managed graph and restricted-image path.
The following remaining component scenarios are demonstrated by
`ComponentShowcase`, but cannot be published as a general managed `.hcexe`:

- general classes, heap allocation policies beyond the admitted bindings, and GC runtime execution;
- value types and boxing beyond the admitted object/array/string/static graph shapes;
- virtual/interface dispatch, delegates and function pointers;
- managed exceptions/unwind and fault-to-trap integration;
- managed threads, TLS, synchronization and the memory model;
- P/Invoke/interop, bounded reflection and metadata lookup;
- Task, continuations, timers and C# async state machines.

`LanguageShowcase.cs` supplies real C# language shapes for these areas, while
`ComponentShowcase.cs` exercises the corresponding compiler/runtime component
contracts. Both execute on the host; neither path silently labels them as
publish-qualified HybridCPU application behavior.

## Restricted AOT boundary

`HybridCPU.DotNetAot.ScalarControlFlowV2` rejects unsupported input instead of
falling back to host JIT, host-native code, LLVM or another backend. Rejections
include:

- managed references or runtime helpers outside the scalar profile;
- `switch`, irreducible CFG, malformed CIL and invalid stack/local joins;
- unsupported opcodes, types, layouts or calls outside the explicit body world;
- open generics or instantiations not proven exact by the pinned graph;
- recursion without paired depth and post-RA stack budgets.

## Bounded recursion

The V1 proof accepts one SCC of static `Int32(Int32)` countdown methods, one
recursive edge per method, an exact `arg0 - 1` transition, a dominating
`arg0 == 0` terminating path and non-negative constant ingress. These fail:

```csharp
static int Unguarded(int value) => Unguarded(value - 1);
static int NonDecrementing(int value) =>
    value == 0 ? 0 : NonDecrementing(value);
static int DynamicIngress(int value) => Countdown(value);
```

Exceeding the configured depth or conservative stack budget is a stable
compile/link rejection, not a runtime stack-overflow policy. Compiler metadata
carries proof and provenance only; loader/runtime/ISE retain execution, fault,
freshness, publication, commit and retire authority.
