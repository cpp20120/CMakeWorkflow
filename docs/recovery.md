# Recovery: ручное решение, аудит и durability

`run.json` остаётся единственным checkpoint исполнения. Нет автоматического
повтора операций после неизвестного результата и нет второй базы recovery.

## Проверить и продолжить прерванный run

После смерти runner запросите `workflow resume RUN_DIRECTORY`. Совместимый
незавершённый checkpoint будет переведён в `outcome_unknown` с exit code 4,
без запуска операций. Повторный resume такого отчёта возвращает 1.

Сначала проверьте внешние результаты, логи и дочерние процессы. Остановите или
дождитесь завершения оставшихся процессов. Lease защищает от другого runner,
но не обнаруживает все осиротевшие процессы и не блокирует сторонний Ninja.

```sh
CMakeWorkflow workflow report RUN_DIRECTORY
```

Обычный текстовый отчёт выводит `Checkpoint SHA256`. Скопируйте hash именно
проверенного отчёта в решение. JSON-отчёт сохраняет прежний формат; автоматизация
может вычислить SHA-256 **исходных байтов** `run.json`, а не пересериализованного JSON.

Принять проверенный результат конкретного узла:

```sh
CMakeWorkflow workflow reconcile RUN_DIRECTORY --node api-workloads \
  --decision succeeded --reason "Checked outputs and logs; the operation completed" \
  --expected-checkpoint CHECKPOINT_SHA256 --confirm-stopped
```

Или явно разрешить новый запуск этого узла, учитывая возможные повторные эффекты:

```sh
CMakeWorkflow workflow reconcile RUN_DIRECTORY --node api-workloads \
  --decision retry --reason "Stopped the old process and removed partial outputs" \
  --expected-checkpoint CHECKPOINT_SHA256 --confirm-stopped
```

`--confirm-stopped` — подтверждение оператора, не результат автоматической
проверки OS. `--reason` обязателен, до 4096 символов. Не помещайте секреты в reason:
это сохраняемые данные аудита. `--json` возвращает обновлённый отчёт.

Решение ничего не запускает. Если неизвестных узлов несколько, обработайте каждый
отдельно, каждый раз используя новый hash. Пока они остаются, run сохраняет
`outcome_unknown`. После последнего решения run получает `needs_attention` /
`recoveryPhase: reconciled`. Продолжение требует отдельной команды:

```sh
CMakeWorkflow workflow resume RUN_DIRECTORY
```

Если crash случился до допуска какого-либо узла и в отчёте нет узлов
`outcome_unknown`, доступно решение на уровне run: та же команда `reconcile`
с `--decision retry`, но **без `--node`**. Это не разрешение повторять успешные
узлы. Run-level решение запрещено, пока остаются неизвестные узлы.

## Что означает succeeded вручную

Это явное утверждение оператора. CLI не выдумывает exit code, длительность или
время завершения процесса. `experimentResult` равен `ManuallyAccepted`.
У `buildVerification.verification` значение `ManuallyAccepted`,
`backendExitCode` — null, а не фиктивный код 0.

Перед принятием результата CLI повторно проверяет manifest, граф и CMake-контекст
под существующими run/tree locks. Для build с зависимым run/profile требуется
наблюдаемый через File API исполняемый artifact: сохраняются его hash и generation,
а последующий запуск проверяет их обычным способом. Для configure требуется
совместимый сохранённый configure provenance. Ручное решение не создаёт отсутствующий
provenance, не угадывает executable и не превращает raw profile/harness outputs
в подтверждённые measurements. При недостатке доказательств используйте явный retry
после проверки внешнего состояния либо новый workflow.

Проверки resume сохраняются: изменение успешных artifacts, дерева или manifest
может запретить продолжение даже после решения. Это не универсальный механизм
восстановления произвольно изменённого проекта.

## Аудит и повторные запросы

`run.json.reconciliations` содержит ID решения, timestamp, локальные actor/host,
nodeId (либо null), decision, reason, hash предыдущего checkpoint, его run status /
recovery phase и снимок предыдущего NodeResult. Решение и новое состояние узла
сохраняются **одной атомарной заменой** `run.json` под run lease.

