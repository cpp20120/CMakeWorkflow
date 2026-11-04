# Remote CLI (этап E)

Команды `remote` — тонкая оболочка над `CMakeWorkflow.Client`. Каждый вызов
создаёт **одно** SSH-соединение с уже запущенным постоянным Linux Host.
CLI не загружает manifest, не поднимает Host, не делает reconnect и никогда
не повторяет потерянный `workflow/start` автоматически.

```sh
# Сначала на удалённой машине:
cmakeworkflow host serve --endpoint /home/me/.cmwf/host.sock

# На локальной машине:
base='--destination devbox --endpoint /home/me/.cmwf/host.sock --workspace /home/me/project'
cmakeworkflow remote find $base --request-id my-build-42
cmakeworkflow remote status $base --operation OPERATION_ID
cmakeworkflow remote inspect $base --operation OPERATION_ID
cmakeworkflow remote events $base --operation OPERATION_ID --after 0 --limit 100
cmakeworkflow remote report $base --operation OPERATION_ID
cmakeworkflow remote artifacts $base --operation OPERATION_ID
cmakeworkflow remote download $base --operation OPERATION_ID \
  --artifact-id stdout:build --output ./build.stdout
```

Все пути, кроме `--output`, относятся к удалённому Host. `download` получает
объект по ID конкретной операции, сверяет конечный SHA-256 и публикует файл
только после полной проверки. При разрыве соединения сохраняются локальные
`.cmwf.part` и `.cmwf.part.meta`; повтори команду явно, чтобы продолжить.

## Начало и отмена операции

```sh
cmakeworkflow remote start $base \
  --request-id my-build-42 --manifest /home/me/project/workflow.json \
  --manifest-sha256 ACTUAL_SHA256_OF_REMOTE_MANIFEST \
  --results-dir /home/me/project/results/run-42 --jobs 2

cmakeworkflow remote inspect $base --operation OPERATION_ID
cmakeworkflow remote cancel $base --operation OPERATION_ID --revision ACTUAL_REVISION
```

`--request-id` задаёт клиент и сохраняет до вызова `start`, а SHA-256 должен
соответствовать **байтам manifest на удалённой машине**. После потери ответа
используй `remote find` с **тем же** request ID. Потеря транспорта не доказывает,
что запуск был отклонён. Нового request ID/автоматического повторного запуска нет.
Отмена требует ожидаемую revision из `remote inspect`; конфликт не приводит к
автоматической повторной попытке.

`events` возвращает одну ограниченную страницу с `nextSequence`, не постоянный
watch. `find`, `status`, `inspect`, `events`, `report`, `artifacts` не мутируют
Host. CLI выводит JSON. Дополнительные параметры подключения:
`--ssh-executable PATH`, `--remote-executable PATH`, `--connect-timeout-seconds N`
(от 1 до 3600). Аутентификация и host keys остаются на стороне OpenSSH.
