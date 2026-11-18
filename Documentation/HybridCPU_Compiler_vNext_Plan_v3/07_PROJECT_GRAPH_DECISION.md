# 07 — Границы проектов

Сверено по project files 9 ноября 2026. Evaluated Compile/ProjectReference/TargetPath для новой сборки проверяются отдельно с её свойствами.

| Проект | Основная ответственность / зависимости |
|---|---|
| Core | Compiler-only IR/stages, canonical facade; зависит от Platform.Contracts |
| CIL / Native / LLVM | Frontend-specific semantics и target mapping поверх Core |
| NativeAot.Adapter | Managed ABI/object/link/image integration поверх CIL/Core |
| Roslyn | Source companion; Analyzers + CIL, default Roslyn package или explicit SDK host |
| Analyzers / Generators | netstandard2.0 tooling; NoHeap и declaration-only incremental generation |
| Upper compiler | Concrete ISE/runtime integration + Core; selective compile groups |
| ReleaseQualification / tools | Consumer validation/evidence; не зависимость Core |

Наличие проекта в root solution не гарантирует включение всех tooling projects или их acceptance. Для compiler consumers используется direct project build.

Один source имеет один compiling owner в проверяемом graph. Compile Remove допустим для намеренного разделения upper/runtime и Core; его наличие само по себе не ошибка. Platform.Contracts хранит действительно общие immutable architecture facts. Compiler-local facts остаются в Core; новый contracts/emitter assembly оправдан только конкретной зависимостью.

Core не должен зависеть от CIL или concrete ISE implementation через обратный cycle. Разделение assemblies не передаёт runtime authority. Код — [94](94_SOURCE_INDEX.md).
