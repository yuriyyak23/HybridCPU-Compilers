$ErrorActionPreference='Stop'
$root='C:/Users/Yuriy/Desktop/HybridCPU ISE'
$plan="$root/Documentation/Compilers/HybridCPU_Compiler_vNext_Plan_v3"
$white="$root/Documentation/Compilers/Whitebook"
$evidence=$PSScriptRoot
New-Item -ItemType Directory -Force $white | Out-Null
$archive="$plan/Archive/migration-notes-20261109"
New-Item -ItemType Directory -Force $archive | Out-Null
$legacy=@('01_V2_TO_V3_CORRECTIONS.md','04_V2_PHASE_DISPOSITION.md','08_OMISSIONS_INTEGRATED.md','90_MIGRATION_MAP.md')
foreach($name in $legacy){
 $source=[IO.Path]::GetFullPath("$plan/$name");$dest=[IO.Path]::GetFullPath("$archive/$name")
 if(!$source.StartsWith([IO.Path]::GetFullPath($plan)+[IO.Path]::DirectorySeparatorChar) -or !$dest.StartsWith([IO.Path]::GetFullPath($archive)+[IO.Path]::DirectorySeparatorChar)){throw 'Archive escaped'}
 if(Test-Path -LiteralPath $source){Move-Item -LiteralPath $source -Destination $dest}
}
$documents=[ordered]@{}
$documents['README.md']=@'
# План модернизации HybridCPU Compiler

Актуализация: 9 ноября 2026. Общая квалификация продукта: **не подтверждена**.

Цель — расширять поддерживаемые C# программы до работающего image через существующие Roslyn companion, CIL importer и backend. Поддержка определяется конкретным consumer, сигнатурами, телами и проверками; версия языка сама по себе не означает поддержку всех его конструкций.

Начать с [отчёта о возможностях и прогрессе](CURRENT_STATE_AND_PROGRESS.md). [00](00_CODE_VERIFICATION_REPORT.md) содержит применимость доказательств; [95](95_IMPLEMENTATION_ORDER_AND_COMMITS.md) — единственную очередь реализации. Для общей архитектуры предназначен [whitebook](../Whitebook/README.md).

| Документ | Назначение |
|---|---|
| [92 — contracts](92_API_AND_TYPE_CHANGES.md) | Существенные private/API границы |
| [93 — acceptance](93_ACCEPTANCE_MATRIX.md) | Возможности и пределы подтверждённой поддержки |
| [94 — source index](94_SOURCE_INDEX.md) | Код и инструменты проверки |
| [96 — validation](96_TEST_STRATEGY.md) | Проверки по типу изменения |
| [98 — toolchain](98_ROSLYN_DOTNET11_CSHARP15_POLICY.md) | Различия SDK, Roslyn и guest support |
| [99 — ограничения](99_BLOCKERS_AND_CLAIM_BOUNDARIES.md) | Открытые направления и пределы claims |
| [Промпт продолжения](EXECUTION_PROMPT.md) | Краткая инструкция следующей итерации |

P0–P9 описывают области ответственности и зависимости, а не дополнительную очередь. [Оглавление](CONTENTS.md) содержит остальные решения. История перехода между версиями плана вынесена в Archive; старые отчёты не переписаны и не являются новыми заданиями.

Compiler отвечает за статическую семантику, зависимости, scheduling, lowering, provenance и identities. Runtime/OS/provider отвечают за live legality, admission, capabilities, execution, commit, retire и publication. Успешная компиляция не передаёт эти полномочия compiler.
'@
$documents['00_CODE_VERIFICATION_REPORT.md']=@'
# 00 — Состояние доказательств

Проверено 9 ноября 2026 по текущим файлам и сохранённым результатам. Новые build/test/guest campaigns в рамках актуализации документации не запускались. Общая квалификация: **нет**.

## Применимость последнего acceptance

Последний проверенный компонентный результат — [v137, 24 октября](../../../TempEnv/doom-continuation-20260905/scalar-default-20261024/REPORT.md): exact constrained Int32/Int64 scalar helpers с 1–8 параметрами и `default(U)` на primary non-root path.

9 ноября повторно вычислены SHA256 всех **541** файлов из его compiler manifest: отсутствующих и изменённых файлов **0**. Из **493** evaluated inputs отсутствующих и изменённых **0**. Это подтверждает сохранность перечисленных входов, но не является новой сборкой, проверкой нового host или всего рабочего дерева. Manifest не доказывает отсутствие новых файлов вне своего списка. Состав текущих compiler inputs отдельно сопоставлен при документационном аудите.

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
'@
$documents['CURRENT_STATE_AND_PROGRESS.md']=@'
# HybridCPU Compiler — возможности и прогресс

Состояние на 9 ноября 2026. Основание: текущие owners в `Compilers/HybridCPU_Compiler`, проекты и contracts, сохранённый acceptance v137 от 24 октября. Документ описывает реализованные ограниченные сценарии, а не полную совместимость с C#/.NET. Общая квалификация продукта не подтверждена.

## Что представляет собой компилятор

HybridCPU Compiler — набор C#/.NET компонентов для подготовки программ под typed-slot VLIW HybridCPU-v2. Он принимает native instruction/IR inputs и ограниченные managed программы. Для C# путь проходит через Roslyn, PE/CIL и существующий semantic importer; это не произвольная трансляция любого .NET приложения.

Основной результат модернизации — существующий связный source→image путь и повторное использование backend, а не только анализ или успешный Roslyn emit. Скомпилированный image остаётся статическим артефактом. Загрузка, разрешение исполнения и публикация результата принадлежат runtime/OS/provider.

## Реализованные возможности

| Область | Что имеется | Существенное ограничение |
|---|---|---|
| C# ingress | Owned source/options/references; source diagnostics и NoHeap до deterministic PE/PDB; CLI source→image | Не весь C# и не произвольные framework dependencies |
| Module/body resolution | BodyWorld, bounded call graph, точные definition/generic identities, раздельные reference roles | Metadata declaration не даёт права импортировать тело; filesystem discovery не заменяет explicit input |
| Scalar control flow | Typed CFG/SSA, scalar arithmetic, branches, helpers, доказанные counted loops и часть nested loops | Original budgets, purity, EH/fault и конкретные формы ограничивают допуск |
| Value types | Immutable single-scalar Int32/Int64 конструкции, getters, factories/local copies/joins, выбранные external definitions | Не общий struct/aggregate ABI |
| Arrays/references/String | Проверенные primitive array и String accessor consumers, argument/identity/fault controls | Произвольный object reference не даёт layout/accessor authority |
| Nullable locals | Primitive carriers и enum с восемью integral underlyings; copy/default/presence/accessors, checked Value | Exact nominal/width proof; internal representation не общедоступный nullable ABI |
| Private nullable consumers | Primary non-root Nullable<int> input/return, fallback computations, scalar/nullable dependencies, bounded loops | Public/root/external nullable signatures, byref и общий EH требуют отдельных gates |
| Constrained scalar helpers | Selected Int32/Int64, exact `where U:struct`, private static primary non-root, 1–8 `U` inputs и `U` return; direct/transitive, `default(U)` | Не general constraint solver; zero-input signature пока отклоняется |
| Backend | Shared scheduling, bundling, agreement, lowering, relocation, serialization; bounded witnessed register allocation | RA должна оставить согласованные final layout/physical operands; нет универсального target claim |
| Политики и профиль | Immutable/versioned policies, explicit opt-in profile compilation, metrics/shadow paths | Детерминизм не доказывает ускорение; persistent cache/concurrent session не следуют из наличия identities |
| Runtime integration | Concrete transport/emission adapter; отдельные managed ABI/EH/GC/object/image owners | Compile success не execution, retire или publication |

