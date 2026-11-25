# Описание DAG в C#

Пакет `CMakeWorkflow.Scripting` собирает стандартный манифест workflow версии 2 из обычного C#.
Он использует DTO и сериализацию из `CMakeWorkflow.Contracts`, а запрос к Host берёт из
`CMakeWorkflow.Client`. Пакет работает в консольных и file-based приложениях .NET 10 и совместим с NativeAOT.

## Примеры

Соберите пакеты Contracts, Client и Scripting одной версии в локальный NuGet feed. В примерах
`0.1.0-script.1` служит локальной версией для разработки. Для релизных пакетов укажите нужную версию в
`#:package` и передайте `-p:ScriptingVersion=VERSION` консольному проекту.

Выполните команды из корня репозитория на Linux с .NET 10, CMake 3.26+, Ninja, GCC и Clang.

```sh
feed="$PWD/out/scripting-feed"
for package in Contracts Client Scripting; do
    dotnet pack "CMakeWorkflow.$package/CMakeWorkflow.$package.csproj" -c Release -p:Version=0.1.0-script.1 -o "$feed"
done
export RestoreAdditionalProjectSources="$feed"
dotnet pack examples/scripting/NativeFragments -c Release -p:Version=0.1.0-script.1 -o "$feed"
workspace=$(mktemp -d)
cp examples/scripting/native/* "$workspace/"

dotnet run --file examples/scripting/workflow.cs -- "$workspace"
dotnet run --project examples/scripting/Matrix -- "$workspace"
```

Первый пример записывает манифест configure → build → CTest и печатает SHA-256. Второй разворачивает
[LINQ-матрицу](../../examples/scripting/Matrix/Program.cs) по компиляторам и конфигурациям,
использует [общий фрагмент](../../examples/scripting/NativeFragments/NativeWorkflowFragments.cs) и добавляет по три измерения
на вариант. Манифест можно передать обычной команде `CMakeWorkflow workflow run` или в Client SDK.
Результаты измерений сравниваются в [History](history-compare-example.md).

Для запуска первого примера через Host откройте второй терминал и запустите
`CMakeWorkflow host serve --endpoint "$host_dir/rpc.sock"`, где `host_dir` создан через `mktemp -d`.
Передайте полученный путь сокета в переменной `endpoint`. На Windows используется
[именованный канал](host-ipc.md).

```sh
# Проверить DAG через Host.
dotnet run --file examples/scripting/workflow.cs -- "$workspace" "$endpoint"
# Записать стабильный request ID и явно запустить.
dotnet run --file examples/scripting/workflow.cs -- "$workspace" "$endpoint" --execute native-smoke-001
```

## Команды script

Файл `workflow.cs` из скопированного native-примера подключает отдельно собранный пакет
`CMakeWorkflow.Examples.NativeFragments`.

```sh
CMakeWorkflow script compose "$workspace/workflow.cs" -- --matrix
CMakeWorkflow script plan "$workspace/workflow.cs"
CMakeWorkflow script run "$workspace/workflow.cs" --approve --request-id native-job-001
```

`compose` сохраняет манифест рядом с C#-файлом. Укажите `--output-dir`, чтобы выбрать другой существующий локальный
каталог. Аргументы после `--` передаются скрипту. Требуется .NET SDK 10 или новее, доступный через PATH.
Выбор SDK учитывает `global.json` рядом с приложением. Время компиляции и выполнения C# ограничивается
`--timeout-seconds` (по умолчанию 300 секунд).

**Запускайте только доверенный C#.** Даже compose и plan могут выполнять произвольные операции на контроллере,
включая запись файлов, запуск процессов, сеть и восстановление NuGet-пакетов. `--approve` разрешает dispatch
workflow на Host. `WorkflowScript.ExportAsync` записывает отдельный ответ версии 1 с полями `relativePaths`,
`sha256` и `manifestBytes`. Stdout и stderr используются для обычного вывода и диагностик.

По умолчанию plan и run поднимают локальный Host на время команды. Для существующего Host используются параметры
[remote CLI](remote-cli.md). Пример для SSH.NET, если проект с CMake presets уже лежит на Host в каталоге
`project` внутри workspace:

