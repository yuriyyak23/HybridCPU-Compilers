# Executive Summary

Существующий WinForms GUI взаимодействует с движком через *legacy*-связки (рефлексия, события `Hybrid_CPU_Events`, `ISE_StateAccessor`, `CompilerResultStore` и т.д.), что затрудняет поддержку и интеграцию нового компилятора. **CpuInterfaceBridge.dll** задаст чёткий API между интерфейсом и бэкендом: асинхронные методы компиляции/эмуляции, потоки логов/событий состояния, и единый контракт диагностики (ILogger/ActivitySource/каналы). Это позволит сохранить сценарии работы (компиляция→эмуляция→мониторинг) на каждом этапе миграции.

Важнейшие моменты: текущие *data model* – это `HybridCpuCompiledProgram`, `HybridCpuMultithreadedCompiledProgram`, `CoreStateSnapshot` и пр. – хранятся в `CompilerResultStore` и `ISE_StateAccessor` (возвращает копии состояний)【48†L29-L37】【47†L6-L13】. WinForms вызывает их напрямую (реализован через `Form_Main.ExternalModulesBridge`【41†L85-L93】), а события (`Hybrid_CPU_Events`) транслят состояние в UI【20†L4-L12】【3†L33-L42】. Для современных шаблонов .NET мы предложим **асинхронный Task-подход** с `CancellationToken`, `IProgress<T>` и `IAsyncEnumerable<T>` (на базе `System.Threading.Channels`)【36†L46-L54】. Диагностика будет через `ILogger` и `System.Diagnostics.Activity`【40†L1-L9】【46†L1-L4】, чтобы обеспечить структурированный лог и распределённую трассировку. 

В итоге архитектура станет следующей:

```mermaid
graph LR
    Host[<b>WinForms UI (Shell + Modules)</b>] -->|CompileAsync/EmulateAsync| CpuInterfaceBridge[<b>CpuInterfaceBridge.dll</b><br/>API-интерфейсы и DTO]
    Host -->|State updates| CpuInterfaceBridge
    CpuInterfaceBridge -->|вызывает| Compiler[<b>HybridCPU Compiler</b><br/>(IR API)]
    CpuInterfaceBridge -->|вызывает| Emulator[<b>HybridCPU ISE</b><br/>(Эмулятор, снапшоты)]
    CpuInterfaceBridge -->|лог/прогресс| ILogger[<b>ILogger/ActivitySource/Channels</b>]
    note right of ILogger: Структурированное логирование и трассировка
```

## Текущее состояние → Целевое состояние (сводная таблица)

| Компонент / Функциональность | Текущее состояние | Целевое состояние |
|-----------------------------|-------------------|-------------------|
| **Вызов компилятора** | Синхронный вызов `CompileSimpleAsmApp()` в UI (код в `Form_Main.ExternalModulesBridge`)【42†L280-L289】. | Асинхронный метод `Task<CompileResult> CompileAsync(...)` в CpuInterfaceBridge с поддержкой `CancellationToken` и потокового логирования. |
| **Хранение результатов компиляции** | Глобальный `CompilerResultStore` (ConcurrentDictionary по VT)【47†L6-L13】. | Сервис/объект (в CpuInterfaceBridge) с методами `GetCompiledProgram(vtId)`; или возвращаемый объект `CompileResult` включает `CompiledProgram`. |
| **Запуск эмуляции** | UI запускает новый `Thread`, вызывает `Processor.CPU_Cores[0].ExecutePipelineCycle()` в цикле【44†L537-L545】, контролирует состояние через `Hybrid_CPU_Events` и `LegacyHostCoreStateBridge`【20†L4-L12】【3†L33-L42】. | Асинхронный метод `Task EmulateAsync(..., IProgress<CoreStateSnapshot> progress)` в CpuInterfaceBridge, который в фоне выполняет цикл эмуляции с отменой; по окончании возвращает результат. События состояния передаются через поток (канал или IObservable). |
| **Обновление UI** | Ручные вызовы `InvokeHostedModuleMethod` из Host (рефлексия по именам) для обновления форм【41†L43-L51】. Нет привязки к интерфейсу. | GUI подписывается на интерфейсные события CpuInterfaceBridge (`event StateChanged(CoreStateSnapshot)` или `IAsyncEnumerable<CoreStateSnapshot> StreamState()`). Обработчики вызывают `BeginInvoke` для UI-обновлений. |
| **Состояние ядра (CoreState)** | Статический класс `ISE_StateAccessor` отдаёт снапшоты состояниях (копии)【48†L29-L37】. UI иногда обходится напрямую через внутренности `Processor`. | CpuInterfaceBridge оборачивает `ISE_StateAccessor` и предоставляет методы `GetCoreState(coreId)`, `GetMemory(address, len)` и т.д., гарантируя потоко-безопасность через копирование. UI не обращается к `Processor.CPU_Cores` напрямую. |
| **Прерывания (Breakpoints)** | Логика остановки исполнения в `Form_Main.Init`/`Breakpoints.cs` со `CancellationTokenSource`; проверяется в цикле эмуляции. | Метод эмуляции CpuInterfaceBridge проверяет и реагирует на breakpoints (возможно, через колбек `IProgress` или событие `BreakpointHit`). UI переводится на соответствующую метку. |
| **Диагностика/Логирование** | Разрозненный вывод `Debug.WriteLine` и `MessageBox` в случае ошибок. Нет структурированного лога. | Все значимые действия (начало/конец компиляции, шаг эмуляции, ошибки) логируются через `ILogger` с `ActivityId`. Создаются `Activity` для Compile и Emulate с общим `traceId` для корреляции. Лог и прогресс передаются в UI (канал или `IProgress`). |
| **Телеметрия компилятора** | Текущий код не использует живую телеметрию компилятора (набор статистики недоступен)【4†L42-L47】. | В CpuInterfaceBridge (или через адаптер) при наличии живого нового компилятора подпитать статистику (отчет об инструкциях, времени) и предоставлять через `CompileResult.Stats`. UI будет показывать “unavailable”, если данных нет. |
| **Threading-модель UI** | UI блокируется длительным кодом (File I/O, циклы), используются `Thread` и `System.Windows.Forms.Timer` без ясной синхронизации. | Все долгие операции выполняются в `Task.Run` или на фоновых потоках. Для UI используется `SynchronizationContext.Post`/`BeginInvoke`. `System.Windows.Forms.Timer` только для UI (не вызывать тяжелые операции). |

