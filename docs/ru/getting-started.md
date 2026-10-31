# Начало работы за пять минут

**[Русский](getting-started.md) · [English](../en/getting-started.md) · [Содержание](README.md)**

CMakeWorkflow запускает сценарии вокруг CMake-проектов, но не заменяет CMake или Ninja. Ниже — самостоятельный пример из репозитория, без интеграции в собственные CMakeLists.

## Требования

.NET **10 SDK**, CMake **3.26+**, Ninja и компилятор C++. Команды выполняются из корня репозитория.

## Собрать и построить план

```sh
dotnet build CMakeWorkflow.slnx -c Release
dotnet bin/Release/net10.0/CMakeWorkflow.dll workflow plan examples/hello/workflow.json --json
```

`workflow plan` валидирует граф и читает существующие метаданные build tree. Он **не запускает** configure или build неявно.

## Выполнить и открыть результат

```sh
dotnet bin/Release/net10.0/CMakeWorkflow.dll workflow run examples/hello/workflow.json --results-dir out/results --json
```

Пример задаёт `configure → build → run` и `build → ctest`. JSONL-поток сообщает настоящий `runDirectory`. Не вычисляйте имя каталога самостоятельно:

```sh
dotnet bin/Release/net10.0/CMakeWorkflow.dll workflow report RUN_DIRECTORY --json
```

**`run.json` — авторитетное состояние.** `events.jsonl` и логи дополняют отчёт. Код 0 у `workflow report` означает, что отчёт удалось прочитать, а не то, что его workflow завершился успешно.

## Использовать уже настроенный build tree

```sh
dotnet bin/Release/net10.0/CMakeWorkflow.dll context inspect BUILD_DIR --json
dotnet bin/Release/net10.0/CMakeWorkflow.dll target resolve --build-dir BUILD_DIR --target EXECUTABLE --json
```

В манифесте используйте `mode: "adopt"`, `provenancePolicy: "allowUnverified"`, не добавляйте `configure`-узел. Это признание неполноты provenance, **не** разрешение игнорировать известные конфликты source/generator/configuration. `target run` всё равно запрашивает native build перед исполнением.

Дальше: [манифесты](../workflows.md), [CLI](../cli.md), [результаты и History](../results-and-history.md), [recovery](../recovery.md) и [интеграции](../integrations.md).
