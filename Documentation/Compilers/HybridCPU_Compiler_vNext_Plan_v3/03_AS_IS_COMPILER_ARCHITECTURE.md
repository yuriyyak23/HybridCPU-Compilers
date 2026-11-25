# 03 — As-is compiler architecture

Состояние physical owners сверять по исходникам и evaluated groups; текущий индекс — [Source index](94_SOURCE_INDEX.md).

Native request → existing IR builder/frontend/capability admission → HybridCpuIrProgramCompiler.Compile → shared ProgramBundler → agreement → PostBundleCompiler lowering/relocation/annotation/serialization → HybridCpuCompiledProgram.

Managed source → Roslyn companion diagnostics/admission → deterministic PE/PDB → RestrictedCilImporterV1 → canonical IR. CIL method/object path и NativeAOT используют shared bundling/post-bundle stages, но сохраняют свои calling convention, RA/EH/metadata, object/link/startup/image responsibilities.

RA — bounded post-bundle transformation: allocation witness, final schedule/bundles, structural agreement, physical operands и final relocation checks. Raw/value-only paths и defaults нельзя случайно менять при convergence.

Compile-only artifact и concrete runtime emission разделены. Runtime adapter validates transport contract/alignment, memory writes/annotations/fetch invalidation; повторной реализации emitter не требуется.

Policy plane: SchedulingPolicyV1, BundlingPolicyV1, CompilationPolicyV1; native effective options и managed options/closure identities. Scoped caller tokens проходят shared orchestration/backend/link/EH/GC boundaries; general inner-loop cancellation latency и shared concurrency не заявлены.

Profile plane: explicit default-off CompileProgramWithProfile, versioned input, smoothing/priority selection и bounded trace. Observers metrics/resource/topology не selectors.

Evidence plane: agreement/proof scalar summaries, source-map/artifact identities и existing ReleaseQualification. Они не снимают runtime authority dependencies.

Physical ownership: canonical facade в Core/Compilation через default glob; upper API runtime groups имеют свой owner. Intentional frontend/tooling exclusions остаются частью project graph, а не незавершённой canonical migration.

LLVM содержит bounded importer/semantic mapping/toolchain probing; broader canonical/runtime/publication reachability требует отдельной проверки. Oracle — validation/research consumer, не production selector.
