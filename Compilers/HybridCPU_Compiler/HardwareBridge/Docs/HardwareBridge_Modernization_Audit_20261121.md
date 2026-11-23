# HardwareBridge → CircuitWeave — аудит и TODO от 2026-11-21

Проверены `master` Bridge `e0fdfdebd537ecfbd67b5fd853940f061a5c41ae` и Core `b5c44685197a40df99a4d48891120b12475c6ca5` через `git ls-remote origin refs/heads/master`. В Bridge 87 коммитов после `d243dd7`. История first-parent содержит все merge PR #41–51. Golden pin плана — `6946ccab9d888f7dae0a4d8e18480f436138acd9`. Приёмка production handoff остаётся открытой.

## Сверка пакетов

| Пакет | Что реализовано | Открытая граница |
| --- | --- | --- |
| HB-INT-01 | Portable DTO default, explicit legacy opt-in, SDK/Core pin guards; local default, positive opt-in and wrong-pin checks passed | Changes remain dirty/unpublished; clean tracked Companion Debug/Release fail on missing compiler-owned contracts; exact RC2 positive path and hosted Windows/Linux gates remain open |
| HB-INT-02 | Segmented capture, input accounting/no-write checks, pinned Golden Windows Csc child/response/source-closure observation | Compiler read buffers, analyzer side effects and independent Core admission remain unproved; hosted Windows regression remains open |
| HB-INT-03 | V1 compatibility, Core-v2 JSONL, проверки execution evidence и sealed staging. Core staged reader слит | Требуются фактические production inputs и положительный независимый Core receipt на актуальной паре pins |
| HB-INT-04 | Scalar session, raw-host trace replay и мутации в fixture scope | Требуется выполнение утверждённого Golden метода и независимый replay наблюдений |
| HB-INT-05 | По 60 записей в [инвентаре v1](../HWIR/Migration/projection-inventory.v1.json) и [review v2](../HWIR/Migration/r3_03_projection-review-inventory.v2.json). Добавлены правила отказа и review парных артефактов | Guard CI подтвердил report-only scope. Требуется source-derived dual-run с Core-owned lowering |
| HB-INT-06 | Windows CI, no-write и protected dispatch gates | На проверенном SHA Companion qualification и Core import QA завершились failure |

Merge diff подтверждает назначение PR. #41 меняет Windows identity в capture, #42 — Actions и parity driver, #43 — executed Csc / IL checks, #44 — sealed workspace publisher, #45 — raw trace replay, #46 — canonical v2 staging, #47 — Companion qualification, #48 — response-file witness, #49 — Core receipt и negative replay, #50 — dual-artifact review, #51 — protected dispatch. Статус реализации в таблице относится к этому коду. Приёмка каждого пакета требует доказательств из следующего раздела.

## Подтверждённые дефекты и условия закрытия

