# Developer Lifecycle

```sh
cmakeworkflow new runtime Demo
cd Demo
cmakeworkflow doctor
cmakeworkflow restore
cmakeworkflow build --profile release
cmakeworkflow test --profile release
cmakeworkflow benchmark --repeats 3
cmakeworkflow pack
```

Команды ищут ближайший `cmwf.project.json` вверх от текущего каталога. `--project DIRECTORY` задаёт точный корень. Scaffold сразу публикует SDK lock и встроенные конфигурации Debug и Release. Для managed-сборки подготовьте окружение через существующие tooling-команды и передайте `--environment-lock SHA256` при создании проекта.

`inspect --json` показывает конфигурацию и источники настроек. `doctor` дополнительно проверяет локальные инструменты. Qt и Vulkan окончательно проверяются при configure на execution host. `restore` показывает отдельные планы SDK, конфигурации и существующего механизма vcpkg. Установка выполняется существующими командами tooling с явным подтверждением.

Если политики изменились, сначала запустите `restore --plan`. Опубликуйте SDK lock через `restore --sdk-plan-sha DIGEST`, затем просмотрите новый план конфигурации и подтвердите его через `restore --configuration-plan-sha DIGEST`. При подтверждении используйте те же `--profile`, `--matrix` или `--overrides`, что и при планировании.

`build`, `test`, `run`, `benchmark`, `profile` и `pack` используют существующий workflow-движок. План можно посмотреть через `--plan --json`. У исполнения есть Host operation ID и отчёт, а для восстановления сохраняются исходный workflow-манифест и его digest. Существующие подкоманды `profile run/analyze/replay`, `workflow`, `project config` и `project sdk` продолжают работать.

CLI и отдельный NativeAOT-потребитель обращаются к публичному `CMakeWorkflow.Developer`. Локальное и удалённое исполнение используют Client SDK и admission Host. Для удалённого запуска подготовьте workspace и укажите пути на Host явно. API и параметры команд описаны в [английской справке](../en/developer-lifecycle.md).
