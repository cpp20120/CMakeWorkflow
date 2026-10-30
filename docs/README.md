# Документация CMakeWorkflow

CMakeWorkflow — самостоятельный CLI на .NET 10 для выполнения явно заданных
сценариев над CMake-проектами. Он поддерживает NativeAOT-публикацию, работу с
существующими build trees, DAG, контроль происхождения артефактов, профилирование
и локальную историю результатов.

Эти документы описывают **исходники текущего checkout**, включая локальные
доработки. Это не обещание наличия всех команд в произвольном установленном
бинарнике. В частности, `profile replay`, `harnessDirectory` и расширенный анализ
профилей присутствуют в рабочем дереве на момент написания, но могут отсутствовать
в более раннем опубликованном CLI. Дата сверки: 2026-10-29.

## Навигация

| Документ | Содержание |
|---|---|
| [Архитектура](architecture.md) | Компоненты, границы ответственности, выполнение и расширение |
| [Workflow-манифесты](workflows.md) | Контексты, узлы, зависимости, ресурсы и жизненный цикл |
| [CLI](cli.md) | Сборка, команды, параметры и примеры вызова |
| [Результаты и History](results-and-history.md) | План, JSONL, `run.json`, артефакты, сравнения и статистика |
| [Профилирование и кампании](profiling-and-campaigns.md) | Варианты сборок, capture/analyze/replay, DagFlow harness и fuzz |
| [Provisioning и интеграции](integrations.md) | Инструменты, CMake bridge, boilerplate, VS Code и проверки |
| [Local Host IPC](host-ipc.md) | Doctor/Fix: CLI, VS Code, NativeAOT, планы установки и локальные подключения |
| [ADR-0002: Local Host](adr/0002-local-host-ipc.md) | Границы первого этапа и выбор транспорта |
| [ADR-0001: MSBuild backend](adr/0001-msbuild-execution-backend.md) | Предложение; не описание существующего runtime |

## С чего начать

Из корня репозитория, с установленными .NET 10 SDK, CMake 3.26+, Ninja и C++
компилятором:

```sh
dotnet build CMakeWorkflow.slnx -c Release
dotnet bin/Release/net10.0/CMakeWorkflow.dll workflow plan examples/hello/workflow.json --json
dotnet bin/Release/net10.0/CMakeWorkflow.dll workflow run examples/hello/workflow.json --json --results-dir out/results
```

Для чтения результата используйте **фактический** `runDirectory` из событий:

```sh
dotnet bin/Release/net10.0/CMakeWorkflow.dll workflow report RUN_DIRECTORY --json
```

`RUN_DIRECTORY`, `RUN_ID`, `BUILD_DIR` и аналогичные имена в примерах — значения,
которые нужно заменить. Пути с пробелами заключайте в кавычки в своей оболочке.
В интеграциях передавайте executable и массив аргументов отдельно.

## Место в наборе инструментов

- CMake и выбранный generator определяют native dependencies, compiler flags,
  инкрементальную сборку и интерпретацию presets.
- CTest запускает зарегистрированные проектом тесты. CDash — отдельный внешний
  сервис для CTest dashboard; CMakeWorkflow не предоставляет CDash server.
- Boilerplate помогает создать и настроить проект, но не является обязательной
  зависимостью CLI.
- CMakeWorkflow исполняет workflow и сохраняет сведения о результате.
- VS Code extension предоставляет интерфейс к CLI и сохранённым данным.
- Tooling provisioning подготавливает инструменты только по явному запросу.

Полезно рассматривать этот набор как инструменты разработки с несколькими
контрактами. Наличие всех компонентов не означает единого установщика,
согласованного релиза или автоматически подтверждённой совместимости их версий.
