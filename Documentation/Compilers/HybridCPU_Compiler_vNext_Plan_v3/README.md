# План модернизации HybridCPU Compiler

Актуализация: 25 ноября 2026. Общая квалификация продукта: **не подтверждена**.

Цель — расширять поддерживаемые C# программы до работающего image через существующие Roslyn companion, CIL importer и backend. Поддержка определяется конкретным consumer, сигнатурами, телами и проверками; версия языка сама по себе не означает поддержку всех его конструкций.

Начать с [отчёта о возможностях и прогрессе](CURRENT_STATE_AND_PROGRESS.md). [00](00_CODE_VERIFICATION_REPORT.md) содержит применимость доказательств; [95](95_IMPLEMENTATION_ORDER_AND_COMMITS.md) — единственную очередь реализации. Для общей архитектуры предназначен [whitebook](../Whitebook/README.md).

| Документ | Назначение |
|---|---|
| [92 — contracts](92_API_AND_TYPE_CHANGES.md) | Существенные private/API границы |
| [93 — acceptance](93_ACCEPTANCE_MATRIX.md) | Возможности и пределы подтверждённой поддержки |
| [94 — source index](94_SOURCE_INDEX.md) | Код и инструменты проверки |
| [96 — validation](96_TEST_STRATEGY.md) | Проверки по типу изменения |
| [98 — toolchain](98_ROSLYN_DOTNET11_CSHARP15_POLICY.md) | Различия SDK, Roslyn и guest support |
| [99 — ограничения](99_BLOCKERS_AND_CLAIM_BOUNDARIES.md) | Открытые направления и пределы claims |
| [Промпт продолжения](EXECUTION_PROMPT.md) | Краткая инструкция следующей итерации |

P0–P9 описывают области ответственности и зависимости, а не дополнительную очередь. [Оглавление](CONTENTS.md) содержит остальные решения. История перехода между версиями плана вынесена в Archive; старые отчёты не переписаны и не являются новыми заданиями.

Compiler отвечает за статическую семантику, зависимости, scheduling, lowering, provenance и identities. Runtime/OS/provider отвечают за live legality, admission, capabilities, execution, commit, retire и publication. Успешная компиляция не передаёт эти полномочия compiler.
