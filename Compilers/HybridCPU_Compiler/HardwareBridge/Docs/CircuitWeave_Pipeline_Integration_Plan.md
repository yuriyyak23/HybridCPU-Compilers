# HardwareBridge → CircuitWeave: план рефакторинга интеграции полного compiler pipeline

**Status:** PROPOSED / docs-only, не HWIR/RTL implementation.  
**Baseline:** `HybridCPU-Compilers/master@c66d2ec2bb8a06e29ced1d884c98a055054b4a26` и `CircuitWeave/master@58a7c44c3353a9c95108192224b5ea190f96464b` на 2026-11-19.  
**Core plan:** [16-package integration roadmap](https://github.com/yuriyyak23/CircuitWeave/blob/master/docs/roadmap/circuitweave/11_Compiler_Pipeline_Automation_Master_Plan.md) и [execution package specs](https://github.com/yuriyyak23/CircuitWeave/blob/master/docs/roadmap/circuitweave/12_CW_INT_Execution_Packages.md) (ссылки на master заработают после merge соответствующего PR).

## 1. Цель и неприкосновенная граница

HardwareBridge — opt-in read-only software adapter, который отдаёт **неподтверждённые аппаратно** snapshot source bytes, Roslyn/CIL/PE/PDB/CompilerFacts и строго allowlisted Golden observations. Он не должен становиться новым C#→RTL компилятором, HWIR authority, отдельным Bindings resolver, EDA runner, source oracle verifier либо Studio publisher. Эти полномочия принадлежат CircuitWeave/его independent verification owners.

**Golden C# и основной Software compiler не изменяются для синтеза.** Ни атрибутов `[Hardware]`, ни `Tick()`, ни переписывания функций, ни generated outputs в Golden worktree. HW intent, clocks/memory/temporal contracts/target находятся только в CircuitWeave-owned external manifests. Если Golden не определяет timing/reset/rank4 — отказ, а не заполнение догадкой.

Не использовать `PayloadSha256` sidecar как подпись/доказательство. Разделять `BridgeSourceSnapshotV1.SnapshotDigest`, `Companion SourceInputDigest`, `PeSha256`, `PdbSha256`, Roslyn source identity и Git HEAD. Worktree с изменёнными файлами идентифицируется capture hashes, а не HEAD.

## 2. Обнаруженные разрывы (репозиторий HardwareBridge)

1. `HWIR/HardwareBridge.Hwir.csproj` содержит `ProjectReference` к sibling `CircuitWeave-main\src\CircuitWeave.Frontend.Roslyn` — хрупкая двусторонняя зависимость, поскольку Core CLI тоже требует sibling `HybridCPU Compilers\...\HardwareBridge.Hwir.csproj`. Продакшен CLI не может требовать конкретное имя локального checkout.
2. `HardwareBridge.Companion.csproj` — отдельный opt-in .NET 11 проект, **не** часть default software compiler. Это граница, которую надо сохранить.
3. `BridgeSidecarCodecV1` сериализует ограниченный v1 payload (`MaxSidecarBytes = 16 MiB`); `BridgeSourceSnapshotV1` имеет бюджеты до 128 documents и refs, 64 MiB total. Полный многопроектный Processor source capture требует segmented versioned index, не один giant JSON.
4. `CompilerFacts` означают successful software closure/CIL import, но не hardware admission. `Observed` означает результат delegate, не independent proof.
5. `TrustedAddWordOracleV1` использует explicitly registered in-process `Func<uint,uint,uint>`; нельзя автоматически расширять его до unsafe general Golden runner. Stateful Golden execution требует отдельной изолированной, replayable среды в квалифицирующей стороне.
6. `HWIR/BridgeHwir*.cs` содержит десятки специализированных source-bound projections с высокоценными fixtures, но это не general Core-owned C# lowering. Нужно сохранять проверенные regression cases и постепенно мигрировать семантические правила в Core.

## 3. Пакеты HardwareBridge с crosswalk в CW-INT

### HB-INT-01 — Portable build / circular reference removal (CW-INT-01, 02)

**Owner:** HardwareBridge project configuration. **Change points:** `HWIR/HardwareBridge.Hwir.csproj`, `HardwareBridge.Companion.csproj`, `HWIR/README.md`, `Tests/README.md`; Core fixes `CircuitWeave.Cli.csproj` independently.

**Implementation:**
1. Отделить `SourceSnapshot` + `CompilerFacts` + `GoldenOracle` от Core HWIR-derived adapters на уровне dependencies и package boundaries.
2. Держать `HardwareBridge.Companion.csproj` opt-in и read-only. Для `HardwareBridge.Hwir.csproj` удалить обязательную fixed sibling `CircuitWeave-main` reference из production path: источник Core assembly поставляется через явно pinned optional test/build adapter или Core-validated artifact interchange, а не Hardcoded path.
3. Установить проверяемые версии DTO и compat fixtures, подлинные root paths передаются явно. Убрать абсолютные user-specific example paths из обязательных команд; примеры могут показывать placeholders.
4. Не добавить `ProjectReference` из главного software compiler к CircuitWeave; assembly ownership и Golden input не изменяются.

**Acceptance:** оба репозитория независимо restore/build на чистых checkout в разных каталожных именах; core CLI без bridge repo способен возвращать typed `MissingBridge`, а при явном bridge input связывает точные bytes. Negative: moved checkout, wrong package ref, wrong commit/SDK, implicit source writes, circular project dependency.

### HB-INT-02 — Exact capture / Csc closure consistency (CW-INT-03)

**Owner:** `SourceSnapshot/BridgeSourceSnapshotV1.cs` и opt-in Csc capture tools под `HWIR/CscSemanticInventory`, `HWIR/EvaluatedProjectGraph`.

**Implementation:**
1. Проверять captured actual Csc inputs, including source generated bytes, analyzer inputs, references, `LangVersion`, defines, nullable, overflow, SDK, MSBuild global properties и fixed compiler invocation identity; не приравнивать project evaluation к target-time execution.
2. Создать v2 outer index для segmented projects/source chunks (не менять payload `sidecar/v1` in place). Сохранить per-chunk unique ordered paths/bytes SHA-256, size ceilings, root closure/provenance, generation category и source ownership. New readers fail closed on unknown versions.
3. Вести отдельные primary source, owned body dependencies и metadata-only identities. Не выводить executable ownership из C# type names, PDB snippets или index самоутверждений.
4. Перед и после экспорта проверять Golden source hash. Outputs только new owned workspace/TEMP с no reparse/symlink/traversal, no overwrite, no model-root build side effects.

**Acceptance:** восстановить один exact clean Golden Csc closure и один current modified-worktree capture с уникальными digest; source bytes rehash совпадают, alternative configurations различаются; 1k+ file inventory читается bounded memory. Negative: missing generator, altered Csc option, stale reference, duplicate logical path, changed source mid-capture, chunk reordering, missing chunk.

### HB-INT-03 — Strict source/facts interchange and versioning (CW-INT-03, 04)

**Owner:** `SourceSnapshot/BridgeSidecarCodecV1.cs`, `CompilerFacts/BridgeCompilerFactsV1.cs`, `CompilerFacts/CompanionBridgeProjectionV1.cs`, `Docs/sidecar-v1.md`.

**Implementation:**
1. v1 backward compatibility: `hybridcpu.hardwarebridge.sidecar/v1` сохраняет `Source`, nullable `Facts` и nullable `GoldenOracle` и нынешние deterministic serialization rules. Не пересчитывать digest по нормализованному дереву без канонического соответствия.
2. Новый envelope должен ссылаться на exact root `SourceSnapshotDigest`, отдельный `CompanionSourceInputDigest`, PE/PDB identities, original/working source provenance и сегменты индекса. Все ссылки строго check-summed и read-only. Core независимо перепроверяет raw bytes и source symbol/body closure.
3. Никаких `Verified` полей и имитированной подписи в bridge DTO. Состояния `Candidate`, `Rejected`, `Observed`, `Blocked` остаются software facts. Несоответствие schema/role/sha → отказ.
4. Существующие P14 exact scalar fixtures и MSBuild/Companion restrictions оставить, пока Core importer/validator не имеет независимых regression tests.

**Acceptance:** v1 test fixtures читаются старым reader; v2 strict reader отвергает unknown fields/duplicate JSON properties, wrong root/chunk sha, forged/unsupported reference roles. Core ingest rejects stale PE/PDB/SDK/root independently. Unit tests не называют этот исход success 'hardware qualified'.

### HB-INT-04 — Golden oracle isolation and trace protocol (CW-INT-10, 11)

**Owner:** `GoldenOracle/TrustedAddWordOracleV1.cs` и `Tests/BridgeSelfTests.cs`; runtime verification policy и независимый attestation относятся к Core.

**Implementation:**
1. Сохранить компактный v1 AddWord delegate как explicitly trusted in-process scalar-only adapter. Нельзя выбирать delegate по строке из sidecar или вызывать arbitrary C# в Core service.
2. Ввести *предложение* отдельного source-hashed vector/step trace interchange для stateful Golden operations: initial state, explicit inputs, logical clock/step, context/lane, outputs/retire/memory effects, controlled nondeterminism, relation к source/assembly/method и exact scope.
3. Реальный Golden runtime/host sandbox + independently trusted provenance принадлежат `CircuitWeave.Verification` или утверждённому isolated host. Bridge только сериализует **действительно наблюдённые** результаты.
4. Record any timebase assumptions explicitly. Missing reset, stall, memory port or rank4 semantics → `Blocked`.

**Acceptance:** существующие bounded AddWord oracle tests проходят без изменения Golden; new trace contract rejects wrong source/clock/context/ordering/invalid initial state; true Golden/RTL comparison lives in Core and is independent.

### HB-INT-05 — Migrate source-specific HWIR projection ownership (CW-INT-06, 13)

**Owner:** specialized `HWIR/BridgeHwir*.cs` as test/reference projections; Core owns all new generic semantic rules.

**Implementation:**
1. Проинвентаризировать каждый BridgeHwir projection: target exact source symbols, behavioral scope (combinational, abstract logical event, stateful), test vector count, present native RTL receipt, source byte identity и known limitations.
2. Составить карту reusable scalar/bit/clock/memory lowering rules → `CircuitWeave.Frontend.Roslyn` / `CircuitWeave.HWIR`; не мигрировать сомнительные hardware assumptions.
3. Добавить dual-run compare для существующих bounded examples: legacy Bridge projection HWIR vs Core-owned new lowering, canonical type/bit/overflow/semantics, Golden observations, native RTL oracle где применимо.
4. После parity только отключать mandatory use specific compiler из production pipeline; regression fixtures оставлять без объявлений глобального Verified.
5. Processor Rank4, physical lane6, memory timing, pipeline retire и trap/replay получают отдельный external hardware contract в Core, не скрытую реализацию в Bridge.

**Acceptance:** минимум один обычный supported Golden method компилируется без `BridgeHwirFooCompiler`; old regression passes; mutation sign/width/CSR/memory event ошибку обнаруживает; missing source closure fails closed. Full Processor remains No-Go.

### HB-INT-06 — Cross-repo contract / CI / no-source-write evidence (CW-INT-01, 02, 03, 10)

**Owner:** `Tests/HardwareBridge.SelfTests.csproj`, `Tests/README.md`, opt-in closure harnesses; Core CI orchestrates integration.

**Implementation:**
1. Полный matrix: Bridge source/codec v1, segmented v2, PE/PDB/Csc, clean/dirty Golden capture, wrong SDK, Core importer and independently owned source verifier.
2. Source integrity pre/post, independent workspace, negative path/zip symlink/linked ancestor, duplicate root, reference role and stale-lock cases. Record exact Git commit and effective working bytes.
3. Запуск на Windows .NET11 и при необходимости Linux для source-only stages; если Golden ISE/native tools отсутствуют, `NotRun/Blocked` с диагностикой.
4. При изменении sidecar schema обязательны парные PR в Core reader; release pin matrix и compatibility window.

**Acceptance:** clean host can export read-only software-facts and Core validates exact imports; absence of native EDA is not bridge PASS; user Golden inputs unchanged byte-for-byte.

## 4. Совместимость и порядок слияния

| Phase | Bridge change | Predecessor in CircuitWeave | Merge / release gate |
|---|---|---|---|
| A | HB-INT-01 portable opt-in config | CW-INT-01 CI | standalone projects build, no mandatory circular ref |
| B | HB-INT-02 closure index | CW-INT-02 | immutable source/PE/PDB and Csc option identity |
| C | HB-INT-03 strict interchange | CW-INT-03/04 reader | both v1 and v2 contract fixtures + unknown rejects |
| D | HB-INT-04 Golden trace boundary | CW-INT-10 independent oracle | no in-process untrusted general execution |
| E | HB-INT-05 legacy projection dual-run | CW-INT-06/13 general frontend | generalized source lowering with intact regression coverage |
| F | HB-INT-06 cross-repo CI | CW-INT-01/02/03 | real clean-host execution + all missing evidence typed |

Bridge and Core PR may be drafted in parallel, but any schema migration must merge compatible reader before switching default producer. No fork of Native ToolBridge, hardware verifier or Studio publisher.

## 5. Interchange candidate (discussion schema, NOT implemented)

A potential `hybridcpu.hardwarebridge.capture-index/v2` **outer** manifest for segmented raw artifacts (no trust upgrade):

```json
{
  "schema": "hybridcpu.hardwarebridge.capture-index/v2",
  "status": "candidate-unqualified",
  "sourceSnapshotDigest": "<exact bridge digest>",
  "companionSourceInputDigest": "<distinct exact digest or null>",
  "sourceKind": "capturedWorkingTree",
  "rootCommit": "<base commit only>",
  "compilerInputsDigest": "<actual Csc closure digest>",
  "chunks": [{"role": "source", "relativePath": "chunks/0001.json", "sha256": "<sha256>", "bytes": 123}],
  "roots": [{"exactMethodIdentity": "<symbol+assembly identity>"}],
  "scope": "software-facts-only"
}
```

Это **не окончательное DTO и не production writer**: до реализации определить strict parser, ceilings, ordering, duplicate member policy, digest canonicalization, schema migration и independent Core reader tests. В v1 sidecar для scalar fixtures ничего автоматически не переписывать.

## 6. Checks и сигналы No-Go

- Exact pinned source bytes и `Csc` host options: считать missing/unknown compile inputs блокирующим, а не подменять evaluation-only graph.
- No source writes: искать modified Golden `git status`, сравнивать source SHA before/after, не считать исходный HEAD полным идентификатором dirty worktree.
- `BridgeSourceSnapshotV1` = diagnostic, `CompilerFacts.Status=Candidate` = software import, `GoldenOracle.Status=Observed` = выполнение allowlisted delegate. Ни один из этих фактов не утверждает hardware equivalence.
- `BridgeHwir*.cs` в отдельном проекте остаётся bounded candidate-only; если circuitweave Core downlevel/недоступен, bridge не переходит на самодельный HWIR/RTL runner.
- Текущий [P16 Processor traceability](https://github.com/yuriyyak23/CircuitWeave/blob/master/docs/implementation/P16_Processor_Traceability_20261114.md) и [HybridCPU porting matrix](https://github.com/yuriyyak23/CircuitWeave/blob/master/docs/roadmap/circuitweave/07_HybridCPU_Porting_Matrix.md) сохраняют `INCOMPLETE` до независимого CPU top proof.
- CI/PR: `NotRun`, skipped, fake tool, incomplete attestation, unapproved native dependency нельзя считать PASS.

**Final acceptance boundary:** HardwareBridge завершает read-only software-facts export и успешно проходит cross-repo ingestion contract; от C#→HWIR→RTL→Physical отвечает CircuitWeave по основному CW-INT roadmap. Это не обещание полной аппаратной компиляции произвольного C#.
