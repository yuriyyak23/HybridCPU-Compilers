# 00 — Состояние доказательств

Сверка 25 ноября 2026. База — чистый detached checkout `master` на `58a4f4dc5b3e0d4b2b1558b0eefb8c74ed37a4e3`. Новая общая квалификация продукта отсутствует.

## Текущая база и граница воспроизводимости

| Проверка | Результат и scope |
| --- | --- |
| Tracked Core и зависимости | Ссылки Roslyn → Cil → Core сохраняются. В tracked дереве отсутствует `Core/IR/Artifacts/**`, хотя production callers ссылаются на `CompilerEmissionPackage` и связанные контракты. Владелец Core ведёт [#38](https://github.com/yuriyyak23/HybridCPU-Compilers/issues/38). |
| Hosted Windows | [HB-INT-06 job](https://github.com/yuriyyak23/HybridCPU-Compilers/actions/runs/45657995837/job/139359800220) на более раннем SHA `d95ff907171fda783897d1a87febdc37f2bcce08` прошёл portable v1/v2 и restore (exit 0), остановился на Core CS0234/CS0246 (build exit 1). Успех portable codec относится к своему проекту. |
| Текущий SHA | При сверке список Actions по точному SHA не вернул запусков. В локальной Linux среде отсутствуют `dotnet` и `pwsh`. Текущие production Debug/Release, C#→image, loader/guest, publication и performance tests — **NOT RUN**. |
| Toolchain | Корневой `global.json` отсутствует. Package declarations и workflow pins перечислены в [98](98_ROSLYN_DOTNET11_CSHARP15_POLICY.md). Evaluated restore/deps и hashes реально загруженных compiler assemblies не получены. |
| HardwareBridge | Закреплённые R0/R2/R3 результаты, отдельные software/legacy profiles и внешний hardware допуск описаны в [HB status](../../../Compilers/HybridCPU_Compiler/HardwareBridge/Docs/current/STATUS.md). Компонентный hosted результат HB не квалифицирует DoomSharp. |

## Текущие source facts

Core содержит canonical facade, shared scheduling/bundling/post-bundle. CIL и NativeAOT используют общие стадии с собственными ABI, metadata, EH/GC и object/link/image owners. Roslyn companion проверяет diagnostics/NoHeap перед emit. BodyWorld различает metadata declarations и явно выбранные тела. Concrete runtime emission остаётся в upper adapter. [Индекс исходников](94_SOURCE_INDEX.md) и [граф проектов](07_PROJECT_GRAPH_DECISION.md).

## Исторический v137 — применимость компонентного результата

План от 9 ноября зафиксировал результат v137 от 24 октября для exact constrained Int32/Int64 scalar helpers с 1–8 параметрами и `default(U)` на primary non-root path. Архивный receipt `scalar-default-20261024/REPORT.md` недоступен в tracked checkout.

По записи от 9 ноября повторно вычислены SHA256 всех **541** файлов из его compiler manifest: отсутствующих и изменённых файлов **0**. Из **493** evaluated inputs отсутствующих и изменённых **0**. Это подтверждает сохранность перечисленных входов, но не является новой сборкой, проверкой нового host или всего рабочего дерева. Manifest не доказывает отсутствие новых файлов вне своего списка. Состав входов на ту дату тоже сверялся. Исторический `compiler-docs-review-20261109/validation.json` недоступен. Сверка исходила из dirty worktree при HEAD `1bfe41a49cf11c80fa6c6aee87accb54b78b331f`; этот SHA не идентифицирует её незакоммиченные байты.

| Сохранённый запуск v137 | Exit | PASS-строк |
|---|---|---|
| Default CompilerProfileSmoke | 0 | 426 |
| Explicit SDK CompilerProfileSmoke | 0 | 427 |
| Full ManagedPortSmoke | 0 | 433 |
| Focused generic zero source | 0 | 48 |

Это счётчики конкретных запусков, не число поддержанных возможностей и не новый запуск 9 ноября. Report содержит команды, outputs, source/evaluated hashes и retained failures. 304 прежних images совпали побайтно; 48 новых default/SDK images равны; повтор PE/PDB/maps/image детерминирован в каждой lane. Release wide-rem и независимый физический oracle сохранены.

Первоначальная проверка generic/concrete I8 code-byte equality завершилась ошибкой: эквивалентный zero допускает разные инструкции. Она сохранена как failed evidence; принятие опирается на semantic и physical oracle. В отчёте пояснён неточный текст старых PASS-строк. Эти строки не следует читать как доказательство byte equality для generic zero.

## Следующее действие

[Очередь 95](95_IMPLEMENTATION_ORDER_AND_COMMITS.md) фиксирует зависимость от production Core и сохраняет один следующий C# consumer. Статическая проверка `IsExactStructScalarMethod` подтверждает отказ при нуле параметров. HCCIL1702 и control runs относятся к историческому v137. Exit текущего воспроизведения неизвестен.
