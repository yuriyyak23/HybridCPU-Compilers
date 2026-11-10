$ErrorActionPreference='Stop'
$plan='C:/Users/Yuriy/Desktop/HybridCPU ISE/Documentation/Compilers/HybridCPU_Compiler_vNext_Plan_v3'
$rows=@(
 @('P0_TOOLCHAIN_REPRO_AND_QUALIFICATION.md','P0 — Toolchain и воспроизводимость',@'
Owner различает compiler SDK, embedded Roslyn, publish SDK и upstream ILCompiler identities. Текущие pins — [98](98_ROSLYN_DOTNET11_CSHARP15_POLICY.md). Изменение одного не означает upgrade остальных или guest support.

Для заявленного reproducibility relation фиксируются output-affecting inputs/options/references/policies/profile, actual host/TargetPath и изолированные outputs. Code bytes, diagnostics и PE/PDB/maps provenance — разные отношения. Cross-root/thread/cold-warm modes проверяются, когда они реализованы и заявлены, а не как универсальная матрица.
'@),
 @('P1_SOURCE_OWNERSHIP_AND_RUNTIME_EDGE.md','P1 — Ownership и runtime edge',@'
Canonical facade и compiler stages физически находятся в Core. Upper compiler содержит concrete ISE emission/production/decomposition и runtime-facing tooling. Core зависит от Platform.Contracts; обратная зависимость от concrete ISE не должна появляться. [Project graph](07_PROJECT_GRAPH_DECISION.md).

Новый перенос оправдан конкретной зависимостью или дублированием ответственности. Один source имеет один evaluated compiling owner. Selective compile groups допустимы. При переносе сохраняются namespace/API/defaults и bounded bytes/annotations/relocations/diagnostics. Runtime emission не переносится в compiler-only core.
'@),
 @('P2_CANONICAL_PIPELINE_CONVERGENCE.md','P2 — Общие стадии',@'
Существуют Core IR compiler, ProgramBundler и PostBundleCompiler. CIL/NativeAOT используют shared stages, сохраняя свои ABI/EH/GC/metadata/object/link/image owners. Второй pipeline не нужен.

Порядок сохраняет static dependencies, schedule, bundles, optional bounded RA, final witness/agreement, physical lowering/relocation и serialization. Defaults/raw/value-only paths не меняются попутно. Typed slots и MatrixTile lane6/HardPinned остаются target contracts.

Изменение shared contract проверяется physical/serialized oracle, final agreement/relocations и affected full regressions. Scheduler changes дополнительно требуют соответствующих threshold/corpus проверок из96.
'@),
 @('P3_CIL_HARDENING_PROVENANCE_SOURCEMAP.md','P3 — CIL и semantic proofs',@'
RestrictedCilImporterV1 — semantic owner managed ingress. BodyWorld supplies explicit selected bodies и exact definitions; malformed metadata/header/token/generic/EH и budget rejection остаются fail-closed. Наличие controls не является exhaustive fuzz/security qualification.

Расширение идёт от actual emitted CIL/metadata к конкретному decoder/proof/lowering gap и source→image. Exact role/nominal identity/context/purity/CFG/SSA/EH/fault/original budgets обязательны. Unsupported и BudgetExhausted не объединяются. Discovery не даёт admission.

Readers имеют call-owned lifetime; dependency не получает primary PDB. Persistent cache/concurrency не следует из sequential reuse. Реализованные scalar/nullable/loop scopes — [93](93_ACCEPTANCE_MATRIX.md), код — [94](94_SOURCE_INDEX.md).
'@),
 @('P4_ROSLYN_MANAGED_SOURCE_COMPANION.md','P4 — C# source companion',@'
Existing companion выполняет owned source/options/references → diagnostics/NoHeap → deterministic PE/PDB → CIL/BodyWorld → object/link/image. CLI compile-source-image существует в том же owner. [Roslyn usage](../../../Compilers/HybridCPU_Compiler/Roslyn/README.md).

Metadata-reference role даёт declarations, selected body-reference — bodies; discovery filesystem/framework не добавляется. Source admission не является interprocedural certification произвольной reference library. Analyzer failures/suppression не должны обходить rejection.

Новый construct проверяется downstream emitted IL/metadata и actual image, с applicable default/explicit SDK lane и matching references. Language/package/host changes — compatibility changes; они не открывают всю BCL/async/unsafe/interop. [Toolchain](98_ROSLYN_DOTNET11_CSHARP15_POLICY.md).
'@),
 @('P5_POLICY_CACHE_INCREMENTAL_CONCURRENCY.md','P5 — Policies, identities и lifetime',@'
Implemented owners имеют immutable scheduling/bundling/composite policies, effective-options identities и role-bound managed provenance. Source/artifact/map/import/graph scopes не взаимозаменяемы. Metrics/reason text не selectors.

Cancellation checkpoints существуют в ingress/shared orchestration/backend/link/EH/GC boundaries; universal inner-operation latency не обещана. Call-owned readers/digests не должны переживать вызов; synchronous multi-file writes не atomic transaction.

Persistent reuse или parallel session вводится только по consumer и измерениям: input/invalidation contract, cold/warm cost, retained memory, stale/corrupt/version controls, deterministic merge и cancel/reuse. Daemon и новая cache hierarchy не обязательны.
'@),
 @('P6_REPRODUCIBLE_PGO.md','P6 — Explicit profile lane',@'
Core содержит explicit opt-in profile compilation и bounded selection trace. Default-off и статическая допустимость кандидатов сохраняются. Observational metrics/resource/topology paths не становятся скрытым selector.

Profile identity и deterministic output доказывают воспроизводимость в заявленном scope, а не profitability. Promotion требует off-baseline, stale/corrupt/version controls, separation training/evaluation и измерения compile overhead плюс output/runtime benefit. Объём measurement определяется claim — [97](97_PERFORMANCE_AND_RESEARCH_EVALUATION.md).
'@),
 @('P7_EVIDENCE_AND_TYPE_SAFETY.md','P7 — Evidence и invalid states',@'
Existing summaries, digests, sidecars и ReleaseQualification validators передают ограниченные compiler facts. Они не заменяются новой public proof-object hierarchy и не предоставляют runtime authority.

Type hardening начинается с воспроизведённого invalid state: противоречие nominal/payload/width, missing identity/row или inconsistent disposition. Сохраняются public schema/defaults и diagnostic precedence, если consumer не требует versioned compatibility change.

Validation проходит actual public/CLI path с negative/control и appropriate serialization/digest relation. Synthetic parser fixture доказывает parser routing, не execution/publication.
'@),
 @('P8_FRONTEND_TARGET_QUALIFICATION.md','P8 — Frontend и target scopes',@'
Native, CIL, NativeAOT, Roslyn и LLVM имеют разные input/ABI/metadata/host contracts. Shared backend не означает одинаковую coverage. Analyzer/generator — tooling; Oracle — validation consumer, не production selector.

Реализованные contours перечислены в [93](93_ACCEPTANCE_MATRIX.md). Для расширения фиксируются exact inputs/options/target, positive/unsupported/malformed controls, deterministic relation и соответствующий artifact. Cross-frontend comparison объявляет semantic relation; byte equality не универсальна.

LLVM broader CFG/ABI/runtime reachability и shipping host integration требуют своих acceptance. Loader/guest/publication evidence необходимо только при заявлении этих уровней.
'@),
 @('P9_RELEASE_CUTOVER_AND_RESEARCH_EVALUATION.md','P9 — Release и performance claims',@'
Existing ReleaseQualification проверяет manifest/matrix/identity/artifact consistency. Clean-build flags — producer declarations; actual commands/isolated roots/exits/input identities нужны отдельно. [Ownership contract](../../../Compilers/HybridCPU_Compiler/ReleaseQualification/README.md).

Promotion выбирает конкретный contour и claim: reproducible publish требует independent builds, execution — runtime reachability/completion, publication — OS/provider evidence. Равные hashes, frozen stability и component smoke этого не заменяют. Не требуется универсальная многодневная нагрузка для compiler-only patch.

Research performance package — отдельный scope с corpus/train/evaluation identities, raw results, machine configuration и variance. Внутренний witness или шумный microbenchmark не даёт численного release performance claim.
'@)
)
foreach($row in $rows){
 $text='# '+$row[1]+"`n`n"+$row[2].Trim()+"`n`nЭто область ответственности, не отдельная очередь. Следующий consumer — [95](95_IMPLEMENTATION_ORDER_AND_COMMITS.md); validation — [96](96_TEST_STRATEGY.md); применимость evidence — [00](00_CODE_VERIFICATION_REPORT.md). Общая qualification не подтверждена.`n"
 [IO.File]::WriteAllText("$plan/$($row[0])",$text,[Text.UTF8Encoding]::new($false))
}
$path="$plan/98_ROSLYN_DOTNET11_CSHARP15_POLICY.md"
$text=[IO.File]::ReadAllText($path).Replace('официальные источники..','официальные источники.')
[IO.File]::WriteAllText($path,$text,[Text.UTF8Encoding]::new($false))
