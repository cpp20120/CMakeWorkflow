# Provisioning, CMake и IDE

> **Язык / Language:** [Русский](integrations.md) · [English](en/integrations.md) · [Documentation index](README.md)

## Provisioning

Tooling — явная подготовка окружения, независимая от scheduler:

```sh
CMakeWorkflow tooling list --json
CMakeWorkflow tooling doctor --profile native --json
CMakeWorkflow tooling plan --profile native --profile profiling --json
CMakeWorkflow tooling setup --profile native --dry-run --json
CMakeWorkflow tooling setup --profile native --yes
```

Profiles: `native`, `cmake`, `ninja`, `profiling`, `fuzz`, `development`, `all`. Doctor проверяет наличие и версии
команд, plan описывает действия. Setup повторно проверяет инструменты и применяет изменения только при `--yes`, если
установка необходима. `--dry-run` возвращает план без установки.

Поддерживаются package managers apt, pacman, dnf, Homebrew и winget.
`--pm none` отключает package installation, `--no-fallback` — standalone fallback, `--install-dir` задаёт
пользовательский bin directory. На Linux package setup использует non-interactive sudo; CLI не предоставляет GUI для
ввода пароля. Отмена не откатывает уже применённые изменения системы.

Fallback CMake/CTest на Linux x64/arm64 использует Kitware release с опубликованным SHA-256 digest и проверкой архива.
Store задаётся `CMAKEWORKFLOW_TOOL_STORE`. Для неподдерживаемых installer/platform combinations выводятся manual
diagnostics. Windows ADK и Xcode автоматически не устанавливаются.
Смотрите [ToolingService](../CMakeWorkflow.Tooling/Tools/ToolingService.cs).

Workflow никогда не запускает provisioning автоматически. Готовность toolchain не равна доступности profiler permissions
и не гарантирует, что проект корректно настроен.

## CMake target bridge

Необязательный [CMakeWorkflowTargetBridge.cmake](../cmake/CMakeWorkflowTargetBridge.cmake)
позволяет CMake custom target вызвать подготовленный executable, не входя рекурсивно в собственный build tree.
`WORKFLOW_RUNNER_EXECUTABLE` указывает на CLI; native prerequisite должен быть выражен через CMake `DEPENDS`.

Путь `--prepared` основан на этом контракте. Его нельзя использовать как универсальное разрешение доверять любому
существующему бинарнику. Для обычного CLI/IDE-запуска target run применяется стандартный build/verification path.
Основным источником targets остаётся CMake File API; bridge не обязателен.

## Boilerplate и CDash

[cmake_boilerplate](https://github.com/cpp20120/cmake_boilerplate) предоставляет генерацию проекта, CMake capabilities,
presets, testing, coverage, sanitizers и опциональный CTest/CDash pipeline. Его Bash/PowerShell bootstrap обслуживает
свой project lifecycle; tooling CLI обслуживает каталог инструментов.

Boilerplate может работать без CMakeWorkflow, а CMakeWorkflow — с проектом, созданным без boilerplate. Нужно
согласовывать пути и presets, а не переносить в runner собственную реализацию compiler flags и CMake dependencies.

CTest/CDash reporting и History решают разные задачи. CDash получает CTest XML через штатный opt-in submission. History
локально индексирует workflow, benchmark и profiling artifacts. SQLite History не является сервером CDash, а
CMakeWorkflow не заменяет CTest discovery или coverage instrumentation.

## VS Code extension

[Расширение VS Code](https://github.com/cpp20120/cmake_workflow_vscode) работает на workspace host, включая
Remote SSH, WSL и Dev Container. Укажите в `cmakeWorkflow.executable` нативный бинарник или `dotnet` и передайте
абсолютный путь к DLL отдельным элементом `cmakeWorkflow.prefixArgs`.

Планирование и запуск workflow, в том числе отдельных узлов, чтение History и анализ сохранённых профилей используют Host RPC.
**Host Operations** показывает состояние операции и узлов, индексацию History и отдельную вкладку событий.
Форма **Open Host API** показывает методы, объявленные подключённым Host. **History & Comparison** показывает активный
каталог и доступные причины пустого списка. В меню **… → Profiles** команда **Saved Profiles** предлагает найденные запуски,
а **Open Existing Profile…** открывает выбранную папку.

Отдельные мастера захвата и replay профилей, целей CMake, установки инструментов и кампаний DagFlow пока вызывают CLI.
Настройки и совместимость описаны в [README расширения](https://github.com/cpp20120/cmake_workflow_vscode/blob/master/README.md).

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
[CI configuration](../.github/workflows/ci.yml) задаёт Linux/macOS/Windows matrix и отдельный Linux NativeAOT smoke.
Наличие job в YAML не доказывает успешное прохождение конкретного commit; проверяйте реальные результаты CI.

Mocks/parser tests не подтверждают доступность PMU, ETW, Instruments, package manager permissions или сохранность отчёта
при аварии. Эти случаи требуют отдельных host integration checks. Сквозной сценарий установки с чистой машины и
совместимость выпусков всех репозиториев также нужно проверять отдельно.
