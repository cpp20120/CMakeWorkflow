# F3 — интеграционная проверка SSH.NET на настоящем sshd

Тесты запускаются **только явно** и не подменяют SSH локальным скриптом.
Нужна отдельная Linux-машина (желательно временная VM), SSH-учётная запись
с приватным ключом, установленный CMakeWorkflow и отдельный доступный на
запись workspace. На удалённой машине нужны CMake >=3.26, Ninja, `base64`,
`grep`, `ps` и POSIX shell.

Параметры передаются переменными окружения:

```bash
export CMWF_SSHNET_HOST=build.example
export CMWF_SSHNET_USER=builder
export CMWF_SSHNET_KEY="$HOME/.ssh/id_ed25519"
export CMWF_SSHNET_PIN='SHA256:ПРОВЕРЕННЫЙ_ОТПЕЧАТОК_СЕРВЕРА'
export CMWF_SSHNET_WORKSPACE=/home/builder/cmwf-f3-workspace
export CMWF_SSHNET_EXE=/home/builder/bin/CMakeWorkflow # необязательно
export CMWF_SSHNET_PORT=22                         # необязательно
```

Отпечаток публичного ключа сервера надо получить по **доверенному каналу**,
а не просто принять ответ непроверенного сервера. Все пути `KEY` — локальные,
остальные пути — удалённые. Persistent Host запускать не нужно.

```bash
dotnet CMakeWorkflow.Host.Tests/bin/Release/net10.0/CMakeWorkflow.Host.Tests.dll \
  --sshnet-live-lifecycle-tests handshake

dotnet CMakeWorkflow.Host.Tests/bin/Release/net10.0/CMakeWorkflow.Host.Tests.dll \
  --sshnet-live-lifecycle-tests workflow

# Отдельно: при уже запущенном persistent Host
export CMWF_SSHNET_ENDPOINT=/home/builder/.cmwf/host.sock
dotnet CMakeWorkflow.Host.Tests/bin/Release/net10.0/CMakeWorkflow.Host.Tests.dll \
  --sshnet-live-lifecycle-tests persistent
```

`handshake` проверяет аутентификацию, привязку ключа сервера, RPC/readiness,
отказ при неправильном pin и неизвестном приватном ключе, отсутствие зависания
при несуществующем исполняемом файле, завершение приватного Host после
закрытия канала. Независимое SSH-соединение с тем же pin проверяет PID.

`persistent` подключается к заранее запущенному постоянному Host дважды,
проверяет неизменность PID и работу RPC после переподключения. Он требует
`CMWF_SSHNET_ENDPOINT` и не запускается по умолчанию.

`workflow` создаёт временный `f3-sshnet-*` внутри указанного workspace,
конфигурирует CMake/Ninja, запускает реальный `invoke`, ждёт маркер `started`,
закрывает SSH-канал **без** `operations/cancel`, проверяет остановку Host и
отсутствие маркера `finished` после освобождения тестового процесса. Удаляются
только файлы собственного теста.

Эти тесты **не** эмулируют обрыв физической сети или падение sshd. Они проверяют
закрытие exec-channel и lifetime частного Host. Проверки persistent durability
и lost-reply относятся к другим тестовым наборам. В обычный GitHub CI тест
не включён: нельзя считать его пройденным до фактического запуска против sshd.
