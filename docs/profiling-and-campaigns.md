# Профилирование и предметные кампании

## Profile run

`profile run` формирует workflow для build, отдельных неинструментированных
timing passes и captures. Для нескольких вариантов задаются повторяемые
`--variant NAME=BUILD_DIR`; имя начинается с lowercase буквы и содержит
lowercase буквы, цифры и `_`. Нужны различные имена и build directories.

```sh
CMakeWorkflow profile run --variant normal=out/normal --variant lto=out/lto --target bench --config Release --repeats 10 --backend auto --mode diagnostic --results-dir out/profiles -- --case chain-4
```

`bench` и его `--case` здесь — пример контракта пользовательской программы;
такая target должна действительно существовать в обоих настроенных деревьях.
CLI не включает O3/LTO по названию варианта. Compiler flags определяет проект.

Основные options: `--build-dir` либо `--variant`, `--target`, `--config`,
`--backend`, `--mode`, `--repeats` (1–100), `--timeout-seconds` (0 < N ≤ 86400),
`--results-dir`. Аргументы executable идут после `--`.
`--source-dir` и `--preset` позволяют задать явный configure одного дерева;
`--preset` с несколькими вариантами отклоняется как неоднозначный.

Также реализован shortcut `--campaign main-o3-lto` с `--o3-build-dir` и
`--lto-build-dir`; его target по умолчанию — `dagflow_stress_bench`.
Для общего API удобнее явно задавать варианты и target.

Timing samples отделены от инструментированных captures. Порядок вариантов
чередуется между повторениями. `diagnostic` — recipe отдельных CPU и scheduler
captures, а не новый универсальный режим платформенного profiler.
На Linux profile run дополнительно планирует counters-pass для perf, когда
выбран режим, отличный от `counters`.

## Backend и возможности

Источник — [ProfileWorkflowExecutor](../CMakeWorkflow.Profiling/ProfileWorkflowExecutor.cs)
и [Backends](../CMakeWorkflow.Profiling/Backends).

| Хост | Backend | Режимы backend |
|---|---|---|
| Linux | `perf` | `cpu`, `scheduler`, `counters` |
| Windows | `etw` | `cpu`, `scheduler` |
| macOS | `instruments` | `cpu`, `scheduler`, `allocations` |

`auto` выбирает backend текущего хоста. **Набор modes у backend не равен
набору modes высокоуровневого `profile run`:** его parser сейчас принимает
`cpu`, `scheduler`, `counters`, `diagnostic`. Например, наличие allocations у
Instruments не означает поддержку `profile run --mode allocations`.
На workflow profile-узле mode передаётся через `options` и проверяется backend.

Наличие perf/WPR/xctrace не гарантирует нужных прав и kernel events.
Успешный `perf stat` не доказывает доступность `perf record` или scheduler
tracing. Ошибки captures должны оставаться ошибками даже после успешного импорта
результата в History. Метрики разных backend нельзя считать эквивалентными.

## Analyze и артефакты

```sh
CMakeWorkflow profile analyze RUN_DIRECTORY
```

Analyze обрабатывает сохранённые captures; он не запускает benchmark заново.
В зависимости от результата создаются `analysis.json`, timing
`comparison.json`, `counter-comparison.json`, нормализованные stack/scheduler
данные и дополнительные отчёты. Исходные traces и manifests остаются артефактами.
Источник точных outputs — [ProfileCommand.cs](../CMakeWorkflow.Profiling/ProfileCommand.cs).

Эти profile-команды не имеют общего JSON completion protocol. При успешном
анализе CLI печатает `Profile analysis: PATH_TO_ANALYSIS_JSON`, при replay —
`Profile replay: RUN_DIRECTORY`. Интеграция должна проверять exit code и эти
реальные пути, а не выбирать «самую свежую» папку сканированием.

## Replay сохранённой пары

В текущем рабочем дереве реализован
[ProfileReplayCommand](../CMakeWorkflow.Profiling/ProfileReplayCommand.cs):

