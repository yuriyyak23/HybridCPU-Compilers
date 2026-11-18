# 98 — Toolchain и Roslyn policy

Сверка локальных pins/contracts: 2026-11-09. Это policy для данного дерева, не обзор последних upstream releases. Qualification: no.

| Identity | Current source contract |
|---|---|
| Compiler SDK / test runner | [global.json](../../../global.json):11.0.100-rc.2.26504.105 / VSTest |
| Companion default | [Roslyn project](../../../Compilers/HybridCPU_Compiler/Roslyn/HybridCPU.Compiler.Roslyn.csproj): Microsoft.CodeAnalysis.CSharp4.14.0, explicit CSharp13 |
| SDK companion lane | UseSdkRoslynForCompanion=true; references из фактического SDK Roslyn/bincore, build-derived CompanionSdkRoslynHost; matching consumers обязательны |
| NoHeap admission | [companion](../../../Compilers/HybridCPU_Compiler/Roslyn/ManagedSourceCompanionV1.cs): companion-noheap-admission/v14 |
| Publish pack SDK | [pack contracts](../../../Compilers/HybridCPU_Compiler/NativeAot/HybridCpuSdkPackContractsV1.cs):10.0.204 |
| Upstream ILCompiler SDK | [seam contracts](../../../Compilers/HybridCPU_Compiler/NativeAot/NativeAotSeamContractsV1.cs):10.0.105 |

Installed SDK и embedded Roslyn — разные identities. Не выводить поддерживаемый companion language из target framework или SDK default; проверять actual parser/API/assembly metadata и emitted IL. Не фиксировать SDK Roslyn product version по старому summary: получать из реально загруженного host.

Default CSharp13 сохраняется. CSharp15 требует explicit matching SDK lane и existing source/oracle/NoHeap/import acceptance. Bounded labeled-break/continue consumers не означают full-language/metadata/host-packaging support. Package upgrade — самостоятельный compatibility change, а не prerequisite modernization.

Analyzer/generator — существующие netstandard2.0 tooling owners; generator уже IIncrementalGenerator и declaration-only. Новые language constructs принимаются по source diagnostics, actual emit/metadata и downstream source→image, не только parse/emit success.

Не менять publish/upstream pins по installed compiler SDK. Host runtime/.NET libraries, Runtime Async, NativeAOT-host, interop, trimming и serialization features не становятся HybridCPU guest support. Для новой upstream-dependent рекомендации сначала проверить официальные источники.

Source/artifact/import/profile identities сохраняют реальные output-affecting compiler/frontend/options/references. Root VSTest сохраняется; MTP migration, compiler-host AOT и resident service допустимы только по отдельному consumer. Не включать preview, unsafe или async как побочный эффект modernization.

Контракт companion/CLI и SDK references: [Roslyn README](../../../Compilers/HybridCPU_Compiler/Roslyn/README.md). Проверки — [96](96_TEST_STRATEGY.md).
