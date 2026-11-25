# DoomSharp compiler refactor plan — предшественник v3

Актуализация навигации: 2026-10-18. Qualification: no.

Продолжение модернизации ведётся в [HybridCPU Compiler vNext v3](../HybridCPU_Compiler_vNext_Plan_v3/README.md). Единственная implementation queue — [v3/95](../HybridCPU_Compiler_vNext_Plan_v3/95_IMPLEMENTATION_ORDER_AND_COMMITS.md); [готовый промпт](../HybridCPU_Compiler_vNext_Plan_v3/EXECUTION_PROMPT.md).

M0–M6 завершены scoped. Это не completion расширенных P0–P9; открытый M7 перенесён в P7/P8/P9 без promotion. [Mapping](Modernize/Roadmap.md) сохраняет смысл принятых foundation slices, не их старые next steps.

- [Evidence/Baseline](Evidence/Baseline.md) — только historical frozen execution scope.
- [Evidence/Validation](Evidence/Validation.md) — применимые правила проверки; основной owner новых итераций — v3/96.
- [Modernize](Modernize/README.md) — переход к existing shared stages/source consumers.
- Текущие code facts/results/limitations — [v3/00](../HybridCPU_Compiler_vNext_Plan_v3/00_CODE_VERIFICATION_REPORT.md) и [v3/93](../HybridCPU_Compiler_vNext_Plan_v3/93_ACCEPTANCE_MATRIX.md).

Root files00–09 — compatibility pointers. Старые SDK/NoHeap versions, PID, project counts и frozen image hashes не являются текущим baseline. Датированные immutable reports не переписываются; недоступные outputs не восстанавливаются ради плана.

Приоритет — работающий C# source→image. Pack/double publish/длительный Doom run нужны для соответствующего runtime/publication claim, не для каждой modernization итерации. Compiler static facts не runtime authority; PublishQualifiedWorkstreams и nonpublish eh-unwind сохраняются.
