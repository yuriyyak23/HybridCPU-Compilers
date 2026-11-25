# 01 — Действующие решения v3

Источник текущего состояния — [00](../../00_CODE_VERIFICATION_REPORT.md), очередь — [95](../../95_IMPLEMENTATION_ORDER_AND_COMMITS.md). P0–P9 — owners/dependencies, не обязательная историческая последовательность.

Сохраняются existing shared stages, Roslyn companion, CIL importer, scoped cancellation/maps, declaration-only incremental generator, versioned policies/profile и scalar evidence summaries. Не проектировать их заново.

Новый subsystem/IR/assembly/proof hierarchy допускается только по concrete consumer/dependency. Compiler language/SDK identity не guest support. Статические facts не runtime permission.

Closed foundation и historical audit corrections не активные задачи. Расширять practical C# source→image, соблюдать [invariants](../../02_ARCHITECTURE_INVARIANTS.md), проверять [по scope](../../96_TEST_STRATEGY.md). Qualification: no.
