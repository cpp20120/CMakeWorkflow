# Композиция конфигураций и варианты сборки

[English](../en/configuration-composition.md)

Расширение конфигурации v1 подключается через `project new --variants`.
Оно использует Project SDK 1.1.0 и хранит свой модуль и lock отдельно.

```sh
cmakeworkflow project new library Demo ./Demo --variants
cd Demo
cmakeworkflow project config plan . --matrix
cmakeworkflow project config lock . --matrix --plan-sha APPROVED_SHA256
cmakeworkflow project config verify . --matrix
cmakeworkflow project config workflow . --matrix > variants.json
cmakeworkflow workflow run variants.json --jobs 2 --results-dir out/results
cmakeworkflow workflow report out/results/RUN_DIRECTORY --json
```

Профили, настройки и матрица объявляются в `cmwf.config.json`. Для одного
профиля передай `--profile release` на каждом шаге. Для одного варианта из
одобренной матрицы используй `workflow --matrix --variant ID`. Пример
манифеста и схема приведены в английской документации.

Порядок настройки идёт от defaults расширения через упорядоченные
`policyFiles` и проектные `settings` к именованному профилю, осям матрицы
и явному `--overrides FILE`. Скалярные значения заменяются отдельно.
Список sanitizer заменяется целиком. Lock сохраняет источник каждого поля.
Матрица поддерживает include/exclude и до 32 вариантов, включая явно
добавленные варианты до исключений.

`plan` только читает файлы. После подтверждения SHA‑256 команда `lock`
публикует неизменяемое поколение в `cmwf-config/<sha256>/lock.json`.
Вариант определяет отдельный каталог `out/build/<Configuration>-<sha256>`.
ID зависит от SDK, политики и выбранного окружения. Изменения C++-исходников
сами по себе сохраняют ID. Workflow проверяет входные файлы и состояние
CMake cache, а `run.json` фиксирует ID варианта. Отпечаток запускаемого
managed-тулчейна остаётся отдельной частью provenance.

| Окружение | Возможности |
| --- | --- |
| Нативный Ninja | Debug, Release, RelWithDebInfo, MinSizeRel. Static/shared для Library, тесты по настройке и IPO после проверки компилятора. |
| Linux GNU | ASan и UBSan отдельно и вместе после проверки компиляции, линковки и запуска. Инструментированные варианты не публикуют обычный пакет. |
| Managed Clang Linux x64 | Использует environment lock P2 и явный binding с Ninja или Ninja Multi-Config. SDK и генератор проверяет Host. |

TSan, managed sanitizer runtimes, явная привязка установленного MSVC
и cross-compilation пока за пределами admission P3. В P4 эти контракты
можно применять к новым SDK после определения их проверенных возможностей.
