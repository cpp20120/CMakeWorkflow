# ADR-0003: единый application API CMakeWorkflow

Дата: 2026-10-30. Статус: **предложение**, RPC ниже не реализованы, если явно не указано обратное. Дополняет [ADR-0002](0002-local-host-ipc.md).

> Реализация начата с этапа 1: read-only application API. Фактические методы, ограничения и проверки описаны в [Application API](../application-api.md). Durable acceptance и workflow mutation RPC пока не реализованы.

## Зачем

Есть workflow engine, отчёты и recovery, History, profiling/campaigns, Tooling и подготовка host environment. Сейчас часть доступна через typed services, часть через CLI handlers, совмещающие parsing, выполнение и Console output. Плагину приходится знать разные команды, формы результатов и правила завершения.

Нужен application API с общими правилами вызова и lifecycle, сохраняющий предметные

контракты. Это граница над существующими подсистемами, а не новый scheduler или универсальная система исполнения произвольных команд.

## Слои
```text
CLI ───────┐
           ├─ Local Host / JSON-RPC ─ Application services ─ Existing engines
VS Code ───┘                                  ▲
CLI standalone ───────────────────────────────┘
```
CLI сохраняет самостоятельный режим. Host вызывает те же typed services внутри процесса, а не собственный CLI через subprocess. Разбор argv и текстовый вывод

остаются в CLI. Переносить следует конкретные handlers по одному; не создавать

общий DI framework, второй runtime или новый формат workflow.

Транспорт сохраняется: StreamJsonRpc, local Unix sockets / named pipes, System.Text.Json source generation и AOT-проверки. VS Code остаётся IDE-адаптером.

## Поверхность API

| Группа | Предлагаемые методы | Основа в текущем коде |
|---|---|---|
| Context | `context/inspect`, `target/resolve` | CMakeInspection, существующие CLI команды |
| Workflow | `workflow/plan`, `workflow/start`, `workflow/resume`, `workflow/reconcile` | WorkflowPlanner, WorkflowRunner, WorkflowReconcileCommand |
| Operations | `operations/get`, `operations/find`, `operations/cancel`, `operations/events` | Новый минимальный lifecycle adapter; не DAG scheduler |
| Reports | `runs/get`, `runs/artifacts`, `artifacts/read` | WorkflowReport, реальные artifact references |
| History | `history/runs`, `history/show`, `history/metrics`, `history/query`, `history/compare`, `history/detect`, `history/import` | HistoryStore и текущие handlers |
| Profiling | `profile/run`, `profile/analyze`, `profile/replay` | ProfileCommand, ProfileAnalyzer, ProfileReplayCommand |
| Campaigns | Методы для конкретных поддержанных кампаний | CampaignCommand; добавлять после typed service extraction |
| Tooling | `tooling/doctor`, `tooling/planFix`, `tooling/applyFix` | **Эти RPC уже существуют** |
| Environment | `environment/plan`, `environment/apply` | **Эти RPC уже существуют**, HostPreparation |

`initialize`, `tooling/progress` и `tooling/fixProgress` также уже существуют.

Фактический текущий wire contract — [HostProtocol.cs](../../CMakeWorkflow.Host/HostProtocol.cs).

Список выше — целевая поверхность, не обещание доступности этих RPC сегодня.

Read-only History и `history/import` имеют разную mutation policy.

Не вводить `execute(command, args)`: он обходил бы typed contracts, approvals, workspace isolation и capability negotiation. Backend capabilities profiling следует возвращать из существующего discovery; `unverified` не означает `ready`.

## Долгие операции

`workflow/start` принимает typed request: manifest, jobs, results directory,

ожидаемый manifest hash и `clientRequestId`. Возвращает operation reference после фиксации принятия запроса, до длительной работы. JSON-RPC request ID и operation ID различны: RPC request может закончиться, а workflow продолжить выполнение.

Общий operation reference содержит `operationId`, `kind`, ownership/lifetime и ссылку на результат, если он уже существует. Domain result остаётся типизированным: WorkflowReport не превращается в произвольный dictionary, а FixResult не выдаётся

за workflow. Progress/transport state не подменяют статус сохранённого результата.

Для первого workflow API:

- host-owned операции переживают разрыв клиента; cancellation — отдельный запрос;
- закрытие VS Code не должно молча уничтожать Host с активными host-owned runs;
- shutdown policy Host и существующая модель per-folder Hosts должны быть

