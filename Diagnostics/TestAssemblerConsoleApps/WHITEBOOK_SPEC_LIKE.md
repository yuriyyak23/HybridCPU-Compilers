# HybridCPU ISE TestAssemblerConsoleApps Whitebook

## 1. Purpose

This whitebook documents the `TestAssemblerConsoleApps` SPEC-like diagnostic harness and a historical large-iteration result snapshot. It is not a post-Ref1/RF-10 timing baseline.

The goal is not to claim "official SPEC CPU" equivalence. The goal is narrower and more honest:

- show that the architecture remains meaningful on large dynamic workloads,
- show that throughput does not collapse when iteration counts move from toy runs toward long-running SPEC-like slices,
- show that legality, typed-slot scheduling, replay, and guard surfaces remain measurable rather than hidden,
- show that the harness scales without architectural lies.

Primary evidence bundle for this whitebook:

- command scope: default SPEC-like diagnostic matrix
- iteration budget: `2000`
- artifact root:
  `bin/Debug/net10.0/TestResults/TestAssemblerConsoleApps/20260421_125521_579_matrix`

## Current comparison status after Ref1

The external post-Ref1 audit is retained at
`HybridCPU-v2-архитектура.md`. It confirms functional scenarios, retirement
composition, replay, safety, Stream/Vector and MatrixTile checks, but observes
different total-cycle and memory-wait behavior after the RF-10 timing model.
Therefore the numbers below remain **Pre-Ref1 / pre-RF-10 historical evidence**;
they must not be used as a timing or wall-clock pass/fail baseline for a current
run.

Each simple execution profile now emits the canonical
`timing_memory_report.json` (`timing-memory-report/v3`) alongside `metrics.json`.
For existing machine consumers it also emits
`post_ref1_timing_memory_report.json` with the prior
`post-ref1-timing-memory-v2` schema value. The manifest retains the
`post_ref1_timing_memory` key and adds `timing_memory`; its own schema remains
`diagnostic-run-manifest/v1`.

Both projections separate functional observation from timing comparison and
transport only producer-owned values. `Availability="Available", Value=0`
means measured zero. `Availability="Unavailable", Value=null` means no truthful
producer exists. Pipeline memory wait plus zero legacy burst/byte counters does
not prove absence of memory activity. Fine-grained fetch, admission,
memory-completion, execute, writeback and early-return cycle buckets remain
unavailable; no consumer-side timing cause or cache bucket is reconstructed.
The top-level non-memory remainder is exact: the central pipeline owner counts
every stalled cycle in `StallCycles` and increments `MemoryStalls` only inside
that same decision when `CountMemoryStall` is true. Therefore
`NonMemoryStallCycles = StallCycles - MemoryStalls`; the subtraction fails
closed as `Unavailable` if the nested-counter invariant is ever violated. WAW
remains an event counter, so a numerical match must not be generalized into a
one-WAW-equals-one-cycle contract.

## 2. Executive Summary

At `2000` SPEC-like iterations the harness now sustains stable end-to-end throughput:

- `alu` and `novt` hold `Raw cycle IPC = 3.3538`
- `vt`, `max`, `lk`, and `bnmcz` hold `Raw cycle IPC = 4.2000`
- all six execution profiles complete successfully
- `Pipeline stalls = 0` for every execution profile
- `Physical lane realization rate = 1.0000` for every execution profile
- the replay phase pair reports `100%` ready-hit rate in both stable and rotating phases

This is the key architectural conclusion:

- the design meaningfully benefits from wide packed execution and VT-aware scheduling under long SPEC-like runs,
5
## 3. Methodology

### 3.1 What "SPEC-like" means here

This harness uses repeated, architecturally shaped workload slices rather than a full official SPEC workload. The slices are designed to exercise:

- single-thread integer issue,
- single-thread vector-adjacent issue without VT packing,
- multi-VT packed scalar issue,
- mixed maximum-IPC issue,
- latency-hiding memory issue,
- bank-rotated memory issue,
- replay/certificate reuse behavior.

This is fair to describe as SPEC-like, but not as SPEC-compliant.

### 3.2 Scaling model

Large iteration counts are no longer implemented by inserting a loop backedge into the emitted program image. Instead, the harness now scales by repeating a known-good static reference slice from the host side.

Current slice capacities:

- `SingleThreadNoVector`: `36`
- `WithoutVirtualThreads`: `36`
- `WithVirtualThreads`: `8`
- `MaximumIpc`: `8`
- `Lk`: `8`
- `Bnmcz`: `8`
- `RefactorShowcase`: `8`

This behavior is implemented in:

- `SimpleAsmApp.cs`
  `CreateWorkloadSlicePlan`
- `SimpleAsmApp.cs`
  `ComputeSliceCapacity`
- `SimpleAsmApp.cs`
  `BuildCompiledWorkloadSlice`
- `SimpleAsmApp.cs`
  `ExecuteCompiledSliceRepeatedly`

### 3.3 Iteration input and timeout scaling

