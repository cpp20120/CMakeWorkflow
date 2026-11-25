# Engineering Kits

Kit задаёт требования к инструментам и платформе. Профиль конфигурации независимо задаёт Debug или Release, линковку, оптимизацию и инструментирование. Конкретный Host и workspace задаются подключением Client SDK.

В Linux x64 доступны managed Clang с Ninja (`linux-clang`) и Ninja Multi-Config (`linux-clang-multi`), системный GCC (`linux-gcc`) и системный Clang (`linux-system-clang`). Для системных Kit Host проверяет семейство, версию и целевую платформу компилятора. Qt и Vulkan используют существующие проверки системных зависимостей. Поддержка MSVC и AppleClang в режиме Kit пока отсутствует.

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

Публичный Developer API публикует Kit lock и композицию конфигурации v2. Host при исполнении проверяет их SHA-256, SDK и фактические инструменты. Для удалённого запуска подготовьте эти файлы в workspace на Host и передайте явные пути через Client SDK. При восстановлении используется исходный утверждённый манифест, даже если активный Kit сменился. Проекты с vcpkg пока используют существующий workflow с явно привязанным receipt.

[Полная справка, схемы, удалённый запуск и сравнение benchmark в History](../en/engineering-kits.md).
