# Результаты, протокол и History

## Версии контрактов

Это независимые значения, их нельзя сравнивать как одну «версию продукта»:

| Контракт | Текущее значение |
|---|---|
| Входной workflow manifest | 1 или 2 |
| `WorkflowPlanView`, `WorkflowEvent`, target resolution | `schemaVersion: 1` |
| Сохраняемый `WorkflowReport` (`run.json`) | `schemaVersion: 1` |
| SQLite schema | `PRAGMA user_version = 4` |

Проверяйте схему конкретного ответа на стороне интеграции. Версия установленного
бинарника и наличие дополнительных команд — отдельная задача совместимости.

## План

[WorkflowPlanView](../CMakeWorkflow.CMake/WorkflowProtocol.cs) содержит
`schemaVersion`, `workflow`, `manifestHash`, `valid`, `nodes`.

У элемента `nodes`: `id`, `kind`, `target`, `buildDirectory`, `configuration`,
`dependencies`, `observedState`, `diagnostics`, `resources`, `exclusive`.
Diagnostic содержит `severity` и `message`; planner использует `Observed`,
`Unverified`, `Runtime` и `Error`.

Смотрите `valid`, diagnostics и exit code независимо. При некоторых структурных
ошибках manifest команда завершается до формирования JSON-плана. Отсутствие
JSON тоже должно корректно отображаться как ошибка.

## Живые события

`workflow run FILE --json` передаёт JSONL в stdout, diagnostics — в stderr.
У события следующие поля:

| Поле | Смысл |
|---|---|
| `schemaVersion` | Версия именно event-протокола |
| `sequence` | Последовательность данного run, начиная с 1 |
| `timestamp` | Время события |
| `type` | `workflow_started`, `workflow_resumed`, `node_started`, `node_finished`, `workflow_finished` |
| `runDirectory` | Фактический каталог результата |
| `nodeId`, `nodeKind` | Идентичность узла либо null для события workflow |
| `status`, `exitCode`, `error` | Состояние/результат, отдельные значения могут быть null |
| `artifacts` | Массив объектов `type`, `path`, `sha256`; hash может отсутствовать |

Статусы узлов: `pending`, `running`, `succeeded`, `failed`, `blocked`, `canceled`,
`needs_attention`, `outcome_unknown`. Итог workflow: `succeeded`, `failed`,
`canceled`, `needs_attention` или `outcome_unknown`; промежуточный — `running`.
У `workflow_finished` нельзя требовать ненулевое поле `exitCode`: authoritative
результат определяется сохранённым отчётом и результатом процесса.

Строки записываются в `events.jsonl` до вывода в stdout. Недоступность event
journal диагностируется отдельно. Клиент должен выдерживать произвольные
границы UTF-8 chunks, LF/CRLF, последнюю строку без newline и обрыв потока.
Пропущенное завершение или несовместимая schema — не успешный run.

## Сохранённое состояние

**`run.json` — авторитетная модель workflow.** JSONL служит уведомлениями и
журналом; это не механизм resume/retry. После завершения вызывайте:

```sh
CMakeWorkflow workflow report RUN_DIRECTORY --json
```

Корневые поля отчёта: `schemaVersion`, `workflow`, `manifestHash`, `startedAt`,
`finishedAt`, `status`, `nodes`, `error`, `environment`, `environmentAtEnd`,
`buildEnvironments`. Полная модель `NodeResult`, включая observations, artifact
identity, `outputs`, argv и timing, находится в
[WorkflowRunner.cs](../CMakeWorkflow.CMake/WorkflowRunner.cs).

Пути `stdout`, `stderr` и отдельные outputs в отчёте могут быть относительными
к run-каталогу; protocol artifact paths формируются как полные пути. Не заменяйте
реальные ссылки догадками по имени target или суффиксу `.exe`. Перед открытием
артефакта проверяйте его наличие. При отсутствии/повреждении отчёта показывайте
это явно; наличие каталога ещё не означает завершение.

Примеры артефактов: `run.json`, `events.jsonl`, node logs, profiling manifests,
`*.results.json`, raw traces, `analysis.json`, `comparison.json`,
`counter-comparison.json` и opt-in snapshots harness. Не каждый run создаёт
все эти файлы.

## Прерванное исполнение и resume

После устранения причины `needs_attention` можно явно запросить продолжение:

```sh
CMakeWorkflow workflow resume RUN_DIRECTORY
# Эквивалент с явным исходным manifest:
CMakeWorkflow workflow run FILE --resume-dir RUN_DIRECTORY
```

