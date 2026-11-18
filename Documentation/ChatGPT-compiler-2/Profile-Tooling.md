# Source-profile and static-contract tooling

This tooling improves source feedback; it is not CIL, graph, image, runtime, or publish
authority.

`HybridCPU.Compiler.Analyzers` is separate from production Core. `Kernel.NoHeap` rejects
obvious source allocation, string/boxing, selected allocation-capable APIs, delegate/lambda,
`async`, `dynamic`, reflection activation/loading, and thread-pool constructs. It cannot prove
transitive absence of allocation across assemblies. `Kernel`, `SIP`, `Driver`, and `NoGc` are
markers, not authority grants; in particular `NoGc` does not remove safepoint obligations.

`HybridCPU.Compiler.Generators` emits deterministic static-contract method manifests and digests.
They provide neither a channel, serializer authority, dispatch authorization, nor a runtime
capability. Future channel generation needs a platform-owned wire, storage, and ownership
contract first.

Required closure: source diagnostics -> importer evidence -> reachable graph -> linker/image
requirements -> runtime/publish provenance. Roslyn, ILCompiler, LLVM, and solvers must not be
introduced into production Core.
