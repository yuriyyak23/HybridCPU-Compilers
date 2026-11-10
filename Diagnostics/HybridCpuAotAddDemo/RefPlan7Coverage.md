# RefPlan7 demo coverage matrix

The demo covers every completed RefPlan7 phase, but it does so through two
different evidence paths. `AOT image` means the scenario is accepted by the
restricted SDK publish path and produces `.hcexe`. `Component` means the real
RefPlan7 compiler/runtime contract is executed on the host with its explicit
qualification option; it does not imply general managed publication.

| Phase | Capability exercised by this demo | Path |
| --- | --- | --- |
| 00 | CIL import, canonical CFG/SSA/phi and deterministic restricted images | AOT image + component |
| 01 | platform/managed ABI identities, RuntimeKernel boot and SDK-pack contract | Component |
| 02 | reference null check, object/type layout, instance field lowering and storage | Component |
| 03 | deterministic heap allocation and active object identity | Component |
| 04 | SZARRAY, UTF-16 strings, value boxing, static fields and one-time type initialization | Component |
| 05 | precise non-moving GC with a transitive object-reference edge | Component |
| 06 | constructors, instance fields/properties, inheritance, override, interface dispatch, casts and runtime dispatch tables | Component |
| 07 | closures, method groups, closed-instance delegates and exact managed function pointers | Component |
| 08 | exact generic methods and generic instance types; three-body generic `.hcexe` corpus | AOT image + component |
| 09 | CIL catch/finally import, EH registration, runtime catch dispatch and C# finally behavior | Component |
| 10 | precise TrapAbi record, RuntimeKernel classification and zero-address fault mapping to `NullReferenceException` | Component |
| 11 | managed Thread-to-VT binding, isolated TLS and multi-thread GC rendezvous | Component |
| 12 | volatile/interlocked lowering, executable memory model, CAS and recursive Monitor | Component |
| 13 | blittable P/Invoke thunk, exact native resolver, pinned roots and deterministic host-service transition | Component |
| 14 | retained public metadata, bounded type/member lookup and C# reflection shape | Component |
| 15 | Task continuation, result/fault state machinery, cancellation, virtual deadline and Roslyn async state machine | Component |
| 16 | checked-in final release-manifest validation and repeated byte-identical publication | AOT image + component |

The six `EntryPoint` corpora are the real publishable subset: scalar control
flow, loops, direct call graphs, bounded direct/mutual recursion and exact
closed generics. The RefPlan7 pack declares all 18 managed workstreams
`QualifiedDefaultOff` and promotes an explicit seven-workstream subset through
the managed graph/restricted-image path. EH and the remaining component rows
above are intentionally not folded into the `.hcexe` entry world.
