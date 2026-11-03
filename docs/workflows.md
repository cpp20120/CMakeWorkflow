# Workflow-манифесты

> **Язык / Language:** [Русский](workflows.md) · [English](en/workflows.md) · [Documentation index](README.md)

Источник контракта — [WorkflowModel.cs](../CMakeWorkflow.CMake/WorkflowModel.cs).
Принимаются `schemaVersion: 1` и `2`; новые примеры используют версию 2.
Неизвестные JSON-поля отклоняются десериализатором.

## Рабочий пример

В репозитории есть [hello/workflow.json](../examples/hello/workflow.json) и
[CMakePresets.json](../examples/hello/CMakePresets.json) с preset `debug`:

```json
{
  "schemaVersion": 2,
  "contexts": {
    "hello": {
      "sourceDirectory": ".",
      "buildDirectory": "../../out/hello",
      "configuration": "Debug",
      "configurePreset": "debug",
      "mode": "managed",
      "provenancePolicy": "strict"
    }
  },
  "nodes": [
    { "id": "configure", "kind": "configure", "context": "hello" },
    { "id": "build", "kind": "build", "context": "hello", "target": "hello", "dependsOn": ["configure"] },
    { "id": "run", "kind": "run", "from": "build" },
    { "id": "test", "kind": "ctest", "context": "hello", "labelRegex": "smoke", "dependsOn": ["build"] }
  ]
}
```

Относительные `buildDirectory`, `sourceDirectory` и явно заданный
`workingDirectory` разрешаются относительно **файла manifest**. Это отличается
от относительных путей в CLI options, обычно разрешаемых от cwd процесса.

## Контексты

| Поле | Значение |
|---|---|
| `buildDirectory` | Обязательный build tree |
| `sourceDirectory` | Source tree; нужен для configure |
| `configuration` | Запрошенная конфигурация, например Debug или Release |
| `configurePreset` | Буквальное имя CMake configure preset; CLI не выводит его из cache |
| `generator` | Ожидаемая идентичность generator; не способ передать `-G` |
| `mode` | `observe`, `adopt` или `managed` |
| `provenancePolicy` | `strict` по умолчанию либо `allowUnverified` |

`observe` предназначен для наблюдения. `adopt` явно разрешает использовать
существующее дерево; при неизвестном provenance обычно нужен
`allowUnverified`. Эта политика не разрешает известные конфликты source,
generator или configuration.

`managed` связывает работу с контролируемым configure provenance. Для создания
или изменения конфигурации нужен явный узел `configure` и корректные зависимости
от него. CLI не добавляет configure автоматически. Если `mode` не указан,
planner выводит managed при выбранном configure-узле либо существующей записи
managed configure; иначе использует observe.

## Узлы

| `kind` | Контракт |
|---|---|
| `inspect` | Читает состояние указанного `context` |
| `configure` | Явно вызывает CMake configure для `context` |
| `build` | `context` + `target`: native executable или library; не utility target |
| `invoke` | `context` + `target`: намеренный запуск произвольной native target, включая custom/aggregate |
| `run` | `from` ссылается на build-узел; запускает его проверенный executable |
| `profile` | `from` ссылается на build-узел; выполняет capture через profiling backend |
| `ctest` | `context`, необязательные `testRegex`/`labelRegex`; нужен build/invoke-предок в том же tree/configuration |

`from` у run/profile автоматически добавляет зависимость от build. У таких
узлов не задаются собственные `context` и `target`.

Общие поля: `id`, `dependsOn`, `resources`, `exclusive`, `timeoutSeconds`.
ID уникальны и состоят из ASCII-букв, цифр, `.`, `_`, `-`, кроме `.` и `..`.
Timeout, если задан, должен быть положительным и не превышать 30 дней.

`arguments` — массив отдельных аргументов executable. `environment` содержит
переопределения окружения, `workingDirectory` — рабочий каталог операции.
У build/invoke/inspect/configure эти run-параметры запрещены. Для CTest не
принимаются `arguments`, `target` и `from`; выбор тестов задаётся regex-полями.

`options` разрешён только для profile. Реализация читает `backend`, `mode`,
`captureTimeoutSeconds`; точный поддерживаемый режим зависит от backend.

Необязательный `harnessDirectory` допустим только у invoke: путь относительно
build tree, без абсолютного пути и сегментов `.`, `..` или пустых компонентов.
Требования к свежести и содержимому описаны в
[документе о harness](profiling-and-campaigns.md#dagflow-harness).

## Артефакты и обязательные условия исполнения

В schema v2 узел может объявить необязательные `inputs`, `outputs` и
`constraints`; старые manifests остаются совместимыми. Вход `path` разрешается
относительно manifest. `sha256` фиксирует ожидаемое содержимое; без него
движок записывает наблюдаемый хеш. Вход проверяется до и после операции.
`snapshot:true` сохраняет проверенную копию файла или каталога в run directory.
Символические ссылки и reparse points в объявленных артефактах отклоняются.

Вход `{"from":"producer","artifact":"name"}` ссылается на именованный
`outputs` предыдущего узла и автоматически добавляет DAG-зависимость.
Потребитель повторно сверяет сохранённый хеш. Если дополнительно указан
`path`, содержимое этого пути тоже должно совпасть с выходом производителя.
У выхода обязательны уникальное `name` и `path`; по умолчанию путь относится
к manifest, а `"root":"run"` относит его к каталогу запуска. После успешной
операции движок требует существование выхода и записывает SHA-256 в `run.json`
и events. `"fresh":true` запрещает заранее существующий путь; для обычных
инкрементальных CMake outputs его не задают. При resume сохранённые артефакты
сверяются повторно.
Прерванный узел с объявленными артефактами или условиями нельзя вручную
пометить `succeeded`: такое решение не восстановит необходимые проверки.
Используйте существующий протокол явного отказа или повторного запуска.

`constraints.cpuSet` поддерживается для узлов `run` и `profile` и задаёт
обязательный список CPU, например `"2,4-5"`.
На Linux проверяется разрешённый набор и target запускается через `taskset`
с установкой affinity до `exec`; фактические предпосылки записываются в
журнал. На платформе без такой гарантии узел отклоняется. Для `profile`
можно потребовать `constraints.profiler` с точными `backend`, `mode` и
необязательным `eventSource`; они сверяются с явно заданными profile options
и возможностями backend до capture. Недоступность прав или событий во время
записи остаётся явной ошибкой.

Это гарантии проверок в моменты допуска и завершения, а не постоянная
блокировка файлов, герметичность среды, стабильная частота CPU или собственная
оценка актуальности CMake target. Предметная семантика Relacy и отчётов профиля
остаётся вне общего workflow engine.

## План и запуск

```sh
CMakeWorkflow workflow plan examples/hello/workflow.json --only run --only test --json
CMakeWorkflow workflow run examples/hello/workflow.json --only run --only test --jobs 2 --results-dir out/results --json
```

`--only` включает prerequisite closure. Это не «запустить узел, игнорируя
зависимости». Whole-graph validation по-прежнему отклоняет структурные ошибки
в других ветвях manifest.

Plan не запускает CMake configure/build. Проверяйте одновременно process exit
code, `valid` и diagnostics. Успешный JSON parse не означает допустимый план,
а допустимый план не гарантирует успешного будущего выполнения.

`exclusive` действует внутри workflow. Для внешних инструментов, которые не
соблюдают leases CMakeWorkflow, это не запрет запускать работу параллельно.
