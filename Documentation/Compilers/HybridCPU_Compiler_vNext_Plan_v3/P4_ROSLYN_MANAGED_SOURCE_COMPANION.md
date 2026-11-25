# P4 — C# source companion

Existing companion выполняет owned source/options/references → diagnostics/NoHeap → deterministic PE/PDB → CIL/BodyWorld → object/link/image. CLI compile-source-image существует в том же owner. [Roslyn usage](../../../Compilers/HybridCPU_Compiler/Roslyn/README.md).

Metadata-reference role даёт declarations, selected body-reference — bodies; discovery filesystem/framework не добавляется. Source admission не является interprocedural certification произвольной reference library. Analyzer failures/suppression не должны обходить rejection.

Новый construct проверяется downstream emitted IL/metadata и actual image, с applicable default/explicit SDK lane и matching references. Language/package/host changes — compatibility changes; они не открывают всю BCL/async/unsafe/interop. [Toolchain](98_ROSLYN_DOTNET11_CSHARP15_POLICY.md).

Это область ответственности, не отдельная очередь. Следующий consumer — [95](95_IMPLEMENTATION_ORDER_AND_COMMITS.md); validation — [96](96_TEST_STRATEGY.md); применимость evidence — [00](00_CODE_VERIFICATION_REPORT.md). Общая qualification не подтверждена.