```sh
CMakeWorkflow script run "$workspace/workflow.cs" --approve --request-id native-job-remote-001 \
  --transport sshnet --host builder.example --user build --key-file "$HOME/.ssh/builder" \
  --host-key-sha256 "$known_host_pin" --endpoint /home/build/private/host.sock \
  --workspace /home/build/workspace --manifest-base project --results-dir .cmakeworkflow/remote-job
```

`--manifest-base` задаёт существующий каталог на Host относительно workspace, по умолчанию его корень.
Относительные пути манифеста отсчитываются от этого каталога. Исходники и presets доставляются отдельно.
`--results-dir` допускает абсолютный путь на Host внутри workspace или относительный путь от workspace.
Поддерживаются `--jobs`, повторяемый `--only`, `--history-db` и `--no-history`.

Перед запуском CLI печатает request ID. При потере ответа CLI переподключается к тому же Host и ищет этот ID.
Если связь ещё недоступна, после восстановления используйте `remote find --request-id ID` с теми же параметрами.
Повтор под тем же ID должен использовать тот же манифест и параметры. Чтобы остановить саму операцию на постоянном
Host, используйте `remote cancel`.

## API и жизненный цикл

- `Workflow.Define` и `Context` задают scope, дерево сборки и параметры CMake. В `options:` доступны
  поля стандартных DTO, в том числе preset, привязка toolchain или варианта, provenance, ресурсы, артефакты
  и action cache.
- `Inspect`, `Configure`, `Build`, `Invoke`, `CTest`, `Run`, `Profile` и `ProfileSnapshot` создают
  типизированные ссылки на узлы. `Run` и `Profile` принимают `TargetBuildRef`. Один объект-ссылка
  может быть общей зависимостью нескольких веток. Для входного артефакта добавьте ссылку на производителя
  в `after` или передайте её как источник исполняемого файла.
- `Benchmark` разворачивает серию последовательных замеров со стандартными метаданными и по умолчанию
  запрашивает эксклюзивное выполнение. `Compose` обходит переданные корни и их зависимости.
  У разных объявлений в одном графе должны быть уникальные ID. Порядок корней, зависимостей и перебора
  опций входит во входные данные. При одинаковом порядке и значениях результат побайтово воспроизводим.
  Изменение DTO, сохранённого из callback или полученного через `ToDefinition`, снимок не меняет.
- `Workflow.Scope` и `WorkflowScope.Id` квалифицируют ID узлов и контекстов для повторно используемых фрагментов.
  Точка разделяет вложенные области. Все области одного `Workflow` могут использовать общие ссылки на узлы.
- `SaveAsync` сохраняет манифест локально. `manifest.PrepareAsync(client.Workflows, resultsDirectory, manifestBase)`
  доставляет до 1 МиБ точных байтов по локальному RPC или SSH. Host проверяет SHA-256 и атомарно публикует
  файл по хешу, переиспользуя существующее совпадающее содержимое. Повтор после обрыва безопасен с теми же
  байтами. На Unix каталог манифеста и workspace должны быть на одной файловой системе с поддержкой
  жёстких ссылок. Для старого Host доставьте файл вручную и передайте его абсолютный путь в `AtHost`.
- `client.Workflows.PreflightAsync` возвращает план и диагностические сообщения без запуска и
  резервирования ресурсов. После одобрения передайте сохранённый request ID в
  `preflight.CreateStartOptions(requestId)` и вызовите `client.Workflows.StartAsync`.
  Host повторно проверяет байты манифеста перед допуском.
- Через `WorkflowOperation` доступны статус, отчёт и durable-отмена. Отмена клиентского токена прекращает
  отдельный RPC или ожидание. Если ответ на запуск потерялся, подключитесь к тому же workspace и вызовите
  `client.Operations.FindAsync(requestId)`. Правила восстановления описаны в
  [контракте Host](../en/workflow-authoring-api.md).

C# выполняется на контроллере. Сериализуются только данные существующего workflow. Манифесты JSON
остаются совместимы со старым CLI и Client SDK.
