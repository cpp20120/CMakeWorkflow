# CLI: команды и вызов

> **Язык / Language:** [Русский](cli.md) · [English](en/cli.md) · [Documentation index](README.md)

Командный контракт определяется [маршрутизацией CLI](../Commands/CliApplication.cs) и обработчиками семейств команд;
[Program.cs](../Program.cs) отвечает за отмену процесса и диагностику выхода. Команды ниже существуют в
текущих исходниках. Для локальной разработки заменяйте `CMakeWorkflow` на
`dotnet bin/Release/net10.0/CMakeWorkflow.dll`.

В синтаксисе ниже квадратные скобки обозначают необязательные аргументы, `|` обозначает выбор, а `...` —
повторение. Параметры вида `PATH` и `NAME` заменяйте своими значениями. Общих флагов `--json` и `--version`
у CLI нет. Вызов без аргументов или с одним `--help` печатает общую справку в stderr и возвращает код 2.
У `remote`, `remote provision` и `cas` есть собственные `help` и `--help` с кодом 0.

## Сборка и публикация

```sh
dotnet build CMakeWorkflow.slnx -c Release
dotnet publish CMakeWorkflow.csproj -c Release -r linux-x64 -o out/native
```

RID в publish — пример для Linux x64; выбирайте RID и NativeAOT prerequisites своего хоста. На Windows имя native
executable имеет суффикс `.exe`. Разработка через DLL требует .NET runtime, сборка — SDK. NativeAOT executable
запускается самостоятельно, а нужные CMake/compiler/profiler tools остаются внешними.

## CMake contexts и targets

```sh
CMakeWorkflow context inspect BUILD_DIR --json
CMakeWorkflow target resolve --build-dir BUILD_DIR --target TARGET --config Release --json
CMakeWorkflow target build --build-dir BUILD_DIR --target TARGET --config Release
CMakeWorkflow target invoke --build-dir BUILD_DIR --target CUSTOM_TARGET --config Release
CMakeWorkflow target run --build-dir BUILD_DIR --target EXECUTABLE --config Release -- ARGUMENT_1 "argument with spaces"
```

`inspect` читает существующие данные, `resolve` использует File API для поиска executable. `build` допускает native
executable/library targets. `invoke` явно разрешает исполнение custom/utility target. `run` запрашивает build, а затем
выполняет executable; всё после `--` передаётся программе.

