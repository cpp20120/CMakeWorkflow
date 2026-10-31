# ADR-0002: локальный CMakeWorkflow Host

**[Русский](0002-local-host-ipc.md) · [English](../../en/adr/0002-local-host-ipc.md) · [Host IPC](../host-ipc.md)**

**Дата:** 30 октября 2026. **Первоначальное решение:** принято для read-only Doctor, позднее дополнено явным Fix и расширено application operation API. Ниже сохраняется исходная мотивация и границы ответственности; фактические текущие возможности определяются согласованным `initialize.capabilities`.

## Контекст

CLI и VS Code нуждаются в одинаковой диагностике окружения, плане исправлений и progress events. Уже существует `ToolingService`, владеющий семантикой обнаружения и установки инструментов. Host должен предоставлять его наружу, а не создавать ещё один установщик или DAG scheduler внутри расширения.

## Решение

Использовать локальный Host в существующем исполняемом файле: StreamJsonRpc поверх Unix sockets / Windows named pipes, TypeScript-клиент на vscode-jsonrpc. CLI и IDE — клиенты общего контракта. Первый этап: `initialize`, `tooling/doctor`, progress notifications и request cancellation. Изменяющие систему операции допускаются только после проверки NativeAOT, совместимости wire protocol и жизненного цикла.

DTO и RPC target metadata генерируются при компиляции (System.Text.Json/PolyType); версии библиотек транспорта фиксируются. Штатные CLI-команды остаются доступными без Host. Extension может лениво запускать приватный Host для workspace или подключаться к заранее указанному адресу. Нет auto-discovery глобального демона, сетевого сервиса, фоновых обновлений или регистрации OS service.

Локальный gRPC технически возможен, включая Unix sockets: он не отклонён как «только сеть». В данном проекте JSON-RPC проще использовать с существующими JSON DTO и TypeScript без отдельной language-neutral IDL/генерации клиента. Появление удалённого сервиса потребовало бы отдельной схемы аутентификации и развёртывания.

## Следствия

Нужны строгие правила endpoint ownership, совместимости версии, workspace isolation, cancellation и cleanup. Успешная NativeAOT-публикация сама по себе не проверяет serialization, framing, ошибки и notification delivery. На одном соединении нельзя произвольно менять workspace; Host не должен менять process-global cwd/env.

У Doctor операции read-only и connection-scoped. У Fix нужны неизменяемые/перепроверяемые планы, сериализация установки, privilege handling и post-install verification. Для durable workflow необходимы собственные operation IDs, восстановление после reconnect и явная семантика отмены. **Разрыв соединения не должен означать повтор или отмену неизвестной mutating operation.**

## Дополнение: Fix

Реализованы `tooling/planFix` / `tooling/applyFix`, одноразовые истекающие планы, проверка непосредственно перед установкой, per-user locking, progress и итоговый Doctor. Клиент может применить план только после явного подтверждения. Установка остаётся в существующем ToolingService; отмена может оставить частично изменённую систему. Автоматические retry/rollback не обещаются. См. [актуальный Host guide](../host-ipc.md).

Ссылки: [StreamJsonRpc NativeAOT](https://microsoft.github.io/vs-streamjsonrpc/docs/nativeAOT.html), [framing](https://microsoft.github.io/vs-streamjsonrpc/docs/extensibility.html), [code-first gRPC](https://learn.microsoft.com/en-us/aspnet/core/grpc/code-first).