```sh
CMakeWorkflow profile replay BASELINE_DIRECTORY CANDIDATE_DIRECTORY --node RUN_NODE --backend auto --timeout-seconds 300 --results-dir out/profiles
CMakeWorkflow profile replay BASELINE_DIRECTORY CANDIDATE_DIRECTORY --node api-workloads --case chain-4 --harness 0 --backend auto --results-dir out/profiles
```

Для direct run используется проверенная executable identity из сохранённого
отчёта. Для invoke нужен конкретный harness case с native replay descriptor и
snapshot бинарника. Команда проверяет hash, argv, workload recipe и affinity;
один и тот же mutable executable path у baseline/candidate отклоняется.
Окружение, которое нельзя воспроизвести из сохранённых данных, также может
сделать replay недопустимым.

Replay выполняет инструментированные проходы исходной нагрузки. Это не новые
неинструментированные timing samples. Наличие пары в History или общего имени
case не является доказательством replayability. Возможность относится к
локальным доработкам исходников; проверяйте наличие команды в используемом CLI.

## DagFlow harness

Invoke-узел может явно задать `harnessDirectory` относительно build tree.
Native target должен сам писать туда `manifest.json`, `summary.json`,
`runs.jsonl`. До запуска каталог должен отсутствовать либо быть пустым.
Фиксированный каталог с предыдущими результатами не допускается для такого
ingestion: иначе нельзя установить принадлежность данных текущему run.

После успешного invoke CLI сохраняет копии под run-каталогом и записывает
ссылки в outputs/events. Поддерживаются snapshot и native replay metadata
в соответствии с
[DagFlowHarnessSnapshot.cs](../CMakeWorkflow.CMake/DagFlowHarnessSnapshot.cs).
Плагин не должен самостоятельно искать подходящий harness в build tree.

History импортирует успешные non-warmup samples с методом `dagflow-harness`,
проверяя структуру, идентичность и необходимые hashes/counts. Raw output
числа сохраняют свои units: summary fixed-point `scale` не является масштабом
raw measurements. Instrumented diagnostic builds не следует использовать
как timing baseline без проверки сопоставимости.

## Остальные кампании

Реализация: [CampaignCommand.cs](../CMakeWorkflow.Campaigns/CampaignCommand.cs).
Эти команды предметно связаны с DagFlow; обычному CMake-проекту они не нужны.

| Команда | Основные параметры |
|---|---|
| `dagflow mutation run` | `--source-dir`, `--suite`, `--cxx`, `--seed`, `--cases`, `--steps`, `--timeout`, `--report` |
| `dagflow weak-memory run` | `--source-dir`, `--protocol`, `--relacy`, `--cxx`, `--timeout`, `--report` |
| `dagflow fuzz seed` | `--layer`, `--source-dir`, `--output` |
| `dagflow fuzz run` | `--build-dir`, `--layer`, `--backend`, `--config`, `--duration`, `--timeout`, `--max-len`, `--corpus`, `--results-dir`, `--replay-only` |
| `dagflow fuzz triage` | `--run-dir` |
| `dagflow fuzz merge` | `--run-dir`, `--output`, `--coverage` |

Fuzz backend должен совпадать с конфигурацией дерева:
`libfuzzer`, `aflpp`, `honggfuzz`. Fuzz run ограничивает duration 1–86400 секунд,
input timeout 1–3600 секунд и max length 1–1048576 байт.
Coverage merge может запускать fuzzer; это не просто копирование файлов.
Merge принимает успешный сохранённый run и проверяет snapshot corpus.

Mutation/weak-memory сохраняют report и `campaign-completion.json`, используемый
для проверки завершённости и импорта. Fuzz run печатает свой фактический
`Fuzz run: ...`; triage создаёт `triage.json`, merge — `merge.json`.
Не считайте незавершённый report успешным и не обещайте rollback corpus или
созданных файлов при отмене.
