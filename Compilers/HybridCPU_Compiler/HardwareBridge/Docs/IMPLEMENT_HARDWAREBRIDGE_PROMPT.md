# HB-01 — Compact HardwareBridge for HybridCPU Compilers (.NET 11)

> **Статус документа:** готовый промпт для отдельного implementation PR. Этот файл — постановка задачи, не подтверждение реализованного bridge.
>
> **Строгий write-allowlist:** `Compilers/HybridCPU_Compiler/HardwareBridge/**` в **`yuriyyak23/HybridCPU-Compilers`**. Всё остальное — **только чтение**, включая соседние каталоги компилятора, `HybridCPU ISE`, `CircuitWeave` и `CircuitWeave-Studio`.

## Роль и конечный результат

Ты — Principal .NET 11 / Roslyn / CIL / compiler provenance engineer. Реализуй **ровно один небольшой, законченный и проверяемый HardwareBridge** для существующего программного компилятора HybridCPU. Его назначение — **read-only экспорт source identity, уже имеющихся compiler facts и доверенных Golden-наблюдений**, пригодных для дальнейшей обработки независимым CircuitWeave. Один независимый **draft PR** в `yuriyyak23/HybridCPU-Compilers`, база — актуальный `master`. **Не внедряй собственный C#→RTL, HWIR, hardware DSL, CIRCT, formal verification, EDA runner, waveform viewer или binding resolver.**

## Проверенные исходные ориентиры

Перед работой снова проверь точную ревизию `master`, интерфейсы и правила включения исходников:

