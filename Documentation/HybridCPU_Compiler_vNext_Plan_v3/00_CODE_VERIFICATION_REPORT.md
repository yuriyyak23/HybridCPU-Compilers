# 00 — Состояние доказательств

Проверено 9 ноября 2026 по текущим файлам и сохранённым результатам. Новые build/test/guest campaigns в рамках актуализации документации не запускались. Общая квалификация: **нет**.

## Применимость последнего acceptance

Последний проверенный компонентный результат — [v137, 24 октября](../../../TempEnv/doom-continuation-20260905/scalar-default-20261024/REPORT.md): exact constrained Int32/Int64 scalar helpers с 1–8 параметрами и `default(U)` на primary non-root path.

9 ноября повторно вычислены SHA256 всех **541** файлов из его compiler manifest: отсутствующих и изменённых файлов **0**. Из **493** evaluated inputs отсутствующих и изменённых **0**. Это подтверждает сохранность перечисленных входов, но не является новой сборкой, проверкой нового host или всего рабочего дерева. Manifest не доказывает отсутствие новых файлов вне своего списка. Состав текущих compiler inputs отдельно сопоставлен при документационном аудите: новых или исключённых файлов в этой выборке нет. Результаты — [validation.json](../../../TempEnv/compiler-docs-review-20261109/validation.json).

| Сохранённый запуск v137 | Exit | PASS-строк |
|---|---|---|
| Default CompilerProfileSmoke | 0 | 426 |
| Explicit SDK CompilerProfileSmoke | 0 | 427 |
| Full ManagedPortSmoke | 0 | 433 |
| Focused generic zero source | 0 | 48 |

Это счётчики конкретных запусков, не число поддержанных возможностей и не новый запуск 9 ноября. Report содержит команды, outputs, source/evaluated hashes и retained failures. 304 прежних images совпали побайтно; 48 новых default/SDK images равны; повтор PE/PDB/maps/image детерминирован в каждой lane. Release wide-rem и независимый физический oracle сохранены.

Первоначальная проверка generic/concrete I8 code-byte equality завершилась ошибкой: эквивалентный zero допускает разные инструкции. Она сохранена как failed evidence; принятие опирается на semantic и physical oracle. В отчёте пояснён неточный текст старых PASS-строк. Эти строки не следует читать как доказательство byte equality для generic zero.

## Текущие source facts

Core содержит canonical facade и shared scheduling/bundling/post-bundle. CIL/NativeAOT используют общие стадии, сохраняя собственные ABI, metadata, EH/GC и object/link/image owners. Roslyn companion выполняет diagnostics/NoHeap до emit; BodyWorld различает metadata declarations и явно выбранные тела. Concrete runtime emission находится в upper adapter.

Root [global.json](../../../global.json) задаёт SDK 11.0.100-rc.2.26504.105 и VSTest. Default companion использует Roslyn 4.14.0/CSharp13; explicit SDK lane подключает matching SDK references. Наличие SDK не означает поддержку всей его BCL или языка в guest. Детали — [98](98_ROSLYN_DOTNET11_CSHARP15_POLICY.md).

Рабочее дерево содержит пользовательские изменения; HEAD `1bfe41a49cf11c80fa6c6aee87accb54b78b331f` не описывает их содержимое. Сторонний dotnet build при аудите не останавливался. Полный solution, Debug, loader/guest/publication и обычная стабильность preview host этим результатом не квалифицированы.

## Следующее действие

Текущий код всё ещё отвергает zero-input constrained helper: exact signature matcher требует 1–8 параметров. Сохранённый v137 consumer получил HCCIL1702/exit 4, inline и concrete controls — image/exit 0. Это единственный следующий блок в [95](95_IMPLEMENTATION_ORDER_AND_COMMITS.md). Общий функциональный обзор — [отчёт](CURRENT_STATE_AND_PROGRESS.md).