Несовпавший `--expected-checkpoint` отклоняется без записи. Если клиент потерял
ответ после commit, перечитайте отчёт: повтор старого решения будет отклонён.
Прерванная запись `.tmp` не считается принятым решением и не восстанавливается
автоматически. Последующий commit заменит оставшийся временный файл.

Resume сохраняет аудит. После разрешённого retry stdout/stderr получают имена
`NODE.attempt-2.stdout.log`, `NODE.attempt-2.stderr.log` и далее. Логи предыдущей
попытки не перезаписываются. Аудит содержит снимок metadata, а не архив всех
внешних файлов: outputs сторонних инструментов могут изменяться.

Этот аудит локальный, без криптографической подписи: пользователь с правом записи
в каталог может изменить его вручную. Actor/host не заменяют аутентификацию.

## Сохранность checkpoint

До запуска операций выполняются:

1. Полная сериализация в `run.json.tmp` в том же каталоге.
2. `FileStream.Flush(true)` для временного файла.
3. Замена `run.json` без промежуточного удаления и без copy/delete fallback.
4. На Linux/macOS — `fsync` каталога и его родителей, включая запись нового
   run-каталога. На macOS дополнительно используется `F_FULLFSYNC`.

На Windows используется `MoveFileExW(REPLACE_EXISTING | WRITE_THROUGH)` после
flush файла. Отдельного POSIX directory-fsync здесь нет; одинаковую с Linux
гарантию namespace durability для любых Windows filesystem не обещаем.

Ошибка записи/flush/replace/sync не игнорируется и не разрешает новый dispatch.
Если замена уже произошла, а завершающая синхронизация не удалась, новый JSON
может быть видим, но его durability не подтверждена. CLI возвращает ошибку либо
`outcome_unknown` (если удалось сохранить диагностический checkpoint).

Это механизм persistence barriers, а не безусловная гарантия против любой потери
питания. Результат зависит от filesystem, mount options, контроллера и соблюдения
устройством flush-команд. NFS/SMB/FUSE и виртуальные диски требуют отдельной
валидации. Сохранность логов, CMake outputs и транзакций внешних систем не следует
из сохранности `run.json`. Exactly-once для произвольных побочных эффектов нет.

Основания: [FileStream.Flush(Boolean)](https://learn.microsoft.com/en-us/dotnet/api/system.io.filestream.flush),
[Linux fsync и directory entry](https://man7.org/linux/man-pages/man2/fsync.2.html),
[Apple fsync / F_FULLFSYNC](https://developer.apple.com/library/archive/documentation/System/Conceptual/ManPages_iPhoneOS/man2/fsync.2.html),
[Windows MoveFileExW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-movefileexw).

## Regression suite

Обычная сборка не содержит environment-controlled fault hooks. Для тестов:

```sh
dotnet build tests/CMakeWorkflow.IntegrationTests.csproj -c Release
dotnet publish CMakeWorkflow.csproj -c Release -p:PublishAot=false \
  -p:RecoveryFaultInjection=true --artifacts-path out/recovery-artifacts -o out/recovery-cli
dotnet tests/bin/Release/net10.0/CMakeWorkflow.IntegrationTests.dll \
  out/recovery-cli/CMakeWorkflow --recovery
```

На Windows executable — `out/recovery-cli/CMakeWorkflow.exe`. Такая сборка
предназначена только для тестов, её нельзя публиковать как обычный CLI.

Suite убивает runner до admission, после flush временного файла, после rename
(Unix), после checkpoint, во время дочернего процесса и после завершения операции,
но до commit результата. Проверяет реальные количества исполнений, решения
`retry`/`succeeded`, отсутствие запуска из reconcile, stale/duplicate decisions,
lease, сохранение аудита и принятие build artifact. Также обрывает запись самого
решения и инъецирует ошибки persistence. Дочерние процессы убирает сам тест.

SIGKILL/TerminateProcess и fault injection **не симулируют отключение питания**.
Для аппаратного power-loss утверждения нужны отдельные reboot/VM/storage fault
тесты на целевых filesystem. CI запускает recovery suite на Linux, Windows и
macOS; наличие job не означает, что все платформы проверены локально.