согласованы ****до**** включения такого ownership;

- после restart Host читает checkpoint, но не повторяет работу автоматически;
- исчезновение process handle не означает успешный результат;
- `operations/get` возвращает фактический report и `outcome_unknown`, если он уже установлен recovery; не выполняет скрытый resume/reconcile;

- `operations/events(afterSequence, limit)` читает ограниченную порцию событий, сообщает о пропусках/обрыве; клиент перечитывает snapshot;

- notifications ускоряют UI, но не являются единственным способом узнать итог.

У существующих Doctor/Fix/environment операций lifetime сейчас connection-bound.

Не менять его молча и не обещать durable installation recovery. Для расширения

нужны capability и отдельные тесты partial installs, disconnect и restart.

## Защита от повторного принятия запросов

До внешнего исполнения принятие `clientRequestId` и digest typed request должно быть записано в durable metadata run. Повтор того же ID с тем же digest возвращает существующий operation reference; с другим digest — конфликт. Lock покрывает проверку и создание записи. Один in-memory dictionary этого не обеспечивает.

Если ответ потерян, клиент использует `operations/find(clientRequestId)` и затем

`operations/get`. Он не отправляет новый start с новым ID автоматически.

Индекс поиска может быть восстановимым cache поверх run metadata; History SQLite не становится prerequisite для scheduler. Layout и атомарное принятие запроса нужно реализовать и проверить перед публикацией `workflow/start`.

Это дедупликация запросов. Она не даёт exactly-once внешним процессам и не отменяет

`outcome_unknown` / ручное решение.

## Snapshot, решения и ошибки

`runs/get` возвращает report и revision — SHA-256 исходных байтов checkpoint.

`workflow/reconcile` использует текущую семантику CLI: node, decision, reason, expected revision, подтверждение остановки старых процессов. Решение атомарно

сохраняется с аудитом и ничего не запускает; resume вызывается отдельно.

В `workflow/resume` для RPC также добавить expected revision, чтобы старый UI

не продолжил другой checkpoint. Для Fix сохраняются single-use planId, expiry

и revalidation; для environment — approval hash и явное разрешение системных

изменений. Согласие на workflow не является согласием на установку пакетов.

Разделять:

- transport/protocol errors: несовместимая версия, повреждённый запрос;
- domain refusals: stale checkpoint/plan, busy, invalid workflow, unsupported backend, отсутствующая возможность или требуемое подтверждение;

- результат выполненной операции: failed, canceled, needs_attention, outcome_unknown и другие статусы существующей предметной модели.

Domain refusal должен иметь стабильный code и пригодный для UI message; подробности в typed data. Ошибка сборки — результат workflow, а не JSON-RPC internal error.

Точные error codes фиксировать вместе с DTO и contract tests, не парсить сообщения.

## Пути, артефакты и изоляция

Workspace задаётся при initialize, операции принадлежат этому workspace.

Пути относятся к filesystem Host, включая Remote SSH/WSL/containers. Нельзя использовать cwd/env одного клиента как глобальное состояние процесса.

Доступ к чужим run directories и внешним artifact paths требует явной политики allowed roots, с проверкой canonical paths/symlinks. `artifacts/read` принимает выданную сервером artifact reference и ограниченный byte range, не произвольный путь для чтения всего диска. Local IPC не отменяет необходимость этих проверок.

## Версии и тесты

Сохранять независимые версии Host protocol, workflow manifest, отчётов и событий.

`initialize` объявляет реальные capabilities; клиент не делает выводы только из номера версии бинарника. Breaking изменения требуют явной совместимости/нового контракта. Новые DTO входят в source-generated metadata и TypeScript runtime validation; никаких generic RPC objects, отражения или dynamic proxies.

Обязательны тесты двух клиентов, разных workspaces, loss of response, повторного request ID, cancel versus disconnect, Host restart, stale revision, artifact path escape, bounded events, CLI/RPC равенства результатов и NativeAOT cross-language IPC.

## Порядок реализации

1. Выделить typed application results из Console-oriented handlers; CLI остаётся совместимым. Подключить read-only context/plan/report/history.

2. Реализовать и проверить принятие start, ownership Host, state query и reconnect.

Только после этого открыть workflow mutation RPC.

3. Добавить cancel/resume/reconcile с revision checks и существующим аудитом.
4. Перенести profiling и конкретные campaigns, сохраняя их result schemas.
5. Перевести GUI по capability negotiation, оставив CLI fallback для старого Host.

