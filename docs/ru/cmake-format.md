# Форматирование CMake и C/C++

```console
cmakeworkflow format --check
cmakeworkflow format --apply
cmakeworkflow format --language cmake --diff
cmakeworkflow format --language cmake --check --file CMakeLists.txt --file "cmake/My Module.cmake" --json
cmakeworkflow format --changed --check
cmakeworkflow format --staged --check
```

По умолчанию проверяются оба языка. Выберите каталог проекта через `--project`. Для `CMakeLists.txt`, `*.cmake` и `*.cmake.in` используется `cmakefmt`, запуск возможен до configure. Для C/C++ используется `clang-format`. Параметр `--target` ограничивает C/C++ источниками цели CMake из build tree.

## Закреплённая версия

```console
cmakeworkflow tooling plan --profile cmakefmt --tool-policy ManagedOnly
cmakeworkflow tooling setup --profile cmakefmt --tool-policy ManagedOnly --yes --plan-sha REVIEWED_SHA256
cmakeworkflow tooling doctor --profile cmakefmt --tool-policy ManagedOnly
cmakeworkflow format --language cmake --check --tool-policy ManagedOnly
```

Подставьте SHA-256 из плана. Установка проверяет архив и дерево файлов, после чего публикует версию атомарно. При последующем использовании managed-форматтера дерево проверяется снова. Политика `AllowSystem` разрешает совместимый бинарник с PATH и использует проверенное хранилище, когда системного бинарника нет. Закреплена v2.4.0 для Linux x64 (GNU при glibc от 2.39, иначе статический musl), Linux ARM64 (glibc от 2.39), Windows x64 и macOS x64/ARM64. Для C/C++ требуется системный `clang-format`. [Релиз cmakefmt v2.4.0](https://github.com/cmakefmt/cmakefmt/releases/tag/v2.4.0)

## Выбор файлов и отчёт

- Для явного списка повторяйте `--file`. Шаблоны `--include` и `--exclude` сопоставляются с относительными путями с прямой косой чертой. Исключения имеют приоритет. Используйте их для сторонних исходников, специфичных для проекта. Деревья с `CMakeCache.txt`, известные каталоги зависимостей и символические ссылки пропускаются. Исходный каталог `build` допустим.
- `--changed` проверяет существующие отслеживаемые файлы с изменениями относительно `HEAD`. `--staged` проверяет выбранные файлы из working tree, только если Git не видит в них незастейдженных правок. Конфигурация берётся из рабочей копии. Сочетание `--staged --apply` недоступно.
- Выбранные CMake-файлы пересекаются со списком, найденным с учётом ignore-правил самого `cmakefmt`. Пакетный запуск использует NUL-разделённый список и `--jobs`. Семантическая проверка включена во всех режимах. Имена с CR или LF не поддерживаются, поскольку отчёт обнаружения выводит одну строку на путь.
- Поддерживаются вложенные `.cmakefmt.yaml`, `.cmakefmt.yml`, `.cmakefmt.toml` и fallback через `.editorconfig`. Для запуска создаётся пустой временный home. [Документация конфигурации cmakefmt](https://cmakefmt.dev/config/)
- Коды выхода для CI: `0` для успешной операции, `1` если нужны правки, `2` при ошибке, `130` при отмене. `--json` возвращает статус по каждому файлу и сырой JSON от `cmakefmt`. При частичном apply отчёт показывает фактически изменённые файлы и ошибки.

## Проверка перед сборкой

В декларативном workflow v2 используйте `"kind": "format_check"` с контекстом, в котором задан `sourceDirectory`. Аргументы `["cmake", "managed"]` выбирают закреплённый CMake-форматтер. Доступны также `["cpp"]`, `["cmake"]` и `["all"]`. Добавьте проверки в зависимости configure. JSON-отчёт доступен как stdout-артефакт узла через `WorkflowOperation.ListArtifactsAsync` и `DownloadArtifactAsync`. Для его чтения используется `FormatReportJson.Default.FormatResult` из Contracts.

В C# используйте `Workflow.FormatCheck`. В [примере форматирования, сборки и тестов](../../examples/scripting/format/) укажите установленную версию пакета Scripting. Запускайте через `script plan workflow.cs` и существующий `script run workflow.cs --approve`. При SSH заранее разместите проект и форматтеры на Host. Проверка читает исходники Host в момент запуска. Если выполнение остановилось после успешной проверки, для продолжения с проверенными исходниками запустите новый workflow. Проверки форматирования не используют Action Cache.

Для локальных .NET-вызовов доступны `SourceFormatting.RunAsync(FormatRequest)` и `ClangTools.FormatAsync` с `FormatTarget`.

Тестовый C# runner измеряет пакетное форматирование. Передайте пути к CLI и нативному исполняемому файлу cmakefmt.

```console
dotnet run --project tests/CMakeWorkflow.IntegrationTests.csproj -c Release -- /path/to/CMakeWorkflow --format-benchmark /path/to/cmakefmt --files 1000 --jobs 4 --repeats 5
```
