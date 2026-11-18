# Open compiler tasks

Status: 2026-09-03. Complete only the first reached, source-confirmed blocker. Do not repair
later diagnostics speculatively.

1. **EH gate — current blocker.** Reconcile the test expectation for a method with native
   finally continuation metadata: final object sections now include the canonical read-only
   `.hcfinally` payload in addition to exception-handling and unwind data. The test must verify
   the full contract, not deny the new required section. This is a test/contract correction,
   not `eh-unwind` qualification.

   Then address one next observed EH failure at a time: final-PC recursive-frame unwind currently
   returns `InvalidMetadata`; rethrow/throw-from-finally currently loses the expected outer
   outcome. The complete gate requires exact CIL lowering for `try/catch/finally`, `leave`,
   `throw`, `rethrow`, and `endfinally`; native transfer; frame roots and safepoints;
   handled/unhandled outcome; malformed/cyclic fail-closed coverage; and loader-backed CPU
   execution of a real image. Keep `eh-unwind` out of `PublishQualifiedWorkstreams`.

2. **One bounded aggregate / receiver-loan route.** Implement caller-owned bounded storage,
   exact `Type&`, non-null and bounds proof, non-escape lifetime, ABI/copy semantics, GC map
   interaction, and caller/callee/link proof. Preserve rejection for general aggregates
   (`HCCIL1810`), reference-bearing inline copies (`HCCIL1209`), and unproven receiver loans
   (`HCSCF-LINK4012`).

3. **Profile-to-graph closure and CoreLib matrices.** Bind source markers to explicit profile
   contracts, importer evidence, reachable NativeAOT graph, compiler-derived requirements, and
   provenance. A marker or consumer declaration is never admission authority.

4. **Static contract/channel metadata.** Extend deterministic manifests only after an explicit
   platform wire/ownership contract exists. No reflection, runtime generation, or capability
   grant follows from generated metadata.

5. **Tool-only performance work.** Establish a baseline corpus and instrumentation outside
   production Core. If a real scheduler scalability blocker is reached, improve
   `HybridCpuLocalListScheduler` while preserving deterministic output, DAG correctness, and
   malformed/cyclic fail-closed behavior. Closure count is not backend proof.

6. **NativeAOT dependency body authority and bounded ABI corpora.** Replace wholesale authority
   for neighbouring dependency DLL RVA bodies with exact multi-module presentation or a verified
   trusted-runtime-body-module contract. Keep DIM and guest ABI bounded, with explicit
   positive/negative corpora.

7. **Validation and publication only after the code scope is complete.** Run one coherent cycle:
   targeted compiler/runtime tests, full ManagedPortSmoke, Doom CoreCLR tests, Release builds,
   compiler/runtime/RuntimeKernel builds, immutable SDK pack, closure report, diagnostic publish
   with derived requirements, then normal Release publish. Before publish/compile-image inspect
   active processes. A real Doom HCEXE requires two clean publishes in separate `TempEnv`
   folders with equal SHA-256 before delivery.
