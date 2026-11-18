# Validation record

This is an evidence log, not a qualification certificate. All listed artifacts are historical
and live under `TempEnv` or its recoverable `ToRemove` directory.

## 2026-09-02 baseline

| Run | Exit | Meaning |
|---|---:|---|
| `CompilerProfileSmoke` | 0 | Source analyzer/generator smoke passed. |
| Doom CoreCLR console smoke | 0, 26/26 | Host execution only; not HybridCPU guest evidence. |
| `ManagedPortSmoke` | `-532462766` (`0xE0434352`) | Stopped in `ThrowBoundarySmoke.Run` because its expectation denied compiler-derived `eh-unwind`. |

The `ThrowBoundarySmoke` mismatch was subsequently corrected in source. It did not qualify EH,
alter `PublishQualifiedWorkstreams`, or authorize a publish.

## Subsequent focused evidence (not final validation)

- Release ISE build after serializing-carrier interlock changes: success (warnings only).
- Targeted `InterlockMemoryStallTailTests` and `CompilerRefPlan7Phase09IseEhExecutionTests`:
  15 passed, 0 failed.
- `CompilerRefPlan7Phase09ManagedEhTests` after frame-home correction: 7 passed, 3 failed.
  The first failure is the stale expected-section set: actual canonical output additionally has
  `ReadOnlyData` for `.hcfinally`. The remaining two are `InvalidMetadata` in
  `CompilerProducedFinalPcMetadataUnwindsRecursiveCompiledFramesWithoutTrapAuthority` and a
  null outcome in `RethrowSkipsCurrentCatchAndThrowInFinallyReplacesException`.

No full validation cycle has occurred after these code changes. No result here proves EH publish
qualification, a qualified Doom HCEXE, or real Doom guest execution.