Точная матрица — [93](93_ACCEPTANCE_MATRIX.md), contracts — [92](92_API_AND_TYPE_CHANGES.md), ссылки на код — [94](94_SOURCE_INDEX.md).

## Что изменилось в ходе модернизации

1. **Общее ядро.** Canonical facade физически находится в Core; shared bundler и post-bundle tail используются существующими путями. Runtime emission сохранён на конкретной границе adapter, без переноса live state в compiler core.
2. **Реальный managed ingress.** Roslyn companion, NoHeap, PE/PDB provenance и BodyWorld связаны с importer/object/link/image. Declaration/body roles и primary-module source maps стали явными границами входа.
3. **Расширение практических consumers.** Добавлены scalar value/nullable, helper/CFG/loop, exact generic context и external definition сценарии. Допуск расширяется по emitted CIL и final typed/body proof, а не по имени C# конструкции.
4. **Последний связный блок.** К 24 октября constrained scalar methods расширены от unary/binary к существующему восьмирегистровому ABI, затем принят exact generic `default(U)` local-zero body. Сохранены original budgets, eager evaluation, identity и runtime boundaries.
5. **Проверяемые артефакты.** Есть deterministic repeat, scalar/CoreCLR/encoded/serialized oracles и negative controls. Неудачные попытки сохраняются отдельно, а не превращаются в PASS.

Это описание конкретно реализованного прогресса. Процент готовности не вычисляется: roadmap содержит независимые consumer, performance и execution/publication требования, не сводимые к числу закрытых helper shapes.

## Что подтверждено проверками

Последний [immutable report v137](../../../TempEnv/doom-continuation-20260905/scalar-default-20261024/REPORT.md) фиксирует Release/tiered-off runs: default profile **426**, SDK profile **427**, full Managed **433** PASS-строки, все exit 0; focused **48** source cases. Добавлены **48** физических cases. Повтор PE/PDB/maps/image, 304 прежних images и 48 lane images проверены в заявленном scope.

9 ноября заново проверены хэши 541 compiler и 493 evaluated inputs: drift 0. Это сохранность входов acceptance, а не повторная кампания или новая qualification runtime. Default и SDK PE/PDB/maps не обязаны совпадать между собой; image equality и semantic equivalence проверяются отдельными отношениями. Generic/concrete I8 zero не имеют заявленного code-byte equality.

## Что остаётся открытым

Ближайший consumer — `private static U Adjust<U>() where U:struct => default(U)`: body уже знаком, но exact method signature admission требует хотя бы один параметр. Отказ HCCIL1702 и положительные inline/concrete controls сохранены; владельцы исправления — metadata matcher и final scalar proof. Реализация выбирается только через [очередь95](95_IMPLEMENTATION_ORDER_AND_COMMITS.md).

Более широкие задачи: новые C# emitted-IL/metadata формы, generic constraints, nullable/aggregate/byref/EH ABI, дополнительные frontend/target contours. Их наличие не означает, что текущие foundation отсутствуют или должны быть переписаны. Cache/concurrency/host AOT/LLVM expansion и performance promotion требуют своего consumer и измерений.

Полная совместимость с C#/.NET/BCL, async/unsafe/interop/trimming, произвольные агрегаты и публичный nullable ABI не подтверждены. Full solution/Debug и loader/guest/publication acceptance не следуют из Release component runs. Исторические host failures не доказывают нынешний blocker и не считаются исправленными без отдельной проверки.

## Как читать план дальше

[Whitebook](../Whitebook/README.md) объясняет архитектуру без истории отдельных итераций. В активном плане 00 хранит применимость evidence, 92/93 — contracts/границы, 95 — одно следующее действие, 96 — validation. Migration notes перенесены в Archive; frozen reports остаются evidence. Это устраняет повторные status ledgers и не стирает failed evidence.
'@
$documents['92_API_AND_TYPE_CHANGES.md']=@'
# 92 — Действующие contracts

Сверка 9 ноября 2026. Код и exact input manifests имеют приоритет перед обзором. Общая архитектура — [whitebook](../Whitebook/README.md), матрица возможностей — [93](93_ACCEPTANCE_MATRIX.md).

## Ownership и pipeline

Core содержит `HybridCpuCanonicalCompiler`, существующий IR и shared scheduling/bundling/post-bundle. CIL/NativeAOT сохраняют собственные calling convention, metadata, EH/GC, object/link/startup/image responsibilities. Concrete `NativeTransportRuntimeAdapter` отвечает за runtime emission/transport; Core не получает runtime state.

Register allocation — bounded post-bundle transform. Accepted allocation требует witness и согласованных final schedule/bundles, physical operands, relocation и agreement. Нельзя подменять этим правилом defaults/raw/value-only paths.

Fixed target contracts: W=8, 4-way SMT, 32×64-bit architectural registers на VT; ALU lanes 0–3, LSU 4–5, shared DMA/MatrixTile lane 6, branch/system lane 7. MatrixTile LOAD/STORE сохраняют stream class и HardPinned. Архитектурные спецификации перечислены в [02](02_ARCHITECTURE_INVARIANTS.md).

## Managed input и identities

- Source diagnostics и NoHeap rejection предшествуют PE emit. Успешный emit ещё не acceptance importer/backend.
- Metadata references предоставляют declarations. Только явно выбранные body references дают bodies; поиск по filesystem/framework не расширяет closure.
- BodyWorld владеет module readers в пределах вызова; generic owner и method contexts проверяются раздельно. Definition identity включает фактические module/signature/context facts.
- Source, PE/PDB artifact, map, import/options и graph identities имеют разные scopes. Dependency methods не используют primary PDB; maps принадлежат исходному primary module.
- Cancellation проверяется в реализованных ingress/orchestration/backend/link boundaries. Общая latency любых внутренних операций и shared-session concurrency не гарантируются.

## Scalar и nullable scopes

Immutable single-scalar Int32/Int64 values требуют exact nominal layout/constructor/getter/body proof. Primitive scalar carrier не равен nominal struct. External value definition берётся из явно переданного PE, не из совпадения имени.

Nullable locals поддерживаются для primitive/enum carriers в точных формах. Payload/presence и packed representation — внутренние lowering contracts, не общий публичный ABI. Checked `Value` использует существующий checked helper/EH boundary.

