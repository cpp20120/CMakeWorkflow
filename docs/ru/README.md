# Документация CMakeWorkflow

**[Русский](README.md) · [English](../en/README.md) · [Выбор языка](../README.md)**

## Начать здесь

[Начало работы за 5 минут](getting-started.md) — сборка, планирование, запуск, отчёт, использование существующего build tree.

## Справочник

| Раздел | О чём |
|---|---|
| [Архитектура](../architecture.md) | Границы CMake и runner, процессы, leases, provenance |
| [Манифесты workflow](../workflows.md) | `contexts`, `nodes`, зависимости, ресурсы и `schemaVersion` |
| [CLI](../cli.md) | Команды, ключи, коды возврата |
| [Результаты и History](../results-and-history.md) | JSONL, `run.json`, SQLite, сравнения, статистика |
| [Recovery](../recovery.md) | `outcome_unknown`, `reconcile`, аудит и durability |
| [Профилирование и DagFlow](../profiling-and-campaigns.md) | Capture, analyze, replay и кампании |
| [Интеграции](../integrations.md) | Provisioning, target bridge, VS Code, ограничения проверок |
| [Local Host IPC](host-ipc.md) | JSON-RPC, Doctor, Fix, NativeAOT |
| [ADR-0001: MSBuild](../adr/0001-msbuild-execution-backend.md) | Не внедрённое предложение альтернативного execution engine |
| [ADR-0002: Host IPC (RU)](adr/0002-local-host-ipc.md) | Решение об IPC и границы ответственности |

Страницы русскоязычного справочника оставлены на прежних URL в `docs/`, чтобы не ломать ссылки из README, исходников и сторонних репозиториев. Это **канонические русские страницы**, а не устаревшие копии. Ссылка на отсутствующий ADR-0003 из старого индекса убрана.

Требования для примера: .NET 10 SDK, CMake 3.26+, Ninja и компилятор C++. Из корня репозитория:

```sh
dotnet build CMakeWorkflow.slnx -c Release
dotnet bin/Release/net10.0/CMakeWorkflow.dll workflow plan examples/hello/workflow.json --json
dotnet bin/Release/net10.0/CMakeWorkflow.dll workflow run examples/hello/workflow.json --results-dir out/results --json
```

Для `workflow report` используйте каталог из события `runDirectory`, а не угадывайте имя run. Сведения о реальной полноте проверки см. в [интеграциях](../integrations.md#проверки-и-ограничения-покрытия).
