# Host observation foundation v1

Готовая read-only точка подключения **внутри подготовленного ISE host-процесса**. Host передаёт существующий `IseObservationService`; bridge не создаёт глобальный Processor, CPU loop, IPC server или background polling. Это библиотечный фундамент, не attach к произвольному PID. Уже запущенный внешний host без опубликованного endpoint этим API недоступен.

Проверенный source: `HybridCPU_ISE/Machine/IseObservationService.MachineState.cs` принимает `IIseMachineStateSource` и syncLock, публикует `SourceProvenance`. Значения: LiveCore, Snapshot, LegacyGlobal, Null. Публичного debug/IPC transport в просмотренных ISE/Tools/bridge исходниках не найдено. Managed Execute остаётся синхронным без live callback. Документальные планы не учитывались как реализованные возможности.

## Подключение

В composition root самого host (пример требует его уже существующий observer):

```csharp
using CpuInterfaceBridge.Diagnostics;

// Не конструирует CPU или global source. observationService принадлежит host.
using var endpoint = IseHostObservationAdapter.Create("ISE host", observationService);
var descriptor = endpoint.Descriptor;
using var connection = endpoint.Connect(descriptor.Identity);
using var subscription = connection.Subscribe(capacity: 128);

// Явное чтение из существующей host/UI точки наблюдения; не вызывает Step.
HostCaptureResult result = connection.CaptureOnce(coreId, cancellationToken);
if (result.Status == HostCaptureStatus.Observed)
{
    var pc = result.Frame!.Snapshot.LiveInstructionPointer;
    var registers = result.Frame.Snapshot.ActiveVirtualThreadRegisters;
    var certificate = result.Frame.Snapshot.RetireVisibilityCertificate;
}
else
{
    // result.Reason сохраняет provider reason. Старый успешный snapshot не подставляется.
}

// В отдельном consumer, если нужны события явных CaptureOnce этой connection:
// await foreach (var ev in subscription.ReadAllAsync(cancellationToken)) { ... }
// connection.Dispose(): только отсоединение observer, без Stop/Pause CPU.
```

`HostObservationEndpoint` также принимает узкий `Func<int, CoreStateSnapshot>` и явный provenance для host-specific read-only адаптера. Это заявление внедряющей стороны, не независимая проверка live source. Для настоящего ISE используйте `IseHostObservationAdapter`, копирующий реальный SourceProvenance. Null/Unknown запрещены. Snapshot остаётся Snapshot, LegacyGlobal остаётся LegacyGlobal — ни один из них не объявляется loader-backed guest evidence.

## Identity и lifecycle

- Descriptor schema `hybridcpu.bridge.host-observation/v1`; transport `ExplicitInProcessReference`. Remote attach, execution control, memory reads — false. Нет breakpoint/Step/Pause/Stop или memory writes.
- Identity включает новый InstanceId, текущий PID, фактическое время старта текущего процесса и имя. Connect требует точного совпадения полной identity и schema. PID или имя сами по себе недостаточны. Это не authentication token.
- ConnectionId отдельный для каждой connection/reconnect. Event.SessionId равен ConnectionId; frame дополнительно содержит host identity, source kind и время завершения capture. Sequence локален для connection, не равен CPU retire sequence.
- Connect/Subscribe ничего не читают. Только CaptureOnce вызывает provider. Подписчики одной connection получают её успешные captures; другая connection их не получает и не забирает. Для общего broadcast используйте одну connection с независимыми подписками.
- Dispose connection: Detached, нормальное завершение event stream после drain, CPU и другие observers продолжают работу. Close endpoint: HostClosed для всех connections, точный CompletionReason, drain событий. После этого новые Subscribe/Capture/Connect отвергаются. Completion никогда не означает guest completion.
- Лимиты: максимум 8 connections по умолчанию (настраивается 1..128); до 128 подписчиков connection; 1..65536 событий в каждом буфере. Общий DiagnosticEventHub теперь ограничивает subscriber count (настраивается 1..4096), сохраняя исходный конструктор с Guid. Переполнение DropNewest, отдельный DroppedEventCount. Неиспользуемые connections/subscriptions нужно Dispose.

