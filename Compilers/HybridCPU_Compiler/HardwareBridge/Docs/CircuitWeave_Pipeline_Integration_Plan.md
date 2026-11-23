# HardwareBridge → CircuitWeave: план рефакторинга интеграции полного compiler pipeline

**Status (2026-11-21):** R0–R3 merged, production handoff blocked. Текущие доказательства и открытые задачи см. в [аудите и TODO](HardwareBridge_Modernization_Audit_20261121.md).

**Baseline:** `HybridCPU-Compilers/master@c66d2ec2bb8a06e29ced1d884c98a055054b4a26` и `CircuitWeave/master@58a7c44c3353a9c95108192224b5ea190f96464b` на 2026-11-19.  
**Core plan:** [16-package integration roadmap](https://github.com/yuriyyak23/CircuitWeave/blob/master/docs/roadmap/circuitweave/11_Compiler_Pipeline_Automation_Master_Plan.md) и [execution package specs](https://github.com/yuriyyak23/CircuitWeave/blob/master/docs/roadmap/circuitweave/12_CW_INT_Execution_Packages.md).

## 1. Цель и неприкосновенная граница

HardwareBridge — opt-in read-only software adapter, который отдаёт **неподтверждённые аппаратно** snapshot source bytes, Roslyn/CIL/PE/PDB/CompilerFacts и строго allowlisted Golden observations. Он не должен становиться новым C#→RTL компилятором, HWIR authority, отдельным Bindings resolver, EDA runner, source oracle verifier либо Studio publisher. Эти полномочия принадлежат CircuitWeave/его independent verification owners.

**Golden C# и основной Software compiler не изменяются для синтеза.** Ни атрибутов `[Hardware]`, ни `Tick()`, ни переписывания функций, ни generated outputs в Golden worktree. HW intent, clocks/memory/temporal contracts/target находятся только в CircuitWeave-owned external manifests. Если Golden не определяет timing/reset/rank4 — отказ, а не заполнение догадкой.

Не использовать `PayloadSha256` sidecar как подпись/доказательство. Разделять `BridgeSourceSnapshotV1.SnapshotDigest`, `Companion SourceInputDigest`, `PeSha256`, `PdbSha256`, Roslyn source identity и Git HEAD. Worktree с изменёнными файлами идентифицируется capture hashes, а не HEAD.

## 2. Обнаруженные разрывы (репозиторий HardwareBridge)

1. На Bridge `e0fdfde` default HWIR включал legacy fixtures и три фиксированных sibling references. В текущих рабочих байтах default собирает portable DTO. Legacy требует явного opt-in, пути Core, точного Core SHA и SDK pin. Core staged v2 reader работает самостоятельно. Публикация нового парного pin и hosted проверка остаются открыты.
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

### Локальный observed-Csc body срез, 2026-11-21

Bridge экспортирует отдельные body facts из фактических закреплённых PE/PDB через `BridgeObservedCscBodyImporterV1`. Core `bridge audit-body-facts` независимо сверяет заданную policy, точный MethodDef, прямой IL-вызов и PDB checksum с raw source bytes. AddWord и SignExtendWord проверены на Golden `6946ccab9d888f7dae0a4d8e18480f436138acd9`. Результат candidate-only. Он расширяет software-facts path и оставляет полную source admission, Companion compilation и trusted handoff открытыми. [Матрица и входы](../../../../../CircuitWeave-main/artifacts/processor-20261114/integration-20261121/README.md).
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

## 5. Исторический эскиз interchange от 2026-11-19

Актуальные схемы и границы реализации описаны в [segmented Csc closure](HB_INT_02_Segmented_Csc_Closure.md), [V2 interchange](HB_INT_03_V2_Interchange.md) и [Core-v2 staging](HB_INT_02_03_Core_V2_Staging.md). Пример ниже сохраняется как исходное предложение.

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

Локальная регрессия рабочих байтов v5 segmented и paired capture от 2026-11-22: 142 tests Linux, 142 tests Windows (6 skips); hashes и логи в `CircuitWeave-main/artifacts/processor-20261114/capture-verifier-20261121-v5/`. Повторные проверки graph/observation/closures обнаруживают drift. Последующая версия сохраняет частичные TEMP outputs вместо рискованного удаления чужого пути. Это synthetic fixture evidence; hosted no-write и trusted receipt остаются открыты.

После исправления cleanup paired capture сохраняет частичные TEMP outputs при отказе и публикует receipt exclusive rename. Локальные текущие fixtures: Linux 70/70; Windows 70 с 3 skips (`CircuitWeave-main/artifacts/processor-20261114/capture-cleanup-audit-20261122/`). Host trust и атомарность входов остаются открыты.

Projector и Core staging больше не удаляют подменённые TEMP-каталоги при отказе; частичные данные сохраняются для инспекции. Локальные synthetic suites: Linux 77, Windows 77 с 3 skips; входные хэши и логи в `CircuitWeave-main/artifacts/processor-20261114/projection-cleanup-20261122/`. Атомарность внешних входов не установлена.

Core добавил отдельный byte-preflight 1 352 staging-копий captured Golden task и десять отрицательных проверок (`CircuitWeave-main/artifacts/processor-20261114/full-csc-inventory-20261122/`). Manifest сформирован из capture без независимого approval; response, полнота, execution, ownership и независимый re-emit не установлены. Это не trusted handoff.

Standalone Core harness на SDK Roslyn сопоставил Golden RSP с упорядоченными группами capture; штатный Core Roslyn отклоняет C# 15 (CS1617). Manifest и mapping не имеют независимого approval. Результат и девять отказов: `CircuitWeave-main/artifacts/processor-20261114/rsp-inventory-20261122/`. Это не execution или trusted handoff.

Изолированный Core-owned SDK tool прошёл Golden response и десять отказов; основной Core CLI не подключён. Capture-derived pins не являются независимым approval. Evidence: `CircuitWeave-main/artifacts/processor-20261114/csc-response-tool-20261122/`.

Локальный Core CLI candidate-only response preflight проверен на Golden capture через отдельный SDK tool (`CircuitWeave-main/artifacts/processor-20261114/csc-response-cli-integration-20261122/`). Для теста CLI использовал временный RC2 runtimeconfig; output paths, production runtime и trust остаются открыты.

Core response audit сопоставил parsed PE/PDB paths и generated directory с локальным capture-derived mapping; 50 indexed generated paths находятся внутри. Refout, полнота, фактическая эмиссия и происхождение bytes не доказаны. Evidence: `CircuitWeave-main/artifacts/processor-20261114/csc-response-tool-20261122/receipt.json`.

Core V2 candidate проверил `/refout` path и hash захваченной reference assembly; связь с конкретным исполнением не установлена. Публичный Bridge master на 2026-11-22 наблюдался как 0d18ae1acc34a582cc8c3b93d01394bc68f598b1, unprotected, без runs для этого SHA; локальный baseline остаётся e0fdfdebd537ecfbd67b5fd853940f061a5c41ae. Evidence: `CircuitWeave-main/artifacts/processor-20261114/integration-20261121/remote-bridge-recheck-20261122.json`.

Свежий isolated Csc replay в Core artifacts сопоставил 1 299 captured inputs, reference assembly и 50 generated outputs; PE/PDB SHA отличаются, причина не изолирована. Первоначальные file reads и generator side effects не трассировались; trusted handoff остаётся открытым. Evidence: `CircuitWeave-main/artifacts/processor-20261114/integration-20261121/golden-release/isolated-replay-20261122.json`.

Изолированный helper replay подтвердил refout и 50 generated outputs, а PE/PDB отличаются. Структурная сверка показала совпадение 30 561 IL hashes при различных metadata/MVID/PDB и путях; причинность не доказана. Evidence: `CircuitWeave-main/artifacts/processor-20261114/integration-20261121/golden-release/PE_PDB_STRUCTURAL_DIFF_20261122.md`.

Core generated snapshot checker проверил 50 локальных файлов с capture-derived inventory, без execution/provenance. При фиксированном cwd и изменении трёх output paths два Csc replay дали разные PE/PDB и одинаковые refout/generated; отдельное влияние cwd или каждого path не изолировано. Evidence: `CircuitWeave-main/artifacts/processor-20261114/integration-20261121/golden-release/output-path-control-20261122/receipt.json`.

Core candidate composition связал response и локальный 50-file generated snapshot по ordinal/hash/path; execution и provenance не установлены. Controlled replay отдельно изолировал output switches. Evidence: `CircuitWeave-main/artifacts/processor-20261114/generated-output-preflight-golden-v1/positive.log`, `CircuitWeave-main/artifacts/processor-20261114/integration-20261121/golden-release/output-switch-isolation-20261122/`.

Локальный Csc контроль `/pathmap` изменил PE/PDB, но не refout/generated и не восстановил captured parity. Evidence: `CircuitWeave-main/artifacts/processor-20261114/integration-20261121/golden-release/PATHMAP_ONLY_CONTROL_20261122.md`.

Core добавил локальный machine-bound verifier сохранённого replay; declared bytes и output set перепроверены, исходные file reads, provenance и атомарный snapshot не установлены. Evidence: `CircuitWeave-main/artifacts/processor-20261114/csc-replay-verifier-20261122/`.

Pinned Golden PE winner helper и существующий Core RTL-кандидат совпали на семи constructed mask inputs; мутация обнаружена. Это не cycle или full Processor verification. Evidence: `CircuitWeave-main/artifacts/processor-20261114/pagefault-winner-runtime-20261122/rtl-crosscheck/receipt.json`.

Core candidate join локально связывает claims preflight и replay по 50 generated rows, не перечитывая underlying bytes и не устанавливая execution/provenance. Evidence: `CircuitWeave-main/artifacts/processor-20261114/csc-full-closure-candidate-20261122/`.

Core candidate join сверяет captured response SHA replay с response role inventory, включая отказ перепинованной подмены. Это не execution/trust attestation. Evidence: `CircuitWeave-main/artifacts/processor-20261114/csc-full-closure-candidate-20261122/independent-negative-cases.json`.

Core candidate join повторно хэширует текущие bytes трёх generated snapshots и replay outputs; историческое исполнение и provenance не установлены. Pinned winner PE vs existing RTL проверены на 197 376 constructed vectors, без cycle claims. Evidence: `CircuitWeave-main/artifacts/processor-20261114/csc-full-closure-candidate-20261122/evidence.json`, `CircuitWeave-main/artifacts/processor-20261114/pagefault-winner-expanded-20261122/receipt.json`.

Коррекция Core byte-level join: ранее sealed generated contents не хэшировались; это исправлено и altered copy отвергнута. Execution/provenance не установлены. Evidence: `CircuitWeave-main/artifacts/processor-20261114/csc-full-closure-candidate-20261122/negative-cases.json`.

### 2026-11-22 — exhaustive winner mask projection and closure boundary

A direct oracle from Golden commit `6946ccab9d888f7dae0a4d8e18480f436138acd9` PE (`dc7f02ca3bc714f9e6a83b9de3c03b8dd6da64794eb477b2c5b057d7b7d10dc0`, MethodDef `0x06006c03`, IL SHA-256 `f469deec6497613b9a9f97d34d0850a9195f6031e19af57b53204dede3f85908`) matched the existing candidate RTL (`c802ae6989a0eee6bed23b581e3b8f25413459bbdbc8268f542a19bc8123269f`) on all 16,777,216 WB/MEM/EX mask triples. The test fixes lane occupancy to the respective mask and lane indices to 0–7; its inputs are synthetic. The WB-priority mutant produced a fatal mismatch at `81/00/00`; the shell did not reliably record its exit code. Commands, tool hashes and scope: `artifacts/processor-20261114/pagefault-winner-expanded-20261122/full-domain-rtl-receipt.json` in Core. This expands, rather than replaces, the historical 197,376-vector evidence. It does not qualify other RTL inputs, actual production execution, trusted handoff, Processor timing/state, or release verification.

The current full-closure candidate independently rehashes current sealed/raw/replay bytes, but does not authenticate the receipt issuer, prove historical execution, or admit the capture. Positive and ten negative cases are recorded in `artifacts/processor-20261114/csc-full-closure-candidate-20261122/`. Both results remain candidate-only; integration remains incomplete.

### 2026-11-22 — capture root separation and repeat replay

The candidate full-closure join previously accepted the same directory for capture and replay and counted its bytes twice. It now rejects equal or lexically nested sealed, raw and replay roots, including a drive root; linked path components are rejected separately. The final targeted build and positive/14 negative cases are in Core `artifacts/processor-20261114/csc-full-closure-roots-20261122-final/negative-cases.json`. Root separation does not establish physical independence, execution, provenance, atomicity or admission.

An isolated replay followed by the same Csc invocation at the same cwd, response and output paths produced matching PE, PDB, refout and all 50 generated files; `artifacts/processor-20261114/replay-cause-20261122/repeat-comparison.json` records the comparison. This does not identify the cause of the captured PE/PDB mismatch. The V2 Csc profile is passed through composition in the current sources, but currently rejects analyzers, generators and generated sources and bounds source/reference counts to 128; the captured Golden closure exceeds these limits. Full independent handoff remains open.

### 2026-11-22 — Csc closure sidecar transport audit

The stage producer now emits `csc-closure-profile.json`; the disposable Core reader host independently re-imports the exact staged Core index/closure and calls `CscClosureProfileV1.Audit` against the sidecar SHA pin. The reader compares hexadecimal file digests case-insensitively because the producer emits lowercase SHA while Core V2 rows use uppercase SHA. A local synthetic producer→Core fixture passed (`CandidateOnly`, 5 file rows, 1 generated row); changing the sidecar bytes while retaining its reviewed pin was rejected. Evidence: `CircuitWeave-main/artifacts/processor-20261114/csc-profile-sidecar-fixture-20261122/receipt.json`.

Scope remains transport-only: the fixture is not Golden execution evidence, the test host does not wire the profile into CW-R3 composition, and execution/provenance/admission remain `NotEstablished`/`NotEstablished`/`NotAdmitted`. Hosted execution on clean, exact current pins is still required; no trusted handoff receipt or Processor qualification is claimed.

### CW-R3 consumer follow-up — 2026-11-22

The Core worktree now requires the externally pinned closure sidecar at the CW-R3 composition boundary. The consumer re-audits the sidecar during its live drift checks and reports the candidate-only sidecar receipt alongside the stage result. A synthetic CWR3 fixture accepts the valid crosswalk and refuses a missing sidecar or a rehashed false execution claim. See `CircuitWeave-main/artifacts/processor-20261114/cwr3-closure-sidecar-20261122/receipt.json`. This updates the earlier statement about local composition wiring only; it does not qualify production Golden inputs or the hosted trust gate.

### 2026-11-22 — pinned Golden task-level Csc observation

A fresh clean TEMP checkout of the fixed Golden commit 6946ccab9d888f7dae0a4d8e18480f436138acd9 was built with SDK 11.0.100-rc.2.26504.105. A forced Release rebuild succeeded (0 errors, 45 warnings) and the live MSBuild observer recorded five successful Csc task events, including the main project event with 1,105 .cs arguments, 180 reference switches and 10 analyzer switches. PE/PDB hashes and raw task-event receipt: CircuitWeave-main/artifacts/processor-20261114/golden-pinned-csc-task-capture-20261122/receipt.json. The clone remained clean.

This is an unqualified software observation. It is not an OS argv/cwd witness, does not prove compile-time input bytes or generator completeness, and has not been imported independently by Core. The local dirty Golden worktree was not used. R1-01 remains limited to its Linux toy fixture; trusted handoff, method/Companion binding, hardware scopes and stateful Processor evidence remain open.

### 2026-11-22 — Windows task-command projection

HardwareBridge now has a Windows-only projector for externally pinned live MSBuild Csc event receipts. The actual pinned Golden event set projected to five ordered command token lists. For the main project the projection has 1,104 source arguments, 180 references, 10 analyzers, 2 analyzer configs and 3 embedded inputs. The previous preliminary event receipt's sourceCount field was a heuristic count; use the exact token-kind count in [projection receipt](../../../../../CircuitWeave-main/artifacts/processor-20261114/golden-pinned-csc-task-capture-20261122/projection-receipt.json) as the corrected figure. Stale external pin and response-file argument were refused. The projection records cwd as unknown and remains unqualified; it does not hash/stage inputs or prove OS argv/read bytes. Details are in the TaskTimeObserver/README.md.

### P0 recheck — portable default and SDK guard, 2026-11-22

Current working HardwareBridge.Hwir.csproj builds in portable default mode under SDK 11.0.100-rc.2.26504.105: Release Rebuild exit 0, 0 warnings/errors. Explicit opt-in with empty BridgeExpectedSdkVersion refuses with HBINT0104 WrongSdk, exit 1. [Receipt and raw logs](../../../../../CircuitWeave-main/artifacts/processor-20261114/hb-p0-portable-sdkguard-20261122/receipt.json). This verifies these two local P0 conditions on dirty Bridge bytes at HEAD e0fdfdebd537ecfbd67b5fd853940f061a5c41ae; hosted cross-platform gate and other P0 acceptance remain open.

Positive opt-in follow-up: `EnableBridgeHwirLegacyFixtures=true` Release Rebuild passed against a clean TEMP clone of Core HEAD `714e5c939bb1817481cf4970e3e343aa92bfa23b` on installed SDK `11.0.100` (exit 0, 1,021 warnings, 0 errors); a wrong `9.0.321` pin failed closed as `HBINT0104 WrongSdk`. [Receipt/logs](../../../../../CircuitWeave-main/artifacts/processor-20261114/bridge-optin-current-core-20261122/receipt.json), SHA-256 `79f77bc890c385db9228468d8c0ed0cc222eafbd3db5a4c7af5a9a6f5cca4780`. This remains local dirty-Bridge evidence on a non-RC2 SDK and does not close hosted or trusted acceptance.

### Hosted CI recheck — 2026-11-22

### Hosted/remote refresh — 2026-11-23

`git ls-remote --heads origin master` returned Bridge remote `0d18ae1acc34a582cc8c3b93d01394bc68f598b1`; local dirty Bridge remains at `e0fdfdebd537ecfbd67b5fd853940f061a5c41ae`. GitHub confirms the remote commit deletes 142 files (21,921 lines), all under `TempEnv`, and reports `master` unprotected. The newest three visible Bridge Actions runs are all historical failures: `44896587092` and `44896587044` on `e0fdfde`, and `44896579817` on `66e7455`; none targets `0d18ae1`. The first two job lists and conclusions are recorded in [the refresh artifact](../../../../../CircuitWeave-main/artifacts/processor-20261114/hosted-ci-recheck-20261123/runs.json), SHA-256 `47b2ed64061ad615d3660865d29c03529e10b41708424ee8b8596e7e2c8c1627`. Core Actions API returned HTTP 404; Core remote master could not be read within the 30.2s `ls-remote` call. Do not interpret that as proof of a missing ref or a successful CI run.

R0-01 clean tracked-source recheck: archived `Compilers/HybridCPU_Compiler` plus `Compilers/HybridCPU_Platform.Contracts` from Bridge HEAD `e0fdfdebd537ecfbd67b5fd853940f061a5c41ae`; archive SHA-256 `cd289fb8fe2ada9344448583dbe85597e7a23930e0f9c9abbd01415866b2aab0`. The tracked `Core/IR/Artifacts` tree contains zero files and `.gitignore:64` excludes `artifacts/`. Companion Debug and Release each failed with 20 compiler errors, including missing `HybridCPU.Compiler.Core.IR.Artifacts`, `CompilerEmissionPackage`, `CompilerSidebandEnvelope`, descriptor/typed-slot/evidence contracts. [Receipt/logs](../../../../../CircuitWeave-main/artifacts/processor-20261114/bridge-r0-complete-deps-probe-20261122/receipt.json), receipt SHA-256 `bb7775d1d6e4736b4ea1697ae809e94c24aedca8c6e174e15ade5181b0e136dd`. This uses available SDK `11.0.100`, not required RC2; it reconfirms the tracked-contract owner blocker only.

Public run metadata: hosted-ci-recheck-20261122/runs.json in Core artifacts. Run 44896587044 on current Bridge HEAD e0fdfdebd537ecfbd67b5fd853940f061a5c41ae failed in R0-01 Companion production-build qualification; its Windows Debug/Release job exited 1. Run 44896579817 on 66e74559e7aec675f8a72f96185e66324a175488 failed; published annotations show UnprotectedMasterRef and missing compiler contract types from excluded Core Core/IR/Artifacts. Detailed logs require GitHub sign-in and were unavailable, so the exact R0 failure cause remains unconfirmed. Neither run tests today's dirty local changes. The contracts blocker condition remains: Core owner publishes the required tracked compiler contracts in an allowed/Core-owned location, then Bridge reruns the exact pinned producer and importer jobs. Trust acceptance also requires protected master and a positive trusted receipt on aligned pins.

### 2026-11-22 — pinned Golden Linux Csc process observation

The fixed Golden commit was cloned cleanly to TEMP and built on Ubuntu WSL with SDK 11.0.100-rc.2.26504.105. The Windows PowerShell VMX projection-lineage verifier succeeded separately (manifest SHA-256 839dfa9ac9c43f51c751104b8886ea8575fa12476fb196b7313a391afbf35724e); only that Windows-shell target was disabled in the Linux build. Release build exited 0; five successful project Csc tasks and five OS Csc execs were observed, including the main project. The main temporary response file was opened and unlinked. Tracked clone status remained clean. Receipt: artifacts/processor-20261114/golden-linux-csc-observation-20261122/receipt.json (task events and logs beside it).

Classification: local unqualified cross-runtime observation. The response-file bytes were not retained; strace files remain in TEMP. This does not prove Windows production execution, full compile-time input closure, independent Core admission, Companion/method facts, RTL, or stateful Processor behavior. No trusted handoff is claimed.

### 2026-11-22 — pinned Golden response-file capture

A separate clean TEMP clone of Golden commit `6946ccab9d888f7dae0a4d8e18480f436138acd9` was rebuilt on Ubuntu WSL with SDK `11.0.100-rc.2.26504.105` (Core HEAD `b5c44685197a40df99a4d48891120b12475c6ca5`; Bridge HEAD `e0fdfdebd537ecfbd67b5fd853940f061a5c41ae`; neither identifier claims these dirty working bytes are committed). The separate Windows PowerShell VMX lineage verifier succeeded, then only that Windows-shell MSBuild target was disabled in the Linux invocation. Exit 0, five successful task events, five captured response files, and clean tracked status before/after.

The main `HybridCPU_ISE.csproj` Csc task is cross-linked to Csc PID 676 by the exact response path in task interval/OS exec. Its response bytes are 124,246 bytes, SHA-256 `0c4cae5ac1aca2cdb6b6df1c7e7ab55fe98a773927760b568c563db3d0d17b74`; compiler exited 0. [Immutable task events, all five response files, main process trace, and hashes](../../../../../CircuitWeave-main/artifacts/processor-20261114/golden-linux-csc-rsp-observation-20261122/receipt.json).

This remains an unqualified Linux observation: it does not prove Windows production execution, source/reference/analyzer bytes read by Csc, generator output closure, provenance admission, or hardware equivalence. The full 1,104-source Golden task remains outside RoslynCscProfileV2's supported subset.

### 2026-11-22 — Core compiler-source inventory vs binding roots

CW-R3 now permits a profile-bound `source` row to be a compiler-required source that is not itself an approved binding root. The main selected root remains checked against the approved manifest; `owned-body` rows still require independent call-graph ownership; `unselected` remains refused. The synthetic fixture includes `src/AdditionalCompilerInput.cs` as a compiler source outside the binding roots and passes the freshly rehashed `unselected`-role refusal plus existing fail-closed cases. [Build/run receipt](../../../../../CircuitWeave-main/artifacts/processor-20261114/cwr3-additional-source-20261122-final/receipt.json), Core local HEAD `b5c44685197a40df99a4d48891120b12475c6ca5`.

This fixes composition semantics for multi-file compiler closures only. It does not lift production scale bounds or implement analyzer/source-generator replay; V2 still refuses the Golden analyzer/generator profile and independent closure remains unadmitted.

### 2026-11-22 — Csc source bytes reconstructed from process reads

The exact main response file names 1,104 distinct `.cs` inputs. A bounded audit of the Csc process/thread tree (`PID 676`, 67 trace files) reconstructed complete `read`/`pread64` buffers for all 1,104 paths: 8,978,960 bytes total. Every reconstructed digest matches the corresponding post-build byte in the clean pinned Golden clone; the normalized response-file source path set equals the captured manifest set (1,104/1,104). Source archive and thread trace archive verification passed. [Receipt, source manifest, source bytes, raw traces, and validation](../../../../../CircuitWeave-main/artifacts/processor-20261114/golden-linux-csc-source-read-observation-20261122/receipt.json).

Classification remains unqualified Linux observation. The package does not establish the 180 reference bytes, analyzer/config inputs, embedded resources, in-memory generator output, independent Core admission, Windows production execution, or trusted handoff.

### 2026-11-22 — Core generated-output replay candidate join

Core joined its pinned generated-output preflight/inventory/set and adjusted response to the isolated replay receipt, then rehashed all 50 generated output files (583,941 bytes) in the sealed, raw capture and replay trees. The build passed and 14 negative cases rejected stale pins, path/root overlap, missing/extra/altered bytes and incomplete sets. [Candidate run receipt](../../../../../CircuitWeave-main/artifacts/processor-20261114/csc-full-closure-candidate-20261122-current/receipt.json).

The result remains `CandidateOnly`: this machine-bound non-atomic rehash does not authenticate issuer or prove historical execution, complete compiler inputs, analyzer execution/ownership, independent Core admission, trusted handoff, or hardware equivalence.

### 2026-11-22 — Core isolated Csc replay receipt recheck

Core's bounded verifier accepted the existing replay receipt and rehashed 1,296 declared input paths, 1,299 clone paths and 191 unchanged external-input digests around the recorded replay; it matched recorded PE/PDB/refout and 50 generated outputs. All eight negative cases rejected. [Candidate evidence](../../../../../CircuitWeave-main/artifacts/processor-20261114/csc-replay-receipt-verifier-20261122-current/receipt.json). The verifier does not run Csc or authenticate execution/provenance; analyzer/generator side effects and trusted handoff remain open.

### 2026-11-22 — Windows task observation and cross-runtime source/IL comparison

Windows Release build of the isolated pinned Golden passed with five successful MSBuild Csc task events and unchanged tracked tree. Main task projection enumerated 1,104 source, 180 reference, 10 analyzer, 2 analyzer-config and 3 embedded inputs with no response-file token. Windows task source tokens were resolved against the build invocation project directory for a post-build current-file snapshot, not a Csc read-buffer capture. Its source path set matched the Linux response manifest; 1,102 source files matched byte-for-byte and two generated files differed only by CRLF/LF. All normalized C# text matched. Same-commit Linux rebuild passed; PEReader found 30,561 matching method keys, raw signature blobs, raw IL byte streams, and MethodAttributes/MethodImplAttributes flags. It does not compare custom attributes, body headers, local signatures, exception regions, type/assembly metadata, or full PE/PDB semantics. [Candidate evidence](../../../../../CircuitWeave-main/artifacts/processor-20261114/golden-windows-csc-task-observation-20261122/attempt-03/receipt.json).

This is task-level evidence, not a Windows OS process/read witness. PE/PDB and SDK Csc DLL hashes differ; external input bytes, analyzer side effects, provenance/trust and hardware equivalence remain open.

### 2026-11-22 — Windows Csc process and response capture

A separate clean TEMP clone at pinned Golden commit `6946ccab9d888f7dae0a4d8e18480f436138acd9` was built on Windows with SDK `11.0.100-rc.2.26504.105`. Five actual `csc.exe` processes were observed by 35 ms `Win32_Process` polling; each PID/parent PID, executable path/hash, OS command line and response path was captured, and all five response files were copied and hashed. All five task events succeeded; build exit 0; tracked tree unchanged (`243a67c8ef462c1da2d1f06df9bee2c31508cb40`). [Receipt and response bytes](../../../../../CircuitWeave-main/artifacts/processor-20261114/golden-windows-csc-task-observation-20261122/attempt-05/receipt.json), receipt SHA-256 `92c46806e7800ca3d4ea996cab21696e9497d3ea4bfc9a9b7d082db0180ff4dd`.

Remaining: WMI sampling is not complete process tracing; response bytes do not prove read buffers; all five captured response argument lists match the corresponding task event in order after matching output tokens; this does not prove Csc read those bytes. Source/reference/analyzer/config/embedded read closure, trusted handoff and independent admission remain open. No hardware or Processor scope is qualified.

### 2026-11-22 — Linux Csc reference/analyzer input reads (historical earlier capture; superseded by the fresh 186/196-input capture below)

Extended the pinned Linux Csc trace analysis across main response-file filesystem inputs. An independent response parser confirmed exact set equality: all 196 distinct declared non-source inputs were opened by the traced Csc process/thread tree, with no missing, extra or duplicate paths. Recorded `read`/`pread64` bytes fully reconstructed and matched pinned post-build bytes for 180 inputs / 5,338,258 bytes: 178 references, one analyzer config and one embedded input. Sixteen remain incomplete: 10 analyzers, one analyzer config, two embedded sources, two references and SourceLink. [Detailed receipt and capture](../../../../../CircuitWeave-main/artifacts/processor-20261114/golden-linux-csc-input-read-closure-20261122/receipt.json), SHA-256 `6f3c17d4417c9c9e1cce3d9c2c5310c9086b01bb53a1ece78d7d1125deec3aac`.

This remains local unqualified trace analysis. File opens do not prove complete file bytes; mapped/truncated inputs and generator side effects remain open. It is not Windows parity, trusted handoff, independent Core admission or hardware evidence.

Verifier `verify-closure-capture.ps1` passed 207 pinned-input, read-byte and artifact-hash checks; full analyzer read bytes remain unobserved.

### 2026-11-22 — Linux Csc analyzer mapping and read-buffer update

A fresh clean pinned Golden Linux Release build (commit `6946ccab9d888f7dae0a4d8e18480f436138acd9`, tracked tree `243a67c8ef462c1da2d1f06df9bee2c31508cb40`, Ubuntu WSL SDK `11.0.100-rc.2.26504.105`) succeeded with five successful Csc task events and unchanged tracked state. With a 2 MiB strace string limit, all 196 paths in the retained-response/current-task crosswalk were opened; read buffers for 186 inputs / 6,802,994 bytes matched pinned post-build bytes: 180 references, three embedded inputs, two analyzer configs and SourceLink. The remaining 10 analyzer DLLs each had a successful offset-zero `PROT_READ` mapping covering the full file length, but mapping does not prove contents/page faults; zero analyzer read buffers are complete. [Receipt and captured inputs](../../../../../CircuitWeave-main/artifacts/processor-20261114/golden-linux-csc-input-mmap-observation-20261122/receipt.json), SHA-256 `a2a995888080e1351e44fed6102d38f4d7eb06f2bdd6c819fe415c576d3200d9`; verifier passed 218 checks.

At initial packaging, the current build's temporary response bytes had not been separately included. They were later reconstructed from 33 pread64 buffers in the same Csc PID 705 raw trace: 124,246 bytes, SHA-256 0c4cae5ac1aca2cdb6b6df1c7e7ab55fe98a773927760b568c563db3d0d17b74. The supplemental verifier replayed this reconstruction and passed seven checks; the exact bytes crosswalk to the successful task's ordered arguments and 196-path open set ([receipt](../../../../../CircuitWeave-main/artifacts/processor-20261114/golden-linux-csc-response-read-capture-20261122/receipt.json), SHA-256 611ef88544589e016dd7a2ae00a7fcf592ff62c08a7ce7516fb122b36fe8c10a). The ten analyzer DLL images are separately snapshotted and hash-cross-checked 10/10 ([receipt](../../../../../CircuitWeave-main/artifacts/processor-20261114/golden-linux-csc-analyzer-images-20261122/receipt.json), SHA-256 c3b4a3527b24cd686d560a7a665e8fc08f8d4cad884b31e6efe2e68af95fe859); mmap does not prove page faults or consumption. This Linux evidence does not establish Windows read parity, analyzer/generator output or side-effect closure, independent Core admission, trusted handoff or hardware/Processor verification. HB-INT-02 and integration acceptance remain open.

### Core profile capacity follow-up — 2026-11-22

The Core bounded `RoslynCscProfileV2` ceiling was raised to 2,048 source rows, 2,048 references and 4,096 ordered Csc arguments while retaining aggregate byte budgets and the exact fail-closed option subset. The 187-check CW-INT-04 executable independently re-emitted exact PE/PDB bytes for two roots from a synthetic 129-source/129-reference profile, passed P02 resolution/locking and refused reordered references ([receipt](../../../../../CircuitWeave-main/artifacts/processor-20261114/core-csc-profile-capacity-20261122/receipt.json), SHA-256 `5417b3b2b390118c006da0658467c9d5d0cbee1d3b8085daebe0fc896363ec70`). Analyzer/generator/generated-source inputs remain unsupported and full Golden independent replay/admission remains open.

### Hosted evidence refresh — 2026-11-22

`git ls-remote` reports Bridge remote `master` at `0d18ae1acc34a582cc8c3b93d01394bc68f598b1`, while the local Bridge HEAD remains `e0fdfdebd537ecfbd67b5fd853940f061a5c41ae`; the sole remote delta removes `TempEnv` files outside the permitted subtree. No Actions runs are listed for the remote tip. The latest relevant runs are still failures on `e0fdfde`: HB-INT-06 run `44896587092` (event `push`; preflight job failed before private Core/Golden checkout; Windows Bridge contract/source/mutation job failed) and R0-01 run `44896587044` (event `push`; Windows production graph/build/PE/source-immutability job failed). Job log endpoints returned HTTP 403, so failure details are not asserted from logs. The unauthenticated CircuitWeave Actions API returned 404; no current Core remote run or positive trusted receipt was available. [Machine-readable record](../../../../../CircuitWeave-main/docs/roadmap/circuitweave/CW_INT_Backlog.v1.json).

### Linux analyzer image snapshot — 2026-11-22

Ten analyzer DLL images were copied from the pinned Linux Csc PID 705 mmap paths and match the recorded post-build disk SHA-256 values. The pinned parent/report verifier checks exact path, order, length and hash for all ten ([receipt](../../../../../CircuitWeave-main/artifacts/processor-20261114/golden-linux-csc-analyzer-images-20261122/receipt.json), SHA-256 `c3b4a3527b24cd686d560a7a665e8fc08f8d4cad884b31e6efe2e68af95fe859`). mmap remains path/range evidence and does not prove page faults or consumed content. The current main response bytes were separately reconstructed from the same trace's read buffers: 124,246 bytes, 33 calls, SHA-256 `0c4cae5ac1aca2cdb6b6df1c7e7ab55fe98a773927760b568c563db3d0d17b74`; ordered task arguments and the 196-open path set match ([response receipt](../../../../../CircuitWeave-main/artifacts/processor-20261114/golden-linux-csc-response-read-capture-20261122/receipt.json), receipt SHA-256 `611ef88544589e016dd7a2ae00a7fcf592ff62c08a7ce7516fb122b36fe8c10a`). Analyzer outputs/side effects, Windows parity, independent admission and trusted handoff remain open.


### 2026-11-22 — current Bridge fixture suite and response crosswalk

The exact Windows attempt-05 task/process/response crosswalk was independently audited: five successful Csc tasks map one-to-one to five sampled Csc processes and five copied response files; all ordered task argument vectors match their response copies after the documented executable/`/noconfig` normalization. The receipt SHA is `92c46806e7800ca3d4ea996cab21696e9497d3ea4bfc9a9b7d082db0180ff4dd`. This is a candidate transport crosswalk; WMI sampling can miss short-lived processes and copied response bytes do not prove the compiler consumed those bytes.

`python -m unittest discover -s HWIR/EvaluatedProjectGraph -p 'test_*.py' -v` on Bridge HEAD `e0fdfdebd537ecfbd67b5fd853940f061a5c41ae` passed 148 tests with 6 platform-dependent symlink skips (exit 0). Pinned log: [`unittest.log`](../../../../../CircuitWeave-main/artifacts/processor-20261114/bridge-hbint02-regression-20261122/unittest.log), SHA-256 `4ad9b67fe605abe068becb431fc1df2de405962ee1cde54507f33a7969682ca4`; [receipt](../../../../../CircuitWeave-main/artifacts/processor-20261114/bridge-hbint02-regression-20261122/receipt.json), SHA-256 `d7c868f28cd78065af230066975d03bdfdd5f8741d0a9117f1035a8399cb24c3` pins 18 Python inputs. The suite exercises fixture transport, mutation refusal, and synthetic 1,001-source bounds; it is not production Csc capture, Core independent admission, trusted handoff, or hardware verification.


### 2026-11-22 — Windows task/process/response candidate bundle

The new bounded `windows_task_rsp_bundle_v1.py` adapter independently validated Windows Golden attempt-05. All four JSON receipts/crosswalks, five PID-named response copies, and the compiler executable are SHA-pinned; it emits five ordered command records (token counts 233/247/246/225/1323). Candidate output is [`bundle.json`](../../../../../CircuitWeave-main/artifacts/processor-20261114/windows-csc-task-rsp-bundle-20261122/bundle.json); receipt SHA-256 `49e2a25351a8c68e25364352afd6f2cb20373ad8464c50fda492618a536700cf`. Independent artifact verifier passes its 10 input pins and three adapter-code pins. The projection explicitly leaves cwd `NotRecorded`, response consumption `NotEstablished`, Core admission `NotAdmitted`, and hardware verification `NotRun`. This does not establish Roslyn RSP grammar parity or read buffers, because comparison uses Win32 `CommandLineToArgvW`; WMI sampling is incomplete. No Core staging/import has occurred.

The standard Bridge regression command `python -m unittest discover -s HWIR/EvaluatedProjectGraph -p 'test_*.py' -v` now discovers the 14 new adapter mutation cases through a narrow top-level wrapper; current result is 162 tests, 6 symlink skips, exit 0. [`receipt.json`](../../../../../CircuitWeave-main/artifacts/processor-20261114/bridge-hbint02-adapter-discovery-20261122/receipt.json) SHA-256 `f98174ccbb5f91e3889b720492d09f7e12f8a8bdf5a751850e29253b0d11b199` pins 21 Python inputs and the log. The earlier 148-test run predates the wrapper and is superseded as latest regression evidence.


### Exact Windows-to-Core staging gate — 2026-11-22

The bundle is not yet a reviewed HB-INT-02 capture accepted by `stage_core_v2.py`: that producer requires a same-invocation `workingDirectory`, evaluated Compile item completeness, exact source-map closure, project graph/root pins and generator-output directory identity. This bundle intentionally reports cwd NotRecorded and contains task/process/response crosswalk only. Do not infer cwd from the project path or launcher. Required evidence before staging: capture cwd directly for each Csc invocation, bind the corresponding evaluated project graph/Compile inventory and generated-output closure to task/PID/response pins, archive current source/reference/analyzer/config/embed bytes, then run Bridge staging and independent Core V2 verifier with external pins.


### 2026-11-22 — Windows Csc launch, path grammar, and closure capture

Pinned Golden `6946ccab9d888f7dae0a4d8e18480f436138acd9` / tree `243a67c8ef462c1da2d1f06df9bee2c31508cb40` was built in a separate isolated clone with SDK `11.0.100-rc.2.26504.105`. The disposable Csc launch probe captured launch-time cwd and raw RSP bytes under a read-only handle through child exit; it crosswalked five successful Csc tasks to five child PIDs/exit-0 records and exact parsed expanded args. Same-build target captured 1,191 Compile items and their set matched all Csc source operands. 1,176 tracked source inputs matched pinned Golden bytes; 15 `obj` compile inputs were MSBuild-generated. The emitted source directory contained 50 `.cs` outputs. The producer captured and reverified 2,218 input rows (86,594,404 bytes), closure `0046f8dde1a242beb95e2c587c4f7d96e54ee20b17d2b33ae9d9415bddc271dc`, index SHA `6cb4dc2e4fb15cebb8a3a629ec6521c5535423c9970ea88fe34f7fccdfda6f4e`; persistent bundle: `CircuitWeave-main/artifacts/processor-20261114/golden-windows-csc-capture-20261122/capture-v2`. Direct Csc rebuild matched all 44 instrumented PE/PDB output hashes.

Actual SDK analyzer inputs exposed `/debug-`, `/embed:` and paths containing `targets/../analyzers`. The Bridge parser now recognizes `/debug-`, embeds and metadata role, and resolves compiler input components before collapsing `..`, refusing traversal through symlink/junction/reparse components. Core's deprecated V2 projection stores `embedded` as metadata-only. The component walk is non-atomic and assumes a sealed, non-mutating snapshot. This evidence does not prove Csc consumed the captured RSP or source/analyzer bytes, close analyzer side effects, or qualify Core admission/trust or hardware. No approved Processor roots were supplied, so Core staging, HWIR/RTL and simulation remain open.


The post-slice Bridge discovery run passed: `python -m unittest discover -v`, 177 tests, 12 platform-dependent skips, exit 0, Bridge HEAD `e0fdfdebd537ecfbd67b5fd853940f061a5c41ae`. [Receipt](../../../../../CircuitWeave-main/artifacts/processor-20261114/bridge-hbint02-discovery-windows-capture-20261122.receipt.json), log SHA-256 `0de90f4d2357d1c2c3540bf548662904ade03b90eeac7ad699fe4739cc3b096c`. This is local producer regression evidence only; hosted CI, independent Core admission and hardware qualification remain open.


**Graph provenance correction:** the pinned graph manifest uses values captured by an injected `BeforeTargets=CoreCompile` target in the observed build; this was not a separate MSBuild `ProjectGraph` evaluation. `compileFiles` contains 1,176 Golden-origin inputs. The other 15 Csc source operands are build-generated `obj` files marked separately as `msbuild-generated`; the crosswalk verifies all 1,191 target Compile items against the response-derived Csc source set. Fixture test success, same-build target evidence and instrumented OS child execution remain separate evidence classes.

### P0 follow-up — pinned RC2 local opt-in, 2026-11-23

The pinned Windows x64 .NET SDK archive `11.0.100-rc.2.26504.105` was obtained from official release metadata and its SHA-512 matched. With that SDK, local Release Rebuild of `HWIR/HardwareBridge.Hwir.csproj` passed using `EnableBridgeHwirLegacyFixtures=true`, `BridgeExpectedSdkVersion=11.0.100-rc.2.26504.105`, clean TEMP Core clone `714e5c939bb1817481cf4970e3e343aa92bfa23b`, exit 0 / 0 errors / 1,021 warnings. [Receipt and log](../../../../../CircuitWeave-main/artifacts/processor-20261114/bridge-optin-rc2-20261123/receipt.json), receipt SHA-256 `b3233082bddb429c705820cea781e206760d9d5b02621bffd6302ac5dcc9fe67`. This qualifies only the local opt-in compile on dirty Bridge bytes; tracked Companion contracts, hosted trusted handoff, independent Core admission, and hardware proof remain open.

### Core handoff regression refresh — 2026-11-23

The current Core reader and CW-R3 composition suites were rebuilt/re-run under pinned SDK `11.0.100-rc.2.26504.105`: CW-INT-03 61 assertions, CW-INT-04 187 checks, CW-R3 fixture exit 0. Results remain synthetic; Core reports CandidateOnly and does not establish Golden admission or native RTL. [Receipt](../../../../../CircuitWeave-main/artifacts/processor-20261114/core-handoff-recheck-20261123/rc2/receipt.json), SHA-256 `5550d2b144851db236d96e2a75492f66ba5ec732a490b44fb89fb6c89517c6fe`.

### Golden compiler-input gap evidence — 2026-11-23

A Core-side independent byte checker rehashed the retained Windows Csc capture: 2,218 entries, 86,594,404 bytes, 2,241 chunks, five command argv digests, with wrong-pin refusal. The captured primary Csc has 1,104 source / 180 reference operands but also 10 analyzer, two analyzer-config and three embedded operands; all projects include 50 analyzer binaries and 50 generated-source snapshots. Current Core Roslyn profile refuses analyzers/generator outputs and additional Csc switches, so actual independent re-emission remains unavailable. [Receipt](../../../../../CircuitWeave-main/artifacts/processor-20261114/golden-csc-inputs-recheck-20261123/receipt.json), SHA-256 `6cad8b8f60279680a3c4d1b866df0968d93c07fc54e71d8a20bebcde9589c03a`. Pins were retained capture comparisons, not external approval; do not treat this as independent Core import.

### Roslyn captured-byte SourceText API subcheck — 2026-11-23

Using SDK `11.0.100-rc.2.26504.105`, direct `SourceText.From(byte[], length, UTF8, Sha256, throwIfBinaryDetected:true, canBeEmbedded:true)` preserved the indexed SHA-256 and allowed `EmbeddedText.FromSource` for all 15 actual embedded rows (7,733 bytes; zero mismatches). The API log is `CircuitWeave-main/artifacts/processor-20261114/golden-csc-inputs-recheck-20261123/sourcetext-embedded-api-probe.log`, SHA-256 `d6a536a372825c3f0aa8bb147478b30b842649502a30b4581998e75df5bee78a`. This corrects the earlier probe failure mode; it does not qualify a full replay. The next required evidence is an isolated Roslyn replay from captured byte operands with the actual generator-output ordering independently established, exact PE/PDB comparison, and no use of the Golden PDB as an ordering oracle. Integration remains INCOMPLETE.

### Bridge producer mutation suite — 2026-11-23

On dirty Bridge worktree at `e0fdfdebd537ecfbd67b5fd853940f061a5c41ae`, `python -m unittest discover -s HWIR/EvaluatedProjectGraph -p 'test_*.py' -v` passed 177 tests with 12 platform-dependent skips (exit 0; 196.549 s). [Receipt](../../../../../CircuitWeave-main/artifacts/processor-20261114/bridge-producer-suite-20261123.receipt.json), SHA-256 `070ca3cbc4b17d9453f565a07f6ee47c6081ac4a284b0e13a93011c2fdd121eb`; [log](../../../../../CircuitWeave-main/artifacts/processor-20261114/bridge-producer-suite-20261123.log), SHA-256 `f61a2e568dcf94c807b6c533eb4a8afd6ce4bc749e4619666baa11fad05ec318`. This exercises Bridge producer/synthetic fixture refusal paths only. R1-01 task-execution attestation, owner-approved roots, trusted Core admission, hosted CI and hardware verification remain open.

### Portable default and current pinned opt-in build — 2026-11-23

With SDK `11.0.100-rc.2.26504.105`, the default `HardwareBridge.Hwir.csproj` Release build passed with zero warnings/errors; evaluated `ProjectReference` items were empty. Explicit legacy-fixture opt-in rebuilt successfully against a clean TEMP clone of current Core `65c3b961c084039f43b5dc1a9df158d99d2b5844` (zero errors, 1,021 XML documentation warnings). A deliberately wrong Core SHA was refused with `HBINT0103 WrongCoreRevision` (exit 1). [Receipt and logs](../../../../../CircuitWeave-main/artifacts/processor-20261114/bridge-build-recheck-20261123/receipt.json), receipt SHA-256 `95f33e41f39aede16e2894d2cf70ff3e83f6d619b79f6f090d79f220e7f9f98a`. This validates local build isolation and pin guard behavior only; companion compiler contracts, trusted Core admission and Processor hardware remain open.

### R0 Companion tracked-contract recheck — exact pinned SDK, 2026-11-23

A clean TEMP clone of the tracked parent repository at `e0fdfdebd537ecfbd67b5fd853940f061a5c41ae` was checked with SDK `11.0.100-rc.2.26504.105`. `git ls-files Compilers/HybridCPU_Compiler/Core/IR/Artifacts/**` returned zero files; `git check-ignore -v` attributes `CompilerEmissionPackage.cs` to root `.gitignore:64` (`artifacts/`). `HardwareBridge.Companion.csproj` failed Debug and Release with 20 compiler errors in each configuration for missing `HybridCPU.Compiler.Core.IR.Artifacts` and associated contracts. [Proof, logs, and receipt](../../../../../CircuitWeave-main/artifacts/processor-20261114/bridge-r0-current-tracked-recheck-20261123/receipt.json), SHA-256 `2c5d3a60454a5ebfbe3847379a2b00820d5a9b7e2f088a5a9f0b39de5300a26c`. Resolution owner: Core compiler owner must publish the complete compatible artifact/sideband/descriptor/typed-slot/evidence contract set as tracked files in an allowed Core-owned location. Then rerun clean pinned Companion Debug/Release and independent handoff. This is an owner blocker; no ignored file was copied into the clone and no out-of-scope path was edited.

### Full Golden Roslyn replay differential — 2026-11-23

An isolated Core-side Roslyn API replay consumed the pinned capture rows: 1,104 source operands, 180 reference byte images and 50 generated-source snapshots. Direct byte `SourceText` construction reproduced all 1,154 PDB document paths and SHA-256 values; adding captured generator snapshots as embedded text reproduced all 53 decompressed EmbeddedSource payloads. The captured logical generator-row order does not match PDB document order. A diagnostic variant used actual Golden PDB ordering, and a second diagnostic injected VERSIONINFO `.res` extracted from the actual PE. Neither variant matched full PE/PDB hashes. With the output-derived resource the `.rsrc` section matched exactly, while `.text` still differed; method IL bodies matched, but the native PDB id/hash still differed. These two oracle-guided inputs make the runs diagnostic only. [Receipt/report/harness and logs](../../../../../CircuitWeave-main/artifacts/processor-20261114/golden-full-replay-diagnostic-v4-20261123/receipt.json), receipt SHA-256 `2ddd023fa84f47adc6b8174f70f92faa900da545c71ace8adca190d22ad79290`. Resolved local substep: the independent generator-driver witness now derives and byte/order-checks all 50 outputs and their order. Remaining: bind MSBuild-generated VERSIONINFO/native-resource bytes to the actual Csc task or postprocessor and close the full PDB serialization comparison. No profile widening or PE/PDB equivalence is claimed; integration remains INCOMPLETE.


### Independent Roslyn generator-driver witness — 2026-11-23

Executed all 10 generators from the captured primary Csc analyzer binaries using the pinned RC2 Roslyn APIs, 1,104 captured source operands, 180 metadata images and two analyzer configs. The driver emitted 50 sources without load or generator diagnostics; each captured text matched exactly after stripping only the captured UTF-8 BOM, and the independently produced order matched the 50 Golden PDB document paths. The PDB was read only as a comparison oracle; no PDB ordering was fed into the driver. [Harness, log and receipt](../../../../../CircuitWeave-main/artifacts/processor-20261114/golden-generator-driver-witness-20261123/receipt.json), receipt SHA-256 `ed855b014a1b964fd50a4215e389af1180d780edda672e78b2ec8fa8ffb47703`. This closes the local generator-output/order witness only. It does not prove production Csc execution, capture completeness/ownership, trusted Core admission, or full PE/PDB equivalence. Next: bind the actual VERSIONINFO/native resource bytes to the observed Csc task/postprocessor, then continue the PE/PDB differential and independent handoff. Integration remains INCOMPLETE.


### Historical generator-derived-order differential (superseded below) — 2026-11-23

The full replay was rerun with all 50 generated-source rows ordered from the independent pinned-analyzer GeneratorDriver manifest, without feeding PDB order into that mode. The emitted PDB document order matched all 1,154 Golden document paths, and PE/PDB hashes matched the separate PDB-guided diagnostic output (`861292cb…` / `1319ba65…`) byte-for-byte. Against Golden PE/PDB, hashes still differ (`1ed30367…` / `164f6a91…`). This run supplied no Win32 VERSIONINFO resource and remains diagnostic. [Harness, manifest, log and receipt](../../../../../CircuitWeave-main/artifacts/processor-20261114/golden-generator-order-replay-20261123/receipt.json), receipt SHA-256 `21bb9bf5f1a5fa9f5d67eb6a487fe6e6f0726dfb8b3bc2d5094ab5db3adcb95d`. Next: capture and bind the exact resource bytes to observed MSBuild/Csc execution, then continue PE/PDB differential and independent Core admission. Integration remains INCOMPLETE.


### Historical captured-metadata VERSIONINFO differential (superseded below) — 2026-11-23

The pinned Roslyn `CSharpCompilation.AppendDefaultVersionResource` generated the version-resource record from the captured source/reference compilation and the independent generator-order manifest. A standard null `.res` header was added for linking; no bytes extracted from the Golden PE were used as replay input. The generated 828-byte RT_VERSION payload hash matched the payload extracted from the Golden PE as a comparison check, and replay PE SHA matched the prior output-derived-resource diagnostic variant. PDB document paths matched all 1,154 Golden documents. Full equivalence remains false: actual/replay PE `1ed303674a94b65276ae56e57d877647d7e28400ac8c9b4d7ea6a4371d7b2859` / `7e113c5596fed213ed91c2e41249e9d0e769d728b4fc014500ae99998499884a`; actual/replay PDB `164f6a911f94715ead588e843c6fcb2a6c77ebc5ae920fbedafa32fb80af718a` / `1319ba65368953d0d82c248de87dd21f9e1a8fda5d1ba8d069ea785d639475b6`. The capture evaluation has empty `Win32Resource`; default resource synthesis is compiler behavior, not a Csc argv input. This narrows the resource question but does not attest the original Csc execution or prove independent Core receipt. PDB semantic comparison found all 1,154 documents, 30,561 method-debug records, 71,642 sequence points, 25,504 scopes, 8,152 locals, 44 constants and 1,006 CDI keys aligned; the only row payload differences are the 50 generated-source EmbeddedSource compressed blobs, whose decompressed content is identical. PDB byte sizes differ by 612 bytes. [Semantic comparison and compression probe](../../../../../CircuitWeave-main/artifacts/processor-20261114/golden-captured-version-resource-replay-20261123/pdb-semantic-diff.log). [Probe, resource, replay and receipt](../../../../../CircuitWeave-main/artifacts/processor-20261114/golden-captured-version-resource-replay-20261123/receipt.json), receipt SHA-256 `5940b13c82299236f4172e8df9e466b3b7a06f322a8ab0971d128f56fe9a5b78`. Next: resolve PE/PDB serialization deltas and obtain trusted independent Core admission; integration remains INCOMPLETE.

### Exact local Roslyn replay — 2026-11-23

The formerly open local PE/PDB serialization delta is resolved for the retained pinned capture. Pinned Roslyn GeneratorDriver independently executed the 10 captured analyzers and derived 50 generated sources/order; applying the primary Csc SHA-256 document checksum option produced exact PE and PDB hashes matching Golden. Receipt/report/harness/log: `CircuitWeave-main/artifacts/processor-20261114/golden-generator-direct-source-checksum-replay-20261123/receipt.json`. Scope is deterministic replay only; original Csc file reads, atomic capture, analyzer side effects, owner-approved roots, trusted Core admission, hosted no-write and hardware verification remain open. Matrix status remains INCOMPLETE.

Core boundary follow-up: source review confirms `RoslynCscProfileV2` is passed through CW-R3 composition, and its refusal of non-empty analyzer/generator/generated-source paths is deliberate fail-closed behavior. No current wiring defect is evidenced. Independent Core replay of this closure requires a separately versioned bounded contract and a qualified isolated analyzer/generator host; CW-INT-14 still refuses native execution with `CW-EXT01-ISOLATION`. Do not broaden V2 or treat the local path-loaded generator replay as Core admission.

### Adjacent Processor helper differential — SignExtendWord — 2026-11-23

The pinned Golden PE MethodDef `0x0600180d` matched the sign-extension expression implemented by the candidate RTL over all 4,294,967,296 UInt32 inputs; the candidate was emitted at Core HEAD `b5c44685197a40df99a4d48891120b12475c6ca5`. At `0x80000000` the zero-extension alternative differs; the earlier finite native receipt separately records an RTL mutant mismatch. [Receipt/report/harness/log](../../../../../CircuitWeave-main/artifacts/processor-20261114/integration-20261121/sign-extend-full-domain-20261123/receipt.json), receipt SHA-256 `96356ace545662e1816b2fd72d25909ea091e7d1d302bc47a12a420408055280`. This is exhaustive Golden-method-versus-expression evidence. The actual native RTL run remains finite (196,608 vectors), source admission/Studio load are NotRun, and no stateful Processor behavior is verified.

Core-current projection check for the SignExtendWord helper: current Core HEAD `9d0b4b9f50ab30bba3bea5eb2d59a4509cf0f435` returned `Admitted` and emitted HWIR/MLIR/vector files byte-identical to the earlier bounded projection. [Receipt/report/log](../../../../../CircuitWeave-main/artifacts/processor-20261114/integration-20261121/sign-extend-generic-current-core-20261123/receipt.json), SHA-256 `2a303689fe98b25e4657f099b3bc5c0e44b74bb2e90a663f886ad13928cc6d2f`. This is method-projection admission only; the pinned CIRCT executable was not available for re-elaboration, and complete Csc source admission remains open.

### Current local Bridge build qualification — 2026-11-23

Against clean Core `9d0b4b9f50ab30bba3bea5eb2d59a4509cf0f435`, SDK `11.0.100` builds Core Frontend.Roslyn, Bridge portable default, and Bridge explicit pinned opt-in successfully. Portable default evaluates no Core ProjectReferences; wrong SDK pin is refused by `HBINT0104 WrongSdk`. Evidence: `../../../../../CircuitWeave-main/artifacts/processor-20261114/bridge-build-current-core-20261123/receipt.json` (SHA-256 `9dd5363e27957dcdf048a26ddc341e49ef2b1d1387294d91985cd574890f1f2a`). This covers local build isolation and pin guards only. It does not qualify the dirty Bridge bytes at a tracked commit, hosted Windows/Linux jobs, reviewed trusted pins, Core admission or hardware. R0-R3 remain open.


### CW-INT-14 Linux isolation mechanism probe — 2026-11-23

Core-side WSL2/bubblewrap/cgroup v2 fixture evidence is recorded at `../../../../../CircuitWeave-main/artifacts/processor-20261114/cw-int14-bwrap-probe-20261123/receipt.json` (SHA-256 `e422608eef895b861fd011c6326ae6c3dc771fae6bcefbb54421abe291eaf30c`). It confirmed namespaces, read-only input/closed host mounts, zero capabilities, network denial, and observed CPU/memory/PID limits for a Python adversarial fixture. No Core-approved ToolBridge executor or Roslyn generator ran; this does not close CW-INT-14, HB-INT-03/06, or trusted handoff. Exact hosted Windows/Linux qualification remains open.


### CW-INT-14 Roslyn generator sandbox follow-up — 2026-11-23

A real Roslyn `GeneratorDriver` fixture ran a hostile generator in Ubuntu WSL2/bubblewrap with cgroup v2; the exact SHA-pinned assembly byte array was loaded, and positive output repeated byte-identically. Wrong/altered analyzer pins, altered AdditionalText, memory, CPU and child-process attacks were refused or contained. Core artifact receipt: `../../../../../CircuitWeave-main/artifacts/processor-20261114/roslyn-generator-isolation-20261123/receipt.json` (SHA-256 `451a55035e5132687bd798993e1eb394e3080e1a97eda429c93930b847f9b17e`). This does not integrate the Core ToolBridge or production Csc handoff; no Golden or Bridge production generator was run. CW-INT-14/HB-INT-03/HB-INT-06 and trusted acceptance remain open.

#### Integration seam audit — 2026-11-23

Core source review found no safe route to enable this fixture through the
existing EXT01 planner: it binds absolute workspace paths, and the approved
execution path still returns `CW-EXT01-ISOLATION`. Keep this fail-closed
behavior. `IHardwareToolBridge` is not a sandbox and its observation contract
does not attest isolation. Core's next implementation slice must add a
separate Roslyn replay/sandbox contract over immutable staged input bytes,
sandbox-relative paths, exact analyzer/generator/reference/SDK/runtime pins,
bounded outputs and a verifiable OS isolation receipt. Native negative tests,
Windows qualification and hosted CI are required before any positive trust
handoff. The fixture is a mechanism witness only; it does not close HB-INT-03,
HB-INT-06, CW-INT-14 or R0-R3.

Core has since added the byte-only request/result contract
`CircuitWeave.RoslynSandbox` with 22 passing structural contract assertions
(receipt `CircuitWeave-main/artifacts/processor-20261114/roslyn-sandbox-contract-20261123/receipt.json`,
SHA-256 `2e1c3c754891063881ef3cd8e91807863c9fb3920109cefe119e55de55c64849`).
This does not launch or admit generator outputs. EXT01 remains fail-closed;
HB-INT-03/06, CW-INT-14 and trusted acceptance remain open pending qualified
production handoff.

Core subsequently added a fixed-purpose Linux supervisor/host/worker. Its
root-WSL fixture covers repeat output bytes, CPU/memory/PID containment,
timeout/cancellation cleanup, driver pin refusal and missing-controller
refusal. The adversarial generator can forge a valid output frame and exit
before GeneratorDriver completes; the host accepts it, so the result is not
execution evidence. Evidence:
`C:/Users/Yuriy/Desktop/CircuitWeave-main/artifacts/processor-20261114/roslyn-sandbox-linux-host-20261123/receipt.json`.
The host removed its unpinned Bash dependency and now waits for cgroup cleanup.
This mechanism result does not qualify unprivileged/hosted delegation, Windows,
production Csc inputs, authenticated receipts, Bridge execution, or Core source
admission. CW-INT-14 and the overall integration remain open.

Update 2026-11-23 — exact capture boundary rechecked at Core
`9d0b4b9f50ab30bba3bea5eb2d59a4509cf0f435`, Bridge
`e0fdfdebd537ecfbd67b5fd853940f061a5c41ae`, Golden pinned commit
`6946ccab9d888f7dae0a4d8e18480f436138acd9`. The retained capture index is
`6cb4dc2e4fb15cebb8a3a629ec6521c5535423c9970ea88fe34f7fccdfda6f4e` with
closure root `0046f8dde1a242beb95e2c587c4f7d96e54ee20b17d2b33ae9d9415bddc271dc`.
Recheck reports five Csc commands, 2,218 rows / 86,594,404 bytes, but
`ownerApprovedRoots: Missing` and `coreImport: NotRun`. A separate primary
GeneratorDriver witness matched 50 generated texts after BOM removal while
leaving production execution, trusted admission, and full PE/PDB replay
unproven. This does not authorize a producer-to-admission claim. The next
dependency is Core-owned root selectors and independent import of the exact
captured V2 closure; after that, the replay adapter must reject every command
semantic not preserved by its worker profile. CW-INT-03/06/14 remain open.


Update 2026-11-23 — Core completed an independent raw-byte import of the exact
retained Csc capture. Its disposition is `UnqualifiedByteInventory`, pinned to
index `6cb4dc2e4fb15cebb8a3a629ec6521c5535423c9970ea88fe34f7fccdfda6f4e` and
capture root `0046f8dde1a242beb95e2c587c4f7d96e54ee20b17d2b33ae9d9415bddc271dc`.
It covered five Csc commands, 2,218 rows, 2,241 chunks and 86,594,404 bytes,
including exact argv-to-row path/multiplicity binding for 2,153 file operands
and refusal of unknown switches; five negative cases refused, including an argv-to-row mismatch. Receipt SHA-256
`ff84e8c7f41f5a613e2f6d6cd911d10361b5db87683e7c2b8773c3370d5d6dba` is in
Core `artifacts/processor-20261114/csc-capture-core-import-20261123/receipt.json`.
This import does not convert capture rows to the separate Core staged-source
V2 contract and supplies no approved Processor roots. Production execution,
source admission, hardware/RTL and trusted handoff remain open.


### 2026-11-23 — Processor selector candidate audit

Core retained a configuration-bound `ScalarAluOps.AddWord` candidate record (artifact `CircuitWeave-main/artifacts/processor-20261114/processor-root-candidate-review-20261123/candidate-review.json`, SHA-256 `9e44adb21e129de02cfda57242ae84043e3973615560a9f0c2b6a17f4a9891ab`). The candidate is pinned to Golden Debug PE/PDB hashes and token `0x06001837`; a separate Release PE has token `0x06001834`. At the time of this 2026-11-23 note, the full-replay PE/PDB token had not yet been revalidated; see the 2026-11-24 correction below. The record is `CandidateOnly`, not owner approved, and does not supply complete Processor scope. Gate remains open until owner selects one PE/PDB pair and approves complete exact selectors/source scope; transport and staged-reader candidate import were not yet run at the time of this note; see the 2026-11-24 correction below. Owner-approved source admission remains NotAdmitted. Integration remains INCOMPLETE.

### 2026-11-24 — Raw capture to Core staged V2 transport

Core now has `HardwareBridgeRawCaptureStagerV2`, an explicit adapter between the raw `hybridcpu.hardwarebridge.csc-closure-index/v2` byte importer and the distinct `circuitweave.bridge-source-closure-index/v2` reader. It first reimports raw pins, then copies and rehashes every row/chunk into a fresh sibling workspace, emits a deterministic Core V2 index and a separate raw-path crosswalk, independently verifies the staged closure through both Core reader layers, and atomically publishes only to an absent destination. The exact capture passed ten positive/negative fixture checks on Windows and Ubuntu 26.04.1 WSL2 with identical staged index, closure and map hashes. Receipt: `../../../../../CircuitWeave-main/artifacts/processor-20261114/csc-capture-core-stage-20261124/receipt.json` (SHA-256 `8d7fa612fc1ce0e88b86c152c964f1c7686719828713696295535c1dbb4f10ad`).

The output remains `CandidateOnly` / `NotAdmitted`: test roots are mechanics inputs, not owner approval, and the stage receipt itself does not establish PE/PDB identity; subsequent PEReader candidate review v2 now binds AddWord to the full-replay pair, but does not establish owner approval or complete Processor scope. It does not create the unsupported Roslyn Csc profile or run CWR3 composition; analyzers/generators/generated-source execution still needs a separate isolated profile. It also does not establish an atomic snapshot of mutable raw inputs, production Csc read/execution, trusted handoff, HWIR/RTL, simulation, or Processor equivalence. Integration remains INCOMPLETE.


### 2026-11-24 — Full-replay AddWord selector identity

A Core PEReader probe now resolves `ScalarAluOps.AddWord` in the byte-identical pinned full-replay PE/PDB pair as MethodDef `0x06001834` (PE `1ed303674a94b65276ae56e57d877647d7e28400ac8c9b4d7ea6a4371d7b2859`, PDB `164f6a911f94715ead588e843c6fcb2a6c77ebc5ae920fbedafa32fb80af718a`; MVID `ff458091-35ad-4db1-8458-a00fa367ffce`). This resolves the prior note that the replay token had not yet been checked. Candidate review v2: [Core artifact](../../../../../CircuitWeave-main/artifacts/processor-20261114/processor-root-candidate-review-20261124/candidate-review.json), SHA-256 `7701ac986c0f2d4613befa3d11c02667592d1fe8a9ac86f88d8bcf0dbf073b1e`.

The result pins one scalar method only. It is not owner approval or a complete Processor root manifest. Source admission and Processor integration remain INCOMPLETE.


### 2026-11-24 — Core staged Csc closure profile

Core now emits and independently audits `CscClosureProfileV1` from the pinned raw Bridge capture and the staged Core V2 index. The sidecar covers all five commands, 2,218 raw input rows, 2,153 argv operands and 50 generated-source rows. Windows and Ubuntu WSL2 produced identical index/map/profile hashes. Receipt: [Core artifact](../../../../../CircuitWeave-main/artifacts/processor-20261114/csc-capture-core-stage-20261124/receipt-profile-v2.json), SHA-256 `507902222f749b250e651cad7c01b0be7cecd0d829693768a8d067a5d9bc1da1`.

This confirms a candidate closure crosswalk only; it does not attest original Csc reads/execution, source ownership, or admission. Roslyn re-emission profile V2 still rejects analyzers/generators/generated sources, and no CWR3 composition or trusted handoff is claimed. Integration remains INCOMPLETE.

## 2026-11-24 downstream source join

Core independently staged all raw Csc capture rows and verified the Csc closure profile on Windows and Ubuntu WSL2. A downstream join recheck found the AddWord candidate source SHA does not match the captured ScalarAluOps.cs bytes; details are recorded in the Core mismatch receipt. Root admission remains refused until the exact source bytes are reconciled and the owner approves complete Processor roots. This does not change the raw capture or staged transport result.

### Correction: AddWord source join (2026-11-24)

The initial raw-hash mismatch is explained by 543 CRLF line endings in captured Windows Csc source. After CRLF-to-LF normalization, the source matches the pinned Golden source exactly; see corrected Core receipt CircuitWeave-main/artifacts/processor-20261114/processor-root-source-capture-join-20261124/receipt-v2.json (SHA-256 `afa4c3e4565fb91330fe3fd1a83c8e7474bd76e9fe5f87eacfca8b807221379f). This corrects the earlier mismatch wording. It does not grant root approval or source admission.



### Core generated-source order audit — 2026-11-23

The staged Core V2 contract now keeps `generator-source` rows distinct from Csc `source` operands. A separately pinned Core audit joins all 50 captured generated rows to GeneratorDriver output ordinals using full syntax-tree paths/hints, verifies captured bytes and BOM-aware SourceText hashes, and compares all 1,154 document paths in the actual pinned primary PDB; the 50 generated paths match driver order. Four fail-closed mutations (wrong PDB pin, row reorder, altered byte hash, missing row) were refused. The CWR3 synthetic fixture also confirms a generated-source row with a valid sidecar is rejected at the explicit guard before stage publication. The fixture passed 13 checks on Windows and Ubuntu 26.04.1 WSL2 with identical staged hashes. Core CLI Release build: 0 errors, 2,865 existing CS1591 warnings. [Core receipt](C:/Users/Yuriy/Desktop/CircuitWeave-main/artifacts/processor-20261114/golden-generated-order-audit-20261123/receipt.json) (SHA-256 `bdd339ab27240b4383e1a23c078e81b899f8f09c668795516e576112315ee468`); [Core evidence matrix](C:/Users/Yuriy/Desktop/CircuitWeave-main/artifacts/processor-20261114/integration-20261121/integration-matrix.json) (SHA-256 `f3017c3e9c4f4abeb0b60533e1d24eb5a2744885b5e5d6b5578803b770e41023`).

This is a byte/order/PDB-path consistency audit only. It does not establish production GeneratorDriver execution, provenance, source admission, PE/PDB CWR3 composition, trusted handoff, owner-approved Processor roots, HWIR/RTL/simulation, or Processor equivalence. Integration remains INCOMPLETE.


### HB-INT-01 current portable/pin recheck — 2026-11-23

Current Bridge bytes preserve portable HWIR as source-snapshot-only with no default ProjectReference. Fresh .NET 11.0.100 Release default build passed with zero warnings/errors; `PortableDependencyBoundary.Tests.py` and `HWIR/Tests/test_parity_guard.py` passed. Empty and mismatched SDK pins refuse with `HBINT0104`; missing Core root/frontend refuses with `HBINT0101`. Positive pinned opt-in is carried by the existing current-Core build receipt: the clean Core clone is HEAD `9d0b4b9f50ab30bba3bea5eb2d59a4509cf0f435`, and the current project/pin-test bytes match the recorded Bridge input inventory. [Recheck receipt/logs](C:/Users/Yuriy/Desktop/CircuitWeave-main/artifacts/processor-20261114/bridge-portable-gate-20261123/receipt.json), SHA-256 `7dc52b9f4b8abd439fe46de27bf2e860b641dda62be126847b85f6e0c65c9299`.

This closes local portable/pinned build mechanics only. Hosted CI, protected branch/trusted handoff, Golden execution, CWR3 complete Processor scope, RTL/simulation, and Processor equivalence remain open.


### Supplemental PE/PDB candidate closure — 2026-11-23

A new Core V2 candidate workspace now appends the exact recorded isolated-capture PE/PDB bytes to the 2,218-row staged input package as `compiler-pe` and `compiler-pdb`, preserving the original raw capture and old immutable stage. Core independently imported and audited the resulting 2,220 rows, output-role/profile joins, 50 generated-source entries, and all 1,154 actual PDB document paths; four malformed order/pin mutations were refused. Index SHA-256 `1c12a236664a283486cfd550c2d82b254e8b69bb9897fad17df8a04faabea4b2`, closure `cdd75ea5d434649976599d85323ada86bfe99899fa6cade5fc8b46ee9581b7c1`, closure profile `3af238e6fa6c9b2ed2d975c8f92cf596d21bf2890aa59aba1b93917be8a5ec02`. [Receipt and retained pair](../../artifacts/processor-20261114/csc-output-pair-stage-20261123/receipt-v1/receipt.json), SHA-256 `368d5538245a540b08a2fed4299149d86274641bba85901c62b3e7cdec639da3`.

This closes candidate output-byte availability only. Producer execution/provenance remain unestablished, the recorded independent Roslyn replay remains `UnqualifiedReplayMismatch`, and generated-source CWR3 composition, trusted handoff, owner-approved roots and all hardware/Processor verification remain open.


### Follow-up: staged Core V2 independent GeneratorDriver-order replay — 2026-11-23

The replay was repeated from the output-inclusive staged V2 closure, with 50 generated rows ordered only by the separately captured GeneratorDriver ordinals and generated document paths rooted at the separately observed driver output location. It emitted successfully with 0 parser diagnostics; its 1,154 PDB document paths and order match the captured Golden PDB, and its PDB SHA-256 `1319ba65368953d0d82c248de87dd21f9e1a8fda5d1ba8d069ea785d639475b6` matches the earlier independent-order replay. The PE does **not** match that earlier replay: `72ae3e4d9415e2707fd24707a10a5904642ab5848ed38b87adb0c468357dc7e2` vs `861292cb9d8d61aa977cd9f57cc30f344d979a87484c6ba1b55b083a9721730b`; neither matches captured Golden PE `1ed303674a94b65276ae56e57d877647d7e28400ac8c9b4d7ea6a4371d7b2859`. This inconsistent PE result despite an identical replay PDB is unresolved and supersedes the earlier statement that both PE and PDB matched the separate diagnostic replay. No Win32 resource was supplied. [Receipt, harness and output](../../../../../CircuitWeave-main/artifacts/processor-20261114/golden-driver-order-replay-20261123/receipt.json); receipt SHA-256 `1e0833a810686397dce10cbfb67fbd47d5b5b4879aeb7694580b3f6cba0cc39e`.

Follow-up: compare the exact effective staged inputs and emit options between the two independent-order runs, then bind task-time VERSIONINFO/native-resource bytes to observed MSBuild/Csc execution. This remains a diagnostic ordering/path consistency result only; execution, provenance, PE/PDB equivalence, Core admission, trusted handoff and hardware verification remain open. Integration remains INCOMPLETE.


### Replay differential follow-up — 2026-11-23

A row-by-row audit compared the earlier role-separated Core profile (SHA-256 `a8f23695…`) with output-inclusive V2 (`3af238e6…`): all 1,352 non-output file keys, byte counts and SHA-256 values match; all 1,323 primary Csc argv tokens match (`e9efe337…`), as do working directory, generator output directory, compiler (`8b7a5476…`), project and assets pins. The previous replay harness (`d90667ea…`) was rerun against reconstructed indexes over both old and new staged chunks; both emitted PE `72ae3e4d…` and PDB `1319ba65…`. A second run of the new harness produced the same pair. Thus the earlier recorded PE `861292cb…` has not been reproduced from either retained staged closure, even though the PDB hash and document order are stable. Its original exact replay input bundle is not retained alongside the earlier receipt, so this does not prove which historical input varied.

A PEReader comparison of the two replay PEs found 2,725 types and 30,315 method definitions; method keys, signatures, IL bodies and method flags all match. Their MVIDs differ (`58183e89-e93d-4313-80c5-aa2fbfddd015` vs `32f04379-70b1-4da0-9a0f-7f5e4cabe391`), and metadata-level PE bytes remain different. This narrows the discrepancy to PE identity/metadata outside the compared method bodies but does not establish equivalence or explain the prior artifact. [Follow-up receipt and PE diff tool](../../../../../CircuitWeave-main/artifacts/processor-20261114/golden-driver-order-replay-20261123/receipt-v2.json); receipt SHA-256 `57cd30a24d3ac3d045be7a9f5e37e76f0d73a083d77dbb87f185400e39a4e27a`.

Next owner/action condition: retain and hash the exact replay input index/bundle, Roslyn/runtime binaries and harness for every replay; reproduce the `861292cb…` result or supersede it with a fully pinned deterministic replay; then bind task-time resource production and rerun independent Core verification. No `Verified`, admission, execution/provenance or hardware-equivalence claim is made. Integration remains INCOMPLETE.


### PE metadata differential — 2026-11-23

The PE comparison narrowed the replay mismatch: the prior and current replay images have 2,725 types and 30,315 method definitions, with no missing methods, signature differences, IL-body differences or method-flag differences. All 35,147 custom-attribute semantic keys match. Metadata heaps `#Strings`, `#US` and `#Blob` are byte-identical; differences are 38 bytes in `#~`, 16 bytes in `#GUID` (the MVID), plus `.text`/`.reloc` section bytes. Therefore method-body parity is stronger than a smoke result, but it does not explain the PE identity/table delta, reproduce the earlier PE, or satisfy full PE/PDB equivalence. The comparison source and repeatability/input audit are retained in [receipt v2](../../../../../CircuitWeave-main/artifacts/processor-20261114/golden-driver-order-replay-20261123/receipt-v2.json) (SHA-256 `1dac2e8701c21e8c25b4b424827227843bad8ddec64344f83bf9ebcb1912ebee`).

Next: identify the affected `#~` table/rows and map changed metadata tokens to their owning symbols, then reproduce or supersede the older PE using a fully retained input package and pinned runtime. Continue task-time VERSIONINFO binding and independent Core admission afterward. Integration remains INCOMPLETE.


### FieldRVA layout follow-up — 2026-11-23

The 38 changed `#~` bytes were mapped to all 33 `FieldRVA` rows. Every row points to the same FieldDef token in both replay PEs, every RVA moves by exactly `−40`, and all owners are in `<PrivateImplementationDetails>`. Method RVAs remain identical. The corresponding 536-byte static-data window is byte-identical after applying that relocation (SHA-256 `d0c81f92…`). This explains the table and relocation-section differences as an address-layout delta, but not what caused the 40-byte placement shift or the different MVID. [Field map and receipt v3](../../../../../CircuitWeave-main/artifacts/processor-20261114/golden-driver-order-replay-20261123/receipt-v3.json); receipt SHA-256 `dfb4586473a2b7fa3b6e73dc2f6cf1ef6390738c13d371c694d72a2eba89923a`.

Next: identify which emit/layout input or Roslyn writer decision shifts the `<PrivateImplementationDetails>` data base by 40 bytes while source/argv/profile hashes remain fixed; reproduce the prior PE under a retained exact runtime and harness. Full PE/PDB identity and trusted Core admission remain open; integration remains INCOMPLETE.