## Проект **CpuInterfaceBridge.dll** – API и дизайн

**Цель CpuInterfaceBridge.dll:** стать мостом между WinForms и ядром системы, задав строгий контракт. Предлагаемый API состоит из двух подсистем: **компиляция** и **эмуляция**.

**Ключевые интерфейсы и типы данных:** 

- `ICompilerService`  
  - `Task<CompileResult> CompileAsync(SourceCode source, CompileOptions options, CancellationToken ct)` – запускает компиляцию асинхронно. Возвращает `CompileResult`, который содержит `CompiledProgram[] Programs` (один на каждую VT) и `CompilationStatistics`.  
  - `IAsyncEnumerable<CompilerMessage> CompileLogStream(SourceCode source, CompileOptions options, CancellationToken ct)` – возвращает поток структурированных сообщений (debug/info/warning/error) по ходу компиляции.  
- `IEmulatorService`  
  - `Task EmulateAsync(EmulationRequest request, CancellationToken ct, IProgress<CoreStateSnapshot> progress)` – выполняет эмуляцию программы. `CoreStateSnapshot` описывает состояние заданного ядра в каждый такт.  
  - `Task StepAsync(EmulationRequest request, CancellationToken ct)` – выполняет один такт эмуляции (как Play/NextStep).  
  - `Task PauseAsync()` и `Task StopAsync()` – приостанавливают/останавливают эмуляцию.  

- **DTO (data transfer objects):**  
  - `CompileOptions` – параметры компиляции (VT count, оптимизации, разделения кода, домены потоков).  
  - `CompiledProgram` – канонический результат компиляции (например, сериализованное изображение, макет бандлов, статистика).  
  - `CompilationStatistics` – числовая статистика (время, число инструкций, бандлов и т.д.).  
  - `CompilerMessage` – запись лога (уровень, текст, опционально: код ошибки, расположение, VT).  
  - `EmulationRequest` – содержит базовый адрес, список статических данных (если нужно) и параметры эмуляции (например, ограничение тактов).  
  - `CoreStateSnapshot` – снимок состояния ядра (IP, регистры, флаги, активный VT, состояние пайплайна, счётчики и т.д.). Можно заимствовать `HybridCPU_ISE.CoreStateSnapshot` или создать упрощённый аналог.  
  - `SessionContext` – (опционно) информация о текущем сеансе (ID, начало, метаданные).  

