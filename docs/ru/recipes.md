# Типизированные рецепты workflow

**[Русский](recipes.md) · [English](../en/recipes.md) · [Содержание](README.md)**

Рецепт — типизированные входные параметры builder, а не новый исполнитель или альтернативный формат запуска. У
`BenchmarkRecipeV1` собственные
`kind: "benchmark"` и `schemaVersion: 1`; результат — обычный
`WorkflowDocument` версии 2. [JSON Schema](../../schemas/recipes/benchmark.v1.schema.json)
описывает форму входа, а builder дополнительно проверяет пути на текущем хосте. Profile, Test Matrix и Fuzz смогут
получить независимые контракты позже; существующий профильный CLI не меняется.

## Генерация и запуск

Из каталога проекта с **уже настроенным** CMake build tree:

```sh
CMakeWorkflow workflow generate benchmark \
  --build-dir out/build/release --target my_benchmark --config Release \
  --repeats 10 --timeout-seconds 300 --output workflows/chain.json \
  -- --benchmark chain --workers 2
CMakeWorkflow workflow plan workflows/chain.json --json
CMakeWorkflow workflow run workflows/chain.json --jobs 8 --results-dir out/results --json
CMakeWorkflow workflow report RUN_DIRECTORY --json
```

`RUN_DIRECTORY` берётся из события `runDirectory` в JSONL. Генерация только записывает манифест: configure, build, тесты
и benchmark не запускаются. Относительные `--build-dir` и `--output` считаются от cwd процесса CLI; в самом манифесте
путь build tree абсолютный. Все значения после `--`
попадают в argv программы отдельными строками без shell parsing. Обязательны
`--build-dir`, `--target`, `--repeats`, `--timeout-seconds`, `--output`. Существующий файл не перезаписывается.
Финальный файл появляется только после полной записи временного файла в том же каталоге.

Builder создаёт один контекст `adopt`/`allowUnverified`, один узел `build` и узлы `timing_001`…`timing_N`. Каждый
timing-узел зависит от предыдущего и использует ресурс `measurement-host`; даже при `--jobs 8` измерения не
перекрываются. Узлы используют `from: "build"`, поэтому существующий runner сохраняет проверку сборки и идентичности
executable. Конфликт известного provenance planner по-прежнему отвергает. Измеряется время выполнения процесса без
профилировщика, а не внутренние метрики benchmark. Десять повторов сами по себе не доказывают регрессию; History
сравнивает их отдельно.

## Остальные рецепты

У каждого JSON-рецепта собственные `kind` и `schemaVersion: 1`. Все builders создают **обычный `WorkflowDocument` v2**.
JSON-рецепт передают в
`workflow generate`, а полученный манифест — в существующие `workflow plan`
и `workflow run`. Генерация ничего не конфигурирует и не исполняет. После любой генерации используйте показанную выше
цепочку `workflow plan`,
`workflow run` и `workflow report`. Относительные пути прямого CLI считаются от cwd; относительные пути в JSON — от
каталога файла рецепта. Host принимает абсолютные пути внутри workspace. Существующий манифест не перезаписывается.

### Profile

Контракт: [profile.v1.schema.json](../../schemas/recipes/profile.v1.schema.json). Поле `platform` обязательно: `linux`,
`windows` или `macos`. Оно определяет граф при генерации независимо от ОС клиента. `backend: auto` превращается
соответственно в `perf`, `etw` или `instruments`; запуск на другой платформе не меняет capture автоматически. Старые
`profile run` и `profile/prepare`
сохраняют контракт.

```sh
CMakeWorkflow workflow generate profile --build-dir out/build/release \
  --target my_benchmark --config Release --platform linux --backend auto \
  --mode cpu --repeats 3 --timeout-seconds 300 --output workflows/profile.json \
  -- --case chain
CMakeWorkflow workflow plan workflows/profile.json
CMakeWorkflow workflow run workflows/profile.json --results-dir out/profile-results
CMakeWorkflow profile analyze RUN_DIRECTORY
```

Вместо `--build-dir` можно повторять `--variant name=DIR`. JSON позволяет указать `sourceDirectory` и `configurePreset`
для одного явно управляемого варианта:

```json
{"kind":"profile","schemaVersion":1,"variants":[{"name":"main","buildDirectory":"../out/build/release"}],"target":"my_benchmark","configuration":"Release","repeats":3,"timeoutSeconds":300,"arguments":["--case","chain"],"backend":"auto","mode":"cpu","platform":"linux"}
```

Существующий profile builder создаёт build, обычные timing-запуски и изолированные capture-узлы. На Linux режим CPU
добавляет отдельный проход счётчиков; diagnostic может добавить scheduler trace. Профильные узлы используют
`measurement-host` и зависимости измерений. Анализ сохранённых трасс возможен через `profile analyze`; CLI также
анализирует успешные captures после `workflow run`, как и раньше.

### Test Matrix

Контракт: [test-matrix.v1.schema.json](../../schemas/recipes/test-matrix.v1.schema.json). Группа задаёт ровно один
`testRegex` или `labelRegex`.

```sh
CMakeWorkflow workflow generate test-matrix --build-dir out/build/release \
  --target my_tests --config Release --label unit --label integration \
  --order parallel --timeout-seconds 300 --output workflows/tests.json
```

```json
{"kind":"test-matrix","schemaVersion":1,"buildDirectory":"../out/build/release","target":"my_tests","configuration":"Release","groups":[{"name":"unit","labelRegex":"unit"},{"name":"integration","testRegex":"integration_.*"}],"executionOrder":"parallel","timeoutSeconds":300}
```

Граф: `build_tests → {ctest_unit, ctest_integration}`. `parallel` разрешает параллельную работу независимых групп;
`sequential` связывает их в порядке рецепта. У каждой CTest-группы есть build-предок в том же контексте. Timeout
относится к процессу CTest, не к каждому тесту. Максимум 64 группы. Выбор и запуск тестов остаются обязанностью CTest.
Ошибка build блокирует зависимые группы.

### Build Matrix

Контракт: [build-matrix.v1.schema.json](../../schemas/recipes/build-matrix.v1.schema.json).

```sh
CMakeWorkflow workflow generate build-matrix \
  --variant debug=out/build/debug --variant release=out/build/release \
  --target my_tests --target my_benchmark --output workflows/builds.json
```

```json
{"kind":"build-matrix","schemaVersion":1,"variants":[{"name":"debug","buildDirectory":"../out/build/debug","configuration":"Debug"},{"name":"release","buildDirectory":"../out/build/release","configuration":"Release"}],"targets":["my_tests","my_benchmark"]}
```

Каждый вариант создаёт отдельный принятый (`adopt`) контекст, каждая пара вариант/цель — build-узел (`build_debug_001` и
т. д.). Имя варианта не меняет флаги, компилятор или preset. Деревья должны быть уже настроены. Независимые деревья
могут собираться параллельно; runner сериализует операции с одним физическим деревом. Буквальные дубли путей отвергаются
builder, алиасы контролирует planner. Лимит — 512 build-узлов. Managed configure в этой версии не генерируется.

### Benchmark Matrix

Контракт: [benchmark-matrix.v1.schema.json](../../schemas/recipes/benchmark-matrix.v1.schema.json). Оси удобно задавать
типизированным JSON:

```json
{"kind":"benchmark-matrix","schemaVersion":1,"variants":[{"name":"o3","buildDirectory":"../out/build/o3","configuration":"Release"},{"name":"lto","buildDirectory":"../out/build/lto","configuration":"Release"}],"target":"my_benchmark","repeats":10,"timeoutSeconds":300,"fixedArguments":[],"axes":[{"name":"case","flag":"--benchmark","values":["chain","independent"]},{"name":"workers","flag":"--workers","values":["1","2","4","8","16"]}],"executionOrder":"round-robin"}
```

```sh
CMakeWorkflow workflow generate benchmark-matrix \
  --recipe recipes/benchmark-matrix.json --output workflows/matrix.json
CMakeWorkflow workflow plan workflows/matrix.json
CMakeWorkflow workflow run workflows/matrix.json --jobs 8 --results-dir out/results
CMakeWorkflow workflow report RUN_DIRECTORY --json
```