Private primary non-root `Nullable<int>` input/return и helper graphs допускаются только с exact signature/definition, purity, CFG/SSA, original budgets и fault/EH/cctor guards. Есть forward/countable producers, dependencies и bounded argument carriers. Public/root/external nullable signatures, general aggregates/byref/EH остаются отдельными gates.

Selected constrained scalar methods: private static primary non-root `U M<U>(U...) where U:struct`, method context ровно Int32 или Int64, 1–8 exact `!!0` by-value параметров и `!!0` return. GP index 0, точные struct flags и nominal core ValueType constraint; owner context проверяется независимо. Arity9, mixed primitive substitution, дополнительные constraints/GP и byref не допускаются этим исключением.

Discovery shape не даёт admission. Graph-owned final definition/purity/typed-body proof должен завершиться до установки ingress authority. BudgetExhausted из proof сохраняется; discovery не может скрыть budget или fault.

`default(U)` принят для exact selected signed context: immediate untargeted `ldloca/initobj !!0/ldloc` одного resolved scalar local заменяется scalar zero/store с сохранением offsets, rows и budgets. Произвольный initobj/address escape не открыт. Zero-input constrained signature пока не принята — [95](95_IMPLEMENTATION_ORDER_AND_COMMITS.md).

## Loops, effects и policies

Допускаются доказанные counted I4/U4/I8/U8 loops и конкретные nested forms. Bounds/induction требуют exact carrier, invariant invocation/reset и no-wrap proofs; multiply и offset проверяются отдельно. Это не разрешение arbitrary loops, mutable/nonlinear bounds или effects. Eager аргументы сохраняют faults и вызовы даже при неиспользуемом параметре callee.

Immutable scheduling/bundling/composite policies и explicit default-off profile input имеют versioned identities. Observational metrics/shadow paths не должны незаметно выбирать код. Наличие identity не создаёт persistent cache contract; profile determinism не performance qualification.

Compiler facts/annotations/evidence не предоставляют runtime admission, permissions, commit, retire или publication. Release validator проверяет заявленные структурные условия, а actual execution доказывается отдельно. Применимость последнего snapshot — [00](00_CODE_VERIFICATION_REPORT.md).
'@
$documents['93_ACCEPTANCE_MATRIX.md']=@'
# 93 — Матрица возможностей и acceptance

9 ноября 2026. «Реализовано» означает ограниченный контракт текущего owner, а не полную языковую или runtime совместимость. Последние сохранённые runs и применимость хэшей — [00](00_CODE_VERIFICATION_REPORT.md). Новая test campaign при обновлении документации не проводилась.

| Область | Реализовано в существующих owners | Граница |
|---|---|---|
| Core | Canonical facade, shared scheduling/bundling/agreement/post-bundle, bounded witnessed RA | Не новый ISA и не runtime authority |
| C# companion | Diagnostics/NoHeap, deterministic PE/PDB, CLI source→image | Default CSharp13; CSharp15 требует explicit matching SDK lane; не full-language support |
| BodyWorld | Bounded closure, exact definitions/generic contexts, explicit body roles | Declaration-only reference не даёт body authority |
| Provenance/maps | Source/artifact/import/options identities, primary PE/PDB maps | Dependency не получает primary PDB |
| Scalar values | Immutable single-scalar I4/I8 constructors/getters/factories/local/copy/join, selected external definitions | Exact nominal/layout/purity proof; не general aggregate ABI |
| Arguments | Primitive/reference assignments, проверенные arrays/String consumers | Layout/accessor/fault authority не выводится из ObjectReference |
| Nullable locals | Primitive carriers и enum integral underlyings, default/copy/presence/accessors, checked Value | Nominal widths и internal lowering; не общий nullable/EH ABI |
| Private Nullable<int> | Primary non-root input/return, fallback computations, scalar/nullable dependencies | Public/root/external signatures и broader byref/EH отдельны |
| Scalar helpers/CFG | Pure direct/transitive calls, typed forward CFG, доказанные counted/nested loops | Original budgets, no-wrap, faults, cctor/EH/role/identity guards |
| Constrained helpers | Exact signed I4/I8 owner/method contexts, private static 1–8 `U` operands, `U` return, `default(U)` | Zero-input, arity9, general constraints/byref/aggregate не открыты |
| Cancellation | Scoped checkpoints в ingress/shared orchestration/backend/link | Не universal latency или shared concurrency guarantee |
| Policies/profile | Immutable policies, explicit opt-in profile lane/trace | Нет доказанного общего performance win или persistent cache acceptance |
| Runtime adapter | Concrete emission/transport contract | Image success не loader/guest/publication |
| Release tools | Structural validation, identities, scalar summaries | Декларации clean builds и parser success не actual build/execution receipts |

## Последний компонентный блок

v137 подтвердил generic scalar zero в 48 source и 48 physical cases: arity3–8 × selected I4/I8 × owner int/long × direct/transitive. Unary/binary zero дополнительно проверены CLI. Full default/SDK/Managed runs имеют exit 0; counts и manifests хранятся в00/report, а не дублируются здесь.

Проверены deterministic PE/PDB/maps/image repeat, independent scalar/host/encoded/serialized oracle, presence/extrema/high-bit/overflow, original inputs и eager call counts. Generic/concrete I8 zero семантически эквивалентны; code-byte equality для них не заявлена. Negative families охватывают roles/constraints/identity/body/effects/EH/byref/budgets/fault/cancel/reuse/diagnostic precedence в указанном в report объёме, не все возможные комбинации языка.

## Общие пределы

MatrixTile lane6/HardPinned сохраняется. Release wide-remainder exact equality и независимый quotient/MUL/SUB physical oracle подтверждены последним компонентным набором. Debug equivalence не следует из Release результата.

Full solution, произвольная BCL, async/unsafe/interop/trimming, general aggregates, loader/guest/publication и обычная стабильность preview host не квалифицированы этими runs. Общая qualification — **нет**. Следующий consumer определяется только [95](95_IMPLEMENTATION_ORDER_AND_COMMITS.md); широкие ограничения — [99](99_BLOCKERS_AND_CLAIM_BOUNDARIES.md).
'@
$documents['94_SOURCE_INDEX.md']=@'
# 94 — Индекс кода

Пути проверены 9 ноября 2026. Индекс показывает owners; наличие файла не является acceptance. Actual Compile/ProjectReference/TargetPath проверяются с параметрами конкретной сборки.