Read-only вертикальный срез — ближайший шаг. Не мигрировать всё одновременно и

не объявлять durable lifecycle уже существующих Fix/environment операций готовым.

## Durable acceptance: commit point

Для `workflow/start` вводится durable acceptance: состояние `accepted` означает принятие запроса, но не начало исполнения. `operationId` идентифицирует операцию, а не OS-процесс. JSON-RPC request ID, `clientRequestId` и `operationId` имеют разную семантику.

**Идентичность для дедупликации:** `(workspaceId, clientRequestId)`. Digest рассчитывается по канонизированному typed request, включая зафиксированный `expectedManifestHash` (и остальные поля, влияющие на семантику). Он **не** вычисляется заново из текущего файла manifest при повторе. Канонизация, формат digest и версия схемы должны быть определены контрактом.

**Порядок принятия:**

1. Проверить handshake, workspace binding, права доступа и синтаксис envelope, достаточный для определения `clientRequestId` и digest запроса. Не читать и не валидировать текущее содержимое manifest на этом этапе.
2. Захватить межпроцессный lock общего namespace принятия и восстановить/проверить обнаруженные опубликованные acceptance records до решения о новом принятии.
3. Найти запись по `(workspaceId, clientRequestId)`:
   - Совпадает digest — вернуть **существующий** operation reference, даже если исходный manifest с тех пор изменился или исчез.
   - Digest отличается — вернуть `request_id_conflict`; не обращаться к текущему manifest.
   - Запись повреждена, неоднозначна или recovery не завершилось — отказать с `acceptance_recovery_required`, не принимать второй operation ID.
4. **Только для нового ID** проверить ожидаемый hash manifest, DAG, ограничения исполнения и allowed roots. Зафиксировать исходный абсолютный каталог разрешения относительных путей (`manifestBaseDirectory`) и его проверенную canonical identity в контексте workspace. Создать immutable snapshot исходных байтов manifest с content digest; перенос файла snapshot **не изменяет** `manifestBaseDirectory` и семантику относительных путей.
5. Durably сохранить immutable snapshot **до публикации** acceptance, которая на него ссылается: выполнить необходимые file/directory persistence barriers, затем проверить digest и доступность snapshot. Acceptance metadata содержит snapshot digest, неизменяемый `manifestBaseDirectory`, `operationId`, `workspaceId`, `clientRequestId`, request digest, исходную revision и `accepted / not_dispatched`. Создать staging metadata в том же filesystem, синхронизировать файл, атомарно опубликовать запись и выполнить поддерживаемые platform-specific persistence barriers, включая directory metadata при необходимости.
6. Только после подтверждённого durable commit разрешить переход runner к отдельному durable `dispatch_authorized` checkpoint. External dispatch запрещён до такого checkpoint.
7. Освободить lock и вернуть operation reference. Принятие не зависит от доставки RPC-ответа.

**Commit point** — завершение предусмотренных для платформы persistence barriers, подтверждающих публикацию валидных acceptance metadata. `rename` сам по себе не обязательно является durable commit.

**Recoverable publication:** если crash произошёл после атомарной публикации, но до подтверждения directory fsync (или эквивалентного barrier), найденный после restart файл сохраняет свой первоначальный `operationId`. Под lock Host проверяет целостность, схему, digest, привязку к workspace, наличие durably опубликованного snapshot, его digest и сохранённый `manifestBaseDirectory`, завершает доступные persistence barriers и восстанавливает индекс. До завершения этой процедуры новая операция с тем же `clientRequestId` запрещена. Повреждение или противоречие metadata — `acceptance_recovery_required`, а не молчаливое создание второго run.

Если опубликованная, но **ещё не durably committed** запись полностью потеряна при power loss и не обнаруживается, локальный файловый протокол не может гарантировать восстановление её ID. Однако до подтверждения commit внешний dispatch не допускается. Дедупликация гарантирована для durable acceptance и обнаруженных recoverable publications, а не для данных, потерянных до commit.

Индекс `clientRequestId → operationId` восстанавливается из durable run metadata. History SQLite не является обязательной зависимостью scheduler; in-memory dictionary не обеспечивает durable identity. Файловая политика атомарности/flush должна быть описана и протестирована для Linux и Windows. Lock обеспечивает сериализацию, но не заменяет persistence barriers.

**Инварианты:**

