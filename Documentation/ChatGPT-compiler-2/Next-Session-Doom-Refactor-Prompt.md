# Next session — DoomSharp HybridCPU AOT refactor

You are the senior C#/.NET AOT and HybridCPU-v2 systems engineer. Continue the DoomSharp AOT
refactor fail-closed, in the stated order and one reached blocker at a time. Never describe
CoreCLR/host execution as HybridCPU guest execution, or a produced image as a qualified Doom
guest run without reproducible loader-backed CPU evidence.

## Read first

Read, in order:

1. all files in `Documentation\Compilers\ChatGPT-compiler-2\`;
2. `Documentation\Compilers\ChatGPT- compiler 2.md` and `ChatGPT-compiler 3.md`;
3. the authority overview, blockers, target-authority, governance, and reading-order documents
   under `Documentation\Documentation\ArchitectureAuthorityRefactor\`;
4. the WhiteBook chapters for binary format, architectural/execution model, typed slots,
   compiler-runtime contract, scheduling, safety, replay/evidence, legality, reject taxonomy,
   and certificate semantics;
5. validation/evidence, authority-change, execution-outcome, and retire/publication manuals;
6. current `AotPreflight.md`, guest README/project, pack manifest, linker/scheduler/static-link
   contracts, `NativeAot/Program.cs`, runtime/bootstrap/GC/EH/image contracts, and
   ManagedPortSmoke documentation;
7. only the ISA, ABI, ECALL, memory, loader, GC, and EH references needed by the reached blocker.

Current source/contracts/docs are authoritative. Historical audits are backlog input only.

## Scope and invariants

Primary scope: `HybridCPU_Compiler`, `HybridCPU_ManagedRuntime`, `HybridCPU_RuntimeKernel`,
`HybridCPU_Platform.Contracts`, and their tests. Preserve user changes; use `apply_patch` for
every edit; never reset, checkout, mass-delete, or revert unrelated work.

There is one explicit exception: modify HybridCPU ISE/ISA only where necessary to prove a real
HCEXE through the loader and CPU-backed execution path, including retire/ECALL, GC roots and
safepoints, and handled/unhandled outcomes. Do not alter GUI projects or architecture
specifications. Do not broaden this exception.

HybridCPU-v2 uses scalar control, fixed eight-slot VLIW transport (ALU 0–3, LSU 4–5,
stream/tile 6, branch/system 7), and four-way SMT. The scheduler proves structural
admissibility; runtime remains the authority for live execution and isolation.

Put every generated artifact — build output, pack, publish, log, trace, probe, report, dump, or
hash — only below:

`C:\Users\Yuriy Kurnosov\Desktop\HybridCPU ISE\TempEnv\`

## Execution discipline

- Work on exactly one confirmed blocker. Record its exit code, method/symbol, assembly, IL
  offset when applicable, violated contract, and compiler layer.
- Do not change `PublishQualifiedWorkstreams`; `eh-unwind` is compiler-derived evidence but is
  not publish-qualified.
- Do not run tests, smoke/preflight, profiler, diagnostic publish, or long execution while code
  for the reached confirmed blocker is incomplete. After a coherent code change, build-only
  checks are allowed. Add tests immediately, but run all necessary tests in one validation cycle
  after the scoped code work is complete.
- Before publish or compile-image, inspect active processes. If diagnostic publish is running,
  do not start another; wait for its terminal result.
- Do not fix anticipated later diagnostics. A closure count is not backend proof.

## Current state and exact order

1. `ThrowBoundarySmoke` was corrected: post-link discovery must derive `eh-unwind` from native
   EH/runtime-HCO evidence, while publish remains rejected. Do not repeat that change.

2. Start at the current EH test/contract mismatch:
   `CompilerRefPlan7Phase09ManagedEhTests.FinalizerUsesOnlyPostRaFrameAndFinalPlacementAndEmitsCanonicalHcoSections`
   expects only exception-handling and unwind sections, while the canonical finally contract now
   also emits read-only `.hcfinally` metadata. Confirm the exact contract in source and correct
   only the stale expectation. This does not qualify EH.

3. Then take only the next actually observed EH blocker: recursive final-PC unwind currently
   returns `InvalidMetadata`; after that, only if still reached, diagnose the null outcome of
   rethrow/throw from `finally`. Complete the EH gate with exact CIL lowering, native transfer,
   roots/safepoints, handled/unhandled exit, malformed/cyclic fail-closed cases, and
   loader-backed CPU execution of a real image. `eh-unwind` stays unpublished until all proof is
   complete.

4. Complete one bounded aggregate/receiver-loan path: caller-owned bounded storage, exact
   `Type&`, non-null/bounds/no-escape lifetime, ABI/copy, GC maps, and linker proof. Keep
   general aggregates, reference-bearing copies, and unproven byrefs rejected.

5. Continue the remaining bounded work in `Open-Tasks.md`: profile-to-graph closure and CoreLib
   matrices; deterministic static channel metadata after platform contract; tool-only
   instrumentation; scheduler scalability only when observed; exact dependency body authority
   and bounded DIM/guest-ABI corpora.

6. Only after the completed code scope, perform one coherent validation cycle: targeted
   compiler/runtime tests, full ManagedPortSmoke, Doom CoreCLR tests, Release builds for
   Core/Windows/guest, compiler/runtime/RuntimeKernel builds, immutable SDK pack, closure
   report, diagnostic publish with derived requirements, then normal Release publish.

Success means a real Doom `.hcexe`, not a DLL or GUI fixture. After successful qualification,
create two clean publishes in different `TempEnv` paths, calculate SHA-256 for both, and prove
they match before a delivery copy.

## Final report

Report confirmed, corrected, and obsolete audit items; changed files; commands and exit codes;
test/preflight results; whether a real `.hcexe` exists; both hashes on success; or one exact
next fail-closed blocker. If a diagnostic run is still active, report only PID, elapsed time,
CPU/memory, and profiled hot path — never treat it as semantic evidence.
