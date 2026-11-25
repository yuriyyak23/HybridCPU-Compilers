# 91 — Target architecture from current source

Target — cleaner ownership and reproducible contracts around existing stages.

Native ingress / CIL importer / bounded qualified LLVM mapping → frontend semantic/capability admission → existing shared scheduling/bundling → optional witnessed RA → final agreement/lowering/relocation/annotations/serialization → current artifact/object/link/image owners.

Roslyn companion stays source diagnostics/owned identity/deterministic PE/PDB → existing CIL importer. No general direct source compiler. Runtime emitter remains at concrete ISE/facade edge; live legality/provider/commit/retire/publication stay outside compiler.

Core owns compiler-only IR/stages/policy/artifacts and physical canonical facade. Platform.Contracts owns genuinely shared immutable architecture facts. Frontend and upper/runtime owners retain concrete implementation. Новая selective compilation migration допустима по concrete dependency, не требует нового assembly автоматически.

Evidence plane uses existing scalar summaries, digests, sidecars and ReleaseQualification. Policy/profile/source input versions name output-affecting choices. Observers are nonselecting; accepted profile is per-compile snapshot.

Remaining cache/concurrency/native-host packaging are optional consumer-driven extensions. Target schema/module changes must solve an existing cycle, invalid state or measured latency/memory problem. Compiler language/package version and HybridCPU guest support are separate.

[Invariant contract](02_ARCHITECTURE_INVARIANTS.md), [current APIs](92_API_AND_TYPE_CHANGES.md), [per-contour qualification](P8_FRONTEND_TARGET_QUALIFICATION.md).