- [ ] **P0 · R0-01 / HB-INT-01,06 · владелец software compiler.** В текущем Bridge checkout `e0fdfde` файла `CompilerEmissionPackage.cs` также нет; прежнее упоминание «локальный файл существует» относилось к отдельному TEMP overlay, а не к текущему дереву. Повторная чистая tracked source export проверка подтвердила: `git ls-tree -r HEAD -- Compilers/HybridCPU_Compiler/Core/IR/Artifacts` пуст, а `git check-ignore -v` показывает `.gitignore:64:artifacts/`. На полной tracked выгрузке `Compilers/HybridCPU_Compiler` и sibling `Compilers/HybridCPU_Platform.Contracts` Companion Debug и Release оба завершились 20 ошибками; среди них `CS0234` для namespace `HybridCPU.Compiler.Core.IR.Artifacts` и `CS0246` для `CompilerEmissionPackage`, `CompilerSidebandEnvelope`, `DescriptorEnvelope`, `TypedSlotFactsEnvelope`, `CompilerEvidenceEnvelope`, `SidebandRequirement`, `DescriptorAbiStatus`. [Чистый-build receipt и логи](../../../../../CircuitWeave-main/artifacts/processor-20261114/bridge-r0-complete-deps-probe-20261122/receipt.json), archive SHA-256 `cd289fb8fe2ada9344448583dbe85597e7a23930e0f9c9abbd01415866b2aab0`, receipt SHA-256 `350bd9f3d87bee1032259407101e82698fdb140334e02939bd3d2ac80e3eb374`; Bridge SHA `e0fdfdebd537ecfbd67b5fd853940f061a5c41ae`, локальный SDK `11.0.100`. Условие владельцу: опубликовать аутентичные tracked contracts и их зависимости в разрешённой/Core-owned области, исправить исключение публикации, затем повторить Debug/Release на утверждённом RC2 SDK из нового чистого checkout с PE/DLL, dependency и no-write receipts. Первый промежуточный export не включал ни Core, ни `Platform.Contracts`; второй включал Core, но пропустил `Platform.Contracts`. Оба промежуточных каталога помечены в `STATUS.json` как `INVALID` и не являются qualification evidence.
- [ ] **P0 · HB-INT-01 · владелец Bridge — локальная реализация исправлена, promotion/hosted acceptance открыты.** Историческая чистая база `e0fdfde` имела default `EnableBridgeHwirLegacyFixtures=true` и пустой SDK pin bypass; текущий dirty working tree переключает default на portable DTO-only, включает sibling references только при explicit opt-in и требует `BridgeExpectedSdkVersion`. На текущих байтах Release Rebuild прошёл (0 warnings/errors), explicit opt-in с пустым pin отказал `HBINT0104 WrongSdk`, exit 1. [Receipt](../../../../../CircuitWeave-main/artifacts/processor-20261114/hb-p0-portable-sdkguard-20261122/receipt.json). Закрытие: перенести эти Bridge-owned изменения в новый tracked SHA, затем успешный exact-SHA hosted Windows/Linux portable build, успешный explicit pinned legacy path и подтверждённый отказ при пустом/неверном pin.
- **Дополнительная локальная positive проверка opt-in, 2026-11-22:** Bridge Release Rebuild с `EnableBridgeHwirLegacyFixtures=true` успешно собрался (exit 0, 1,021 итоговых warning, 0 errors) на чистом TEMP clone Core HEAD `714e5c939bb1817481cf4970e3e343aa92bfa23b`; SDK был `11.0.100`, а не закреплённый RC2. Pin `9.0.321` отдельно отказал с `HBINT0104 WrongSdk` (exit 1). [Receipt и логи](../../../../../CircuitWeave-main/artifacts/processor-20261114/bridge-optin-current-core-20261122/receipt.json); SHA receipt `79f77bc890c385db9228468d8c0ed0cc222eafbd3db5a4c7af5a9a6f5cca4780`. Это снимает только локальный вопрос positive opt-in для указанной пары рабочих байтов. Точный SDK, чистый tracked Bridge SHA, Companion build, hosted Windows/Linux и trust gate остаются открыты.
- [ ] **P0 · R0-02 / R2-02 / HB-INT-06 · владельцы репозиториев и Core.** Branch API на проверенном Bridge SHA вернул `protected:false`, публичная аннотация CI фиксирует `UnprotectedMasterRef` на push. Наличие утверждённых значений переменных в окружении этого запуска отдельно не подтверждено. Защитить `master`, согласовать Core и Golden pins и настроить scoped read-only credential. Сохранить dispatch-only проверку. Закрытие — разрешённый `workflow_dispatch`, валидный receipt настоящего Core importer и negative mutation evidence. При смене Core SHA согласовать хардкод gate и Core-side producer pin. [HB-INT-06 Core gate](https://github.com/yuriyyak23/HybridCPU-Compilers/actions/runs/44896587092).
- [ ] **P0 · R1-01/R1-03 / HB-INT-02/03/04 · Bridge и Core.** В изолированной копии утверждённого Golden связать наблюдавшееся исполнение Csc с response bytes, generated sources, references/options, PE/PDB, MethodDef и owned method graph. Доставить реальные Companion facts в Core, перепроверить source hashes до и после и показать отказ на изменённом входе или stale PE/PDB. Scalar fixture replay сохранить как регрессию. Уточнение 2026-11-22: Windows attempt-05 наблюдал пять Csc процессов и сохранил response bytes; пять ordered argument lists совпали с соответствующими task events по output token. Копирование response не доказывает, какие bytes Csc прочитал; source/reference/analyzer read buffers и complete closure не установлены.
- [ ] **P1 · R1-02 · Bridge CI.** На merge #41 Windows synthetic job завершился failure, Linux synthetic job — success. Точный текст исключения Windows из внешнего аудита не подтверждён публичной сводкой. Критерий закрытия — успешные Windows/Linux no-write jobs на актуальном pin с проверкой readonly fixture и identity. [Paired no-write](https://github.com/yuriyyak23/HybridCPU-Compilers/actions/runs/44896493127).
- [ ] **P1 · R2-03 · Bridge CI.** Последний наблюдавшийся parity run до merge #42 завершился failure с нулём jobs. После #42 workflow настроен на `pull_request` и `workflow_dispatch`. На финальном Bridge SHA API показал два других запуска и ни одного parity запуска. Критерий закрытия — существующие Windows/Linux jobs реально исполнились на точном SHA и сохранили отчёты byte parity и negative cases. [Последний проверенный parity run](https://github.com/yuriyyak23/HybridCPU-Compilers/actions/runs/44896567578).
- [ ] **P1 · R3-03 / HB-INT-05 · Core.** Выполнить source-derived dual-run для поддерживаемого метода, сопоставить legacy и Core HWIR, связать результат с независимым Golden/RTL evidence в этом scope. Успешный migration guards run подтверждает только проверку ограждений. [HWIR migration guards](https://github.com/yuriyyak23/HybridCPU-Compilers/actions/runs/44896546714).

## Локальное воспроизведение

### Remote and hosted refresh — 2026-11-23

`git ls-remote --heads origin master` observed Bridge `master=0d18ae1acc34a582cc8c3b93d01394bc68f598b1`, while the local checkout is dirty at `e0fdfdebd537ecfbd67b5fd853940f061a5c41ae`. GitHub identifies the remote commit's parent as `e0fdfde` and its 142 changed files (21,921 deletions) are all under `TempEnv`; the branch API reports `protected=false`. The latest three visible Bridge workflow runs are failures `44896587092` and `44896587044` on `e0fdfde`, plus `44896579817` on `66e7455`; none is on the observed remote head. The Core Actions API returned HTTP 404 and Core `ls-remote` yielded no result in 30.2 seconds, so its current remote head is unconfirmed. [Exact run/job snapshot](../../../../../CircuitWeave-main/artifacts/processor-20261114/hosted-ci-recheck-20261123/runs.json), SHA-256 `47b2ed64061ad615d3660865d29c03529e10b41708424ee8b8596e7e2c8c1627`. API metadata only; no hosted logs were fetched.

Использованы отдельные распаковки `git archive <точный SHA>` в `%TEMP%` на Windows с SDK `11.0.100`. Это фиксирует дефекты чистого дерева. Hosted конфигурация с точным RC SDK проверяется отдельно.

| Команда из чистой копии | Наблюдение | SHA-256 лога |
| --- | --- | --- |
| `dotnet build Compilers/HybridCPU_Compiler/HardwareBridge/HardwareBridge.Companion.csproj -c Release -nologo` | exit 1, 20 ошибок `CS0234/CS0246` | `6d115796c7dbfea2c3e01780583beda61a76ab7ff33f64e21b66a7ba4edcf2a0` |
| Та же Companion команда с `-c Debug` | exit 1, 20 ошибок `CS0234/CS0246` | `297448b5b987bccd1874ece1e9993ee1def2e92330dbced3f33d2b9ac559763c` |
| `dotnet build Compilers/HybridCPU_Compiler/HardwareBridge/HWIR/HardwareBridge.Hwir.csproj -c Release -nologo` | exit 1, отсутствуют три sibling refs | `7c2e48de6ce2922cc64de389355ec0b57d66ecdfd50c82ca9cc7ca5dea961838` |
| Та же HWIR команда с `-p:EnableBridgeHwirLegacyFixtures=false` | exit 0, 0 ошибок и предупреждений | `8669461a7df9b1d974ddf87d7adfd321dbb5c69869b988a0dda8d4a8ecd354bb` |

В отдельную TEMP копию добавили один локальный `CompilerEmissionPackage.cs` (SHA-256 `fcb28bbbfee6b6811c5d03b852407f6d7fcaa6fd9fe630685429be8e4b66c09a`). Companion Release прошёл с 0 ошибок и 33 предупреждениями. Этот контрольный запуск установил причину чистого отказа. Для публикации файла нужны ревью владельца и CI на новом SHA. SHA-256 контрольного лога `86dd6b717366ab1aac336ba77e6bdca2ad279ddf5b0ec824698fc9590e070855`. Логи Bridge сохранены в `%TEMP%/bridge-audit-25015497-eb62-4445-b79f-c56e25d5e831`.

На Core SHA отдельно воспроизведён `CS1012` в `RoslynIndependentBindingSourceVerifier.cs:489`. После исправления только литерала в TEMP проявился `CS0103` для `HwBindingStatus` в `QualifiedBridgeCandidatePipeline.cs:125`. Квалификация чистой сборки требует нового Core pin с обеими правками и повторной парной проверки. [Core roadmap](https://github.com/yuriyyak23/CircuitWeave/blob/master/docs/roadmap/circuitweave/11_Compiler_Pipeline_Automation_Master_Plan.md).

## Повторная локальная сверка рабочих байтов

- Historical snapshot before the 2026-11-23 remote refresh: Bridge `master` matched local `e0fdfdebd537ecfbd67b5fd853940f061a5c41ae` and API returned `protected:false`. This was superseded: remote now points to `0d18ae1acc34a582cc8c3b93d01394bc68f598b1`, one TempEnv-only commit ahead; see the dated remote refresh below. The Core/Studio 404s and unavailable credentialed `ls-remote` were observations from that historical check only.
- Windows paired no-write tests локально воспроизвели два teardown-отказа `PermissionError: [WinError 5]` на loose Git objects. Исправлена только очистка временных fixtures. Две целевые проверки прошли на Python 3.15.2. Hosted traceback по исходному запуску недоступен, поэтому причинность того failure остаётся неподтверждённой.
- Windows fixture encoder и production .NET codec с SDK `11.0.100-rc.2.26504.105` согласовали V2 index `8F4B195CE064F06DC24C67383BD57A3497A5FBCCE959BB64360605AF18F50E7E` и closure `8107F7A9B676F2A39558A3FF333FABE9E547E63A45F1E57EF4104AC679A83520`. Изменённый исходник, доклеенный байт index и смена роли reference отклонены. Квалификация R2-03 остаётся открыта до парных hosted jobs.
- Закреплённый Golden `6946ccab9d888f7dae0a4d8e18480f436138acd9` совпал с remote `master`. Локальный dirty Model имеет отдельный HEAD `1bfe41a49cf11c80fa6c6aee87accb54b78b331f`. Доказательства между ними привязываются к исходным байтам.
- R0-01 требует публикации `CompilerEmissionPackage.cs` владельцем software compiler или эквивалентного проверенного комплекта настоящих контрактов. Затем необходимо повторить Companion Debug/Release из чистого commit. Владельцу Bridge CI также нужно сверить выводы в `HardwareBridge/.artifacts/bin` с историческими путями в `hb-int01-detached-boundary.yml`. Workflow и правило исключения находятся вне согласованной области Bridge.

- Локальный RC2 R2-03 после исправления duplicate-roots mutation и проверки canonical crosswalk path прошёл 1/64/65/1 001 rows и 23 typed mutations в Python и production .NET на Windows и WSL. Отдельный no-write suite: 55 cases Windows (3 skips), 55 Linux. Оба являются локальными synthetic checks.
- R3-03 AddWord compatibility: обе трассы дали одинаковые HWIR/MLIR, 8 292 результата совпали с реальной pinned Golden assembly. Legacy использует общий bounded Core frontend, поэтому этот результат подтверждает совместимость и bounded native behavior. Он не закрывает переход остальных 59 projection entries или trusted handoff.

- R1-01 локальное production execution: exact SDK, raw Golden pin, 5 live Csc processes с MSBuild-preserved response bytes. Captured dirty Model собран отдельно и имеет 6 Csc projects. Реальные loaded inputs и outputs записаны как наблюдения. R1-03 production Companion projection и trusted Core receipt остаются открыты.

Точный Release replay с сохранённым RSP и `DOTNET_ROOT` / `DOTNET_ROOT_X64` на RC2 повторил PE, PDB и 50 generated files побайтно для raw Golden и dirty Model. Windows CIM подтвердил live Golden process argv и путь RSP, а syscall чтения отдельно не трассировался. Dirty original имеет live task logger и сохранённый RSP. Primary Golden task имеет 1 104 source и 180 references, при V1 лимитах 128/128. `CompanionSourceRequestV1` не переносит полный executed profile. Для R1-03 нужен owner-published contract и versioned observed-Csc ingress для фактического PE/PDB, полного профиля входов и independent Core verifier.

- Локальный segmented verifier теперь отклоняет неизвестный input kind и отсутствие primary project после пересчёта closure root. Необязательный pinned graph связывает node IDs, global properties и primary role с root project. Linux прошёл 123 capture/staging и 17 projection tests. Windows прошёл те же наборы с пятью и одним skips соответственно. Это synthetic integrity checks. [Результаты](../../../../../CircuitWeave-main/artifacts/processor-20261114/capture-verifier-20261121-v2/).
- Независимый Core audit проверяет canonical source-map digest и явно сообщает `sourceMapCompleteness=NotEstablished`. Самосогласованная частичная карта остаётся candidate, поскольку восстановление полноты managed IR rows требует независимого импортера. [Проверки](../../../../../CircuitWeave-main/artifacts/processor-20261114/integration-20261121/body-map-digest-audit/README.md).

Дополнение 2026-11-22: paired capture повторно проверяет pinned graph, observation и closures после обоих capture и после staging receipt (до publication); в версии v5 при выявленном drift удалял outputs; последующее исправление сохраняет частичные TEMP outputs для безопасной инспекции. На хэшированных рабочих байтах v5 Linux прошёл 142 tests, Windows — 142 tests с 6 skips. Логи и input hashes: `CircuitWeave-main/artifacts/processor-20261114/capture-verifier-20261121-v5/`. Это не hosted evidence и не execution attestation.

Коррекция paired capture: чужие TEMP-каталоги не удаляются обработчиком ошибок; частичные outputs сохраняются, итоговый receipt публикуется exclusive rename после проверок. Linux 70/70, Windows 70 с 3 skips: `CircuitWeave-main/artifacts/processor-20261114/capture-cleanup-audit-20261122/`. Произвольная конкурентная подмена путей и атомарный snapshot не устранены.

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

## Follow-up evidence — 2026-11-22

- Added a Core reader-host call to `CscClosureProfileV1.Audit` for the staged producer sidecar, with a separately supplied sidecar SHA pin; the existing raw Core V2 import remains independently checked first.
- A local synthetic producer→Core invocation passed with `CandidateOnly` (1 command, 5 files, 1 generated file); the altered-sidecar negative was rejected for SHA pin mismatch. The lowercase/uppercase digest representation mismatch in the reader was fixed using case-insensitive comparison of hexadecimal hashes.
- Code/build/test and run identifiers, pins, per-file hashes, receipt and explicit evidence limits are recorded in `CircuitWeave-main/artifacts/processor-20261114/csc-profile-sidecar-fixture-20261122/receipt.json`.
- TODO: run this path on clean pinned worktrees in hosted CI and record exact event, SHA, jobs and conclusion; add rehashed semantic mutations (ordering, roles, argv mapping, PE/PDB) to the sidecar negative matrix; connect the sidecar reader to supported CW-R3 composition without claiming execution from a capture; repeat against the exact Golden closure and resolve actual Csc/PE/PDB differences. Trust gate, protected branch, hosted secrets/dispatch, and positive trusted receipt remain unverified.

### 2026-11-22 composition update

The local CW-R3 connection is now implemented: the required external sidecar pin is audited against the imported V2 closure and rechecked by the live guard; a synthetic composition receipt reports the audit. Remove “connect the sidecar reader to supported CW-R3 composition” from the remaining TODO for the current local path. Still required: clean pinned hosted replay, exact event/job/log evidence, rehashed semantic mutation matrix, exact Golden Csc closure, explanation of PE/PDB divergence, protected-ref trust and a positive trusted receipt. The bounded `RoslynCscProfileV2` has not been widened to the Golden closure.

### 2026-11-22 — pinned Golden task-level Csc observation

The fixed Golden commit was available upstream and checked out cleanly in a new TEMP clone. SDK 11.0.100-rc.2.26504.105 completed a forced Release rebuild with 0 errors and 45 warnings. The task-time logger emitted five successful Csc events (one command event each); HybridCPU_ISE had 1,104 source arguments, 180 references and 10 analyzer switches. Receipt and raw logs: CircuitWeave-main/artifacts/processor-20261114/golden-pinned-csc-task-capture-20261122/.

Classification: UnqualifiedSoftwareObservation. The task event does not attest OS process argv/cwd or bytes actually opened, and no Core independent import, closure admission or hardware verification followed. The build used a clean clone of the pinned commit, not the dirty local Golden worktree. Two missing Release generated paths in the prior inventory recheck remain unresolved as provenance/path entries despite matching content hashes elsewhere. Golden execution witness and trusted handoff remain TODO.

### 2026-11-22 — Windows task-command projection

A new Bridge projector parses externally pinned live MSBuild Csc command events on Windows using CommandLineToArgvW, keeps ordinal tokens, and refuses response-file arguments. On the pinned Golden event set, the main project has 1,104 positional C# sources, 180 references, 10 analyzers, 2 analyzer configs and 3 embedded inputs. The preliminary receipt's sourceCount was a heuristic; the [ordinal projection receipt](../../../../../CircuitWeave-main/artifacts/processor-20261114/golden-pinned-csc-task-capture-20261122/projection-receipt.json) is authoritative for projected token categories. Stale pin and @rsp negatives refused. Cwd remains NotRecorded; the tool does not hash or stage inputs and cannot promote execution, provenance or admission.

### 2026-11-22 — portable default and pinned SDK guard recheck

The current Bridge HWIR project builds with legacy fixtures off by default (Release Rebuild, exit 0, zero warnings/errors) on pinned SDK 11.0.100-rc.2.26504.105. With legacy fixtures explicitly enabled and an empty SDK pin, it fails closed as HBINT0104 WrongSdk (exit 1). [Receipt/logs](../../../../../CircuitWeave-main/artifacts/processor-20261114/hb-p0-portable-sdkguard-20261122/receipt.json). Evidence covers dirty local working bytes at Bridge HEAD e0fdfdebd537ecfbd67b5fd853940f061a5c41ae; it is not hosted CI and does not qualify other fixture pins or Core integration.

### 2026-11-22 — hosted CI evidence refresh

Run 44896587044 (push, master, Bridge SHA e0fdfdebd537ecfbd67b5fd853940f061a5c41ae) concluded failure; its Windows Companion Debug/Release job exited 1. Run 44896579817 (push, master, SHA 66e74559e7aec675f8a72f96185e66324a175488) concluded failure; annotations show UnprotectedMasterRef and missing CompilerEmissionPackage, TypedSlotFactsEnvelope, DescriptorEnvelope, and CompilerSidebandEnvelope from the excluded Core artifact tree. Evidence: Core artifact hosted-ci-recheck-20261122/runs.json. GitHub exposed run/job annotations but required sign-in for detailed logs; R0's exact failure is therefore unknown. Both runs are historical, not validation of the current dirty changes. Owner condition: publish the missing tracked Core contracts and rerun clean exact-SHA jobs. Do not bypass branch trust; positive protected-master handoff remains unconfirmed.

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

Core accepted the pinned preflight/inventory/generated-set/adjusted-response/replay-receipt join and rehashed 50 generated files (583,941 bytes) across sealed capture, raw capture and replay roots. The candidate build passed; all 14 negative cases were refused. [Evidence receipt](../../../../../CircuitWeave-main/artifacts/processor-20261114/csc-full-closure-candidate-20261122-current/receipt.json). This is a `CandidateOnly` current-byte check, not proof of historical execution, complete input closure, analyzer ownership, independent admission, trusted handoff or hardware verification.

### 2026-11-22 — Core isolated Csc replay receipt recheck

Core verified current input and output bytes against the existing isolated replay receipt: 1,296 declared paths, 1,299 clone paths, 191 external-input hashes unchanged around the recorded replay, and PE/PDB/refout plus 50 generated outputs. All eight negative tests rejected. [Evidence](../../../../../CircuitWeave-main/artifacts/processor-20261114/csc-replay-receipt-verifier-20261122-current/receipt.json). This does not launch Csc, authenticate historical execution or establish complete analyzer side-effect coverage or trusted admission.

### 2026-11-22 — Windows task observation and cross-runtime source/IL comparison

Windows build of the isolated pinned Golden succeeded: five MSBuild Csc tasks succeeded and the tracked tree stayed unchanged. The main task projection contains 1,104 source, 180 reference, 10 analyzer, 2 analyzer-config and 3 embedded arguments without response-file tokens. Windows task source tokens were resolved against the build invocation project directory for a post-build current-file snapshot, not a Csc read-buffer capture. Linux/Windows source path sets match; 1,102 source bytes are equal, and two SDK-generated files differ only by CRLF/LF. All 1,104 sources match after newline normalization. PEReader reports 30,561 matching method definitions, method keys, raw signature blobs, raw IL byte streams, and `MethodAttributes`/`MethodImplAttributes` flags across Windows/Linux PE outputs. Custom attributes, body headers, local signatures, exception regions, type/assembly metadata, and full PE/PDB semantics were not compared. [Receipt](../../../../../CircuitWeave-main/artifacts/processor-20261114/golden-windows-csc-task-observation-20261122/attempt-03/receipt.json).

PE/PDB and Csc DLL hashes differ. This does not establish Windows OS process/read buffers, complete external-input capture, analyzer side-effect closure, trusted admission or hardware equivalence.




### 2026-11-22 — Windows Csc process and response bytes

A repeat clean pinned Golden Windows Release build observed five actual `csc.exe` processes using 35 ms `Win32_Process` polling. The capture records each executable path and hash, PID/parent PID, process command line and `@response` path, with copied and SHA-256-pinned bytes for all five response files. All five MSBuild Csc events succeeded; build exit was 0; tracked Golden tree remained `243a67c8ef462c1da2d1f06df9bee2c31508cb40`. [Process receipt and captured response files](../../../../../CircuitWeave-main/artifacts/processor-20261114/golden-windows-csc-task-observation-20261122/attempt-05/receipt.json).

This supplies a Windows OS-process observation and actual response-file bytes, beyond the previous task-only evidence. Sampled WMI may miss short-lived processes; captured response bytes are not proof of Csc read buffers, and no complete compiler-input closure, trusted handoff, independent Core admission or hardware result is established. Five token lists matched exactly by ordered comparison to the corresponding MSBuild task event after matching output tokens; this does not prove Csc read those response bytes.

### 2026-11-22 — Linux Csc reference/analyzer input reads (historical earlier capture; superseded by the fresh 186/196-input capture below)

Extended the pinned Linux Csc trace analysis across main response-file filesystem inputs. An independent response parser confirmed exact set equality: all 196 distinct declared non-source inputs were opened by the traced Csc process/thread tree, with no missing, extra or duplicate paths. Recorded `read`/`pread64` bytes fully reconstructed and matched pinned post-build bytes for 180 inputs / 5,338,258 bytes: 178 references, one analyzer config and one embedded input. Sixteen remain incomplete: 10 analyzers, one analyzer config, two embedded sources, two references and SourceLink. [Detailed receipt and capture](../../../../../CircuitWeave-main/artifacts/processor-20261114/golden-linux-csc-input-read-closure-20261122/receipt.json), SHA-256 `6f3c17d4417c9c9e1cce3d9c2c5310c9086b01bb53a1ece78d7d1125deec3aac`.

This remains local unqualified trace analysis. File opens do not prove complete file bytes; mapped/truncated inputs and generator side effects remain open. It is not Windows parity, trusted handoff, independent Core admission or hardware evidence.

Verifier `verify-closure-capture.ps1` passed 207 pinned-input, read-byte and artifact-hash checks; full analyzer read bytes remain unobserved.

### 2026-11-22 — Linux Csc analyzer mappings and expanded read reconstruction

A fresh clean pinned Golden Linux Release build (commit `6946ccab9d888f7dae0a4d8e18480f436138acd9`, tracked tree `243a67c8ef462c1da2d1f06df9bee2c31508cb40`, Ubuntu WSL SDK `11.0.100-rc.2.26504.105`) succeeded with five successful Csc task events and unchanged tracked state. The 196 paths in the retained-response/current-task crosswalk were all observed opened; 186 inputs / 6,802,994 bytes matched reconstructed read buffers to pinned post-build bytes (180 references, three embedded inputs, two analyzer configs, SourceLink). All 10 analyzer DLLs had successful offset-zero `PROT_READ` mappings spanning their file lengths and their post-build file images were separately copied/hash-cross-checked 10/10. That is mapping/file-image evidence only: analyzer contents/page faults are not established and zero analyzer read buffers are complete. [Receipt](../../../../../CircuitWeave-main/artifacts/processor-20261114/golden-linux-csc-input-mmap-observation-20261122/receipt.json), SHA-256 `a2a995888080e1351e44fed6102d38f4d7eb06f2bdd6c819fe415c576d3200d9`; independent receipt audit and 218-check verifier passed.

The current temporary main response file was not copied from disk, but its bytes were subsequently reconstructed from 33 `pread64` buffers in the same Csc PID 705 trace: 124,246 bytes, SHA-256 `0c4cae5ac1aca2cdb6b6df1c7e7ab55fe98a773927760b568c563db3d0d17b74`. The exact response bytes match the current successful task's ordered-argument crosswalk and 196-path open set; seven verifier checks pass. [Supplemental receipt](../../../../../CircuitWeave-main/artifacts/processor-20261114/golden-linux-csc-response-read-capture-20261122/receipt.json), SHA-256 `611ef88544589e016dd7a2ae00a7fcf592ff62c08a7ce7516fb122b36fe8c10a`. Windows read parity, analyzer/generator side effects, independent Core admission, trusted handoff and hardware verification remain open.

### Cross-repository follow-up — 2026-11-22

Independent audit of current HB-INT-01 bytes found no remaining portable-default or empty-SDK-guard defect: default HWIR rebuild passed, portable dependency boundary passed 8/8, parity guard passed 9 with one Windows symlink skip, and an opt-in empty SDK pin failed with `HBINT0104 WrongSdk`. Follow-up: positive legacy opt-in passed against clean Core clone `714e5c939bb1817481cf4970e3e343aa92bfa23b` on available SDK `11.0.100`, and wrong SDK pin failed closed; the required RC2 positive path and hosted trusted gate remain open. Current Core HEAD reran the synthetic 187-check, 129-source/129-reference/two-root profile executable and CW-R3 composition fixture; this is not Golden replay. Evidence: `CircuitWeave-main/artifacts/processor-20261114/bridge-optin-current-core-20261122/receipt.json` and `core-current-csc-cwr3-20261123/receipt.json`.

### Hosted run refresh — 2026-11-22

The Bridge remote master moved to `0d18ae1acc34a582cc8c3b93d01394bc68f598b1`; comparing with local HEAD `e0fdfdebd537ecfbd67b5fd853940f061a5c41ae` shows one commit consisting only of `TempEnv` removals outside this audit's authorized subtree. No workflow run is listed for `0d18ae1`. The most recent relevant runs remain failed push events on `e0fdfde`: HB-INT-06 `44896587092` failed its no-secret preflight and Windows Bridge contract/source/mutation job; R0-01 `44896587044` failed its Windows production graph/build/PE/source-immutability job. Job log requests returned 403; step-level failure reasons cannot be confirmed from raw logs. The CircuitWeave Actions API returned 404 to this unauthenticated query. No positive trusted handoff is evidenced.

### Linux analyzer image snapshot — 2026-11-22

Ten analyzer DLL images were copied from the pinned Linux Csc PID 705 mmap paths; all captured bytes match the post-build hashes in the pinned trace report. The verifier checks parent/report pins and exact path/length/hash crosswalk for 10/10. Receipt SHA-256 `c3b4a3527b24cd686d560a7a665e8fc08f8d4cad884b31e6efe2e68af95fe859`. This establishes current file image availability, not page faults, analyzer output or filesystem side effects.


### Current local suite and Windows crosswalk — 2026-11-22

Independent audit of attempt-05 matched all five task event ordered argument vectors to the five copied response files and one-to-one sampled `csc.exe` PIDs. The process sample is not complete process tracing; response copies are not compiler read-buffer proof. Existing Windows task-event projection refuses `@rsp` inputs and does not bind response-byte hashes into its evidence model; a dedicated bundle adapter remains required.

Current Bridge Python suite command `python -m unittest discover -s HWIR/EvaluatedProjectGraph -p 'test_*.py' -v` passed 148 tests, skipped 6 symlink cases, exit 0 on HEAD `e0fdfdebd537ecfbd67b5fd853940f061a5c41ae`. Log SHA-256 `4ad9b67fe605abe068becb431fc1df2de405962ee1cde54507f33a7969682ca4`, receipt SHA-256 `d7c868f28cd78065af230066975d03bdfdd5f8741d0a9117f1035a8399cb24c3`. These are tests/fixtures only and do not qualify a Golden capture or Core receipt.


### Windows attempt-05 candidate bundle and expanded suite — 2026-11-22

Adapter validation rechecked externally supplied SHA pins for the task events, process observation, crosswalk, capture receipt, five response copies and the actual compiler executable. Five ordered commands projected; [receipt](../../../../../CircuitWeave-main/artifacts/processor-20261114/windows-csc-task-rsp-bundle-20261122/receipt.json) SHA-256 `49e2a25351a8c68e25364352afd6f2cb20373ad8464c50fda492618a536700cf`. The output is unqualified transport evidence: copied response bytes are not consumption proof, cwd remains unknown, and no Core admission/trust/hardware claim is made.

After adding a top-level test-discovery wrapper for only these adapter tests, standard Bridge discovery passed 162 tests with 6 symlink skips, exit 0; [receipt](../../../../../CircuitWeave-main/artifacts/processor-20261114/bridge-hbint02-adapter-discovery-20261122/receipt.json) SHA-256 `f98174ccbb5f91e3889b720492d09f7e12f8a8bdf5a751850e29253b0d11b199`.


### Exact Windows-to-Core staging gate — 2026-11-22

The bundle is not yet a reviewed HB-INT-02 capture accepted by `stage_core_v2.py`: that producer requires a same-invocation `workingDirectory`, evaluated Compile item completeness, exact source-map closure, project graph/root pins and generator-output directory identity. This bundle intentionally reports cwd NotRecorded and contains task/process/response crosswalk only. Do not infer cwd from the project path or launcher. Required evidence before staging: capture cwd directly for each Csc invocation, bind the corresponding evaluated project graph/Compile inventory and generated-output closure to task/PID/response pins, archive current source/reference/analyzer/config/embed bytes, then run Bridge staging and independent Core V2 verifier with external pins.


### 2026-11-22 — Windows Csc launch, path grammar, and closure capture

Pinned Golden `6946ccab9d888f7dae0a4d8e18480f436138acd9` / tree `243a67c8ef462c1da2d1f06df9bee2c31508cb40` was built in a separate isolated clone with SDK `11.0.100-rc.2.26504.105`. The disposable Csc launch probe captured launch-time cwd and raw RSP bytes under a read-only handle through child exit; it crosswalked five successful Csc tasks to five child PIDs/exit-0 records and exact parsed expanded args. Same-build target captured 1,191 Compile items and their set matched all Csc source operands. 1,176 tracked source inputs matched pinned Golden bytes; 15 `obj` compile inputs were MSBuild-generated. The emitted source directory contained 50 `.cs` outputs. The producer captured and reverified 2,218 input rows (86,594,404 bytes), closure `0046f8dde1a242beb95e2c587c4f7d96e54ee20b17d2b33ae9d9415bddc271dc`, index SHA `6cb4dc2e4fb15cebb8a3a629ec6521c5535423c9970ea88fe34f7fccdfda6f4e`; persistent bundle: `CircuitWeave-main/artifacts/processor-20261114/golden-windows-csc-capture-20261122/capture-v2`. Direct Csc rebuild matched all 44 instrumented PE/PDB output hashes.

Actual SDK analyzer inputs exposed `/debug-`, `/embed:` and paths containing `targets/../analyzers`. The Bridge parser now recognizes `/debug-`, embeds and metadata role, and resolves compiler input components before collapsing `..`, refusing traversal through symlink/junction/reparse components. Core's deprecated V2 projection stores `embedded` as metadata-only. The component walk is non-atomic and assumes a sealed, non-mutating snapshot. This evidence does not prove Csc consumed the captured RSP or source/analyzer bytes, close analyzer side effects, or qualify Core admission/trust or hardware. No approved Processor roots were supplied, so Core staging, HWIR/RTL and simulation remain open.


The post-slice Bridge discovery run passed: `python -m unittest discover -v`, 177 tests, 12 platform-dependent skips, exit 0, Bridge HEAD `e0fdfdebd537ecfbd67b5fd853940f061a5c41ae`. [Receipt](../../../../../CircuitWeave-main/artifacts/processor-20261114/bridge-hbint02-discovery-windows-capture-20261122.receipt.json), log SHA-256 `0de90f4d2357d1c2c3540bf548662904ade03b90eeac7ad699fe4739cc3b096c`. This is local producer regression evidence only; hosted CI, independent Core admission and hardware qualification remain open.


**Graph provenance correction:** the pinned graph manifest uses values captured by an injected `BeforeTargets=CoreCompile` target in the observed build; this was not a separate MSBuild `ProjectGraph` evaluation. `compileFiles` contains 1,176 Golden-origin inputs. The other 15 Csc source operands are build-generated `obj` files marked separately as `msbuild-generated`; the crosswalk verifies all 1,191 target Compile items against the response-derived Csc source set. Fixture test success, same-build target evidence and instrumented OS child execution remain separate evidence classes.

### P0 recheck — exact pinned SDK positive opt-in, 2026-11-23

The earlier local opt-in pass used SDK `11.0.100`, below the plan pin. Follow-up downloaded the official Windows x64 SDK `11.0.100-rc.2.26504.105`; archive SHA-512 matched release metadata. Release Rebuild with legacy fixtures explicitly enabled and `BridgeExpectedSdkVersion` set to the exact pin passed against the clean TEMP Core clone at `714e5c939bb1817481cf4970e3e343aa92bfa23b` (exit 0, 0 errors, 1,021 warnings). Receipt SHA-256 `b3233082bddb429c705820cea781e206760d9d5b02621bffd6302ac5dcc9fe67`. The Bridge worktree remains dirty; this is local opt-in build evidence only. R0 Companion still fails because tracked compiler contracts are absent; no hosted trusted receipt or hardware qualification is implied.

### Core importer/composition regression refresh — 2026-11-23

Exact pinned Core SDK `11.0.100-rc.2.26504.105` rebuild/retest passed CW-INT-03 (61), CW-INT-04 (187) and CW-R3 fixture suite (exit 0). This confirms current synthetic reader→composition linkage and fail-closed tests only; real Golden staging, approved selectors, hosted trusted admission, and hardware qualification are still open. Receipt SHA-256 `5550d2b144851db236d96e2a75492f66ba5ec732a490b44fb89fb6c89517c6fe`.

### Captured Golden input closure recheck — 2026-11-23

The retained actual Windows Csc capture passed an independently implemented Core-side SHA-256 recheck across 2,218 entries / 86,594,404 bytes / 2,241 chunks and five command argv digests; wrong index pin refused. Exact primary operand counts and 110 analyzer/config/generated-source inventory records are in `CircuitWeave-main/artifacts/processor-20261114/golden-csc-inputs-recheck-20261123/report.json`. This narrows the software blocker: source/reference counts fit current bounds, but profile V2 does not support captured analyzer/generator/generated-source and compiler-switch inventory. Capture pins remain unapproved producer-derived comparisons. Owner-approved roots are absent, so Core V2 stage/import was deliberately not attempted. Receipt SHA-256 `6cad8b8f60279680a3c4d1b866df0968d93c07fc54e71d8a20bebcde9589c03a`.

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


### Generator-derived-order Roslyn replay — 2026-11-23

The full replay was rerun with all 50 generated-source rows ordered from the independent pinned-analyzer GeneratorDriver manifest, without feeding PDB order into that mode. The emitted PDB document order matched all 1,154 Golden document paths, and PE/PDB hashes matched the separate PDB-guided diagnostic output (`861292cb…` / `1319ba65…`) byte-for-byte. Against Golden PE/PDB, hashes still differ (`1ed30367…` / `164f6a91…`). This run supplied no Win32 VERSIONINFO resource and remains diagnostic. [Harness, manifest, log and receipt](../../../../../CircuitWeave-main/artifacts/processor-20261114/golden-generator-order-replay-20261123/receipt.json), receipt SHA-256 `21bb9bf5f1a5fa9f5d67eb6a487fe6e6f0726dfb8b3bc2d5094ab5db3adcb95d`. Next: capture and bind the exact resource bytes to observed MSBuild/Csc execution, then continue PE/PDB differential and independent Core admission. Integration remains INCOMPLETE.


### Historical captured-metadata VERSIONINFO differential (superseded below) — 2026-11-23

The pinned Roslyn `CSharpCompilation.AppendDefaultVersionResource` generated the version-resource record from the captured source/reference compilation and the independent generator-order manifest. A standard null `.res` header was added for linking; no bytes extracted from the Golden PE were used as replay input. The generated 828-byte RT_VERSION payload hash matched the payload extracted from the Golden PE as a comparison check, and replay PE SHA matched the prior output-derived-resource diagnostic variant. PDB document paths matched all 1,154 Golden documents. Full equivalence remains false: actual/replay PE `1ed303674a94b65276ae56e57d877647d7e28400ac8c9b4d7ea6a4371d7b2859` / `7e113c5596fed213ed91c2e41249e9d0e769d728b4fc014500ae99998499884a`; actual/replay PDB `164f6a911f94715ead588e843c6fcb2a6c77ebc5ae920fbedafa32fb80af718a` / `1319ba65368953d0d82c248de87dd21f9e1a8fda5d1ba8d069ea785d639475b6`. The capture evaluation has empty `Win32Resource`; default resource synthesis is compiler behavior, not a Csc argv input. This narrows the resource question but does not attest the original Csc execution or prove independent Core receipt. PDB semantic comparison found all 1,154 documents, 30,561 method-debug records, 71,642 sequence points, 25,504 scopes, 8,152 locals, 44 constants and 1,006 CDI keys aligned; the only row payload differences are the 50 generated-source EmbeddedSource compressed blobs, whose decompressed content is identical. PDB byte sizes differ by 612 bytes. [Semantic comparison and compression probe](../../../../../CircuitWeave-main/artifacts/processor-20261114/golden-captured-version-resource-replay-20261123/pdb-semantic-diff.log). [Probe, resource, replay and receipt](../../../../../CircuitWeave-main/artifacts/processor-20261114/golden-captured-version-resource-replay-20261123/receipt.json), receipt SHA-256 `5940b13c82299236f4172e8df9e466b3b7a06f322a8ab0971d128f56fe9a5b78`. Next: resolve PE/PDB serialization deltas and obtain trusted independent Core admission; integration remains INCOMPLETE.

### Follow-up: exact captured Roslyn replay — 2026-11-23

The previous captured-metadata VERSIONINFO replay section is superseded on PE/PDB parity: exact local replay now matches both pinned Golden outputs byte-for-byte. The key remaining source of prior PDB delta was generated `SourceText` checksum metadata: replay must honor `/checksumalgorithm:SHA256`. The result is retained in `CircuitWeave-main/artifacts/processor-20261114/golden-generator-direct-source-checksum-replay-20261123/receipt.json` with harness/report/log. This is not original Csc execution attestation, Core admission, hosted trust, or RTL/simulation verification. Update CW-INT-04 as locally replayable, but retain R0-R3 and overall integration as open.

### Core reader boundary and replay provenance follow-up — 2026-11-23

Core source inspection confirms the V2 Csc profile is already propagated through CW-R3 composition. Its refusal of analyzer/generator/generated-source rows is deliberate fail-closed behavior, not a missing composition link. The local exact replay rehashed the ten analyzer paths against retained capture rows before `AnalyzerFileReference` loaded them, but the load was path-based and was not atomically bound to the checked file handles; there is no original Csc read witness. CW-INT-14's existing ToolBridge audit still blocks approved process execution with `CW-EXT01-ISOLATION`. Resolution requires a distinct bounded Core replay contract plus a qualified isolated execution facility, followed by independent Core receipt. No positive handoff is claimed.

### Processor SignExtendWord scope extension — 2026-11-23

An exhaustive software differential invoked pinned Golden PE MethodDef `0x0600180d` for all UInt32 values and matched `unchecked((ulong)(long)(int)input)` with zero mismatches. Candidate RTL is pinned to SHA-256 `85c57d6caff229d79dac217e13cda8026f4606a400619c4ad933a36c1305ad72`, emitted at Core HEAD `b5c44685197a40df99a4d48891120b12475c6ca5`, and implements the same sign-extension expression. At `0x80000000` the zero-extension alternative differs; the prior finite native receipt separately records an RTL mutant mismatch. [Receipt/report/harness/log](../../../../../CircuitWeave-main/artifacts/processor-20261114/integration-20261121/sign-extend-full-domain-20261123/receipt.json), SHA-256 `96356ace545662e1816b2fd72d25909ea091e7d1d302bc47a12a420408055280`. The all-domain result compares PE behavior with the expression, not RTL execution. Native RTL is still verified only on 196,608 finite inputs; source admission and Studio remain NotRun. This does not close Processor/stateful or R3 acceptance.

### Current-Core helper projection recheck — 2026-11-23

The exact pinned `SignExtendWord` source projection was rebuilt against CircuitWeave HEAD `9d0b4b9f50ab30bba3bea5eb2d59a4509cf0f435`; current frontend verdict was `Admitted`. HWIR/MLIR/vector hashes match the prior `b5c44685197a40df99a4d48891120b12475c6ca5` projection. Receipt: `CircuitWeave-main/artifacts/processor-20261114/integration-20261121/sign-extend-generic-current-core-20261123/receipt.json`, SHA-256 `2a303689fe98b25e4657f099b3bc5c0e44b74bb2e90a663f886ad13928cc6d2f`. This does not refresh native RTL/Studio output or qualify the full Csc/Bridge closure; pinned CIRCT was not locally available and full source admission remains open.

### HB-INT-01 local recheck — 2026-11-23

Current authorized Bridge working bytes were copied to `%TEMP%` and built against a clean Core clone at `9d0b4b9f50ab30bba3bea5eb2d59a4509cf0f435` with SDK `11.0.100`. Core Frontend.Roslyn, portable default, and explicit legacy opt-in builds passed; default evaluated zero Core ProjectReferences. A deliberately wrong SDK pin failed closed as `HBINT0104 WrongSdk`. Receipt: `../../../../../../CircuitWeave-main/artifacts/processor-20261114/bridge-build-current-core-20261123/receipt.json`, SHA-256 `9dd5363e27957dcdf048a26ddc341e49ef2b1d1387294d91985cd574890f1f2a`. This updates local qualification only. TODO remains: qualify exact tracked Bridge SHA on hosted Windows/Linux, publish reviewed compatible pins, and obtain positive trusted Core import receipt. Companion tracked compiler contracts remain owned outside the permitted Bridge subtree.


### CW-INT-14 Linux isolation mechanism probe — 2026-11-23

Core-side WSL2/bubblewrap/cgroup v2 fixture evidence is recorded at `../../../../../CircuitWeave-main/artifacts/processor-20261114/cw-int14-bwrap-probe-20261123/receipt.json` (SHA-256 `e422608eef895b861fd011c6326ae6c3dc771fae6bcefbb54421abe291eaf30c`). It confirmed namespaces, read-only input/closed host mounts, zero capabilities, network denial, and observed CPU/memory/PID limits for a Python adversarial fixture. No Core-approved ToolBridge executor or Roslyn generator ran; this does not close CW-INT-14, HB-INT-03/06, or trusted handoff. Exact hosted Windows/Linux qualification remains open.


### CW-INT-14 Roslyn generator sandbox follow-up — 2026-11-23

A real Roslyn `GeneratorDriver` fixture ran a hostile generator in Ubuntu WSL2/bubblewrap with cgroup v2; the exact SHA-pinned assembly byte array was loaded, and positive output repeated byte-identically. Wrong/altered analyzer pins, altered AdditionalText, memory, CPU and child-process attacks were refused or contained. Core artifact receipt: `../../../../../CircuitWeave-main/artifacts/processor-20261114/roslyn-generator-isolation-20261123/receipt.json` (SHA-256 `451a55035e5132687bd798993e1eb394e3080e1a97eda429c93930b847f9b17e`). This does not integrate the Core ToolBridge or production Csc handoff; no Golden or Bridge production generator was run. CW-INT-14/HB-INT-03/HB-INT-06 and trusted acceptance remain open.

Core source audit confirms that this fixture cannot be promoted through EXT01
as-is: approved execution still refuses, its planner binds absolute workspace
paths, and the generic ToolBridge observation carries no isolation receipt.
Keep this boundary closed. Next Core-owned work is a separate immutable-byte
Roslyn replay and Linux sandbox contract with sandbox-relative inputs, exact
runtime/tool pins, bounded outputs, verified cgroup/namespace controls and
receipt validation; Windows and hosted evidence remain additional gates. This
does not change Bridge package acceptance or satisfy HB-INT-03/06.

Core's byte-only `CircuitWeave.RoslynSandbox` request/result foundation now
passes 22 structural assertions; it still has no process host. Receipt:
`CircuitWeave-main/artifacts/processor-20261114/roslyn-sandbox-contract-20261123/receipt.json`,
SHA-256 `2e1c3c754891063881ef3cd8e91807863c9fb3920109cefe119e55de55c64849`.
Do not interpret it as Core generator execution, Csc profile admission, or a
trusted handoff. EXT01 remains deliberately fail-closed pending native host
qualification.

Update 2026-11-23: Core now has a separate fixed-purpose Linux Roslyn
supervisor/host/worker. Its root-WSL fixture covers repeated output bytes and
CPU/memory/PID/timeout/cancellation containment. The hostile-generator test
also forges a valid worker output frame and exits before GeneratorDriver
completes; the host accepts the frame, proving its output/receipt is not
execution evidence. Evidence:
`C:/Users/Yuriy/Desktop/CircuitWeave-main/artifacts/processor-20261114/roslyn-sandbox-linux-host-20261123/receipt.json`.
The host also removed the unpinned Bash dependency and waits for cgroup cleanup.
This is mechanism-only evidence. It does not qualify unprivileged delegation,
hosted CI, Windows, the Bridge producer, authenticated receipt import, or Core
source admission; EXT01 and HB-INT-03/06 remain open.

Update 2026-11-23 — replay scope and dependency rechecked against retained
Golden artifacts. The primary Csc input byte check covers 2,218 rows /
86,594,404 bytes and five commands, but its report states owner-approved Core
roots are missing and Core import was not run. The separate GeneratorDriver
witness is a useful independent replay observation (50 generated texts match
after BOM removal), not evidence of original Csc execution or trusted Core
admission. Do not reuse its result as a production output receipt. Actionable
owner condition: Core must publish approved V2 root selectors for the exact
Golden capture and independently import the pinned closure. Then qualify a
strict adapter over all five command profiles, preserving supported options,
analyzer configs and source encodings or refusing the command. Until then,
HB-INT-03/06 and CW-INT-14 remain open; this audit does not qualify full
Golden-to-RTL or stateful Processor integration.


Update 2026-11-23 — Core independently re-read the exact raw Csc capture bytes
through its new unqualified inventory reader (5 commands / 2,218 rows / 2,241
chunks / 86,594,404 bytes, exact argv crosswalk for 2,153 rows, five negative
refusals including mismatched argv path; receipt SHA-256
`ff84e8c7f41f5a613e2f6d6cd911d10361b5db87683e7c2b8773c3370d5d6dba`). This
closes raw input byte verification only. Do not describe it as the staged Core
V2 source import: owner-approved roots remain missing, and the raw Csc profile
has not been converted into the Core source-owned contract. Continue with the
separately pinned root manifest and full source-scope handoff; HB-INT-03/06 and
CW-INT-14 remain open.


### 2026-11-23 — Processor selector candidate audit

A Core candidate note pins `ScalarAluOps.AddWord` to the bounded Golden Debug PE/PDB and token `0x06001837`; an independently retained Release body audit pins the same named method to a distinct PE and token `0x06001834`. These are configuration-specific identities, not interchangeable selectors. The current full-replay PE/PDB is a third pair. No selector set is owner approved or complete for Processor. See Core candidate record SHA-256 `9e44adb21e129de02cfda57242ae84043e3973615560a9f0c2b6a17f4a9891ab`. Required closure: owner chooses one exact PE/PDB pair, approves complete selectors and source/contract scope, then Core revalidates and imports a sealed capture. Until then, no positive source-admission receipt is claimed.

### 2026-11-24 — Raw capture to Core staged V2 transport

Core now has `HardwareBridgeRawCaptureStagerV2`, an explicit adapter between the raw `hybridcpu.hardwarebridge.csc-closure-index/v2` byte importer and the distinct `circuitweave.bridge-source-closure-index/v2` reader. It first reimports raw pins, then copies and rehashes every row/chunk into a fresh sibling workspace, emits a deterministic Core V2 index and a separate raw-path crosswalk, independently verifies the staged closure through both Core reader layers, and atomically publishes only to an absent destination. The exact capture passed ten positive/negative fixture checks on Windows and Ubuntu 26.04.1 WSL2 with identical staged index, closure and map hashes. Receipt: `../../../../../CircuitWeave-main/artifacts/processor-20261114/csc-capture-core-stage-20261124/receipt.json` (SHA-256 `8d7fa612fc1ce0e88b86c152c964f1c7686719828713696295535c1dbb4f10ad`).

The output remains `CandidateOnly` / `NotAdmitted`: test roots are mechanics inputs, not owner approval, and the stage receipt itself does not establish PE/PDB identity; subsequent PEReader candidate review v2 now binds AddWord to the full-replay pair, but does not establish owner approval or complete Processor scope. It does not create the unsupported Roslyn Csc profile or run CWR3 composition; analyzers/generators/generated-source execution still needs a separate isolated profile. It also does not establish an atomic snapshot of mutable raw inputs, production Csc read/execution, trusted handoff, HWIR/RTL, simulation, or Processor equivalence. Integration remains INCOMPLETE.


### 2026-11-24 — Full-replay AddWord selector identity

A Core PEReader probe now resolves `ScalarAluOps.AddWord` in the byte-identical pinned full-replay PE/PDB pair as MethodDef `0x06001834` (PE `1ed303674a94b65276ae56e57d877647d7e28400ac8c9b4d7ea6a4371d7b2859`, PDB `164f6a911f94715ead588e843c6fcb2a6c77ebc5ae920fbedafa32fb80af718a`; MVID `ff458091-35ad-4db1-8458-a00fa367ffce`). This resolves the prior note that the replay token had not yet been checked. Candidate review v2: [Core artifact](../../../../../CircuitWeave-main/artifacts/processor-20261114/processor-root-candidate-review-20261124/candidate-review.json), SHA-256 `7701ac986c0f2d4613befa3d11c02667592d1fe8a9ac86f88d8bcf0dbf073b1e`.

The result pins one scalar method only. It is not owner approval or a complete Processor root manifest. Source admission and Processor integration remain INCOMPLETE.


### 2026-11-24 — Core staged Csc closure profile

Core now emits and independently audits `CscClosureProfileV1` from the pinned raw Bridge capture and the staged Core V2 index. The sidecar covers all five commands, 2,218 raw input rows, 2,153 argv operands and 50 generated-source rows. Windows and Ubuntu WSL2 produced identical index/map/profile hashes. Receipt: [Core artifact](../../../../../CircuitWeave-main/artifacts/processor-20261114/csc-capture-core-stage-20261124/receipt-profile-v2.json), SHA-256 `507902222f749b250e651cad7c01b0be7cecd0d829693768a8d067a5d9bc1da1`.

This confirms a candidate closure crosswalk only; it does not attest original Csc reads/execution, source ownership, or admission. Roslyn re-emission profile V2 still rejects analyzers/generators/generated sources, and no CWR3 composition or trusted handoff is claimed. Integration remains INCOMPLETE.

### 2026-11-24 Core source-join correction

Core's first exact-hash join flagged the AddWord source row; a byte-level follow-up proved the difference is 543 Windows CRLF pairs only. CRLF-to-LF normalization matches the pinned Golden source SHA. The corrected Core receipt is CircuitWeave-main/artifacts/processor-20261114/processor-root-source-capture-join-20261124/receipt-v2.json with SHA-256 `afa4c3e4565fb91330fe3fd1a83c8e7474bd76e9fe5f87eacfca8b807221379f. Raw-byte identity, complete owner-approved roots and source admission are still not established.



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
