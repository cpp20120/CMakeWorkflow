# Сравнение двух C++-сборок в History

**[Русский](history-compare-example.md) · [English](../en/history-compare-example.md)**

Ускорит ли программу поиск медианы через `std::nth_element` вместо полной сортировки?
[Пример](../../examples/history-compare/main.cpp) собирает обе реализации из одного исходника. При каждом запуске
программа генерирует одинаковые данные и выводит одну и ту же медиану.

Выполняйте команды из корня репозитория. Понадобятся CMake 3.27+, Ninja, компилятор с поддержкой C++17
и [релизный бинарник](https://github.com/cpp20120/CMakeWorkflow/releases) в PATH. Если
[собрали CLI из исходников](getting-started.md#собрать-и-построить-план), используйте
`dotnet bin/Release/net10.0/CMakeWorkflow.dll` вместо `CMakeWorkflow`.

## Запустить базовую версию

```sh
cmake -S examples/history-compare -B out/history-compare/build -G Ninja -DCMAKE_BUILD_TYPE=Release -DUSE_NTH_ELEMENT=OFF
CMakeWorkflow workflow generate benchmark --build-dir out/history-compare/build --target median_bench --config Release --repeats 10 --timeout-seconds 30 --output out/history-compare/benchmark.json
CMakeWorkflow workflow run out/history-compare/benchmark.json --results-dir out/history-compare/baseline --history-db out/history-compare/history.sqlite
```

CMake настраивает дерево сборки и публикует метаданные цели через File API. Сгенерированный workflow собирает
`median_bench` и запускает его десять раз последовательно. При повторном прогоне пропустите генерацию и используйте
готовый `benchmark.json`.

## Запустить новую версию

```sh
cmake -S examples/history-compare -B out/history-compare/build -DUSE_NTH_ELEMENT=ON
CMakeWorkflow workflow run out/history-compare/benchmark.json --results-dir out/history-compare/candidate --history-db out/history-compare/history.sqlite
```

CMake пересобирает цель с другой реализацией. Запускайте обе версии на одной машине, с тем же компилятором и
конфигурацией Release. Один манифест задаёт одинаковую нагрузку и способ измерения.

## Сравнить и открыть результаты

```sh
CMakeWorkflow history runs --db out/history-compare/history.sqlite --status succeeded --limit 2
CMakeWorkflow history compare BASELINE_ID CANDIDATE_ID --db out/history-compare/history.sqlite
```

Скопируйте идентификаторы из первого столбца и сопоставьте их с путями `baseline` и `candidate` в последнем.
`process_wall_time` включает запуск процесса и генерацию данных. Отрицательная разница медиан означает, что новая
версия отработала быстрее. В одном локальном прогоне медиана снизилась с 55,4 до 10,6 мс. При статусе `unverified`
или `incompatible` History выводит причину и опускает разницу.
Подробнее в разделе о [сопоставимости измерений и обнаружении регрессий](../en/results-and-history.md#comparable-measurements-versus-regressions).

Чтобы открыть отчёт, подставьте каталог из `history runs` в команду
`CMakeWorkflow workflow report RUN_DIRECTORY --json`. Сохранённый `run.json` содержит статусы узлов и пути к логам.
В `stdout` узла `timing_001` обеих версий будет одна и та же медиана входных данных, `2149777886`.
