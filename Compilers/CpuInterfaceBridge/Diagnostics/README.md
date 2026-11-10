# Bridge diagnostics v1

Host-side debug foundation: см. [HOST-DEBUG.md](HOST-DEBUG.md) — явный in-process endpoint, реальные observation adapters, identity/lifecycle и bounded subscriptions. Это не remote attach к произвольному запущенному процессу.

Пассивный библиотечный API для compiler tools. Никакие методы Diagnostics не запускают compiler, loader, CPU, Step, Pause или Stop. Публичные сигнатуры существующего facade сохранены. Применимых AGENTS.md в предках репозитория и Compilers не найдено; исходный diff bridge был пуст. Существующий dirty diff вне bridge сохранён.

Файлы изменения (относительно Compilers/CpuInterfaceBridge):

- Изменены: CpuInterfaceBridge.cs, ICpuInterfaceBridgeServices.cs, EmulationContracts.cs, LegacyCompilerService.cs, LegacyEmulatorService.cs, HybridCpu_InterfaceBridge.csproj.
- Добавлены: .gitignore; Diagnostics/Session.cs, RunnerReportImporter.cs, DiagnosticQueries.cs, DiagnosticEvents.cs, CompilerToolExample.cs, README.md.
- Добавлены: Validation/Bridge.Diagnostics.Validation.csproj, Isolation.props, Stubs.cs, Program.cs, Validate.ps1, Fixtures/runner-v1.json.
- Сгенерированные build/test/log outputs исключены через .gitignore и находятся в .artifacts/diagnostics-20260905.

Пример подключения (готовый аналог компилируется как `CompilerToolExample.ReadFailure`):

```csharp
// Потоки открывает вызывающий tool по явно выбранным путям.
var session = RunnerReportImporter.Import(reportStream, Guid.NewGuid(),
    "explicit report selected by compiler tool", packageBytes: optionalHcexeStream);
var card = DiagnosticQueries.Failure(session);
var method = DiagnosticQueries.Locate(pc, session.CodeRecords);
var handleMatch = DiagnosticQueries.CompareHandle(session, typeId, actualReceiver);
var comparison = DiagnosticQueries.Compare(previous, session);
```

Импорт принимает строго `hybridcpu.ise-cpu-backed-image-run/v1`, JSON без дублирующихся свойств, не более 4 MiB по умолчанию. Потоки остаются открытыми; чтение начинается с их текущей позиции. Передавайте полный JSON и полные байты пакета HCEXE. Не сканирует каталоги и не открывает `image` из JSON. Ошибки схемы, числовых типов/диапазонов, duplicate TypeId/handle и противоречивый caller PC отвергаются исключениями (`InvalidDataException`, `JsonException`, `InvalidOperationException`, `FormatException`); неподдерживаемые status сохраняются с `Unsupported`. Неизвестные дополнительные поля игнорируются. Не все instruction-summary поля импортируются.

`DiagnosticSessionV1.Schema` задаёт версию DTO. SessionId — identity импорта, не выдуманный runtime run ID. `ReportSha256` идентифицирует байты JSON, не запуск. Source задаёт вызывающая сторона, не аутентификация producer. Report image path сравнивается с optional expectedImagePath буквально. В текущем Program.cs SHA уже есть: `image.PackageSha256`, SHA-256 всего пакета из `HybridCpuRestrictedImageBuilderV1.Inspect`. Без байтов это `ReportedOnly`; с совпавшими байтами — `VerifiedAgainstSuppliedBytes`; несовпадение — `Mismatch`, не успешная привязка. Байты без SHA в отчёте — `BytesOnlyUnbound`. Проверка SHA не доказывает подлинность JSON, исполнение или qualification.

Loader status, execution outcome и qualification независимы. Runner пишет JSON после успешного Load, но отдельного поля loader status не имеет: API оставляет его `Unavailable`, а не подменяет проверку отчётным предположением. Qualification всегда `Unavailable`: API не имеет qualification policy и не производит PublishQualified. ExecutionOutcome — сообщённый runner status, не повторный guest run.

Code records собираются из nearby/filtered/caller/diagnostic выборок реального runner; это неполная таблица. Интервалы `[codeStart,codeEnd)`, отрицательные/переполненные/пустые диапазоны запрещены, совпадающие записи объединяются, неоднозначное перекрытие возвращает `Contradictory`. `FromOffset` делает checked addition. Assembly не вырезается предположительно из MethodIdentity. FinalProgramCounter и LastRetiredBundlePc различаются; карточка использует первый, второй доступен отдельно.