**Асинхронные сценарии и потоки:**  
- **Компиляция:** `CompileAsync` возвращает единоразовый результат, но лог/прогресс передаётся через `IAsyncEnumerable<CompilerMessage>` или `Channel<CompilerMessage>`. Это позволяет UI в режиме реального времени отображать события компилятора (например, “Сборка 3/5, VT=1” и т.д.). Каналы хорошо подходят для producer/consumer модели【36†L46-L54】.  
- **Эмуляция:** Передача *снимков состояния* через `IProgress<CoreStateSnapshot>` (события вызываются внутри цикла эмуляции). Можно также использовать `IAsyncEnumerable<CoreStateSnapshot>` для последовательного чтения состояний. При завершении эмуляции генерируется событие `EmulationCompleted`.  

**Диагностика и логирование:**  
- Используем `ILogger` (из Microsoft.Extensions.Logging) во всём CpuInterfaceBridge (и, по возможности, внедряем его в GUI). Каждая операция (Compile, Emulate) имеет свой `Activity` (с помощью `ActivitySource`)【46†L1-L4】, чтобы шаги компиляции/эмуляции группировались под одним `traceId`. Это даст контекстные логи, соответствующие операциям. Например: `using var activity = activitySource.StartActivity("Compile"); logger.LogInformation("Команда компиляции: {Options}", options);`.  
- UI может подписываться на `ILogger` вывод (например, через `ILoggerProvider` в памяти) или получать поток логов через `CompileLogStream`.  
- При ошибках (исключениях) в CpuInterfaceBridge они оборачиваются в понятный `ErrorResult`/исключение с деталями, и логируются как `LogError`. UI при этом может отобразить диалог с кнопкой “Copy details”.  
- **Важно:** `ActivitySource` следует создавать один раз на библиотеку【40†L7-L14】.  

**Модель потоков:**  
- **UI Thread**: отвечает только за рендеринг; любая долгая операция (компиляция, эмуляция) работает на фоновых потоках (`Task.Run` или специальном потоке). UI подпишется на события и прогресс, вызывая `InvokeRequired/BeginInvoke` по необходимости.  
- **Background Thread/Tasks**: CpuInterfaceBridge выполняет свою работу в фоновых задачах. Progress-обновления и лог пишутся потокобезопасно. Каналы (Channels) обеспечивают очереди сообщений без блокировки. Например, `Channel<CompilerMessage> channel = Channel.CreateUnbounded<CompilerMessage>();` и запись `await channel.Writer.WriteAsync(msg)` при компиляции【37†L1-L4】.  
- **Synchronization**: доступ к общему состоянию (например, к кэшу компилятора или к ISE state) внутри CpuInterfaceBridge блокируется или управляется потокобезопасными структурами. Однако UI не должен напрямую читать `Processor.CPU_Cores` – только через `CpuInterfaceBridge.GetCoreStateSnapshot(coreId)`, который внутри может использовать `ISE_StateAccessor`【48†L29-L37】 (замок и копии данных).  

## Миграционный план

Миграция проводится постепенно, с сохранением работоспособности на каждом шаге:

