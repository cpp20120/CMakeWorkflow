# Provisioning, CMake и IDE

## Provisioning

Tooling — явная подготовка окружения, независимая от scheduler:

```sh
CMakeWorkflow tooling list --json
CMakeWorkflow tooling doctor --profile native --json
CMakeWorkflow tooling plan --profile native --profile profiling --json
CMakeWorkflow tooling setup --profile native --dry-run --json
CMakeWorkflow tooling setup --profile native --yes
```

Profiles: `native`, `cmake`, `ninja`, `profiling`, `fuzz`, `development`, `all`.
Doctor проверяет наличие и версии команд, plan описывает действия. Setup
повторно проверяет инструменты и применяет изменения только при `--yes`, если
установка необходима. `--dry-run` возвращает план без установки.

Поддерживаются package managers apt, pacman, dnf, Homebrew и winget.
`--pm none` отключает package installation, `--no-fallback` — standalone
fallback, `--install-dir` задаёт пользовательский bin directory.
На Linux package setup использует non-interactive sudo; CLI не предоставляет
GUI для ввода пароля. Отмена не откатывает уже применённые изменения системы.

Fallback CMake/CTest на Linux x64/arm64 использует Kitware release с опубликованным
SHA-256 digest и проверкой архива. Store задаётся `CMAKEWORKFLOW_TOOL_STORE`.
Для неподдерживаемых installer/platform combinations выводятся manual
diagnostics. Windows ADK и Xcode автоматически не устанавливаются.
Смотрите [ToolingService](../CMakeWorkflow.Tooling/ToolingService.cs).

Workflow никогда не запускает provisioning автоматически. Готовность toolchain
не равна доступности profiler permissions и не гарантирует, что проект корректно
настроен.

## CMake target bridge

Необязательный [CMakeWorkflowTargetBridge.cmake](../cmake/CMakeWorkflowTargetBridge.cmake)
позволяет CMake custom target вызвать подготовленный executable, не входя
рекурсивно в собственный build tree. `WORKFLOW_RUNNER_EXECUTABLE` указывает
на CLI; native prerequisite должен быть выражен через CMake `DEPENDS`.

Путь `--prepared` основан на этом контракте. Его нельзя использовать как
универсальное разрешение доверять любому существующему бинарнику. Для обычного
CLI/IDE-запуска target run применяется стандартный build/verification path.
Основным источником targets остаётся CMake File API; bridge не обязателен.

## Boilerplate и CDash

[cmake_boilerplate](https://github.com/cpp20120/cmake_boilerplate) предоставляет
генерацию проекта, CMake capabilities, presets, testing, coverage, sanitizers
и опциональный CTest/CDash pipeline. Его Bash/PowerShell bootstrap обслуживает
свой project lifecycle; tooling CLI обслуживает каталог инструментов.

Boilerplate может работать без CMakeWorkflow, а CMakeWorkflow — с проектом,
созданным без boilerplate. Нужно согласовывать пути и presets, а не переносить
в runner собственную реализацию compiler flags и CMake dependencies.

CTest/CDash reporting и History решают разные задачи. CDash получает CTest XML
через штатный opt-in submission. History локально индексирует workflow,
benchmark и profiling artifacts. SQLite History не является сервером CDash,
а CMakeWorkflow не заменяет CTest discovery или coverage instrumentation.

## VS Code extension

Отдельный репозиторий:
[cmake_workflow_vscode](https://github.com/cpp20120/cmake_workflow_vscode).
Текущая реализация — workspace extension: CLI выполняется на том же workspace
host, в том числе Remote SSH, WSL и Dev Container.

Используются `cmakeWorkflow.executable` и `cmakeWorkflow.prefixArgs`. Например,
для development executable — `dotnet`, а prefixArgs содержит **один** аргумент
с абсолютным путём к DLL. Не объединяйте executable и аргументы в shell string.

В локальном исходнике extension реализованы:

- Выбор manifest/workspace, plan/run, отдельные или несколько узлов с prerequisites.
- TreeView, diagnostics, артефакты, граф workflow и просмотр сохранённых runs.
- History, baseline/candidate, сравнения, detection, метрики и descriptive charts.
- Run/Profile Variants, ручной Replay, Analyze, просмотр profiles.
- Target build/invoke/run, двухцелевой sequence, tooling plan/setup.
- Параметры DagFlow mutation/weak-memory/fuzz-команд.

Это описание доступного кода интеграции, а не гарантия публикации конкретной
версии extension. Подробности установки, команд и settings находятся в её README.

Плагин проверяет workspace trust, запускает CLI с массивом аргументов, разделяет
stdout/stderr и после выполнения читает сохранённые результаты. Scheduler,
provenance и statistical verdict остаются в CLI. `.binlog` или MSBuild backend
сейчас не являются частью этого интерфейса.

Для живого workflow UI использует JSONL; после обрыва потока не следует
показывать успех без отчёта. Отмена на разных OS имеет разные механизмы:
сохранение финального отчёта при forced termination не гарантировано.

## Проверки и ограничения покрытия

Репозиторий содержит executable test projects. После сборки solution:

```sh
dotnet tests/bin/Release/net10.0/CMakeWorkflow.IntegrationTests.dll bin/Release/net10.0/CMakeWorkflow
dotnet CMakeWorkflow.History.Tests/bin/Release/net10.0/CMakeWorkflow.History.Tests.dll
dotnet CMakeWorkflow.Profiling.Tests/bin/Release/net10.0/CMakeWorkflow.Profiling.Tests.dll
dotnet CMakeWorkflow.Campaigns.Tests/bin/Release/net10.0/CMakeWorkflow.Campaigns.Tests.dll
dotnet CMakeWorkflow.Tooling.Tests/bin/Release/net10.0/CMakeWorkflow.Tooling.Tests.dll
```

На Windows в первом вызове передаётся apphost `CMakeWorkflow.exe`.
[CI configuration](../.github/workflows/ci.yml) задаёт Linux/macOS/Windows matrix
и отдельный Linux NativeAOT smoke. Наличие job в YAML не доказывает успешное
прохождение конкретного commit; проверяйте реальные результаты CI.

Mocks/parser tests не подтверждают доступность PMU, ETW, Instruments, package
manager permissions или сохранность отчёта при аварии. Эти случаи требуют
отдельных host integration checks. Сквозной сценарий установки с чистой машины
и совместимость выпусков всех репозиториев также нужно проверять отдельно.
