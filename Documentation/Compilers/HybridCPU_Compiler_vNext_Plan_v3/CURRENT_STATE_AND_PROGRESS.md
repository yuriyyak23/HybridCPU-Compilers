# HybridCPU Compiler — возможности и прогресс

Состояние на 25 ноября 2026 по tracked коду на `58a4f4dc5b3e0d4b2b1558b0eefb8c74ed37a4e3`. Реализованные сценарии описаны по owners. Их текущая сборка и runtime требуют отдельных receipts. Исторический v137 и новые ограничения сборки разобраны в [00](00_CODE_VERIFICATION_REPORT.md).

## Что представляет собой компилятор

HybridCPU Compiler — набор C#/.NET компонентов для подготовки программ под typed-slot VLIW HybridCPU-v2. Он принимает native instruction/IR inputs и ограниченные managed программы. Для C# путь проходит через Roslyn, PE/CIL и существующий semantic importer; это не произвольная трансляция любого .NET приложения.

Основной результат модернизации — существующий связный source→image путь и повторное использование backend, а не только анализ или успешный Roslyn emit. Скомпилированный image остаётся статическим артефактом. Загрузка, разрешение исполнения и публикация результата принадлежат runtime/OS/provider.

## Реализованные возможности

| Область | Что имеется | Существенное ограничение |
|---|---|---|
| C# ingress | Owned source/options/references; source diagnostics и NoHeap до deterministic PE/PDB; CLI source→image | Не весь C# и не произвольные framework dependencies |
| Module/body resolution | BodyWorld, bounded call graph, точные definition/generic identities, раздельные reference roles | Metadata declaration не даёт права импортировать тело; filesystem discovery не заменяет explicit input |
| Scalar control flow | Typed CFG/SSA, scalar arithmetic, branches, helpers, доказанные counted loops и часть nested loops | Original budgets, purity, EH/fault и конкретные формы ограничивают допуск |
| Value types | Immutable single-scalar Int32/Int64 конструкции, getters, factories/local copies/joins, выбранные external definitions | Не общий struct/aggregate ABI |
| Arrays/references/String | Проверенные primitive array и String accessor consumers, argument/identity/fault controls | Произвольный object reference не даёт layout/accessor authority |
| Nullable locals | Primitive carriers и enum с восемью integral underlyings; copy/default/presence/accessors, checked Value | Exact nominal/width proof; internal representation не общедоступный nullable ABI |
| Private nullable consumers | Primary non-root Nullable<int> input/return, fallback computations, scalar/nullable dependencies, bounded loops | Public/root/external nullable signatures, byref и общий EH требуют отдельных gates |
| Constrained scalar helpers | Selected Int32/Int64, exact `where U:struct`, private static primary non-root, 1–8 `U` inputs и `U` return; direct/transitive, `default(U)` | Не general constraint solver; zero-input signature пока отклоняется |
| Backend | Shared scheduling, bundling, agreement, lowering, relocation, serialization; bounded witnessed register allocation | RA должна оставить согласованные final layout/physical operands; нет универсального target claim |
| Политики и профиль | Immutable/versioned policies, explicit opt-in profile compilation, metrics/shadow paths | Детерминизм не доказывает ускорение; persistent cache/concurrent session не следуют из наличия identities |
| Runtime integration | Concrete transport/emission adapter; отдельные managed ABI/EH/GC/object/image owners | Compile success не execution, retire или publication |

Точная матрица — [93](93_ACCEPTANCE_MATRIX.md), contracts — [92](92_API_AND_TYPE_CHANGES.md), ссылки на код — [94](94_SOURCE_INDEX.md).

## Что изменилось в ходе модернизации

1. **Общее ядро.** Canonical facade физически находится в Core; shared bundler и post-bundle tail используются существующими путями. Runtime emission сохранён на конкретной границе adapter, без переноса live state в compiler core.
2. **Реальный managed ingress.** Roslyn companion, NoHeap, PE/PDB provenance и BodyWorld связаны с importer/object/link/image. Declaration/body roles и primary-module source maps стали явными границами входа.
3. **Расширение практических consumers.** Добавлены scalar value/nullable, helper/CFG/loop, exact generic context и external definition сценарии. Допуск расширяется по emitted CIL и final typed/body proof, а не по имени C# конструкции.
4. **Последний связный блок.** К 24 октября constrained scalar methods расширены от unary/binary к существующему восьмирегистровому ABI, затем принят exact generic `default(U)` local-zero body. Сохранены original budgets, eager evaluation, identity и runtime boundaries.
5. **Проверяемые артефакты.** Есть deterministic repeat, scalar/CoreCLR/encoded/serialized oracles и negative controls. Неудачные попытки сохраняются отдельно, а не превращаются в PASS.

Это описание конкретно реализованного прогресса. Процент готовности не вычисляется: roadmap содержит независимые consumer, performance и execution/publication требования, не сводимые к числу закрытых helper shapes.

## Что подтверждено проверками

Запись от 9 ноября приводит результаты v137 от 24 октября и сверку сохранённых входов (541 compiler, 493 evaluated, drift 0 в той выборке). Архивные распечатки не входят в текущий checkout. Новая компиляция и переоценка этого manifest не выполнялись. [00](00_CODE_VERIFICATION_REPORT.md) сохраняет исходные counts и границы отчёта.

Production Core остаётся prerequisite актуальной проверки C#→image. Tracked declarations отсутствуют, а предыдущий exact-SHA hosted job остановился на Core. Текущие loader/guest/publish outcomes отсутствуют.

## Что остаётся открытым

Ближайший consumer — `private static U Adjust<U>() where U:struct => default(U)`: body уже знаком, но exact method signature admission требует хотя бы один параметр. Отказ HCCIL1702 и положительные inline/concrete controls сохранены; владельцы исправления — metadata matcher и final scalar proof. Повторный запуск зависит от Core build prerequisite. Реализация выбирается через [очередь 95](95_IMPLEMENTATION_ORDER_AND_COMMITS.md).

Более широкие задачи: новые C# emitted-IL/metadata формы, generic constraints, nullable/aggregate/byref/EH ABI, дополнительные frontend/target contours. Их наличие не означает, что текущие foundation отсутствуют или должны быть переписаны. Cache/concurrency/host AOT/LLVM expansion и performance promotion требуют своего consumer и измерений.

Полная совместимость с C#/.NET/BCL, async/unsafe/interop/trimming, произвольные агрегаты и публичный nullable ABI не подтверждены. Full solution/Debug и loader/guest/publication acceptance не следуют из Release component runs. Исторические host failures не доказывают нынешний blocker и не считаются исправленными без отдельной проверки.

## Как читать план дальше

[Whitebook](../Whitebook/README.md) объясняет архитектуру без истории отдельных итераций. В активном плане 00 хранит применимость evidence, 92/93 — contracts/границы, 95 — одно следующее действие, 96 — validation. Migration notes перенесены в Archive; frozen reports остаются evidence. Это устраняет повторные status ledgers и не стирает failed evidence.