- [HardwareBridge](https://github.com/yuriyyak23/HybridCPU-Compilers/tree/master/Compilers/HybridCPU_Compiler/HardwareBridge): уже есть `SourceSnapshot/`, `CompilerFacts/`, `GoldenOracle/`, `Tests/`, `Docs/` с README; расширяй эти каталоги, а не создавай конкурирующую подсистему.
- [Основной проект](https://github.com/yuriyyak23/HybridCPU-Compilers/blob/master/Compilers/HybridCPU_Compiler/HybridCPU_Compiler.csproj): `net11.0`, default recursive `Compile` для `HardwareBridge/**/*.cs`, проектная ссылка на ISE; **файл вне allowlist изменять запрещено**.
- [ManagedSourceCompanionV1](https://github.com/yuriyyak23/HybridCPU-Compilers/blob/master/Compilers/HybridCPU_Compiler/Roslyn/ManagedSourceCompanionV1.cs), [Input](https://github.com/yuriyyak23/HybridCPU-Compilers/blob/master/Compilers/HybridCPU_Compiler/Roslyn/ManagedSourceCompanionV1.Input.cs), [BodyWorld](https://github.com/yuriyyak23/HybridCPU-Compilers/blob/master/Compilers/HybridCPU_Compiler/Roslyn/ManagedSourceCompanionV1.BodyWorld.cs): `CompanionSourceRequestV1`, `CompileBodyWorld`, `SourceInputDigest`, принадлежащие PE/PDB, distinct body/metadata references; не делай второй Roslyn-компилятор.
- [CompanionArtifactIdentityV1](https://github.com/yuriyyak23/HybridCPU-Compilers/blob/master/Compilers/HybridCPU_Compiler/Roslyn/CompanionArtifactIdentityV1.cs): различай digest входа и PE/PDB output identity.
- [ManagedSourceMapV1](https://github.com/yuriyyak23/HybridCPU-Compilers/blob/master/Compilers/HybridCPU_Compiler/Cil/ManagedSourceMapV1.cs), [RestrictedCilImporterV1](https://github.com/yuriyyak23/HybridCPU-Compilers/blob/master/Compilers/HybridCPU_Compiler/Cil/RestrictedCilImporterV1.cs): точные сигнатуры, provenance, ограничения PDB. Primary PDB **не подтверждает** dependency methods.
- [CircuitWeave](https://github.com/yuriyyak23/CircuitWeave), [Bindings](https://github.com/yuriyyak23/CircuitWeave/tree/main/src/CircuitWeave.Bindings), [Roslyn hardware frontend](https://github.com/yuriyyak23/CircuitWeave/tree/main/src/CircuitWeave.Frontend.Roslyn), [Independent verification](https://github.com/yuriyyak23/CircuitWeave/tree/main/src/CircuitWeave.Verification), [External tools roadmap](https://github.com/yuriyyak23/CircuitWeave/blob/main/docs/roadmap/09_External_Tool_Adoption_2026-11-10.md). Эти системы **владеют hardware-контрактами и квалификацией**.
- [CircuitWeave-Studio](https://github.com/yuriyyak23/CircuitWeave-Studio): только внешний read-only consumer опубликованных позже артефактов, без прямой зависимости данного bridge.

**Базовая ревизия для постановки:** `HybridCPU-Compilers/master` = `8bd3fe612f6c5f9ea007639c985103f6653ea3af` (10.11.2026). На момент выполнения обязательно перейти на **последний** `master` и проверить изменения.

## Жёсткие ограничения

1. **Write-allowlist:** только `Compilers/HybridCPU_Compiler/HardwareBridge/**`, включая тесты и документацию. Не менять `.csproj` снаружи, `*.slnx`, `global.json`, CI, Roslyn/CIL/Core, ISE, CircuitWeave или Studio; не создавать PR в других репозиториях.
2. Все выходы HardwareBridge — **diagnostic/candidate-only**. `SourceInputDigest`, PE/PDB SHA и совпадение Golden-векторов **не являются proof**. Никаких `AcceptedRtl`, `Verified`, успешного EDA-status или самоаттестации.
3. **Не добавляй атрибуты в Golden ISE.** Hardware Attributes объявляют намерение, Binding Manifest и resolver принадлежат CircuitWeave, `HwComponent` — независимой библиотеке CircuitWeave. Bridge лишь предоставляет проверяемый входной материал.
4. Не выполнять непроверенные сборки/плагины/assembly от проекта через `Assembly.Load`, reflection invocation, dynamic code, `Process.Start`, shell, network или автоматический поиск файлов. Golden oracle — **только явный, доверенный, allowlisted delegate/adapter** с контролируемыми входами.
5. Минимизировать API, зависимости и код. Использовать существующие `ManagedSourceCompanionV1` / `RestrictedCilImporterV1` / `ManagedSourceMapV1` по их реальному контракту. Никаких параллельных канонических binding lock, symbol resolver, call-graph importer или serialization frameworks.
6. Соблюдать `net11.0`; текущий companion имеет отдельные C# 13 и gated SDK-host C# 15 режимы — не расширяй аппаратное подмножество языков. Все диагностические отказные пути fail-closed, без безусловного `catch { return Success; }`.

## Объём реализации — только 3 небольших модуля

### A. `SourceSnapshot/` — explicit immutable source provenance

- Создай необходимые компактные sealed records + сервис/фабрику для снимка **заранее переданных** caller-owned исходников, виртуальных путей, reference images и exact source-root selection; делай defensive copies и проверки размеров/количества/уникальности путей, запрет неоднозначных ссылок, cancellation.
- Экспортируй точные provenance inputs: schema/version, compiler and Roslyn/admission policy, language host mode, ordered source/reference digests, source input digest, explicit owned body reference indices vs metadata-only indices. Используй уже сформированные identity из Companion, когда доступны; **не выдавай вычисленный по другой политике hash за `SourceInputDigest`**.
- Source snapshot — read-only DTO. Никакой записи в ISE, отсутствующего source guessing, скрытых filesystem reference discovery и допущений об эквивалентности по совпадению имени типа.

### B. `CompilerFacts/` — bounded projection из существующего компилятора

- Адаптируй `ManagedSourceBodyWorldResultV1` и/или `ManagedSourceCompanionResultV1`: диагностика, точный status admission/import, declared roots, закрытый call graph (только реально присутствующий), assembly/PE/PDB identities, существующие managed source maps. Не создавай новый CIL importer и не интерпретируй VLIW IR как аппаратную схему.
- Зафиксируй scope: primary PE/PDB map относится только к соответствующему primary module; metadata-only references не подтверждают исполнимое тело. Отсутствующий closure, неоднозначная сигнатура, failure analyzer, неверный digest/PE/PDB => **диагностический отказ**, без silent fallback.
- Экспортируй нормализованные read-only, deterministic facts для внешнего CircuitWeave, без жёсткой ссылки на его пакеты и без копирования его публичных DTO. При необходимости предложи минимальный versioned JSON/JSONL *sidecar* contract в `HardwareBridge/Docs/`, с стабильным порядком и hash входов.

### C. `GoldenOracle/` — independent observable reference

- Добавь **минимальный контракт исполнения доверенного Golden adapter**, который принимает фиксированный набор типизированных входов, явно заданные bit widths/signedness и возвращает exact bit-pattern outputs + status/diagnostics. Пример первого допускаемого случая: чистая скалярная `AddWord`-подобная функция, **только если доступен реальный доверенный источник**.
- Golden является эталонным наблюдением, а **не** синтезом, RTL simulator или доказательством эквивалентности. Не угадывать latency, reset, memory semantics, clock cycles; stateful/heap/native/exceptions/side-effects — blocked до отдельного контракта и oracle.
- Для тестовых векторов учитывай `unchecked` modulo, sign-extension, разные high-half inputs, zero/all-ones, граничные значения. Результат должен быть пригоден для сравнения CircuitWeave verification ladder, но сам bridge не сравнивает его с RTL и не выдаёт `Pass` за hardware proof.

## Минимальная структура файлов

Используй уже существующие каталоги, не создавай отдельную библиотеку/solution внутри компилятора без нужды:

```text
Compilers/HybridCPU_Compiler/HardwareBridge/
  SourceSnapshot/   // contracts, copy/bounds and identity projection
  CompilerFacts/    // exact facts and diagnostics projection
  GoldenOracle/     // trusted scalar oracle contracts/observations
  Tests/            // isolated positive/negative deterministic self-tests
  Docs/             // README, versioned sidecar specs, integration limits
```

У каждого нового C# файла — краткое file header / XML-summary с назначением и ограничениям расширения. Существующие README в этих **пяти** каталогах можно улучшить, но только в allowlist.

### Особенность тестов и `*.csproj`

Корневой `HybridCPU_Compiler.csproj` использует implicit recursive `Compile` для `HardwareBridge/**/*.cs`. Если добавляешь отдельный тестовый проект **внутри** `HardwareBridge/Tests/`, обеспечь, чтобы тестовые исходники и generated assembly metadata **не ломали родительскую сборку** (например, `#if HARDWAREBRIDGE_TESTS` только в test sources с символом в отдельном test project, и проверь `obj/bin` default excludes). Никакого изменения родительского csproj. Если из-за этого изоляция и запускаемые тесты не обеспечиваются честно — откажись от фиктивного тестового проекта, задокументируй ограничение и предложи последующий интеграционный PR, но реальную логику без тестов не называй finished.

## Проверки и отказные тесты

- `dotnet --info` + сборка существующего проекта на доступном pinned `.NET 11`; отдельный запуск тестов/самопроверок, если реализовано без изменений за пределами allowlist.
- Детерминизм и defensive copies: исходные массивы изменены после запроса; порядок roots/references; повторная сериализация даёт идентичные байты.
- Отказ: duplicate/traversal/empty source identity, changed reference body vs metadata-only, overload/generic mismatch, wrong PE/PDB pair, absent source closure, cancelled operation, oversized inputs, malformed or stale sidecar.
- Golden: граничные sign-extension/overflow/upper-32-bit тесты; unsupported side-effects/exceptions/untrusted delegate blocked, никакого доступа к ISE по произвольному имени из JSON.
- Проверка архитектурных границ: нет DLL reference в `CircuitWeave` или Studio; нет своего HWIR, Verilog, CIRCT, EDA runner, binding resolver; только read-only наблюдения.
- `git diff --name-only origin/master...HEAD`: **каждый изменённый путь** начинается с `Compilers/HybridCPU_Compiler/HardwareBridge/`. Сравнить public API и отсутствие package drift. Не заявлять `PASS` для неисполненных тестов.

## Приёмка / PR

Создай независимую ветку `feat/hybridcpu-compact-hardwarebridge` от последнего `master`, затем **один draft PR** в `yuriyyak23/HybridCPU-Compilers`:

- чёткий список файлов, краткое описание каждой подсистемы и её ограничений;
- реальные команды build/test и результаты или честно отмеченные blockers;
- маленький пример детерминированного source/facts/oracle sidecar, помеченный **unqualified / candidate**;
- зафиксированный формат экспорта и инструкции, как его в дальнейшем подключить в CircuitWeave через внешнюю verification/Bindings границу;
- ни одного изменённого файла вне allowlist.

**Критерий успеха:** компактный и тестируемый read-only bridge для входных фактов и Golden-наблюдений, без изменения существующего VLIW compiler, без изменения ISE и без выдачи hardware qualification. Если существующие API нельзя корректно использовать без запрещённых изменений — реализуй только честно работающую часть, укажи точный blocker и не имитируй полноту.