Пример даёт два build-узла и 200 timing-узлов. Порядок осей и значений определяет индексы декартовых комбинаций;
последняя ось меняется быстрее. Флаг и значение передаются как два точных argv-элемента. ID вида
`timing_VARIANT_COMBINATION_REPETITION`, например `timing_o3_0001_001`. ID вместе с argv манифеста определяет сценарий;
History пока не извлекает структурированные оси из такого имени.

Порядок `sequential`: вариант → комбинация → повтор. `round-robin`:
повтор → комбинация → вариант, причём в чётных повторах порядок вариантов обратный. Все timing-узлы образуют одну
цепочку и используют
`measurement-host`, поэтому даже `--jobs 8` не даёт перекрытия измерений. До создания графа проверяется размер
произведения; предел — 4096 узлов. Измеряется длительность процесса, не внутренние метрики benchmark и не статистическая
значимость. Нативная сборка остаётся за CMake.

## Host preparation

Существующий `profile/prepare` не меняется. Новые capability:

| Рецепт           | Метод                     |
|------------------|---------------------------|
| Profile          | `profileRecipe/prepare`   |
| Test Matrix      | `testMatrix/prepare`      |
| Build Matrix     | `buildMatrix/prepare`     |
| Benchmark Matrix | `benchmarkMatrix/prepare` |

Каждый метод принимает один типизированный объект
`{"requestId":"32-hex","outputPath":"/workspace/workflows/name.json","recipe":{...}}`. Ответ содержит
`schemaVersion: 1`, `manifest` и `manifestSha256` точных сохранённых байтов. Host проверяет границы workspace и
commit-once binding. При потере ответа повторяют тот же запрос с тем же ID; конфликтующие параметры или занятый выходной
файл отвергаются. Подготовка не означает запуска. Клиент отдельно вызывает `workflow/plan`, затем durable
`workflow/start` с возвращённым manifest/hash, отдельным `clientRequestId`
и каталогом результатов. IDE отправляет поля рецепта, а не массив
`WorkflowNode[]`.

Неверные kind/version, отсутствующие поля, неизвестные JSON-свойства, дубли ID, неверный regex, несовместимые
backend/platform, дубли build tree и чрезмерные графы отвергаются до появления финального манифеста. JSON Schemas
описывают вход; типизированные builders проверяют семантику и пути.

Fuzz, mutation и weak-memory остаются отдельными типизированными сервисами. Возможный путь интеграции описан
в [заметке о будущих рецептах кампаний](campaign-recipe-future.md).

## Host RPC

После согласования capability `benchmark/prepare` клиент передаёт один типизированный объект запроса в позиционном
массиве JSON-RPC:

```json
{
  "requestId": "0123456789abcdef0123456789abcdef",
  "outputPath": "/workspace/workflows/chain.json",
  "recipe": {
    "kind": "benchmark",
    "schemaVersion": 1,
    "buildDirectory": "/workspace/out/build/release",
    "target": "my_benchmark",
    "configuration": "Release",
    "repeats": 10,
    "timeoutSeconds": 300,
    "arguments": ["--benchmark", "chain", "--workers", "2"]
  }
}
```

Ответ: `{ "schemaVersion": 1, "manifest": "/workspace/workflows/chain.json",
"manifestSha256": "..." }`. `requestId` содержит 32 hex-символа. Все пути абсолютные и ограничены workspace, заданным в
`initialize`. Повтор того же запроса с тем же ID возвращает уже сохранённый результат; другие параметры или путь с тем
же ID дают `request_id_conflict`. Параллельные запросы не перезаписывают манифест. SHA-256 вычисляется от точных
сохранённых байтов.

Если ответ потерян, повторите **тот же** `benchmark/prepare` с прежним ID. Подготовка не означает принятия запуска. VS
Code может отправлять параметры формы в Host, затем передать путь в `workflow/plan`. Для исполнения клиент отдельно
вызывает durable `workflow/start` с возвращёнными manifest/hash, новым `clientRequestId` и каталогом результатов.
Статус, переподключение, отмена и recovery остаются в существующем API операций.