| Этап               | Изменяемые компоненты               | Риски совместимости         | Критерии приёмки                       | План rollback                    |
|--------------------|------------------------------------|-----------------------------|----------------------------------------|----------------------------------|
| **Этап 0: Стартовая стабилизация**  | Добавить в Host-код *обёртки* для логирования и времени компиляции/эмуляции (через `ILogger`). Организовать единые ID операций. Не менять логику. | Возможен лишний лог трафик, но UI не ломается. | Логи операций появляются, correlation-id передаётся. `Application.ThreadException` ловит исключения; упало приложение отловлено и залогировано. | Отключить логирование (оставив UI нетронутым).       |
| **Этап 1: CpuInterfaceBridge – Шаг 1. Интерфейс библиотеки**  | В проекте CpuInterfaceBridge.dll описать интерфейсы `ICompilerService`, `IEmulatorService`, DTO (`CompileResult`, `CoreStateSnapshot` и т.д.), минимальные реализации-адаптеры, делегирующие текущим методам. Например, `CompileAsync` вызовет `CompileSimpleAsmApp` синхронно внутри `Task.Run`. | Поскольку интерфейсы новые, текущий UI не использует их сразу – нет прямого конфликта. Главное – грамотно реализовать адаптеры к существующему коду (временно допуская дублирование логики). | Компиляция/эмуляция через CpuInterfaceBridge *без видимых изменений* поведения. Unit-тесты на адаптеры проходят. Новые API возвращают тот же результат, что и старая логика. | До полного тестирования оставить прямые вызовы старой логики. |
| **Этап 2: CpuInterfaceBridge – Шаг 2. Асинхронность и отмена**  | Перевести реализации `CompileAsync` и `EmulateAsync` на Task/async с `CancellationToken`. Заменить запуск `Thread` в эмуляции на `Task.Run`. Убрать `Thread.Sleep` внутри (использовать `Task.Delay`). | Неправильное управление токеном отмены может привести к гонке: если UI ожидает результата синхронно. Нужно внимательно протестировать сохранение порядка событий. | Вызов `CompileAsync` не блокирует UI; при нажатии Cancel задача отменяется (Task завершается с `OperationCanceledException`). Эмуляция реагирует на Pause/Stop мгновенно (цикл останавливается). | На время отладки можно вернуть синхронные версии. |
| **Этап 3: Отвязка Presenter от WinForms**  | Вынести **логический код** (populating UI lists, форматирование данных) из форм в отдельные презентеры/сервис-объекты. Например, `OpcodePresenter`, `StatisticsPresenter` получают `CoreStateSnapshot` и наполняют ListView. UI формы подписываются на события этих презентеров. | Надо следить, чтобы названия контролов/методов (в рефлексии) совпадали; пока ещё возможно вызывать старые методы для «шагов интеграции». | Поведение списков, подсветок, сортировки не изменилось. Тесты визуального соответствия (если есть) проходят. Новые Presenter-классы тестируются отдельно. | Оставить методы в коде форм для обратной совместимости, но пометить устаревшими. |
| **Этап 4: Конфигурация, профили, сессии** | Ввести объект `WorkspaceConfig` или аналог, в котором хранятся пути к файлам, параметры компиляции/эмуляции и состояние UI (открытые вкладки, курсоры и т.д.). Загружать/сохранять в JSON. | Формат JSON может отвалиться от старых настроек; убедиться, что дефолтная загрузка без файла продолжается без ошибок. | После перезапуска восстанавливаются: открытые файлы, состояние форм, последний результат компиляции, выбранная страница. Профили компиляции/эмуляции можно сохранять и загружать вручную. | Если что-то пойдет не так, можно запустить приложение без config-файла (восстановление дефолтного поведения). |
| **Этап 5: Улучшение UX и оптимизация** | Активировать виртуализацию для больших таблиц (`DataGridView.VirtualMode`), настроить AutoScaleMode для HDPI, оптимизировать частоты обновлений (ранее все видимые табы обновлялись каждый тик; теперь только активные). | Есть риск, что пользователь посчитает «ничего не происходит», если панель не перерисовывается. Поэтому всегда визуально индицировать состояние (например, показывать индекс текущего такта). | UI остаётся плавным (нет задержек >50ms при обновлении), количество обновлений соответствует настройкам профиля. Найдены и устранены узкие места (CPU, mem). | Отключить виртуализацию или вернуться к предыдущему обновлению всех табов (с потерей производительности). |

**Таблица API CpuInterfaceBridge.dll (пример):**

| Интерфейс / Метод               | Параметры                                                       | Описание                                                                         |
|---------------------------------|-----------------------------------------------------------------|-----------------------------------------------------------------------------------|
| `ICompilerService.CompileAsync` | `(SourceCode source, CompileOptions options, CancellationToken ct)` | Асинхронно компилирует исходный код. Возвращает `Task<CompileResult>`.            |
| `ICompilerService.GetCompileLog` (или `CompileLogStream`) | `(SourceCode source, CompileOptions options, CancellationToken ct)`  | Возвращает `IAsyncEnumerable<CompilerMessage>` – поток сообщений компилятора.       |
| `IEmulatorService.EmulateAsync` | `(EmulationRequest req, CancellationToken ct, IProgress<CoreStateSnapshot> progress)` | Асинхронно выполняет эмуляцию, выдаёт снимки состояния через `progress`.           |
| `IEmulatorService.StepAsync`    | `(int coreId, CancellationToken ct)`                             | Выполняет один шаг эмуляции для ядра `coreId`.                                     |
| `IEmulatorService.PauseAsync`   | `(int coreId)`                                                  | Приостанавливает эмуляцию указанного ядра (`CancellationToken`).                   |
| `IEmulatorService.StopAsync`    | `(int coreId)`                                                  | Останавливает эмуляцию (без уведомления об успешном завершении).                   |
| `CoreStateSnapshot` (DTO)       | `(int CoreId, ulong LiveIP, ulong CycleCount, bool IsStalled, ulong[] Regs, ...)` | Снимок состояния ядра (IP, счётчики, регистры, состояние пайплайна и т.д.).       |
| `CompileResult` (DTO)           | `(CompiledProgram[] Programs, CompilationStatistics Stats, bool Success, string[] Errors)` | Результат компиляции: артефакты по VT, статистика, ошибки.                        |
| `CompilerMessage` (DTO)         | `(LogLevel Level, string Text, int Line, int Column, int VT)`     | Запись лога компилятора с уровнем и дополнительной информацией.                     |

