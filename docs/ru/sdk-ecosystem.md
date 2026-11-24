# Экосистема нативных SDK

[English](../en/sdk-ecosystem.md)

```sh
cmakeworkflow project sdk list --json
cmakeworkflow project sdk show Native.Runtime --version 1.0.0 --json
cmakeworkflow project new runtime Demo ./Demo --variants
cmakeworkflow project sdk requirements ./Demo
cd Demo
cmakeworkflow project config plan . --matrix
cmakeworkflow project config lock . --matrix --plan-sha APPROVED_SHA256
cmakeworkflow project config workflow . --matrix > variants.json
cmakeworkflow workflow run variants.json --jobs 2 --results-dir out/results
```

Встроенный каталог задаёт точную версию, наследование, форму проекта,
политику по умолчанию, capabilities, требования, модули и файлы заготовки.
CLI и Host используют один каталог. Host сверяет SHA‑256 дескриптора и
модулей с configuration lock. SDK 1.1.0 и ранее одобренные варианты P3
сохраняют формат и идентичность. У новых SDK отдельный набор модулей
`ecosystem-v1` и прежнее расширение P3.

| SDK | Возможности и требования |
| --- | --- |
| Runtime | Ограниченная очередь, static/shared, CTest, benchmark, экспорт пакета, опциональные Threads. Поддерживает managed Linux x64 и ambient сборку. |
| Interop | C ABI с opaque handles, C/C++ тесты, статическая и динамическая библиотеки. Для P/Invoke теста нужен установленный .NET 10 SDK. Освобождение handle выполняет библиотека, которая его создала. |
| Qt | Ambient Qt 6.3+ Core/Widgets/Test и moc/uic/rcc. Headless тесты используют offscreen plugin. |
| Graphics | Ambient Vulkan headers/loader и glslc. SPIR-V генерируется отдельно в каждом дереве. Тест Vulkan instance требует установленный ICD. |
| Embedded | Дескриптор требований будущего cross-target provider. Создание исполняемого проекта отклоняется до появления провайдера. |

Qt/Vulkan обнаруживаются при CMake configure и фиксируются как локальные
ambient зависимости. Логи конфигурации содержат их отпечатки. Для выдачи
переносимого пакета Qt и Graphics понадобится отдельная deployment policy.
CI устанавливает зависимости явно и печатает точные версии пакетов.
При подготовке проекта или плана загрузки и установки отсутствуют.

Runtime содержит готовую static/shared матрицу и профиль ASan для Linux GCC.
В README заготовки есть команды benchmark, profile и сравнения в History.
Interop подключает managed consumer к CTest по явно включаемой опции.
P5 будет объединять существующие шаги в единый developer CLI.