- Повтор с тем же ID и digest не зависит от изменений текущего manifest; права workspace проверяются всегда.
- Только новый request проходит текущую manifest validation.
- Acceptance не публикуется до durable snapshot, а перенос snapshot не меняет контекст разрешения относительных путей.
- Внешний процесс не запускается до committed acceptance и durable `dispatch_authorized`.
- `accepted / not_dispatched` восстанавливается без автоматического запуска.
- После `dispatch_authorized` отсутствие PID/handle не является доказательством отсутствия исполнения; применяется существующая recovery-семантика, включая `outcome_unknown`.
- Это дедупликация принятия запросов, а не гарантия exactly-once внешнего исполнения.

До реализации и fault-injection проверки протокола `workflow/start` не объявляется в Host capabilities.

## Связь acceptance metadata и runner checkpoint; явный resume

Acceptance record фиксирует request identity, content digest immutable manifest snapshot и исходный `manifestBaseDirectory` (абсолютный путь, после проверки canonical workspace binding). Runner checkpoint отдельно фиксирует допуск к исполнению, состояние узлов и результат. Между ними сохраняется проверяемая связь по `operationId`, `workspaceId`, snapshot digest и неизменяемому base directory. Изменение внешнего manifest после принятия не меняет уже принятую работу.

Planner/runner должны получать исходный base directory явно, а не вычислять его через `Path.GetDirectoryName(snapshotPath)`. Относительные ссылки в принятом JSON разрешаются относительно зафиксированного base directory; доступ к реальным файловым зависимостям во время исполнения по-прежнему проверяется по allowed roots и symlink policy. Snapshot manifest не означает snapshot всего source tree, CMake cache или toolchain. Если такие зависимости должны быть неизменными, для них требуется отдельная политика фиксации или проверка provenance; скрыто подменять их содержимое нельзя.

| Acceptance / runner | Интерпретация | Допустимое действие |
|---|---|---|
| `accepted / not_dispatched` | Есть committed acceptance, но **нет** durable разрешения на внешний dispatch | `workflow/resume(expectedRevision)` по явному запросу |
| `accepted / dispatch_authorized` | Допуск к исполнению мог быть использован | Только recovery-safe resume с проверкой checkpoint и custody; при неопределённости `outcome_unknown` |
| `accepted / running` | Есть подтверждённое исполнение | Наблюдение, отмена, штатный recovery |
| `accepted / needs_attention` | Известная причина остановки или блокировка выполнения; неизвестность исхода **не** установлена | Устранить причину и выполнить `workflow/resume(expectedRevision)` по существующим правилам recovery |
| `accepted / outcome_unknown` | Не установлен исход уже допущенного исполнения; повтор может вызвать дублирование эффекта | Только ручной `workflow/reconcile` с expected revision, reason и `confirm-stopped`, затем отдельный `workflow/resume` при необходимости |
| `accepted / terminal` | Результат durably зафиксирован | Только чтение результата; поздняя отмена ничего не меняет |

Для `accepted / not_dispatched` `workflow/resume` сверяет expected checkpoint revision, workspace ownership и immutable snapshot, получает execution lock, durable фиксирует `dispatch_authorized` и передаёт управление существующему runner. Он не запускает произвольную новую работу, не использует изменённый manifest, сохраняет первоначальный `manifestBaseDirectory` и не делает скрытый reconcile.

`not_dispatched` определяется отсутствием **durable dispatch authorization**, а не отсутствием процесса. Повтор `resume` с устаревшей revision отклоняется; после возможного dispatch действуют существующие custody/checkpoint/recovery правила. Если Host не может доказать отсутствие dispatch authorization из-за повреждения или пропуска checkpoint, он обязан отказать с recovery-required, а не предполагать безопасность повторного запуска.

`needs_attention` не является синонимом `outcome_unknown`: `workflow/reconcile` сохраняет текущий узкий контракт и применяется **только** к `outcome_unknown`. Для `needs_attention` допускается устранение причины и явный recovery-safe `resume` с revision guard; если при этом обнаружена неопределённость фактического исполнения, переход в `outcome_unknown` требует отдельного reconcile, а не автоматического переопределения результата.

## Workspace-scoped operation identity

Публичная идентичность операции задаётся парой:

`(workspaceId, operationId)`

Идентичность запроса для дедупликации:

`(workspaceId, clientRequestId)`

`workspaceId` — стабильная идентичность workspace в durable metadata. Он не равен JSON-RPC session ID, PID Host или произвольной строке пути.

