# Локальный Host IPC

**[Русский](host-ipc.md) · [English](../en/host-ipc.md) · [Содержание](README.md)**

`CMakeWorkflow host serve` — локальный JSON-RPC Host с доступом через Unix domain sockets (Linux/macOS) или Windows named pipes. Он использует существующие сервисы CLI, не вводит второго scheduler, HTTP-сервера и отдельной History. Типизированный .NET SDK и клиенты VS Code могут обращаться к одному Host; NativeAOT поддерживается через source-generated JSON/PolyType metadata.

## Что предлагает Host

`initialize` согласует версию протокола, workspace и список доступных `capabilities`. Не следует жёстко предполагать, что все установленные бинарники имеют одинаковый набор методов. В текущих исходниках доступны семейства:

| Возможность | Методы / назначение |
|---|---|
| Tooling | `tooling/doctor`, `tooling/planFix`, `tooling/applyFix`, progress notifications |
| CMake | `context/inspect`, `target/resolve`, `workflow/plan` |
| Workflow | `workflow/start`, `workflow/recoveryInspect`, `workflow/reconcile`, `workflow/resume`, `workflow/resumeExecute` |
| Operations | `operations/find`, `operations/get`, `operations/cancel`, `operations/events`, `runs/get` |
| History | `history/runs`, `history/show`, `history/metrics`, `history/query`, `history/compare`, `history/detect` |
| Environment | `environment/plan`, `environment/apply` |

**Важно:** Doctor/Fix работают в рамках соединения, а durable workflow operations обладают собственной идентичностью и могут пережить разрыв соединения. Disconnect не означает отмену запущенного workflow. При переподключении нужно прочитать operation state или persisted report. Отмена запроса `$/cancelRequest` и явная отмена операции — разные контракты.

## Запустить Host

После `dotnet build CMakeWorkflow.slnx -c Release` на Linux/macOS:

```sh
host_dir=$(mktemp -d /tmp/cmwf.XXXXXX)
dotnet bin/Release/net10.0/CMakeWorkflow.dll host serve --endpoint "$host_dir/rpc.sock"
```

Из второго терминала (подставьте реальный путь):

```sh
dotnet bin/Release/net10.0/CMakeWorkflow.dll host doctor \
  --endpoint /tmp/cmwf.XXXXXX/rpc.sock --workspace "$PWD" --profile cmake --json
```

Socket directory должен существовать, иметь права 0700 и не быть symlink; длина socket path ограничена 100 UTF-8 байтами. Host не удаляет занятый или stale socket автоматически. После аварийного завершения проверяйте отсутствие владельца перед удалением.

На Windows PowerShell:

```powershell
dotnet bin/Release/net10.0/CMakeWorkflow.dll host serve --endpoint '\\.\pipe\cmakeworkflow-demo'
```

```powershell
dotnet bin/Release/net10.0/CMakeWorkflow.dll host doctor `
  --endpoint '\\.\pipe\cmakeworkflow-demo' `
  --workspace "$PWD" --profile cmake --json
```

Named pipes открываются с `PipeOptions.CurrentUserOnly`; отдельная блокировка предотвращает конкурирующий Host с тем же именем. TCP слушателя нет. IPC предназначен для доверенного локального пользователя, **не является sandbox** для недоверенных скриптов и процессов.

## Протокол и ограничения

StreamJsonRpc 2.26.150, `HeaderDelimitedMessageHandler`, `SystemTextJsonFormatter`, UTF-8 JSON-RPC 2.0 с `Content-Length`; TypeScript использует vscode-jsonrpc 10.0.0. Параметры positional, например:

```json
{"jsonrpc":"2.0","id":1,"method":"initialize","params":[{"protocolVersion":1,"workspaceDirectory":"/absolute/workspace"}]}
```

Handshake обязателен перед остальными запросами, workspace закреплён за соединением. Host не меняет глобальный cwd/environment. Максимум 16 клиентов; Doctor ограничивает число одновременных проверок и выдаёт нумерованные `tooling/progress` уведомления, завершение которых нужно проверять. Отмена/разрыв соединения должны дожидаться освобождения ресурсов probe. Возможность сериализовать DTO в обычном .NET недостаточна для подтверждения NativeAOT; необходимы реальные end-to-end RPC тесты.

## Doctor и явный Fix

```sh
CMakeWorkflow host fix --endpoint ENDPOINT --workspace "$PWD" \
  --profile native --dry-run --json
CMakeWorkflow host fix --endpoint ENDPOINT --workspace "$PWD" \
  --profile native --yes --json
```

До `--yes` выполняются только чтение и планирование. `planFix` создаёт connection-bound одноразовый `planId` с TTL 10 минут; новая заявка заменяет предыдущую. `applyFix` перепроверяет логический план непосредственно перед изменением системы и сериализует установки под per-user lock. Планы с чужим/просроченным/использованным ID отклоняются. Версии пакетов выбираются менеджером пакетов; TTL плана — **не pin бинарных артефактов**.

Можно задать `--pm auto|none|apt|pacman|dnf|brew|winget`, `--no-fallback`, `--install-dir`. Установка может оставить частичное состояние при cancellation; нет rollback и автоматического восстановления installer operation. Отсутствие прав `sudo -n` диагностируется, но Host не собирает пароль. После обрыва ответа следует сначала вызвать Doctor, а не автоматически повторять установку. Пользовательский PATH оболочки Host не меняет.

## VS Code и тесты

Отдельное расширение [cmake_workflow_vscode](https://github.com/cpp20120/cmake_workflow_vscode) запускается на workspace host (включая SSH/WSL/Dev Container). Оно может поднять собственный Host или подключиться к настроенному `cmakeWorkflow.hostEndpoint`, не присваивая себе владение внешним Host. Установка инструментов требует подтверждения. `cmakeWorkflow.toolingInstallDirectory` относится к окружению процессов данного workspace, а не к глобальному PATH или уже настроенным CMake trees.

Для проверки:

```sh
dotnet run --project CMakeWorkflow.Host.Tests
dotnet run --project CMakeWorkflow.Tooling.Tests
dotnet publish CMakeWorkflow.csproj -c Release -r linux-x64 \
  -o out/host-native -p:TreatWarningsAsErrors=true
```

У расширения отдельные тесты; чтобы включить реальную Host integration, задайте `CMAKEWORKFLOW_HOST_TEST_BINARY` абсолютным путём к исполняемому CLI. Без этого mock-тесты не подтверждают полный cross-language IPC. Точная спецификация DTO: [исходники Host](../../CMakeWorkflow.Host/) и [клиент SDK](../../CMakeWorkflow.Client/). Исходная запись о решении: [ADR-0002](adr/0002-local-host-ipc.md).
