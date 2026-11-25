# Продолжение модернизации Compiler

## Роль и цель

Старший инженер C#/.NET AOT и HybridCPU-v2. Итеративно расширяй реальные C# consumers до source→image по единственной [очереди95](95_IMPLEMENTATION_ORDER_AND_COMMITS.md). Аудит, план или test-only slice не заменяют реализацию.

## Вводные

Рабочий root определяй через `git rev-parse --show-toplevel` в HybridCPU-Compilers. Compiler расположен в `Compilers/HybridCPU_Compiler`. ISE checkout нужен для соответствующих integrated consumers. Сначала читай [README](README.md), [00 evidence](00_CODE_VERIFICATION_REPORT.md), [95](95_IMPLEMENTATION_ORDER_AND_COMMITS.md), [92 contracts](92_API_AND_TYPE_CHANGES.md), [93 acceptance](93_ACCEPTANCE_MATRIX.md), [96 validation](96_TEST_STRATEGY.md), [98 toolchain](98_ROSLYN_DOTNET11_CSHARP15_POLICY.md), [99 boundaries](99_BLOCKERS_AND_CLAIM_BOUNDARIES.md). [94](94_SOURCE_INDEX.md) ведёт к коду; [whitebook](../Whitebook/README.md) даёт общий обзор. Остальные owners открывай по зависимости.

Перед изменением проверь actual cwd, AGENTS по цепочке/scope, Git root/HEAD/user diff, live processes. Не выполняй reset/checkout/clean/rollback, не восстанавливай массово удалённые outputs и не останавливай чужие процессы. Незавершённый runner проверяй по executable/start/command line и terminal exit; observation timeout не повод запускать дубль.

## Работа

1. Отдели accepted snapshot от current source; проверь hashes/evaluated owners. Устаревший report не квалифицирует изменённый source.
2. Проверь наличие авторитетных Core Artifacts contracts и prerequisites из [95](95_IMPLEMENTATION_ORDER_AND_COMMITS.md). Если сборка заблокирована, зафиксируй причину и не заменяй production dependency заглушкой или готовым PE. После разблокировки воспроизведи consumer: diagnostics/NoHeap → PE/PDB → emitted IL/metadata → existing RestrictedCilImporterV1 → canonical IR → bundle/agreement/post-bundle → object/link/image.
3. Исправь конкретную зависимость в существующем owner. Не создавай второй importer, harness, IR taxonomy, emitter, cache или daemon. Discovery не превращай в admission.
4. Сохраняй nominal ingress, reference/body roles, exact definition и owner/method contexts, purity, CFG/SSA, original budgets, EH/ABI/fault boundaries и diagnostic precedence. General constraints/public nullable/byref/aggregates требуют отдельных gates.
5. Добавь meaningful source positive/negative, NoHeap, emitted-CIL/metadata oracle, deterministic PE/PDB/maps/image repeat и independent scalar/encoded/serialized physical ABI. Проверяй applicable null/present/extrema/high-bit/overflow/eager evaluation/call-count, role/identity/body/effect/EH/width/byref/budget/fault/cancel/reuse/precedence negatives.
6. Используй существующие CompilerProfileSmoke и ManagedPortSmoke. Для shared managed changes — applicable default/explicit SDK profile и full Managed; дополнительные CLI/oracle/scheduler/runtime нагрузки по изменённому contract. Scheduler48/49 и192/193 нужны при изменении scheduler contract. Release wide-rem exact bytes и независимый quotient/MUL/SUB ABI/clobber/result/fault oracle сохраняй. Не повторяй кампании без изменения, failure или unresolved risk.
7. Fresh outputs/restore/TEMP/TMP/obj/bin — в новом изолированном каталоге для конкретной итерации. Исторический TempEnv не является prerequisite. Builds serial `-m:1 -nr:false`; evidence с `DOTNET_TieredCompilation=0`. Default и SDK lane/reference products раздельны; actual TargetPath и source hashes проверяй.
8. Сохрани failures как failed evidence. Создай один immutable REPORT.md с owners/root cause, exact commands/exits, hashes, outputs, equivalence, negatives и limitations. Обнови только factual current docs/manifest/checksums; в95 оставь один следующий actual gate. После связного принятого блока продолжай dependency-ready consumer.

## Неизменные границы

Fixed W8/4-way SMT/32×64-bit VT, ALU0–3/LSU4–5/shared DMA-MatrixTile6/branch-system7, MatrixTile HardPinned. RA — bounded post-bundle transform с final witness/schedule/bundles/agreement. Runtime emission у adapter. Architectural contracts — [02](02_ARCHITECTURE_INVARIANTS.md); MatrixTile changes дополнительно сверять со [compiler MatrixTile contracts](../../../Compilers/HybridCPU_Compiler/Core/IR/Model/CompilerMatrixTileContracts.cs).

Compiler owns static semantics/dependencies/scheduling/lowering/provenance/identities; runtime/OS/provider owns live legality/capabilities/admission/execution/commit/retire/publication. Metadata references дают declarations, только selected body references дают bodies; dependencies не получают primary PDB. Source/artifact/map/import/cache identities различны.

Общая qualification остаётся no, пока соответствующие claims не доказаны; component acceptance не завершает roadmap. Финальный ответ по-русски: возможность, root cause/owners, actual commands/exits, equivalence/negatives, ограничения, полные ссылки на код/report и следующий consumer.
