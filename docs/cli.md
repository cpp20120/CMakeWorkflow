# CLI: команды и вызов

> **Язык / Language:** [Русский](cli.md) · [English](en/cli.md) · [Documentation index](README.md)

Командный контракт определяется [Program.cs](../Program.cs) и обработчиками
семейств команд. Команды ниже существуют в текущих исходниках. Для локальной
разработки заменяйте `CMakeWorkflow` на
`dotnet bin/Release/net10.0/CMakeWorkflow.dll`.

## Сборка и публикация

```sh
dotnet build CMakeWorkflow.slnx -c Release
dotnet publish CMakeWorkflow.csproj -c Release -r linux-x64 -o out/native
```

RID в publish — пример для Linux x64; выбирайте RID и NativeAOT prerequisites
своего хоста. На Windows имя native executable имеет суффикс `.exe`. Разработка
через DLL требует .NET runtime, сборка — SDK. NativeAOT executable запускается
самостоятельно, а нужные CMake/compiler/profiler tools остаются внешними.

## CMake contexts и targets

```sh
CMakeWorkflow context inspect BUILD_DIR --json
CMakeWorkflow target resolve --build-dir BUILD_DIR --target TARGET --config Release --json
CMakeWorkflow target build --build-dir BUILD_DIR --target TARGET --config Release
CMakeWorkflow target invoke --build-dir BUILD_DIR --target CUSTOM_TARGET --config Release
CMakeWorkflow target run --build-dir BUILD_DIR --target EXECUTABLE --config Release -- ARGUMENT_1 "argument with spaces"
```

`inspect` читает существующие данные, `resolve` использует File API для поиска
executable. `build` допускает native executable/library targets. `invoke` явно
разрешает исполнение custom/utility target. `run` запрашивает build, а затем
выполняет executable; всё после `--` передаётся программе.

`--prepared` у build/run — специальный путь для CMake bridge с уже выраженным
`DEPENDS`, а не обычная оптимизация «пропустить build». Подробнее в
[интеграциях](integrations.md#cmake-target-bridge).

## Workflow

```sh
CMakeWorkflow workflow plan FILE --only NODE_A --only NODE_B --json
CMakeWorkflow workflow run FILE --only NODE_A --only NODE_B --jobs 2 --results-dir DIR --history-db CATALOG --json
CMakeWorkflow workflow run FILE --no-history --json
CMakeWorkflow workflow report RUN_DIRECTORY --json
CMakeWorkflow workflow sequence --build-dir BUILD_DIR --first FIRST --second SECOND --config Release --first-arg ARG --second-arg ARG
```

`--jobs` по умолчанию 1. `--only` повторяемый. Без `--results-dir` workflow run
использует `out/workflow-runs` рядом с manifest и создаёт внутри отдельный run.
Явный относительный `--results-dir` разрешается от cwd CLI. Читайте фактический
путь из протокола, не воспроизводите алгоритм имени каталога в клиенте.

`sequence` выполняет build первого target → его executable → build второго →
его executable. Аргументы задаются повторяемыми `--first-arg` и `--second-arg`.
`--controller-build-dir` относится к защите от небезопасного вложенного build;
обычная интеграция IDE не должна подставлять его произвольно.

## History

```sh
CMakeWorkflow history import RESULTS_ROOT --recursive --db CATALOG --json
CMakeWorkflow history runs --limit 50 --status failed --db CATALOG --json
CMakeWorkflow history show RUN_ID --db CATALOG --json
CMakeWorkflow history metrics --run RUN_ID --metric METRIC --variant VARIANT --scenario CASE --db CATALOG --json
CMakeWorkflow history query --metric METRIC --run RUN_ID --db CATALOG --json
CMakeWorkflow history compare BASELINE_ID CANDIDATE_ID --db CATALOG --json
CMakeWorkflow history detect BASELINE_ID CANDIDATE_ID --metric process_wall_time --direction higher-worse --min-percent 5 --alpha 0.05 --db CATALOG --json
```

`runs` поддерживает также `--since`, `--until` с ISO timestamps, `--workflow`
и `--variant`. В `metrics` обязателен `--run`, в `query` — `--metric`;
они принимают фильтры `--variant` и `--scenario`. `detect` также позволяет
сузить семейство проверок через `--variant`/`--scenario`.

У query-команд путь каталога задаётся `--db`. У workflow/profile run и profile
replay — `--history-db` либо `--no-history`. Одновременная передача этих двух
run-options запрещена. `CMAKEWORKFLOW_HISTORY_DB` задаёт общий default catalog.

## Profiling и DagFlow

```sh
CMakeWorkflow profile run --build-dir BUILD_DIR --target EXECUTABLE --backend auto --mode cpu --repeats 3 --config Release --results-dir out/profiles -- PROGRAM_ARG
CMakeWorkflow profile run --variant before=BUILD_A --variant after=BUILD_B --target EXECUTABLE --mode counters --repeats 3
CMakeWorkflow profile analyze RUN_DIRECTORY
CMakeWorkflow profile replay BASELINE_DIRECTORY CANDIDATE_DIRECTORY --node NODE --case CASE --backend auto --results-dir out/profiles
CMakeWorkflow dagflow mutation run --source-dir SOURCE_DIR --suite all
CMakeWorkflow dagflow weak-memory run --source-dir SOURCE_DIR --protocol all --relacy RELACY_ROOT
CMakeWorkflow dagflow fuzz seed --layer wake_protocol --output CORPUS_ROOT
CMakeWorkflow dagflow fuzz run --build-dir BUILD_DIR --layer wake_protocol --duration 60 --results-dir out/fuzz-runs
CMakeWorkflow dagflow fuzz triage --run-dir RUN_DIRECTORY
CMakeWorkflow dagflow fuzz merge --run-dir RUN_DIRECTORY --output CORPUS_DIRECTORY
```

`dagflow profile` маршрутизируется в тот же profiling handler. Для прямого
run-узла в `profile replay` не передавайте `--case`. Это пути run-каталогов,
тогда как History compare/detect принимают run IDs.
Детали и ограничения — в [profiling and campaigns](profiling-and-campaigns.md).

## Tooling

```sh
CMakeWorkflow tooling list --json
CMakeWorkflow tooling doctor --profile native --json
CMakeWorkflow tooling plan --profile native --profile profiling --pm auto --json
CMakeWorkflow tooling setup --profile native --dry-run --json
CMakeWorkflow tooling setup --profile native --yes
```

`--pm`: `auto`, `none`, `apt`, `pacman`, `dnf`, `brew`, `winget`.
Дополнительно: `--install-dir PATH`, `--no-fallback`. Alias `setup` направляет
в `tooling setup`. Подробности в [provisioning](integrations.md#provisioning).

## Выход и ошибки

Для workflow run: 0 — succeeded, 1 — failed, 130 — пользовательская отмена.
Общий entry point также использует 2 для некоторых ошибок выбора команды.
У tooling doctor код 2 означает неготовность инструментов; setup может вернуть
2 при неудачной установке. Не переносите интерпретацию кода 2 на все команды.

`workflow report` с кодом 0 означает успешное чтение отчёта, а не успешность
сохранённого workflow: проверяйте `status` в данных. `history detect` может
успешно вернуть `regression`; такой результат читается из JSON, а не из exit code.

Разделяйте stdout, stderr, spawn error, exit code и termination signal.
`--json` поддерживается только соответствующими обработчиками; у profile
run/analyze/replay и DagFlow campaigns нет общего JSONL-протокола завершения.