| Ответственность | Код |
|---|---|
| Canonical facade | [HybridCpuCanonicalCompiler](../../../Compilers/HybridCPU_Compiler/Core/Compilation/HybridCpuCanonicalCompiler.cs) |
| IR compilation | [HybridCpuIrProgramCompiler](../../../Compilers/HybridCPU_Compiler/Core/IR/Compilation/HybridCpuIrProgramCompiler.cs) |
| Shared bundling / allocation ordering | [HybridCpuProgramBundler](../../../Compilers/HybridCPU_Compiler/Core/IR/Compilation/HybridCpuProgramBundler.cs) |
| Lowering / relocation / serialization | [HybridCpuPostBundleCompiler](../../../Compilers/HybridCPU_Compiler/Core/IR/Compilation/HybridCpuPostBundleCompiler.cs) |
| Runtime transport / emission | [adapter](../../../Compilers/HybridCPU_Compiler/API/Runtime/NativeTransportRuntimeAdapter.cs), [emission](../../../Compilers/HybridCPU_Compiler/API/Runtime/NativeTransportRuntimeAdapter.Emission.cs) |
| Source compilation / admission | [ManagedSourceCompanionV1](../../../Compilers/HybridCPU_Compiler/Roslyn/ManagedSourceCompanionV1.cs), [NoHeap](../../../Compilers/HybridCPU_Compiler/Analyzers/KernelNoHeapAnalyzer.cs) |
| Reference roles / BodyWorld | [input](../../../Compilers/HybridCPU_Compiler/Roslyn/ManagedSourceCompanionV1.Input.cs), [BodyWorld](../../../Compilers/HybridCPU_Compiler/Roslyn/ManagedSourceCompanionV1.BodyWorld.cs) |
| Source CLI | [ManagedSourceCommandLineV1](../../../Compilers/HybridCPU_Compiler/Roslyn/ManagedSourceCommandLineV1.cs), [usage](../../../Compilers/HybridCPU_Compiler/Roslyn/README.md) |
| CIL decode / envelope | [RestrictedCilImporterV1](../../../Compilers/HybridCPU_Compiler/Cil/RestrictedCilImporterV1.cs) |
| Metadata / exact generic signatures | [MetadataBindingsV1](../../../Compilers/HybridCPU_Compiler/Cil/RestrictedCilImporterV1.MetadataBindingsV1.cs) |
| Graph / module lifetime | [CallGraphV1](../../../Compilers/HybridCPU_Compiler/Cil/RestrictedCilImporterV1.CallGraphV1.cs), [ModuleContext](../../../Compilers/HybridCPU_Compiler/Cil/RestrictedCilImporterV1.ModuleContext.cs) |
| CFG / SSA | [ControlFlowV2](../../../Compilers/HybridCPU_Compiler/Cil/RestrictedCilImporterV1.ControlFlowV2.cs) |
| Nullable / generic local zero | [NullableInt32](../../../Compilers/HybridCPU_Compiler/Cil/RestrictedCilImporterV1.NullableInt32.cs) |
| Scalar projections / purity | [StaticProjectionV1](../../../Compilers/HybridCPU_Compiler/Cil/RestrictedCilImporterV1.StaticProjectionV1.cs), [ExternalScalarValues](../../../Compilers/HybridCPU_Compiler/Cil/RestrictedCilImporterV1.ExternalScalarValues.cs) |
| Maps / object link | [ManagedSourceMapV1](../../../Compilers/HybridCPU_Compiler/Cil/ManagedSourceMapV1.cs), [ScalarControlFlowV2ObjectLinkerV1](../../../Compilers/HybridCPU_Compiler/Cil/ScalarControlFlowV2ObjectLinkerV1.cs) |
| Physical native ABI | [HybridCpuNativeAbiContractV2](../../../Compilers/HybridCPU_Compiler/Core/Target/HybridCpuNativeAbiContractV2.cs) |
| Source regressions | [CompilerProfileSmoke](../../../Compilers/HybridCPU_Compiler/Tools/CompilerProfileSmoke/Program.cs), [nullable](../../../Compilers/HybridCPU_Compiler/Tools/CompilerProfileSmoke/NullableInt32Checks.cs), [I8 values](../../../Compilers/HybridCPU_Compiler/Tools/CompilerProfileSmoke/Int64ValueChecks.cs) |
| Physical / serialized oracle | [ManagedPortSmoke](../../../Compilers/HybridCPU_Compiler/Tools/ManagedPortSmoke/Program.cs), [nullable](../../../Compilers/HybridCPU_Compiler/Tools/ManagedPortSmoke/NullableValueSmoke.cs), [evaluator](../../../Compilers/HybridCPU_Compiler/Tools/ManagedPortSmoke/NativeConstantSmoke.cs), [wide-rem](../../../Compilers/HybridCPU_Compiler/Tools/ManagedPortSmoke/UnsignedDivisionSmoke.cs) |
| Scheduler measurement | [SchedulerBenchmark](../../../Compilers/HybridCPU_Compiler/Tools/SchedulerBenchmark/SchedulerBenchmark.csproj) |
| Release evidence boundaries | [ReleaseQualification README](../../../Compilers/HybridCPU_Compiler/ReleaseQualification/README.md) |
| Toolchain / isolation | [global.json](../../../global.json), [Isolation.props](../../../Compilers/CpuInterfaceBridge/Validation/Isolation.props), [SDK Roslyn references](../../../Compilers/HybridCPU_Compiler/Roslyn/SdkRoslynHost.targets) |

Project boundaries: [Core](../../../Compilers/HybridCPU_Compiler/Core/HybridCPU.Compiler.Core.csproj), [CIL](../../../Compilers/HybridCPU_Compiler/Cil/HybridCPU.Compiler.Cil.csproj), [Roslyn](../../../Compilers/HybridCPU_Compiler/Roslyn/HybridCPU.Compiler.Roslyn.csproj), [upper compiler](../../../Compilers/HybridCPU_Compiler/HybridCPU_Compiler.csproj), [NativeAOT adapter](../../../Compilers/HybridCPU_Compiler/NativeAot/HybridCPU.Compiler.NativeAot.Adapter.csproj), [LLVM](../../../Compilers/HybridCPU_Compiler/LLVM/HybridCPU.Compiler.Llvm.csproj).
'@
$documents['95_IMPLEMENTATION_ORDER_AND_COMMITS.md']=@'
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
'@
$documents['99_BLOCKERS_AND_CLAIM_BOUNDARIES.md']=@'
# 99 — Открытые границы

9 ноября 2026. Единственная очередь реализации — [95](95_IMPLEMENTATION_ORDER_AND_COMMITS.md). Этот файл не содержит второго backlog по отдельным helper shapes.

## Функциональное расширение

Главное направление — реальные C# consumers и мешающие им emitted-IL/metadata/CFG/ABI/backend ограничения. Existing companion, importer, maps, cancellation и shared stages уже реализованы; их повторное создание не является задачей.

General constraints, public/root/external nullable signatures, более широкий aggregate/byref/EH ABI, новые language/BCL forms и дополнительные frontend/target contours требуют отдельных acceptance. Отсутствие blanket поддержки не означает отсутствие всех частных форм: точный реализованный scope — [93](93_ACCEPTANCE_MATRIX.md).

Persistent cache, shared-session concurrency, host AOT, broader LLVM и default-on PGO не prerequisites обычного C# consumer. Нужны конкретный потребитель, lifetime/invalidation/identity contract и измеримые результаты. Deterministic profile lane не доказывает performance win.

## Границы доказательств