## Достоверность и ограничения

Capture сохраняет live PC, registers, VT PCs, cycle count и pipeline certificates из существующего snapshot. Live PC не подменяется retire certificate PC. Массивы копируются в read-only collections до публикации. CoreId/VT диапазоны, совпадение active PC с supplied VT table и null snapshot/certificates проверяются. Таблицы ограничены 4096 элементами каждая как лимит диагностического payload, а не новое ограничение ISA. Пустые списки не дорисовываются нулями; отсутствие guest image/run/qualification явно Unavailable.

Снимок — наблюдение provider, не атомарный guest stop и не полная трасса retire. Endpoint сериализует свои captures и lifecycle, но **не синхронизирует CPU execution**. Host отвечает за безопасную точку вызова и корректное чтение своего machine-state source. IseObservationService использует переданный syncLock; endpoint не делает предположения о том, что CPU writer держит тот же lock. Нельзя использовать sampling для доказательства, что конкретная инструкция была/не была исполнена или blocker устранён.

Cancellation до provider не делает чтения; после возвращения provider не публикует отменённый capture. Синхронное чтение нельзя принудительно прервать: зависший provider блокирует capture и lifecycle endpoint до возвращения. Timeout/удалённый cancellation transport не заявлены. Ошибка provider возвращает ProviderFault с исходным ex.Message и без frame; противоречивый snapshot — InvalidSnapshot. Такие captures не публикуются как успешные события. Историю ошибок при необходимости сохраняет вызывающая сторона по результатам CaptureOnce.

Memory API намеренно отсутствует: legacy compat observer может возвращать zero-padded memory, а общий ICoreStateService не несёт read-validity evidence. GC roots, loader statuses, runtime-owned image/run identity и managed ECALL callback пока не публикуются этим host observation контрактом. Их нельзя извлечь из CoreStateSnapshot предположением.

## Validation

Из корня репозитория:

```powershell
powershell -NoProfile -File Compilers/CpuInterfaceBridge/Validation/Validate.ps1 -IncludeIntegration -IncludeHostAdapterSmoke -ArtifactName host-debug-20260905
```

ArtifactName — только имя под bridge/.artifacts (без slash/path traversal). Obj/bin каждого проекта, dependencies, CLI home, TEMP/TMP и логи направлены туда. Проверки последовательные, publish и guest run отсутствуют.

Проверено 2026-09-05:

- Full bridge + ISE/compiler build: exit 0 (integration-build.log).
- Harness реальных bridge sources с dependency doubles: **69 assertions, exit 0** (harness.log), включая 27 новых проверок host lifecycle/identity/capture/buffering.
- RealHostAdapter smoke компилирует/использует настоящий `IseObservationService` с `NullMachineStateSource`, проверяя fail-closed отказ без CPU reads: **exit 0**. Его отдельный лог real-host-adapter.log.
- `git diff --check -- Compilers/CpuInterfaceBridge`: exit 0; Validate.ps1 parser: 0 ошибок.

Изменения этого пула: Diagnostics/HostObservation.cs, IseHostObservationAdapter.cs, DiagnosticEvents.cs, HOST-DEBUG.md, README.md; Validation/HostObservationTests.cs, Program.cs, validation csproj, Validate.ps1 и RealHostAdapter/{Program.cs,RealHostAdapter.csproj}. Существующие tools, compiler/ISE, активные процессы и TempEnv этим пулом не изменялись.

Ближайшее следующее подключение — в конкретном host composition root передать существующий observer адаптеру и вызывать CaptureOnce из его текущей безопасной точки наблюдения. Для **внешнего** attach нужен отдельно согласованный host-owned transport с authenticated endpoint discovery, проверкой instance identity, version/capability handshake, ограничениями framing/payload и disconnect semantics. Отдельный PID без такой поддержки недостаточен; здесь не создан фиктивный Attach(processId).
