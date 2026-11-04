# Удалённый Host

Реализованы и проверены на Linux-хосте пользователя этапы A–C:
`host bridge --stdio --endpoint PATH` соединяет stdin/stdout
с существующим Host через `LocalTransport`, а `CMakeWorkflowClient.ConnectSshAsync`
подключает typed SDK через установленный OpenSSH. Durable-запуск использует
существующие workflow/operation services. Этапы D–F — загрузка результатов,
удалённые CLI-команды и ephemeral Host — пока не реализованы.

Host запускается независимо командой `host serve --endpoint PATH`. Мост
передаёт байты без изменения JSON-RPC framing и использует ограниченные буферы.
Stdout содержит только протокол, диагностика идёт в stderr. Завершение моста
закрывает его соединение и не запрашивает остановку постоянного Host.
Код завершения моста не подтверждает принятие или выполнение RPC-запроса.

Сохраняются требования к приватному каталогу Unix-сокета и к текущему
пользователю Windows named pipe. Новый планировщик или хранилище не создаётся;
durable-операциями по-прежнему владеет существующий координатор Host.

Проверки передачи байтов, EOF с обеих сторон, отмены и некорректных аргументов
вынесены в `--bridge-unit-tests`. Группа `--bridge-tests` дополнительно запускает
реальный Host и дочерний процесс моста, выполняет последовательные RPC и
проверяет сохранение Host после отключения.

## Подключение SDK

Самостоятельно опубликуй и скопируй Host на удалённую Linux-машину и запусти
`host serve` от нужного пользователя. SDK не устанавливает программы и не
запускает постоянный Host. SSH alias, аутентификация и known_hosts настраиваются
обычными средствами OpenSSH.

```csharp
var options = new SshHostOptions(
    "build-linux", "/run/user/1000/cmakeworkflow/host.sock", "/srv/projects/example")
{
    RemoteExecutable = "/home/builduser/bin/CMakeWorkflow",
    ConnectionTimeout = TimeSpan.FromSeconds(15)
};
await using var client = await CMakeWorkflowClient.ConnectSshAsync(options, cancellationToken);
var status = await client.Operations.GetAsync(existingOperationId, cancellationToken);
```

Endpoint и workspace — абсолютные Linux-пути на удалённой машине без компонентов
`.` и `..`; они не преобразуются в локальные пути. RemoteExecutable — путь или
имя команды на удалённой машине, SshExecutable — локальный OpenSSH. Файлы не
синхронизируются. Host проверяет workspace и существующие права доступа.

SSH запускается через `ArgumentList`, с `-T` и `BatchMode=yes`. Аргументы
удалённой команды экранируются для POSIX shell. Проверка ключа Host сохраняется;
пароли не запрашиваются. stdout используется только для JSON-RPC. stderr
постоянно считывается, а последние 16 Ки символов доступны в `SshDiagnostics` и
в `SshConnectionException` вместе с кодом завершения SSH. Протокол и typed
services SDK остаются прежними, автоматических повторов запросов нет.

Таймаут ограничивает локальное ожидание handshake, даже если peer не подтверждает
RPC-отмену. Истечение времени даёт `TimeoutException`, отмена вызывающим кодом —
`OperationCanceledException`. Затем допускается общий срок очистки **две секунды**
для завершения/ожидания SSH и дочитывания stderr. Превышение срока очистки явно
сообщается как ошибка I/O. Процесс завершается до освобождения RPC и pipe streams.

Освобождение SSH-клиента закрывает транспорт и не посылает `operations/cancel`.
Принятыми durable-операциями продолжает владеть постоянный Host. Это отличается
от существующего `LaunchAsync`, где клиент владеет частным Host. Ephemeral SSH
Host пока не реализован. Windows-клиент и Windows remote shell здесь не проверены.

## Запуск существующего удалённого workflow