`--prepared` у build/run — специальный путь для CMake bridge с уже выраженным
`DEPENDS`, а не обычная оптимизация «пропустить build». Подробнее в
[интеграциях](integrations.md#cmake-target-bridge).

### Параметры contexts и targets

- `context inspect BUILD_DIR [--json]` выводит JSON в обеих формах. `--json` ставится после пути.
- Для всех `target` обязательны `--build-dir DIR` и `--target NAME`. `--config CONFIG` выбирает конфигурацию CMake.
- `--controller-build-dir DIR` указывает дерево управляющей сборки для проверки вложенного вызова у `build`,
  `invoke` и `run`. Обработчик `resolve` принимает флаг, но не выполняет эту проверку.
- `--prepared` допустим для `build` и `run`, `--json` для `resolve`, а `-- ARGS...` для `run`.

## Workflow

```sh
CMakeWorkflow workflow generate benchmark --build-dir BUILD_DIR --target EXECUTABLE --config Release --repeats 10 --timeout-seconds 300 --output workflows/bench.json -- PROGRAM_ARG
CMakeWorkflow workflow plan FILE --only NODE_A --only NODE_B --json
CMakeWorkflow workflow run FILE --only NODE_A --only NODE_B --jobs 2 --results-dir DIR --history-db CATALOG --json
CMakeWorkflow workflow run FILE --no-history --json
CMakeWorkflow workflow report RUN_DIRECTORY --json
CMakeWorkflow workflow report RUN_DIRECTORY --html
CMakeWorkflow workflow sequence --build-dir BUILD_DIR --first FIRST --second SECOND --config Release --first-arg ARG --second-arg ARG
```

`workflow generate benchmark` только записывает обычный манифест v2 и не перезаписывает существующий файл. Относительные
`--build-dir` и `--output`
считаются от cwd CLI. Полный пример — в [рецептах](ru/recipes.md).

`workflow generate profile`, `test-matrix`, `build-matrix` и
`benchmark-matrix` используют отдельные типизированные контракты. Сложные матрицы передаются через
`--recipe FILE --output MANIFEST`; см. [описание рецептов](ru/recipes.md).

`--jobs` по умолчанию 1. `--only` повторяемый. Без `--results-dir` workflow run использует `out/workflow-runs` рядом с
manifest и создаёт внутри отдельный run. Явный относительный `--results-dir` разрешается от cwd CLI. Читайте фактический
путь из протокола, не воспроизводите алгоритм имени каталога в клиенте.

`sequence` выполняет build первого target → его executable → build второго → его executable. Аргументы задаются
повторяемыми `--first-arg` и `--second-arg`.
`--controller-build-dir` относится к защите от небезопасного вложенного build; обычная интеграция IDE не должна
подставлять его произвольно.

### Планирование, запуск и восстановление

| Команда | Аргументы и флаги |
| --- | --- |
| `workflow plan FILE` | `[--only NODE]...` `[--controller-build-dir DIR]` `[--json]` |
| `workflow run FILE` | `[--only NODE]...` `[--jobs N]` `[--results-dir DIR]` `[--controller-build-dir DIR]` `[--json]` `[--history-db PATH \| --no-history]` |
| `workflow run FILE --resume-dir RUN_DIRECTORY` | `[--jobs N]` `[--controller-build-dir DIR]` `[--json]` `[--history-db PATH \| --no-history]` |
| `workflow report RUN_DIRECTORY` | `[--json \| --html]` после пути |
| `workflow resume RUN_DIRECTORY` | Использует манифест из сохранённого `run.json`. |
| `workflow reconcile RUN_DIRECTORY` | `[--node ID]` `--decision succeeded\|retry` `--reason TEXT` `--expected-checkpoint SHA256` `--confirm-stopped` `[--json]` |
| `workflow sequence` | `--build-dir DIR` `--first TARGET` `--second TARGET` `[--config CONFIG]` `[--first-arg ARG]...` `[--second-arg ARG]...` `[--controller-build-dir DIR]` |

`--only` выбирает узлы вместе с зависимостями. `--jobs` задаёт предел параллелизма. `workflow plan` принимает
`--jobs` и `--results-dir` через общий парсер, но эти значения используются только при запуске. `--resume-dir`
несовместим с `--only` и `--results-dir`. У `workflow run --json` события выполнения идут в stdout в формате JSONL,
у `workflow plan --json` выводится один JSON-объект.

`workflow reconcile` записывает решение для checkpoint в состоянии `outcome_unknown`. SHA-256 относится к текущему
`run.json`, `--reason` принимает до 4096 символов, `--confirm-stopped` подтверждает остановку прежних процессов.
Без `--node` допустим только `retry` для прерванного запуска без узлов с неизвестным исходом. Условия и порядок
восстановления описаны в [recovery](recovery.md).

### HTML-отчёт

`workflow report RUN_DIRECTORY --html` создаёт или обновляет `RUN_DIRECTORY/report.html` из сохранённых
`run.json` и `analysis.json`. Для сохранённого replay достаточно `analysis.json`; новый `profile replay … --html`
создаёт отчёт сразу. HTML открывается без сети. Для кнопок скачивания и просмотра исходных артефактов
сохраняйте структуру каталога рядом с HTML.
Если захват прервался, `profile replay … --html` экспортирует доступные данные и сохраняет код ошибки replay.

Верхняя сводка показывает статус, количество сэмплов всего и с декодированным стеком, пропуски,
предупреждения и до десяти функций по self samples на захват. Для пар доступны дельты времени,
сопоставимых счётчиков и долей горячих функций, а также ссылки на оба flamegraph.

Отчёт сам подбирает графики из сохранённых измерений. Варианты с одинаковыми параметрами нагрузки
сравниваются по медианам и общим индексам повторов. Strip plot и boxplot показывают наблюдения и
квартили. Варьирующиеся `workers` и `shards` дают тепловую карту, `arrival_rate` и задержка — кривую
насыщения, `workers` и throughput — кривую масштабирования. Efficiency рассчитывается при наличии
точки с одним worker в той же серии. Для каждой кривой остальные параметры нагрузки фиксированы.

Относительная heatmap показывает `100 × (медиана candidate − медиана baseline) / |медиана baseline|`
для каждой сопоставимой ячейки. Цвет учитывает `direction` метрики. Пустая ячейка означает, что
одной стороны нет или процент невозможно вычислить. Абсолютные тепловые карты доступны рядом.

В benchmark-matrix recipe ось может объявить `role` со значением `workers`, `shards`, `arrival-rate`,
`matrix-x`, `matrix-y`, `sweep` или `facet`. Workflow сохраняет выбранные значения, роли, первый вариант
как baseline и порядок выполнения в `benchmarkTrial`. History переносит метаданные в каталог. Для
старых результатов отчёт извлекает оси из аргументов. Ось `facet` фиксируется при построении среза.

### Флаги генерации манифестов

`--compiler-cache-profile NAME` у benchmark выбирает именованный профиль измерения кеша компилятора.

| Команда | Обязательные аргументы | Дополнительные флаги |
| --- | --- | --- |
| `workflow generate benchmark` | `--build-dir DIR`, `--target NAME`, `--repeats N`, `--timeout-seconds N`, `--output FILE` | `--config CONFIG`, `--source-dir DIR`, `--preset NAME`, `--compiler-cache-profile NAME`, `-- ARGS...` |
| `workflow generate profile` | `--build-dir DIR` или повторяемый `--variant NAME=DIR`, затем `--target NAME`, `--platform linux\|windows\|macos`, `--output FILE` | `--config CONFIG`, `--repeats N`, `--timeout-seconds N`, `--backend BACKEND`, `--mode MODE`, `--source-dir DIR`, `--preset NAME`, `--pmc-events LIST`, `--pmc-interval N`, `--memory-stall-event EVENT`, `-- ARGS...` |
| `workflow generate test-matrix` | `--build-dir DIR`, `--target NAME`, `--output FILE`, хотя бы один `--label REGEX` или `--test NAME=REGEX` | `--config CONFIG`, `--order parallel\|sequential`, `--timeout-seconds N`. `--label` и `--test` можно повторять. |
| `workflow generate build-matrix` | Повторяемые `--variant NAME=DIR` и `--target NAME`, `--output FILE` | `--config CONFIG` |
| `workflow generate benchmark-matrix` | `--recipe FILE`, `--output FILE` | |
| `workflow generate KIND --recipe FILE --output FILE` | `KIND` может быть любым из пяти перечисленных видов. | Остальные параметры задаются в JSON-рецепте. |

У `generate profile` по умолчанию 3 повтора, 300 секунд, `--backend auto` и `--mode cpu`. У `test-matrix`
порядок по умолчанию `parallel` и тайм-аут 300 секунд на группу. `--label` фильтрует по метке CTest,
`--test` задаёт имя группы и регулярное выражение по именам тестов. Варианты backend, mode и параметры PMC
описаны ниже в разделе профилирования. [Рецепты](ru/recipes.md)

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

Добавляйте `--html OUTPUT.html` в конце команд `compare`, `detect`, `runs`, `metrics`, `query`,
`matrix` и `sweep`. `matrix` и `sweep` принимают `--metric NAME` и фильтры `query`.
Для HTML у `runs` также обязателен `--metric NAME`.

```sh
CMakeWorkflow history compare BASELINE_ID CANDIDATE_ID --db CATALOG --html compare.html
CMakeWorkflow history detect BASELINE_ID CANDIDATE_ID --metric METRIC --direction higher-worse --min-percent 5 --db CATALOG --html detect.html
CMakeWorkflow history detect RUN_ID RUN_ID --paired --baseline-variant baseline --candidate-variant candidate --metric METRIC --direction higher-worse --min-percent 5 --db CATALOG --html paired.html
CMakeWorkflow history matrix --metric METRIC --run RUN_ID --db CATALOG --html matrix.html
CMakeWorkflow history sweep --metric METRIC --run RUN_ID --db CATALOG --html sweep.html
CMakeWorkflow history runs --metric METRIC --limit 50 --db CATALOG --html runs.html
```

`compare` рисует медианы и сырые измерения. `detect` показывает эффект и описательный bootstrap-интервал
для проверенных серий с достаточными данными. Вердикт остаётся результатом permutation test с поправкой
Holm. `matrix` строит heatmap по `workers` и `shards` или другой паре варьирующихся числовых осей.
`sweep` строит XY по числовым параметрам, `runs` — ряд медиан по дате
или номеру запуска с commit SHA, компилятором, генератором и хостом, если они сохранены. При наличии
сопоставимой серии из девяти и более точек контрольные границы вычисляются по первым восьми запускам.

`history detect --paired` включает отдельный односторонний sign-flip test с поправкой Холма. Флаг означает,
что одинаковые `sampleIndex` действительно образуют экспериментальные пары. Требуются не менее 10 пар,
уникальные индексы и точное совпадение их наборов. Для двух вариантов одного запуска укажите
`--baseline-variant` и `--candidate-variant`. Если сохранённый рецепт использовал `sequential`, такой
запуск не подходит для парного детекта. Эффект считается как медиана парных разностей, делённая на
модуль медианы baseline. HTML bootstrap пересэмплирует целые пары. Обычный `history detect` использует
перестановки между двумя выборками.

`runs` поддерживает также `--since`, `--until` с ISO timestamps, `--workflow`
и `--variant`. В `metrics` обязателен `--run`, в `query` — `--metric`; они принимают фильтры `--variant` и `--scenario`.
`detect` также позволяет сузить семейство проверок через `--variant`/`--scenario`.

У query-команд путь каталога задаётся `--db`. У workflow/profile run и profile replay — `--history-db` либо
`--no-history`. Одновременная передача этих двух run-options запрещена. `CMAKEWORKFLOW_HISTORY_DB` задаёт общий default
catalog.

Для запуска через Host используйте `remote start` с `--history-default`, `--history-db PATH` или `--no-history`.
Путь каталога для `--history-db` должен быть абсолютным и находиться внутри workspace Host. При отсутствии флагов
индексация отключена. Команды `remote history-status --operation ID` и `remote history-retry --operation ID`
показывают состояние и повторяют индексацию по ID операции. См. [справочник remote CLI](ru/remote-cli.md).

### Флаги History

Команды History принимают `--db PATH` для выбора каталога. Для команд `matrix` и `sweep`
доступен HTML; для остальных команд также доступен `--json`.

| Команда | Остальные аргументы и флаги |
| --- | --- |
| `history import PATH` | `--recursive` обходит подкаталоги. |
| `history runs` | `--limit N` по умолчанию 50, `--status STATUS`, `--since ISO`, `--until ISO`, `--workflow TEXT`, `--variant NAME`. |
| `history show RUN_ID` | Показывает один запуск. |
| `history metrics --run RUN_ID` | `--metric NAME`, `--variant NAME`, `--scenario NAME`. |
| `history query --metric NAME` | `--run RUN_ID`, `--variant NAME`, `--scenario NAME`. |
| `history compare BASELINE_ID CANDIDATE_ID` | Сравнивает два запуска. |
| `history detect BASELINE_ID CANDIDATE_ID` | Обязательны `--metric NAME`, `--direction higher-worse\|lower-worse`, `--min-percent N`. Дополнительно `--variant NAME`, `--scenario NAME`, `--alpha N` (по умолчанию 0.05). |
| `history host-summary RUN_ID [RUN_ID]...` | Сводка по 1–32 запускам с метриками и артефактами профилирования. |

## Profiling и DagFlow

```sh
CMakeWorkflow profile run --build-dir BUILD_DIR --target EXECUTABLE --backend auto --mode cpu --repeats 3 --config Release --results-dir out/profiles -- PROGRAM_ARG
CMakeWorkflow profile run --variant before=BUILD_A --variant after=BUILD_B --target EXECUTABLE --mode counters --repeats 3
CMakeWorkflow profile analyze RUN_DIRECTORY
CMakeWorkflow profile replay BASELINE_DIRECTORY CANDIDATE_DIRECTORY --node NODE --case CASE --backend auto --results-dir out/profiles
CMakeWorkflow dagflow mutation run --source-dir SOURCE_DIR --suite all
CMakeWorkflow dagflow weak-memory run --source-dir SOURCE_DIR --protocol all --relacy RELACY_ROOT
CMakeWorkflow dagflow weak-memory run --source-dir SOURCE_DIR --protocol deque --mutant deque-relax-publication --relacy RELACY_ROOT
CMakeWorkflow dagflow fuzz seed --layer wake_protocol --output CORPUS_ROOT
CMakeWorkflow dagflow fuzz run --build-dir BUILD_DIR --layer wake_protocol --duration 60 --results-dir out/fuzz-runs
CMakeWorkflow dagflow fuzz triage --run-dir RUN_DIRECTORY
CMakeWorkflow dagflow fuzz merge --run-dir RUN_DIRECTORY --output CORPUS_DIRECTORY
```

`dagflow profile` маршрутизируется в тот же profiling handler. Для прямого run-узла в `profile replay` не передавайте
`--case`. Это пути run-каталогов, тогда как History compare/detect принимают run IDs. Детали и ограничения —
в [profiling and campaigns](profiling-and-campaigns.md).

### Флаги `profile run`

| Флаг | Назначение |
| --- | --- |
| `--build-dir DIR` | Одно дерево сборки. |
| `--variant NAME=DIR` | Повторяемый именованный вариант с отдельным деревом сборки. Имя начинается со строчной латинской буквы и может содержать строчные буквы, цифры и `_`. |
| `--campaign main-o3-lto` | Кампания DagFlow для двух конфигураций. Требует `--o3-build-dir DIR` и `--lto-build-dir DIR`, исключает `--variant` и `--build-dir`. |
| `--o3-build-dir DIR`, `--lto-build-dir DIR` | Деревья O3 и LTO для кампании. |
| `--target NAME` | Целевой executable. Для `main-o3-lto` по умолчанию `dagflow_stress_bench`, иначе обязателен. |
| `--source-dir DIR`, `--preset NAME`, `--config CONFIG` | Исходное дерево, configure preset и конфигурация CMake. `--preset` допустим с одним вариантом. При preset без `--source-dir` используется текущий каталог. |
| `--backend BACKEND` | `auto` по умолчанию, `perf` для Linux, `etw` для Windows, `instruments` для macOS. |
| `--mode MODE` | `cpu` по умолчанию. Также `scheduler`, `counters` и `diagnostic`. Последний собирает CPU-профиль и добавляет планировщик на Linux и Windows. |
| `--repeats N`, `--timeout-seconds N` | 1–100 повторов, по умолчанию 3. Тайм-аут одной пробы больше нуля и не более 86400 секунд, по умолчанию 300. |
| `--results-dir DIR`, `--output DIR` | Каталог результатов. `--results-dir` имеет приоритет, по умолчанию `out/profiles`. |
| `--pmc-events LIST`, `--pmc-interval N` | 1–4 различных источника через запятую и интервал переполнения для ETW в режиме `counters`. По умолчанию `TotalCycles,InstructionRetired` и 65536. Источники и их допустимые интервалы показывает `wpr -pmcsources`. |
| `--memory-stall-event EVENT` | Явное событие задержек памяти для `counters` с `perf` или `etw`. |
| `--history-db PATH`, `--no-history` | Выбрать каталог History или отключить импорт. Взаимоисключающие флаги. |
| `-- ARGS...` | Передать оставшиеся аргументы исполняемой программе. |

Выберите один способ задания деревьев: `--build-dir`, `--variant` или `--campaign`. Матрица поддерживаемых режимов
и правила измерений описаны в [профилировании](profiling-and-campaigns.md).

### Анализ, replay и исторические запуски

`dagflow profile` и `profile` принимают один и тот же набор подкоманд.

```text
CMakeWorkflow profile analyze RUN_DIRECTORY
CMakeWorkflow profile flamegraph PERF_DATA OUTPUT_JSON
CMakeWorkflow profile render-historical DIRECTORY
CMakeWorkflow profile replay BASELINE_DIRECTORY CANDIDATE_DIRECTORY --node ID
  [--case NAME] [--harness INDEX] [--backend auto|perf|etw]
  [--results-dir DIR] [--timeout-seconds N] [--history-db PATH | --no-history]
CMakeWorkflow profile run-historical --output DIR [--source-dir DIR]
  [--trials N] [--stat-trials N] [--jobs N] [--resume]
  [--shuffle-algorithm NAME] [--seeds SEED,SEED,...]
  [--schedule-strategy NAME] [--shuffle-scope block|global]
```

- `analyze` повторно разбирает сохранённый запуск. `flamegraph` декодирует выборки из `perf.data` в JSON.
  `render-historical` строит отчёт по сохранённым историческим результатам. У этих трёх команд только позиционные аргументы.
- У `replay` `--node` выбирает узел в обоих запусках. `--case` выбирает кейс native harness, а `--harness` —
  его индекс от 0 до 1000, по умолчанию 0. `--harness` требует `--case`. По умолчанию backend `auto`,
  результаты в `out/profiles`, тайм-аут 300 секунд (допустимо больше 0 и до 86400 включительно).
- `run-historical` работает на Linux. `--source-dir` по умолчанию текущий каталог. `--trials` задаёт число
  timing-проб (по умолчанию 5), `--stat-trials` число прогонов `perf stat` на конфигурацию (по умолчанию 3).
  Оба числа от 1 до 1000. `--jobs` задаёт параллелизм сборки, по умолчанию 4. `--resume` использует журнал из `--output`.
- `--shuffle-algorithm` принимает `none`, `dotnet`, `mt19937`, `pcg32`, `pcg64`, `xoshiro256ss`, `xoroshiro128pp`,
  `splitmix64`, `sfc64`, `philox`, `threefry`, `python-legacy` (по умолчанию).
- `--seeds` принимает 1–64 различных UInt32 через запятую, по умолчанию `20260930`. Для `dotnet` максимум 2147483647.
- `--schedule-strategy` принимает `sequential`, `shuffle-global`, `random-interleaved` (по умолчанию),
  `randomized-blocks`, `balanced-blocks`. `--shuffle-scope` по умолчанию `block`, для `shuffle-global` используется
  `global`. [Историческое расписание](ru/historical-scheduling.md)

### Флаги кампаний DagFlow

Префикс `dagflow` необязателен для `mutation`, `weak-memory` и `fuzz`. Относительные пути аргументов разрешаются от текущего каталога.

| Команда | Обязательные флаги | Дополнительные флаги |
| --- | --- | --- |
| `dagflow mutation run` | | `--source-dir DIR`, `--suite NAME`, `--cxx EXE`, `--seed SEED`, `--cases N`, `--steps N`, `--timeout SECONDS`, `--report FILE` |
| `dagflow weak-memory run` | `--relacy DIR` | `--source-dir DIR`, `--protocol NAME`, `--cxx EXE`, `--timeout SECONDS`, `--report FILE`, `--mutant NAME` |
| `dagflow fuzz seed` | `--layer NAME` | `--source-dir DIR`, `--output DIR` |
| `dagflow fuzz run` | `--build-dir DIR`, `--layer NAME` | `--backend NAME`, `--duration SECONDS`, `--timeout SECONDS`, `--corpus DIR`, `--results-dir DIR`, `--config CONFIG`, `--max-len N`, `--replay-only` |
| `dagflow fuzz triage` | `--run-dir DIR` | |
| `dagflow fuzz merge` | `--run-dir DIR` | `--output DIR`, `--coverage` |

`--source-dir` по умолчанию текущий каталог, `--cxx` выбирает компилятор (по умолчанию `c++`). У mutation
`--suite` по умолчанию `all`, `--cases` и `--steps` задают размеры кампании, `--seed` — seed генератора.
У weak-memory `--protocol` по умолчанию `all`, `--mutant` выбирает конкретную мутацию. Для обеих кампаний
`--report` задаёт путь отчёта. У fuzz `--layer` выбирает слой, `--backend` выбирает движок (`libfuzzer`, `aflpp` или `honggfuzz`),
`--duration` задаёт бюджет кампании, `--timeout` — тайм-аут пробы, `--max-len` — предел размера входа,
а `--replay-only` ограничивает запуск воспроизведением корпуса. `--coverage` запрашивает объединение по покрытию для libFuzzer.
[Профилирование и кампании](profiling-and-campaigns.md)

## Tooling

```sh
CMakeWorkflow tooling list --json
CMakeWorkflow tooling doctor --profile native --json
CMakeWorkflow tooling plan --profile native --profile profiling --pm auto --json
CMakeWorkflow tooling setup --profile native --dry-run --json
CMakeWorkflow tooling setup --profile native --yes
```

`--pm`: `auto`, `none`, `apt`, `pacman`, `dnf`, `brew`, `winget`. Дополнительно: `--install-dir PATH`, `--no-fallback`.
Alias `setup` направляет в `tooling setup`. Подробности в [provisioning](integrations.md#provisioning).

### Общий каталог инструментов

`tooling list [--json]` выводит поддерживаемые профили и инструменты. `tooling doctor` проверяет готовность,
`tooling plan` формирует план, а `tooling setup` применяет его при необходимости.

- `[PROFILE]...` и повторяемый `--profile NAME` выбирают профили для `doctor`, `plan` и `setup`.
- `--pm NAME` и его алиас `--package-manager NAME` выбирают менеджер пакетов. `--install-dir DIR` задаёт каталог
  установки, `--no-fallback` отключает резервную установку. `--json` включает структурированный ответ.
- `--dry-run` выводит план для `setup`. `--yes` разрешает установку недостающих инструментов.
  Общий парсер принимает эти два флага и на других командах tooling, где они не добавляют изменений.

### Visual Studio

```text
CMakeWorkflow tooling vs doctor|plan|setup [--profile NAME]... [--instance ID]
  [--bootstrapper FULL_PATH --install-path FULL_PATH [--build-tools]]
  [--json] [--yes --plan-sha SHA256]
```

`--profile` выбирает компоненты: `windows-cpp`, `windows-cpp-clang`, `windows-cpp-perf`, `windows-cpp-graphics`.
`--instance` выбирает существующий экземпляр Visual Studio. `--bootstrapper` и `--install-path` задают установщик
и каталог новой установки, `--build-tools` выбирает Build Tools. `setup` требует `--yes` и `--plan-sha`
из плана. [Подготовка Visual Studio и выбор toolchain](ru/toolchain-discovery.md)

## Требования окружения

```text
CMakeWorkflow env doctor REQUIREMENTS.json [--json]
CMakeWorkflow env plan REQUIREMENTS.json [--json]
CMakeWorkflow env setup REQUIREMENTS.json --yes --plan-sha SHA256 [--allow-system-changes] [--json]
CMakeWorkflow env probe-etw user|kernel.cpu|kernel.scheduler [--json]
```

`env doctor` проверяет выполнение требований, `env plan` выводит план. `env setup` требует `--yes` и
`--plan-sha`, совпадающий с полем `approvalSha256` плана. `--allow-system-changes` разрешает предусмотренные
планом системные изменения. Общий парсер принимает эти три флага при `doctor` и `plan`, которые только инспектируют
окружение. `env probe-etw` на Windows проверяет выбранный тип ETW-сессии и возвращает 2 при статусе, отличном от
`available`. Формат требований описан в [HOST_PREPARATION.md](../HOST_PREPARATION.md).

## Host

```text
CMakeWorkflow host serve (--endpoint PATH | --stdio)
CMakeWorkflow host doctor --endpoint PATH [--workspace DIR] [--profile NAME]... [--install-dir DIR] [--json]
CMakeWorkflow host fix --endpoint PATH [--workspace DIR] [--profile NAME]... [--install-dir DIR] [--json]
  [--pm NAME] [--no-fallback] [--yes] [--dry-run]
CMakeWorkflow host bridge --stdio --endpoint PATH
```

- `serve` запускает Host. `--endpoint` задаёт Unix-сокет или Windows named pipe, `--stdio` использует stdin/stdout.
- `doctor` и `fix` подключаются к существующему Host. `--workspace` по умолчанию текущий каталог.
  `--profile` повторяется, `--install-dir` задаёт каталог установки инструментов, `--json` включает JSON-вывод.
- `fix` формирует план и применяет его с `--yes`. `--dry-run` оставляет только план, даже если указан `--yes`.
  `--pm` выбирает менеджер пакетов, `--no-fallback` отключает резервную установку.
- `bridge` соединяет stdin/stdout клиента с существующим endpoint. Это служебная команда для SSH-транспорта.
  [Host IPC](ru/host-ipc.md)

## Remote

Для операций с запущенным Host используйте `CMakeWorkflow remote COMMAND --endpoint PATH --workspace DIR`.
Добавьте параметры выбранного транспорта.

| Транспорт | Параметры |
| --- | --- |
| `--transport openssh` (по умолчанию) | `--destination USER@HOST`, `[--ssh-executable PATH]` (по умолчанию `ssh`). |
| `--transport sshnet` | `--host HOST`, `--user USER`, `--key-file LOCAL_KEY`, `--host-key-sha256 SHA256:PIN`, `[--port N]` (по умолчанию 22). |
| `--transport local` | Подключается к локальному endpoint. |

Оба SSH-транспорта принимают `--remote-executable PATH` (по умолчанию `cmakeworkflow`) и
`--connect-timeout-seconds N` (1–3600, по умолчанию 15). Локальный транспорт принимает тайм-аут через общий
парсер, но не использует его. `--endpoint` и `--workspace` относятся к окружению Host. Ключ `--key-file`,
выходной файл `--output`, каталог `--store` и программа `--ssh-executable` находятся на стороне клиента.

| Подкоманда | Аргументы и флаги сверх параметров транспорта |
| --- | --- |
| `start` | `--request-id ID` `--manifest REMOTE_FILE` `--manifest-sha256 SHA256` `--results-dir REMOTE_DIR` `[--jobs N]` `[--only NODE]...` `[--history-db REMOTE_FILE \| --history-default \| --no-history]` |
| `find` | `--request-id ID` |
| `status`, `inspect`, `report`, `artifacts` | `--operation ID` |
| `events` | `--operation ID` `[--after SEQUENCE]` `[--limit N]` |
| `download` | `--operation ID` `--artifact-id ID` `--output LOCAL_FILE` |
| `stash` | `--operation ID` `--store LOCAL_CAS` |
| `cancel` | `--operation ID` `--revision N` |
| `history-status`, `history-retry` | `--operation ID` |

`start --jobs` принимает 1–4096 (по умолчанию 1), `--only` — до 1000 ID узлов. `events --after` задаёт курсор
событий, `--limit` ограничивает страницу до 1–500 записей. `inspect` возвращает данные checkpoint и ревизию для
`cancel`. При потере ответа на `start` используйте `find` с тем же `--request-id`. `stash` импортирует
финализированные артефакты в локальное хранилище CAS. [Remote CLI](ru/remote-cli.md)

### Установка удалённого Host

```text
CMakeWorkflow remote provision plan|apply --host HOST --user USER
  --key-file LOCAL_KEY --host-key-sha256 SHA256:PIN --workspace REMOTE_DIR
  --publish-dir LOCAL_DIR --install-root REMOTE_DIR --rid linux-x64|linux-arm64
  [--port N] [--connect-timeout-seconds N]
```

`plan` проверяет исходную публикацию из `--publish-dir` и готовит план установки в `--install-root`.
`apply` дополнительно требует `--approval-sha256 DIGEST` из этого плана. Вывод обеих команд — JSON.
SSH-порт по умолчанию 22, тайм-аут подключения 15 секунд (1–3600). [Удалённая установка](ru/f2-remote-provisioning.md)

## CAS

```text
CMakeWorkflow cas STORE show OPERATION
CMakeWorkflow cas STORE fetch OPERATION ARTIFACT_ID OUTPUT
CMakeWorkflow cas STORE pin OPERATION
CMakeWorkflow cas STORE unpin OPERATION
CMakeWorkflow cas STORE drop-action ACTION_DIGEST
CMakeWorkflow cas STORE gc [--apply]
```

`STORE` — локальный каталог CAS. `show` выводит запись операции, `fetch` извлекает артефакт в `OUTPUT`.
`pin` и `unpin` меняют защиту записи от сборки мусора. `drop-action` удаляет ссылку на результат действия по
его digest. `gc` показывает кандидатов на очистку, `--apply` применяет её. [Artifact CAS](en/artifact-cas.md)

## Выход и ошибки

Для workflow run: 0 — succeeded, 1 — failed, 130 — пользовательская отмена. Общий entry point также использует 2 для
некоторых ошибок выбора команды. У tooling doctor код 2 означает неготовность инструментов; setup может вернуть 2 при
неудачной установке. Не переносите интерпретацию кода 2 на все команды.

`workflow report` с кодом 0 означает успешное чтение отчёта, а не успешность сохранённого workflow: проверяйте `status`
в данных. `history detect` может успешно вернуть `regression`; такой результат читается из JSON, а не из exit code.

Ожидаемые ошибки аргументов, файлов, инструментов, базы данных и транспорта выводятся кратко с exit code 1.
Исключения вне явной [классификации ошибок CLI](../Commands/CliErrors.cs) распространяются с диагностикой runtime.
Ожидаемые ошибки автоматического History import выводятся отдельно и не подменяют результат workflow.

Разделяйте stdout, stderr, spawn error, exit code и termination signal.
`--json` поддерживается только соответствующими обработчиками; у profile run/analyze/replay и DagFlow campaigns нет
общего JSONL-протокола завершения.
