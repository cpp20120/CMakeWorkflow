# Управление тулчейнами

[English](../en/toolchain-management.md)

Команды `tooling` выполняются на машине сборки. Контроллер может задать
требования в политике Project SDK, а Host проверяет lock и фактический
набор инструментов перед конфигурацией и использованием каталога сборки.

```sh
cmakeworkflow tooling catalog
cmakeworkflow tooling installed
cmakeworkflow tooling resolve --profile linux-clang --version ninja=1.13.1
cmakeworkflow tooling plan --profile linux-clang --tool-policy ManagedOnly
cmakeworkflow tooling setup --profile linux-clang --tool-policy ManagedOnly --yes --plan-sha PLAN_SHA256
cmakeworkflow tooling lock --profile linux-clang --approve-sha ENVIRONMENT_SHA256
cmakeworkflow tooling verify --environment-lock ENVIRONMENT_SHA256
cmakeworkflow project new application Example ./Example --environment-lock ENVIRONMENT_SHA256
cmakeworkflow project sdk workflow ./Example > ./Example/workflow.pinned.json
cmakeworkflow workflow run ./Example/workflow.pinned.json
```

Для установки нужен SHA-256 плана provisioning. Для публикации lock нужен
`environmentSha256` из `resolve`. Если указаны точные версии, передавайте
те же `--version КОМПОНЕНТ=ВЕРСИЯ` при планировании, установке и публикации.
`resolve`, `verify` и запуск workflow не устанавливают инструменты.
Повреждённое содержимое хранилища отклоняется.

| Платформа | Доступные возможности |
| --- | --- |
| Linux x64, glibc ≥ 2.31 | Verified CMake/CTest/CPack, Ninja, LLVM, Debian sysroot, vcpkg и registry. Нативный GNU x64, Ninja или Ninja Multi-Config, lock v1/v2 |
| Системные инструменты Linux x64 | `resolve --profile cmake-ninja --tool-policy AllowSystem` показывает происхождение CMake, CTest и Ninja. Для системных инструментов переносимый lock не выпускается |
| Windows x64 | `tooling installed` переиспользует поиск MSVC и Windows SDK. Для запуска действуют существующие профили MSVC/Ninja |
| macOS | Каталог и standalone Project SDK. Обычные CMake presets используют выбранные CMake инструменты |

Lock v2 включает host, target и ABI, генератор и SHA-256 компонентов.
Host отдельно фиксирует отпечаток фактического окружения для каталога
сборки и восстановления. `tooling lock --write` сохраняет байты и SHA-256
формата v1. Эксперименты vcpkg продолжают использовать v1 и отдельное
одобрение плана зависимостей.

Требования задаются в `settings.toolchain` Project SDK. Команда
`project sdk workflow` проверяет SDK lock и печатает workflow с выбранным
environment lock. Составление независимых осей compiler, architecture,
configuration, sanitizers, linkage и optimization относится к P3.