Workspace, manifest, исходники, toolchain и необходимый build context уже должны
находиться на удалённой машине. Host читает и сохраняет snapshot manifest,
планирует workflow, удерживает build-tree leases, запускает CMake и записывает
результаты там же. Относительные пути внутри manifest сохраняют удалённый базовый
каталог. SDK не читает эти файлы локально и не синхронизирует исходники.

Перед запуском получи SHA-256 точных байтов удалённого manifest: например,
выполни на удалённой машине `sha256sum /srv/projects/example/workflow.json`
или используй hash из существующего процесса размещения manifest. Несовпадение
hash отклоняется до принятия запроса. `Manifest` и `ResultsDirectory` — абсолютные
пути Host внутри инициализированного workspace. Host отклоняет относительные
пути, выход за workspace и symlink escape. Не вызывай локальный `Path.GetFullPath`
и не собирай удалённые пути с разделителем файловой системы клиента.

```csharp
// Сохрани ID и точные параметры запроса в состоянии своего приложения ДО StartAsync.
// После отключения или перезапуска клиента загрузи тот же ID.
var requestId = Guid.NewGuid().ToString("N");
var request = new StartWorkflowOptions(
    requestId,
    "/srv/projects/example/workflow.json",
    expectedRemoteManifestSha256,
    Jobs: 2,
    ResultsDirectory: "/srv/projects/example/results");

await using (var submitting = await CMakeWorkflowClient.ConnectSshAsync(options, cancellationToken))
{
    var operation = await submitting.Workflows.StartAsync(request, cancellationToken);
    Console.WriteLine(operation.OperationId); // Сохрани, если ответ получен.
} // Закрывается соединение; принятой операцией продолжает владеть постоянный Host.
```

`StartAsync` отправляет один запрос durable admission. Явный повтор идентичного
запроса с тем же ID возвращает ту же операцию, в том числе после завершения.
Изменённые параметры с тем же ID отклоняются. Принятый snapshot manifest
неизменяем: дальнейшее редактирование исходного файла его не заменяет.

## Переподключение, события и отмена

Подключись к **тому же постоянному Host и workspace**. Сохранённый request ID
позволяет найти операцию даже после потери ответа о её принятии:

```csharp
await using var inspecting = await CMakeWorkflowClient.ConnectSshAsync(options, cancellationToken);
var accepted = await inspecting.Operations.FindAsync(requestId, cancellationToken);
var operation = inspecting.Operations.GetHandle(accepted.OperationId);
var state = await operation.GetSnapshotAsync(cancellationToken);
var recovery = await operation.InspectRecoveryAsync(cancellationToken);
Console.WriteLine($"{state.OperationId}: {state.State}; recovery: {recovery.Decision}");

// Ноль для первой страницы; затем — сохранённый cursor обработанной страницы.
var page = await operation.ReadEventsAsync(savedSequence, limit: 100, ct: cancellationToken);
foreach (var evt in page.Events) Console.WriteLine(evt);
savedSequence = page.NextSequence; // Сохраняй после обработки событий.
// При HasMore читай следующую страницу; активную операцию можно опрашивать позднее.
```

Handle привязан к соединению. После reconnect создай новый через
`Operations.GetHandle`: это не отправляет новый workflow и не запускает resume.
Для обычной активной операции `WaitForCompletionAsync(ct: cancellationToken)`
ждёт `succeeded`, `failed` или `canceled`. Ограничивай ожидание токеном; для
прерванных и неопределённых операций используй status/recovery inspection.

Обрыв SSH или отмена start RPC не доказывает отказ admission. Автоматического
reconnect/redispatch нет. Сначала выполни `Operations.FindAsync`; отсутствие
результата не разрешает молча повторить запуск с новым ID. Сохрани первоначальный
запрос и разреши неопределённость явно. Потерянный транспортный ответ отличается
от durable-состояния `outcome_unknown` после прерывания Host. Последнее может
требовать операторской проверки и явного `ReconcileAsync` с актуальной revision
и подтверждением остановки внешнего выполнения. События не заменяют recovery
checkpoint; reconnect не выполняет reconciliation или resume автоматически.

Отмена принятой операции запрашивается отдельно:

```csharp
var current = await operation.InspectRecoveryAsync(cancellationToken);
var receipt = await operation.RequestCancellationAsync(current.Revision, cancellationToken);
Console.WriteLine(receipt.State);
// Receipt фиксирует намерение. Проверь конечное состояние: операция могла успеть
// завершиться. При конфликте revision нужна новая inspection, а не перезапись.
```

Отключение инициатора или отменяющего клиента не отзывает durable acceptance
или cancellation intent. Для продолжения выполнения постоянный Host должен
оставаться запущенным; при его прерывании действует существующий recovery.
Каталоги результатов в metadata остаются **удалёнными** путями. Этап C даёт
status и replay событий; загрузка отчётов и файлов относится к этапу D.

## Проверки

Release-сборка и группы `--ssh-tests`, `--ssh-lifecycle-tests` проходят в sandbox.
Проверяются quoting, alias, typed RPC, ограничение stderr, ошибки запуска/протокола,
оборванные сообщения, пути, отмена и завершение дочерних процессов. Для лимита
300 мс проверяется возврат вместе с очисткой быстрее трёх секунд.

На 2026-11-04 sandbox блокировал `Socket.Bind` с `Permission denied`. Пользователь
выполнил на Linux-хосте `--bridge-tests` и `--ssh-integration-tests` и прислал
успешный вывод: реальные bridge и постоянный Host работают, переподключение
успешно, закрытие SSH не останавливает Host. Интеграционный fixture заменяет
только сетевой переход OpenSSH локальным POSIX shell; настоящий sshd и учётные
данные не требуются. Соединение с настоящим удалённым sshd пока не проверено.

Причина зависания lifecycle была установлена временными метками времени:
handshake ждал ответа на `$/cancelRequest`. Добавлено локальное ограничение
ожидания, затем завершение процесса и ограниченная очистка. Метки удалены.
Полная CI-матрица и NativeAOT publish/smoke в этом этапе не запускались; formatter
с явными source-generated metadata переиспользован, новых wire DTO нет.

Этап C проверен 2026-11-04: Release-сборка Host.Tests с `--no-restore` прошла
с 0 предупреждений и 0 ошибок. Sandbox снова остановил запуск на `Socket.Bind`
до первого сценария. Пользователь выполнил на Linux-хосте:

```sh
timeout --kill-after=5s 90s dotnet \
  CMakeWorkflow.Host.Tests/bin/Release/net10.0/CMakeWorkflow.Host.Tests.dll \
  --ssh-workflow-tests
```

Все четыре сценария прошли:

- `execution`: настоящий CMake/Ninja workflow, два клиента, disconnect/reconnect,
  идемпотентность и конфликт request ID, неизменность snapshot, постраничный replay
  и однократный внешний эффект.
- `cancellation`: отключение инициатора и отменяющего клиента, конфликт revision,
  durable-отмена, остановка процесса и отсутствие повторного dispatch.
- `paths`: абсолютные удалённые пути, границы workspace, symlink escape,
  отсутствующие workspace/manifest и неверный hash; отклонённые запросы не выполняются.
- `lost-reply`: тестовый процесс теряет успешный ответ admission до получения SDK;
  отдельное SSH-соединение находит ту же операцию по request ID, читает события
  и видит завершение без повторного start.

Тесты используют постоянный Host и настоящие bridge-процессы. Только lost-reply
заменяет bridge тестовым обработчиком frames для точного воспроизведения потери
ответа. Требуются CMake 3.26+ и Ninja. Для изоляции сбоя добавь имя одного сценария
после `--ssh-workflow-tests`. Сетевой переход OpenSSH заменён тем же POSIX fixture,
что и в B. Настоящий sshd, Windows и NativeAOT publish в C не проверялись.
Production transport, scheduler и поведение RPC не менялись: существующий
durable-путь SDK уже предоставляет необходимые операции.

Работа остановлена после C. Остальные команды проверок приведены в
[английском руководстве](../en/remote-host.md). Следующий этап — D, получение результатов.