- Последний Release acceptance относится к exact inputs и режиму из [00](00_CODE_VERIFICATION_REPORT.md), не ко всему dirty tree.
- Debug и full-solution acceptance не следует из Release component runs. Release wide-rem exact bytes сохраняются; Debug equivalence требует собственной проверки.
- Исторические preview-host/Code Integrity failures не объявляются ни текущим воспроизведённым blocker, ни исправленной причиной. Их подробности остаются в immutable reports.
- CoreCLR/scalar/encoded/serialized oracle не равен guest execution. Равные images не доказывают независимые clean builds без provenance producer.
- Release schemas/parser/hash checks проверяют consistency/admission, а не actual loader/runtime/replay/publication.

Compiler facts/profile/annotations/secure descriptors не выдаёт permission runtime/OS/provider. Execution, commit, retire и publication — разные события с отдельными владельцами. Общая qualification продукта **не подтверждена**. Нужный объём проверки выбирается по заявленному claim, без автоматического требования многодневной нагрузки для обычного compiler patch.
'@
$documents['EXECUTION_PROMPT.md']=@'
# Продолжение модернизации Compiler

## Роль и цель

Старший инженер C#/.NET AOT и HybridCPU-v2. Итеративно расширяй реальные C# consumers до source→image по единственной [очереди95](95_IMPLEMENTATION_ORDER_AND_COMMITS.md). Аудит, план или test-only slice не заменяют реализацию.

## Вводные

Root: `C:/Users/Yuriy/Desktop/HybridCPU ISE`. Compiler: `Compilers/HybridCPU_Compiler`. Сначала читай [README](README.md), [00 evidence](00_CODE_VERIFICATION_REPORT.md), [95](95_IMPLEMENTATION_ORDER_AND_COMMITS.md), [92 contracts](92_API_AND_TYPE_CHANGES.md), [93 acceptance](93_ACCEPTANCE_MATRIX.md), [96 validation](96_TEST_STRATEGY.md), [98 toolchain](98_ROSLYN_DOTNET11_CSHARP15_POLICY.md), [99 boundaries](99_BLOCKERS_AND_CLAIM_BOUNDARIES.md). [94](94_SOURCE_INDEX.md) ведёт к коду; [whitebook](../Whitebook/README.md) даёт общий обзор. Остальные owners открывай по зависимости.

Перед изменением проверь actual cwd, AGENTS по цепочке/scope, Git root/HEAD/user diff, live processes. Не выполняй reset/checkout/clean/rollback, не восстанавливай массово удалённые outputs и не останавливай чужие процессы. Незавершённый runner проверяй по executable/start/command line и terminal exit; observation timeout не повод запускать дубль.

## Работа

1. Отдели accepted snapshot от current source; проверь hashes/evaluated owners. Устаревший report не квалифицирует изменённый source.
2. Воспроизведи consumer: diagnostics/NoHeap → PE/PDB → emitted IL/metadata → existing RestrictedCilImporterV1 → canonical IR → bundle/agreement/post-bundle → object/link/image.
3. Исправь конкретную зависимость в существующем owner. Не создавай второй importer, harness, IR taxonomy, emitter, cache или daemon. Discovery не превращай в admission.
4. Сохраняй nominal ingress, reference/body roles, exact definition и owner/method contexts, purity, CFG/SSA, original budgets, EH/ABI/fault boundaries и diagnostic precedence. General constraints/public nullable/byref/aggregates требуют отдельных gates.
5. Добавь meaningful source positive/negative, NoHeap, emitted-CIL/metadata oracle, deterministic PE/PDB/maps/image repeat и independent scalar/encoded/serialized physical ABI. Проверяй applicable null/present/extrema/high-bit/overflow/eager evaluation/call-count, role/identity/body/effect/EH/width/byref/budget/fault/cancel/reuse/precedence negatives.
6. Используй существующие CompilerProfileSmoke и ManagedPortSmoke. Для shared managed changes — applicable default/explicit SDK profile и full Managed; дополнительные CLI/oracle/scheduler/runtime нагрузки по изменённому contract. Scheduler48/49 и192/193 нужны при изменении scheduler contract. Release wide-rem exact bytes и независимый quotient/MUL/SUB ABI/clobber/result/fault oracle сохраняй. Не повторяй кампании без изменения, failure или unresolved risk.
7. Fresh outputs/restore/TEMP/TMP/obj/bin — под `TempEnv/doom-continuation-20260905/<iteration>`. Builds serial `-m:1 -nr:false`; evidence с `DOTNET_TieredCompilation=0`. Default и SDK lane/reference products раздельны; actual TargetPath и source hashes проверяй.
8. Сохрани failures как failed evidence. Создай один immutable REPORT.md с owners/root cause, exact commands/exits, hashes, outputs, equivalence, negatives и limitations. Обнови только factual current docs/manifest/checksums; в95 оставь один следующий actual gate. После связного принятого блока продолжай dependency-ready consumer.

## Неизменные границы

Fixed W8/4-way SMT/32×64-bit VT, ALU0–3/LSU4–5/shared DMA-MatrixTile6/branch-system7, MatrixTile HardPinned. RA — bounded post-bundle transform с final witness/schedule/bundles/agreement. Runtime emission у adapter. Architectural contracts — [02](02_ARCHITECTURE_INVARIANTS.md); MatrixTile changes дополнительно сверять со [stream transport](../../Documentation/Stream%20WhiteBook/03_MatrixTile/02_Tile_Stream_Transport.md).

Compiler owns static semantics/dependencies/scheduling/lowering/provenance/identities; runtime/OS/provider owns live legality/capabilities/admission/execution/commit/retire/publication. Metadata references дают declarations, только selected body references дают bodies; dependencies не получают primary PDB. Source/artifact/map/import/cache identities различны.

Общая qualification остаётся no, пока соответствующие claims не доказаны; component acceptance не завершает roadmap. Финальный ответ по-русски: возможность, root cause/owners, actual commands/exits, equivalence/negatives, ограничения, полные ссылки на код/report и следующий consumer.
'@
$documents['07_PROJECT_GRAPH_DECISION.md']=@'
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

Наличие проекта в root solution не гарантирует включение всех tooling projects или их acceptance. Для compiler consumers используется direct project build. Устаревшие сведения о missing corpus reference и количестве solution entries удалены: они не описывают задачу модернизации и не заменяют actual graph evaluation.

Один source имеет один compiling owner в проверяемом graph. Compile Remove допустим для намеренного разделения upper/runtime и Core; его наличие само по себе не ошибка. Platform.Contracts хранит действительно общие immutable architecture facts. Compiler-local facts остаются в Core; новый contracts/emitter assembly оправдан только конкретной зависимостью.

