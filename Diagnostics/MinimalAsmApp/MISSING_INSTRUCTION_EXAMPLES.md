# Инструкции модели без примеров MinimalAsmApp

Срез актуализирован 2026-06-18 по локальному коду проекта и текущим примерам `MinimalAsmApp/Examples`.

## Методика

- Источник model inventory: `OpcodeRegistry.Opcodes` из `HybridCPU_ISE.csproj`.
- Инструкция считается поддерживаемой моделью, если её status равен `Mandatory` или `OptionalEnabled` и `InstructionRegistry.IsRegistered(opcode)` возвращает `true`.
- `Reserved`, parser-only, descriptor-only и прочие fail-closed строки не включены в список отсутствующих примеров.
- Покрытие examples определяется по ссылкам на `InstructionsEnum` во всех `.cs` файлах `MinimalAsmApp/Examples`, включая общие builders в `Examples/Support`.
- Совпадение выполняется по numeric opcode и всем enum aliases, а не только по отображаемому mnemonic.

## Итог

| Показатель | Количество |
|---|---:|
| Поддерживаемые model rows | 215 |
| Представлены в examples | 215 |
| Отсутствуют в examples | 0 |

Наличие инструкции в этом списке означает отсутствие отдельного enum-backed примера, а не отсутствие runtime реализации.

## Общие принципы новых примеров

1. Один пример должен демонстрировать одну семантическую семью и минимальное число инструкций вокруг неё.
2. Использовать canonical encoder или typed compiler facade, если такой ABI уже опубликован; не вводить alias/fallback ради примера.
3. Для исполнимого contour проверять наблюдаемый результат: register, memory, PC, mask, completion или retire evidence.
4. Для privileged/system/accelerator строк не имитировать отсутствующий backend: ограничиться encode/decode, descriptor и legality evidence либо явно fail-closed сценарием.
5. Векторные примеры должны явно задавать element type, VL/count, stride, mask и адресные диапазоны; отдельно проверять opcode identity.
6. Добавлять пример в `CpuExampleCatalog`, давать стабильные `Name`, `Description`, `Category` и детерминированный результат.
7. Не считать текстовое упоминание mnemonic покрытием: пример должен построить carrier через enum/helper и проверить его.

## Отсутствующие инструкции

Отсутствующих enum-backed примеров нет. Все поддерживаемые model rows представлены в `MinimalAsmApp/Examples` через `InstructionsEnum` или общий helper, который строит canonical carrier.

## Рекомендуемый порядок реализации

1. Базовые scalar, bit-manipulation, comparison и typed memory инструкции.
2. Control-flow примеры с минимальными deterministic programs.
3. Vector arithmetic, comparison, reduction, permutation и transfer families.
4. System/CSR и synchronization instructions.
5. Privileged, VMX, descriptor-backed Lane6/Lane7 строки только в пределах опубликованной legality/runtime authority.

## MatrixTile ISE-ready rows

Phase 19 закрыл MatrixTile compiler-sideband conformance. Следующие
runtime/ISA строки готовы для ISE/compiler-facing использования через typed
MatrixTile helper path:

| Instruction | Текущее покрытие MinimalAsmApp | ISE/compiler helper |
|---|---|---|
| `MTILE_LOAD` | `Examples/10_Vector/MatrixTileEncodingExample.cs`, `Examples/10_Vector/MatrixTileLoadCompilerExample.cs` | `CompileMtileLoad` / `AppAsmFacade.MtileLoad` |
| `MTILE_STORE` | `Examples/10_Vector/MatrixTileEncodingExample.cs`, `Examples/10_Vector/MatrixTileStoreCompilerExample.cs` | `CompileMtileStore` / `AppAsmFacade.MtileStore` |
| `MTILE_MACC` | `Examples/10_Vector/MatrixTileEncodingExample.cs`, `Examples/10_Vector/MatrixTileMaccCompilerExample.cs` | `CompileMtileMacc` / `AppAsmFacade.MtileMacc` |
| `MTRANSPOSE` | `Examples/10_Vector/MatrixTileEncodingExample.cs`, `Examples/10_Vector/MatrixTileTransposeCompilerExample.cs` | `CompileMtranspose` / `AppAsmFacade.Mtranspose` |

Usage boundary:

- `MTILE_MACC` должен нести explicit runtime-owned `MatrixTileNumericPolicy`
  и `MatrixTileLayoutPolicy` sidebands.
- `MTRANSPOSE` должен нести explicit runtime-owned `MatrixTileLayoutPolicy`
  и не должен нести MACC numeric sideband.
- `MTILE_LOAD` и `MTILE_STORE` не вводят compute numeric authority.
- Compiler emission остаётся downstream transport evidence; runtime strict
  projection/materialization остаётся fail-closed authority для missing,
  tampered, unsupported или operation-mismatched sidebands.

`MatrixTileEncodingExample` остаётся encoding/descriptor evidence примером.
Отдельные `MatrixTile*CompilerExample` примеры используют typed compiler
helpers выше и проверяют сохранение source/lowered sidebands без raw opcode
ingress или fallback carriers.