*Clever snippet:* `Task<CompileResult> CompileAsync(...)` создаёт `Activity` (например, `using (source.StartActivity("Compile")) { ... }`), пишет логи через `ILogger`.    

## Тесты и выпуск

**Стратегия тестирования:**  
- **Unit tests:** логика интерфейсов CpuInterfaceBridge (на уровне mock-компилятора/эмулятора). Например, имитировать быстрый компилятор и убедиться, что `CompileAsync` возвращает правильный `CompileResult`, а `CompileLogStream` генерирует ожидаемый поток сообщений. Тестировать отмену задач.   
- **Integration tests:** запуск реального компилятора (`HybridCPU_Compiler`) через `CpuInterfaceBridge` и сверка `CompileResult` с ожидаемым (например, простая программа Ассемблера). Тест на `ISE_StateAccessor`: запросы `GetCoreStateSnapshot` дают консистентные данные при параллельной эмуляции.  
- **UI smoke tests:** открыть приложение, проследить рабочий сценарий: Open→Compile→View pipeline→Emulate→Step/Play→Stop. Проверить, что интерфейс не блокируется, ошибки не вылетают. Автоматизация через UI-тесты WinForms (FlaUI или WinAppDriver) желательна.  
- **Метрики выпуска:**  
  - Отсутствие UI-blocking (среднее время отклика UI < X ms при эмуляции).  
  - Процент успешных сборок (Compile) и эмуляций.  
  - *Crash-free rate*: нет неотловленных исключений (Application.ThreadException не срабатывает).  
  - Корреляция логов (наличие Activity ID) – ручной аудит логов.  

**Риски и mitigations:** основные пункты приведены в таблице миграции. Важно обеспечить **обратную совместимость**: на каждом этапе старый путь (`InvokeHostedModuleMethod` и статические события) по возможности остается рабочим («горячей отладка» через feature-flags или фазы). Например, в начале `CompileAsync` может делегировать старой функции, а в конце – вызывать только новый код. Цель – не прерывать сценарии, пока пользователь не готов переключиться на новую интеграцию. 

## Чек-лист готовности GUI к выпуску

- **CpuInterfaceBridge интеграция:**  
  - [ ] Компиляция происходит асинхронно; после нажатия Compile UI остаётся отзывчивым, можно отменить задачу.  
  - [ ] Эмуляция (Play/Step) выполняется в фоне; при нажатии Pause/Stop управление возвращается UI без зависания.  
  - [ ] Результаты компиляции (выданный `CompiledProgram`) корректно отображаются в UI (список инструкций, адресов, цвет VT) – как до миграции.  
  - [ ] Состояние ядра (PC, регистры) обновляется в UI через новые события, без cross-thread исключений.  
- **Диагностика/лог:**  
  - [ ] `ILogger` выводится в консоль/файл или показывает в UI (ошибки и предупреждения из компиляции/эмуляции читаемо).  
  - [ ] Используются `ActivitySource` – легко сопоставить начало и конец Compile/Emulate (наличие traceId в логах). 【46†L1-L4】  
  - [ ] В случае ошибки компиляции или сбоя в эмуляции пользователь видит диалог с текстом ошибки и возможность скопировать трассировку.  
- **Производительность/UX:**  
  - [ ] UI обновляется плавно (нет ощутимых «фризов» при эмуляции).  
  - [ ] Обновление UI огранизовано (смена табов/панелей не приводит к массовым вызовам, только для активных вкладок).  
  - [ ] High DPI режим протестирован (метрики/лейаут).  
- **Фичи:**  
  - [ ] Настройки и профиль компиляции/эмуляции (если введены) работают – их изменения влияют на поведение без перезагрузки.  
  - [ ] Проверено, что тест `Phase09CompilerTelemetryTruthTests` проходит (либо с неизмененной константой «no telemetry»).  

**Источники:** фрагменты кода из репозитория HybridCPU (см. `LegacyUiContracts.cs`【20†L4-L12】, `LegacyHostCoreStateBridge.cs`【3†L33-L42】, `CompilerResultStore.cs`【47†L6-L13】, `ISE_StateAccessor.cs`【48†L29-L37】, `Form_Main.ExternalModulesBridge.cs`【41†L85-L93】【43†L320-L333】) показывают существующие контракты и приёмы. Рекомендации по асинхронности, каналам и логированию опираются на официальную документацию Microsoft (Channel【36†L46-L54】, ILogger/Activity【40†L1-L9】【46†L1-L4】).