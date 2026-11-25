# 95 — Единственная очередь реализации

Сверка 9 ноября 2026. Один следующий consumer; P0–P9 не образуют параллельную очередь.

## Zero-input constrained scalar helper

```csharp
private static U Adjust<U>() where U : struct => default(U);
```

Selected Int32/Int64, private static primary non-root path. [Текущий metadata matcher](../../../Compilers/HybridCPU_Compiler/Cil/RestrictedCilImporterV1.MetadataBindingsV1.cs) явно отвергает parameter count 0. Сохранённый [v137 source](../../../TempEnv/doom-continuation-20260905/scalar-default-20261024/probe/ZeroArgumentsV2.cs) получил HCCIL1702/exit 4; [inline](../../../TempEnv/doom-continuation-20260905/scalar-default-20261024/probe/ZeroArgumentsInlineV2.cs) и [concrete](../../../TempEnv/doom-continuation-20260905/scalar-default-20261024/probe/ZeroArgumentsConcreteV2.cs) controls — exit 0/image. Нового воспроизведения 9 ноября не было; matcher и сохранённые compiler inputs не изменены.

Исправление должно сохранить exact signed owner/method context, nominal definition/struct constraints, body roles, final purity/typed CFG/SSA, original budgets, EH/cctor/fault и diagnostic precedence. Existing [scalar proof](../../../Compilers/HybridCPU_Compiler/Cil/RestrictedCilImporterV1.StaticProjectionV1.cs) и [zero projection](../../../Compilers/HybridCPU_Compiler/Cil/RestrictedCilImporterV1.NullableInt32.cs) повторно использовать. Discovery не даёт admission. General byref/initobj/aggregate/constraint solver не входит в этот consumer.

Завершение: meaningful source positive/negative, emitted IL/metadata oracle, deterministic repeat, independent physical ABI/call-count/zero oracle и applicable default/SDK/full Managed acceptance по [96](96_TEST_STRATEGY.md). Один immutable report, затем актуальные00/92/93/95/99/README/manifest/checksums и ровно один новый воспроизведённый gate.

Последний принятый scope и его ограничения — [00](00_CODE_VERIFICATION_REPORT.md). Состояние roadmap не равно component acceptance; общая qualification отсутствует.
