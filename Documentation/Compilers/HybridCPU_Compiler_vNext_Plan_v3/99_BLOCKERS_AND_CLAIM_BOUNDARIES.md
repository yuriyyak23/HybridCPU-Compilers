# 99 — Открытые границы

25 ноября 2026. Единственная очередь реализации — [95](95_IMPLEMENTATION_ORDER_AND_COMMITS.md). Этот файл не содержит второго backlog по отдельным helper shapes.

## Общая зависимость сборки

[P0](P0_TOOLCHAIN_REPRO_AND_QUALIFICATION.md) и software Core owner закрывают [#38](https://github.com/yuriyyak23/HybridCPU-Compilers/issues/38): tracked production declarations, точный граф и изолированные Debug/Release receipts. [00](00_CODE_VERIFICATION_REPORT.md) отделяет это состояние от исторических component runs.

## Функциональное расширение

Главное направление — реальные C# consumers и мешающие им emitted-IL/metadata/CFG/ABI/backend ограничения. Existing companion, importer, maps, cancellation и shared stages уже реализованы; их повторное создание не является задачей.

General constraints, public/root/external nullable signatures, более широкий aggregate/byref/EH ABI, новые language/BCL forms и дополнительные frontend/target contours требуют отдельных acceptance. Отсутствие blanket поддержки не означает отсутствие всех частных форм: точный реализованный scope — [93](93_ACCEPTANCE_MATRIX.md).

Persistent cache, shared-session concurrency, host AOT, broader LLVM и default-on PGO не prerequisites обычного C# consumer. Нужны конкретный потребитель, lifetime/invalidation/identity contract и измеримые результаты. Deterministic profile lane не доказывает performance win.

## Границы доказательств

- Последний Release acceptance относится к exact inputs и режиму из [00](00_CODE_VERIFICATION_REPORT.md), не ко всему dirty tree.
- Debug и full-solution acceptance не следует из Release component runs. Release wide-rem exact bytes сохраняются; Debug equivalence требует собственной проверки.
- Исторические preview-host/Code Integrity failures не объявляются ни текущим воспроизведённым blocker, ни исправленной причиной. Их подробности остаются в immutable reports.
- CoreCLR/scalar/encoded/serialized oracle не равен guest execution. Равные images не доказывают независимые clean builds без provenance producer.
- Release schemas/parser/hash checks проверяют consistency/admission, а не actual loader/runtime/replay/publication.

Compiler facts/profile/annotations/secure descriptors не выдаёт permission runtime/OS/provider. Execution, commit, retire и publication — разные события с отдельными владельцами. Общая qualification продукта **не подтверждена**. Нужный объём проверки выбирается по заявленному claim, без автоматического требования многодневной нагрузки для обычного compiler patch.