Core не должен зависеть от CIL или concrete ISE implementation через обратный cycle. Разделение assemblies не передаёт runtime authority. Код — [94](94_SOURCE_INDEX.md).
'@
foreach($entry in $documents.GetEnumerator()){[IO.File]::WriteAllText("$plan/$($entry.Key)",$entry.Value.Trim()+"`n",[Text.UTF8Encoding]::new($false))}
[IO.File]::WriteAllText("$archive/README.md",@'
# История миграционных решений

Перенесено из активного плана 9 ноября 2026: corrections, disposition, omissions и migration map. Эти заметки объясняют прежнюю организацию плана и не являются текущей очередью или новым acceptance. Исходные тексты сохранены; их относительные ссылки рассчитаны на прежнее расположение.

Текущее состояние — ../../README.md, очередь — ../../95_IMPLEMENTATION_ORDER_AND_COMMITS.md. Immutable execution reports не перемещались и не переписывались.
'@)
$whitebook=@'
# HybridCPU Compiler — Whitebook

Редакция 9 ноября 2026. Обзор архитектуры и возможностей компилятора HybridCPU-v2. Основание — текущие source owners и [отчёт о состоянии](../HybridCPU_Compiler_vNext_Plan_v3/CURRENT_STATE_AND_PROGRESS.md). Это описание реализованного устройства и его границ, не обещание полной совместимости с C#/.NET и не release qualification.

## 1. Назначение

Компилятор преобразует поддержанные входные программы в статические артефакты для typed-slot VLIW HybridCPU-v2. Его задачи: установить допустимую статическую семантику, построить зависимости и control flow, назначить операции на ресурсы target, сформировать bundles, выполнить lowering/relocation и подготовить код с необходимыми metadata и provenance.

Основные входы — native instruction/IR path и managed PE/CIL. C# поступает через Roslyn companion, поэтому успешный синтаксический анализ или PE emit — только первая часть пути. Поддержка языка определяется тем, какие emitted IL, signatures, bodies и runtime contracts проходят downstream compiler.

Компилятор не является владельцем live legality. Даже корректный image должен быть принят и исполнен runtime/OS/provider по их собственным правилам. Статические facts, annotations и доказательства не являются capability или permission.

## 2. Целевая архитектура

Базовый контракт HybridCPU-v2 фиксирует bundle шириной W=8, 4-way SMT и architectural state 32×64-bit registers на virtual thread. Тип слота влияет на placement и legality:

| Lanes | Назначение |
|---|---|
| 0–3 | ALU |
| 4–5 | LSU |
| 6 | Shared DMA/MatrixTile |
| 7 | Branch/system |

MatrixTile LOAD/STORE используют lane6, соответствующий stream class и HardPinned contract. Это не обычные LSU операции. Компилятор обязан сохранять target facts при scheduling, bundling и physical lowering.

Этот whitebook не заменяет ISA/CPU contracts. Нормативные источники: [VLIW format](../../Documentation/WhiteBook/2.%20vliw-binary-format.md), [architectural model](../../Documentation/WhiteBook/4.%20architectural-model.md), [typed slots](../../Documentation/WhiteBook/6.%20typed-slot-scheduling.md), [compiler/runtime](../../Documentation/WhiteBook/7.%20compiler-runtime-contract.md) и [MatrixTile transport](../../Documentation/Stream%20WhiteBook/03_MatrixTile/02_Tile_Stream_Transport.md).

## 3. Путь от входа к image

```mermaid
flowchart TD
    CS[C# source и explicit references] --> R[Roslyn diagnostics и NoHeap]
    R --> PE[Deterministic PE и PDB]
    PE --> BW[BodyWorld и Restricted CIL importer]
    N[Native frontend inputs] --> IR[Existing compiler IR]
    BW --> IR
    IR --> SB[Scheduling и bundling]
    SB --> RA[Optional bounded register allocation]
    RA --> AG[Final agreement и witness checks]
    AG --> T[Lowering и relocation]
    T --> A[Serialization / object / link / image]
    A --> RT[Runtime adapter / loader boundary]
```

Диаграмма показывает общие стадии, а не обязательный вызов одной facade каждым frontend. Canonical facade находится в Core. CIL и NativeAOT используют shared stages и сохраняют собственные ABI, EH/GC, object/link/startup/image responsibilities. Native, managed и LLVM входы не считаются автоматически семантически эквивалентными.

При source error или NoHeap violation emit не должен выдавать принятый PE. Unsupported IL должен завершить compiler refusal до object/image. Final output допускается только после проверок соответствующего пути.

## 4. Managed frontend и владение входами

Roslyn companion владеет переданными source, options и references, выполняет diagnostics/NoHeap и deterministic emit. CLI предоставляет `compile-source-image` с явными source/reference/root и output options. Default language — CSharp13; CSharp15 доступен через explicit matching SDK-host lane в поддержанных consumers.

Роли references различаются. Metadata declaration позволяет разрешать типы и сигнатуры, но не импортировать произвольное тело. Только явно выбранные body references дают BodyWorld право использовать тела dependency. Поиск DLL по filesystem/framework не заменяет input authority.

BodyWorld строит bounded closure методов, проверяет exact module/definition/signature и generic owner/method contexts. Module readers имеют scoped lifetime. Discovery обнаруживает кандидата; admission требует final semantic/typed/body proof. Это различие особенно важно для constrained generic helpers: сходство имени или carrier не доказывает nominal identity и допустимость тела.

PE/PDB maps принадлежат primary module. Dependency method token не получает primary PDB. Source, artifact, map, import/options и graph identities описывают разные отношения и не должны использоваться как взаимозаменяемые cache keys.

Основные owners: [companion](../../../Compilers/HybridCPU_Compiler/Roslyn/ManagedSourceCompanionV1.cs), [BodyWorld](../../../Compilers/HybridCPU_Compiler/Roslyn/ManagedSourceCompanionV1.BodyWorld.cs), [importer](../../../Compilers/HybridCPU_Compiler/Cil/RestrictedCilImporterV1.cs).

## 5. Семантика, типы и поддержанные C# сценарии

Importer работает с ограниченным CIL и typed control-flow/dataflow, включая CFG/SSA и budgets. Допуск зависит от exact signatures, bodies, effects, nominal types и роли метода, а не только от opcode или C# syntax.

Реализованы scalar Int32/Int64 consumers, arithmetic/branches и pure helper graphs, а также доказанные counted и некоторые nested loops. Loop bounds требуют конкретных invariant/no-wrap proofs. Возможные faults и eager evaluation нельзя удалить только потому, что результат или параметр не используется.

Для value types поддержаны immutable single-scalar конструкции, getters, factories, local copies/joins и явно выбранные external definitions. Scalar representation не превращает любой struct того же размера в допустимый nominal value. General aggregate ABI остаётся отдельной задачей.

Nullable locals покрывают primitive carriers и enum с integral underlyings. Presence/payload либо packed representation — internal lowering, не обещание общего nullable ABI. Checked `Value` использует существующий checked helper и соответствующий EH contract. Private primary non-root Nullable<int> inputs/returns и их helper graphs имеют более узкий допуск, чем local operations.

Последний принятый constrained scope: private static method `U M<U>(U...) where U:struct`, selected Int32/Int64, 1–8 exact `U` operands и `U` return, primary non-root signed contexts. Проверяются exact struct constraint, owner/method context, definition, purity, typed body и original budgets. Direct/transitive helper и generic `default(U)` local-zero body реализованы. Zero-input constrained signature пока отвергается.

Primitive arrays, String accessors и reference/argument consumers доступны в проверенных bounded формах с identity, GC/fault и layout guards. Это не общая совместимость с BCL. Полная поддержка C#, async, unsafe, interop, trimming, произвольных constraints/byref/EH и публичного nullable ABI не заявлена. Актуальная [матрица](../HybridCPU_Compiler_vNext_Plan_v3/93_ACCEPTANCE_MATRIX.md) отделяет private/local scopes от общих контрактов.

## 6. Shared backend

Backend использует существующий IR и общие механизмы scheduling, bundling, agreement и post-bundle compilation. Scheduler обязан учитывать data/control/resource dependencies и typed-slot placement. Bundling и final lowering должны сохранить эти ограничения в physical instruction stream.

Register allocation применяется как bounded post-bundle transformation. Принятый allocation требует witness, согласованных final schedule/bundles, physical operands и final relocation/agreement. Нельзя считать initial schedule достаточным доказательством после изменения размещения регистров.

Post-bundle tail выполняет mechanical lowering, разрешение control-flow relocations, annotations и serialization в зависимости от caller. Object/link/image owners добавляют свои symbol/ABI/metadata обязанности. Общая стадия backend не отменяет frontend-specific semantics.

Ключевые owners: [canonical facade](../../../Compilers/HybridCPU_Compiler/Core/Compilation/HybridCpuCanonicalCompiler.cs), [bundler](../../../Compilers/HybridCPU_Compiler/Core/IR/Compilation/HybridCpuProgramBundler.cs), [post-bundle](../../../Compilers/HybridCPU_Compiler/Core/IR/Compilation/HybridCpuPostBundleCompiler.cs), [managed object linker](../../../Compilers/HybridCPU_Compiler/Cil/ScalarControlFlowV2ObjectLinkerV1.cs).

## 7. Компоненты и integration boundaries

| Компонент | Ответственность |
|---|---|
| Core | Compiler IR, shared stages, policies, artifacts, canonical facade |
| Platform.Contracts | Общие immutable architectural facts |
| CIL | Restricted importer, managed proofs, maps и method/object path |
| Roslyn / Analyzers / Generators | Source companion, NoHeap, declaration-only incremental tooling |
| Native / LLVM | Frontend-specific inputs/mapping; LLVM coverage отдельно ограничена |
| NativeAot.Adapter | Managed ABI/EH/GC/metadata и object/link/image integration |
| Upper compiler / runtime adapter | Concrete ISE emission/transport и runtime-facing integration |
| Validation tools | Source, physical oracle, scheduler и release evidence consumers |

Core зависит от общих contracts и не должен получать обратную зависимость от concrete ISE или CIL implementation. Selective compile groups upper проекта намеренно отделяют compiling owners. Root solution не является исчерпывающим списком tooling graphs; actual evaluated project properties нужны для каждой проверяемой сборки.

Concrete runtime adapter проверяет transport requirements и выполняет runtime-facing emission, memory/annotation/fetch operations своего scope. Compiler-only compilation не выполняет эти действия. NativeAOT target artifacts, compiler host AOT и runtime publication — разные направления.

## 8. Policies, профиль и воспроизводимость

Scheduling/bundling/composite policies и output-affecting options имеют immutable/versioned identities. Profile-guided compile — explicit opt-in, default-off lane. Metrics/resource/topology shadow paths наблюдают работу; наблюдение не должно становиться скрытым selector.

Детерминированный profile result означает повторяемость при определённых inputs/options, но не доказанное ускорение. Performance promotion требует benchmark с объявленной baseline и измерением времени/ресурсов/качества результата. Persistent cache, daemon и concurrent session не возникают автоматически из наличия digests; им нужны lifetime, invalidation, cancellation и reuse contracts.

Default companion Roslyn и SDK-host lane используют отдельные products/references. Root SDK pin описывает compiler tooling, не guest BCL support. Смена package/language/SDK — отдельное compatibility change. Точные pins хранятся в [global.json](../../../global.json) и [toolchain policy](../HybridCPU_Compiler_vNext_Plan_v3/98_ROSLYN_DOTNET11_CSHARP15_POLICY.md), а не дублируются в этом whitebook.

## 9. Как доказывается работоспособность

Проверка consumer должна пройти весь заявленный путь. Source positive/negative и NoHeap дополняются emitted IL/metadata oracle, importer/backend result и exact artifacts. Deterministic PE/PDB/maps/image repeat проверяется отдельно от semantic equivalence разных source forms.

Независимые scalar/CoreCLR и encoded/serialized physical oracles проверяют результаты и ABI. Нужны extrema/high-bit/overflow, presence/null, eager evaluation/call counts и applicable role/identity/effect/EH/byref/budget/fault/cancel/reuse/precedence negatives. Успешная строка в логе полезна только в рамках реально выполненных assertions.

Основные existing harnesses — CompilerProfileSmoke и ManagedPortSmoke. Shared managed changes требуют applicable default/SDK source lanes и full Managed regression. Scheduler или runtime contracts требуют дополнительных соответствующих нагрузок. Новая campaign должна иметь fresh isolated outputs, actual commands/exits и input/evaluated hashes. Failed attempts сохраняются как failures.

Последний сохранённый acceptance v137 от 24 октября: default profile426/SDK427/Managed433 PASS-строки, все exit0; focused48 source и48 новых physical cases. 9 ноября manifests сопоставлены с source без drift. Это не новый запуск и не qualification всех входов или окружений. Подробности, включая неудачную слишком сильную I8 byte-equality проверку, находятся в [immutable report](../../../TempEnv/doom-continuation-20260905/scalar-default-20261024/REPORT.md).

## 10. Степень готовности и развитие

Связный compiler foundation и ограниченные source→image consumers реализованы. Следующая работа расширяет конкретную семантику поверх existing owners; общий rewrite IR/importer/backend не следует из оставшихся gaps.

Ближайший consumer — zero-input constrained scalar helper. Единственная изменяемая очередь находится в [95](../HybridCPU_Compiler_vNext_Plan_v3/95_IMPLEMENTATION_ORDER_AND_COMMITS.md); whitebook не создаёт конкурирующий backlog. Более широкие generic/ABI/language/target и performance claims требуют своих проверок.

Общая qualification остаётся неподтверждённой. Release component PASS не доказывает full solution/Debug, ordinary-host stability, loader/guest/replay, independent clean builds или publication. Структурный release validator не исполняет заявленную программу. Compiler artifact, runtime admission, execution, retire и OS publication должны иметь отдельные доказательства.

Для работы с кодом: [source index](../HybridCPU_Compiler_vNext_Plan_v3/94_SOURCE_INDEX.md). Для фактического прогресса и применимости evidence: [отчёт](../HybridCPU_Compiler_vNext_Plan_v3/CURRENT_STATE_AND_PROGRESS.md) и [00](../HybridCPU_Compiler_vNext_Plan_v3/00_CODE_VERIFICATION_REPORT.md).
'@
[IO.File]::WriteAllText("$white/README.md",$whitebook.Trim()+"`n",[Text.UTF8Encoding]::new($false))
$contents=@'
# Навигация по плану

Актуализация 9 ноября 2026. Общая qualification не подтверждена.

## Начать здесь

- [README](README.md)
- [Возможности и прогресс](CURRENT_STATE_AND_PROGRESS.md)
- [00 — Применимость evidence](00_CODE_VERIFICATION_REPORT.md)
- [95 — Единственная очередь](95_IMPLEMENTATION_ORDER_AND_COMMITS.md)
- [Промпт продолжения](EXECUTION_PROMPT.md)
- [Whitebook компилятора](../Whitebook/README.md)

## Архитектура и contracts

- [02 — Invariants](02_ARCHITECTURE_INVARIANTS.md)
- [03 — Текущая архитектура](03_AS_IS_COMPILER_ARCHITECTURE.md)
- [05 — Граница SingNextOS](05_SINGNEXTOS_CODESIGN_CONTRACT.md)
- [06 — IR](06_IR_LAYERING_DECISION.md)
- [07 — Project graph](07_PROJECT_GRAPH_DECISION.md)
- [91 — Target architecture](91_TARGET_ARCHITECTURE.md)
- [92 — Contracts](92_API_AND_TYPE_CHANGES.md)
- [93 — Acceptance](93_ACCEPTANCE_MATRIX.md)
- [94 — Source index](94_SOURCE_INDEX.md)
- [96 — Validation](96_TEST_STRATEGY.md)
- [97 — Performance/research](97_PERFORMANCE_AND_RESEARCH_EVALUATION.md)
- [98 — Toolchain](98_ROSLYN_DOTNET11_CSHARP15_POLICY.md)
- [99 — Открытые границы](99_BLOCKERS_AND_CLAIM_BOUNDARIES.md)

## Области ответственности

P0–P9 — owners/dependencies, не последовательная очередь:

- [P0 — Toolchain/repro](P0_TOOLCHAIN_REPRO_AND_QUALIFICATION.md)
- [P1 — Ownership/runtime edge](P1_SOURCE_OWNERSHIP_AND_RUNTIME_EDGE.md)
- [P2 — Shared pipeline](P2_CANONICAL_PIPELINE_CONVERGENCE.md)
- [P3 — CIL/provenance](P3_CIL_HARDENING_PROVENANCE_SOURCEMAP.md)
- [P4 — Roslyn companion](P4_ROSLYN_MANAGED_SOURCE_COMPANION.md)
- [P5 — Policies/cache/session](P5_POLICY_CACHE_INCREMENTAL_CONCURRENCY.md)
- [P6 — Profile](P6_REPRODUCIBLE_PGO.md)
- [P7 — Evidence/type safety](P7_EVIDENCE_AND_TYPE_SAFETY.md)
- [P8 — Frontend/target qualification](P8_FRONTEND_TARGET_QUALIFICATION.md)
- [P9 — Release/research](P9_RELEASE_CUTOVER_AND_RESEARCH_EVALUATION.md)

[Manifest](plan_manifest.json), [checksums](CHECKSUMS.sha256). [Архив миграционных заметок](Archive/migration-notes-20261109/README.md) не входит в маршрут продолжения.
'@
[IO.File]::WriteAllText("$plan/CONTENTS.md",$contents.Trim()+"`n",[Text.UTF8Encoding]::new($false))
# Remove historical cross-links from the remaining active owner notes.
foreach($file in Get-ChildItem $plan -File -Filter '*.md'){
 $s=[IO.File]::ReadAllText($file.FullName)
 foreach($name in $legacy){$s=$s.Replace("]($name)","](Archive/migration-notes-20261109/$name)")}
 [IO.File]::WriteAllText($file.FullName,$s,[Text.UTF8Encoding]::new($false))
}
$m=[ordered]@{
 schema='hybridcpu.compiler-plan-manifest/v1';plan='HybridCPU_Compiler_vNext_Plan_v3';verification_date='2026-11-09';revision='documentation-reconciled-20261109';qualification='no'
 current_status_file='00_CODE_VERIFICATION_REPORT.md';capability_report='CURRENT_STATE_AND_PROGRESS.md';whitebook='../Whitebook/README.md';acceptance_file='93_ACCEPTANCE_MATRIX.md';source_index='94_SOURCE_INDEX.md';validation_file='96_TEST_STRATEGY.md'
 continuation=@{queue_owner='95_IMPLEMENTATION_ORDER_AND_COMMITS.md';execution_prompt='EXECUTION_PROMPT.md';priority='real CSharp source-to-image consumers in existing owners';phase_files_are='responsibility areas, not another implementation queue'}
 baseline=@{observed_head='1bfe41a49cf11c80fa6c6aee87accb54b78b331f';dirty_worktree=$true;observation_date='2026-11-09';recheck_before_implementation=$true}
 evidence=@{report='../../../TempEnv/doom-continuation-20260905/scalar-default-20261024/REPORT.md';execution_date='2026-10-24';source_recheck_date='2026-11-09';compiler_manifest_count=541;evaluated_manifest_count=493;manifest_drift=0;new_test_campaign=$false;default_profile=@{exit=0;pass_lines=426};sdk_profile=@{exit=0;pass_lines=427};managed=@{exit=0;pass_lines=433};focused_source=@{exit=0;pass_lines=48};limits='exact manifests and report scope; not current host/full solution/guest/publication qualification'}
 next_bounded_gate=@{queue_owner='95_IMPLEMENTATION_ORDER_AND_COMMITS.md';owner='P3/P4';consumer='zero-input constrained signed scalar helper';status='open';source_fact='exact matcher rejects parameter count zero';saved_evidence='v137 HCCIL1702 exit4, inline/concrete exit0; not reexecuted 2026-11-09'}
 toolchain=@{source='../../../global.json';companion_project='../../../Compilers/HybridCPU_Compiler/Roslyn/HybridCPU.Compiler.Roslyn.csproj';policy='98_ROSLYN_DOTNET11_CSHARP15_POLICY.md'}
 phases=@(Get-ChildItem $plan -File -Filter 'P[0-9]_*.md'|Sort-Object Name|ForEach-Object {@{id=$_.Name.Substring(0,2);file=$_.Name;role='owner/dependency reference; scope from 93, queue from 95'}})
 required_invariants=@('compiler facts do not grant runtime authority','metadata declarations do not grant body authority','discovery does not grant admission','component acceptance is not roadmap completion')
 archive='Archive/migration-notes-20261109/README.md'
}
[IO.File]::WriteAllText("$plan/plan_manifest.json",($m|ConvertTo-Json -Depth 15)+"`n",[Text.UTF8Encoding]::new($false))
Write-Output 'Documentation written; validation and checksums pending.'
