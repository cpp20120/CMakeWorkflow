# Удалённый Host

Реализованы этапы A и B: `host bridge --stdio --endpoint PATH` соединяет stdin/stdout
с существующим Host через `LocalTransport`, а `CMakeWorkflowClient.ConnectSshAsync`
подключает typed SDK через установленный OpenSSH. Этапы C–F в этот коммит не входят.

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

Работа остановлена после B. Точные команды проверок и ограничения приведены в
[английском руководстве](../en/remote-host.md). Следующий этап — C, durable remote workflow.