Runtime types берутся из `loaded.TypeSystem.Descriptors` и `TypeHandle(TypeId)` через поля runner; сохранён полный descriptor JSON с fields. TypeId не считается handle, receiver не считается object reference или handle автоматически. Для проверки caller явно передаёт ожидаемый TypeId. Отсутствие таблиц отличается от опубликованного пустого массива через `*TableState`/`GcDigestsState`. Нулевой Safepoints — измеренное по отчёту значение; roots отсутствуют. GC строки сохраняются непреобразованными, численность roots из Reason не угадывается.

`managedObservations` содержит Operation, Receiver, Argument1/2, числовые external Status/Error и Value. В текущем `HybridCpuManagedEcallObservationV1` нет PC и provider reason, поэтому per-event ProviderReason — `Unavailable`. Точная строка `result.Reason` сохраняется в карточке без обрезки/переформулировки: она может включать last-managed-reason и fault context, но не приписывается каждому событию. Эти observations не гарантируют полную историю.

CIL доступен лишь для точной предоставленной точки method/native offset с неотрицательным CIL offset, указанным source и совпадающим SHA байт-проверенного пакета. Нет интерполяции и встроенного CIL mapper. Ответственность producer — предоставить достоверный mapping; bridge проверяет его идентичность и согласованность, а не правильность компиляции. Несогласованные/неоднозначные точки — Contradictory, отсутствие — Unavailable.

Compare отдельно показывает различия import sessions, runtime run IDs (если предоставлены), image SHA, paths и report bytes. Отсутствие прежней ошибки не доказывает достижение точки и устранение blocker, даже при Completed.

## Live boundary

`IDiagnosticEventSource<T>` выдаёт независимые `DiagnosticSubscription<T>`. `DiagnosticEventHub<T>` — вход для явного in-process producer; Subscribe сам по себе ничего не запускает. Capacity положительный, политика **DropNewest**: переполненному подписчику не доставляется новое событие, его DroppedEventCount увеличивается. Sequence монотонен в пределах session и учитывает потерянные события. Replay отсутствует. Один reader на подписку; для второго consumer нужна новая подписка. Complete завершает после drain, поддерживает точную ошибку; cancellation/Dispose отсоединяют подписчика. Отписываться необходимо, чтобы hub не сохранял неиспользуемые буферы.

```csharp
var producer = new DiagnosticEventHub<ManagedEcall>(sessionId);
IDiagnosticEventSource<ManagedEcall> source = producer;
using var subscription = source.Subscribe(capacity: 128);
// Владелец runtime в будущем явно вызывает producer.Publish(observation).
await foreach (var item in subscription.ReadAllAsync(cancellationToken))
    Consume(item); // контролируйте subscription.DroppedEventCount
```

Текущее `HybridCpuIseManagedGuestExecutionRunnerV1.Execute` синхронно возвращает результат и не предоставляет публичный live callback. `ManagedObservations` — копия накопленного списка; нет stream/attach контракта. Глобальные Processor объекты bridge не позволяют подключиться к отдельному процессу. Минимальное будущее live подключение вне scope — callback от владельца исполнения с run/image identity и неизменяемыми наблюдениями в существующем runtime, затем явная передача в hub. Здесь нет IPC-сервера или нового CPU loop. Существующий IseCoreStateService читает PC/registers/pipeline certificates через observation service; это отдельное локальное наблюдение, не loader-backed guest evidence.

## Legacy commands

Default LegacyCompilerService возвращает failure без compile action. Старый action-конструктор остаётся доступным, выполняет явно переданное действие, но не объявляет успех по общему CompilerResultStore. Для fresh results используйте `LegacyCompilerService.WithOwnedResults`: delegate должен возвращать результаты этого вызова. CompileLogStream начинает новую операцию сразу при вызове; это отражено в facade/interface XML docs.

LegacyEmulatorService остаётся глобальным legacy runner, не HCEXE runner. BudgetExhausted добавлен в конец enum (старые значения сохранены). Pause/Stop/Canceled не становятся Completed; ошибки сохраняют ex.Message в FailureReason и событии. Breakpoint читает PC каждый цикл независимо от progress sampling. Нет достоверного guest completion detector: завершение бюджета не вызывает legacy OnEmulationComplete. Старый legacy event channel не превращён в broadcast; новые подписчики diagnostics используют hub.

## Validation

Из корня репозитория:

```powershell
powershell -NoProfile -File Compilers/CpuInterfaceBridge/Validation/Validate.ps1
# Опционально один последовательный integration build перед harness:
powershell -NoProfile -File Compilers/CpuInterfaceBridge/Validation/Validate.ps1 -IncludeIntegration
git diff --check -- Compilers/CpuInterfaceBridge
```

Скрипт задаёт DirectoryBuildPropsPath=Validation/Isolation.props и BridgeValidationRoot=bridge/.artifacts/diagnostics-20260905, перенаправляет obj/bin каждого проекта, NuGet packages, CLI home, TEMP/TMP и логи. Harness компилирует реальные Diagnostics и legacy source с dependency doubles, без xUnit packages, без ISE/компилятора и guest execution. `IncludeIntegration` сообщает свой exit отдельно; после подключения runner скрипт сохраняет nonzero integration exit даже при успешном harness. Не используйте обычный dotnet run без изоляционных properties.

Проведённый цикл 2026-09-05, SDK 10.0.204:

- `dotnet build ...HybridCpu_InterfaceBridge.csproj --disable-build-servers -m:1 -p:DefineTestSupport=false -p:DirectoryBuildPropsPath=<absolute Isolation.props> -p:BridgeValidationRoot=<absolute artifacts>`: **exit 1**, 294 ошибки. Перенос BaseIntermediateOutputPath привёл к включению старых ISE obj generated .cs: CS0579/CS0101/CS0111 (assembly attributes и VMX projection definitions). Это ограничение данной полной сборки, не доказательство дефекта новых Diagnostics; зависимости и старые outputs не исправлялись. Лог `.artifacts/diagnostics-20260905/integration-build.log`.
- `dotnet run --project ...Validation/Bridge.Diagnostics.Validation.csproj --disable-build-servers` с теми же isolation properties: **exit 0**, 33 assertions, один warning CS9113 в test double. Лог `.artifacts/diagnostics-20260905/harness.log`.
- `git diff --check -- Compilers/CpuInterfaceBridge`: exit 0, только уведомления о CRLF.

На момент исходного цикла компоновка bridge с настоящим ISE/compiler оставалась непроверенной; результат следующего шага ниже. Реальные runtime callbacks (их нет), соответствие конкретного SharpDoom package отчёту и guest qualification по-прежнему не проверены. Fixtures синтетические, это не evidence SharpDoom. TempEnv не читался/не изменялся, активные прогоны не управлялись, publish и guest run не запускались.

## Подключение существующего tool, 2026-09-05

По отдельному разрешению пользователя изменены `Tools/HybridCpuIseImageRunner/Program.cs` и `.csproj`, добавлены `ReportCommand.cs` и README. Режим `--report <runner.json> [--image <package.hcexe>]` вызывает `CompilerToolExample.ReadFailure` до входа в execution path. Предыдущие dirty-изменения runner сохранены. Остальные существующие tools, compiler и ISE source не редактировались.

В bridge обновлены Validation/Program.cs (CLI tests), validation csproj (компиляция настоящего ReportCommand), Isolation.props (исключение старых obj/bin из default source glob), Validate.ps1 и этот README. `Validate.ps1 -IncludeRunner` использует отдельный `.artifacts/tool-integration-20260905`, последовательно собирает tool и выполняет harness. Скрипт возвращает integration failure, даже если harness прошёл.

Финальные проверки:

- `Validate.ps1 -IncludeRunner`: **exit 0**; полная сборка runner + bridge + настоящие зависимости **exit 0**, harness **42 assertions, exit 0**. Первичная полная сборка имела 45 warnings в зависимостях, 0 errors. Ранее мешавшие old obj дубли устранены только настройкой изоляционного glob.
- Первый расширенный harness нашёл необработанный InvalidDataException (exit -532462766). CLI catch исправлен, повторная полная сборка и 42 проверки прошли. Исходный лог сохранён как `harness-initial.log`.
- Собранный `HybridCpuIseImageRunner.dll --report <fixture>`: **exit 0**; `--report` без пути: **exit 2**; повреждённый report: **exit 3**; `--report <fixture-with-sha> --image <mismatching-fixture>`: **exit 4**, карточка сохраняет Mismatch.
- `git diff --check -- Compilers/CpuInterfaceBridge Tools/HybridCpuIseImageRunner`: **exit 0**. PowerShell parser: 0 ошибок в Validate.ps1.

Логи и CLI JSON: `.artifacts/tool-integration-20260905/{tool-build.log,integration-build.log,harness.log,passive-report.json,damaged-report.stderr.log,mismatch-report.json}`. Запускался только passive report mode; эти проверки не являются guest execution или qualification образа.
