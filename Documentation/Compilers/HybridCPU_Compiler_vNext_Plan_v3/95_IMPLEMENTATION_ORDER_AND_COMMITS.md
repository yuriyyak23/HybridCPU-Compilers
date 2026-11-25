# 95 — Единственная очередь реализации

Сверка 25 ноября 2026 на `58a4f4dc5b3e0d4b2b1558b0eefb8c74ed37a4e3`. P0–P9 задают владельцев и зависимости. Здесь остаётся один следующий функциональный consumer.

## Зависимость от production Core

Статически подтверждено отсутствие tracked `Core/IR/Artifacts/**` при наличии production callers. Сборка того же тракта ранее остановилась на CS0234/CS0246. До запуска полного C#→image требуется восстановление подлинных контрактов в [#38](https://github.com/yuriyyak23/HybridCPU-Compilers/issues/38) и чистое прохождение сборки на точном SHA. Не подменять контракты заглушками, исключёнными callers, чужим PE или portable codec. [Evidence](00_CODE_VERIFICATION_REPORT.md).

## Zero-input constrained scalar helper

```csharp
private static U Adjust<U>() where U : struct => default(U);
```

Selected Int32/Int64, private static primary non-root path. [`IsExactStructScalarMethod`](../../../Compilers/HybridCPU_Compiler/Cil/RestrictedCilImporterV1.MetadataBindingsV1.cs) явно отвергает `signature[2] == 0` и ограничивает верхнюю границу числом ABI argument registers. Исторический v137 сообщил HCCIL1702/exit 4 для zero-argument и exit 0 с image для inline/concrete controls. Fixtures и отчёт v137 недоступны в этом checkout. Текущий source→image repro остаётся **BLOCKED** до рабочего production graph.

Исправление должно сохранить exact signed owner/method context, nominal definition/struct constraints, body roles, final purity/typed CFG/SSA, original budgets, EH/cctor/fault и diagnostic precedence. Existing [scalar proof](../../../Compilers/HybridCPU_Compiler/Cil/RestrictedCilImporterV1.StaticProjectionV1.cs) и [zero projection](../../../Compilers/HybridCPU_Compiler/Cil/RestrictedCilImporterV1.NullableInt32.cs) повторно использовать. Discovery не даёт admission. General byref/initobj/aggregate/constraint solver не входит в этот consumer.

Завершение: meaningful source positive/negative, emitted IL/metadata oracle, deterministic repeat, independent physical ABI/call-count/zero oracle и applicable default/SDK/full Managed acceptance по [96](96_TEST_STRATEGY.md). Один immutable report, затем актуальные 00/92/93/95/99/README, manifest и checksums и ровно один новый воспроизведённый gate.

Последний принятый scope и его ограничения — [00](00_CODE_VERIFICATION_REPORT.md). Состояние roadmap не равно component acceptance; общая qualification отсутствует.
