# Stage E2 — SSH.NET транспорт

Новая реализация клиентского SSH-канала использует NuGet `SSH.NET` `2026.0.0`,
а не внешний процесс `ssh`. Старый OpenSSH остаётся транспортом по умолчанию.
Контракт JSON-RPC, durable admission, операции и скачивание артефактов не меняются.

```sh
cmakeworkflow remote status --transport sshnet \
  --host build.example --user builder --key-file /home/me/.ssh/id_ed25519 \
  --host-key-sha256 SHA256:ПРОВЕРЕННЫЙ_ОТПЕЧАТОК \
  --endpoint /home/builder/.cmwf/host.sock \
  --workspace /home/builder/project --operation OPERATION_ID
```

Host key pin обязателен. Отпечаток нужно получить по **доверенному каналу**, а
не копировать без проверки с подозрительного сервера. Используется exec без PTY;
стандартный вывод — бинарный JSON-RPC, stderr не смешивается с ним.

Поддерживается вход по локальному файлу приватного ключа. OpenSSH config,
ssh-agent, ProxyJump и запрос пароля пока не поддержаны. Режим работает с
**уже запущенным** persistent Host; автоматический запуск Host относится к F.

Проверка параметров: `--sshnet-contract-tests`. Настоящее SSH-подключение:
`--sshnet-live-tests` с переменными `CMWF_SSHNET_HOST`, `CMWF_SSHNET_USER`,
`CMWF_SSHNET_KEY`, `CMWF_SSHNET_PIN`, `CMWF_SSHNET_ENDPOINT`,
`CMWF_SSHNET_WORKSPACE` (опционально `CMWF_SSHNET_PORT`, `CMWF_SSHNET_EXE`).
