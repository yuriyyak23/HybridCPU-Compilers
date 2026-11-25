# HardwareBridge → CircuitWeave: план рефакторинга интеграции полного compiler pipeline

**Status (2026-11-21):** R0–R3 merged, production handoff blocked. Текущие доказательства и открытые задачи см. в [аудите и TODO](HardwareBridge_Modernization_Audit_20261121.md).

**Baseline:** `HybridCPU-Compilers/master@c66d2ec2bb8a06e29ced1d884c98a055054b4a26` и `CircuitWeave/master@58a7c44c3353a9c95108192224b5ea190f96464b` на 2026-11-19.  
**Core plan:** [16-package integration roadmap](https://github.com/yuriyyak23/CircuitWeave/blob/master/docs/roadmap/circuitweave/11_Compiler_Pipeline_Automation_Master_Plan.md) и [execution package specs](https://github.com/yuriyyak23/CircuitWeave/blob/master/docs/roadmap/circuitweave/12_CW_INT_Execution_Packages.md).

**Runtime direction (2026-11-24):** CircuitWeave is a Windows application with .NET/C# as its primary runtime and implementation language. Do not prioritize Python, Java, or Linux as application runtimes; use only narrowly required wrappers/support tooling, with native C# preferred for Bridge capture and interchange. Linux-only checks are supporting evidence and do not satisfy Windows acceptance. Core's current `CircuitWeave.slnx` excludes Linux sandbox tooling, retained separately in `CircuitWeave.LinuxTooling.slnx`; its CI job is manual opt-in and skipped by default. This changes build grouping, not the still-open Windows replay/trust gates.

**Core replay profile update (2026-11-25):** Core's `/pathmap` profile blocker is now represented as separately pinned path identity. A strict synthetic Windows `Derive → Audit → Replay` fixture passed, and the pinned Golden V4 transport profile maps to a non-authorizing candidate request. No pinned Golden Csc replay or Windows isolation has been performed; admission remains closed. Details and log hashes are in the Core backlog key `cscPathMapReplayProfile20261125` and the audit TODO.

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


### Section-layout cross-check — 2026-11-23

The saved PE section-table audit (`python section-layout-audit.py`) confirms: Golden and the Roslyn-default VERSIONINFO replay have `.text` virtual size 5,498,344, `.rsrc` at RVA `0x542000`, and `.reloc` at `0x544000`; the captured resource replay’s `.rsrc` matches Golden, but its complete PE is still not identical. The prior no-resource replay also had `.text` virtual size 5,498,344 and FieldRVA locations matching Golden. The current no-resource replay has no `.rsrc`, `.text` virtual size 5,498,304, and all 33 private static-field RVAs are 40 bytes lower. Both no-resource PEs lack `.rsrc`, so resource presence does not explain their relative 40-byte delta. The older PE `861292cb…` remains unreproducible from the retained old/new staged chunks despite identical indexed file facts and Csc argv; no cause is assigned. [Section table, resource receipt and replay receipt v4](../../../../../CircuitWeave-main/artifacts/processor-20261114/golden-driver-order-replay-20261123/receipt-v4.json); receipt SHA-256 `62da710aecb487cad297937aa2a0a52e1b7c3f20a6360a5b928b438904be8adb`.

Next: recover or recreate the exact historical replay materialization/runtime/harness; run both variants from one immutable package while varying one factor at a time. Obtain resource bytes only from observed MSBuild/Csc task execution for final qualification. Integration remains INCOMPLETE.

### Core CWR3 regression recheck — 2026-11-23

Core HEAD `23c01bf5baeb1377a1317e2523a25f96713a1c75`, SDK `11.0.100`: the targeted CWR3 fixture project built and ran with exit 0. Receipt `CircuitWeave-main/artifacts/processor-20261114/cwr3-recheck-20261123/receipt.json` (SHA-256 `53475bdb5bff3cc2795bc14a8114a179f9f21f97f1260d3678d96159d329b546`). Generated-source import remains explicitly refused before stage publication; V2 still excludes generators and generated source. This synthetic result does not qualify producer execution/provenance, trusted handoff, RTL or Processor behavior. Next condition: a separately versioned, bounded Core replay profile must independently bind generator/toolchain inputs, derive generated bytes/order without trusting Bridge assertions, and require exact PE/PDB re-emission before CWR3 composition. Hosted protected-branch trust and hardware verification remain open.



### Current portable/pinned build recheck — 2026-11-23

Current Bridge HEAD `2be1784125193911ea635cb4fce0661b0b4a4bda` matched live `origin/master`. On SDK `11.0.100`, the portable HWIR Release build passed with zero warnings/errors and zero evaluated ProjectReference items; boundary checks passed 8/8 and parity guard passed 10 with one symlink test skipped for unavailable Windows privilege. Explicit pinned opt-in against a clean Core worktree at HEAD `23c01bf5baeb1377a1317e2523a25f96713a1c75` passed with zero errors (1,064 XML documentation warnings); an incorrect SDK pin refused as `HBINT0104`. Receipt: `CircuitWeave-main/artifacts/processor-20261114/bridge-current-20261123/receipt.json`, SHA-256 `6afd19e3b2a57d51ceb82ab1433247934998024e0a337cba00320ebcfdc5c9a8`.

Core live `origin/master` was not confirmed by the current query, branch protection and hosted CI were not checked. This does not prove trusted handoff, Golden execution, admission, RTL or Processor verification. No Golden bytes were used; integration remains INCOMPLETE.

### V3 profile/output closure follow-up — 2026-11-23

The response audit confirms the primary Csc command has 1,323 tokens and `/refout` at ordinal 197. A 2,333,696-byte refout image with SHA-256 `561c94bd965e725327f154931a5536f08af1defae24c3635c8d03f5141c6382a` is now retained at `CircuitWeave-main/artifacts/processor-20261114/csc-refout-candidate-20261123/HybridCPU_ISE.ref.dll` (receipt SHA `e1716be71c8ea8fdaa85a1a938c46508d076a6efd31f9b6dd622f5ab68b0cced`). Its parser audit matches captured path/bytes but does not attest original Csc emission or provenance. The existing output-pair Core V2 package contains compiler PE/PDB but not refout; the current raw-to-staged package has no compiler outputs. Replay resource bytes are output-derived diagnostic input, not task-bound capture. Keep generated-source CWR3 refusal until a separately versioned full output replay contract binds source/generated ordering, analyzers/config/embedded inputs, complete argv/runtime, resource, refout, PE/PDB and exact comparison. Hosted trust, admission and hardware verification remain open.

### Hosted gates on current Bridge HEAD — 2026-11-23

Live GitHub Actions metadata on Bridge `2be1784125193911ea635cb4fce0661b0b4a4bda` shows: `45152898617` HB-INT-02 fixture run success, not executed Csc; `45152898606` HB-INT-06 fixture refusals success, with paired-Core, dispatch-policy and Golden-approved jobs skipped; `45152898604` pinned-Core QA failure, with preflight failed before private Core checkout; `45152898576` R0 Companion production graph/build failure. Snapshot [runs.json](CircuitWeave-main/artifacts/processor-20261114/hosted-ci-current-20261123/runs.json), SHA-256 `6ba51be3c70f32462372cafd776f73a134328e3c5540bd0a95e99d0eff6c582a`.

Failed job logs return HTTP 403 and receipt artifacts return HTTP 401, so exact failure causes are not established. Branch protection also returns HTTP 401; no protected-ref evidence. Core Actions API returns HTTP 404. Do not report a positive trusted receipt from these runs. Hosted Core pairing, cross-repo read secret/dispatch, R0 contracts, independent admission, and hardware proof remain open.

### R0 clean-checkout contract recheck — 2026-11-23

The parent Bridge checkout is `2be1784125193911ea635cb4fce0661b0b4a4bda`; an isolated clean worktree at the same commit contains no tracked `Core/IR/Artifacts` files. With .NET SDK `11.0.100`, `HardwareBridge.Companion.csproj` Debug and Release builds each exit 1 with 20 unique compiler errors, including CS0234 for the missing `HybridCPU.Compiler.Core.IR.Artifacts` namespace and CS0246 for `CompilerEmissionPackage`, `CompilerEvidenceEnvelope`, `CompilerSidebandEnvelope`, `DescriptorAbiStatus`, `DescriptorEnvelope`, `SidebandRequirement`, and `TypedSlotFactsEnvelope`. Logs and command provenance are in `CircuitWeave-main/artifacts/processor-20261114/r0-clean-2be178-20261123/`; receipt SHA-256 `99045b856a28c55f80a2b1beab839166c1dac9d997463343337f6a495c453ae1`.

The original local ignored `CompilerEmissionPackage.cs` remains outside the permitted change subtree and hashes to `fcb28bbbfee6b6811c5d03b852407f6d7fcaa6fd9fe630685429be8e4b66c09a`; it is absent from the clean checkout. This establishes that the local passing build is not reproducible from tracked Bridge commit bytes. Owner: HybridCPU compiler/Core IR artifact-contract owner. Closure condition: publish the authoritative complete transitive declarations as tracked compiler-owned source, establish their compatibility with the Companion production graph, then pass clean-checkout Debug/Release and current Bridge workflow pinned against declared Core/SDK. We must not fabricate or copy the ignored declaration into HardwareBridge. R0 remains blocked; integration remains INCOMPLETE.

### 2026-11-23 — Core V3 captured-output linkage and Bridge AddWord opt-in graph

Core adds a separately versioned `CscCapturedGeneratedOutputProfileV3` linkage preflight. It preserves primary Csc source argv order independently from captured GeneratorDriver output order and requires PE, PDB, refout and task-bound resource rows. The retained Golden package joins 50 generated rows to 50 PDB generated documents, while V3 correctly refuses because required compiler outputs are missing. Targeted audit: 8 checks passed on SDK `11.0.100`; receipt `artifacts/processor-20261114/csc-v3-linkage-20261124/receipt.json`, SHA-256 `4ebdda7d616a009b8f129e15fc061ed0c44536b8499073ac5edd8f639cc9d01a`. This does not wire V3 into CLI, replay Csc independently or establish execution/provenance/admission. Next Core slice: separately versioned transport/import of task-bound output roles and exact independent re-emission before composition.

The Bridge opt-in HWIR graph now references Core-owned `CircuitWeave.HWIR` alongside Core Roslyn, under the existing explicit pinned opt-in only. A clean-Core pinned local Bridge opt-in build passed with zero warnings/errors; portable default still evaluates zero ProjectReferences; boundary tests passed 8/8 and parity guard 10/10 with one Windows symlink-privilege skip. The `golden-addword-hwir` workflow and smoke project now pass Core root/frontend/HEAD/SDK pins and the explicit fixture opt-in through restore/build. Receipt `artifacts/processor-20261114/golden-addword-workflow-recheck-20261123/receipt.json`, SHA-256 `922fb40c08251b66032f0ebd377a95e779e0630a7ee11fc66cae502d03e1f7d8`.

The full AddWord workflow has not passed on hosted CI after this change. A local clean Windows restore was blocked because nested MSBuild `Exec git` returned code 9009; no 8,292-vector result is claimed for the updated workflow. AddWord remains an unapproved single-method candidate (`approved:false`); native RTL/simulation/Processor equivalence remain unproven. Integration remains INCOMPLETE.

### Independent parent rerun — V3 linkage audit

The parent independently rebuilt `CircuitWeave.CscGeneratedOrderAudit` on Core HEAD `23c01bf5baeb1377a1317e2523a25f96713a1c75` with SDK `11.0.100` (0 warnings, 0 errors) and reran the retained pinned Golden input join (8 checks; 50 generated rows and 50 generated PDB documents). V3 refused the current partial package for missing PE/PDB/refout/task-bound resource output rows. [Independent rerun receipt](../../artifacts/processor-20261114/csc-v3-linkage-independent-20261123/receipt-parent-rerun.json), SHA-256 `e512c508ff4becb9e1d250a7dbb4aee444a3c19a7ba0d20c41c03b9c81c6796d`. This remains a linkage preflight, not a Csc replay or admission result.

### Golden MSBuild task execution and resource-role correction — 2026-11-23

See Core receipt `CircuitWeave-main/artifacts/processor-20261114/golden-pinned-task-execution-20261123/receipt-pinned-task-execution.json` (SHA-256 `efaa5a4da293a7f93b8a8df71dd4dd8225dd98d3e55f0fa89cefc88e5ecd42c3`). The pinned Golden source commit was built in an isolated clean checkout, but installed SDK `11.0.100` differed from `global.json`'s `11.0.100-rc.2.26504.105`. Primary Csc produced PE/PDB/refout and had no `/win32res` or `/resource`; no separate resource output was observed. Thus the earlier V3 note requiring a separate resource row unconditionally is overstrict. The V3 transport contract should encode observed resource presence and only require bytes for an actually emitted resource. This build's PE/PDB do not match the retained historical pair and it does not prove independent admission or trusted handoff. Integration remains INCOMPLETE.

### V3 output supplement — 2026-11-23

A Core-owned bounded V3 importer now supplements a V2 input closure with refout plus an explicit resource state and structural task-output witness. The importer and 18 focused synthetic checks passed; see Core receipt `CircuitWeave-main/artifacts/processor-20261114/csc-v3-output-supplement-20261123/receipt.json` (SHA-256 `bf5210a31b6cdcd9131f23f67b4d837430c26b84541a8c7fa0e2a540817022be`). This is transport only. The witness is not authenticated and remains `TaskWitness=NotEstablished`; the importer is not composed with CWR3, does not replay Csc, and returns `Admission=NotAdmitted`. Integration remains incomplete.

### Exact pinned SDK task run — 2026-11-23

The exact SDK `11.0.100-rc.2.26504.105` from Golden `global.json` was installed only in TEMP. On clean tracked tree `243a67c8ef462c1da2d1f06df9bee2c31508cb40`, Release restore/build passed (45 warnings, zero errors). The main Csc task emitted PE/PDB/refout and no separate resource output. Refout matches the previously response-audited candidate (`561c94bd...`); PE/PDB still differ from the retained historical pair. The cause is not established, so prior SDK variance is not a sufficient explanation. See receipt `CircuitWeave-main/artifacts/processor-20261114/golden-pinned-task-execution-20261123/receipt-pinned-sdk-rc2.json`, SHA-256 `e0d7b536a8d06471505fed749d9e90cd1f33898d7b6921ddb8f5f11a29283d81`. Preserve all three output hashes as distinct observations; no independent admission or hardware qualification is established.

### V3 linkage profile contract correction — 2026-11-23

The Core V3 linkage profile now treats separate resource output as conditional on an explicit resource state. PE, PDB and refout remain required; `CapturedTaskOutput` requires the resource row, and `NotPresent` rejects it. The pinned capture's primary Csc invocation had no resource switches. Focused audit: 8 checks passed; 50 generated source rows joined to 50 PDB documents, while the incomplete output set correctly refused. Receipt `CircuitWeave-main/artifacts/processor-20261114/csc-v3-linkage-contract-update-20261123/receipt.json`, SHA-256 `a717fbd7d8ebf28030ad534e2153c986f7270c1f219eded7cc2744dd41613934`. The profile remains candidate linkage only.

### Pinned-SDK PE/PDB comparison detail — 2026-11-23

Under exact SDK `11.0.100-rc.2.26504.105`, the emitted PE's method-level comparison matches the retained historical PE for 30,561 methods (zero body, signature, or method-flag differences within comparator scope). PDB path analysis: 1,154 documents each, 1,104 common relative source paths with identical checksums, and 50 generated docs each with matching checksums across sets but different generated roots. Absolute document paths differ throughout. This is evidence consistent with path-root variation; it does not establish full PE/PDB equivalence or a sole cause. Receipt `CircuitWeave-main/artifacts/processor-20261114/golden-output-semantic-compare-rc2-20261123/receipt.json`, SHA-256 `236f8362e08c8c5a2a2e21b66d1d282a91056f0dcf3a9867c6564a5ed9ba2069`.

### AddWord local exact-blob smoke — 2026-11-23

The focused local smoke now passes using the exact Git blob bytes from pinned Golden commit `6946ccab9d888f7dae0a4d8e18480f436138acd9`: restore/build succeeded on SDK `11.0.100`, `.deps.json` included Frontend Roslyn, and 8,292 C# oracle/HWIR vectors per operation passed for AddWord, SubWord and MultiplyWord. Local Windows checkout bytes had CRLF and were not substituted for the pinned LF blob. Receipt `CircuitWeave-main/artifacts/processor-20261114/addword-deps-repro-20261123/receipt-addword-local.json`, SHA-256 `b5e772383242f09166b987c510d71d2d585995b47a896f788f5b4d109f21fc01`. This was a local run against dirty Bridge working bytes; hosted CI, approved scope, native RTL, simulation, and independent admission are still unproven.

### V3 transport CLI and direct Csc replay — 2026-11-23

Core's CWR3 CLI now accepts the V3 output supplement pins and runs V2/V3 import and linkage preflight, then deliberately returns CandidateOnly before CWR3 coordinator/session execution because independent Csc re-emission is not established. The Core CLI Release build passed (0 warnings/errors), and V3 importer tests passed 18 checks. The partial-option probe was rejected by the earlier global pin guard (exit 2); it did not exercise the V3-specific all-or-none diagnostic. Core receipt: `CircuitWeave-main/artifacts/processor-20261114/cwr3-v3-cli-transport-20261123/receipt.json`, SHA-256 `64f11adf81dfc78bbc5ce3665d416e664d1d0394102a905b2687067ebe0e5b95`.

Direct replay of the observed primary Csc argv with the exact pinned compiler produced a refout matching the exact-SDK MSBuild output, while PE and PDB hashes differed. The mismatch cause remains unestablished; the result does not qualify provenance or admission. Integration remains incomplete.

Guard diagnostic follow-up (2026-11-23): partial V3 option sets are checked before general required pins. The CLI now reports the V3 all-or-none refusal and exits 2; rebuild had 21 existing warnings and 0 errors. Updated receipt SHA-256: `a62172b39c2cec2dfd645b8b47e83194dc5ba11ae06b8ec5333d36fe9e3b2abf`; matrix SHA-256: `3f45be0ccf131a06a3e6906b57657a03a901e966400a5b11275b6bab613cced5`.

### AddWord runtime repeat — 2026-11-23

Rechecked the smoke from clean Core worktree `23c01bf5baeb1377a1317e2523a25f96713a1c75` with exact pinned Golden Git blob bytes. Runtime `.deps.json` includes Frontend Roslyn and smoke passes 8,292 vectors per operation. Receipt `CircuitWeave-main/artifacts/processor-20261114/addword-deps-repro-20261123/receipt-repeat-clean-core.json`, SHA-256 `0796a82fe3a46409544afe0a152665d286142be4397aa1c8ba0caeebf0dd48f7`. This addresses only the local dependency reproduction. Hosted CI/pinned Bridge publication, approved scope, native RTL, and independent admission remain unverified.

### Core V3 cross-import linkage fixture — 2026-11-23

Core independently exercised the V2 base + V3 output supplement + Csc closure/generated-order profile join. One positive synthetic join and four cross-binding negative cases passed; the result remains CandidateOnly with independent replay NotRun and admission NotAdmitted. Core receipt `CircuitWeave-main/artifacts/processor-20261114/cwr3-v3-cross-import-linkage-20261123/receipt.json`, SHA-256 `fb4c7cd16aa4cae733beb9c4514a9bdda2a46ec0fdb037f1d41fb066cb91c0f6`. This validates transport linkage only; it does not attest task execution, trust, CWR3 stage publication, or hardware behavior. Integration remains incomplete.

### Portable boundary rerun after Core.HWIR opt-in reference — 2026-11-23

Against dirty local Bridge HEAD `2be1784125193911ea635cb4fce0661b0b4a4bda`, `python Tests/PortableDependencyBoundary.Tests.py -v` passed 8/8. Portable Release build with `EnableBridgeHwirLegacyFixtures=false` passed with 0 warnings/errors. Pinned opt-in Release build against clean Core worktree `23c01bf5baeb1377a1317e2523a25f96713a1c75` and SDK `11.0.100` passed with 0 warnings/errors after explicitly adding Git to the process PATH; the first invocation failed because MSBuild Exec could not resolve Git (9009). Receipt `CircuitWeave-main/artifacts/processor-20261114/bridge-portable-optin-recheck-20261123/receipt.json`, SHA-256 `78719b313217e81145bbcdb3aaf08871d86647032fa10ce65970d2b4000ac362`. This is local evidence against dirty Bridge bytes; hosted CI and publication of the source change are still pending, and it does not establish R0 contracts or independent hardware acceptance.

### Core V3 CLI synthetic regression — 2026-11-23

The Core test-only CLI-handler fixture passed a complete synthetic V3 transport path and wrong-witness-pin refusal. Both stop before CWR3 stage/session creation; authority remains CandidateOnly/NotAdmitted. Receipt `CircuitWeave-main/artifacts/processor-20261114/cwr3-v3-cli-integration-20261123/receipt.json`, SHA-256 `d9780ddbf486019c014930e082903680e2a047345db9ec16927debdef784e575`. It calls the public handler in-process and uses a synthetic emitted PE/PDB; producer provenance, independent replay, trusted handoff and hardware acceptance remain unproven.

### 2026-11-23 — corrected Csc re-emission inputs; raw response bytes remain missing

The prior Core diagnostic replay incorrectly added all 50 generator outputs to `EmbeddedText`. A corrected replay embeds only the three task-captured `embedded` rows, quotes the comma-bearing `/embed` token before Roslyn command-line parsing, and repeats deterministically. It emits 1,104 source trees + 50 generated trees against 180 references; all 1,154 PDB document paths/order match the independent generated-order manifest and captured PDB, but byte hashes still differ (captured PE/PDB `1ed30367…7b2859` / `164f6a91…f718a`; replay `9b6b8396…3d9c19` / `46bca6d9…efab8d`). No resource was supplied and analyzers were not executed.

This recheck also found that the retained primary Csc argv is a `CommandLineToArgvW`/whitespace projection, not the original response-file bytes: the recorded `.rsp` is absent, although its former path, length and SHA are retained. The comma in one `/embed` path loses quoting in the token projection; parsing it unquoted yields four embed entries instead of the three task-captured rows. Quote restoration is a reconstruction from the captured row, not evidence of the original response syntax. Therefore prior “direct exact Csc argv replay” wording is narrowed to a reconstructed argument replay. Receipt `artifacts/processor-20261114/golden-driver-order-replay-embedded-inputs-only-20261123/receipt.json`, SHA-256 `e0e2639804acbf84243670d4d95c4db40dab088e6e097abf32a1136e8aea12b8`. Matrix SHA-256 `d2744371dc3c29e476d67ea5ce142d4b179faf8b888956efbd8da78c54935d20`.

Closure needs an immutable capture of the actual primary task response bytes/quoting and all effective emit/resource parameters from the pinned run, followed by bounded Core re-emission that matches PE, PDB and refout exactly. Until then, replay/admission stay NotEstablished/NotAdmitted.

### Correction — pinned capture-v2 response/replay evidence — 2026-11-23

The earlier response-file-missing replay note is superseded for the qualified local capture-v2 package. Raw task response files, launch cwd, and five Csc/task crosswalks are retained; the producer verifier revalidated the 2,218-row, 86,594,404-byte closure. Exact pinned SDK direct build comparison matched all 44 instrumented PE/PDB outputs. This is still unqualified producer evidence: original Csc input consumption, analyzer side effects, independent Core admission, trusted handoff, and hardware verification remain unproven. See the Core golden-windows-csc-capture-20261122/validation-v2.json and golden-windows-csc-cwd-observation-20261122/retry-12-direct-short-gen/comparison.json.


### Correction — pinned capture-v2 response/replay evidence — 2026-11-23

The earlier response-file-missing replay note is superseded for the qualified local capture-v2 package. Raw task response files, launch cwd, and five Csc/task crosswalks are retained; the producer verifier revalidated the 2,218-row, 86,594,404-byte closure. Exact pinned SDK direct build comparison matched all 44 instrumented PE/PDB outputs. This is still unqualified producer evidence: original Csc input consumption, analyzer side effects, independent Core admission, trusted handoff, and hardware verification remain unproven. See the Core golden-windows-csc-capture-20261122/validation-v2.json and golden-windows-csc-cwd-observation-20261122/retry-12-direct-short-gen/comparison.json.


### 2026-11-23 — V4 output-bearing supplement and Core import

An additive `circuitweave.bridge-compiler-output-supplement/v4` now carries PE/PDB/refout (and an optional explicitly observed resource) separately from the unchanged V2 input closure. Core adds a separately pinned V4 importer; its focused suite passed 25 positive/negative checks and a real staged import accepted the three output roles as `Candidate`. Raw capture pins, Core staged V2 pins, Csc profile, output index, closure, and witness are cross-bound in `CircuitWeave-main/artifacts/processor-20261114/csc-output-supplement-v4-20261123/real-import-receipt.json` (SHA-256 `90a9033dae68cbc026fbdeeb43423e9a1b24e681f486f368b8f57a06658826d8`). The bytes are from the pinned direct-build comparison and do not prove original task emission/consumption. Execution/provenance/task witness remain `NotEstablished`; admission remains `NotAdmitted`; trusted receipt, independent replay, hardware verification, hosted CI and Studio publication remain open.

### Follow-up — V4 profile linkage evidence — 2026-11-23

The combined Core V4 profile audit is now complete at candidate-only scope for the pinned V2 closure, 50 generated outputs and PE/PDB/refout. See Core `artifacts/processor-20261114/csc-output-supplement-v4-20261123/real-profile-import-receipt.json` (SHA-256 `c4e2f191b5116bd2b49b6c7b0dbab3b5952f9f35d681ac4de531aa3cf8b63c92`). Remaining gaps include independently replayed Csc outputs, attestation that the original task emitted/consumed these exact bytes, trusted receipt, hosted run and hardware qualification; all authority claims remain closed.

Current-Core restage confirmation (2026-11-24): the raw capture was staged again with Core HEAD `23c01bf5baeb1377a1317e2523a25f96713a1c75`; the V2 index/profile hashes remained identical and the 13-check stage/import harness passed. The combined profile result was repeated against this fresh staged workspace. Stage receipt: Core `artifacts/processor-20261114/csc-v4-current-core-stage-20261124/receipt.json`, SHA-256 `1c440bffadec9d3a1e4c4d981f5e8ab711fe34c4a4452ae87b24181078ad9be7`; combined receipt remains SHA-256 `c4e2f191b5116bd2b49b6c7b0dbab3b5952f9f35d681ac4de531aa3cf8b63c92`.

### 2026-11-24 — Captured response replay follow-up

Core recorded a standalone invocation of the pinned Csc with the captured primary response file. It exited 0 with 1,352 primary inputs unchanged; refout matched the direct-build baseline while PE and PDB hashes differed. The generated-source-order audit passed for 50 generated rows/PDB documents. Receipt SHA-256 `eaaeec60f37155c7213b0535176c9c2cf7a480e807a03ab5fd110f0151697dbf`; diagnostic SHA-256 `e14fed1fb2fd5589d2df47033838990926a02e4bfa4281b18659543aea88e4aa`. This does not prove original task output consumption or semantic PE equivalence. Keep replay status `ReplayOutputsDiffer`, admission `NotAdmitted`, and integration INCOMPLETE until exact task outputs/effective emit/resource parameters and independent trusted verification are established.

### 2026-11-24 — bounded PE/PDB comparison update

Core compared the standalone replay PE with direct-build baseline: 30,561/30,561 MethodDefs, no missing entries and no signature, flags or IL-body differences within that comparator's scope. PDB document identities, sequence points, scopes, variables and constants match; 219 ImportScope rows and one CustomDebugInformation row differ in row-order comparison, and PDB IDs differ. Evidence paths and hashes are in the Core execution matrix. These bounded comparisons do not establish complete artifact equivalence, original task output consumption, provenance or trusted admission. Keep integration INCOMPLETE.

### 2026-11-24 — exact RC2-host replay resolves standalone output mismatch

The earlier standalone response replay used the RC2 Csc binary under the global RTM host and produced differing PE/PDB hashes. The RC2 compiler runtimeconfig allows major roll-forward; the embedded PDB compilation-options record showed the runtime-version delta. A replay with `DOTNET_ROOT` pinned to the isolated RC2 SDK/runtime and `DOTNET_ROLL_FORWARD=Disable` exited 0, preserved 1,352 inputs, and matched all direct-build PE/PDB/refout full hashes. The 8-check generated-order audit passed for 50 rows/documents. See Core matrix and exact replay receipt SHA-256 `1829bf96303000aae0fe8b220998f156b3f1ac8189a5c91b9f19b805cfd6f473`. This remains standalone candidate evidence; original task output consumption and trusted Core admission are not established. Integration remains INCOMPLETE.

The three matching exact-RC2 replay outputs are preserved in a separate replay-produced candidate handoff, SHA-256 `dc35b26e94217badadc84fcb1af3fdb3638898ac5c0f99718a29937375eaa745`, and linked to V4's output pins and the original response/task crosswalk. This handoff is candidate data; original task output consumption and Core admission remain unproven.

Runtime selection is now supported by a hostfxr trace from the matched Csc process: exact RC2 runtime selected under the isolated `DOTNET_ROOT`, with the environment override disabling the runtimeconfig's `Major` roll-forward. Trace SHA-256 `59c212596bdbe0d68b445bc44926599f632cfe778ded2ff397df48cf421330d9`; traced replay receipt SHA-256 `4009d4d0c491447799ab414a233e97ef3f5f48cb569e699af5b9867d4cbd6bf0`. This qualifies the captured Windows output pair only; a newer Linux pinned output pair remains unreplayed. Core V4 reimport is Candidate/NotAdmitted.



The current replay-output handoff receipt hash is `17734fc049c24eb7ce1c6b2e77f3071e99132f22f8b63378a8a6e5b05b823337`; it records the hostfxr trace and states that RC2 selection used an environment override because the adjacent runtimeconfig declares `Major` roll-forward.

### Core V4 candidate transport CLI — 2026-11-23

Core's qualified-candidate CLI now wires V4 output pins/import/profile/generated-order checks with live reimport and remains fail-closed before CWR3/session/stage. V4 synthetic fixture passed 11 checks; V3 regression passed 9. Receipt SHA-256 `92aff2e2b14f2780bddfc3ba7aeb5b488ae4bbc17b2f89a7f8d330fff348b0d9`. The remaining CWR3 composition, Core-owned replay, original task output binding, trust and hardware scope remain open.

### V4 real-process transport recheck — 2026-11-23

After the generated-PDB lookup fix, the Release V4 transport test passed 11 synthetic CLI checks plus 3 assertions using current-Core staged V2 pins and the exact-RC2 standalone replay-produced V4 bundle. The V3 transport regression passed all 9 checks. A separate production CLI process also reached the same `CandidateOnly` report and exited 2 by design before CWR3 composition; it reported 50 generated/PDB documents and three compiler outputs, with no stage/session publication. Receipt `artifacts/processor-20261114/cwr3-v4-transport-20261123/production-cli-real-v4-receipt.json`, SHA-256 `ea1d82daf556f2880a7a68c88fa6ee5241a0d42b632dd7fe58cf26cd63a3eea2`; current V4 log SHA-256 `cbb4caf43292f0d4cc69ded9d3aea89323222c9da6abc02a782fea2d25c6944f`; V3 log SHA-256 `54f0940f5088fd47dec5c2b4e318706977b991d2648cc72af9ff9ff36ecdc1bd`. This qualifies CLI transport wiring and fail-closed behavior only. Replay bytes are standalone-produced; original task consumption, Core-owned independent replay/admission, trusted hosted evidence, generated-input role mapping, and hardware verification remain open. Integration remains INCOMPLETE.

### Linux RC2 Core recheck — 2026-11-23

The pinned Linux SDK/runtime archive (`11.0.100-rc.2.26504.105`, archive SHA-256 `a468acd553a4c5ee4cd15e49afd54c986f75aba8cbb64e55168aadce3c422a7e`) was extracted into WSL temp tooling and used with `DOTNET_ROLL_FORWARD=Disable`. Current Core passed V4 public CLI transport (15 synthetic checks plus 3 real-pin assertions), V3 CLI regression (9), V4 output importer (26), and generated-order audit (8 checks; 50 generated rows/PDB documents). The CLI remains CandidateOnly/NotAdmitted and refuses before CWR3; the order audit is also CandidateOnly/NotAdmitted. Per-test logs, host/SDK details, exact pins and source hashes are in `artifacts/processor-20261114/cwr3-v4-transport-20261123/linux-cross-runtime-receipt.json`, SHA-256 `b6c20f84f1794bd0f9ecffdc87fb0809ec614b2b799d9529cd63f25455fe9e63`. The Linux task PE/PDB pair itself was not replayed: 16 non-source input buffers in its observed capture remain incomplete; no claim of original task consumption or production/profile-bound Core-owned GeneratorDriver execution and Csc re-emission follows; the later synthetic sandbox execution is recorded below and does not establish task replay, trust/admission, or hardware equivalence. Integration remains INCOMPLETE.

### Core V4 witness join follow-up — 2026-11-24

The Core V4 importer test now rejects five witness-row mutations even after recomputing witness and output-index pins (role, path, output hash, byte count, ordering). Windows SDK `11.0.100` fixture passed 31 checks; receipt `C:/Users/Yuriy/Desktop/CircuitWeave-main/artifacts/processor-20261114/csc-v4-witness-rows-20261124/receipt.json`, SHA-256 `67c02dcebda3659b6b35600c77e96b18b85b95baa8dbcd7597538ab52b8fb2f4`. A current-Core real-pin CLI probe imported staged V2 inputs and exact-RC2 standalone replay-produced V4 output bytes only as CandidateOnly and refused before CWR3 composition or publication. This was the pre-identity-check state; the follow-up below adds PE/PDB/refout artifact identity checks. GeneratorDriver profile execution, trusted handoff/admission, hosted evidence and Processor hardware verification remain open. Current machine-readable matrix: `C:/Users/Yuriy/Desktop/CircuitWeave-main/artifacts/processor-20261114/integration-20261121/integration-matrix.json`, SHA-256 `90944f3475e18d24e0cf112b4bf054d334df66efee909e95f3475087a4117081`. Integration stays INCOMPLETE.

### Core V4 artifact-identity follow-up — 2026-11-24

The Core V4 importer verifies PE CodeView GUID/stamp against Portable PDB content ID, and PE assembly identity against compiler refout assembly identity. 33 importer checks passed with actual Roslyn-emitted positive and mismatched outputs; a current-Core real-pin CLI run accepted the pinned standalone exact-RC2 replay bytes as CandidateOnly (15 synthetic + 3 real assertions), then stopped before CWR3 composition/publication. Receipt `C:/Users/Yuriy/Desktop/CircuitWeave-main/artifacts/processor-20261114/csc-v4-output-identity-20261124/receipt.json`, SHA-256 `eb60745baa7f801ed533112d3d55203e7ab61e3ebf80e9d623a3dc4809a7ce70`. These checks link artifact identities but do not prove full PE/PDB semantic equality or original MSBuild task output consumption. Linux original task buffers, Core independent Csc replay/admission, hosted trust, GeneratorDriver profile and hardware scope remain open.
### Linux Csc replay preflight — 2026-11-23

Before replay, a pinned-clone path snapshot matched all 1,104/1,104 current source-file hashes against the captured source inventory and 192/196 current non-source hashes against the post-build observation. Four current generated inputs differ: `HybridCPU_ISE.GeneratedMSBuildEditorConfig.editorconfig`, `HybridCPU_ISE.GlobalUsings.g.cs`, `HybridCPU_ISE.AssemblyInfo.cs`, and `HybridCPU_ISE.sourcelink.json`. Their task-time read buffers are among the incomplete capture; overall 16 non-source task-time buffers remain unreconstructed. Snapshot `artifacts/processor-20261114/linux-csc-replay-candidate-20261123/input-snapshot.json`, SHA-256 `0c774572bb8e5a8d4c4b16f769245ac8e5e8fb2466b7513009de2299dfb1fd1e`; matrix now records the exact per-path expected/current hashes. No replay was run because supplying current or reconstructed bytes would not establish the original task inputs. Clear this blocker by recovering and hashing all 16 task-time buffers, checking complete ordered response bytes, then running the Csc replay in the bounded no-network read-only sandbox and comparing PE/PDB/refout hashes. Integration remains INCOMPLETE.
### Roslyn sandbox result trust state — 2026-11-23

The Core sandbox validation DTO now exposes `EvidenceLevel=StructuralOnly`, `Execution=NotEstablished`, and `Admission=NotAdmitted` as fixed properties. This makes the non-authoritative status explicit even when a hostile generator forges the stdout frame and the host accepts a structurally valid payload. The Windows and isolated Linux contract fixtures each passed 23 assertions. The Linux worker Release build passed with zero warnings/errors on the exact RC2 Linux SDK; the Windows worker Release build passed with zero warnings/errors under SDK 11.0.100. The root-WSL native Linux harness passed 14 isolation and fail-closed assertions against the current worker, including a generated syntax-error refusal; a forged frame remains `StructuralOnly` / `NotEstablished` / `NotAdmitted`. The worker calls `RunGeneratorsAndUpdateCompilation`, checks updated-compilation diagnostics, and verifies its generated-tree count before returning candidate bytes. Receipt `artifacts/processor-20261114/roslyn-sandbox-explicit-status-20261123/receipt.json`, SHA-256 `12b69a7aef640ef67d86a75277534bfa6bc56fa304bfa05fdc465cc1b9e286fe`. This does not authenticate GeneratorDriver completion or admit sources. The next Core-owned slice needs a strict resolved profile joined to staged V2 rows: exact ordered sources/references/analyzers, analyzer-config and additional-file bytes/paths, source encoding/checksum, and parse/compilation options. CWR3 must remain fail-closed pending independent Csc replay and trusted admission. Integration remains INCOMPLETE.

### Core sandbox profile identity follow-up — 2026-11-24

Core's Linux Roslyn sandbox profile now requires `AssemblyName` under profile schema v2 and passes it through to the worker compilation. Worker and host-specific test project build successfully; the portable contracts fixture passes 24 assertions. Native Linux execution was not run on this Windows host. This narrows a compiler-context mismatch but does not prove profile-to-Csc parity or trusted execution. Receipt `C:/Users/Yuriy/Desktop/CircuitWeave-main/artifacts/processor-20261114/sandbox-assembly-profile-v2-20261124/receipt.json`, SHA-256 `9052e58c86f764362cba1283f0cb77bda7bcf54bd602f66dc5bece2084f75489`; current matrix SHA-256 `90944f3475e18d24e0cf112b4bf054d334df66efee909e95f3475087a4117081`. Integration remains INCOMPLETE.

### Profile v2 options follow-up — 2026-11-24

The worker profile now carries bounded explicit compiler options (AssemblyName, library/release output, deterministic, no unsafe, language/docs/source-kind/encoding, nullable, checked, defines), and the worker validates then applies them. Release builds passed and portable contract assertions remain 24. Updated receipt `C:/Users/Yuriy/Desktop/CircuitWeave-main/artifacts/processor-20261114/sandbox-assembly-profile-v2-20261124/receipt.json`, SHA-256 `9052e58c86f764362cba1283f0cb77bda7bcf54bd602f66dc5bece2084f75489`; matrix SHA-256 `90944f3475e18d24e0cf112b4bf054d334df66efee909e95f3475087a4117081`. Native run and derivation from Golden task inputs remain required; integration is INCOMPLETE.

### Native synthetic profile v2 confirmation — 2026-11-23

Core's profile v2 worker ran natively in the pinned Linux/WSL2 sandbox with 14 checks passed. A synthetic generator observed all currently supported compiler/parse/source settings in GeneratorExecutionContext. Receipt `C:/Users/Yuriy/Desktop/CircuitWeave-main/artifacts/processor-20261114/sandbox-assembly-profile-v2-20261124/receipt.json`, SHA-256 `9052e58c86f764362cba1283f0cb77bda7bcf54bd602f66dc5bece2084f75489`; native log SHA-256 `676360221fabb4ba47c15609804d92810c6a3c94ced698efc9d98cb91f4aeaf3`. This is synthetic candidate evidence; original Csc mapping, authenticated worker execution, admission, and hardware verification remain open. Matrix SHA-256 `90944f3475e18d24e0cf112b4bf054d334df66efee909e95f3475087a4117081`. Integration remains INCOMPLETE.

### 2026-11-23 — Core-owned candidate Csc input resolution

Core now re-audits the V2 index and Csc closure profile, requires an explicit unique primary command, joins every supported file-valued argv token to the same profile and imported V2 row, and rereads exact bytes from the owned workspace with size/SHA checks. The real staged Windows candidate passed 5 resolver synthetic checks, 15 V4 CLI synthetic checks and 5 real-pin assertions with 1,299 ordered file rows and 50 generated witnesses kept separate; zero-byte sources are preserved. Negative cases refuse a stale external profile pin and a workspace source changed after capture. Evidence receipt `C:/Users/Yuriy/Desktop/CircuitWeave-main/artifacts/processor-20261114/csc-candidate-input-resolver-20261123/receipt.json`, SHA-256 `f0748e7ff7b6fbc0c9824ea2db2abac20033266004b3d6f519328c9c25a4c7cc`; test log SHA-256 `a58b28f7412d799a3389a3077970034ca30a58a023dabfd704f2abea0f2bc9e5`. Candidate byte freezing does not run Roslyn/GeneratorDriver or establish task-time provenance/admission. Next is complete options/analyzer-config transfer into the isolated Core worker and independent output replay. Current matrix SHA-256 `11abab7787826e4e05e0d1681e6bf7ad0d885c9b0125468b2cc622d2a7b9aed3`. Integration remains INCOMPLETE.

### 2026-11-23 — Zero-byte source handoff to sandbox

The Core sandbox input blob now preserves zero-length byte arrays and the Linux worker read helper accepts that length while verifying the SHA-256 of empty bytes. The compiler profile itself remains nonempty. RC2 worker Release build passed with zero warnings/errors, the portable request contract passed 25 checks, and the native WSL2 sandbox suite passed 17 checks including a zero-byte source. Receipt `C:/Users/Yuriy/Desktop/CircuitWeave-main/artifacts/processor-20261114/sandbox-empty-input-20261123/receipt.json`, SHA-256 `fc30939741e7e95da1d95ef2b96ef68fec300c2af6392f9abd4e90b53b3f37d7`; native log SHA-256 `73dac501677e25d7dfbad616381fddc984ea5a7706d4c5f963f460246c9a07d7`; build log SHA-256 `df606a8f9906dbaab92970ee3a2a315aa1ad5ce3913e14edca294fcfa05140de`. Output remains StructuralOnly / NotEstablished / NotAdmitted. Analyzer-config mapping is still absent; integration remains INCOMPLETE. Matrix SHA-256 `11abab7787826e4e05e0d1681e6bf7ad0d885c9b0125468b2cc622d2a7b9aed3`.

### Core analyzer-config transport recheck — 2026-11-23

Core sandbox v2 now digest-binds analyzer-config bytes and original virtual paths, forwards them into the Linux worker, and supplies global options to the generator. Synthetic WSL2 hostile-generator and refusal checks passed (19 native assertions; 27 portable contract assertions); the implemented parser is explicitly global-only. Evidence: Core receipt e0e11cc3ddecac61ce74b2352eb477bad18bf0f2f6a9e66541f918f6f06d5c6d, native log SHA-256 8120ca4a933566b2d037ae9c86c5744cf09f08415d1c2f75f9412583c5f20f8f. This does not compose the staged Csc resolver into a full request or prove Golden execution, trust/admission, or hardware. Matrix 772ae35afdc1c49bc4008ba44bc5c7da034551d1b04a7ddeb9334976b765a885; integration remains INCOMPLETE.

### Candidate request composition follow-up — 2026-11-23

Core now has a resolver-to-sandbox mapper for the supported Csc source/reference/analyzer/config/additional rows, preserving their row order, SHA pins, and virtual paths. Unsupported Csc file roles fail closed; the current staged command contains an embedded role and cannot yet form a complete sandbox request. The synthetic mapper test passed, but compiler profile options are still caller-supplied and uncorrelated to argv. Receipt 9f65490f0789c1c4fa63c8c2430b3221459a84f7d4d17507f0755c050afd9135, regression log 63ea9b0a87e3d93904108925b17f4745f4efe940719c7467a3ed1083987ed936, current matrix 772ae35afdc1c49bc4008ba44bc5c7da034551d1b04a7ddeb9334976b765a885. Integration remains INCOMPLETE.

### Core request v3 follow-up — 2026-11-24

The isolated Core request/wire now preserves and hashes analyzer-config bytes, virtual paths, and a distinct embedded-input category. Native synthetic checks passed (20 worker, 28 contracts); the staged current-Core candidate rows mapped as CandidateOnly but were not executed because the compiler options profile is not qualified from its captured Csc argv. Global analyzer-config parsing supports only explicit global-only syntax and rejects sections. Evidence receipt e0e11cc3ddecac61ce74b2352eb477bad18bf0f2f6a9e66541f918f6f06d5c6d, mapper 9f65490f0789c1c4fa63c8c2430b3221459a84f7d4d17507f0755c050afd9135, matrix 772ae35afdc1c49bc4008ba44bc5c7da034551d1b04a7ddeb9334976b765a885. No Golden execution, trusted handoff, admission, RTL/Processor or Studio evidence was added; integration remains INCOMPLETE.
# 2026-11-24 Core replay follow-up

CircuitWeave Core now maps the staged v3 compiler closure into the isolated Roslyn worker. The full candidate replay did not complete: worker exit 137 occurred while initializing analyzer 10 (`System.Text.RegularExpressions.Generator.dll`); no generated output was accepted. Core contract tests passed 30 assertions and its native Linux isolation suite passed 21 fixture assertions, which do not qualify this captured candidate. The evidence is in `CircuitWeave-main/artifacts/processor-20261114/csc-candidate-generator-replay-20261124/replay-followup-20261124.md` and the Core integration matrix. Preserve CandidateOnly/NotAdmitted until the failure is diagnosed, a complete independent replay is compared, and trusted handoff succeeds. HardwareBridge capture/provenance, positive trust receipt, AddWord RTL equivalence, stateful Processor traces and Studio consumption remain open; overall integration is INCOMPLETE.

### Correction — kernel-confirmed effective memory ceiling (2026-11-24)

The earlier analyzer-specific framing is narrowed: WSL kernel records at 03:27 and 03:41 MSK correlate full candidate runs with cgroup OOM at 262,144 kB, although the request asks the visible per-run cgroup for 4 GiB. A separate 03:31 OOM record is not attributed to this candidate. The hidden ancestor ceiling is not inspectable through the delegated mount. Loading analyzer images on demand and zeroing temporary copies did not complete the full run; the single-analyzer/full-source diagnostic remained inconclusive. Evidence and hashes are recorded in the Core replay follow-up. Do not infer a generator defect from the last stage marker or claim any generated output. Clear the resource-owner blocker before retrying unchanged inputs; retain all trust, hardware, Processor and Studio gates as open.

### Core-owned AddWord recheck — 2026-11-24

CircuitWeave Core HEAD `23c01bf5baeb1377a1317e2523a25f96713a1c75` was rebuilt from the raw pinned Golden AddWord Git blob. Its HWIR and native CIRCT/Icarus candidate matched all 8,292 saved actual-Golden-PE output vectors; the existing zero-extension fault was rejected at vector 4. The generated RTL bytes match the historical candidate. This does not change source admission: the run used current dirty Core working bytes, finite combinational vectors, no formal proof, and no trusted producer receipt. Retain CandidateOnly/NotAdmitted; full hardware and Processor acceptance remain open. Evidence: `CircuitWeave-main/artifacts/processor-20261114/addword-current-core-20261124/receipt.json`.


### 2026-11-24 — P02/P06 AddWord authority wiring audit

Core read-only audit at HEAD 23c01bf5baeb1377a1317e2523a25f96713a1c75: CWR3 candidate composition supplies a Roslyn source verifier to P02, but constructs P06 with no material provider, oracle, native simulator, independent tools or provenance gate. The AddWord provenance gate separately refuses without its independent P02 verifier. No gate was weakened and no production source changed. Close only after an authorized session provider binds actual candidate RTL, exact Golden PE/PDB and closure files and P06 native/formal/signed independent attestations complete. Evidence: Core `artifacts/processor-20261114/p02-p06-addword-wiring-audit-20261124/receipt.json`; integration remains INCOMPLETE.



### 2026-11-24 — AddWord source-model formal proof

Yosys 0.52 SAT proved current Core AddWord candidate RTL equivalent to a direct bit-vector translation of the pinned Golden method for all 128 two-state inputs; the zero-extension mutant was refuted by a SAT counterexample. This is CandidateOnly source-model evidence, not independent C#-to-RTL qualification, P06, owner admission or trusted handoff. Receipt in Core: `artifacts/processor-20261114/addword-current-core-20261124/formal-source-model/formal-receipt.json, SHA-256 119df27f30b686fdfc219d06ba982905513b8e56152c8968de8e600b48010636. Integration remains INCOMPLETE.


### 2026-11-24 — SubWord current-Core native/formal recheck

Current dirty Core HEAD `23c01bf5baeb1377a1317e2523a25f96713a1c75` rebuilt SubWord from raw Golden blob `c375dd8f9388fb09bf0cb8c4c09a06f3a25403bc` and executed actual pinned Golden PE `dc7f02ca3bc714f9e6a83b9de3c03b8dd6da64794eb477b2c5b057d7b7d10dc0`, MethodDef `0x06001835`, MVID `0c68887d-128e-4a50-add2-d38aafc99d72`. The 8,292 freshly generated ordered PE outputs matched the archived vector records; current Core HWIR and CIRCT/Icarus RTL matched all vectors. Yosys SAT proved the RTL equivalent to a direct pinned-source subtraction/sign-extension bit-vector model for all 128 two-state input bits. The zero-extension mutant failed native vector 1 and produced a formal counterexample. Receipt: `artifacts/processor-20261114/subword-current-core-20261124/receipt.json`, SHA-256 `3832ee093fdc852af96e6c05a9b865aca8dc28bd3942d5b1dd4046ec28fa654a`.

CandidateOnly/NotAdmitted: current dirty Core, hand-authored source model, and bounded scalar scope; no P06 authority, trusted handoff, stateful Processor claim, or Studio qualification. Integration remains INCOMPLETE.

### 2026-11-24 — MultiplyWord current-Core native/formal recheck

The current dirty Core harness build compiled MultiplyWord from the pinned raw Golden blob. It executed Golden PE MethodDef `0x06001836` from PE SHA-256 `dc7f02ca3bc714f9e6a83b9de3c03b8dd6da64794eb477b2c5b057d7b7d10dc0` (MVID `0c68887d-128e-4a50-add2-d38aafc99d72`). Fresh output rows matched all 8,292 archived ordered Golden vectors; Core HWIR and CIRCT/Icarus RTL each matched those outputs. Yosys SAT proved current RTL against a direct modulo-32 multiplication and sign-extension model of the pinned method for all 128 two-state input bits; a zero-extension mutation failed native vector 14. The bounded Core harness uses a custom wrap multiply evaluator because the stock evaluator still rejects `mul` (HWV0502). Receipt: `artifacts/processor-20261114/multiplyword-current-core-20261124/receipt.json`, SHA-256 `015b9fe93d14bddbaf34b18c678c97199051b1192d8f61ba8a2c801d01c4032c`.

CandidateOnly/NotAdmitted: no independent C#-to-RTL authority, P06 attestation, trusted handoff, owner approval, stateful Processor claim, or Studio qualification. Integration remains INCOMPLETE.

### 2026-11-24 — Integration matrix receipt-link audit

Audited every `receipt` and `formalReceipt` path in `artifacts/processor-20261114/integration-20261121/integration-matrix.json`. Twenty stale relative paths pointed outside the `processor-20261114` artifact subtree; each was corrected only after confirming the target file exists. Two additional stale receipt SHA fields were updated to the current bytes after hashing the referenced file. Re-resolution now reports zero missing receipt targets and zero receipt-hash mismatches; JSON parse passes. Current matrix SHA-256: `723AD7E0BCF54CA6AFD88EF0C56E828CFB6C4D5490ABAF748816C8DCADF593E6`. The backlog records this as `currentExecutionEvidence.currentMatrixSha256`; historical package receipt hashes remain unchanged. This bookkeeping repair does not upgrade any evidence or integration status.

### 2026-11-24 — Core AddWordImmediate candidate update

Core added a bounded AddWordImmediate source projection with a Proposed, NotAdmitted binding. Using Golden commit 6946ccab9d888f7dae0a4d8e18480f436138acd9 (MethodDef `0x06001833` PE SHA-256 `dc7f02ca3bc714f9e6a83b9de3c03b8dd6da64794eb477b2c5b057d7b7d10dc0`), a Core-only Release build passed and candidate HWIR/native RTL matched 8,292 actual Golden PE vectors with zero RTL mismatches. This does not resolve the existing trusted P02/P06 handoff gap or qualify Processor integration. Receipt: `artifacts/processor-20261114/addword-immediate-golden-oracle-20261124/receipt.json` (SHA-256 08d1b7c690503037ebcf2760d039ca115ab67e3b5462304b96f5c9296f834222); matrix SHA-256 772ae35afdc1c49bc4008ba44bc5c7da034551d1b04a7ddeb9334976b765a885. Integration remains INCOMPLETE.


### 2026-11-24 — ShiftLeftWordImmediate bounded candidate slice

Pinned Golden commit 6946ccab9d888f7dae0a4d8e18480f436138acd9, MethodDef `0x0600183c` MVID `0c68887d-128e-4a50-add2-d38aafc99d72` source SHA-256 `f1726c298722496f1218aeee41de7c8f75c79c26a36c0e606cae7e8b5123c5fe, PE SHA-256 `dc7f02ca3bc714f9e6a83b9de3c03b8dd6da64794eb477b2c5b057d7b7d10dc0`, and PDB SHA-256 `3a5aaa1fdbf18032199d0fea4f26cdc0b64afb0a5a475e7115c2556b249de578` identify the inputs. Core source projection, typed HWIR and native RTL matched all 8,362 actual Golden PE vectors. RTL mutations removing sign extension and using mask `0x3f` were rejected at vectors 23 and 24. Yosys 0.52 SAT passed against a hand-authored direct bit-vector model; this is candidate-only and is not a P06 attestation. The proposed binding remains NotAdmitted. Receipt: `artifacts/processor-20261114/shiftleft-word-immediate-golden-20261124/receipt.json, SHA-256 96873d35c9b9576ee353cacb0b483061c8ea7740cf38949b95997b1d17fcd027. Updated integration matrix SHA-256: 772ae35afdc1c49bc4008ba44bc5c7da034551d1b04a7ddeb9334976b765a885. No trusted handoff, independent P06 authority, stateful Processor proof, or Studio load verification is established. Integration remains INCOMPLETE.







### 2026-11-24 — Matrix path and binding-contract recheck

A recursive audit found 7 stale receipt paths that escaped the processor-20261114 artifact root; corrected them after confirming each target exists. All 81 receipt/formalReceipt JSON targets now resolve and every present receipt digest matches. The current matrix SHA-256 is a8694463bfb851c5b45198d655800affe1b180fe6ccfb8942e8db41976675296. The ShiftLeftWordImmediate compiler now rejects a binding whose declared signedness differs from the mixed contract; the negative mutation returns HWG0602. Updated bounded-slice receipt SHA-256: 96873d35c9b9576ee353cacb0b483061c8ea7740cf38949b95997b1d17fcd027. These bookkeeping and refusal checks do not advance admission, P06, or Processor integration; overall status remains INCOMPLETE.


### 2026-11-24 — ShiftRightLogicalWordImmediate bounded candidate slice

Pinned Golden commit `6946ccab9d888f7dae0a4d8e18480f436138acd9`, MethodDef `0x0600183d`, MVID `0c68887d-128e-4a50-add2-d38aafc99d72`, source SHA-256 `f1726c298722496f1218aeee41de7c8f75c79c26a36c0e606cae7e8b5123c5fe`, PE SHA-256 `dc7f02ca3bc714f9e6a83b9de3c03b8dd6da64794eb477b2c5b057d7b7d10dc0`, and PDB SHA-256 `3a5aaa1fdbf18032199d0fea4f26cdc0b64afb0a5a475e7115c2556b249de578` identify the pinned inputs. Core source projection/HWIR and native CIRCT RTL matched all 8,362 actual Golden PE vectors. Arithmetic-shift, mask-0x3f, and zero-extension RTL mutants were rejected at vectors 69, 24, and 68. Yosys 0.52 SAT passed against a hand-authored direct model; this is candidate-only, not a P06 attestation. The compiler now checks the proposed mixed signedness contract; the binding remains NotAdmitted. Receipt: `artifacts/processor-20261114/shiftright-logical-word-immediate-20261124/receipt.json`, SHA-256 `bbf9cfeb581f1c769c1324dfc79bc4a6f6ce7100f5b7389864ec0da5d1cc9cd6`. The integration matrix now resolves all 82 receipt/formalReceipt JSON links with zero digest mismatches; its SHA-256 is `a8694463bfb851c5b45198d655800affe1b180fe6ccfb8942e8db41976675296`. Integration remains INCOMPLETE; trusted handoff, independent P06 and stateful Processor evidence remain open.



### 2026-11-24 SRAIW candidate slice

`ShiftRightArithmeticWordImmediate` (pinned Golden MethodDef `0x0600183e`) теперь имеет ограниченный Core source→HWIR→firtool RTL candidate: 8,362 фактических Golden PE outputs совпали в Core evaluator и Icarus на generated RTL; generated RTL прошёл candidate-only Yosys SAT; три RTL semantic mutations и binding Revision mutation отказаны. Receipt `artifacts/processor-20261114/shiftright-arithmetic-word-immediate-20261124/receipt.json` SHA-256 `480bb8593ad297712e3648035317ee5cfb1d4fec805d932fc58ac24147549209`. Admission/P06/trusted handoff/full Processor acceptance остаются открытыми.

### 2026-11-24 SRAW bounded candidate

`ShiftRightArithmeticWord(ulong, ulong)` имеет candidate source→typed HWIR→CIRCT RTL. Golden PE MethodDef `0x06001840` и source/PE/PDB pins зафиксированы в receipt; PE MethodDef также проверяется Core harness. 8 372 actual PE vectors совпали в Core и на generated RTL, три semantic mutations отказаны, generated module прошёл bounded Yosys SAT. Owner admission, trusted handoff, P06 и stateful Processor acceptance остаются открытыми. Receipt: `artifacts/processor-20261114/shiftright-arithmetic-word-20261124-sraw/receipt.json`, SHA-256 `313665d74717b00c783f1d472a95e5f5b4fa11ba1160ccd9db280473738d944a`.

### 2026-11-24 SRLW bounded candidate

`ShiftRightLogicalWord(ulong, ulong)` теперь имеет отдельный bounded source→HWIR→RTL candidate. Golden PE MethodDef `0x0600183f`, exact PE/PDB/source pins, vector oracle, native simulation и generated-module SAT находятся в receipt `artifacts/processor-20261114/shiftright-logical-word-20261124-srlw/receipt.json` (SHA-256 `dfc7aad6dd972ec59a32a50cd6fb408eb7ffc8c6996bda4a8b3546c25a6a5d83`). Независимый review подтвердил unsigned shift followed by source-owned sign extension. Полное admission/trust/P06/stateful acceptance остаётся открытым.

### 2026-11-24 SLLW bounded candidate

`ShiftLeftWord(ulong, ulong)` is the final register word-shift sibling in the candidate ladder: pinned source/PE/PDB, source-owned `SignExtendWord`, typed HWIR, native RTL simulation, generated-RTL SAT, and three rejected semantic mutations are recorded in `artifacts/processor-20261114/shiftleft-word-20261124-sllw/receipt.json` (SHA-256 `2c6eb306026f473ac95f9c688e430be1e06ad0142032cd76ec10b2a5168597d3`). All seven word-shift/ADDIW projects passed after the shared mode extension. Binding admission, independent P06, trusted handoff and stateful Processor proof remain open.

### 2026-11-24 HardwareBridge portable build check

Уточнено по текущему Bridge worktree: portable `HardwareBridge.Hwir` default проходит без Core `ProjectReference`; legacy fixtures остаются explicit opt-in; missing SDK pin отказывается fail-closed (`HBINT0104`). Static mutation tests: 8/8 PASS. Dynamic dirty-Core pin gate now verified: with Git/.NET on the cmd.exe child PATH, MSBuild refuses `HBINT0103 DirtyCoreCheckout`; initial attempt without that PATH exited 9009. Evidence в `artifacts/processor-20261114/bridge-portable-boundary-20261124/receipt.json`.


### 2026-11-24 Core word-candidate regression cross-reference

The current dirty Core Release solution and seven bounded word operation harnesses (ADDIW, SLLIW, SRLIW, SRAIW, SRAW, SRLW, SLLW) passed after restore on SDK 11.0.100. Core receipt: `C:/Users/Yuriy/Desktop/CircuitWeave-main/artifacts/processor-20261114/current-word-regression-20261124/receipt.json`, SHA-256 `ed8851958c59205b42a7192908f1dfb7a83aac738de8b92ec049b0223d5d5aa3`; matrix SHA-256 `27bea854e62ad48323e29c8669a6bcfd5e80b3a79455dd66ac8a5ea0b70d6cb3`. This updates regression evidence only; Bridge producer/current dirty bytes, remote pins, trusted handoff, independent P06 and stateful Processor acceptance remain open.


### 2026-11-24 — Csc output supplement candidate contract

Initial synthetic-only checkpoint, superseded by the archived-output capture below: the additive `segmented_csc_output_capture_v1.py` passed its first 9 tests. Current receipt SHA-256 `40433828ab571f70cef37b76aca05c3d08b74d1d45d5a4331619a32628d325cb` records 11 synthetic tests, capture of three archived standalone replay outputs, and independent current Core V4 import as Candidate. Execution, original task consumption, provenance and admission remain NotEstablished/NotEstablished/NotEstablished/NotAdmitted. Current Core matrix SHA-256 `27bea854e62ad48323e29c8669a6bcfd5e80b3a79455dd66ac8a5ea0b70d6cb3`.

### 2026-11-24 P06 handoff audit cross-reference

Independent read-only audit found no safe code-only P06 transition: candidate research returns before the verification ladder, while production P06 requires an already Verified binding/current lock and independent authority adapters/session capture not presently constructed. Closure conditions are protected trusted Bridge/Core hosted receipt, complete Csc/generator/analyzer capture and replay, signed P02 admission, independent P06 evidence, content-addressed CW-INT-09/11 linkage, and external Processor timing/lifecycle contracts. Do not bypass these gates; integration remains incomplete.


### 2026-11-24 output-source path binding correction

Independent review prompted stronger source-path binding. The Bridge now requires safe containment under primary `workingDirectory`, checks the argv-derived logical output path, and compares normalized absolute paths against `workingDirectory + output path`. An unrelated same-cwd file and a byte-identical alternate-root file with the same relative output path are both refused; the 11-test suite passes. The actual archived manifest source paths match the primary command `workingDirectory` (the project `nodeId` root differs), so three archived replay outputs were captured and independently accepted by Core V4 as Candidate. This still proves neither original Csc task execution/consumption nor provenance/admission. Current receipt SHA-256 `40433828ab571f70cef37b76aca05c3d08b74d1d45d5a4331619a32628d325cb`; matrix SHA-256 `27bea854e62ad48323e29c8669a6bcfd5e80b3a79455dd66ac8a5ea0b70d6cb3`.


### 2026-11-24 archived output capture and independent Core V4 import

The new capture path replayed the archived primary V2 parent and `primary-output-manifest.json`, emitted and self-verified three actual archived replay output payloads (compiler PE, PDB and refout; 9,131,180 bytes), and reproduced the already pinned V4 index/closure. Current Core V4 independently accepted the bundle as `Candidate` with all three expected roles. Receipt: `C:/Users/Yuriy/Desktop/CircuitWeave-main/artifacts/processor-20261114/bridge-csc-output-v1-20261124/receipt.json`, SHA-256 `40433828ab571f70cef37b76aca05c3d08b74d1d45d5a4331619a32628d325cb`; matrix SHA-256 `27bea854e62ad48323e29c8669a6bcfd5e80b3a79455dd66ac8a5ea0b70d6cb3`. These are archived standalone exact-RC2 response-replay outputs that match a recorded direct-build hash comparison. Original Csc task execution/output consumption, trusted provenance and admission remain NotEstablished/NotAdmitted; this is not a positive handoff receipt or P06.

### 2026-11-24 current evidence update

The Windows standalone Csc response replay harness has a Windows PowerShell 5.1 argument fallback and explicitly sets `DOTNET_ROOT` to the captured RC2 SDK with roll-forward disabled. Current Bridge/Core pins were `2be1784125193911ea635cb4fce0661b0b4a4bda` / `23c01bf5baeb1377a1317e2523a25f96713a1c75`; pinned Golden commit remained `6946ccab9d888f7dae0a4d8e18480f436138acd9`. Replay exited 0, 1,352 primary input rows remained unchanged, and compiler PE/PDB/refout hashes matched the pinned manifest. Receipt `CircuitWeave-main/artifacts/processor-20261114/csc-output-supplement-v4-20261123/standalone-csc-replay-receipt.json`, SHA-256 `a5f1f28940a35f1f495ae21ad1827b60a1b164b91dfc3ef18a853f855eb6b414`.

This qualifies only a standalone response replay. It does not establish that the original MSBuild task consumed these output bytes, nor provenance, trusted handoff, or Core admission. Core's independent V4 audit found that CWR3 cannot consume the current output rows without a new strict profile/adapter covering ordered generated and embedded inputs and independent re-emission. Keep the refusal fail-closed. Core integration matrix SHA-256 `9ec79638b71b2f6d2edc2c3f050822474c4da3cd3ad9c14a978749baa80b8361`; integration remains INCOMPLETE.

### Follow-up — DivideWord actual-PE oracle (2026-11-24)

Captured `DivideWord` facts from the pinned Golden PE after checking source SHA-256 `f1726c298722496f1218aeee41de7c8f75c79c26a36c0e606cae7e8b5123c5fe`, PE SHA-256 `dc7f02ca3bc714f9e6a83b9de3c03b8dd6da64794eb477b2c5b057d7b7d10dc0`, MVID and MethodDef `0x06001837`. The runner produced 8,501 vectors including divisor-zero and signed-overflow cases. Receipt in Core artifacts: `artifacts/processor-20261114/divideword-pe-oracle-20261124/receipt.json`, SHA-256 `c367781ac15aca2b6643931d696790d5e48e016a4f865c5944253e53c284c88d`.

At this oracle-only checkpoint, the artifact established Golden PE software outputs only and had no Core lowering/HWIR/RTL result. This status is superseded by the DivideWord Core candidate section immediately below. Historical matrix SHA-256 at that checkpoint: `03bfc70be1541c975eceb23c46a86f391aef2c833fd6c85f0d0d73008a451c43`. Integration remains INCOMPLETE.

### Follow-up — DivideWord Core candidate (2026-11-24)

Core now has a narrowly pinned DivideWord candidate mode and `divw32_trunc0` semantic contract. The new fixture verifies source/PE identity, exact 32-bit opcode shape, altered-source/binding refusal, and all 8,501 actual pinned PE outputs against Core evaluator. Core-generated CIRCT RTL passed Icarus over the same 8,501 vectors; a deliberately incorrect zero-divisor result was caught at the first row. Yosys SAT passed for the Core RTL against a direct source-model miter. These results are combinational CandidateOnly evidence and do not represent a trusted Bridge handoff, P06 authority, or Processor acceptance. Receipt SHA-256 `c367781ac15aca2b6643931d696790d5e48e016a4f865c5944253e53c284c88d`; full Core Release solution build passed (0 warnings, 0 errors); matrix SHA-256 `0d2cfd4b79ae9f99208fc1c2abf8170289c6fe194ba3d94db63b556166448f6d`. Integration remains INCOMPLETE.

### 2026-11-24 AddWord compatibility pin recheck

After supplying the required explicit Core properties, the Bridge opt-in fixture exposed missing direct compile references to the Core `Bindings` and `Contracts` projects. The HWIR project now declares these conditional references beside its existing pinned Frontend/HWIR references; portable default remains opt-out. With Git available to MSBuild `Exec` via a temporary command shim, `dotnet restore`/`dotnet build` of `HardwareBridge.Hwir.csproj` passed against a clean detached Core `23c01bf5baeb1377a1317e2523a25f96713a1c75` at SDK `11.0.100` (0 warnings/errors). The full `CircuitWeave.Golden.AddWord.Smoke` restore/build/run then passed: HardwareBridge → Golden AddWord Roslyn closure/effects → validated HWIR, 8,292 C# oracle vectors each for AddWord, SubWord and MultiplyWord. Evidence is recorded in `artifacts/processor-20261114/divideword-pe-oracle-20261124/receipt.json`, SHA-256 `c367781ac15aca2b6643931d696790d5e48e016a4f865c5944253e53c284c88d`; matrix SHA-256 `3ee21eedafa613450f484ec8bafad64c1f6a4ffb8fd8811d78df5faa1e55e0ce`.

This is bounded CandidateOnly compatibility evidence: no CIRCT/RTL/formal proof was performed for this smoke, and it does not establish a trusted handoff, admission or P06. Main Core working tree remains dirty, so its uncommitted changes were not represented by this clean SHA pin; the run qualifies only the committed `23c01bf…` Core revision. Integration remains INCOMPLETE.
### 2026-11-24 — DivideUnsignedWord (DIVUW) actual-PE source-to-RTL candidate

Pinned Golden method `DivideUnsignedWord` was invoked from the archived pinned PE: commit association `6946ccab9d888f7dae0a4d8e18480f436138acd9`, source Git blob `c375dd8f9388fb09bf0cb8c4c09a06f3a25403bc`, source SHA-256 `f1726c298722496f1218aeee41de7c8f75c79c26a36c0e606cae7e8b5123c5fe`, PE SHA-256 `dc7f02ca3bc714f9e6a83b9de3c03b8dd6da64794eb477b2c5b057d7b7d10dc0`, MVID `0c68887d-128e-4a50-add2-d38aafc99d72`, MethodDef `0x06001838`. Its 8,501 actual PE vectors include zero divisor, low-32 operand truncation, unsigned quotient and sign extension of result bit 31. The source/PE bytes are archived pins; current Golden worktree was not used, and commit membership remains linked to prior archived evidence.

Core added the narrow `divuw32_divzero_ones` u32-only opcode through the exact pinned compiler mode, both HWIR validators, scalar evaluator, CIRCT lowering (`comb.divu` plus zero-divisor mux), and source-model formal mapper. Core HWIR/evaluator matched all 8,501 PE rows. Core-generated CIRCT RTL passed Icarus 12.0 across those same rows; a zero-divisor mutant failed at row 0. Yosys 0.52 SAT passed against a hand-authored direct 2-state source model. The full `CircuitWeave.slnx` Release build passed with zero errors and 1,056 warnings.

Commands: pinned RC2 `dotnet run --project DivideUnsignedWordOracle.csproj -c Release --no-build -- <pinned source> <pinned PE> <vectors>`; `dotnet build CircuitWeave.slnx --no-restore --configuration Release --verbosity minimal`; test project `dotnet run --project tests/CircuitWeave.Golden.DivideUnsignedWord.Tests/CircuitWeave.Golden.DivideUnsignedWord.Tests.csproj -c Release --no-build -- <source> <binding> <vectors> <oracle.json> <pinned PE> <output dir>`; native tools: `firtool --verilog`, `iverilog -g2012`, `vvp`, and `yosys -p "read_verilog -formal -sv divuw.sv divuw-formal-miter.sv; prep -top divuw_formal_miter; flatten; prep -top divuw_formal_miter; sat -prove mismatch 0"`. Exact arguments, input/output SHA-256 and tool hashes are in `artifacts/processor-20261114/divideunsignedword-pe-oracle-20261124/{oracle-receipt.json,candidate-receipt.json}`.

This remains `CandidateOnly`: binding Proposed/NotAdmitted, no trusted handoff, no Core formal-adapter execution, no independent P06, no hosted evidence, no stateful Processor lifecycle or Studio load check. Integration remains INCOMPLETE. Current candidate receipt SHA-256 `65085bae84dca05fc0c52341edc9da0a6ba0866c893c01105248df655291a0fc`; oracle receipt SHA-256 `fbe9ef79186803576cb7394ac6aee7f43bed2af3c753eca0ecd4d003617a4835`; integration matrix SHA-256 `772ae35afdc1c49bc4008ba44bc5c7da034551d1b04a7ddeb9334976b765a885`.

### CWR3 effective cgroup budget follow-up — 2026-11-24

The staged V3 candidate replay exited 137 with no worker output: the request set 4 GiB but the kernel correlated the run to a hidden effective 256 MiB WSL/distro ancestor. `LinuxRoslynGeneratorSandboxHost` configures and reads the newly created child cgroup only; the receipt therefore attests child controls, not the full inherited hierarchy. The current cgroup mount view does not expose the ancestor that imposed the observed ceiling. Keep this run `NotEstablished`/`NotAdmitted` and do not infer an analyzer defect. The exact run evidence is the `roslynSandboxCandidateReplayFollowup20261124` matrix row and [follow-up note](../../../../../CircuitWeave-main/artifacts/processor-20261114/csc-candidate-generator-replay-20261124/replay-followup-20261124.md). Before another full replay, the WSL/distro owner must identify and authorize a way to raise/attest the effective budget, or the complete worker must be reduced below a measured safe ceiling. Any future host qualification must record ancestor effective limits and refuse when hierarchy coverage is unknown. Current matrix SHA-256 `772ae35afdc1c49bc4008ba44bc5c7da034551d1b04a7ddeb9334976b765a885`; integration remains INCOMPLETE.

### 2026-11-24 — current-Core trunc compatibility regression

A fresh SLLW test found shared lowering emitted unchecked UInt64-to-Int32 `trunc`, which the validators/evaluator incorrectly refused because signedness changed. The Core contract now defines trunc as low-bit narrowing with destination-owned interpretation. Full Core Release solution build passed (0 warnings/errors), nine word/division Core HWIR regressions passed, and the current-Core SLLW candidate matched 8,372 actual pinned Golden PE vectors. Regenerated SLLW CIRCT RTL passed Icarus on all 8,372; Yosys source-model SAT passed for this candidate only. Receipt SHA-256 `eb1a58be38b7b7e919e663f7e8875c1afd6931ec740efdd2cddc5a6630e728eb`; current matrix SHA-256 `242e00602c22289cb71ba519943cf38eed2abe5183a274d98ecc656a29d82678`. The other eight regressions did not regenerate RTL. No binding admission, trusted handoff, P06 or stateful Processor proof is established. Integration remains INCOMPLETE.

### Matrix and portable-boundary test reconciliation — 2026-11-24

The current matrix is `CircuitWeave-main/artifacts/processor-20261114/integration-20261121/integration-matrix.json`, SHA-256 `242e00602c22289cb71ba519943cf38eed2abe5183a274d98ecc656a29d82678`; older matrix hashes above identify historical checkpoints. `python Tests/PortableDependencyBoundary.Tests.py` passes 8 assertions. The project graph contract is zero unconditional Core `ProjectReference` items and four Core references only under `EnableBridgeHwirLegacyFixtures=true`; the opt-in pinned guard refuses missing/empty SDK and Core pins. This static check is separate from evaluated default/opt-in MSBuild graphs and portable/pinned build evidence. Integration remains INCOMPLETE.

### 2026-11-24 — DivideWord current-Core native/formal recheck

After the shared `trunc` compatibility change, the exact archived pinned source/PE bytes were rehashed and DivideWord was rerun against current dirty Core `23c01bf5baeb1377a1317e2523a25f96713a1c75` (SDK 11.0.100). Core HWIR matched all 8,501 actual pinned PE vectors; fresh current-Core MLIR was lowered by CIRCT firtool 1.160.0, and Icarus 12.0 passed all vectors with zero mismatches. Yosys 0.52 SAT found no counterexample against a hand-authored direct 2-state source model (13,255 variables / 39,319 clauses); zero-divisor mutation failed at vector 0. Targeted test project build passed with 0 warnings/errors. Receipt: `artifacts/processor-20261114/divideword-current-core-20261124/receipt.json`, SHA-256 `da761f7213f9a70293844d1efee0ddbe0d1a82cca103bb23d06873e65468d18e`; current matrix SHA-256 `fa6912b2bae3c15b02d847290c0d4d10777f8ad4398800451c3b6ca5e4612f51`.

This requalifies only the current-Core combinational candidate for DivideWord. Source/PE hashes match retained archived pins; current Golden commit membership was not freshly verified from the Golden clone. Binding remains Proposed/NotAdmitted; no trusted handoff, independent P06, stateful Processor, hosted or Studio qualification is established. Integration remains INCOMPLETE.

### 2026-11-24 — DivideUnsignedWord current-Core native/formal recheck

Following the shared `trunc` compatibility repair, the archived pinned DivideUnsignedWord source/PE and vector bytes were rehashed and rerun against current dirty Core `23c01bf5baeb1377a1317e2523a25f96713a1c75` (SDK 11.0.100). Core HWIR matched 8,501 actual pinned PE outputs; fresh current-Core MLIR lowered via CIRCT firtool 1.160.0 to RTL that passed Icarus 12.0 for all 8,501 vectors with zero mismatches. Yosys 0.52 SAT passed against a direct hand-authored 2-state source model; a zero-divisor mutant failed at vector 0. The targeted test build passed with 0 warnings/errors. Receipt `artifacts/processor-20261114/divideunsignedword-current-core-20261124/receipt.json`, SHA-256 `272ec1af4a50128f20b1c5167ba5751ce680a986f51a45963681b193de36dfa1`; current matrix SHA-256 `aada31962b80bae0ed4666d5f8b8ba885a96328c82c16ae86818bad8ca829060`.

This qualifies only the current-Core combinational candidate for DIVUW. Golden source/PE commit membership is inherited from prior archived evidence, not freshly verified from the current Golden clone. Binding remains Proposed/NotAdmitted; trusted handoff, independent P06, stateful Processor, hosted, and Studio qualification remain open. Integration remains INCOMPLETE.

### 2026-11-24 — AddWordImmediate current-Core PE-bound native/formal recheck

Independent audit found the previous ADDIW fixture compared its saved vector file only with a custom BigInteger evaluator and did not load or invoke the pinned PE method. The fixture now verifies PE SHA-256/MVID/MethodDef `0x06001833`/signature and invokes that method for every vector before comparing with current Core HWIR. Against pinned source/PE bytes and 8,292 vectors, the updated harness passed; fresh current-Core MLIR lowered to CIRCT RTL and Icarus passed all vectors, while Yosys SAT passed against a direct low-32 wrapping-add/sign-extension source model. A zero-extension mutant failed at vector 4. Targeted and full CircuitWeave.slnx Release builds passed with zero warnings/errors; full-build log SHA-256 `c27b51b33843847d9510183aabb6324bde471e8c79b15fb7b8b5ca9dc3748d3c`. Test source SHA-256 `76fbf16e97d654c9b77b7d45bfd917b33e1d41235824da2bbbba6e61ac38fdcb`; receipt `artifacts/processor-20261114/addword-immediate-current-core-20261124/receipt.json`, SHA-256 `4aab66cd2c4ee4ed8f7819dbf456a8bee42f2a0ed29a8acfd7e440ab18b4cfd1`; current matrix SHA-256 `41b1bb0ecaee3abf7f667eda0b21dff9bdf18fab54224c327c5c96fa4a77906b`.

This remains one combinational candidate. Source and PE hashes match archived pins; the current Golden clone lacks the pinned commit object, so commit membership is inherited from prior evidence. No trusted handoff, admission, independent P06, stateful Processor, hosted CI, or Studio qualification is established. Integration remains INCOMPLETE.

### 2026-11-24 — cgroup-v2 effective budget and receipt v2 fail-closed hardening

The Linux sandbox previously attested only child cgroup controls. Core now parses unified `/proc/self/cgroup` membership and cgroup2 mountinfo into a unique visible ancestor chain, decodes mountinfo escapes, and explicitly treats mountinfo root `/` as insufficient proof that host ancestors are visible. A pure reducer computes the minimum CPU quota ratio, memory.max and pids.max over ordered visible samples plus an externally host-pinned hidden-ancestor sample. Before launch, the host requires a fresh SHA-pinned attestation bound to host id, cgroup namespace, mount root/point and delegated root; it must cover hidden ancestors, declare an exclusive budget, provide finite caps, and remain valid through the requested runtime plus cleanup. After descendant reaping, it rechecks the attestation, visible hierarchy digest and OOM-kill counters. Receipt v2 reports hierarchy completeness/digest, attestation SHA, effective limits and child memory.current; the Core structural verifier refuses missing/incomplete/drifted evidence.

Portable tests passed: hierarchy/attestation 22, effective-limit reduction 15, and sandbox receipt contracts 39. Full `CircuitWeave.slnx` Release build passed with 0 errors and 3,041 warnings (log SHA-256 `0546fdaf61acaf7df303999484ddbb0bfc8003f7a85e23a3a98c5c6788b8d104`). Receipt: `artifacts/processor-20261114/cgroup-effective-budget-v2-20261124/receipt.json`, SHA-256 `5c5ef2e66dd28cbcf219ec1cccef528913d63acd13807c8dd23042501338f9ff`; current matrix SHA-256 `0b867fc5235c307b8805649155b54b2f1602259cf707f7e2f139d634d696a9c6`.

Native Linux qualification and CWR3 replay were not run: current WSL has `/proc/self/cgroup=0::/non-systemd`, a cgroup2 mount with visible root `/`, but no host-budget attestation variables; `dotnet` is not available on WSL PATH. No host budget was raised or freshly attested. The previously observed 256 MiB ancestor remains prior evidence, not a new measurement. Positive receipt, trusted admission and integration completion remain unestablished. Closure requires the WSL/distro owner to provide a fresh externally verified exclusive budget attestation pinned in host deployment configuration and a reviewed Linux runtime for native replay.

### 2026-11-24 bounded scalar candidate recheck

Core added a PE-backed recheck of the saved CLMUL and high-multiply candidate RTL: actual pinned PE MethodDef `0x06001841` matched 346,336 and 359,488 vectors respectively, and Icarus passed every PE-output vector. Receipt SHA-256 `21f80eb602eaf8fae614890ba624ef10273e46ab6228dff793ffa08f76821e32`. This requalifies only saved combinational candidate artifacts; RTL was not regenerated in this turn, commit reachability is inherited, and neither P06/trusted admission nor Processor lifecycle is established. Integration remains INCOMPLETE.

### 2026-11-24 scalar ALU PE/RTL recheck

Core's actual-pinned-PE `ScalarAluOps.Compute` outputs now match the saved candidate RTL across CLMUL-family (346,336), high multiply (359,488), signed/unsigned word remainder (377,024), and wide DIV/DIVU/REMU (372,640) corpora; Icarus passed all rows, and a result-XOR mutation was rejected. Receipt SHA-256 `77ee371e8be08c64a0d8430ec76032b464c8afdcc2a50fa94e57bb2a17782ac7`. RTL was carried forward, not regenerated in this turn; the REM overflow sideband is not an architectural trap proof. Golden commit reachability is inherited. No admission/P06/trusted handoff or full Processor proof; integration remains INCOMPLETE.


### 2026-11-24 — signed compare HWIR validation and stale Golden capture stop

Fixed source-derived `slt64` typing in signed base comparisons, `SLTI`, and signed `MIN`/`MAX`: the u64 dispatcher ports are now bitcast to explicit signed 64-bit HWIR values before the signed comparison. This addresses the Core validator's qualified operand/sign/width contract without changing the source bit pattern. The Bridge portable Release build passed with compatibility and legacy fixtures disabled (0 warnings/errors). The Debug/Release ImportInventory fixtures passed; they evaluate imports only and do not run model targets.

An explicit compatibility-pinned Release CLI attempt compiled and ran against isolated clean Core snapshot `4409436ee77a87874fd596426ebe6b9c81fa10dd` (parent `23c01bf5baeb1377a1317e2523a25f96713a1c75`), Bridge HEAD `2be1784125193911ea635cb4fce0661b0b4a4bda` with a dirty Bridge worktree, SDK `11.0.100`, and the matched Release capture/report/graph/import tuple. Candidate HWIR validation passed the signed-compare checks. Core then refused at HWC1403 because the Golden root project file hash was stale. Rechecking all 1,135 captured root source rows against the current read-only Golden worktree found 10 changed source files; the root project hash is `5121e5a6d54028c3e21aab9961f231d082aee1caedd56646dbc3bfeef6a8c3ea` in capture versus `cecc3f84871ea6899e90261c703141619c370500383f00b77d07e2931a5995b9` currently. Golden HEAD is `1bfe41a49cf11c80fa6c6aee87accb54b78b331f`, not the pinned `6946ccab9d888f7dae0a4d8e18480f436138acd9`; the active clone has a dirty `.csproj` and lacks the pinned commit object.

The old evidence does not qualify the current Golden tree. No Studio snapshot, MLIR, RTL, simulation, or formal output was published from this attempt. Reopen capture after an isolated Release Csc run against exact current worktree bytes, and regenerate the semantic report, project graph and matching Release imports graph as one hash-bound tuple. Do not rewrite old hashes or edit the Golden tree. [Attempt receipt](C:/Users/Yuriy/Desktop/CircuitWeave-main/artifacts/processor-20261114/dispatch-current-core-regenerated-compat-snapshot-20261124/attempt-receipt.json), SHA-256 `f88fc21587a9bf5381aee52da86597c20f9ba01a80a4e88d368c118a17917a57. Integration remains INCOMPLETE.

### 2026-11-24 — свежий dirty Golden capture и текущий signed-REM RTL candidate

Предыдущая запись о stale capture выше сохранена как история и заменена этой проверкой. Для фактического текущего dirty Golden worktree выполнен Release Csc build с SDK `11.0.100` и внешними промежуточными каталогами: 6 Csc projects, 1,452 уникальных входных файлов, 1,138 root Csc sources и 50 generator outputs. Capture SHA-256 `5355938f42025ba63801677e9f226a3a39ca40f57d3f301e482084404f8a366c`; независимый Core replay/cross-check прошёл без root/dependency diagnostics. Golden HEAD `1bfe41a49cf11c80fa6c6aee87accb54b78b331f` dirty; закреплённый `6946ccab9d888f7dae0a4d8e18480f436138acd9` в рабочем clone отсутствует. Следовательно, source-derived dispatch использует именно текущие dirty bytes, а не pinned commit.

Core изолированная копия `e4cdb0b5421c7a378e9ed285472b9b47a0ba6c83` совместно с Bridge `2be1784125193911ea635cb4fce0661b0b4a4bda` построила свежий 86-opcode Studio/HWIR кандидат (`c9d66cbe1acb8cb0d935d6e660e0c74d07a2b2d5a229003921edab3117628689`). CIRCT firtool 1.160.0 сгенерировал 81 SystemVerilog module files for 86 opcode cases; Icarus 12.0 сопоставил модуль dispatch с 377,024 fresh dirty-worktree Golden PE outputs over all 86 selected opcode values (172 words, 2,192 per word), включая 2 fatal-sideband rows (PASS). MLIR SHA-256 `631f609a9777694ad22d52a70a3cb579ac650bccd582bd6d7f5ad96a56f4656c`; dispatch RTL SHA-256 `dc536f9e94f43055ae71c83b0a8cb43f73f893ace469a59e4917df1589c96906`; vector SHA-256 `6a49271514ab7fb0df3fcca002d01a9e348c5b30d7e010b81a69ff409bca9b30`. Receipt: `CircuitWeave-main/artifacts/processor-20261114/dispatch-current-core-regenerated-release-20261124-v6/rtl-candidate-receipt.json`, SHA-256 `313c79d52d3a959a8e74f219d01a77e1343d9947d8201dafa2a0074ab231bb93`.

Finite simulation corpus covers all 86 selected opcode values, with 2,192 rows for each of 172 full opcode words. It is not exhaustive over the operand space. Две строки fatal code 7 остаются caller-projected `UnknownException`; они не подтверждают architectural trap, retire или flush mapping. Это CandidateOnly, без formal equivalence, независимого P06, admission, trusted handoff, stateful Processor или Studio consumer read qualification. Матрица `integration-matrix.json` SHA-256 `2907bb784332bf4ece1307579b79936fb25175ee1fe3b5c125c169bbd506d344` и backlog фиксируют открытый scope. Интеграция остаётся INCOMPLETE.

### 2026-11-24 follow-up — Csc PE/PDB and same-capture 86-opcode dispatch replay

The exact current dirty-worktree Release Csc outputs are now preserved under `artifacts/processor-20261114/dispatch-current-core-regenerated-release-20261124-v6/csc-execution/`: PE SHA-256 `1e1600fef2e55429b0190df4c2d71ac25aa25890448fb55924a9eb34df59ae43`, PDB SHA-256 `9a3de4dea80f68c88c007978e070abda0e1d64775f8fca691f8ca62bd5833108`, reference output SHA-256 `d8ee9b2fae9fad61add017f5c8dc18b5cf3f228fe2250baebc1b47b8866b877e`. The PE's `ScalarAluOps.Compute` MethodDef is `0x060018c3`, MVID `7ab0d2a8-4707-4846-b411-f7a9f6505996`; Portable PDB CodeView GUID/age matches its PDB id and contains 1,188 documents (1,138 Csc source inputs + 50 generator outputs). All 2,452 recorded Csc input path/length/SHA rows were rehashed after capture/replay with zero drift. Receipt `csc-execution/output-capture-receipt.json`, SHA-256 `74d64d2f26ae31dd689313354b8dd37694b0c5642b11307ba91b138b69738878`.

The fresh dirty-worktree PE was invoked on all 377,024 dispatch-corpus rows across the 86 selected opcode values; every result matched the existing pinned-PE result, and the emitted vector file remained byte-identical at SHA-256 `6a49271514ab7fb0df3fcca002d01a9e348c5b30d7e010b81a69ff409bca9b30`. Current generated dispatch RTL then passed Icarus over those fresh-PE rows with 2 source-projected fatal-sideband rows. A whole-result XOR-1 RTL mutation failed at row 0. Receipt `csc-execution/current-dirty-pe-dispatch-rtl-receipt.json`, SHA-256 `3a62485959773855caf5e5f47e573c27bfa9b4832c74df0615bef634b81afa17`; mutation receipt SHA-256 `76c777f379571b5fdfd2798df52c5b227b5021be45700bfaec5807e0a7c89a5e`.

This is a same-capture, bounded software-to-RTL candidate result. The actual Csc task's output write and original downstream consumption are not independently intercepted. It uses current dirty Golden bytes rather than pinned commit `6946ccab9d888f7dae0a4d8e18480f436138acd9`; the finite corpus covers all 86 selected opcode values but not the full operand space; fatal sideband is not architectural trap proof. Core-owned independent re-emission/composition, P06, binding/trusted admission and stateful Processor evidence remain open. Updated matrix SHA-256 `2907bb784332bf4ece1307579b79936fb25175ee1fe3b5c125c169bbd506d344`; integration remains INCOMPLETE.

The PE/PDB inspector now also maps `Compute` MethodDef `0x060018c3` to 91 PDB sequence points in `ScalarAluOps.cs`; that document's SHA-256 `83de112d1233fbd7e9782512cbb7d37f4542d5e9987720883da9b1a978ca0d62` is one of the post-build-rehashed Csc input rows. This is method-to-source identity for the captured build only, not proof that the original Csc task consumed or later admitted the output.

The durable `csc-execution/` bundle now includes the MSBuild binary/diagnostic logs, exact source capture, project graph, matched imports graph, independent semantic replay and cross-check, compiler PE/PDB/refouts, generated-source files, helper scripts, vectors, and simulation/mutation logs. Its 34-file / 300,681,445-byte evidence manifest SHA-256 is `a6d72002092c640f8b76061fc7c8901545d2a75309fe862d46e7cad961deef3c`. The top-level current dispatch receipt is SHA-256 `313c79d52d3a959a8e74f219d01a77e1343d9947d8201dafa2a0074ab231bb93`; updated machine matrix SHA-256 `2907bb784332bf4ece1307579b79936fb25175ee1fe3b5c125c169bbd506d344`.

### 2026-11-24 trust/host recheck and remaining Core composition stop

Read-only remote recheck confirmed Bridge `origin/master` equals local `2be1784125193911ea635cb4fce0661b0b4a4bda`. Core local `origin/master` is `23c01bf5baeb1377a1317e2523a25f96713a1c75`, but live `ls-remote` produced no output after >150 seconds and was canceled; no Core live SHA is claimed. Branch-protection state for either repo is `NotVerified`: `gh` is unavailable and the available web API route could not read it. WSL currently reports cgroup membership `0::/non-systemd`, cgroup-v2 mount root `/`, visible `memory.max=max`, `pids.max=max`, `cpu.max=max 100000`, no `dotnet`, and no host-budget attestation variables. Hidden ancestor limits remain unknown, so no native Core replay/positive trust receipt is claimed. Receipt: `remote-wsl-budget-recheck.json`, SHA-256 `9cc316060427af897a1bc13cdaef77fb86ba0e023bdc94b96020af39645bcf92`.

A separate locally actionable Core gate remains: `QualifiedBridgeCandidatePipeline` reimports V3/V4 transport and then intentionally returns `refused before CWR3 composition`; its audit report says `cwr3Composition=NotRun`. Do not remove that refusal. Closure requires a Core-owned replay gate that independently derives the Csc profile, runs complete inputs in a host-attested isolated worker, and byte-compares generated sources plus PE/PDB/refout before composition; malformed, absent, stale or mismatched evidence must still refuse. This cannot produce positive execution evidence without the host attestation and runnable native runtime. The integration matrix SHA-256 is now `2907bb784332bf4ece1307579b79936fb25175ee1fe3b5c125c169bbd506d344`; integration remains INCOMPLETE.

The saved current-PE oracle runner source and output log are included in the Csc evidence bundle, with a repeat run confirming the 377,024-row vector file is byte-identical. The bundle is now 38 files / 300,682,666 bytes, manifest SHA-256 `a6d72002092c640f8b76061fc7c8901545d2a75309fe862d46e7cad961deef3c`; the refreshed top-level candidate receipt SHA-256 is `313c79d52d3a959a8e74f219d01a77e1343d9947d8201dafa2a0074ab231bb93`, and matrix SHA-256 is `2907bb784332bf4ece1307579b79936fb25175ee1fe3b5c125c169bbd506d344`.


### Scope correction — current dispatch corpus covers 86 selected opcode cases

The earlier note that the 377,024-row corpus exercised signed REM only was incorrect. A reproducible audit compared all 86 `opcode[15:0]` comparators in generated `scalar_word_dispatch_partial.sv` with the vector file: the sets match exactly. The file contains 172 complete opcode words, the 86 values with upper prefixes `0x0000` and `0x1234`, with 2,192 rows for every word. The Icarus bench checks `supported` and result equality on every row. The fresh current dirty-worktree Golden PE first matched every row against the archived pinned-PE outputs; current generated RTL then matched the fresh PE output rows. Coverage audit: `csc-execution/dispatch-corpus-coverage-audit.json`, SHA-256 `9e627daf6c84a393630ed7f75b9ca8bbcac9922a93d356453be7a7c11306012d`.

This upgrades the bounded simulation scope to all 86 selected combinational dispatch cases, not to exhaustive proof: it remains a finite 2,192-row corpus per opcode word, uses dirty current Golden bytes rather than pinned commit `6946ccab9d888f7dae0a4d8e18480f436138acd9`, and the two fatal sideband rows do not establish architectural trap/retire semantics. No independent P06, binding admission, trusted handoff, full Processor lifecycle, or Studio consumer qualification follows. Updated candidate receipt SHA-256 `313c79d52d3a959a8e74f219d01a77e1343d9947d8201dafa2a0074ab231bb93`; matrix SHA-256 `2907bb784332bf4ece1307579b79936fb25175ee1fe3b5c125c169bbd506d344`. Integration remains INCOMPLETE.


### Core re-emission mapping checkpoint — 2026-11-24

Core's strict Csc re-emission profile audit passed 19 synthetic assertions and mapped all 1,299 command tokens plus 2,168 pinned file rows from the real pinned V4 fixture to `CandidateOnly / MappingOnly`. The full Release solution build passed with 0 warnings and 0 errors. Receipt SHA-256 `317d28cc6aef631f5b8e5b331f72a1e9453a3f7afddee745616f094b79313c38`; matrix SHA-256 `bb63975b14c5bb66a43e35820a4ec277bb2090b1e8c662f8d97b509712e03860`. The audit does not invoke Csc or CSharpCompilation.Emit. The 15-check pinned V4 suite retains its refusal before CWR3 composition. `/pathmap` and unmodeled signing/resource/native/output modes remain fail-closed. Execution/replay are NotRun, admission NotAdmitted, and integration remains INCOMPLETE pending attested replay, trusted handoff, and independent verification.


### Core high-multiply formal follow-up — 2026-11-24

A local Yosys SAT attempt for the saved MULH/MULHU/MULHSU candidate did not complete: combined and isolated MULH solves ran without results and were stopped. No formal equivalence is claimed; the RTL still only has inherited finite actual-PE vector evidence and was not regenerated in this turn. Receipt SHA-256 `02c63f26bb0348e7ae22a8ebae27bca53676a37789f5eae8f268729ce35bcd2b`; current machine matrix SHA-256 `869e2c6657c658f041555ef7e6723daf19c6649733e45b42a8e24402810d62eb`. Closure requires a completing arithmetic-capable solver and mutation rejection. Integration remains INCOMPLETE.


### Core high-multiply split-product proof — 2026-11-24

The direct word-level product model timed out; a source-derived split-product/carry model allowed Yosys SAT to prove the saved MULH/MULHU/MULHSU candidate for all 128 two-state input bits. A result-bit mutant was rejected. Candidate RTL was carried forward, not regenerated; this remains candidate formal evidence and does not grant P06 or admission. Receipt SHA-256 `4b7d0854208c212c92414d5ef58757b6b864ef82da77d7fb370f07cf02865878`; current matrix SHA-256 `25f89adc649ebbf70be3bb9aa68e7d766a361106dcae2c154114f1d45818d2a3`. Integration remains INCOMPLETE.


### Current v6 Release import graph recheck — 2026-11-24

The existing v6 dispatch bundle has a matching Release capture/graph/import tuple: source capture `5355938f…8f8a366c`, project graph `99124e36…915ae9`, imports graph `9bb54ebc…e0c44`; inventory and replay crosscheck pin the same graph and capture. The prior mismatch finding applied to another bundle, not this v6. Exact fresh rerun remains unreconstructed because the original manifest and working/enum source input files plus exact imports-collector command/log were not preserved. No new artifacts were emitted. Matrix SHA-256 `25f89adc649ebbf70be3bb9aa68e7d766a361106dcae2c154114f1d45818d2a3`; integration remains INCOMPLETE.

### 2026-11-24 — v7 current-core publisher recheck

The explicit legacy-compat CLI build now passes in a clean temporary Core worktree at `32713671acc39a84c5279cf5fac8a864ded8d4d0`, derived from `23c01bf5baeb1377a1317e2523a25f96713a1c75` by two local commits: align the Core `Microsoft.CodeAnalysis` central pin to Bridge's 5.12.0 minimum, and carry the current capture-bound import-graph v2 validator. The pinned dirty Bridge worktree input was `worktree-sha256-9e37298d47ebf0dd9f905467bb49377c39dbace084c3e4a9f8cc1fbaf847c993`; SDK `11.0.100`; result 0 errors / 2,865 warnings. This is an isolated compatibility build, not a clean published Core/Bridge release.

A fresh `studio publish-working-word-dispatch` run returned `candidateOnly` and regenerated snapshot `c9d66cbe1acb8cb0d935d6e660e0c74d07a2b2d5a229003921edab3117628689`, HWIR `a3270564a3ec27aa0d5abe381be581af2d322871262199ff1e7a60f131dd2f04`, and MLIR `631f609a9777694ad22d52a70a3cb579ac650bccd582bd6d7f5ad96a56f4656c`. The historical v6 raw inventory/manifest/command was absent; v7 used the tracked sample manifest and a derived raw inventory with producer annotations removed before the publisher revalidated and rebuilt them. The sample pins SDK `11.0.100-rc.2.26504.105`, so it does not establish current-SDK admission. The Golden input was the dirty worktree at `1bfe41a49cf11c80fa6c6aee87accb54b78b331f`, not the pinned commit bytes.

The 377,024-row current-dirty-PE/RTL result belongs to the identical snapshot digest and is inherited from v6; no v7 RTL emission, simulation, formal proof, Studio-consumer load, independent admission, or trusted receipt was run. Keep the exact v6 reproduction open. Closure still requires a fresh pinned-Golden capture, manifest and raw-inventory inputs matching SDK 11.0.100, regenerated RTL and independent verification, followed by the protected trusted handoff. Receipt: `C:/Users/Yuriy/Desktop/CircuitWeave-main/artifacts/processor-20261114/dispatch-current-core-regenerated-release-20261124-v7/receipt.json`.


The v2 fail-closed check also rejected a stale `sourceCaptureSha256` (exit 2 / `HWC1403`) before publication; no artifacts were emitted in the negative workspace. Current remote refs remain unobserved: bounded `git ls-remote` attempts yielded no output and GitHub branch/API fetch was restricted.
Static package integrity check passed for all 8 `cwstudio.project/v1` artifact references (length and SHA-256); the Studio consumer itself was not launched. Updated v7 receipt SHA-256 `bb01ed6bf0f106a599f7ca5821204b8e93f2fda404b0c46f492e80eb9a3c5f99`.


### 2026-11-24 — Exact pinned-byte Windows Release Csc capture

A clean temporary checkout was created from pinned Golden commit `6946ccab9d888f7dae0a4d8e18480f436138acd9` with `core.autocrlf=false` and `core.eol=lf`. All 3,530 tracked regular blobs were byte-identical before and after Release builds. Windows SDK `11.0.100` was used; Golden declares `11.0.100-rc.2.26504.105` with roll-forward, so this is a separately pinned toolchain candidate, not a silent replacement for the declared pin.

The baseline build passed (45 warnings, 0 errors). A second build instrumented the Csc task and child process, emitted generated source files, and passed with 5 successful task events, 5 pinned compiler processes, 5 saved response files, and ordered task-command/RSP crosswalk. The root project response names 1,104 source files, 180 references, 10 analyzers, 2 analyzer configs, and 3 embedded inputs. Post-build hashing and rehashing recorded 1,299 existing file rows with no drift. Fifty generated outputs (583,942 bytes) are retained by content hash. PE method/IL comparison across default and `EmitCompilerGeneratedFiles=true` builds found 30,561 method definitions and no method-body, signature, or method-flag differences; PE/PDB bytes differ.

Receipt: `artifacts/processor-20261114/golden-pinned-6946ccab-release-20261124/pinned-windows-csc-capture-receipt.json`, SHA-256 `BAD86844AC8B0D63603214006F14CC11CC5ABDB932CBC515259AEA6717CE0B13`. Machine record: `pinnedGoldenWindowsCscCapture20261124`. The probe holds the response path read-only during child execution but does not prove Roslyn consumed those exact bytes; the recorded input inventory is post-build hashing, not OS read attestation. Non-root task inputs are not fully byte-inventoried. No independent Core admission, CWR3 composition, hardware verification, trusted handoff, or Studio load follows. Integration remains INCOMPLETE.


### 2026-11-24 — Pinned Golden Csc capture → independent Core v2 import

This follow-up removes the earlier gaps in the pinned Windows capture bundle. Five successful Csc task/process/response-file crosswalks were captured from a raw-blob checkout of Golden commit `6946ccab9d888f7dae0a4d8e18480f436138acd9` (tree `243a67c8ef462c1da2d1f06df9bee2c31508cb40`). Before and after both Release builds, all 3,530 tracked regular blobs were exact. The active Windows SDK was `11.0.100`; Golden declares `11.0.100-rc.2.26504.105` with roll-forward, so SDK compatibility remains explicitly open.

The generated v2 bundle records all five Csc response closures and 50 generator outputs: 2,223 entries, 2,246 chunks, 86,325,070 bytes, and 2,158 argument-bound inputs. Core `HardwareBridgeCscCaptureImporterV2.Import` independently rehashed it; `HardwareBridgeRawCaptureStagerV2.Stage` produced a fresh staged v2 workspace and independently reimported it. Four negative checks refused a wrong external index pin, wrong capture-root pin, altered chunk, and missing chunk. The Core source was the current dirty HEAD `23c01bf5baeb1377a1317e2523a25f96713a1c75`; importer/stager source SHA-256 values are `ffc483144b71190a7c2bb78009b0703ee6bf41c2a107533ac8bf98e7067d7725` and `68c66287cea46513228ad6a948a66627d74a124571bcbfc5d02ad5b4c3a69400`. Admission remains `NotAdmitted`.

Raw index SHA-256 `AEFEABF11C004F8BE4DB3997538CF18852209501B9B3C336773DE60EA741AA08`; capture-root SHA-256 `BF5A40F5E32ACF4165760AD892373DAEB63AA4CE5D6F63D641855F1F9680D507`. Staged index `22AABE251BD8E8DD366BB4210D120521BFE4891D8CF43986E6E72B6BD5D619C4`, closure `BA787AF77C28011AD22BA3F9C437540E085EC80F61FD4234358D8D2BE200210B`, transport map `C3A6CB109A054E619D2E828A32349EEC93A1B997A908D905108A8C09A478548F`, and Csc closure profile `F19FE6934FF588A923A60250FA55DFC4E661CC7DACAC99DF98B249BA96E261DD`. Output manifest pins 15 PE/PDB/refout files across all five Csc tasks (SHA-256 `1C10A35781C9B1DDE5F4D2EF9F021C378D3A83C3504A9A6F8FE85322E2366280`). Slice receipt: `artifacts/processor-20261114/golden-pinned-6946ccab-release-20261124/pinned-golden-core-csc-integration-slice-v1.json`, SHA-256 `EDD793BE20964A4F5A0937849971AA25E7FB8129B0C28B5E99C7224419893D06`. Machine record: `pinnedGoldenCoreCscIntegrationSlice20261124`.

The capture is a post-build byte inventory: the probe held the response path read-only during execution but did not prove Roslyn consumed the exact saved bytes. No owner-approved source scope, P06, CWR3 composition, RTL, simulation, protected trusted handoff, or Studio consumer load is established. Existing v2 capture claims sourced from a CRLF-converted checkout are not substituted for this exact-blob capture. Integration remains INCOMPLETE.


PE/PDB identity is directly paired for the current generated-output Csc build: PE `c83c09a84f52aca5f5dc7f20c2844f638ce077adeabfd1953eef5e3380bfdf70`, PDB `765cd64c3cce42a34ac3c9511c0dd0240c2963a524abd5738a1c591ca7dd45c4`, MVID `76cddc5e-7202-4948-a401-e18d444b70f1`; CodeView/PDB identity matched. `ScalarAluOps.AddWord` is MethodDef `0x06001834`, and its PDB document checksum matches exact pinned source SHA-256 `f1726c298722496f1218aeee41de7c8f75c79c26a36c0e606cae7e8b5123c5fe` (2 sequence points). Audit SHA-256 `98C790D59C9733C01E53E793E80AC34FDFB7BBDE7F3D2E29309DBB4B90A4DF39`. This confirms method/source identity for the build, not Csc input consumption or hardware equivalence.


### 2026-11-24 — Pinned PE CLMUL family and fixed-bit formal check

The exact pinned-source Windows Release PE (`c83c09a84f52aca5f5dc7f20c2844f638ce077adeabfd1953eef5e3380bfdf70`, MVID `76cddc5e-7202-4948-a401-e18d444b70f1`, Compute MethodDef `0x06001841`) was run on the existing 346,336-row CLMUL-family candidate corpus for opcode values `0x003a`, `0x0160`, and `0x0161`. The source file hash was the exact pinned blob `f1726c298722496f1218aeee41de7c8f75c79c26a36c0e606cae7e8b5123c5fe`; current dirty candidate source normalizes to those bytes. The PE output vectors matched the existing corpus byte-for-byte. Icarus then compared all 346,336 pinned-PE rows against saved candidate RTL and passed.

Yosys 0.52 proved 192 fixed output-bit cones (64 low, 64 high, 64 reverse) against an independent XOR-of-bit-products model directly derived from pinned source window starts 0/64/63. All 192 returned `SAT proof finished - no model found: SUCCESS`. A model mutation changing high-window start 64→65 was rejected with counterexample `op1=0x2`, `op2=0x8000000000000000`, output bit 0. The earlier combined 552,106-variable proof was stopped before a result and is retained only as an unqualified attempt.

Formal slice receipt: `artifacts/processor-20261114/golden-pinned-6946ccab-release-20261124/clmul-formal-slice-receipt.json`, SHA-256 `D5D705220FB085989D5139B5B1A1226A99C55D069A5915EF2EAC0C5A50C34C1B`. Machine record: `pinnedGoldenClmulFormalSlice20261124`.

This is CandidateOnly. The saved candidate RTL (`e5176bb397922da4112717275403266ad95685bae2ca90c2457e96fe5276463a`) was carried forward and was not regenerated by current Core HEAD; the source model is independent but source-derived, not a separate authority. No current Core owner scope/admission, CWR3 composition, P06, trusted receipt, full Processor lifecycle/timing contract, or Studio load is established. The pin/toolchain mismatch between Windows SDK `11.0.100` and Golden RC2 `11.0.100-rc.2.26504.105` also remains open. Integration remains INCOMPLETE.


### 2026-11-24 — SDK pin compatibility comparison

To qualify the explicit Windows `11.0.100` versus pinned RC2 toolchain difference, the exact-byte RC2 PE (`ae6c12d93b834405db2b5f8ec0442073d83b3a0901b9f7a7497afeaafdeb72cd`) was compared against the exact-byte Windows default Release PE (`efc1ab689a2667b6ed669fd0e0e6d43456beb9c8b3964c03f26de6de751c5d5d`). The bounded comparer found 30,561 MethodDefs on both sides, zero missing methods, zero raw IL body differences, and zero signature/method-flag differences. Comparison receipt `rc2-vs-windows-baseline-pe-il.json`, SHA-256 `A1FC192DF0D26F248033B6F3CB61F74AB361CDFE671B5F407CADAE3DFB011D10`. The generated-source-output Windows PE was also compared to RC2 with the same method-level result (receipt SHA-256 `A800FE49D0E51AE09449F0C18FEBE160130F847437C109835440F07CFE02C27C`).

This resolves method-level IL compatibility for these exact inputs and configurations only. It does not establish full PE/PDB identity, generator/analyzer/reference equivalence, whole compiler profile compatibility, or replace the declared RC2 pin. Updated Csc integration receipt SHA-256 `EDD793BE20964A4F5A0937849971AA25E7FB8129B0C28B5E99C7224419893D06`; updated CLMUL receipt SHA-256 `D5D705220FB085989D5139B5B1A1226A99C55D069A5915EF2EAC0C5A50C34C1B`.

### 2026-11-24 — Portable default and pinned opt-in recheck

The portable default now gates the existing Roslyn 5.12.0 `PackageReference` on `EnableBridgeHwirLegacyFixtures=true`, alongside the already gated Core project references and legacy fixtures. The changed project SHA-256 is `C8194BE0F3A57B0FB87E82237A0DD797BEC0ED30DCBE9E3E92D93AF05C786D44`. SDK `11.0.100` portable Release build passed with 0 warnings and 0 errors; evaluated `net11.0` dependency graph contains no package or project dependencies. The opt-in build passed in a TEMP copy of Bridge against isolated clean Core compatibility HEAD `32713671acc39a84c5279cf5fac8a864ded8d4d0`, with 0 errors (1,064 existing CS1591 documentation warnings). Negative checks refused missing Core pins as `HBINT0101 MissingCoreFrontend` and an incorrect SDK as `HBINT0104 WrongSdk`.

Receipt: `C:/Users/Yuriy/Desktop/CircuitWeave-main/artifacts/processor-20261114/bridge-portable-pinned-optin-20261124/receipt.json`, SHA-256 `0EF0005733817084518C347A17AA2D39FE651635347E2156567A5DEDF2F9983B`. The temp copy substituted an explicit Git executable path because this build shell's `cmd` PATH omitted Git; repository project files contain no such path. This does not prove positive pin compatibility with current dirty Core HEAD `23c01bf5baeb1377a1317e2523a25f96713a1c75`, hosted CI, trusted handoff, or any source admission/hardware criterion. HB-INT-01 portable boundary is locally qualified for the recorded bytes/configuration; overall integration remains INCOMPLETE.

### 2026-11-24 current dirty-Core V2 profile composition recheck

Command: `dotnet run --project tests/CircuitWeave.CWR3.QualifiedCandidate/CircuitWeave.CWR3.QualifiedCandidate.csproj -c Release` on SDK `11.0.100`, Core HEAD `23c01bf5baeb1377a1317e2523a25f96713a1c75` (dirty worktree). The synthetic multi-file V2 profile reaches the current Core CLI and independently reconstructs its ordered source/reference set, PDB/PE, owned method/helper graph, and P02 resolution; `SymbolResolution`, `SourceAdmission`, and typed HWIR validation complete. The fixture backend intentionally refuses RTL, leaving independent verification and evidence promotion `NotRun`. The fixture's mutated/reordered profile, stale/altered input pins, wrong MethodDef, altered PE/PDB and generated-source-without-separate-output-profile cases fail closed.

Receipt: `artifacts/processor-20261114/cwr3-profiled-composition-recheck-20261124/receipt.json`, SHA-256 `B31429A5FFDA26F281BCACB8D3CD95BB5654B9FCD17CA45FA25DBAB8DE02DA99`; raw run log SHA-256 `925E13C50520B9D06B43323ED438EEF5F54B3C25585646A62DDE08299F9F48F1`. This repairs the historical description that V2 profile was not passed to composition for this supported non-generated-source route. It remains a synthetic candidate composition witness; actual captured generated-source V4 still refuses before CWR3, no trusted admission or RTL/P06 result follows, and Processor integration remains INCOMPLETE.

### 2026-11-24 P02→P06 provenance verifier wiring checkpoint

The qualified-only `ProductionCompilerComposition` now wires the same `RoslynIndependentBindingSourceVerifier` used by its resolver into `PinnedAddWordOracleProvenanceGate` through `HardwareVerificationLadder`. The ordinary `Create(PinnedGoldenRoslynInputs)` path remains fail-closed and unchanged. The CWR3 qualified-candidate fixture and P06 verification-ladder contract fixture both exit 0 on SDK `11.0.100`; P06 fixture symlink negatives are explicitly NOT RUN on this Windows account because link creation is denied. The P06 test harness no longer calls Unix chmod on an inert text fixture and now classifies denied symlink creation as an environment skip.

Receipt: `artifacts/processor-20261114/p02-p06-provenance-wiring-20261124/receipt.json`, SHA-256 `D8E0871D9EBBD6B9142647FF75790993732204E2291F01B162B9A1C2CA6E464A`. Wiring is not P06 qualification: qualified composition still supplies no session-bound material provider, pinned Golden oracle, simulator, independent proof/equivalence tool or stage attestation. P06 remains Blocked; the production fixture intentionally ends with RTL refusal and P06/EvidencePromotion NotRun. No binding is admitted and integration remains INCOMPLETE.

### 2026-11-24 — V4 output-path ordering interop correction

A read-only Core/Bridge audit found an availability defect: Python sorted output rows by Unicode code point, while `BridgeSegmentIndexImporterV4` enforces `StringComparer.Ordinal` over UTF-16 units. For legal paths containing U+E000 and U+10000, the two runtimes ordered the rows oppositely. Bridge output capture and its independent verification now use UTF-16 ordinal keys; Core V4 importer coverage includes actual Roslyn-emitted PE/PDB/refout fixtures under those paths.

Python 3.15.2 Bridge contract suite: 12/12 passed. .NET SDK 11.0.100 Core V4 importer suite: 34 checks passed, including the new cross-runtime path case. Receipt: `C:/Users/Yuriy/Desktop/CircuitWeave-main/artifacts/processor-20261114/bridge-csc-output-utf16-ordinal-20261124/receipt.json`, SHA-256 `92256B05C3FCC5A84D3EB8C621D1AE458747A868E8399A74EC3DC89803C8D8E2`.

This corrects only transport ordering. Outputs are generated fixtures; no production Csc task execution/response consumption, trusted provenance, source admission, RTL or P06 is established. Integration remains INCOMPLETE.

### P02/P06 call-path correction — 2026-11-24

The qualified composition contains the verifier-backed `PinnedAddWordOracleProvenanceGate`, but a source-level call-path audit confirms that the current CWR3 CLI does not execute it: the CLI uses `ResearchCandidateAsync`, whose coordinator returns immediately after candidate RTL emission when `acceptedPath=false`. The independent ladder runs only in accepted `CompileAsync`, which requires a verified singleton binding/lock and owned session. The gate wiring is therefore configured but unreachable from the current CWR3 candidate invocation; no P06 stage has run. Audit receipt: `C:/Users/Yuriy/Desktop/CircuitWeave-main/artifacts/processor-20261114/p02-p06-callpath-recheck-20261124/receipt.json`, SHA-256 `7C2B9AB477B8EE4E7E32DB8340D334C7320938E2254974E5E93146338B778FDC`.

Closure requires an externally approved Verified binding/lock, Core-owned material capture after HWIR/RTL are frozen into the session, exact Golden source/PE/PDB and complete closure, plus qualified independent simulator/formal/equivalence/attestation authorities. Do not route Proposed CWR3 bindings through accepted compilation or infer authority from Bridge pins. P06 remains Blocked and integration remains INCOMPLETE.

### Runtime direction — 2026-11-24

CircuitWeave application and integration surfaces are .NET/C# first, with Windows as the primary operating environment. Python is allowed only as the minimum wrapper required at a Windows launch boundary; integration utilities default to C#, and existing Python capture tooling is transitional until a C#-equivalent path is qualified. Java is not a target runtime. Linux is supporting infrastructure only for a specific launch/tool boundary that cannot run on Windows. Core retains independent C# V2/V4 import and admission authority. The current Python V4 output-capture script is auxiliary producer tooling and does not attest Csc task execution. Any C# producer replacement must preserve external pins, safe no-overwrite snapshots, Core revalidation and portable Bridge/Core build separation.

### 2026-11-24 — Clean portable restore/build artifact isolation

The prior build's Core DLL lines came from stale `.artifacts/obj/HardwareBridge.Hwir/project.assets.json` left by a legacy opt-in build against TEMP Core compatibility. Current source evaluation was already portable; `--no-restore` reused the stale assets. A fresh isolated `ArtifactsPath` in TEMP followed by ordinary `--no-restore` Release build passed with zero warnings/errors, zero evaluated ProjectReference/PackageReference items and zero restored project references. Evidence: `artifacts/processor-20261114/hb-portable-clean-artifacts-20261124/receipt.json`, SHA-256 `2423E3DB104006C34CD9E82D3287616F578443C998F659C38D42F3BD8DFA08AB`. Existing intermediates were preserved. This closes the local stale-assets ambiguity only; clean-checkout hosted evidence and trusted handoff remain open.

### 2026-11-24 — Core C# V4 output capture and raw-pin binding

The Core C# CLI now provides `bridge capture-csc-outputs`, which consumes the pinned V2 closure, Csc profile and output manifest, makes a fresh combined workspace, emits V4 output rows/index/witness and re-imports with Core V4. The synthetic Roslyn producer/import suite passed 40 checks; receipt is in Core `artifacts/processor-20261114/bridge-v4-csharp-output-producer-20261124/receipt.json` (SHA-256 `EC0512537EDE5481BE431FF4FCDA9AC654B4086F3832FDE3BF3AFCB6D6F50ED5`). No trust/admission claim is made.

The end-to-end fixture exposed that V4 `BaseIndexSha256`/`BaseClosureSha256` bind the raw HardwareBridge capture pins; the V2 base index/closure are separate request pins. The Python transitional output producer currently writes V2 pins into V4 `Base*` and lacks separate raw pin options. Its independent Python verification does not establish Core V4 interoperability; update or retire that producer before relying on its V4 artifacts.

### Raw capture pin alignment follow-up — 2026-11-24

Bridge's transitional Python V4 helper now takes explicit raw capture index/root pins separately from V2 index/closure pins and writes the former to V4 `Base*`. Its 12/12 Python contract tests pass. Core's native C# producer and independent V4 fixture suite pass 40/40 separately. Receipt for both results: Core `artifacts/processor-20261114/bridge-v4-raw-pin-parity-20261124/receipt.json`, SHA-256 `457E2BA34CE86640DBC964BE14C9CF11DB2D9C0A1816D1AD62E18681851A9FC0`. No Python-produced workspace has yet been imported by Core V4, so cross-runtime producer-to-reader parity remains unqualified.

### Expanded candidate producer negatives — 2026-11-24

The C# Core producer fixture now also refuses a wrong raw-capture pin and a manifest whose PE output digest is stale. Fresh isolated Release build succeeded with zero errors; 42/42 fixture checks passed. Bridge's raw-pin-aware Python contract suite passed 12/12 separately. Receipt: Core `artifacts/processor-20261114/bridge-v4-csharp-producer-42-20261124/receipt.json`, SHA-256 `87CF53AECD160939CC81E6C14A62C324EDCC7C5F876EF8CFCD60357E09215716`. Python-generated output has not yet been imported by Core V4; all results remain synthetic candidate scope.

### Python V4 → Core V4 pinned Golden cross-import — 2026-11-24

Correction to the preceding same-date status: Core V4 has now imported the exact index/witness bytes emitted by Bridge's Python V4 producer for the pinned Golden output snapshot. Parent HB index/root pins are `6cb4dc2e4fb15cebb8a3a629ec6521c5535423c9970ea88fe34f7fccdfda6f4e` / `0046f8dde1a242beb95e2c587c4f7d96e54ee20b17d2b33ae9d9415bddc271dc`; Python V4 index/closure/witness pins are `a067d2b7a5616f364720771b71aeceff964930162938082ec340c2e088019ed6` / `dee3490f7d5328a892e30e21ea27311e4f7db772b8726de29da3ac4a04df921f` / `323fe08e7f0461a342ab550c4b658abc6c550ac5808a008ea37f44a7ebf124fd`. Core independently checked its separate V2 closure and Csc profile, then accepted three output rows as Candidate. Wrong raw index, altered output payload and stale witness pin were each rejected. Python suite passed 12/12 and Core suite 42/42. A replay script reruns parent verification, Python capture and Core import with isolated .NET artifacts and checks the pinned SDK/runtime, both HEADs, and source hashes. Evidence: Core `artifacts/processor-20261114/bridge-v4-python-core-import-20261124/receipt.json`, SHA-256 `D06B7B9835AADF2D0E2954C969CFC001E0B6F4DA1702A281D49EE48E8919CEA8`; snapshot manifest SHA-256 `625A871B78CDF57701824245BFB2963101CE7DB353A14306F46CF90E645379C0` (2,276 files verified). This proves pinned candidate-byte interoperability only. Csc response consumption, task provenance, trusted handoff, admission and hardware/Processor verification remain open; the product remains Windows/.NET/C# first, with Python auxiliary only. Integration remains INCOMPLETE.

Remote trust recheck: Bridge origin `master` was observed at `2be1784125193911ea635cb4fce0661b0b4a4bda`, matching local HEAD. Core origin master could not be established in this environment (`git ls-remote` did not return; unauthenticated GitHub API ref lookup returned 404; `gh` CLI is absent). No current protected-master or hosted Actions run evidence is claimed. A protected trusted receipt remains a hard gate and was not bypassed.

### Historical next .NET-first capture migration (superseded below)

The current Python `segmented_csc_capture_v2.py` remains the raw HardwareBridge V2 writer. The separate `EvaluatedProjectGraph` application already targets .NET 11 and has no mandatory Core dependency; the next implementation slice is an additive C# writer consuming existing graph/observation bytes and emitting the same raw HB V2 `commands`/`entries`/chunks, followed by independent Core raw importer and stager verification. This must preserve canonical bytes and the full safe-copy/source-map/file-pin contract, without introducing Bridge→Core references. Observation generation is a separate remaining Python boundary: C# `CscTaskLogger` does not yet produce the full observation schema. No Python runtime migration or producer parity is claimed until exact HB index/chunk bytes and negative cases are independently qualified.

### 2026-11-24 — .NET raw HB V2 writer parity (synthetic transport only)

An additive .NET 11 `capture-v2` mode now exists in `HWIR/EvaluatedProjectGraph`; it consumes the established graph and observation inputs and writes raw `hybridcpu.hardwarebridge.csc-closure-index/v2` plus chunks. After independent review, the writer now binds observed node IDs to evaluated project paths, bounds all generated-output path traversal by count and timeout, and creates the output directory exclusively on Windows. Re-run on that corrected synthetic fixture matched Python index bytes (3,995 bytes, SHA-256 `30385be5369b48ff5af05897e83434852865aac724ead747c0696f8c73b86092`) and all six chunk hashes. Independent Core `HardwareBridgeCscCaptureImporterV2` accepted the actual C# bundle as `UnqualifiedByteInventory`/`NotAdmitted` (1 command, 6 inputs/chunks, 219 bytes); a wrong index pin was rejected. Bridge Python fixture suite passed 29 tests (3 platform-dependent skips); fresh isolated Release build on SDK `11.0.100` completed with 0 warnings/errors. Unknown Csc argument and stale graph pin were refused before output creation. Evidence and exact inputs/source hashes: Core `artifacts/processor-20261114/hb-v2-dotnet-writer-parity-20261124/receipt.json`.

This closes serialization parity for this fixture only. The C# writer still consumes prebuilt graph/observation; observation generation and witnessed production Csc/response consumption are not implemented/proven. No pinned Golden raw closure, staging qualification, trusted receipt, source admission or hardware verification is claimed. Python remains a transitional fixture/orchestration wrapper pending C# observation production; .NET/C# remains the Windows application runtime priority. Integration is INCOMPLETE.

### 2026-11-24 — C# Windows task/RSP bundle assembler

An additive .NET 11 assembler now emits the existing `hybridcpu.hardwarebridge.windows-csc-task-rsp-bundle/v1` contract from externally pinned task events, sampled process rows, task/RSP crosswalk, capture receipt, compiler image and copied response bytes. The synthetic fixture suite passed 3/3, including stale event pin and altered response rejection before publication. On the existing Windows capture for pinned Golden `6946ccab9d888f7dae0a4d8e18480f436138acd9` / tracked tree `243a67c8ef462c1da2d1f06df9bee2c31508cb40`, C# and Python emitted identical 281,939-byte bundle SHA-256 `786b2896e1c7885365212bdcd9f3e100e6a9904d7f930991614b87a03c52a58e` for five observed task/process/response rows. Evidence: Core `artifacts/processor-20261114/hb-rsp-bundle-dotnet-golden-20261124/receipt.json`, SHA-256 `f5411356412a3d165c2ad51964b9a0dcdb21d944b29eb231d61eb42264f50f22`; immutable output bundle: `.../windows-csc-task-rsp-bundle.v1.json` with the same SHA.

The bundle explicitly retains `workingDirectory=NotRecorded`, `ResponseBytesConsumed=NotEstablished`, source/input-byte closure `NotEstablished`, and admission `NotAdmitted`. The underlying process evidence is sampled and crosswalk equality does not prove Csc read those exact response bytes. This adapter cannot yet produce raw HB closure V2 or feed the independent Core importer. Next: add a real Windows cwd/read-buffer witness and bind it with evaluated source inventory before C# observation generation can emit a qualified raw closure. .NET/C# remains the Windows application priority; Python is only the current oracle/auxiliary wrapper, Java is not an application runtime, Linux is supporting only. Integration remains INCOMPLETE.

### 2026-11-24 — Pinned Golden Csc launch probe, Debug/Release

The existing .NET CscLaunchProbe was run on a clean TEMP clone of pinned Golden commit `6946ccab9d888f7dae0a4d8e18480f436138acd9`, leaving the original dirty worktree untouched. With SDK `11.0.100-rc.2.26504.105`, Debug and Release builds each intercepted five Csc child launches; compiler path/hash pins matched and all children exited 0. For each child the probe recorded CscToolTask cwd and snapshotted the response bytes while holding a handle that denied write/delete until child exit. Before/after source manifests were identical (3,530 tracked files; SHA-256 `752c3ed30ccdccce4e211df4e1c331cc13b7cc9bc218452d96a5694ff1d27e36`); builds had zero errors (49 Debug / 45 Release warnings). Package and exact per-file hashes: Core `artifacts/processor-20261114/golden-csc-launchprobe-dotnet-20261124/receipt.json`, SHA-256 `03406f33561d1cf722e643bf55ba3a3a4930de19884f2ec875ae0c8334846cd5`.

This establishes the launcher's pinned child process, cwd and stable response snapshot only. Csc read/consumption is `NotEstablished`; the new launch run is not directly joined to existing task-event evidence (`directJoin=NotEstablished`); input closure and Core admission remain absent. Next: capture task identity during the same instrumented build, then construct and independently verify C# source-byte inventory. Integration remains INCOMPLETE.

### 2026-11-24 — Same-run CscTaskLogger ↔ CscLaunchProbe join

Both existing C# observers ran during the same Release build of the clean pinned Golden clone. The five task events join one-to-one to five Csc child launches: ordered expanded Csc arguments after executable and `/noconfig` equal Win32-parsed tokens from each copied RSP; task project directory equals probe cwd; and unique event times enclose launch, snapshot, child start and exit. All child exits were 0, compiler path/hash pins matched, and all response byte hashes/lengths matched. The 3,530-file source manifest stayed unchanged (SHA-256 `752c3ed30ccdccce4e211df4e1c331cc13b7cc9bc218452d96a5694ff1d27e36`). Release build: 0 errors, 45 warnings. Receipt: Core `artifacts/processor-20261114/golden-csc-task-launchprobe-joined-dotnet-20261124/receipt.json`, SHA-256 `c38871f44a4dd0f1960e77d46a6b594d0fbfa2d5145e641540947eb7490295c9`; all 15 evidence hashes were independently rechecked. `verify-same-run-join.py` replays the mapping and reports `PASS 5/5`.

This establishes a same-invocation task/process/response identity join. It does not establish that Csc read or parsed the captured RSP bytes. Full Csc source-byte closure, independent Core import/admission, trusted handoff and hardware behavior remain open. Next: use the bound C# task and response facts with evaluated Compile inventories to assemble source bytes; do not promote response-read claims without OS read-buffer evidence. Integration remains INCOMPLETE.

### 2026-11-24 — .NET evaluator Release globals fixture

The .NET 11 `EvaluatedProjectGraph` CLI now accepts additive `--global-property=NAME=VALUE` arguments while preserving its legacy defaults (`Configuration=Debug`, `EnableInternalTestHooks=true`) and prior-inventory positional argument. The Windows PowerShell fixture passed against a freshly built SDK `11.0.100-rc.2.26504.105` tool: Debug and Release selected different configuration-conditioned Compile items and node identities, while the hook default stayed true. A Release evaluation of the clean pinned Golden clone (`6946ccab9d888f7dae0a4d8e18480f436138acd9`, tree `243a67c8ef462c1da2d1f06df9bee2c31508cb40`) produced five nodes and 1,101 root Compile items; output graph SHA-256 `0b962bf78bad350dfa959ef30fdf3689a9c8f40257b7863fe352e1bce013576a`. Receipt: Core `artifacts/processor-20261114/hb-evaluator-globals-release-20261124/receipt.json`, SHA-256 `917e107fcbad45a5e8886adf8d9edc1c05cd6f937a2985736daa1b9dbaf76403a`.

The same-run Golden Csc inputs contain 10 analyzer and 2 analyzer-config arguments per task, without `/generatedfilesout`. Evaluation does not run targets, so this graph alone cannot close target-generated or source-generator inputs. C# observation V2 remains fail-closed until generator outputs and all other compiler input bytes are captured and bound. Windows/.NET/C# remains the application priority; Python is only a required boundary/orchestration wrapper, Java is not an application runtime, and Linux is supporting infrastructure. Integration remains INCOMPLETE.

### 2026-11-24 — Same-run Csc source arguments mapped to evaluated Compile

Correction to the preceding TODO: the source path mapping was independently completed for the five same-run Golden Release tasks. Win32 `CommandLineToArgvW` parsing of each pinned RSP, with `/embed:` excluded from Csc source arguments, yielded 1,104 / 29 / 18 / 9 / 31 ordinary source arguments. Each set maps bijectively to its Release graph Compile items plus three target-generated MSBuild source files: `.NETCoreApp,Version=v11.0.AssemblyAttributes.cs`, `<Assembly>.AssemblyInfo.cs`, and `<Assembly>.GlobalUsings.g.cs`. Evaluator graph SHA-256 `0b962bf78bad350dfa959ef30fdf3689a9c8f40257b7863fe352e1bce013576a`; the 15 additional file snapshots were present and rehashed in the same-build artifact tree. The five `project.assets.json` inputs were also identified and pinned. Detailed row/file hashes and receipt evidence: Core `artifacts/processor-20261114/hb-evaluator-globals-release-20261124/receipt.json`, SHA-256 `917e107fcbad45a5e8886adf8d9edc1c05cd6f937a2985736daa1b9dbaf76403`.

This proves argument-to-evaluated/filesystem inventory mapping only. Each RSP also contains 10 analyzers, 2 analyzer configs and 3 `/embed:` files; no `/generatedfilesout` is present, and source-generator outputs can be in memory. Analyzer/reference/config bytes and generator outputs are not yet fully captured in C#. Csc read/consumption remains `NotEstablished`; do not emit a closed observation V2 or advance provenance/admission from this mapping alone. Integration remains INCOMPLETE.

### 2026-11-24 — C# source-argument mapper fails closed on generated Compile inputs

Added an additive .NET 11 `CscSourceArgumentMapper` that validates independent SHA-256 pins for the evaluated graph and RSP, checks node/project/cwd identity, parses Windows argv, and requires exact equality between ordinary Csc source operands and the selected Compile set. Its synthetic fixture passed the positive mapping and refused wrong graph pin, wrong RSP pin, and an altered Compile inventory before writing outputs. Independent Golden Release attempt on the main project refused with exit 2 and no output because the RSP contains `HybridCPU_ISE.GlobalUsings.g.cs`, which evaluation-only Compile inventory does not include. This is the intended fail-closed result; the prior audit mapped all 15 target-generated files as separate saved snapshots, but this new direct mapper does not yet take those target outputs as a separately pinned role. Source/fixture/DLL hashes and exact graph/RSP pins: Core receipt `artifacts/processor-20261114/hb-evaluator-globals-release-20261124/receipt.json`, SHA-256 `917e107fcbad45a5e8886adf8d9edc1c05cd6f937a2985736daa1b9dbaf76403`.

The mapper does not hash source bytes or claim Csc read/consumption. Next .NET slice: add an explicit independently pinned target-generated-source inventory input, retain exact source-set matching, then capture analyzer/reference/config/embed/resource bytes and generator outputs from the same execution. Source argument mapping alone does not close observation V2 or qualify Core import. Integration remains INCOMPLETE.

### 2026-11-24 — C# raw V2 writer replayed pinned Golden capture through Core

The .NET 11 raw writer was run on the saved same-build graph and observation from the clean Windows Release capture of pinned Golden commit `6946ccab9d888f7dae0a4d8e18480f436138acd9` / tree `243a67c8ef462c1da2d1f06df9bee2c31508cb40` (historical 2026-11-22 instrumented run). C# emitted 2,218 rows / 2,241 chunks / 86,594,404 input bytes. Its index was byte-identical to the saved Python output: SHA-256 `6cb4dc2e4fb15cebb8a3a629ec6521c5535423c9970ea88fe34f7fccdfda6f4e`, closure root `0046f8dde1a242beb95e2c587c4f7d96e54ee20b17d2b33ae9d9415bddc271dc`. The exact C# output snapshot and 2,242-file SHA manifest are published under Core `artifacts/processor-20261114/hb-v2-csharp-golden-replay-20261124/`; manifest SHA-256 `e81a6def26eb7b3451ed33a2e3494a3626347ba153b150b98a58dd544dfa3776`.

The independent Core Release importer accepted the published bundle and re-read/staged it with 13 checks: 5 commands, 2,218 inputs, 2,153 argv-bound rows, 2,241 chunks, 86,594,404 bytes. Raw import is `UnqualifiedByteInventory / NotAdmitted`; staged profile is `CandidateOnly / NotAdmitted` (staged index `726cad580204bf4146d1029aa1318840cb3bd0f64726e04c834c436fbfe99377`, closure `4c3a75a1329bfe7afbd28b85a524271f15bbf1cc52bb6ad254f017c9240654f8`). Replay script checks every published file then reruns the independent Core import/stager. Receipt SHA-256 `8be85cc6d0594b353ec2ad9bdad1cd86ae988d7fad2958a2a5db66d41c6712b5`.

This supersedes the 2026-11-22 validation note that no independent Core reader receipt existed for that historical bundle, and closes C# raw serialization parity on those pinned Golden bytes. It does not migrate task/RSP observation generation from Python, prove Csc read/consumption, secure trusted handoff, approve Processor roots, admit source facts, or verify RTL/simulation/Processor/Studio. The latest 2026-11-24 same-run RSP still fails the evaluation-only source mapper on target-generated `GlobalUsings.g.cs`; a separate pinned generated-source role is required. Integration remains INCOMPLETE.

### 2026-11-24 — Pinned generated-source manifest closes current path-set mapping

The C# mapper now accepts an independently SHA-pinned `hardwarebridge.csc-generated-source-inventory/v1` manifest bound to the exact graph SHA, node ID, project, role, path, byte length and SHA-256 for each generated file. The manifest has 15 target-generated rows (three per project) from the same joined Release build. The Windows fixture passed an exact 2 ordinary + 1 generated source map and six negative cases (bad graph/RSP/manifest pins, missing row, altered generated bytes, changed Compile set) with no output on refusal.

On the five current pinned Golden same-run RSP copies, all project maps passed exact set equality: 1,104 / 29 / 18 / 9 / 31 source operands, three generated files per project. The five map documents and generated inventory snapshot are published under Core `artifacts/processor-20261114/hb-csc-source-map-golden-20261124/`; map manifest SHA-256 `864248c65e9b86034dd39f7f6b7b17d2e86ddb4e60584e1a474730e9c295afc5`, receipt SHA-256 `d395475f1b65ff41c1402681a50fd3b4ef87aa8c8f5b618ef66e5781674225f2`.

This closes only path-set mapping plus pinned target-generated file snapshots. Ordinary source bytes are not hashed; source-generator outputs, analyzer side effects, references/config/embed/resource inputs, Csc read/consumption, full closure, Core admission and trust remain open. Do not promote the map to an observation or verification receipt. Integration remains INCOMPLETE.

### Runtime direction — Windows/.NET first (2026-11-24)

The application runtime and primary implementation target is Windows with .NET/C#. Python, Java, and Linux are not prioritized as application runtimes. Keep them only where an existing tool or required launch/orchestration wrapper needs them to run CircuitWeave under Windows; do not introduce Java as an application dependency. Prefer native .NET/C# for capture, interchange, verification, CLI, and launch paths. Linux-only evidence may inform portability and historical audit, but it does not substitute for the Windows acceptance path. This direction does not waive any evidence gate; integration remains INCOMPLETE.

### Windows/.NET source-byte snapshot — 2026-11-24

Added standalone C# `CscSourceByteSnapshotter` to bind the five pinned Release source maps, their exact source-map-set manifest, identical pre/post source manifests, and the pinned 15-row target-generated source inventory. It verifies current bytes before publishing an unqualified snapshot and refuses stale pins, changed files, path drift, missing rows, or malformed map membership. Build: `dotnet build HWIR/EvaluatedProjectGraph/TaskTimeObserver/CscSourceByteSnapshotter.csproj -c Release -p:BaseIntermediateOutputPath=<new TEMP> -p:OutputPath=<new TEMP>` under SDK `11.0.100`, 0 warnings/errors; assembly SHA-256 `66fc476a3a76918614919f08eac0091cca70557ed6f5a4f534d3f66582ef92ef`. Synthetic PowerShell fixture PASS (positive package plus five negative cases).

On Golden commit `6946ccab9d888f7dae0a4d8e18480f436138acd9`, tree `243a67c8ef462c1da2d1f06df9bee2c31508cb40`, isolated source clone `g6946-1279ce2a`, the C# utility matched all 1,176 mapped source bytes against pre/post manifest pin `752c3ed30ccdccce4e211df4e1c331cc13b7cc9bc218452d96a5694ff1d27e36`, plus all 15 generated sources against inventory pin `5fc5fd6d86548c6937b3a9ff8b078147ca717582fafe44e2490d800149864ff1`. Graph SHA `0b962bf78bad350dfa959ef30fdf3689a9c8f40257b7863fe352e1bce013576a`; source-map-set manifest SHA `b75c8dac1e85f9780bd71d06c2226453177c1294a6a6b45cd624f6999117be1c`.

Published snapshot: `CircuitWeave-main/artifacts/processor-20261114/hb-csc-source-byte-snapshot-golden-20261124/`; snapshot manifest SHA-256 `b6ffc1a4acc8515525699c88f1474b6d9aba0eccd1e86d0c6708eb232e26b362`; publication manifest SHA-256 `66da487a02c2677c2b8490ae20d79f3f836b2424132ba678fdcc9ddfc983f420`; receipt SHA-256 `0d0c6ef4d95c2663cd18a81170ca49e044a0407d4c4f409e5072423d90fc57eb`. All 1,201 published data/evidence files were rehashed after copy (12,632,592 bytes). Snapshot is current-byte inventory only: Csc read/response consumption, complete compiler input closure, provenance, independent admission, trust, RTL, and Processor verification remain NotEstablished/NotAdmitted. In particular, this does not replace the absent current-run analyzer/source-generator outputs or create V2 observation.

### Snapshot scope wording correction — 2026-11-24

Independent review found that the first snapshot manifest field `ordinaryCurrentBytesMatchPinnedManifest` could be read as covering every row in the 3,530-row repository manifest. The implementation verifies the 1,176 ordinary source rows selected by the five Csc maps. The C# claim field is now explicitly named `mappedOrdinarySourceBytesMatchPinnedManifest`; the fixture asserts this bounded claim. The v2 snapshot supersedes the earlier publication without overwriting it. Updated artifact: `CircuitWeave-main/artifacts/processor-20261114/hb-csc-source-byte-snapshot-golden-20261124-v2/`; snapshot manifest SHA-256 `b737a88524af791e5e715ccb87f14dcc6d9fbb31fe1ea9cc1c28d27513b7a55f`; publication manifest SHA-256 `30db8b5505d19c680133ab8fea9fa9bff1e397008eed0ba2267bfadb9ed5ae84`; receipt SHA-256 `362d0a7b1b98b09437f785bc4494e24eb3ece0ed3f9dffffbc670f8b0625b49d`. Final C# source SHA-256 `666e5437c9a497b2e6c360bede3380566a16713ff71410dc96739789e8b3bfe7`, fixture SHA-256 `628601c28b095f68837fbd55306f05c36a8470d5741e9a032b142bdb3e2324ee`, SDK 11.0.100 Release build 0 warnings/errors. All 1,201 published rows rehashed (12,632,597 bytes). Scope remains an unqualified current-byte snapshot; it does not establish Csc read/consumption or complete compiler closure.

### Core Windows Csc candidate replay boundary — 2026-11-24

Added an unwired Core-owned C# `CscCandidateReplayRunnerV1`. It accepts a primary resolver-frozen byte set, an audited ordered reemit profile, externally pinned Windows `csc.dll` and dotnet host images, a new direct TEMP output directory, timeout and externally pinned expected PE/PDB/refout/generated-source rows. It materializes input bytes with exact original file leaf names, reconstructs argv using `ArgumentList`, remaps source and generated paths through captured `/pathmap`, verifies tool hashes before/after launch, kills the process tree on timeout, and compares every output row. It does not alter historical execution/provenance/admission claims and is not wired to CWR3.

Independent Windows SDK 11.0.100 fixture command: `dotnet run --project tests/CircuitWeave.CscCandidateReplayRunner.Tests/CircuitWeave.CscCandidateReplayRunner.Tests.csproj -c Release -p:ArtifactsPath=%TEMP%\\cw-csc-candidate-replay-runner-root-final3-20261124\\artifacts -p:NoWarn=CS1591 --no-launch-profile -v:q`; exit 0, log SHA-256 `6d04b82f800792ed7eee68ed8c4bfe2506555c57190cf6d95939200b25b875ef`. The real SDK Csc fixture emitted PDB/refout identical to its baseline (PDB 748 bytes, SHA `25955839325a9be4b1fbc52a7f3cdf3ea6cbcae6773c375abd8e6d3778932c29`; refout 4,096 bytes, SHA `86014ba143831f71eff8e97b3dd1b0a33029c2f4abbaeaccb308920ceb07a81c`). PE lengths match at 3,584 bytes but SHA differs (`4ed2955556d792a7d28343c1d250a9c9b986e34cb6bb5db6d304a3404c6b13de` vs `7baea17a3983702a6a366db1322589bf5f5c34dcc56a239ced39f4198f0bc664`); PEReader confirms the CodeView PDB path changes with the relocated output directory. Replay therefore returns `Refused/NotPassed`; the fixture also verifies stale output pin, wrong Csc pin and bounded timeout refusals.

Receipt and log: Core `artifacts/processor-20261114/csc-candidate-replay-runner-20261124/receipt.json` (SHA-256 `4875ac22e888573a246e7fc581df941d72d37c3bc3756b4e8ee09c6dffac4107`) and `tests.log`. Runner source SHA-256 `3300e4b4a6a5cadd571e9c547c443f47397a371d22a93d78a5e79eb662fe3b94`; fixture SHA-256 `b6ea34c552a04a3947b4d74ecfc41ced3caff42129a4fde95e2d789d0633a4f2`. This is a synthetic relocation boundary check only: no source generator was exercised, no pinned Golden Csc replay succeeded, and no CLI/CWR3 composition, provenance, trust or admission claim follows. Keep V4 composition fail-closed until exact or explicitly accepted path-normalized PE comparison is established under the applicable plan.

### Runtime migration acceptance — 2026-11-24

Primary product target: Windows with native .NET/C#. Python, Java, and Linux are permitted only as narrowly required wrappers to launch CircuitWeave under Windows; Java is not an application dependency. Current Python observer/capture and Linux-only replay/sandbox code are transitional/support paths and do not meet Windows acceptance. Closure requires C# observation/capture parity, an approved Windows isolation boundary for untrusted Csc inputs, and independent Windows Core import/replay with no Java/Linux/WSL dependency (except an explicitly justified minimum launcher wrapper). Keep this gate open; integration remains INCOMPLETE.

### Core Windows Csc exact-path attempt — 2026-11-24

The latest Core-owned Windows candidate replay attempt remains refused. A synthetic source generator ran, but baseline/replay absolute output roots differed; PE/PDB mismatched while refout/generated source matched. The test confirmed junction cleanup on that mismatch, but exact-mode compiler failure/timeout cleanup was not reached. Preserve the fail-closed gate and do not wire the runner into composition. Evidence and exact source hashes are indexed as `cscExactPathJunctionAttempt20261124` in the Core backlog. Integration remains INCOMPLETE.

### Core Windows Csc exact-path fixture update — 2026-11-24

The synthetic Windows Csc/source-generator replay now passes exact PE/PDB/refout/generated-source byte comparison at identical Csc-visible paths. The fixture verified TEMP junction cleanup after success, compiler failure, and timeout. This supersedes the earlier failed exact-path fixture attempt, not any Golden or production admission gate. Receipt is indexed as `cscExactPathJunctionReplay20261124` in the Core machine-readable backlog. Integration remains INCOMPLETE.

### Bridge portable boundary recheck — 2026-11-24

The current portable Bridge default passes Windows Release build on SDK 11.0.100 with 0 warnings/errors and 8 dependency-boundary tests. Legacy opt-in without pins is rejected (`HBINT0101`). A positive pinned build awaits a clean, remotely verified Core SHA. Receipt is indexed as `bridgePortableBuildRecheck20261124` in the Core backlog. Integration remains INCOMPLETE.

### Windows task-event projection C# parity — 2026-11-24

A new .NET 11 C# projector under the authorized Bridge subtree reproduces the saved Windows task-event projection V1 bytes and refuses stale/unknown/duplicate/uncaptured-response inputs. Independent Windows tests passed 14 assertions; CLI output SHA matched the saved projection. This is one utility migration only. Python still participates in observation-V2 assembly; task event projection is not an OS process witness or Csc read-consumption proof. Next action is strict C# observation V2 assembly over pinned graph/task/process/RSP/source/generated roles, followed by the existing C# raw capture writer. Receipt is indexed in the Core backlog as `windowsTaskEventProjectorCsharp20261124`. Integration remains INCOMPLETE.

### .NET task projection CLI wiring — 2026-11-24

The existing `EvaluatedProjectGraph` command now routes `project-task-events-v1` to the new C# projector, and an independent run against saved Windows Golden task events reproduced the frozen projection bytes. The command remains a task-event projection and does not establish process execution or input consumption. C# Observation V2 assembly remains the next capture milestone. Integrated receipt is indexed as `integratedCommandReceipt` in Core backlog entry `windowsTaskEventProjectorCsharp20261124`. Integration remains INCOMPLETE.

### Frozen V2 source rehash — 2026-11-24

The saved pinned Windows V2 capture inputs were rehashed: both baseline and isolated clones match the fixed Golden commit/tree, and all 3,389 listed paths exist and match the recorded SHA values. No Csc read-consumption claim follows. This corpus is ready for C# observation assembly parity only; integration remains INCOMPLETE.

### C# Windows Csc observation V2 assembly — 2026-11-24

A Bridge-owned .NET 11 assembler now joins SHA-pinned evaluated graph, task events, process probes, response copies, build metadata, source/file pins, generated source outputs, and explicit graph role selector. Independent Release build passed with 0 warnings/errors. On frozen Windows retry-11 capture (Golden `6946ccab9d888f7dae0a4d8e18480f436138acd9`, tree `243a67c8ef462c1da2d1f06df9bee2c31508cb40`), output SHA exactly matched the frozen V2 observation: `17a5b8153fc15b5a4e9f7668a7e414c851ed3c81c0fc6ee36dc749bf6626e5e9`. Wrong graph pin refused with exit 2 and no output file. Receipt and immutable output are in Core `artifacts/processor-20261114/hb-csharp-observation-v2-assembler-20261124/`.

This proves deterministic reconstruction and byte parity from that saved task-time evidence lineage. It does not prove Csc consumed the response/source/analyzer bytes, constitute a fresh Golden build, establish trust/admission, or satisfy RTL/Processor/Studio acceptance. Full integration remains INCOMPLETE. Python remains in legacy capture orchestration until the same Windows workflow is fully migrated and independently admitted; no Java/Linux application runtime is introduced.

**Remaining producer wiring:** the new assembler is currently a standalone .NET executable with a fixture-prepared assembly manifest. It is not yet called by the primary evaluated-graph CLI and no C# command yet produces that manifest from a live capture. Wire those steps into the Windows C# path, then exercise the resulting end-to-end capture; until then the Python orchestration is transitional and integration remains INCOMPLETE.

### .NET CLI Observation V2 route — 2026-11-24

`EvaluatedProjectGraph assemble-csc-observation-v2` now routes to the Bridge-owned assembler. Release build passed with 0 warnings/errors; invocation against the frozen retry-11 evidence reproduced exact SHA `17a5b8153fc15b5a4e9f7668a7e414c851ed3c81c0fc6ee36dc749bf6626e5e9`. Eight portable dependency-boundary checks passed after the added ProjectReference; no Core backlink was introduced. Updated receipt/artifact: `artifacts/processor-20261114/hb-csharp-observation-v2-assembler-20261124/`.

Open: the fixture assembly manifest is still prepared separately; no C# live-capture command creates it yet. The CLI route is saved-evidence assembly only, not proof of fresh Csc execution/reads, trust, or admission. Python orchestration remains transitional. Integration remains INCOMPLETE.

### C# assembly-manifest producer — 2026-11-24

Added `prepare-csc-observation-v2-manifest` to the main Bridge CLI. Given pinned graph, crosswalk, build metadata, explicit source roots/compiler path/primary node, it derives probe file pins and emits a new manifest only under TEMP. The frozen retry-11 run produced manifest SHA `5b2d4fe8bb20d0cce4746dfe2c8c26b094b869f637cda60b639b4e285c87f293`; the subsequent assembler output again matched V2 SHA `17a5b8153fc15b5a4e9f7668a7e414c851ed3c81c0fc6ee36dc749bf6626e5e9`. Invalid primary role refused (exit 2, no output). Snapshot and receipt updated in Core artifact directory.

This closes fixture-manifest preparation, not production capture: no fresh Golden Csc task has been generated through the integrated CLI, and observation remains unqualified with no Csc read-consumption/admission claim. The next gate is running this C# producer chain on an isolated fresh Windows Golden build, then independent Core import/admission. Integration remains INCOMPLETE.

### Generated-source root binding and role scope — 2026-11-24

The C# manifest producer and assembler now require `generatedRoot` to equal the pinned build metadata root. Positive frozen reconstruction remains byte-exact; a substituted temp root was refused by both preparation and assembly (exit 2, no output). The V2 `owned-body` field is retained for frozen contract compatibility but means project-reference reachability only. Core must independently verify method/call-graph ownership before use; this capture label is not an ownership proof.

### Core C# capture import from assembled V2 — 2026-11-24

The main Bridge CLI `capture-v2` consumed the C#-assembled frozen Observation V2 and reproduced the pinned raw capture index SHA `6cb4dc2e4fb15cebb8a3a629ec6521c5535423c9970ea88fe34f7fccdfda6f4e` and closure SHA `0046f8dde1a242beb95e2c587c4f7d96e54ee20b17d2b33ae9d9415bddc271dc` (2,218 rows, 2,241 chunks). The Core C# CaptureImport independent reader/stager then passed 13 checks, including raw-to-staged handoff, over 86,594,404 input bytes. A full hash manifest confirmed all 2,242 generated files matched the existing published snapshot exactly; no duplicate 86 MB copy was needed. Receipt is indexed in Core backlog as `csharpCaptureV2IndependentCoreImport20261124`.

Core result is `UnqualifiedByteInventory`, `CandidateOnly`, `NotAdmitted`. This establishes import and byte staging only; it is not `Verified`, production Golden re-execution, Csc read-consumption, trust, or hardware/Processor proof. Integration remains INCOMPLETE.

### Fresh isolated Golden Windows Csc execution — 2026-11-24

Created a new clean TEMP clone from the retained pinned source object store and verified HEAD/tree `6946ccab9d888f7dae0a4d8e18480f436138acd9` / `243a67c8ef462c1da2d1f06df9bee2c31508cb40`; the user Golden worktree was not used. Restored and built with pinned SDK `11.0.100-rc.2.26504.105`, C# `CscTaskLogger` and `CscLaunchProbe`; build exit 0, five successful task events and five probe child processes (all child exit 0), five held/copied RSPs, 1,191 compile items, 50 generator outputs. Tracked source inventory was 3,530 paths before/after with zero hash changes. Captured 15 compiler PE/refout/PDB output rows. Receipt and snapshots are at Core `artifacts/processor-20261114/golden-windows-csharp-fresh-capture-20261124/`.

This is fresh isolated compiler execution evidence, not yet assembled crosswalk/Observation V2 or independent Core admission. Csc response/source read-consumption remains NotEstablished. The C# live crosswalk producer is in progress; keep integration INCOMPLETE.

### Fresh live crosswalk and independent Core import — 2026-11-24

Follow-up: the integrated C# producer joined the five task events to five instrumented Csc child processes and copied response bytes, then the C# Observation V2 assembler and segmented capture writer produced the fresh package. Core independently imported and staged the package with 13 passing checks (2,218 rows / 86,594,529 bytes), rerun from the published snapshot. Windows .NET 11 CLI Release build and help launch passed with 0 errors (3,119 warnings); `eng/circuitweave.ps1` launches it without Python/Java/Linux runtime dependencies. Wrong facts/Git pins, untracked build-root input and generated-root overlap were rejected before output creation. Fresh assembler replay retained exact observation SHA; legacy metadata schema remains accepted. Receipt: Core `artifacts/processor-20261114/golden-windows-csharp-crosswalk-v2-20261124/receipt.json`, SHA-256 `64c37c6ab805e04af00655e22214865cc0f91f7f7ece1b186803ce5188db4a35`; crosswalk SHA-256 `34ca2d16650fce43c242220515a3f5328f04cb48f78f7727e21cba0b9d38142a`. Status remains CandidateOnly / NotAdmitted. Read-consumption, trusted handoff, RTL and Processor verification are unproven.

Runtime constraint: CircuitWeave application orchestration, compilation, capture, import and verification are .NET/C# first and Windows runnable. Python, Java or Linux may appear only as narrow helper wrappers where an external tool requires them on Windows; they are not default application runtimes.

### Core Windows .NET staging slice — 2026-11-25

Core now pins stable .NET SDK `11.0.100` with prerelease selection disabled and runs its managed CLI through `eng/circuitweave.ps1`. Its C# stager verified and copied a local candidate .NET/Roslyn/analyzer toolchain (1,140 files; 151,510,973 bytes), then a capability-free AppContainer probe ran pinned Csc version reporting and a deterministic synthetic C# compile. The synthetic PE was 3,072 bytes and its SHA-256 was independently rechecked. Exact-file ACL grants also passed staged synthetic Csc PE overwrite in a read-only input tree, with file ACL restoration. Input-manifest SHA-256 is `B81B18A9C128DEC7343236FD34A7DF7D292DA0340B55F330C361AFD1BBFDE180`; probe log is Core `artifacts/processor-20261114/pinned-dotnet-csc-appcontainer-synthetic-compile-20261125-primary.log`, SHA-256 `A24FCA50765B447F5396D67711FE1EAE5E3362916B91DB7FE9E0157F14BC82A6`. This remains CandidateOnly: the Golden primary argv, analyzer workload, input read-consumption, PE/PDB/refout/generated outputs, trusted handoff, RTL and Processor checks have not run. Core HEAD is `23c01bf5baeb1377a1317e2523a25f96713a1c75` with a dirty working tree; Core remote master could not be retrieved. Do not treat this probe as positive handoff or completion.

### Primary Csc path-identity gate — 2026-11-25

The pinned PE CodeView entry and portable PDB documents contain absolute paths rooted at the captured TEMP project directory. A synthetic Csc run through a temporary project-directory junction recorded the junction alias as the CodeView PDB path; artifact `artifacts/processor-20261114/csc-cwd-junction-codeview-probe-20261125.json`, SHA-256 `A3CB189BE3D0AD7CC3B9621141D1915F8D7807437D70A76AA0D89456D23F0315`. This feasibility probe ran outside AppContainer and is not Golden evidence. Core's host now supports exact-file grants: staged synthetic Csc overwrote one precreated PE in a read-only tree and restored the file ACL; probe log SHA-256 `AAA5AFBCD6576D6D4A143DAD1EC1AC3EB7F5B48DB6B6EDE00010639A48516ADD`. PDB/refout behavior remains untested. The existing replay runner still cannot preserve the primary command: source argv is relative and no `/pathmap` is captured. Before any Golden launch, Core must prove a reversible external CWD alias to an in-sandbox target and cleanup on success/failure/timeout. Preserve all source/reference/analyzer/config/embedded hashes and exact ordering; do not characterize a modified argv as the original command.


### 2026-11-25 — Current primary replay plan preflight

Core added a C# path/argv planner bound to the pinned Golden commit/tree, raw capture and Core pins, ordered argv, output profile hashes, and externally pinned absolute-input rebases. Its synthetic test passed against the observed ordinal shape and kept all 1,104 relative sources and output options unchanged. The actual pinned primary preflight is blocked (Core receipt `artifacts/processor-20261114/captured-primary-appcontainer-preflight-20261125.json`, SHA-256 `F1457A2BED352830BCE4D481D62F19B9E506D5B2A4343AAFCBF18A77B7A7F2EC`): the exact captured working directory already exists, the proposed writable `obj` junction overlaps three source operands, and available V3/V4 output profiles bind stale Core/Capture pins. The current output hash manifest inventories bytes but is not an independently imported output profile/task witness. The planner test log is `artifacts/processor-20261114/csc-appcontainer-replay-plan-tests-20261125-primary-independent.log`, SHA-256 `7DC0F50CEFDDD85F5AAB1CA037F9F12B44381F8868FDF3EA61749A1DA0BB08AA`. No Golden Csc was launched. Next: build current-pin C# V3/V4 profile/manifest producers and resolve the CWD/output layout and AppContainer host same-user path threat; keep positive handoff and integration acceptance closed.


### CW-INT output profile producer gap — 2026-11-25

The current-pin primary AppContainer preflight in backlog key `capturedPrimaryAppContainerPreflight20261125` is blocked. Core's V3/V4 APIs audit supplied profile/order/manifest bytes; `bridge capture-csc-outputs` consumes a supplied manifest and does not produce current-pin V3/V4 artifacts. The raw output hash manifest is byte inventory only, not a compiler task witness. The on-disk historical generated-order artifact now hashes to `DE42395B48332F91A231EF81BCF894461C054F5A2DC9A4D8955475CF5B91CB26` (previously recorded as `D8519662…`); its Core/Capture pins remain `726cad…` / `6cb4…`, so it is still stale. Next producer must derive order from retained GeneratorDriver witness, have Core independently audit it against PDB/capture rows, bind current task witness/crosswalk/comparison and emit canonical candidate-only profiles under fresh paths. Existing captured CWD and three writable-`obj` source overlaps remain unresolved; exact-file ACL path replacement remains unqualified. Current checked-out Golden is dirty and not at pinned commit `6946ccab…`; no new Golden execution is permitted from that checkout. Core remote master could not be refreshed during this check. Overall integration remains INCOMPLETE.

### Current-capture GeneratorDriver replay — 2026-11-25

The retained order manifest from the Nov 23 RC2 probe could not be rebound: 49/50 generated sources matched current bytes, with `ProfileSerializerContext.g.cs` differing by one byte. I reran the C# GeneratorDriver probe against the current SHA-pinned captured closure using stable .NET SDK `11.0.100` and its Roslyn `bincore`. It parsed 1,104 sources, 180 references, 10 analyzers, and 2 analyzer configs; loaded 10 generator instances without diagnostics/load failures; and regenerated 50/50 current captured source texts byte-for-byte. Receipt `artifacts/processor-20261114/current-captured-generator-driver-witness-20261125/receipt.json` SHA-256 `55f8be8c9b10314ed25638ce094b5d1410115352fe296b4cc9a7caf0d3628687`; log SHA-256 `583689176f2dd3f9f659ad1ca0b19767ee991a7fe6ec2cba06b9db2690105302`; probe source SHA-256 `f0eaad104158ce92c73c1c8c63a9dd71979eea01e5a52b471433ee7b42746dc3`. This is a GeneratorDriver replay over saved captured bytes, not a Golden Csc execution. The probe's path-string PDB-order counter is not trusted; independent `CscGeneratedSourceOrderV1.Audit` must still validate the manifest rows and actual current PDB order. No current V3/V4 output profile or admission is established; integration remains INCOMPLETE.
`receipt.json` now also records the failed first invocation (wrong chunk-bundle directory) as a separate diagnostic artifact. The current receipt SHA-256 is `0efba823c227a4f5cec76e84bc381b51e9d88282990a2895a067e8ae6d79f79e`; this supersedes the preliminary receipt hash in the preceding entry. The successful current-driver log remains `583689176f2dd3f9f659ad1ca0b19767ee991a7fe6ec2cba06b9db2690105302`.

### Current-pin generated-order producer and audit — 2026-11-25 follow-up

Core now exposes the C# API `CscGeneratedSourceOrderProducerV1` and CLI `bridge produce-csc-generated-order`. It consumes SHA-pinned closure/profile, Core index, GeneratorDriver log and PDB; binds all rows to current captured generated-source bytes; and runs `CscGeneratedSourceOrderV1.Audit` before writing the manifest. The current probe regenerated 50/50 captured source texts under .NET/Roslyn 11.0.100. A Windows-only PDB path normalization accepts the exact `\\?\C:\...` DOS form as its drive path and refuses UNC/device/malformed forms. Synthetic producer tests passed 21/21; separate Core consumer passed 8/8 for 50 rows and 50 PDB generated documents. Manifest SHA-256 `5489dd8a32e8ad9156973596103ec7e00702d2854d0ee5a86e58c3e592b59f0c`; driver log SHA-256 `583689176f2dd3f9f659ad1ca0b19767ee991a7fe6ec2cba06b9db2690105302`; final source SHAs are in backlog key `currentCapturedGeneratorDriverWitness20261125`. This remains a replay of saved captured closure bytes: Golden Csc execution is `NotRun`; execution provenance is `NotEstablished`; admission is `NotAdmitted`. Next: produce/import current-pin task-output manifest and V4 output profile. Golden primary AppContainer execution is still blocked on current CWD/output mapping and the exact-file grant hardlink threat. Integration remains INCOMPLETE.

### Exact-file grant hardlink refusal — 2026-11-25

Core now rejects every otherwise-valid exact-file write grant before creating an AppContainer profile or child. The reason is a reproduced Windows same-user race: `CreateHardLinkW` succeeds while a compatible file handle is open, so handle-bound ACL mutation alone cannot contain an AppContainer writer from writing through an outside hardlink. Targeted policy probe passed: `dotnet run --project tests/CircuitWeave.WindowsAppContainerHost.Tests/CircuitWeave.WindowsAppContainerHost.Tests.csproj -c Release --no-restore -p:WarningLevel=0 -- exact-file-grant-policy-probe`; it confirmed refusal before launch with ACL/target bytes unchanged. Log SHA-256 `04e9670d71d0c861e532ecc5c7c59133c25b06e77a2b024f8400aa2a21822024`. Host/test source SHAs and status are in backlog key `appContainerExactFileGrantHardlinkRefusal20261125`. The full host suite was interrupted after a stall at the external-CWD alias probe and is not a pass. Exact-file grants are unavailable; the hardlink/TOCTOU threat is not fixed, and primary Golden replay remains blocked. Integration remains INCOMPLETE.

### Core current-pin V4 linkage update — 2026-11-25

Core now has a .NET/C# CLI route that independently imports the staged V2 closure and current V4 output bundle, constructs `CscCapturedCompilerOutputProfileV4`, and audits it again against actual generated-order/PDB bytes before publication. Current profile artifact SHA-256 `456c6de272c76c9c1fb1506ef165a5c51eedad285f94fd16c104a17cf43d0873`; implementation, commands, pins, 42 V4 importer regression checks, and 15 CWR3 V4 transport checks plus a real pinned V2/V4 candidate probe (log SHA-256 `9902118256fc720c4a022eec2b2db822590fa8a316fc0cee6fb4ed666680b01e`), are recorded in Core backlog key `currentCapturedV4OutputProfile20261125`. This binds three post-process inventoried outputs only. It does not establish Golden Csc execution, response consumption, task output provenance, trusted handoff or hardware verification. Integration remains INCOMPLETE. Windows R0 receipt generation still invokes Python through Core `eng/build.ps1`; migrate that observer to C# before treating Python as an acceptable Windows entrypoint dependency.

### Windows .NET R0 receipt migration — 2026-11-25

Replaced the Windows `eng/build.ps1` Python dependency with the Core-owned C# runner `eng/CircuitWeave.R0Receipt`. The R0 workflow and managed receipt jobs now run on `windows-latest`; both policy checks and receipt execution use .NET/C#. `eng/build.sh` is now only a WSL interop shim to the Windows PowerShell entrypoint and fails closed if that interop is unavailable. Python remains in legacy observer parity tests, not the Windows application/build entrypoint. C# policy tests passed 26/26. The Windows script ran the full 11-command R0 sequence under SDK 11.0.100: mandatory smoke restore/build/run, actual evaluated `ProjectReference` check, Core solution restore/build, P14 restore/build/run all exited 0; receipt result `Passed`, Golden snapshot `NotRun` because no Golden root was supplied. Receipt: `artifacts/processor-20261114/r0-dotnet-runner-final-receipt-20261125/receipt.v1.json`, SHA-256 `58c071d1b21d38475d433e087ad4451a2942e5b401c6024e43286f23bb501d21`. C# and legacy Python workflow audits produced identical 50-workflow JSON bytes, SHA-256 `107d7e760e92d3e49c9e314390c208c5d72e188e114f046cbfcde9a7c2325c27`. Two existing push allowlists that contradicted the R0 `master` policy were corrected (`cw-int-03` and `cw-p06`). Hosted evidence for this dirty local revision is not available. This closes the local R0 Windows entrypoint slice only; no Golden Csc, independent RTL, Processor state, trusted handoff or hardware admission is established. Integration remains INCOMPLETE.

### 2026-11-25 — Core separate-process V4 consumer

The current Core CLI was invoked as a separate process with the current pinned V2/V4 candidate and generated-order evidence. It rechecked the V2 index `22AABE251BD8E8DD366BB4210D120521BFE4891D8CF43986E6E72B6BD5D619C4`, V4 output index `D2F6787D87EAF8F35A934160CC3942EE050A580DA3A6E72AA5A8A1DDCC89AD66`, 50 generated rows / 50 PDB documents, and three compiler-output roles. It exited 2 before CWR3 composition because execution/provenance are `NotEstablished` and admission is `NotAdmitted`; no session was created. Result is transport-only, not independent replay or trusted handoff. Core receipt: `CircuitWeave-main/artifacts/processor-20261114/current-captured-generator-driver-witness-20261125/v4-independent-core-consumer.stdout.json`, SHA-256 `BE5C6B938C8ECC42236FB6BEB8EA6F17FECFB324D63829260B1E43B8BFAA4D9C`. Integration remains incomplete. Product runtime direction is Windows + .NET/C#; Python, Java and Linux are support/launch infrastructure only when specifically required.

### Runtime and hosted-evidence clarification — 2026-11-25

CircuitWeave's application target is Windows + .NET/C#. Python, Java and Linux are not prioritized as application runtimes; they may appear only as narrowly justified support/launch wrappers or infrastructure for running CircuitWeave under Windows. Local/origin refs currently agree with Core `23c01bf5baeb1377a1317e2523a25f96713a1c75` and Bridge `2be1784125193911ea635cb4fce0661b0b4a4bda`; the fixed Golden remains `6946ccab9d888f7dae0a4d8e18480f436138acd9`. Hosted Actions receipts and protection settings were not obtained (`gh` unavailable, unauthenticated Actions API 404). Do not infer current hosted state from the local workflows.

### Windows AppContainer follow-up — 2026-11-25

The Core host regression project compiles cleanly, but its full Windows AppContainer fixture remains incomplete: synthetic sandbox and synthetic Csc probes passed; the run did not reach final cleanup/timeout checks and was interrupted during external working-directory alias handling. Loopback probes were bounded to 3 seconds (Core test source SHA-256 `F189E6D2DD3728982B2FD544849A2D25D64BEC0428F80341A5BB2B6F3388F188`). Interrupted test roots and junctions were removed and the two associated TEMP ACEs were restored; no test processes or those temp paths remain. This does not qualify the Golden Csc replay.

### 2026-11-25 — Windows alias ACL boundary retest

Core stopped changing the global TEMP DACL during external working-directory alias setup. The complete local AppContainer host fixture passed: capability-free synthetic launch/Csc, alias under exact target, no outside-TEMP read/write or loopback, unchanged global TEMP DACL, timeout/cancellation and alias cleanup. Evidence log is `CircuitWeave-main/artifacts/processor-20261114/appcontainer-alias-no-global-temp-acl-20261125/windows-appcontainer-host-tests.log`, SHA-256 `EC3D9D900BFE85545CAD4CFEF118A51C33CEB792185AF609837BFB019E59EC06`. This remains synthetic CandidateOnly evidence. Captured Golden replay is still blocked by existing CWD identity, three writable-obj/source overlaps and same-user replacement risk.
