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
