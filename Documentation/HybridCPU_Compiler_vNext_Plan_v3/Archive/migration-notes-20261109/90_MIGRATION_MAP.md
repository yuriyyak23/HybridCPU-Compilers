# 90 — Migration decisions

| Existing responsibility | Действие |
|---|---|
| Canonical compiler | физически Core/Compilation; не повторять перенос из API |
| Concrete runtime/emission/production/decomposition/Phase00 | existing upper owners; live state не переносить в Core |
| Shared ProgramBundler/PostBundleCompiler/RA | reuse; final witness/physical relocation/agreement сохранять |
| CIL importer/BodyWorld/maps | расширять actual emitted C# gap, не заменять semantic importer |
| Roslyn companion/generator | existing source→PE/PDB route; generator declaration-only |
| Policies/profile/evidence | existing versioned owners; не создавать конкурирующий V3 universe |
| Cache/session/concurrency | только по measured consumer/lifetime/invalidation |
| Release/LLVM/host packaging | отдельный scope, не обязательная migration перед C# |

Точные physical owners — [94](../../94_SOURCE_INDEX.md), решение по IR — [06](../../06_IR_LAYERING_DECISION.md), project graph — [07](../../07_PROJECT_GRAPH_DECISION.md), текущая очередь — [95](../../95_IMPLEMENTATION_ORDER_AND_COMMITS.md). Qualification: no.
