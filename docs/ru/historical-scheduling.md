# Расписание исторических бенчмарков DagFlow

Старые CLI-вызовы сохраняют Python-порядок с seed `20260930`. Можно независимо
выбрать PRNG, стратегию, область перемешивания и число повторений:

```sh
CMakeWorkflow dagflow profile run-historical --source-dir /path/to/DagFlow \
  --output /path/to/results --trials 5 --stat-trials 3 --jobs 4 \
  --shuffle-algorithm pcg64 --seeds 42,1337,2026 \
  --schedule-strategy balanced-blocks --shuffle-scope block
```

Поддерживаются `none`, `dotnet`, `mt19937`, `pcg32`, `pcg64`, `xoshiro256ss`,
`xoroshiro128pp`, `splitmix64`, `sfc64`, `philox`, `threefry`, `python-legacy`.
Стратегии: `sequential`, `shuffle-global`, `random-interleaved`,
`randomized-blocks`, `balanced-blocks`. Область: `block` или `global`;
`shuffle-global` требует `global` (выбирается автоматически, если scope не указан).
`none` отключает случайные перестановки; детерминированная ротация balanced-blocks
при этом сохраняется. Параметры и точные варианты PRNG описаны в
[полной документации CLI](../en/profiling-and-campaigns.md#reproducible-measurement-schedules)
и [лицензионных/implementation notes](../prng-notices.md).

Несколько seed создают отдельные каталоги `seed-000-42`, `seed-001-1337`, …
с общим индексом `experiments.json`. У каждого запуска свои manifest, snapshot,
schedule, journal и результаты. Seed задаёт порядок и **не превращается
автоматически в независимое измерение производительности**. Число повторений
остаётся отдельным параметром. В каждом блоке сохраняется полный одинаковый
набор workload для сравниваемых конфигураций; парность есть в сырых строках.

Configure/build и зависимые диагностические шаги не перемешиваются. Измерения
выполняются последовательно через прежний command dispatcher. `schedule.json`
сохраняется до dispatch и связывается хешем с manifest. При `--resume` используется
сохранённый порядок, без повторной генерации PRNG. Несовместимые параметры,
изменённый набор измерений и изменённые файлы явно отклоняются. Неопределённый
исход или отмена уже принятой команды не дают права автоматически повторить её.

Старые архивы остаются доступны для чтения. Полностью завершённый legacy-запуск
можно явно мигрировать при resume из записанного порядка команд. Прерванный старый
архив без сохранённого будущего расписания возобновить таким способом нельзя;
нужен новый каталог результатов. Старые результаты не переинтерпретируются молча.

Успешные новые запуски получают `historical-completion.json`; `history import`
проверяет хеши и импортирует каждый seed отдельно, сохраняя PRNG/version, seed,
strategy, block/pair IDs и позицию измерения. Незавершённые результаты не выдаются
за завершённые. Все генераторы и стратегии проверяются отдельным NativeAOT smoke.
