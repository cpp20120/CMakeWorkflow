# F2 — установка Host через SSH.NET

Используется схема из CasinoShiz.Deploy: **probe → plan → approve → apply → verify**.
Это не перенос Ansible, Kubernetes, DockerAgent или отдельного ToolCatalog.

`remote provision plan` читает архитектуру Linux-хоста, наличие инструментов и
содержимое локального publish, вычисляет SHA-256 каждой копируемой записи и
`approvalSha256`. При планировании удалённая ФС не меняется.

`remote provision apply` требует явного `--approval-sha256`, повторно вычисляет
план и только после совпадения начинает установку. Файлы передаются по SSH.NET
SFTP с проверкой ключа сервера, проверяются `sha256sum` на целевой машине,
складываются в версионированный каталог и активируются атомарной заменой
символической ссылки `current`. Повторное применение той же версии идемпотентно.

Пример:

```bash
dotnet publish CMakeWorkflow.csproj -c Release -r linux-x64 -o /tmp/cmwf-publish
cmakeworkflow remote provision plan \
  --host builder.example --user builder \
  --key-file /home/me/.ssh/id_ed25519 --host-key-sha256 SHA256:TRUSTED_PIN \
  --workspace /home/builder/project --publish-dir /tmp/cmwf-publish \
  --install-root /home/builder/.local/share/cmakeworkflow --rid linux-x64
# Сверь план и возьми значение approvalSha256.
cmakeworkflow remote provision apply ... --approval-sha256 APPROVAL_SHA256
```

Инсталляция не делает `sudo`, не устанавливает CMake/компилятор и не трогает
запущенный persistent Host. Проверка инструментов — read-only; подробная
диагностика и исправления остаются за существующим `tooling doctor/fix`.
При разрыве соединения результат установки может быть неизвестен: проверить
`current`, версии и `.bootstrap-lock`; автоматически удалять старую блокировку
запрещено. Рабочий каталог должен существовать до установки.

Проверка настоящего `sshd` и SFTP добавляется в Docker F3 harness.
