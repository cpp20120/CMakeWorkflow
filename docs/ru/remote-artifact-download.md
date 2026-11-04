# Загрузка артефактов через SDK (D3)

Для завершённой операции SDK получает каталог через тот же JSON-RPC Host,
который выполнял workflow. Удалённый файл выбирается по `artifactId`, а не по
пути от клиента:

```csharp
await using var client = await CMakeWorkflowClient.ConnectSshAsync(options, ct);
var operation = client.Operations.GetHandle(savedOperationId);
var catalog = await operation.ListArtifactsAsync(ct);
var stdout = catalog.Artifacts.Single(x => x.Kind == "stdout");
await operation.DownloadArtifactAsync(stdout, localDestinationPath, ct: ct);
```

Файл публикуется только после проверки полного SHA-256. Родительский каталог
должен существовать, готовый файл не перезаписывается. При разрыве SSH или
отмене остаются `<target>.cmwf.part` и `<target>.cmwf.part.meta`. Каждые 1 МиБ
SDK фиксирует сброшенный на диск префикс, его SHA-256 и offset; checkpoint
связан с operation ID, artifact ID, размером, timestamp и SHA-256 артефакта.
При повторном вызове с новым SSH-соединением SDK проверяет префикс, отбрасывает
незакоммиченный хвост и продолжает загрузку. Никакого автоматического
reconnect или повторного `workflow/start` нет.

Повреждённый префикс, некорректный chunk и неверный конечный SHA удаляют
непроверенный partial. Неудача транспорта сохраняет checkpoint. При
несовпадающей identity существующие partial-файлы не перезаписываются.
Готовый файл появляется атомарным rename внутри одного файлового раздела.

Проверка на POSIX с CMake, Ninja и локальным IPC:

```sh
dotnet build CMakeWorkflow.Host.Tests/CMakeWorkflow.Host.Tests.csproj -c Release
timeout --kill-after=5s 100s dotnet \
  CMakeWorkflow.Host.Tests/bin/Release/net10.0/CMakeWorkflow.Host.Tests.dll \
  --ssh-download-tests
```

Интеграционный тест проверяет восстановление загрузки лога больше 3 МиБ на
новом SSH bridge, подмену partial, конфликт checkpoint identity, неправильный
SHA, защиту от перезаписи и `run.json`. Настоящий sshd не участвует. Отдельная
задача перед использованием Host в недоверенном multi-user окружении —
закрыть гонку между проверкой пути и открытием дескриптора в существующем D2.
