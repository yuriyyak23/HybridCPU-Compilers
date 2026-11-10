# HybridCPU RefPlan7 C# AOT demo

The demo has two deliberately separate modes. This keeps the claims aligned
with the final RefPlan7 release manifest instead of presenting host execution as
a HybridCPU managed publish.

| Mode | What it proves |
| --- | --- |
| `RestrictedAot` (default) | Produces real deterministic `.hcexe`, image manifest and provenance for the publish-qualified scalar/CFG/call/recursion/exact-generic subset. |
| `ComponentShowcase` | Runs the actual RefPlan7 compiler, ABI, RuntimeKernel and ManagedRuntime component APIs on the host. These workstreams are qualified default-off and are **not** managed-publish claims. |

## RefPlan7 coverage

`ComponentShowcase.cs` performs 22 fail-fast checks across the completed plan:

- platform/managed ABI and SDK-pack contracts;
- ordinary CIL import, CFG/SSA/phi and Roslyn async `MoveNext` EH admission;
- type layout, deterministic heap allocation, SZARRAY, UTF-16 strings, value
  boxing and precise non-moving GC;
- virtual/interface dispatch, delegates, exact closed generics and
  catch/finally behavior using the C# corpus in `LanguageShowcase.cs`;
- RuntimeKernel boot, managed Thread-to-VT binding, TLS, virtual deadlines,
  volatile/interlocked lowering and trap ABI separation;
- blittable P/Invoke thunk planning, bounded retained reflection and metadata;
- cooperative Task/continuation/cancellation plus a real C# async state machine;
- validation of the checked-in Phase 16 evidence-bound final release manifest.

`EntryPoint.cs` contains six independently publishable body worlds:

| `DemoCorpus` | Entry root | Main coverage |
| --- | --- | --- |
| `ControlFlow` | `ControlFlowRoot` | scalar arithmetic, nested diamonds, CFG joins/phis |
| `Loop` | `LoopRoot` | `for`, `while`, `do/while`, loop-carried values, break/continue |
| `CallGraph` | `CallGraphRoot` | exact direct calls and a shared callee |
| `DirectRecursion` | `DirectRecursionRoot` | one proven countdown SCC |
| `MutualRecursion` | `MutualRecursionRoot` | one proven two-method SCC |
| `Generics` | `GenericRoot` | nested exact `Int32` generic instantiations |

The worlds remain separate because V1 admits one recursive SCC per presentation
and the existing HCO linker has a fail-closed signed-16 direct-call relocation.

The methods in `EntryPoint` are static by design: the current restricted
publication contract requires a static parameterless startup root and an exact
presented body world. This is not a claim that RefPlan7 only supports static
methods. `LanguageShowcase` now includes constructors, private instance fields,
properties, inheritance, a virtual override, interface implementation,
closed-instance method-group delegates and a generic `Box<T>` instance type.
`ComponentShowcase` additionally resolves those shapes through the actual
ManagedRuntime dispatch and delegate tables.

See `RefPlan7Coverage.md` for the phase-by-phase executable coverage matrix and
the distinction between real `.hcexe` publication and host component execution.

## Install the RefPlan7 pack

The demo wrapper completes two source-tree packaging details that the historical
base installer does not yet carry forward: the Phase 15 pack-file identity and
the `HybridCPU.Platform.Contracts.dll` adapter dependency.

```powershell
$runtime = '\Documents\HybridCPU-ExternalSources\dotnet-94ea82652cdd4e0f8046b5bd5becbd11461482ca\src\runtime'
.\HybridCpuAotAddDemo\Install-RefPlan7SdkPack.ps1 `
  -IlCompilerDirectory "$runtime\artifacts\bin\coreclr\windows.x64.Release\ilc" `
  -RuntimeReferenceDirectory "$runtime\.dotnet\shared\Microsoft.NETCore.App\10.0.5"
```

The installer is immutable/fail-closed and refuses a populated destination.
Use a new explicitly named destination when refreshing a pack. Pass
`/p:HybridCpuSdkPackRoot=C:\exact\pack\` when it is not in the default location.

## Run the component showcase

```powershell
dotnet run --project .\HybridCpuAotAddDemo\HybridCpuAotAddDemo.csproj -c Release `
  -p:DemoMode=ComponentShowcase
```

This is a host executable used to exercise component contracts. It explicitly
prints that managed publication is unsupported.

## Publish real HybridCPU images

Publish one corpus:

```powershell
dotnet publish .\HybridCpuAotAddDemo\HybridCpuAotAddDemo.csproj -c Release `
  -r hybridcpu -p:DemoMode=RestrictedAot -p:DemoCorpus=Generics `
  -p:PublishDir=.\HybridCpuAotAddDemo\publish-refplan7\Generics\
```

Or run the host showcase and publish all six corpora:

```powershell
.\HybridCpuAotAddDemo\Publish-RefPlan7Corpora.ps1
```

Each publication emits `HybridCpuAotAddDemo.hcexe`, its image sidecar and
`.provenance.json`. Provenance binds the pinned ILCompiler graph, runtime
references, pack files, managed graph, ordered objects, backend digest and,
where applicable, bounded-recursion proof and post-RA stack evidence. There is
no LLVM, host-JIT or host-native fallback on this path.

See `UnsupportedScenarios.md` for the exact boundary. Successful C# compilation
or host execution alone is not a HybridCPU publish-support claim.
