# Домен запуска процессов (v1)

[English](../en/process-execution.md) · [Архитектура](../architecture.md)

`execution/start` принимает `domain: "process"`, `clientRequestId`, путь manifest,
его SHA-256, `jobs` и `resultsDirectory`. Пути относятся к машине Host и должны
находиться внутри инициализированного workspace. Принятый snapshot manifest
неизменяем. Process runner использует общий `ExecutionScheduler`, process backend,
checkpoint `run.json` и тот же SQLite-протокол admission, что и CMake workflow.
Отчёт имеет собственную схему v1; результаты читаются через `execution/report`.
`operations/find/get/cancel/events` и API загрузки артефактов общие для обоих доменов.

```json
{
  "schemaVersion": 1,
  "domain": "process",
  "nodes": [
    { "id": "prepare", "executable": "dotnet", "arguments": ["--info"] },
    { "id": "check", "executable": "dotnet", "arguments": ["--list-sdks"],
      "dependencies": ["prepare"], "resources": ["sdk"] }
  ]
}
```

## Явное восстановление

После остановки всех внешних процессов:

1. `workflow/recoveryInspect` возвращает сохранённую revision и привязку checkpoint.
2. `workflow/reconcile` записывает `decision: "needs_attention"`, пояснение оператора,
   ожидаемую revision и `confirmedQuiescent: true`.
3. `workflow/resume` с revision **1** и подтверждением остановки проверяет process
   checkpoint, сохраняет аудит и допускает продолжение с revision **2**.
   Для идемпотентного повтора admission используется revision 2. Этот шаг ничего не запускает.
4. `workflow/resumeExecute` с revision **2** и подтверждением остановки запускает
   попытку, принадлежащую Host. Под operation/run leases исполнитель повторно проверяет
   точные байты checkpoint и фиксирует одноразовый SQLite claim до изменения состояния
   runner и любых внешних эффектов.
5. После потери ответа найдите прежнюю операцию; не отправляйте новый start с другим ID.
   Отключение клиента постоянного Host не отменяет принятую попытку. Отмена запрашивается
   через `operations/cancel` с отдельной revision cancellation intent.

Сохраняются operation ID и первоначальный snapshot. Изменения исходного manifest
не подменяют принятую операцию. Продолжение сейчас использует `jobs = 1`.

SDK требует явно передать recovery revision в `ExecuteResumeAsync`. Текущее значение **2** получается из сохранённых
переходов reconciliation/admission `0 -> 1 -> 2`; это не счётчик повторов и не значение, которое SDK должен угадывать.
После reconciliation в `needs_attention` и проверки остановки внешних процессов используйте результат admission:

```csharp
RecoverySnapshot reconciled = await operation.InspectRecoveryAsync(ct);
RecoverySnapshot admitted = await operation.AdmitResumeAsync(
    reconciled.Revision, confirmedQuiescent: true, ct: ct);
RecoverySnapshot scheduled = await operation.ExecuteResumeAsync(admitted.Revision, ct);
```

`scheduled` подтверждает постановку на выполнение, а не завершение. Устаревшая revision отклоняется до dispatch.
Прерывание RPC может произойти после постановки попытки на выполнение под управлением Host; после переподключения
сначала проверьте прежнюю операцию.

Решение SQLite `needs_attention` **не разрешает повтор running-узла**. При наличии
`running` или `outcome_unknown` продолжение отклоняется. Исполняются только узлы
`pending`, `blocked` или `needs_attention` без metadata предыдущего запуска, которые
также не были допущены в последнем привязанном к SQLite dispatch checkpoint.
Успешные узлы сохраняют результаты и логи; failed/canceled тоже остаются терминальными
и повторно не исполняются. Ошибочный prerequisite блокирует зависимые узлы.
Если все результаты узлов уже сохранены, а терминальный run checkpoint отсутствует,
продолжение может завершить операцию без повторных внешних эффектов.

Process-отчёт v1 дополнен необязательными dependencies, `recoveryPhase`, `resumeCount`
и аудитом: пояснение, время подтверждения, исходные байты checkpoint до восстановления
и привязанного dispatch checkpoint. SQLite сохраняет точные process dispatch bytes
рядом с существующим hash. Проверяются snapshot SHA, ID/порядок узлов, dependencies,
история допуска, сохранённые терминальные результаты, границы workspace и binding.
Схема CMake `WorkflowReport` не меняется. Используется source-generated JSON.

Ранее сохранённые v1 отчёты остаются читаемыми. Для старых операций без сохранённых
dispatch bytes новый process resume отклоняется: отсутствие доказательств не подтверждает
отсутствие запуска. API не предоставляет ручного per-node override для повторения
неопределённого эффекта. Такие результаты требуют отдельной проверки и внешнего решения.

Перед внешними эффектами сохраняется вся партия `running` и вызывается существующий
`ConfirmRunnerCheckpoint`. События дописываются с возрастающими sequence; новые логи
имеют суффикс `.resume-1` и создаются через create-new. Прежние логи не обрезаются.
Авария до claim позволяет исполнить ту же явно допущенную попытку. Авария после claim
расходует её, даже если процесс не стартовал. Повторного claim и автоматического reset нет.

Quiescence — утверждение оператора, а не автоматическое обнаружение осиротевших
процессов. Отчёт и SQLite — доверенные локальные данные без подписи. Код executable
и внешние входы не snapshot-ятся. Команды выполняются с правами Host; домен рассчитан
на доверенный workspace и авторизованных клиентов.

## Проверка

```sh
dotnet build CMakeWorkflow.Host.Tests -c Release -p:RecoveryFaultInjection=true -m:1
dotnet CMakeWorkflow.Host.Tests/bin/Release/net10.0/CMakeWorkflow.Host.Tests.dll --process-recovery-tests
```

Suite использует реальные процессы, SIGKILL, symlink и локальный JSON-RPC, включая
потерю транспортного ответа. Сейчас требуется Linux; проверка Windows выполняется
отдельно. В обычной сборке recovery fault hooks из переменных окружения отключены.
