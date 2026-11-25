# Engineering Kits

[Кампании Kit и проверки регрессий из C#](programmable-engineering.md).

Kit задаёт требования к инструментам и платформе. Профиль конфигурации независимо задаёт Debug или Release, линковку, оптимизацию и инструментирование. Конкретный Host и workspace задаются подключением Client SDK.

В Linux x64 доступны managed Clang с Ninja (`linux-clang`) и Ninja Multi-Config (`linux-clang-multi`), системный GCC (`linux-gcc`) и системный Clang (`linux-system-clang`). Для системных Kit Host проверяет семейство, версию и целевую платформу компилятора. Qt и Vulkan используют существующие проверки системных зависимостей. `windows-msvc` использует MSVC и Windows SDK на Windows x64, а `macos-appleclang` — AppleClang и macOS SDK на ARM64.

```sh
cmakeworkflow new runtime Demo
cd Demo
cmakeworkflow kit use linux-clang
cmakeworkflow restore --plan
cmakeworkflow kit lock linux-clang --plan-sha KIT_DIGEST
cmakeworkflow restore --plan
cmakeworkflow restore --configuration-plan-sha CONFIG_DIGEST
cmakeworkflow test --profile release
```

Для managed Kit отдельно подтвердите установку инструментов и публикацию environment lock с помощью команды из плана. Параметр `--kit ID` переопределяет активный Kit проекта, `kit clear` возвращает режим без Kit. Смена Kit сохраняет SDK lock и предыдущие build trees. Для нового профиля или матрицы просмотрите и подтвердите соответствующий план конфигурации.

Публичный Developer API публикует Kit lock и композицию конфигурации v2. Host при исполнении проверяет их SHA-256, SDK и фактические инструменты. Для удалённого запуска подготовьте эти файлы в workspace на Host и передайте явные пути через Client SDK. При восстановлении используется исходный утверждённый манифест, даже если активный Kit сменился. Managed Linux Kit автоматически привязывает receipt vcpkg после отдельного подтверждения установки через `tooling vcpkg install . --yes --plan-sha DIGEST`. Receipt для локального плана привязан к пути workspace. Матрица `benchmark --kit-matrix linux-gcc,linux-clang` запускает одобренные варианты на одном Host и записывает обе оси сравнения.

[Полная справка, схемы, удалённый запуск и сравнение benchmark в History](../en/engineering-kits.md).
