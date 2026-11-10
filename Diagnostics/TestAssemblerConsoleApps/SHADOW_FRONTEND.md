# ShadowFrontend modes

All public CPU modes are selectable independently of the diagnostic workload:

| CLI value | CPU mode | Behavior |
|---|---|---|
| `off` (default) | `Off` | Original CPU path with existing LoopBuffer |
| `observe` | `Observe` | Shadow comparison alongside the original route |
| `cold-warm` (also `coldwarm`) | `ColdWarm` | Opt-in immutable-fact serving through existing fresh materialization; original fetch remains |

For example, from the repository root:

```powershell
dotnet run --project Diagnostics/TestAssemblerConsoleApps -- alu --iterations 32 --minimal-logs --shadow-frontend off
dotnet run --project Diagnostics/TestAssemblerConsoleApps -- alu --iterations 32 --minimal-logs --shadow-frontend observe
dotnet run --project Diagnostics/TestAssemblerConsoleApps -- alu --iterations 32 --minimal-logs --shadow-frontend cold-warm
```

The option also applies to named matrices and comparisons, for example
`matrix-smoke --iterations 32 --minimal-logs --shadow-frontend observe`.
The parent persists the mode in each profile; isolated workers configure newly
bootstrapped and directly constructed diagnostic cores before execution through
the existing `CPU_Core.ConfigureShadowFrontend` API. Invalid modes are rejected.

Each child artifact directory contains `profile.json`, a manifest with the selected
mode, and `shadow_frontend.json` with end-of-worker CPU snapshots. The manifest
registers that report. Snapshots expose comparisons/mismatches, served COLD/WARM,
runtime decode/restore and cache telemetry. Registration uses weak references so
diagnostics do not retain replaced cores and their memory; a null snapshot means
the old core was collected. Timed-out/killed workers may not emit a final report;
the selected mode remains in the profile/manifest.

Some architectural suites execute micro-ops or owner probes directly and do not
enter pipeline fetch/decode. Selecting a mode does not turn those probes into
ShadowFrontend coverage. LoopBuffer hits can also avoid Shadow, so a selected
ColdWarm mode does not guarantee a WARM hit for every workload. Use the counters.

HOT is disabled and is not an additional selectable CPU mode. COLD/WARM safety
remains bounded/Partial and performance unqualified; this console integration is
not a full activation exit or evidence of speedup. See the
[activation progress report](../../HybridCPU_ISE/Frontend/docs/ShadowFrontend-Refactoring-Plan/10-ACTIVATION-PROGRESS-REPORT.md).

Bounded verification (2026-10-26): Debug build; ALU in all modes plus default
OFF; matrix-smoke (4 iterations), StreamVector and MatrixTile passed. ALU had
25 Observe comparisons (0 mismatches), 25 COLD in ColdWarm and 0 WARM. WhiteBook
retains seven identical failures with OFF and ColdWarm (DSC/L7 boundary assertions
and source-root lookup); it is not reported as passed. Exact scripts, inputs,
logs and limits: [receipt](../_audit_output/shadow-frontend/testassembler-modes-20261026/final-receipt.json).
