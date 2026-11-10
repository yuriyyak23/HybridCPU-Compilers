# SchedulerBenchmark

Tool-only BenchmarkDotNet lane for `HybridCpuLocalListScheduler`. It measures deterministic
straight-line scalar CIL corpora at 64/512/2048 operations. It is not referenced by production
Core/CIL/NativeAOT projects, does not establish backend legality, and must write BenchmarkDotNet
artifacts only below an explicitly selected `TempEnv` working/output directory.

Build does not execute the benchmark. Benchmark execution belongs to the single final validation
cycle and must use `--artifacts-path` under `TempEnv`.

`--scheduler-cancel-after <positive-milliseconds>` runs the existing 2048-operation fixture once
with a caller cancellation token, after fixture setup. It prints Completed or Cancelled without
returning a partial schedule. This is a host responsiveness probe, not a hard timeout guarantee:
dependency/DAG preparation and an individual cycle search finish before the next checkpoint.
Legacy benchmark/default scheduling remains unchanged. Scheduler instances and input graphs must
be caller-serialized/stable; the token does not establish concurrent reuse or canonical/fallback
backend cancellation. `2147483647` is a supported positive timeout value.

`--fallback-cancel-after <positive-milliseconds>` uses the same fixture/probe for the
program-order scheduler that canonical compilation selects for large blocks. Cancellation
is observed before/after dependency preparation, between blocks/instructions and during
cycle finalization. Individual dependency/DAG/legality operations remain synchronous.
Both probes qualify their direct scheduler stage only; canonical token propagation is separate.

`--canonical-cancel-after <positive-milliseconds>` invokes the token-aware canonical compiler
on a 2048-word native chain through its normal fallback choice. No runtime emission occurs.
Cancellation is observed at compile stage boundaries and inside the selected scheduler;
IR building, bundling, lowering and serialization remain synchronous within each operation.
No partial compiled artifact is returned. Metrics/profile/shadow and frontend adapters still
use their existing APIs until separately wired and validated.
