# Навигация по плану

Актуализация 25 ноября 2026. Общая qualification не подтверждена.

## Начать здесь

- [README](README.md)
- [Возможности и прогресс](CURRENT_STATE_AND_PROGRESS.md)
- [00 — Применимость evidence](00_CODE_VERIFICATION_REPORT.md)
- [95 — Единственная очередь](95_IMPLEMENTATION_ORDER_AND_COMMITS.md)
- [Промпт продолжения](EXECUTION_PROMPT.md)
- [Whitebook компилятора](../Whitebook/README.md)

## Архитектура и contracts

- [02 — Invariants](02_ARCHITECTURE_INVARIANTS.md)
- [03 — Текущая архитектура](03_AS_IS_COMPILER_ARCHITECTURE.md)
- [05 — Граница SingNextOS](05_SINGNEXTOS_CODESIGN_CONTRACT.md)
- [06 — IR](06_IR_LAYERING_DECISION.md)
- [07 — Project graph](07_PROJECT_GRAPH_DECISION.md)
- [91 — Target architecture](91_TARGET_ARCHITECTURE.md)
- [92 — Contracts](92_API_AND_TYPE_CHANGES.md)
- [93 — Acceptance](93_ACCEPTANCE_MATRIX.md)
- [94 — Source index](94_SOURCE_INDEX.md)
- [96 — Validation](96_TEST_STRATEGY.md)
- [97 — Performance/research](97_PERFORMANCE_AND_RESEARCH_EVALUATION.md)
- [98 — Toolchain](98_ROSLYN_DOTNET11_CSHARP15_POLICY.md)
- [99 — Открытые границы](99_BLOCKERS_AND_CLAIM_BOUNDARIES.md)

## Области ответственности

P0–P9 — owners/dependencies, не последовательная очередь:

- [P0 — Toolchain/repro](P0_TOOLCHAIN_REPRO_AND_QUALIFICATION.md)
- [P1 — Ownership/runtime edge](P1_SOURCE_OWNERSHIP_AND_RUNTIME_EDGE.md)
- [P2 — Shared pipeline](P2_CANONICAL_PIPELINE_CONVERGENCE.md)
- [P3 — CIL/provenance](P3_CIL_HARDENING_PROVENANCE_SOURCEMAP.md)
- [P4 — Roslyn companion](P4_ROSLYN_MANAGED_SOURCE_COMPANION.md)
- [P5 — Policies/cache/session](P5_POLICY_CACHE_INCREMENTAL_CONCURRENCY.md)
- [P6 — Profile](P6_REPRODUCIBLE_PGO.md)
- [P7 — Evidence/type safety](P7_EVIDENCE_AND_TYPE_SAFETY.md)
- [P8 — Frontend/target qualification](P8_FRONTEND_TARGET_QUALIFICATION.md)
- [P9 — Release/research](P9_RELEASE_CUTOVER_AND_RESEARCH_EVALUATION.md)

[Manifest](plan_manifest.json), [checksums](CHECKSUMS.sha256). [Архив миграционных заметок](Archive/migration-notes-20261109/README.md) не входит в маршрут продолжения.
