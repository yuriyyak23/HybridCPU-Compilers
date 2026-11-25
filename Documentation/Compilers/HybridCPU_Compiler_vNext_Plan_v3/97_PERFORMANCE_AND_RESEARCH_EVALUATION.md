# 97 — Performance / research decisions

Existing SchedulerBenchmark already records stage/total/resource/search measurements and threshold48/49,192/193 plus DAG/mixed corpus. Do not claim absence of all datasets or create a second benchmark suite.

Сохранённые scoped data показывают deterministic selection; без измерения actual final image/cycles/runtime нельзя заявлять profitability. Исторические corpus/template counts не означают all-ISA coverage и не являются текущим baseline.

## Next measurement scope

Pick a concrete cost/objective, prioritizing the C# source/import consumer. Record only
metrics that substantiate that objective (for example allocations for a storage change,
compile latency for reuse, target cycles for schedule quality), exact corpus/host/mode
and correctness relation. Cold/warm, working set, all search counters or runtime datasets
are required only when the claim depends on them; no universal measurement ledger.

Keep host runtime effects distinct from HybridCPU algorithm/code quality. .NET11 hardware/JIT/CLI/server changes can shift host timings without target improvement; compatibility and cold/warm controls are in98. Microbenchmark guards become release thresholds only after variance is characterized.

Pooling/spans/frozen collections/SIMD/generic math are candidates after hot-path evidence and lifetime/overflow/determinism controls. Parallel units/cache daemon only after economic measurements and deterministic merge/invalidation acceptance.

PGO research requires static admission preserved, off/stale/corrupt controls, repeat identities, training/evaluation separation and both compile overhead and output/runtime benefit. Do not weaken budgets/precedence to manufacture a win.

Paper-quality raw tables/statistics/hardware/profile capture archive is optional research scope, not mandatory evidence for every architectural patch. Success means reduced ownership/duplication/invalid states, stronger scoped determinism/diagnostics or measured cost/quality improvement. New API syntax alone is not success.