The console now asks for the SPEC-like iteration budget at startup when no explicit `--iterations` override is provided. Wall-clock budgets are auto-scaled from the requested iteration count.

Important current policy:

- default matrix suggested iteration count: `250`
- replay suggested iteration count: `1,000,000`
- timeout scaling baseline: `100` iterations
- timeout scaling cap: `512x` the profile base timeout

This behavior is implemented in:

- `Program.cs`
  `ResolveIterationBudget`
- `Program.cs`
  `PromptForWorkloadIterations`
- `Program.cs`
  `ComputeAutoTimeoutMs`

## 4. Historical Top-Line Results at 2000 Iterations

The values in this section predate the post-Ref1 audit. They are retained to
preserve reproducibility of the historical run, not as a current timing target.

### 4.1 Throughput summary

| Profile | Workload shape | Ref slice iters | Slice execs | Retired instr | Cycles | Raw IPC | Retire IPC | Wide-path successes | Partial-width issues |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `alu` | `spec-like-single-thread-int` | 36 | 56 | 10,447 | 3,115 | 3.3538 | 3.6136 | 1,835 | 334 |
| `novt` | `spec-like-single-thread-vector` | 36 | 56 | 10,447 | 3,115 | 3.3538 | 3.6136 | 1,835 | 334 |
| `vt` | `spec-like-rate-packed-scalar` | 8 | 250 | 42,000 | 10,000 | 4.2000 | 4.6667 | 9,000 | 0 |
| `max` | `spec-like-rate-packed-mixed` | 8 | 250 | 42,000 | 10,000 | 4.2000 | 4.6667 | 9,000 | 0 |
| `lk` | `spec-like-latency-hiding-memory` | 8 | 250 | 42,000 | 10,000 | 4.2000 | 4.6667 | 9,000 | 0 |
| `bnmcz` | `spec-like-bank-rotated-memory` | 8 | 250 | 42,000 | 10,000 | 4.2000 | 4.6667 | 9,000 | 0 |

### 4.2 Scheduler, legality, and memory-pressure summary

| Profile | Total bursts | Bytes transferred | Cross-VT cycle groups | Slack reclaim ratio | SMT reg-group rejects | NOP elision skips |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| `alu` | 59,036 | 472,288 | 0 | 0.0000 | 0 | 1,224 |
| `novt` | 59,036 | 472,288 | 0 | 0.0000 | 0 | 1,224 |
| `vt` | 1,035,375 | 8,283,000 | 32 | 0.4255 | 847,125 | 750 |
| `max` | 1,035,375 | 8,283,000 | 32 | 0.4255 | 847,125 | 750 |
| `lk` | 1,035,375 | 8,283,000 | 32 | 0.8158 | 439,250 | 750 |
| `bnmcz` | 1,035,375 | 8,283,000 | 32 | 0.7867 | 502,000 | 750 |

### 4.3 Common invariants across the matrix

Across all six measured execution profiles:

- `Run status = Succeeded`
- `Failure message = <none>`
- `Likely failing stage = NoGrossFailureDetected`
- `Pipeline stalls = 0`
- `Frontend stalls = 0`
- `Branch mispredicts = 0`
- `Width-drop share = 0.0000`
- `Physical lane realization rate = 1.0000`

This matters because it separates true architectural throughput from harness-induced decode or control-flow corruption.

## 5. How to Read the IPC Numbers

Two IPC metrics are printed, and they should not be treated as interchangeable.

### 5.1 Raw cycle IPC

`Raw cycle IPC = InstructionsRetired / CycleCount`

For a current run, compare this only to an independently established post-RF-10
timing baseline. `timing_memory_report.json` records this policy with the
observed cycle and memory-telemetry state; the legacy-named alias exists only
for JSON/manifest compatibility.

This is the main top-line throughput number for article use because it is end-to-end with total cycles in the denominator.

Article-safe interpretation:

- `alu` and `novt` sustain about `3.35` instructions retired per cycle
- `vt`, `max`, `lk`, and `bnmcz` sustain `4.20` instructions retired per cycle

### 5.2 Retire-normalized IPC

`IPC (retire-normalized)` is effectively a retire-bandwidth metric. It reflects how many physical lanes are retired per retire-active cycle.

Article-safe interpretation:

- use it as a supporting width metric,
- do not use it as the only throughput headline,
- it is especially useful when comparing packed wide retire behavior across profiles.

At `2000` iterations:

- `alu` and `novt` retire at `3.6136` physical lanes per retire cycle
- `vt`, `max`, `lk`, and `bnmcz` retire at `4.6667` physical lanes per retire cycle

## 6. Architectural Interpretation

### 6.1 Single-thread baseline still benefits from wide packing

The `alu` and `novt` profiles are intentionally simpler than the VT-packed profiles, but they still deliver:

- `Raw IPC = 3.3538`
- `Wide-path successes = 1835`
- `Physical lane realization rate = 1.0000`

This means the architecture is not only interesting under multi-VT packing. Even the baseline single-thread slices retain meaningful width and do not degrade into scalar-only retirement.