При первом открытии workspace Host создаёт или читает его идентификатор. Повторные подключения к тому же workspace используют тот же ID.

Требования:

- Идентификатор должен сохраняться между перезапусками Host.
- Клиенты не вправе назначать или подменять `workspaceId`.
- Host разрешает операции только внутри workspace, закреплённого при `initialize`.
- Run metadata содержит `workspaceId` и позволяет проверять принадлежность операции.
- Одинаковые `clientRequestId` в разных workspaces не конфликтуют.
- Один `clientRequestId` в одном workspace не может обозначать два разных typed requests.
- `operations/find` выполняет поиск только в namespace текущего workspace.
- Symlink aliases одного физического workspace не должны создавать независимые execution namespaces.
- Копирование или перемещение workspace требует явно определённой политики переноса identity, чтобы случайная копия metadata не создавала два одновременно активных workspace с одинаковой идентичностью.

**Дополнительное ограничение:** стабильного UUID недостаточно для защиты от двух Host-процессов, обслуживающих одно workspace. Для durable mutation используется межпроцессная координация на уровне общего workspace/run store.

Workspace identity определяет область принадлежности, а canonical path и allowed roots — область разрешённого filesystem access. Эти механизмы дополняют друг друга и не взаимозаменяемы.

## Cancellation semantics

`operations/cancel` — запрос на остановку исполнения, а не подтверждение остановки процессов.

Контракт различает три события:

1. `cancel_requested`: Host принял запрос на отмену.
2. `cancel_acknowledged`: execution supervisor подтвердил обработку запроса.
3. Terminal outcome: фактическое итоговое состояние workflow.

Успешный ответ `operations/cancel` гарантирует только принятие запроса на отмену. Он не гарантирует `canceled`, освобождение ресурсов или отсутствие работающих дочерних процессов.

Для `workflow/start` и связанных host-owned операций:

- Запрос отмены должен быть идемпотентным.
- Повторная отмена не создаёт вторую процедуру остановки.
- Отмена уже завершённой операции не изменяет terminal outcome.
- Отмена не переписывает `failed`, `succeeded`, `needs_attention` или `outcome_unknown` в `canceled`.
- `canceled` допустим только после подтверждённого завершения управляемого исполнения и необходимого cleanup.
- Если Host потерял возможность установить исход, применяется recovery-семантика, а не фиктивное успешное завершение отмены.
- Disconnect клиента сам по себе не отменяет host-owned operation.
- Shutdown Host должен иметь явно определённую политику остановки, ожидания и фиксации промежуточного состояния.
- Cancellation не инициирует автоматические `resume` или `reconcile`.

`cancel_acknowledged` не является terminal outcome. Если отмена и завершение конкурируют, итог определяется фактически зафиксированным состоянием исполнения.

Для connection-bound Doctor/Fix/environment сохраняется текущий lifecycle. Эти операции не приобретают host-owned durability автоматически.

## Обязательные contract tests

До публикации `workflow/start` и `operations/cancel` проверить:

- Потерю JSON-RPC ответа после durable commit.
- Повторный `clientRequestId` с тем же и другим digest, включая retry после изменения или удаления manifest.
- Crash до публикации, между rename и directory fsync, после durable commit и непосредственно перед/после external dispatch.
- Два клиента, одновременно принимающих одинаковый request ID.
- Два Host-процесса, конкурирующих за одно workspace.
- Symlink aliases и разные workspaces с одинаковым request ID.
- Перезапуск Host с восстановлением индекса операций и сохранением `operationId` из recoverable publication.
- Resume из `accepted / not_dispatched` с expected revision, первоначальным snapshot и исходным `manifestBaseDirectory`, а также stale revision refusal.
- Crash до durable snapshot, после snapshot и перед acceptance publication: не публиковать ссылку на отсутствующий snapshot.
- Перемещение snapshot в run directory и относительные пути в manifest: planner/runner используют исходный base directory.
- `needs_attention` после устранения причины допускает recovery-safe resume без reconcile; `outcome_unknown` не допускает resume до ручного reconcile.
- Cancel versus completion, cancel versus disconnect и повторную отмену.
- Crash во время cancellation и корректную классификацию неопределённого исхода.
- Сохранение ранее зафиксированного terminal outcome после поздней отмены.

Fault-injection tests должны проверять durable metadata и фактическое число external dispatch attempts, а не только возвращаемые DTO.
