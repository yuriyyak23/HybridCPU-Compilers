# Foundation architecture decisions

Foundation decisions зафиксированы 18 октября 2026. Сверка путей и границ evidence — 25 ноября 2026. Старый внешний audit не является очередью; actual source выше прежних тезисов. Qualification: no.

- Canonical facade физически Core/Compilation/HybridCpuCanonicalCompiler.cs. Shared IrProgramCompiler/ProgramBundler/PostBundleCompiler уже используются canonical/CIL/NativeAOT.
- Runtime emission/production/decomposition/Phase00 — upper owners; Core не получает live execution/permission state.
- RestrictedCilImporterV1 уже содержит bounded metadata/CFG/generic/EH/session/provenance механизмы. Roslyn companion и declaration-only incremental generator уже существуют.
- Toolchain/embedded/publish/upstream identities различны; актуальные pins — [v3/98](../../HybridCPU_Compiler_vNext_Plan_v3/98_ROSLYN_DOTNET11_CSHARP15_POLICY.md), не старые RC1 числа.
- Policy/profile/trace и scalar evidence summaries есть. Persistent cache/concurrency и measurable PGO win не следуют из их наличия.
- LLVM имеет реальную bounded mapping implementation; это не general production/runtime qualification.
- C# source→PE/PDB→existing CIL→shared backend→image — приоритет. Direct source lowering остаётся experimental; новый importer/IR/Contracts assembly требует конкретной зависимости.
- Внешняя рекомендация становится задачей только после source inspection и actual repro/consumer need. Отсутствие exhaustive proof не compiler defect.

Точные owners — [v3/94](../../HybridCPU_Compiler_vNext_Plan_v3/94_SOURCE_INDEX.md), current status — [v3/00](../../HybridCPU_Compiler_vNext_Plan_v3/00_CODE_VERIFICATION_REPORT.md), единственная очередь — [v3/95](../../HybridCPU_Compiler_vNext_Plan_v3/95_IMPLEMENTATION_ORDER_AND_COMMITS.md).