Проверяются идентичность manifest и графа, сохранённые успешные build observations
и artifacts. Успешные узлы повторно не исполняются. Resume не является общим
retry для failed/canceled запусков. Отчёт содержит `findings`, `resumeCount`
и `recoveryPhase`; JSONL не используется для восстановления состояния.

Перед запуском операций scheduler атомарно заменяет `run.json`, сохраняя
допущенные узлы как `running`. Это намерение исполнить операцию, а не доказательство
её фактического старта. Если runner аварийно завершился, процесс мог как не
успеть запуститься, так и уже произвести внешние изменения.

При resume совместимого checkpoint со статусом `running`, незавершёнными узлами
или фазой recovery `resuming`/`running` runner **не запускает операции**:

- сохраняет статус workflow `outcome_unknown` и `recoveryPhase: interrupted`;
- переводит только узлы `running` в `outcome_unknown`, сохраняя остальные состояния;
- не увеличивает `resumeCount` и не выдумывает время завершения;
- возвращает exit code **4**. Повторный resume отклоняется с exit code **1**.

Эта классификация сохраняется в `run.json`; новые события JSONL при ней не
создаются. Читайте отчёт через `workflow report`, даже если stdout пуст.
Несовместимый manifest/граф или ошибка preflight могут отклонить resume раньше,
не изменяя checkpoint. Lease run-каталога не позволяет классифицировать активный
запуск вторым runner.

Для `outcome_unknown` нужна ручная проверка дочерних процессов, логов и внешних
изменений. Команда `workflow reconcile` позволяет принять результат либо явно
разрешить повтор с обязательной причиной, проверкой hash checkpoint и аудитом.
После решения требуется отдельный `resume`. Подробности команд, persistence
barriers и платформенных ограничений — в [Recovery](recovery.md).

## Локальная History

[HistoryStore](../CMakeWorkflow.History/HistoryStore.cs) индексирует сохранённые
результаты. SQLite — восстановимый индекс, исходные run-каталоги остаются
источником данных. Scheduler не зависит от успешности индексации. Ошибка импорта
сообщается в stderr и не должна переопределять exit status workflow.

Автоматически индексируются завершённые workflow, в том числе failed/canceled,
а профильные результаты доступны для импорта после их сохранения. Есть ручной
`history import PATH --recursive`. Активные отчёты пропускаются. Mutation и
weak-memory reports требуют completion marker с hash; старый частичный JSON
без такого маркера нельзя считать завершённой кампанией.

Default database: `CMAKEWORKFLOW_HISTORY_DB`, иначе
`LocalApplicationData/CMakeWorkflow/history.sqlite`. Локальная host identity
хранится под каталогом состояния CMakeWorkflow; `CMAKEWORKFLOW_STATE_DIR`
позволяет изменить его расположение. Это локальная случайная identity,
не hostname и не OS machine ID.

Импорт транзакционный и идемпотентный. Каталог поддерживает миграции схемы
0→1→2→3→4 и версию indexer для обновления derived measurements. Перенос run в
другой путь и повторный импорт создают другую локальную run identity.
Не обращайтесь к таблицам SQLite напрямую из плагина: используйте History CLI.

## Сравнение и статистика

`history compare` возвращает descriptive comparison: количество samples,
median, min/max, mean, population standard deviation и допустимые deltas.
Совпадение названия метрики само по себе недостаточно: проверяются единицы,
method, workload, variant/scenario, host и зафиксированные характеристики
окружения/toolchain. `missing`, `incompatible`, `unverified` объясняют, почему
численное изменение не следует интерпретировать как сравнимый результат.

`history detect` выполняет односторонний permutation test разницы медиан:

- Нужны явные metric, worse-direction и minimum practical effect.
- Для проверяемой пары нужны успешные, доступные и сопоставимые runs, как минимум
  10 различных индивидуальных samples с каждой стороны для variant/scenario.
- Используются exact permutations для малых наборов и 9 999 deterministic
  Monte Carlo permutations для больших; применяется поправка Holm.
- `regression` требует adjusted p-value ≤ alpha и ухудшение не меньше порога.
- `insufficient`, `unverified` и `inconclusive` не означают отсутствие регрессии.

Фильтры variant/scenario меняют семейство проверок для Holm correction. Графики
raw samples и совпадение fingerprint не доказывают независимость измерений,
отсутствие thermal drift или фоновой нагрузки. Подробности реализации:
[HistoryDetector.cs](../CMakeWorkflow.History/HistoryDetector.cs).