### 6.2 VT-packed execution is where the architecture clearly differentiates itself

The `vt`, `max`, `lk`, and `bnmcz` profiles all sustain:

- `Raw IPC = 4.2000`
- `Retire IPC = 4.6667`
- `Wide-path successes = 9000`
- `Partial-width issues = 0`

This is the strongest evidence that the architecture has a real throughput reason to exist under long-running SPEC-like slices:

- packing survives long runs,
- cross-VT schedule groups remain active,
- the machine continues to issue and retire wide without width drops.

### 6.3 Legality pressure is visible, bounded, and not hidden

In the VT-packed profiles the system reports large counts of register-group legality rejects:

- `vt`: `847,125`
- `max`: `847,125`
- `lk`: `439,250`
- `bnmcz`: `502,000`

This is not a sign of failure by itself. It is evidence that typed-slot legality and certificate-driven scheduling are active and observable. Importantly:

- rejects are measured, not hidden,
- throughput remains high despite those rejects,
- no guard counters indicate owner-context, domain, or boundary violations.

That is a good architectural story: legality constrains issue, but does not collapse the execution envelope.

### 6.4 Memory-oriented slices still hold the wide-throughput line

`lk` and `bnmcz` preserve the same top-line throughput as `vt` and `max` while also showing distinct scheduler behavior:

- `lk` slack reclaim ratio: `0.8158`
- `bnmcz` slack reclaim ratio: `0.7867`

This supports the claim that the architecture is not only good at "clean packed ALU demos". It remains effective when the slice shape is memory-oriented and bank-aware.

## 7. Replay and Certificate Reuse

Replay is measured separately from the raw execution profiles through the replay phase pair workload.

At `2000` iterations:

| Phase | Ready hits | Misses | Checks saved | Invalidations | Hit rate |
| --- | ---: | ---: | ---: | ---: | ---: |
| Stable | 6,000 | 0 | 36,000 | 6,000 | 100.00% |
| Rotating | 6,000 | 0 | 36,000 | 7,999 | 100.00% |

Key interpretation:

- stable and rotating phases both keep perfect ready-hit rate,
- rotating phase pays additional invalidation cost from phase mismatch,
- replay does not need to be described with blanket determinism claims,
- the system exposes reuse and invalidation explicitly.

This is important for article truthfulness:

- replay effectiveness is real,
- replay boundaries are also real,
- the design does not need architectural fiction to look good.


## 8. Why the Architecture Still "Makes Sense" on Large Workloads

The strongest article-grade points are:

1. Throughput remains stable at large iteration counts.
   The `2000`-iteration matrix does not show a progressive collapse. The machine stays in the same throughput class as the validated shorter runs.

2. Wide execution is real, not cosmetic.
   VT-packed profiles sustain `4.2` raw IPC and `4.6667` retire width over `10,000` cycles and `42,000` retired instructions.

3. Legality is visible, not swept under the rug.
   Register-group rejects and slack-reclaim behavior are measurable and interpretable.

4. Replay is useful without being oversold.
   The replay pair shows full reuse hit rate together with explicit invalidation accounting.

5. The harness now scales honestly.
   Long runs are produced by repeating legal reference slices rather than by relying on a broken post-bundling control-flow trick.

## 9. Caveats

For article accuracy, the following caveats should remain explicit:

- this is SPEC-like, not official SPEC CPU,
- the workloads are synthetic reference slices, not full application binaries,
- the batch-summary field `Last observed progress` currently reflects the last executed slice, not the full aggregate run,
- replay metrics are a separate workload surface and should not be mixed directly with raw execution IPC.

These caveats do not invalidate the results. They simply keep the claims bounded and technically honest.

## 10. Reproduction

Interactive default matrix:

```powershell
dotnet run --project .\TestAssemblerConsoleApps\TestAssemblerConsoleApps.csproj
```

Explicit large-iteration run:

```powershell
dotnet run --project .\TestAssemblerConsoleApps\TestAssemblerConsoleApps.csproj -- matrix-spec --iterations 2000
```

Single-profile checks:

```powershell
dotnet run --project .\TestAssemblerConsoleApps\TestAssemblerConsoleApps.csproj -- alu --iterations 2000
dotnet run --project .\TestAssemblerConsoleApps\TestAssemblerConsoleApps.csproj -- vt --iterations 2000
```

## 11. Article-Ready Conclusion

`TestAssemblerConsoleApps` now provides a credible SPEC-like validation harness for HybridCPU ISE:

- it scales to large iteration counts,
- it preserves wide packed throughput,
- it exposes legality and replay behavior instead of hiding them,
- and it avoids the false low-IPC pathology caused by the earlier broken loop-backedge implementation.

In short:

- the architecture remains performance-meaningful under long SPEC-like runs,
- the measured advantage of VT-packed and mixed wide issue persists at scale,
- and the current harness is suitable as a truth-oriented evidence source for an article, as long as it is described as SPEC-like rather than official SPEC.
