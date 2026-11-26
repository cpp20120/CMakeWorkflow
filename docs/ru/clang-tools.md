# Форматирование и анализ Clang

Единая команда и выбор CMake-файлов описаны в разделе [Форматирование CMake и C/C++](cmake-format.md).

Запускайте команды в корне CMake-проекта или укажите `--project DIR`. Нужные LLVM-инструменты должны быть установлены на машине, где выполняется CLI. Конкретную версию можно выбрать через `--executable PATH`.

```sh
cmakeworkflow format --check
cmakeworkflow format --apply
cmakeworkflow format --target runtime --build-dir build
cmakeworkflow lint --target runtime --build-dir build --jobs 8
cmakeworkflow lint --tool clang-check --target runtime --build-dir build
cmakeworkflow analyze --preset clang-release
cmakeworkflow lint --tool iwyu --build-dir build
```

| Команда | Инструмент | Данные |
| --- | --- | --- |
| `format` | clang-format | C/C++-исходники и заголовки проекта или выбранного target |
| `lint` | clang-tidy | Translation units и их команды компиляции |
| `lint --tool clang-check` | clang-check | Команды компиляции для синтаксической и семантической проверки |
| `analyze` | Clang Static Analyzer через проверки `clang-analyzer-*` | Команды компиляции |
| `lint --tool iwyu` | Драйвер Include What You Use | Команды компиляции |
| `lint --apply-replacements DIR` | clang-apply-replacements | YAML с исправлениями из DIR |

`format` по умолчанию проверяет формат и возвращает 1, если нужны изменения. Запись происходит с `--apply`. Файлы передаются пакетами с ограничением на размер argv и число файлов `--batch-size`. `--jobs` ограничивает параллельные вызовы. Для `--target` используются исходники и заголовки, объявленные в CMake, в том числе header file sets.

Для готовой сборки укажите `--build-dir`. По умолчанию CLI проверяет корень проекта и каталог `build`. Для `compile_commands.json` включите `CMAKE_EXPORT_COMPILE_COMMANDS` при конфигурации Ninja или Makefiles. Для отбора по target заранее запросите CMake File API.

```sh
mkdir -p build/.cmake/api/v1/query
touch build/.cmake/api/v1/query/codemodel-v2
cmake -S . -B build -G Ninja -DCMAKE_EXPORT_COMPILE_COMMANDS=ON
```

При `--preset NAME` CMake читает preset и конфигурирует отдельное дерево под `.cmakeworkflow/clang/`. Обычный binaryDir preset при этом переопределяется. Конфигурация может исполнять код проекта. Если анализу нужны заголовки, которые появляются после сборки, передайте готовый build tree. Для multi-config укажите `--configuration NAME`.

clang-tidy и clang-check обрабатывают translation units параллельно с сохранением флагов и рабочих каталогов CMake. Compilation database фильтруется и разбивается на части, чтобы каждый процесс загружал ограниченный объём данных. Команды общего файла от разных targets разделяются по object output CMake. Неоднозначный выбор завершается диагностикой. IWYU использует параллелизм своего драйвера и требует установленного совместимого IWYU.

Настройте `.clang-tidy` и `.clang-format` в проекте. Для clang-tidy доступен `--checks GLOB`. Исправления можно выгрузить в пустой каталог и применить отдельной командой после просмотра.

```sh
cmakeworkflow lint --target runtime --build-dir build --export-fixes out/tidy-fixes
cmakeworkflow lint --apply-replacements out/tidy-fixes
```

YAML может указывать файлы для редактирования. Используйте доверенные результаты. Диагностика, повышенная clang-tidy до ошибки, сбой инструмента или таймаут дают ненулевой код выхода. `--timeout-seconds` ограничивает отдельный вызов инструмента.
